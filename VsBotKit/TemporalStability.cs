namespace VsBotKit;

/// <summary>Куда клонится стабильность прямо сейчас.</summary>
public enum StabilityTrend
{
    /// <summary>Замеров ещё мало — судить не о чем.</summary>
    Unknown,

    /// <summary>Растёт: место лечит.</summary>
    Rising,

    /// <summary>Падает: место тянет.</summary>
    Falling,

    /// <summary>Стоит на месте (в пределах мёртвой зоны).</summary>
    Steady
}

/// <summary>Разлом («rift») так, как его видит клиент: где он и какого размера.</summary>
/// <param name="Id">Номер разлома у сервера — по нему разлом узнаётся между посылками.</param>
/// <param name="Size">Размер: от него зависит, с какого расстояния разлом уже вредит.</param>
public sealed record RiftInfo(int Id, double X, double Y, double Z, double Size)
{
    /// <summary>Расстояние от точки до середины разлома.</summary>
    public double DistanceTo(double x, double y, double z)
    {
        double dx = X - x, dy = Y - y, dz = Z - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"разлом #{Id} ({X:0}, {Y:0}, {Z:0}) размер {Size:0.#}";
}

/// <summary>
/// ЧИСТЫЕ ПРАВИЛА ТЕМПОРАЛЬНОЙ СТАБИЛЬНОСТИ — числа и формулы взяты ИЗ СБОРОК
/// ИГРЫ (VSSurvivalMod 1.22.6), а не придуманы.
///
/// Откуда что:
/// * <see cref="HurtsBelow"/>, <see cref="DamageBase"/> —
///   EntityBehaviorTemporalStabilityAffected.OnGameTick: раз в четыре секунды
///   на сервере, если стабильность ниже 0,13, игрок получает урон
///   <c>0,15 − стабильность</c> (тип урона — «временной»).
/// * <see cref="GlitchBelow"/>, <see cref="FogBelow"/> — там же: ниже 0,2
///   начинается «глюк» экрана, ниже 0,3 — ржавый туман. Боту картинка не
///   нужна, но это те самые пороги, на которых живой игрок разворачивается.
/// * <see cref="RiftPenalty"/> — ModSystemRifts.OnServerTick100ms: сервер
///   каждые 0,1 с ставит игроку <c>stabilityOffset</c> по БЛИЖАЙШЕМУ разлому,
///   и эта поправка ПРЯМО складывается со стабильностью места.
///
/// Здесь нет ни мира, ни сети — только числа, поэтому правила проверяются
/// стендом (ТемпоральнаяСтабильностьTests).
/// </summary>
public static class StabilityRules
{
    /// <summary>Имя атрибута сущности, под которым игра держит стабильность игрока.</summary>
    public const string Key = "temporalStability";

    /// <summary>Ниже этого сервер бьёт временным уроном раз в четыре секунды.</summary>
    public const double HurtsBelow = 0.13;

    /// <summary>Урон = это число минус стабильность (при стабильности ниже <see cref="HurtsBelow"/>).</summary>
    public const double DamageBase = 0.15;

    /// <summary>Ниже этого у живого игрока начинает «глючить» экран.</summary>
    public const double GlitchBelow = 0.2;

    /// <summary>Ниже этого мир затягивает ржавым туманом.</summary>
    public const double FogBelow = 0.3;

    /// <summary>Раз во столько секунд сервер отвешивает временной урон.</summary>
    public const double DamageEverySeconds = 4;

    /// <summary>
    /// Сколько здоровья снимет один такой удар. Ноль — не бьёт вовсе.
    /// </summary>
    public static double DamagePerHit(double own) =>
        own < HurtsBelow ? Math.Max(0, DamageBase - own) : 0;

    /// <summary>
    /// ПОПРАВКА ОТ РАЗЛОМА — дословно формула сервера
    /// (ModSystemRifts.OnServerTick100ms):
    /// <c>d = max(0, расстояние − 2 − размер/2); −(max(0, 1 − d/3))² × 20</c>.
    ///
    /// Число −20 огромно нарочно: стабильность места редко бывает выше 1,5, и
    /// вплотную к разлому она уходит в ноль за секунды. Зато уже в трёх блоках
    /// от края разлома поправка РОВНО ноль — то есть «держаться подальше от
    /// разломов» на деле означает «не подходить к ним ближе трёх блоков», а не
    /// «уйти от них на сотню».
    /// </summary>
    public static double RiftPenalty(double distance, double size)
    {
        double edge = Math.Max(0, distance - 2 - size / 2);
        double near = Math.Max(0, 1 - edge / 3);
        return -(near * near) * 20;
    }

