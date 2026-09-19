namespace VsBotKit;

/// <summary>Чем кончилась разведка.</summary>
public enum ProspectStop
{
    /// <summary>Руда найдена.</summary>
    Found,
    /// <summary>Прошли весь план и ничего не нашли.</summary>
    Nothing,
    /// <summary>Отменили снаружи.</summary>
    Cancelled,
    /// <summary>Кончилось отведённое время.</summary>
    Timeout,
    /// <summary>Проходка упёрлась: лава, вода, нечем копать.</summary>
    Stopped
}

/// <summary>Итог разведки.</summary>
public sealed record ProspectReport(
    ProspectStop Stop,
    string Reason,
    IReadOnlyList<OreFind> Found,
    int ShaftDepth,
    int DriftsDug,
    int CellsDug,
    double Seconds)
{
    public bool Success => Stop == ProspectStop.Found;

    /// <summary>
    /// ПОМЕХА ОБЩИМ СЛОВОМ: пройдёт ли она сама (<see cref="Setback"/>).
    ///
    /// Не вторая копия <see cref="Stop"/>, а ответ на ДРУГОЙ вопрос — тот же
    /// разбор, что у дороги (<see cref="RoadReport.Hindrance"/>). Разведку
    /// зовёт наряд посреди добычи (<c>MiningOrder.FetchAsync</c>), и голый
    /// отказ разведки хоронил вместе с ней и весь наряд.
    /// </summary>
    public Setback Hindrance { get; init; } = Setback.None;

    public override string ToString() =>
        (Found.Count > 0
            ? $"нашёл: {string.Join("; ", Found.Take(4))}"
            : "не нашёл ничего") +
        $"; ствол {ShaftDepth} бл, штреков {DriftsDug}, прокопано {CellsDug} кл; " +
        $"{Seconds:0} с: {Reason}";
}

/// <summary>
/// РАЗВЕДКА: уйти под землю и искать руду там, где её не видно сверху.
///
/// Зачем. Всё, что умел рудокоп до сих пор, начиналось со слов «вижу руду».
/// А на ровном месте руды не видно вовсе: она в камне, и живой игрок в этом
/// случае бьёт ствол вниз и расходится штреками. Без этого «иди найди медь»
/// упиралось в честное, но бесполезное «меди на виду нет».
///
/// КАК КОПАЕТ ЧЕЛОВЕК. Ствол ведут не отвесно (из отвесного не выбраться), а
/// лесенкой — этим занимается <see cref="Tunnel"/>. На нужной глубине от
/// ствола расходятся штреки: каждый новый метр хода открывает новые стены, и
/// смотреть надо именно на них. Поэтому разведка не «ищет по памяти», а
/// спрашивает <see cref="Ores.Find"/> после каждого куска хода — то есть
/// находит руду ровно тогда, когда её увидел бы игрок.
///
/// Класс — механизм. На какую глубину идти, сколько штреков бить и ради чего,
/// решает роль.
/// </summary>
public class Prospecting
{
    private readonly BotContext ctx;

