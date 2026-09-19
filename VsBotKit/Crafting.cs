using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace VsBotKit;

/// <summary>
/// КАК КЛИЕНТ КРАФТИТ (установлено чтением разобранных сборок игры 1.22.6,
/// не рассуждением). Ниже — только то, что видно в коде, с указанием места.
///
/// Отдельного пакета крафта НЕТ. Крафт целиком собран из обычных операций
/// с инвентарём — Vintagestory.Server/ServerSystemInventory ставит обработчики
/// только на пакеты 7 (ActivateInventorySlot), 8 (MoveItemstack),
/// 9 (FlipItemstacks).
///
/// 1. У каждого игрока сервер САМ создаёт инвентарь "craftinggrid-&lt;uid&gt;"
///    (PlayerInventoryManager.defaultInventories) и открывает его в выживании
///    (ServerSystemInventory.OnPlayerSwitchGameMode; в творческом режиме —
///    наоборот, ЗАКРЫВАЕТ). Верстак игре не нужен: сетка 3×3 всегда при игроке.
///    Единственное ограничение рецепта — RequiresTrait (класс персонажа),
///    проверяется в CharacterSystem.Event_MatchesGridRecipe.
/// 2. Инвентарь — 10 слотов: 0..8 сетка (индекс = строка*3 + столбец),
///    9 — слот вывода (InventoryCraftingGrid: this[GridSizeSq] == outputSlot).
/// 3. Как только в клетку что-то кладут, сервер сам вызывает
///    InventoryCraftingGrid.OnItemSlotModified → FindMatchingRecipe: перебирает
///    рецепты и, если сошлось, кладёт результат в слот 9 и помечает его
///    «грязным». Клиенту прилетает InventoryUpdate(31) — ИМЕННО ЭТО
///    и есть подтверждение «рецепт собрался», а не наша уверенность.
///    RecipeBase.MatchesAtPosition сверяет ВСЕ девять клеток: лишнее в сетке
///    ломает рецепт, поэтому перед закладкой сетка обязана быть пустой.
/// 4. ЗАБИРАЕТ РЕЗУЛЬТАТ ЧЕЛОВЕК SHIFT-КЛИКОМ, и это пакет 7, а не 8.
///    GuiElementItemSlotGridBase.SlotClick при shift зовёт
///    inventory.ActivateSlot(slotId, слот сам по себе, op) и шлёт то, что
///    вернул InvNetworkUtil.GetActivateSlotPacket — Packet_ActivateInventorySlot.
///    На сервере InventoryCraftingGrid.ActivateSlot(9, …) оборачивает работу
///    в BeginCraft()/EndCraft() (это глушит пересчёт рецепта на КАЖДОЕ
///    изменение клетки во время списывания), а InventoryBase.ActivateSlot при
///    ShiftDown подменяет источник на сам слот вывода и зовёт
///    InventoryManager.TryTransferAway — игра САМА раскладывает результат по
///    подходящим слотам. Дальше ItemSlotCraftingOutput.TryPutInto при ShiftDown
///    уходит в CraftMany и повторяет рецепт, пока хватает составляющих.
///    То есть ОДИН shift-клик = весь возможный тираж закладки.
///    Побочное следствие, о котором надо знать: если места в сумках не хватило,
///    InventoryCraftingGrid.ActivateSlot делает
///    InventoryManager.DropItem(outputSlot, fullStack: true) — остаток падает
///    НА ЗЕМЛЮ. Поэтому перед закладкой считаем свободное место (RoomFor).
/// 5. Ингредиенты списываются только когда результат реально забран
///    (ItemSlotCraftingOutput.CraftSingle/CraftMany → InventoryCraftingGrid.
///    ConsumeIngredients → GridRecipe.ConsumeInput).
/// 6. ЛОВУШКА С «lastChanged». Сравнения у пакетов 7 и 8 ОБРАТНЫЕ друг другу:
///      пакет 8 (InventoryNetworkUtil.handleMoveItemStackPacket →
///              SendDirtyInventoryContents): откат, если
///              inv.lastChangedSinceServerStart &gt; присланного значения
///              → безопасно слать long.MaxValue;
///      пакет 7 (InventoryNetworkUtil.handleActivateInventorySlotPacket):
///              откат, если inv.lastChangedSinceServerStart &lt; присланного
///              → long.MaxValue ОТВЕРГАЕТСЯ ВСЕГДА, слать надо 0.
///    Одно и то же число в обоих пакетах молча ломает половину крафта.
/// 7. Список рецептов клиенту ПРИХОДИТ: Packet_ServerAssets(19).Recipes[] —
///    по записи на реестр рецептов, Code == "gridrecipes", Quantity — сколько
///    рецептов, Data — просто склеенные GridRecipe.ToBytes
///    (ServerMain.RecipesToPacket + RecipeRegistryGeneric.FromBytes).
///    Поэтому рецепты берём из сети, а не из json на диске: сервер уже развернул
///    все варианты («plank-{wood}» → отдельный рецепт на каждое дерево) и уже
///    применил свои настройки мира.
///
/// ЧЕГО БОТ НЕ УМЕЕТ И ГОВОРИТ ОБ ЭТОМ ВСЛУХ — см. CraftIngredient.NeedsAttributes
/// (сверка атрибутов стака) и комментарий у PlanFill (прочность одинаковых
/// инструментов считается по лучшему экземпляру).
/// </summary>
public sealed class Crafting
{
    /// <summary>Ширина сетки. InventoryCraftingGrid.GridSize == 3.</summary>
    public const int GridWidth = 3;

    /// <summary>Клеток для ингредиентов (0..8).</summary>
    public const int GridCells = 9;

    /// <summary>Слот вывода. InventoryCraftingGrid: this[GridSizeSq] — outputSlot.</summary>
    public const int OutputSlot = 9;

    /// <summary>Код реестра рецептов сетки в Packet_ServerAssets (GameMain: "gridrecipes").</summary>
    public const string GridRecipeRegistry = "gridrecipes";

    /// <summary>
    /// Зажатый Shift в пакете 7. Числа берём из клиента игры:
    /// GuiElementItemSlotGridBase.SlotClick собирает Modifiers как
    /// (shift ? 2 : 0) | (ctrl ? 1 : 0) | (alt ? 4 : 0).
    /// </summary>
    public const int ShiftKey = 2;

    private readonly BotContext ctx;
    private readonly List<CraftRecipe> recipes = [];
    private readonly Dictionary<string, List<CraftRecipe>> byOutput = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Диагностика: что делает крафт (по-русски, для чата и лога).</summary>
    public event Action<string>? OnLog;

    /// <summary>Реестр рецептов получен и разобран.</summary>
    public bool Ready { get; private set; }

    /// <summary>Почему рецепты не разобрались (null — всё в порядке).</summary>
    public string? LoadError { get; private set; }

    /// <summary>
    /// Все рецепты сетки, как их прислал сервер. Отдаём копию: реестр
    /// перезаполняется из потока приёма пакетов при переподключении.
    /// </summary>
    public IReadOnlyList<CraftRecipe> All
    {
        get { lock (recipes) return recipes.ToArray(); }
    }

    /// <summary>
    /// Сколько ждать ответа сервера на действие с инвентарём. Сервер шлёт
    /// изменённые слоты пачкой раз в 30 мс (ServerSystemInventory.SendDirtySlots),
    /// так что запас здесь — на сеть и лаг, а не на «авось».
    /// </summary>
    public TimeSpan ServerReplyTimeout { get; set; } = TimeSpan.FromSeconds(4);

    public Crafting(BotContext ctx)
    {
        this.ctx = ctx;
        ctx.Bot.OnPacket += HandlePacket;
        // Реестры привязаны к соединению: после переподключения сервер пришлёт
        // их заново, а старые id блоков и предметов могут уже не совпасть
        ctx.Bot.OnSessionReset += Reset;
    }

    private void Reset()
    {
        lock (recipes)
        {
            recipes.Clear();
            byOutput.Clear();
            Ready = false;
            LoadError = null;
        }
    }

    // ---------------- получение реестра рецептов ----------------

    private void HandlePacket(Packet_Server p)
    {
        if (p.Id != 19 || p.Assets?.Recipes == null)
            return;

        for (int i = 0; i < p.Assets.RecipesCount; i++)
        {
            var pkt = p.Assets.Recipes[i];
            if (pkt == null || pkt.Code != GridRecipeRegistry || pkt.Data == null)
                continue;
            LoadGridRecipes(pkt.Data, pkt.Quantity);
            return;
        }
        LoadError = "сервер не прислал реестр \"" + GridRecipeRegistry + "\"";
    }

    /// <summary>
    /// Разобрать байты реестра. Формат — ровно то, что пишет
    /// GridRecipe.ToBytes / RecipeBase.ToBytes / CraftingRecipeIngredient.ToBytes
    /// (VintagestoryAPI.dll). Подформаты (ItemStack, JsonItemStack, условие по
    /// тегам) читаем ИХ ЖЕ кодом — свои велосипеды тут врали бы молча.
    ///
    /// Если разбор не сошёлся байт в байт (изменилась версия игры) — честно
    /// говорим «рецептов нет», а не работаем по мусору.
    /// </summary>
    private void LoadGridRecipes(byte[] data, int quantity)
    {
        var parsed = new List<CraftRecipe>(quantity);
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            for (int i = 0; i < quantity; i++)
            {
                var recipe = ReadRecipe(reader);
                if (recipe != null)
                    parsed.Add(recipe);
            }
            if (ms.Position != ms.Length)
                throw new InvalidDataException(
                    $"после {quantity} рецептов осталось {ms.Length - ms.Position} лишних байт");
        }
        catch (Exception ex)
        {
            LoadError = $"реестр рецептов не разобран: {ex.GetType().Name} {ex.Message}";
            OnLog?.Invoke(LoadError);
            return;
        }

