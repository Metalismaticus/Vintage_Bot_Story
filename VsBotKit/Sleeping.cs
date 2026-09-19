using Vintagestory.API.Datastructures;
using BlockFacing = Vintagestory.API.MathTools.BlockFacing;

namespace VsBotKit;

/// <summary>Найденная кровать: куда кликать, где её блок-сущность и сколько она даёт сна.</summary>
public sealed record BedInfo(BlockPos Pos, BlockPos Head, string Code, float SleepEfficiency)
{
    /// <summary>
    /// Сколько часов сна даёт кровать. Формула — из самой игры
    /// (BlockBed.GetPlacedBlockInfo): sleepEfficiency × часов в сутках / 2.
    /// </summary>
    public double SleepHours(double hoursPerDay) => SleepEfficiency * hoursPerDay / 2.0;

    public override string ToString() => $"{Code} {Pos}";
}

/// <summary>
/// Кровать и сон.
///
/// Как это устроено в игре (читано в BlockBed / BlockEntityBed / ModSleeping,
/// а не додумано):
///
/// - кровать — два блока: «...-head-&lt;сторона&gt;» и «...-feet-&lt;сторона&gt;»;
///   блок-сущность (и место посадки) живёт только у ГОЛОВЫ. Правый клик по
///   любой половине сервер приводит к голове тем же способом, что и мы здесь:
///   facing = BlockFacing.FromCode(LastCodePart(0)), и если кликнули по ногам,
///   голова = позиция + facing.Opposite (BlockBed.OnBlockInteractStart);
/// - лечь = сесть на кровать (EntityAgent.TryMount). Сервер откажет, если:
///     * кровать уже занята (BlockEntityBed.MountedBy != null),
///     * усталость игрока ≤ 8 (EntityBehaviorTiredness.Tiredness, жёсткое
///       число 8f прямо в BlockBed),
///     * идёт временная буря, а в конфиге мира temporalStormSleeping = 0
///       (значение по умолчанию);
///   ОТКАЗ МОЛЧАЛИВЫЙ: сообщение «not-tired-enough» показывает себе сам
///   клиент, сервер в этом случае просто ничего не делает. Поэтому «лёг» —
///   только по подтверждению состояния (см. IsSleeping), а не по факту клика;
/// - лёг → сервер пишет в WatchedAttributes игрока поддерево "mountedOn"
///   (className = "bed", posx/posy/posz — координаты головы кровати) и
///   ставит tiredness/isSleeping = 1. Оба дерева синхронизируются клиенту —
///   это ровно то же знание, что у живого игрока;
/// - встаёт игрок ПРИСЕДАНИЕМ: клавиша Sneak уходит в EntityControls КРОВАТИ
///   (ServerMain.HandleMoveKeyChange отдаёт клавиши сиденью), а её обработчик
///   BlockEntityBed.onControls на Sneak делает TryUnmount. Сервер снимает
///   атрибут "mountedOn" — а RemoveAttribute у SyncedTreeAttribute помечает
///   дерево целиком грязным, так что пробуждение клиенту тоже видно;
/// - сам сервер поднимает игрока, когда усталость дошла до нуля
///   (BlockEntityBed.RestPlayer) или когда началась буря;
/// - СОН УСКОРЯЕТ ВРЕМЯ, но не сам по себе: ModSleeping разгоняет календарь
///   («sleeping», до ×17000) только когда СПЯТ ВСЕ онлайн-игроки, кроме
///   зрителей (ModSleeping.AreAllPlayersSleeping). Один бот на людном сервере
///   время не ускорит.
///
/// Точка возрождения кроватью НЕ ставится: в Vintage Story её ставит
/// временная шестерёнка (см. SetRespawnHereAsync).
///
/// Это механизм. Когда ложиться и стоит ли — дело роли.
/// </summary>
public class Sleeping
{
    private readonly BotContext ctx;
    private readonly Hands hands;

