using System.Text.Json.Serialization;

using Vintagestory.API.Common;

namespace VsBotKit;

/// <summary>
/// В ЧЁМ СЧИТАТЬ ЗАКАЗ НА МЕТАЛЛ.
///
/// Заказчик дословно: «я пишу добудь 20 меди, а он добывает 20 кусков
/// самородков, а не самородной меди, он не смотрит сколько и всего». Слово
/// «двадцать» у человека и у бота значило РАЗНОЕ, и спорить тут не о чем —
/// это надо было назвать вслух и дать выбрать.
///
/// Три ответа, и все три честные:
///   • <see cref="Штуки"/> — как было: двадцать подобранных кусков чего угодно,
///     хоть самородков по 5 единиц, хоть бедной руды по 15;
///   • <see cref="Единицы"/> — единицы металла, как их считает сама игра
///     (слиток — сто единиц, см. <see cref="MetalRules.UnitsPerIngot"/>);
///   • <see cref="Слитки"/> — те же единицы, но сотнями: «двадцать меди» — это
///     двадцать слитков, то есть на четыре кирки с топором.
///
/// Значение пишется СЛОВОМ, а не номером: без преобразователя окно управления
/// показало бы «2», и в сохранённый пресет лёг бы тот же номер — открыв файл
/// через полгода, понять из него нельзя ничего. Та же оговорка стоит у
/// <see cref="RevengePolicy"/> и <see cref="IdleAction"/>, и по той же причине.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MetalCount
{
    /// <summary>Штуки того, что выпало: самородки, куски руды, слитки — всё вперемешку.</summary>
    Штуки,

    /// <summary>Единицы металла игры: слиток — сто, самородок — пять.</summary>
    Единицы,

    /// <summary>Слитки: сто единиц металла в каждом.</summary>
    Слитки
}

/// <summary>
/// СКОЛЬКО МЕТАЛЛА В ОДНОЙ ШТУКЕ ЭТОЙ ВЕЩИ — ответ реестра, а не догадка.
/// </summary>
/// <param name="Code">Код вещи: «nugget-nativecopper», «ore-poor-hematite-andesite».</param>
/// <param name="Units">Единиц металла в ОДНОЙ штуке.</param>
/// <param name="Into">Во что она обращается: «ingot-copper», «ironbloom». Пусто — только дроблением.</param>
/// <param name="How">Чем берётся: «плавка» или «дробление» — это человеку в отчёт.</param>
public readonly record struct MetalWorth(string Code, int Units, string Into, string How)
{
    public override string ToString() =>
        $"{Code} — {Units} ед." + (Into.Length > 0 ? $" ({How} в {Into})" : $" ({How})");
}

/// <summary>
/// Что бот понял под заказом на металл: в чём считает, сколько это в единицах
/// и из чего это набирается. Готовится ДО выхода и говорится вслух.
/// </summary>
/// <param name="Metal">Слово человека: «медь», «cassiterite».</param>
/// <param name="Asked">Сколько просили — в <paramref name="Unit"/>.</param>
/// <param name="Unit">В чём считаем.</param>
/// <param name="Units">Сколько это единиц металла игры (0 — считаем штуками).</param>
/// <param name="Sources">Что даёт этот металл и по скольку единиц за штуку.</param>
public sealed record MetalPlan(string Metal, int Asked, MetalCount Unit, int Units,
    IReadOnlyList<MetalWorth> Sources)
{
    /// <summary>Металл ли это вообще: реестр знает хоть одну вещь с единицами.</summary>
    public bool IsMetal => Sources.Count > 0;

    public override string ToString() => MetalRules.Say(this);
}

