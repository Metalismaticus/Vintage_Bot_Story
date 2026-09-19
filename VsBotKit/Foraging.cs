namespace VsBotKit;

/// <summary>Почему конкретный куст или гриб не дался.</summary>
public enum ForageMiss
{
    /// <summary>Взяли.</summary>
    None,
    /// <summary>Куст на месте, но ягоды ещё не поспели.</summary>
    Unripe,
    /// <summary>Не дотянуться: забор, обрыв, чужой участок.</summary>
    OutOfReach,
    /// <summary>Сломать не вышло (инструмент, приват, сервер отказал).</summary>
    NotBroken,
    /// <summary>Собрал, а в сумке не прибавилось.</summary>
    NothingDropped,
    /// <summary>Это вообще не еда (блок сменился, пока шли).</summary>
    NotFood,
    /// <summary>
    /// Посев растёт на ЧУЖОЙ ГРЯДКЕ — это не дикая еда, а поле хозяина.
    /// Дикие посевы игра сеет прямо на землю (worldgen/blockpatches/crop.json),
    /// а вспаханная клетка под ними значит, что кто-то это посадил.
    /// </summary>
    Farmed
}

/// <summary>
/// Итог обхода за дикой едой — ПО ФАКТАМ, а не одним числом.
///
/// Живой случай приёмки: бот стоит в малиннике, кусты неспелые (или до них не
/// дотянуться из-за забора) — и отвечает «дикой еды в 32 бл вокруг не нашлось».
/// Это прямая неправда: еда рядом, и человек уводил бота искать другое место
/// вместо того, чтобы подождать созревания или убрать преграду. Ноль был один
/// на все причины, потому что наружу выходило только число.
/// </summary>
/// <param name="Places">Сколько мест обобрано (0 — ни одного).</param>
/// <param name="Seen">Сколько источников еды бот вообще увидел вокруг.</param>
/// <param name="Misses">Чем кончились те, что не дались: причина → сколько раз.</param>
/// <param name="Reason">Та же новость словами — её и показывают человеку.</param>
public sealed record ForageReport(int Places, int Seen,
    IReadOnlyDictionary<ForageMiss, int> Misses, string Reason)
{
    /// <summary>Успех — только по факту: хоть что-то собрано.</summary>
    public bool Success => Places > 0;

    // ЗДЕСЬ СТОЯЛА «ПОМЕХА ОБЩИМ СЛОВОМ» (Setback Hindrance), И ЕЁ БОЛЬШЕ НЕТ.
    // СКАЗАНО ВСЛУХ: СБОР ЧЕРЕЗ ВОЗВРАТ НЕ ХОДИТ И НИКОГДА НЕ ХОДИЛ.
    //
    // Поле считалось на каждом обходе (одно чистое правило, свой стенд) — и НЕ
    // ЧИТАЛОСЬ НИ ОДНОЙ СТРОКОЙ ДЕРЕВА. Зовут сбор пятеро, и все пятеро
    // спрашивают у него числа и Success: поручение (Errands.ForageAsync),
    // распорядок выживания (BehaviorSurvivalRoutines), сама роль
    // (SurvivorRole), ручной вызов из окна (BotTools) и диагностика. Ни один
    // из них не возврат: сбор — обычный навык, у него нет ни захода, ни
    // потолка повторов, ни KeepGatheringAsync, и Resume про него не знает.
    //
    // Отчего это не мелочь. Незваное поле, которое считают и не читают, врёт
    // дважды: снаружи оно обещает, что сбор умеет объяснять свои остановки
    // возврату, а внутрь тянет стенд, который стережёт слова, а не дело
    // (закон 8 и правило 7 проекта). Волна 09.09 записала сбор в «семь работ,
    // что отдавали Setback.None и потому сдавались с первого раза» — про сбор
    // это было неверно: сдаваться ему не с чего, его никто не переспрашивает.
    //
    // ПОНАДОБИТСЯ — ЗАВЕДЁМ ОБРАТНО, И ВМЕСТЕ С ТЕМ, КТО ПРОЧТЁТ. Правило было
    // написано и разобрано целиком (чужая грядка — NotMine, не дотянуться —
    // OutOfReach, «не поспело» имени не получает, потому что срок ему игровые
    // СУТКИ, а не пять секунд Resume.WorldPauseSeconds); его история лежит в
    // этом файле, в git и в ТЕСТЫ.md. Заготовки на будущее в дереве не держим.

    public override string ToString() => Reason;
}

