using Vintagestory.API.Util;

namespace VsBotKit;

/// <summary>
/// ПРОМЫВКА ЛОТКОМ: горсть земли в лоток, лоток в воду — и в руках то, чего
/// в зимнем мире взять больше негде.
///
/// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ. Две волны подряд бот не мог сделать лук и обе объяснили
/// это одинаково: «flaxfibers в зимнем мире нет». Здесь стояла вторая неправда
/// вдогонку первой — «волокна падают ровно с одного блока». По ассетам
/// установленной игры 1.22.7 их источников несколько: спелый лён crop-flax-9;
/// ГЛИНЯНЫЕ СХРОНЫ (blocktypes/clay/lootvessel.json — lootvessel-forage
/// 0,5 × 1..12, -farming 0,75 × 2..10, -arcticsupplies 0,5 × 1..12), и это тоже
/// БЛОК; дрифтеры, шиверы и боуторны. И лоток (pan.json, panningDrops) —
/// КОСТЯНАЯ ЗЕМЛЯ с шансом 0,15 из ассета, от времени года не зависящая вовсе.
/// Схрон надо найти (в живом мире заказчика в 96 блоках были только «-tool»,
/// «-ore» и «-seed», волокон не дающие), а костяная земля лежала в четырнадцати
/// блоках от точки входа всё это время — оттого лоток, а не схрон.
///
/// КАК ЭТО УСТРОЕНО В ИГРЕ (читано в VSSurvivalMod: BlockPan, и в
/// VintagestoryLib: ServerSystemInventory.HandleHandInteraction):
///
/// 1. ЛОТОК — БЛОК, А НЕ ПРЕДМЕТ. Узнаётся не по коду «pan-wooden», а по тому,
///    что в его атрибутах лежит таблица «panningDrops»
///    (<see cref="WorldModel.PanningDrops"/>). Мод со своим лотком работает так же.
///
/// 2. ГОРСТЬ. Правый клик ПУСТЫМ лотком по промываемому блоку —
///    <c>BlockPan.TryTakeMaterial</c>. Три условия, и все три от сервера:
///    у блока атрибут «pannable»; НАД НИМ ВОЗДУХ (иначе игра отвечает
///    ingameerror-panning-requireairabove); клетка не в чужой заявке.
///    Блок при этом МЕНЯЕТСЯ: полная костяная земля становится «bonysoil-7»,
///    дальше слой убывает до «bonysoil-1», а с последней горстью клетка
///    исчезает. Один блок = ВОСЕМЬ горстей.
///
/// 3. ПРОМЫВКА. Правый клик ПОЛНЫМ лотком, держать не меньше 3,4 с
///    (<c>OnHeldInteractStop</c>: «if (secondsUsed &gt;= 3.4f)»). Секунды считает
///    сервер сам — от прихода StartHeldItemUse до прихода StopHeldItemUse
///    (<c>secondsPassed = (ElapsedMilliseconds − controls.UsingBeginMS) / 1000</c>),
///    поэтому держать надо ЖИВЫМ временем, а не числом в пакете.
///
/// 4. НОГИ В ВОДЕ — ОБЯЗАТЕЛЬНО, И ЭТО ЧЕСТНОСТЬ, А НЕ ПРИДИРКА.
///    <c>OnHeldInteractStart</c> при <c>!FeetInLiquid</c> отвечает
///    ingameerror-panning-notinwater и НЕ ставит признак «canpan». Проверка эта
///    стоит под <c>api.Side == Client</c>, то есть СЕРВЕР её не делает: безголовый
///    бот промыл бы и на суше. Живой игрок не может, значит и бот не должен
///    (закон 5) — воду спрашиваем сами и без неё честно отказываемся.
///
/// 5. ЧТО ВЫПАДАЕТ. <c>CreateDrop</c> ПЕРЕМЕШИВАЕТ таблицу и идёт по ней до
///    первого попадания, после чего выходит: за одну промывку выпадает НЕ БОЛЬШЕ
///    ОДНОЙ вещи. Поэтому «шанс 0,15» из ассета — это НЕ доля промывок,
///    кончившихся волокном: волокно может не достаться потому, что раньше него
///    в перемешанной колоде выпала кость (0,3). Настоящее число считает
///    <see cref="ChancePerPan"/>, и оно заметно меньше.
/// </summary>
public sealed class Panning
{
    /// <summary>
    /// Сколько секунд игра требует держать лоток. Число ИГРЫ, а не наше:
    /// <c>BlockPan.OnHeldInteractStop</c>, «if (secondsUsed &gt;= 3.4f)».
    /// </summary>
    public const double GameHoldSeconds = 3.4;

