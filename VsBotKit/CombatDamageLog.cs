using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Vintagestory.API.Config;

namespace VsBotKit;

/// <summary>
/// ОТ ЧЕГО ИМЕННО УБАВИЛОСЬ ЗДОРОВЬЕ — так, как это сказал СЕРВЕР.
///
/// Раньше причины не было вовсе: бой видел «здоровье стало меньше» и объявлял
/// нападение. В журнале заказчика это выглядит так (22:52 и рядом):
///     [чат:-5] Потеряно 0,03 хп дрифтер (моб)     → вход в бой
///     [чат:-5] Потеряно 1,07 хп от gravity        → ТОТ ЖЕ вход в бой
///     [чат:-5] Потеряно 0,13 хп от hunger         → ТОТ ЖЕ вход в бой
/// Словами заказчика: «боевой режим просто может бежать в стену, рандомно
/// включается, нужно разграничить причины». Причина «рандома» — вот она: от
/// падения и голода бот честно шёл искать обидчика в тридцати блоках.
/// </summary>
public enum DamageFrom
{
    /// <summary>Существо (моб). Единственный случай, когда есть кого бить.</summary>
    Creature,

    /// <summary>Другой игрок. Тоже живой противник.</summary>
    Player,

    /// <summary>Своё тело: голод, холод, жара, яд, удушье. Отбиваться не от кого.</summary>
    Body,

    /// <summary>Мир: падение, огонь, взрыв, блок-ловушка. Отбиваться не от кого.</summary>
    World,

    /// <summary>
    /// НЕ УРОН ВОВСЕ: здоровья ПРИБАВИЛОСЬ (повязка, еда, отдых). Сервер шлёт
    /// это той же группой −5 и теми же образцами (damagelog-heal).
    ///
    /// Отдельным значением, а не «просто не строка про урон», ради журнала:
    /// капающее лечение по 0,7 хп надо сворачивать в одну строку, а свернуть
    /// можно только то, что разобрано в число и причину. В бой это значение не
    /// ведёт никогда — <see cref="DamageHit.ByLiving"/> для него ложь, а
    /// <see cref="DamageLogRules.Read"/> его и вовсе не отдаёт.
    /// </summary>
    Heal,

    /// <summary>Строку журнала урона разобрать не вышло — врать не будем.</summary>
    Unknown
}

/// <summary>
/// ОДИН УДАР ПО БОТУ — факт от сервера, а не догадка.
/// </summary>
/// <param name="Amount">Сколько хп сняли (число из строки сервера).</param>
/// <param name="From">Кто/что ударило.</param>
/// <param name="Who">
/// Имя обидчика ТАК, КАК ЕГО НАЗВАЛ СЕРВЕР («дрифтер», «глубинный боуторн»,
/// имя игрока). Для урона от тела и мира — название вида урона, если сервер
/// его назвал («hunger», «gravity»), иначе null.
/// </param>
/// <param name="Raw">Исходная строка — чтобы человек мог сверить наш разбор.</param>
/// <param name="At">Когда пришла.</param>
public sealed record DamageHit(double Amount, DamageFrom From, string? Who, string Raw, DateTime At)
{
    /// <summary>Ударил ЖИВОЙ противник — только тогда бой имеет смысл.</summary>
    public bool ByLiving => From is DamageFrom.Creature or DamageFrom.Player;

    public override string ToString() =>
        $"{Amount:0.##} хп " + From switch
        {
            DamageFrom.Creature => $"от «{Who}» (моб)",
            DamageFrom.Player => $"от игрока {Who}",
            DamageFrom.Body => $"от своего тела ({Who ?? "вид урона не назван"})",
            DamageFrom.World => $"от мира ({Who ?? "вид урона не назван"})",
            DamageFrom.Heal => $"прибавилось ({Who ?? "источник не назван"})",
            _ => "неизвестно от чего"
        };
}