    public Sleeping(BotContext ctx, Hands hands)
    {
        this.ctx = ctx;
        this.hands = hands;
        ctx.Bot.OnPacket += HandlePacket;
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// Порог усталости, ниже которого игра не даёт лечь. Число 8f зашито
    /// в самой игре (BlockBed.OnBlockInteractStart), а не выбрано нами.
    /// </summary>
    public const float MinTiredness = 8f;

    /// <summary>Сколько ждать подтверждения от сервера после клика по кровати.</summary>
    public double ConfirmSeconds { get; set; } = 2.5;

    // ---------------- что бот знает о себе ----------------

    private ITreeAttribute? OwnTree(string path) =>
        ctx.Entities.Self?.WatchedAttributes.GetTreeAttribute(path);

    /// <summary>Пришло ли вообще дерево усталости (иначе поведение «tiredness» выключено).</summary>
    public bool TirednessKnown => OwnTree("tiredness") != null;

    /// <summary>Усталость бота: растёт 0.75 за игровой час, максимум — часов в сутках / 2.</summary>
    public float Tiredness => OwnTree("tiredness")?.GetFloat("tiredness", 0f) ?? 0f;

    /// <summary>Максимум усталости (EntityBehaviorTiredness.SlowTick: HoursPerDay / 2).</summary>
    public float MaxTiredness => ctx.Clock.HoursPerDay / 2f;

    /// <summary>
    /// На какой кровати бот сейчас лежит (null — ни на какой). Берётся из
    /// "mountedOn": сервер кладёт туда className и координаты блок-сущности
    /// сиденья (BlockEntityBed.MountableToTreeAttributes).
    /// </summary>
    public BlockPos? SleepingOn
    {
        get
        {
            var t = OwnTree("mountedOn");
            if (t == null || t.GetString("className") != "bed")
                return null;
            // posy сервер пишет как InternalY (высота + измерение × 32768);
            // в нулевом измерении это та же координата
            return new BlockPos(t.GetInt("posx"), t.GetInt("posy") % 32768, t.GetInt("posz"));
        }
    }

    /// <summary>Бот лежит в кровати (подтверждено атрибутами от сервера).</summary>
    public bool IsSleeping => SleepingOn != null;

    /// <summary>Флаг сна у самого игрока (его же читает ModSleeping, считая спящих).</summary>
    public bool IsSleepingFlag => (OwnTree("tiredness")?.GetInt("isSleeping", 0) ?? 0) > 0;

    // ---------------- поиск кровати ----------------

    /// <summary>
    /// Кровать ли это. Признак берём тот же, что и сама игра: у блока кровати
    /// есть атрибут sleepEfficiency (BlockBed.GetPlacedBlockInfo,
    /// BlockEntityBed.Initialize). Реестр блоков пришёл с сервера, так что
    /// это работает и для модовых кроватей, о которых мы ничего не знаем.
    /// </summary>
    public bool IsBed(BlockPos pos) => Describe(pos) != null;

    /// <summary>Кровать ли блок с таким кодом (для поиска по коду, без чтения мира).</summary>
    public bool IsBedCode(string code) =>
        ctx.World.GameBlocksReady && SleepEfficiencyOf(ctx.World.GetGameBlockByCode(code)) != null;

    private static float? SleepEfficiencyOf(Vintagestory.API.Common.Block? block)
    {
        var attrs = block?.Attributes;
        if (attrs == null)
            return null;
        var value = attrs["sleepEfficiency"];
        return value.Exists ? value.AsFloat(0.5f) : null;
    }

    /// <summary>
    /// Разобрать кровать в точке: где её голова (там блок-сущность и место сна)
    /// и какова эффективность сна. null — в точке не кровать.
    /// </summary>
    public BedInfo? Describe(BlockPos pos)
    {
        if (!ctx.World.GameBlocksReady)
            return null;
        var block = ctx.World.GetGameBlock(pos.X, pos.Y, pos.Z);
        if (SleepEfficiencyOf(block) is not { } efficiency)
            return null;

        string code = block.Code?.ToShortString() ?? "";

        // Ровно как в BlockBed.OnBlockInteractStart: сторона — последняя часть
        // кода, «голова/ноги» — предпоследняя; от ног до головы шаг в сторону,
        // противоположную коду
        var head = pos;
        if (block.LastCodePart(1) == "feet" && BlockFacing.FromCode(block.LastCodePart(0)) is { } facing)
        {
            var d = facing.Opposite.Normali;
            head = new BlockPos(pos.X + d.X, pos.Y + d.Y, pos.Z + d.Z);
        }
        return new BedInfo(pos, head, code, efficiency);
    }

    /// <summary>
    /// Ближайшая кровать вокруг точки (по умолчанию — вокруг бота).
    /// Ищет по загруженным чанкам, то есть только там, где бот уже был.
    ///
    /// ИЩЕТ ШАРОМ, А НЕ БЛИНОМ, И ЭТО ЖИВАЯ ПРАВКА. Здесь стояло
    /// <c>height: 4</c> — четыре клетки вверх и четыре вниз, — а радиус вокруг
    /// был 32. Жалоба заказчика: «он не учитывает вообще любую кровать в своем
    /// доме, пофиг кто её поставил, главное кровать в его доме». Дом у него в
    /// два этажа с погребом: кровать на втором этаже стоит на семь клеток выше
    /// порога, кровать в подвале — на пять ниже, и НИ ОДНУ из них бот не видел,
    /// даже стоя в дверях. Число «32» человеку обещали расстоянием («не дальше
    /// 32 бл»), а меряли им только два направления из шести.
    ///
    /// Теперь коробка обхода — КУБ со стороной радиуса, а из найденного берётся
    /// то, что и правда лежит в шаре: угол куба отстоит на 55 бл при радиусе 32,
    /// и отдать такую кровать значило бы «нашёл и отказался» — ровно тот случай,
    /// от которого предостерегает <see cref="BehaviorSleepAtNight.MaxBedDistance"/>.
    /// Поэтому лимит обхода поднят: угловые кровати выбрасываются здесь, и
    /// набрать восьмёрку одними углами больше нельзя.
    ///
    /// ЦЕНА ИЗМЕРЕНА, А НЕ ПРИКИНУТА: живой бот 02.09 обошёл 33×33×97 клеток за
    /// 0,04 с, то есть 2,6 млн клеток в секунду; куб 65×65×65 — это 0,1 с.
    /// Столько раз в секунду его никто и не зовёт (см. <see cref="BehaviorSleepAtNight"/>:
    /// пустой поиск отдыхает <see cref="BehaviorSleepAtNight.RetrySeconds"/>).
    /// </summary>
    public BedInfo? FindBedNear(BlockPos? center = null, int radius = 16)
    {
        if (center is not { } from)
        {
            if (ctx.Self.Position is not { } p)
                return null;
            from = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        }
        // Отбор по коду, а не перебор всего подряд: FindBlocks обходит кольцами
        // и обрывается по лимиту, так что фильтровать надо в самом предикате
        foreach (var pos in ctx.World.FindBlocks(IsBedCode, from, radius, radius, limit: 32))
            if (Depot.Apart(from, pos) <= radius && Describe(pos) is { } bed)
                return bed;
        return null;
    }

    /// <summary>
    /// Занята ли кровать: сервер держит в блок-сущности id и uid лежащего
    /// (BlockEntityBed.ToTreeAttributes) и рассылает её клиентам при посадке
    /// (MarkDirty в DidMount). null — данных о блок-сущности ещё нет,
    /// то есть честный ответ «не знаю».
    /// </summary>
    public bool? IsOccupied(BedInfo bed)
    {
        var be = ctx.World.BlockEntitiesIn(bed.Head, bed.Head).FirstOrDefault();
        if (be?.Attributes is not { } tree)
            return null;
        return tree.GetLong("mountedByEntityId", 0) != 0 ||
               tree.GetString("mountedByPlayerUid") != null;
    }

    // ---------------- можно ли лечь ----------------

    /// <summary>
    /// Пустит ли игра бота в постель прямо сейчас. Проверяются те же условия,
    /// что и на сервере — чтобы не слать заведомо бесполезный клик и, главное,
    /// чтобы уметь назвать причину отказа (сервер её не присылает).
    /// </summary>
    public bool CanSleepNow(BedInfo? bed, out string reason)
    {
        if (ctx.Entities.Self == null)
        {
            reason = "себя ещё не вижу";
            return false;
        }
        if (IsSleeping)
        {
            reason = "уже в кровати";
            return false;
        }
        if (bed == null)
        {
            reason = "кровати нет";
            return false;
        }
        // Буря: BlockBed отказывает при StormStrength > 0, если в конфиге мира
        // temporalStormSleeping = 0 (значение по умолчанию). Сам конфиг серверу
        // клиент не присылает, поэтому считаем запретом любую активную бурю
        if (ctx.Storms.Known && ctx.Storms.Active)
        {
            reason = "идёт временная буря — игра не даёт спать";
            return false;
        }
        if (TirednessKnown && Tiredness <= MinTiredness)
        {
            reason = $"ещё не хочу спать (усталость {Tiredness:0.#} из нужных > {MinTiredness})";
            return false;
        }
        if (IsOccupied(bed) == true)
        {
            reason = "кровать занята";
            return false;
        }
        reason = "";
        return true;
    }

    // ---------------- лечь и встать ----------------

    /// <summary>Лёг в кровать (подтверждено сервером).</summary>
    public event Action<BedInfo>? OnWentToBed;

    /// <summary>Лечь не вышло — с причиной.</summary>
    public event Action<string>? OnCouldNotSleep;

    /// <summary>Встал (сам или подняли).</summary>
    public event Action? OnWokeUp;

    /// <summary>
    /// Дойти до кровати и лечь. Возвращает true ТОЛЬКО если сервер подтвердил
    /// посадку атрибутом "mountedOn" — «отправил клик» успехом не считается.
    /// </summary>
    public async Task<bool> GoToBedAsync(BedInfo? bed = null, double maxSeconds = 60,
        CancellationToken ct = default)
    {
        bed ??= FindBedNear();
        if (!CanSleepNow(bed, out string why) || bed == null)
        {
            if (IsSleeping)
                return true;
            OnLog?.Invoke($"спать не буду: {why}");
            OnCouldNotSleep?.Invoke(why);
            return false;
        }

        // Кликать можно по любой половине, но подходить лучше к той, что нашли
        var click = bed.Pos;
        // Результат дороги сам по себе ничего не решает: важно только, дотянемся
        // ли мы в итоге — это и проверит ReachForAsync
        if (!hands.InReach(click))
            await ctx.Movement.TravelToAsync(click, maxSeconds, ct);
        if (!await hands.ReachForAsync(click, maxSeconds, ct))
        {
            const string far = "до кровати не дотянуться";
            OnLog?.Invoke(far);
            OnCouldNotSleep?.Invoke(far);
            return false;
        }

        // Присед снимаем ДО клика: после посадки клавиша Sneak уходит в
        // управление кровати и тут же поднимает бота обратно
        await ctx.Actions.SetSneakAsync(false);

        // Обычный правый клик без удержания — как у игрока: сервер сажает
        // в кровать уже на StartBlockUse (BlockBed.OnBlockInteractStart)
        await hands.UseBlockAsync(click, seconds: 0, onFace: 4, sneak: false, ct: ct);

        if (await WaitAsync(() => IsSleeping, ConfirmSeconds, ct))
        {
            OnLog?.Invoke($"лёг спать: {bed}");
            OnWentToBed?.Invoke(bed);
            return true;
        }

        // Сервер молча отказал — назовём наиболее вероятную причину по тем же
        // данным, что были у него
        string reason = IsOccupied(bed) == true ? "кровать заняли"
            : ctx.Storms.Known && ctx.Storms.Active ? "началась буря"
            : TirednessKnown && Tiredness <= MinTiredness ? $"усталость всего {Tiredness:0.#}"
            : "сервер не подтвердил посадку (клан/приват? кровать сломана?)";
        OnLog?.Invoke($"лечь не удалось: {reason}");
        OnCouldNotSleep?.Invoke(reason);
        return false;
    }

    /// <summary>
    /// Встать с кровати. Игрок делает это приседанием: клавиша уходит в
    /// управление кровати, и её обработчик снимает игрока (BlockEntityBed.onControls).
    /// Возвращает true, когда сервер снял атрибут "mountedOn".
    ///
    /// Почему сначала «отпустить», а потом «нажать»: кровать реагирует только
    /// на ПЕРЕХОД клавиши в нажатую (EntityControls.UpdateFromPacket сравнивает
    /// с прошлым состоянием), а её собственный флаг Sneak после прошлого подъёма
    /// остаётся взведённым — StopAllMovement в onControls отрабатывает РАНЬШЕ,
    /// чем AttemptToggleAction запишет flags[Sneak] = true. Живой игрок
    /// выбирается из этого, просто нажав Shift ещё раз; делаем так же.
    /// </summary>
    public async Task<bool> WakeAsync(CancellationToken ct = default)
    {
        if (!IsSleeping)
            return true;

        bool up = false;
        for (int attempt = 0; attempt < 2 && !up; attempt++)
        {
            await ctx.Actions.SetSneakAsync(false);
            await Task.Delay(100, ct).ContinueWith(_ => { });
            await ctx.Actions.SetSneakAsync(true);
            await Task.Delay(150, ct).ContinueWith(_ => { });
            await ctx.Actions.SetSneakAsync(false);
            up = await WaitAsync(() => !IsSleeping, ConfirmSeconds, ct);
        }
        if (up)
        {
            OnLog?.Invoke("встал с кровати");
            OnWokeUp?.Invoke();
        }
        else
        {
            OnLog?.Invoke("встать не получилось — сервер всё ещё считает бота лежащим");
        }
        return up;
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, double seconds, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(100, ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested)
                break;
        }
        return condition();
    }

    // ---------------- точка возрождения ----------------

    /// <summary>
    /// Точка возрождения, как её знает сервер. Приходит в собственном
    /// PlayerData(41) полями Spawnx/Spawny/Spawnz — сервер шлёт этот пакет
    /// владельцу и при входе, и КАЖДЫЙ РАЗ при смене точки
    /// (ServerPlayer.SetSpawnPosition → SendOwnPlayerData). Пока точка не
    /// задана, там лежит мировой спавн.
    /// </summary>
    public BlockPos? RespawnPoint { get; private set; }