        lock (recipes)
        {
            recipes.Clear();
            byOutput.Clear();
            recipes.AddRange(parsed);
            foreach (var r in parsed)
            {
                if (!byOutput.TryGetValue(r.OutputCode, out var list))
                    byOutput[r.OutputCode] = list = [];
                list.Add(r);
            }
            LoadError = null;
            Ready = recipes.Count > 0;
        }
        OnLog?.Invoke($"рецептов сетки получено: {parsed.Count}");
    }

    private static CraftRecipe? ReadRecipe(BinaryReader r)
    {
        // --- RecipeBase.FromBytes ---
        string name = r.ReadString();
        r.ReadBoolean();                                    // ShowInCreatedBy — только для справочника игры
        r.ReadBoolean();                                    // AverageDurability — считает сервер
        if (!r.ReadBoolean()) r.ReadString();               // флаг пишется как «Attributes == null»
        string? requiresTrait = r.ReadBoolean() ? r.ReadString() : null;
        if (r.ReadBoolean()) r.ReadString();                // CopyAttributesFrom
        SkipStringMap(r);                                   // AllowedVariants — сервер их уже развернул
        SkipStringMap(r);                                   // SkipVariants
        int mergeCount = r.ReadInt32();
        for (int i = 0; i < mergeCount; i++) r.ReadString();// MergeAttributesFrom

        // --- GridRecipe.FromBytes ---
        int width = r.ReadInt32();
        int height = r.ReadInt32();
        bool shapeless = r.ReadBoolean();
        var output = ReadIngredient(r);
        var cells = new CraftIngredient?[width * height];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = r.ReadBoolean() ? null : ReadIngredient(r); // true = клетка пустая
        r.ReadInt32();                                      // RecipeGroup — группировка в справочнике
        r.ReadString();                                     // IngredientPattern — у нас уже разложено по клеткам

        // Результат у рецепта сетки всегда конкретный: маски вида «plank-{wood}»
        // сервер разворачивает в отдельные рецепты ещё при загрузке
        // (RecipeBase.GenerateRecipesForAllIngredientCombinations)
        string? outputCode = output.Code;
        if (outputCode == null || width <= 0 || height <= 0)
            return null;

        return new CraftRecipe
        {
            Name = name,
            Width = width,
            Height = height,
            Shapeless = shapeless,
            Cells = cells,
            OutputCode = outputCode,
            OutputQuantity = Math.Max(1, output.Quantity),
            RequiresTrait = requiresTrait
        };
    }

    /// <summary>Словарь «строка → массив строк» из RecipeBase — нам он не нужен, но байты пропустить обязаны.</summary>
    private static void SkipStringMap(BinaryReader r)
    {
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            r.ReadString();
            int n = r.ReadInt32();
            for (int j = 0; j < n; j++) r.ReadString();
        }
    }

    /// <summary>Один ингредиент — порядок полей из CraftingRecipeIngredient.ToBytes.</summary>
    private static CraftIngredient ReadIngredient(BinaryReader r)
    {
        var match = (EnumRecipeMatchType)r.ReadInt32();
        var kind = (EnumItemClass)r.ReadInt32();
        string? code = r.ReadBoolean() ? r.ReadString() : null;
        int quantity = r.ReadInt32();

        bool needsAttributes = false;
        if (match == EnumRecipeMatchType.Exact && r.ReadBoolean())
        {
            // Читает сама игра: класс, id, размер стака, дерево атрибутов.
            // Конструктор без resolver'а нам и нужен — блоки/предметы мы
            // сопоставляем по коду, а не по объекту
            var stack = new ItemStack(r);
            needsAttributes = stack.Attributes is { Count: > 0 };
        }

        bool consume = r.ReadBoolean();
        int durabilityChange = r.ReadInt32();
        string[]? allowed = r.ReadBoolean() ? ReadStringArray(r) : null;
        string[]? skip = r.ReadBoolean() ? ReadStringArray(r) : null;
        if (r.ReadBoolean())
            // ReturnedStack: часть ингредиентов возвращается игроку (пустая миска).
            // Читаем игровым кодом; instancer в JsonItemStack.FromBytes не участвует
            new JsonItemStack().FromBytes(r, (IClassRegistryAPI)null!);
        if (r.ReadBoolean()) r.ReadString();                // RecipeAttributes — данные для модов
        string id = r.ReadString();
        var tags = ComplexTagConditionExtensions.FromBytes(r);

        return new CraftIngredient
        {
            Match = match,
            Kind = kind,
            Code = code,
            Quantity = Math.Max(1, quantity),
            Consumed = consume,
            DurabilityChange = durabilityChange,
            AllowedVariants = allowed,
            SkipVariants = skip,
            Tags = tags,
            Id = id,
            NeedsAttributes = needsAttributes
        };
    }

    private static string[] ReadStringArray(BinaryReader r)
    {
        var array = new string[r.ReadInt32()];
        for (int i = 0; i < array.Length; i++) array[i] = r.ReadString();
        return array;
    }

    // ---------------- справочник: что и из чего ----------------

    /// <summary>
    /// Рецепты, дающие этот предмет. Код сравнивается точно; чтобы найти
    /// «что-нибудь похожее», есть FindRecipes.
    /// </summary>
    public IReadOnlyList<CraftRecipe> RecipesFor(string outputCode)
    {
        lock (recipes)
            return byOutput.TryGetValue(outputCode, out var list)
                ? list.ToArray()
                : Array.Empty<CraftRecipe>();
    }

    /// <summary>Рецепты, у которых код результата содержит подстроку («pickaxe», «plank-oak»).</summary>
    public IEnumerable<CraftRecipe> FindRecipes(string part, int limit = 20)
    {
        lock (recipes)
            return recipes
                .Where(r => r.OutputCode.Contains(part, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToArray();
    }

    // ================= ЧИСТЫЕ ПРАВИЛА =================
    // Здесь нет ни сети, ни состояния: только арифметика «влезет / не влезет».
    // Ровно поэтому её можно проверить тестами, а не живым сервером.

    /// <summary>
    /// Однородная кучка на складе: ОДИН код, сколько всего штук в сумках,
    /// остаток прочности лучшего экземпляра и предел стака этого предмета.
    /// </summary>
    /// <param name="Durability">
    /// int.MaxValue — предмет вообще не изнашивается (CollectibleObject.Durability == 0).
    /// </param>
    public readonly record struct Pile(string Code, int Count, int Durability, int MaxStack);

    /// <summary>
    /// Чего просит ОДНА клетка сетки и чем её можно набить.
    /// </summary>
    /// <param name="Cell">индекс клетки в сетке 3×3 (строка*3 + столбец)</param>
    /// <param name="PerCraft">сколько штук уходит в клетку на ОДИН сбор</param>
    /// <param name="Tool">
    /// true — это инструмент: он не тратится, а стирается, и в клетку кладётся
    /// РОВНО PerCraft штук независимо от числа повторов
    /// (GridRecipe.ConsumeInputAt снимает прочность, а не количество).
    /// </param>
    /// <param name="Wear">сколько прочности снимает один сбор</param>
    /// <param name="Codes">коды со склада, которые эта клетка примет</param>
    /// <param name="What">как назвать требование человеку</param>
    /// <param name="Group">
    /// Буква ингредиента в шаблоне рецепта («W», «N»). Клетки одной буквы
    /// стараемся набивать ОДНИМ сортом — так делает и живой игрок, и так
    /// рецепту не приходится гадать, какой вариант ставить в результат.
    /// </param>
    public readonly record struct CellNeed(
        int Cell, int PerCraft, bool Tool, int Wear, IReadOnlyList<string> Codes, string What,
        string Group = "");

    /// <summary>Что и сколько кладём в одну клетку по плану.</summary>
    public readonly record struct CellFill(int Cell, string Code, int Count, bool Tool)
    {
        public override string ToString() => $"клетка {Cell}: {Code} ×{Count}";
    }

    /// <summary>План ОДНОЙ закладки сетки: сколько сборов и что по клеткам.</summary>
    public sealed record GridPlan(int Repeats, IReadOnlyList<CellFill> Fills)
    {
        public override string ToString() =>
            $"{Repeats} сбор(ов): " + string.Join("; ", Fills);
    }

    /// <summary>
    /// Разложить рецепт <paramref name="repeats"/> раз в ОДНУ закладку сетки.
    /// null — столько за раз не выходит.
    ///
    /// ГЛАВНОЕ ПРАВИЛО, из-за которого этот расчёт вообще существует:
    /// В ОДНОЙ КЛЕТКЕ ЛЕЖИТ ТОЛЬКО ОДИН СОРТ. Старый счёт складывал весь
    /// подходящий материал в кучу («30 гранита + 30 андезита = 60 камня = 15
    /// сборов»), а сервер такую клетку не примет: два разных стака в один слот
    /// не сливаются, перенос молча не проходит, и бот залипал в ожидании.
    /// Поэтому клетке ищется ОДИН код, которого хватает на все повторы сразу,
    /// и он же обязан влезть в стак (PerCraft*repeats &lt;= MaxStack).
    ///
    /// Клетки перебираются от самой узкой (меньше всего подходящих кодов):
    /// иначе широкий ингредиент забирает единственную кучку, годную соседу.
    /// Из подходящих кодов берётся САМАЯ МАЛЕНЬКАЯ достаточная кучка — большие
    /// оставляем следующим клеткам.
    ///
    /// ЧЕГО ЗДЕСЬ НЕТ И ЭТО НАРОЧНО: прочность одинаковых инструментов считается
    /// по лучшему экземпляру. Рецепт с двумя одинаковыми инструментами в разных
    /// клетках посчитается оптимистично — окончательное слово всё равно за
    /// сервером, и его отказ мы увидим и назовём.
    /// </summary>
    public static GridPlan? PlanFill(IReadOnlyList<Pile> piles, IReadOnlyList<CellNeed> needs,
        int repeats)
    {
        if (repeats <= 0 || needs.Count == 0)
            return null;

        var left = new Dictionary<string, int>(StringComparer.Ordinal);
        var wear = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var p in piles)
        {
            left[p.Code] = p.Count;
            wear[p.Code] = p.Durability;
            stack[p.Code] = Math.Max(1, p.MaxStack);
        }

        // Какой сорт уже выбран для этой буквы рецепта — при прочих равных
        // держимся его: живой игрок кладёт восемь досок одного дерева, а не
        // мешанину, и рецепту не приходится гадать, какой вариант ставить
        // в результат
        var byGroup = new Dictionary<string, string>(StringComparer.Ordinal);

        var fills = new List<CellFill>(needs.Count);
        foreach (var need in needs.OrderBy(n => n.Codes.Count).ThenBy(n => n.Cell))
        {
            // Инструмент кладётся один раз на всю закладку, но прочности
            // должно хватить на каждый сбор
            int want = need.Tool ? need.PerCraft : need.PerCraft * repeats;
            string? pick = null;
            foreach (var code in need.Codes)
            {
                if (!left.TryGetValue(code, out int have) || have < want)
                    continue;
                if (stack.TryGetValue(code, out int max) && want > max)
                    continue;
                if (need.Tool && wear.GetValueOrDefault(code) < (long)need.Wear * repeats)
                    continue;
                // Сорт своей буквы бьёт любой другой; иначе — самая маленькая
                // достаточная кучка
                if (need.Group.Length > 0 && byGroup.GetValueOrDefault(need.Group) == code)
                {
                    pick = code;
                    break;
                }
                if (pick == null || have < left[pick])
                    pick = code;
            }
            if (pick == null)
                return null;
            left[pick] -= want;
            if (need.Group.Length > 0)
                byGroup.TryAdd(need.Group, pick);
            fills.Add(new CellFill(need.Cell, pick, want, need.Tool));
        }
        return new GridPlan(repeats, fills.OrderBy(f => f.Cell).ToArray());
    }

    /// <summary>
    /// Сколько сборов подряд влезет в ОДНУ закладку сетки, но не больше limit.
    /// Ищем двоичным поиском: если закладка на N сборов сходится, то на N−1
    /// она сходится тем более (каждой клетке нужно меньше того же самого),
    /// поэтому «сходится» — свойство монотонное.
    /// </summary>
    public static int MaxRepeats(IReadOnlyList<Pile> piles, IReadOnlyList<CellNeed> needs,
        int limit)
    {
        if (limit <= 0 || needs.Count == 0 || PlanFill(piles, needs, 1) == null)
            return 0;
        int low = 1, high = limit;
        while (low < high)
        {
            int mid = low + (high - low + 1) / 2;
            if (PlanFill(piles, needs, mid) != null)
                low = mid;
            else
                high = mid - 1;
        }
        return low;
    }

    /// <summary>
    /// Сколько ещё штук такого предмета влезет в эти слоты: добивка начатых
    /// стопок плюс пустые слоты целиком.
    ///
    /// Считать это ОБЯЗАТЕЛЬНО ДО закладки: shift-клик по слоту вывода на
    /// сервере кончается InventoryManager.DropItem(outputSlot, fullStack: true),
    /// то есть непоместившееся падает на землю. Для бота, который уходит
    /// мостить дорогу, это потерянная работа.
    /// </summary>
    public static int RoomFor(IEnumerable<SlotContent> slots, string code, int maxStack)
    {
        int max = Math.Max(1, maxStack);
        int room = 0;
        foreach (var s in slots)
        {
            if (s.IsEmpty)
                room += max;
            else if (string.Equals(s.Code, code, StringComparison.Ordinal))
                room += Math.Max(0, max - s.Count);
        }
        return room;
    }

    // ---------------- склад: перевод живого состояния в чистые правила ----------------

    /// <summary>
    /// Кучки материала, которыми бот вправе распоряжаться при крафте.
    ///
    /// Какие слоты вообще годятся — решает <see cref="SelfState.CarrySlots"/>:
    /// одежда, курсор, земля под ногами и творческий инвентарь туда не входят,
    /// как и первые четыре слота «backpack» (это САМИ НАДЕТЫЕ СУМКИ, и без
    /// этого знания бот скормил бы рецепту собственный рюкзак). Своей копии
    /// этого правила здесь нет нарочно — оно одно на весь бот.
    /// </summary>
    private List<Pile> Piles()
    {
        var byCode = new Dictionary<string, (int Count, int Best)>(StringComparer.Ordinal);
        foreach (var (_, _, content) in ctx.Self.CarrySlots())
        {
            if (content.IsEmpty || content.Code is not { } code)
                continue;
            // Предмет без прочности не стирается вовсе — для расчёта износа
            // у него бесконечный запас (иначе доска «ломалась» бы от рецепта)
            int durability = ctx.World.Durability(code) > 0 ? content.Durability : int.MaxValue;
            var had = byCode.GetValueOrDefault(code);
            byCode[code] = (had.Count + content.Count, Math.Max(had.Best, durability));
        }
        return byCode
            .Select(kv => new Pile(kv.Key, kv.Value.Count, kv.Value.Best,
                Math.Max(1, ctx.World.MaxStackSize(kv.Key))))
            .ToList();
    }

    /// <summary>
    /// Требования рецепта по клеткам сетки 3×3, уже сведённые к тому, что
    /// реально лежит в сумках. Клетка рецепта (строка, столбец) ложится в сетку
    /// без смещения: индекс = строка*3 + столбец. Сервер проверяет рецепт во
    /// всех допустимых смещениях (GridRecipe.Matches), нам достаточно левого
    /// верхнего угла — но тогда остальные клетки обязаны быть пусты.
    /// </summary>
    private List<CellNeed> Needs(CraftRecipe recipe, IReadOnlyList<Pile> piles)
    {
        var needs = new List<CellNeed>();
        for (int row = 0; row < recipe.Height; row++)
        for (int col = 0; col < recipe.Width; col++)
        {
            int index = row * recipe.Width + col;
            if (index >= recipe.Cells.Length || recipe.Cells[index] is not { } ing)
                continue;
            var codes = piles.Where(p => ing.Matches(ctx.World, p.Code))
                             .Select(p => p.Code)
                             .ToArray();
            needs.Add(new CellNeed(
                Cell: row * GridWidth + col,
                PerCraft: ing.Quantity,
                Tool: !ing.Consumed,
                Wear: Math.Max(1, -ing.DurabilityChange),
                Codes: codes,
                // ИМЕНЕМ, А НЕ ВИТРИНОЙ. «What» уезжает в отказы («не хватает:
                // stone-* (надо 4, есть 0)»), а по ним бот ИЩЕТ материал на
                // складе. Витрина «stone-* ×4» ломала поиск целиком — см.
                // CraftIngredient.Name
                What: ing.Name,
                Group: ing.Id ?? ""));
        }
        return needs;
    }

    /// <summary>Сколько такого предмета лежит в сумках (сетка крафта не в счёт).</summary>
    public int CountOwned(string code)
    {
        int total = 0;
        foreach (var (_, _, content) in ctx.Self.CarrySlots())
            if (string.Equals(content.Code, code, StringComparison.Ordinal))
                total += content.Count;
        return total;
    }

    // ---------------- справки «могу / не могу» ----------------

    /// <summary>
    /// Сколько раз подряд бот может собрать этот рецепт прямо сейчас.
    /// Считается по ОДНОЙ закладке сетки: сколько сборов туда влезет.
    /// </summary>
    public int MaxCraftable(CraftRecipe recipe, int limit = 999)
    {
        var piles = Piles();
        return MaxRepeats(piles, Needs(recipe, piles), limit);
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: рецепт заперт ЧЕРТОЙ КЛАССА, и открыть её боту нечем.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЭТО НАПИСАНО — ЛУК. На простой лук
    /// (<c>bow-simple</c>) черты не надо, а на грубый (<c>bow-crude</c>) надо
    /// «bowyer» — при том что материалы у грубого ДЕШЕВЛЕ и код его КОРОЧЕ.
    /// Разбор «что из чего» (<see cref="Stock.PlanFor"/>) сортирует кандидатов
    /// по тиру, а при равном — по длине кода; у всех луков тир 0, и первым в
    /// работу шёл именно <c>bow-crude</c>. Бот сходил бы за верёвкой, разложил
    /// сетку — и получил от сервера немой отказ: «рецепт не собран». И так
    /// каждый заход снаряжения, вечно.
    ///
    /// ПОЧЕМУ НЕ «А ЕСТЬ ЛИ У МЕНЯ ЭТА ЧЕРТА». Потому что бот своих черт НЕ
    /// ЗНАЕТ: класс персонажа сервер держит у сущности игрока, а бот его не
    /// читает ни одной строкой. Выдумать «наверное, есть» нельзя (правило 6),
    /// а обещать сбор, который сервер не примет, — это молчаливый отказ,
    /// который запрещён правилом 4. Значит честный ответ один: рецепт с чертой
    /// в работу НЕ БЕРЁМ и говорим об этом вслух.
    ///
    /// ЧТО ЭТИМ ТЕРЯЕТСЯ, И ЭТО СКАЗАНО ВСЛУХ: бот с классом, у которого черта
    /// ЕСТЬ, откажется от рецепта, который ему доступен. Руками такой рецепт
    /// собрать по-прежнему можно — <see cref="CraftAsync"/> сюда не смотрит,
    /// он смотрит на выбор лучшего рецепта; отказ сервера в этом случае назовёт
    /// черту по имени.
    ///
    /// СКОЛЬКО ИМЕННО ТЕРЯЕТСЯ — ПЕРЕСЧИТАНО ПО ВСЕЙ ПАПКЕ РЕЦЕПТОВ, А НЕ ПО
    /// ОДНОМУ ЛУКУ. Здесь стояло «цена этого правила: охотник не соберёт грубый
    /// лук и безперьевую стрелу», и это было занижением В ШЕСТЬДЕСЯТ РАЗ:
    /// обход <c>assets/survival/recipes/</c> 1.22.7 даёт <c>requiresTrait</c>
    /// на 124 записях сетки —
    /// <c>clothier</c> 115, <c>bowyer</c> 3, <c>improviser</c> 3,
    /// <c>merciless</c> 2, <c>tinkerer</c> 1.
    ///
    /// А ВОТ «ЧТО ЭТИМ ЗАБИРАЕТСЯ» ЗДЕСЬ СТОЯЛО НЕПРАВДОЙ, И ЭТО ВАЖНЕЕ САМОГО
    /// ЧИСЛА. Было написано: «ВСЕ круглые щиты заперты чертой <c>clothier</c>,
    /// бот, которому роль велела носить щит, собрать его не сможет». Пересчёт
    /// по <c>grid/tool/roundshield.json</c> 1.22.7 руками (28 рецептов, из них
    /// с чертой 22) даёт обратное: у КАЖДОГО из трёх кодов есть ОТКРЫТЫЙ
    /// рецепт, а <c>shield-woodmetal</c> открыт целиком. Фальксы тоже открыты —
    /// <c>merciless</c> держит только <c>blade-blackguard-iron</c> и
    /// <c>shield-blackguard</c>.
    ///
    /// ЧТО ИЗ ЭТОГО СЛЕДУЕТ. Правило берёт рецепты ПО-РЕЦЕПТНО, а не по коду
    /// вещи: заперт один — берётся соседний открытый. Значит в ванилле
    /// <b>оно не отнимает у бота ничего</b>: из кодов с запертым рецептом
    /// целиком заперты только те, у которых открытого нет вовсе —
    /// <c>bow-crude</c>, <c>bow-recurve</c>, <c>arrow-crude</c> (<c>bowyer</c>),
    /// <c>sling</c> (<c>improviser</c>), <c>spear-generic-hacking</c>
    /// (<c>tinkerer</c>), <c>blade-blackguard-iron</c>, <c>shield-blackguard</c>
    /// (<c>merciless</c>) и одежда с <c>linen-*</c>/<c>sewingkit</c>
    /// (<c>clothier</c>, он и даёт 115 записей из 124).
    ///
    /// НЕ ВЫЧЁРКИВАЙ ЭТОТ АБЗАЦ, ЕСЛИ СОБРАЛСЯ «ПОЧИНИТЬ» ЩИТ: требования щита
    /// в запасах не существует вовсе (<c>Shielding</c> только докладывает «щита
    /// нет в сумках — рука свободна»), и прежняя запись первым делом отговорила
    /// бы следующего от щита выдуманной ценой.
    ///
    /// А ВОТ ЧЕРТА НА КЛАССЕ И ПРАВДА ОДНА К ОДНОМУ (characterclasses.json):
    /// <c>bowyer</c> — только hunter, <c>clothier</c> — только tailor,
    /// <c>improviser</c> — только malefactor, <c>merciless</c> — только
    /// blackguard, <c>tinkerer</c> — только clockmaker; у <c>commoner</c> черт
    /// нет вовсе.
    ///
    /// Поведение при таком радиусе остаётся тем же и по той же причине: класса
    /// своего бот не читает НИ ОДНОЙ строкой, и обещать сбор, который сервер не
    /// примет, нельзя. Но радиус обязан быть назван, а не приписан двум
    /// рецептам.
    /// </summary>
    public static bool LockedByTrait(CraftRecipe recipe) =>
        recipe.RequiresTrait is { Length: > 0 };

    /// <summary>Бот может собрать этот предмет прямо сейчас (хотя бы одним рецептом).</summary>
    public bool CanCraft(string outputCode, int quantity = 1) =>
        BestRecipeFor(outputCode, quantity) != null;

    /// <summary>
    /// Лучший рецепт для предмета: тот, который сейчас можно повторить больше раз.
    /// null — либо рецепта нет, либо не хватает материалов (см. Missing).
    /// </summary>
    public CraftRecipe? BestRecipeFor(string outputCode, int quantity = 1)
    {
        var piles = Piles();
        CraftRecipe? best = null;
        int bestTimes = 0;
        foreach (var r in RecipesFor(outputCode))
        {
            if (r.Width > GridWidth || r.Height > GridWidth)
                continue;
            // Черта класса — см. LockedByTrait: сервер такой сбор не примет,
            // и обещать его значит вечно раскладывать сетку впустую
            if (LockedByTrait(r))
                continue;
            int times = MaxRepeats(piles, Needs(r, piles), Math.Max(1, quantity));
            if (times > bestTimes)
            {
                bestTimes = times;
                best = r;
            }
        }
        return bestTimes > 0 ? best : null;
    }

    /// <summary>
    /// Чего не хватает на ОДИН сбор рецепта: (описание ингредиента, надо, есть).
    /// Пустой список — материалы есть.
    ///
    /// «Есть» считается ПО ОДНОМУ КОДУ, а не суммой всего подходящего: клетка
    /// держит один сорт, и «камня 60» при 30 граните и 30 андезите — враньё,
    /// из-за которого бот брался за рецепт, который не собрать.
    /// </summary>
    public IReadOnlyList<(string Ingredient, int Need, int Have)> Missing(CraftRecipe recipe)
    {
        var piles = Piles();
        var needs = Needs(recipe, piles);
        var gaps = new List<(string, int, int)>();
        if (needs.Count == 0)
        {
            gaps.Add((recipe.OutputCode, 1, 0));
            return gaps;
        }

        var left = piles.ToDictionary(p => p.Code, p => p.Count, StringComparer.Ordinal);
        foreach (var need in needs.OrderBy(n => n.Codes.Count).ThenBy(n => n.Cell))
        {
            string? best = null;
            foreach (var code in need.Codes)
                if (best == null || left[code] > left[best])
                    best = code;
            int have = best == null ? 0 : left[best];
            if (have < need.PerCraft)
                gaps.Add((need.What, need.PerCraft, have));
            else
                left[best!] -= need.PerCraft;
        }
        // ОДНО И ТО ЖЕ — ОДНОЙ СТРОКОЙ. Нехватка считается ПО КЛЕТКАМ, а
        // полублок дороги просит камень в двух клетках, и человек читал в
        // журнале: «не хватает: stone-meteorite-iron (надо 1, есть 0),
        // stone-meteorite-iron (надо 1, есть 0)». Дважды названное одно
        // выглядит как ошибка счёта и мешает понять, сколько же надо
        return Merge(gaps);
    }

    /// <summary>
    /// СХЛОПНУТЬ ОДИНАКОВЫЕ НЕХВАТКИ: сколько клеток ни просило бы один и тот
    /// же материал, человеку и снабжению это ОДНА строка с суммой.
    ///
    /// «Есть» берём наименьшее из встреченных: клетки считались по одной и той
    /// же куче, и завысить остаток тут значило бы соврать в меньшую сторону про
    /// нехватку.
    ///
    /// Чистое правило — оно уезжает и в отказ человеку, и в список покупок
    /// снабжения (<c>Depot.Shopping</c>), и проверять его надо тестом.
    /// </summary>
    public static IReadOnlyList<(string Ingredient, int Need, int Have)> Merge(
        IReadOnlyList<(string Ingredient, int Need, int Have)> gaps)
    {
        var order = new List<string>();
        var sum = new Dictionary<string, (int Need, int Have)>(StringComparer.Ordinal);
        foreach (var (code, need, have) in gaps)
        {
            if (sum.TryGetValue(code, out var had))
                sum[code] = (had.Need + need, Math.Min(had.Have, have));
            else
            {
                sum[code] = (need, have);
                order.Add(code);
            }
        }
        return order.Select(code => (code, sum[code].Need, sum[code].Have)).ToList();
    }

    /// <summary>
    /// ЧЕГО НЕ ХВАТАЕТ НА ЭТУ ВЕЩЬ — СПИСКОМ КОДОВ, а не прозой.
    ///
    /// ЗАЧЕМ. До сих пор единственным способом узнать нехватку снаружи был
    /// РАЗБОР ОТКАЗА словами (<see cref="WorkSupply.Lacking"/>). Разбор прозы
    /// живёт ровно до первой правки в тексте — и уже умер: витрина «stone-* ×4»
    /// давала боту код материала «4», и рейс на склад уходил за несуществующим.
    /// Прозу разбирать всё равно придётся (навык отвечает строкой, и знать про
    /// дорогу бегун очереди не обязан), но у КРАФТА теперь есть прямой ответ, и
    /// спрашивают его там, где рецепт под рукой.
    ///
    /// Пустой список значит одно из двух и оба честные: либо материал есть,
    /// либо рецепта на эту вещь в мире нет вовсе — второе видно по
    /// <see cref="RecipesFor"/>.
    ///
    /// Форма ответа та же, что у разбора прозы, — нарочно: рейс на склад
    /// делает ОДИН механизм (<c>Depot.SupplyAsync</c>), и подавать ему два
    /// разных списка значило бы завести вторую механику докупки.
    /// </summary>
    public IReadOnlyList<(string Code, int Need, int Have)> Shortfall(string outputCode)
    {
        // ЗАПЕРТЫЕ ЧЕРТОЙ КЛАССА СЮДА НЕ БЕРЁМ — ТЕМ ЖЕ ПРАВИЛОМ, ЧТО И ВЕЗДЕ
        // (см. LockedByTrait). Здесь этого вопроса не было, и выходил пустой
        // рейс через полкарты: CraftAsync звал Shortfall, тот отвечал «не
        // хватает верёвки ×3» по рецепту bow-crude, снабжение гнало бота за
        // верёвкой — а вернувшись, он получал от BestRecipeFor null и отказ
        // «требует черту класса bowyer». И так каждый раз. Ровно тот же путь
        // проходит дорога (Roads.WhyCannotMake): она называла нехватку
        // материала на рецепт, который сама же потом отвергала
        var fits = RecipesFor(outputCode)
            .Where(r => r.Width <= GridWidth && r.Height <= GridWidth && !LockedByTrait(r))
            .ToList();
        if (fits.Count == 0)
            return [];

        // Рецептов на одну вещь бывает несколько. Хоть по одному материал есть —
        // нехватки НЕТ ВОВСЕ, и никуда идти не надо: спрашивать склад в этом
        // случае значило бы гнать бота через полкарты за тем, что уже в сумке
        var short_ = fits.Select(Missing).ToList();
        if (short_.Any(gaps => gaps.Count == 0))
            return [];

        return Gaps(short_);
    }

    /// <summary>
    /// ЧЕГО НЕ ХВАТАЕТ НА ЛУЧШИЙ ИЗ РЕЦЕПТОВ — одно место на весь класс: и
    /// список для снабжения (<see cref="Shortfall"/>), и отказ человеку
    /// (<see cref="WhyCannot"/>) обязаны говорить про ОДИН И ТОТ ЖЕ рецепт.
    /// Разойдись они — бот сходил бы за камнем, а отказ называл бы железо.
    /// </summary>
    private IReadOnlyList<(string Code, int Need, int Have)> Gaps(
        IReadOnlyList<IReadOnlyList<(string Ingredient, int Need, int Have)>> variants)
    {
        var reach = Reachable;
        int best = BestVariant(
            variants.Select(v => (IReadOnlyList<(string, int, int)>)
                v.Select(g => (g.Ingredient, g.Need, g.Have)).ToList()).ToList(),
            reach);
        return variants[best].Select(g => (Code: g.Ingredient, g.Need, g.Have)).ToList();
    }

    /// <summary>
    /// СКОЛЬКО ЭТОГО БОТ ВООБЩЕ МОЖЕТ ДОСТАТЬ — спрашиваем снабжение (сумки
    /// плюс опись всех сундуков, куда бот заглядывал). Снабжения нет — считаем
    /// только сумки: врать про недоступный сундук нельзя.
    /// </summary>
    private int Reachable(string code) =>
        ctx.Supply is { } supply ? supply.Reachable(code)
        : ctx.Depot is { } depot ? depot.AtHand(code)
        : 0;

    /// <summary>
    /// КАКОЙ ИЗ РЕЦЕПТОВ НА ОДНУ ВЕЩЬ БРАТЬ, КОГДА МАТЕРИАЛА НЕТ НИ НА ОДИН.
    /// ЧИСТОЕ ПРАВИЛО.
    ///
    /// ЖИВОЙ СЛУЧАЙ 16.08, ради которого оно написано:
    ///   «на дорожное (stonepath-free) не хватает: stone-meteorite-iron
    ///    (надо 4, есть 0), soil-* (надо 1, есть 0)»
    /// Метеоритным железом дорогу не мостит никто. Сервер разворачивает рецепт
    /// на КАЖДУЮ породу камня, а прежний выбор брал ту, что стояла в реестре
    /// первой, — и слал бота за тем, чего нет ни в сумке, ни на складе, ни в
    /// земле поблизости. Рядом при этом лежало два сундука обычного камня.
    ///
    /// Мерка «доступно» приходит снаружи (<paramref name="reachable"/>) и
    /// считает сумки и опись; порядок в реестре не участвует вовсе — он бьётся
    /// только при полном равенстве, и тогда берётся первый, как и раньше.
    ///
    /// Три мерки по старшинству:
    ///   1) сколько составляющих ВООБЩЕ НЕ ДОСТАТЬ — такой рецепт безнадёжен;
    ///   2) сколько штук не хватит даже после похода — короче рейс;
    ///   3) сколько не хватает прямо сейчас — меньше нести.
    /// </summary>
    /// <param name="variants">нехватки по каждому рецепту (пустой — нехватки нет)</param>
    /// <param name="reachable">сколько такого кода бот может достать всего</param>
    public static int BestVariant(
        IReadOnlyList<IReadOnlyList<(string Code, int Need, int Have)>> variants,
        // System.Func НАЗВАН ПОЛНОСТЬЮ НАРОЧНО: в Vintagestory.API.Common есть
        // свой Func<>, и с появлением здесь using-а этого пространства имя стало
        // неоднозначным — сборка встала целиком (та же беда описана у Hands.cs)
        System.Func<string, int> reachable)
    {
        int best = -1;
        (int Hopeless, int AfterTrip, int Now) score = default;
        for (int i = 0; i < variants.Count; i++)
        {
            int hopeless = 0, afterTrip = 0, now = 0;
            foreach (var (code, need, have) in variants[i])
            {
                int got = Math.Max(have, reachable(code));
                if (got < need)
                {
                    hopeless++;
                    afterTrip += need - got;
                }
                now += Math.Max(0, need - have);
            }
            var mine = (hopeless, afterTrip, now);
            if (best < 0 || Less(mine, score))
            {
                best = i;
                score = mine;
            }
        }
        return Math.Max(0, best);

        static bool Less((int H, int A, int N) a, (int H, int A, int N) b) =>
            a.H != b.H ? a.H < b.H : a.A != b.A ? a.A < b.A : a.N < b.N;
    }

    /// <summary>
    /// Что бот может сделать прямо сейчас: код результата и сколько штук.
    /// Отсортировано по коду — список длинный, его обычно фильтруют.
    /// </summary>
    /// <param name="maxTimes">
    /// Потолок повторов на рецепт. Рецептов в реестре больше тысячи, а полное
    /// моделирование «сколько влезет» на каждый — это заметные секунды;
    /// для ответа «что я могу» хватает небольшого числа.
    /// </param>
    public IReadOnlyList<(string Code, int Count)> WhatCanICraft(int limit = 100, int maxTimes = 16)
    {
        // Снимок склада делаем один на весь обход — иначе он пересобирается
        // на каждый из полутора тысяч рецептов
        var piles = Piles();
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in All)
        {
            if (r.Width > GridWidth || r.Height > GridWidth)
                continue;
            // «МОГУ» — ЭТО ОБЕЩАНИЕ, и обещать закрытое чертой класса нельзя
            // (см. LockedByTrait). Иначе «!могу» выдавал бы человеку список, в
            // котором половина строк кончается немой сеткой у сервера
            if (LockedByTrait(r))
                continue;
            var needs = Needs(r, piles);
            // Быстрый отсев: если хоть одной клетке нечего дать вовсе,
            // моделирование гонять незачем
            if (needs.Count == 0 || needs.Any(n => n.Codes.Count == 0))
                continue;
            int times = MaxRepeats(piles, needs, maxTimes);
            if (times <= 0)
                continue;
            int total = times * r.OutputQuantity;
            if (!best.TryGetValue(r.OutputCode, out int had) || total > had)
                best[r.OutputCode] = total;
        }
        return best.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                   .Take(limit)
                   .Select(kv => (kv.Key, kv.Value))
                   .ToArray();
    }

    // ---------------- собственно крафт ----------------

    /// <summary>Результат крафта: сколько штук реально прибавилось в инвентаре.</summary>
    public sealed record CraftResult(bool Success, int Crafted, string Message)
    {
        public override string ToString() => Message;
    }

    /// <summary>
    /// Собрать предмет по рецепту сетки.
    ///
    /// Успехом считается ТОЛЬКО прирост предмета в собственных сумках
    /// (правило «не врать о результате»): пакет ушёл — это ещё ничего.
    /// Каждый шаг подтверждается сервером и называется вслух:
    ///   что положили в каждую клетку и что сервер туда положил на самом деле;
    ///   что сервер собрал в слоте вывода;
    ///   сколько штук реально прибавилось в сумках после shift-клика.
    ///
    /// СЕТКА РАЗБИРАЕТСЯ ВСЕГДА — и после успеха, и после любого отказа, и при
    /// отмене (finally). Оставленное в ней выпадет при смерти
    /// (InventoryCraftingGrid.OnOwningEntityDeath) и сломает следующий рецепт:
    /// RecipeBase.MatchesAtPosition сверяет все девять клеток. Если что-то
    /// всё-таки осталось (сумки полны) — об этом говорится отдельной строкой,
    /// а не молчанием.
    /// </summary>
    public async Task<CraftResult> CraftAsync(string outputCode, int quantity = 1,
        CancellationToken ct = default)
    {
        if (quantity <= 0)
            return new CraftResult(false, 0, "количество должно быть больше нуля");
        if (!Ready)
            return new CraftResult(false, 0, LoadError ?? "реестр рецептов ещё не пришёл от сервера");

        // Сперва о рецепте, потом о сетке: «такого не делают в этом мире» —
        // факт реестра, и человеку он полезнее, чем «сетки нет». Обратный
        // порядок прятал отсутствие рецепта за сообщением о сетке
        if (RecipesFor(outputCode).Count == 0)
            return new CraftResult(false, 0, $"рецепта на \"{outputCode}\" в реестре сервера нет");

        string? gridId = ctx.Self.GetInventoryId("craftinggrid");
        if (gridId == null)
            return new CraftResult(false, 0,
                "сервер не прислал сетку крафта — бот ещё не вошёл в мир или он в творческом режиме");

        var recipe = BestRecipeFor(outputCode, quantity);

        // МАТЕРИАЛА НЕТ — СХОДИТЬ ЗА НИМ НА СКЛАД И ПОПРОБОВАТЬ СНОВА.
        //
        // ЗАКАЗ 15.08: «не хватает материала на крафт → спросить опись склада →
        // если там есть → сходить, взять, вернуться, доделать»; и отдельно —
        // «это должно работать и для дороги, и для инструментов, и вообще для
        // любого крафта посреди работы». Вот это «вообще»: сюда приходит КАЖДЫЙ
        // крафт бота — и снаряжение, и дорога, и рука человека из панели, — и
        // ни один из них про склад знать не обязан.
        //
        // Рейса как такового здесь нет: ходит склад (Depot.SupplyAsync), он же
        // спрашивает опись и он же решает, не слишком ли далеко. Здесь только
        // «спросить рецепт, чего не хватает» и «попробовать ещё раз»
        if (recipe == null && await RestockAsync(outputCode, ct) > 0)
            recipe = BestRecipeFor(outputCode, quantity);

        if (recipe == null)
            return new CraftResult(false, 0, WhyCannot(outputCode));

        int made = 0;
        string stop = "";
        try
        {
            while (made < quantity)
            {
                ct.ThrowIfCancellationRequested();

                // 1. СЕТКА ОБЯЗАНА БЫТЬ ПУСТА. Сервер сверяет все девять клеток
                //    (RecipeBase.MatchesAtPosition): лишний камень в углу — и
                //    рецепт «не собирается» без единого слова о причине
                var stuck = await EmptyGridAsync(gridId, ct);
                if (stuck.Count > 0)
                {
                    stop = "сетку не удалось освободить перед закладкой: " + Describe(stuck);
                    break;
                }

                // 2. Сколько сборов имеет смысл: и по нужде, и по месту в сумках.
                //    Место считаем ДО закладки — иначе сервер выбросит остаток
                //    результата на землю (см. заголовок файла, пункт 4)
                int outStack = Math.Max(1, ctx.World.MaxStackSize(recipe.OutputCode));
                int room = RoomFor(ctx.Self.CarrySlots().Select(s => s.Content),
                    recipe.OutputCode, outStack);
                if (room < recipe.OutputQuantity)
                {
                    stop = $"некуда положить {recipe.OutputCode}: в сумках место на {room} шт., " +
                           $"а один сбор даёт {recipe.OutputQuantity}";
                    break;
                }

                int want = (int)Math.Ceiling((quantity - made) / (double)recipe.OutputQuantity);
                want = Math.Min(want, room / recipe.OutputQuantity);

                var piles = Piles();
                var needs = Needs(recipe, piles);
                var plan = PlanFill(piles, needs, MaxRepeats(piles, needs, want));
                if (plan == null)
                {
                    stop = made == 0 ? WhyCannot(outputCode) : "";
                    break;
                }

                // 3. Разложить и сказать вслух, что легло НА САМОМ ДЕЛЕ
                OnLog?.Invoke($"закладка: {plan} (рецепт {recipe.Name})");
                if (await LayOutAsync(gridId, plan, ct) is { } refused)
                {
                    stop = refused;
                    break;
                }

                // 4. Сервер сам проверяет рецепт и кладёт результат в слот вывода.
                //    Пока в слоте 9 не появился ИМЕННО НАШ код — рецепт не собран.
                //    Проверять «слот не пуст» мало: там мог остаться чужой
                //    предпросмотр от прошлой закладки
                if (!await WaitAsync(() => IsOutput(gridId, recipe.OutputCode), ct))
                {
                    var seen = GridSlot(gridId, OutputSlot);
                    stop = seen.IsEmpty
                        ? $"сервер не признал рецепт \"{recipe.Name}\" собранным" +
                          (recipe.RequiresTrait != null
                              ? $" (нужна черта класса \"{recipe.RequiresTrait}\")"
                              : " — составляющие в сетке он видит, а совпадения нет")
                        : $"сервер собрал из сетки не то: в слоте вывода {seen}, ждали {recipe.OutputCode}";
                    break;
                }
                OnLog?.Invoke($"сервер собрал: в слоте вывода {GridSlot(gridId, OutputSlot)}");

                // 5. Забрать по-человечески: shift-клик по слоту вывода. Один
                //    такой клик уносит весь тираж закладки (ItemSlotCraftingOutput.
                //    CraftMany), поэтому цикла «взять по одной» здесь нет
                int before = CountOwned(recipe.OutputCode);
                await ShiftClickAsync(gridId, OutputSlot);
                int got = await SettleAsync(() => CountOwned(recipe.OutputCode), before, ct);
                if (got <= 0)
                {
                    stop = $"сервер не отдал результат: shift-клик по слоту {OutputSlot} " +
                           $"инвентаря {gridId} ничего не прибавил в сумках " +
                           $"(в слоте вывода теперь {GridSlot(gridId, OutputSlot)})";
                    break;
                }
                made += got;
                OnLog?.Invoke($"забрал {got} × {recipe.OutputCode} (всего {made} из {quantity})");
            }
        }
        finally
        {
            // РАЗБИРАЕМ СЕТКУ ВСЕГДА. Токен здесь намеренно не тот: даже когда
            // работу отменили, составляющие обязаны вернуться в сумки, иначе
            // они просто пропадут для бота
            var left = await EmptyGridAsync(gridId, CancellationToken.None);
            if (left.Count > 0)
                OnLog?.Invoke("В СЕТКЕ ОСТАЛОСЬ (убрать некуда, сумки полны): " + Describe(left));
        }

        if (made > 0)
        {
            string tail = made >= quantity ? ""
                : $"; просили {quantity}, дальше не вышло: " +
                  (stop.Length > 0 ? stop : "материалы кончились");
            return Done(true, made, $"сделал {made} × {recipe.OutputCode} (рецепт {recipe.Name}){tail}");
        }
        return Done(false, 0, stop.Length > 0
            ? stop
            : $"по рецепту \"{recipe.Name}\" в сумках ничего не прибавилось");
    }

    /// <summary>
    /// БРАТЬ ЛИ НЕДОСТАЮЩИЙ МАТЕРИАЛ СО СКЛАДА. Здесь это только выключатель на
    /// сам крафт: «идти ли», «как далеко» и «сколько нести» — числа склада
    /// (<see cref="Depot.SupplyForCraft"/> и соседние), и второй их копии тут
    /// нет нарочно.
    ///
    /// Зачем отдельный выключатель. Крафт зовут и оттуда, где уходить с места
    /// нельзя вовсе (примерка в панели, разбор «что я умею»); такому вызову
    /// достаточно одной строки <c>MayTakeFromDepot = false</c>.
    /// </summary>
    public bool MayTakeFromDepot { get; set; } = true;

    /// <summary>
    /// СПРОСИТЬ ОПИСЬ И ПРИНЕСТИ НЕДОСТАЮЩЕЕ на эту вещь. Возвращает, сколько
    /// штук прибавилось в сумках (0 — никуда не ходили или ничего не нашлось).
    ///
    /// Отказы называются вслух все до одного: молчаливое «не хватает
    /// материалов» при полном сундуке дома — ровно та беда, с которой заказчик
    /// и пришёл.
    /// </summary>
    public async Task<int> RestockAsync(string outputCode, CancellationToken ct = default)
    {
        if (!MayTakeFromDepot)
            return 0;

        // Спрашиваем РЕЦЕПТ, а не разбираем собственный отказ словами: имя
        // материала здесь есть в чистом виде, и терять его по дороге незачем
        var lacking = Shortfall(outputCode);
        if (lacking.Count == 0)
            return 0;

        // ЦЕПОЧКА ОДНА НА ВЕСЬ БОТ И ЖИВЁТ ОНА В СНАБЖЕНИИ: сумки → склад этой
        // работы → дом → крафт → добыть рядом. Прежде здесь звался ОДИН склад,
        // и «смотреть дома недостающие» не делал никто — ровно на этом и встала
        // дорога 16.08 при шестнадцати сундуках дома
        if (ctx.Supply is not { } supply)
        {
            OnLog?.Invoke($"на {outputCode} не хватает {Depot.Names(lacking)}, " +
                          "а снабжение к боту не подключено — принести неоткуда");
            return 0;
        }

        // Крафт составляющих снабжению здесь запрещать не надо: от круга
        // «собери из собранного» бережёт счётчик витков (Supply.MaxChainDepth)
        var поиск = await supply.GetAsync(lacking, ct: ct);
        OnLog?.Invoke($"{outputCode}: {поиск.Reason}");
        return поиск.Brought;
    }

    /// <summary>
    /// ПОЧЕМУ ЭТОГО НЕ СДЕЛАТЬ — по фактам реестра и склада, а не «не вышло».
    ///
    /// Открыто нарочно: этот же ответ показывают человеку дорога и снаряжение,
    /// и он обязан называть ТОТ ЖЕ рецепт, за материалом которого пойдёт
    /// снабжение (см. <see cref="Gaps"/>).
    /// </summary>
    public string WhyCannot(string outputCode)
    {
        var known = RecipesFor(outputCode);
        if (known.Count == 0)
            return $"рецепта на \"{outputCode}\" в реестре сервера нет";
        var fits = known.Where(r => r.Width <= GridWidth && r.Height <= GridWidth).ToList();
        if (fits.Count == 0)
            return $"рецепт на \"{outputCode}\" требует сетку " +
                   $"{known[0].Width}×{known[0].Height}, а у игрока она 3×3";
        // ЧЕРТА КЛАССА — ОТДЕЛЬНЫЙ ОТВЕТ, А НЕ «НЕ ХВАТАЕТ МАТЕРИАЛОВ». Разница
        // не косметическая: за материалом человек пошлёт бота, а черту носить
        // неоткуда, и посылать бесполезно. Так же читается и грубый лук:
        // «bow-crude — нужна черта класса bowyer», а не «не хватает верёвки»
        var open = fits.Where(r => !LockedByTrait(r)).ToList();
        if (open.Count == 0)
            return $"рецепт на \"{outputCode}\" есть, но требует черту класса " +
                   $"\"{fits[0].RequiresTrait}\" — своего класса бот не знает вовсе, " +
                   "и обещать сбор, который сервер не примет, не станет";
        fits = open;
        // РЕЦЕПТ НАЗЫВАЕТСЯ ТОТ ЖЕ, ЗА КОТОРЫМ ПОЙДЁТ СНАБЖЕНИЕ (см. Gaps):
        // прежде здесь стоял fits[0] — первый по реестру, — и человек читал
        // «не хватает stone-meteorite-iron», пока бот нёс со склада гранит
        var gaps = Gaps(fits.Select(Missing).ToList());
        if (gaps.Count == 0)
            return $"материалы на \"{outputCode}\" есть, но закладка сетки не сходится: " +
                   "в одну клетку кладётся только один сорт, а его на полный сбор не хватает";
        return "не хватает материалов (" +
               string.Join(", ", gaps.Select(g => $"{g.Code}: надо {g.Need}, есть {g.Have}")) + ")";
    }

    private CraftResult Done(bool ok, int crafted, string message)
    {
        OnLog?.Invoke(message);
        return new CraftResult(ok, crafted, message);
    }

    /// <summary>Что сейчас лежит в слоте сетки по мнению сервера (пусто — если такого слота нет).</summary>
    private SlotContent GridSlot(string gridId, int slot)
    {
        var slots = ctx.Self.Inventories.TryGetValue(gridId, out var s) ? s : null;
        return slots != null && slot >= 0 && slot < slots.Length ? slots[slot] : new SlotContent(null, 0);
    }

    /// <summary>В слоте вывода лежит именно ожидаемый предмет.</summary>
    private bool IsOutput(string gridId, string outputCode)
    {
        var slot = GridSlot(gridId, OutputSlot);
        return !slot.IsEmpty && string.Equals(slot.Code, outputCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Разложить план по клеткам. null — легло всё; иначе причина отказа
    /// словами, годными для человека.
    /// </summary>
    private async Task<string?> LayOutAsync(string gridId, GridPlan plan, CancellationToken ct)
    {
        foreach (var fill in plan.Fills)
        {
            int need = fill.Count;
            // Стопок одного кода в сумках столько же, сколько слотов; больше
            // подходов, чем слотов, быть не может — иначе мы в вечном цикле
            int attempts = ctx.Self.CarrySlots().Count() + 1;
            while (need > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (attempts-- <= 0)
                    return $"клетка {fill.Cell}: перекладывание {fill.Code} не сходится, " +
                           $"недобрано {need}";

                var from = FindCarried(fill.Code, fill.Tool);
                if (from == null)
                    return $"клетка {fill.Cell}: {fill.Code} кончился в сумках, недобрано {need}";

                int move = Math.Min(from.Value.Count, need);
                int was = GridSlot(gridId, fill.Cell).Count;
                await ctx.Actions.MoveItemAsync(from.Value.InventoryId, from.Value.Slot,
                    gridId, fill.Cell, move);
                if (!await WaitAsync(() => GridSlot(gridId, fill.Cell).Count > was, ct))
                    return $"клетка {fill.Cell}: сервер не принял {move} × {fill.Code} " +
                           $"из {from.Value.InventoryId}:{from.Value.Slot} " +
                           $"(в клетке было {was}, там же и осталось)";
                need -= GridSlot(gridId, fill.Cell).Count - was;
            }
            OnLog?.Invoke($"положено в клетку {fill.Cell}: {GridSlot(gridId, fill.Cell)}");
        }
        return null;
    }

    /// <summary>
    /// Где в сумках лежит этот код (null — нигде). Для ИНСТРУМЕНТА берём самый
    /// целый экземпляр: рецепт снимает прочность, и убитым топором закладка
    /// сорвётся на середине, хотя рядом в сумке лежит новый.
    /// </summary>
    private (string InventoryId, int Slot, int Count)? FindCarried(string code, bool tool = false)
    {
        (string InventoryId, int Slot, int Count)? best = null;
        int bestWear = -1;
        foreach (var (invId, slot, content) in ctx.Self.CarrySlots())
        {
            if (content.IsEmpty || !string.Equals(content.Code, code, StringComparison.Ordinal))
                continue;
            if (!tool)
                return (invId, slot, content.Count);
            if (content.Durability > bestWear)
            {
                bestWear = content.Durability;
                best = (invId, slot, content.Count);
            }
        }
        return best;
    }

    /// <summary>
    /// Shift-клик по слоту — ровно то, что делает человек мышью
    /// (GuiElementItemSlotGridBase.SlotClick при зажатом Shift).
    ///
    /// TargetLastChanged здесь НОЛЬ, и это не небрежность: сервер отвергает
    /// пакет 7, если inv.lastChangedSinceServerStart МЕНЬШЕ присланного числа
    /// (InventoryNetworkUtil.handleActivateInventorySlotPacket), то есть
    /// сравнение обратное пакету 8, где мы шлём long.MaxValue. Ноль проходит
    /// всегда, потому что счётчик сервера с нуля и растёт.
    /// </summary>
    private Task ShiftClickAsync(string inventoryId, int slot) =>
        ctx.Bot.SendPacketAsync(new Packet_Client
        {
            Id = 7,
            ActivateInventorySlot = new Packet_ActivateInventorySlot
            {
                TargetInventoryId = inventoryId,
                TargetSlot = slot,
                MouseButton = 0,        // EnumMouseButton.Left
                Modifiers = ShiftKey,   // зажатый Shift
                Priority = 0,           // EnumMergePriority.AutoMerge — как у живого клика
                TargetLastChanged = 0,
                TabIndex = 0,
                Dir = 0
            }
        });

    /// <summary>
    /// РАЗОБРАТЬ СЕТКУ: вернуть в сумки всё из клеток 0..8. Возвращает то, что
    /// вернуть не удалось (пустой список — сетка чиста).
    ///
    /// Слот вывода не трогаем: там лежит не предмет, а предпросмотр — сервер
    /// пересчитает его сам, как только клетки опустеют
    /// (InventoryCraftingGrid.FindMatchingRecipe).
    ///
    /// Сперва как человек — shift-клик по клетке: игра сама разложит стопку по
    /// подходящим слотам, добьёт начатые и не потребует от нас угадывать, куда
    /// влезет (InventoryBase.ActivateSlot → InventoryManager.TryTransferAway).
    /// Если после этого в клетке что-то осталось, докладываем руками по слотам —
    /// и ни одна клетка не бросается на полпути: раньше первая же упрямая
    /// клетка обрывала разбор, и всё остальное оставалось висеть в сетке.
    /// </summary>
    public async Task<IReadOnlyList<(int Cell, SlotContent Content)>> EmptyGridAsync(
        string? gridId = null, CancellationToken ct = default)
    {
        gridId ??= ctx.Self.GetInventoryId("craftinggrid");
        if (gridId == null)
            return [];

        for (int cell = 0; cell < GridCells; cell++)
        {
            var was = GridSlot(gridId, cell);
            if (was.IsEmpty)
                continue;

            await ShiftClickAsync(gridId, cell);
            // Ждём ЛЮБОГО ответа сервера по этой клетке, а не только «опустела»:
            // при полных сумках она не опустеет никогда, и ждать полный таймаут
            // на каждой из девяти клеток — это полминуты молчания
            await WaitAsync(() =>
            {
                var now = GridSlot(gridId, cell);
                return now.IsEmpty || now.Count != was.Count;
            }, ct);

            if (!GridSlot(gridId, cell).IsEmpty)
                await HandOutAsync(gridId, cell, ct);
        }

        var left = new List<(int, SlotContent)>();
        for (int cell = 0; cell < GridCells; cell++)
            if (GridSlot(gridId, cell) is { IsEmpty: false } content)
                left.Add((cell, content));
        return left;
    }

    /// <summary>
    /// Доложить остаток клетки руками, слот за слотом. Переносим РОВНО столько,
    /// сколько влезает в выбранный слот: сервер считает перенос удавшимся только
    /// при op.MovedQuantity == op.RequestedQuantity (InventoryBase.TryMoveItemStack),
    /// и на «половине» он откатывает свою картинку у клиента, хотя предметы уже
    /// переехали — после такого бот и клиент видят разное.
    /// </summary>
    private async Task HandOutAsync(string gridId, int cell, CancellationToken ct)
    {
        var tried = new HashSet<(string, int)>();
        int attempts = ctx.Self.CarrySlots().Count() + 1;
        while (attempts-- > 0)
        {
            ct.ThrowIfCancellationRequested();
            var content = GridSlot(gridId, cell);
            if (content.IsEmpty || content.Code is not { } code)
                return;

            int max = Math.Max(1, ctx.World.MaxStackSize(code));
            var target = RoomySlot(code, max, tried);
            if (target == null)
            {
                OnLog?.Invoke($"сетка: {content} из клетки {cell} убрать некуда — в сумках нет места");
                return;
            }
            tried.Add((target.Value.InventoryId, target.Value.Slot));

            int move = Math.Min(content.Count, target.Value.Fits);
            await ctx.Actions.MoveItemAsync(gridId, cell,
                target.Value.InventoryId, target.Value.Slot, move);
            if (!await WaitAsync(() => GridSlot(gridId, cell).Count < content.Count, ct))
                OnLog?.Invoke($"сетка: клетка {cell} — сервер не принял {move} × {code} " +
                              $"в {target.Value.InventoryId}:{target.Value.Slot}");
        }
    }

    /// <summary>
    /// Слот в сумках, куда влезет этот предмет, и сколько именно влезет.
    /// Сперва добиваем начатую стопку, потом занимаем пустой слот — так же
    /// решает и <see cref="SelfState.FreeSlotFor"/>; здесь дополнительно нужен
    /// пропуск уже испробованных слотов и остаток места, поэтому перебор свой.
    /// </summary>
    private (string InventoryId, int Slot, int Fits)? RoomySlot(string code, int maxStack,
        ISet<(string, int)> skip)
    {
        (string, int)? empty = null;
        foreach (var (invId, slot, content) in ctx.Self.CarrySlots())
        {
            if (skip.Contains((invId, slot)))
                continue;
            if (content.IsEmpty)
            {
                empty ??= (invId, slot);
                continue;
            }
            if (string.Equals(content.Code, code, StringComparison.Ordinal) &&
                content.Count < maxStack)
                return (invId, slot, maxStack - content.Count);
        }
        return empty is { } e ? (e.Item1, e.Item2, maxStack) : null;
    }

    /// <summary>Что осталось в сетке — словами для человека.</summary>
    private static string Describe(IReadOnlyList<(int Cell, SlotContent Content)> left) =>
        string.Join(", ", left.Select(l => $"клетка {l.Cell}: {l.Content}"));

    /// <summary>
    /// Дождаться ВСЕЙ прибавки, а не первой пришедшей штуки, и вернуть её.
    ///
    /// Один shift-клик по слоту вывода делает столько сборов, сколько позволяет
    /// закладка (ItemSlotCraftingOutput.CraftMany), а изменённые слоты сервер
    /// шлёт пачкой раз в 30 мс (ServerSystemInventory.SendDirtySlots) — и бот
    /// разбирает их по одному. Померив счёт по первому же пакету, он доложил бы
    /// «сделал 1» там, где сделал шестнадцать, и пошёл бы крафтить заново.
    /// Поэтому ждём появления прибавки, а потом — пока она перестанет расти.
    /// </summary>
    private async Task<int> SettleAsync(Func<int> count, int before, CancellationToken ct)
    {
        if (!await WaitAsync(() => count() > before, ct))
            return count() - before;

        int last = count();
        var quiet = DateTime.UtcNow;
        var deadline = DateTime.UtcNow + ServerReplyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(30, ct);
            int now = count();
            if (now != last)
            {
                last = now;
                quiet = DateTime.UtcNow;
                continue;
            }
            // Четверти секунды тишины хватает: пачка слотов уходит раз в 30 мс
            if (DateTime.UtcNow - quiet > TimeSpan.FromMilliseconds(250))
                break;
        }
        return count() - before;
    }

    /// <summary>Подождать условие, опрашивая своё состояние; false — не дождались.</summary>
    private async Task<bool> WaitAsync(Func<bool> done, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ServerReplyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (done())
                return true;
            await Task.Delay(50, ct);
        }
        return done();
    }
}

