using GameMaterial = Vintagestory.API.Common.EnumBlockMaterial;

namespace VsBotKit;

/// <summary>
/// Разбор кода руды и правило «руда ли это» — без сети и без мира: на входе
/// либо строка, либо уже полученные ОТВЕТЫ РЕЕСТРА.
///
/// Живёт отдельным файлом ровно поэтому: этим занимаются двое — безголовый
/// бот и клиентский мод. Написать разбор дважды значит гарантированно
/// разойтись в мелочах, а расходятся такие вещи всегда не вовремя.
/// </summary>
public static class OreCode
{
    /// <summary>
    /// РУДОПОДОБЕН ЛИ БЛОК — ПО ОТВЕТАМ РЕЕСТРА, А НЕ ПО ПРИСТАВКЕ КОДА.
    ///
    /// ЖИВОЙ СЛУЧАЙ, из-за которого правило переписано (19.08, читы включены).
    /// Здесь стояло <c>code.StartsWith("ore-")</c> — рукописная приставка вместо
    /// вопроса к игре. Метеоритное железо зовётся <c>meteorite-iron</c>, щебень
    /// его — <c>loosestones-meteorite-iron-*</c>, а падает с обоих
    /// <c>stone-meteorite-iron</c>: ни один из трёх кодов под приставку не
    /// подходит. Наряд честно писал «stone-meteorite-iron — не руда, беру
    /// обычным поручением» — и вся рудная машина проходила мимо: чит «сквозь
    /// камень», осмотр на 48 бл, ход по жиле, счёт металла. Заказчик увидел это
    /// как «с читами железо не добыл». По форме это ровно та же ошибка, что
    /// была со стрелами, где «враг» решался списком приставок.
    ///
    /// Признаков ДВА, и оба — ответы самой игры:
    ///   • материал блока игра зовёт рудой (<c>blockmaterial: "Ore"</c> стоит у
    ///     ore-graded, ore-ungraded и ore-gem в ассетах 1.22.7). Это и держит
    ///     на рудной дороге уголь, кварц и самоцветы: металла в них нет вовсе
    ///     (кварц плавится в СТЕКЛО), и одной проверки на металл им мало;
    ///   • в блоке или в том, что с него падает, реестр видит МЕТАЛЛ. Это и
    ///     есть метеорит: материал у него в ассетах самый обычный «Stone», а
    ///     выпадение стоит пятидесяти единиц металла
    ///     (<c>smeltedRatio 2 → ingot-meteoriciron</c>). С модовой рудой это
    ///     работает так же — своего списка кодов нет ни одного.
    /// </summary>
    /// <param name="material">Материал блока, как его зовёт САМА игра.</param>
    /// <param name="metalInside">
    /// В блоке или в его выпадениях есть металл — ответ реестра предметов
    /// (у безголового бота это <see cref="Metals.MetalInside"/>).
    /// </param>
    public static bool IsOre(GameMaterial material, bool metalInside) =>
        material == GameMaterial.Ore || metalInside;

    /// <summary>
    /// МЕТАЛЛ ЛИ ЭТА ВЕЩЬ — по ответам самой игры о ней, без списка кодов.
    ///
    /// Признаков два, и оба стоят в ассетах игры:
    ///   • разновидность «metal» — её и читает <c>ItemNugget.GetHeldItemInfo</c>,
    ///     печатая игроку «5 units of Copper» (ingot-copper, metalbit-iron…);
    ///   • «forgable» — вещь куют на наковальне. Ради железной крицы (ironbloom):
    ///     разновидностей у неё нет вовсе, а металл она самый настоящий, и
    ///     самородки лимонита с гематитом плавятся именно в неё.
    ///
    /// Живёт здесь, а не у счёта металла, потому что спрашивают об этом ДВОЕ:
    /// безголовый бот (<see cref="Metals.IsMetalThing"/>) и клиентский мод, у
    /// которого своего реестра нет — у него сама игра.
    /// </summary>
    public static bool IsMetalThing(Vintagestory.API.Common.CollectibleObject? thing) =>
        thing != null &&
        (thing.Variant.ContainsKey("metal") || thing.Attributes?["forgable"].AsBool() == true);

