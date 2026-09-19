namespace VsBotKit;

/// <summary>
/// ВРЕМЕННЫЙ БЛОК — тот, что поставлен НА ВРЕМЯ работы и в конце снимается.
/// </summary>
/// <param name="Where">Клетка, куда он встал (по подтверждению сервера, а не по намерению).</param>
/// <param name="Code">
/// Чем встал. Код нужен не для красоты отчёта: по нему уборка и узнаёт, НАШ
/// ли блок ещё стоит в клетке. Без кода бот снял бы то, что положили на это
/// место после нас, — чужую дорогу, чужой пол, собственное полотно.
/// </param>
/// <param name="Who">Кто поставил: «стройка», «добыча», «дорога».</param>
/// <param name="Why">Ради чего: «столб наверх на 2 бл».</param>
/// <param name="At">Когда поставлен.</param>
/// <param name="Seq">Порядковый номер: больше — значит поставлен позже.</param>
public sealed record TempBlock(BlockPos Where, string Code, string Who, string Why,
    DateTime At, int Seq)
{
    public override string ToString() => $"{Code} в {Where} ({Who}: {Why})";
}

/// <summary>Лес, который снять не вышло, и почему именно.</summary>
public sealed record TempLeftover(TempBlock Block, string Why)
{
    public override string ToString() => $"{Block} — {Why}";
}

/// <summary>
/// Чем кончилась уборка лесов.
/// </summary>
/// <param name="Had">Сколько временных блоков бот за собой числил.</param>
/// <param name="Removed">Сколько снято ПО ФАКТУ: клетка проверена после слома.</param>
/// <param name="Vanished">
/// Сколько к уборке уже не стояло: осыпалось, кто-то снял, или на это место
/// легло другое. Это не успех уборки и не её провал — это отдельная новость,
/// и валить её в «снял» значило бы приписывать себе чужую работу.
/// </param>
/// <param name="Left">Что осталось стоять и почему — с координатами.</param>
public sealed record TempCleanup(int Had, int Removed, int Vanished,
    IReadOnlyList<TempLeftover> Left)
{
    /// <summary>
    /// Успех — ТОЛЬКО когда не осталось ни одного леса. «Снял девять из
    /// десяти» успехом не считается: забытые леса посреди чужой постройки
    /// хуже, чем не построенные вовсе.
    /// </summary>
    public bool Success => Left.Count == 0;

    public override string ToString() => TempBlocks.Say(Had, Removed, Vanished, Left);
}

/// <summary>
/// ВРЕМЕННЫЕ ЛЕСА: память о том, что поставлено на время, и уборка ровно этого
/// и только этого.
///
/// ЗАКАЗ ЧЕЛОВЕКА, дословно: «разрешить строить временные леса во время
/// стройки или копания объектов, которые в конце убираем».
///
/// ЖИВОЙ СЛУЧАЙ, ради которого класс появился (журнал 16.08, дорога вниз на
/// 32 блока):
///   01:33:46 [стройка] наверх на 2 бл: блоков 362 — хватит на столб в 2 бл
///   01:33:46 [добыча] под ногами уже stonepath-free — столбиться некуда
///   01:33:46 [стройка] этот приём отсюда не поднял ни на блок — вычёркиваю
///   01:33:46 [стройка] наверх на 2 бл: … а есть блоков 0 и лестниц 0
///   01:33:46 [движение] не дойти до (512067, 112, 512241): перебрал всё, что умею
/// Бот запирается там, где живой игрок кинул бы пару блоков под ноги и потом
/// снял бы их обратно. Кинуть он умел и раньше (столб, лестница по стене,
/// подпорка под ступень — всё это <see cref="Scaffolding"/> и
/// <see cref="Mining.PillarUpAsync"/>); чего он не умел — это ПОМНИТЬ, что
/// поставленное временное, и в конце его снять.
///
/// ЗАСТРЯТЬ ИЗ-ЗА СВОЕГО ЖЕ ЛЕСА. Блоков в сумке было 362, а не ноль: столб не
/// вырос не от нехватки материала, а потому что клетка ног была занята тонким
/// покрытием, которое бот сам подложил себе под ноги, — и
/// <see cref="Mining.PillarUpAsync"/> отказывается ставить блок в занятую
/// клетку. «Блоков 0» во второй строке — это уже слова вычеркнутого приёма, а
/// не остаток сумки. Прежде класс этот случай только ОПИСЫВАЛ; теперь у него
/// есть и лечение — <see cref="PullInTheWayAsync"/>: приём, упёршийся в
/// собственный лес, снимает его и пробует снова.
///
/// ВТОРОЙ СТРОЙКИ ЗДЕСЬ НЕТ, и это главное. Класс не ставит ни одного блока:
/// ставят прежние механизмы, а сюда они только ДОКЛАДЫВАЮТ — «поставил такой-то
/// блок там-то, ради того-то» (<see cref="Note"/>). Здесь живут ровно четыре
/// вопроса, которых раньше не было ни у кого:
///   1) можно ли вообще ставить временный блок ЗДЕСЬ (<see cref="MayPlace"/>);
///   2) что и в каком порядке снимать (<see cref="TakeDownOrder"/>);
///   3) что снять не вышло — и это говорится ВСЛУХ с координатами;
///   4) не мой ли лес мне сейчас и мешает (<see cref="PullInTheWayAsync"/>).
///
/// УБОРКА ТОЛЬКО ПО ФАКТУ. «Сломал» не значит «снял»: подтверждением служит
/// клетка после слома — нашего блока в ней больше нет. Всё, что осталось
/// стоять, попадает в отчёт словами «лесов не убрано N, вот где». Забытые леса
/// посреди чужой постройки — хуже, чем не построить их вовсе, и молчаливая
/// «частичная уборка» была бы здесь худшим из возможных ответов.
///
/// Класс — МЕХАНИЗМ. Строить ли леса вообще и убирать ли их после работы,
/// решает роль (ручки <c>СтроитьВременныеЛеса</c> и <c>УбиратьЛесаПослеРаботы</c>).
/// </summary>
public sealed class TempBlocks
{
    private readonly BotContext ctx;