/// <summary>
/// Требование одной клетки рецепта — ровно то, что прислал сервер
/// (CraftingRecipeIngredient). Своих домыслов тут нет.
/// </summary>
public sealed class CraftIngredient
{
    /// <summary>Как сравнивать код: точно, по маске, по регулярке или только по тегам.</summary>
    public required EnumRecipeMatchType Match { get; init; }

    /// <summary>Блок или предмет — сервер сверяет класс первым делом.</summary>
    public required EnumItemClass Kind { get; init; }

    /// <summary>Короткий код или маска («plank-*»); null — сравнение только по тегам.</summary>
    public string? Code { get; init; }

    /// <summary>Сколько штук нужно в клетке на один крафт.</summary>
    public required int Quantity { get; init; }

    /// <summary>false — это инструмент: он остаётся, но теряет прочность.</summary>
    public bool Consumed { get; init; } = true;

    /// <summary>Изменение прочности инструмента за крафт (обычно отрицательное).</summary>
    public int DurabilityChange { get; init; }

    public string[]? AllowedVariants { get; init; }
    public string[]? SkipVariants { get; init; }

    /// <summary>Условие по тегам предмета (например «любой нож»).</summary>
    public ComplexTagCondition<TagSet> Tags { get; init; }

    /// <summary>Буква ингредиента в шаблоне рецепта («W», «N») — для диагностики.</summary>
    public string? Id { get; init; }