    /// <summary>
    /// ЕСТЬ ЛИ В ЭТОЙ ВЕЩИ МЕТАЛЛ — та же пара путей, какими его перебирает
    /// сама игра в <c>ItemOre.GetHeldItemInfo</c>:
    ///   1) вещь ПЛАВИТСЯ в металл — самородок, слиток, кусок метеорита;
    ///   2) вещь не плавится, но игра знает, сколько в ней металла
    ///      (<c>metalUnits</c>) — это кусок руды, его дробят молотом.
    ///
    /// Сколько именно металла — считает <see cref="MetalRules"/> у бота; здесь
    /// только «есть или нет», и этого хватает вопросу «руда ли это».
    /// </summary>
    /// <param name="thing">Вещь из реестра (null — реестр про неё молчит).</param>
    /// <param name="byCode">
    /// Чем найти вещь по коду: выход плавки надо проверить на металл, иначе
    /// кварц (плавится в стекло) сойдёт за руду металла.
    /// </param>
    public static bool MetalInThing(Vintagestory.API.Common.CollectibleObject? thing,
        System.Func<string, Vintagestory.API.Common.CollectibleObject?> byCode)
    {
        if (thing == null)
            return false;
        if (thing.CombustibleProps is
                { SmeltingType: Vintagestory.API.Common.EnumSmeltType.Smelt } melt &&
            melt.SmeltedRatio > 0 && melt.SmeltedStack is { StackSize: > 0, Code: { } into } &&
            IsMetalThing(byCode(into.ToShortString())))
            return true;
        return thing.Attributes?["metalUnits"].AsInt(0) > 0;
    }

    /// <summary>
    /// Что даёт руда, по-человечески. Заказчик увидел в списке «галену» и
    /// справедливо спросил, что это такое: коды игры — это минералы, а
    /// человеку нужен МЕТАЛЛ.
    ///
    /// Здесь не перевод, а ответ на вопрос «зачем оно мне»: галенит даёт
    /// свинец и серебро, касситерит — олово, и по этому и выбирают, куда идти.
    /// </summary>
    public static string WhatItGives(string metal) => metal switch
    {
        "nativecopper" => "медь (самородная)",
        "chalcopyrite" => "медь",
        "malachite" => "медь",
        "bornite" => "медь",
        "tetrahedrite" => "медь",
        "cassiterite" => "олово",
        "sphalerite" => "цинк",
        "bismuthinite" => "висмут",
        "galena" => "свинец и серебро",
        "nativesilver" => "серебро",
        "nativegold" => "золото",
        "limonite" => "железо",
        "magnetite" => "железо",
        "hematite" => "железо",
        "ilmenite" => "титан",
        "chromite" => "хром",
        "platinum" => "платина",
        "pentlandite" => "никель",
        "lignite" => "уголь (бурый)",
        "anthracite" => "уголь (антрацит)",
        "bituminouscoal" => "уголь",
        "quartz" => "кварц",
        "olivine" => "оливин",
        "sulfur" => "сера",
        "borax" => "бура (для плавки)",
        "cinnabar" => "киноварь (ртуть)",
        "fluorite" => "флюорит",
        "corundum" => "корунд",
        "diamond" => "алмаз",
        "emerald" => "изумруд",
        _ => metal
    };

    /// <summary>
    /// Насколько густо в блоке — словами. Богатство влияет на то, сколько
    /// самородков выпадет, и по нему решают, стоит ли жила похода.
    /// </summary>
    public static string RichnessWords(string richness) => richness switch
    {
        "poor" => "бедная",
        "medium" => "средняя",
        "rich" => "богатая",
        "bountiful" => "щедрая",
        _ => richness
    };

    /// <summary>
    /// ЧТО ИЩЕТ ЧЕЛОВЕК — металл, а не минерал.
    ///
    /// Игроку всё равно, малахит перед ним или халькопирит: ему нужна МЕДЬ.
    /// Минералов у меди пять, у железа три, а выбирать из тридцати позиций
    /// «все виды меди» — это работа за игру, а не игра.
    ///
    /// Здесь одна строка — один осмысленный выбор, и за ней список минералов,
    /// которые ему отвечают.
    /// </summary>
    public static readonly (string Name, string[] Minerals)[] MetalGroups =
    [
        ("медь",     ["nativecopper", "chalcopyrite", "malachite", "bornite", "tetrahedrite"]),
        ("олово",    ["cassiterite"]),
        ("железо",   ["limonite", "magnetite", "hematite"]),
        ("свинец",   ["galena"]),
        ("серебро",  ["nativesilver", "galena"]),
        ("золото",   ["nativegold"]),
        ("цинк",     ["sphalerite"]),
        ("висмут",   ["bismuthinite"]),
        ("никель",   ["pentlandite"]),
        ("хром",     ["chromite"]),
        ("титан",    ["ilmenite"]),
        ("платина",  ["platinum"]),
        ("уголь",    ["lignite", "anthracite", "bituminouscoal"]),
        ("сера",     ["sulfur"]),
        ("бура",     ["borax"]),
        ("кварц",    ["quartz"]),
        ("самоцветы", ["diamond", "emerald", "corundum", "olivine", "fluorite"]),
    ];

