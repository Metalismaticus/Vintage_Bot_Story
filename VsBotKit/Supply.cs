namespace VsBotKit;

/// <summary>
/// ЗВЕНО ЦЕПОЧКИ «ГДЕ ВЗЯТЬ НЕДОСТАЮЩЕЕ». Порядок звеньев назвал заказчик, и
/// он же порядок значений: сумки → склад этой работы → дом → крафт → добыть
/// рядом.
/// </summary>
public enum SupplyLink
{
    /// <summary>Уже при себе.</summary>
    Bags,

    /// <summary>Сундук, отмеченный ДЛЯ ЭТОЙ РАБОТЫ (временное хранилище задачи).</summary>
    JobDepot,

    /// <summary>Дом: постоянный склад роли и сундуки вокруг точки дома.</summary>
    Home,

    /// <summary>Собрать по рецепту сетки.</summary>
    Craft,

    /// <summary>Добыть в мире рядом: подобрать, сломать блок, собрать.</summary>
    Nearby
}

/// <summary>Что вышло на ОДНОМ звене. Пустой <paramref name="Why"/> не бывает.</summary>
/// <param name="Brought">на сколько штук прибавилось в сумках — по сумке, а не по пакетам</param>
public sealed record SupplyStep(SupplyLink Link, int Brought, string Why)
{
    public override string ToString() => $"{Supply.LinkName(Link)}: {Why}";
}

/// <summary>
/// ЧЕМ КОНЧИЛСЯ ПОИСК НЕДОСТАЮЩЕГО.
/// </summary>
/// <param name="Got">нехватки больше нет</param>
/// <param name="Brought">сколько штук прибавилось в сумках за всю цепочку</param>
/// <param name="From">какое звено закрыло нехватку (null — ни одно)</param>
/// <param name="Tried">
/// Каждое пройденное звено со своим словом. Ради этого списка всё и написано:
/// человек обязан отличать «нигде нет» от «не сходил».
/// </param>
public sealed record SupplyOutcome(bool Got, int Brought, SupplyLink? From, string Reason,
    IReadOnlyList<SupplyStep> Tried)
{
    public override string ToString() => Reason;
}

/// <summary>
/// СНАБЖЕНИЕ: ГДЕ ВЗЯТЬ НЕДОСТАЮЩЕЕ. ОДИН МЕХАНИЗМ НА ВСЯКУЮ РАБОТУ, КОТОРОЙ
/// НУЖЕН МАТЕРИАЛ.
///
/// ЗАЧЕМ ОН ЗАВЁЛСЯ (живой прогон 16.08, слова заказчика дословно):
///   «правка — я просил изначально интеллектуально, не важно дорога или нет,
///    если нет ресурсов для стройки и для проекта не отмечен склад с ресурсами,
///    то смотреть дома недостающие»
/// и раньше того:
///   «он знал что у него есть склад и проверил что там есть, там было все для
///    крафта дороги, а он не стал туда возвращаться и докрафчивать».
///
/// В журнале это выглядело так: в 01:20:40 дорога отказалась («мостить нечем:
/// блоков в сумках нет вовсе»), а в 01:21:14 бот пересчитал шестнадцать
/// сундуков в двадцати шагах и насчитал в них 1023 предмета.
///
/// ЭТО НЕ ПРО ДОРОГУ. Дорога — только первый потребитель; та же беда ждала
/// карьер, штольню, факелы и инструменты, и четыре копии одного правила были бы
/// делом одного вечера.
///
/// ВТОРОЙ МЕХАНИКИ ЗДЕСЬ НЕТ, И ЭТО ГЛАВНОЕ. Каждое звено — дверь к тому, кто
/// этим и так ведает:
///   сумки          → <see cref="Depot.AtHand"/> (та же мерка, что и у описи);
///   склад работы   → <see cref="Depot.SupplyAsync"/> по временному хранилищу;
///   дом            → он же, но по <see cref="Depot.HomePoints"/>;
///   крафт          → <see cref="Stock.MakeAsync"/> — мастерская запасов, она
///                    же разбирает «что из чего» и умеет тёску;
///   добыть рядом   → <see cref="Errands.FetchAsync"/>.
/// Ни походки, ни описи, ни рецептов здесь не написано ни строчки: тут только
/// ПОРЯДОК и ЧЕСТНЫЙ ОТКАЗ, называющий оборвавшееся звено.
///
/// ДОЛГ ПРОШЛОЙ ВОЛНЫ ЗАКРЫТ ЗДЕСЬ, И НАЗВАТЬ ЭТО НАДО ВСЛУХ. Прошлый заход
/// честно признался: «Stock остался со своей цепочкой крафта и добычи … Это по
/// сути ВТОРАЯ цепочка „крафт → добыть“». Так и было: звено «крафт» звало
/// <c>Crafting.CraftAsync</c> — то есть умело собрать только из ТОГО, ЧТО УЖЕ В
/// СУМКЕ, — а рядом, в запасах, лежал разбор «топор ← топорище ← вытесать из
/// камня ← подобрать камень», который про сумку не спрашивал. Две цепочки на
/// одну работу расходятся в первый же вечер: живьём одна отвечала «не хватает
/// составляющих», а вторая в это же время знала, где их взять. Теперь звено
/// «крафт» — дверь в ту самую мастерскую, а мастерская за материалом ходит
/// сюда же (<see cref="Stock"/>): порядок звеньев один на всех, и он здесь.
///
/// Класс — механизм. «Ходить ли», «как далеко» и «сколько времени тратить» —
/// числа роли, и живут они там, где жили (Depot.SupplyForCraft,
/// Depot.SupplyMaxBlocks, Depot.SupplyMaxMinutes, Depot.SupplyBatch).
/// </summary>
public class Supply
{
    private readonly BotContext ctx;

