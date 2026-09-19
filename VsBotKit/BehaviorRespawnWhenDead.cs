namespace VsBotKit;

/// <summary>
/// Возрождение: пока бот мёртв, он не делает НИЧЕГО, кроме попыток встать.
///
/// Зачем отдельная способность, если смерть и так ловится пакетом. Пакет
/// можно не получить: он приходит один раз, и если бот в этот момент
/// переподключался или ещё не знал своего ClientId, смерть проходит мимо.
/// Дальше начинается самое неприятное: бот считает себя живым, ходит,
/// планирует, копает — а сервер молча отказывает во всём, потому что
/// мёртвым нельзя ни ломать, ни ставить (<c>noprivilege-buildbreak-dead</c>).
///
/// Живьём это выглядело как «карьер сломал 0 из 70», «вскопал слой и подвис»,
/// «не поднимается столбом» — три разных жалобы с одной причиной.
///
/// Поэтому здесь смерть определяется ПО ФАКТУ (здоровье на нуле), а не по
/// извещению, и возрождение повторяется, пока не подействует.
/// </summary>
public class BehaviorRespawnWhenDead : BotBehavior
{
    private readonly BotContext ctx;
    private DateTime nextTry = DateTime.MinValue;
    private DateTime deadSince = DateTime.MinValue;
    private bool saidNoLives;

    public BehaviorRespawnWhenDead(BotContext ctx) => this.ctx = ctx;

    /// <summary>Сколько ждать между попытками встать, секунд.</summary>
    public double RetrySeconds { get; set; } = 4;

    /// <summary>
    /// Сколько здоровье должно держаться на нуле, чтобы счесть бота мёртвым.
    /// Мгновенные просадки бывают при рассинхроне, поэтому не дёргаемся сразу.
    /// </summary>
    public double ConfirmSeconds { get; set; } = 1.5;

    /// <summary>Бот мёртв и пытается возродиться (сколько секунд уже лежит).</summary>
    public event Action<double>? OnDead;

    /// <summary>Бот снова на ногах.</summary>
    public event Action? OnAlive;

    /// <summary>
    /// Возрождений больше нет — вставать бот не будет, и это не молчание, а
    /// решение сервера. Сообщается один раз на смерть, а не каждые четыре
    /// секунды.
    /// </summary>
    public event Action<string>? OnNoLivesLeft;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // СМОТРЕТЬ ЗА СОБОЙ НАДО И МЁРТВЫМ. Место смерти и причину смерти
        // ведёт DeathRecovery, а звал его до сих пор только «поход за вещами»
        // — способность, которую роль вправе не подключать вовсе, и которая
        // всё равно не тикает, пока эта держит ход за собой. То есть причина
        // смерти не дописывалась бы никогда. Двойной вызов безвреден:
        // Observe() пересчитывает содержимое сумок только когда оно менялось
        ctx.Deaths?.Observe();

        if (ctx.Self.Health is not { } hp || ctx.Self.MaxHealth is not { } max || max <= 0)
            return false;

        if (hp > 0)
        {
            if (deadSince != DateTime.MinValue)
            {
                deadSince = DateTime.MinValue;
                saidNoLives = false;
                OnAlive?.Invoke();
            }
            return false;
        }

        if (deadSince == DateTime.MinValue)
            deadSince = DateTime.UtcNow;
        double lying = (DateTime.UtcNow - deadSince).TotalSeconds;
        if (lying < ConfirmSeconds)
            return true;   // ход всё равно наш: живым бот сейчас не является

        // СМЕРТЬ, УВИДЕННАЯ ПО ЗДОРОВЬЮ, — ТОЖЕ СМЕРТЬ. Извещение сервера
        // (пакет 45) приходит РАЗ, и переподключавшийся бот его теряет: тогда
        // журнал смертей и место могилы оставались пустыми, а человек читал
        // «бот жив» про лежащего бота. Запись сама разберётся, что смерть
        // одна: второй раз она её не запишет
        ctx.Deaths?.NoticeDeath("здоровье на нуле");