    /// <summary>
    /// Ближе какого расстояния до середины разлома он уже вредит.
    /// Дальше — поправка ровно ноль, и бежать не от чего.
    /// </summary>
    public static double RiftHarmRadius(double size) => 2 + size / 2 + 3;

    // ---------------- что мир решил про разломы ----------------

    /// <summary>
    /// Ключ настройки мира, которой хозяин сервера включает разломы. Не выдуман:
    /// ровно его читает сам мод (ModSystemRifts.Event_SaveGameLoaded:
    /// <c>api.World.Config.GetString("temporalRifts", "visible")</c>).
    /// </summary>
    public const string RiftModeKey = "temporalRifts";

    /// <summary>Разломов в мире нет вовсе.</summary>
    public const string RiftModeOff = "off";

    /// <summary>
    /// Разломы есть, но мир их не показывает. Три следствия, и все из сборки
    /// (VSSurvivalMod 1.22.6, ModSystemRifts):
    /// • список разломов клиенту НЕ уходит вовсе — <c>BroadCastRifts</c> первой
    ///   же строкой выходит при <c>riftMode != "visible"</c>;
    /// • на стабильность они НЕ влияют — <c>OnServerTick100ms</c>, который и
    ///   ставит игроку <c>stabilityOffset</c>, выходит там же и по тому же
    ///   условию;
    /// • дрифтеров на поверхности они при этом плодят по-прежнему
    ///   (<c>riftsEnabled = riftMode != "off"</c>), и сообщение «разломы
    ///   включены» боту приходит.
    /// </summary>
    public const string RiftModeInvisible = "invisible";

    /// <summary>Разломы есть, видны и тянут стабильность — умолчание самой игры.</summary>
    public const string RiftModeVisible = "visible";