    /// <summary>Сервер сообщил новую точку возрождения.</summary>
    public event Action<BlockPos>? OnRespawnPointChanged;

    private void HandlePacket(Packet_Server p)
    {
        // Пакет 41 приходит и про других игроков — там полей спавна нет
        if (p.Id != 41 || p.PlayerData is not { } pd || pd.PlayerUID != ctx.Bot.PlayerUid)
            return;
        var point = new BlockPos(pd.Spawnx, pd.Spawny, pd.Spawnz);
        if (RespawnPoint == point)
            return;
        RespawnPoint = point;
        OnRespawnPointChanged?.Invoke(point);
    }

    /// <summary>
    /// Предмет, которым в игре ставится точка возрождения. Кровать её НЕ ставит:
    /// в Vintage Story это делает временная шестерёнка (класс ItemTemporalGear,
    /// код предмета — из ассетов игры: itemtypes/resource/gear.json,
    /// classByType «gear-temporal»). Шестерёнка при этом ТРАТИТСЯ.
    /// </summary>
    public string RespawnItemCode { get; set; } = "gear-temporal";

    /// <summary>
    /// Сколько держать шестерёнку. Игра засчитывает применение строго дольше
    /// 3.45 с (ItemTemporalGear.OnHeldInteractStop), а секунды сервер меряет
    /// сам, по своим часам, — поэтому берём с запасом.
    /// </summary>
    public double RespawnHoldSeconds { get; set; } = 4.0;

    /// <summary>
    /// Поставить точку возрождения там, где бот стоит.
    ///
    /// Как это делает игрок: берёт временную шестерёнку, наводится на блок
    /// (любой, кроме транслокатора) и держит правую кнопку 3.5 секунды. Сервер
    /// ставит точку в позицию ИГРОКА, а не блока, и тратит шестерёнку.
    ///
    /// Успех подтверждается не кликом, а тем, что сервер прислал новую точку
    /// в PlayerData(41) и она рядом с ботом. Если в конфиге мира
    /// temporalGearRespawnUses = 0, игра запрещает это молча — тогда вернём false.
    /// </summary>
    public async Task<bool> SetRespawnHereAsync(CancellationToken ct = default)
    {
        if (ctx.Self.Position is not { } p)
            return false;
        if (hands.CountOf(RespawnItemCode) <= 0)
        {
            OnLog?.Invoke($"нет предмета {RespawnItemCode} — точку возрождения ставить нечем");
            return false;
        }

        // Целимся в блок под ногами: до него точно дотянуться, и это не
        // транслокатор (по нему игра шестерёнку не применяет)
        var feet = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y) - 1, (int)Math.Floor(p.Z));
        if (ctx.World.GetBlockId(feet) == 0)
        {
            OnLog?.Invoke("под ногами пусто — не на что навести шестерёнку");
            return false;
        }
        if (ctx.World.GetBlockCode(feet) is { } code &&
            code.Contains("translocator", StringComparison.OrdinalIgnoreCase))
        {
            OnLog?.Invoke("под ногами транслокатор — игра шестерёнку тут не применит");
            return false;
        }

        if (await hands.TakeToHandAsync(RespawnItemCode, ct) is null)
        {
            OnLog?.Invoke($"не смог взять {RespawnItemCode} в руку");
            return false;
        }

        var before = RespawnPoint;
        int had = hands.CountOf(RespawnItemCode);

        await ctx.Movement.FaceAsync(feet.X + 0.5, feet.Z + 0.5, ct);
        await hands.UseHeldAsync(RespawnHoldSeconds, at: feet, ct: ct);

        // Ждём именно ответ сервера: и новую точку, и списанную шестерёнку
        bool moved = await WaitAsync(
            () => RespawnPoint is { } now && now != before &&
                  Math.Abs(now.X - p.X) <= 2 && Math.Abs(now.Z - p.Z) <= 2,
            ConfirmSeconds, ct);

        if (!moved)
        {
            bool spent = hands.CountOf(RespawnItemCode) < had;
            OnLog?.Invoke(spent
                ? "шестерёнка потрачена, но новую точку возрождения сервер не прислал"
                : "точка возрождения не изменилась (шестерёнка цела: держали мало? запрещено конфигом?)");
            return false;
        }

        OnLog?.Invoke($"точка возрождения теперь {RespawnPoint}");
        return true;
    }
}

/// <summary>
/// Ночью — спать, если есть куда и вокруг спокойно.
///
/// ЧТО ДОЛЖНО СОЙТИСЬ, ЧТОБЫ БОТ ЛЁГ (всё читано в BlockBed / BlockEntityBed /
/// ModSleeping сборок 1.22.6, а не додумано). Одного заданного дома МАЛО, и
/// заказчик спросил об этом прямо: «Так я же в вебформе отметил дом, разве это
/// недостаточно??»
///
///   1. НОЧЬ (<c>GameClock.IsNight</c>) — днём игра в постель не пустит;
///   2. КРОВАТЬ — блок с атрибутом sleepEfficiency. Дом её НЕ ЗАМЕНЯЕТ: точка
///      дома это «куда идти», а лечь можно только в кровать, и поставить её
///      должен хозяин. Ищется вокруг бота и вокруг дома (<see cref="Home"/>);
///   3. ДО СЛУЧАЙНОЙ КРОВАТИ НЕ ДАЛЬШЕ <see cref="MaxBedDistance"/> — иначе бот
///      бросал бы забой ради чужой койки за полкарты. КРОВАТЬ В СВОЁМ ДОМЕ ПОД
///      ЭТУ МЕРКУ НЕ ПОПАДАЕТ (<see cref="GoHomeForBed"/>): домой бот ходит без
///      потолка — так же, как в бурю и от безделья;
///   4. УСТАЛОСТЬ ВЫШЕ 8 (<see cref="Sleeping.MinTiredness"/>) — число зашито
///      в самой игре, и сервер молча отказывает всем, кто не дотянул;
///   5. НЕ ИДЁТ ВРЕМЕННАЯ БУРЯ — при temporalStormSleeping = 0 (значение мира
///      по умолчанию) игра спать не даёт;
///   6. КРОВАТЬ СВОБОДНА и вокруг нет опасных, а бота не били последние
///      <see cref="CalmSeconds"/> секунд;
///   7. НЕТ ЖИЗНЕННОЙ НУЖДЫ ВПЕРЁД СНА (<see cref="WakeFor"/>): голодного с
///      едой в сумке спать не укладывают.
///
/// Чего из этого не хватает — бот говорит САМ, ночью и один раз на причину
/// (<see cref="OnNotSleeping"/>). Молчаливых отказов здесь больше нет.
///
/// Способность ДОЛЖНА стоять первой в списке: пока бот лежит, только она может
/// решить его поднять. Если её обгонит боевая или лечебная способность, та
/// начнёт двигать лежащего бота — а лежащий игрок сервером не двигается, его
/// клавиши уходят кровати.
/// </summary>
public class BehaviorSleepAtNight : BotBehavior
{
    private readonly BotContext ctx;
    private readonly Sleeping sleeping;
    private DateTime nextTry = DateTime.MinValue;

    /// <summary>
    /// КОГДА СНОВА ОБХОДИТЬ МИР В ПОИСКАХ КРОВАТИ. Отдельно от
    /// <see cref="nextTry"/> нарочно: та пауза придерживает ВСЮ способность
    /// после неудачной попытки лечь, а эта — только сам обход, и дорога домой
    /// её не ждёт. Слепи их в одну — и бот, идущий домой, вставал бы посреди
    /// поля на полминуты после каждого отрезка.
    /// </summary>
    private DateTime nextLook = DateTime.MinValue;
    private float lastHealth = -1;
    private DateTime calmAfter = DateTime.MinValue;

    public BehaviorSleepAtNight(BotContext ctx, Sleeping sleeping)
    {
        this.ctx = ctx;
        this.sleeping = sleeping;
    }

    /// <summary>Спать вообще (можно выключить, например на время работы).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Где кровать. Если не задана — ищется рядом каждый раз.</summary>
    public BlockPos? Bed { get; set; }

    /// <summary>
    /// ДОМ — ВТОРОЕ МЕСТО, ГДЕ ИЩЕТСЯ КРОВАТЬ. Функцией, а не копией точки:
    /// дом у бота ОДИН (<see cref="Shelter.Home"/>), и вторая его копия однажды
    /// разъехалась бы с панелью — ровно так же, как это заведено у отметки
    /// точки возрождения (Presets.Survive: homeSpawn.Spawn.Home).
    ///
    /// ЖИВАЯ БЕДА. Ручка роли «Дом» обещает человеку «куда прятаться в бурю,
    /// ГДЕ СПАТЬ, куда идти без дела» — а сон про дом не знал вовсе и смотрел
    /// только себе под ноги. Копатель, ушедший в забой за двести блоков, кровать
    /// не находил никогда и молчал об этом всю ночь. Жалоба заказчика 16.08:
    /// «и не ложится спать, не идет домой при бездействии».
    ///
    /// ВТОРАЯ ПОЛОВИНА ТОЙ ЖЕ БЕДЫ, ЖАЛОБА 01.09: «он не учитывает вообще любую
    /// кровать в своем доме, пофиг кто её поставил, главное кровать в его доме».
    /// Одного «поиска вокруг дома» мало и хватить не могло: сервер шлёт куски
    /// мира вокруг ИГРОКА, и из забоя дома не видно ВООБЩЕ. Поэтому дом — не
    /// только второе место поиска, но и МЕСТО, КУДА БОТ ИДЁТ
    /// (<see cref="GoHomeForBed"/>), а там уже ищет.
    /// </summary>
    public Func<BlockPos?>? Home { get; set; }