/// <summary>
/// АРИФМЕТИКА МЕТАЛЛА — чистые правила, взятые у самой игры.
///
/// Откуда числа (Vintage Story 1.22.7, проверено по ассетам и по коду):
///   • assets/survival/itemtypes/resource/nugget.json — у каждого самородка
///     combustibleProps: smeltedRatio 20, smeltedStack ingot-* по одной штуке;
///   • Vintagestory.GameContent.ItemNugget.GetHeldItemInfo считает то, что
///     видит игрок в подсказке: units = SmeltedStack.StackSize * 100 / SmeltedRatio.
///     Отсюда и сотня: слиток — сто единиц, самородок — пять;
///   • Vintagestory.GameContent.ItemNugget.OnCreatedByCrafting дробит кусок руды
///     в самородки как metalUnits / 5 — той же пятёркой, что и выходит из
///     формулы выше;
///   • assets/survival/itemtypes/resource/ore-graded.json — metalUnits куска
///     руды: бедный 15, средний 20, богатый 25, щедрый 35 (у касситерита и
///     гематита свои).
///
/// Своей таблицы здесь НЕТ НИ ОДНОЙ: числа читаются из реестра сервера
/// (<see cref="Metals"/>), а тут только счёт.
/// </summary>
public static class MetalRules
{
    /// <summary>
    /// Единиц металла в слитке. Не выдумано: <c>ItemNugget.GetHeldItemInfo</c>
    /// делит сотню на <c>SmeltedRatio</c>, чтобы получить единицы одной штуки, —
    /// значит сотня и есть слиток.
    /// </summary>
    public const int UnitsPerIngot = 100;

    /// <summary>
    /// Сколько единиц металла в ОДНОЙ штуке — формулой самой игры.
    /// Ноль, если плавка бессмысленна (нулевое отношение).
    /// </summary>
    /// <param name="smeltedRatio">Сколько штук идёт на выход (CombustibleProperties.SmeltedRatio).</param>
    /// <param name="smeltedStackSize">Сколько слитков на выходе (JsonItemStack.StackSize).</param>
    public static int UnitsPerPiece(int smeltedRatio, int smeltedStackSize) =>
        smeltedRatio <= 0 || smeltedStackSize <= 0
            ? 0
            : smeltedStackSize * UnitsPerIngot / smeltedRatio;

    /// <summary>Перевести заказ («20 меди») в единицы металла.</summary>
    public static int ToUnits(int amount, MetalCount unit) => unit switch
    {
        MetalCount.Слитки => Math.Max(0, amount) * UnitsPerIngot,
        MetalCount.Единицы => Math.Max(0, amount),
        _ => 0   // штуками металл не мерят — считать нечего
    };

    /// <summary>
    /// Перевести добытые единицы в то, в чём человек просил.
    ///
    /// Слитки — ЦЕЛЫМ ДЕЛЕНИЕМ, и это не мелочь: 1999 единиц — это девятнадцать
    /// слитков, а не двадцать. Округлить вверх значило бы доложить «принёс 20»
    /// за неполный слиток, а нам обещано «успех — только по факту».
    /// </summary>
    public static int FromUnits(int units, MetalCount unit) => unit switch
    {
        MetalCount.Слитки => Math.Max(0, units) / UnitsPerIngot,
        MetalCount.Единицы => Math.Max(0, units),
        _ => 0
    };

    /// <summary>Как звать единицу счёта в отчёте: «слитков», «ед. металла», «шт.».</summary>
    public static string Word(MetalCount unit) => unit switch
    {
        MetalCount.Слитки => "слитков",
        MetalCount.Единицы => "ед. металла",
        _ => "шт."
    };

    /// <summary>
    /// ТО ЖЕ САМОЕ, НО В ОТВЕТЕ НА ВОПРОС «ЧЕМ СЧИТАЮ»: «слитками», «штуками».
    ///
    /// Падеж — не придирка, а то, читается фраза или спотыкается. У
    /// <see cref="Word"/> слово стоит ПОСЛЕ числа («принёс 19 из 20 слитков») и
    /// обязано быть в родительном; здесь оно стоит после «считаю» и обязано
    /// быть в творительном. Пока форма была одна, бот говорил «считаю СЛИТКОВ»
    /// — в чате это ещё сходило за опечатку, а теперь эта же строка стоит
    /// заголовком разбора в окне, где человек её читает глазами каждый раз.
    /// </summary>
    public static string Counting(MetalCount unit) => unit switch
    {
        MetalCount.Слитки => "слитками",
        MetalCount.Единицы => "единицами металла",
        _ => "штуками"
    };

    /// <summary>
    /// СКОЛЬКО ШТУК НАДО НАБРАТЬ, чтобы вышло столько единиц. Округление ВВЕРХ:
    /// 2000 единиц по 15 за кусок — это 134 куска, а не 133 с хвостиком.
    /// Ноль на входе (реестр не знает вещи) — ноль на выходе, а не деление на ноль.
    /// </summary>
    public static int PiecesFor(int units, int unitsPerPiece) =>
        unitsPerPiece <= 0 || units <= 0 ? 0 : (units + unitsPerPiece - 1) / unitsPerPiece;