    /// <summary>
    /// ПОКАЖЕТ ЛИ МИР РАЗЛОМЫ и станут ли они тянуть стабильность. Оба вопроса
    /// решаются ОДНИМ условием сервера (<c>riftMode != "visible"</c>), поэтому и
    /// ответ здесь один: не «visible» — значит бот их не увидит и они ему не
    /// повредят. null — настроек мира ещё нет, и врать в обе стороны нельзя.
    /// </summary>
    public static bool? RiftsShown(string? mode) =>
        mode is null ? null : mode.Equals(RiftModeVisible, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ЧТО СКАЗАТЬ ПРО РАЗЛОМЫ — чистое правило, ни мира, ни бота.
    ///
    /// Ради чего оно появилось отдельно. Команда «!стабильность» различала
    /// только «разломов в мире нет» и во всех прочих случаях говорила
    /// «Разломов поблизости не вижу». В мире с <c>temporalRifts = invisible</c>
    /// сервер отвечает «разломы включены» И НЕ ШЛЁТ НИ ОДНОГО — и бот выдавал
    /// незнание за знание: «поблизости не вижу» вместо «этот мир мне их не
    /// показывает». Это ровно то изображение знания, которого проект не терпит.
    /// </summary>
    /// <param name="enabled">Что сервер сказал отдельным сообщением канала «rifts» (null — не сказал).</param>
    /// <param name="mode">Настройка мира <see cref="RiftModeKey"/> (null — настроек мира ещё нет).</param>
    /// <param name="nearest">Разломы, о которых сервер рассказал, ближайшие первыми.</param>
    public static string SayRifts(bool? enabled, string? mode,
        IReadOnlyList<(RiftInfo Rift, double Distance)> nearest)
    {
        if (enabled == false || string.Equals(mode, RiftModeOff, StringComparison.OrdinalIgnoreCase))
            return "Разломов в этом мире нет";

        // МИР ЗАПРЕТИЛ ИХ ПОКАЗЫВАТЬ. Пустой список тут не значит «рядом их
        // нет»: сервер не шлёт его вовсе. Но и бежать от них незачем —
        // стабильность в этом режиме они не трогают, и это надо сказать сразу,
        // иначе человек примется искать разлом, которого ему не покажут
        if (RiftsShown(mode) == false)
            return "Разломы в этом мире есть, но мир не велит их показывать " +
                   $"({RiftModeKey} = {mode}) — где они, я не знаю и знать не могу. " +
                   "На стабильность они в этом режиме не влияют, но дрифтеров плодят";

        if (nearest.Count == 0)
            return enabled == null
                ? "Про разломы сервер мне пока ничего не сказал"
                : "Разломов поблизости не вижу";

        // РАССТОЯНИЯ МОЖЕТ НЕ БЫТЬ (бот ещё не знает, где стоит), и подставлять
        // вместо него ноль нельзя: ноль читается как «вплотную», то есть как
        // «сейчас умру». Такое расстояние приходит сюда как NaN и называется
        // словами
        return "Разломы: " + string.Join("; ", nearest.Take(3).Select(p =>
            double.IsNaN(p.Distance)
                ? $"{p.Rift} — далеко ли, не знаю: не знаю, где я сам"
                : $"{p.Rift} — {p.Distance:0.#} бл" +
                  (RiftHarms(p.Distance, p.Rift.Size) ? " (ТЯНЕТ)" : "")));
    }

    /// <summary>Вредит ли разлом с такого расстояния.</summary>
    public static bool RiftHarms(double distance, double size) =>
        distance < RiftHarmRadius(size);

    /// <summary>
    /// КУДА ОТОЙТИ ОТ РАЗЛОМА: та же прямая, что от разлома к боту, но длиной
    /// «вредный радиус плюс запас». Отходим ПО ГОРИЗОНТАЛИ: подниматься или
    /// спускаться ради разлома незачем, а вот лезть вверх по отвесной стене —
    /// повод застрять.
    ///
    /// Бот стоит ровно в середине разлома (такое бывает: разлом появляется
    /// там, где темно, в том числе на голове) — уходим на восток: направление
    /// в этом случае не значит ничего, а «никуда не пойду» значит смерть.
    /// </summary>
    public static (double X, double Z) AwayFromRift(double x, double z,
        double riftX, double riftZ, double size, double margin)
    {
        double dx = x - riftX, dz = z - riftZ;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.001)
        {
            dx = 1;
            dz = 0;
            len = 1;
        }
        double need = RiftHarmRadius(size) + Math.Max(0, margin);
        return (riftX + dx / len * need, riftZ + dz / len * need);
    }

    /// <summary>
    /// КУДА КЛОНИТСЯ. Мёртвая зона нужна, чтобы дрожание последнего знака не
    /// выглядело ростом: сервер шлёт число по пять раз в секунду, и без неё
    /// «растёт» и «падает» сменялись бы каждый тик.
    /// </summary>
    /// <param name="older">Замер в начале окна.</param>
    /// <param name="newer">Последний замер.</param>
    /// <param name="seconds">Сколько секунд между ними.</param>
    /// <param name="deadZonePerMinute">Меньше этого хода в минуту — «стоит».</param>
    public static StabilityTrend TrendOf(double older, double newer, double seconds,
        double deadZonePerMinute)
    {
        if (!(seconds > 0))
            return StabilityTrend.Unknown;
        double perMinute = (newer - older) / seconds * 60;
        if (Math.Abs(perMinute) < Math.Max(0, deadZonePerMinute))
            return StabilityTrend.Steady;
        return perMinute > 0 ? StabilityTrend.Rising : StabilityTrend.Falling;
    }