        // ПРЕРВАТЬ ТО, ЧЕМ БОТ ЗАНЯТ, ПОКА ОН МЁРТВ.
        //
        // Мёртвому сервер отказывает во всём, но начатая при жизни команда об
        // этом не знает: она продолжает ходить, копать и ждать подтверждений,
        // которых уже не будет, и ДЕРЖИТ ТЕЛО за собой. В окне управления это
        // выглядело как «занят: команда добудь» навсегда, а в игре — как
        // залипший после смерти бот. Возрождение важнее любого дела.
        //
        // Смотрим КАЖДЫЙ тик, а не один раз при смерти: пока бот лежит, роль
        // успевает начать новое дело по своему расписанию — и тело опять
        // оказывается занято тем, кому в этом мире уже во всём отказано.
        // Молчаливым это не будет: очередь на тело говорит, кого прервали
        //
        // Смерть обрывает и ВОЗВРАТ К ПРЕРВАННОМУ, а не только владение телом.
        // Между заходами работа тела не держит вовсе (ждёт, пока рефлекс
        // доест), и в этот самый миг Turn.Busy пуст — одной прежней проверки
        // мало: встав, бот полез бы доделывать тот карьер, в котором его и
        // убили. StopAll сам зовёт Turn.Interrupt, второй раз звать не надо
        //
        // И ТРЕТЬЕ УСЛОВИЕ — НОГИ, а это ЖИВАЯ ЖАЛОБА 01.09. Дословно из журнала:
        //   23:05:33 погиб, возрождаюсь
        //   23:05:36 [позиция] полные данные: (-84,5, 115, 390) -> (-30,5, 116, 5,5)
        //   23:05:48 дорога: 53 точек до (-160, 108, 132), всего до цели 7618 бл
        // Сервер перебросил мёртвого на 385 блоков, а поход, задуманный ДО
        // смерти, переброс пережил — и повёл бота в другой конец мира за семь с
        // половиной тысяч блоков. Поход ходит и БЕЗ владения телом (живьём это
        // «!иди»: тело берёт приказом, а дорогу зовёт без токена этого
        // владения), и обеих прежних проверок на такой случай мало: Turn.Busy
        // пуст, возврату ждать нечего — а ноги несут. Спрашиваем прямо, идёт ли
        // бот: имя живого похода видно снаружи (Movement.WalkOwner)
        if (DeathHasSomethingToStop(ctx.Turn.Busy, ctx.Resume.Waiting.Count,
                ctx.Movement.WalkOwner))
            ctx.Resume.StopAll("бот мёртв");

        // ВОЗРОЖДЕНИЙ МОЖЕТ БОЛЬШЕ НЕ БЫТЬ.
        //
        // В мире с настройкой playerlives сервер считает смерти и на исходе
        // жизней отвечает на кнопку возрождения одним сообщением в чат
        // («Cannot revive! All lives used up.» — HandleSpecialKey), а больше
        // не делает НИЧЕГО. Бот при этом слал бы запрос каждые четыре секунды
        // до конца ночи и писал «встаю» — то есть врал бы человеку в лицо.
        // Сколько осталось, говорит сам сервер (пакет 45), гадать не о чем
        if (ctx.Deaths is { NoLivesLeft: true } deaths)
        {
            if (!saidNoLives)
            {
                saidNoLives = true;
                OnNoLivesLeft?.Invoke(
                    "возрождений не осталось — сервер меня не поднимет. " + deaths.Report());
            }
            return true;   // мёртвый занимает ход целиком, встать он не может
        }