/// <summary>
/// Дикая еда: ягоды, грибы, ДИКИЕ ПОСЕВЫ (злаки и овощи, что растут сами).
///
/// В игре это два разных действия, и бот делает именно их:
/// - ягодные кусты СОБИРАЮТ, удерживая правую кнопку (поведение Harvestable);
/// - грибы и посевы ЛОМАЮТ (обычная добыча, поэтому идёт через Mining).
/// Выпавшее подбирается ногами — бот проходит по вещам, как игрок.
///
/// ОТКУДА ВЗЯТ СПИСОК ДИКИХ ПОСЕВОВ — не выдуман, а спрошен у игры. Генератор
/// мира сеет их сам, файлом assets/survival/worldgen/blockpatches/crop.json:
/// пятнами по 5–16 штук ложатся spelt, rye, rice, amaranth, flax, sunflower,
/// soybean, peanut, cassava, pineapple, licorice, fennel и овощи — carrot,
/// onion, parsnip, turnip. Все они — блоки вида «crop-‹вид›-‹стадия›» класса
/// BlockCrop (assets/survival/blocktypes/plant/crop/*.json), и на грядке, и в
/// поле это ОДИН И ТОТ ЖЕ блок. Спелость видна прямо в коде — последним числом,
/// а сколько стадий у этого вида, говорит реестр сервера (CropProps.GrowthStages).
/// Еда падает с них не «по названию»: бот смотрит ВЫПАДЕНИЯ блока в реестре
/// (Block.Drops) и спрашивает про них сытость — так работают и модовые посевы.
///
/// ЧУЖИЕ ГРЯДКИ БОТ НЕ ТРОГАЕТ. Дикий посев и посаженный человеком отличаются
/// только клеткой под ними: под посаженным вспаханная земля («farmland-…»),
/// под диким обычная. Живой игрок это видит, и бот обязан видеть тоже.
///
/// Что считать едой — настройка: списки масок открыты, роль может дописать
/// свои (модовые ягоды, чужие растения).
/// </summary>
public class Foraging
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Foraging(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;

        // Ягодник и посев по дороге не сносим: живой игрок обходит куст, с
        // которого потом кормится, и не топчет поле, а не расчищает ими тропу.
        // Знание «что такое куст и что такое посев» остаётся здесь одно (маски
        // открыты роли, в том числе для модовых ягод), а расчистка подлеска
        // про это только спрашивает
        mining.SpareGrowth = code => IsBush(code) || IsCrop(code);
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// Что собирают удержанием правой кнопки. В 1.20+ ягодные кусты называются
    /// «fruitingbush-…», старое имя оставлено для прежних миров и модов.
    /// </summary>
    public List<string> HarvestableMasks { get; } = ["fruitingbush", "berrybush"];

    /// <summary>
    /// Что ломают ради еды помимо посевов (грибы).
    ///
    /// ОТСЮДА УБРАНЫ «wildvegetable» и «smallcactus». Не по вкусу, а по факту:
    /// в 1.22 таких блоков в игре НЕТ ВОВСЕ — ни в реестре, ни в посадках
    /// генератора мира (проверено по assets/survival/worldgen/blockpatches).
    /// Две маски из трёх не совпадали ни с чем, и «бот собирает дикие овощи»
    /// было неправдой ровно с того дня, как это написали. Настоящие дикие
    /// овощи — это посевы carrot, onion, parsnip, turnip, и берутся они ниже,
    /// вместе со злаками.
    /// </summary>
    public List<string> BreakableMasks { get; } = ["mushroom"];

    /// <summary>
    /// Как в этом мире называются посевы. Ванильное имя одно — «crop»
    /// (assets/survival/blocktypes/plant/crop/*.json, code: "crop"), список
    /// открыт для модов, которые сеют своё под другим именем.
    ///
    /// Маска — только запасной ход: сперва бот спрашивает у реестра, есть ли у
    /// блока свойства посева (CropProps). Мод, сделавший посев по-игровому,
    /// подхватится сам, без единой правки.
    /// </summary>
    public List<string> CropMasks { get; } = ["crop"];

    /// <summary>Собирать дикие посевы (злаки и овощи, что растут сами).</summary>
    public bool HarvestWildCrops { get; set; } = true;

    /// <summary>
    /// Не трогать посевы на вспаханной земле. Это ЧУЖОЕ ПОЛЕ: дикий посев
    /// растёт прямо на земле, а вспаханная клетка под ним значит, что кто-то
    /// это посадил — человек, деревня или сам бот.
    /// </summary>
    public bool SkipFarmland { get; set; } = true;

    /// <summary>
    /// Часть кода блока вспаханной земли. Взята из игры (blocktypes/soil/
    /// farmland.json, code: "farmland"), а не придумана; открыта для модов.
    /// </summary>
    public string FarmlandMask { get; set; } = "farmland";

    /// <summary>Сколько держать кнопку на кусте (в игре сбор ~1 с).</summary>
    public double HarvestSeconds { get; set; } = 1.2;

    /// <summary>Собирать только заведомо безопасную еду (поганки — мимо).</summary>
    public bool AvoidPoisonous { get; set; } = true;

    /// <summary>
    /// Потолок времени на заход, когда его не назвали в вызове. Нужен затем же,
    /// зачем он есть у карьера и штольни: очередь задач умеет подкручивать
    /// «минут» только через порог механизма, а не через аргументы навыка —
    /// иначе одно и то же число жило бы в двух местах.
    /// </summary>
    public double MaxSeconds { get; set; } = 180;

    /// <summary>Номер спелой стадии куста (EnumFruitingBushGrowthState.Ripe).</summary>
    private const int RipeGrowthState = 4;

    /// <summary>Куст вообще (спелость по коду не видна — она в блок-сущности).</summary>
    public bool IsBush(string code) =>
        HarvestableMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Спелый ли куст. Спелость живёт в блок-сущности (growthState, Ripe = 4),
    /// а НЕ в коде блока: коды вида «fruitingbush-grown-blueberry-free»
    /// про урожай ничего не говорят. Прежняя проверка по слову «ripe»
    /// не находила ни одного куста в игре.
    /// </summary>
    public bool IsRipeBush(BlockPos pos)
    {
        if (ctx.World.GetBlockCode(pos) is not { } code || !IsBush(code))
            return false;
        var attrs = ctx.World.GetBlockEntity(pos)?.Attributes;
        // Нет блок-сущности (старые кусты) — судим по коду, как раньше
        return attrs == null
            ? code.Contains("ripe", StringComparison.OrdinalIgnoreCase)
            : attrs.GetInt("growthState") == RipeGrowthState;
    }

    public bool IsBreakableFood(string code)
    {
        if (!BreakableMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return false;
        // Поганка сытная, но отнимает 50 хп при максимуме 15 — не еда
        return !AvoidPoisonous || ctx.World.GetFoodHealthByCode(code) >= 0;
    }

    // ---------------- дикие посевы ----------------

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: какая это стадия роста. У посевов игра держит её прямо
    /// в коде блока последним числом («crop-spelt-9» — девятая). null — числа
    /// в конце нет, значит это не посев.
    /// </summary>
    public static int? StageOf(string code)
    {
        int cut = code.LastIndexOf('-');
        if (cut < 0 || cut + 1 >= code.Length)
            return null;
        return int.TryParse(code[(cut + 1)..], out int stage) ? stage : null;
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: код без стадии, вместе с чёрточкой («game:crop-spelt-»).
    /// По нему у реестра спрашивают, какие вообще стадии у этого вида бывают.
    /// </summary>
    public static string? StageFamily(string code)
    {
        int cut = code.LastIndexOf('-');
        return cut < 0 ? null : code[..(cut + 1)];
    }

    private readonly Dictionary<string, int> ripeStages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Посев ли это. Спрашиваем РЕЕСТР: у блока-посева игра присылает свойства
    /// роста (CropProps), и модовый посев узнаётся так же, как ванильный.
    /// Маска — только запасной ход, если реестр блоков ещё не собран.
    /// </summary>
    public bool IsCrop(string code)
    {
        if (StageOf(code) == null)
            return false;
        if (ctx.World.GameBlocksReady && ctx.World.GetGameBlockByCode(code) is { } block)
            return block.CropProps != null ||
                   CropMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase));
        return CropMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// На какой стадии посев считается спелым. Сперва слово реестра
    /// (CropProps.GrowthStages — у пшеницы 9, у ананаса 16), а если его нет —
    /// самая большая стадия, какая вообще есть у этого вида в реестре блоков.
    /// Оба ответа спрошены у игры; выдуманного числа здесь нет.
    /// Возвращает 0, если спросить не у кого (реестр ещё не пришёл).
    /// </summary>
    public int RipeStage(string code)
    {
        if (StageFamily(code) is not { } family)
            return 0;
        if (ripeStages.TryGetValue(family, out int known))
            return known;

        int stages = 0;
        if (ctx.World.GameBlocksReady && ctx.World.GetGameBlockByCode(code) is { CropProps: { } props })
            stages = props.GrowthStages;
        if (stages <= 0)
            foreach (string sibling in ctx.World.SearchBlockCodes(family, limit: 64))
                if (StageOf(sibling) is { } s && s > stages)
                    stages = s;

        // Реестра ещё нет — не запоминаем ноль навсегда, спросим позже
        if (stages > 0)
            ripeStages[family] = stages;
        return stages;
    }

    /// <summary>
    /// Посев дорос до последней стадии — с него и падает еда, а не одни семена.
    /// Генератор мира сеет пятно вперемешку («crop-spelt-2», «-4», «-6», «-9»),
    /// поэтому половина пятна на еду не годится, и это не повод его ломать.
    /// </summary>
    public bool IsRipeCrop(string code)
    {
        if (!IsCrop(code))
            return false;
        int ripe = RipeStage(code);
        return ripe > 0 && StageOf(code) >= ripe;
    }

    /// <summary>
    /// С этого посева падает БЕЗОПАСНАЯ ЕДА. Спрашиваем выпадения блока в
    /// реестре (Block.Drops) и сытость каждого — так лён (одни волокна и
    /// зерно) и морковь разбираются сами собой, и модовые посевы тоже.
    /// </summary>
    public bool CropFeeds(string code)
    {
        if (ctx.Gathering is not { } gathering)
            return false;   // спросить не у кого — не выдумываем
        foreach (string drop in gathering.DropsOf(code))
            if (ctx.World.IsSafeFood(drop))
                return true;
        return false;
    }

    /// <summary>
    /// Под посевом ЧУЖАЯ ГРЯДКА. Дикий посев генератор мира сеет прямо на
    /// землю; вспаханная клетка под ним значит, что это чьё-то поле.
    /// </summary>
    public bool OnFarmland(BlockPos pos) =>
        ctx.World.GetBlockCode(pos.X, pos.Y - 1, pos.Z) is { } under &&
        under.Contains(FarmlandMask, StringComparison.OrdinalIgnoreCase);

    /// <summary>Спелый дикий посев, с которого падает еда, и он не на чужой грядке.</summary>
    public bool IsWildFoodCrop(BlockPos pos) =>
        HarvestWildCrops &&
        ctx.World.GetBlockCode(pos) is { } code && IsRipeCrop(code) && CropFeeds(code) &&
        !(SkipFarmland && OnFarmland(pos));

    // ---------------- поиск ----------------

    /// <summary>Что вообще может оказаться едой — по одному коду, без клетки.</summary>
    private bool LooksEdible(string code) =>
        IsBush(code) || IsBreakableFood(code) || (HarvestWildCrops && IsCrop(code));

    /// <summary>
    /// Съедобные блоки вокруг точки: кусты со спелыми ягодами, грибы и спелые
    /// дикие посевы (чужие грядки не в счёт).
    /// </summary>
    private IEnumerable<BlockPos> FoodBlocksAround(BlockPos centre, int radius, int limit) =>
        ctx.World.FindBlocks(LooksEdible, centre, radius, height: 6, limit: limit * 3)
            .Where(p => IsRipeBush(p) ||
                        (ctx.World.GetBlockCode(p) is { } c && IsBreakableFood(c)) ||
                        IsWildFoodCrop(p))
            .Take(limit);

    /// <summary>Ближайший источник дикой еды.</summary>
    public BlockPos? FindFood(int radius = 24)
    {
        if (ctx.Self.Position is not { } p)
            return null;
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        return FoodBlocksAround(here, radius, 1).Cast<BlockPos?>().FirstOrDefault();
    }

    /// <summary>
    /// Собрать конкретный куст/гриб. Успех — только если в инвентаре
    /// ПРИБАВИЛОСЬ: «кликнул» и «собрал» — разные вещи (правило 4).
    /// </summary>
    public async Task<bool> HarvestAsync(BlockPos pos, CancellationToken ct = default) =>
        await TryHarvestAsync(pos, ct) == ForageMiss.None;

    /// <summary>
    /// То же самое, но с ПРИЧИНОЙ отказа. Молчаливый false наверху сливался с
    /// «еды тут нет», и бот в неспелом малиннике докладывал, что малины нет.
    /// </summary>
    public async Task<ForageMiss> TryHarvestAsync(BlockPos pos, CancellationToken ct = default)
    {
        string code = ctx.World.GetBlockCode(pos) ?? "";
        if (code.Length == 0)
            return ForageMiss.NotFood;

        if (IsBush(code))
        {
            if (!IsRipeBush(pos))
            {
                OnLog?.Invoke($"{code} в {pos} ещё не поспел — не трогаю");
                return ForageMiss.Unripe;
            }
            if (!await hands.ReachForAsync(pos, ct: ct))
            {
                OnLog?.Invoke($"до {code} в {pos} не дотянуться");
                return ForageMiss.OutOfReach;
            }

            int before = FoodCount();
            await hands.UseBlockAsync(pos, HarvestSeconds, ct: ct);
            await Task.Delay(700, ct).ContinueWith(_ => { });
            await mining.CollectDropsAsync(4, 8, ct);

            int gained = FoodCount() - before;
            if (gained <= 0)
            {
                OnLog?.Invoke($"{code}: ягод не прибавилось");
                return ForageMiss.NothingDropped;
            }
            OnLog?.Invoke($"обобрал {code} (+{gained})");
            return ForageMiss.None;
        }

        // ДИКИЙ ПОСЕВ. Ломается как обычный блок, но перед ударом надо
        // ответить на два вопроса, которых у гриба нет: дорос ли он до еды и
        // не чужая ли это грядка
        if (HarvestWildCrops && IsCrop(code))
        {
            if (SkipFarmland && OnFarmland(pos))
            {
                OnLog?.Invoke($"{code} в {pos} растёт на вспаханной земле — это чужая грядка, не трогаю");
                return ForageMiss.Farmed;
            }
            if (!IsRipeCrop(code))
            {
                OnLog?.Invoke($"{code} в {pos} ещё не дорос " +
                              $"(стадия {StageOf(code)} из {RipeStage(code)}) — не трогаю");
                return ForageMiss.Unripe;
            }
            if (!CropFeeds(code))
            {
                OnLog?.Invoke($"с {code} еда не падает — мимо");
                return ForageMiss.NotFood;
            }

            int had = FoodCount();
            var broke = await mining.BreakAsync(pos, ct);
            if (!broke.Success)
            {
                OnLog?.Invoke($"{code}: {broke}");
                return ForageMiss.NotBroken;
            }
            await mining.CollectDropsAsync(4, 8, ct);
            int got = FoodCount() - had;
            OnLog?.Invoke(got > 0 ? $"сжал {code} (+{got})" : $"{code} сломан, но еды не прибавилось");
            return got > 0 ? ForageMiss.None : ForageMiss.NothingDropped;
        }

        if (IsBreakableFood(code))
        {
            int before = FoodCount();
            var result = await mining.BreakAsync(pos, ct);
            if (!result.Success)
            {
                OnLog?.Invoke($"{code}: {result}");
                return ForageMiss.NotBroken;
            }
            await mining.CollectDropsAsync(4, 8, ct);
            int gained = FoodCount() - before;
            OnLog?.Invoke(gained > 0 ? $"собрал {code} (+{gained})" : $"{code} сломан, но в сумке пусто");
            return gained > 0 ? ForageMiss.None : ForageMiss.NothingDropped;
        }
        return ForageMiss.NotFood;
    }

    /// <summary>Сколько съедобного (и безопасного) у бота с собой.</summary>
    private int FoodCount()
    {
        int total = 0;
        foreach (var (invId, slots) in ctx.Self.OwnInventories)
        {
            if (invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            foreach (var s in slots)
                if (s.Code is { } c && ctx.World.IsSafeFood(c))
                    total += s.Count;
        }
        return total;
    }

    /// <summary>
    /// Обойти окрестности и набрать еды: столько источников, сколько успеется.
    /// Возвращает, сколько мест обобрано.
    /// </summary>
    /// <param name="anchor">
    /// Откуда мерить радиус. Если не задан — от места, где бот стоит СЕЙЧАС,
    /// и это важно: раньше точка отсчёта пересчитывалась на каждом шаге, и бот
    /// уходил «радиус за радиусом» — с 24 блоками на шести кустах получалось
    /// больше сотни блоков от лавки.
    /// </param>
    public async Task<ForageReport> GatherAsync(int radius = 32, int maxPlaces = 8,
        double maxSeconds = 180, BlockPos? anchor = null, CancellationToken ct = default)
    {
        var misses = new Dictionary<ForageMiss, int>();
        void Missed(ForageMiss why) => misses[why] = misses.GetValueOrDefault(why) + 1;

        if (ctx.Self.Position is not { } start)
            // «ГДЕ Я — ПОКА НЕИЗВЕСТНО» — ЭТО НЕДОЕХАВШИЙ МИР, а не свойство
            // места: своя сущность приходит уже после входа. Помеха временная,
            // и сказано это СЛОВАМИ, потому что читать их будет человек:
            // общего слова (Setback) у сбора нет — разобрано у ForageReport
            return new ForageReport(0, 0, misses,
                "за едой не пошёл: где я — пока неизвестно (своя сущность приходит " +
                "уже после входа в мир)");
        var home = anchor ?? new BlockPos(
            (int)Math.Floor(start.X), (int)Math.Floor(start.Y), (int)Math.Floor(start.Z));

        // ЧТО ВООБЩЕ ВИДНО ВОКРУГ — считаем ДО сбора и вместе с неспелым.
        // Иначе неспелый малинник неотличим от голого поля, а это разные
        // новости: в первом случае надо подождать, во втором — уйти
        int seen = 0, unripe = 0, farmed = 0;
        foreach (var p in ctx.World.FindBlocks(LooksEdible, home, radius, height: 6, limit: 96))
        {
            seen++;
            if (ctx.World.GetBlockCode(p) is not { } c)
                continue;
            if (IsBush(c) && !IsRipeBush(p))
                unripe++;
            // Дикое пятно посевов игра сеет вперемешку по стадиям, и половина
            // его на еду не годится — это не «поля нет», а «рано». Чужую
            // грядку считаем отдельно: там дело не в спелости, а в том, что
            // это не наше
            else if (HarvestWildCrops && IsCrop(c))
            {
                if (SkipFarmland && OnFarmland(p))
                    farmed++;
                else if (!IsRipeCrop(c))
                    unripe++;
            }
        }
        if (unripe > 0)
            misses[ForageMiss.Unripe] = unripe;
        if (farmed > 0)
            misses[ForageMiss.Farmed] = farmed;

        int done = 0;
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        var visited = new HashSet<BlockPos>();

        while (done < maxPlaces && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var target = FoodBlocksAround(home, radius, 32).FirstOrDefault(b => !visited.Contains(b));
            if (target == default)
                break;
            visited.Add(target);
            var why = await TryHarvestAsync(target, ct);
            if (why == ForageMiss.None)
                done++;
            else
                Missed(why);
        }

        // Возвращаемся к якорю: бот ушёл за едой, а не переехал
        if (done > 0 && !ct.IsCancellationRequested)
            await ctx.Movement.TravelToAsync(home, 90, ct);

        // Помехи общим словом (Setback) у сбора нет нарочно — разобрано у
        // ForageReport: читать её тут некому, а поле, которое считают и не
        // читают, врёт про умение, которого нет
        var report = new ForageReport(done, seen, misses, Say(done, seen, misses, radius));
        OnLog?.Invoke(report.Reason);
        return report;
    }

    /// <summary>
    /// Итог обхода словами. Правило чистое (числа уже посчитаны), и оно тут
    /// одно: «ничего не собрал» обязано называть, ЧТО именно помешало, иначе
    /// наверху всё сливается в «еды вокруг не нашлось».
    /// </summary>
    public static string Say(int places, int seen, IReadOnlyDictionary<ForageMiss, int> misses,
        int radius)
    {
        if (places > 0)
            return $"обобрал мест: {places}" + (seen > places ? $" (видел источников {seen})" : "");
        if (seen == 0)
            return $"дикой еды в {radius} бл вокруг не видно";

        var parts = new List<string>();
        void Part(ForageMiss why, string word)
        {
            if (misses.TryGetValue(why, out int n) && n > 0)
                parts.Add($"{word} {n}");
        }
        Part(ForageMiss.Unripe, "ещё не поспело");
        Part(ForageMiss.Farmed, "на чужой грядке");
        Part(ForageMiss.OutOfReach, "не дотянуться до");
        Part(ForageMiss.NotBroken, "не сломать");
        Part(ForageMiss.NothingDropped, "собрал впустую");
        Part(ForageMiss.NotFood, "оказалось не едой");

        return $"дикая еда в {radius} бл есть ({seen} источников), но взять её не вышло: " +
               (parts.Count > 0 ? string.Join(", ", parts) : "ни один не дался");
    }
}