    /// <summary>
    /// ЧТО ЭТО ЗНАЧИТ ЧЕЛОВЕЧЕСКИМИ СЛОВАМИ — одной строкой для панели, чата и
    /// журнала. Строка ОДНА на всех нарочно: три разных описания одного и того
    /// же числа разошлись бы в первый же день.
    /// </summary>
    public static string Say(double? own, StabilityTrend trend)
    {
        if (own is not { } v)
            return "неизвестна";
        string where =
            v < HurtsBelow ? "бьёт временным уроном"
            : v < GlitchBelow ? "очень низко"
            : v < FogBelow ? "низко"
            : v >= 0.999 ? "полная"
            : "в порядке";
        string move = trend switch
        {
            StabilityTrend.Rising => ", растёт",
            StabilityTrend.Falling => ", падает",
            StabilityTrend.Steady => ", стоит",
            _ => ""
        };
        return $"{v:0.##} ({where}{move})";
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО «ПОРА БЕЖАТЬ»: решение и ПРИЧИНА словами. Причина
    /// возвращается всегда — молчаливый отказ неотличим от поломки.
    ///
    /// Гистерезис (<paramref name="calmAt"/>) обязателен: у самого порога число
    /// дрожит, и без него бот дёргался бы «бегу — не бегу» по пять раз в
    /// секунду, не сделав ни шагу.
    /// </summary>
    /// <param name="enabled">Роль вообще разрешила уходить.</param>
    /// <param name="own">Своя стабильность (null — сервер не сказал).</param>
    /// <param name="runBelow">Ниже этого — уходить.</param>
    /// <param name="calmAt">Выше этого — считать, что обошлось.</param>
    /// <param name="alreadyRunning">Уже уходим (тогда порог отпускания выше).</param>
    public static (bool Run, string Why) RunVerdict(bool enabled, double? own,
        double runBelow, double calmAt, bool alreadyRunning)
    {
        if (!enabled)
            return (false, "уходить из-за стабильности мне не велено");
        if (own is not { } v)
            return (false, "сервер про мою стабильность ничего не шлёт — " +
                           "либо в этом мире её нет вовсе, либо я ещё не в мире");

        double bar = Math.Clamp(runBelow, 0, 1);
        double calm = Math.Clamp(Math.Max(calmAt, bar), 0, 1);

        // «ПОРОГ ЕДИНИЦА» — ЭТО «УХОДИТЬ ВСЕГДА», И ЭТО НЕ ПРИДИРКА.
        // Полная стабильность равна РОВНО единице (сервер зажимает её в 0..1),
        // и строгое «ниже порога» на таком пороге не срабатывало бы никогда.
        // Кривая потребности (NeedCurves.Instability) при таком пороге уже
        // отвечает «созрело», и планировщик выбирал бы дело, которое само
        // отказывается работать, — молча и каждый тик
        if (bar >= 1)
            return (true, $"порог {bar:0.##} — уходить всегда, так настроено " +
                          $"(стабильность {v:0.##})");
        // ЗАПАС СЧИТАЕТ ОБЩИЙ МЕХАНИЗМ, а не эта строка: ровно тем же дребезгом
        // у порога болел фонарь в руке (58 пар «взял → убрал» за смену), и
        // держать два рукописных правила «держи прежний ответ, пока число в
        // полосе» — значит однажды поправить одно и забыть другое
        if (Hysteresis.Holds(v < bar, v < calm, alreadyRunning))
            return (true, v < bar
                ? $"стабильность {v:0.##} ниже порога {bar:0.##}"
                : $"стабильность {v:0.##} ещё не поднялась до {calm:0.##}");
        return (false, alreadyRunning
            ? $"стабильность {v:0.##} поднялась до {calm:0.##} — можно возвращаться к делам"
            : $"стабильность {v:0.##} не ниже порога {bar:0.##}");
    }
}

/// <summary>
/// ТЕМПОРАЛЬНАЯ СТАБИЛЬНОСТЬ БОТА — то самое число, что живой игрок видит по
/// шестерёнке в углу экрана.
///
/// ОТКУДА ОНО БЕРЁТСЯ (проверено живьём на испытательном сервере 15.08):
/// сервер держит стабильность в СИНХРОНИЗИРУЕМЫХ атрибутах сущности игрока под
/// ключом «temporalStability» (EntityBehaviorTemporalStabilityAffected.
/// OwnStability) и шлёт её клиенту обычным обновлением атрибутов — по пять раз
/// в секунду, потому что поведение переписывает число каждый серверный тик.
/// В живом прогоне бот получил 4,7 обновления в секунду и увидел число 1 при
/// y = 113. Никакого «знания сверх игрока» тут нет: ровно этот пакет получает
/// и обычный клиент, он же рисует по нему шестерёнку.
///
/// ЧЕГО БОТ НЕ ЗНАЕТ И НЕ ВЫДУМЫВАЕТ. Стабильность МЕСТА (то, куда бот придёт,
/// если пойдёт) считается на сервере по трёхмерному шуму от СИДА МИРА
/// (SystemTemporalStability.GetTemporalStability): шум по x/80, y/80, z/80,
/// плюс подтягивание к 0,8…1,5 у поверхности, минус глубина ниже уровня моря,
/// минус 1,5 × сила бури. Сида у клиента нет, и посчитать «сколько будет вон
/// там» бот не может. Поэтому он делает то же, что человек: СМОТРИТ НА СВОЁ
/// ЧИСЛО и на то, растёт оно или падает (<see cref="Trend"/>), а из формулы
/// берёт лишь то, что знает точно и без сида:
///   * поправку от разлома — она приходит списком по каналу «rifts»;
///   * знак от высоты: в формуле два слагаемых зависят от y и оба растут
///     вверх, значит подъём к поверхности стабильность места не понижает.
///
/// НАСТРОЕК ЗДЕСЬ НЕТ: пороги «когда бежать», вес и «бежать ли вообще» —
/// дело роли (см. BehaviorSeekStability и ручки роли). Здесь только механизм.
/// </summary>
public sealed class TemporalStability
{
    private readonly EntityModel entities;
    private readonly DeathRules? rules;
    private readonly Queue<(DateTime When, double Value)> samples = new();
    private int riftChannel = -1;