        if (DateTime.UtcNow >= nextTry)
        {
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            OnDead?.Invoke(lying);
            await ctx.Self.RespawnAsync();
        }
        // Мёртвый занимает ход целиком: пусть остальные способности не тратят
        // силы на мир, в котором им всё равно откажут
        return true;
    }

    /// <summary>
    /// ЕСТЬ ЛИ У МЁРТВОГО ЧТО ГАСИТЬ — чистое правило, без тела и без мира.
    ///
    /// ТРИ ПРИЗНАКА, И КАЖДЫЙ ЗАВЕДЁН ЖИВЫМ СЛУЧАЕМ, А НЕ ДЛЯ ПОЛНОТЫ:
    ///   • ТЕЛО ЗАНЯТО — начатая при жизни команда крутится вхолостую и держит
    ///     тело за собой («занят: команда добудь» навсегда);
    ///   • ЖДЁТ ВОЗВРАТ — между заходами работа тела не держит вовсе, и, встав,
    ///     бот полез бы доделывать тот карьер, в котором его и убили;
    ///   • НОГИ ИДУТ — поход ходит и БЕЗ владения телом (живьём это «!иди»:
    ///     тело он берёт приказом, а дорогу зовёт без токена этого владения).
    ///     Без третьего признака и вышло 01.09: сервер перебросил мёртвого на
    ///     385 блоков, а поход, задуманный до смерти, переброс пережил — и
    ///     повёл бота за семь с половиной тысяч блоков в другой конец мира.
    ///
    /// Ни одного признака — гасить нечего, и звать <c>Resume.StopAll</c> дважды
    /// в секунду ради пустоты не надо.
    /// </summary>
    /// <param name="busy">Чем занято тело (пусто — ничем).</param>
    /// <param name="waiting">Сколько работ ждут возврата.</param>
    /// <param name="walking">Имя живого похода (пусто — бот стоит).</param>
    public static bool DeathHasSomethingToStop(string busy, int waiting, string walking) =>
        busy.Length > 0 || waiting > 0 || walking.Length > 0;
}

// ======================================================================
//  ДОМ КАК ТОЧКА ВОЗВРАЩЕНИЯ
// ======================================================================

/// <summary>Что бот знает про свою точку возрождения прямо сейчас.</summary>
public enum HomeSpawnState
{
    /// <summary>Дом не задан — отмечать нечего.</summary>
    NoHome,

    /// <summary>Мир запрещает свою точку возрождения (temporalGearRespawnUses = 0).</summary>
    Forbidden,

    /// <summary>Точка возрождения уже дома — делать нечего.</summary>
    AtHome,

    /// <summary>Отметить надо, но нечем: временной шестерёнки нет.</summary>
    NoGear,

    /// <summary>Надо идти домой и ставить точку.</summary>
    NeedsMarking,

    /// <summary>До дома не дошли.</summary>
    NotReached,

    /// <summary>Сервер новую точку не подтвердил.</summary>
    Refused,

    /// <summary>Точка поставлена и подтверждена сервером.</summary>
    Marked
}

/// <summary>Итог попытки отметить дом — с причиной, а не «не вышло».</summary>
public sealed record HomeSpawnResult(HomeSpawnState State, string Message)
{
    /// <summary>Точка возрождения дома (поставили сейчас или уже стояла).</summary>
    public bool Success => State is HomeSpawnState.Marked or HomeSpawnState.AtHome;

    public override string ToString() => Message;
}