    /// <summary>
    /// ЧТО БОТ ПОНЯЛ ПОД ЗАКАЗОМ — ОДНОЙ СТРОКОЙ, ЕЩЁ ДО ВЫХОДА.
    ///
    /// Это важнее самого выбора единицы: человек, написавший «добудь 20 меди»,
    /// обязан увидеть, во что бот превратил его двадцатку, ПОКА можно поправить,
    /// а не через сорок минут по содержимому сундука.
    ///
    /// Чистое правило: ни сети, ни мира — только то, что уже спросили у реестра.
    /// </summary>
    public static string Say(MetalPlan plan)
    {
        if (!plan.IsMetal)
            return Counted(plan);

        // Штуками металл не мерят, и перечень вещей означает тут ДРУГОЕ: не «во
        // что обойдётся заказ», а «сколько металла в том, что принесу»
        string откуда = plan.Unit == MetalCount.Штуки
            ? "Металла в них разное: "
            : "Набирается это так: ";
        return $"{Counted(plan)}. {откуда}{Sources(plan)}";
    }

    /// <summary>
    /// ТОЛЬКО СЧЁТ: в чём считаю и во что это выходит — без перечня вещей.
    ///
    /// Отделено от <see cref="Say"/> ради ОКНА. В чат обе половины идут одной
    /// фразой, и это правильно: в чате нет ни картинок, ни строк — сказать всё
    /// можно только словами. А в конструкторе задач вещи стоят рядом ЖИВЫМИ
    /// СТРОКАМИ с картинками (заказчик: «придумай красиво»), и повторять их же
    /// ещё раз прозой значит писать одно и то же дважды.
    ///
    /// Половины две, а счёт один: <see cref="Say"/> собран ИЗ ЭТОЙ строки, а не
    /// написан рядом с ней.
    /// </summary>
    public static string Counted(MetalPlan plan)
    {
        if (!plan.IsMetal)
            return $"«{plan.Metal} ×{plan.Asked}»: металла в этом реестр не знает — " +
                   $"считаю ШТУКАМИ того, что выпадет";

        if (plan.Unit == MetalCount.Штуки)
            return $"«{plan.Metal} ×{plan.Asked}»: считаю ШТУКАМИ того, что выпадет " +
                   $"(ручка «ВЧёмСчитатьМеталл»)";

        string сколько = plan.Unit == MetalCount.Слитки
            ? $"{plan.Asked} слитков — это {plan.Units} ед. металла"
            : $"{plan.Units} ед. металла — это {plan.Units / (double)UnitsPerIngot:0.##} слитка";

        return $"«{plan.Metal} ×{plan.Asked}»: считаю {Counting(plan.Unit).ToUpperInvariant()}, " +
               $"{сколько} (ручка «ВЧёмСчитатьМеталл»)";
    }

    /// <summary>
    /// ИЗ ЧЕГО НАБИРАЕТСЯ ЗАКАЗ — ВЕЩЬ ЗА ВЕЩЬЮ, ЧИСЛАМИ, А НЕ ФРАЗОЙ.
    ///
    /// Раньше этот счёт жил внутри строки для чата и наружу отдавался только
    /// склеенным текстом. Теперь то же самое просит ОКНО: заказчик просил
    /// «лучше отображать в вебформе тоже, придумай красиво в конструкторе
    /// задачи» — а окну нужны числа отдельно, чтобы поставить рядом с каждой
    /// вещью её картинку.
    ///
    /// Счёт от этого не раздвоился: <see cref="Say"/> теперь СОБИРАЕТ СВОЮ
    /// СТРОКУ ИМЕННО ОТСЮДА. Посчитай окно «сколько кусков» само — оно
    /// показывало бы одно, а бот говорил бы в чат другое, и спорить было бы не
    /// о чем.
    ///
    /// Порядок — от богатой вещи к бедной: человек, читающий разбор, хочет
    /// сперва увидеть, чем набрать заказ дешевле всего.
    /// </summary>
    public static IReadOnlyList<MetalPiece> Pieces(MetalPlan plan) =>
        plan.Sources
            .OrderByDescending(s => s.Units)
            .ThenBy(s => s.Code, StringComparer.Ordinal)
            .Select(s => new MetalPiece(s.Code, s.Units, PiecesFor(plan.Units, s.Units), s.How))
            .ToList();