    /// <summary>
    /// Как далеко бот согласен идти за СЛУЧАЙНОЙ кроватью. Он же — радиус
    /// поиска: искать дальше, чем готов идти, значит находить и отказываться,
    /// а искать ближе — не видеть кровати, до которой дошёл бы.
    ///
    /// СВОЙ ДОМ ЭТОЙ МЕРКОЙ НЕ МЕРЯЕТСЯ, и это правка по жалобе заказчика:
    /// «главное кровать в его доме». Дом — это место, названное ЧЕЛОВЕКОМ, и
    /// ходит к нему бот без потолка везде: в бурю (<see cref="Shelter"/>) и от
    /// безделья (<see cref="BehaviorIdle"/>) дорога домой ничем не ограничена.
    /// Второй мерки для той же дороги заводить нельзя — разойдётся с первыми
    /// двумя. Чем это кончалось: копатель в забое за двести блоков не находил
    /// кровати НИКОГДА (чанки дома ему даже не присылали), а найди он её —
    /// отказался бы идти, потому что 200 > 32.
    /// </summary>
    public double MaxBedDistance { get; set; } = 32;

    /// <summary>
    /// В каком радиусе искать кровать, если она не задана. Ноль и меньше —
    /// брать <see cref="MaxBedDistance"/>: одна человеческая мерка («как далеко
    /// я согласен идти за кроватью») не должна крутиться двумя ручками.
    /// </summary>
    public int SearchRadius { get; set; }

    /// <summary>Сколько отводится на дорогу до кровати (и на один отрезок дороги домой).</summary>
    public double TravelSeconds { get; set; } = 60;

    /// <summary>
    /// ИДТИ ДОМОЙ, КОГДА КРОВАТИ РЯДОМ НЕТ, — и уже там её искать.
    ///
    /// ЖАЛОБА ЗАКАЗЧИКА СЛОВО В СЛОВО: «он не учитывает вообще любую кровать в
    /// своем доме, пофиг кто её поставил, главное кровать в его доме». Поиск
    /// вокруг дома тут не помогал и помочь не мог: чанки дома боту, стоящему в
    /// забое за двести блоков, НЕ ПРИСЫЛАЮТ ВОВСЕ — искать в них нечего.
    /// «Дойти до дома, а там уже искать» — это не то же самое, что «искать в 32
    /// блоках вокруг себя», и делает это ровно эта ручка.
    ///
    /// ПОЧЕМУ ПОЛИТИКА, А НЕ ВСЕГДА. Копателю ночь пути домой — брошенная
    /// штольня, и человек вправе сказать «работай, спи где придётся». Выключил —
    /// бот честно скажет, что домой не идёт и почему (см. <see cref="ГдеИскалКровать"/>).
    /// </summary>
    public bool GoHomeForBed { get; set; } = true;

    /// <summary>Не спать во время бури (игра и сама не даст, но и идти незачем).</summary>
    public bool SkipDuringStorm { get; set; } = true;

    /// <summary>
    /// Кого считать опасным. Механизм не знает, кто враг — это знание роли
    /// (или боевой способности). Не задано — проверка не делается.
    /// </summary>
    public Func<EntityInfo, bool>? IsDangerous { get; set; }

    /// <summary>В каком радиусе опасные существа мешают лечь.</summary>
    public double DangerRadius { get; set; } = 12;

    /// <summary>Сколько после последнего полученного урона считать, что идёт бой.</summary>
    public double CalmSeconds { get; set; } = 15;

    /// <summary>
    /// ЗА ЧЕМ ЕЩЁ ВСТАВАТЬ (и ради чего не ложиться вовсе). Возвращает причину
    /// словами или null — «нужды нет». Не задано — проверка не делается.
    ///
    /// ЗАЧЕМ ЭТО ПОНАДОБИЛОСЬ. У пути «голоден → поел» два замка, и спор за
    /// ТЕЛО (BodyArbiter) — только второй. Первый — спор за ХОД: раннер идёт по
    /// весам и обрывает обход на первой способности, вернувшей true. Сон весит
    /// 90, еда 80, и лежащий бот возвращает true до самого рассвета — значит
    /// тик еды НЕ ЗОВЁТСЯ ВООБЩЕ. А сон в игре гонит время, и сытость за ночь
    /// падает до нуля. Заявление «смерть от голода при наличии еды недопустима
    /// ни при какой настройке» без этой строки кодом не подтверждалось: бот
    /// вставал только когда голод начинал бить уроном (0,13 хп), потому что
    /// урон поднимает Danger().
    ///
    /// Механизм не знает, что такое голод и рана, — это знание способностей,
    /// и подключает их пресет выживания (<see cref="Presets.Survive"/>).
    /// </summary>
    public Func<string?>? WakeFor { get; set; }

    /// <summary>Пауза после неудачной попытки лечь.</summary>
    public double RetrySeconds { get; set; } = 30;

    /// <summary>Лёг спать.</summary>
    public event Action<BedInfo>? OnWentToBed;

    /// <summary>Не смог лечь — с причиной.</summary>
    public event Action<string>? OnCouldNotSleep;

    /// <summary>Подняли раньше времени (рассвет, буря, опасность).</summary>
    public event Action<string>? OnWokeUp;

    /// <summary>
    /// НОЧЬ, А БОТ НЕ СПИТ — И ВОТ ЧЕГО НЕ ХВАТАЕТ. Одна причина, словами.
    ///
    /// ЖИВАЯ БЕДА, РАДИ КОТОРОЙ ЗАВЕДЕНО. Жалоба заказчика 16.08: «не ложится
    /// спать». В журнале за всю ночь про сон НЕ БЫЛО НИ ОДНОЙ СТРОКИ: все шесть
    /// поворотов, на которых способность решала «не сегодня», были молчаливым
    /// <c>return false</c>. Молчание тут — худший из ответов: «кровати рядом
    /// нет» и «сломано» со стороны неотличимы, а лечится первое одной кроватью.
    ///
    /// Говорится только НОЧЬЮ — днём не спать не новость; и один раз на причину
    /// (<see cref="Alarm"/>, та же тревога, которой уже глушили «снарядиться не
    /// выходит» и «ходить за этим мне запрещено»).
    /// </summary>
    public event Action<string>? OnNotSleeping;

    /// <summary>
    /// ПОЧЕМУ Я НЕ СПЛЮ — ОДНА ТРЕВОГА НА ВСЕ ОТКАЗЫ ЭТОЙ ДВЕРИ. Причина у неё
    /// в каждый миг ровно одна: <see cref="TickAsync"/> выходит на первом же
    /// отказе, и второго в том же тике не бывает.
    ///
    /// ЖИВАЯ ЖАЛОБА, РАДИ КОТОРОЙ ЗАВЕДЕНА. «ночь, а спать я не буду: сон
    /// выключен» стояло в журнале сотнями строк подряд. Глушитель тут был и
    /// работал — но НЕ ТОТ. <see cref="LogRepeats"/> считает СОБЫТИЯ: бот
    /// тридцать раз уткнулся в стену, и правильно сказать об этом один раз
    /// числом, а через десять секунд — снова, потому что это уже другой
    /// приступ. А «сон выключен ручкой» — не событие, а СОСТОЯНИЕ: само оно не
    /// пройдёт никогда, переменит его только человек, повернувший ручку.
    /// Окно в пять минут честно делало своё дело и всё равно оставляло по
    /// тридцать четыре одинаковых строки за прогон (bot.log 30.08, каждая с
    /// хвостом «и то же ещё ~715 раз»): пересказывать длящееся раз в пять минут
    /// — то же самое, что каждый тик, только медленнее.
    ///
    /// ДЛЯЩЕЕСЯ В ЭТОМ ПРОЕКТЕ ГЛУШИТ <see cref="Alarm"/>, и второй копии тут
    /// не заведено: та же тревога стоит у «снарядиться не выходит»
    /// (<c>BehaviorEquipSelf</c>) и у слово в слово такого же отказа
    /// «закрыть нечем, но ходить за этим мне запрещено: ручка «…»»
    /// (<c>BehaviorMeetNeed</c>).
    ///
    /// КЛЮЧ — НОВОСТЬ, А НЕ ГОТОВЫЙ ТЕКСТ (<see cref="LogRepeats.NewsOf"/>):
    /// «усталость 6,3» и «усталость 6,4» — одна причина, а не две. Правило
    /// ключа чистое и общее, своей копии здесь нет; наружу при этом уходит
    /// ЖИВАЯ строка с настоящими числами — свернули мы повтор, а не правду.
    /// </summary>
    private readonly Alarm неСплю = new("сон") { RepeatAfter = TimeSpan.MaxValue };