    /// <summary>
    /// Сколько горстей даёт один полный промываемый блок: сам блок плюс семь
    /// убывающих слоёв (<c>pannedBlock: "bonysoil-7"</c> → … → «-1» → пусто).
    /// </summary>
    public const int HandfulsPerBlock = 8;

    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Panning(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// Сколько держать кнопку. Чуть больше игрового порога: секунды считает
    /// сервер по приходу пакетов, а сеть добавляет и отнимает миллисекунды.
    /// </summary>
    public double HoldSeconds { get; set; } = GameHoldSeconds + 0.6;

    /// <summary>Сколько блоков вокруг смотреть, ища воду и промываемое.</summary>
    public int SearchRadius { get; set; } = 24;

    /// <summary>
    /// Сколько промывок делать за один заход. Число даёт роль
    /// (LivingRole.ПромывокЗаЗаход): каждая промывка — четыре секунды
    /// неподвижности и четыре единицы сытости.
    /// </summary>
    public int PansPerVisit { get; set; } = 25;

    /// <summary>
    /// РАЗРЕШЕНО ЛИ МЫТЬ. Решает роль (LivingRole.МытьЛоткомКогдаИначеНегде);
    /// сам механизм умеет мыть всегда и на эту дверь только смотрит — спрашивает
    /// её тот, кто зовёт (снабжение и команда «!промой»). Иначе выключенная
    /// галочка выглядела бы поломкой лотка, а не решением хозяина.
    /// </summary>
    public bool Allowed { get; set; } = true;

    /// <summary>
    /// На сколько клеток от воды бот согласен тянуться к промываемому блоку.
    /// Дальше руки не хватает: сервер при AntiAbuse проверяет дальность сам
    /// (<c>IsInInteractionRangeOf</c>) и молча выкидывает пакет.
    /// </summary>
    public int ReachFromWater { get; set; } = 3;

    // ==================================================================
    //  ЧИСТЫЕ ПРАВИЛА. Ни сети, ни мира — только таблица из ассета и счёт.
    // ==================================================================

    /// <summary>Одна строка таблицы выпадений лотка.</summary>
    /// <param name="Code">код вещи («flaxfibers»)</param>
    /// <param name="Chance">шанс из ассета (NatFloat.avg)</param>
    /// <param name="ManMade">
    /// Рукотворное. Игра выбрасывает такие строки на мирах без «loreContent»
    /// (<c>BlockPan.OnLoaded</c>), и наконечники стрел из песка — как раз они.
    /// </param>
    public readonly record struct PanDrop(string Code, double Chance, bool ManMade);

    /// <summary>
    /// ТАБЛИЦА ВЫПАДЕНИЙ ДЛЯ ЭТОГО МАТЕРИАЛА — из JSON, присланного сервером.
    ///
    /// Ключи таблицы — маски игры («@(bonysoil|bonysoil-.*)»), и сверяются они
    /// тем же <c>WildcardUtil.Match</c>, каким сверяет их сама игра. Подходит
    /// ПОСЛЕДНЯЯ подошедшая маска — так же, как в <c>BlockPan.CreateDrop</c>.
    ///
    /// Пустой список — этот материал лотком не моется вовсе.
    /// </summary>
    public static IReadOnlyList<PanDrop> DropsFor(string? dropsJson, string? materialCode)
    {
        if (string.IsNullOrEmpty(dropsJson) || string.IsNullOrEmpty(materialCode))
            return [];
        List<PanDrop>? выбрано = null;
        try
        {
            var таблица = Newtonsoft.Json.Linq.JObject.Parse(dropsJson);
            foreach (var пара in таблица)
            {
                if (!WildcardUtil.Match(пара.Key, materialCode))
                    continue;
                var строки = new List<PanDrop>();
                foreach (var запись in пара.Value as Newtonsoft.Json.Linq.JArray ??
                                       new Newtonsoft.Json.Linq.JArray())
                {
                    string? код = запись["code"]?.ToObject<string>();
                    if (код is not { Length: > 0 })
                        continue;
                    // Шанс в ассетах пишут и числом, и NatFloat'ом {avg, var}
                    var шанс = запись["chance"];
                    double c = шанс is Newtonsoft.Json.Linq.JObject o
                        ? o["avg"]?.ToObject<double?>() ?? 0
                        : шанс?.ToObject<double?>() ?? 0;
                    строки.Add(new PanDrop(код, c,
                        запись["manMade"]?.ToObject<bool?>() == true));
                }
                выбрано = строки;      // последняя подошедшая маска, как в игре
            }
        }
        catch { return []; }
        return выбрано ?? (IReadOnlyList<PanDrop>)[];
    }

    /// <summary>
    /// НАСТОЯЩИЙ ШАНС ДОСТАТЬ ЭТУ ВЕЩЬ ЗА ОДНУ ПРОМЫВКУ.
    ///
    /// ПОЧЕМУ НЕ ПРОСТО «0,15». <c>BlockPan.CreateDrop</c> перемешивает таблицу
    /// и идёт по ней до ПЕРВОГО попадания, после чего выходит. Значит волокно
    /// достанется только тогда, когда никто из тех, кто в этот раз встал перед
    /// ним, не выпал раньше. Кость с шансом 0,3 отнимает у волокна почти
    /// четверть его собственных попаданий.
    ///
    /// СЧЁТ. Для случайной перестановки вероятность, что перед нужным окажется
    /// ровно r чужих строк, одинакова для всех r (1/n), а сам набор из r строк
    /// равновероятен среди всех сочетаний. Отсюда
    /// P = c · (1/n) · Σ<sub>r</sub> e<sub>r</sub>(1−c<sub>j</sub>) / C(n−1, r),
    /// где e<sub>r</sub> — элементарные симметрические многочлены от «не выпал».
    ///
    /// ЖИВОЕ ЧИСЛО, РАДИ КОТОРОГО ЭТО СЧИТАЕТСЯ: для костяной земли ванили
    /// 1.22.7 выходит 0,111 вместо 0,15 — то есть на двенадцать волокон нужно
    /// не восемьдесят промывок, а сто восемь. Разница в двадцать пять минут, и
    /// сказать её человеку надо ДО того, как он станет ждать.
    ///
    /// ЗДЕСЬ СТОЯЛИ ДРУГИЕ ЧИСЛА — «около 0,09» и «полторы сотни», — и они были
    /// ВЫДУМАНЫ (закон 6). Приёмка 27.08 пересчитала эту же функцию дважды
    /// независимо: замкнутой формулой 0,111346 и Монте-Карло по игровому
    /// алгоритму (перемешать и идти до первого попадания) 0,111364, на 12 штук
    /// 107,8 промывки. Соседняя подпись в <c>LivingRole</c> той же волны писала
    /// верное «0,111 … около ста десяти промывок»: два комментария про одно
    /// число расходились, и выдуман был тот, что стоял у самой формулы.
    /// </summary>
    public static double ChancePerPan(IReadOnlyList<PanDrop> drops, string wanted)
    {
        if (drops.Count == 0 || wanted.Length == 0)
            return 0;
        double итог = 0;
        int n = drops.Count;
        for (int k = 0; k < n; k++)
        {
            if (!string.Equals(drops[k].Code, wanted, StringComparison.OrdinalIgnoreCase))
                continue;
            double c = Math.Clamp(drops[k].Chance, 0, 1);
            if (c <= 0)
                continue;

            // e[r] — сумма произведений «не выпал» по всем сочетаниям из r чужих
            var e = new double[n];       // e[0..n-1], чужих ровно n-1
            e[0] = 1;
            int взято = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == k)
                    continue;
                double q = 1 - Math.Clamp(drops[j].Chance, 0, 1);
                for (int r = ++взято; r >= 1; r--)
                    e[r] += e[r - 1] * q;
            }

            double среднее = 0;
            for (int r = 0; r <= n - 1; r++)
                среднее += e[r] / Combinations(n - 1, r);
            итог += c * среднее / n;
        }
        return итог;
    }

    /// <summary>Сочетания C(n, r) — числа малые, счёт прямой.</summary>
    private static double Combinations(int n, int r)
    {
        if (r < 0 || r > n)
            return 1;
        double итог = 1;
        for (int i = 1; i <= r; i++)
            итог = итог * (n - r + i) / i;
        return итог;
    }

    /// <summary>
    /// СКОЛЬКО ПРОМЫВОК НАДО НА СТОЛЬКО ВЕЩЕЙ — среднее, а не обещание.
    /// Ноль шанса — <see cref="int.MaxValue"/>: столько не намыть никогда, и
    /// отказ обязан сказать это словами, а не бесконечным кругом.
    /// </summary>
    public static int PansFor(double chancePerPan, int count) =>
        chancePerPan <= 0 ? int.MaxValue
            : (int)Math.Ceiling(Math.Max(1, count) / chancePerPan);

    // ==================================================================
    //  ЖИВОЙ ПУТЬ
    // ==================================================================

    /// <summary>Есть ли у сервера вообще лоток (реестр пришёл и лоток в нём).</summary>
    public bool Ready => ctx.World.PanningDrops.Count > 0;

    /// <summary>Код лотка, который бот НЕСЁТ с собой (null — лотка нет).</summary>
    public string? CarriedPan =>
        ctx.World.PanningDrops.Keys.FirstOrDefault(code => hands.CountOf(code) > 0);

    /// <summary>Таблица выпадений лотка, который бот несёт (или любого известного).</summary>
    public string? DropsJson =>
        CarriedPan is { } свой && ctx.World.PanningDrops.TryGetValue(свой, out var мой)
            ? мой
            : ctx.World.PanningDrops.Values.FirstOrDefault();

    /// <summary>Коды лотков, известных серверу (обычно один — «pan-wooden»).</summary>
    public IReadOnlyList<string> KnownPans => ctx.World.PanningDrops.Keys.ToArray();

    /// <summary>
    /// Какие блоки реестра моются лотком. Обход реестра стоит денег, а спрашивают
    /// его на каждый разбор снаряжения — поэтому список считается один раз.
    /// Списка кодов у нас нет и быть не должно: мод добавит свой песок (закон 6).
    /// </summary>
    public IReadOnlyList<string> PannableCodes =>
        pannable ??= ctx.World.SearchBlockCodes("", int.MaxValue)
            .Where(ctx.World.IsPannable)
            .ToArray();

    private string[]? pannable;

    /// <summary>
    /// Чем и с каким шансом лоток закроет нужное: пары «материал → шанс за одну
    /// промывку», лучшее вперёд. Пусто — лотком этого не добыть.
    ///
    /// САМА ВЕЩЬ ВПЕРЕДИ СВОИХ СЛОЁВ. Шанс у «bonysoil» и у «bonysoil-3»
    /// одинаков (маска ассета накрывает оба), и при равенстве вперёд идёт
    /// КОРОТКИЙ код: человеку в отказе надо прочесть «костяная земля», а не
    /// «третий слой костяной земли», которого он в мире и не видел.
    /// </summary>
    public IReadOnlyList<(string Material, double Chance)> WaysTo(string wanted)
    {
        if (DropsJson is not { } json)
            return [];
        if (ways.TryGetValue(wanted, out var готово))
            return готово;

        var найдено = new List<(string Material, double Chance)>();
        foreach (string code in PannableCodes)
        {
            double шанс = ChancePerPan(DropsFor(json, code), wanted);
            if (шанс > 0)
                найдено.Add((code, шанс));
        }
        найдено.Sort((a, b) => a.Chance != b.Chance
            ? b.Chance.CompareTo(a.Chance)
            : a.Material.Length != b.Material.Length
                ? a.Material.Length - b.Material.Length
                : string.CompareOrdinal(a.Material, b.Material));
        var итог = найдено.ToArray();
        // Пустой реестр не запоминаем: он придёт через секунду (та же осторожность,
        // что у Errands.Sources)
        if (PannableCodes.Count > 0)
            ways[wanted] = итог;
        return итог;
    }

    private readonly Dictionary<string, (string Material, double Chance)[]> ways =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// СТОЯТ ЛИ НОГИ БОТА В ВОДЕ ПРЯМО СЕЙЧАС — ТЕМ ЖЕ ПРАВИЛОМ, ЧТО У ИГРЫ.
    ///
    /// ЗДЕСЬ БЫЛА ВТОРАЯ КОПИЯ ПРАВИЛА, И КОПИЯ ДРУГАЯ (закон 2). Стояло просто
    /// «в клетке ног вода», а игра считает иначе — по УРОВНЮ ЗАПОЛНЕНИЯ и
    /// ДРОБНОЙ высоте тела (<see cref="JumpPhysics.FeetInLiquid"/>). Расходятся
    /// они на мелкой воде и на тёсаных полах: тело на 112,6 в клетке с
    /// «water-…-3» — у игры ноги НЕ в воде (0,375 &lt; 0,6), живой игрок промывку
    /// начать не может, а копия отвечала «да». Именно этой проверкой закрыт
    /// закон 5 («бот не делает того, чего не может живой игрок»), и закрывать
    /// его не тем правилом нельзя.
    /// </summary>
    public bool FeetInWater
    {
        get
        {
            if (ctx.Self.Position is not { } p)
                return false;
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y), z = (int)Math.Floor(p.Z);
            return ctx.World.IsWater(x, y, z) &&
                   JumpPhysics.FeetInLiquid(p.Y, ctx.World.LiquidFill(x, y, z),
                                            ctx.World.LiquidFill(x, y + 1, z) > 0);
        }
    }

    /// <summary>
    /// ВО ЧТО ПРЕВРАТИТСЯ КЛЕТКА, ОТДАВ ГОРСТЬ — дословный перевод
    /// <c>BlockPan.TryTakeMaterial</c> (VSSurvivalMod 1.22.7, ilspycmd 27.08):
    /// <list type="number">
    /// <item>у блока есть вариант «layer» — слой убывает на единицу, а с
    /// последнего («1») остаётся ВОЗДУХ;</item>
    /// <item>варианта нет — блок становится тем, что назван в его атрибуте
    /// <c>pannedBlock</c>, а если атрибута нет — своим же кодом со слоем 7.</item>
    /// </list>
    ///
    /// СКАЗАНО ВСЛУХ: вариант «layer» читается КАК ПОСЛЕДНЯЯ ЧИСЛОВАЯ ЧАСТЬ
    /// КОДА, потому что карты вариантов сервер боту не шлёт — приходят только
    /// коды. Для ванильных промываемых («bonysoil-3», «sand-granite-2») это
    /// одно и то же, но если чужой мод назовёт слой иначе, счёт разойдётся, и
    /// разойдётся МОЛЧА. Поэтому по нему НЕ решается «взял ли»: там решает
    /// сам факт перемены блока, а это число только объясняет разницу вслух.
    /// </summary>
    /// <param name="code">код блока ДО горсти</param>
    /// <param name="pannedInto">атрибут <c>pannedBlock</c> из реестра, если он есть</param>
    /// <returns>ожидаемый код после горсти; пустая строка — станет воздухом</returns>
    public static string AfterHandful(string code, string? pannedInto)
    {
        int дефис = code.LastIndexOf('-');
        if (дефис > 0 && дефис < code.Length - 1 &&
            int.TryParse(code[(дефис + 1)..], out int слой) && слой is >= 1 and <= 7)
            return слой == 1 ? "" : code[..(дефис + 1)] + (слой - 1);
        return pannedInto is { Length: > 0 } ? pannedInto : code + "-7";
    }

    /// <summary>Что вышло из промывки — числами, а не словами «помыл».</summary>
    /// <param name="Pans">сколько промывок ДОШЛО до конца (горсть была, кнопку держали)</param>
    /// <param name="Gained">что прибавилось в сумках, по кодам</param>
    /// <param name="Message">человеческий итог или честный отказ</param>
    public sealed record PanReport(int Pans, IReadOnlyDictionary<string, int> Gained, string Message)
    {
        public bool Success => Pans > 0;
        public override string ToString() => Message;
    }

    /// <summary>
    /// ОТДАЁТ ЛИ ЭТОТ МАТЕРИАЛ ТО, ЧТО НУЖНО. Пустое «нужно» — годится любой
    /// промываемый (так спрашивает человек командой «!лоток», когда ему просто
    /// интересно, есть ли где мыть).
    /// </summary>
    public bool Gives(string? materialCode, string? wanted) =>
        wanted is not { Length: > 0 } ||
        (DropsJson is { } json && materialCode is { Length: > 0 } &&
         ChancePerPan(DropsFor(json, materialCode), wanted) > 0);

    /// <summary>
    /// НАЙТИ, ГДЕ МЫТЬ ИМЕННО НУЖНОЕ: клетка воды, из которой рука дотягивается
    /// до промываемого блока с воздухом над ним, И ЭТОТ БЛОК ОТДАЁТ ТО, ЗАЧЕМ
    /// ПРИШЛИ.
    ///
    /// ЗАЧЕМ ДОВОД «ЧТО НУЖНО» — ЖИВОЙ ТУПИК, НАЗВАННЫЙ ПРИЁМКОЙ 27.08. Здесь
    /// искался ЛЮБОЙ промываемый блок у воды, а <c>Stock</c> ставил способ по
    /// конъюнкции двух РАЗНЫХ вопросов: «в реестре есть блок, дающий волокна»
    /// и «какой-то промываемый блок есть у воды». Это не обязан быть один и тот
    /// же блок — и в живом мире он им обычно НЕ бывает: у воды лежит песок
    /// (<c>sand.json</c>, <c>gravel.json</c> — все <c>pannable: true</c>), а
    /// волокна падают с костяной земли, до которой от берега четырнадцать
    /// блоков. Выходило: план обещает «намыть волокна», счёт берёт шанс
    /// КОСТЯНОЙ ЗЕМЛИ, а бот уходит мыть ПЕСОК, в таблице которого волокон нет
    /// вовсе, — и стоит в воде до истечения срока, обещая то, чего из песка не
    /// бывает. Ровно «обещание без факта», запрещённое законом 4.
    ///
    /// Возвращает пару «где стоять — что мыть». null — такого места нет, и
    /// отказ обязан назвать, чего именно не хватило: воды или земли у воды.
    /// </summary>
    /// <param name="wanted">что нужно намыть; null/пусто — годится любой материал</param>
    public (BlockPos Stand, BlockPos Material)? FindSpot(string? wanted = null)
    {
        if (ctx.Self.Position is not { } p)
            return null;
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

        foreach (var вода in ctx.World.FindBlocks(
                     (cell, _) => ctx.World.IsWater(cell.X, cell.Y, cell.Z),
                     here, SearchRadius, height: 8, limit: 64))
        {
            // Стоять надо В воде, а дышать — НАД ней: клетка выше обязана быть
            // проходимой, иначе бот моет лоток, захлёбываясь
            if (!ctx.World.IsPassable(вода.X, вода.Y + 1, вода.Z))
                continue;
            if (NearestMaterial(вода, wanted) is { } материал)
                return (вода, материал);
        }
        return null;
    }

    /// <summary>
    /// Промываемый блок в пределах руки от клетки, НАД КОТОРЫМ УЖЕ ВОЗДУХ ИЛИ
    /// СТАНЕТ ВОЗДУХ.
    ///
    /// «Станет» — это про снег, и это не мелочь, а живая беда 27.08. Костяная
    /// земля у самой воды нашлась сразу (bonysoil (33, 112, 11), saltwater
    /// (33, 113, 9) — два блока), а промывка не начиналась вовсе: сверху лежал
    /// snowlayer-3. <c>BlockPan.TryTakeMaterial</c> в этом случае выходит МОЛЧА
    /// (клиенту он показывает ingameerror-panning-requireairabove, боту не
    /// показывает ничего), и «места для промывки рядом нет» было правдой ровно
    /// на толщину снега. Живой игрок снег сбивает рукой за миг.
    /// </summary>
    private BlockPos? NearestMaterial(BlockPos from, string? wanted = null)
    {
        for (int dx = -ReachFromWater; dx <= ReachFromWater; dx++)
        for (int dy = -ReachFromWater; dy <= ReachFromWater; dy++)
        for (int dz = -ReachFromWater; dz <= ReachFromWater; dz++)
        {
            var cell = new BlockPos(from.X + dx, from.Y + dy, from.Z + dz);
            string? код = ctx.World.GetBlockCode(cell);
            if (!ctx.World.IsPannable(код) || !Gives(код, wanted))
                continue;
            if (AirAbove(cell) || Clearable(Над(cell)))
                return cell;
        }
        return null;
    }

    /// <summary>
    /// Над клеткой воздух — то самое условие, которое игра проверяет сама
    /// (<c>BlockPan.TryTakeMaterial</c>: «если блок сверху не пустой — выйти»).
    /// </summary>
    private bool AirAbove(BlockPos cell) =>
        ctx.World.GetBlockId(cell.X, cell.Y + 1, cell.Z) == 0;

    /// <summary>
    /// МОЖНО ЛИ СБИТЬ ТО, ЧТО ЛЕЖИТ СВЕРХУ. Только МЕЛОЧЬ: у неё нет ни
    /// коллизии, ни блок-сущности — снежный покров, трава, цветы. Стену, пол и
    /// сундук ради горсти земли не ломают: заказчик уже видел бота, который
    /// выламывал пол его дома себе на столб, и второй такой дороги к кирке
    /// заводить нельзя. Последнее слово всё равно за общей заставой
    /// (<c>Mining.BreakAsync</c> → <c>BreakGuard.MayBreak</c>), эта проверка
    /// лишь не даёт к ней ходить с заведомо чужим.
    /// </summary>
    private bool Clearable(BlockPos cell) =>
        ctx.World.GetBlockId(cell.X, cell.Y, cell.Z) > 0 &&
        ctx.World.GetCollisionTop(cell.X, cell.Y, cell.Z) <= WorldModel.StepHeight &&
        ctx.World.GetBlockEntity(cell) == null;

    /// <summary>Клетка над этой — одно место на весь лоток, чтобы не разъехалось.</summary>
    private static BlockPos Над(BlockPos cell) => new(cell.X, cell.Y + 1, cell.Z);

    /// <summary>
    /// ПРОМЫТЬ СТОЛЬКО-ТО РАЗ ИМЕННО РАДИ <paramref name="wanted"/>.
    ///
    /// ЧТО ЗДЕСЬ ФАКТ, А ЧТО НЕТ — БЕЗ ПРИУКРАШИВАНИЯ (закон 4). Подпись этого
    /// метода обещала: «промывка удалась — значит в сумках прибавилось»; это
    /// было неправдой, и приёмка 27.08 назвала её седьмой находкой.
    /// <list type="bullet">
    /// <item>ФАКТ ОТ СЕРВЕРА: горсть взята — блок в мире переменился, и это
    /// проверяется каждый круг. Число в <see cref="PanReport.Pans"/> — счёт
    /// именно ВЗЯТЫХ ГОРСТЕЙ.</item>
    /// <item>ФАКТ ОТ СЕРВЕРА: что выпало — прибавка в сумках
    /// (<see cref="PanReport.Gained"/>).</item>
    /// <item>НЕ ФАКТ, И ТАК И СКАЗАНО: что промывка дошла до конца. Игра о ней
    /// боту не шлёт НИЧЕГО (порог <c>secondsUsed &gt;= 3.4f</c> считает сервер
    /// молча), поэтому «промыл» здесь — это «подержал кнопку дольше порога»,
    /// а не подтверждение.</item>
    /// </list>
    /// </summary>
    /// <param name="pans">сколько горстей взять за этот заход</param>
    /// <param name="wanted">ради чего моем; null — любой промываемый материал</param>
    /// <param name="ct">отмена</param>
    public async Task<PanReport> PanAsync(int pans, string? wanted = null,
                                          CancellationToken ct = default)
    {
        var пусто = new Dictionary<string, int>();
        if (!Ready)
            return new PanReport(0, пусто,
                "реестр блоков сервера ещё не пришёл — про лоток сказать нечего");
        if (CarriedPan is not { } лоток)
            return new PanReport(0, пусто,
                "лотка нет: промывать нечем. Лоток делается из бревна и любого ножа " +
                "(рецепт сервера «pan-wooden»); положите готовый в сумку или сделайте");

        if (FindSpot(wanted) is not { } место)
            return new PanReport(0, пусто, ПочемуНекудаВстать(wanted));

        var (встать, материал) = место;
        OnLog?.Invoke($"мою лотком: стою в воде {встать}, беру горсть из " +
                      $"{ctx.World.GetBlockCode(материал)} {материал}");

        var было = Снимок();
        int промыто = 0;
        for (int i = 0; i < pans && !ct.IsCancellationRequested; i++)
        {
            // 1. Встать В ВОДУ. Спрашиваем каждый круг: течение и всплытие
            //    выносят тело из клетки, а игра смотрит на ноги в этот самый миг
            if (!FeetInWater && !await ctx.Movement.MoveToCellAsync(встать, ct))
            {
                OnLog?.Invoke($"до воды {встать} не дошёл — промывка без воды невозможна");
                break;
            }
            if (!FeetInWater)
            {
                OnLog?.Invoke("ноги не в воде: игра такую промывку не считает " +
                              "(ingameerror-panning-notinwater) — не вру, что помыл");
                break;
            }

            // 2. СНЕГ СВЕРХУ — СБИТЬ. Игра требует над промываемым воздух и
            //    молчит, когда его нет: без этого шага «места нет» было бы
            //    правдой на всю зиму
            if (!AirAbove(материал) && Clearable(Над(материал)))
            {
                var снял = await mining.BreakAsync(Над(материал), ct);
                if (!снял.Success || !AirAbove(материал))
                {
                    OnLog?.Invoke($"над {материал} не воздух " +
                                  $"({ctx.World.GetBlockCode(материал.X, материал.Y + 1, материал.Z)}), " +
                                  $"и убрать не вышло: {снял.Message} — горсть игра не отдаст");
                    break;
                }
            }

            // 3. Горсть. Факт — перемена блока в мире
            string? до = ctx.World.GetBlockCode(материал);
            if (!ctx.World.IsPannable(до))
            {
                // Слои кончились — ищем следующий блок у той же воды
                if (NearestMaterial(встать, wanted) is not { } ещё)
                {
                    OnLog?.Invoke($"промываемое у воды {встать} кончилось после {промыто} промывок");
                    break;
                }
                материал = ещё;
                до = ctx.World.GetBlockCode(материал);
            }
            if (await hands.TakeToHandAsync(лоток, ct) is null)
            {
                OnLog?.Invoke($"лоток {лоток} не взять в руку — промывка не начата");
                break;
            }
            await hands.UseHeldAsync(0.2, материал, onFace: 4, ct: ct);
            await Task.Delay(400, ct).ContinueWith(_ => { });
            string? сталоТут = ctx.World.GetBlockCode(материал);
            if (сталоТут == до)
            {
                OnLog?.Invoke($"сервер горсть не отдал: {материал} как был {до}. " +
                              "Чаще всего это чужая заявка на землю или не воздух над блоком");
                break;
            }
            // ЧТО ОБЕЩАЛА ИГРА — говорим вслух, когда вышло иначе. Решает всё
            // равно перемена блока выше: ожидание считается по коду, а карты
            // вариантов сервер не шлёт (см. AfterHandful)
            string ждали = AfterHandful(до ?? "", ctx.World.PannedInto(до));
            if (!string.Equals(сталоТут ?? "", ждали, StringComparison.OrdinalIgnoreCase))
                OnLog?.Invoke($"горсть взята, но {материал} стал «{сталоТут}», " +
                              $"а игра обещала «{(ждали.Length == 0 ? "воздух" : ждали)}» — " +
                              "считаю по перемене блока, не по ожиданию");

            // 4. Промывка: держим дольше игрового порога
            await hands.UseHeldAsync(HoldSeconds, материал, onFace: 4, ct: ct);
            await Task.Delay(400, ct).ContinueWith(_ => { });
            промыто++;
        }

        var стало = Снимок();
        var прибавка = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (код, сколько) in стало)
        {
            int дельта = сколько - было.GetValueOrDefault(код);
            if (дельта > 0)
                прибавка[код] = дельта;
        }

        // СЛОВА РОВНО ПО ФАКТАМ: взятые горсти сервер подтвердил переменой
        // блока, прибавку — сумкой, а «промыл» подтвердить нечем, и потому
        // сказано «подержал кнопку», а не «промыл» (закон 4)
        string итог = промыто == 0
            ? "не взял ни одной горсти"
            : $"взял {промыто} горстей и промыл каждую" +
              (прибавка.Count == 0
                  ? " — не выпало ничего (лоток отдаёт не больше одной вещи за раз, и чаще пусто)"
                  : ": " + string.Join(", ", прибавка.Select(p => $"{p.Key} ×{p.Value}")));
        OnLog?.Invoke(итог);
        return new PanReport(промыто, прибавка, итог);
    }

    /// <summary>
    /// ОТКАЗ ОБЯЗАН НАЗВАТЬ, ЧЕГО ИМЕННО НЕ ХВАТИЛО — воды или земли у воды.
    /// «Негде мыть» без этого посылает человека искать наугад.
    /// </summary>
    private string ПочемуНекудаВстать(string? wanted = null)
    {
        if (ctx.Self.Position is not { } p)
            return "себя не вижу — где мыть, сказать не могу";
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

        int воды = ctx.World.FindBlocks((cell, _) => ctx.World.IsWater(cell.X, cell.Y, cell.Z),
            here, SearchRadius, height: 8, limit: 64).Count();
        // Считаем ТОЛЬКО ту землю, что отдаёт нужное: иначе отказ говорил бы
        // «промываемое рядом есть» про песок, из которого волокон не бывает
        var земля = ctx.World.FindBlocksNamed(
            (_, code) => ctx.World.IsPannable(code) && Gives(code, wanted),
            here, SearchRadius, height: 8, limit: 32).ToList();
        string чего = wanted is { Length: > 0 } ? $" (той, что отдаёт {wanted})" : "";

        if (воды == 0 && земля.Count == 0)
            return $"в {SearchRadius} блоках нет ни воды, ни промываемой земли{чего} — " +
                   "промывать негде и нечего";
        if (воды == 0)
            return $"промываемое рядом есть ({земля.Count} клеток, например " +
                   $"{земля[0].Code} {земля[0].Cell}), а ВОДЫ в {SearchRadius} блоках нет. " +
                   "Игра требует стоять ногами в воде (ingameerror-panning-notinwater)";
        if (земля.Count == 0)
            return $"вода рядом есть ({воды} клеток), а промываемой земли{чего} в " +
                   $"{SearchRadius} блоках нет";
        return $"вода есть ({воды} клеток) и промываемое есть ({земля.Count} клеток, например " +
               $"{земля[0].Code} {земля[0].Cell}), но друг от друга они дальше {ReachFromWater} " +
               "блоков: до земли от воды надо дотянуться рукой. Перенесите землю к воде " +
               "(«!добудь bonysoil N», потом положить у берега) или найдите берег с землёй";
    }

    /// <summary>Опись сумок кодами — чтобы прибавку считать фактом, а не памятью.</summary>
    private Dictionary<string, int> Снимок()
    {
        var итог = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, _, содержимое) in ctx.Self.CarrySlots())
            if (!содержимое.IsEmpty && содержимое.Code is { Length: > 0 } код)
                итог[код] = итог.GetValueOrDefault(код) + Math.Max(1, содержимое.Count);
        return итог;
    }
}