    /// <summary>
    /// Сколько вещей называть в строке для человека. Строка идёт в ЧАТ, а там
    /// у меди пять минералов по четыре богатства — двадцать вещей в одной
    /// фразе человек не дочитает.
    /// </summary>
    private const int ВСтрокеВещей = 4;

    /// <summary>Из чего набирается заказ — той же арифметикой, что и <see cref="Pieces"/>.</summary>
    private static string Sources(MetalPlan plan) =>
        string.Join("; ", Pieces(plan).Take(ВСтрокеВещей).Select(п => п.ToString()));
}

/// <summary>
/// ОДНА СТРОКА РАЗБОРА: чем набирать заказ и сколько таких штук нужно.
/// </summary>
/// <param name="Code">Код вещи: «nugget-nativecopper», «ore-poor-nativecopper-andesite».</param>
/// <param name="Units">Единиц металла в ОДНОЙ штуке — ответ реестра игры.</param>
/// <param name="Pieces">
/// Сколько таких штук нужно на весь заказ. Ноль — считаем не единицами, а
/// штуками, и делить тут нечего.
/// </param>
/// <param name="How">Чем берётся: «плавка» или «дробление молотом в самородки».</param>
public readonly record struct MetalPiece(string Code, int Units, int Pieces, string How)
{
    public override string ToString() =>
        $"{Code} — {Units} ед." + (Pieces > 0 ? $", то есть около {Pieces} шт." : "");
}

/// <summary>
/// СКОЛЬКО В ЭТОМ МЕТАЛЛА — вопрос к РЕЕСТРУ СЕРВЕРА, единственное место.
///
/// Зачем отдельным классом. Спрашивают об этом ТРОЕ: наряд («набрал ли я
/// двадцать меди»), отчёт («в чём я считал») и панель («что бот понял»). Пока
/// ответа не было вовсе, наряд считал ШТУКИ — и заказчик получал двадцать
/// самородков (сто единиц, один слиток) вместо двадцати слитков.
///
/// Ни одной таблицы кодов здесь нет: и отношение плавки, и единицы куска руды
/// приходят с сервера в реестре предметов (Packet_ItemType.CombustibleProps и
/// Attributes), а собирает из них настоящие Item сама игра
/// (Vintagestory.Common.ItemTypeNet). С модовой рудой это работает так же.
/// </summary>
public sealed class Metals
{
    private readonly BotContext ctx;

    /// <summary>Ответ реестра про один код: спрашивается один раз (наряд зовёт на каждую пересчётку).</summary>
    private readonly Dictionary<string, MetalWorth?> known = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Металл ли эта вещь — тот же кэш, но про выход плавки.</summary>
    private readonly Dictionary<string, bool> metalThing = new(StringComparer.OrdinalIgnoreCase);

    public Metals(BotContext ctx) => this.ctx = ctx;

    /// <summary>Реестр предметов игры собран — без него отвечать нечем.</summary>
    public bool Ready => ctx.World.GameItemsReady;

    /// <summary>
    /// СКОЛЬКО МЕТАЛЛА В ОДНОЙ ШТУКЕ. null — реестр про эту вещь такого не знает
    /// (палка, доска, кварц: он плавится в стекло, а стекло — не металл).
    ///
    /// Два пути, и оба игровые — ровно в том же порядке, в каком их перебирает
    /// сама игра в <c>ItemOre.GetHeldItemInfo</c>:
    ///   1) вещь ПЛАВИТСЯ в металл — самородок, слиток, кусок металла;
    ///   2) вещь не плавится, но игра знает, сколько в ней металла
    ///      (<c>metalUnits</c>) — это кусок руды, его дробят молотом в самородки.
    /// </summary>
    public MetalWorth? Worth(string? code)
    {
        if (code is not { Length: > 0 })
            return null;
        if (known.TryGetValue(code, out var cached))
            return cached;

        MetalWorth? answer = null;
        var thing = ctx.World.GetCollectible(code);

        if (thing?.CombustibleProps is { SmeltingType: EnumSmeltType.Smelt } melt &&
            melt.SmeltedStack is { Code: { } into })
        {
            string result = into.ToShortString();
            int units = MetalRules.UnitsPerPiece(melt.SmeltedRatio, melt.SmeltedStack.StackSize);
            // ПЛАВИТСЯ — ЕЩЁ НЕ ЗНАЧИТ «МЕТАЛЛ». Кварц плавится в стекло по той
            // же строке ассетов (ore-quartz: smeltedRatio 4 → glass-plain ×2), и
            // без этой проверки наряд «кварц 20» отчитывался бы «слитками»
            if (units > 0 && IsMetalThing(result))
                answer = new MetalWorth(code, units, result, "плавка");
        }

        if (answer == null && thing?.Attributes?["metalUnits"] is { } attr && attr.Exists)
        {
            int units = attr.AsInt(0);
            if (units > 0)
                answer = new MetalWorth(code, units, "", "дробление молотом в самородки");
        }

        known[code] = answer;
        return answer;
    }

