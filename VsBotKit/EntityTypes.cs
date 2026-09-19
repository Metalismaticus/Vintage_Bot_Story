namespace VsBotKit;

/// <summary>
/// ЧТО СЕРВЕР РАССКАЗАЛ ПРО ТИП СУЩНОСТИ: класс, теги, атрибуты, клиентские
/// поведения.
///
/// Теги — СЛОВА САМОЙ ИГРЫ, а не наши: у стрелы это «inanimate, projectile»,
/// у лежащей на земле вещи «inanimate, item», у дрифтера «rust-creature,
/// habitat-land». Придумывать их заново незачем и нельзя.
/// </summary>
/// <param name="Behaviors">
/// КЛИЕНТСКИЕ ПОВЕДЕНИЯ ТИПА — их сервер тоже шлёт, и до 27.08 они выбрасывались.
///
/// В пакет их кладёт <c>EntityTypeNet.EntityPropertiesToPacket</c> строкой
/// <c>properties.Client?.BehaviorsAsJsonObj</c> — то есть едет ровно блок
/// <c>client.behaviors</c> из описания сущности, каждое поведение своим куском
/// JSON с полем <c>code</c>. СЕРВЕРНЫЕ поведения НЕ едут (в упаковщике их нет
/// вовсе), и потому ни задач ИИ, ни <c>emotionstates</c>, ни условий появления
/// у бота нет и быть не может.
/// </param>
/// ЗДЕСЬ ЛЕЖАЛИ ЕЩЁ ПЯТЬ ПОЛЕЙ ПРО УМЕНИЯ ТВАРИ — среда обитания, лазанье по
/// лестницам, лазанье по любой стене, высота шага и множитель урона от падения.
/// Их собирал и подробно расписывал <c>HandlePacket</c>, а СПРАШИВАЛ ИХ НИКТО:
/// заводились они под поиск пути ЗА ТВАРЬ, а тот снят целиком (см.
/// <see cref="CreatureReach"/> — воздух вокруг бота обходится без чужих ног, и
/// сквозь блок не проходит ни ходящий, ни лезущий, ни летящий). Поля пережили
/// свою причину и стали разбором на каждый пакет типов ради никого — закон 8.
public sealed record EntityTypeInfo(
    string Code,
    string Class,
    IReadOnlySet<string> Tags,
    string? Attributes,
    EntityBox? Collision = null,
    EntityBox? Selection = null,
    IReadOnlySet<string>? Behaviors = null);

/// <summary>
/// КОРОБКА СУЩНОСТИ, блоков: ширина по земле и высота от НОГ.
///
/// Коробка у игры одна на обе стороны и не смещена: <c>Entity.SetCollisionBox</c>
/// делает из двух чисел <c>X1 = −длина/2, X2 = +длина/2, Y2 = высота</c>, то есть
/// она центрирована по X и Z вокруг положения сущности и растёт от ног вверх.
/// Своей мерки тут нет вовсе — это ровно те два числа, что пришли пакетом.
/// </summary>
/// <param name="Width">Сторона по земле (<c>hitboxSize.x</c> в ассете).</param>
/// <param name="Height">Высота от ног (<c>hitboxSize.y</c>).</param>
public readonly record struct EntityBox(double Width, double Height);