    /// <summary>
    /// Точный ингредиент требует атрибутов стака (например определённый тип
    /// сундука). Бот атрибуты чужих стаков не хранит, поэтому такой рецепт
    /// он может посчитать доступным ошибочно — решает всё равно сервер.
    /// </summary>
    public bool NeedsAttributes { get; init; }

    /// <summary>
    /// Подходит ли предмет с таким кодом. Повторяет
    /// CraftingRecipeIngredient.SatisfiesAsIngredient, но по коду, а не по стаку:
    /// у бота в инвентаре только код, количество и прочность.
    /// Чего здесь СОЗНАТЕЛЬНО нет — сверки атрибутов стака (см. NeedsAttributes).
    /// </summary>
    public bool Matches(WorldModel world, string? slotCode)
    {
        if (slotCode == null)
            return false;
        var collectible = world.GetCollectible(slotCode);
        if (collectible == null || collectible.ItemClass != Kind)
            return false;

        if (Match == EnumRecipeMatchType.Exact)
            return Code != null && string.Equals(Code, slotCode, StringComparison.Ordinal);

        var haystack = new AssetLocation(slotCode);
        if (Code != null)
        {
            var needle = new AssetLocation(Code);
            if (!WildcardUtil.Match(needle, haystack, AllowedVariants))
                return false;
            if (SkipVariants != null && WildcardUtil.Match(needle, haystack, SkipVariants))
                return false;
        }
        // Теги приходят с сервера числовыми ручками и в реестре предметов,
        // и в рецепте — сравниваются напрямую, без имён
        return Tags.Matches(collectible.Tags);
    }