    /// <summary>Окно, по которому судим о ходе стабильности.</summary>
    public TimeSpan TrendWindow { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Короче этого окна о ходе не судим вовсе. Между двумя соседними пакетами
    /// стабильность меняется на тысячные, и «растёт» по такой паре означало бы
    /// шум, а не движение.
    /// </summary>
    public double MinTrendSeconds { get; set; } = 4;

    /// <summary>Меньше этого хода в минуту считаем, что стабильность стоит.</summary>
    public double TrendDeadZonePerMinute { get; set; } = 0.005;

    /// <summary>Что случилось со стабильностью и разломами — словами.</summary>
    public event Action<string>? OnLog;

    // ---------------- журнал: одно событие — одна строка ----------------

    /// <summary>
    /// ВСЁ, ЧТО ГОВОРИТ СТАБИЛЬНОСТЬ, ИДЁТ ЧЕРЕЗ ЭТУ ДВЕРЬ — и другой у неё нет.
    ///
    /// ЖИВОЙ СЛУЧАЙ, словами заказчика: «[стабильность] разломов поблизости
    /// нет» печатается около десяти раз в минуту. И это не поломка бота, а
    /// работа сервера: список разломов рассылается тиком раз в три секунды
    /// (ModSystemRifts.OnServerTick3s, 2999 мс) — и не только когда разломы
    /// родились или умерли, но и КАЖДЫЙ РАЗ, когда игрок переходит в соседний
    /// чанк (там же: сравнение <c>chunkIndexbyPlayer</c>). Идущий бот меняет
    /// чанк постоянно, поэтому одна и та же новость приходила ему десятками
    /// за минуту, а новостью не была ни разу.
    ///
    /// Заглушка стоит ЗДЕСЬ, а не у каждой строки: правило «одно событие — одна
    /// строка» должно быть ОДНО на всю стабильность (правило 1 проекта), иначе
    /// следующая новая жалоба снова поедет в журнал россыпью. Механизм общий —
    /// тот же <see cref="LogRepeats"/>, которым уже глушили «тупик» у движения
    /// и «ломаю» у добычи.
    ///
    /// Считает по ГОТОВОМУ ТЕКСТУ, поэтому «разломов поблизости: 3» после
    /// «разломов поблизости нет» выходит СРАЗУ: изменилось — значит новость.
    /// </summary>
    private void Say(string message)
    {
        if (repeats.Say(message, DateTime.UtcNow) is { } line)
            OnLog?.Invoke(line);
    }

    /// <summary>
    /// ДОСКАЗАТЬ НЕДОСКАЗАННОЕ: сколько раз бот промолчал о том же самом.
    /// Молчание, о котором не сказано, — это уже не «тихо», а «скрыл».
    /// </summary>
    /// <param name="всё">
    /// Взять и ещё НЕ ОСТЫВШЕЕ. Обычный хвост берёт только то, о чём давно не
    /// повторялись, — так задумано у самой заглушки: иначе в бою она давала бы
    /// вторую волну шума вместо первой. Но смена мира — это не «дело кончилось»,
    /// а «мира больше нет»: приступ оборвался наверняка, и ждать, пока он
    /// остынет, уже негде. Спрашиваем хвост так, будто время тишины прошло.
    /// </param>
    private void SayTail(bool всё = false)
    {
        var when = всё ? DateTime.UtcNow.AddSeconds(repeats.QuietSeconds) : DateTime.UtcNow;
        if (repeats.Tail(when) is { } tail)
            OnLog?.Invoke(tail);
    }

    /// <summary>
    /// Насколько глушить повторы в журнале стабильности, секунд (0 — не
    /// глушить вовсе, на разбор живого журнала).
    ///
    /// ПЯТЬ МИНУТ, а не десять секунд, как у движения, и вот почему. У движения
    /// «тупик» — это событие: бот уткнулся и через миг пойдёт иначе. А
    /// «разломов поблизости нет» — это СОСТОЯНИЕ мира, и меняется оно не чаще,
    /// чем сервер плодит и хоронит разломы (часы игрового времени). Десять
    /// секунд оставили бы шесть строк в минуту вместо десяти — то есть не
    /// починили бы ничего.
    /// </summary>
    public double QuietRepeatSeconds
    {
        get => repeats.QuietSeconds;
        set => repeats.QuietSeconds = value;
    }

    private readonly LogRepeats repeats = new() { QuietSeconds = 300 };

    public TemporalStability(BotClient bot, EntityModel entities, DeathRules? rules = null)
    {
        this.entities = entities;
        this.rules = rules;
        bot.OnPacket += HandlePacket;
        // НОВОЕ СОЕДИНЕНИЕ — ДРУГОЙ МИР И ДРУГИЕ РАЗЛОМЫ. Номер канала выдаёт
        // сервер, и на другом сервере он другой; замеры прошлого мира тем более
        // ни о чём не говорят
        bot.OnSessionReset += Forget;
    }

    /// <summary>Забыть замеры и разломы: сменился мир — прежнее знание врёт.</summary>
    public void Forget()
    {
        // СНАЧАЛА ДОСКАЗАТЬ, ПОТОМ ЗАБЫТЬ. Смена мира — единственный момент,
        // когда приступ повторов заведомо кончился: считать «разломов
        // поблизости нет» прежнего мира вместе с новым нельзя, а потерять счёт
        // молча — значит скрыть, сколько раз бот об этом промолчал
        SayTail(всё: true);
        repeats.Clear();

        samples.Clear();
        rifts.Clear();
        riftChannel = -1;
        RiftsEnabled = null;
    }

    /// <summary>
    /// Своя стабильность, 0..1. null — сервер её не присылал: либо бот ещё не в
    /// мире, либо в этом мире временной стабильности нет вовсе (тогда игра не
    /// вешает игроку и само поведение).
    /// </summary>
    public double? Own =>
        entities.Self is { } me && me.WatchedAttributes.HasAttribute(StabilityRules.Key)
            ? me.WatchedAttributes.GetDouble(StabilityRules.Key)
            : null;

    /// <summary>Сервер хоть раз прислал число.</summary>
    public bool Known => Own != null;

    /// <summary>
    /// Что о временной стабильности говорят НАСТРОЙКИ МИРА (их сервер шлёт
    /// клиенту пакетами 1 и 21, ключ «temporalStability»). null — настроек ещё
    /// нет или ключа в них нет.
    /// </summary>
    public bool? OnInThisWorld => rules?.Text("temporalStability") is { } text
        ? text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"
        : null;

    /// <summary>Ход стабильности, единиц в минуту (со знаком). null — замеров мало.</summary>
    public double? PerMinute
    {
        get
        {
            var window = Window();
            return window is { } w ? (w.Newer - w.Older) / w.Seconds * 60 : null;
        }
    }

    /// <summary>Куда клонится стабильность.</summary>
    public StabilityTrend Trend
    {
        get
        {
            var window = Window();
            return window is { } w
                ? StabilityRules.TrendOf(w.Older, w.Newer, w.Seconds, TrendDeadZonePerMinute)
                : StabilityTrend.Unknown;
        }
    }

    /// <summary>Растёт (место лечит) — это и есть ответ «здесь лучше».</summary>
    public bool Rising => Trend == StabilityTrend.Rising;

    /// <summary>Падает: место тянет вниз.</summary>
    public bool Falling => Trend == StabilityTrend.Falling;

    /// <summary>Одной строкой для панели, чата и журнала.</summary>
    public string Say() => StabilityRules.Say(Own, Trend);

    private (double Older, double Newer, double Seconds)? Window()
    {
        lock (samples)
        {
            if (samples.Count < 2)
                return null;
            var first = samples.Peek();
            var last = samples.Last();
            double seconds = (last.When - first.When).TotalSeconds;
            return seconds >= MinTrendSeconds ? (first.Value, last.Value, seconds) : null;
        }
    }

    // ---------------- разломы ----------------

    private readonly List<RiftInfo> rifts = [];

    /// <summary>
    /// РАЗЛОМЫ, О КОТОРЫХ СЕРВЕР РАССКАЗАЛ. Приходят по каналу «rifts»
    /// (ModSystemRifts) списком тех, что ближе 200 блоков к боту, и только если
    /// мир разрешил их видеть (настройка мира <c>temporalRifts = visible</c>).
    /// Это ровно то же знание, что у живого игрока: по этому же пакету клиент
    /// их и рисует.
    /// </summary>
    public IReadOnlyList<RiftInfo> Rifts
    {
        get { lock (rifts) return rifts.ToArray(); }
    }

    /// <summary>
    /// Разломы в этом мире включены (сервер сказал отдельным сообщением).
    /// null — сервер ещё не сказал; false — их нет, и бежать не от чего.
    ///
    /// ВНИМАНИЕ: <c>true</c> НЕ означает «я их увижу». Сервер ставит здесь
    /// <c>riftMode != "off"</c>, а показывает разломы только в режиме
    /// «visible» — см. <see cref="RiftsShown"/>.
    /// </summary>
    public bool? RiftsEnabled { get; private set; }

    /// <summary>
    /// РЕЖИМ РАЗЛОМОВ ИЗ НАСТРОЕК МИРА: «off», «invisible» или «visible».
    /// null — настройки мира ещё не пришли (пакеты 1 и 21), и гадать нечего.
    ///
    /// Ключа может не быть и в пришедших настройках — тогда мир живёт по
    /// умолчанию самой игры, и оно тут же и подставляется: сервер читает эту
    /// настройку ровно так же (<c>Config.GetString("temporalRifts", "visible")</c>).
    /// </summary>
    public string? RiftsMode => rules is { Known: true }
        ? rules.Text(StabilityRules.RiftModeKey) ?? StabilityRules.RiftModeVisible
        : null;

    /// <summary>
    /// ПОКАЗЫВАЕТ ли мир разломы боту (и тянут ли они стабильность): это одно и
    /// то же условие сервера. null — настроек мира ещё нет.
    /// </summary>
    public bool? RiftsShown => StabilityRules.RiftsShown(RiftsMode);

    /// <summary>
    /// Разломы, о которых сервер рассказал, БЛИЖАЙШИЕ ПЕРВЫМИ, с расстоянием до
    /// каждого. Своей позиции нет — порядок оставляем как пришёл, а расстояния
    /// не выдумываем (0 значило бы «вплотную», то есть худшую из возможных лжи).
    /// </summary>
    public IReadOnlyList<(RiftInfo Rift, double Distance)> Nearest(double? x, double? y, double? z)
    {
        var all = Rifts;
        if (x is not { } px || y is not { } py || z is not { } pz)
            return all.Select(r => (r, double.NaN)).ToArray();
        return all.Select(r => (Rift: r, Distance: r.DistanceTo(px, py, pz)))
            .OrderBy(p => p.Distance)
            .ToArray();
    }

    /// <summary>
    /// Ближайший разлом, который ПРЯМО СЕЙЧАС вредит: то есть тот, чья поправка
    /// к стабильности не ноль. Дальше трёх блоков от края разлома поправки нет
    /// вовсе — и такой разлом сюда не попадает, хотя и виден.
    /// </summary>
    public (RiftInfo Rift, double Distance)? NearestHarmful(double x, double y, double z)
    {
        (RiftInfo, double)? best = null;
        foreach (var rift in Rifts)
        {
            double d = rift.DistanceTo(x, y, z);
            if (!StabilityRules.RiftHarms(d, rift.Size))
                continue;
            if (best is null || d < best.Value.Item2)
                best = (rift, d);
        }
        return best;
    }

    // ---------------- приём пакетов ----------------

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            // Полное и частичное обновление атрибутов сущности, и то же самое
            // пачкой. Пачка тут не для порядка: живой сервер шлёт стабильность
            // ИМЕННО ею (60), и проба, считавшая только 38, показала ноль
            // обновлений при живом потоке в пять штук в секунду
            case 37:
            case 38:
            case 60:
                Sample(DateTime.UtcNow);
                break;

            case 56 when p.NetworkChannels?.ChannelNames != null:
                for (int i = 0; i < p.NetworkChannels.ChannelNamesCount; i++)
                    if (p.NetworkChannels.ChannelNames[i] == "rifts" &&
                        p.NetworkChannels.ChannelIds != null && i < p.NetworkChannels.ChannelIdsCount)
                    {
                        riftChannel = p.NetworkChannels.ChannelIds[i];
                        Say($"канал разломов найден (id {riftChannel})");
                    }
                break;

            case 55 when p.CustomPacket is { } cp && cp.ChannelId == riftChannel:
                ReadRiftPacket(cp);
                break;
        }
    }