    /// <summary>
    /// ПОЧЕМУ ЛЕЧЬ НЕ ВЫШЛО — вторая дверь и вторая тревога
    /// (<see cref="OnCouldNotSleep"/>). Отдельно от <see cref="неСплю"/>
    /// НАРОЧНО, и это не украшение: эти две двери чередуются по кругу. Сон
    /// говорит «кровати рядом нет — иду домой», уходит, за
    /// <see cref="RetrySeconds"/> не подходит и говорит «домой не подхожу», —
    /// и так всю ночь. Одна тревога на обе считала бы каждый такой круг сменой
    /// причины и печатала бы ОБЕ строки каждые тридцать секунд, то есть ровно
    /// тот шум, от которого лечим.
    /// </summary>
    private readonly Alarm неВышлоЛечь = new("сон") { RepeatAfter = TimeSpan.MaxValue };

    /// <summary>
    /// ЧЕРЕЗ СКОЛЬКО СЕКУНД НАПОМИНАТЬ ПРО ТУ ЖЕ САМУЮ ПРИЧИНУ. Бесконечность
    /// (умолчание) — не напоминать по секундомеру вовсе; ноль — не глушить
    /// вовсе, на разбор живого журнала (как у глушителей движения и разломов).
    ///
    /// БЕСКОНЕЧНОСТЬ ЗДЕСЬ НЕ МОЛЧАНИЕ. Напоминание есть, и оно не секундомер,
    /// а САМА НОЧЬ: рассвело — тревоги снимаются (<see cref="Забыть"/>), и
    /// следующей ночью бот скажет своё «не сплю, потому что…» снова, как в
    /// первый раз. Мерка взята у мира, а не выдумана числом, и на сервере с
    /// длинной ночью не соврёт.
    /// </summary>
    public double QuietSeconds
    {
        get => quietSeconds;
        set
        {
            quietSeconds = value;
            var срок = double.IsFinite(value)
                ? TimeSpan.FromSeconds(Math.Max(0, value))
                : TimeSpan.MaxValue;
            неСплю.RepeatAfter = срок;
            неВышлоЛечь.RepeatAfter = срок;
        }
    }

    private double quietSeconds = double.PositiveInfinity;

    /// <summary>
    /// В каком радиусе искать кровать на самом деле: своё число, если задано,
    /// иначе «как далеко согласен идти» (<see cref="MaxBedDistance"/>).
    /// </summary>
    public int LookAround => SearchRadius > 0
        ? SearchRadius
        : Math.Max(1, (int)Math.Round(MaxBedDistance));

    /// <summary>Сказать причину — но не повторяться (см. <see cref="неСплю"/>).</summary>
    private bool Say(string why)
    {
        if (неСплю.Raise(LogRepeats.NewsOf(why), DateTime.UtcNow))
            OnNotSleeping?.Invoke(why);
        return false;   // «сказал и не сплю» — ход не забираем
    }

    /// <summary>
    /// Сказать, почему лечь не вышло, — и тоже не повторяться
    /// (см. <see cref="неВышлоЛечь"/>).
    ///
    /// ДВЕРЬ ЗАВЕДЕНА ПОТОМУ, ЧТО ЕЁ НЕ БЫЛО ВОВСЕ. Отказы похода
    /// («домой не подхожу», «тело забрало «…»», «не ложусь — проголодался»)
    /// уходили в журнал голым событием, без всякого глушителя, и повторялись
    /// каждые <see cref="RetrySeconds"/> — сорок строк за ночь на каждый.
    /// </summary>
    private void ЛечьНеВышло(string почему)
    {
        if (неВышлоЛечь.Raise(LogRepeats.NewsOf(почему), DateTime.UtcNow))
            OnCouldNotSleep?.Invoke(почему);
    }

    /// <summary>
    /// ОТКАЗ КОНЧИЛСЯ — ЗАБЫТЬ СКАЗАННОЕ, и следующий такой же прозвучит как
    /// первый. Зовётся ровно там, где бот перестал отказываться спать: днём
    /// (и когда календарь молчит) и когда он уже лежит в кровати.
    ///
    /// Это и есть то напоминание, которое <see cref="QuietSeconds"/> не берёт
    /// с секундомера: «ночь, а я не сплю» — новость КАЖДОЙ ночи, а не одна на
    /// всю сессию, и молчащий об этом бот нарушил бы правило «честный отказ,
    /// никогда молчание».
    /// </summary>
    private void Забыть()
    {
        неСплю.Clear();
        неВышлоЛечь.Clear();
    }

    // ---------------- тело: без него сон никуда не идёт ----------------

    /// <summary>
    /// ЧЬИМ ИМЕНЕМ СОН ДЕРЖИТ ТЕЛО. Человек читает это имя в окне управления
    /// («занят: …») и в отказах чужих дел — значит оно обязано называть ДЕЛО,
    /// а не механизм.
    /// </summary>
    public static string BodyOwner(string дело) => $"сон: {дело}";

    /// <summary>
    /// СТУПЕНЬ ВЛАДЕНИЯ ТЕЛОМ У СНА — фоновое дело роли, как прогулка без дела
    /// и отметка дома. Ни приказ человека, ни рефлекс, ни дело жизни она не
    /// задерживает: перебивают её мгновенно и по-прежнему.
    /// </summary>
    public const BodyArbiter.Importance BodyLevel = BodyArbiter.Importance.Routine;

    /// <summary>
    /// ВЗЯТЬ ТЕЛО ПОД ДОРОГУ — ОДНО МЕСТО НА ОБА ПОХОДА СНА (домой за кроватью
    /// и к самой кровати).
    ///
    /// ЗАЧЕМ, ЖИВЬЁМ. Сон был ЕДИНСТВЕННОЙ способностью проекта, которая ходит
    /// и тела не просит вовсе: обращений к распорядителю в этом файле было
    /// НОЛЬ. Пока тело чаще бывало свободным, это сходило с рук; а когда
    /// очередь задач стала держать его ВСЮ ЗАДАЧУ, вышло вот что (стенд
    /// приёмки, ночь, дом за 12 бл):
    ///   «дорога до (…): телом распоряжается уже не тот, кто затеял этот поход
    ///    (было «очередь: карьер у реки», стало ничьё) — ходьбу отдаю»
    ///   «СОН: домой за кроватью не подхожу: за 20 с ближе не стал (12,0 бл)»
    /// Поход гас за 407 миллисекунд, а человеку докладывали про двадцать
    /// секунд дороги. Ходил сон ЧУЖИМ ТЕЛОМ — тем, которое взяла очередь под
    /// свою работу, — и гас вместе с чужим владением.
    ///
    /// ЧТО ДАЁТ ВЛАДЕНИЕ. Дорога идёт по ЕГО токену: отнимут тело — поход
    /// кончится в ту же миллисекунду и у своего источника, а не через сторожа
    /// наряда, и назвать отнявшего можно поимённо (<see cref="BodyArbiter.Hold.EndedBy"/>).
    /// А равный по важности (та же очередь задач) ноги у сна не отнимает вовсе.
    ///
    /// А ВОТ ЛЕЧЬ СПАТЬ СНУ ЭТО ЕЩЁ НЕ ДАЁТ, и вторую половину чинили не здесь.
    /// Сон и очередь задач — оба фоновое дело роли, равный равного не
    /// перебивает; пока очередь держала тело ВСЮ ЗАДАЧУ, бот всю ночь писал
    /// «телом сейчас распоряжается „очередь: …“ — подожду 30 с» и не ложился
    /// ни разу. Теперь очередь берёт тело НА ПОХОД (<c>WorkRunner.Legs</c>):
    /// на время самой работы оно свободно, и сон получает его первым же
    /// вопросом. Ступень сна при этом НЕ ПОДНЯТА и подниматься не должна:
    /// «!иди ко мне» в час ночи бот обязан исполнить сразу, а приказ равного
    /// не перебивает.
    /// </summary>
    private BodyArbiter.Hold? Тело(string дело, CancellationToken ct) =>
        ctx.Turn.TryTake(BodyOwner(дело), BodyLevel, ct);