/// <summary>
/// ЧЕТЫРЕ ОБРАЗЦА СТРОК ЖУРНАЛА УРОНА — те самые, по которым сервер их и
/// собирает (EntityPlayer.OnHurt в VintagestoryAPI.dll):
///
///   Source == Entity          → damagelog-damage-byentity
///   Source == Player          → damagelog-damage-byplayer
///   вид урона — удар (Blunt / Piercing / Slashing) → damagelog-damage-attack
///   всё остальное             → damagelog-damage
///
/// Порядок в этом списке — ПОРЯДОК ПРОВЕРКИ, и он не случаен: «от {1}» из
/// простого образца подошло бы и к строке «от тупой удар (источник: Block)».
/// </summary>
/// <param name="ByEntity">Например: «Потеряно {0:0.##} хп {1} (моб)».</param>
/// <param name="ByPlayer">Например: «Потеряно {0:0.##} хп из-за игрока {1}».</param>
/// <param name="Attack">Например: «Потеряно {0:0.##} хп от {1} (источник: {2})».</param>
/// <param name="Plain">Например: «Потеряно {0:0.##} хп от {1}».</param>
/// <param name="HealPlain">Строка лечения — чтобы не принять её за удар.</param>
/// <param name="HealByEntity">Лечение от существа (бывает у модов).</param>
public sealed record DamageLogTemplates(
    string ByEntity, string ByPlayer, string Attack, string Plain,
    string? HealPlain = null, string? HealByEntity = null)
{
    /// <summary>Ключи игры, под которыми эти образцы лежат в языковых файлах.</summary>
    public const string KeyByEntity = "damagelog-damage-byentity";
    public const string KeyByPlayer = "damagelog-damage-byplayer";
    public const string KeyAttack = "damagelog-damage-attack";
    public const string KeyPlain = "damagelog-damage";
    public const string KeyHealPlain = "damagelog-heal";
    public const string KeyHealByEntity = "damagelog-heal-byentity";
}

/// <summary>
/// ЧИСТЫЕ ПРАВИЛА РАЗБОРА ЖУРНАЛА УРОНА: строка сервера → факт. Ни сети, ни
/// файлов — только счёт по образцу, поэтому всё закрыто тестами
/// (VsBotKit.Tests/CombatCauseTests.cs).
///
/// ПОЧЕМУ ВООБЩЕ РАЗБОР ТЕКСТА, А НЕ ПАКЕТ. Пакета с причиной урона нет.
/// Сервер кладёт в синхронизируемые атрибуты только onHurt (сколько сняли) и
/// onHurtDir (откуда толкнуло) — вида урона там НЕТ (Entity.ReceiveDamage,
/// VintagestoryAPI.dll). Единственное место, где причина доезжает до клиента, —
/// это сообщение в чат-группу −5, которое сервер шлёт ЛИЧНО пострадавшему
/// (EntityPlayer.OnHurt). Живой игрок читает ту же строку глазами.
///
/// СТРОКИ НЕ ВЫДУМАНЫ. Их берут из языковых файлов самой игры
/// (<see cref="GameLang"/>), на том языке, который бот сам попросил при входе
/// (Packet_ClientRequestJoin.Language), плюс английский — на него сервер
/// откатывается, когда нужного перевода у него нет (Lang.GetL).
/// </summary>
public static class DamageLogRules
{
    /// <summary>
    /// Образец игры → выражение для разбора. «{0:0.##}» становится числом,
    /// «{1}» и «{2}» — словами, всё прочее — буква в букву.
    ///
    /// Число ищем щедро: сервер печатает его СВОЕЙ культурой (string.Format в
    /// Lang), поэтому в живом журнале заказчика стоит «0,03» с запятой, а на
    /// другом сервере будет «0.03». Обе записи и неразрывный пробел разбирает
    /// <see cref="ReadNumber"/>.
    /// </summary>
    public static Regex ToRegex(string template)
    {
        var pattern = new System.Text.StringBuilder("^");
        int i = 0;
        while (i < template.Length)
        {
            var brace = PlaceHolder.Match(template, i);
            if (!brace.Success)
            {
                pattern.Append(Regex.Escape(template[i..]));
                break;
            }
            pattern.Append(Regex.Escape(template[i..brace.Index]));
            pattern.Append(brace.Groups[1].Value == "0"
                // {0} — всегда количество хп
                ? @"(?<hp>[0-9][0-9 \s.,]*)"
                // {1}, {2} — слова: лениво, иначе «(моб)» из хвоста уедет внутрь
                : $"(?<a{brace.Groups[1].Value}>.+?)");
            i = brace.Index + brace.Length;
        }
        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
    }

