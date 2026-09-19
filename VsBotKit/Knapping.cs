using Vintagestory.Common;

// Стадии взаимодействия и вид взаимодействия берём из игры, но целиком
// Vintagestory.API.Common тянуть нельзя: там свой Func<> (см. Hands.cs)
using EnumHandInteractNw = Vintagestory.API.Common.EnumHandInteractNw;

namespace VsBotKit;

/// <summary>
/// Камнетёсство: первые инструменты из камня — нож, топор, лопата, мотыга,
/// наконечники копья и стрел.
///
/// Как это устроено в игре (читано в VSSurvivalMod: ItemStone, ItemFlint,
/// BlockKnappingSurface, BlockEntityKnappingSurface, KnappingRecipe и в
/// VintagestoryLib: ServerSystemInventory.HandleHandInteraction,
/// ServerSystemBlockSimulation.HandleBlockEntityPacket):
///
/// 1. ПОВЕРХНОСТЬ. Камень кладётся на землю ПРАВЫМ кликом В ПРИСЯДЕ
///    (проверяется byEntity.Controls.ShiftKey), то есть пакетом 25
///    UseType = HeldItemInteract, EnumHandInteract = StartHeldItemUse —
///    сервер по нему зовёт Collectible.OnHeldUseStart → OnHeldInteractStart.
///    Дальше два РАЗНЫХ предмета ведут себя по-разному:
///    * ItemFlint (кремень): кликаем по земле, поверхность появляется на
///      грани клика (blockSel.Position + Face), из руки уходит 1 кремень;
///    * ItemStone (камень «stone-...»): поверхность делается ТОЛЬКО из уже
///      лежащего на земле блока «loosestones-&lt;порода&gt;-...» той же породы —
///      он заменяется поверхностью на своём месте, а камень из руки НЕ
///      расходуется. Сам блок «loosestones» игрок кладёт тем же приседом
///      с кликом по земле (тогда уходит 1 камень из руки).
///    Блок поверхности игра ищет по коду «knappingsurface» (жёстко в
///    ItemStone/ItemFlint), блок-сущность — класс «KnappingSurface».
///
/// 2. РЕЦЕПТ. Клиент открывает диалог выбора и шлёт блок-сущности пакет
///    1001 с protobuf-сериализованным RecipeId (SerializerUtil.Serialize).
///    Пока рецепт не выбран, удары не откалывают ничего: OnRemove сразу
///    возвращает false. Список рецептов клиенту ПРИХОДИТ — сервер шлёт
///    реестры рецептов в Packet_ServerAssets.Recipes (ServerMain.RecipesToPacket),
///    код реестра «knappingrecipes». Мы разбираем эти байты сами (формат
///    задан LayeredVoxelRecipe.ToBytes / CraftingRecipeIngredient.ToBytes /
///    JsonItemStack.ToBytes), поэтому список того, что бот умеет вытесать,
///    берётся из реестра сервера, а не из списка в коде.
///
/// 3. УДАР. Левый клик по вокселю поверхности: клиент шлёт блок-сущности
///    пакет 1002 с телом (int x, int y, int z, bool mouseMode, ushort
///    facing.Index) — BlockEntityKnappingSurface.SendUseOverPacket. Сервер в
///    OnReceivedClientPacket разбирает его и зовёт OnUseOver, который убирает
///    ОДИН воксель (радиус 0) и заодно обваливает куски, отвалившиеся от
///    фигуры (tryBfsRemove). Воксели рецепта не убираются никогда.
///    ВАЖНО: сервер молча выходит, если в активном слоте пусто, — камень
///    должен оставаться в руке всё время работы.
///
/// 4. ИТОГ. Когда оставшиеся воксели совпали с рецептом (MatchesRecipe),
///    сервер сам кладёт готовое в инвентарь (TryGiveItemstack) и стирает
///    блок поверхности. Отсюда и честная проверка результата: пропал блок +
///    выросло число нужных предметов в инвентаре.
///
/// Ударов надо ровно «сколько вокселей в заготовке минус сколько в рецепте»:
/// заготовка — квадрат 10×10 (CreateInitialWorkItem, воксели 3..12), рецепты
/// в assets/survival/recipes/knapping занимают от 12 до 72 вокселей.
/// </summary>
public sealed class Knapping
{
    private readonly BotContext ctx;

    // Реестры приходят в потоке сети, а читают их в потоке работы бота.
    // Поэтому не дописываем на месте, а подменяем ссылку целиком: читатель
    // всегда видит либо старый полный набор, либо новый полный набор

    /// <summary>Рецепты, разобранные из реестра сервера (пусто — ещё не пришли).</summary>
    private volatile KnappingRecipeInfo[] recipes = [];

    /// <summary>Код предмета → класс предмета из реестра («ItemStone», «ItemFlint»).</summary>
    private volatile Dictionary<string, string> itemClasses = new(StringComparer.Ordinal);

    /// <summary>Предметы с атрибутом knappable — по нему игра решает, годится ли камень.</summary>
    private volatile HashSet<string> knappable = new(StringComparer.Ordinal);

    /// <summary>Код блока камнетёсной поверхности — тот же, что ищет сама игра.</summary>
    public const string SurfaceBlockCode = "knappingsurface";