    /// <summary>
    /// ТЕЛО ЗАНЯТО ДРУГИМ — честный отказ и пауза, чтобы не спрашивать дважды
    /// в секунду. Пауза тут не осторожность: тик идёт два раза в секунду, а
    /// занятое дело держит тело минутами, — без неё распорядитель писал бы в
    /// журнал свой отказ каждые три секунды всю ночь.
    /// </summary>
    private bool ТелоЗанято(string чего)
    {
        nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        return Say($"ночь, {чего}: телом сейчас распоряжается «{ctx.Turn.Busy}» — " +
                   $"подожду {RetrySeconds:0} с и спрошу снова");
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        NoteDamage();

        if (sleeping.IsSleeping)
        {
            // ЛЁГ — ЗНАЧИТ ОТКАЗ КОНЧИЛСЯ. Причина, по которой бот не спал, с
            // этой минуты неправда, и держать её в памяти нельзя: встанет он
            // ради еды, наткнётся на ту же беду — и обязан назвать её снова
            Забыть();

            // Лежим. Решаем, не пора ли встать — раньше остальных способностей
            string? getUp =
                WhyNotCalm() is { } тревога ? тревога
                : VitalNeed() is { } нужда ? нужда
                : SkipDuringStorm && ctx.Storms.Known && ctx.Storms.Active ? "началась буря"
                : !Enabled ? "спать больше не нужно"
                : ctx.Clock.Ready && !ctx.Clock.IsNight ? "рассвело"
                : null;

            if (getUp == null)
                return true; // спим — ход держим за собой, чтобы бота не дёргали

            if (await sleeping.WakeAsync(ct))
                OnWokeUp?.Invoke(getUp);
            return false;    // встали — пусть дальше действуют остальные
        }

        // ДО НОЧИ МОЛЧИМ СОВСЕМ: «не сплю, потому что день» — не новость, а
        // строка каждые полсекунды. Ночью же молчать нельзя ни об одном отказе
        if (!ctx.Clock.Ready || !ctx.Clock.IsNight)
        {
            // РАССВЕЛО — ТРЕВОГИ СНИМАЕМ. Ровно этим ночь и становится тем
            // напоминанием, которого не даёт секундомер: сказанное вчера
            // сегодня скажется снова (см. Забыть)
            Забыть();
            return false;
        }

        // Пауза после неудачной попытки — единственный отказ, о котором тут не
        // говорят: причина той попытки уже сказана, и повторять её нечем
        if (DateTime.UtcNow < nextTry)
            return false;

        if (!Enabled)
            return Say("ночь, а спать я не буду: сон выключен (ручка роли «СпатьНочью»)");
        if (ctx.Movement.IsBusy)
            return Say("ночь, а я в дороге — лягу, как дойду");
        if (SkipDuringStorm && ctx.Storms.Known && ctx.Storms.ShelterNow)
            return Say("ночь, а идёт временная буря: игра в постель не пустит " +
                       "(в настройках мира temporalStormSleeping = 0 по умолчанию)");
        // ПРИЧИНА НАЗЫВАЕТСЯ ОДНА И НАСТОЯЩАЯ. Здесь стояло «рядом опасно (или
        // только что били)» — две разные беды, слепленные словом «или», и
        // человек по такой строке не мог узнать НИ ОДНОЙ из них. В журнале
        // заказчика (19.08, 23:57:39) она вышла с хвостом «и то же ещё 390 раз»,
        // и ответить по ней на вопрос «а рядом-то кто?» было нечем
        if (WhyNotCalm() is { } нетишины)
            return Say($"ночь, а {нетишины} — не лягу, пока не станет тихо {CalmSeconds:0} с");
        // Усталость меньше игрового порога — сервер всё равно откажет,
        // и идти через полкарты незачем. Число 8 зашито в самой игре
        // (BlockBed.OnBlockInteractStart), а не выбрано нами
        if (sleeping.TirednessKnown && sleeping.Tiredness <= Sleeping.MinTiredness)
            // Усталость тикает всю ночь — значит она МЕРКА, и печатается дробью
            // (см. Мерка). Порог 8 при ней стоит целым нарочно: он не тикает
            return Say($"ночь, а спать ещё не хочу: усталость {Мерка(sleeping.Tiredness)}, " +
                       $"а игра пускает в постель только выше {Sleeping.MinTiredness:0.#}");

        // И НЕ ЛОЖИТЬСЯ, ПОКА НУЖДА СТОИТ. Одного подъёма мало: сон весит
        // больше еды, тикает первым и на следующем же тике лёг бы обратно —
        // еда так и не получила бы ни одного тика, а со стороны это выглядело
        // бы как «встал и тут же уснул опять». Говорим один раз на причину:
        // тик идёт дважды в секунду, и в шуме настоящей беды не увидеть.
        //
        // ЗДЕСЬ СТОЯЛА СВОЯ ПАМЯТЬ «о чём уже сказали» — одно поле на одну
        // строку. Делала она ровно то же, что общая тревога, и была третьей
        // копией правила «сказать один раз, а переменившееся — снова»; теперь
        // и эта причина идёт общей дверью, а поля больше нет
        if (VitalNeed() is { } сперваДругое)
        {
            ЛечьНеВышло($"не ложусь — {сперваДругое}");
            return false;
        }

        // КРОВАТЬ — ПОД НОГАМИ ИЛИ ДОМА. Второе место появилось потому, что
        // ручка «Дом» обещает человеку «где спать», а сон про дом не знал
        // (см. Home): копатель в забое кровати не находил никогда
        var home = Home?.Invoke();
        var bed = Bed is { } known ? sleeping.Describe(known) : null;
        // КРОВАТЬ НАШЛАСЬ ПО ДОМУ, А НЕ ПОД НОГАМИ. Разница не украшение: до
        // домашней бот идёт без потолка (это его дом), а до случайной — только
        // в пределах «ЗаКроватьюНеДальше»
        bool домашняя = false;
        if (bed == null && DateTime.UtcNow >= nextLook)
        {
            bed = sleeping.FindBedNear(radius: LookAround);
            if (bed == null && home is { } рядомДом)
            {
                bed = sleeping.FindBedNear(рядомДом, LookAround);
                домашняя = bed != null;
            }
            // ПУСТОЙ ОБХОД ОТДЫХАЕТ, И ЭТО ЗАМЕРЕННОЕ ЧИСЛО, А НЕ ОСТОРОЖНОСТЬ.
            // Куб радиусом 32 — это 65×65×65 ≈ 275 тысяч клеток; живой бот
            // 02.09 обошёл 105 тысяч за 0,041 с, то есть 2,6 млн клеток в
            // секунду, — значит один такой обход стоит около 0,1 с. Тик идёт
            // ДВАЖДЫ В СЕКУНДУ, и до этой строки ночь без кровати съедала бы
            // пятую часть всего хода бота на пересчёт одного и того же.
            //
            // Дорога домой этой паузы НЕ ЖДЁТ: она идёт своими отрезками ниже,
            // а обход ей не нужен — «кровати рядом нет» уже выяснено.
            if (bed == null)
                nextLook = DateTime.UtcNow.AddSeconds(RetrySeconds);
        }

        if (bed == null)
        {
            // ДОЙТИ ДО ДОМА И ИСКАТЬ ТАМ. Поиск вокруг дома выше уже был и
            // ничего не дал — а это чаще всего значит не «кровати нет», а
            // «чанков дома мне не присылали»: сервер шлёт куски мира вокруг
            // ИГРОКА, и из забоя за двести блоков дом боту не виден вовсе
            if (ДорогаДомойЗаКроватью(home) is { } кудаИдти)
                return await ПойтиДомойЗаКроватьюAsync(кудаИдти, ct);
            return Say(ГдеИскалКровать(home, ПочемуНеИдуДомой(home)));
        }

        if (ctx.Self.Position is { } p)
        {
            double dx = bed.Pos.X + 0.5 - p.X, dy = bed.Pos.Y - p.Y, dz = bed.Pos.Z + 0.5 - p.Z;
            double away = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (ЗаПотолком(домашняя, away, MaxBedDistance))
                return Say($"ночь, кровать вижу ({bed.Pos}), но до неё {away:0} бл — дальше " +
                           $"{MaxBedDistance:0} я за случайной кроватью не хожу " +
                           "(ручка роли «ЗаКроватьюНеДальше»)");
        }

        // И К НАЙДЕННОЙ КРОВАТИ — НА СВОИХ НОГАХ. Дорога к ней ничем не
        // отличается от дороги домой: те же минуты ходьбы, тот же первый
        // желающий, отнимающий тело на полпути (разбор — у Тело)
        using var ход = Тело($"иду к кровати {bed.Pos}", ct);
        if (ход == null)
            return ТелоЗанято($"к кровати {bed.Pos} не иду");

        nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        string? failure = null;
        void Catch(string why) => failure = why;
        sleeping.OnCouldNotSleep += Catch;
        bool slept;
        try
        {
            slept = await sleeping.GoToBedAsync(bed, СрокДороги(bed.Pos), ход.Token);
        }
        finally
        {
            sleeping.OnCouldNotSleep -= Catch;
        }

        if (slept)
            OnWentToBed?.Invoke(bed);
        else
            // ПРИЧИНА НАЗЫВАЕТСЯ НАСТОЯЩАЯ. Отняли тело — «до кровати не
            // дотянуться» тут ложь: не дотянулись мы потому, что нас увели
            ЛечьНеВышло(ход.Interrupted
                ? $"шёл к кровати {bed.Pos}, а тело забрало «{ход.EndedBy}» — не лёг"
                : failure ?? "не удалось лечь");
        return true; // дорога до кровати — тоже работа этой способности
    }