    private static readonly Regex PlaceHolder = new(@"\{(\d+)(?::[^}]*)?\}", RegexOptions.Compiled);

    /// <summary>
    /// Число из строки сервера. Пробелы (в том числе неразрывный) убираем,
    /// запятую считаем той же точкой: культура сервера нам неизвестна, а
    /// «сколько хп» она не меняет.
    /// </summary>
    public static double? ReadNumber(string text)
    {
        string clean = text.Replace(" ", "").Replace(" ", "").Replace(",", ".").Trim();
        return double.TryParse(clean, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    /// <summary>
    /// Виды урона, которые наносит СВОЁ ТЕЛО. Список не выдуман: это
    /// <see cref="Vintagestory.API.Common.EnumDamageType"/> из сборок игры,
    /// разделённый по простому признаку — от этого не отбиваются оружием.
    /// Сравниваем по имени, потому что сервер печатает вид урона СВОИМ языком
    /// (Lang.Get, а не Lang.GetL), и на английском сервере это ровно
    /// «hunger», «gravity» — как в журнале заказчика.
    /// </summary>
    private static readonly HashSet<string> BodyKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Vintagestory.API.Common.EnumDamageType.Hunger),
        nameof(Vintagestory.API.Common.EnumDamageType.Frost),
        nameof(Vintagestory.API.Common.EnumDamageType.Heat),
        nameof(Vintagestory.API.Common.EnumDamageType.Poison),
        nameof(Vintagestory.API.Common.EnumDamageType.Suffocation),
        nameof(Vintagestory.API.Common.EnumDamageType.Injury)
    };

    /// <summary>
    /// Своё тело это или мир. Разницы для боя нет никакой (бить некого и там,
    /// и там), но для журнала есть: «0,13 хп от hunger» человек должен читать
    /// как «пора есть», а не как «кто-то напал».
    /// </summary>
    public static DamageFrom KindOf(string? word) =>
        word is { Length: > 0 } && BodyKinds.Contains(word.Trim())
            ? DamageFrom.Body
            : DamageFrom.World;

    /// <summary>
    /// РАЗОБРАТЬ ОДНУ СТРОКУ. null — «это не про урон» (лечение, чужая строка,
    /// незнакомый образец). Молчаливого «наверное, ударили» здесь нет нарочно:
    /// именно эта догадка и гнала бота в бой от голода.
    /// </summary>
    public static DamageHit? Read(string line, DamageLogSample sample, DateTime at) =>
        ReadLine(line, sample, at) is { From: not DamageFrom.Heal } hit ? hit : null;

    /// <summary>
    /// ВСЁ, ЧТО СЕРВЕР СКАЗАЛ ЭТОЙ СТРОКОЙ, включая лечение. null — образец не
    /// подошёл (чужая строка).
    ///
    /// Зачем отдельная дверь. Бою лечение не нужно — ему нужен ответ «бить или
    /// нет», и <see cref="Read"/> нарочно отдаёт по лечению null. А ЖУРНАЛУ
    /// нужно и лечение: без числа и причины двадцать строк «Получено 0,7 хп от
    /// heal» не свернуть в одну «вылечился на 14 хп за 20 с». Разбор при этом
    /// ОДИН — тот же проход по тем же образцам, второй копии нет.
    /// </summary>
    public static DamageHit? ReadLine(string line, DamageLogSample sample, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        string text = line.Trim();

        foreach (var (from, rx) in sample.InOrder)
        {
            var m = rx.Match(text);
            if (!m.Success)
                continue;
            if (ReadNumber(m.Groups["hp"].Value) is not { } hp)
                return null;
            string? who = m.Groups["a1"].Success ? m.Groups["a1"].Value.Trim() : null;
            // from == null — образец лечения: урона не было, здоровья прибавилось
            var kind = from == null ? DamageFrom.Heal
                     : from == DamageFrom.World ? KindOf(who)
                     : from.Value;
            return new DamageHit(hp, kind, who, text, at);
        }
        return null;
    }