    /// <summary>
    /// МЕТАЛЛ ЛИ ЭТА ВЕЩЬ — спрошено у игры, а не по списку кодов.
    ///
    /// Само правило (разновидность «metal» либо «forgable») живёт у разбора
    /// руды (<see cref="OreCode.IsMetalThing"/>): тот же вопрос задаёт
    /// клиентский мод, у которого реестра нет — у него сама игра. Здесь только
    /// поход в реестр сервера и памятка на сессию.
    /// </summary>
    public bool IsMetalThing(string? code)
    {
        if (code is not { Length: > 0 })
            return false;
        if (metalThing.TryGetValue(code, out bool cached))
            return cached;

        bool metal = OreCode.IsMetalThing(ctx.World.GetCollectible(code));
        metalThing[code] = metal;
        return metal;
    }

    /// <summary>
    /// ЕСТЬ ЛИ МЕТАЛЛ В ЭТОМ БЛОКЕ — В НЁМ САМОМ ИЛИ В ТОМ, ЧТО С НЕГО ПАДАЕТ.
    ///
    /// Второе и есть главное. Метеорит (<c>meteorite-iron</c>) сам по себе ни
    /// во что не плавится: плавится ПАДАЮЩИЙ с него <c>stone-meteorite-iron</c>
    /// — пятьдесят единиц металла в штуке. То же и со щебнем на поверхности
    /// (<c>loosestones-meteorite-iron-*</c>), и с россыпями
    /// (<c>looseores-*</c> роняет самородки).
    ///
    /// Выпадения читает тот, кто их и держит (<see cref="Gathering.DropsOf"/> —
    /// с памяткой на сессию, дальше <see cref="WorldModel.DropsOfBlock"/>):
    /// второму такому же чтению разъехаться с первым было бы делом вечера.
    /// </summary>
    public bool MetalInside(string? code)
    {
        if (code is not { Length: > 0 })
            return false;
        if (Worth(code) != null)
            return true;
        foreach (string drop in ctx.Gathering is { } gathering
                     ? gathering.DropsOf(code)
                     : ctx.World.DropsOfBlock(code))
            if (Worth(drop) != null)
                return true;
        return false;
    }

    /// <summary>
    /// РУДОПОДОБЕН ЛИ БЛОК — ЕДИНСТВЕННОЕ МЕСТО, ГДЕ ЭТО СПРАШИВАЮТ У РЕЕСТРА.
    ///
    /// Само правило чистое и лежит у разбора кода
    /// (<see cref="OreCode.IsOre(Vintagestory.API.Common.EnumBlockMaterial, bool)"/>)
    /// — его же зовёт клиентский мод. Здесь только ответы игры: материал блока
    /// и металл в выпадениях.
    ///
    /// ОТВЕТ ЗАПОМИНАЕТСЯ, И ЭТО НЕ УКРАШЕНИЕ: осмотр округи радиусом 48 — это
    /// около миллиона вопросов о клетках, а разных кодов среди них десятки.
    /// Без памятки каждый вопрос стоил бы разбора выпадений.
    ///
    /// ПОКА РЕЕСТР НЕ ПРИШЁЛ — НЕ ЗАПОМИНАЕМ. «Нет» до реестра перестанет быть
    /// правдой через секунду, а запомненное «нет» осталось бы на всю сессию:
    /// та же охрана стоит у <see cref="Ores.StoneDrops"/> и
    /// <see cref="Errands.Sources"/>, и заведена она по живым случаям.
    /// </summary>
    public bool IsOreBlock(string? code)
    {
        if (code is not { Length: > 0 })
            return false;
        if (oreBlock.TryGetValue(code, out bool cached))
            return cached;

        // НАСТОЯЩИЙ ОБЪЕКТ БЛОКА — единственный, у кого есть и материал, и
        // выпадения. Нет его (реестр ещё в пути или кода такого нет) — это
        // «пока не знаю», и запоминать это нельзя
        if (ctx.World.GetGameBlockByCode(code) is not { BlockId: > 0 } block)
            return false;

        // Материал — ответ реестра БЛОКОВ, и предметы для него не нужны: так
        // уголь и кварц остаются рудой, даже когда реестр предметов ещё в пути
        if (OreCode.IsOre(block.BlockMaterial, metalInside: false))
            return oreBlock[code] = true;
        // А про металл спросить пока некого — и ответ «нет» запоминать нельзя
        if (!Ready)
            return false;

        return oreBlock[code] = OreCode.IsOre(block.BlockMaterial, MetalInside(code));
    }