/// <summary>
/// РЕЕСТР ТИПОВ СУЩНОСТЕЙ ОТ СЕРВЕРА — одно место на весь проект.
///
/// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ, ЖИВЫМ СЛУЧАЕМ (журнал 17.08, 23:49:47 → 23:51:14).
/// Полторы минуты подряд, дважды в секунду:
///   отбиваюсь от bowtornprojectile / bowtornprojectile повержен
/// Сто восемьдесят пар. bowtornprojectile — это СТРЕЛА, воткнутая в стену
/// (скорость цели в оценке боя так и записана: 0 бл/с). Бой брал её в цели,
/// потому что список врагов был РУКОПИСНЫЙ и в нём стояло «bowtorn» —
/// а код стрелы начинается ровно с этих букв.
///
/// КАК ЭТО УСТРОЕНО В ИГРЕ (проверено по её же сборкам 1.22.7, не по догадке):
/// - типы сущностей приходят клиенту пакетом реестра
///   <c>Packet_ServerAssets(19).Entities[]</c> — там код, класс, теги,
///   атрибуты (<c>ServerMain</c> собирает его через
///   <c>EntityTypeNet.EntityPropertiesToPacket</c>);
/// - теги едут БИТОВОЙ МАСКОЙ (<c>Packet_TagSetFast.Part1..Part4</c> — это
///   ровно <c>TagSetFast.storage</c>, четыре по 64 бита), а имена к ним — в
///   том же пакете, <c>TagRegistries[]</c> с именем реестра «entities».
///   Смещение бита — это ПОРЯДКОВЫЙ НОМЕР имени в списке
///   (<c>ConcurrentTagRegistryFast</c>: <c>storage[i / 64] |= 1 &lt;&lt; i</c>,
///   а сервер сортирует имена по смещению перед отправкой). Клиент игры
///   собирает свой реестр этим же списком (<c>ClientSystemStartup.LoadTags</c>);
/// - у ВСЕГО неживого игра ставит тег <c>inanimate</c>: вещи на земле, стрелы
///   и копья, падающие блоки, лодки, манекены, лифт, соломенное чучело.
///   Двадцать шесть описаний сущностей в assets игры 1.22.7, и ни одного
///   зверя среди них.
///
/// Это МЕХАНИЗМ: он только пересказывает, что сказал сервер. Кого считать
/// врагом — дело роли и боя.
/// </summary>
public sealed class EntityTypes
{
    /// <summary>Имя реестра тегов сущностей в пакете (второй — «collectibles»).</summary>
    private const string TagRegistryName = "entities";

    /// <summary>Тег игры для всего неживого: вещи, снаряды, лодки, манекены.</summary>
    public const string InanimateTag = "inanimate";

    private readonly object gate = new();
    private Dictionary<string, EntityTypeInfo> byCode = new(StringComparer.Ordinal);
    private IReadOnlySet<string> tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Реестр пришёл и разобран (до этого спрашивать бесполезно).</summary>
    public bool Ready { get; private set; }

    /// <summary>Реестр разобран: сколько типов и сколько имён тегов.</summary>
    public event Action<int, int>? OnReady;

    /// <summary>Разбор реестра не удался — это НЕ молчание, а названная беда.</summary>
    public event Action<string>? OnParseError;

    public EntityTypes(BotClient bot) => bot.OnPacket += HandlePacket;

    /// <summary>
    /// Новая сессия: реестр придёт заново. Держаться за прошлый нельзя —
    /// на другом сервере другой набор модов и другой порядок тегов, то есть
    /// те же биты означали бы уже другие слова.
    ///
    /// ЦЕНА ЭТОГО — ОКНО СЛЕПОТЫ: пока реестр не пришёл, бот не считает врагом
    /// НИКОГО (прежний рукописный список имён работал и без реестра). Окно это
    /// открывается не однажды при старте, а на КАЖДОМ переподключении, и молча
    /// оставлять такое нельзя — <c>BehaviorAttackHostiles.FindAttacker</c>
    /// один раз говорит вслух «меня бьют, а врага узнать нечем».
    ///
    /// НАСКОЛЬКО ОНО ДЛИННОЕ — ЗАМЕРЕНО ЖИВЬЁМ 27.08, а не прикинуто. Прежняя
    /// волна написала «реестр приезжает задолго до первой твари» без единого
    /// замера, и приёмка справедливо назвала это догадкой. Живой журнал
    /// (out/волна-27-08-сборщик/бот-1/логи/bot.log):
    /// <code>
    ///   06:14:19.061  Сервер готов, запрашиваю вход в мир...
    ///   06:14:21.193  [мир] реестр сущностей: 820 типов, 37 признаков
    ///   06:14:21.397  Мир финализирован, подтверждаю загрузку
    /// </code>
    /// То есть реестр приходит РАНЬШЕ, чем мир финализирован, — на 204 мс, — а
    /// до финализации бот в мире ещё не стоит и рядом с ним нет никого. Окно
    /// слепоты закрывается ДО входа, и это теперь факт, а не оправдание.
    /// </summary>
    public void Reset()
    {
        lock (gate)
        {
            byCode = new Dictionary<string, EntityTypeInfo>(StringComparer.Ordinal);
            Ready = false;
        }
    }

    /// <summary>
    /// Что сервер знает про этот тип. Код принимается и с доменом
    /// («game:drifter-normal»), и без — сервер шлёт по-разному.
    /// </summary>
    public EntityTypeInfo? Get(string? code)
    {
        if (code is not { Length: > 0 })
            return null;
        lock (gate)
        {
            if (byCode.TryGetValue(Bare(code), out var t))
                return t;
        }
        return null;
    }