    /// <summary>Класс блок-сущности поверхности (blocktypes/stone/knappingsurface.json).</summary>
    public const string SurfaceEntityClass = "KnappingSurface";

    // Номера пакетов блок-сущности из BlockEntityKnappingSurface
    private const int PacketSelectRecipe = 1001;
    private const int PacketUseOver = 1002;
    private const int PacketCancel = 1003;

    /// <summary>Грань «верх» (BlockFacing.UP.Index) — по ней кликают по земле.</summary>
    private const int FaceUp = 4;

    public Knapping(BotContext ctx)
    {
        this.ctx = ctx;
        ctx.Bot.OnPacket += HandlePacket;
    }

    public event Action<string>? OnLog;

    /// <summary>Реестр рецептов получен и разобран.</summary>
    public bool Ready => recipes.Length > 0;

    /// <summary>Рецепты пришли с сервера и разобраны.</summary>
    public event Action? OnRecipesReady;

    /// <summary>Все рецепты камнетёсства, как их прислал сервер.</summary>
    public IReadOnlyList<KnappingRecipeInfo> Recipes => recipes;

    /// <summary>
    /// Пауза между ударами. 0.25 с — это BuildRepeatDelay клиента игры:
    /// столько ждёт настоящий клиент между повторными кликами при зажатой
    /// кнопке. Быстрее — это уже не человек.
    /// </summary>
    public int StrikeDelayMs { get; set; } = 250;

    /// <summary>Сколько ждать подтверждения от сервера на каждый шаг.</summary>
    public int ConfirmMs { get; set; } = 3000;

    /// <summary>Радиус поиска подходящего камня на земле и готовой поверхности.</summary>
    public int SearchRadius { get; set; } = 6;

    // ---------------- реестр рецептов ----------------

    private void HandlePacket(Packet_Server p)
    {
        if (p.Id != 19 || p.Assets == null)
            return;

        // Классы и атрибуты предметов: по классу игра решает, КАК предмет
        // кладётся на землю (ItemStone или ItemFlint), по атрибуту knappable —
        // годится ли он для камнетёсства вообще (BlockKnappingSurface.OnLoaded)
        if (p.Assets.Items != null)
        {
            var classes = new Dictionary<string, string>(StringComparer.Ordinal);
            var knap = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < p.Assets.ItemsCount; i++)
            {
                var it = p.Assets.Items[i];
                if (it?.Code is not { } code)
                    continue;
                if (it.ItemClass is { Length: > 0 } cls)
                    classes[code] = cls;
                if (it.Attributes is { Length: > 0 } attrs && IsKnappableJson(attrs))
                    knap.Add(code);
            }
            itemClasses = classes;
            knappable = knap;
        }

