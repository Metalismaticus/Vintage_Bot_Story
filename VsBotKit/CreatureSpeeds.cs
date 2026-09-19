using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Util;

namespace VsBotKit;

/// <summary>
/// СКОЛЬКО ТВАРЬ ОБЪЯВИЛА СВОЕЙ СКОРОСТИ — из описания сущности в ассетах игры.
///
/// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ, ЖИВЫМ СЛУЧАЕМ (журнал 19.08, 18:35:49, и потом ещё
/// трижды слово в слово):
///   drifter-normal в бою — не связываюсь: если не трогать, за 10 с снимет
///   около 10,1 хп; дерусь не выйдет — скорость его ещё не мерил — гнаться
///   вслепую не буду; ухожу не выйдет — скорость его ещё не мерил — не знаю,
///   оторвусь ли; закладываюсь не выйдет — закладываться нечем
/// Все три выхода закрыты, и два из трёх — одной и той же нехваткой. Замер
/// теперь идёт от пакетов (см. <see cref="CombatPace"/>), но и он не всегда
/// поспевает: тварь, которая стоит и бьёт, скорости не покажет НИКОГДА, а
/// решать «догоню ли» надо сейчас. Тогда берётся объявленное — не догадка, а
/// то самое число, по которому её двигает сервер.
///
/// ГДЕ ИМЕННО ОНО ЛЕЖИТ (проверено по ассетам игры 1.22.7, не по памяти):
/// assets/&lt;домен&gt;/entities/**/*.json, в СЕРВЕРНОЙ половине описания —
/// <c>server.behaviors[code=taskai].aitasks[].movespeed</c>. У дрифтера это
/// 0.018 у погони (<c>seekentity</c>) и бегства, 0.01 и 0.008 у брождения.
/// Клиенту эта половина не рассылается вовсе: в пакете реестра
/// (<c>EntityTypeNet.EntityPropertiesToPacket</c>) едут класс, теги, коробки и
/// общие <c>attributes</c> — серверных задач там нет ни одной. Потому и читаем
/// с диска, как уже читаются языковые файлы (<see cref="GameLang"/>).
///
/// БЕРЁМ МАКСИМУМ ПО ВСЕМ ЗАДАЧАМ, а не скорость погони. Ошибиться здесь можно
/// только в одну сторону: «он медленнее, чем на самом деле» — это бот, который
/// побежал и не оторвался. Максимум ошибается в сторону осторожности.
///
/// ЧЕГО ЗДЕСЬ НЕТ. Модовой твари, которой нет в ассетах установленной игры,
/// здесь не найдётся — и выдумывать за неё число нельзя: ответ null, и бой
/// скажет об этом вслух своими словами.
/// </summary>
public sealed class CreatureSpeeds
{
    /// <summary>
    /// ДЛИНА ШАГА ИГРОКА ЗА ОДИН ФИЗИЧЕСКИЙ ТИК — та мерка, в которой игра
    /// записывает <c>movespeed</c> твари.
    ///
    /// Откуда это: <c>EntityControls.CalcMovementVectors</c> собирает игроку
    /// <c>WalkVector</c> длиной <c>dt · BaseMoveSpeed · MovespeedMultiplier ·
    /// OverallSpeedMultiplier</c>, а траверсер твари
    /// (<c>Vintagestory.Essentials.StraightLineTraverser.yawToMotion</c>,
    /// он же <c>WaypointsTraverser</c>) ставит ЕДИНИЧНЫЙ вектор и умножает его
    /// ровно на <c>movespeed · OverallSpeedMultiplier</c>. Дальше оба идут в
    /// ОДИН И ТОТ ЖЕ модуль земли (<c>PModuleOnGround</c>), где вектор
    /// превращается в движение одинаково для всех.
    ///
    /// Значит отношение скоростей — это отношение длин векторов, и «во сколько
    /// раз тварь медленнее игрока» считается без единой своей формулы.
    /// </summary>
    public static double PlayerWalkVector =>
        GlobalConstants.BaseMoveSpeed * BotGamePhysics.GamePlayerPhysics.TickSeconds;

    /// <summary>
    /// ОБЪЯВЛЕННОЕ <c>movespeed</c> — В БЛОКАХ В СЕКУНДУ, мерой самого бота.
    ///
    /// Пересчёт идёт через шаг игрока (см. <see cref="PlayerWalkVector"/>) и
    /// через ОДНО число скорости, которым бот меряет и себя
    /// (<see cref="Movement.BaseWalkSpeed"/>). Второй мерки заводить нельзя:
    /// вопрос «догоню ли» — это сравнение с собственной скоростью, и обе
    /// стороны сравнения обязаны быть в одной шкале.
    /// </summary>
    public static double BlocksPerSecond(double movespeed) =>
        movespeed / PlayerWalkVector * Movement.BaseWalkSpeed;

    private readonly Dictionary<string, double> byCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сколько описаний прочитано (0 — ассетов не нашлось).</summary>
    public int Count => byCode.Count;

    /// <summary>Почему пусто — человеку, а не молчанием.</summary>
    public string? Why { get; private set; }

    private CreatureSpeeds() { }

    // Ассеты меняются только вместе с установленной игрой, а описаний сущностей
    // под сотню: читаем один раз на запуск, как и языковые файлы
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CreatureSpeeds> Read = new();

    /// <summary>Прочитать описания сущностей установленной игры (кэшируется).</summary>
    public static CreatureSpeeds Load(string? gameFolder = null) =>
        Read.GetOrAdd(gameFolder ?? "", _ => ReadFiles(gameFolder));