    public Supply(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    /// <summary>
    /// Пробовать делать недостающее по рецепту. Выключать стоит там, где уходить
    /// с места нельзя вовсе; числа крафта тут не живут.
    /// </summary>
    public bool MayCraft { get; set; } = true;

    /// <summary>Пробовать добывать недостающее в мире рядом.</summary>
    public bool MayFetchNearby { get; set; } = true;

    /// <summary>
    /// ЗАЩИТА ОТ КРУГА, а не порог «стоит ли овчинка». Крафт, не найдя
    /// материала, спрашивает снабжение снова — уже про составляющие
    /// (<see cref="Crafting.RestockAsync"/>), и это правильно: факелу нужна
    /// палка, палку тоже где-то берут. Но рецепт бывает и кольцевым, а сумка
    /// на каждом витке одна и та же. Три витка — это «вещь ← её материал ←
    /// материал материала», глубже живой игрок посреди работы не лезет.
    /// </summary>
    public int MaxChainDepth { get; set; } = 3;

    /// <summary>На каком витке цепочки мы сейчас (0 — снаружи).</summary>
    private int depth;

    /// <summary>Как назвать звено человеку.</summary>
    public static string LinkName(SupplyLink link) => link switch
    {
        SupplyLink.Bags => "сумки",
        SupplyLink.JobDepot => "склад этой работы",
        SupplyLink.Home => "дом",
        SupplyLink.Craft => "крафт",
        _ => "добыть рядом"
    };

    /// <summary>
    /// СКОЛЬКО ЭТОГО БОТ МОЖЕТ ДОСТАТЬ, НЕ ВЫДУМЫВАЯ: сумки плюс опись всех
    /// сундуков, куда он сам заглядывал.
    ///
    /// Спрашивает это ВЫБОР РЕЦЕПТА (<see cref="Crafting.Shortfall"/>): из
    /// нескольких рецептов на одну вещь брать надо тот, чью нехватку бот может
    /// закрыть. Живой случай, ради которого: «на дорожное (stonepath-free) не
    /// хватает: stone-meteorite-iron (надо 4, есть 0)» — метеоритным железом
    /// дорогу не мостят, оно просто стояло первым в реестре.
    ///
    /// ЧЕСТНО ПРО ГРАНИЦУ: «добыть рядом» сюда не входит. Ответ на «растёт ли
    /// это в мире» — обход тринадцати тысяч блоков реестра, а спрашивают отсюда
    /// про каждый рецепт-кандидат. Значит рецепт, чей материал только копают,
    /// проиграет тому, чей материал лежит в сундуке, — и это то, чего мы и
    /// хотим.
    /// </summary>
    public int Reachable(string code) =>
        ctx.Depot is not { } depot ? 0 : depot.AtHand(code) + depot.InDepot(code);

    /// <summary>
    /// ЧЕГО ВСЁ ЕЩЁ НЕ ХВАТАЕТ — по сумке прямо сейчас, а не по числам, с
    /// которыми пришли. Считаем ТОЙ ЖЕ меркой, какой ищут на складе
    /// (<see cref="Depot.Suits"/>): «soil-*» вхождением не находится ни в одном
    /// коде мира, и без этой мерки бот шёл бы за землёй с полной сумкой земли.
    /// </summary>
    /// <param name="have">
    /// Чужая мерка «сколько такого при себе»; null — складская.
    ///
    /// Спрашивается ради ЗАПАСОВ. Требование «держать при себе кирку» меряется
    /// не подстрокой кода, а видом инструмента из реестра сервера
    /// (<see cref="Stock.Have"/>): щуп «prospectingpick-copper» — тоже кирка, а
    /// меч в игре зовётся «blade-longsword-iron», и слова «sword» в коде нет
    /// вовсе. Мерь мы такое своей меркой — бот сходил бы домой за киркой,
    /// держа её в руке.
    /// </param>
    public IReadOnlyList<(string Code, int Need, int Have)> Still(
        IReadOnlyList<(string Code, int Need, int Have)> lacking,
        Func<string, int>? have = null)
    {
        var left = new List<(string, int, int)>();
        foreach (var (code, need, was) in lacking)
        {
            int now = have != null ? have(code)
                : ctx.Depot is { } depot ? depot.AtHand(code) : was;
            if (now < need)
                left.Add((code, need, now));
        }
        return left;
    }

    /// <summary>
    /// НАЙТИ И ПРИНЕСТИ НЕДОСТАЮЩЕЕ — ВСЯ ЦЕПОЧКА ЦЕЛИКОМ.
    ///
    /// Порядок звеньев ровно тот, что назвал заказчик: сумки → склад, отмеченный
    /// для этой работы → дом → крафт → добыть рядом. Останавливаемся на первом
    /// же звене, которое закрыло нехватку.
    /// </summary>
    /// <param name="lacking">чего и сколько не хватает (на один сбор рецепта)</param>
    /// <param name="allowed">
    /// Разрешение старше настройки склада: очередь задач подаёт сюда поле самой
    /// задачи («домойЗаМатериалом»). null — решает <see cref="Depot.SupplyForCraft"/>.
    /// </param>
    /// <param name="ct">Отмена.</param>
    /// <param name="craft">Пробовать ли звено «крафт»; null — по настройке класса.</param>
    /// <param name="nearby">
    /// Пробовать ли звено «добыть рядом»; null — по настройке класса.
    ///
    /// Спрашивается ПОШТУЧНО, а не полем класса, ради живого случая: дороге
    /// нужен готовый дорожный камень, и выкапывать его в мире — это разбирать
    /// чужую (а то и свою вчерашнюю) дорогу. Составляющие того же рецепта
    /// (камень, землю) копать можно и нужно — и их спросит уже вложенный вызов
    /// из крафта, со своим ответом на этот вопрос.
    /// </param>
    /// <param name="stores">
    /// Пробовать ли кладовые (склад работы и дом); null — да.
    ///
    /// Выключают его не из бережливости, а по ЧЕСТНОСТИ: склад ищет по КУСКУ
    /// КОДА и другого способа у него нет, а требование бывает видом из реестра
    /// («любой топор»). Поход за таким кончился бы пустым сундуком — зато с
    /// настоящей ходьбой через полкарты, и об этом надо сказать словами, а не
    /// потоптаться (см. <see cref="Stock"/>).
    /// </param>
    /// <param name="have">
    /// Чужая мерка «сколько такого при себе»; null — складская
    /// (см. <see cref="Still"/>).
    /// </param>
    public async Task<SupplyOutcome> GetAsync(
        IReadOnlyList<(string Code, int Need, int Have)> lacking, bool? allowed = null,
        CancellationToken ct = default, bool? craft = null, bool? nearby = null,
        bool? stores = null, Func<string, int>? have = null)
    {
        var шаги = new List<SupplyStep>();
        if (lacking.Count == 0)
            return new SupplyOutcome(false, 0, null,
                "чего не хватает, никто не назвал — искать нечего", шаги);

        string чего = Depot.Names(lacking);

        // 1. СУМКИ. Спрашиваем заново, а не верим числам, с которыми пришли:
        // между отказом работы и этим вопросом бот мог что-то подобрать ногами
        var осталось = Still(lacking, have);
        if (осталось.Count == 0)
        {
            шаги.Add(new SupplyStep(SupplyLink.Bags, 0, $"{чего} — это всё уже при мне"));
            return new SupplyOutcome(true, 0, SupplyLink.Bags, шаги[0].Why, шаги);
        }
        шаги.Add(new SupplyStep(SupplyLink.Bags, 0, $"в сумках нет: {Depot.Names(осталось)}"));

        if (depth >= Math.Max(1, MaxChainDepth))
            return Fail(шаги, чего,
                $"глубже я не полез: цепочка «взять недостающее» уже идёт {depth}-м витком");

        depth++;
        try
        {
            int принёс = 0;

            // 2. СКЛАД, ОТМЕЧЕННЫЙ ДЛЯ ЭТОЙ РАБОТЫ. Отмечен — значит у задачи
            // своё хранилище (Depot.UseTemporary), и брать надо оттуда: чужой
            // склад роли задаче не обещали
            if (!(stores ?? true))
            {
                шаги.Add(new SupplyStep(SupplyLink.JobDepot, 0,
                    "в кладовые за этим не иду: они ищут по куску кода, а просят вид из реестра"));
                шаги.Add(new SupplyStep(SupplyLink.Home, 0, "дома по той же причине не смотрю"));
            }
            else if (ctx.Depot is { } depot)
            {
                if (depot.Temporary)
                {
                    var рейс = await depot.SupplyAsync(осталось, allowed, ct);
                    принёс += рейс.Brought;
                    шаги.Add(new SupplyStep(SupplyLink.JobDepot, рейс.Brought, рейс.Reason));
                    if (Done(осталось = Still(осталось, have)))
                        return Win(шаги, SupplyLink.JobDepot, принёс, чего);
                }
                else
                {
                    шаги.Add(new SupplyStep(SupplyLink.JobDepot, 0,
                        "склад для этой работы не отмечен — смотрю дома"));
                }

                // 3. ДОМ. Постоянный склад роли и сундуки вокруг точки дома —
                // одним обходом и одной описью (Depot.HomePoints)
                var дома = depot.HomePoints();
                if (дома.Count == 0)
                {
                    шаги.Add(new SupplyStep(SupplyLink.Home, 0, depot.WhyNoHome()));
                }
                else
                {
                    var рейс = await depot.SupplyAsync(осталось, allowed, ct, дома);
                    принёс += рейс.Brought;
                    шаги.Add(new SupplyStep(SupplyLink.Home, рейс.Brought, рейс.Reason));
                    if (Done(осталось = Still(осталось, have)))
                        return Win(шаги, SupplyLink.Home, принёс, чего);
                }
            }
            else
            {
                шаги.Add(new SupplyStep(SupplyLink.JobDepot, 0, "склад к боту не подключён"));
                шаги.Add(new SupplyStep(SupplyLink.Home, 0, "дома искать нечем: склад не подключён"));
            }

            // 4. КРАФТ. Крафт, не найдя материала, спросит снабжение снова — уже
            // про составляющие; от круга бережёт счётчик витков выше
            принёс += await CraftAsync(осталось, шаги, craft ?? MayCraft, have, ct);
            if (Done(осталось = Still(осталось, have)))
                return Win(шаги, SupplyLink.Craft, принёс, чего);

            // 5. ДОБЫТЬ РЯДОМ. Последним: это долго и уводит с места
            принёс += await NearbyAsync(осталось, шаги, nearby ?? MayFetchNearby, ct);
            if (Done(осталось = Still(осталось, have)))
                return Win(шаги, SupplyLink.Nearby, принёс, чего);

            return Fail(шаги, чего, $"не хватает {Depot.Names(осталось)}", принёс);
        }
        finally { depth--; }

        static bool Done(IReadOnlyList<(string, int, int)> left) => left.Count == 0;
    }

    /// <summary>
    /// ТОЛЬКО КЛАДОВЫЕ: сумки → склад этой работы → дом. Короткая дверь в ту же
    /// цепочку, без крафта и без похода в мир.
    ///
    /// Нужна тем, у кого свои крафт и добыча уже написаны и написаны лучше —
    /// прежде всего запасам (<see cref="Stock"/>) с их многошаговым разбором
    /// «что из чего». Отдельного механизма тут нет: это те же самые звенья
    /// <see cref="GetAsync"/> и в том же порядке, просто цепочка обрывается
    /// раньше.
    /// </summary>
    public Task<SupplyOutcome> FromStoresAsync(string code, int count,
        bool? allowed = null, CancellationToken ct = default, Func<string, int>? have = null)
    {
        int сейчас = have != null ? have(code) : ctx.Depot is { } depot ? depot.AtHand(code) : 0;
        return GetAsync([(code, Math.Max(1, count), сейчас)], allowed, ct,
            craft: false, nearby: false, have: have);
    }

    /// <summary>
    /// ЗАКРЫТЬ ЖИЗНЕННУЮ НУЖДУ: лечебное, съедобное, дрова под костёр — всё, что
    /// человек называет не кодом, а СВОЙСТВОМ («чем перевязаться», «что съесть»).
    ///
    /// ЗАЧЕМ ОТДЕЛЬНАЯ ДВЕРЬ. Слова заказчика дословно: «за всем для жизни что
    /// ему нужно хилки и тд он должен решать здесь может добыть или надо домой
    /// за ними (если они там есть)». Решать это обязано ОДНО правило — вот это
    /// самое, — а не заводиться заново у лечения, у еды и у костра. До правки
    /// каждый рефлекс упирался в своё молчание: «ранен, а лечиться нечем» —
    /// и всё, при том что дома в сундуке лежали бинты.
    ///
    /// ПОЧЕМУ НЕ ПРОСТО <see cref="GetAsync"/>. Тот спрашивает КОДЫ, а у нужды
    /// кода нет: «лечебное» — это свойство предмета из реестра сервера
    /// (WorldModel.GetHealingByCode и соседние). Поэтому здесь ровно один свой
    /// шаг — развернуть нужду в настоящие коды реестра, — а дальше та же
    /// цепочка и тот же честный отказ. Ни одного выдуманного имени: что
    /// считать лечебным, отвечает игра.
    ///
    /// ПОРЯДОК КАНДИДАТОВ — ПО ДОСТУПНОСТИ (<see cref="Reachable"/>): впереди
    /// то, что уже лежит в сумке или в описанном сундуке. Иначе бот пошёл бы
    /// делать бинт из тростника, имея дома стопку готовых.
    /// </summary>
    /// <param name="нужда">Как назвать это человеку: «лечебное», «еда».</param>
    /// <param name="годится">Что из реестра закрывает нужду.</param>
    /// <param name="count">Сколько штук нужно.</param>
    /// <param name="tries">
    /// Сколько разных кодов пробовать. Подходящих бывает десятки (еды на сервере
    /// сотня видов), и обойти их все — это обойти и все походы домой.
    /// </param>
    public async Task<SupplyOutcome> ForVitalAsync(string нужда, Func<string, bool> годится,
        int count = 1, CancellationToken ct = default, int tries = 3,
        bool? craft = null, bool? nearby = null)
    {
        var коды = Suitable(годится);
        if (коды.Count == 0)
            return new SupplyOutcome(false, 0, null,
                $"{нужда}: в реестре этого сервера нет ни одного подходящего предмета " +
                "(а если реестр ещё не пришёл — я про него просто не знаю)", []);

        SupplyOutcome? последний = null;
        foreach (string code in коды.Take(Math.Max(1, tries)))
        {
            if (ct.IsCancellationRequested)
                break;
            var итог = await GetAsync([(code, Math.Max(1, count), Reachable(code))], null, ct,
                craft: craft, nearby: nearby);
            if (итог.Got)
                return итог;
            последний = итог;
        }
        return последний ?? new SupplyOutcome(false, 0, null, $"{нужда}: искать не начал", []);
    }

    /// <summary>
    /// Коды реестра, закрывающие нужду, — доступные впереди. Пустой ответ
    /// значит и «таких нет», и «реестр не пришёл»: различить это здесь нечем, и
    /// отказ говорит про оба случая разом, а не выбирает удобный.
    ///
    /// ЧЕСТНО ПРО ГРАНИЦУ: спрашивается реестр ПРЕДМЕТОВ, не блоков. Бинты,
    /// еда и дрова в игре — предметы; нужда, которую закрывает блок (стог сена
    /// под ночлег), сюда не попадёт, и обещать обратное нельзя.
    /// </summary>
    public IReadOnlyList<string> Suitable(Func<string, bool> годится) =>
        ctx.World.SearchItemCodes("", int.MaxValue)
            .Where(годится)
            .OrderByDescending(Reachable)
            .ThenBy(c => c.Length)
            .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ---------------- звенья ----------------

    /// <summary>
    /// СДЕЛАТЬ НЕДОСТАЮЩЕЕ. Возвращает, на сколько штук прибавилось в сумках.
    ///
    /// СВОЕЙ МЕХАНИКИ ЗДЕСЬ НЕТ — это дверь в мастерскую запасов
    /// (<see cref="Stock.MakeAsync"/>), и в этом вся правка. Прежде звено звало
    /// <c>Crafting.CraftAsync</c> напрямую, то есть умело собрать ровно из того,
    /// что УЖЕ ЛЕЖИТ В СУМКЕ, и на пустой сумке отвечало «не хватает
    /// составляющих». А в запасах в это же время жил разбор «топор ← топорище +
    /// палка, топорище ← вытесать из камня, камень ← подобрать с земли» — и он
    /// знал, где эти составляющие взять. Две цепочки на одну работу расходятся
    /// в первый же вечер, и живьём разошлись: «Axe сделать не из чего» при
    /// палках и камнях в двадцати шагах.
    ///
    /// Мастерская, не найдя материала, спросит снабжение снова — уже про
    /// составляющие, и придёт ровно сюда, в тот же порядок звеньев. От круга
    /// бережёт счётчик витков (<see cref="MaxChainDepth"/>).
    ///
    /// Рецепт спрашивается по ТОМУ ЖЕ коду, каким нехватку назвал крафт. Код
    /// бывает образцом («stone-*»), и рецепта на образец не бывает — так и
    /// говорим: выдумывать под звёздочку конкретное имя здесь нечем, это дело
    /// выбора рецепта (<see cref="Crafting.Shortfall"/>).
    /// </summary>
    private async Task<int> CraftAsync(IReadOnlyList<(string Code, int Need, int Have)> lacking,
        List<SupplyStep> шаги, bool may, Func<string, int>? have, CancellationToken ct)
    {
        if (!may)
        {
            шаги.Add(new SupplyStep(SupplyLink.Craft, 0,
                "не скрафтить: этому поиску собирать по рецепту не велено"));
            return 0;
        }
        if (ctx.Crafting is not { Ready: true })
        {
            шаги.Add(new SupplyStep(SupplyLink.Craft, 0,
                "не скрафтить: реестр рецептов сетки с сервера ещё не пришёл"));
            return 0;
        }
        if (ctx.Stock is not { } мастерская)
        {
            шаги.Add(new SupplyStep(SupplyLink.Craft, 0,
                "не скрафтить: запасы к боту не подключены — собирать некому"));
            return 0;
        }

        int Сколько(string code) =>
            have != null ? have(code) : ctx.Depot is { } d ? d.AtHand(code) : 0;

        int сделано = 0;
        var почему = new List<string>();
        foreach (var (code, need, было) in lacking)
        {
            if (ct.IsCancellationRequested)
                break;
            int was = Сколько(code);
            // Мастерская сама решит, что именно делать под этот код (требование
            // бывает родом — «Axe»), сама сходит за материалом и сама скажет,
            // на чём упёрлась
            var (жалоба, _) = await мастерская.MakeAsync(code, Math.Max(1, need - было), ct: ct);
            int got = Сколько(code) - was;
            сделано += Math.Max(0, got);
            if (got <= 0)
                почему.Add($"{code} — {жалоба ?? "ничего не прибавилось"}");
        }

        шаги.Add(new SupplyStep(SupplyLink.Craft, сделано, сделано > 0
            ? $"сделал {сделано} шт"
            : "не скрафтить: " + (почему.Count > 0
                ? string.Join("; ", почему)
                : "не хватает составляющих")));
        return сделано;
    }

    /// <summary>
    /// ДОБЫТЬ РЯДОМ. Образец («stone-*») сперва разворачивается в настоящие
    /// коды реестра (<see cref="Errands.Expand"/>): поручение ищет источник по
    /// куску кода, а звёздочки в кодах мира не бывает — со звёздочкой поход
    /// вышел бы пустым, зато с настоящей ходьбой.
    /// </summary>
    private async Task<int> NearbyAsync(IReadOnlyList<(string Code, int Need, int Have)> lacking,
        List<SupplyStep> шаги, bool may, CancellationToken ct)
    {
        if (!may)
        {
            шаги.Add(new SupplyStep(SupplyLink.Nearby, 0,
                "рядом не добываю: этому поиску ходить за недостающим в мир не велено"));
            return 0;
        }
        if (ctx.Errands is not { } errands)
        {
            шаги.Add(new SupplyStep(SupplyLink.Nearby, 0,
                "рядом не добыть: поручения к боту не подключены"));
            return 0;
        }

        int добыто = 0;
        var почему = new List<string>();
        foreach (var (code, need, have) in lacking)
        {
            if (ct.IsCancellationRequested)
                break;
            // Из подходящих берём тот, у которого В МИРЕ ЕСТЬ ИСТОЧНИК: имя
            // рецепта — это образец, а копают конкретный камень
            string? реальный = errands.Expand(code)
                .FirstOrDefault(c => errands.Sources(c).Length > 0);
            if (реальный == null)
            {
                почему.Add($"{code} — ни одного блока, с которого он падает, в реестре нет");
                continue;
            }
            var got = await errands.FetchAsync(реальный, Math.Max(1, need - have), ct: ct);
            добыто += Math.Max(0, got.Delivered);
            if (got.Delivered <= 0)
                почему.Add($"{реальный} — {got.Message}");
        }

        шаги.Add(new SupplyStep(SupplyLink.Nearby, добыто, добыто > 0
            ? $"добыл рядом {добыто} шт"
            : "рядом не добыть: " + (почему.Count > 0
                ? string.Join("; ", почему)
                : "источника поблизости не нашлось")));
        return добыто;
    }

    // ---------------- ответ человеку ----------------

    private SupplyOutcome Win(List<SupplyStep> шаги, SupplyLink link, int brought, string чего)
    {
        string ответ = $"{чего} — взял: {LinkName(link)} ({шаги[^1].Why})";
        OnLog?.Invoke(ответ);
        return new SupplyOutcome(true, brought, link, ответ, шаги);
    }

    /// <summary>
    /// ОТКАЗ ОБЯЗАН НАЗВАТЬ ОБОРВАВШЕЕСЯ ЗВЕНО — ВСЕ ДО ОДНОГО. Прежде бот
    /// отвечал одной строкой «мостить нечем», и по ней нельзя было понять,
    /// сходил он куда-нибудь или нет; заказчик читал это как лень.
    /// </summary>
    private SupplyOutcome Fail(List<SupplyStep> шаги, string чего, string хвост, int brought = 0)
    {
        string ответ = $"{хвост}; искал так: " +
                       string.Join(" → ", шаги.Select(s => s.ToString()));
        OnLog?.Invoke(ответ);
        return new SupplyOutcome(false, brought, null, ответ, шаги);
    }
}
