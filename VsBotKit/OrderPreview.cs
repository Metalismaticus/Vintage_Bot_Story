namespace VsBotKit;

/// <summary>
/// ОТКУДА ЭТО БЕРЁТСЯ — один блок-источник, годный к показу человеку.
/// </summary>
/// <param name="Code">Код блока игры: «ore-poor-nativecopper-andesite».</param>
/// <param name="Name">Как игра зовёт его человеку; пусто — языковых файлов нет.</param>
/// <param name="IsBlock">Всегда блок: ломают именно блоки. Поле есть ради картинки.</param>
public readonly record struct OrderBlock(string Code, string Name, bool IsBlock = true)
{
    public override string ToString() => Name.Length > 0 ? $"{Name} ({Code})" : Code;
}

/// <summary>
/// ВО ЧТО ПРЕВРАТИТСЯ ЗАКАЗ — РАЗБОР ОДНОЙ СТРОКИ НАРЯДА, СОБРАННЫЙ ДО ВЫХОДА.
///
/// ЗАКАЗЧИК ДОСЛОВНО: «лучше отображать в вебформе тоже, придумай красиво в
/// конструкторе задачи». В чат и в журнал бот это уже говорил
/// (<see cref="MetalRules.Say"/>): «медь ×20: считаю СЛИТКАМИ, 20 слитков — это
/// 2000 ед. металла». Но человек НАБИРАЕТ задание в окне, а слышал ответ уже
/// после «Пустить» — то есть ровно тогда, когда поправить нельзя.
///
/// ВТОРОГО СЧЁТА ЗДЕСЬ НЕТ НИ ОДНОГО, и это главное. Всё, что тут делается, —
/// это спросить у тех же механизмов, что ведут наряд, и сложить ответы в одну
/// запись:
///   • в чём считать этот заказ — <see cref="Ores.UnitFor"/> (ручка роли
///     «ВЧёмСчитатьМеталл» плюс ответ реестра «а металл ли это вообще»);
///   • сколько это единиц и чем набирается — <see cref="Metals.PlanFor"/> и
///     <see cref="MetalRules.Pieces"/>, числа у которых из ассетов игры;
///   • как понято слово человека — <see cref="Errands.Resolve"/>, тем же
///     переводом и в том же порядке, каким его разбирает
///     <see cref="MiningOrder.FetchAsync"/>;
///   • из каких блоков это выпадает — <see cref="Errands.Sources"/>, а он
///     читает выпадения из реестра (<see cref="WorldModel.DropsOfBlock"/>).
/// Посчитай окно хоть одно из этих чисел само — оно показывало бы одно, а бот
/// приносил бы другое, и спорить было бы не о чем.
///
/// ПОРЯДОК ВОПРОСОВ ПОВТОРЯЕТ НАРЯД, а не удобен разбору. Наряд сперва смотрит,
/// не руда ли это (<see cref="MiningOrder.IsOreOrder"/>) и не порода ли
/// (<see cref="Ores.IsStoneOrder"/>), и только потом переводит слово через
/// поручения. Слово «медь» блоком не зовётся ни одним, и загляни разбор сразу
/// в поручения — окно честно написало бы «не знаю, что это», пока бот отлично
/// понимает и копает.
/// </summary>
/// <param name="Wanted">Слово человека как набрано: «медь», «метеоритное железо».</param>
/// <param name="Asked">Сколько просили.</param>
/// <param name="Unit">В чём это считается НА САМОМ ДЕЛЕ (ручка роли + реестр).</param>
/// <param name="Metal">Счёт металла: единицы и чем набирается.</param>
/// <param name="Pieces">Из чего набирается: вещь, единиц в штуке, сколько штук.</param>
/// <param name="Code">Код игры, если слово свелось к ОДНОЙ вещи; иначе пусто.</param>
/// <param name="Name">Человеческое имя этой вещи.</param>
/// <param name="IsBlock">Блок это или предмет — по реестру, а не по виду кода.</param>
/// <param name="Understood">Насколько уверенно понято слово, человеческими словами.</param>
/// <param name="Blocks">Из каких блоков это выпадает (сколько просили показать).</param>
/// <param name="BlocksTotal">Сколько таких блоков всего в реестре сервера.</param>
/// <param name="Why">
/// ЧЕСТНЫЙ ОТКАЗ. Пусто — разбор вышел. Иначе здесь стоит причина ТЕМИ ЖЕ
/// словами, какими откажется сам бот: «под „медь“ подходит три: …; какое из
/// них?», «реестр блоков сервера ещё не пришёл». Молчаливый пустой разбор
/// человек читает как «окно сломалось» и жмёт «Пустить» вслепую.
/// </param>
public sealed record OrderPreview(
    string Wanted,
    int Asked,
    MetalCount Unit,
    MetalPlan Metal,
    IReadOnlyList<MetalPiece> Pieces,
    string Code,
    string Name,
    bool IsBlock,
    string Understood,
    IReadOnlyList<OrderBlock> Blocks,
    int BlocksTotal,
    string Why)
{
    /// <summary>Разбор состоялся: хоть что-то бот про это слово знает.</summary>
    public bool Ok => Why.Length == 0;

    /// <summary>
    /// ТА ЖЕ СТРОКА, ЧТО БОТ ГОВОРИТ В ЧАТ И ПИШЕТ В ЖУРНАЛ. Своей у окна нет
    /// нарочно: человек, услышавший от бота одно, а прочитавший в окне другое,
    /// перестаёт верить обоим.
    /// </summary>
    public string Say => MetalRules.Say(Metal);

    public override string ToString() => Say;
}