    /// <summary>
    /// ЧЕГО НЕ ХВАТАЕТ, ЧТОБЫ ЛЕЧЬ, — когда кровати не нашлось. Строка обязана
    /// называть ВСЁ, что человек может поправить: где смотрели, задан ли дом и
    /// что кроватью в игре считается блок с sleepEfficiency, а не точка дома.
    ///
    /// ПРЯМОЙ ВОПРОС ЗАКАЗЧИКА 16.08: «Так я же в вебформе отметил дом, разве
    /// это недостаточно??» Недостаточно, и раньше об этом не говорилось нигде:
    /// дом — это КУДА идти, а лечь можно только в кровать, и её должен кто-то
    /// поставить. Ответ на такой вопрос обязан стоять в журнале, а не в голове
    /// того, кто писал способность.
    /// </summary>
    private string ГдеИскалКровать(BlockPos? home, string? почемуНеПошёл = null)
    {
        string где = $"смотрел вокруг себя в {LookAround} бл (и вверх, и вниз)";
        где += home is { } дом
            ? $" и вокруг дома {дом}"
            : "; дома у меня нет — задайте точку в окне управления (ручка «Дом») или скажите " +
              "в чат «!дом тут», стоя на месте";
        if (почемуНеПошёл is { Length: > 0 } причина)
            где += $"; домой за кроватью не иду: {причина}";
        return $"ночь, спать пора, а кровати нет: {где}. Дом — это куда идти, а лечь можно " +
               "только в КРОВАТЬ: поставьте её дома (в игре это блок с sleepEfficiency — " +
               "любая кровать из соломы, шкур или ткани)";
    }

    /// <summary>
    /// ПОТОЛОК «ЗаКроватьюНеДальше» — ТОЛЬКО СЛУЧАЙНОЙ КРОВАТИ. Чистое правило:
    /// оно и решает, и объясняет отказ, и проверяется числами в обе стороны.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНАЯ СТРОКА, А НЕ УСЛОВИЕ НА МЕСТЕ. Здесь стоял просто
    /// <c>away &gt; MaxBedDistance</c> — и жалоба заказчика упиралась ровно в
    /// него: «главное кровать в его доме». Кровать в СВОЁМ доме этой меркой не
    /// меряется вовсе (дом человек назвал сам, и ходит к нему бот без потолка —
    /// см. <see cref="MaxBedDistance"/>), а чужая койка за полкарты — меряется.
    /// Разница в одном слове, и её обязан сторожить свой стенд.
    /// </summary>
    /// <param name="домашняя">кровать нашлась вокруг дома, а не вокруг бота</param>
    public static bool ЗаПотолком(bool домашняя, double доКровати, double потолок) =>
        !домашняя && доКровати > потолок;

    // ---------------- дорога домой за кроватью ----------------

    /// <summary>
    /// СРОК НА ДОРОГУ — СЕКУНДА НА БЛОК, но не меньше <see cref="TravelSeconds"/>.
    ///
    /// ЗАЧЕМ НЕ ПРОСТО <see cref="TravelSeconds"/>. Роль ставит его щедро, но по
    /// мерке «ЗаКроватьюНеДальше» (LivingRole: <c>Math.Max(30, MaxBedDistance)</c>),
    /// то есть под СЛУЧАЙНУЮ кровать в тридцати двух блоках. Дом же этой меркой
    /// не меряется вовсе, и выдать на двести блоков тридцать секунд значило бы
    /// обрывать дорогу на полпути и честно, но впустую докладывать «не дошёл».
    /// Секунда на блок — тот же щедрый запас, каким его считает роль: бот
    /// проходит несколько блоков в секунду.
    /// </summary>
    private double СрокДороги(BlockPos куда) =>
        Ноги() is { } ноги
            ? Math.Max(TravelSeconds, Depot.Apart(ноги, куда))
            : TravelSeconds;

    /// <summary>Своя клетка (пол под ногами) или null — «где я, не знаю».</summary>
    private BlockPos? Ноги() =>
        ctx.Self.Position is { } p
            ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
            : null;

    /// <summary>
    /// НАДО ЛИ ИДТИ ДОМОЙ ЗА КРОВАТЬЮ — и куда именно. null — не надо (или нечем).
    ///
    /// Мерка тут одна и та же, что у поиска: пока дом ближе <see cref="LookAround"/>,
    /// он УЖЕ внутри той коробки, которую бот только что обошёл вокруг себя, —
    /// значит идти некуда, кровати там и правда нет, и надо сказать об этом, а
    /// не топтаться. Дальше <see cref="LookAround"/> — дом за краем взгляда,
    /// и туда стоит дойти.
    ///
    /// ЗАСТАВА «МИРА ВОКРУГ НЕТ» НЕ ПРИДИРКА. Пока куски мира под ногами не
    /// пришли, дороги не существует: путь считается по загруженным чанкам, и
    /// выход в неё означал бы минуту топтания на месте вместо честного ответа.
    /// </summary>
    private BlockPos? ДорогаДомойЗаКроватью(BlockPos? home) =>
        GoHomeForBed && home is { } дом && Ноги() is { } ноги &&
        ctx.World.IsKnown(ноги) && Depot.Apart(ноги, дом) > LookAround
            ? дом
            : null;

    /// <summary>
    /// ПОЧЕМУ ДОМОЙ НЕ ИДУ — словами и только когда это к делу. Пусто значит
    /// «дом рядом (или его нет вовсе), и дорога тут ни при чём»: приписывать к
    /// отказу лишнюю половину фразы — тот самый шум, который перестают читать.
    /// </summary>
    private string? ПочемуНеИдуДомой(BlockPos? home)
    {
        if (home is not { } дом)
            return null;                      // про отсутствие дома скажет сама строка
        if (Ноги() is not { } ноги)
            return "где я стою — не знаю, сервер ещё не прислал моё тело";
        if (Depot.Apart(ноги, дом) <= LookAround)
            return null;                      // дом и так в осмотренной округе
        if (!GoHomeForBed)
            return "так настроено (ручка роли «ДомойЗаКроватью» выключена)";
        return "куски мира вокруг меня ещё не пришли — вслепую в дорогу не выйду";
    }

    /// <summary>
    /// ОДИН ОТРЕЗОК ДОРОГИ ДОМОЙ ЗА КРОВАТЬЮ. Ход держим за собой: дорога до
    /// кровати — такая же работа этой способности, как и сам сон (так же
    /// устроен и поход к найденной кровати, <see cref="Sleeping.GoToBedAsync"/>).
    ///
    /// ОТКАЗ ТОЛЬКО ПО ФАКТУ, ЧТО НЕ СДВИНУЛИСЬ. Дорога в двести блоков не
    /// укладывается в один срок, и считать её неудачей по возврату <c>false</c>
    /// значило бы бросать её у самого порога. Меряем расстояние ДО и ПОСЛЕ:
    /// стало ближе — идём дальше следующим тиком, не стало — ждём
    /// <see cref="RetrySeconds"/> и говорим вслух (иначе бот всю ночь молча
    /// долбился бы в непроходимую стену).
    /// </summary>
    private async Task<bool> ПойтиДомойЗаКроватьюAsync(BlockPos дом, CancellationToken ct)
    {
        // Сюда попадают только с известной точкой (см. ДорогаДомойЗаКроватью),
        // так что «было» тут всегда настоящее число
        double было = Ноги() is { } старт ? Depot.Apart(старт, дом) : 0;

        // ТЕЛО — ДО ПЕРВОГО ШАГА И ДО ПЕРВОГО СЛОВА. Не взяв его, сон шёл бы
        // чужими ногами и гас от первой же смены хозяина, успев наобещать
        // человеку дорогу (разбор — у Тело)
        using var ход = Тело("домой за кроватью", ct);
        if (ход == null)
            return ТелоЗанято($"домой {дом} за кроватью не иду");

        // Дверь тут та же, что у всех прочих «почему я не сплю»: своей второй
        // заглушки у дороги домой нет и быть не должно
        Say($"ночь, кровати рядом нет — иду домой {дом} ({Мерка(было)} бл): " +
            "кровать в моём доме считается моей, чья бы она ни была");

        var вышел = DateTime.UtcNow;
        bool дошёл = await ctx.Movement.TravelToAsync(дом, TravelSeconds, ход.Token);
        if (дошёл)
        {
            // ПРИШЁЛ — СМОТРЕТЬ СЕЙЧАС, а не через полминуты. Отдых пустого
            // обхода (nextLook) заведён против пересчёта ОДНОГО И ТОГО ЖЕ, а
            // здесь вокруг бота уже другой мир — тот, ради которого он и шёл
            nextLook = DateTime.MinValue;
            return true;
        }

        // ТЕЛО ОТНЯЛИ ПОСРЕДИ ДОРОГИ — И ЭТО НЕ «НЕ СДВИНУЛСЯ». Дорогу оборвал
        // не рельеф и не срок, а тот, кто важнее, и назвать его обязаны
        // поимённо: ровно этой лжи стоил живой случай («за 20 с ближе не стал»
        // про поход, который погас за 407 мс). А пауза тут — вторая половина
        // той же беды: без неё способность затевала бы новую дорогу тем же
        // тиком, то есть дважды в секунду, и каждый раз с полным пересчётом пути
        if (ход.Interrupted)
        {
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            ЛечьНеВышло($"шёл домой {дом} за кроватью, а тело забрало " +
                        $"«{ход.EndedBy}» — дорогу прерываю, подожду " +
                        $"{RetrySeconds:0} с и пойду снова");
            return true;
        }

        // ТЕЛА НЕ ВИДНО — ЭТО НЕ «НЕ СДВИНУЛСЯ». Бота могли убить по дороге, и
        // назвать это «ближе не стал» значило бы соврать про причину; своё
        // молчание тут тоже не годится, поэтому говорим как есть
        if (Ноги() is not { } теперь)
        {
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            ЛечьНеВышло($"шёл домой {дом} за кроватью, а где я теперь — не знаю: " +
                        "сервер перестал присылать моё тело");
            return true;
        }
        double стало = Depot.Apart(теперь, дом);
        if (стало >= было - 1)
        {
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            // СЕКУНДЫ — НАСТОЯЩИЕ, А НЕ ОТПУЩЕННЫЕ. Печатать здесь TravelSeconds
            // значило бы врать всякий раз, когда поход кончился раньше срока:
            // человек читал «за 20 с ближе не стал» про 407 миллисекунд
            ЛечьНеВышло($"домой {дом} за кроватью не подхожу: за " +
                        $"{Мерка((DateTime.UtcNow - вышел).TotalSeconds)} с " +
                        $"ближе не стал ({Мерка(стало)} бл) — подожду {RetrySeconds:0} с " +
                        "и попробую снова");
        }
        return true;
    }