    /// <summary>Рудоподобен ли блок — тот же кэш, что у <see cref="Worth"/>, но про блоки.</summary>
    private readonly Dictionary<string, bool> oreBlock = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// СКОЛЬКО МЕТАЛЛА В ДОБЫТОМ. Возвращает и штуки, и единицы, и то, чего не
    /// понял, — молчать про непонятое нельзя: человек прочтёт «принёс 0» и
    /// решит, что бот не копал.
    /// </summary>
    /// <param name="gained">Что прибавилось: код вещи → сколько штук.</param>
    /// <param name="mine">Идёт ли эта вещь в зачёт заказа (правило зовущего).</param>
    public MetalTally Count(IReadOnlyDictionary<string, int> gained,
        System.Func<string, bool> mine)
    {
        int pieces = 0, units = 0;
        var unknown = new List<string>();
        foreach (var (code, n) in gained)
        {
            if (n <= 0 || !mine(code))
                continue;
            pieces += n;
            if (Worth(code) is { } worth)
                units += worth.Units * n;
            else
                unknown.Add(code);
        }
        return new MetalTally(pieces, units, unknown);
    }

    /// <summary>
    /// ЧТО БОТ ПОНЯЛ ПОД ЗАКАЗОМ — собирается ДО выхода, чтобы сказать вслух.
    ///
    /// Из чего берётся металл, тоже спрашиваем у реестра: перебираем предметы,
    /// в коде которых стоит имя минерала («nativecopper», «malachite»), и
    /// оставляем те, про которые игра знает единицы. Для меди это самородок
    /// (5 ед.) и куски руды по богатству (15/20/25/35 ед.).
    /// </summary>
    /// <param name="wanted">Слово человека: «медь», «cassiterite».</param>
    /// <param name="asked">Сколько просили.</param>
    /// <param name="unit">В чём считаем (ручка роли).</param>
    public MetalPlan PlanFor(string wanted, int asked, MetalCount unit)
    {
        var sources = new List<MetalWorth>();
        var seen = new HashSet<int>();
        foreach (string mineral in MineralsOf(wanted))
            foreach (string code in ctx.World.SearchItemCodes(mineral, int.MaxValue))
                if (Worth(code) is { } worth && seen.Add(worth.Units))
                    sources.Add(worth);

        int units = sources.Count > 0 ? MetalRules.ToUnits(asked, unit) : 0;
        return new MetalPlan(wanted, asked, unit, units, sources);
    }

    /// <summary>
    /// Какие минералы отвечают слову человека. Правило одно и живёт у руды
    /// (<see cref="OreCode.MetalGroups"/>): второй список «медь — это ещё и
    /// малахит» разошёлся бы с первым в тот же вечер.
    ///
    /// Открыто наружу ради разбора заказа (<see cref="OrderPreview"/>): окну
    /// надо сказать, ИЗ КАКИХ БЛОКОВ берётся медь, а спрашивают это по
    /// минералу («nativecopper»), не по слову «медь» — блока с таким кодом в
    /// игре нет ни одного.
    /// </summary>
    public static IEnumerable<string> MineralsOf(string wanted)
    {
        foreach (var (name, minerals) in OreCode.MetalGroups)
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return minerals;
        return [wanted];
    }
}

/// <summary>Итог счёта: штуки, единицы металла и то, про что реестр промолчал.</summary>
/// <param name="Pieces">Сколько ШТУК подошло под заказ.</param>
/// <param name="Units">Сколько в них единиц металла.</param>
/// <param name="Unknown">Коды, про которые реестр единиц не знает.</param>
public readonly record struct MetalTally(int Pieces, int Units, IReadOnlyList<string> Unknown);
