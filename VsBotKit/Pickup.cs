namespace VsBotKit;

/// <summary>Вещь, лежащая на земле ОТДЕЛЬНОЙ СУЩНОСТЬЮ (то, что выпало).</summary>
/// <param name="Id">Сущность: по ней следят, исчезла ли вещь.</param>
/// <param name="Mine">Выпало из нас самих (сервер помечает byPlayerUid).</param>
/// <param name="ByPlayer">Вещь выброшена ИГРОКОМ (нашим или чужим) — у таких есть секунда неприкосновенности.</param>
public readonly record struct Drop(long Id, string Code, int Count,
    double X, double Y, double Z, double Distance, bool Mine, bool ByPlayer);

/// <summary>Почему подбор закончился.</summary>
public enum PickupStop
{
    /// <summary>Вокруг чисто.</summary>
    Done,
    /// <summary>Сумки полны — дальше собирать некуда.</summary>
    BagsFull,
    /// <summary>Кончилось отпущенное время.</summary>
    NoTime,
    /// <summary>Остались вещи, до которых не дойти (упало в яму, за стену, в воду).</summary>
    Unreachable,
    /// <summary>Тело забрали (рефлекс важнее) или отменили.</summary>
    Stopped,
}

/// <param name="Items">Сколько куч подобрано.</param>
/// <param name="Units">Сколько штук всего.</param>
/// <param name="Left">Сколько куч осталось лежать.</param>
public record PickupResult(int Items, int Units, int Left, PickupStop Stop, string Why,
    IReadOnlyList<string> Taken)
{
    public bool Any => Items > 0;

    public override string ToString() => Items == 0
        ? $"не подобрал ничего: {Why}"
        : $"подобрал {Units} шт. ({string.Join(", ", Taken.Distinct())})" +
          (Left > 0 ? $", осталось лежать {Left}: {Why}" : "");
}

/// <summary>
/// Подбор выпавшего. ОДИН на всё: после копки, после рубки, после разделки,
/// после смерти, за брошенной едой, по команде.
///
/// Почему это вообще нужно отдельно. Сломать блок и получить его — РАЗНЫЕ
/// события: игра роняет добычу на землю отдельной сущностью, и пока по ней
/// не пройти ногами, она так и лежит. Живьём это видно было в отчёте карьера:
/// «сломано 9, получено: ничего». Бот исправно копал и уходил без добычи.
///
/// Как это устроено в самой игре (EntityBehaviorCollectEntities, проверено
/// по коду игры):
/// <list type="bullet">
/// <item>сервер сам кладёт вещь в сумку, когда игрок РЯДОМ — слать нечего;</item>
/// <item>«рядом» — это <b>1,5 блока</b> от СЕРЕДИНЫ тела, и по вертикали тоже:
/// вещь на дне ямы с кромки не достать, надо спуститься;</item>
/// <item>у вещей, выброшенных игроком, есть <b>секунда</b> неприкосновенности —
/// брошенную еду сразу не подобрать, надо постоять;</item>
/// <item>мёртвый не подбирает, и сразу после возрождения — тоже (щит от урона).</item>
/// </list>
/// Есть и ловушка на будущее: настройка игрока ItemCollectMode (пакет 32)
/// со значением 1 означает «подбирать только в присяде». Бот её не шлёт,
/// поэтому подбор у него всегда самоходный — но если роль вдруг начнёт слать
/// пакет 32, подбор молча умрёт. Отсюда и комментарий.
/// </summary>
public class Pickup
{
    private readonly BotContext ctx;

    /// <summary>Места, где мы что-то сломали и добыча должна лежать.</summary>
    private readonly List<(BlockPos At, DateTime When)> noted = [];
    private readonly Lock notedLock = new();

    /// <summary>
    /// Вещи, до которых не дошли, и почему. Память ОБЩАЯ, а не на один заход:
    /// иначе способность каждые три секунды снова идёт к той же кучке за
    /// стеной. Живьём это и выглядело как «бот бесцельно бегает».
    ///
    /// СЧЁТ ЗДЕСЬ НЕ СВОЙ. Ровно это правило («раз не вышло — не долби каждый
    /// раз, но и не забывай навсегда») уже написано механизмом
    /// <see cref="Hopeless{TKey}"/>, и своя копия жила тут только потому, что
    /// ключом ему когда-то могла быть одна строка. Теперь ключ любой, и копий
    /// не осталось: тем же механизмом помнит неудачи добыча с убитых
    /// (<c>BehaviorLootKills</c>) и цепочка снабжения (<c>Stock</c>).
    ///
    /// Молчит он тут нарочно: про пропущенную вещь бот и так говорит своими
    /// словами через <see cref="OnSkipped"/>, и вторых слов на то же самое не
    /// нужно — поэтому на <c>OnSay</c> здесь никто не подписан.
    /// </summary>
    private readonly Hopeless<long> gaveUp = new("вещь на земле");

    public Pickup(BotContext ctx)
    {
        this.ctx = ctx;
        // Срок безнадёги задаётся полем выше, а у механизма своё умолчание
        // (десять минут — мерка снабжения). Ставим своё сразу: иначе первая
        // же недоступная вещь закрылась бы не на две минуты, а на десять,
        // и никто бы этого не заметил
        gaveUp.TryAgainAfter = TimeSpan.FromSeconds(retryFailedSeconds);
    }