    /// <summary>
    /// СЕРВЕР ОБЪЯВИЛ ЭТОТ ТИП НЕЖИВЫМ — одна дверь на весь проект.
    ///
    /// Спрашивают разные: бой («стрела это или тварь»), снимок обстановки для
    /// нейросети («кто рядом»). Пока каждый спрашивал по-своему, в снимке
    /// значилось «Существа рядом: bowtornprojectile x3» — та же ложь, только
    /// адресованная не боту, а человеку и модели.
    ///
    /// Реестра ещё нет — отвечаем «не знаю, значит не объявлял»: выдумывать
    /// неживость за сервер нельзя, а здоровье спросит тот, кому это важно.
    /// </summary>
    public bool Inanimate(string? code) => Get(code)?.Tags.Contains(InanimateTag) ?? false;

    /// <summary>
    /// ЕСТЬ ЛИ У ТИПА ЭТОТ ТЕГ ИГРЫ. Теги — слова САМОЙ игры из её же ассетов,
    /// а не наш список: «animal», «huntable», «predator», «ferocious»,
    /// «tamed», «semitamed», «humanoid», «villager». Проверено по
    /// assets/survival/entities/ 1.22.7.
    ///
    /// Реестра ещё нет — «нет тега». Выдумывать за сервер нельзя; спрашивающий
    /// обязан уметь жить с «не знаю» (см. <see cref="HuntRules.WhyNotPrey"/>,
    /// где незнание НЕ разрешает бить).
    /// </summary>
    public bool HasTag(string? code, string tag) => Get(code)?.Tags.Contains(tag) ?? false;

    /// <summary>
    /// ВСЕ ИМЕНА МЕТОК, КАКИЕ ПРИСЛАЛ СЕРВЕР, — без оглядки на регистр.
    ///
    /// ЗАЧЕМ. Метки для обороны набирает ЧЕЛОВЕК в ручке «ВражескиеМетки», а
    /// опечатка в ней раньше просто не совпадала ни с чем: бот молча переставал
    /// считать врагом волка, и узнать об этом было неоткуда. Спросив у реестра,
    /// какие метки бывают, отказ может назвать опечатку по имени (закон 4).
    ///
    /// Реестра ещё нет — список пуст, и спрашивающий обязан это различать:
    /// «меток не бывает» и «я их ещё не видел» — РАЗНОЕ.
    /// </summary>
    public IReadOnlySet<string> TagNames
    {
        get { lock (gate) return tagNames; }
    }

    /// <summary>
    /// КОРОБКА СТОЛКНОВЕНИЙ ТВАРИ — ТА САМАЯ, ПО КОТОРОЙ ИГРА ЗАСЧИТЫВАЕТ
    /// ПОПАДАНИЕ СТРЕЛЫ, и она ПРИХОДИТ ПО СЕТИ.
    ///
    /// ЭТО НАХОДКА, И ОНА ПРОТИВОРЕЧИТ ТОМУ, ЧТО НАПИСАНО В ЛУЧЕ ЗРЕНИЯ.
    /// В <c>Sight.LookAtCreature</c> стоит «РОСТ ТВАРИ СЕРВЕР НЕ ШЛЁТ (коробка
    /// живёт в описании сущности, а его у бота нет)», и обе стороны луча
    /// меряются СВОИМ ростом. Разбор пакета реестра говорит обратное:
    /// <c>Packet_EntityType</c> везёт <c>CollisionBoxLength/Height</c> и
    /// <c>SelectionBoxLength/Height</c> — их кладёт туда
    /// <c>Vintagestory.Common.EntityTypeNet.EntityPropertiesToPacket</c>
    /// (сверено ilspycmd по 1.22.7). Значит рост цели у бота ЕСТЬ.
    ///
    /// ЗАЧЕМ ЭТО ЛУКУ. Попадание игра считает так
    /// (<c>EntityProjectileBase.TryHitTarget</c>): коробку цели двигают в её
    /// положение и пересекают с коробкой летящей стрелы. Без ширины и высоты
    /// цели решить «стоит ли выстрел» нечем: 0,6 бл дрифтера и 1,2 бл волка —
    /// это вдвое разный шанс с одного и того же места.
    ///
    /// Реестра ещё нет или сервер прислал нули — null, «не знаю». Выдумывать
    /// за сервер размер нельзя (правило 6), а зовущий обязан уметь жить с
    /// незнанием.
    /// </summary>
    public EntityBox? HitBox(string? code) => Get(code)?.Collision;