    /// <summary>
    /// КАПЛЯ ЭТО ИЛИ НОВОСТЬ — чистое правило журнала.
    ///
    /// Живой случай, журнал заказчика 16.08 20:22: двадцать строк «Получено
    /// 0,7 хп от heal» подряд за полминуты и «Потеряно 0,13 хп от hunger»
    /// каждые шесть секунд весь прогон. Одно и то же событие, читать его
    /// человеку незачем — но и выбросить нельзя: сумма и причина ему нужны.
    ///
    /// Каплей считается всё, от чего НЕ ОТБИВАЮТСЯ: лечение, своё тело (голод,
    /// холод, яд), мир (падение, огонь). Удар живого противника каплей не
    /// бывает никогда — он редок и важен, и выходит в журнал сразу же.
    /// </summary>
    public static bool IsTrickle(DamageHit hit) => !hit.ByLiving;

    /// <summary>
    /// ЭТО НЕ ЗАЖИВЛЕНИЕ, А ВОССТАНОВЛЕНИЕ ЦЕЛИКОМ — возрождение, лечение
    /// хозяином, смена мира.
    ///
    /// ЖИВОЙ СЛУЧАЙ 23.08, 11:02:21, строка из журнала заказчика:
    ///     промолчал о повторах: «лечение (heal)» ещё 20 (10012,3 хп)
    /// Десять тысяч хп «вылечился» — это возрождение (сервер прислал «Получено
    /// 9999 хп от heal»), сложенное с двадцатью каплями обычного заживления по
    /// 0,7. Число не выдумано ботом и не ошибка счёта: сервер правда сказал
    /// 9999. Но человеку в этой сумме нет ни одного полезного бита.
    ///
    /// ПРИЗНАК — ФАКТ ИГРЫ, А НЕ ПОРОГ НАУГАД. Заживление добавляет доли:
    /// восстановить ЦЕЛУЮ полосу здоровья одним разом оно не может никогда.
    /// Значит прибавка не меньше всей полосы — это не заживление, чем бы она
    /// ни была вызвана. Полосу спрашиваем у самой игры (EntityBehaviorHealth,
    /// maxhealth), а не назначаем числом здесь.
    ///
    /// Полосы не знаем (сущность ещё не пришла) — не решаем: пусть свёртка
    /// работает как прежде. Врать «это возрождение» на пустом месте хуже, чем
    /// сложить лишнюю строку.
    /// </summary>
    public static bool IsFullHeal(DamageHit hit, double? maxHealth) =>
        hit.From == DamageFrom.Heal && maxHealth is { } max && max > 0 && hit.Amount >= max;

    /// <summary>
    /// Как сказать про восстановление целиком. Число сервера остаётся в
    /// строке: журнал — это запись того, что он сказал, а не наш пересказ.
    /// </summary>
    public static string SayFullHeal(DamageHit hit) =>
        $"здоровье восстановлено целиком (сервер: {hit.Amount:0.##} хп) — " +
        "это не заживление, и в его сумму я это не кладу";

    /// <summary>
    /// ЧЕМ КАПЛЯ ОТЛИЧАЕТСЯ ОТ КАПЛИ — ключ свёртки. Лечение не глушит голод,
    /// голод не глушит падение: причина у них разная, и НОВАЯ причина обязана
    /// прозвучать сразу, а не ждать конца чужого окна молчания.
    /// </summary>
    public static string TrickleKey(DamageHit hit) =>
        hit.From == DamageFrom.Heal
            ? $"лечение ({hit.Who ?? "источник не назван"})"
            : $"урон от {hit.Who ?? "неназванного"}";

    /// <summary>
    /// ОДНА СТРОКА ВМЕСТО ДЕСЯТКОВ: сколько накапало, за сколько и сколько раз.
    /// Слова заказчика — «вылечился на N хп за M с».
    /// </summary>
    public static string SayTrickle(DamageHit hit, LogRepeats.Folded fold) =>
        (hit.From == DamageFrom.Heal ? "вылечился на" : "потерял") +
        $" {fold.Sum:0.##} хп за {fold.Seconds:0} с " +
        $"({fold.Missed + 1} раз, {hit.Who ?? "причина не названа"})";
}

/// <summary>
/// Готовые к разбору образцы одного языка. Отдельным типом — потому что
/// языков всегда ДВА: тот, что бот попросил, и английский, на который сервер
/// откатывается, не найдя перевода (Lang.GetL → DefaultLocale).
/// </summary>
public sealed class DamageLogSample
{
    /// <summary>Пары «что это значит → чем ловить», в порядке проверки.</summary>
    public IReadOnlyList<(DamageFrom? From, Regex Rx)> InOrder { get; }