    /// <summary>
    /// КОД-ОБРАЗЕЦ ИНГРЕДИЕНТА — ровно тот, что прислал сервер в рецепте
    /// («stone-*», «soil-*», «game:clay-blue»). Годится и человеку, и поиску:
    /// по нему ищут на складе (<c>Depot.Suits</c> понимает звёздочку так же,
    /// как игра, — WildcardUtil.Match).
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="Describe"/>. Живой прогон 15.08: «там было
    /// всё для крафта дороги, а он не стал туда возвращаться». Нехватку бот
    /// называл витриной «stone-* ×4», а разбор отказа (<c>WorkSupply.Lacking</c>)
    /// читал из неё ПОСЛЕДНЕЕ слово перед скобкой — то есть «4». За «4» бот и
    /// сходил на склад: такого там, разумеется, нет. Количество в имени
    /// материала не нужно вовсе — оно и так сказано словами «надо 4».
    /// </summary>
    public string Name =>
        Code ?? (Match == EnumRecipeMatchType.TagsOnly ? "предмет по тегам" : "?");

    /// <summary>Человекочитаемое требование — для показа рецепта целиком.</summary>
    public string Describe() => Quantity > 1 ? $"{Name} ×{Quantity}" : Name;

    public override string ToString() => Describe();
}

