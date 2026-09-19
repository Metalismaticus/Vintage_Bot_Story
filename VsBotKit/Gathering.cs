namespace VsBotKit;

/// <summary>Найденная «лежанка»: что лежит, где, что с неё упадёт и насколько ценно.</summary>
/// <param name="Pos">Клетка блока</param>
/// <param name="Code">Код блока ("loosestones-granite-free", "looseores-cassiterite-granite-free")</param>
/// <param name="Drops">Коды выпадений из реестра сервера ("stone-granite", "nugget-cassiterite")</param>
/// <param name="Value">Ценность по настройке <see cref="Gathering.DropValues"/></param>
/// <param name="Distance">Расстояние от бота по горизонтали и высоте, блоков</param>
public sealed record LooseFind(BlockPos Pos, string Code, string[] Drops, int Value, double Distance);

/// <summary>
/// Сбор с земли: камни, палки, кремни, самородки, яйца.
///
/// Это НЕ добыча. В игре такие блоки не ломают киркой — их подбирают ОДНИМ
/// правым кликом без удержания: поведение блока «RightClickPickup»
/// (BlockBehaviorRightClickPickup.OnBlockInteractStart) само выдаёт выпадения
/// игроку и ставит на место блока воздух. Ломать их киркой можно, но это
/// лишние секунды и износ инструмента на ровном месте.
///
/// Из того же метода игры взяты все условия, которые бот обязан соблюсти:
/// - в АКТИВНОМ слоте хотбара должно быть пусто ИЛИ лежать ровно то, что
///   выпадет (проверка activeHotbarSlot.Empty || Itemstack.Equals(drops));
/// - Shift держать НЕЛЬЗЯ: с ним поведение возвращает false, а у камня
///   с флинтом shift+ПКМ вообще начинает камнетёсство (BlockLooseStones);
/// - удержания нет: сервер обрабатывает первый же клик.
///
/// Что именно «лежит на земле и подбирается», бот НЕ угадывает по имени:
/// список собирается из реестра сервера — блоки, у которых есть поведение
/// «RightClickPickup». Похожие по виду блоки без него (looseboulders, куски
/// хрусталя crystal-*) в список не попадают, их надо ломать — этим занимается
/// Mining. Маски (что из найденного брать, а что обходить) — настройка роли.
/// </summary>
public class Gathering
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    // Таблицы строятся из реестра сервера (пакет 19), а не из списка в коде
    private bool[] pickupById = [];
    private bool[] litterById = [];
    private HashSet<string> pickupCodes = new(StringComparer.Ordinal);
    private HashSet<string> litterCodes = new(StringComparer.Ordinal);

    // Выпадения блока разбираются один раз: их считает поиск на каждой клетке
    private readonly Dictionary<string, string[]> dropsCache = new(StringComparer.Ordinal);

    public Gathering(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
        // Реестр блоков приходит одним пакетом при входе в мир и заново после
        // переподключения. Слушаем его сами, и вот почему: модель мира хранит
        // ИМЕНА поведений (WorldModel.HasBlockBehavior), а сбору нужны их
        // СВОЙСТВА — «dropsPickupMode» решает, отдаст клик выпадения или сам
        // блок, то есть подбираем мы камешек с земли или чужой кувшин. На
        // собранном объекте блока свойств нет вовсе: незнакомые классы
        // подменяются пустышками (см. StubBehaviors)
        ctx.Bot.OnPacket += HandlePacket;
    }

    public event Action<string>? OnLog;

    // ---------------- настройки ----------------

    /// <summary>
    /// Имена поведений «подбирается правым кликом». Имя берётся у самой игры:
    /// SurvivalCoreSystem регистрирует его как
    /// RegisterBlockBehaviorClass("RightClickPickup", typeof(BlockBehaviorRightClickPickup)),
    /// а класс лежит в VSSurvivalMod.dll, на которую ссылается проект.
    /// Роль может дописать сюда поведение из мода.
    /// </summary>
    public HashSet<string> PickupBehaviors { get; } = new(StringComparer.Ordinal) { PickupBehaviorName };

    /// <summary>
    /// Брать только «мусор на земле». Признак — свойство поведения
    /// dropsPickupMode: с ним клик отдаёт ВЫПАДЕНИЯ блока (камни, палки,
    /// кремни, самородки, яйца), без него — сам блок, а это уже поставленные
    /// кем-то вещи: миска, кувшин, фонарь, ведро, ракушка, тыква. Разграбливать
    /// чужой быт — не сбор, поэтому по умолчанию включено.
    /// </summary>
    public bool GroundLitterOnly { get; set; } = true;

    /// <summary>
    /// Если список не пуст — собирать только то, чей код содержит одну из
    /// подстрок. Пусто — всё, что реестр считает подбираемым (политика роли).
    /// </summary>
    public List<string> OnlyMasks { get; } = [];

    /// <summary>Что обходить стороной, даже если подбирается.</summary>
    public List<string> SkipMasks { get; } = [];

    /// <summary>
    /// Ценность находки: маска кода ВЫПАДЕНИЯ → вес. Берётся максимальный
    /// подошедший. Это политика: механизм только считает по таблице.
    ///
    /// МЕТАЛЛА В ЭТОЙ ТАБЛИЦЕ БОЛЬШЕ НЕТ, и это не мелочь. Здесь стояла строка
    /// <c>["nugget-"] = 4</c> — рукописная приставка, решавшая «дорого ли это»,
    /// — а «металл ли это» с 19.08 спрашивают у реестра
    /// (<see cref="IsNuggetLitter"/>). Две мерки одного вопроса расходятся
    /// молча, и разошлись они сразу: щебень метеорита роняет
    /// stone-meteorite-iron — пятьдесят единиц железа в штуке, — под приставку
    /// не подходит и стоил единицу, дешевле кремня. Вес металла теперь один и
    /// живёт в <see cref="MetalLitterValue"/>.
    /// </summary>
    public Dictionary<string, int> DropValues { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["flint"] = 3,
        ["ore-"] = 2,
        ["clearquartz"] = 2,
    };

    /// <summary>
    /// ЧЕГО СТОИТ НАХОДКА, С КОТОРОЙ ПО РЕЕСТРУ ПАДАЕТ МЕТАЛЛ. Политика роли —
    /// механизм только сравнивает веса.
    ///
    /// ЖИВОЙ СЛУЧАЙ (19.08), из-за которого вес отделён от таблицы масок.
    /// Наряд подбирает россыпь так: <c>FindLoose(радиус, limit: 12)</c>, а
    /// порядок в этом списке — по ценности. Щебень метеорита
    /// (loosestones-meteorite-iron-*) роняет stone-meteorite-iron, то есть
    /// пятьдесят единиц железа, которые берутся РУКОЙ, без кирки четвёртого
    /// тира и без шахты. Приставочная таблица про такой код не знала ничего и
    /// давала ему <see cref="DefaultValue"/> = 1 — дешевле кремня и вчетверо
    /// дешевле палки-самородка. Двенадцать мест списка выбирали ближние
    /// самородки, и <c>IsNuggetLitter</c>, уже починенный на реестр, до
    /// метеоритного щебня просто не доходил.
    /// </summary>
    public int MetalLitterValue { get; set; } = 4;

    /// <summary>Вес находки, не попавшей ни в одну маску ценности.</summary>
    public int DefaultValue { get; set; } = 1;

    /// <summary>Сначала идти за дорогим, а не за ближним.</summary>
    public bool PreferValuable { get; set; } = true;

    /// <summary>Сколько ждать подтверждения от сервера после клика, сек.</summary>
    public double ConfirmSeconds { get; set; } = 2.0;

    /// <summary>Разбирать ли клетки выше и ниже бота при поиске.</summary>
    public int SearchHeight { get; set; } = 4;

    // ---------------- реестр: что подбирается ----------------

    /// <summary>Реестр разобран и бот знает, что подбирается руками.</summary>
    public bool RegistryReady => pickupCodes.Count > 0;

    /// <summary>
    /// Имя поведения — из класса игры: «BlockBehaviorRightClickPickup» без
    /// приставки «BlockBehavior» и есть регистрационное имя. Так переименование
    /// класса в игре сломает сборку, а не молча выключит сбор. Если
    /// VSSurvivalMod.dll почему-то не загрузилась, остаётся то же имя строкой.
    /// </summary>
    private static readonly string PickupBehaviorName = ResolvePickupBehaviorName();

    private static string ResolvePickupBehaviorName()
    {
        const string prefix = "BlockBehavior";
        try
        {
            string name = PickupBehaviorClassName();
            return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        }
        catch
        {
            return "RightClickPickup";
        }
    }

    // Обращение к типу из VSSurvivalMod вынесено в ОТДЕЛЬНЫЙ метод намеренно:
    // сборка типа проверяется при первом вызове метода, поэтому не загрузись
    // мод — исключение поймает try выше, а не уронит бота на старте
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string PickupBehaviorClassName() =>
        typeof(Vintagestory.GameContent.BlockBehaviorRightClickPickup).Name;

    private void HandlePacket(Packet_Server p)
    {
        // 19 — ServerAssets, реестр блоков (тот же пакет разбирает WorldModel)
        if (p.Id != 19 || p.Assets?.Blocks == null)
            return;
        try
        {
            int max = 0;
            for (int i = 0; i < p.Assets.BlocksCount; i++)
                if (p.Assets.Blocks[i] is { } b)
                    max = Math.Max(max, b.BlockId);

            var pickup = new bool[max + 1];
            var litter = new bool[max + 1];
            var pickupNames = new HashSet<string>(StringComparer.Ordinal);
            var litterNames = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < p.Assets.BlocksCount; i++)
            {
                var bt = p.Assets.Blocks[i];
                if (bt?.Code is not { Length: > 0 } code || bt.Behaviors == null)
                    continue;
                for (int b = 0; b < bt.BehaviorsCount; b++)
                {
                    var beh = bt.Behaviors[b];
                    if (beh?.Code is not { } name || !PickupBehaviors.Contains(name))
                        continue;
                    pickup[bt.BlockId] = true;
                    pickupNames.Add(code);
                    if (ReadDropsPickupMode(beh.Attributes))
                    {
                        litter[bt.BlockId] = true;
                        litterNames.Add(code);
                    }
                }
            }

            pickupById = pickup;
            litterById = litter;
            pickupCodes = pickupNames;
            litterCodes = litterNames;
            dropsCache.Clear();
            OnLog?.Invoke($"реестр: подбирается руками {pickupNames.Count} блоков, из них лежит на земле {litterNames.Count}");
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"не разобрал реестр подбираемых блоков: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// Свойство поведения dropsPickupMode из его же настроек в реестре
    /// (сервер шлёт их строкой JSON в Packet_Behavior.Attributes).
    /// </summary>
    private static bool ReadDropsPickupMode(string? attributes)
    {
        if (string.IsNullOrEmpty(attributes))
            return false;
        try
        {
            var o = Newtonsoft.Json.Linq.JObject.Parse(attributes);
            return o.GetValue("dropsPickupMode", StringComparison.OrdinalIgnoreCase)?.ToObject<bool>() == true;
        }
        catch { return false; }
    }

    /// <summary>Подбирается ли такой блок правым кликом (по реестру сервера).</summary>
    public bool IsPickable(string? code) =>
        code != null && (GroundLitterOnly ? litterCodes.Contains(code) : pickupCodes.Contains(code));

    /// <summary>То же по id блока — без разбора строк (пригодится в поиске пути).</summary>
    public bool IsPickable(int blockId)
    {
        var table = GroundLitterOnly ? litterById : pickupById;
        return blockId > 0 && blockId < table.Length && table[blockId];
    }

    /// <summary>Подбираем ли мы это по политике роли (маски «только» и «мимо»).</summary>
    public bool IsWanted(string code) =>
        IsPickable(code) &&
        !SkipMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase)) &&
        (OnlyMasks.Count == 0 || OnlyMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: блок даёт нужное — либо он сам им зовётся, либо нужное
    /// есть среди его выпадений.
    ///
    /// Второе и есть главное. Живой случай: боту не хватало камня на топорище,
    /// и он искал «stone-granite», а лежит на земле блок с ДРУГИМ именем —
    /// «loosestones-granite-free». По коду он не подходит («stones-granite», не
    /// «stone-granite»), а падает с него ровно «stone-granite».
    ///
    /// Правило живёт ЗДЕСЬ, а не у поручений: выпадения знает тот, кто их
    /// читает из реестра. Второй такой же мерке в другом файле разъехаться с
    /// этой было бы делом одного вечера.
    /// </summary>
    public static bool Gives(string code, IEnumerable<string> drops, string wanted) =>
        Holds(code, wanted) || drops.Any(d => Holds(d, wanted));

    /// <summary>
    /// КОД СОДЕРЖИТ НАЗВАННОЕ СЛОВО — одна мерка на весь проект, и открыта она
    /// нарочно. Ею же судит цельный камень (<see cref="WholeStone.OnlyByIsolation"/>):
    /// он отвечает на вопрос «просят сам блок, а обычным ударом он не
    /// выпадает», и вопрос этот стоит РЯДОМ с <see cref="Gives(string, IEnumerable{string}, string)"/> — разойдись
    /// две мерки, и поручение считало бы источником не то, что берёт.
    /// </summary>
    public static bool Holds(string code, string part) =>
        part.Length == 0 || code.Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>То же по живому реестру: спросить выпадения самому.</summary>
    public bool Gives(string code, string wanted) => Gives(code, DropsOf(code), wanted);

    // ---------------- что с этого будет ----------------

    /// <summary>
    /// Коды выпадений блока. Сам ответ даёт РЕЕСТР (<see cref="WorldModel.DropsOfBlock"/>) —
    /// здесь только память на время сессии: поиск спрашивает это на каждой
    /// клетке округи, а разбор стопок стоит перебора двух реестров.
    ///
    /// Знание «что из чего падает» живёт в одном месте нарочно. Заказчик 17.08
    /// заметил, что бот его имеет («подобрал looseores-nativecopper-andesite-free
    /// (+2 nugget-nativecopper)»), — и был прав; беда была бы в том, чтобы это
    /// знание завести дважды.
    /// </summary>
    public string[] DropsOf(string code)
    {
        if (dropsCache.TryGetValue(code, out var cached))
            return cached;
        var result = ctx.World.DropsOfBlock(code);
        dropsCache[code] = result;
        return result;
    }

    /// <summary>
    /// Ценность находки: наибольшее из веса металла (если реестр видит металл в
    /// выпадениях) и весов по таблице <see cref="DropValues"/>.
    ///
    /// ПРО МЕТАЛЛ СПРАШИВАЕТСЯ РЕЕСТР, И ЭТО ТА ЖЕ МЕРКА, ЧТО У ПОДБОРА
    /// (<see cref="IsNuggetLitter"/>), а не её вторая копия: иначе «дорого ли
    /// это» и «металл ли это» отвечали бы про один блок разное — ровно то, на
    /// чём погорел метеорит.
    /// </summary>
    public int ValueOf(string code)
    {
        int best = IsNuggetLitter(code) ? Math.Max(DefaultValue, MetalLitterValue) : DefaultValue;
        foreach (var drop in DropsOf(code))
            foreach (var (mask, weight) in DropValues)
                if (drop.Contains(mask, StringComparison.OrdinalIgnoreCase))
                    best = Math.Max(best, weight);
        return best;
    }

    /// <summary>
    /// Россыпь МЕТАЛЛА на земле — то есть с блока падает металл, а не камень.
    ///
    /// Отличить её глазами нельзя: looseores и loosestones используют ОДНУ
    /// модель (block/stone/loosestones/{cover}1) и различаются только кодом
    /// и выпадением. Поэтому судим по выпадению — но СПРАШИВАЕМ О НЁМ РЕЕСТР,
    /// а не приставку.
    ///
    /// ЖИВОЙ СЛУЧАЙ (19.08). Здесь стояло «падает что-то на nugget-». Щебень
    /// метеорита (loosestones-meteorite-iron-*) роняет stone-meteorite-iron —
    /// пятьдесят единиц металла в одной штуке, которые берутся РУКОЙ, без
    /// кирки четвёртого тира и без шахты, — и под приставку не подходил. Бот
    /// проходил мимо готового металла и уходил искать жилу.
    ///
    /// Такая россыпь ценна вдвойне: генератор мира кладёт её над залежью,
    /// значит руда есть и под ногами.
    /// </summary>
    public bool IsNuggetLitter(string code)
    {
        if (ctx.Metal is not { } metals)
        {
            // МОЛЧАТЬ ТУТ НЕЛЬЗЯ. Без счёта металла ответ «не металл» получают
            // ВСЕ россыпи разом, и со стороны это выглядит как «металла на
            // земле нет» — человек уводит бота копать шахту, стоя на самородках.
            // Один раз на сессию: этот вопрос задаётся на каждой клетке округи
            if (!saidMetalNotReady)
            {
                saidMetalNotReady = true;
                OnLog?.Invoke("счёт металла не подключён — металл на земле от камня отличить не могу");
            }
            return false;
        }
        return DropsOf(code).Any(d => metals.Worth(d) != null);
    }

    /// <summary>О том, что спросить про металл некого, говорим один раз на сессию.</summary>
    private bool saidMetalNotReady;

    // ---------------- поиск ----------------

    private BlockPos? Here()
    {
        if (ctx.Self.Position is not { } p)
            return null;
        return new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
    }

    private double DistanceTo(BlockPos pos)
    {
        if (ctx.Self.Position is not { } p)
            return double.MaxValue;
        double dx = pos.X + 0.5 - p.X, dy = pos.Y - p.Y, dz = pos.Z + 0.5 - p.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Что лежит вокруг и подбирается: коды, координаты, выпадения, ценность.
    /// Порядок — как решено настройкой: сначала дорогое, потом ближнее.
    /// </summary>
    /// <param name="radius">Радиус поиска в клетках от центра</param>
    /// <param name="limit">Сколько находок вернуть</param>
    /// <param name="centre">Центр поиска (по умолчанию — где стоит бот)</param>
    /// <param name="wanted">
    /// Кусок кода нужного ВЫПАДЕНИЯ («stone-granite», «flint»). Пусто — берём
    /// всё подбираемое.
    ///
    /// ОТБОР ИДЁТ ДО ОГРАНИЧЕНИЯ ПО ЧИСЛУ, и это не мелочь. Раньше искавший
    /// камень отсеивал нужное ПОСЛЕ того, как поиск вернул первые находки, а
    /// возвращает он их по ценности: восемь самородков рядом — и камня в списке
    /// нет вовсе. Живьём это выглядело как «не подбирал камни для инструментов»,
    /// хотя камни лежали кругом.
    /// </param>
    public List<LooseFind> FindLoose(int radius = 24, int limit = 32, BlockPos? centre = null,
        string? wanted = null)
    {
        var from = centre ?? Here();
        if (from is not { } origin)
            return [];
        if (!RegistryReady)
        {
            OnLog?.Invoke("реестр блоков ещё не пришёл — что подбирается, пока неизвестно");
            return [];
        }

        bool Suits(string code) =>
            IsWanted(code) && (wanted is not { Length: > 0 } part || Gives(code, part));

        var found = ctx.World
            .FindBlocks(Suits, origin, radius, SearchHeight, limit: limit * 4)
            .Select(p =>
            {
                string code = ctx.World.GetBlockCode(p) ?? "";
                return new LooseFind(p, code, DropsOf(code), ValueOf(code), DistanceTo(p));
            })
            .Where(f => f.Code.Length > 0);

        var ordered = PreferValuable
            ? found.OrderByDescending(f => f.Value).ThenBy(f => f.Distance)
            : found.OrderBy(f => f.Distance);
        return ordered.Take(limit).ToList();
    }

    /// <summary>Ближайшая (или самая ценная) находка, null — вокруг пусто.</summary>
    public LooseFind? FindNearestLoose(int radius = 24, BlockPos? centre = null,
        string? wanted = null) =>
        FindLoose(radius, 1, centre, wanted).FirstOrDefault();

    // ---------------- подбор ----------------

    /// <summary>
    /// Подобрать конкретную клетку: подойти, приготовить руку, кликнуть.
    ///
    /// true возвращается ТОЛЬКО когда сервер подтвердил: блок исчез И в сумке
    /// прибавилось. Ушедший пакет ничего не значит — сервер молча откажет,
    /// если место в чужой заявке (Claims.TryAccess), блок укреплён
    /// (BlockBehaviorReinforcable.AllowRightClickPickup) или рука занята.
    /// </summary>
    public async Task<bool> PickUpAsync(BlockPos pos, CancellationToken ct = default)
    {
        string code = ctx.World.GetBlockCode(pos) ?? "";
        if (code.Length == 0)
            return false;
        if (!IsPickable(code))
        {
            OnLog?.Invoke($"{code} руками не подбирается — это добыча (Mining.BreakAsync)");
            return false;
        }

        var drops = DropsOf(code);
        if (!await hands.ReachForAsync(pos, ct: ct))
        {
            OnLog?.Invoke($"не дотянулся до {code} в {pos}");
            return false;
        }
        // Блок мог осыпаться (UnstableFalling) или его успел взять другой игрок
        if (ctx.World.GetBlockCode(pos) != code)
        {
            OnLog?.Invoke($"пока шёл, {code} в {pos} исчез");
            return false;
        }
        if (!await PrepareHandAsync(drops, ct))
            return false;

        // Присед ломает сбор: в OnBlockInteractStart поведение сразу выходит
        // при Controls.ShiftKey, а у камня с кремнем shift+ПКМ — камнетёсство
        await ctx.Actions.SetSneakAsync(false);

        int beforeDrops = CountOf(drops);
        int beforeAll = TotalItems();

        // Удержания нет: сервер отрабатывает первый же клик, поэтому seconds = 0
        await hands.UseBlockAsync(pos, 0, ct: ct);

        bool gone = await WaitBlockGoneAsync(pos, code, ct);
        if (!gone)
        {
            OnLog?.Invoke($"{code}: сервер не отдал (чужая заявка, укрепление или занята рука)");
            return false;
        }

        // Если сумка полна, игра роняет вещи на землю — доходим ногами, как игрок
        int gained = CountOf(drops) - beforeDrops;
        if (gained <= 0)
        {
            await mining.CollectDropsAsync(4, 6, ct);
            gained = CountOf(drops) - beforeDrops;
        }
        if (gained <= 0 && TotalItems() - beforeAll > 0)
            gained = TotalItems() - beforeAll;   // выпало не то, что обещал реестр

        if (gained <= 0)
        {
            OnLog?.Invoke($"{code}: блок исчез, а в сумке не прибавилось — некуда класть?");
            return false;
        }
        OnLog?.Invoke($"подобрал {code} (+{gained} {(drops.Length > 0 ? drops[0] : "?")})");
        return true;
    }

    /// <summary>
    /// Приготовить руку так, как требует сама игра: активный слот хотбара
    /// должен быть ПУСТ либо держать ровно то, что выпадет — иначе
    /// OnBlockInteractStart возвращает false и клик пропадает впустую.
    /// </summary>
    private async Task<bool> PrepareHandAsync(string[] drops, CancellationToken ct)
    {
        if (hands.Held is { IsEmpty: true })
            return true;
        if (hands.Held is { Code: { } held } && drops.Contains(held, StringComparer.Ordinal))
            return true;   // уже держим такую же стопку — игра это разрешает

        if (await hands.FreeHandAsync())
        {
            await Settle(ct);
            return true;
        }
        // Свободного слота нет: берём в руку такую же вещь — это второй
        // разрешённый игрой случай
        foreach (var drop in drops)
            if (await hands.TakeToHandAsync(s => s.Code == drop, ct) != null)
            {
                await Settle(ct);
                return true;
            }
        OnLog?.Invoke("хотбар забит, а такой же вещи нет — игра не даст подобрать");
        return false;
    }

    /// <summary>
    /// Дать серверу узнать, что рука сменилась.
    ///
    /// СМЕНА РУКИ — ЭТО ПАКЕТ, и сервер узнаёт о ней не мгновенно. Кликнув
    /// сразу за переключением слота, бот стучится по блоку, всё ещё держа в
    /// руке кирку с точки зрения сервера, — а поведение подбора при занятой
    /// руке молча отказывает. Живьём это выглядело так: бот дошёл до россыпи
    /// самородной меди и получил «сервер не отдал», хотя всё сделал верно.
    ///
    /// Своего числа тут БОЛЬШЕ НЕТ: было 220 рядом с Hands.HandChangeSettleMs =
    /// 120, то есть две ручки на одну и ту же задержку — поправишь одну, и
    /// вторая разойдётся молча. Число одно, и живёт оно там, где смену слота и
    /// делают.
    /// </summary>
    private Task Settle(CancellationToken ct) =>
        Task.Delay(Math.Max(0, hands.HandChangeSettleMs), ct).ContinueWith(_ => { },
            CancellationToken.None);

    /// <summary>
    /// Дождаться подтверждения сервера: поведение ставит на место блока воздух
    /// (BlockAccessor.SetBlock(0, pos)), и это изменение приходит нам пакетом.
    /// </summary>
    private async Task<bool> WaitBlockGoneAsync(BlockPos pos, string code, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (ctx.World.GetBlockCode(pos) != code)
                return true;
            await Task.Delay(100, ct).ContinueWith(_ => { });
        }
        return ctx.World.GetBlockCode(pos) != code;
    }

    private int CountOf(string[] codes)
    {
        if (codes.Length == 0)
            return 0;
        int total = 0;
        foreach (var (invId, slots) in ctx.Self.OwnInventories)
        {
            if (invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            foreach (var s in slots)
                if (s.Code is { } c && codes.Contains(c, StringComparer.Ordinal))
                    total += s.Count;
        }
        return total;
    }

    private int TotalItems()
    {
        int total = 0;
        foreach (var (invId, slots) in ctx.Self.OwnInventories)
        {
            if (invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            foreach (var s in slots)
                total += s.Count;
        }
        return total;
    }

    // ---------------- обход ----------------

    /// <summary>
    /// Обойти окрестности и собрать всё, что лежит. Возвращает, сколько мест
    /// собрано.
    /// </summary>
    /// <param name="radius">Радиус от якоря — дальше бот не уходит</param>
    /// <param name="maxPlaces">Сколько мест собрать за раз</param>
    /// <param name="maxSeconds">Сколько всего на это тратить</param>
    /// <param name="anchor">
    /// Точка отсчёта. Не задана — место, где бот стоит СЕЙЧАС. Радиус меряется
    /// от неё и только от неё: если считать от текущего места, бот уходит
    /// «радиус за радиусом» и не возвращается.
    /// </param>
    /// <param name="wanted">
    /// Кусок кода нужного выпадения — «набери мне камня», а не «набери всего».
    /// Пусто — брать всё подбираемое, как и было.
    /// </param>
    public async Task<int> GatherAsync(int radius = 24, int maxPlaces = 12, double maxSeconds = 180,
        BlockPos? anchor = null, CancellationToken ct = default, string? wanted = null)
    {
        if ((anchor ?? Here()) is not { } home)
            return 0;

        int done = 0;
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        var skip = new HashSet<BlockPos>();

        while (done < maxPlaces && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var target = FindLoose(radius, 32, home, wanted).FirstOrDefault(f => !skip.Contains(f.Pos));
            if (target == null)
            {
                OnLog?.Invoke(done == 0
                    ? wanted is { Length: > 0 } what
                        ? $"рядом не лежит ничего, с чего берётся {what}"
                        : "рядом ничего не лежит"
                    : $"больше нечего брать, собрано мест: {done}");
                break;
            }
            skip.Add(target.Pos);   // не берём одну клетку дважды, даже если не вышло
            if (await PickUpAsync(target.Pos, ct))
                done++;
        }

        // Бот ходил за камнями, а не переезжал — возвращаемся к якорю
        if (done > 0 && !ct.IsCancellationRequested)
            await ctx.Movement.TravelToAsync(home, 90, ct);
        return done;
    }
}

/// <summary>
/// Собирать с земли, когда делать нечего.
///
/// ПОЛИТИКА, а не механизм: библиотека даёт способность, но по умолчанию она
/// ВЫКЛЮЧЕНА. Бот-продавец, стоящий у лавки, не должен внезапно уходить за
/// палками; включает её роль, которой это надо.
/// </summary>
public class BehaviorGatherWhenIdle : BotBehavior
{
    private readonly Gathering gathering;

    public BehaviorGatherWhenIdle(Gathering gathering)
    {
        this.gathering = gathering;
    }

    /// <summary>Способность выключена по умолчанию — это решение роли.</summary>
    public bool Enabled { get; set; }

    /// <summary>Откуда мерить радиус. null — от места, где бот стоял в тот момент.</summary>
    public BlockPos? Anchor { get; set; }

    /// <summary>Радиус вылазки от якоря, клеток.</summary>
    public int Radius { get; set; } = 16;

    /// <summary>Сколько мест брать за один заход.</summary>
    public int PlacesPerRun { get; set; } = 3;

    /// <summary>Сколько секунд отводить на заход.</summary>
    public double RunSeconds { get; set; } = 45;

    /// <summary>Пауза между заходами, сек: без неё бот метёт округу без передышки.</summary>
    public int CooldownSeconds { get; set; } = 30;

    /// <summary>Собрано мест за заход.</summary>
    public event Action<int>? OnGathered;

    private DateTime lastRun = DateTime.MinValue;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled)
            return false;
        if ((DateTime.UtcNow - lastRun).TotalSeconds < CooldownSeconds)
            return false;

        // Ходить стоит, только если что-то действительно лежит: иначе способность
        // будет «брать ход» вхолостую и глушить те, что ниже приоритетом
        if (gathering.FindNearestLoose(Radius, Anchor) == null)
        {
            lastRun = DateTime.UtcNow;
            return false;
        }

        lastRun = DateTime.UtcNow;
        int done = await gathering.GatherAsync(Radius, PlacesPerRun, RunSeconds, Anchor, ct);
        lastRun = DateTime.UtcNow;   // отсчёт паузы — от КОНЦА захода, а не от начала
        if (done > 0)
            OnGathered?.Invoke(done);
        return done > 0;
    }
}