    public DamageLogSample(DamageLogTemplates t)
    {
        var list = new List<(DamageFrom?, Regex)>
        {
            // Сперва самые узнаваемые: у них есть хвост («(моб)», «игрока»)
            (DamageFrom.Creature, DamageLogRules.ToRegex(t.ByEntity)),
            (DamageFrom.Player, DamageLogRules.ToRegex(t.ByPlayer)),
            (DamageFrom.World, DamageLogRules.ToRegex(t.Attack)),
            (DamageFrom.World, DamageLogRules.ToRegex(t.Plain))
        };
        // Лечение ловим НАРОЧНО, чтобы честно сказать «это не урон», а не
        // молча пропустить строку в общий мешок непонятого
        if (t.HealByEntity is { Length: > 0 } he)
            list.Insert(0, (null, DamageLogRules.ToRegex(he)));
        if (t.HealPlain is { Length: > 0 } hp)
            list.Insert(0, (null, DamageLogRules.ToRegex(hp)));
        InOrder = list;
    }
}

// ЯЗЫКОВЫЕ ФАЙЛЫ ИГРЫ живут отдельным файлом — GameLang.cs.
//
// ЧИТАТЕЛЕЙ У НИХ ТЕПЕРЬ ДВОЕ, и это уже не обещание, а факт:
//   1. разбор строк урона — отсюда;
//   2. поиск вещи по ЧЕЛОВЕЧЕСКОМУ имени («!добудь метеоритное железо 10») —
//      GameLang.FindByName, зовут его поручения (Errands.Resolve).
// Прежде здесь стояло честное «второй ЕЩЁ НЕ НАПИСАН»: живой бот на такое слово
// отвечал «не знаю, откуда берётся метеоритное железо», хотя блок в реестре был
// и звался «meteorite-iron». Теперь мост от русского слова к коду есть, и
// проверяется он стендом (NameLookupTests).
//
// Отдельный файл при этом оправдан и одним читателем: язык игры — механизм
// сам по себе (шестнадцать тысяч строк, подстановки «*», кэш на запуск), и
// держать его внутри журнала урона значило бы, что второй читатель заведёт
// себе своё чтение файлов — вторую механику в другом месте.

/// <summary>
/// ЖУРНАЛ УРОНА СЕРВЕРА — МЕХАНИЗМ: слушает чат-группу −5, разбирает строки
/// в факты и ПОМНИТ, кто как больно бьёт.
///
/// Замер силы удара — не украшение, а вторая половина ответа на вопрос
/// «выживу ли»: сколько именно снимает дрифтер, никакой пакет не сообщает, но
/// он говорит это сам, каждым ударом. Живой игрок узнаёт то же и тем же
/// способом. Поэтому в оценке боя стоит ЗАМЕР, а не выдуманное число.
/// </summary>
public sealed class DamageWatch
{
    private readonly object gate = new();
    private readonly List<DamageHit> recent = [];
    private readonly Dictionary<string, Blows> blows = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// ВСЯ ПОЛОСА ЗДОРОВЬЯ — спрашиваем её у того, кто знает тело.
    ///
    /// Отдельным швом, а не через весь состав бота, нарочно: этому классу от
    /// тела нужно ровно одно число, и брать ради него весь состав значило бы
    /// связать разбор чата с половиной библиотеки. Не поставили — правило
    /// про восстановление целиком просто не срабатывает
    /// (<see cref="DamageLogRules.IsFullHeal"/>), и свёртка работает как прежде.
    /// </summary>
    public Func<double?>? MaxHealth { get; set; }

    private DamageLogSample? mine;
    private DamageLogSample? english;

    private sealed class Blows
    {
        public double Worst;
        public DateTime LastAt;
        public double SumGaps;
        public int Gaps;
    }

    /// <summary>Сколько секунд держать разобранные удары (дальше они не новость).</summary>
    public double MemorySeconds { get; set; } = 60;

    /// <summary>Почему причину урона знать не можем (null — всё в порядке).</summary>
    public string? Why { get; private set; }

    /// <summary>Образцы прочитаны — причина урона нам доступна как ФАКТ.</summary>
    public bool Ready => mine != null || english != null;

    /// <summary>Языковые строки игры (по ним узнаём имя ударившего моба).</summary>
    public GameLang? Lang { get; private set; }