/// <summary>
/// ДОМ КАК ТОЧКА ВОЗРОЖДЕНИЯ — механизм.
///
/// Живой случай, ради которого это написано: бот погиб, возродился за 412
/// блоков от вещей, честно решил «до вещей 412 бл — дальше 250, не иду» и
/// остался ни с чем посреди пещеры. Причина не в пороге 250: причина в том,
/// что возрождался он у мирового спавна, а запасы лежат дома.
///
/// КАК ТОЧКА ВОЗРОЖДЕНИЯ СТАВИТСЯ В ЭТОЙ ИГРЕ (читано в 1.22.6, а не
/// додумано). СНОМ В КРОВАТИ — НЕ СТАВИТСЯ. Во всём моде выживания вызов
/// <c>IServerPlayer.SetSpawnPosition</c> ровно ОДИН, и он лежит в
/// ItemTemporalGear.OnHeldInteractStop: игрок берёт ВРЕМЕННУЮ ШЕСТЕРЁНКУ
/// (<c>gear-temporal</c>), наводится на блок и держит правую кнопку дольше
/// 3.45 с; сервер ставит точку в позицию ИГРОКА и ТРАТИТ шестерёнку.
/// BlockBed при этом не трогает точку возрождения вовсе — он только сажает
/// игрока на кровать и убавляет усталость. Поэтому «отметить дом» здесь
/// делается шестерёнкой (см. <see cref="Sleeping.SetRespawnHereAsync"/>), и
/// это ровно то, что делает живой игрок: ни телепортации, ни команд сервера.
///
/// ТОЧКА НЕ ВЕЧНАЯ. Настройка мира <c>temporalGearRespawnUses</c> говорит,
/// сколько ВОЗРОЖДЕНИЙ она переживёт: в наборах мира встречается «−1»
/// (вечная), «20», «3» и «0» (свою точку ставить нельзя вовсе). Сервер
/// убавляет остаток на каждом возрождении (ServerMain.GetSpawnPosition), а
/// когда он кончается — точку стирает, и игрок снова возрождается у мирового
/// спавна. САМ ОСТАТОК КЛИЕНТУ НЕ ПРИСЫЛАЕТСЯ; присылается только точка
/// (пакет 41, поля Spawnx/y/z). Поэтому врать «осталось 2 использования»
/// нельзя — зато видно, когда точка перестала быть домом, и тогда её надо
/// поставить заново.
///
/// Это механизм. Где дом и надо ли его отмечать — дело роли.
/// </summary>
public sealed class HomeSpawn
{
    private readonly BotContext ctx;
    private int deathsWhenMarked;
    private bool everMarked;

    public HomeSpawn(BotContext ctx) => this.ctx = ctx;

    /// <summary>Что произошло — словами.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Где дом. ФУНКЦИЯ, а не значение, нарочно: точка дома в боте уже
    /// живёт — её держит укрытие (<c>Shelter.Home</c>), туда её кладёт роль
    /// из своей настройки. Заведи мы здесь вторую копию — рано или поздно
    /// человек поменял бы дом в панели, укрытие узнало бы, а точка
    /// возрождения осталась бы у старого сарая.
    /// </summary>
    public Func<BlockPos?> Home { get; set; } = () => null;

    /// <summary>Насколько точка возрождения может отстоять от дома, чтобы считаться домашней.</summary>
    public double Tolerance { get; set; } = 4;

    /// <summary>Сколько отводится на дорогу домой.</summary>
    public double TravelSeconds { get; set; } = 180;

    /// <summary>Куда бот собирается возвращаться (null — дом не задан).</summary>
    public BlockPos? Point => Home();

    /// <summary>
    /// Точка возрождения, как её знает СЕРВЕР. Приходит в своём PlayerData(41)
    /// и обновляется каждый раз, когда сервер её меняет.
    /// </summary>
    public BlockPos? ServerPoint => ctx.Sleeping?.RespawnPoint;

    /// <summary>
    /// Точка возрождения стоит дома. null — ответить нечестно: либо дома нет,
    /// либо сервер ещё не присылал своей точки.
    /// </summary>
    public bool? AtHome =>
        Point is not { } home || ServerPoint is not { } server
            ? null
            : Near(home, server, Tolerance);

    /// <summary>
    /// Сколько раз бот умирал ПОСЛЕ того, как отметил дом. Это НЕ «остаток
    /// использований точки»: остаток считает сервер и клиенту не шлёт. Но
    /// сравнить это число с <c>temporalGearRespawnUses</c> человек может сам.
    /// </summary>
    public int? DeathsSinceMarked =>
        everMarked && ctx.Deaths is { } deaths ? deaths.Deaths - deathsWhenMarked : null;

    /// <summary>Дом отмечался в этой сессии хоть раз.</summary>
    public bool EverMarked => everMarked;

