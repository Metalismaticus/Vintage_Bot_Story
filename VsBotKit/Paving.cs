namespace VsBotKit;

/// <summary>
/// ДОРОГА И ДВЕРЬ — ОДНИ ПРАВИЛА НА БЕЗГОЛОВОГО БОТА И НА КЛИЕНТСКИЙ МОД.
///
/// ЗАЧЕМ ОТДЕЛЬНЫМ ФАЙЛОМ. Оба вопроса задаёт <see cref="IPathWorld"/>, и оба
/// имели ДВА РАЗНЫХ ОТВЕТА — приёмка 27.08 назвала это четвёртой находкой:
/// <list type="bullet">
/// <item>«дорога ли это»: бот решал по рукописному списку кусков кода, мод — по
/// множителю скорости из реестра. На мощёных дорогах заказчика они строили
/// РАЗНЫЕ маршруты по одному и тому же миру;</item>
/// <item>«дверь ли это»: бот брал двери из РЕЕСТРА (поведения Door, TrapDoor,
/// FenceGate) и знал слово «gate», а мод искал в коде подстроку «door». Для
/// мода калитка была стеной, и игрок обходил загон там, где бот шёл насквозь.</item>
/// </list>
/// Файл линкуется в мод так же, как <see cref="Footing"/> и <see cref="Ceiling"/>:
/// ни сети, ни мира тут нет — только чистые правила.
/// </summary>
public static class Paving
{
    /// <summary>
    /// КУСКИ КОДОВ РУКОТВОРНОЙ МОСТОВОЙ. Каждый проверен по ассетам
    /// установленной игры 1.22.7 (27.08), и это не педантизм:
    ///
    /// ЗДЕСЬ ЛЕЖАЛИ ДВА ВЫДУМАННЫХ КОДА — «gravelpath» и «flagstone». Блоков с
    /// такими кодами в игре НЕТ ВОВСЕ (grep по всему assets/survival —
    /// ни одного файла), и это ровно тот случай, на котором проект горел
    /// шестикратно. Убраны.
    ///
    /// Что осталось и где живёт: «path» — stonepath.json и wood/woodtyped/path.json;
    /// «cobblestone» — семь файлов; «brick» — шесть; «plank» — десять.
    ///
    /// ПОЧЕМУ СПИСОК ВООБЩЕ НУЖЕН, РАЗ ЕСТЬ РЕЕСТР. Множитель скорости выше
    /// единицы в ванили есть ТОЛЬКО у семейства stonepath (1,30 и 1,20) и у
    /// деревянной дорожки (1,12) — одиннадцать файлов на всю игру. А человек
    /// мостит двор и булыжником, и кирпичом, и досками: по ним идётся не
    /// быстрее, но это его дорога, и ломать её бот не должен. Поэтому ответ —
    /// сумма двух: «игра говорит, что тут быстрее» ИЛИ «человек это мостил».
    /// </summary>
    public static readonly string[] PavedParts = ["path", "cobblestone", "brick", "plank"];

    /// <summary>
    /// ДОРОГА ЛИ ЭТО. Одно правило на оба конца.
    /// </summary>
    /// <param name="walkSpeedMultiplier">Множитель скорости у блока из реестра (1 — обычный).</param>
    /// <param name="code">Код блока; null — клетка неизвестна.</param>
    /// <param name="parts">Куски кодов мостовой; null — <see cref="PavedParts"/>.</param>
    public static bool IsRoad(float walkSpeedMultiplier, string? code,
                              IEnumerable<string>? parts = null)
    {
        if (walkSpeedMultiplier > 1f)
            return true;
        if (code is not { Length: > 0 })
            return false;
        foreach (string part in parts ?? PavedParts)
            if (code.Contains(part, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Как у блока хранится состояние двери.</summary>
    public enum Door : byte
    {
        /// <summary>Не дверь вовсе.</summary>
        None = 0,
        /// <summary>Старая дверь: «-opened-» в коде.</summary>
        OpenByCode = 1,
        /// <summary>Старая дверь: «-closed-» в коде.</summary>
        ClosedByCode = 2,
        /// <summary>Новая дверь: код неизменен, состояние в блок-сущности.</summary>
        ByEntity = 3
    }

    /// <summary>Поведения реестра, которыми игра метит двери, люки и калитки.</summary>
    public static readonly string[] DoorBehaviors = ["Door", "TrapDoor", "FenceGate"];

    /// <summary>
    /// ДВЕРЬ ЛИ ЭТО И ГДЕ У НЕЁ СОСТОЯНИЕ — по реестру, а не по буквам кода.
    ///
    /// МАСКА ПО КОДУ УЗКАЯ НАРОЧНО: «hatch» ловил бы соломенные крыши (thatch!),
    /// и сотни блоков разом стали бы «дверьми», то есть проходимыми насквозь.
    /// А слово «gate» тут обязательно: у калиток игры (<c>woodenfencegate</c>,
    /// <c>roughhewnfencegate</c>, <c>wattlegate</c>, <c>fence-bamboogate</c>)
    /// слова «door» в коде нет вовсе, а класс у них <c>BlockFenceGate</c>.
    /// </summary>
    /// <param name="behaviors">Имена поведений блока из реестра сервера.</param>
    /// <param name="code">Код блока.</param>
    public static Door DoorKindOf(IEnumerable<string>? behaviors, string? code)
    {
        if (behaviors != null)
            foreach (string b in behaviors)
                foreach (string known in DoorBehaviors)
                    if (string.Equals(b, known, StringComparison.Ordinal))
                        // Поведение Door — это всегда новая дверь: состояние
                        // лежит в блок-сущности, код неизменен
                        return Door.ByEntity;

        if (code is not { Length: > 0 })
            return Door.None;
        if (!code.Contains("door", StringComparison.OrdinalIgnoreCase) &&
            !code.Contains("gate", StringComparison.OrdinalIgnoreCase))
            return Door.None;
        return code.Contains("opened", StringComparison.OrdinalIgnoreCase) ? Door.OpenByCode
             : code.Contains("closed", StringComparison.OrdinalIgnoreCase) ? Door.ClosedByCode
             : Door.ByEntity;
    }
}