    /// <summary>Под замком: докладывают сюда и рефлексы, и долгие дела.</summary>
    private readonly object gate = new();

    private readonly List<TempBlock> standing = [];

    private int seq;

    public TempBlocks(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    // ---------------- настройки механизма ----------------

    /// <summary>
    /// Разрешено ли ставить временные блоки. Выключает тот, кому нельзя
    /// оставлять в мире ни одного своего блока даже на минуту, — например
    /// караванщик на чужом сервере.
    ///
    /// Своих блоков это не отнимает: полотно дороги, крепь, факелы ставятся
    /// как ставились. Речь ровно о лесах — о том, что заведомо снимут.
    /// </summary>
    public bool Allowed { get; set; } = true;

    /// <summary>
    /// Убирать ли леса, когда работа кончилась. Выключают, когда леса нужны
    /// и после: столб к площадке, по которому потом лазать.
    /// </summary>
    public bool TakeDownAfterWork { get; set; } = true;

    /// <summary>
    /// Сколько временных блоков вообще держать в памяти. Предел механизма, а
    /// не политика: список без предела растёт всю ночь, а уборка на пять тысяч
    /// клеток — это не уборка, а вторая смена.
    ///
    /// Переполнение НЕ МОЛЧИТ: самое старое забывается, и об этом говорится
    /// вслух, иначе леса пропали бы из памяти незаметно и остались бы в мире.
    /// </summary>
    public int MaxStanding { get; set; } = 512;

    /// <summary>Сколько секунд ждать, пока поднимется своё же выпавшее.</summary>
    public double PickUpSeconds { get; set; } = 6;

    // ---------------- что бот за собой числит ----------------

    /// <summary>Сколько временных блоков стоит по памяти бота.</summary>
    public int Count { get { lock (gate) return standing.Count; } }

    /// <summary>Все леса, что бот за собой числит.</summary>
    public IReadOnlyList<TempBlock> Standing { get { lock (gate) return standing.ToArray(); } }

    /// <summary>Числится ли за нами блок в этой клетке.</summary>
    public bool Mine(BlockPos where)
    {
        lock (gate)
            return standing.Any(b => b.Where == where);
    }

    // ---------------- чистые правила ----------------

    /// <summary>
    /// МОЖНО ЛИ СТАВИТЬ ВРЕМЕННЫЙ БЛОК — чистое правило: три ответа «нет» и
    /// один «да», и каждый со словами.
    ///
    /// ЧУЖОЙ ПРИВАТ — ЗАПРЕТ БЕЗ ОГОВОРОК. Живой игрок в чужой заявке блок не
    /// поставит: сервер ему откажет. Бот, который пробует, ничем не отличается
    /// от бота, который ломится в запертую дверь, — и вдобавок копит себе
    /// «приметы чужого» на ровном месте (см. <see cref="Claims.NoteRefusal"/>).
    ///
    /// ЗАНЯТАЯ КЛЕТКА — тоже нет, и это не придирка: сервер поставит блок
    /// поверх другого только там, где тот помечен заменяемым, а «поставил»
    /// без смены блока — это то самое враньё по намерению, которое запрещает
    /// правило 4.
    /// </summary>
    /// <param name="allowed">Разрешено ли роли ставить временные блоки.</param>
    /// <param name="foreignWhy">Чем место чужое (пусто — место не чужое).</param>
    /// <param name="what">Что уже стоит в клетке (пусто — клетка свободна).</param>
    /// <param name="seen">Видно ли клетку вообще (чанк пришёл).</param>
    public static (bool May, string Why) MayPlaceRule(bool allowed, string foreignWhy, string what,
        bool seen = true)
    {
        if (!allowed)
            return (false, "временные леса ставить запрещено настройкой");
        if (foreignWhy.Length > 0)
            return (false, $"это чужое место ({foreignWhy}) — временный блок тут " +
                           "не поставит и живой игрок");
        // КЛЕТКИ НЕ ВИДНО — ЭТО НЕ «КЛЕТКА СВОБОДНА». Про непрогруженный кусок
        // мира сказать нечего, и поставить туда блок значит объявить успехом
        // то, чего никто не подтверждал (правило 4). Прежняя застава столба
        // такую клетку молча отвергала (IsPassable у неизвестной клетки —
        // ложь), но говорила при этом «под ногами уже » с пустотой вместо
        // имени блока: кода-то она не знала. Отвечаем честно и словами
        if (!seen)
            return (false, "клетки под ногами не видно — этот кусок мира ко мне не приходил");
        if (what.Length > 0)
            return (false, $"клетка занята ({what})");
        return (true, "");
    }

    /// <summary>
    /// В КАКОМ ПОРЯДКЕ СНИМАТЬ ЛЕСА — чистое правило: сверху вниз, а на одной
    /// высоте позже поставленное первым.
    ///
    /// СВЕРХУ ВНИЗ — не аккуратность, а необходимость. Снимешь низ столба
    /// раньше верха — и бот, стоящий на этом столбе, полетит вниз со всей
    /// высоты вместо одной клетки, а верхние блоки повиснут в воздухе (или,
    /// если это гравий с песком, осыплются на голову). Сверху вниз бот сходит
    /// по своему же столбу, снимая под собой по клетке, — ровно так его и
    /// разбирает <see cref="Mining.PillarDownAsync"/>.
    ///
    /// ПОЗЖЕ ПОСТАВЛЕННОЕ ПЕРВЫМ — на случай, когда леса стоят в два слоя:
    /// подпорка под ступень положена раньше, чем ступень на ней, и снимать
    /// надо в обратном порядке.
    /// </summary>
    public static IReadOnlyList<TempBlock> TakeDownOrder(IEnumerable<TempBlock> standing) =>
        standing
            .OrderByDescending(b => b.Where.Y)
            .ThenByDescending(b => b.Seq)
            .ToList();

    /// <summary>
    /// МОЖНО ЛИ СНИМАТЬ ЭТОТ ЛЕС ПРЯМО СЕЙЧАС — чистое правило.
    ///
    /// Блок не под ногами — снимай и не думай. Блок ПОД НОГАМИ — это своя же
    /// опора, и сломать её значит упасть; падение до
    /// <see cref="AStarPathFinder.FallDamageFreeHeight"/> игра прощает, дальше
    /// снимает здоровье, и платить здоровьем за уборку бот не станет. Правило
    /// урона тут не выдумано заново, а взято у поиска пути — оно в проекте одно.
    /// </summary>
    /// <param name="underFoot">Блок держит наши ноги.</param>
    /// <param name="fall">На сколько блоков мы упадём, сняв его.</param>
    public static (bool May, string Why) MayPullRule(bool underFoot, double fall)
    {
        if (!underFoot)
            return (true, "");
        double hurt = AStarPathFinder.EstimateFallDamage(fall);
        return hurt <= 0
            ? (true, "")
            : (false, $"стою на нём, а под ним {fall:0.#} бл пустоты — " +
                      $"падение стоило бы {hurt:0.#} хп, сперва надо слезть");
    }

    /// <summary>
    /// ЧТО БОТ ЗА СОБОЙ ЧИСЛИТ, КОГДА РАБОТА ЕЩЁ НЕ КОНЧИЛАСЬ — чистое правило.
    ///
    /// Незаконченный заход леса НЕ снимает: подпорка, по которой бот через
    /// минуту полезет обратно, нужна ему стоящей. Но и промолчать про неё
    /// нельзя — человек нашёл бы её в мире через неделю и назвал бы браком.
    ///
    /// ОБЕЩАТЬ УБОРКУ, КОТОРОЙ НЕ БУДЕТ, ТОЖЕ НЕЛЬЗЯ: при выключенной ручке
    /// <see cref="TakeDownAfterWork"/> эти блоки так и останутся стоять, и
    /// сказать об этом надо сейчас, а не после.
    ///
    /// Правило живёт ЗДЕСЬ, а не у дороги и карьера порознь: до сведения
    /// потоков эту строку говорила одна дорога (<see cref="Roads.BuildAsync"/>),
    /// а карьер при незаконченном заходе возвращался молча — тот же случай,
    /// те же леса, и ни слова человеку.
    /// </summary>
    /// <param name="standing">Сколько лесов числится за ботом.</param>
    /// <param name="takeDownAfterWork">Разрешено ли убирать их после работы.</param>
    /// <param name="work">Чем занят: «дорога», «карьер» — его же словом и говорим.</param>
    public static string OweRule(int standing, bool takeDownAfterWork, string work)
    {
        if (standing <= 0)
            return "";
        return $"за собой числю {standing} временных блоков — " +
               (takeDownAfterWork
                   ? $"сниму, когда {work} кончится"
                   : "убирать их запрещено настройкой, так и останутся");
    }

    /// <inheritdoc cref="OweRule"/>
    public string Owe(string work) => OweRule(Count, TakeDownAfterWork, work);

    /// <summary>
    /// ЧТО СКАЗАТЬ ПРО УБОРКУ — чистое правило, и молчаливого ответа среди
    /// возможных нет.
    ///
    /// Недоубранное называется ЧИСЛОМ И КООРДИНАТАМИ: «лесов не убрано N, вот
    /// где». Человеку, который придёт разбираться, нужны именно координаты —
    /// по фразе «часть лесов осталась» не найти ничего.
    /// </summary>
    public static string Say(int had, int removed, int vanished, IReadOnlyList<TempLeftover> left)
    {
        if (had == 0)
            return "временных блоков за собой не оставлял — убирать нечего";

        string head = $"лесов было {had}: снял {removed}" +
                      (vanished > 0 ? $", ещё {vanished} и так уже не стояло" : "");
        if (left.Count == 0)
            return head + " — ни одного не осталось";

        const int show = 8;
        return head + $"; ЛЕСОВ НЕ УБРАНО {left.Count}, вот где: " +
               string.Join("; ", left.Take(show).Select(l => l.ToString())) +
               (left.Count > show ? $"; и ещё {left.Count - show}" : "");
    }

    // ---------------- живая половина ----------------

    /// <summary>
    /// Можно ли поставить временный блок в эту клетку — то же правило
    /// <see cref="MayPlaceRule"/>, только числа собраны из мира.
    /// </summary>
    public (bool May, string Why) MayPlace(BlockPos where)
    {
        // Чужое спрашиваем у того, кто про чужое и знает, — и спрашиваем именно
        // «внутри ли заявки», а не «далеко ли до неё»: держаться от чужого на
        // 500 бл велено ПРИ ВЫБОРЕ МЕСТА РАБОТЫ, и переносить это число на
        // подпорку под собственной ногой значило бы запретить боту вылезти из
        // ямы на полкарты вокруг любого чужого забора
        string foreign = ctx.Claims?.Inside(where) is { } place ? place.Why : "";
        bool seen = ctx.World.IsKnown(where);
        string what = seen && !ctx.World.IsPassable(where.X, where.Y, where.Z)
            ? ctx.World.GetBlockCode(where) ?? "чем-то"
            : "";
        return MayPlaceRule(Allowed, foreign, what, seen);
    }

    /// <summary>
    /// ЗАПОМНИТЬ ПОСТАВЛЕННЫЙ ВРЕМЕННО БЛОК. Зовут ПОСЛЕ подтверждения от
    /// сервера, а не вместо него: список лесов, куда попадают не вставшие
    /// блоки, — это список мест, где бот потом будет ломать пустоту.
    /// </summary>
    /// <param name="who">Кто поставил: «стройка», «добыча».</param>
    /// <param name="why">Ради чего: «столб наверх на 2 бл».</param>
    public TempBlock Note(BlockPos where, string code, string who, string why)
    {
        TempBlock block;
        TempBlock? forgotten = null;
        lock (gate)
        {
            // Та же клетка второй раз — это тот же лес: перезаписываем, а не
            // копим. Иначе уборка ломала бы одну клетку дважды и второй раз
            // честно докладывала бы «тут пусто»
            standing.RemoveAll(b => b.Where == where);
            block = new TempBlock(where, code, who, why, DateTime.UtcNow, ++seq);
            standing.Add(block);
            if (standing.Count > Math.Max(1, MaxStanding))
            {
                forgotten = standing[0];
                standing.RemoveAt(0);
            }
        }
        if (forgotten is { } old)
            OnLog?.Invoke($"лесов набралось больше {MaxStanding} — самый старый забываю " +
                          $"({old}); он ОСТАНЕТСЯ в мире, снять его будет некому");
        return block;
    }

    /// <summary>
    /// ЗАБЫТЬ ЛЕС: блок исчез не нашей уборкой — сами разобрали столб под
    /// собой, осыпалось, кто-то снял. Молчаливое накопление таких записей
    /// превратило бы отчёт уборки в список пустых клеток.
    /// </summary>
    public bool Forget(BlockPos where)
    {
        lock (gate)
            return standing.RemoveAll(b => b.Where == where) > 0;
    }

    /// <summary>Забыть все леса разом (новая работа — чужие леса не наши).</summary>
    public int ForgetAll()
    {
        lock (gate)
        {
            int n = standing.Count;
            standing.Clear();
            return n;
        }
    }

    /// <summary>
    /// СНЯТЬ ЛЕСА — ровно те, что ставили сами, и только их.
    ///
    /// Порядок задаёт чистое правило <see cref="TakeDownOrder"/>: сверху вниз.
    /// Каждый блок проходит три заставы, и о каждой говорится вслух:
    ///   • НАШ ЛИ ОН ЕЩЁ. В клетке может стоять уже другое — тогда это не наш
    ///     лес, а чужая работа, и ломать её нельзя;
    ///   • МОЖНО ЛИ СНЯТЬ СЕЙЧАС (<see cref="MayPullRule"/>) — не упадём ли;
    ///   • СНЯЛСЯ ЛИ ПО ФАКТУ — клетка проверяется ПОСЛЕ слома.
    ///
    /// Выпавшее подбираем: лес — это наш же блок, и бросать его на землю
    /// значит терять материал, из которого потом класть полотно.
    /// </summary>
    /// <param name="who">
    /// Чьи леса снимать («стройка»), пусто — все. Нужно, когда одна работа
    /// кончилась, а другая ещё идёт и её подпорки убирать рано.
    /// </param>
    public async Task<TempCleanup> TakeDownAsync(string who = "",
        CancellationToken ct = default)
    {
        var mine = Standing
            .Where(b => who.Length == 0 ||
                        string.Equals(b.Who, who, StringComparison.OrdinalIgnoreCase))
            .ToList();
        int had = mine.Count;
        if (had == 0)
            return new TempCleanup(0, 0, 0, []);

        OnLog?.Invoke($"работа кончилась — снимаю свои леса: {had} бл" +
                      (who.Length > 0 ? $" (поставила {who})" : ""));

        int removed = 0, vanished = 0;
        var left = new List<TempLeftover>();

        foreach (var block in TakeDownOrder(mine))
        {
            if (ct.IsCancellationRequested)
            {
                left.Add(new TempLeftover(block, "уборку прервали"));
                continue;
            }

            var (pulled, why) = await PullOneAsync(block, ct);
            switch (pulled)
            {
                case PullResult.Pulled:
                    removed++;
                    break;
                case PullResult.WasntThere:
                    vanished++;
                    if (why.Length > 0)
                        OnLog?.Invoke(why);
                    break;
                default:
                    left.Add(new TempLeftover(block, why));
                    break;
            }
        }

        var cleanup = new TempCleanup(had, removed, vanished, left);
        OnLog?.Invoke(cleanup.ToString());
        return cleanup;
    }

    /// <summary>Чем кончилась попытка снять ОДИН лес.</summary>
    public enum PullResult
    {
        /// <summary>Снят по факту: клетка проверена после слома.</summary>
        Pulled,
        /// <summary>Его там уже и не было: осыпался, кто-то снял, легло чужое.</summary>
        WasntThere,
        /// <summary>Остался стоять — причина в словах.</summary>
        Stayed
    }

    /// <summary>
    /// СНЯТЬ ОДИН ЛЕС — три заставы и подтверждение по факту, в одном месте.
    ///
    /// Вынесено из <see cref="TakeDownAsync"/> не ради красоты: тем же порядком
    /// снимается лес, который встал НА ПУТИ посреди работы
    /// (<see cref="PullInTheWayAsync"/>). Разъедься эти два снятия копиями — и
    /// одно из них рано или поздно перестало бы спрашивать «не упаду ли, сняв
    /// его из-под собственных ног», а это падение с высоты столба.
    /// </summary>
    private async Task<(PullResult Result, string Why)> PullOneAsync(TempBlock block,
        CancellationToken ct)
    {
        var at = block.Where;

        // 1. НАШ ЛИ ОН ЕЩЁ. Клетку могли занять после нас — своим же
        // полотном, чужой постройкой, осыпавшимся гравием
        if (!ctx.World.IsKnown(at))
            return (PullResult.Stayed, "чанк не прогружен — что там сейчас, не видно");
        // ПУСТО СПРАШИВАЕМ ПО id, А НЕ ПО КОДУ. У пустой известной клетки
        // код — «air» (законный код воздуха из реестра), а null бывает
        // только у непрогруженного чанка, и его отсекли выше. Пока здесь
        // стоял пустой код, эта ветка не срабатывала НИКОГДА: осыпавшийся
        // сам лес уходил ниже, в ветку «тут уже чужое», и бот докладывал
        // «в (x,y,z) уже не мой лес, а air — не трогаю». Число выходило
        // верное, а слова — враньё про несуществующий чужой блок
        if (ctx.World.GetBlockId(at.X, at.Y, at.Z) == 0)
        {
            Forget(at);
            return (PullResult.WasntThere, "");
        }
        string now = ctx.World.GetBlockCode(at) ?? "чем-то";
        if (!now.Contains(block.Code, StringComparison.OrdinalIgnoreCase) &&
            !block.Code.Contains(now, StringComparison.OrdinalIgnoreCase))
        {
            Forget(at);
            return (PullResult.WasntThere, $"в {at} уже не мой лес, а {now} — не трогаю");
        }

        // 2. НЕ УПАДЁМ ЛИ, сняв его из-под собственных ног
        var (may, whyNot) = MayPullRule(UnderFoot(at), FallIfPulled(at));
        if (!may)
            return (PullResult.Stayed, whyNot);

        if (!ctx.Mining.CanBreak(at))
            return (PullResult.Stayed, $"{now} не берётся тем, что в руках");

        var broke = await ctx.Mining.BreakAsync(at, ct);
        if (!broke.Success)
            // Причину берём у самой добычи; пустой она не бывает, но
            // молчаливый отказ без слов был бы хуже всего
            return (PullResult.Stayed, broke.Message ?? broke.ToString());

        // 3. ПО ФАКТУ, А НЕ ПО ОТВЕТУ. Слом подтверждён самой добычей, но
        // спросить клетку ещё раз стоит ничего: сюда могло осыпаться
        if (ctx.World.GetBlockCode(at) is { Length: > 0 } still &&
            still.Contains(block.Code, StringComparison.OrdinalIgnoreCase))
            return (PullResult.Stayed, $"сломал, а {still} в клетке остался");

        Forget(at);
        // Свой же блок обратно в сумку: в живом прогоне бот вставал именно
        // потому, что блоков не осталось совсем
        await ctx.Mining.CollectDropsAsync(4, PickUpSeconds, ct);
        return (PullResult.Pulled, "");
    }

    /// <summary>
    /// СВОЙ ЖЕ ЛЕС, ВСТАВШИЙ НА ПУТИ, — чистое правило: какие из названных
    /// клеток числятся за нами.
    ///
    /// Порядок ответа тот же, что у уборки (<see cref="TakeDownOrder"/>):
    /// сверху вниз, позже поставленное первым. Снимать надо ровно СВОЁ:
    /// чужая стена мешает не меньше, но ломать её — это уже не уборка за собой.
    /// </summary>
    public IReadOnlyList<TempBlock> MineAmong(IEnumerable<BlockPos> cells)
    {
        var want = cells.ToHashSet();
        return TakeDownOrder(Standing.Where(b => want.Contains(b.Where)));
    }

    /// <summary>
    /// УБРАТЬ СВОЙ ЖЕ ЛЕС, КОТОРЫЙ ТЕПЕРЬ МЕШАЕТ ПРОЙТИ.
    ///
    /// ЖИВОЙ СЛУЧАЙ, словами заказчика: «ставил леса временные и сам застрял
    /// из-за них». Журнал 16.08, 12:53:10–12:53:16 читается как приговор:
    ///   12:53:10 [стройка] строю лестницу на 7 бл вдоль стены юг
    ///   12:53:13 [стройка] лестницей: не поднялся на 106
    ///   12:53:14 [добыча] столбиться в (512184, 105, 512278) нельзя:
    ///            клетка занята (ladder-wood-acacia-north)
    ///   12:53:16 [штольня] ступень в сторону (-1, 0) не вышла (клетка
    ///            (512183, 106, 512278) вскрыта, а тело в неё не идёт)
    /// Одна и та же своя перекладина, вставшая в клетку ног, отняла у бота
    /// подряд ВСЕ три приёма: столб не растёт в занятую клетку, а лестница под
    /// ногами делает тело лазающим — и игра снимает с него опору
    /// (GamePlayerPhysics: OnGround = CollidedVertically &amp;&amp; !IsClimbing),
    /// без которой нет прыжка, а без прыжка нет и шага на ступень.
    ///
    /// Прежний класс этот случай ЗНАЛ и честно писал в своей же док-строке, что
    /// не чинит его. Теперь чинит: приём, который упёрся в собственный лес,
    /// снимает его и пробует снова.
    ///
    /// Возвращает, сколько снято ПО ФАКТУ. Ноль — либо своего там нет вовсе,
    /// либо снять не дали, и тогда причина уже сказана вслух.
    /// </summary>
    /// <param name="cells">Клетки, в которых что-то мешает.</param>
    /// <param name="why">Чему мешает — теми же словами, что в журнале.</param>
    public async Task<int> PullInTheWayAsync(IEnumerable<BlockPos> cells, string why,
        CancellationToken ct = default)
    {
        var mine = MineAmong(cells);
        if (mine.Count == 0)
            return 0;

        int pulled = 0;
        foreach (var block in mine)
        {
            if (ct.IsCancellationRequested)
                break;
            var (result, said) = await PullOneAsync(block, ct);
            if (result == PullResult.Pulled)
            {
                pulled++;
                OnLog?.Invoke($"мой же лес {block} мешал ({why}) — снял");
            }
            else if (result == PullResult.Stayed)
                OnLog?.Invoke($"мой же лес {block} мешает ({why}), а снять не выходит: {said}");
        }
        return pulled;
    }

    /// <summary>Клетка держит наши ноги (мы в ней или на ней).</summary>
    private bool UnderFoot(BlockPos cell) =>
        // Правило «тело занимает эту клетку» в проекте одно и живёт у дороги —
        // там оно и появилось, когда бот пробовал мостить клетку под собой
        ctx.Movement.FeetCell is { } feet && RoadPlan.StandsOn(feet, cell);

    /// <summary>
    /// На сколько блоков мы провалимся, сняв эту клетку: считаем пустоту под
    /// ней, пока не упрёмся в твердь. Дальше пятнадцати не смотрим — с такой
    /// высоты падать нельзя в любом случае, а лишний обход мира стоит времени.
    /// </summary>
    private double FallIfPulled(BlockPos cell)
    {
        const int look = 15;
        for (int d = 1; d <= look; d++)
            if (ctx.World.IsSolid(cell.X, cell.Y - d, cell.Z))
                return d;
        return look + 1;
    }
}