    /// <summary>Две клетки рядом (по прямой, с учётом высоты).</summary>
    public static bool Near(BlockPos a, BlockPos b, double tolerance)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= tolerance;
    }

    /// <summary>
    /// ЧТО ДЕЛАТЬ С ТОЧКОЙ ВОЗРОЖДЕНИЯ — решение без единого обращения к миру.
    ///
    /// Вынесено отдельной чистой функцией, потому что цена ошибки здесь не
    /// «медленно», а «бот потратил единственную временную шестерёнку не там»
    /// или «бот каждые две минуты ходит домой ставить то, что уже стоит».
    /// </summary>
    /// <param name="home">Точка дома (null — дома нет).</param>
    /// <param name="serverPoint">Точка возрождения по словам сервера (null — не присылал).</param>
    /// <param name="ownSpawnAllowed">Мир разрешает свою точку (temporalGearRespawnUses ≠ 0).</param>
    /// <param name="gearsInBag">Сколько временных шестерёнок в сумках.</param>
    /// <param name="tolerance">С какого расстояния точка считается домашней.</param>
    public static (HomeSpawnState State, string Why) Decide(
        BlockPos? home, BlockPos? serverPoint, bool ownSpawnAllowed, int gearsInBag,
        double tolerance)
    {
        if (home is not { } h)
            return (HomeSpawnState.NoHome, "дом не задан — отмечать нечего");

        if (!ownSpawnAllowed)
            return (HomeSpawnState.Forbidden,
                "в настройках мира temporalGearRespawnUses = 0: своя точка возрождения " +
                "здесь запрещена, шестерёнку тратить не стану");

        // КЛЕТКУ ПЕЧАТАЕТ САМ BlockPos, а не «{s.X}, {s.Y}, {s.Z}» руками: у
        // него ToString уже игровой (WorldOrigin). Пока числа разбирались по
        // полям, эта строка выдавала «точка возрождения уже дома (512061, 112,
        // 512257)» — адрес, которого нет ни в одном окне игры
        if (serverPoint is { } s && Near(h, s, tolerance))
            return (HomeSpawnState.AtHome, $"точка возрождения уже дома {s}");

        if (gearsInBag <= 0)
            return (HomeSpawnState.NoGear,
                "точку возрождения ставить нечем: временной шестерёнки нет" +
                (serverPoint is { } p ? $"; возрождаться буду на {p}" : ""));

        return (HomeSpawnState.NeedsMarking,
            serverPoint is { } q
                ? $"точка возрождения на {q}, а дом на {h} — иду отмечать"
                : $"своей точки возрождения нет — иду отмечать дом {h}");
    }

    /// <summary>Что бот сделал бы прямо сейчас и почему — без действий.</summary>
    public (HomeSpawnState State, string Why) Plan() => Decide(
        Point, ServerPoint,
        ctx.Deaths?.Rules.OwnSpawnAllowed ?? true,
        ctx.Hands?.CountOf(ctx.Sleeping?.RespawnItemCode ?? "gear-temporal") ?? 0,
        Tolerance);

    /// <summary>
    /// Дойти до дома и отметить его точкой возрождения.
    ///
    /// Успех — только по подтверждению сервера: <see cref="Sleeping"/> ждёт
    /// новую точку в PlayerData(41) и сверяет её с местом, где бот стоит.
    /// «Подержал шестерёнку» успехом не считается: мир мог это запретить, и
    /// тогда бот всю ночь возрождался бы у мирового спавна, считая, что дома.
    /// </summary>
    public async Task<HomeSpawnResult> MarkAsync(double maxSeconds = 240,
        CancellationToken ct = default)
    {
        var (state, why) = Plan();
        if (state != HomeSpawnState.NeedsMarking)
        {
            if (state != HomeSpawnState.AtHome)
                OnLog?.Invoke(why);
            return new HomeSpawnResult(state, why);
        }
        if (Point is not { } home)
            return new HomeSpawnResult(HomeSpawnState.NoHome, "дом не задан — отмечать нечего");

        if (ctx.Self.Position == null)
        {
            const string blind = "своей позиции ещё не знаю — идти домой не от чего";
            OnLog?.Invoke(blind);
            return new HomeSpawnResult(HomeSpawnState.NotReached, blind);
        }

        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        OnLog?.Invoke(why);

        if (!Standing(home))
        {
            double travel = Math.Max(10, Math.Min(TravelSeconds,
                (deadline - DateTime.UtcNow).TotalSeconds - 10));
            await ctx.Movement.TravelToAsync(home, travel, ct);
            // Судим по расстоянию, а не по слову дороги: она могла вернуть
            // false и всё-таки подвести вплотную
            if (!Standing(home))
            {
                string far = $"до дома не дошёл, осталось {DistanceTo(home):0} бл — " +
                             "точку возрождения ставить негде";
                OnLog?.Invoke(far);
                return new HomeSpawnResult(HomeSpawnState.NotReached, far);
            }
        }

        // Причину отказа знает только сам механизм шестерёнки — перехватываем
        // его последнее слово, чтобы не отвечать роли «не вышло» без причины
        string? said = null;
        void Catch(string m) => said = m;
        ctx.Sleeping.OnLog += Catch;
        bool ok;
        try
        {
            ok = await ctx.Sleeping.SetRespawnHereAsync(ct);
        }
        finally
        {
            ctx.Sleeping.OnLog -= Catch;
        }

        if (!ok)
        {
            string refused = said ?? "сервер новую точку возрождения не подтвердил";
            OnLog?.Invoke($"дом отметить не вышло: {refused}");
            return new HomeSpawnResult(HomeSpawnState.Refused, refused);
        }

        everMarked = true;
        deathsWhenMarked = ctx.Deaths?.Deaths ?? 0;
        int uses = ctx.Deaths?.Rules.GearRespawnUses ?? -1;
        string done = $"дом отмечен точкой возрождения {home}" +
                      (uses < 0 ? "" : $"; по настройкам мира её хватит на {uses} возрождений");
        OnLog?.Invoke(done);
        return new HomeSpawnResult(HomeSpawnState.Marked, done);
    }

    private double DistanceTo(BlockPos p)
    {
        if (ctx.Self.Position is not { } me)
            return double.MaxValue;
        double dx = p.X + 0.5 - me.X, dy = p.Y - me.Y, dz = p.Z + 0.5 - me.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private bool Standing(BlockPos p) => DistanceTo(p) <= Tolerance;
}