    /// <summary>Разобранный удар. Ключ к тому, лезть ли в бой.</summary>
    public event Action<DamageHit>? OnHurt;

    /// <summary>Строка группы −5, которую разобрать не вышло, — говорим вслух.</summary>
    public event Action<string>? OnPuzzled;

    /// <summary>Сколько ударов разобрано за сессию — по нему тик видит новое.</summary>
    public long Count { get; private set; }

    /// <summary>Последний разобранный удар.</summary>
    public DamageHit? Last { get; private set; }

    public DamageWatch(BotClient bot, string? gameFolder = null)
    {
        Setup(bot.Language, gameFolder);
        bot.OnChat += Handle;
        // КТО ГРУППУ ПОНИМАЕТ, ТОТ ЕЁ И ПЕЧАТАЕТ. Отличить каплю лечения от
        // удара дрифтера можно только разобрав строку, а разбирают её здесь
        bot.ChatLogRule = LogLine;
        // Новое соединение — новый мир и новые враги: сила их ударов к старому
        // миру отношения не имеет
        bot.OnSessionReset += Forget;
    }

    /// <summary>Стенд и тесты: собрать журнал на готовых образцах, без файлов игры.</summary>
    public DamageWatch(DamageLogTemplates templates, GameLang? lang = null)
    {
        mine = new DamageLogSample(templates);
        Lang = lang;
    }

    private void Setup(string language, string? gameFolder)
    {
        Lang = GameLang.Load(language, gameFolder);
        var fallback = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? null
            : GameLang.Load("en", gameFolder);

        mine = Lang?.DamageTemplates() is { } t ? new DamageLogSample(t) : null;
        english = fallback?.DamageTemplates() is { } e ? new DamageLogSample(e) : null;
        Lang ??= fallback;

        if (Ready)
            return;
        // МОЛЧАТЬ НЕЛЬЗЯ. Без образцов бой вернётся к прежней догадке «здоровье
        // упало — значит напали», то есть к той самой беде: голод и падение
        // снова погонят бота искать обидчика
        Why = $"языковых файлов игры не нашёл (язык «{language}», " +
              $"игра: {GameFolders.FindGame() ?? "не найдена"}) — " +
              "причину урона от сервера читать нечем";
    }

    private void Forget()
    {
        lock (gate)
        {
            recent.Clear();
            blows.Clear();
            Last = null;
        }
        repeats.Clear();
    }

    /// <summary>
    /// Разобрать одну строку чата — ту же, что приходит подпиской на бота.
    /// Открыта нарочно: стенд должен уметь подать боту ту самую строку из
    /// журнала заказчика, не поднимая сети.
    /// </summary>
    public void Notice(ChatMessage m) => Handle(m);

    /// <summary>
    /// Насколько сворачивать капающие строки журнала урона, секунд (0 — не
    /// сворачивать вовсе, как было до правки). Роль может открутить обратно,
    /// когда разбирает беду по живому журналу — так же, как у движения и добычи.
    /// </summary>
    public double QuietRepeatSeconds
    {
        get => repeats.QuietSeconds;
        set => repeats.QuietSeconds = value;
    }

    /// <summary>
    /// ГЛУШИТЕЛЬ ПОВТОРОВ — ОБЩИЙ, тот же, что у движения, добычи, разломов и
    /// укрытия (<see cref="LogRepeats"/>). Минута выбрана по живому случаю:
    /// голод каплет каждые шесть секунд весь прогон, лечение — раз в секунду
    /// по полминуты; за минуту оба дают по одной строке с суммой.
    /// </summary>
    private readonly LogRepeats repeats = new() { QuietSeconds = 60 };