    /// <summary>В каком радиусе искать выпавшее, если не сказано иное.</summary>
    public double Radius { get; set; } = 12;

    /// <summary>
    /// Насколько выше или ниже нас вещь ещё «наша». Кучка этажом ниже — не
    /// ближайшая, а чужая: за ней бот полезет через полкарты.
    /// </summary>
    public double MaxHeightDiff { get; set; } = 4;

    /// <summary>
    /// Сколько ждать у вещи. Игрокское выпавшее держит секунду
    /// неприкосновенности, поэтому ждём с запасом.
    /// </summary>
    public double WaitAtDropSeconds { get; set; } = 1.8;

    /// <summary>
    /// Сколько стоять у вещи, выпавшей ИЗ БЛОКА. Неприкосновенности у неё нет
    /// (её даёт только EntityItem.CanCollect брошенному ИГРОКОМ), поэтому здесь
    /// ждать нужно лишь мгновение — пока сервер прокрутит свой тик сбора.
    /// Ждать полторы секунды у каждого куска руды значит терять минуты за карьер.
    /// </summary>
    public double WaitAfterWalkSeconds { get; set; } = 0.6;

    /// <summary>
    /// СКОЛЬКО СТОЯТЬ У ЭТОЙ ВЕЩИ — одно правило на оба пути подбора (обход и
    /// «подбери вот это»). Раньше эта строчка была написана дважды, слово
    /// в слово, и разъехаться им ничего не мешало.
    /// </summary>
    public double WaitSecondsFor(in Drop drop) =>
        drop.ByPlayer ? WaitAtDropSeconds : WaitAfterWalkSeconds;

    /// <summary>
    /// ЧАСЫ ПОДБОРА И ЕГО ЖЕ ВЫДЕРЖКА — весь его отсчёт времени идёт ЧЕРЕЗ ЭТУ
    /// ПАРУ и больше ниоткуда. По умолчанию это настоящие часы и настоящая
    /// пауза, то есть живой бот ничего об этой паре не знает.
    ///
    /// ЗАЧЕМ ЗАВЕДЕНО (живой случай — из прогона самого набора). Проверка
    /// «у выпавшего из блока бот лишнего не стоит» мерила простой СЕКУНДОМЕРОМ
    /// МАШИНЫ: запускала подбор и смотрела, сколько прошло. Под нагрузкой (в
    /// прогоне их восемь разом) выдержка в 0,6 с превращалась в 1,4 и 1,6 —
    /// не потому, что бот стоял, а потому, что машине было не до него. Набор,
    /// который иногда врёт, однажды спрячет настоящую поломку.
    ///
    /// Спросить надо СОБСТВЕННЫЙ ОТСЧЁТ ПОДБОРА: сколько он сам себе отмерил и
    /// пережил. Тот же приём уже стоит в проекте у памяти транслокаторов, у
    /// значков предметов и у самих транслокаторов — второго способа спрашивать
    /// время здесь нарочно не заводится.
    /// </summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Чем подбор пережидает — см. <see cref="Now"/>: часы и выдержка ходят
    /// парой, иначе подменивший одно получил бы вечный цикл.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; set; } =
        (span, ct) => Task.Delay(span, ct).ContinueWith(_ => { });

    /// <summary>
    /// Как часто, стоя у вещи, спрашивать сумку. Ответ приходит пакетом, и
    /// чаще опрашивать бессмысленно, а реже — значит стоять дольше нужного.
    /// </summary>
    private static readonly TimeSpan Опрос = TimeSpan.FromMilliseconds(60);

    /// <summary>Сколько секунд отводится на дорогу к одной вещи.</summary>
    public double WalkSeconds { get; set; } = 12;

    /// <summary>
    /// Насколько близко подходить. Сервер собирает в 1,5 бл, поэтому «почти
    /// вплотную» — это про запас, а требовать 0,4 бессмысленно: над вещью,
    /// лежащей на клетку ниже, такой точки просто нет, и бот кружит впустую.
    /// </summary>
    public double StopDistance { get; set; } = 0.9;