/// <summary>
/// ДЕРЖАТЬ ТОЧКУ ВОЗРОЖДЕНИЯ ДОМА — способность.
///
/// Механизм лежит в <see cref="HomeSpawn"/>, здесь только политика: когда
/// проверять, ходить ли ради этого через полкарты и что делать, если
/// шестерёнок нет.
///
/// Почему это отдельная способность, а не «поставил один раз при запуске».
/// Точка возрождения ИСЧЕЗАЕТ сама: сервер убавляет её остаток на каждом
/// возрождении и стирает, когда тот кончился. То есть после третьей смерти
/// бот молча начинает возрождаться у мирового спавна — ровно тот случай, с
/// которого всё началось. Проверять надо постоянно, а не однажды.
/// </summary>
public class BehaviorKeepHomeSpawn : BotBehavior
{
    private readonly BotContext ctx;
    private DateTime nextTry = DateTime.MinValue;
    private string lastSaid = "";

    /// <summary>
    /// РЕЙС ЗА ШЕСТЕРЁНКОЙ УЖЕ КОНЧИЛСЯ НИЧЕМ — БОЛЬШЕ НЕ ХОЖУ.
    ///
    /// Прямая просьба заказчика: «если не нашёл в игровой сессии дома
    /// шестеренку, то больше не бегает по кд домой на склад». Раньше это был
    /// флажок <c>gearHopeless</c> прямо здесь, и в его описании стояло
    /// «сбрасывается новым соединением» — а сбрасывать было НЕЧЕМ: ни одной
    /// строки, гасящей флажок, в классе не было. То есть бот, однажды не нашедший
    /// шестерёнку, не ходил за ней до конца работы программы, даже перезайдя в
    /// мир и даже если хозяин её туда положил.
    ///
    /// Теперь правило общее (<see cref="VsBotKit.Hopeless"/>) и им же пользуются
    /// запасы; забывание на новом соединении делает механизм, а не обещание в
    /// комментарии. «Не пробовать до конца захода» осталось ровно тем, о чём
    /// просил заказчик: <see cref="Hopeless{TKey}.TryAgainAfter"/> здесь null.
    /// </summary>
    public Hopeless Hopeless { get; } = new() { TryAgainAfter = null };