    /// <summary>
    /// КОРОБКА ВЫДЕЛЕНИЯ — ЕЮ ИГРА МЕРЯЕТ ВЫСОТУ КОНТАКТА, а не коробкой
    /// столкновений. В <c>AiTaskBaseTargetable.hasDirectContact</c> луч идёт
    /// в <c>targetEntity.SelectionBox.Y2 · 7/16</c> — по коробке ВЫДЕЛЕНИЯ и по
    /// росту ЦЕЛИ, а не стрелка.
    ///
    /// Своей у большинства тварей нет (в ассетах стоит только <c>hitboxSize</c>),
    /// и тогда игра берёт коробку столкновений — <c>Entity.updateColSelBoxes</c>:
    /// <c>SelectionBoxSize ?? CollisionBoxSize</c>. Здесь та же подстановка и
    /// ровно в том же порядке: заведи свою — разошлись бы молча.
    /// </summary>
    public EntityBox? SightBox(string? code) =>
        Get(code) is { } type ? type.Selection ?? type.Collision : null;

    /// <summary>
    /// Коробка из пакета: два целых числа со своим масштабом. Ноль и минус —
    /// «сервер не прислал» (для коробки выделения игра шлёт −1 ровно с этим
    /// смыслом, см. <c>EntityTypeNet</c>), и тогда ответ null, а не коробка
    /// нулевого размера.
    /// </summary>
    private static EntityBox? Box(int length, int height, double scale) =>
        length > 0 && height > 0 ? new EntityBox(length / scale, height / scale) : null;

    /// <summary>
    /// Масштаб коробки столкновений: <c>CollectibleNet.SerializePlayerPos</c> —
    /// <c>(int)(v · 10240 · 1000)</c>. Число не наше и не переписано «примерно».
    /// </summary>
    private const double CollisionScale = 10240.0 * 1000.0;

    /// <summary>
    /// Масштаб коробки выделения: <c>CollectibleNet.SerializeFloatPrecise</c> —
    /// <c>(int)(v · 1024)</c>. Масштабы у двух коробок РАЗНЫЕ, и перепутать их
    /// значит получить рост дрифтера в десять тысяч блоков.
    /// </summary>
    private const double SelectionScale = 1024.0;

    private static string Bare(string code) =>
        code.StartsWith("game:", StringComparison.Ordinal) ? code[5..] : code;