    /// <summary>
    /// ЧТО ИЗ СТРОКИ ГРУППЫ −5 ПОПАДЁТ В ЖУРНАЛ ХОЗЯИНА (null — промолчать,
    /// повтор сосчитан). Ставится дверью <see cref="BotClient.ChatLogRule"/>.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 16.08, 20:22:50–20:23:16): двадцать строк
    /// «Получено 0,7 хп от heal» подряд и «Потеряно 0,13 хп от hunger» каждые
    /// шесть секунд. Ради ПРИЧИНЫ урона разбор этой группы и заводился, и
    /// выбросить её нельзя — но капающее человеку читать незачем.
    ///
    /// Правило: удар живого противника и незнакомая строка выходят СРАЗУ и
    /// слово в слово от сервера; капающее (лечение, голод, падение, огонь)
    /// сворачивается в одну строку с суммой. Ничего не пропадает: приступ,
    /// кончившийся сам собой, досказывается хвостом.
    /// </summary>
    public string? LogLine(ChatMessage m)
    {
        if (m.GroupId != GlobalConstants.DamageLogChatGroup)
            return m.Text;

        var at = DateTime.UtcNow;
        var hit = ReadAny(m.PlainText, at);

        // СПЕРВА СВОЯ ПРИЧИНА, И ТОЛЬКО ПОТОМ ХВОСТ ПРО ЧУЖИЕ. Порядок здесь
        // не вкусовщина, а условие того, чтобы обещанная строка вообще
        // прозвучала.
        //
        // ЧТО БЫЛО НЕ ТАК. Хвост шёл первым — и он забирает из памяти ВСЁ
        // остывшее, то есть в том числе ту самую причину, о которой эта строка
        // и есть: у капли, которая каплет ровно раз в окно молчания, приступ
        // «остывает» в тот же миг, когда приходит его продолжение. Свёртка
        // после хвоста начинала с чистого листа («промолчали 0 раз») и печатала
        // строку сервера как есть, а обещанное заказчику «вылечился на N хп за
        // M с» не звучало НИ РАЗУ. Оба конца при этом были зелёными: правило
        // считало верно, а живой путь до него не доходил.
        //
        // Теперь своя причина сворачивается ПЕРВОЙ и тем самым остаётся
        // тёплой; хвост досказывает ровно то, ради чего заведён, — приступы
        // ДРУГИХ причин, кончившиеся сами собой и потому иначе оставшиеся бы
        // несосчитанными до следующего такого же урона, то есть, может быть,
        // до следующего дня.
        string? line = m.Text;
        // ВОССТАНОВЛЕНИЕ ЦЕЛИКОМ — МИМО СВЁРТКИ, И ЭТО ГЛАВНОЕ ЗДЕСЬ. Сложи мы
        // его с заживлением, и человек прочитает «вылечился на 10012 хп» —
        // ровно та строка из живого журнала 23.08, в которой нет ни одного
        // полезного бита
        if (hit != null && DamageLogRules.IsFullHeal(hit, MaxHealth?.Invoke()))
            return Glue(repeats.Tail(at, "хп"), DamageLogRules.SayFullHeal(hit));

        if (hit != null && DamageLogRules.IsTrickle(hit))
        {
            var fold = repeats.Fold(DamageLogRules.TrickleKey(hit), at, hit.Amount);
            line = fold is not { } f ? null
                 // Первая такая причина (или первая после тишины) звучит как
                 // есть: это новость, и ждать её нельзя
                 : f.Missed == 0 ? m.Text
                 : DamageLogRules.SayTrickle(hit, f);
        }

        return Glue(repeats.Tail(at, "хп"), line);
    }

    /// <summary>Хвост и строка — одной строкой: у журнала на событие ровно одна.</summary>
    private static string? Glue(string? tail, string? line) =>
        tail == null ? line : line == null ? tail : $"{tail}; {line}";

    /// <summary>
    /// Разбор своим языком, а не вышло — английским (сервер откатывается на
    /// него, не найдя перевода). Лечение сюда входит: <see cref="LogLine"/> без
    /// него не свернёт каплю.
    /// </summary>
    private DamageHit? ReadAny(string text, DateTime at) =>
        (mine != null ? DamageLogRules.ReadLine(text, mine, at) : null)
        ?? (english != null ? DamageLogRules.ReadLine(text, english, at) : null);