    public BehaviorKeepHomeSpawn(BotContext ctx, HomeSpawn? spawn = null)
    {
        this.ctx = ctx;
        Spawn = spawn ?? new HomeSpawn(ctx);
        Hopeless.OnSay += m => OnCannotMark?.Invoke(m);
        ctx.Bot.OnSessionReset += () => Hopeless.Forget();
    }

    /// <summary>Механизм: дом, точка сервера, отметка.</summary>
    public HomeSpawn Spawn { get; }

    /// <summary>
    /// Следить за точкой возрождения вообще. ВЫКЛЮЧЕНО по умолчанию, и это
    /// не осторожность ради осторожности.
    ///
    /// Каждая постановка ТРАТИТ временную шестерёнку (gear-temporal) — вещь
    /// редкую, которую копят на транслокатор. Способность попадает во все
    /// роли разом (см. Presets.Survive), а ручка для неё заведена не у каждой:
    /// включённая по умолчанию, она заставляла Жителя, Продавца и прочих
    /// раз в минуту проверять точку и тратить шестерёнки, о которых их никто
    /// не просил, — и выключить это из панели было нечем.
    ///
    /// Роль, которой точка нужна, включает её сама (у Копателя это ручка
    /// «ОтмечатьДом»).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Как часто проверять, дома ли точка возрождения, секунд.</summary>
    public double CheckSeconds { get; set; } = 60;

    /// <summary>Пауза после неудачной попытки отметить дом.</summary>
    public double RetrySeconds { get; set; } = 300;

    /// <summary>Сколько отводится на одну попытку (дорога домой плюс сама отметка).</summary>
    public double TimeoutSeconds { get; set; } = 240;

    /// <summary>Дальше этого ради отметки не пойдём: дорога домой — тоже риск.</summary>
    public double MaxDistance { get; set; } = 500;

    /// <summary>Не выходить ради этого в бурю: укрытие важнее.</summary>
    public bool AvoidStorm { get; set; } = true;

    /// <summary>
    /// Сходить за временной шестерёнкой на склад, когда в сумке её нет.
    ///
    /// БЕЗ ЭТОГО СПОСОБНОСТЬ БЫЛА ОБЕЩАНИЕМ, А НЕ РАБОТОЙ. Шестерёнка в мире
    /// не выкапывается и не крафтится: она падает с дрифтеров и лежит в
    /// руинах. Значит единственный честный источник для бота — сундук, куда
    /// её положил хозяин. Пока этого хода не было, в журнал уходило «[дом]
    /// точку возрождения ставить нечем», хотя три шестерёнки лежали дома.
    /// </summary>
    public bool FetchGearFromDepot { get; set; } = true;

    /// <summary>Сколько шестерёнок брать со склада за раз: точка тратит по одной на постановку.</summary>
    public int KeepGears { get; set; } = 1;

    /// <summary>Точка возрождения теперь дома.</summary>
    public event Action<HomeSpawnResult>? OnMarked;

    /// <summary>
    /// Проверили — точка возрождения дома, делать нечего. Говорится один раз
    /// на каждое «стало дома»: иначе человек не узнает, что бот вообще следит
    /// за точкой, а каждые полминуты одно и то же — уже не честность, а шум.
    /// </summary>
    public event Action<string>? OnAtHome;

