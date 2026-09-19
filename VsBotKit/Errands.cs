namespace VsBotKit;

/// <summary>
/// ЧЕМ КОНЧИЛСЯ ПЕРЕВОД ЧЕЛОВЕЧЕСКОГО СЛОВА В КОД ИГРЫ.
///
/// Ответов ровно три, и путать их нельзя:
///   • <paramref name="Code"/> не пуст — поняли однозначно, можно идти;
///   • <paramref name="Code"/> пуст, а <paramref name="Choices"/> не пуст —
///     подходит несколько, и надо ПЕРЕСПРОСИТЬ, а не выбрать молча;
///   • пусто и то, и другое — такого имени в игре нет, и это честный отказ.
/// </summary>
/// <param name="Code">Код игры, если он один-единственный.</param>
/// <param name="Say">Что сказать человеку — всегда, даже когда всё хорошо.</param>
/// <param name="Choices">Кого нашли, лучшие первыми (для переспроса).</param>
public sealed record NameLookup(string? Code, string Say, IReadOnlyList<ThingName> Choices)
{
    /// <summary>Надо переспросить: подходит несколько.</summary>
    public bool NeedsAsking => Code == null && Choices.Count > 0;

    /// <summary>Не нашли вовсе.</summary>
    public bool Unknown => Code == null && Choices.Count == 0;

    public override string ToString() => Say;
}

/// <summary>Чем кончилось поручение.</summary>
/// <param name="Wanted">что просили</param>
/// <param name="Asked">сколько просили</param>
/// <param name="Delivered">сколько НА САМОМ ДЕЛЕ прибавилось (или сложено)</param>
public sealed record ErrandReport(string Wanted, int Asked, int Delivered, string Message)
{
    public bool Success => Delivered >= Asked;
    public override string ToString() => Message;
}

