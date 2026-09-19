using Newtonsoft.Json.Linq;

namespace VsBotKit;

/// <summary>
/// СКОЛЬКО ИГРОВЫХ ЧАСОВ ТУША ПРОЛЕЖИТ, ПРЕЖДЕ ЧЕМ ПРОПАСТЬ, — из описания
/// сущности в ассетах игры.
///
/// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ, ЖИВЫМИ СЛОВАМИ ЗАКАЗЧИКА (28.08): «трупы остаются с
/// вещами и не исчезают». Разделка ждала, пока освободится тело, а туша ждать
/// не умеет: она пропадает ВМЕСТЕ с добычей — на землю из неё не выпадает
/// ничего. Чтобы отказ «бросаю копать» был не капризом, а расчётом, боту надо
/// знать СРОК. Выдумывать его нельзя (закон 6), и спрашивается он у игры.
///
/// КАК ЭТО УСТРОЕНО В ИГРЕ (разобрано ilspycmd по VSEssentials.dll 1.22.7,
/// класс <c>EntityBehaviorDeadDecay</c>, не по памяти):
/// <list type="bullet">
/// <item><c>Initialize</c>: <c>HoursToDecay = typeAttributes["hoursToDecay"]
/// .AsFloat(96f)</c> — то есть срок берётся ИЗ САМОЙ ЗАПИСИ ПОВЕДЕНИЯ, а если
/// поля там нет, игра ставит <see cref="GameDefaultHours"/>. Это не наше
/// умолчание, а её собственное;</item>
/// <item><c>OnEntityDeath</c>: <c>TotalHoursDead = World.Calendar.TotalHours</c>
/// — миг смерти записывается в <c>WatchedAttributes["decay"]["totalHoursDead"]</c>,
/// а WatchedAttributes сервер шлёт клиенту. Значит «когда умерла» бот знает
/// от сервера, и только «сколько ей отведено» — отсюда;</item>
/// <item><c>OnGameTick</c>: <c>if (!Alive &amp;&amp; TotalHoursDead +
/// HoursToDecay &lt; Calendar.TotalHours) DecayNow()</c> — вот и весь срок.</item>
/// </list>
///
/// ПОЧЕМУ С ДИСКА, А НЕ ИЗ ПАКЕТА. <c>deaddecay</c> стоит в СЕРВЕРНОЙ половине
/// описания (<c>server.behaviors</c>), а клиенту едет только клиентская:
/// <c>EntityTypeNet.EntityPropertiesToPacket</c> кладёт в пакет
/// <c>properties.Client?.BehaviorsAsJsonObj</c> и ничего больше (разбор — у
/// <see cref="EntityTypes"/>). Ровно по той же причине читаются с диска
/// объявленные скорости тварей (<see cref="CreatureSpeeds"/>) и языковые файлы
/// (<see cref="GameLang"/>); третьего способа узнать серверную половину у бота
/// нет.
///
/// ЧИСЛА У ЗВЕРЕЙ РАЗНЫЕ, И РАЗНИЦА ОГРОМНА — потому и нельзя было обойтись
/// одним общим числом: у дрифтера <c>hoursToDecay: 3</c>
/// (lore/drifter.json 1.22.7), у волка <c>96</c> (animal/mammal/wolf-adult.json).
/// При обычной скорости времени это шесть реальных минут против трёх с
/// лишним часов: за дрифтером надо бросать кирку, за волком — нет.
///
/// ЧЕГО ЗДЕСЬ НЕТ. Твари, которой нет в ассетах установленной игры (мод, чужой
/// сервер), здесь не найдётся, и твари БЕЗ поведения <c>deaddecay</c> — тоже:
/// игра такую тушу по сроку не убирает вовсе. Оба случая отвечают null, и тот,
/// кто спрашивал, обязан сказать об этом своими словами, а не додумать число.
/// </summary>
public sealed class CarcassDecay
{
    /// <summary>
    /// СОБСТВЕННОЕ УМОЛЧАНИЕ ИГРЫ, когда в записи поведения срок не написан:
    /// <c>typeAttributes["hoursToDecay"].AsFloat(96f)</c>. Так стоит у лося
    /// (<c>{ code: "deaddecay" }</c> без единого поля) — и 96 часов он получает
    /// не от нас, а от <c>EntityBehaviorDeadDecay.Initialize</c>.
    /// </summary>
    public const double GameDefaultHours = 96;

    /// <summary>Как поведение распада зовётся в описании сущности.</summary>
    private const string BehaviorCode = "deaddecay";

    private readonly Dictionary<string, double> byCode = new(StringComparer.OrdinalIgnoreCase);

    private CarcassDecay() { }