    /// <summary>
    /// Отметить не вышло — с причиной. Одна и та же причина повторно не
    /// говорится: «дом не задан» каждые полминуты — это не честность, а шум.
    /// </summary>
    public event Action<string>? OnCannotMark;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || ctx.Self.IsDead || (ctx.Self.Health ?? 1) <= 0)
            return false;
        if (DateTime.UtcNow < nextTry || ctx.Movement.IsBusy)
            return false;

        nextTry = DateTime.UtcNow.AddSeconds(CheckSeconds);

        var (state, why) = Spawn.Plan();
        if (state == HomeSpawnState.AtHome)
        {
            if (why != lastSaid)
            {
                lastSaid = why;
                OnAtHome?.Invoke(why);
            }
            return false;
        }
        if (state != HomeSpawnState.NeedsMarking)
        {
            // ШЕСТЕРЁНКУ НЕ ВЫКАПЫВАЮТ — ЗА НЕЙ ХОДЯТ. Единственный источник,
            // который бот вправе использовать, — свой же сундук склада, куда
            // её положил хозяин. Всё остальное («сделать», «добыть») для
            // gear-temporal не существует, и обещать это было бы враньём
            string gear = ctx.Sleeping?.RespawnItemCode ?? "gear-temporal";
            if (state == HomeSpawnState.NoGear && FetchGearFromDepot &&
                ctx.Depot is { Known: true } depot && !Hopeless.Given(gear))
            {
                using var errand = ctx.Turn.TryTake("за временной шестерёнкой",
                    BodyArbiter.Importance.Routine, ct);
                if (errand == null)
                    return false;   // тело занято; про отказ говорит очередь на тело

                int got = await depot.TakeAsync(gear, Math.Max(1, KeepGears), errand.Token);
                nextTry = DateTime.UtcNow.AddSeconds(got > 0 ? CheckSeconds : RetrySeconds);
                if (got <= 0)
                    // НЕ БЕГАТЬ НА СКЛАД ПО КРУГУ. Живой случай заказчика: рейс
                    // за шестерёнкой не удавался (до дальних сундуков не дойти),
                    // и бот заводил его снова каждые RetrySeconds — «просто
                    // бегал туда-сюда, в итоге жесть какая-то». Его слова: «если
                    // не нашёл в игровой сессии дома шестеренку, то больше не
                    // бегает по кд домой на склад». Один честный отказ на
                    // соединение — и молчим до новой сессии или пока не положат
                    Hopeless.GiveUp(gear,
                        $"{why}; на складе её тоже нет — положите {gear} в сундук склада " +
                        "или выключите отметку дома. Больше за ней в этот заход не хожу");
                else
                    // ПРИНЕСЛИ — ЗНАЧИТ СКЛАД ЖИВОЙ. Следующая нехватка обязана
                    // снова дойти до сундука, а не упереться во вчерашнее «нет»
                    Hopeless.TryAgain(gear, "шестерёнка нашлась");
                return true;    // рейс на склад занимает бота целиком
            }
            Say(why);
            return false;
        }

        if (AvoidStorm && ctx.Storms.ShelterNow)
            return false;
        if (Spawn.Point is { } home && ctx.Self.Position is { } me)
        {
            double dx = home.X + 0.5 - me.X, dy = home.Y - me.Y, dz = home.Z + 0.5 - me.Z;
            double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > MaxDistance)
            {
                Say($"до дома {distance:0} бл — дальше {MaxDistance:0}, за точкой возрождения " +
                    "сейчас не пойду");
                return false;
            }
        }

        // ДОРОГА ДОМОЙ — ЭТО МИНУТЫ ХОДЬБЫ, и тело обязано быть за нами.
        // Без владения выходило две пары рук на одних клавишах: наряд роли вёл
        // бота на делянку, а отметка — домой, и он топтался между ними
        using var hold = ctx.Turn.TryTake("отметить дом", BodyArbiter.Importance.Routine, ct);
        if (hold == null)
            return false;   // тело занято; про сам отказ говорит очередь на тело

        var result = await Spawn.MarkAsync(TimeoutSeconds, hold.Token);
        // Пауза от КОНЦА попытки: поход домой длинный, и иначе следующая
        // началась бы сразу, не дав боту ни поесть, ни поработать
        nextTry = DateTime.UtcNow.AddSeconds(result.Success ? CheckSeconds : RetrySeconds);
        if (result.Success)
        {
            lastSaid = result.Message;
            OnMarked?.Invoke(result);
        }
        else
        {
            Say(result.Message);
        }
        return true;    // поход домой занимает бота целиком, ход был наш
    }

    private void Say(string message)
    {
        if (message == lastSaid)
            return;
        lastSaid = message;
        OnCannotMark?.Invoke(message);
    }
}