    /// <summary>
    /// Жизненная нужда, ради которой встают и не ложатся. Пусто — нужды нет.
    /// Спрашивается в ДВУХ местах (подъём и «не ложиться»), и правило одно.
    /// </summary>
    private string? VitalNeed()
    {
        string? нужда = WakeFor?.Invoke();
        return нужда is { Length: > 0 } ? нужда : null;
    }

    /// <summary>
    /// «МЕНЯ БЬЮТ» — ЭТО ФАКТ ОТ СЕРВЕРА, А НЕ «ЗДОРОВЬЯ СТАЛО МЕНЬШЕ».
    ///
    /// ЖИВОЙ СЛУЧАЙ, ЗА КОТОРЫЙ ЗАПЛАЧЕНО ЦЕЛОЙ НОЧЬЮ (bot.log 19.08, мороз
    /// −4 °C, с 00:17:21 и до конца журнала):
    ///     [чат:-5] Потеряно 0,2 хп от frost
    ///     это не нападение: 0,2 хп от своего тела (frost) — отбиваться не от кого
    ///     [чат:-5] потерял 1,2 хп за 60 с (6 раз, frost)
    /// Мороз снимает по капле ШЕСТЬ РАЗ В МИНУТУ, то есть чаще, чем истекают
    /// <see cref="CalmSeconds"/> = 15 с. Сон сравнивал здоровье с прежним и
    /// всякую убыль читал как удар — значит «только что били» не истекало у
    /// него НИКОГДА, и лечь он не мог до самого рассвета. Бот замерзал ровно
    /// потому, что не ложился в кровать, которая бы его и спасла.
    ///
    /// Бой эту разницу знал и говорил вслух той самой строкой выше
    /// (<see cref="DamageHit.ByLiving"/>). Своей копии догадки здесь больше
    /// нет: вопрос один, и ответ на него один.
    /// </summary>
    private void NoteDamage()
    {
        if (ctx.Damage is { Ready: true } log)
        {
            foreach (var hit in log.Since(seenBlows))
                if (hit.ByLiving)
                {
                    lastBlow = hit;
                    calmAfter = DateTime.UtcNow.AddSeconds(CalmSeconds);
                }
            seenBlows = log.Count;
            return;
        }

        // ОБРАЗЦОВ ЖУРНАЛА УРОНА НЕТ (не нашлись языковые файлы игры) — живём
        // прежней догадкой, но говорим об этом: иначе «не лягу, потому что
        // били» опять станет неотличимо от «не лягу, потому что мёрзну»
        if (ctx.Self.Health is not { } hp)
            return;
        if (lastHealth >= 0 && hp < lastHealth)
        {
            lastBlow = null;
            calmAfter = DateTime.UtcNow.AddSeconds(CalmSeconds);
        }
        lastHealth = hp;
    }

    /// <summary>Сколько ударов журнала урона мы уже разобрали.</summary>
    private long seenBlows;

    /// <summary>Последний удар ЖИВОГО противника — им и называем причину.</summary>
    private DamageHit? lastBlow;

    /// <summary>
    /// ПОЧЕМУ НЕ ТИХО — ОДНОЙ ЖИВОЙ ПРИЧИНОЙ, или null «тихо».
    ///
    /// Вопрос задаётся из двух мест — «пора вставать» и «не лягу», — и ответ у
    /// них обязан быть один и тот же: разойдись они, бот вставал бы по одной
    /// мерке, а ложиться отказывался по другой.
    /// </summary>
    private string? WhyNotCalm()
    {
        if (DateTime.UtcNow < calmAfter)
        {
            // «0,0», А НЕ «0»: мерка обязана и ВЫГЛЯДЕТЬ меркой — см. Мерка
            double назад = CalmSeconds - (calmAfter - DateTime.UtcNow).TotalSeconds;
            return lastBlow is { } удар
                ? $"меня били {Мерка(назад)} с назад ({удар})"
                : $"здоровья убавилось {Мерка(назад)} с назад, а причину сервер не назвал " +
                  "(языковых файлов игры не нашлось — см. журнал урона)";
        }
        if (IsDangerous is not { } dangerous || ctx.Self.Position is not { } p)
            return null;
        // ЖИВОСТЬ СПРАШИВАЕТ САМА МЕРКА ОПАСНОСТИ, а не эта строка.
        //
        // Здесь стояла своя копия догадки «нет здоровья — значит жив»
        // ((e.Health ?? 1) > 0), и живой случай 17.08 она стоила ночи сна:
        // «[сон] ночь, а рядом опасно… (и то же ещё 637 раз)» — рядом лежала
        // воткнутая в стену стрела боуторна, у которой здоровья нет вовсе.
        // Теперь и бой, и лечение, и сон спрашивают одно правило
        // (BehaviorAttackHostiles.IsHostile → CombatTargets.CanBeFought)
        // И НАЗЫВАЕМ, КТО ИМЕННО И В СКОЛЬКИХ БЛОКАХ. Без имени и числа человек
        // не отличит «в двенадцати блоках бродит дрифтер» (это надолго, и лечит
        // это стена или другая кровать) от «кто-то стоит вплотную»
        var кто = ctx.Entities.Nearby(p.X, p.Y, p.Z, DangerRadius)
            .Where(e => e.Id != ctx.Entities.OwnEntityId && dangerous(e))
            .OrderBy(e => Math.Sqrt(Sq(e.X - p.X) + Sq(e.Y - p.Y) + Sq(e.Z - p.Z)))
            .FirstOrDefault();
        if (кто == null)
            return null;
        double далеко = Math.Sqrt(Sq(кто.X - p.X) + Sq(кто.Y - p.Y) + Sq(кто.Z - p.Z));
        return $"рядом опасно: {кто.Code} в {Мерка(далеко)} бл " +
               $"(смотрю на {Мерка(DangerRadius)} бл вокруг)";

        static double Sq(double v) => v * v;
    }

    /// <summary>
    /// ЧИСЛО, КОТОРОЕ ТИКАЕТ, ПЕЧАТАЕТСЯ ДРОБЬЮ ВСЕГДА — иначе глушитель
    /// повторов принимает его за ЛИЧНОСТЬ и молчать перестаёт.
    ///
    /// ЖИВОЙ СЛУЧАЙ 22.08, 17:17:41–17:17:46 — пять строк за пять секунд про
    /// одного и того же дрифтера:
    ///   [сон] ночь, а рядом опасно: drifter-normal в 11,1 бл … не лягу
    ///   [сон] ночь, а рядом опасно: drifter-normal в 9 бл … не лягу
    ///   [сон] ночь, а рядом опасно: drifter-normal в 7 бл … не лягу
    ///   [сон] ночь, а рядом опасно: drifter-normal в 5 бл … не лягу
    ///   [сон] ночь, а рядом опасно: drifter-normal в 3 бл … не лягу
    /// и следом такие же пять про «меня били 0 с назад… 3,6… 5… 7… 15».
    /// Заглушка (<see cref="LogRepeats"/>) стояла и работала: новость она
    /// считает по <see cref="LogRepeats.NewsOf"/>, где ДРОБНОЕ число — мерка,
    /// а ЦЕЛОЕ — личность (клетка мира, номер ряда). Сломан был не глушитель, а
    /// эта строка: формат «0.#» роняет дробную часть у ровных чисел, и «9,0 бл»
    /// печаталось как «9 бл» — то есть мерка выходила наружу под видом
    /// личности, и каждая новая была глушителю НОВОЙ.
    ///
    /// Второй копии правила тут нет и быть не должно: правило одно и живёт у
    /// <see cref="LogRepeats.NewsOf"/>. Здесь — только обязанность говорящего
    /// печатать мерку так, как правило её и опознаёт.
    /// </summary>
    private static string Мерка(double value) => value.ToString("0.0");
}