    /// <summary>
    /// ЗОНА СБОРА — ЭТО ЦИЛИНДР ИГРЫ, А НЕ НАШ ШАР. Горизонтальный радиус,
    /// блоков (<c>horRange</c> у самой игры).
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 19.08, 18:35:27), из-за которого правило
    /// переписано. Бот вернулся за вещами после смерти, встал вплотную к
    /// рюкзакам и сказал:
    ///   [подбор] не взял backpack-sturdy: не дойти
    ///   [движение] ближе не подойти: по полу 0,9 бл, по высоте −0,1 бл —
    ///              ни шагом, ни маршрутом
    /// То есть рюкзаки лежали ПОД НОГАМИ, а бот считал, что до них не достал,
    /// уходил — и терял всё, что нёс.
    ///
    /// ПОЧЕМУ ТАК ВЫХОДИЛО. Здесь мерился ШАР: √(0,9² + 1,025²) = 1,36 — это
    /// больше прежних 1,25, и вещь объявлялась недостижимой. А игра меряет
    /// иначе (VSEssentials 1.22.7, <c>EntityBehaviorCollectEntities</c>):
    ///   GetEntitiesAround(середина тела, horRange: 1.5, vertRange: 1.5)
    /// и дальше <c>EntityPos.InRangeOf</c> (VintagestoryAPI):
    ///   dx² + dz² ≤ horRange²  И  |Δy| ≤ vertRange
    /// Это ЦИЛИНДР: по горизонтали 1,5 и по высоте 1,5 отдельно. Вещь под
    /// ногами в него попадает всегда, а шар её терял ровно потому, что
    /// складывал горизонталь с высотой.
    ///
    /// Числа не наши и ручками роли не являются: их назначает игра, и разойтись
    /// с ней тут нельзя — сервер всё равно соберёт по-своему.
    /// </summary>
    public double CollectHorizontal { get; } = 1.5;

    /// <summary>Половина высоты зоны сбора, блоков (<c>vertRange</c> у игры).</summary>
    public double CollectVertical { get; } = 1.5;

    /// <summary>
    /// ДОСТАЁТ ЛИ СЕРВЕР ВЕЩЬ ОТСЮДА — чистое правило, слепок с игры.
    /// Сдвиги считаются от СЕРЕДИНЫ ТЕЛА до положения вещи: именно эту точку
    /// игра кладёт в <c>GetEntitiesAround</c>.
    /// </summary>
    /// <param name="dx">Сдвиг по X, блоков.</param>
    /// <param name="dy">Сдвиг по высоте, блоков (вещь ниже — отрицательный).</param>
    /// <param name="dz">Сдвиг по Z, блоков.</param>
    /// <param name="horizontal">Горизонтальный радиус зоны (<see cref="CollectHorizontal"/>).</param>
    /// <param name="vertical">Половина высоты зоны (<see cref="CollectVertical"/>).</param>
    public static bool WithinCollect(double dx, double dy, double dz,
        double horizontal, double vertical) =>
        dx * dx + dz * dz <= horizontal * horizontal && Math.Abs(dy) <= vertical;

    /// <summary>
    /// Высота середины тела над ногами, блоков.
    ///
    /// Игра берёт её так: <c>Pos.InternalY + SelectionBox.Y1 + SelectionBox.Y2 / 2</c>.
    /// У игрока низ коробки на земле, а рост — <see cref="BodySize.Height"/>,
    /// то есть середина ровно на половине роста. Прежние «0,9» были округлением
    /// от руки и в паре с шаром давали ту самую ошибку на вещи под ногами.
    /// </summary>
    public static double BodyMiddle => BodySize.Height / 2;

    /// <summary>
    /// Через сколько секунд снова пробовать вещь, до которой не дошли.
    /// Мир меняется — яму могли засыпать, — но не каждые три секунды.
    /// </summary>
    public double RetryFailedSeconds
    {
        get => retryFailedSeconds;
        set
        {
            retryFailedSeconds = value;
            gaveUp.TryAgainAfter = TimeSpan.FromSeconds(Math.Max(0, value));
        }
    }

    private double retryFailedSeconds = 120;

    /// <summary>Что подобрано (код и количество) — для журнала роли.</summary>
    public event Action<string, int>? OnPicked;

    /// <summary>Почему вещь пропущена: код и причина.</summary>
    public event Action<string, string>? OnSkipped;

    /// <summary>
    /// Запомнить: здесь мы что-то сломали, добыча должна лежать. Зовётся
    /// из <see cref="Mining.BreakAsync"/>, поэтому карьер, рубка, покос и
    /// поручения получают подбор бесплатно — ни строчки в них не меняется.
    /// </summary>
    public void Note(BlockPos at)
    {
        lock (notedLock)
        {
            // Одно место — одна пометка: карьер ломает клетки пачками, и
            // список не должен расти вместе с ними
            noted.RemoveAll(n => n.At.X == at.X && n.At.Y == at.Y && n.At.Z == at.Z);
            noted.Add((at, Now()));
            if (noted.Count > 64)
                noted.RemoveRange(0, noted.Count - 64);
        }
    }

    /// <summary>Есть ли неубранная добыча (для способности «сходи подбери»).</summary>
    public bool HasNotes
    {
        get { lock (notedLock) return noted.Count > 0; }
    }

    /// <summary>
    /// СКОЛЬКО МЕСТ ПОМЕЧЕНО. Нужно не для красоты: список ограничен 64
    /// местами, и если дедупликация в <see cref="Note"/> сломается, он начнёт
    /// расти на каждый удар карьера и вытеснит старые настоящие места. По
    /// одному «есть или нет» этого не увидеть — ни человеку, ни тесту.
    /// </summary>
    public int NoteCount
    {
        get { lock (notedLock) return noted.Count; }
    }

    /// <summary>Ближайшее место с добычей; вещи там могли и исчезнуть — это проверит подбор.</summary>
    public BlockPos? NextNote()
    {
        if (ctx.Self.Position is not { } me)
            return null;
        lock (notedLock)
        {
            if (noted.Count == 0)
                return null;
            return noted
                .OrderBy(n => (n.At.X - me.X) * (n.At.X - me.X) +
                              (n.At.Y - me.Y) * (n.At.Y - me.Y) +
                              (n.At.Z - me.Z) * (n.At.Z - me.Z))
                .First().At;
        }
    }

    /// <summary>
    /// РАЗОБРАНО ЛИ ПОМЕЧЕННОЕ МЕСТО — то есть можно ли забыть о нём.
    ///
    /// ЖИВОЙ СЛУЧАЙ, КОТОРЫЙ ОТКРЫЛА САМА ПОЧИНКА ХОДЬБЫ. Пометки разбираются
    /// по одной: взяли ближайшую, сходили, забыли. Пока
    /// <see cref="CollectAroundAsync"/> шла ДО проверки тела, на один отказ
    /// уходило до минуты ходьбы. Теперь отказ мгновенный — и безусловное
    /// забывание, стоявшее сразу за вызовом, стало выедать пометки со
    /// скоростью одной за <c>CooldownSeconds</c>: за длинный наряд («медь ×30»
    /// в живом журнале) бот вычистил бы память о КАЖДОМ месте, где ломал руду,
    /// ни к одному не подойдя. Это ровно та беда, ради которой пометки и
    /// заведены: «сломано 9, получено: ничего».
    ///
    /// ПРАВИЛО ОДНО: не ходили — не разбирали. <see cref="PickupStop.Stopped"/>
    /// и означает «тела не дали или забрали», то есть места мы не видели;
    /// всякий другой итог — это уже осмотр, пусть и неудачный («не дойти»,
    /// «кончилось время», «сумки полны»), и повторять его сразу же незачем.
    ///
    /// ЗДЕСЬ, А НЕ У ВЫЗЫВАЮЩЕГО: пометки — имущество подбора (см.
    /// <see cref="Note"/>, <see cref="NextNote"/>, <see cref="ForgetNote"/>),
    /// и решать их судьбу по своему же итогу должен он. Оставь мы это
    /// способности — второй, кто станет разбирать пометки, решит иначе, и
    /// разъехаться им будет нечему помешать.
    /// </summary>
    public static bool PlaceHandled(PickupStop stop) => stop != PickupStop.Stopped;

    /// <summary>
    /// СТОИТ ЛИ ОВЧИНКА ВЫДЕЛКИ — одно чистое правило на всю добычу: и на своё
    /// выпавшее, и на убитых. Возвращает причину отказа словами; null — идти
    /// можно.
    ///
    /// ПОЧЕМУ ЗДЕСЬ, А НЕ ДВАЖДЫ. Ровно этот же вопрос задавала себе добыча с
    /// убитых (<see cref="KillLootRules.Decide"/>), и слова у неё были свои. Две
    /// одинаковые мерки с разными словами — это будущее «почему за трупом он не
    /// идёт с двадцати блоков, а за рудой идёт с тридцати»; правило одно, а
    /// НАЗВАНИЕ ЦЕЛИ приходит доводом, потому что «до тела» и «до помеченного
    /// места» человек читает по-разному.
    ///
    /// Ноль и меньше — предела нет: «не ходить никуда» выражается снятой
    /// галочкой, а не числом со знаком.
    /// </summary>
    /// <param name="what">Куда идём, родительным падежом: «тела», «помеченного места».</param>
    public static string? TooFarToGo(string what, double distance, double limit) =>
        limit > 0 && distance > limit
            ? $"до {what} {distance:0.#} бл, а дальше {limit:0} я за добычей не хожу"
            : null;

    /// <summary>Забыть место (собрали или не смогли).</summary>
    public void ForgetNote(BlockPos at)
    {
        lock (notedLock)
            noted.RemoveAll(n => n.At.X == at.X && n.At.Y == at.Y && n.At.Z == at.Z);
    }

    /// <summary>Забыть все места (новое дело — старые хвосты не тянем).</summary>
    public void ForgetAllNotes()
    {
        lock (notedLock) noted.Clear();
    }

    /// <summary>
    /// Что выпавшее лежит вокруг точки. Вещи без разобранной стопки не
    /// показываются: сущность есть, а что в ней — сервер ещё не прислал.
    /// </summary>
    public List<Drop> Around(double x, double y, double z, double radius,
        Func<string, bool>? want = null, bool includeGivenUp = false)
    {
        var found = new List<Drop>();
        foreach (var e in ctx.Entities.Nearby(x, y, z, radius))
        {
            if (!e.IsItem)
                continue;
            if (!includeGivenUp && GaveUpOn(e.Id))
                continue;
            if (ctx.DroppedItemCode(e) is not { } code)
                continue;
            if (want != null && !want(code))
                continue;
            double dx = e.X - x, dy = e.Y - y, dz = e.Z - z;
            if (Math.Abs(dy) > MaxHeightDiff)
                continue;
            string? by = e.WatchedAttributes.GetString("byPlayerUid");
            found.Add(new Drop(e.Id, code, ctx.DroppedItemCount(e), e.X, e.Y, e.Z,
                Math.Sqrt(dx * dx + dy * dy + dz * dz),
                by == ctx.Bot.PlayerUid, by != null));
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found;
    }

    /// <summary>Что выпавшее лежит вокруг НАС.</summary>
    public List<Drop> Around(double? radius = null, Func<string, bool>? want = null,
        bool includeGivenUp = false) =>
        ctx.Self.Position is { } me
            ? Around(me.X, me.Y, me.Z, radius ?? Radius, want, includeGivenUp)
            : [];

    /// <summary>На этой вещи мы уже обожглись и пока к ней не идём.</summary>
    public bool GaveUpOn(long id) => gaveUp.Given(id);

    /// <summary>
    /// Почему мы на этой вещи обожглись (пусто — не обжигались). Причина
    /// хранится ТА ЖЕ САМАЯ, что сказана человеку через <see cref="OnSkipped"/>:
    /// раньше её вовсе не сохраняли, и «почему бот сюда не идёт» можно было
    /// узнать только вычитав старую строку журнала.
    /// </summary>
    public string WhyGaveUpOn(long id) => gaveUp.Why(id);

    private void GiveUpOn(long id, string why) => gaveUp.GiveUp(id, why);

    /// <summary>
    /// Пройтись по всему выпавшему вокруг и собрать.
    /// </summary>
    /// <param name="radius">Где искать; по умолчанию <see cref="Radius"/>.</param>
    /// <param name="want">
    /// Что брать (по коду). Роль решает сама: карьеру нужен камень и руда,
    /// а гнилую еду и грязь можно оставить.
    /// </param>
    /// <param name="onlyMine">Только своё — выпавшее из нас (могила, своя добыча).</param>
    /// <param name="maxSeconds">Общий предел: подбор не должен съедать весь день.</param>
    /// <param name="importance">
    /// С какой важностью занимать тело. Голодный за едой — рефлекс, добыча
    /// после копки — обычное дело, которое рефлекс имеет право перебить.
    /// </param>
    /// <param name="takeBody">
    /// Занимать ли тело. Тот, кто УЖЕ им владеет (карьер, поручение), обязан
    /// передать false: иначе подбор отберёт тело у собственного вызывающего,
    /// и тот продолжит работать с отменённым поручением. Живьём это выглядело
    /// как «The CancellationTokenSource has been disposed» посреди карьера.
    /// </param>
    public async Task<PickupResult> CollectAsync(double? radius = null,
        Func<string, bool>? want = null, bool onlyMine = false,
        double maxSeconds = 60, CancellationToken ct = default,
        BodyArbiter.Importance importance = BodyArbiter.Importance.Routine,
        string owner = "подбор", bool takeBody = true)
    {
        var taken = new List<string>();
        int items = 0, units = 0;
        // Вещи, до которых не дошли: второй раз к ним не идём, иначе бот
        // будет вечно бегать к одной и той же кучке за стеной
        var giveUp = new HashSet<long>();
        var deadline = Now().AddSeconds(maxSeconds);
        PickupStop stop = PickupStop.Done;
        string why = "вокруг чисто";

        using var hold = takeBody ? ctx.Turn.TryTake(owner, importance, ct) : null;
        // Не дали тело — значит НЕ ИДЁМ. Раньше подбор в этом случае шёл всё
        // равно и перетягивал бота с дороги: в дальнем походе это выглядело
        // как «прошёл 40 блоков вперёд и вернулся назад»
        if (takeBody && hold == null)
            return new PickupResult(0, 0, 0, PickupStop.Stopped, "тело занято другим делом", []);
        var token = hold?.Token ?? ct;

        while (!token.IsCancellationRequested)
        {
            if (Now() >= deadline)
            {
                stop = PickupStop.NoTime;
                why = $"вышло время ({maxSeconds:0} с)";
                break;
            }

            var around = Around(radius, want)
                .Where(d => !giveUp.Contains(d.Id) && (!onlyMine || d.Mine))
                .ToList();
            if (around.Count == 0)
                break;

            var drop = around[0];

            // Сумки. Идти за вещью, которую некуда положить, — это и есть
            // «бот бегает и ничего не делает»: сервер молча её не отдаст
            if (ctx.Self.FreeSlotFor(drop.Code, drop.Count) == null)
            {
                stop = PickupStop.BagsFull;
                why = $"сумки полны, {drop.Code} класть некуда";
                OnSkipped?.Invoke(drop.Code, "некуда положить");
                break;
            }

            int had = CountOf(drop.Code);
            var walk = await WalkOntoAsync(drop, deadline, token);

            await WaitTakenAsync(drop, had, token);

            // Честная проверка: вещь могла ИСЧЕЗНУТЬ, а не попасть к нам —
            // время жизни вышло, унесло водой, подобрал другой игрок.
            // Верим только сумке
            int now = CountOf(drop.Code);
            giveUp.Add(drop.Id);
            if (now > had)
            {
                items++;
                units += now - had;
                taken.Add(drop.Code);
                OnPicked?.Invoke(drop.Code, now - had);
                continue;
            }

            // Неудача запоминается НАДОЛГО: следующий заход не должен снова
            // упереться в ту же кучку. Но НЕ КОГДА КОНЧИЛОСЬ ВРЕМЯ: вещь в этом
            // не виновата, и закрывать её на две минуты за наш недобор секунд
            // значит выбросить добычу собственными руками
            var lying = ctx.Entities.Get(drop.Id);
            string reason = WhyNotTaken(gone: lying == null, walk.OutOfTime, walk.Arrived,
                lying is { } still ? Gap(still) : "");
            // Запоминаем ТУ ЖЕ причину, что говорим вслух, — одна строка на оба
            // дела, разъехаться нечему
            if (!walk.OutOfTime)
                GiveUpOn(drop.Id, reason);
            OnSkipped?.Invoke(drop.Code, reason);
            // «Кончилось время» и «до неё не дойти» — разные итоги: по первому
            // роль вправе прийти сюда снова, по второму идти незачем
            stop = walk.OutOfTime ? PickupStop.NoTime : PickupStop.Unreachable;
            why = $"{drop.Code} — {reason}";
        }

        if (ct.IsCancellationRequested || (hold != null && hold.Token.IsCancellationRequested))
        {
            stop = PickupStop.Stopped;
            why = "подбор прервали";
        }

        int left = Around(radius, want).Count(d => !onlyMine || d.Mine);
        return new PickupResult(items, units, left, stop, why, taken);
    }

    /// <summary>
    /// Сходить и подобрать вокруг ЧУЖОЙ точки (могила, место разделки,
    /// помеченный сломанный блок).
    ///
    /// ВЛАДЕНИЕ БЕРЁТСЯ ДО ПЕРВОГО ШАГА, И ЭТО ГЛАВНОЕ ЗДЕСЬ.
    ///
    /// ЖИВОЙ СЛУЧАЙ («ДВА ХОЗЯИНА У ХОДЬБЫ», журнал 16.08). В журнале подряд:
    ///   12:59:34 [тело] «своя добыча» не берёт тело: занято «наряд медь ×30»
    ///   12:59:34 [движение] дорога: 9 точек до (512182, 105, 512276)
    ///   12:59:40 [движение] дорога: ходьбу забрал «дорога до (512060, 117,
    ///            512254)» … За тело дерутся двое
    /// Распорядитель тела работал как надо и отказывал «своей добыче» каждые
    /// три секунды — а она ВСЁ ЭТО ВРЕМЯ ШЛА за сто двадцать блоков к месту,
    /// где бот недавно ломал руду. Потому что ходьба стояла ЗДЕСЬ, ВЫШЕ
    /// проверки: тело спрашивалось только в <see cref="CollectAsync"/>, то есть
    /// уже ПОСЛЕ дороги. Способность, которой тела не дали, честно возвращала
    /// «тело занято другим делом» — сделав перед этим полминуты беготни и
    /// отобрав ходьбу у той работы, что телом законно владела.
    ///
    /// Вот он, второй поход внутри одного владения: арбитр цел, дерутся не
    /// с ним, а мимо него. Лечится не вторым арбитром, а порядком двух строк —
    /// сперва спросить тело, потом идти.
    ///
    /// Дальше вниз владение уходит СВОИМ ТОКЕНОМ, а <see cref="CollectAsync"/>
    /// зовётся с <c>takeBody: false</c>: возьми она тело второй раз — сама себе
    /// и откажет («равный равного не перебивает»).
    /// </summary>
    public async Task<PickupResult> CollectAroundAsync(BlockPos center, double radius = 6,
        Func<string, bool>? want = null, bool onlyMine = false,
        double maxSeconds = 60, CancellationToken ct = default,
        BodyArbiter.Importance importance = BodyArbiter.Importance.Routine,
        string owner = "подбор", bool takeBody = true)
    {
        using var hold = takeBody ? ctx.Turn.TryTake(owner, importance, ct) : null;
        // Не дали тело — значит НЕ ИДЁМ, и ни шагу. Слова те же, что у
        // CollectAsync: отказ у обеих дверей один, и читаться он должен
        // одинаково
        if (takeBody && hold == null)
            return new PickupResult(0, 0, 0, PickupStop.Stopped, "тело занято другим делом", []);
        var token = hold?.Token ?? ct;

        // Теперь можно и прийти: искать «вокруг себя» имеет смысл, только
        // когда мы уже на месте
        if (ctx.Self.Position is { } me &&
            Math.Sqrt((me.X - center.X) * (me.X - center.X) + (me.Z - center.Z) * (me.Z - center.Z)) > radius)
        {
            await ctx.Movement.TravelToAsync(center, Math.Min(60, maxSeconds), token);
        }
        return await CollectAsync(radius, want, onlyMine, maxSeconds, token, importance, owner,
            takeBody: false);
    }

    /// <summary>
    /// Подобрать одну конкретную вещь (её уже нашла политика — еда, товар).
    ///
    /// ПАМЯТЬ О НЕУДАЧАХ ЗДЕСЬ ТА ЖЕ, ЧТО У ОБХОДА. Раньше этот путь её не знал
    /// вовсе: он ни разу не спрашивал <see cref="GaveUpOn"/> и ни разу в неё не
    /// писал. Живьём это выглядело так — голодный бот десять раз в секунду
    /// ходил к куску еды в клетке (512018, 109, 512197) на три блока НИЖЕ себя
    /// и каждый раз отказывал одними и теми же словами. Обход к той же кучке
    /// давно бы не пошёл, а «подобрать вот это» ходило вечно.
    /// </summary>
    public async Task<bool> TakeAsync(long entityId, double maxSeconds = 20,
        CancellationToken ct = default,
        BodyArbiter.Importance importance = BodyArbiter.Importance.Routine,
        string owner = "подбор", bool takeBody = true)
    {
        if (ctx.Entities.Get(entityId) is not { } e || ctx.DroppedItemCode(e) is not { } code)
            return false;
        // На этой вещи мы уже обожглись — второй раз в ту же яму не лезем,
        // пока не выйдет RetryFailedSeconds
        if (GaveUpOn(entityId))
            return false;
        string? by = e.WatchedAttributes.GetString("byPlayerUid");
        var drop = new Drop(entityId, code, ctx.DroppedItemCount(e), e.X, e.Y, e.Z, 0,
            by == ctx.Bot.PlayerUid, by != null);

        using var hold = takeBody ? ctx.Turn.TryTake(owner, importance, ct) : null;
        if (takeBody && hold == null)
            return false;   // тело занято — за вещью не идём, и это не её вина
        var token = hold?.Token ?? ct;
        int had = CountOf(code);
        var walk = await WalkOntoAsync(drop, Now().AddSeconds(maxSeconds), token);

        await WaitTakenAsync(drop, had, token);
        // СКОЛЬКО ВЗЯЛИ — ФАКТ, А НЕ ЕДИНИЦА. Прибавку мы знаем точно, и
        // соседний обход (CollectAsync) её же и докладывает. Подобрав стопку
        // из восьми кусков мяса, бот писал «подобрал 1», и любой счётчик
        // добычи по этому пути — а это путь еды и товара — занижал итог
        int now = CountOf(code);
        if (now > had)
        {
            OnPicked?.Invoke(code, now - had);
            return true;
        }
        // Прерванное дело неудачей не считаем: тело отобрал рефлекс, а вещь
        // ни в чём не виновата — запомнить её как недостижимую значит соврать
        if (token.IsCancellationRequested)
            return false;

        // ВРЕМЯ — НЕ ВИНА ВЕЩИ. «Не хватило двадцати секунд на дорогу» закрывало
        // еду на две минуты наравне с «лежит за стеной»; голодному второй
        // попытки может уже не хватить
        if (walk.OutOfTime)
        {
            OnSkipped?.Invoke(code, $"не хватило времени на дорогу ({maxSeconds:0} с)" +
                (ctx.Entities.Get(entityId) is { } far ? $", {Gap(far)}" : "") +
                " — вещь не виновата, приду ещё");
            return false;
        }

        // ПРИЧИНА ОДНА НА ОБА ДЕЛА: и сказать человеку, и запомнить. Порознь их
        // держать нельзя — разъедутся, и на вопрос «почему бот сюда не идёт»
        // память ответит одно, а журнал другое
        // Время сюда не доходит: его случай разобран выше и вещь за него не
        // наказывают — поэтому outOfTime здесь заведомо ложь
        var left = ctx.Entities.Get(entityId);
        string почему = WhyNotTaken(gone: left == null, outOfTime: false, walk.Arrived,
                            left is { } still ? Gap(still) : "") +
                        (left != null ? $", не пойду туда {RetryFailedSeconds:0} с" : "");
        GiveUpOn(entityId, почему);
        OnSkipped?.Invoke(code, почему);
        return false;
    }

    /// <summary>
    /// Чем кончилась дорога к вещи.
    /// </summary>
    /// <param name="Arrived">Встали достаточно близко — сервер вещь отдаст.</param>
    /// <param name="OutOfTime">
    /// Не дошли ИМЕННО ПОТОМУ, что кончилось отпущенное время. Это не «вещь
    /// недостижима», и путать их дорого: недобор двадцати секунд на дорогу
    /// закрывал кусок мяса на две минуты (см. <see cref="RetryFailedSeconds"/>),
    /// а голодному второй попытки может уже не хватить.
    /// </param>
    private readonly record struct WalkResult(bool Arrived, bool OutOfTime);

    /// <summary>
    /// Встать НА вещь. Если она лежит выше или ниже — сначала честно перейти
    /// в её клетку: подбор смотрит и по вертикали, и с кромки ямы до дна
    /// не дотянуться.
    /// </summary>
    private async Task<WalkResult> WalkOntoAsync(Drop drop, DateTime deadline, CancellationToken ct)
    {
        long id = drop.Id;
        // Успех меряем САМИ. Ходилка честно докладывает «пришёл», имея в виду
        // свою цель по горизонтали, — а живьём это выглядело так: бот стоит
        // на кромке ямы в 0,74 бл от кучки, но на 1,9 ниже его середины,
        // и сервер молча ничего не отдаёт
        for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            if (Reaches(id) is not { } near)
                return new WalkResult(false, false);   // вещи больше нет
            if (near)
                return new WalkResult(true, false);
            double left = (deadline - Now()).TotalSeconds;
            if (left <= 0)
                return new WalkResult(false, true);
            double seconds = Math.Min(WalkSeconds, Math.Max(2, left));

            var live = ctx.Entities.Get(id);
            if (live == null)
                return new WalkResult(false, false);

            if (ctx.Self.Position is { } me && live.Y < me.Y - 0.5)
            {
                // Вещь НИЖЕ нас: сверху не дотянуться, надо спуститься в её клетку
                var cell = new BlockPos((int)Math.Floor(live.X), (int)Math.Floor(live.Y),
                    (int)Math.Floor(live.Z));
                await ctx.Movement.MoveToCellAsync(cell, ct);
            }
            else
            {
                await ctx.Movement.ApproachAsync(
                    () => ctx.Entities.Get(id) is { } l ? (l.X, l.Y, l.Z) : null,
                    stopDistance: StopDistance, maxSeconds: seconds, ct: ct);
            }
        }
        if (Reaches(id) == true)
            return new WalkResult(true, false);
        // Три захода вышли, а время ещё есть — значит дело не во времени
        return new WalkResult(false, Now() >= deadline);
    }

    /// <summary>
    /// ПОЧЕМУ ВЕЩЬ ТАК И НЕ ДОСТАЛАСЬ — чистое правило, одно на оба пути
    /// подбора (обход и «подбери вот это»).
    ///
    /// ПОРЯДОК ВОПРОСОВ ЗДЕСЬ И ЕСТЬ ПРАВИЛО. Прежде первым спрашивали
    /// «дошёл ли», а дорога к пропавшей вещи обрывается сама собой — и про
    /// исчезнувшую вещь бот говорил «не дойти». Живой случай (журнал 19.08,
    /// 18:35:27): сразу после «подходить не к кому: цель пропала» шло
    /// «[подбор] не взял backpack-sturdy: не дойти», и человек читал это как
    /// «бот не смог подойти», хотя подходить было уже не к чему.
    /// </summary>
    /// <param name="gone">Вещи в мире больше нет.</param>
    /// <param name="outOfTime">Кончилось отпущенное на дорогу время.</param>
    /// <param name="arrived">Встали в зону сбора.</param>
    /// <param name="gap">Насколько далеко стоим — словами (см. <c>Gap</c>).</param>
    public static string WhyNotTaken(bool gone, bool outOfTime, bool arrived, string gap) =>
        gone ? "исчезла из мира, а в сумку не легла"
        : outOfTime ? "не хватило времени на дорогу"
        : !arrived ? $"не дойти ({gap})"
        : $"не даётся ({gap})";

    /// <summary>
    /// ПОСТОЯТЬ У ВЕЩИ И ДОЖДАТЬСЯ ФАКТА — ПРИБАВКИ В СУМКЕ.
    ///
    /// Подбирает сервер сам, и «подобрал» — это не «сущность пропала», а
    /// «в сумке стало больше». Это РАЗНЫЕ ПАКЕТЫ: исчезновение приезжает одним,
    /// содержимое сумок — другим (31 InventoryUpdate), и между ними лежит целый
    /// круг сети.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 19.08, 18:35:27): ожидание обрывалось в тот самый
    /// миг, когда сущность пропадала, сумка тут же читалась прежней — и бот
    /// писал «[подбор] не взял backpack-sturdy: исчезла» про рюкзак, который
    /// лежал у него под ногами. Потеря рюкзаков стоила ему всего, что он нёс.
    ///
    /// Ожидание тут ОДНО НА ОБА ПУТИ подбора (обход и «подбери вот это»): та же
    /// строка, написанная дважды, и разъехалась бы дважды.
    /// </summary>
    /// <returns>Правда — сервер отдал вещь: в сумке её стало больше.</returns>
    private async Task<bool> WaitTakenAsync(Drop drop, int had, CancellationToken ct)
    {
        var until = Now().AddSeconds(WaitSecondsFor(drop));
        while (Now() < until && !ct.IsCancellationRequested)
        {
            if (CountOf(drop.Code) > had)
                return true;   // сервер ответил — ждать больше нечего
            await Wait(Опрос, ct);
        }
        return CountOf(drop.Code) > had;
    }

    /// <summary>
    /// Сдвиг от СЕРЕДИНЫ тела до вещи по трём осям. Считать от ног нельзя:
    /// игра берёт зону вокруг середины (ноги + половина роста), и именно
    /// поэтому вещь под ногами достаётся, а вещь на дне ямы — нет.
    /// </summary>
    private (double X, double Y, double Z)? OffsetTo(long id)
    {
        if (ctx.Entities.Get(id) is not { } item || ctx.Self.Position is not { } me)
            return null;
        return Offset(item, me);
    }

    private static (double X, double Y, double Z) Offset(EntityInfo item, (double X, double Y, double Z) me) =>
        (item.X - me.X, item.Y - (me.Y + BodyMiddle), item.Z - me.Z);

    /// <summary>Достаёт ли сервер эту вещь прямо сейчас (null — вещи или себя не видим).</summary>
    private bool? Reaches(long id) =>
        OffsetTo(id) is { } d
            ? WithinCollect(d.X, d.Y, d.Z, CollectHorizontal, CollectVertical)
            : null;

    /// <summary>
    /// Насколько мы на самом деле далеко от вещи. Зона сбора у игры —
    /// ЦИЛИНДР (см. <see cref="WithinCollect"/>), поэтому в отказе называются
    /// обе мерки отдельно: по какой из них не прошли, по той и не достали.
    /// </summary>
    private string Gap(EntityInfo item)
    {
        if (ctx.Self.Position is not { } me)
            return "где я — неизвестно";
        var (dx, dy, dz) = Offset(item, me);
        return $"по горизонтали {Math.Sqrt(dx * dx + dz * dz):0.##} (зона {CollectHorizontal:0.##}), " +
               $"по высоте {dy:0.##} (зона ±{CollectVertical:0.##})";
    }

    /// <summary>
    /// Сколько такого уже в сумках. Считается по ВСЕМ носимым слотам, потому
    /// что вещь может лечь в любой — и в хотбар, и в рюкзак.
    /// </summary>
    private int CountOf(string code) =>
        ctx.Self.CarrySlots().Where(s => s.Content.Code == code).Sum(s => s.Content.Count);
}