/// <summary>
/// СБОРКА РАЗБОРА: спросить механизмы бота и сложить ответы.
///
/// Отдельно от <see cref="OrderPreview"/>, потому что запись — чистая (её
/// можно собрать в тесте руками), а сборка лезет в мир: реестр сервера,
/// языковые файлы, ручки роли. Так же разведены и все прочие пары в проекте
/// (<see cref="MetalPlan"/> и <see cref="Metals"/>).
/// </summary>
public static class OrderPreviews
{
    /// <summary>
    /// Сколько блоков-источников называть по умолчанию. Не порог поведения, а
    /// длина списка на экране: спрашивающий волен попросить больше или меньше.
    /// </summary>
    public const int BlocksShown = 8;

    /// <summary>
    /// РАЗОБРАТЬ ОДНУ СТРОКУ ЗАКАЗА. Ни бот никуда не идёт, ни задача никуда не
    /// ставится: спросить «во что это выльется» человек вправе, ничего не
    /// запуская, — ровно как у оценки срока (<c>ControlPanel.JobEta</c>).
    /// </summary>
    /// <param name="ctx">Механизмы бота: реестр, руда, металл, поручения.</param>
    /// <param name="wanted">Слово человека.</param>
    /// <param name="asked">Сколько просили.</param>
    /// <param name="blocks">Сколько блоков-источников назвать.</param>
    public static OrderPreview Of(BotContext ctx, string? wanted, int asked,
        int blocks = BlocksShown)
    {
        string слово = (wanted ?? "").Trim();
        if (слово.Length == 0)
            return Пусто(слово, asked, "не сказано, что добывать");

        // В ЧЁМ СЧИТАЕМ — СПРАШИВАЕМ У РУДЫ, А НЕ РЕШАЕМ ЗДЕСЬ. Там и ручка
        // роли «ВЧёмСчитатьМеталл», и охрана «а металл ли это вообще»: уголь
        // тоже «ore-*», а единиц металла у него нет, и наряд на 20 угля в
        // слитках не кончился бы никогда
        var unit = ctx.Ores is { } руда ? руда.UnitFor(слово) : MetalCount.Штуки;
        var plan = ctx.Metal is { } металл
            ? металл.PlanFor(слово, asked, unit)
            : new MetalPlan(слово, asked, unit, 0, []);
        var pieces = MetalRules.Pieces(plan);

        // КАК БОТ УЗНАЁТ ЭТО СЛОВО — тем же порядком, что и наряд (см. заголовок)
        bool рудаЛи = ctx.Ores is { } рудаМех && рудаМех.IsOreOrder(слово);
        bool камень = Ores.IsStoneOrder(слово);

        string код = "", имя = "", понял = "";
        bool блок = false;
        var куски = new List<string>();

        if (рудаЛи)
        {
            // Металл — это ГРУППА МИНЕРАЛОВ, а не одна вещь: у меди их пять.
            // Списка минералов своего тут нет — он один и живёт у руды
            куски.AddRange(Metals.MineralsOf(слово));
            понял = plan.IsMetal
                ? $"металл игры; ищу минералы: {string.Join(", ", куски)}"
                : $"руда игры; ищу минералы: {string.Join(", ", куски)}";
            // ЧЕЛОВЕК МОГ НАЗВАТЬ И КОД ИГРЫ. «stone-meteorite-iron» — не
            // группа и не минерал, а код выпадения: MineralsOf вернёт его как
            // есть, и по нему же реестр найдёт блок-источник
            код = ctx.World.BlockCodeToId(слово) != null || ctx.World.ItemCodeToId(слово) != null
                ? слово
                : "";
            блок = код.Length > 0 && ctx.World.BlockCodeToId(код) != null;
            имя = код.Length > 0 ? ctx.Errands?.Lang?.NameOf(код) ?? "" : "";
        }
        else if (камень)
        {
            // Порода — тоже семейство, и семейства эти НАСТРАИВАЮТСЯ
            // (Ores.StoneMasks): своего списка «что считать камнем» здесь нет
            куски.AddRange(ctx.Ores?.StoneMasks ?? []);
            понял = $"природная порода; ищу семейства: {string.Join(", ", куски)}";
        }
        else if (ctx.Errands is { } поручения)
        {
            // ТОТ ЖЕ ПЕРЕВОД, ЧТО И У НАРЯДА, включая переспрос. Переспрос —
            // это НЕ разбор: показать человеку «понял вот это», когда бот
            // на самом деле переспросит, значило бы соврать красиво
            var понято = поручения.Resolve(слово);
            if (понято.Code is not { Length: > 0 } рабочий)
                return Пусто(слово, asked, понято.Say, unit, plan, pieces);

            код = рабочий;
            куски.Add(рабочий);
            // Имя и «блок ли» — у той же подсказки, что рисует выпадающий
            // список у поля: второго перевода кода в имя окну не нужно
            var вещь = поручения.Suggest(рабочий, 1).FirstOrDefault()
                       ?? понято.Choices.FirstOrDefault(в => в.Code == рабочий);
            имя = вещь?.Name ?? поручения.Lang?.NameOf(рабочий) ?? "";
            блок = вещь?.IsBlock ?? ctx.World.BlockCodeToId(рабочий) != null;
            понял = вещь?.Understood ?? "";
        }
        else
        {
            return Пусто(слово, asked, "поручения не подключены — перевести слово в код игры нечем",
                unit, plan, pieces);
        }

        // ОТКУДА ЭТО ВЫПАДАЕТ — ответ реестра, одно место на весь проект
        var найдено = new List<string>();
        var видели = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string кусок in куски)
            foreach (string блокКод in ctx.Errands?.Sources(кусок) ?? [])
                if (видели.Add(блокКод))
                    найдено.Add(блокКод);