    private void Handle(ChatMessage m)
    {
        // Журнал урона сервер шлёт в свою группу (−5, GlobalConstants
        // .DamageLogChatGroup) и ЛИЧНО пострадавшему. Общий чат сюда не
        // относится: там кто угодно может написать «Потеряно 5 хп дрифтер (моб)»
        if (m.GroupId != GlobalConstants.DamageLogChatGroup)
            return;
        var at = DateTime.UtcNow;
        string text = m.PlainText;

        var line = ReadAny(text, at);
        if (line == null)
        {
            // Незнакомый образец: строка видна человеку, а бой на неё не
            // реагирует — гадать «наверное, ударили» нельзя
            OnPuzzled?.Invoke(text);
            return;
        }
        // Лечение разобрано, но бою знать о нём нечего: здоровья ПРИБАВИЛОСЬ
        if (line.From == DamageFrom.Heal)
            return;
        var hit = line;

        lock (gate)
        {
            Count++;
            Last = hit;
            recent.Add(hit);
            recent.RemoveAll(h => (at - h.At).TotalSeconds > MemorySeconds);
            if (hit.ByLiving && hit.Who is { Length: > 0 } who)
                Remember(who, hit.Amount, at);
        }
        OnHurt?.Invoke(hit);
    }

    private void Remember(string who, double amount, DateTime at)
    {
        if (!blows.TryGetValue(who, out var b))
        {
            // Держим память небольшой: врагов за сутки набегает много, а
            // считается по ним всего два числа
            if (blows.Count >= 64)
                blows.Clear();
            blows[who] = b = new Blows();
        }
        b.Worst = Math.Max(b.Worst, amount);
        if (b.LastAt != default)
        {
            double gap = (at - b.LastAt).TotalSeconds;
            // Слишком редкие удары — это уже другая стычка, а не темп
            if (gap is > 0.05 and < 10)
            {
                b.SumGaps += gap;
                b.Gaps++;
            }
        }
        b.LastAt = at;
    }

    /// <summary>Самый сильный удар, который мы от него получали (null — не бил ни разу).</summary>
    public double? WorstHitFrom(string? who)
    {
        if (who is not { Length: > 0 })
            return null;
        lock (gate)
            return blows.TryGetValue(who, out var b) && b.Worst > 0 ? b.Worst : null;
    }

    /// <summary>Как часто он бьёт, секунд между ударами (null — замера нет).</summary>
    public double? HitEverySecondsFrom(string? who)
    {
        if (who is not { Length: > 0 })
            return null;
        lock (gate)
            return blows.TryGetValue(who, out var b) && b.Gaps > 0 ? b.SumGaps / b.Gaps : null;
    }

    /// <summary>
    /// Самый сильный удар, который бот вообще получал от живого противника.
    /// Нужен ровно там, где про КОНКРЕТНОГО врага замера ещё нет: считать его
    /// удар нулевым — значит объявить бой бесплатным.
    /// </summary>
    public double? WorstHitEver()
    {
        lock (gate)
        {
            double worst = 0;
            foreach (var b in blows.Values)
                worst = Math.Max(worst, b.Worst);
            return worst > 0 ? worst : null;
        }
    }

    /// <summary>
    /// Удары, пришедшие ПОСЛЕ того, как разобранных было <paramref name="count"/>.
    ///
    /// Именно так способность и должна их читать: судить один и тот же удар
    /// дважды — это второй вход в бой из-за одной царапины, а брать «последний»
    /// — значит проглядеть все, что пришли между тиками (моб успевает ударить
    /// не раз, пока бот занят телом).
    /// </summary>
    public IReadOnlyList<DamageHit> Since(long count)
    {
        lock (gate)
        {
            long fresh = Count - Math.Max(0, count);
            if (fresh <= 0)
                return [];
            int take = (int)Math.Min(fresh, recent.Count);
            return recent.Skip(recent.Count - take).ToList();
        }
    }

    /// <summary>Удары за последние столько секунд (свежие — в конце).</summary>
    public IReadOnlyList<DamageHit> Recent(double seconds)
    {
        var since = DateTime.UtcNow.AddSeconds(-seconds);
        lock (gate)
            return recent.Where(h => h.At >= since).ToList();
    }

    /// <summary>Одна строка для журнала и панели: что бот знает про свой урон.</summary>
    public override string ToString()
    {
        if (!Ready)
            return Why ?? "журнал урона не читается";
        lock (gate)
            return $"журнал урона читаю (язык «{Lang?.Code ?? "?"}»); " +
                   $"разобрано ударов: {Count}; " +
                   (blows.Count == 0
                       ? "живые противники меня ещё не задевали"
                       : "замерено: " + string.Join(", ",
                           blows.Select(kv => $"{kv.Key} до {kv.Value.Worst:0.##} хп")));
    }
}