    /// <summary>Сколько описаний с распадом прочитано (0 — ассетов не нашлось).</summary>
    public int Count => byCode.Count;

    /// <summary>Почему пусто — человеку, а не молчанием.</summary>
    public string? Why { get; private set; }

    // Ассеты меняются только вместе с установленной игрой: читаем один раз на
    // запуск, как и объявленные скорости тварей
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CarcassDecay> Read = new();

    /// <summary>Прочитать описания сущностей установленной игры (кэшируется).</summary>
    public static CarcassDecay Load(string? gameFolder = null) =>
        Read.GetOrAdd(gameFolder ?? "", _ => ReadFiles(gameFolder));

    /// <summary>Собрать из готовых чисел — для стенда и для тестов.</summary>
    public static CarcassDecay FromEntries(IReadOnlyDictionary<string, double> hours)
    {
        var d = new CarcassDecay();
        foreach (var (code, value) in hours)
            d.byCode[code] = value;
        return d;
    }

    /// <summary>
    /// Собрать из ОПИСАНИЙ СУЩНОСТЕЙ, как они написаны в файлах, — тем же
    /// разбором, каким читается диск. Нужно тому, у кого описания не на диске:
    /// проверке самого правила разбора и будущему чтению модовых наборов.
    /// </summary>
    public static CarcassDecay FromDescriptions(params string[] descriptions)
    {
        var d = new CarcassDecay();
        foreach (string text in descriptions)
            if (JToken.Parse(text) is JObject json)
                ReadOne(json, d.byCode);
        return d;
    }

    /// <summary>
    /// СКОЛЬКО ИГРОВЫХ ЧАСОВ ОТВЕДЕНО ТУШЕ ЭТОЙ ТВАРИ. null — такого кода в
    /// ассетах нет либо распада тела у него нет вовсе (тогда туша лежит, пока
    /// её не уберут иначе, и торопиться некуда).
    /// </summary>
    public double? HoursToDecay(string? code)
    {
        if (code is not { Length: > 0 })
            return null;
        // Домен в коде («game:drifter-normal») в описаниях не пишется — снимаем
        // его той же меркой, что и объявленные скорости
        string bare = code.StartsWith("game:", StringComparison.Ordinal) ? code[5..] : code;
        return byCode.TryGetValue(bare, out double hours) ? hours : null;
    }

    private static CarcassDecay ReadFiles(string? gameFolder)
    {
        var decay = new CarcassDecay();
        // ОБХОД ОПИСАНИЙ — ОБЩИЙ (см. EntityDescriptions): «где лежат описания
        // и как их читать» — одно правило на всех, кто их читает, и второй его
        // копии тут нарочно нет
        decay.Why = EntityDescriptions.ReadAll(gameFolder, "сроков распада туш",
            json => ReadOne(json, decay.byCode));
        if (decay.Why == null && decay.byCode.Count == 0)
            decay.Why = $"в {EntityDescriptions.Where(gameFolder)} не нашлось " +
                        "ни одного описания сущности с распадом тела";
        return decay;
    }

    /// <summary>
    /// Разобрать одно описание: найти поведение распада и раздать его срок всем
    /// настоящим кодам этой твари.
    ///
    /// РАЗВОРОТ ВАРИАНТОВ БЕРЁТСЯ ГОТОВЫЙ (<see cref="CreatureSpeeds.Variants"/>),
    /// своей копии тут нет нарочно: правило «код плюс по одному состоянию из
    /// каждой группы» — одно на все описания, и разъехаться этим двум чтениям
    /// значило бы получить у одного «wolf-adult», а у другого «wolf».
    /// </summary>
    private static void ReadOne(JObject json, Dictionary<string, double> into)
    {
        if (json["code"]?.Value<string>() is not { Length: > 0 } baseCode)
            return;
        if (Behavior(json) is not { } deaddecay)
            return;

        // Поля может не быть вовсе — и тогда срок ставит сама игра, а не мы
        double hours = deaddecay["hoursToDecay"]?.Value<double?>() ?? GameDefaultHours;
        foreach (string code in CreatureSpeeds.Variants(baseCode, json["variantgroups"] as JArray))
            into[code] = hours;
    }

    /// <summary>
    /// Запись поведения распада из СЕРВЕРНОЙ половины описания. Ищем по коду
    /// поведения, а не по месту в списке: порядок поведений — дело описания.
    /// </summary>
    private static JObject? Behavior(JObject json)
    {
        if (json["server"]?["behaviors"] is not JArray behaviors)
            return null;
        foreach (var behavior in behaviors)
            if (behavior is JObject b &&
                string.Equals(b["code"]?.Value<string>(), BehaviorCode, StringComparison.Ordinal))
                return b;
        return null;
    }
}