    /// <summary>Замер по нынешнему числу — так его снимает приход пакета.</summary>
    private void Sample(DateTime at)
    {
        if (Own is { } value)
            Note(value, at);
    }

    /// <summary>
    /// Новое показание. ЗНАЧЕНИЕ И ВРЕМЯ ДОВОДАМИ — тем же приёмом, что у
    /// <see cref="Drift.Add"/>: иначе ход стабильности проверялся бы только
    /// живым ожиданием по нескольку секунд на каждую проверку.
    /// </summary>
    public void Note(double value, DateTime at)
    {
        lock (samples)
        {
            samples.Enqueue((at, value));
            // Держим окно: всё, что старше него, к нынешнему ходу отношения не
            // имеет. Одну запись старше окна оставляем нарочно — иначе окно
            // схлопывалось бы до нуля секунд и ход было бы не по чему считать
            while (samples.Count > 2 && at - samples.Peek().When > TrendWindow)
                samples.Dequeue();
        }
    }

    /// <summary>
    /// Разбор сообщения канала «rifts». Номер сообщения — это ПОРЯДОК
    /// регистрации типов на сервере (NetworkChannelBase.RegisterMessageType):
    /// 0 — список разломов, 1 — включены ли они вообще. Гадать по длине тела
    /// нельзя: пустой список весит ноль байт, и его легко принять за что угодно.
    /// </summary>
    private void ReadRiftPacket(Packet_CustomPacket cp)
    {
        try
        {
            using var ms = new MemoryStream(cp.Data ?? []);
            if (cp.MessageId == 0)
            {
                var list = ProtoBuf.Serializer.Deserialize<Vintagestory.GameContent.RiftList>(ms);
                lock (rifts)
                {
                    rifts.Clear();
                    foreach (var r in list?.rifts ?? [])
                        if (r?.Position is { } pos)
                            rifts.Add(new RiftInfo(r.RiftId, pos.X, pos.Y, pos.Z, r.Size));
                }
                Say(rifts.Count == 0
                    ? "разломов поблизости нет"
                    : $"разломов поблизости: {rifts.Count}");
                return;
            }
            var status = ProtoBuf.Serializer.Deserialize<Vintagestory.GameContent.RiftsStatus>(ms);
            RiftsEnabled = status?.Enabled;
            Say(RiftsEnabled == true
                ? "разломы в этом мире есть"
                : "разломов в этом мире нет");
        }
        catch (Exception ex)
        {
            // Молчать нельзя: бот будет считать, что разломов рядом нет, —
            // а это ровно то место, где стабильность падает быстрее всего
            Say($"не разобрал данные разломов: {ex.GetType().Name} {ex.Message}");
        }
    }
}