        if (p.Assets.Recipes == null)
            return;
        for (int i = 0; i < p.Assets.RecipesCount; i++)
        {
            var reg = p.Assets.Recipes[i];
            if (reg?.Code != "knappingrecipes" || reg.Data == null)
                continue;
            try
            {
                recipes = ParseRegistry(reg.Data, reg.Quantity).ToArray();
                OnLog?.Invoke($"рецептов камнетёсства получено: {recipes.Length}");
                OnRecipesReady?.Invoke();
            }
            catch (Exception ex)
            {
                // Не притворяемся, что рецепты есть: без них бот не знает форму
                OnLog?.Invoke($"реестр камнетёсства не разобран: {ex.GetType().Name} {ex.Message}");
            }
        }
    }

    private static bool IsKnappableJson(string attributes)
    {
        try
        {
            return Newtonsoft.Json.Linq.JObject.Parse(attributes)
                .GetValue("knappable", StringComparison.OrdinalIgnoreCase)?.ToObject<bool>() == true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Разбор реестра «knappingrecipes» из Packet_ServerAssets.
    ///
    /// Формат байт задан методами ToBytes самой игры и повторён здесь один
    /// в один: LayeredVoxelRecipe.ToBytes (id, ингредиенты, узор, имя, выход),
    /// CraftingRecipeIngredient.ToBytes, JsonItemStack.ToBytes. Разбирать
    /// через сами классы игры нельзя: их FromBytes требует полноценный
    /// IWorldAccessor (Resolve ищет предметы, пишет в лог, ведёт индекс
    /// ингредиентов), а у headless-бота такого мира нет. Всё, что нужно нам, —
    /// id, код ингредиента, узор и код выхода — лежит в потоке открытым текстом.
    /// </summary>
    private static List<KnappingRecipeInfo> ParseRegistry(byte[] data, int quantity)
    {
        var result = new List<KnappingRecipeInfo>(quantity);
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        for (int i = 0; i < quantity; i++)
        {
            int recipeId = reader.ReadInt32();

            int ingredients = reader.ReadInt32();
            string? ingredientCode = null;
            bool ingredientIsBlock = false;
            for (int k = 0; k < ingredients; k++)
            {
                var (code, isBlock) = ReadIngredient(reader);
                if (k == 0)
                {
                    ingredientCode = code;
                    ingredientIsBlock = isBlock;
                }
            }

            int layers = reader.ReadInt32();
            var pattern = new string[layers][];
            for (int l = 0; l < layers; l++)
                pattern[l] = Vintagestory.API.Util.ReaderWriterExtensions.ReadStringArray(reader);

            string name = reader.ReadString();

            // Выход — JsonItemStack.ToBytes: short Type, string Code, int StackSize,
            // bool + ItemStack (сервер его не заполняет), bool + строка атрибутов
            reader.ReadInt16();
            string outputCode = reader.ReadString();
            int outputCount = reader.ReadInt32();
            if (reader.ReadBoolean())
                _ = new Vintagestory.API.Common.ItemStack(reader);
            if (reader.ReadBoolean())
                reader.ReadString();

            result.Add(new KnappingRecipeInfo
            {
                RecipeId = recipeId,
                Name = name,
                IngredientCode = ShortCode(ingredientCode),
                IngredientIsBlock = ingredientIsBlock,
                OutputCode = ShortCode(outputCode),
                OutputCount = Math.Max(1, outputCount),
                Voxels = GenVoxels(pattern)
            });
        }
        return result;
    }

    /// <summary>Один ингредиент рецепта (CraftingRecipeIngredient.ToBytes).</summary>
    private static (string? Code, bool IsBlock) ReadIngredient(BinaryReader reader)
    {
        int matchingType = reader.ReadInt32();          // EnumRecipeMatchType
        int type = reader.ReadInt32();                  // EnumItemClass: 0 блок, 1 предмет
        string? code = reader.ReadBoolean() ? reader.ReadString() : null;
        reader.ReadInt32();                             // Quantity
        // Готовый стак пишется только у точного совпадения (и то не всегда)
        if (matchingType == 0 && reader.ReadBoolean())
            _ = new Vintagestory.API.Common.ItemStack(reader);
        reader.ReadBoolean();                           // Consume
        reader.ReadInt32();                             // DurabilityChange
        if (reader.ReadBoolean())                       // AllowedVariants
            for (int n = reader.ReadInt32(); n > 0; n--)
                reader.ReadString();
        if (reader.ReadBoolean())                       // SkipVariants
            for (int n = reader.ReadInt32(); n > 0; n--)
                reader.ReadString();
        if (reader.ReadBoolean())                       // ReturnedStack (JsonItemStack)
        {
            reader.ReadInt16();
            reader.ReadString();
            reader.ReadInt32();
            if (reader.ReadBoolean())
                _ = new Vintagestory.API.Common.ItemStack(reader);
            if (reader.ReadBoolean())
                reader.ReadString();
        }
        if (reader.ReadBoolean())                       // RecipeAttributes
            reader.ReadString();
        reader.ReadString();                            // Id
        // Условие по меткам читаем кодом самой игры — свой разбор тут был бы
        // гаданием, а сместиться в потоке нельзя: дальше идёт узор
        _ = Vintagestory.API.Datastructures.ComplexTagConditionExtensions.FromBytes(reader);
        return (code, type == 0);
    }

    /// <summary>
    /// Узор рецепта → воксели 16×16, ровно как LayeredVoxelRecipe.GenVoxels
    /// (слой один — KnappingRecipe.QuantityLayers = 1, поворота нет):
    /// строки узора идут по X, символы — по Z, узор центрируется в 16×16.
    /// </summary>
    private static bool[,] GenVoxels(string[][] pattern)
    {
        var voxels = new bool[16, 16];
        if (pattern.Length == 0 || pattern[0].Length == 0)
            return voxels;
        var layer = pattern[0];
        int rows = layer.Length, len = layer[0].Length;
        int offX = (16 - rows) / 2, offZ = (16 - len) / 2;
        for (int x = 0; x < Math.Min(rows, 16); x++)
            for (int z = 0; z < Math.Min(len, 16); z++)
                voxels[x + offX, z + offZ] = layer[x][z] != '_' && layer[x][z] != ' ';
        return voxels;
    }

    /// <summary>Код без домена: реестры бота хранят коды короткими («flint»).</summary>
    private static string ShortCode(string? code)
    {
        if (string.IsNullOrEmpty(code))
            return "";
        int colon = code.IndexOf(':');
        return colon < 0 ? code : code[(colon + 1)..];
    }

    // ---------------- что бот умеет вытесать ----------------

    /// <summary>Всё, что вообще тешется на этом сервере (коды предметов).</summary>
    public IEnumerable<string> KnownOutputs => recipes.Select(r => r.OutputCode).Distinct();

    /// <summary>Годится ли предмет для камнетёсства (атрибут knappable из реестра).</summary>
    public bool IsKnappable(string? code) => code != null && knappable.Contains(code);

    /// <summary>Сколько таких предметов у бота с собой (точное совпадение кода).</summary>
    public int CountOf(string code) => ctx.Hands.CountOf(s => s.Code == code);

    /// <summary>
    /// Что бот может вытесать ПРЯМО СЕЙЧАС: есть материал в инвентаре.
    /// Список строится из реестра рецептов, а не из списка в коде.
    /// </summary>
    public IEnumerable<KnappingRecipeInfo> Available() =>
        recipes.Where(r => !r.IngredientIsBlock && CountOf(r.IngredientCode) > 0);

    /// <summary>
    /// Рецепт по желаемому предмету: сначала точное совпадение кода, потом
    /// вхождение куска («knifeblade» → нож из любого подходящего камня).
    /// Из нескольких берётся тот, на который у бота больше материала.
    /// </summary>
    public KnappingRecipeInfo? FindRecipe(string wanted)
    {
        var matches = recipes
            .Where(r => string.Equals(r.OutputCode, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            matches = recipes
                .Where(r => r.OutputCode.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return matches
            .OrderByDescending(r => CountOf(r.IngredientCode))
            .FirstOrDefault();
    }

    /// <summary>
    /// Можно ли вытесать: есть ли рецепт, материал и место под поверхность.
    /// Возвращает причину отказа, а не просто «нет».
    /// </summary>
    public bool CanKnap(string wanted, out string reason)
    {
        if (!Ready)
        {
            reason = "реестр рецептов камнетёсства ещё не получен";
            return false;
        }
        if (FindRecipe(wanted) is not { } recipe)
        {
            reason = $"рецепта на «{wanted}» на сервере нет";
            return false;
        }
        if (recipe.IngredientIsBlock)
        {
            reason = $"{recipe.OutputCode}: ингредиент — блок ({recipe.IngredientCode}), такое бот не умеет";
            return false;
        }
        int have = CountOf(recipe.IngredientCode);
        int need = NeededMaterial(recipe);
        if (have < need)
        {
            reason = $"{recipe.OutputCode}: нужно {need} × {recipe.IngredientCode}, есть {have}";
            return false;
        }
        if (PickSurfaceSpot() == null && FindReusableSurface(recipe) == null)
        {
            reason = "негде положить камень: рядом нет свободной клетки с твёрдым полом";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Сколько материала нужно. Один камень обязан ОСТАВАТЬСЯ В РУКЕ всё время
    /// работы: сервер молча пропускает удары с пустой рукой
    /// (BlockEntityKnappingSurface.OnUseOver). Ещё один уходит на саму
    /// поверхность — кремень прямо из руки, «stone-…» через блок loosestones, —
    /// но если поверхность или подходящий камень уже лежат в мире, тратить
    /// нечего.
    /// </summary>
    private int NeededMaterial(KnappingRecipeInfo recipe) =>
        FindReusableSurface(recipe) != null || FindLyingStone(recipe) != null ? 1 : 2;

    /// <summary>
    /// Лежащий на земле камень своей породы: только из него ItemStone делает
    /// поверхность (проверка PathStartsWith("loosestones") и совпадение породы).
    /// Для кремня и прочих предметов такого пути нет.
    /// </summary>
    private BlockPos? FindLyingStone(KnappingRecipeInfo recipe)
    {
        if (ItemClassOf(recipe.IngredientCode) != "ItemStone" || SelfCell() is not { } self)
            return null;
        string rock = VariantPart(recipe.IngredientCode, 1);
        return ctx.World.FindNearestBlock(
            c => c.StartsWith("loosestones-", StringComparison.Ordinal) && VariantPart(c, 1) == rock,
            self, SearchRadius, height: 2);
    }

    // ---------------- работа ----------------

    /// <summary>
    /// Вытесать предмет: положить камень, выбрать рецепт, отколоть лишние
    /// воксели, дождаться выдачи. Каждый шаг подтверждается сервером
    /// (блок сменился / блок-сущность изменилась / в инвентаре прибыло),
    /// отчёт честный: «пакет ушёл» результатом не считается.
    /// </summary>
    public async Task<KnappingReport> KnapAsync(string wanted, CancellationToken ct = default)
    {
        if (!CanKnap(wanted, out string why))
            return new KnappingReport(false, why);
        var recipe = FindRecipe(wanted)!;

        // Камень в руку: без него не положить поверхность и не бить по ней
        if (await ctx.Hands.TakeToHandAsync(s => s.Code == recipe.IngredientCode, ct) is null)
            return new KnappingReport(false, $"не взять в руку {recipe.IngredientCode}");

        var surface = await PrepareSurfaceAsync(recipe, ct);
        if (surface.Pos is not { } pos)
            return new KnappingReport(false, surface.Reason);

        // Камень мог кончиться при установке — сервер тогда молча пропустит
        // все удары, а бот решит, что «бьёт»
        if (ctx.Hands.Held?.Code != recipe.IngredientCode &&
            await ctx.Hands.TakeToHandAsync(s => s.Code == recipe.IngredientCode, ct) is null)
        {
            await CancelSurfaceAsync(pos);
            return new KnappingReport(false,
                $"{recipe.IngredientCode} кончился: с пустой рукой сервер удары не считает");
        }

        if (!await SelectRecipeAsync(pos, recipe, ct))
        {
            await CancelSurfaceAsync(pos);
            return new KnappingReport(false, "сервер не подтвердил выбор рецепта");
        }

        int before = CountOf(recipe.OutputCode);
        var strikes = await StrikeAllAsync(pos, recipe, ct);
        if (!strikes.Ok)
            return new KnappingReport(false, strikes.Reason, strikes.Strikes);

        // Итог: блок поверхности исчез (CheckIfFinished стирает его) и в
        // инвентаре прибыло. Ждём именно этого, а не «мы отправили удары»
        bool given = await WaitAsync(() => CountOf(recipe.OutputCode) > before, ConfirmMs, ct);
        int gained = CountOf(recipe.OutputCode) - before;
        if (!given)
            return new KnappingReport(false,
                $"форма собрана ({strikes.Strikes} ударов), но {recipe.OutputCode} в инвентаре не появился",
                strikes.Strikes);

        return new KnappingReport(true,
            $"вытесал {recipe.OutputCode} ×{gained} из {recipe.IngredientCode} за {strikes.Strikes} ударов",
            strikes.Strikes, gained, recipe.OutputCode);
    }

    // ---------------- поверхность ----------------

    /// <summary>Готовая поверхность бота: та же порода и рецепт ещё не выбран.</summary>
    private BlockPos? FindReusableSurface(KnappingRecipeInfo recipe)
    {
        if (SelfCell() is not { } self)
            return null;
        foreach (var pos in ctx.World.FindBlocks(c => c == SurfaceBlockCode, self, SearchRadius, height: 2, limit: 8))
        {
            if (ctx.World.GetBlockEntity(pos) is not { ClassName: SurfaceEntityClass } be)
                continue;
            if (BaseMaterialOf(be) != recipe.IngredientCode)
                continue;
            int selected = be.Attributes?.GetInt("selectedRecipeId", -1) ?? -1;
            if (selected != -1 && selected != recipe.RecipeId)
                continue;   // на ней уже тешут другое — чужую работу не портим
            // Заготовка должна быть целой: по надколотой чужой форме
            // задуманного не собрать
            if (ReadVoxels(be) is { } v && CountVoxels(v) == 100)
                return pos;
        }
        return null;
    }

    private sealed record SurfaceResult(BlockPos? Pos, string Reason);

    /// <summary>
    /// Положить камень на землю. Кремень ложится прямо на грунт, «stone-…» —
    /// только поверх блока loosestones той же породы (см. ItemStone).
    /// </summary>
    private async Task<SurfaceResult> PrepareSurfaceAsync(KnappingRecipeInfo recipe, CancellationToken ct)
    {
        if (FindReusableSurface(recipe) is { } ready)
        {
            if (!await ctx.Hands.ReachForAsync(ready, ct: ct))
                return new SurfaceResult(null, $"до готовой поверхности {ready} не дойти");
            return new SurfaceResult(ready, "");
        }

        // КАК предмет ложится на землю, решает его класс из реестра сервера:
        // ItemStone — только через блок loosestones своей породы, ItemFlint —
        // прямо на грунт. Незнакомый класс (модовый камень) ведём по пути
        // кремня: он общий, а ошибку всё равно поймает проверка «поверхность
        // появилась там, где ждали»
        bool viaLooseStone = ItemClassOf(recipe.IngredientCode) == "ItemStone";

        // Камень: сперва ищем уже лежащий на земле камень своей породы —
        // именно его игра превращает в поверхность
        if (FindLyingStone(recipe) is { } stonePos && await ctx.Hands.ReachForAsync(stonePos, ct: ct))
            return await MakeSurfaceAsync(stonePos, stonePos, recipe, ct);

        if (PickSurfaceSpot() is not { } cell)
            return new SurfaceResult(null, "негде положить камень: нужна свободная клетка с твёрдым полом рядом");
        var ground = new BlockPos(cell.X, cell.Y - 1, cell.Z);

        if (!await ctx.Hands.ReachForAsync(ground, ct: ct))
            return new SurfaceResult(null, $"до земли в {ground} не дотянуться");

        if (viaLooseStone)
        {
            // Кладём камень на землю блоком loosestones (ItemStone, вторая
            // ветка OnHeldInteractStart) — из руки уходит один камень
            await ctx.Hands.UseHeldAsync(0, at: ground, onFace: FaceUp, sneak: true, ct: ct);
            bool laid = await WaitAsync(
                () => ctx.World.GetBlockCode(cell)?.StartsWith("loosestones-", StringComparison.Ordinal) == true,
                ConfirmMs, ct);
            if (!laid)
                return new SurfaceResult(null,
                    $"камень не лёг в {cell} (там {ctx.World.GetBlockCode(cell) ?? "неизвестно"})");
            // Камень мог быть последним в стаке — берём следующий
            if (ctx.Hands.Held?.Code != recipe.IngredientCode)
                await ctx.Hands.TakeToHandAsync(s => s.Code == recipe.IngredientCode, ct);
            return await MakeSurfaceAsync(cell, cell, recipe, ct);
        }

        // Кремень: кликаем по земле, поверхность встаёт на грань клика
        return await MakeSurfaceAsync(ground, cell, recipe, ct);
    }

    /// <summary>
    /// Собственно превращение в поверхность: присед + правый клик по
    /// <paramref name="clickAt"/>, поверхность ожидается в <paramref name="expectAt"/>.
    /// </summary>
    private async Task<SurfaceResult> MakeSurfaceAsync(BlockPos clickAt, BlockPos expectAt,
        KnappingRecipeInfo recipe, CancellationToken ct)
    {
        if (ctx.Hands.Held?.Code != recipe.IngredientCode)
            return new SurfaceResult(null, $"{recipe.IngredientCode} не в руке — поверхность не сделать");
        if (!ctx.Hands.InReach(clickAt) && !await ctx.Hands.ReachForAsync(clickAt, ct: ct))
            return new SurfaceResult(null, $"до {clickAt} не дотянуться");

        await ctx.Hands.UseHeldAsync(0, at: clickAt, onFace: FaceUp, sneak: true, ct: ct);

        bool placed = await WaitAsync(
            () => ctx.World.GetBlockCode(expectAt) == SurfaceBlockCode &&
                  ctx.World.GetBlockEntity(expectAt)?.ClassName == SurfaceEntityClass,
            ConfirmMs, ct);
        if (!placed)
            return new SurfaceResult(null,
                $"поверхность в {expectAt} не появилась (там {ctx.World.GetBlockCode(expectAt) ?? "неизвестно"})");

        // Материал поверхности сервер берёт из руки — сверяем, иначе рецепт
        // не тот: на граните нож из кремня не вытесать
        if (ctx.World.GetBlockEntity(expectAt) is { } be && BaseMaterialOf(be) is { } mat &&
            mat != recipe.IngredientCode)
            return new SurfaceResult(null, $"на поверхности {mat}, а рецепт на {recipe.IngredientCode}");

        return new SurfaceResult(expectAt, "");
    }

    /// <summary>
    /// Свободная клетка рядом с ботом, где поверхность встанет: пусто в самой
    /// клетке (BlockKnappingSurface заменяет только пустоту) и полноценный
    /// твёрдый верх под ней (HasSolidGround → CanAttachBlockAt UP).
    /// Свою клетку пропускаем: сервер отказывает с «entityintersecting».
    /// </summary>
    private BlockPos? PickSurfaceSpot()
    {
        if (SelfCell() is not { } self)
            return null;
        (int dx, int dz)[] ring =
        [
            (1, 0), (0, 1), (-1, 0), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        ];
        foreach (int dy in new[] { 0, -1 })
            foreach (var (dx, dz) in ring)
            {
                var cell = new BlockPos(self.X + dx, self.Y + dy, self.Z + dz);
                if (ctx.World.GetBlockId(cell.X, cell.Y, cell.Z) != 0)
                    continue;
                if (ctx.World.GetCollisionTop(cell.X, cell.Y - 1, cell.Z) < 1f)
                    continue;
                if (!ctx.Hands.InReach(cell))
                    continue;
                return cell;
            }
        return null;
    }

    private BlockPos? SelfCell() =>
        ctx.Self.Position is { } p
            ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
            : null;

    /// <summary>Материал поверхности: атрибут baseMaterial блок-сущности.</summary>
    private string? BaseMaterialOf(BlockEntityInfo be)
    {
        var stack = be.Attributes?.GetItemstack("baseMaterial");
        if (stack == null)
            return null;
        // EnumItemClass: Block = 0, Item = 1 — id переводим своими реестрами
        return (int)stack.Class == 0 ? ctx.World.BlockIdToCode(stack.Id) : ctx.World.ItemIdToCode(stack.Id);
    }

    private string? ItemClassOf(string code) =>
        itemClasses.TryGetValue(code, out var cls) ? cls : null;

    /// <summary>Кусок кода по порядку от начала: «loosestones-granite-free» → [1] = «granite».</summary>
    private static string VariantPart(string code, int index)
    {
        var parts = code.Split('-');
        return index < parts.Length ? parts[index] : "";
    }

    /// <summary>
    /// Отказаться от начатой поверхности: пакет 1003 — то же, что «отмена» в
    /// диалоге выбора рецепта. Сервер возвращает камень предметом на землю
    /// и убирает блок, а не оставляет мусор в мире.
    /// </summary>
    public Task CancelSurfaceAsync(BlockPos pos) =>
        ctx.Bot.SendPacketAsync(new Packet_Client
        {
            Id = 22,
            BlockEntityPacket = new Packet_BlockEntityPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Packetid = PacketCancel
            }
        });

    // ---------------- рецепт и удары ----------------

    /// <summary>
    /// Выбрать рецепт: пакет 1001 с protobuf-сериализованным id — ровно то,
    /// что шлёт диалог выбора (BlockEntityKnappingSurface.OpenDialog).
    /// </summary>
    private async Task<bool> SelectRecipeAsync(BlockPos pos, KnappingRecipeInfo recipe, CancellationToken ct)
    {
        await ctx.Bot.SendPacketAsync(new Packet_Client
        {
            Id = 22,
            BlockEntityPacket = new Packet_BlockEntityPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Packetid = PacketSelectRecipe,
                Data = Vintagestory.API.Util.SerializerUtil.Serialize(recipe.RecipeId)
            }
        });
        // Сервер подтверждает выбор, разослав блок-сущность заново
        return await WaitAsync(
            () => ctx.World.GetBlockEntity(pos)?.Attributes?.GetInt("selectedRecipeId", -1) == recipe.RecipeId,
            ConfirmMs, ct);
    }

    private sealed record StrikeOutcome(bool Ok, string Reason, int Strikes);

    /// <summary>
    /// Отколоть все лишние воксели. Что колоть, решаем по СОСТОЯНИЮ С СЕРВЕРА
    /// (воксели блок-сущности), а не по своей памяти: один удар может обвалить
    /// сразу несколько вокселей (tryBfsRemove), и «свой» счётчик соврал бы.
    /// </summary>
    private async Task<StrikeOutcome> StrikeAllAsync(BlockPos pos, KnappingRecipeInfo recipe, CancellationToken ct)
    {
        int strikes = 0;
        int misses = 0;
        while (!ct.IsCancellationRequested)
        {
            var be = ctx.World.GetBlockEntity(pos);
            if (ctx.World.GetBlockCode(pos) != SurfaceBlockCode || be == null)
                // Блок исчез — либо форма сошлась (проверит вызывающий по
                // инвентарю), либо поверхность разрушили
                return new StrikeOutcome(true, "", strikes);

            if (ReadVoxels(be) is not { } voxels)
                return new StrikeOutcome(false, "блок-сущность поверхности без вокселей", strikes);

            if (NextTarget(voxels, recipe.Voxels) is not { } target)
            {
                // Лишнего не осталось. Если форма ещё и совпала с рецептом —
                // сервер уже выдаёт предмет (CheckIfFinished). Если не
                // совпала, значит в заготовке не хватает вокселей рецепта:
                // такую фигуру не собрать никакими ударами
                if (SameShape(voxels, recipe.Voxels))
                    return new StrikeOutcome(true, "", strikes);
                return new StrikeOutcome(false,
                    $"в заготовке не хватает вокселей для {recipe.OutputCode} — фигуру уже не собрать",
                    strikes);
            }

            if (!ctx.Hands.InReach(pos) && !await ctx.Hands.ReachForAsync(pos, ct: ct))
                return new StrikeOutcome(false, $"до поверхности {pos} не дотянуться", strikes);
            if (ctx.Hands.Held?.Code != recipe.IngredientCode &&
                await ctx.Hands.TakeToHandAsync(s => s.Code == recipe.IngredientCode, ct) is null)
                return new StrikeOutcome(false,
                    $"{recipe.IngredientCode} кончился — удары сервер считать перестал", strikes);

            int had = CountVoxels(voxels);
            await StrikeAsync(pos, target.X, target.Z);
            strikes++;

            bool changed = await WaitAsync(() =>
            {
                var now = ctx.World.GetBlockEntity(pos);
                if (ctx.World.GetBlockCode(pos) != SurfaceBlockCode || now == null)
                    return true;   // блок пропал — работа кончилась
                return ReadVoxels(now) is { } v && CountVoxels(v) < had;
            }, ConfirmMs, ct);

            if (!changed)
            {
                // Сервер удар не принял. Один раз это может быть задержка сети,
                // два подряд — значит бьём впустую, и врать об этом нельзя
                if (++misses >= 2)
                    return new StrikeOutcome(false,
                        $"сервер не подтвердил скол вокселя ({target.X},{target.Z}) — сделано {strikes} ударов",
                        strikes);
            }
            else misses = 0;

            // Пауза как у человека между кликами; отмену возвращаем отчётом,
            // а не исключением — сколько ударов сделано, знать надо
            try { await Task.Delay(StrikeDelayMs, ct); }
            catch (OperationCanceledException) { return new StrikeOutcome(false, "отменено", strikes); }
        }
        return new StrikeOutcome(false, "отменено", strikes);
    }

    /// <summary>
    /// Один удар по вокселю — тремя пакетами, как настоящий клиент
    /// (SystemMouseInWorldInteractions + ItemStone.OnHeldAttackStop):
    /// «нажал левую» (25, UseType = HeldItemAttack), сам скол (22, 1002),
    /// «отпустил» (25, StopHeldItemUse). Работу делает средний пакет —
    /// остальные два держат серверное состояние руки в правде.
    /// </summary>
    private async Task StrikeAsync(BlockPos pos, int vx, int vz)
    {
        // Точка попадания — верх вокселя: воксель занимает 1/16 клетки,
        // высота заготовки 1/16 (селекционные боксы RegenMeshAndSelectionBoxes)
        double hitX = (vx + 0.5) / 16.0, hitZ = (vz + 0.5) / 16.0, hitY = 1 / 16.0;
        int boxIndex = vx * 16 + vz;   // порядок боксов: X снаружи, Z внутри

        await ctx.Bot.SendPacketAsync(HandPacket(pos, EnumHandInteractNw.StartHeldItemUse,
            hitX, hitY, hitZ, boxIndex, first: true, usingCount: 0));

        await ctx.Bot.SendPacketAsync(new Packet_Client
        {
            Id = 22,
            BlockEntityPacket = new Packet_BlockEntityPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Packetid = PacketUseOver,
                Data = UseOverPayload(vx, vz)
            }
        });

        // UsingCount = 1: у камня OnHeldAttackStep возвращает false, поэтому
        // взаимодействие у настоящего клиента живёт ровно один кадр
        await ctx.Bot.SendPacketAsync(HandPacket(pos, EnumHandInteractNw.StopHeldItemUse,
            hitX, hitY, hitZ, boxIndex, first: false, usingCount: 1));
    }

    private Packet_Client HandPacket(BlockPos pos, EnumHandInteractNw stage,
        double hitX, double hitY, double hitZ, int boxIndex, bool first, int usingCount) => new()
    {
        Id = 25,
        HandInteraction = new Packet_ClientHandInteraction
        {
            // Камнетёсство — это ЛЕВАЯ кнопка (HeldItemAttack), но MouseButton
            // клиент всегда шлёт 2: сервер отбрасывает всё остальное
            // (ServerSystemInventory.HandleHandInteraction)
            UseType = (int)Vintagestory.API.Common.EnumHandInteract.HeldItemAttack,
            MouseButton = 2,
            EnumHandInteract = (int)stage,
            SlotId = Math.Max(0, ctx.Hands.ActiveSlot),
            X = pos.X, Y = pos.Y, Z = pos.Z,
            OnBlockFace = FaceUp,
            SelectionBoxIndex = boxIndex,
            HitX = CollectibleNet.SerializeDoublePrecise(hitX),
            HitY = CollectibleNet.SerializeDoublePrecise(hitY),
            HitZ = CollectibleNet.SerializeDoublePrecise(hitZ),
            UsingCount = usingCount,
            FirstEvent = first ? 1 : 0
        }
    };

    /// <summary>
    /// Тело пакета 1002 — байт в байт BlockEntityKnappingSurface.SendUseOverPacket:
    /// int x, int y, int z, bool mouseMode, ushort facing.Index.
    /// y всегда 0: у камнетёсной заготовки один слой вокселей.
    /// </summary>
    private static byte[] UseOverPayload(int vx, int vz)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(vx);
        w.Write(0);
        w.Write(vz);
        w.Write(true);              // mouseMode: левая кнопка = «отколоть»
        w.Write((ushort)FaceUp);
        return ms.ToArray();
    }

    // ---------------- воксели ----------------

    /// <summary>
    /// Воксели заготовки из блок-сущности. Раскладка — из
    /// BlockEntityKnappingSurface.serializeVoxels: 32 байта, бит с номером
    /// x*16 + z, младший бит первым.
    /// </summary>
    private static bool[,]? ReadVoxels(BlockEntityInfo be)
    {
        byte[]? data = be.Attributes?.GetBytes("voxels");
        if (data == null || data.Length < 32)
            return null;
        var voxels = new bool[16, 16];
        int n = 0;
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
            {
                voxels[x, z] = (data[n / 8] & (1 << (n % 8))) > 0;
                n++;
            }
        return voxels;
    }

    private static int CountVoxels(bool[,] voxels)
    {
        int count = 0;
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
                if (voxels[x, z])
                    count++;
        return count;
    }

    /// <summary>Форма совпала с рецептом — так же считает сервер (MatchesRecipe).</summary>
    private static bool SameShape(bool[,] current, bool[,] wanted)
    {
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
                if (current[x, z] != wanted[x, z])
                    return false;
        return true;
    }

    /// <summary>Первый лишний воксель: есть в заготовке, но не нужен рецепту.</summary>
    private static (int X, int Z)? NextTarget(bool[,] current, bool[,] wanted)
    {
        for (int x = 0; x < 16; x++)
            for (int z = 0; z < 16; z++)
                if (current[x, z] && !wanted[x, z])
                    return (x, z);
        return null;
    }

    // ---------------- служебное ----------------

    private static async Task<bool> WaitAsync(Func<bool> until, int ms, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (until())
                return true;
            try { await Task.Delay(40, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return until();
    }
}

/// <summary>
/// Рецепт камнетёсства, как его прислал сервер (реестр «knappingrecipes»).
/// </summary>
public sealed class KnappingRecipeInfo
{
    /// <summary>Номер рецепта на сервере — его шлём в пакете выбора (1001).</summary>
    public required int RecipeId { get; init; }

    /// <summary>Имя ассета рецепта («game:recipes/knapping/flint-knife»).</summary>
    public required string Name { get; init; }

    /// <summary>Код камня-заготовки: «flint», «stone-granite».</summary>
    public required string IngredientCode { get; init; }

    /// <summary>Заготовка — блок, а не предмет (в выживании такого нет).</summary>
    public required bool IngredientIsBlock { get; init; }

    /// <summary>Что получится: «knifeblade-flint», «axehead-granite».</summary>
    public required string OutputCode { get; init; }

    /// <summary>Сколько штук получится (наконечники стрел — 6).</summary>
    public required int OutputCount { get; init; }

    /// <summary>Воксели, которые надо ОСТАВИТЬ (16×16, слой один).</summary>
    public required bool[,] Voxels { get; init; }

    /// <summary>Сколько вокселей в готовой фигуре.</summary>
    public int FilledVoxels
    {
        get
        {
            int n = 0;
            for (int x = 0; x < 16; x++)
                for (int z = 0; z < 16; z++)
                    if (Voxels[x, z]) n++;
            return n;
        }
    }

    /// <summary>
    /// Сколько ударов понадобится: заготовка — квадрат 10×10 = 100 вокселей
    /// (BlockEntityKnappingSurface.CreateInitialWorkItem). Часть отвалится
    /// сама (обрушение отколотых кусков), так что это верхняя оценка.
    /// </summary>
    public int StrikesNeeded => Math.Max(0, 100 - FilledVoxels);

    public override string ToString() =>
        $"{OutputCode} ×{OutputCount} из {IngredientCode} (~{StrikesNeeded} ударов)";
}

/// <summary>Честный отчёт о работе: что вышло, сколько ударов, сколько получено.</summary>
public sealed record KnappingReport(
    bool Success,
    string Message,
    int Strikes = 0,
    int Gained = 0,
    string? OutputCode = null);