/// <summary>
/// Поручения: «добудь столько-то того-то и принеси».
///
/// Это надстройка над уже работающими механизмами, а не новая механика:
/// - что ломать ради нужного предмета, бот узнаёт из РЕЕСТРА СЕРВЕРА —
///   у каждого блока есть список выпадений (Block.Drops), и по нему видно,
///   что камень даёт булыжник, а россыпь — самородок. Никаких списков
///   в коде: для модовых блоков работает так же;
/// - лежащее на земле подбирает <see cref="Gathering"/>;
/// - ягоды и грибы собирает <see cref="Foraging"/>;
/// - блоки ломает <see cref="Mining"/> честным удержанием кнопки;
/// - складывает в сундук обычным переносом.
///
/// Роль говорит ЧТО и КУДА; как именно — забота этого класса.
/// </summary>
public class Errands
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;
    private readonly Gathering gathering;
    private readonly Foraging foraging;
    private readonly Dictionary<string, string[]> sourceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ПОЧЕМУ НЕ ВЫШЛО ВЗЯТЬ ЦЕЛЫМ — последняя названная причина за этот наряд.
    ///
    /// Нужна отчёту, а не журналу. Отказ случается посреди работы, а отвечает
    /// человеку отчёт в чате — и без этой строки он говорил бы «больше рядом
    /// нет» про породу, на которой бот стоит. Это ровно тот молчаливый обман,
    /// из-за которого «добудь гранит» и выглядел поломкой.
    /// </summary>
    private string? неВзятьЦелым;

    public Errands(BotContext ctx, Hands hands, Mining mining, Gathering gathering, Foraging foraging)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
        this.gathering = gathering;
        this.foraging = foraging;
    }

    public event Action<string>? OnLog;

    /// <summary>Сколько блоков вокруг просматривать в поисках источника.</summary>
    public int SearchRadius { get; set; } = 48;

    /// <summary>Сколько всего времени отводится на поручение, секунд.</summary>
    public double MaxSeconds { get; set; } = 600;

    /// <summary>
    /// ДОБЫВАТЬ ЦЕЛЬНЫЙ КАМЕНЬ, обкапывая блок со всех сторон, — а не бить его
    /// в лоб, получая щебёнку. Здесь только УМОЛЧАНИЕ механизма; решает роль
    /// (ручка «ОбкапыватьРадиЦельногоБлока»), и там же названа цена.
    ///
    /// Выключено — поручение на сам блок породы честно откажется и назовёт
    /// причину, вместо того чтобы бить камень до конца отведённого времени и
    /// не принести ни одного блока (см. <see cref="MineSourceAsync"/>).
    /// </summary>
    public bool TakeWhole { get; set; } = true;

    /// <summary>
    /// СКОЛЬКО ЗАХОДОВ НА ОДНУ ГРАНЬ отпущено обкапыванию. Здесь только
    /// УМОЛЧАНИЕ механизма; решает роль — ручка «ЗаходовНаГраньОбкапывая», и
    /// там же названа цена.
    ///
    /// Заводская тройка — не круглое число, а сумма трёх названных вещей: один
    /// заход на саму грань, один на её осыпание (у гравия и песка в 1.22.7
    /// висит UnstableFalling с fallSidewaysChance 0.75 — падая, они с
    /// вероятностью три четверти сваливаются В СОСЕДНЮЮ клетку и засыпают
    /// только что освобождённую грань) и один на прокоп к соседу, до которого
    /// нет прямой видимости. Всего кругов — <see cref="WholeStone.Faces"/> ×
    /// это число.
    ///
    /// ЭТО ЦЕНА, А НЕ ПРЕДЕЛ ПЕРЕБОРА, — потому ручка и заведена (закон 1):
    /// каждый круг это ЕЩЁ ОДИН СНЕСЁННЫЙ ЧУЖОЙ БЛОК вокруг цели, и при
    /// заводской тройке одно «!добудь» вправе снести до восемнадцати. Хозяину
    /// мира это может быть не по карману, а выключатель
    /// «ОбкапыватьРадиЦельногоБлока» — слишком крупная мера: он отменяет
    /// цельный камень вовсе.
    ///
    /// Ниже единицы не опускается: нулём заходов не освободить ни одной грани,
    /// и обкапывание молча превратилось бы в отказ.
    /// </summary>
    public int WholeStoneRoundsPerFace { get; set; } = 3;

    /// <summary>
    /// Сколько блоков перебрать в реестре, ища источник выпадения (0 — все).
    /// Ограничивать нельзя: в игре блоков около тринадцати тысяч, и первые
    /// четыре тысячи не содержат ни россыпей, ни руд — бот честно отвечал
    /// «в реестре нет блока, с которого падает nugget-copper».
    /// </summary>
    public int RegistryScanLimit { get; set; }

    /// <summary>
    /// С каких блоков падает нужное. Считается по реестру сервера: код блока
    /// подходит, если он сам и есть нужное (камень просят камнем) либо если
    /// нужное есть в его выпадениях (булыжник — с камня, самородок — с россыпи).
    /// </summary>
    public string[] Sources(string wanted)
    {
        if (sourceCache.TryGetValue(wanted, out var cached))
            return cached;

        // Мерка «блок даёт нужное» — ОДНА на весь бот и живёт в сборе
        // (Gathering.Gives): выпадения читает из реестра он, и второй такой же
        // мерке здесь разъехаться с ней было бы делом одного вечера
        var found = new List<string>();
        int seen = 0;
        foreach (string code in ctx.World.SearchBlockCodes(
                     "", RegistryScanLimit > 0 ? RegistryScanLimit : int.MaxValue))
        {
            seen++;
            if (gathering.Gives(code, wanted))
                found.Add(code);
        }
        var result = ShortestFirst(found).ToArray();

        // ПУСТОЙ РЕЕСТР НЕ ЗАПОМИНАЕМ. Реестр блоков приходит пакетом при входе
        // в мир, а спросить поручение могут и раньше (роль поднимается быстрее
        // сети). Запомни мы тогдашний пустой ответ — «не знаю, откуда берётся
        // stone-granite» осталось бы правдой до перезапуска бота, уже при
        // пришедшем реестре
        if (seen > 0)
            sourceCache[wanted] = result;
        return result;
    }

    /// <summary>
    /// САМА ВЕЩЬ ВПЕРЕДИ ЕЁ РАЗНОВИДНОСТЕЙ. Короткий код — это сам блок
    /// («meteorite-iron», «rock-granite»), длинный — его разновидность по
    /// покрову или породе («loosestones-meteorite-iron-water»).
    ///
    /// ЖИВОЙ СЛУЧАЙ (19.08). Порядок был номерами реестра, то есть никаким, и
    /// заказчик прочёл в журнале: «беру stone-meteorite-iron: подходящих блоков
    /// в реестре 5 (например loosestones-meteorite-iron-free,
    /// loosestones-meteorite-iron-water, …)». Сам метеорит в списке был —
    /// пятым. Человек прочёл это как «бот собрался за щебнем», и был прав в
    /// главном: названо было не то, что он имел в виду.
    ///
    /// Порядок один на всех — и на строку в чат, и на список в окне: посчитай
    /// окно свой, и человек увидел бы там одно, а в чате другое. Та же мерка
    /// «короткий код — сама вещь» уже стоит у подсказки (<see cref="Pick"/>).
    /// </summary>
    public static IEnumerable<string> ShortestFirst(IEnumerable<string> codes) =>
        codes.OrderBy(c => c.Length).ThenBy(c => c, StringComparer.Ordinal);

    private static bool Matches(string code, string wanted) =>
        code.Contains(wanted, StringComparison.OrdinalIgnoreCase);

    // ======================================================================
    //  ЧЕЛОВЕЧЕСКОЕ СЛОВО → КОД ИГРЫ
    // ======================================================================

    /// <summary>
    /// Языковые файлы игры — ТЕ ЖЕ, что читает журнал урона. Второго чтения
    /// файлов в проекте быть не должно (правило 1), поэтому берём готовые.
    /// Берём лениво: журнал урона собирается позже поручений, и спросить его
    /// в конструкторе значило бы получить null навсегда.
    ///
    /// Открыто на запись ради стенда: настоящие файлы игры на машине приёмки
    /// есть не всегда, а проверять поиск по имени надо и без них.
    /// </summary>
    public GameLang? Lang
    {
        get => lang ??= ctx.Damage?.Lang;
        set => lang = value;
    }

    private GameLang? lang;

    private readonly Dictionary<string, NameLookup> nameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ПОНЯТЬ, ЧТО ЧЕЛОВЕК НАЗВАЛ. «медь», «rock-granite», «метеоритное железо»,
    /// «метиоритное жилезо» — на входе что угодно.
    ///
    /// ЖИВОЙ ОТКАЗ, ради которого написано. Заказчик: «нужно ещё придумать, как
    /// туда вписывать в команду что угодно, допустим метеоритное железо».
    /// Бот отвечал: «не знаю, откуда берётся метеоритное железо — в реестре
    /// сервера такого выпадения нет». Реестр был ни при чём: блок в нём есть и
    /// зовётся «meteorite-iron». Не было моста от русского слова к коду —
    /// а мост этот лежит в языковых файлах игры, которые бот и так читает.
    ///
    /// ПОРЯДОК ВОПРОСОВ ВАЖЕН, и он не косметический:
    ///   1. реестр знает такой код — значит человек назвал код, и переводить
    ///      нечего. Без этой ступени «pickaxe» из запасов
    ///      (<see cref="Stock"/>) ушёл бы искаться среди русских имён;
    ///   2. с этим словом уже что-то падает — старый путь работает, не трогаем;
    ///   3. и только теперь — языковые файлы.
    ///
    /// НА НЕСКОЛЬКО ПОХОЖИХ — ПЕРЕСПРОС, А НЕ ДОГАДКА. Но «несколько» считается
    /// по ЛУЧШЕЙ ступени точности: если имя названо целиком и такое одно, то
    /// три десятка вещей, у которых оно внутри («Нож из метеоритного железа»),
    /// выбору не мешают. Иначе бот переспрашивал бы всегда и ни на что не
    /// годился.
    /// </summary>
    public NameLookup Resolve(string wanted)
    {
        wanted = wanted.Trim();
        if (wanted.Length == 0)
            return new NameLookup(null, "не сказано, что добывать", []);

        // РЕЕСТР ЕЩЁ НЕ ПРИШЁЛ — это «пока не знаю», а не «такого нет», и
        // запоминать такой ответ нельзя ни в коем случае: он перестанет быть
        // правдой через секунду, когда сервер пришлёт реестр
        if (!ctx.World.SearchBlockCodes("", 1).Any())
            return new NameLookup(null,
                $"реестр блоков сервера ещё не пришёл — что такое «{wanted}», " +
                "сказать пока не могу", []);

        if (nameCache.TryGetValue(wanted, out var had))
            return had;

        var answer = Work();
        nameCache[wanted] = answer;
        return answer;

        NameLookup Work()
        {
            // 1. Это код игры — так его и понимаем
            if (ctx.World.BlockCodeToId(wanted) != null || ctx.World.ItemCodeToId(wanted) != null)
                return new NameLookup(wanted, $"{wanted} — код игры, беру как есть", []);

            // 2. Со словом уже что-то падает: путь, работавший до сегодня
            if (Sources(wanted).Length > 0)
                return new NameLookup(wanted, $"{wanted} — по этому слову в реестре есть " +
                                              "что ломать, беру как есть", []);

            // 3. Человеческое имя из языковых файлов игры
            if (Lang is not { NameCount: > 0 } words)
                return new NameLookup(null,
                    $"не знаю, откуда берётся {wanted} — в реестре сервера такого выпадения нет, " +
                    "а языковых файлов игры у меня нет и по имени искать нечем", []);

            var found = words.FindByName(wanted);
            if (found.Count == 0)
                return new NameLookup(null,
                    $"не знаю, что такое «{wanted}»: ни кода с таким именем в реестре сервера, " +
                    $"ни такого имени среди {words.NameCount} названий игры", []);

            // РЕЕСТР — ПОСЛЕДНЕЕ СЛОВО. В языке игры есть имена вещей, которых
            // на ЭТОМ сервере нет (выключенный мод, урезанная сборка). Обещать
            // по языковому файлу то, чего в реестре нет, — то же враньё
            var real = found.Where(t => Knows(t.Code)).ToList();
            if (real.Count == 0)
                return new NameLookup(null,
                    $"«{wanted}» в языке игры есть ({found[0].Name}, {found[0].Code}), " +
                    "а в реестре ЭТОГО сервера такого нет — мир собран без него", []);

            // Лучшая ступень точности: ниже неё выбор уже не выбор
            int best = real.Min(t => t.Nearness);
            var top = real.Where(t => t.Nearness == best).ToList();
            var byName = top
                .GroupBy(t => GameLang.Tidy(t.Name), StringComparer.Ordinal)
                .ToList();

            if (byName.Count > 1)
                return new NameLookup(null, Ask(wanted, byName.Select(g => g.First()).ToList()),
                    top);

            var one = Pick(top);
            string работать = Workable(one.Code);
            return new NameLookup(работать,
                $"«{wanted}» — это {one.Name} ({one.Code})" +
                (работать == one.Code ? "" : $", ищу по «{работать}»") +
                (best == 3 ? " (принял за опечатку в одну букву)" : "") +
                (top.Count > 1
                    ? $"; под тем же именем ещё {top.Count - 1} " +
                      $"({string.Join(", ", top.Where(t => t.Code != one.Code).Take(3).Select(t => t.Code))})"
                    : ""),
                top);
        }
    }

    /// <summary>
    /// ЧТО ПРЕДЛОЖИТЬ, ПОКА ЧЕЛОВЕК ЕЩЁ НАБИРАЕТ. Лучшие первыми, пусто — ничего
    /// подходящего не нашлось.
    ///
    /// ЖИВАЯ ПРОСЬБА ЗАКАЗЧИКА 16.08, ДОСЛОВНО: «при выдаче задания на поиск
    /// чего-либо и добычу неплохо выпадающий список при вводе с иконкой, чтобы
    /// я точно знал, что бот понял меня». До этого он набирал «медь» в поле
    /// «что» и узнавал, что из этого вышло, ЧЕРЕЗ СОРОК МИНУТ — когда бот
    /// возвращался не с тем или с пустыми руками.
    ///
    /// ВТОРОГО ПОИСКА ПО ИМЕНАМ ЗДЕСЬ НЕТ. Имена ищет тот же
    /// <see cref="GameLang.FindByName"/>, что и <see cref="Resolve"/>, реестр
    /// сервера спрашивается тем же <see cref="Knows"/>, а маски разворачивает
    /// тот же <see cref="Expand"/>. Разница ровно одна: <see cref="Resolve"/>
    /// обязан выбрать ОДНО и отказаться, если выбрать нельзя, а подсказка
    /// показывает несколько и выбирает человек.
    ///
    /// РЕЕСТР — ПОСЛЕДНЕЕ СЛОВО и здесь. Предложить вещь, которой на ЭТОМ
    /// сервере нет (выключенный мод, урезанная сборка), значит соврать
    /// красивее прежнего: человек выберет её из списка с картинкой и будет
    /// уверен, что бот понял.
    /// </summary>
    /// <param name="typed">что набрано в поле</param>
    /// <param name="limit">сколько строк показать</param>
    public IReadOnlyList<ThingName> Suggest(string typed, int limit = 8)
    {
        typed = typed.Trim();
        if (typed.Length == 0 || limit <= 0)
            return [];

        var found = new List<ThingName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Взять(string code, int nearness)
        {
            if (code.Length == 0 || !seen.Add(code))
                return;
            found.Add(new ThingName(code, Lang?.NameOf(code) ?? code,
                ctx.World.BlockCodeToId(code) != null, nearness));
        }

        // 1. НАБРАН КОД — он и первый. Без этой ступени «pickaxe» из запасов
        // уходил бы искаться среди русских имён, ровно как в Resolve
        if (ctx.World.BlockCodeToId(typed) != null || ctx.World.ItemCodeToId(typed) != null)
            Взять(typed, 0);

        // 2. ЧЕЛОВЕЧЕСКОЕ ИМЯ. Маску языка («ore-*-limonite-*» = «Железная
        // руда») разворачиваем в настоящие коды: показать человеку строку со
        // звёздочкой значило бы предложить ему то, чего в реестре нет ни одного
        foreach (var вещь in Lang?.FindByName(typed) ?? [])
        {
            if (found.Count >= limit)
                break;
            if (!вещь.Code.Contains('*'))
            {
                if (Knows(вещь.Code))
                    found.Add(new ThingName(вещь.Code, вещь.Name, вещь.IsBlock, вещь.Nearness));
                seen.Add(вещь.Code);
                continue;
            }
            foreach (string настоящий in Expand(вещь.Code).Take(limit - found.Count))
                if (seen.Add(настоящий))
                    found.Add(new ThingName(настоящий, вещь.Name,
                        ctx.World.BlockCodeToId(настоящий) != null, вещь.Nearness));
        }

        // 3. КУСОК КОДА. Человек набирает и латиницей — «nugget», «copper»,
        // «plank». Ищет это РЕЕСТР СЕРВЕРА своим же поиском, поэтому работает и
        // для модовых вещей, о которых языковой файл игры не знает вовсе
        if (found.Count < limit)
            foreach (string code in ctx.World.SearchBlockCodes(typed, limit)
                         .Concat(ctx.World.SearchItemCodes(typed, limit)))
            {
                if (found.Count >= limit)
                    break;
                Взять(code, 2);
            }

        return found
            .OrderBy(t => t.Nearness)
            // Короткий код — сама вещь, длинный — её разновидность («nugget-
            // copper» против «nugget-copper-rich»). Человек, назвавший материал,
            // имеет в виду материал (тот же порядок, что у Pick)
            .ThenBy(t => t.Code.Length)
            .ThenBy(t => t.Code, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// ПЕРЕСПРОС СПИСКОМ — словами заказчика: «нашёл три: …; какое?».
    /// Молчаливая догадка тут хуже отказа: бот уйдёт на полчаса не за тем.
    /// </summary>
    private static string Ask(string wanted, IReadOnlyList<ThingName> choices)
    {
        var shown = choices.Take(6).Select(t => $"{t.Name} ({t.Code})").ToList();
        return $"под «{wanted}» подходит {choices.Count}: {string.Join("; ", shown)}" +
               (choices.Count > shown.Count ? $" и ещё {choices.Count - shown.Count}" : "") +
               " — какое из них?";
    }

    /// <summary>
    /// КОД, С КОТОРЫМ МОЖНО РАБОТАТЬ, — из кода языка.
    ///
    /// У игры один перевод часто подписывает ЦЕЛОЕ СЕМЕЙСТВО блоков:
    /// «ore-*-limonite-*» — это «Железная руда», и блока ровно с таким кодом в
    /// реестре нет ни одного. Искать по нему — то же, что искать по «*».
    ///
    /// Берём самый длинный кусок без звёздочки — «limonite». Поручение ищет
    /// ПОДСТРОКОЙ в коде блока и в его выпадениях (см. <see cref="Sources"/>),
    /// и именно этот кусок отличает лимонит от всего прочего. Заодно этим же
    /// куском считается и принесённое: в коде самородка стоит он же
    /// («nugget-limonite»).
    /// </summary>
    private static string Workable(string code)
    {
        if (!code.Contains('*'))
            return code;
        string longest = code.Split('*').OrderByDescending(p => p.Length).First().Trim('-');
        return longest.Length > 0 ? longest : code;
    }

    /// <summary>
    /// Из нескольких кодов одного имени берём БЛОК и покороче: блок можно
    /// пойти и сломать, а короткий код — это сама вещь, а не её разновидность.
    /// </summary>
    private static ThingName Pick(IReadOnlyList<ThingName> same) =>
        same.OrderByDescending(t => t.IsBlock)
            .ThenBy(t => t.Code.Length)
            .ThenBy(t => t.Code, StringComparer.Ordinal)
            .First();

    /// <summary>
    /// Знает ли реестр сервера такой код. Звёздочку разворачиваем: у игры один
    /// перевод часто накрывает семейство («ore-*-limonite-*»), и ни одного
    /// блока ровно с таким кодом в реестре нет.
    ///
    /// ОТКРЫТО НАРУЖУ РАДИ ЗАПАСОВ: «в языке игры такое имя есть» и «на ЭТОМ
    /// сервере такая вещь есть» — разные ответы, и разбор человеческого слова
    /// в вид инструмента (<see cref="Stock.WhatToolIs"/>) обязан спрашивать
    /// оба. Второй такой же проверки там заводить нельзя: она разъехалась бы с
    /// этой на первой же маске.
    /// </summary>
    public bool Knows(string code)
    {
        if (!code.Contains('*'))
            return ctx.World.BlockCodeToId(code) != null || ctx.World.ItemCodeToId(code) != null;
        return Expand(code).Count > 0;
    }

    /// <summary>
    /// Настоящие коды реестра под маской вроде «ore-*-limonite-*». Ищем по
    /// самому длинному куску без звёздочки, а уже потом сверяем маску целиком:
    /// перебирать все тринадцать тысяч блоков ради каждой маски незачем.
    /// </summary>
    public IReadOnlyList<string> Expand(string mask)
    {
        if (!mask.Contains('*'))
            return Knows(mask) ? [mask] : [];

        string anchor = mask.Split('*').OrderByDescending(p => p.Length).First();
        var rx = new System.Text.RegularExpressions.Regex(
            "^" + string.Join(".*", mask.Split('*')
                .Select(System.Text.RegularExpressions.Regex.Escape)) + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return ctx.World.SearchBlockCodes(anchor, int.MaxValue)
            .Concat(ctx.World.SearchItemCodes(anchor, int.MaxValue))
            .Where(c => rx.IsMatch(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// ГДЕ ЭТО БЕРЁТСЯ — честный ответ вместо «не знаю».
    ///
    /// Заказчик: «если вещь есть, но добывается не из блока (метеоритное
    /// железо — из метеорита), сказать ГДЕ она берётся, а не „не знаю"».
    /// Слиток метеоритного железа и вправду ни с одного блока не падает: его
    /// выплавляют. Но материал у него игра называет сама
    /// (<see cref="GameLang.MaterialName"/>), а по имени материала находится
    /// настоящий блок мира — тот самый метеорит. Ни одного выдуманного факта:
    /// всё из реестра сервера и языковых файлов игры.
    /// </summary>
    public string WhereFrom(string code)
    {
        // Человеку — имя, себе — код. «ingot-meteoriciron» ему не говорит
        // ничего, «Слиток метеоритного железа» говорит всё
        string это = Lang?.NameOf(code) is { Length: > 0 } n ? $"{n} ({code})" : code;

        var sources = Sources(code);
        if (sources.Length > 0)
            return $"{это} падает с {sources.Length} видов блоков " +
                   $"(например {string.Join(", ", sources.Take(3))})";

        if (Lang?.MaterialName(code) is not { } material)
            return $"{это} ни с одного блока в реестре сервера не падает — " +
                   "в мир он не кладётся вовсе, его делают или выплавляют";

        var изЧего = Lang.FindByName(material)
            .Where(t => t.IsBlock && t.Nearness == 0 && Knows(t.Code))
            .ToList();
        return $"{это} ни с одного блока не падает — его делают или выплавляют. " +
               $"Материал у него «{material}»" +
               (изЧего.Count > 0
                   ? $", а он в мире есть: {string.Join(", ", изЧего.Take(3).Select(t => t.Code))} — " +
                     $"скажи «{material}», и схожу за ним"
                   : ", а блока с таким именем в реестре этого сервера нет");
    }

    /// <summary>
    /// Какими инструментами блок отдаёт нужное.
    ///
    /// Именно СПИСОК, а не один: у одного и того же выпадения в игре обычно
    /// несколько записей под разные инструменты — трава отдаёт сухую траву
    /// и под нож, и под косу (в ассетах два drops-правила подряд). Брать
    /// только первое значило бы: «нужен нож» — и бот с косой в руках честно
    /// отказывался косить луг.
    ///
    /// Пустой список — годится любой инструмент и голые руки.
    /// Значения — типы из EnumTool реестра сервера, поэтому работает и для
    /// модовых блоков и модовых инструментов.
    /// </summary>
    public IReadOnlyList<int> DropToolsFor(string blockCode, string wanted)
    {
        var tools = new List<int>();
        bool anyMatch = false;
        foreach (var drop in ctx.World.GetGameBlockByCode(blockCode)?.Drops ?? [])
        {
            // Сами Drops, а не готовые коды: здесь нужен ещё и ИНСТРУМЕНТ
            // правила (drop.Tool). Код же стопки переводит реестр — тем же
            // одним местом, что и всем прочим (WorldModel.CodeOfStack)
            if (drop == null ||
                ctx.World.CodeOfStack(drop.ResolvedItemstack) is not { } code ||
                !Matches(code, wanted))
                continue;

            anyMatch = true;
            if (drop.Tool is { } tool)
            {
                if (!tools.Contains((int)tool))
                    tools.Add((int)tool);
            }
            else
                return [];   // есть правило без требования — сгодится что угодно
        }
        return anyMatch ? tools : [];
    }

    /// <summary>
    /// Что из своего годится как инструмент нужного типа (null — нет такого).
    /// Тип сверяется с реестром, а не с названием: «нож» может называться
    /// как угодно, в том числе в моде.
    /// </summary>
    public string? OwnedToolOfType(int toolType) =>
        ctx.Self.FindBestItem(s =>
            s.Code is { } c && ctx.World.GetToolTypeByCode(c) == toolType ? 1 : 0)?.Content.Code;

    /// <summary>Сколько нужного у бота сейчас (включая сумки).</summary>
    public int Have(string wanted) => hands.CountOf(wanted);

    /// <summary>
    /// Добыть нужное количество и, если задан сундук, сложить туда.
    ///
    /// Успех считается ТОЛЬКО по факту: сколько прибавилось в инвентаре, а при
    /// доставке — сколько легло в сундук. «Сходил и постарался» успехом не
    /// считается (правило 4 проекта).
    /// </summary>
    public async Task<ErrandReport> FetchAsync(string wanted, int count,
        BlockPos? deliverTo = null, CancellationToken ct = default)
    {
        if (count <= 0)
            return new ErrandReport(wanted, count, 0, "количество должно быть больше нуля");

        // ЧТО ИМЕННО ПРОСЯТ. Человек называет вещь по-человечески («метеоритное
        // железо»), а реестр сервера знает её кодом («meteorite-iron»). Перевод
        // живёт в одном месте (<see cref="Resolve"/>) и отвечает тремя разными
        // ответами: понял / надо переспросить / не знаю. Переспрос и незнание —
        // это КОНЕЦ поручения, а не повод сходить впустую: раньше бот на такое
        // слово честно печатал «не знаю, откуда берётся», а потом всё равно
        // уходил в два пустых круга поиска
        string сказано = wanted;
        var lookup = Resolve(wanted);
        OnLog?.Invoke(lookup.Say);
        if (lookup.Code is not { } понято)
            return new ErrandReport(сказано, count, 0, lookup.Say);
        wanted = понято;

        int had = Have(wanted);
        неВзятьЦелым = null;   // причина прошлого наряда этому наряду не указ
        var deadline = DateTime.UtcNow.AddSeconds(MaxSeconds);
        var sources = Sources(wanted);

        // ГДЕ ОНО БЕРЁТСЯ — ВСЛУХ. Вещь может быть настоящей, а из блоков не
        // выпадать вовсе (слиток метеоритного железа выплавляют). Прежний ответ
        // «не знаю, откуда берётся» был неправдой: бот знает и материал, и блок
        // этого материала, — см. <see cref="WhereFrom"/>.
        //
        // ПОИСК ПРИ ЭТОМ НЕ ОТМЕНЯЕТСЯ, и это не мягкотелость: ягоды с куста
        // снимаются рукой (BlockBehaviorHarvestable), а в списке выпадений
        // куста их нет вовсе — пустой список источников для еды значит
        // «ломать нечего», а не «взять негде»
        string? негдеЛомать = null;
        if (sources.Length == 0)
        {
            негдеЛомать = WhereFrom(wanted);
            OnLog?.Invoke(негдеЛомать);
        }
        else
            OnLog?.Invoke($"беру {wanted}: подходящих блоков в реестре {sources.Length} " +
                          $"(например {string.Join(", ", sources.Take(3))})");

        BlockPos? startedAt = ctx.Self.Position is { } p0
            ? new BlockPos((int)Math.Floor(p0.X), (int)Math.Floor(p0.Y), (int)Math.Floor(p0.Z))
            : null;

        int idleRounds = 0;
        while (Have(wanted) - had < count && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            int before = Have(wanted);

            // 1. Лежит на земле — это самое дешёвое
            if (await TakeLyingAsync(wanted, ct))
            {
                idleRounds = 0;
                continue;
            }

            // 2. Растёт (ягоды, грибы) — собираем, если нужное съедобное
            if (await ForageAsync(wanted, ct))
            {
                idleRounds = 0;
                continue;
            }

            // 3. Ломаем блок-источник
            if (await MineSourceAsync(wanted, sources, ct))
            {
                idleRounds = 0;
                continue;
            }

            // Ничего не нашли: два пустых круга подряд — значит рядом больше нет
            if (Have(wanted) == before && ++idleRounds >= 2)
            {
                OnLog?.Invoke($"{wanted} рядом больше не нахожу");
                break;
            }
        }

        int gained = Have(wanted) - had;

        // ПОЧЕМУ НЕ ВЫШЛО — В САМОМ ОТЧЁТЕ, а не только в журнале. Отчёт уходит
        // человеку в чат ответом на «!добудь», а журнал он читать не обязан:
        // «добыл ×0 — больше рядом нет» на слиток метеоритного железа звучит
        // как «не повезло», хотя на деле его вообще не добывают из земли
        // ПРИЧИНА — ТА, ЧТО БЫЛА НА САМОМ ДЕЛЕ. «Больше рядом нет» про породу,
        // на которой бот стоит, — это неправда, и заказчик читает её в чате.
        //
        // ЗАСТАВА «gained == 0» ОТСЮДА СНЯТА, И ЭТО ПОЧИНКА. Она стояла на обеих
        // настоящих причинах, то есть работала только при ПОЛНОМ нуле. Добыв
        // три гранита из двадцати и упёршись в шестую грань, бот снова говорил
        // человеку « — больше рядом нет», стоя на граните: ровно ту фразу, ради
        // починки которой всё и затевалось. Сюда мы вообще попадаем только при
        // gained < count, то есть работа И ТАК не доделана, и настоящая причина
        // нужна человеку не меньше, чем при нуле.
        string почему =
            негдеЛомать is { } нет ? " — " + нет
            : неВзятьЦелым is { Length: > 0 } целым ? " — " + целым
            : " — больше рядом нет";

        if (deliverTo is not { } chest)
            return new ErrandReport(wanted, count, gained,
                gained >= count
                    ? $"добыл {wanted} ×{gained}"
                    : $"добыл {wanted} ×{gained} из {count}{почему}");

        // Возвращаемся и складываем
        _ = startedAt;   // место старта пока нужно только для журнала
        OnLog?.Invoke($"несу {wanted} ×{gained} к сундуку {chest}");
        int stored = await DeliverAsync(wanted, chest, ct);
        return new ErrandReport(wanted, count, stored,
            stored >= count
                ? $"принёс и сложил {wanted} ×{stored}"
                : $"сложил {wanted} ×{stored} из {count}");
    }

    /// <summary>
    /// Подобрать нужное, если оно лежит на земле рядом.
    ///
    /// ОТБОР ОТДАН САМОМУ СБОРУ, и это не косметика. Раньше поручение брало
    /// восемь ближайших находок и уже потом выкидывало из них неподходящие —
    /// а находки идут по ЦЕННОСТИ: восемь самородков рядом, и камня, за которым
    /// пришли, в списке нет вовсе. Так и вышло «не подбирал камни для
    /// инструментов» при камнях под ногами.
    /// </summary>
    private async Task<bool> TakeLyingAsync(string wanted, CancellationToken ct)
    {
        var lying = gathering.FindLoose(SearchRadius, limit: 8, wanted: wanted);
        if (lying.Count == 0)
            return false;

        foreach (var find in lying)
        {
            if (ct.IsCancellationRequested)
                break;
            if (await gathering.PickUpAsync(find.Pos, ct))
                return true;
        }
        return false;
    }

    /// <summary>Собрать нужное с куста или гриба, если это дикая еда.</summary>
    private async Task<bool> ForageAsync(string wanted, CancellationToken ct)
    {
        if (ctx.World.GetSatietyByCode(wanted) <= 0)
            return false;   // не еда — незачем искать по кустам
        return (await foraging.GatherAsync(SearchRadius, maxPlaces: 3, 90, null, ct)).Success;
    }

    /// <summary>
    /// БЛИЖАЙШИЙ БЛОК, С КОТОРОГО ЭТО ПАДАЕТ (null — рядом такого нет).
    ///
    /// ЗАЧЕМ ОТКРЫТО НАРУЖУ. «Такое бывает в игре» и «такое есть ЗДЕСЬ» — два
    /// разных ответа, и до сих пор снаружи можно было получить только первый
    /// (<see cref="Sources"/> — обход реестра). Живой случай, стоивший недели:
    /// разбор запасов выбирал топор по длине кода, получал «axe-chert»,
    /// объявлял ветку сходящейся — потому что кремнистый сланец в РЕЕСТРЕ есть,
    /// — и цепочка каждый раз умирала об «stone-chert рядом больше не нахожу».
    /// Вокруг при этом лежал перидотит, из которого топор точно такой же.
    ///
    /// ВТОРОГО ПОИСКА ТУТ НЕТ: это ровно тот вопрос и ровно с той же меркой
    /// видимости, каким сам поручение и ищет, что ломать
    /// (<see cref="MineSourceAsync"/> зовёт этот же метод). Разойдись они — и
    /// разбор обещал бы то, чего исполнитель не находит.
    ///
    /// ГДЕ ОНИ ВСЁ-ТАКИ РАСХОДЯТСЯ, И ЭТО НАДО ЗНАТЬ: про ЧУЖОЕ спрашивает
    /// только исполнитель. Найденное здесь может стоять в чужой заявке — тогда
    /// поручение по нему откажется (см. Claims в <see cref="MineSourceAsync"/>),
    /// а разбор поход уже пообещал. Сюда проверку не поднять: обход мира
    /// отбирает клетки по КОДУ блока, а хозяин у клетки — свойство места, и
    /// ближайший непроверенный камень пришлось бы искать вторым обходом. Пока
    /// это стоит помнить так: «нет вокруг» — ответ честный, «есть вокруг» —
    /// ответ про камень, а не про право его сломать.
    /// </summary>
    public BlockPos? NearestSource(string wanted, string[]? sources = null)
    {
        sources ??= Sources(wanted);
        if (sources.Length == 0 || ctx.Self.Position is not { } p)
            return null;
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        var set = new HashSet<string>(sources, StringComparer.OrdinalIgnoreCase);
        return ctx.World.FindNearestBlock(c => set.Contains(c), here, SearchRadius, height: 8);
    }

    /// <summary>
    /// ЕСТЬ ЛИ ЭТО РЯДОМ ПРЯМО СЕЙЧАС — двумя способами, какими бот и берёт:
    /// лежит на земле или стоит блоком-источником.
    ///
    /// Спрашивает разбор «что из чего» (<see cref="Stock.PlanFor"/>) перед тем,
    /// как поставить шаг «добыть». Без этого вопроса разбор обещает поход за
    /// тем, чего в этой местности нет, — и обещает его КАЖДЫЙ заход.
    ///
    /// «НЕ ВИЖУ» — ЭТО НЕ «НЕТ», И ЭТО ГЛАВНАЯ ОГОВОРКА ПРАВИЛА. Бот вне мира,
    /// бот на первых секундах входа и бот в стенде видят вокруг РОВНО НИЧЕГО:
    /// чанков ещё нет. Ответь мы тогда «рядом такого нет», и разбор объявил бы
    /// тупиком всё на свете — включая то, что лежит в двух шагах. Отсутствие
    /// карты не есть отсутствие камня, поэтому незнающий отвечает «да»: пусть
    /// поручение сходит и скажет по факту.
    /// </summary>
    public bool NearbyNow(string wanted) =>
        ctx.World.LoadedChunkCount == 0 || ctx.Self.Position == null ||
        gathering.FindLoose(SearchRadius, limit: 1, wanted: wanted).Count > 0 ||
        NearestSource(wanted) != null;

    /// <summary>Найти и сломать ближайший блок, дающий нужное.</summary>
    private async Task<bool> MineSourceAsync(string wanted, string[] sources, CancellationToken ct)
    {
        if (NearestSource(wanted, sources) is not { } target)
            return false;

        // ЧУЖОЕ СПРАШИВАЕМ И ЗДЕСЬ. Поручение — это автопоиск: клетку выбирает
        // бот, а не человек, и ближайшая доска вполне может оказаться стеной
        // соседского сарая. Сам отказ Claims огласит с числом и причиной
        if (ctx.Claims is { } owners && !owners.Suits(target))
            return false;

        string code = ctx.World.GetBlockCode(target) ?? "?";

        // ИНСТРУМЕНТ РЕШАЕТ, ВЫПАДЕТ ЛИ НУЖНОЕ. Живьём бот полчаса косил луг
        // рукой и не получил ни травинки: сухая трава падает только под нож
        string? tool = null;
        var accepted = DropToolsFor(code, wanted);
        if (accepted.Count > 0)
        {
            foreach (int toolType in accepted)
                if (OwnedToolOfType(toolType) is { } owned)
                {
                    tool = owned;
                    break;
                }
            if (tool == null)
            {
                OnLog?.Invoke($"{code} отдаёт {wanted} только под инструменты " +
                              $"типов {string.Join("/", accepted)}, а таких у меня нет");
                return false;
            }
        }

        // ПОРЯДОК УДАРОВ РЕШАЕТ, ВЫПАДЕТ ЛИ НУЖНОЕ, — ровно так же, как решает
        // инструмент выше, и живой случай тот же по форме.
        //
        // ЖИВОЙ СЛУЧАЙ. «Добудь rock-granite 20»: источник выбирался меркой
        // Gathering.Gives — «блок сам зовётся нужным». Гранит и вправду зовётся
        // rock-granite, а выпадает с него stone-granite, щебёнка (rock.json,
        // dropsByType). Бот честно бил породу, честно подбирал щебёнку, и ни
        // одного rock-granite в сумке не появлялось НИКОГДА — наряд доходил до
        // конца отведённого времени и говорил «больше рядом нет», хотя порода
        // была под ногами. Заказчик сказал это своими словами: «чтобы мы не
        // копали из камня, когда слой толще 1, то нужно „шахматкой“ копать для
        // добычи цельных блоков».
        //
        // Правило игры одно на весь проект и живёт в WholeStone; здесь — руки
        bool обкопал = false;
        if (WholeStone.OnlyByIsolation(ctx.World, code, gathering.DropsOf(code), wanted))
        {
            if (!await FreeAllSidesAsync(target, code, wanted, ct))
                return false;
            обкопал = true;
        }

        if (!await hands.ReachForAsync(target, ct: ct))
            return false;

        var result = await mining.BreakAsync(target, ct, withTool: tool);
        if (!result.Success)
        {
            // ОБКОПАННАЯ КЛЕТКА МОГЛА ОСЫПАТЬСЯ САМА, и это не отказ, а успех:
            // при включённом в мире allowFallingBlocks поведение
            // BreakIfFloating ломает повисший блок само, едва изменится сосед
            // (BlockBehaviorBreakIfFloating.OnNeighbourBlockChange), и целый
            // блок уже лежит на земле.
            //
            // ТОЛЬКО ПОСЛЕ СВОЕГО ЖЕ ОБКАПЫВАНИЯ, а не на всякое «тут пусто»:
            // успех без прибавки в сумке обнуляет счётчик пустых кругов
            // (см. FetchAsync), и наряд крутился бы до конца срока, объявляя
            // работу там, где её не было
            if (обкопал && result.Why == MiningRefusal.Nothing)
            {
                await mining.CollectDropsAsync(6, 8, ct);
                return true;
            }
            OnLog?.Invoke($"{code} {target}: {result}");
            return false;
        }
        await mining.CollectDropsAsync(6, 8, ct);
        return true;
    }

    /// <summary>
    /// ОБКОПАТЬ БЛОК СО ВСЕХ ШЕСТИ СТОРОН — «шахматка» на одну клетку.
    ///
    /// Это не новое правило, а РУКИ к правилу игры: целым камень падает только
    /// когда ни одна из шести граней не упирается в твердь
    /// (<see cref="WholeStone"/>, разбор BreakIfFloating и rock.json). Сама
    /// игра говорит об этом человеку в справочнике блока: «Full block can be
    /// obtained by breaking all adjacent blocks».
    ///
    /// СПИСОК СТОРОН ПЕРЕСПРАШИВАЕТСЯ У МИРА НА КАЖДОМ КРУГЕ, а не берётся
    /// один раз. Пока обкапываешь, мир меняется сам: сосед мог осыпаться
    /// (гравий), мог упасть повисший блок, могла утечь вода. Считай мы по
    /// старому списку — бот бил бы по воздуху и получал отказ «ломать нечего»
    /// там, где всё уже сделано.
    ///
    /// НИЗ — ПОСЛЕДНИМ (порядок задаёт <see cref="WholeStone.HoldingSides"/>):
    /// пока стоят боковые, бот работает стоя рядом, а сними пол под целью
    /// раньше — и до оставшихся граней придётся тянуться через дыру.
    ///
    /// ЧЕСТНЫЙ ОТКАЗ ВМЕСТО МОЛЧАЛИВОГО ТРУДА. Не поддался сосед, не
    /// дотянуться, некуда сойти — работа прекращается И НАЗЫВАЕТСЯ ПРИЧИНА. Это
    /// и есть «слой толще 1» из слов заказчика: у однослойной кладки или у
    /// плиты на коренной породе шестая грань не освобождается никогда, и целого
    /// блока оттуда не будет, сколько ни бей.
    /// </summary>
    private async Task<bool> FreeAllSidesAsync(BlockPos cell, string code, string wanted,
        CancellationToken ct)
    {
        // ПРИЧИНА ГОВОРИТСЯ И В ЖУРНАЛ, И В ОТЧЁТ. Журнал человек читать не
        // обязан, а отчёт приходит ему в чат ответом на «!добудь» — и без этой
        // пары «больше рядом нет» осталось бы последним словом про породу, на
        // которой бот стоит
        bool Отказ(string почему)
        {
            неВзятьЦелым = почему;
            OnLog?.Invoke(почему);
            return false;
        }

        int held = WholeStone.HoldingSides(ctx.World, cell).Count;
        if (held == 0)
            return true;    // уже висит: бей и получай целый

        if (!TakeWhole)
            return Отказ($"{code} отдаёт {wanted} только ЦЕЛЫМ блоком, а целым он падает лишь " +
                         "тогда, когда сняты все шесть соседей (правило игры BreakIfFloating). " +
                         "Обкапывать не велено ручкой «ОбкапыватьРадиЦельногоБлока» — за этим не иду");

        OnLog?.Invoke($"{code} {cell}: беру ЦЕЛЫМ — обкапываю {held} сторон(ы), " +
                      "иначе с камня падает щебёнка");

        // ЧИСЛО КРУГОВ — ЭТО ЧИСЛО ГРАНЕЙ У КУБА, ЗАПАС НА ТЯГОТЕНИЕ И ЗАПАС НА
        // ПРОКОП К ЗАМУРОВАННОМУ СОСЕДУ.
        //
        // Это не порог выгоды («стоит ли платить столькими соседями») — на этот
        // вопрос отвечает ОДНА ручка «ОбкапыватьРадиЦельногоБлока», — а предел
        // самого перебора. Здесь стояло ровно шесть с припиской «больше шести
        // означало бы, что кто-то ставит блоки нам навстречу», и это неправда
        // про игру: их ставит ТЯГОТЕНИЕ. У гравия и песка в 1.22.7 висит
        // UnstableFalling с fallSidewaysChance 0.75 — падая, они с вероятностью
        // три четверти сваливаются В СОСЕДНЮЮ клетку, то есть освобождённая
        // грань засыпается сама. Один такой круг съеден — и перебор кончался
        // молча, а человек читал в чате «больше рядом нет».
        //
        // Запас — два лишних круга на грань: один на осыпание, один на прокоп
        // к соседу, до которого нет прямой видимости (см. ниже, там же
        // разобрана геометрия ровного выхода породы). Этого хватает на живой
        // случай — четыре боковых, один прокоп и низ, шесть ударов из
        // восемнадцати кругов — и мало для вечной работы. Число граней берём у
        // игры (BlockFacing.ALLFACES), а не пишем шестёркой.
        //
        // А СКОЛЬКО ЗАХОДОВ НА ГРАНЬ — СПРАШИВАЕТСЯ У РОЛИ, потому что это
        // ЦЕНА: каждый круг это ещё один снесённый чужой блок вокруг цели
        // (ручка «ЗаходовНаГраньОбкапывая», там же разобрано, почему заводом
        // три). Своего числа здесь нет.
        //
        // ЧИСЛО СЧИТАЕТСЯ ОДИН РАЗ И ОДНИМ ИМЕНЕМ — потому что в отказе внизу
        // оно НАЗЫВАЕТСЯ ЧЕЛОВЕКУ. Там стояло своё выражение (Faces * 2), и
        // бот говорил «за 12 кругов грани кончиться не успели», отработав их
        // восемнадцать: закон 4 — честный отказ, а не приблизительный
        int кругов = WholeStone.Faces * Math.Max(1, WholeStoneRoundsPerFace);
        for (int round = 0; round < кругов; round++)
        {
            if (ct.IsCancellationRequested)
                return false;

            var hold = WholeStone.HoldingSides(ctx.World, cell);
            if (hold.Count == 0)
                return true;

            // ДО КАКОЙ ГРАНИ МЫ ВООБЩЕ ДОТЯНЕМСЯ — И ЧТО ДЕЛАТЬ, ЕСЛИ НИ ДО
            // ОДНОЙ. Вот здесь и стояла беда, которую доказала приёмка 28.08:
            // круг брал hold[0] и бил по нему, а удар требует ПРЯМОЙ ВИДИМОСТИ
            // (Hands.ReachForAsync → Sight). У соседа, замурованного породой,
            // видимой грани нет ни одной, и до него не дотянуться НИКОГДА —
            // сколько ни стой рядом. Само правило чистое и живёт в WholeStone,
            // здесь только руки: луч и удар.
            var шаг = WholeStone.КудаБить(hold, cell,
                s => hands.Sight.LookAt(s).Visible,
                s => hands.Sight.LookAt(s).BlockedBy);
            if (шаг.Отказ is { } почему)
                return Отказ($"{cell} целым не взять: {почему}");
            if (шаг.Куда is not { } side)
                return true;   // грани кончились между двумя вопросами
            if (шаг.ПрокопКСоседу is { } закрытый)
                OnLog?.Invoke($"{cell}: до соседа {закрытый} не дотянуться — " +
                              $"прокапываюсь к нему через {side}");

            // ПОТОЛОК НАД ГОЛОВОЙ СПРАШИВАЕМ ТЕМ ЖЕ, ЧЕМ СПРАШИВАЕТ КАРЬЕР.
            //
            // Обкапывание вынимает ШЕСТЬ опор подряд, а в rock.json рядом с
            // BreakIfFloating висит UnstableRock с impactDamageMul: 6 и
            // maxSupportDistance от 2 до 6. Про обвалы в проекте знает целый
            // раздел карьера — а этот, новый спрашивающий не спрашивал ничего
            // и мог уронить свод себе на голову с шестикратным уроном. Мерка
            // одна на двоих (Quarry.CeilingSafeAt), второй тут не заводим
            if (ctx.Movement.FeetCell is { } подНогами &&
                !ctx.Quarry.CeilingSafeAt(подНогами))
                return Отказ($"{cell} целым не взять: над головой нестабильная порода — " +
                             "снимать опоры под ней значит уронить свод на себя " +
                             "(impactDamageMul 6). Дайте эту работу там, где над " +
                             "головой открытое небо или крепкий свод");

            // ПОЛ ПОД СОБОЙ НЕ ЛОМАЕМ — упадёшь в свою же работу. Уходят в
            // сторону тем же общим приёмом, каким уходят карьер и дорога
            if (ctx.Movement.FeetCell is { } feet &&
                side == new BlockPos(feet.X, feet.Y - 1, feet.Z) &&
                !await ctx.Movement.StepAsideFromAsync(side, ct: ct))
                return Отказ($"{cell} целым не взять: стою на {side}, а сойти с неё некуда");

            if (!await hands.ReachForAsync(side, ct: ct))
                return Отказ($"{cell} целым не взять: до соседа {side} не дотянуться");

            var got = await mining.BreakAsync(side, ct);
            if (!got.Success)
                return Отказ($"{cell} целым не взять: сосед {side} не поддался ({got}) — " +
                             "шестую грань тут не освободить");
        }
        // КРУГИ КОНЧИЛИСЬ, А ГРАНИ ОСТАЛИСЬ — ЭТО ОТКАЗ, И ОН ОБЯЗАН ЗВУЧАТЬ.
        //
        // Здесь стояло голое «return …Count == 0», то есть выход на ЛОЖЬ без
        // единого слова: ни в журнал, ни в отчёт. Дальше наряд печатал человеку
        // в чат « — больше рядом нет» про породу, на которой бот стоит, — ровно
        // ту фразу, ради починки которой всё и затевалось
        var осталось = WholeStone.HoldingSides(ctx.World, cell);
        if (осталось.Count == 0)
            return true;
        return Отказ($"{cell} целым не взять: за {кругов} кругов грани " +
                     $"кончиться не успели — держат ещё {осталось.Count} " +
                     $"(например {осталось[0]}). Так бывает, когда освобождённое место " +
                     "засыпает сыпучим: у гравия и песка fallSidewaysChance 0.75");
    }

    /// <summary>
    /// Сложить нужное в сундук. Возвращает, сколько реально легло — по убыли
    /// в своём инвентаре, а не по числу отправленных пакетов.
    /// </summary>
    public async Task<int> DeliverAsync(string wanted, BlockPos chest, CancellationToken ct = default)
    {
        // Сундук — такое же хранилище, как костёр и туша: разница только
        // в том, каким пакетом его открывают (см. Containers)
        var box = Container.Chest(ctx, hands, chest);
        box.OnLog += m => OnLog?.Invoke(m);
        int stored = await box.PutAllAsync(code => Matches(code, wanted), ct);
        OnLog?.Invoke($"в сундук легло {wanted} ×{stored}");
        return stored;
    }
}