    private void HandlePacket(Packet_Server p)
    {
        if (p.Id != 19 || p.Assets is not { } assets || assets.Entities == null)
            return;
        try
        {
            var names = ReadTagNames(assets);
            var types = new Dictionary<string, EntityTypeInfo>(
                Math.Max(16, assets.EntitiesCount), StringComparer.Ordinal);
            int неразобранныхПоведений = 0;

            for (int i = 0; i < assets.EntitiesCount; i++)
            {
                var et = assets.Entities[i];
                if (et?.Code is not { Length: > 0 } code)
                    continue;
                types[Bare(code)] = new EntityTypeInfo(
                    Bare(code), et.Class ?? "", DecodeTags(et.Tags, names), et.Attributes,
                    Box(et.CollisionBoxLength, et.CollisionBoxHeight, CollisionScale),
                    Box(et.SelectionBoxLength, et.SelectionBoxHeight, SelectionScale),
                    ReadBehaviors(et, ref неразобранныхПоведений));
            }

            lock (gate)
            {
                byCode = types;
                tagNames = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
                Ready = true;
            }
            // ОДНО ИСПОРЧЕННОЕ ПОВЕДЕНИЕ НЕ ХОРОНИТ ВЕСЬ РЕЕСТР, но и молчать о
            // нём нельзя: узнавание врага смотрит на «commandable», и если его
            // кусок не разобрался, бот ударит чьего-то питомца, а человек будет
            // гадать почему
            if (неразобранныхПоведений > 0)
                OnParseError?.Invoke(
                    $"в реестре типов не разобрано поведений: {неразобранныхПоведений} " +
                    "(узнавание по поведениям на них слепо)");
            OnReady?.Invoke(types.Count, names.Length);
        }
        catch (Exception ex)
        {
            // Молчать нельзя: без реестра бой не отличит стрелу от твари, и
            // человек обязан прочитать причину, а не гадать по последствиям
            OnParseError?.Invoke($"реестр типов сущностей не разобран: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// ИМЕНА КЛИЕНТСКИХ ПОВЕДЕНИЙ ТИПА ИЗ ПАКЕТА.
    ///
    /// Каждое поведение приезжает куском JSON целиком (<c>Packet_Behavior
    /// .Attributes</c>) — тем самым, что записан в описании сущности, вида
    /// <c>{ code: "commandable" }</c>. Имя лежит в поле <c>code</c>, и берётся
    /// оно оттуда же, откуда его берёт сама игра при разборе пакета
    /// (<c>EntityTypeNet.FromPacket</c> → <c>EntityClientProperties</c> →
    /// <c>EntityBehavior</c> по коду).
    /// </summary>
    private static HashSet<string> ReadBehaviors(Packet_EntityType et, ref int неразобранных)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (et.Behaviors == null)
            return found;
        for (int i = 0; i < et.BehaviorsCount; i++)
        {
            if (et.Behaviors[i]?.Attributes is not { Length: > 0 } json)
                continue;
            try
            {
                // ИМЯ БЕРЁМ, ТОЛЬКО ЕСЛИ ОНО И ПРАВДА СТРОКА. Раньше здесь
                // стоял ToObject<string>(), и на поведении мода вида
                // { code: { … } } он кидал ArgumentException — а ловилка
                // ждала одну лишь JsonException. Исключение пролетало наружу,
                // во внешний catch разбора реестра, и Ready оставался ложью
                // НА ВСЮ СЕССИЮ: бот не считал врагом никого, не знал дичи и
                // коробок, а сон и лечение при этом были уверены, что рядом
                // безопасно. Щуп приёмки снял это дословно:
                //     «реестр типов сущностей не разобран:
                //      ArgumentException Can not convert Object to String»
                var поле = Newtonsoft.Json.Linq.JObject.Parse(json)["code"];
                if (поле is Newtonsoft.Json.Linq.JValue { Type: Newtonsoft.Json.Linq.JTokenType.String } имя &&
                    имя.Value as string is { Length: > 0 } code)
                    found.Add(code);
                // Поля «code» нет вовсе — это не поломка, а поведение без
                // имени: пропускаем молча, как пропускала прежняя ветка.
                // А вот «code» НЕ строкой — именно поломка, о ней говорим
                else if (поле != null)
                    неразобранных++;
            }
            // Ловим ВСЁ, и это не лень: обещание строкой выше — «одно
            // испорченное поведение не хоронит весь реестр». Любой мод вправе
            // положить в этот кусок что угодно, а перечислять сорта поломок
            // значит однажды снова пропустить незнакомую
            catch (Exception)
            {
                неразобранных++;
            }
        }
        return found;
    }

    private static string[] ReadTagNames(Packet_ServerAssets assets)
    {
        if (assets.TagRegistries == null)
            return [];
        for (int i = 0; i < assets.TagRegistriesCount; i++)
        {
            var reg = assets.TagRegistries[i];
            if (reg?.RegistryName != TagRegistryName || reg.TagNames == null)
                continue;
            var names = new string[reg.TagNamesCount];
            Array.Copy(reg.TagNames, names, reg.TagNamesCount);
            return names;
        }
        return [];
    }

    /// <summary>
    /// РАЗЛОЖИТЬ БИТОВУЮ МАСКУ ТЕГОВ ОБРАТНО В СЛОВА.
    ///
    /// Считается ровно так же, как считает игра
    /// (<c>ConcurrentTagRegistryFast.SlowEnumerateTagNames</c>): имя с номером
    /// i сидит в бите i % 64 куска i / 64. Куски в пакете лежат по порядку —
    /// Part1 это storage[0].
    /// </summary>
    internal static HashSet<string> DecodeTags(Packet_TagSetFast? tags, string[] names)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (tags == null || names.Length == 0)
            return found;
        ulong[] parts = [(ulong)tags.Part1, (ulong)tags.Part2, (ulong)tags.Part3, (ulong)tags.Part4];
        for (int i = 0; i < names.Length && i < parts.Length * 64; i++)
            if (names[i] is { Length: > 0 } name && ((parts[i / 64] >> (i % 64)) & 1) == 1)
                found.Add(name);
        return found;
    }
}