/// <summary>
/// Рецепт сетки в разобранном виде. Клетки уже разложены по позициям
/// (сервер присылает их массивом Width×Height), шаблон-строка не нужна.
/// </summary>
public sealed class CraftRecipe
{
    /// <summary>Имя ассета рецепта («game:recipes/grid/chest») — для лога.</summary>
    public required string Name { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Порядок ингредиентов не важен — сервер сверяет их как набор.</summary>
    public required bool Shapeless { get; init; }

    /// <summary>Клетки построчно, длина Width×Height; null — клетка должна быть пуста.</summary>
    public required CraftIngredient?[] Cells { get; init; }

    /// <summary>Код результата (короткая форма, как в реестре сервера).</summary>
    public required string OutputCode { get; init; }

    /// <summary>Сколько штук даёт один сбор.</summary>
    public required int OutputQuantity { get; init; }

    /// <summary>
    /// Черта класса персонажа, без которой рецепт не сработает
    /// (CharacterSystem.Event_MatchesGridRecipe). null — доступен всем.
    /// </summary>
    public string? RequiresTrait { get; init; }

    public IEnumerable<CraftIngredient> Ingredients => Cells.OfType<CraftIngredient>();

    public override string ToString() =>
        $"{OutputCode} ×{OutputQuantity} ({Width}×{Height}" + (Shapeless ? ", без формы" : "") + ")";
}