    /// <summary>Собрать из готовых чисел — для стенда и для тестов.</summary>
    public static CreatureSpeeds FromEntries(IReadOnlyDictionary<string, double> speeds)
    {
        var s = new CreatureSpeeds();
        foreach (var (code, value) in speeds)
            s.byCode[code] = value;
        return s;
    }

    /// <summary>
    /// Собрать из ОПИСАНИЙ СУЩНОСТЕЙ, как они написаны в файлах, — тем же
    /// разбором, каким читается диск. Нужно тому, у кого описания не на диске:
    /// проверке правила разбора и будущему чтению модовых наборов.
    /// </summary>
    public static CreatureSpeeds FromDescriptions(params string[] descriptions)
    {
        var s = new CreatureSpeeds();
        foreach (string text in descriptions)
            if (JToken.Parse(text) is JObject json)
                ReadOne(json, s.byCode);
        return s;
    }

    /// <summary>
    /// САМАЯ БЫСТРАЯ ЗАДАЧА ЭТОЙ ТВАРИ, бл/с. null — такого кода в ассетах нет
    /// (мод, чужой сервер) либо задач с ходьбой у него нет вовсе (стрела,
    /// лежащая вещь). Врать за них нечем, и не надо.
    /// </summary>
    public double? TopSpeed(string? code)
    {
        if (code is not { Length: > 0 })
            return null;
        string bare = code.StartsWith("game:", StringComparison.Ordinal) ? code[5..] : code;
        return byCode.TryGetValue(bare, out var movespeed) ? BlocksPerSecond(movespeed) : null;
    }

    private static CreatureSpeeds ReadFiles(string? gameFolder)
    {
        var speeds = new CreatureSpeeds();
        // ОБХОД ОПИСАНИЙ — ОБЩИЙ (см. EntityDescriptions): тот же самый обход
        // нужен ещё и срокам распада туш, и держать его в двух местах значило
        // бы дважды обойти диск и однажды разойтись
        speeds.Why = EntityDescriptions.ReadAll(gameFolder, "объявленных скоростей тварей",
            json => ReadOne(json, speeds.byCode));
        if (speeds.Why == null && speeds.byCode.Count == 0)
            speeds.Why = $"в {EntityDescriptions.Where(gameFolder)} не нашлось " +
                         "ни одного описания сущности со скоростью хода";
        return speeds;
    }

    /// <summary>
    /// Разобрать одно описание: развернуть варианты в настоящие коды и взять
    /// самую быструю задачу для каждого.
    /// </summary>
    private static void ReadOne(JObject json, Dictionary<string, double> into)
    {
        if (json["code"]?.Value<string>() is not { Length: > 0 } baseCode)
            return;
        var tasks = AiTasks(json);
        if (tasks.Count == 0)
            return;

        foreach (string code in Variants(baseCode, json["variantgroups"] as JArray))
        {
            double top = 0;
            foreach (var task in tasks)
                if (SpeedOf(task, code) is { } speed && speed > top)
                    top = speed;
            if (top > 0)
                into[code] = top;
        }
    }

    /// <summary>
    /// Задачи ИИ из серверной половины описания. Ищем по наличию списка
    /// <c>aitasks</c>, а не по имени поведения: имя поведения — дело мода,
    /// а список задач опознаётся сам.
    /// </summary>
    private static List<JObject> AiTasks(JObject json)
    {
        var found = new List<JObject>();
        if (json["server"]?["behaviors"] is not JArray behaviors)
            return found;
        foreach (var behavior in behaviors)
            if (behavior["aitasks"] is JArray tasks)
                foreach (var task in tasks)
                    if (task is JObject t)
                        found.Add(t);
        return found;
    }

    /// <summary>
    /// Скорость ОДНОЙ задачи для ЭТОГО кода. Правило «…ByType» — игровое: маска
    /// сверяется тем же <see cref="WildcardUtil"/>, каким её сверяет игра, и
    /// подошедшая маска ЗАМЕНЯЕТ общее число (у медведя-полярного 0.06 против
    /// 0.025 у солнечного — в одной и той же задаче).
    /// </summary>
    private static double? SpeedOf(JObject task, string code)
    {
        if (task["movespeedByType"] is JObject byType)
            foreach (var pair in byType)
                if (WildcardUtil.Match(pair.Key, code) && pair.Value?.Value<double?>() is { } typed)
                    return typed;
        return task["movespeed"]?.Value<double?>();
    }

    /// <summary>
    /// РАЗВЕРНУТЬ ВАРИАНТЫ В НАСТОЯЩИЕ КОДЫ — так же, как это делает игра при
    /// сборке реестра: код плюс по одному состоянию из каждой группы, через
    /// дефис и в порядке групп («drifter» + type[normal…] → «drifter-normal»).
    ///
    /// Без разворота двух волков не различить: <c>wolf-adult.json</c> и
    /// <c>wolf-baby.json</c> объявляют ОДИН И ТОТ ЖЕ код «wolf» и разные
    /// скорости, и по одному коду волчонок оказался бы взрослым волком.
    /// </summary>
    public static List<string> Variants(string baseCode, JArray? groups)
    {
        var codes = new List<string> { baseCode };
        if (groups == null)
            return codes;
        foreach (var group in groups)
        {
            // Группа без перечисленных состояний (states берутся из свойств
            // мира) нам не по зубам — честно бросаем весь разворот, чем врать
            // половиной кодов
            if (group["states"] is not JArray states || states.Count == 0)
                return [];
            var next = new List<string>(codes.Count * states.Count);
            foreach (string code in codes)
                foreach (var state in states)
                    if (state.Value<string>() is { Length: > 0 } s)
                        next.Add(code + "-" + s);
            codes = next;
        }
        return codes;
    }
}