    public Prospecting(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    // ---------------- настройки ----------------

    /// <summary>
    /// До какой высоты опускаться. В большинстве миров интересное начинается
    /// заметно ниже поверхности; ноль — считать от <see cref="DepthBelowStart"/>.
    /// </summary>
    public int? TargetY { get; set; }

    /// <summary>На сколько блоков опуститься от места старта, если высота не задана.</summary>
    public int DepthBelowStart { get; set; } = 24;

    /// <summary>Длина одного штрека в клетках.</summary>
    public int DriftLength { get; set; } = 24;

    /// <summary>Сколько штреков бить с одного уровня.</summary>
    public int Drifts { get; set; } = 4;

    /// <summary>
    /// Через сколько клеток хода осматриваться.
    ///
    /// Число — политика роли («ОсматриватьсяЧерезКлеток»), и цена у него с двух
    /// сторон: чаще — дороже по времени (каждый осмотр перебирает клетки шара),
    /// реже — можно пройти мимо вскрытой жилы, потому что смотрят на стены
    /// ровно в этот миг, а не всю дорогу.
    /// </summary>
    public int LookEvery { get; set; } = 6;

    /// <summary>В каком радиусе замечать вскрытую руду.</summary>
    public int LookRadius { get; set; } = 10;

    /// <summary>
    /// ДАЛЬШЕ ЭТОГО НЕ СМОТРИМ ДАЖЕ С ЧИТОМ, блоков. Не физика, а защита от
    /// вписанной в настройку тысячи: осмотр перебирает клетки шара, и радиус
    /// 250 — это уже около 65 миллионов вопросов к модели мира. Замер по нашему
    /// же журналу: один безнадёжный расчёт маршрута — 1,67 млн вопросов и
    /// 155–206 мс, то есть на радиусе 250 один взгляд стоит секунды, а на
    /// тысяче — минуты, и бот просто встанет.
    ///
    /// Число 250 названо заказчиком: «дальность поиска руды с читом — до 250 и
    /// чтобы значение крутилось в настройках, а не было зашито».
    /// </summary>
    public const int MaxLookRadiusCheating = 250;

    /// <summary>
    /// НАСКОЛЬКО ДАЛЕКО СМОТРЕТЬ, КОГДА ВКЛЮЧЕНО ЗРЕНИЕ СКВОЗЬ СТЕНЫ. Честному
    /// боту хватает <see cref="LookRadius"/> — он ищет открытую жилу в стене.
    /// Читер видит руду в породе, и смотреть теми же десятью блоками значит не
    /// пользоваться читом вовсе. Действует ТОЛЬКО при включённом флаге.
    ///
    /// Крутится настройкой роли («ДальностьРудыСквозьСтены»), потолок —
    /// <see cref="MaxLookRadiusCheating"/>. Число за потолком не отвергается, а
    /// ПРИЖИМАЕТСЯ к нему: молча отбросить вписанное человеком значило бы
    /// оставить его думать, что бот смотрит на тысячу блоков.
    /// </summary>
    public int LookRadiusCheating
    {
        get => lookRadiusCheating;
        set => lookRadiusCheating = ClampLookRadius(value);
    }

    private int lookRadiusCheating = 48;

    /// <summary>
    /// Прижать дальность к разумным пределам — чистое правило, его и проверяет
    /// тест. Ноль и отрицательное — это не «не смотреть вовсе», а описка:
    /// смотреть надо всегда, иначе чит просто перестаёт работать молча.
    /// </summary>
    public static int ClampLookRadius(int wanted) =>
        Math.Clamp(wanted, 1, MaxLookRadiusCheating);

    /// <summary>Сколько секунд отводится на всю разведку.</summary>
    public double MaxSeconds { get; set; } = 900;

    /// <summary>
    /// Останавливаться на первой же подходящей находке.
    ///
    /// Решает роль («ОстанавливатьсяНаПервойНаходке»): наряду «принеси 20 меди»
    /// хватает первой жилы — дальше копать некогда; а разведке места под
    /// будущую шахту первая жила ничего не говорит, ей нужна вся картина, и
    /// выключенная остановка — это как раз она.
    /// </summary>
    public bool StopOnFirst { get; set; } = true;

    /// <summary>
    /// СНАЧАЛА ОБОЙТИ ОКРУГУ, потом копать.
    ///
    /// Заказчик поймал это живьём и сказал ровно так: «поиск чего-либо — это
    /// ходить по округе и искать, а не просто пытается в землю 1 блок копнуть
    /// и не видит меди». И он прав: разведка киркой в сто раз дороже разведки
    /// ногами, а руда выходит наружу сама — в обрывах, оврагах, устьях пещер,
    /// на осыпях. Живой игрок сперва обходит место, и только не найдя ничего,
    /// берётся за кирку.
    /// </summary>
    public bool WalkAround { get; set; } = true;

    /// <summary>Насколько далеко отходить от места старта при обходе.</summary>
    public int SweepRadius { get; set; } = 64;

    /// <summary>Сколько точек осмотра обойти.</summary>
    public int SweepStops { get; set; } = 12;

    /// <summary>
    /// Подбирать с земли куски самородной руды.
    ///
    /// Это не мелочь и не жадность: генератор мира кладёт россыпь самородков
    /// ИМЕННО НАД ЗАЛЕЖЬЮ (см. <see cref="Gathering.IsNuggetLitter"/>). То
    /// есть найденный на земле кусок меди — это и добыча, и указание, где
    /// копать. Обход запоминает такие места и роет ствол там, а не где стоял.
    /// </summary>
    public bool TakeNuggets { get; set; } = true;

    // ---------------- работа ----------------

    private BlockPos? Feet => ctx.Self.Position is { } p
        ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
        : null;

    /// <summary>Четыре стороны света: штреки расходятся по ним.</summary>
    private static readonly (int X, int Z)[] Ways = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    /// <summary>
    /// Искать руду под землёй: ствол вниз, потом штреки в стороны.
    /// </summary>
    /// <param name="metal">Что ищем: «медь», «олово», «» — любую руду.</param>
    public async Task<ProspectReport> SearchAsync(string metal = "",
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(MaxSeconds);
        var found = new List<OreFind>();
        int cells = 0, drifts = 0, depth = 0;
        // ПОСЛЕДНЯЯ ЖИВАЯ ПОМЕХА — та, что мешала, пока шли часы. Нужна ровно
        // сроку, чтобы он не встал на её место (Resume.TimeIsNotACause)
        var живая = Setback.None;

        // Новая разведка — новый разговор: прошлое «не вижу» пересказать надо
        сказаноПроОсмотр = "";

        if (Feet is not { } start)
            // «Не знаю, где стою» — это НЕДОЕХАВШИЙ МИР, а не свойство места:
            // своя сущность приходит уже после входа в мир. Помеха временная,
            // и повтор тут честен (то же слово и по той же причине у дороги —
            // Roads.PaveToAsync)
            return Report(ProspectStop.Stopped, "не знаю, где стою", found, 0, 0, 0, started,
                Setback.NotLoaded);
        if (ctx.Tunnel is not { } tunnel)
            // ПРОХОДКИ У БОТА НЕТ ВОВСЕ — это не помеха, а несобранный бот:
            // ждать нечего, и повтор ничего не переменит. Честного слова на
            // «меня собрали без рук» в словаре нет, и выдумывать его сюда
            // незачем: причина стоит рядом словами
            return Report(ProspectStop.Stopped, "проходка недоступна", found, 0, 0, 0, started);

        // МОЖЕТ, ИСКАТЬ И НЕ НАДО: вдруг руда уже на виду. Осмотр с места стоит
        // миллисекунды, а обход — минуты ходьбы, и смотреть надо ДО того, как
        // бот ушёл за девяносто блоков. Живой случай заказчика: «пробежал мимо
        // самородков меди и просто над медью 30 блоков, когда он с читами
        // виденья» — обход начинался сразу с дороги к первой точке спирали
        bool наВиду = Look(metal, found);
        if (наВиду && StopOnFirst)
            return Report(ProspectStop.Found,
                $"руда была на виду с самого начала (осмотр на " +
                $"{SightWord(SightRadius(), SeeingThroughStone)})",
                found, 0, 0, 0, started);

        // ---- обход округи ногами: самое дешёвое, что можно сделать ----
        var digHere = start;
        if (WalkAround)
        {
            // НАЙДЕННОЕ НА МЕСТЕ ОБХОД НЕ ОТМЕНЯЕТ. Здесь была прямая беда:
            // осмотр с места повторялся внутри обхода и на любой непустой
            // находке обход обрывался ПЕРВОЙ ЖЕ строкой — «руда нашлась не
            // сходя с места, обход не нужен», — не сделав ни одной из двенадцати
            // точек. При «ОстанавливатьсяНаПервойНаходке = false» это выбрасывало
            // весь дешёвый осмотр округи ногами: бот бил ствол там, где стоял,
            // — ровно то поведение, против которого обход и заводили
            if (наВиду)
                OnLog?.Invoke($"руда на виду есть ({found.Count} кл.), но браться за " +
                              "первую находку мне не велено — всё равно обхожу округу");
            var walked = await SweepAsync(start, metal, found, deadline, ct);
            if (found.Count > 0 && StopOnFirst)
                return Report(ProspectStop.Found, "нашёл на обходе, копать не пришлось",
                    found, 0, 0, 0, started);
            if (ct.IsCancellationRequested)
                return Report(ProspectStop.Cancelled, "отменено", found, 0, 0, 0, started);
            // Россыпь самородков лежит НАД залежью — ствол бьём там, а не где стояли
            if (walked is { } spot)
            {
                // Но не за чужим забором: россыпь под окном соседа — это
                // указание копать там, где копать нельзя
                if (ctx.Claims is { } owners && !owners.Suits(spot))
                {
                    OnLog?.Invoke($"россыпь в {spot} лежит у чужого — ствол там бить не стану");
                }
                else
                {
                    digHere = spot;
                    OnLog?.Invoke($"на обходе нашлась россыпь в {spot} — ствол бью здесь");
                    await ctx.Movement.TravelToAsync(spot, 120, ct);
                }
            }
        }

        start = Feet ?? digHere;

        // ГДЕ БЬЁМ СТВОЛ — тоже автопоиск, а значит тоже под правилом чужого.
        // Отойти перед началом (это делает роль) мало: обход мог увести
        // обратно, да и место старта роль выбирает не всегда
        if (ctx.Claims is { } claims2 && !claims2.Suits(start))
            // Чужая заявка: ждать её снятия бессмысленно, и слово на это в
            // общем словаре есть ровно одно
            return Report(ProspectStop.Stopped,
                $"копать тут нельзя: {claims2.Judge(start).Say()}", found, 0, 0, 0, started,
                Setback.NotMine);
        int targetY = TargetY ?? start.Y - DepthBelowStart;
        OnLog?.Invoke($"разведка: ствол с {start.Y} до {targetY}, потом {Drifts} штрека " +
                      $"по {DriftLength} кл" + (metal.Length > 0 ? $", ищу {metal}" : ""));

        // ---- ствол ----
        if (targetY < start.Y)
        {
            var shaft = await DigAsync(tunnel, new BlockPos(start.X, targetY, start.Z),
                metal, found, deadline, ct);
            cells += shaft.Cells;
            depth = start.Y - (Feet?.Y ?? start.Y);

            if (shaft.Stopped is { } why)
                // ЧЕМ ВСТАЛ СТВОЛ — СЛОВОМ САМОЙ ПРОХОДКИ, а не своим. Лаву,
                // осыпь и незаложенную воду разведка отличить не умеет и
                // гадать за проходку не станет: та уже назвала это общим
                // словом (Tunnel.WhatStopped), его и передаём наверх
                return Report(found.Count > 0 ? ProspectStop.Found : ProspectStop.Stopped,
                    $"ствол: {why}", found, depth, 0, cells, started, shaft.Hindrance);
            if (found.Count > 0 && StopOnFirst)
                return Report(ProspectStop.Found, "нашёл по дороге вниз", found, depth, 0, cells, started);
        }

        // ---- штреки ----
        var hub = Feet ?? start;
        for (int i = 0; i < Drifts; i++)
        {
            if (ct.IsCancellationRequested)
                return Report(ProspectStop.Cancelled, "отменено", found, depth, drifts, cells, started);
            if (DateTime.UtcNow > deadline)
                // СРОК ДОКЛАДЫВАЕТ ПОСЛЕДНЮЮ ЖИВУЮ ПОМЕХУ. Разведка живёт
                // ПОЛОВИНОЙ остатка наряда: её часы кончаются, когда у наряда
                // времени ещё вдоволь, — и «вышло время разведки» прятало бы
                // и лаву в стволе, и игрока на клетке
                return Report(found.Count > 0 ? ProspectStop.Found : ProspectStop.Timeout,
                    $"вышло время разведки ({MaxSeconds:0} с)", found, depth, drifts, cells,
                    started, живая);

            var (dx, dz) = Ways[i % Ways.Length];
            var end = new BlockPos(hub.X + dx * DriftLength, hub.Y, hub.Z + dz * DriftLength);
            OnLog?.Invoke($"штрек {i + 1} из {Drifts}: к {end}");

            var drift = await DigAsync(tunnel, end, metal, found, deadline, ct);
            cells += drift.Cells;
            drifts++;
            // Помеху штрека держим ЖИВОЙ до конца разведки: штрек оборвался,
            // разведка пошла в другую сторону — но если следом кончится срок,
            // назвать надо то, что мешало на самом деле
            if (drift.Hindrance != Setback.None)
                живая = drift.Hindrance;

            if (found.Count > 0 && StopOnFirst)
                return Report(ProspectStop.Found, $"нашёл в штреке {drifts}",
                    found, depth, drifts, cells, started);
            if (drift.Stopped is { } why)
                OnLog?.Invoke($"штрек {drifts} оборвался: {why} — беру другое направление");

            // Возвращаемся к устью, чтобы следующий штрек шёл от него, а не
            // от конца предыдущего: иначе выходит не звезда, а змея
            if (i + 1 < Drifts && !await ctx.Movement.TravelToAsync(hub, 90, ct))
            {
                OnLog?.Invoke("обратно к устью не дошёл — дальше штреки бить неоткуда");
                break;
            }
        }

        return Report(found.Count > 0 ? ProspectStop.Found : ProspectStop.Nothing,
            found.Count > 0 ? "нашёл" : "прошёл весь план и ничего не встретил",
            found, depth, drifts, cells, started);
    }

    /// <summary>
    /// ОБХОД: пройти по округе и посмотреть по сторонам.
    ///
    /// Точки осмотра идут раскручивающейся спиралью: сперва ближние, потом
    /// дальние. Так бот сначала обшаривает то, что рядом (и куда быстро
    /// вернуться), и только потом уходит.
    ///
    /// Возвращает место, где стоит бить ствол, — россыпь самородков на земле.
    /// Такая россыпь в игре лежит НАД залежью, то есть это не просто добыча,
    /// а указание, где копать. Нет россыпи — null, и ствол бьётся там, где
    /// стояли.
    /// </summary>
    private async Task<BlockPos?> SweepAsync(BlockPos start, string metal,
        List<OreFind> found, DateTime deadline, CancellationToken ct)
    {
        BlockPos? litterAt = null;
        int stops = Math.Max(1, SweepStops);
        int looked = 0, picked = 0, skipped = 0;

        OnLog?.Invoke($"обхожу округу: {stops} точек в радиусе {SweepRadius} бл" +
                      (ctx.Claims is { KeepAway: > 0 } ? "; точки у чужого пропускаю" : ""));

        // ПОД НОГАМИ УЖЕ ПОСМОТРЕЛИ — это делает SearchAsync ДО захода сюда, и
        // делает всегда. Второй такой же осмотр стоял здесь и обрывал обход на
        // первой же непустой находке: «руда нашлась не сходя с места — обход не
        // нужен», ноль пройденных точек из двенадцати. Решать, хватит ли
        // найденного, — дело зовущего (он один знает про
        // «ОстанавливатьсяНаПервойНаходке»), а дело обхода — ХОДИТЬ

        for (int i = 0; i < stops; i++)
        {
            if (ct.IsCancellationRequested || DateTime.UtcNow > deadline)
                break;

            // Спираль: угол раскручивается полтора оборота, радиус растёт
            double angle = 2 * Math.PI * i * 1.5 / stops;
            double r = SweepRadius * (0.35 + 0.65 * i / Math.Max(1, stops - 1));
            int x = start.X + (int)Math.Round(r * Math.Cos(angle));
            int z = start.Z + (int)Math.Round(r * Math.Sin(angle));
            int y = ctx.World.NearestSupportY(x, start.Y, z, up: 24, down: 24) ?? start.Y;
            var stop = new BlockPos(x, y, z);

            // ЧУЖОЕ ПРОВЕРЯЕТСЯ У КАЖДОЙ ТОЧКИ, А НЕ ОДИН РАЗ ПЕРЕД ВЫХОДОМ.
            // Живой случай: бот честно отошёл на 500 бл от соседской заявки и
            // начал разведку — а спираль обхода на 64 бл уводила его обратно к
            // забору, где он и копал. Настройка «держаться 500» при этом
            // показывала 500 и выглядела работающей
            if (ctx.Claims is { } claims && !claims.Suits(stop))
            {
                skipped++;
                continue;
            }

            // На каждую точку — своя доля времени, чтобы одна недостижимая
            // не съела весь обход
            double slice = Math.Max(15, (deadline - DateTime.UtcNow).TotalSeconds / (stops - i));
            if (!await ctx.Movement.TravelToAsync(stop, slice, ct))
                OnLog?.Invoke($"до точки осмотра {i + 1} не дошёл — смотрю оттуда, куда дошёл");
            looked++;

            // 1. Что видно глазами. Спрашиваем сам осмотр: он и отвечает
            // «прибавилось ли НОВОЕ», а не «есть ли хоть что-то в списке»
            if (Look(metal, found))
            {
                OnLog?.Invoke($"на обходе увидел: {found[^1]}");
                if (StopOnFirst)
                    return litterAt;
            }

            // 2. Что лежит под ногами
            if (TakeNuggets && ctx.Gathering is { RegistryReady: true } gathering)
            {
                foreach (var loose in gathering.FindLoose(LookRadius, limit: 8))
                {
                    if (ct.IsCancellationRequested)
                        break;
                    if (!gathering.IsNuggetLitter(loose.Code))
                        continue;
                    // Россыпь не того металла тоже полезна как добыча, но
                    // местом для ствола её считать нельзя
                    bool ours = metal.Length == 0 ||
                                loose.Drops.Any(d => d.Contains(metal, StringComparison.OrdinalIgnoreCase)) ||
                                OreCode.MetalGroups.Any(g =>
                                    g.Name.Equals(metal, StringComparison.OrdinalIgnoreCase) &&
                                    g.Minerals.Any(m => loose.Drops.Any(d =>
                                        d.Contains(m, StringComparison.OrdinalIgnoreCase))));

                    if (await gathering.PickUpAsync(loose.Pos, ct))
                    {
                        picked++;
                        if (ours)
                            litterAt ??= loose.Pos;
                    }
                }
            }
        }

        OnLog?.Invoke($"обход: осмотрено точек {looked}, подобрано с земли {picked}" +
                      (skipped > 0 ? $", пропущено у чужого {skipped}" : "") +
                      (found.Count > 0 ? $", видно руды {found.Count}" : ", руды на виду нет") +
                      (litterAt is { } at ? $"; россыпь в {at}" : ""));
        return litterAt;
    }

    /// <summary>
    /// Прокопать один кусок и смотреть по сторонам по дороге.
    ///
    /// <c>Hindrance</c> — ПОМЕХА СЛОВОМ САМОЙ ПРОХОДКИ (<see cref="Tunnel.WhatStopped"/>),
    /// а не пересказом её строки. Пересказывать было нечем: «лава в (…)» и
    /// «вода не унялась» отличаются от «тело не прошло» только буквами, а для
    /// возврата это разница между «жди и берись заново» и «дальше убьёт».
    /// </summary>
    private async Task<(int Cells, string? Stopped, Setback Hindrance)> DigAsync(Tunnel tunnel,
        BlockPos to, string metal, List<OreFind> found, DateTime deadline, CancellationToken ct)
    {
        int wasSteps = tunnel.MaxSteps;
        double wasSeconds = tunnel.MaxSeconds;
        bool wasStop = tunnel.StopWhenInReach;
        bool wasBreak = tunnel.BreakTarget;
        var wasHook = tunnel.OnInteresting;
        var wasInteresting = tunnel.InterestingBy;

        int cells = 0;
        string? stopped = null;
        var помеха = Setback.None;
        try
        {
            tunnel.MaxSteps = Math.Max(DriftLength, DepthBelowStart) * 3;
            tunnel.MaxSeconds = Math.Max(30, (deadline - DateTime.UtcNow).TotalSeconds);
            // Цель разведки — не «дойти», а «увидеть»: до конечной точки хода
            // дотягиваться незачем.
            tunnel.StopWhenInReach = false;
            // А вот последнюю клетку ломать НАДО. Обычно её берегут — там
            // стоит руда, которую разобьют вслепую. Здесь же конец штрека —
            // это просто точка в камне, и не сломав её, ход упирался бы в
            // собственную цель и докладывал «тело в неё не идёт»
            tunnel.BreakTarget = true;

            // ЧТО СЧИТАТЬ ИНТЕРЕСНЫМ — РЕШАЕТ РЕЕСТР, а не приставка кода в
            // проходке. Пока там стояло «ore-», вскрытый в стене метеорит
            // (meteorite-iron) разведке не сообщался вовсе
            tunnel.InterestingBy = ctx.Ores.IsOre;

            // ГЛАВНОЕ МЕСТО. Проходка сама сообщает, что вскрыла в стенах, —
            // и разведке остаётся только решить, то ли это, что искали
            tunnel.OnInteresting = (pos, code, _) =>
            {
                var (m, rich) = Ores.Describe(code);
                var find = new OreFind(pos, code, m, rich, 0);
                if (ctx.Ores.Suits(code, metal) && !found.Any(f => f.Pos == pos))
                {
                    found.Add(find);
                    OnLog?.Invoke($"вскрыл {m} ({rich}) в {pos}");
                }
                return Task.CompletedTask;
            };

            var dug = await tunnel.DigToAsync(to, ct);
            cells = dug.Steps;
            if (!dug.Success)
            {
                stopped = dug.Reason;
                // Слово проходки берём КАК ЕСТЬ: что с ним делать, решает одно
                // чистое правило (WhatStopped), а не это место
                помеха = dug.Hindrance;
            }
        }
        finally
        {
            tunnel.MaxSteps = wasSteps;
            tunnel.MaxSeconds = wasSeconds;
            tunnel.StopWhenInReach = wasStop;
            tunnel.BreakTarget = wasBreak;
            tunnel.OnInteresting = wasHook;
            tunnel.InterestingBy = wasInteresting;
        }

        // Плюс обычный осмотр: ход мог пройти рядом с уже открытой пещерой,
        // и руда в её стене видна не хуже, чем в своей
        Look(metal, found);
        return (cells, stopped, помеха);
    }

    /// <summary>
    /// НА СКОЛЬКО БЛОКОВ БОТ СМОТРИТ СЕЙЧАС — одно место на весь осмотр.
    ///
    /// ЧИТУ — СВОЁ РАССТОЯНИЕ. Живой случай заказчика: «медь не нашёл с читами,
    /// это шутка? я проверил, рядом с домом есть». Зрение сквозь стены было
    /// включено, а смотрел бот на те же 10 бл, что и честный, — а честному эти
    /// 10 бл нужны, чтобы разглядеть жилу в стене шахты. Весь смысл чита в том,
    /// чтобы видеть руду в породе на расстоянии, иначе он ничего не меняет.
    /// Слова заказчика: «если включен чит, мы же ищем ее эффективней, чем
    /// просто находим самородки».
    ///
    /// Отдельным методом, а не выражением внутри <see cref="Look"/>, ровно
    /// затем, чтобы ручку <see cref="LookRadiusCheating"/> можно было проверить
    /// тестом: приёмка нашла, что потолок у неё проверен, а ПОЛЬЗУЕТСЯ ли ею
    /// кто-нибудь — нет. Читерское зрение молча возвращалось к честным 10 бл, и
    /// заказ «до 250 настройкой» отчитывался выполненным.
    /// </summary>
    public int SightRadius() => SightRadius(LookRadius);

    /// <summary>
    /// ТО ЖЕ ПРАВИЛО ДЛЯ ЧУЖОГО ЧЕСТНОГО РАДИУСА.
    ///
    /// Нужно потому, что смотрит на руду не одна разведка: наряд
    /// (<see cref="MiningOrder.SearchRadius"/>) сначала берёт то, что видно, и
    /// только не найдя — зовёт разведку. Живой журнал заказчика начинался
    /// именно с наряда: «медь на виду нет — ухожу в разведку», и это при
    /// включённом чите. Наряд про читерскую дальность не знал вовсе и смотрел
    /// на свои честные 32 бл, потому что ручка жила только здесь.
    ///
    /// Второго такого правила заводить нельзя: тогда «ДальностьРудыСквозьСтены»
    /// значила бы у наряда и у разведки разное, и объяснить это человеку было
    /// бы нечем.
    /// </summary>
    /// <param name="honest">На сколько блоков смотрит честный бот в этом деле.</param>
    public int SightRadius(int honest) =>
        ctx.Cheats.On(ctx.Cheats.SeeThroughWalls, nameof(Cheats.SeeThroughWalls))
            ? Math.Max(honest, LookRadiusCheating)
            : honest;

    /// <summary>Смотрим ли мы прямо сейчас сквозь камень — для журнала.</summary>
    public bool SeeingThroughStone =>
        ctx.Cheats.On(ctx.Cheats.SeeThroughWalls, nameof(Cheats.SeeThroughWalls));

    /// <summary>
    /// НА СКОЛЬКО И ЧЕМ СМОТРЕЛИ — ОДНИМИ СЛОВАМИ У ВСЕХ, КТО СМОТРИТ.
    ///
    /// Заказчику обещали проверку живьём: «в журнале обязана появиться строка
    /// “осмотр на 48 бл СКВОЗЬ КАМЕНЬ (чит)…”». А смотрит первым НАРЯД
    /// (<see cref="MiningOrder"/>), и он говорил своими словами, без дальности
    /// и без единого слова про чит: с включённым читом бот сразу находил медь
    /// и обещанной строки человек не видел никогда — то есть проверка,
    /// выданная ему на руки, была невыполнима.
    ///
    /// Слова живут здесь, а не по копии у каждого смотрящего: разойдись копии —
    /// и человек прочёл бы в журнале два разных описания одного и того же
    /// взгляда. Статический нарочно: наряд обязан говорить так же и тогда,
    /// когда разведка боту вовсе не подключена.
    /// </summary>
    public static string SightWord(int radius, bool throughStone) =>
        throughStone ? $"{radius} бл СКВОЗЬ КАМЕНЬ (чит)" : $"{radius} бл";

    /// <summary>
    /// ЧТО СКАЗАЛ ПРО ОСМОТР В ПРОШЛЫЙ РАЗ — чтобы не повторяться.
    ///
    /// Осмотр случается после каждых <see cref="LookEvery"/> клеток хода, и
    /// строчка на каждый был бы не журналом, а метелью. Но и молчать нельзя:
    /// заказчик включил чит и не смог по журналу понять, действует ли он —
    /// строки про осмотр не было ВООБЩЕ НИ ОДНОЙ. Поэтому говорим тогда, когда
    /// картина сменилась.
    /// </summary>
    private string сказаноПроОсмотр = "";

    private void СказатьПроОсмотр(string слова)
    {
        if (сказаноПроОсмотр == слова)
            return;
        сказаноПроОсмотр = слова;
        OnLog?.Invoke(слова);
    }

    /// <summary>
    /// Осмотреться: НАШЁЛ ЛИ ЧТО-ТО НОВОЕ. Найденное дописывается в
    /// <paramref name="found"/>, повторы не кладутся.
    ///
    /// ОТВЕТ ИМЕННО ПРО НОВОЕ, а не «есть ли хоть что-то в списке», и это не
    /// придирка к слову. Список ходит по рукам: его наполняет осмотр с места,
    /// потом с ним же идут в обход, потом в ствол и в штреки. Пока ответом было
    /// «список не пуст», обход, получив непустой список, отвечал «руда нашлась
    /// не сходя с места — обход не нужен» и возвращался, не сделав ни одной из
    /// двенадцати точек: находка ПРОШЛОГО осмотра выглядела находкой этого.
    ///
    /// Открыто наружу нарочно: это единственный, кто спрашивает
    /// <see cref="SightRadius()"/>, и проверять шов «ручка → взгляд» больше
    /// негде — сама разведка без сервера не ходит.
    /// </summary>
    public bool Look(string metal, List<OreFind> found)
    {
        int radius = SightRadius();
        int было = found.Count;

        // СПРАШИВАЕМ ИМЕННО ТО, ЧТО ИЩЕМ. Раньше здесь стояло пустое слово —
        // «любую руду», — и отбор шёл уже после. С читом запас выборки забирала
        // ближняя кварцевая жила, а искомая медь до отбора не доходила (см.
        // Ores.Find). Это и был живой случай «меди на виду нет» с читом
        foreach (var ore in ctx.Ores.Find(radius, metal, limit: 12))
        {
            if (!ctx.Ores.Suits(ore.Code, metal) || found.Any(f => f.Pos == ore.Pos))
                continue;
            found.Add(ore);
        }

        string чем = SightWord(radius, SeeingThroughStone);
        string что = metal.Length > 0 ? metal : "руды";
        СказатьПроОсмотр(found.Count > было
            ? $"осмотр на {чем}: вижу {что} — ближняя {found[было]}"
            : $"осмотр на {чем}: {что} не вижу");

        return found.Count > было;
    }

    /// <summary>
    /// ПОМЕХА ВРЕМЕННАЯ ИЛИ ПОСТОЯННАЯ — СЛОВАМИ РАЗВЕДКИ, ПЕРЕВЕДЁННЫМИ В
    /// ОБЩИЕ (<see cref="Setback"/>). ЧИСТОЕ ПРАВИЛО.
    ///
    /// ЗАЧЕМ ПЕРЕВОД, А НЕ ОДИН СЛОВАРЬ НА ВСЕХ — разобрано у дороги
    /// (<see cref="Roads.WhatStopped"/>).
    ///
    /// ЧЕМ ЭТОТ СЛОВАРЬ ОСОБЕННЫЙ. <see cref="ProspectStop.Stopped"/> —
    /// самое общее слово разведки: под ним и «не знаю, где стою», и «копать
    /// тут нельзя, чужое», и всё, чем встала проходка. Решать по нему одному
    /// значило бы либо ждать у чужого забора трижды, либо бросать разведку
    /// из-за непришедшей своей сущности. Поэтому уточняет его тот, кто помеху
    /// и видел, — ровно как у дороги «Blocked» уточняется словом клетки.
    /// </summary>
    /// <param name="lastLive">
    /// Что помешало на самом деле — словом проходки
    /// (<see cref="Tunnel.WhatStopped"/>) или своим.
    /// </param>
    public static Setback WhatStopped(ProspectStop stop, Setback lastLive = Setback.None)
    {
        // ЧУЖОЕ ОБЕЩАНИЕ НАВЕРХ НЕ ПЕРЕДАЁМ. Setback.Unfinished значит «свою
        // долю я сделал», и на этом обещании стоит обнуление счёта пустых
        // возвратов (см. Resume.RunAsync). Ход, упёршийся в свой предел длины,
        // обещал это ЗА СЕБЯ — а прочтёт слово наряд, позвавший разведку, и
        // держать обещание будет не он: наряд с нулём в сумке закрутил бы себя
        // в круг до конца собственного срока. Для разведки же упёршийся ход —
        // обычное дело: этот предел она сама ему и ставит
        if (lastLive == Setback.Unfinished)
            lastLive = Setback.None;

        return stop switch
        {
            ProspectStop.Found => Setback.None,
            ProspectStop.Cancelled => Setback.Stopped,

            // СРОК ДОКЛАДЫВАЕТ ПОМЕХУ, А НЕ ВСТАЁТ НА ЕЁ МЕСТО. У разведки
            // это особенно дорого: наряд даёт ей ПОЛОВИНУ своего остатка
            // (MiningOrder.FetchAsync, forSearch), то есть её срок кончается,
            // когда у наряда времени ещё вдоволь
            ProspectStop.Timeout => Resume.TimeIsNotACause(lastLive),

            // Самое общее слово — уточняем его тем, кто помеху видел
            ProspectStop.Stopped => lastLive,

            // «ИСКАЛ ЧЕСТНО И НЕ НАШЁЛ» — ЧЕСТНОГО ИМЕНИ НЕТ. Прошли весь
            // план: ствол на глубину, четыре штрека, обход округи — и ничего.
            // Ждать бесполезно (руда не прорастёт), но и «бросай» неправда:
            // правильный ответ — «поищи в другом месте», а такого ответа в
            // словаре нет вовсе. NoRoute сюда натягивать нельзя (дорога-то
            // есть, бот её сам и прокопал), OutOfReach — тем более (дотянулся
            // до каждой стены, просто руды в ней не было). Отдаём «причину не
            // назвали»: сама причина стоит словами в Reason — «прошёл весь
            // план и ничего не встретил»
            ProspectStop.Nothing => Setback.None,

            _ => Setback.None
        };
    }

    private ProspectReport Report(ProspectStop stop, string reason, List<OreFind> found,
        int depth, int drifts, int cells, DateTime started, Setback live = Setback.None)
    {
        var report = new ProspectReport(stop, reason, found, depth, drifts, cells,
            (DateTime.UtcNow - started).TotalSeconds)
        {
            // Помеху общим словом считает чистое правило, и считает ЗДЕСЬ — в
            // одном месте на все десять выходов из разведки
            Hindrance = WhatStopped(stop, live)
        };
        OnLog?.Invoke(report.ToString());
        return report;
    }
}