    /// <summary>
    /// НАЗЫВАЕТ ЛИ КОД ИЗВЕСТНЫЙ МИНЕРАЛ — по СЛОВАРЮ ЧЕЛОВЕКА
    /// (<see cref="MetalGroups"/>), а не по реестру игры.
    ///
    /// ЭТО ПОСЛЕДНИЙ РУБЕЖ, И ЗВАТЬ ЕГО МОЖНО ТОЛЬКО ТАМ, ГДЕ РЕЕСТРА ПОД РУКОЙ
    /// НЕТ ВОВСЕ: чистому правилу отбора по одному лишь коду
    /// (<see cref="Ores.SuitsCode"/>) надо как-то отличить породу от руды, а
    /// спросить ему некого. Живой бот сюда не попадает: у него на этот вопрос
    /// отвечает реестр (<see cref="Ores.IsOre"/>), и метеорит, которого в
    /// словаре нет, узнаётся именно там.
    /// </summary>
    public static bool NamesAMineral(string code) =>
        code is { Length: > 0 } &&
        System.Array.Exists(MetalGroups, g => System.Array.Exists(g.Minerals,
            m => code.Contains(m, System.StringComparison.OrdinalIgnoreCase)));

    /// <summary>Подходит ли минерал под выбранный человеком металл.</summary>
    public static bool Matches(string groupName, string mineral)
    {
        if (string.IsNullOrEmpty(groupName))
            return true;   // «любая руда»
        foreach (var (name, minerals) in MetalGroups)
        {
            if (!string.Equals(name, groupName, System.StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (string m in minerals)
                if (string.Equals(m, mineral, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        // Не группа — значит спросили минерал по имени, как раньше
        return mineral.Contains(groupName, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// СЛОВАРЬ ЧЕЛОВЕКА, А НЕ РЕЕСТР ИГРЫ: минералы, которым бот умеет
    /// объяснить, что они дают (<see cref="WhatItGives"/>). Ровно для этого
    /// список и нужен — команда «.ap ores» в моде печатает его человеку.
    ///
    /// РЕШАТЬ ПО НЕМУ, РУДА ЛИ ЭТО, НЕЛЬЗЯ, и на этом обожглись: наряд считал
    /// рудой то, что стоит в этом списке или начинается на «ore-», — и
    /// метеоритного железа в нём не оказалось. «Руда ли это» спрашивают у
    /// реестра (<see cref="IsOre(GameMaterial, bool)"/>), а список остаётся
    /// тем, чем был: подсказкой на русском.
    /// </summary>
    public static readonly string[] KnownMetals =
    [
        "nativecopper", "chalcopyrite", "malachite", "bornite", "tetrahedrite",
        "cassiterite", "sphalerite", "bismuthinite", "galena", "nativesilver",
        "nativegold", "limonite", "magnetite", "hematite", "ilmenite", "chromite",
        "platinum", "pentlandite", "lignite", "anthracite", "bituminouscoal",
        "quartz", "olivine", "sulfur", "borax", "cinnabar", "fluorite",
        "corundum", "diamond", "emerald"
    ];

    /// <summary>Слова богатства, какие бывают в кодах игры.</summary>
    private static readonly string[] Richness = ["poor", "medium", "rich", "bountiful"];

    /// <summary>
    /// Металл и богатство из кода блока.
    ///
    /// В игре ДВА вида кодов, и на этом мы уже обожглись:
    ///   ore-poor-cassiterite-andesite  — с богатством,
    ///   ore-quartz-granite             — без него.
    /// Разбор «второе слово это богатство, третье металл» на втором виде даёт
    /// металл «granite», и живьём бот радостно докладывал «нашёл: shale» —
    /// то есть шёл добывать породу вместо руды.
    ///
    /// Поэтому смотрим, ЧТО ИМЕННО стоит вторым словом, а не на его место.
    /// </summary>
    public static (string Metal, string Richness) Describe(string code)
    {
        var parts = code.Split('-');
        if (parts.Length < 2)
            return ("", "");

        bool hasRichness = parts.Length > 2 &&
            System.Array.Exists(Richness, r =>
                string.Equals(r, parts[1], System.StringComparison.OrdinalIgnoreCase));

        string rich = hasRichness ? parts[1] : "";
        string metal = hasRichness
            ? (parts.Length > 2 ? parts[2] : "")
            : parts[1];
        return (metal, rich);
    }
}