        // РЕЕСТРА МОЖЕТ И НЕ БЫТЬ — окно человек открывает раньше, чем бот
        // входит в мир. «Ниоткуда не выпадает» в этот миг было бы враньём на
        // секунду и навсегда: разбор надо переспросить, а не запомнить.
        //
        // СПРАШИВАЕМ ИМЕННО РЕЕСТР КОДОВ (BlockRegistryReady), а не собранные
        // объекты блоков. Разница живая: коды приходят пакетом при входе в мир,
        // и по ним уже ищет Errands.Sources, а объекты игры собираются позже и
        // не всегда. Пока тут стояла проверка объектов, разбор на олово в мире
        // без олова врал «реестр ещё не пришёл» при полностью пришедшем реестре
        if (найдено.Count == 0 && !ctx.World.BlockRegistryReady)
            return new OrderPreview(слово, asked, unit, plan, pieces, код, имя, блок, понял,
                [], 0, "реестр блоков сервера ещё не пришёл — откуда это берётся, скажу после входа в мир");

        // ПОРЯДОК — ТОТ ЖЕ, ЧТО В ЧАТЕ (Errands.ShortestFirst): сама вещь
        // впереди её разновидностей. Свой порядок здесь показывал бы человеку
        // не то, что бот называет вслух
        var список = Errands.ShortestFirst(найдено)
            .Take(Math.Max(0, blocks))
            .Select(c => new OrderBlock(c, ctx.Errands?.Lang?.NameOf(c) ?? ""))
            .ToList();

        return new OrderPreview(слово, asked, unit, plan, pieces, код, имя, блок, понял,
            список, найдено.Count,
            найдено.Count > 0
                ? ""
                : $"«{слово}»: в реестре ЭТОГО сервера нет ни одного блока, с которого это падает");
    }

    private static OrderPreview Пусто(string wanted, int asked, string why,
        MetalCount unit = MetalCount.Штуки, MetalPlan? plan = null,
        IReadOnlyList<MetalPiece>? pieces = null) =>
        new(wanted, asked, unit, plan ?? new MetalPlan(wanted, asked, unit, 0, []),
            pieces ?? [], "", "", false, "", [], 0, why);
}
