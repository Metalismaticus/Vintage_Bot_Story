// Игровые типы берём ТОЧЕЧНО. Тянуть весь Vintagestory.API.Common нельзя:
// там свой Func<>, и он конфликтует с системным (та же оговорка в Hands.cs).
using ILogger = Vintagestory.API.Common.ILogger;
using LoggerBase = Vintagestory.API.Common.LoggerBase;
using EnumLogType = Vintagestory.API.Common.EnumLogType;

using Vintagestory.API.Config;

namespace VsBotKit;

// ======================================================================
//  ЧЕЛОВЕЧЕСКИЕ ИМЕНА ПРЕДМЕТОВ
// ======================================================================

/// <summary>
/// Как предмет называется у ЧЕЛОВЕКА: «Кремень», а не «flint».
///
/// ОТКУДА БЕРЁМ. Имена не приходят с сервера вовсе: сервер шлёт реестры кодов
/// (пакет 19), а перевод кода в слова — дело клиента, и лежит оно в файлах
/// установленной игры: assets/game/lang/&lt;язык&gt;.json. Поэтому имя берётся
/// у самой игры и её же способом — <c>Lang.PreLoad</c> + <c>Lang.GetMatching</c>
/// из VintagestoryAPI. Своего разбора этих файлов здесь НЕТ намеренно: там
/// живут ключи со звёздочками (<c>block-ore-*-nativecopper-*</c> → «Руда
/// самородной меди»), запасной английский язык и порядок совпадений. Написать
/// «почти такой же» разбор — значит однажды показать человеку не то имя,
/// которое он видит в своей игре.
///
/// ЧЕГО ЗДЕСЬ НЕТ. Списка кодов и списка имён (правило 5). Если игра не
/// установлена или язык не читается, имён просто нет — и об этом говорится
/// вслух в <see cref="Problem"/>, а показывается голый код. Выдумывать имя
/// («Флинт») нельзя: код — правда, выдумка — нет.
///
/// ПОЧЕМУ СТАТИКА. <c>Lang</c> в игре один на весь процесс: у него один
/// текущий язык и один словарь. Заводить два «языковых хозяйства» в одном боте
/// невозможно физически, поэтому обёртка тоже одна.
/// </summary>
public static class GameNames
{
    private static readonly object gate = new();
    private static bool loaded;

    /// <summary>Имя файла настроек клиента — в нём выбранный человеком язык.</summary>
    public const string ClientSettingsFile = "clientsettings.json";

    /// <summary>Язык, на котором в конце концов удалось прочитать имена.</summary>
    public const string FallbackLanguage = "en";

    /// <summary>Имена прочитаны и ими можно пользоваться.</summary>
    public static bool Ready { get; private set; }

    /// <summary>Какой язык загружен ("ru", "en"...). Пусто — ни одного.</summary>
    public static string Language { get; private set; } = "";

    /// <summary>Почему имён нет. Пусто — всё в порядке (или ещё не пробовали).</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>Молчаливый журнал для игры: её жалобы нам ни к чему, свои честнее.</summary>
    private sealed class SilentLogger : LoggerBase
    {
        protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
    }

    /// <summary>
    /// На каком языке человек играет САМ. Спрашиваем его настройки клиента:
    /// имена в панели должны совпадать с теми, что он видит в игре, — иначе
    /// «Слиток меди» в окне и «Copper ingot» в игре читаются как разные вещи.
    /// </summary>
    public static string? OwnerLanguage()
    {
        try
        {
            string path = Path.Combine(GameFolders.DataFolder(), ClientSettingsFile);
            if (!File.Exists(path))
                return null;
            var settings = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            return settings.GetValue("language", StringComparison.OrdinalIgnoreCase)
                ?.ToObject<string>() is { Length: > 0 } lang ? lang : null;
        }
        catch
        {
            // Настройки клиента — чужой файл: его могли переписать, испортить
            // или запретить читать. Это не повод падать, это повод взять запасной
            return null;
        }
    }

    /// <summary>
    /// Прочитать имена. Зовётся сколько угодно раз: повторный вызов с тем же
    /// языком ничего не делает.
    ///
    /// Возвращает, получилось ли. Причина отказа — в <see cref="Problem"/>.
    /// </summary>
    /// <param name="language">Язык или null — тогда язык игры хозяина.</param>
    public static bool Load(string? language = null)
    {
        lock (gate)
        {
            string want = language is { Length: > 0 } ? language : OwnerLanguage() ?? FallbackLanguage;
            if (loaded && Ready && string.Equals(Language, want, StringComparison.OrdinalIgnoreCase))
                return true;

            loaded = true;
            Ready = false;
            Language = "";
            Problem = "";

            if (GameFolders.FindGame() is not { } game)
            {
                Problem = "игра не найдена на этой машине — имена предметов брать неоткуда, " +
                          $"показываю коды (см. переменную {GameFolders.EnvVar})";
                return false;
            }

            string assets = Path.Combine(game, "assets");
            if (!Directory.Exists(assets))
            {
                Problem = $"в папке игры нет {assets} — имена предметов брать неоткуда, показываю коды";
                return false;
            }

            // Игра ищет список языков по СВОЕЙ настройке GamePaths.AssetsPath,
            // а не по тому пути, что ей передали: без этой строки PreLoad лезет
            // за <папка бота>\assets\game\lang\languages.json и падает. Открытого
            // способа поправить настройку у игры нет (сеттер закрыт), поэтому
            // берём его отражением — тем же приёмом, что и Lighting для чужих
            // внутренностей. Не вышло — честно говорим, а не молчим
            if (AssetsPathSetter is not { } setPath)
            {
                Problem = "у игры больше нет настройки GamePaths.AssetsPath — " +
                          "имена предметов читать нечем, показываю коды";
                return false;
            }

            try
            {
                setPath.Invoke(null, [assets]);
                // Языка, которого у игры нет, PreLoad не пугается: он молча
                // берёт английский. Поэтому «какой язык вышел» спрашиваем
                // у самой игры, а не считаем, что получили заказанный
                Lang.PreLoad(new SilentLogger(), assets, want);
                Ready = true;
                Language = Lang.CurrentLocale ?? "";
                Problem = string.Equals(Language, want, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $"языка «{want}» у игры нет — имена показываю на «{Language}»";
                return true;
            }
            catch (Exception e)
            {
                Problem = $"имена предметов не читаются ({e.Message}) — показываю коды";
                return false;
            }
        }
    }

    /// <summary>
    /// Закрытый сеттер GamePaths.AssetsPath. Ищется один раз: пропадёт он
    /// в новой версии игры — узнаем об этом словами, а не падением.
    /// </summary>
    private static readonly System.Reflection.MethodInfo? AssetsPathSetter =
        typeof(GamePaths)
            .GetProperty("AssetsPath",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetSetMethod(nonPublic: true);

    /// <summary>
    /// Ключ перевода для кода предмета — ровно тот, что в файлах игры.
    ///
    /// Правила игры: у предметов ключ «item-&lt;код&gt;», у блоков
    /// «block-&lt;код&gt;»; домен «game» в ключ не пишется (его подставляет сам
    /// <c>Lang</c>), а чужой домен пишется впереди — «mymod:item-thing».
    /// Чистая функция: формат чужой, ломается молча, поэтому закрыта тестом.
    /// </summary>
    public static string KeyFor(string code, bool block)
    {
        string c = code.Trim().ToLowerInvariant();
        string domain = "", path = c;
        int colon = c.IndexOf(':');
        if (colon >= 0)
        {
            domain = c[..colon];
            path = c[(colon + 1)..];
        }
        string kind = block ? "block" : "item";
        return domain.Length == 0 || domain == "game"
            ? $"{kind}-{path}"
            : $"{domain}:{kind}-{path}";
    }

    /// <summary>
    /// Имя предмета по-человечески или null, если игра его не знает (модовый
    /// предмет без перевода, чужой сервер). Null — это честное «не знаю»,
    /// а не пустая строка: пустая строка в окне читается как «без названия».
    /// </summary>
    /// <param name="block">
    /// Это блок, а не предмет. Знать точно важно: «bread-spelt-perfect» в игре
    /// БЛОК, и по ключу предмета имени у него нет. Если ошиблись — пробуем
    /// второй вид, лишь бы человек увидел слова.
    /// </param>
    public static string? Name(string? code, bool block = false)
    {
        if (code is not { Length: > 0 })
            return null;
        if (!Ready && !Load())
            return null;
        return Lookup(KeyFor(code, block)) ?? Lookup(KeyFor(code, !block));
    }

    private static string? Lookup(string key)
    {
        try
        {
            // GetMatchingIfExists честно отдаёт null, когда перевода нет,
            // — в отличие от Get, который возвращает сам ключ и выдал бы
            // «item-flint» за имя предмета
            return Lang.GetMatchingIfExists(key) is { Length: > 0 } name ? name : null;
        }
        catch
        {
            // В переводе бывают фигурные скобки под подстановку; форматирование
            // без аргументов на них падает. Имя не стоит упавшего окна
            return null;
        }
    }

    /// <summary>Имя, а если игра его не знает — сам код. Для показа человеку.</summary>
    public static string Describe(string? code, bool block = false) =>
        Name(code, block) ?? code ?? "";

    /// <summary>Забыть загруженное (нужно тестам и смене языка на ходу).</summary>
    public static void Forget()
    {
        lock (gate)
        {
            loaded = false;
            Ready = false;
            Language = "";
            Problem = "";
        }
    }
}

// ======================================================================
//  КАРТИНКИ ПРЕДМЕТОВ
// ======================================================================

/// <summary>
/// Сколько улик про картинки предметов удалось собрать у установленной игры.
/// </summary>
/// <param name="GameFolder">Где смотрели (null — игра не найдена).</param>
/// <param name="ItemTypes">Сколько описаний предметов у игры.</param>
/// <param name="Modelled">Из них с объёмной моделью (полем shape).</param>
public sealed record IconEvidence(string? GameFolder, int ItemTypes, int Modelled);

/// <summary>Папка с выгруженными картинками — та, что нашлась рядом с ботом.</summary>
/// <param name="Path">Где лежит (null — папки нет ни в одном обычном месте).</param>
/// <param name="Files">Сколько png в ней нашлось, считая подпапки.</param>
/// <param name="MadeByInv">
/// ПАПКУ, ПОХОЖЕ, ВЫГРУЖАЛИ «inv», А НАДО «all» — и это ФАКТ ПО ЭТОЙ ПАПКЕ, а
/// не догадка вообще (см. <see cref="ItemIcons.MadeByInv"/>).
///
/// Зачем поле. Совет «перевыгрузите с all» верен ровно до тех пор, пока человек
/// его не выполнил. Сказанный после — он врёт про его машину и посылает делать
/// заново уже сделанное. Живой случай 16.08: заказчик по этому самому совету
/// перевыгрузил папку (было 8582 файла, стало 18560, фонарь на месте) — и
/// строка внизу вкладки по-прежнему звала бы его выгружать заново.
/// </param>
public sealed record IconFolder(string? Path, int Files, bool MadeByInv = false);

/// <summary>
/// КАРТИНКА ОДНОЙ ВЕЩИ — ответ на вопрос «что мне про неё показать».
///
/// Ровно три поля, и все три нужны окну: без <see cref="Kind"/> оно спросит
/// картинку не из той папки (один код бывает и блоком, и предметом), без
/// <see cref="Has"/> — нарисует битый квадратик у каждой невыгруженной вещи,
/// без <see cref="Code"/> — не сможет спросить вовсе. Имена полей нарочно те
/// же, что уже отдаёт вкладка «Инвентарь» (код, класс, картинка).
/// </summary>
/// <param name="Code">Код вещи — то, чем её зовёт сервер.</param>
/// <param name="Kind">
/// «block» или «item» — по реестру сервера, а не по виду кода. Это же слово
/// идёт в запрос картинки (<c>kind</c>) и это же имя подпапки у игры.
/// </param>
/// <param name="Has">Файл и правда лежит: рисовать. Ложь — писать имя словом.</param>
public sealed record ItemIcon(string Code, string Kind, bool Has)
{
    /// <summary>Это блок (а не предмет) — тем же словом, что у игры.</summary>
    public bool Block => Kind == ItemIcons.BlockFolder;

    /// <summary>
    /// Почему картинки нет — словами, по фактам и ТОЛЬКО когда спросят
    /// (свойство считается на месте, а не заранее): в описи склада вещей бывают
    /// сотни, и строка на каждую — это лишняя работа на каждый опрос окна.
    /// Пусто — картинка есть.
    /// </summary>
    public string Why => Has ? "" : ItemIcons.WhyNoPicture(Code, Block);
}

/// <summary>
/// КАРТИНКИ ПРЕДМЕТОВ: бот их НЕ РИСУЕТ, но ПОКАЗЫВАЕТ — если человек их
/// один раз выгрузил из игры и положил рядом.
///
/// ЧЕГО БОТ НЕ УМЕЕТ И НЕ БУДЕТ. Готовых картинок предметов в assets игры не
/// лежит ни одной: предмет — ОБЪЁМНАЯ МОДЕЛЬ, и то, что игрок видит в ячейке,
/// рисует видеокарта (модель из json, текстура из атласа, поворот и свет окна
/// инвентаря). Бот безголовый, видеокарты у него нет — нарисовать он не может
/// физически. Улики про это считает <see cref="Inspect"/>: пропадут однажды
/// объёмные модели — числа изменятся, и разговор начнётся заново, а не с
/// чьей-то памяти.
///
/// ТЕКСТУРУ ВМЕСТО ИКОНКИ НЕ ПОДСОВЫВАЕМ. Живой случай: у медного топора
/// (axe-felling-copper) единственная текстура — block/metal/ingot/copper.png,
/// ровный оранжевый квадрат 16×16. Топором там и не пахнет: топор получается,
/// когда этой текстурой обтягивают модель item/tool/axe/copper. Отдай мы этот
/// квадрат «иконкой топора» — человек увидел бы полный рюкзак оранжевых
/// квадратиков и решил бы, что у бота одна медь. У кремня то же самое: его
/// «текстура» — плитка камня.
///
/// ЧТО БОТ УМЕЕТ. Рисует их ГРАФИЧЕСКИЙ КЛИЕНТ игры, и у него для этого есть
/// свои команды (сверено по VintagestoryLib.dll 1.22.6,
/// Vintagestory.Client.SystemClientCommands):
///
///   .blockitempngexport [inv|all] [размер=100] [домен]  — ВСЁ РАЗОМ;
///   .exponepng code &lt;block|item&gt; &lt;код&gt; [размер=100]     — одну вещь;
///   .exponepng hand [размер=100]                        — то, что в руке.
///
/// Кладёт клиент их так (это и есть наши имена файлов):
///   блоки  → icons/block/&lt;код&gt;.png       (код как есть, со всеми «/»)
///   вещи   → icons/item/&lt;код&gt;.png        (в общей выгрузке «/» заменён на «-»)
///
/// Значит, боту остаётся ровно одно честное дело: ВЗЯТЬ ГОТОВЫЙ ФАЙЛ, если он
/// есть, и промолчать словом-именем, если его нет. Ничего не додумывая: имя
/// файла собирается по правилам самой игры (<see cref="FileNames"/>), а не по
/// нашей догадке.
///
/// ПОЧЕМУ НЕ ИЩЕМ В ЧУЖОЙ ПАПКЕ. Один и тот же код бывает И БЛОКОМ, И
/// ПРЕДМЕТОМ — это разные вещи с разными картинками. Проверено по assets
/// 1.22.6: среди корневых кодов таких восемь — candle, cheese, egg, leather,
/// ore, paper, saltpeter, clutter, — а с их разновидностями пар больше.
/// Возьми мы картинку блока для предмета «за неимением лучшего», человек
/// увидел бы в рюкзаке свечу-подсвечник вместо свечи-заготовки и не понял бы,
/// почему бот её «не может поставить». Поэтому блок ищется только в block/,
/// предмет только в item/, и точка.
/// </summary>
public static class ItemIcons
{
    private static readonly object gate = new();
    private static IconEvidence? seen;

    /// <summary>Папка описаний предметов внутри домена ассетов.</summary>
    public const string ItemTypesFolder = "itemtypes";

    /// <summary>«КАРТИНКИ ЛЕЖАТ ЗДЕСЬ» — переменная окружения, её слушаем первой.</summary>
    public const string FolderEnvVar = "VSBOT_ICONS";

    /// <summary>Имя папки с картинками — ровно то, что делает сама игра.</summary>
    public const string FolderName = "icons";

    /// <summary>Подпапка блоков — так её называет игра при выгрузке.</summary>
    public const string BlockFolder = "block";

    /// <summary>Подпапка предметов — так её называет игра при выгрузке.</summary>
    public const string ItemFolder = "item";

    /// <summary>
    /// Как часто перечитывать папку. Человек кладёт картинки при ЖИВОМ боте
    /// («выгрузил в игре — переложил — смотрю в окно»), и требовать перезапуска
    /// ради этого незачем; читать же папку на каждую клетку каждого опроса —
    /// сотни обращений к диску в секунду впустую.
    /// </summary>
    public static readonly TimeSpan RescanAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// ЧЕРЕЗ СТОЛЬКО ОБХОДИМ ПАПКУ ЦЕЛИКОМ, ЧТО БЫ НИ ГОВОРИЛА ОТМЕТКА.
    ///
    /// ЗАЧЕМ, ЕСЛИ ЕСТЬ ОТМЕТКА КАТАЛОГОВ. Затем, что отметка иногда МОЛЧИТ, и
    /// это не наша ошибка, а поведение самой Windows: время каталога лежит ещё
    /// и в записи РОДИТЕЛЯ, а <c>GetLastWriteTimeUtc</c> читает именно её —
    /// NTFS же обновляет эту копию лениво. Поймано стендом: под нагрузкой
    /// (полный прогон тестов) файл в папку положен, а время каталога не
    /// сдвинулось ни через секунду, ни через минуту фальшивых часов; тот же
    /// тест в одиночку проходит, потому что система успевает сбросить кэш.
    ///
    /// Молча полагаться на отметку значило бы обещание из ЗАПУСК.md («положил
    /// картинку — бот подхватит») со скрытым «иногда». Поэтому отметка осталась
    /// БЫСТРОЙ ДОРОГОЙ, а правда — полный обход, и он всё равно случается.
    /// Цена честности на папке заказчика: 23 мс и 9,5 МБ раз в пять минут
    /// вместо каждых двадцати секунд — экономия та же пятнадцатикратная.
    /// </summary>
    public static readonly TimeSpan RescanAnywayAfter = TimeSpan.FromMinutes(5);

    /// <summary>
    /// ЧАСЫ — подменяются в тестах, как у памяти встреч и памяти транслокаторов
    /// (<see cref="TlMemory.Now"/>). Иначе обещание «положил картинку при живом
    /// боте — через двадцать секунд она в окне» проверить нечем: ждать в тесте
    /// настоящие двадцать секунд нельзя, а без проверки временем ровно здесь
    /// уже пряталась поломка (см. <see cref="LookedAtFolder"/>).
    /// </summary>
    public static Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>
    /// Пересчитать описания предметов у установленной игры. Считается один
    /// раз за запуск: файлы игры при живом боте не меняются, а обход трёх сотен
    /// json на каждый запрос окна — пустая работа.
    /// </summary>
    public static IconEvidence Inspect()
    {
        lock (gate)
            return seen ??= Count();
    }

    /// <summary>Забыть подсчёт и папку с картинками (нужно тестам).</summary>
    public static void Forget()
    {
        lock (gate)
        {
            seen = null;
            folder = null;
            index = null;
            scanned = default;
            walked = default;
            stamp = "";
        }
    }

    // ---------------- готовые картинки рядом с ботом ----------------

    private static string? folder;
    private static HashSet<string>? index;
    private static DateTime scanned;

    /// <summary>Когда папку обходили ЦЕЛИКОМ в последний раз (см. <see cref="RescanAnywayAfter"/>).</summary>
    private static DateTime walked;

    private static string stamp = "";

    /// <summary>
    /// Где искать папку с картинками — по порядку, тем же правилом, что у
    /// игры и у настроек бота: сперва то, что сказал человек, потом обычные
    /// места. Папка состояния идёт раньше папки рядом с программой: в
    /// контейнере рядом лежит слой образа, который пересборка стирает (см.
    /// <see cref="BotFolders.StateEnvVar"/>), и картинки пропали бы молча.
    ///
    /// ПОСЛЕДНЕЙ СМОТРИМ ТУДА, КУДА ИХ КЛАДЁТ САМА ИГРА. Прямой вопрос заказчика
    /// 16.08: «расположение для папок иконок странное, так точно нужно?» — и он
    /// прав, что это выглядит странно. Обязательного в этом ничего нет: бот
    /// нашёл картинки в кандидате «рядом с программой», а у него это
    /// <c>VintageBotStory\bin\Debug\net10.0\icons</c> — то есть внутри каталога сборки,
    /// который пересборка стирает вместе с картинками. Ровно это 18.08 и
    /// случилось: 18 560 картинок пропали разом. Поэтому у запущенного из
    /// сборки первым кандидатом теперь стоит устойчивое место рядом с проектом
    /// (<see cref="BotFolders.Lasting"/>).
    ///
    /// Класть их туда человека заставляла не механика, а рецепт: игра
    /// выгружает картинки в <c>icons</c> рядом с собой (клиент зовёт
    /// <c>Save("icons/block/…")</c> относительным путём, см.
    /// <c>SystemClientCommands.OnRenderBlockItemPngs</c>), а ЗАПУСК.md велел
    /// скопировать эту папку к боту. Копия и есть лишний шаг, на котором всё
    /// разъезжается: перевыгрузил в игре — а бот показывает старое.
    ///
    /// Поэтому игровая папка добавлена кандидатом, и именно ПОСЛЕДНИМ: своя
    /// папка рядом с ботом старше — в неё человек кладёт то, что выбрал сам
    /// (например, только нужные вещи или картинки с чужой машины).
    /// </summary>
    public static IEnumerable<string> FolderCandidates() =>
        FolderCandidates(
            Environment.GetEnvironmentVariable(FolderEnvVar),
            BotFolders.Lasting,
            BotFolders.State(),
            BotFolders.Beside,
            GameFolders.FindGame());

    /// <summary>
    /// ТО ЖЕ ПРАВИЛО, НО ЧИСТОЕ: ни диска, ни переменных окружения. Папки у
    /// каждой машины свои, живьём порядок не проверишь — а ошибиться в нём
    /// стоит ровно того, что уже случилось (см. ниже), поэтому правило закрыто
    /// стендом.
    ///
    /// УСТОЙЧИВОЕ МЕСТО ИДЁТ ПЕРВЫМ (после сказанного человеком). Живая потеря
    /// 18.08: 18 560 картинок лежали в <c>VintageBotStory\bin\Debug\net10.0\icons</c>,
    /// и пересборка стёрла их вместе с папкой сборки — вернуть было нечего.
    /// Слова заказчика: «иконки нужно все же в проекте хранить». Первым
    /// кандидатом теперь стоит папка РЯДОМ С ПРОЕКТОМ
    /// (<see cref="BotFolders.OutsideBuild"/>), и туда же окно управления зовёт
    /// человека класть выгрузку.
    ///
    /// СТАРЫЕ МЕСТА ОСТАЛИСЬ И ЧИТАЮТСЯ. У кого картинки уже лежат в папке
    /// состояния, рядом с программой или в папке игры — у того ничего не
    /// сломалось: список только удлинился сверху. Пустая папка дорогу следующей
    /// не закрывает (см. <see cref="Pick"/>), поэтому новое место, в которое
    /// ещё ничего не положили, старое не прячет.
    ///
    /// ОДИНАКОВЫЕ ПАПКИ НАЗЫВАЕМ ОДИН РАЗ. При обычной установке «состояние» и
    /// «рядом с программой» — одна и та же папка, и человек читал в окне один и
    /// тот же путь дважды подряд, будто бот смотрит в два разных места.
    /// </summary>
    /// <param name="told">Что сказано переменной <see cref="FolderEnvVar"/>; путь ДО папки картинок.</param>
    /// <param name="lasting">Устойчивое место (<see cref="BotFolders.Lasting"/>); null — запущено не из сборки.</param>
    /// <param name="state">Папка состояния бота (<see cref="BotFolders.State"/>).</param>
    /// <param name="beside">Папка рядом с программой (<see cref="BotFolders.Beside"/>).</param>
    /// <param name="game">Папка установленной игры; null — игры на машине нет.</param>
    public static IEnumerable<string> FolderCandidates(
        string? told, string? lasting, string state, string beside, string? game)
    {
        var сказанные = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> Все()
        {
            if (told is { Length: > 0 })
                yield return told;
            if (lasting is { Length: > 0 })
                yield return Path.Combine(lasting, FolderName);
            if (state is { Length: > 0 })
                yield return Path.Combine(state, FolderName);
            if (beside is { Length: > 0 })
                yield return Path.Combine(beside, FolderName);
            if (game is { Length: > 0 })
                yield return Path.Combine(game, FolderName);
        }

        foreach (string куда in Все())
            if (сказанные.Add(Path.TrimEndingDirectorySeparator(куда)))
                yield return куда;
    }

    /// <summary>
    /// ИМЕНА ФАЙЛОВ, под которыми игра сохраняет картинку этого кода, — по
    /// порядку, от самого вероятного. Пустой список — этому коду картинки не
    /// бывает (пустой код, чужие буквы, попытка уехать по «..»).
    ///
    /// Правила НЕ НАШИ, а игровые (SystemClientCommands.OnRenderBlockItemPngs):
    ///   • домен в имя файла не идёт — игра берёт только Code.Path;
    ///   • блок кладётся в block/, предмет в item/ — и никогда наоборот;
    ///   • в ОБЩЕЙ выгрузке у предметов «/» заменён на «-», у блоков нет.
    /// Поэтому у предмета два имени, у блока одно. Плоское «icons/&lt;код&gt;.png»
    /// добавлено последним: так картинки складывает человек руками, когда
    /// выгружает по одной.
    ///
    /// ЧУЖИЕ БУКВЫ ОТСЕКАЕМ ЗДЕСЬ. Код приходит из реестра сервера, а на сервере
    /// бывают моды и бывают шутники: «../../../etc/passwd» в коде предмета
    /// превратился бы в чтение чужого файла через окно управления. Разрешены
    /// только те буквы, из которых игра и составляет коды.
    ///
    /// Чистая функция — потому и закрыта тестом: ошибись здесь на один дефис,
    /// и человек увидит пустые клетки при полной папке картинок.
    /// </summary>
    public static IReadOnlyList<string> FileNames(string? code, bool block)
    {
        if (code is not { Length: > 0 })
            return [];

        string path = code.Trim().ToLowerInvariant();
        // Домен («game:», «mymod:») в имя файла не входит: игра берёт Code.Path
        int colon = path.IndexOf(':');
        if (colon >= 0)
            path = path[(colon + 1)..];
        path = path.Trim('/');
        if (path.Length == 0)
            return [];

        foreach (char c in path)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/'))
                return [];
        // Пустых кусков быть не может (значит, нет и «..»), но проверяем прямо:
        // защита должна читаться, а не выводиться из соседней строки
        foreach (string part in path.Split('/'))
            if (part.Length == 0 || part == "." || part == "..")
                return [];

        string kind = block ? BlockFolder : ItemFolder;
        string dashed = path.Replace('/', '-');

        var names = new List<string>(3);
        void Add(string name)
        {
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }

        Add($"{kind}/{path}.png");
        Add($"{kind}/{dashed}.png");
        Add($"{path}.png");
        return names;
    }

    /// <summary>Столько картинок отвечает <see cref="Pick"/>, когда папки нет или её не прочесть.</summary>
    public const int NoFolder = -1;

    /// <summary>
    /// КАКУЮ ИЗ ПАПОК-КАНДИДАТОВ ВЗЯТЬ. Чистое правило, поэтому и закрыто
    /// тестом: живьём его не проверишь — папки-кандидаты у каждой машины свои.
    ///
    /// ПУСТАЯ ПАПКА НЕ ЗАКРЫВАЕТ ДОРОГУ СЛЕДУЮЩЕЙ. Раньше побеждал первый
    /// кандидат, который ПРОСТО СУЩЕСТВУЕТ, — и однажды созданная пустая
    /// «icons» рядом с ботом навсегда прятала настоящую выгрузку, лежащую
    /// дальше по списку (например, в папке игры). Со стороны это выглядит как
    /// «бот не видит картинок», хотя они есть, и найти причину нечем.
    ///
    /// Пустую всё же запоминаем и отдаём, если картинок нет нигде: без этого
    /// пропала бы разница между «папки нет вовсе» и «папка есть, а png в ней
    /// нет», а это РАЗНЫЕ ответы человеку (см. <see cref="Say"/>).
    ///
    /// Считать картинки спрашиваем СНАРУЖИ и ЛЕНИВО: на первой же непустой
    /// папке перебор кончается, и лишних обходов диска не бывает.
    /// </summary>
    /// <param name="pngCount">
    /// Сколько png в этой папке; <see cref="NoFolder"/> — папки нет или её
    /// не прочесть (сетевой диск, права, унесли во время обхода).
    /// </param>
    public static string? Pick(IEnumerable<string> candidates, Func<string, int> pngCount)
    {
        string? пустая = null;
        foreach (string dir in candidates)
        {
            if (dir is not { Length: > 0 })
                continue;
            int сколько = pngCount(dir);
            if (сколько > 0)
                return dir;
            if (сколько == 0)
                пустая ??= dir;
        }
        return пустая;
    }

    /// <summary>
    /// ОТМЕТКА ПАПКИ: времена изменения всех её каталогов, сложенные в строку.
    /// Пусто — папки нет или её не прочесть.
    ///
    /// ЗАЧЕМ ЭТО, А НЕ ПРОСТО ОБХОД. Ровно то, ради чего отметка нужна, она и
    /// ловит: имя файла в описи меняется, только когда файл ДОБАВИЛИ, УДАЛИЛИ
    /// или ПЕРЕИМЕНОВАЛИ, — а всё это трогает время каталога, в котором он
    /// лежит. Проверено на этой самой машине: добавление, удаление и
    /// переименование файла в <c>icons\block</c> двигали время этого каталога
    /// (время КОРНЯ при этом не двигалось — поэтому берём все каталоги дерева,
    /// а не один). Содержимое картинки к описи имён отношения не имеет, и
    /// незамеченная правка внутри png нам не беда.
    /// </summary>
    public static string Stamp(IEnumerable<(string Dir, DateTime Written)> dirs)
    {
        var части = new List<string>();
        foreach (var (dir, written) in dirs)
            части.Add($"{dir}|{written.Ticks}");
        части.Sort(StringComparer.Ordinal);   // порядок обхода диска не обещан
        return string.Join(";", части);
    }

    /// <summary>
    /// НАДО ЛИ ОБХОДИТЬ ПАПКУ ЗАНОВО. Чистое правило — потому и закрыто
    /// тестом: живьём его не проверишь, чужих папок на стенде нет.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ОНО ЕСТЬ. У заказчика в папке 18 560
    /// картинок, и окно управления спрашивает про них на каждый опрос. Обход
    /// такой папки замерен на его же выгрузке: 23 мс и 9,5 МБ мусора КАЖДЫЕ
    /// 20 секунд (<see cref="RescanAfter"/>) — за час открытого окна это
    /// 1,7 ГБ выделений и полминуты работы с диском на ровном месте. В памяти
    /// при этом не оседает ничего (опись как весила 2,5 МБ, так и весит), но
    /// платить за неё столько незачем: отметка каталогов отвечает на тот же
    /// вопрос тремя обращениями к диску вместо восемнадцати тысяч.
    ///
    /// СРОК ОСТАЁТСЯ ГЛАВНЫМ. Пока он не вышел, не смотрим вообще ничего:
    /// человек кладёт картинки при живом боте, но не по десять раз в секунду.
    /// </summary>
    /// <param name="haveIndex">Опись уже есть. Нет — обход нужен всегда.</param>
    /// <param name="since">Сколько прошло с прошлого взгляда.</param>
    /// <param name="rescanAfter">Через сколько вообще смотреть (<see cref="RescanAfter"/>).</param>
    /// <param name="known">Отметка папки, снятая при прошлом обходе.</param>
    /// <param name="now">Отметка папки сейчас. Пусто — папки нет: искать заново.</param>
    public static bool NeedFullScan(bool haveIndex, TimeSpan since, TimeSpan rescanAfter,
        string known, string now)
    {
        if (!haveIndex)
            return true;
        if (since < rescanAfter)
            return false;
        // Отметки нет ни прежней, ни нынешней — судить не по чему: папку могли
        // унести, могли положить на сетевой диск. Тогда честнее обойти
        return now.Length == 0 || known.Length == 0 || known != now;
    }

    /// <summary>
    /// СМОТРЕЛИ ЛИ МЫ НА ПАПКУ ВООБЩЕ — и, значит, можно ли двигать часы
    /// «прошло с прошлого взгляда».
    ///
    /// ЖИВАЯ БЕДА, РАДИ КОТОРОЙ ПРАВИЛО ВЫДЕЛЕНО ОТДЕЛЬНО. Часы двигались на
    /// КАЖДЫЙ вопрос об иконке — в том числе на тот, где срок ещё не вышел и
    /// папку никто не трогал. А окно управления спрашивает картинки по
    /// нескольку раз в секунду, пока открыто: «прошло с прошлого взгляда»
    /// никогда не дорастало до двадцати секунд, полного обхода не случалось
    /// НИКОГДА, и картинка, положенная при живом боте, не появлялась, пока
    /// окно не закроют. То есть обещание <see cref="RescanAfter"/> ломалось
    /// ровно тогда, когда оно единственно и нужно.
    ///
    /// Часы двигает только настоящий взгляд: снятая отметка каталогов (или
    /// полный обход, когда описи ещё нет).
    /// </summary>
    /// <param name="haveIndex">Опись уже есть.</param>
    /// <param name="since">Сколько прошло с прошлого ВЗГЛЯДА.</param>
    /// <param name="rescanAfter">Через сколько вообще смотреть.</param>
    public static bool LookedAtFolder(bool haveIndex, TimeSpan since, TimeSpan rescanAfter) =>
        !haveIndex || since >= rescanAfter;

    /// <summary>
    /// ПОРА ОБОЙТИ ПАПКУ ЦЕЛИКОМ, ЧТО БЫ НИ ГОВОРИЛА ОТМЕТКА — см.
    /// <see cref="RescanAnywayAfter"/>: отметка каталогов на Windows иногда
    /// молчит о положенном файле, и без этого потолка картинка не появлялась бы
    /// вовсе, а бот при этом «честно» отвечал бы, что папка не менялась.
    /// </summary>
    /// <param name="sinceWalk">Сколько прошло с последнего ПОЛНОГО обхода.</param>
    /// <param name="walkAnywayAfter">Потолок (<see cref="RescanAnywayAfter"/>).</param>
    public static bool WalkAnyway(TimeSpan sinceWalk, TimeSpan walkAnywayAfter) =>
        walkAnywayAfter > TimeSpan.Zero && sinceWalk >= walkAnywayAfter;

    /// <summary>
    /// Времена каталогов дерева — корень и все подпапки. Пустой список, если
    /// папки нет или её не прочесть: тогда решение принимает
    /// <see cref="NeedFullScan"/>, а не молчаливое «ничего не менялось».
    /// </summary>
    private static IEnumerable<(string, DateTime)> Каталоги(string? dir)
    {
        if (dir is not { Length: > 0 })
            yield break;
        string[] все;
        try
        {
            if (!Directory.Exists(dir))
                yield break;
            все = Directory.GetDirectories(dir, "*", SearchOption.AllDirectories);
        }
        catch
        {
            // Права, сетевой диск, папку унесли прямо сейчас — это «отметки
            // нет», и обход будет полным
            yield break;
        }
        yield return (dir, Directory.GetLastWriteTimeUtc(dir));
        foreach (string под in все)
            yield return (под, Directory.GetLastWriteTimeUtc(под));
    }

    /// <summary>
    /// Перечитать папку, если пора. Держим СПИСОК имён, а не спрашиваем диск
    /// про каждую клетку: в окне до сотни клеток и опрос каждые несколько
    /// секунд — это тысячи обращений к диску в минуту вместо одного обхода.
    ///
    /// А ПОЛНЫЙ ОБХОД ДЕЛАЕМ, ТОЛЬКО ЕСЛИ ПАПКА МЕНЯЛАСЬ (см.
    /// <see cref="NeedFullScan"/>): при 18 560 картинках он стоит 23 мс и
    /// 9,5 МБ мусора, и повторять его каждые 20 секунд впустую незачем.
    /// </summary>
    private static void Fresh()
    {
        lock (gate)
        {
            var прошло = Now() - scanned;
            bool посмотрели = LookedAtFolder(index != null, прошло, RescanAfter);
            string сейчас = index != null && посмотрели ? Stamp(Каталоги(folder)) : "";
            // ОТМЕТКА — БЫСТРАЯ ДОРОГА, А НЕ ПРАВДА (см. RescanAnywayAfter):
            // Windows отдаёт время каталога из записи родителя, а NTFS обновляет
            // её лениво, и положенный файл может не сдвинуть отметку сколь
            // угодно долго. Потолок спрашиваем только на самом взгляде: между
            // взглядами папку никто не смотрит вовсе
            bool пораВсёравно = посмотрели && WalkAnyway(Now() - walked, RescanAnywayAfter);
            if (!пораВсёравно &&
                !NeedFullScan(index != null, прошло, RescanAfter, stamp, сейчас))
            {
                // ЧАСЫ ДВИГАЕТ ТОЛЬКО НАСТОЯЩИЙ ВЗГЛЯД (см. LookedAtFolder).
                // Отметку сняли и она та же — значит следующие двадцать секунд
                // смотреть незачем, часы сдвигаем. А вот когда срок ещё НЕ
                // вышел, папку никто не трогал: сдвинь часы и тут — и окно,
                // спрашивающее картинки по нескольку раз в секунду, отодвигало
                // бы срок вечно, а положенная при живом боте картинка не
                // появилась бы никогда
                if (посмотрели)
                    scanned = Now();
                return;
            }
            scanned = Now();

            HashSet<string>? последняя = null;
            folder = Pick(FolderCandidates(), dir =>
            {
                try
                {
                    if (!Directory.Exists(dir))
                        return NoFolder;
                    последняя = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string file in Directory.EnumerateFiles(dir, "*.png",
                                 SearchOption.AllDirectories))
                        последняя.Add(Path.GetRelativePath(dir, file).Replace('\\', '/'));
                    return последняя.Count;
                }
                catch
                {
                    // Папку могли положить на сетевой диск, запретить чтение,
                    // унести прямо во время обхода. Картинки не стоят упавшего
                    // окна: пусть их просто не будет, а причина скажется словами
                    последняя = null;
                    return NoFolder;
                }
            });
            // Обход кончается НА ВЫБРАННОЙ папке (Pick возвращает её сразу), и
            // список имён от неё же. Если выбрана пустая — список пуст, что
            // правда и есть
            index = последняя ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            walked = Now();
            // Отметку снимаем С ВЫБРАННОЙ папки, а не с той, что была до обхода:
            // папка могла смениться (человек убрал «icons» рядом с ботом, и
            // победила папка игры), и чужая отметка навсегда убедила бы нас,
            // что «ничего не менялось»
            stamp = Stamp(Каталоги(folder));
        }
    }

    /// <summary>
    /// ПРИМЕТЫ ВЫГРУЗКИ «all»: эти три файла кладёт только она, а «inv» не
    /// кладёт НИ ОДНОГО из них.
    ///
    /// Почему именно они, и почему это факт, а не примета «на глазок». Все три
    /// блока задают творческий инвентарь готовыми стопками, а вкладок у них нет
    /// вовсе — а выгрузка «inv» берёт блок ТОЛЬКО с непустым
    /// <c>CreativeInventoryTabs</c> (VintagestoryLib 1.22.6,
    /// <c>SystemClientCommands.OnRenderBlockItemPngs</c>). Сверено по самим
    /// описаниям: <c>wood/chest.json</c> и <c>metal/lantern.json</c> —
    /// <c>creativeinventory: {  }</c> плюс <c>creativeinventoryStacksByType</c>;
    /// <c>reed/baskettrap.json</c> — <c>creativeinventory</c> нет вовсе, только
    /// <c>creativeinventoryStacks</c>.
    ///
    /// Трое, а не один: сервер бывает с модами, которые чинят или сносят
    /// отдельный блок, и одна пропавшая примета не должна объявлять полную
    /// выгрузку неполной.
    /// </summary>
    public static readonly IReadOnlyList<string> AllOnlyMarks =
    [
        $"{BlockFolder}/chest-north.png",
        $"{BlockFolder}/lantern-large-up.png",
        $"{BlockFolder}/baskettrap-reed.png"
    ];

    /// <summary>
    /// ВЫГРУЖАЛИ ЛИ ЭТУ ПАПКУ КОМАНДОЙ «inv». Чистое правило — потому и закрыто
    /// тестом: чужих папок на стенде нет.
    ///
    /// ЗАЧЕМ ВООБЩЕ СПРАШИВАТЬ. Совет «перевыгрузите с all» верен ровно до того
    /// мига, как человек его выполнил; сказанный после — он врёт про его машину.
    /// Пустая папка сюда не попадает: там разговор другой («выгрузите хоть
    /// что-нибудь»), и звать перевыгружать нечего.
    /// </summary>
    /// <param name="files">Сколько png в папке; 0 и меньше — судить не о чем.</param>
    /// <param name="has">Есть ли в папке файл с таким именем (относительным, через «/»).</param>
    public static bool MadeByInv(int files, Func<string, bool> has) =>
        files > 0 && !AllOnlyMarks.Any(has);

    /// <summary>
    /// СКОЛЬКО ПАМЯТИ ДЕРЖАТ КАРТИНКИ — по тому, что лежит, а не по ощущению.
    ///
    /// ПРЯМОЙ ВОПРОС ЗАКАЗЧИКА 17.08: «бот стал много жрать оперативки, это
    /// из-за иконок? надо тогда сделать возможность включать режим без них».
    /// Отвечать на такое догадкой нельзя, а один раз ответить числом мало:
    /// завтра картинок станет вдвое больше. Поэтому число живёт в окне рядом с
    /// весом модели мира (вкладка «Память»), и сравнить их можно глазами.
    ///
    /// ЧТО ИМЕННО СЧИТАЕМ. Сами картинки в памяти не держатся ВОВСЕ: файл
    /// читается на запрос окна и тут же отпускается (<see cref="Bytes"/>).
    /// Держится только ОПИСЬ ИМЁН — по строке на файл. Её и считаем: два байта
    /// на букву (строки .NET двухбайтовые) плюс заголовок строки и место в
    /// наборе.
    ///
    /// ЧИСЛО НЕМНОГО ЗАНИЖЕНО, И ЭТО СКАЗАНО ЧЕСТНО. Замер на папке заказчика
    /// (18 560 файлов): GC.GetTotalMemory даёт 2,49 МБ, здесь насчитывается
    /// 2,22 МБ. Разница — запас, который .NET держит в самом наборе (место под
    /// рост) и которого в именах нет; врать в БОЛЬШУЮ сторону «для запаса»
    /// было бы хуже: тогда картинки выглядели бы дороже, чем они есть, а
    /// человек по этому числу решает, оставлять ли их.
    /// </summary>
    public static (int Files, long Bytes) Footprint()
    {
        Fresh();
        lock (gate)
        {
            if (index is not { } опись)
                return (0, 0);
            long байт = 0;
            foreach (string имя in опись)
            {
                // 22 Б — заголовок строки .NET (объект, длина, замыкающий ноль),
                // округление до 8 — выравнивание кучи, 24 Б — запись в HashSet
                // вместе с корзиной
                long строка = 22 + 2L * имя.Length;
                байт += (строка + 7) / 8 * 8 + 24;
            }
            return (опись.Count, байт);
        }
    }

    /// <summary>Что за папка с картинками нашлась и сколько в ней файлов.</summary>
    public static IconFolder Look()
    {
        Fresh();
        lock (gate)
            return new IconFolder(folder, index?.Count ?? 0,
                // Судим по СВОЕМУ же обходу папки, а не спрашиваем диск заново:
                // список имён уже собран, и второй поход к диску разошёлся бы
                // с ним ровно тогда, когда человек перекладывает картинки
                MadeByInv(index?.Count ?? 0, name => index?.Contains(name) == true));
    }

    /// <summary>
    /// Полный путь к картинке этого кода или null, если её не выгружали.
    /// null — честное «нет», а не повод нарисовать что-нибудь похожее.
    /// </summary>
    public static string? Find(string? code, bool block)
    {
        Fresh();
        lock (gate)
        {
            if (folder is not { } dir || index is not { Count: > 0 } have)
                return null;
            foreach (string name in FileNames(code, block))
                if (have.Contains(name))
                    return Path.Combine(dir, name.Replace('/', Path.DirectorySeparatorChar));
            return null;
        }
    }

    /// <summary>Есть ли картинка у этого кода. Для окна: рисовать или писать словом.</summary>
    public static bool Has(string? code, bool block) => Find(code, block) != null;

    /// <summary>
    /// ПОЧЕМУ У ЭТОЙ ВЕЩИ НЕТ КАРТИНКИ — словами и по фактам. Чистая функция:
    /// всё, что ей нужно, передано снаружи, поэтому её и проверяет тест.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ОНА ЕСТЬ. 16.08 заказчик: «и иконку фонаря
    /// не подхватил» — в клетке стояло словом «lantern-large-up», а соседние
    /// вещи были нарисованы. Разбор показал, что сопоставление имён тут ни при
    /// чём: файла <c>icons/block/lantern-large-up.png</c> не существует вовсе,
    /// как и всех <c>chest-*</c>, <c>crate-*</c>, <c>cabinet-*</c>,
    /// <c>bucket-*</c>, <c>pie-*</c>. Причина — В САМОЙ ВЫГРУЗКЕ, и она
    /// проверена по коду игры (VintagestoryLib 1.22.6,
    /// <c>SystemClientCommands.OnRenderBlockItemPngs</c>): выгрузка <c>inv</c>
    /// берёт блок ТОЛЬКО если у него непустой <c>CreativeInventoryTabs</c>, а у
    /// фонаря, сундука, ящика, шкафа, ведра и пирога вкладки пустые —
    /// творческий инвентарь им задан через <c>creativeinventoryStacks</c>
    /// (assets/survival/blocktypes/metal/lantern.json: <c>creativeinventory:
    /// {  }</c> плюс <c>creativeinventoryStacksByType</c>). Выгрузка <c>all</c>
    /// такого различия не делает и кладёт их все.
    ///
    /// Пока бот об этом молчал, человек видел голый код и разумно решал, что
    /// сломан поиск файла. Молчание тут дороже любой ошибки.
    /// </summary>
    public static string SayNoPicture(string? code, bool block, IconFolder folder)
    {
        var имена = FileNames(code, block);
        if (имена.Count == 0)
            return $"«{code}» не похоже на код вещи: в имени файла игра допускает только " +
                   "латинские буквы, цифры, «-», «_» и «/» — картинку под такой код " +
                   "не искал вовсе";

        string искал = "искал " + string.Join(", ", имена);

        if (folder.Path is not { Length: > 0 } папка)
            return $"картинок нет ни в одной из папок, куда я смотрю ({искал}) — {Recipe}";
        if (folder.Files == 0)
            return $"папка {папка} есть, но png в ней не нашлось ({искал}) — {Recipe}";

        // ПРИЧИНУ НАЗЫВАЕМ ТУ, КОТОРАЯ ПОДХОДИТ ЭТОЙ ПАПКЕ. «Перевыгрузите с
        // all» — верный совет ровно до того мига, как человек его выполнил:
        // сказанный после, он посылает делать заново уже сделанное и врёт про
        // его машину. Отличить одно от другого можно фактом, а не догадкой
        // (см. MadeByInv), — значит, обязаны
        return $"в {папка} нет ни одного из этих файлов: {string.Join(", ", имена)}. " +
               (folder.MadeByInv ? ЧастаяПричина : ВыгрузкаПолная);
    }

    /// <summary>
    /// ПАПКА ПОЛНАЯ, А ЭТОЙ ВЕЩИ В НЕЙ НЕТ. Тогда «inv» ни при чём, и звать
    /// перевыгружать нечестно: выгрузка знает ровно те коды, что были у ИГРЫ на
    /// машине человека, а код пришёл с СЕРВЕРА — мод, другая версия, чужая
    /// сборка. Сказать это прямо дешевле, чем оставить человека гонять выгрузку
    /// по второму разу.
    /// </summary>
    public const string ВыгрузкаПолная =
        "Выгрузка похожа на полную («all»): вещи, которые пропускает «inv», " +
        "в папке есть. Значит, дело не в ней — этого кода игра на вашей машине " +
        "не выгружала вовсе (мод сервера, другая версия сборки). Одну вещь можно " +
        "добить командой «.exponepng code <block|item> <код>»";

    /// <summary>
    /// САМАЯ ЧАСТАЯ ПРИЧИНА ОДИНОКОЙ ПУСТОЙ КЛЕТКИ при полной папке — и она не
    /// догадка, а разбор живого случая с фонарём (см.
    /// <see cref="SayNoPicture"/>). Строка отдельной константой, потому что её
    /// говорят двое: клетка про одну вещь и общая строка внизу вкладки.
    /// </summary>
    public const string ЧастаяПричина =
        "Скорее всего, выгрузка делалась командой «.blockitempngexport inv»: " +
        "она пропускает вещи, у которых творческий инвентарь задан не вкладками, " +
        "а готовыми стопками, — фонарь, сундук, ящик, шкаф, ведро, пирог. " +
        "Перевыгрузите с «all» вместо «inv» (проверено по коду игры 1.22.6, " +
        "SystemClientCommands.OnRenderBlockItemPngs)";

    /// <summary>Почему нет картинки У ЭТОЙ вещи — по нынешней папке. Для окна и журнала.</summary>
    public static string WhyNoPicture(string? code, bool block) =>
        SayNoPicture(code, block, Look());

    /// <summary>
    /// СПРОСИТЬ КАРТИНКУ ПО КОДУ ВЕЩИ — один способ на все вкладки, где есть
    /// вещи: сумки, склад, запасы, задачи.
    ///
    /// ЗАЧЕМ. Заказ 16.08: «и везде где нужно теперь мы можем использовать
    /// игровые иконки, допустим на складе». До этого поля «картинка» и «класс»
    /// собирала одна вкладка «Инвентарь» прямо в панели, а склад отдавал голые
    /// коды. Второй сборщик тех же двух полей рано или поздно разошёлся бы с
    /// первым по одной строке — и на складе показалась бы не та вещь, которая
    /// там лежит (один код бывает И блоком, И предметом: candle, cheese, egg,
    /// ore, paper…).
    ///
    /// Блок это или предмет, решает НЕ имя кода, а реестр сервера — тем же
    /// вопросом, что и у сумок (<see cref="Bags.IsBlock"/>).
    /// </summary>
    public static ItemIcon Ask(WorldModel world, string? code)
    {
        string код = (code ?? "").Trim();
        return код.Length == 0
            ? new ItemIcon("", ItemFolder, false)
            : Of(код, Bags.IsBlock(world, код));
    }

    /// <summary>
    /// То же, но когда «блок или предмет» УЖЕ спрошено у реестра. Второй вход в
    /// тот же механизм, а не вторая его копия: у сумок ответ уже есть (он нужен
    /// им ещё и ради имени), и спрашивать реестр второй раз на каждую клетку
    /// незачем.
    /// </summary>
    public static ItemIcon Of(string? code, bool block)
    {
        string код = (code ?? "").Trim();
        return new ItemIcon(код, block ? BlockFolder : ItemFolder,
            код.Length > 0 && Has(код, block));
    }

    /// <summary>
    /// Сама картинка байтами или null. Читаем ТОЛЬКО то, что нашли обходом
    /// своей же папки: имя из запроса никогда не превращается в путь напрямую.
    /// </summary>
    public static byte[]? Bytes(string? code, bool block)
    {
        if (Find(code, block) is not { } path)
            return null;
        try
        {
            return File.ReadAllBytes(path);
        }
        catch
        {
            // Файл могли забрать между обходом и чтением — это «нет картинки»,
            // а не беда: клетка покажет имя словом, как и без папки
            return null;
        }
    }

    private static IconEvidence Count()
    {
        if (GameFolders.FindGame() is not { } game)
            return new IconEvidence(null, 0, 0);

        string assets = Path.Combine(game, "assets");
        int total = 0, modelled = 0;
        try
        {
            foreach (string domain in Directory.EnumerateDirectories(assets))
            {
                string types = Path.Combine(domain, ItemTypesFolder);
                if (!Directory.Exists(types))
                    continue;
                foreach (string file in Directory.EnumerateFiles(types, "*.json",
                             SearchOption.AllDirectories))
                {
                    total++;
                    try
                    {
                        if (MentionsShape(File.ReadAllText(file)))
                            modelled++;
                    }
                    catch
                    {
                        // Нечитаемый файл — не повод бросать счёт: улики
                        // и так приблизительные, а молчаливый ноль хуже
                    }
                }
            }
        }
        catch
        {
            // Папка ассетов может быть недоступна (права, сетевой диск).
            // Тогда улик просто меньше — вывод об этом и скажет
        }
        return new IconEvidence(game, total, modelled);
    }

    /// <summary>
    /// Задаёт ли описание предмета объёмную форму. Игра пишет это поле
    /// по-разному — «shape», «shapeByType», «shapebytype», — поэтому ищем
    /// имя поля без учёта регистра. Чистая функция: закрыта тестом.
    /// </summary>
    public static bool MentionsShape(string? itemTypeJson)
    {
        if (itemTypeJson is not { Length: > 0 })
            return false;
        int at = 0;
        while ((at = itemTypeJson.IndexOf("shape", at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int i = at + "shape".Length;
            // «shapeByType» — то же поле; дальше пропускаем кавычку и пробелы
            if (Starts(itemTypeJson, i, "bytype"))
                i += "bytype".Length;
            while (i < itemTypeJson.Length && (itemTypeJson[i] is '"' or '\'' or ' ' or '\t'))
                i++;
            if (i < itemTypeJson.Length && itemTypeJson[i] == ':')
                return true;
            at++;
        }
        return false;
    }

    private static bool Starts(string text, int at, string what) =>
        at + what.Length <= text.Length &&
        text.AsSpan(at, what.Length).Equals(what, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Одна строка человеку: почему в списке нет картинок. Чистая функция от
    /// улик — её и проверяет тест, а не содержимое чужой папки.
    /// </summary>
    public static string Verdict(IconEvidence e)
    {
        if (e.GameFolder is null)
            return "картинок предметов нет: игра на этой машине не найдена, " +
                   "смотреть было негде";
        if (e.ItemTypes == 0)
            return $"картинок предметов нет: в {e.GameFolder} не нашлось описаний предметов — " +
                   "смотреть было нечего";
        if (e.Modelled == 0)
            return $"картинок предметов нет: ни одно из {e.ItemTypes} описаний не задаёт форму — " +
                   "это не похоже на игру, которую я знаю; стоит посмотреть заново";
        return $"картинок предметов нет: игра рисует их на видеокарте по объёмным моделям " +
               $"({e.Modelled} описаний из {e.ItemTypes} задают форму), готовых файлов-иконок " +
               "в её ассетах не лежит. Текстуру вместо иконки не показываю: у медного топора " +
               "она — ровный оранжевый квадрат слитка, и в списке это выглядело бы как медь";
    }

    /// <summary>Куда человеку идти за рецептом выгрузки — одним и тем же словом везде.</summary>
    public const string Recipe = "рецепт в ЗАПУСК.md, раздел «Иконки предметов»";

    /// <summary>
    /// ЧТО С КАРТИНКАМИ — одна строка человеку. Чистая функция от улик и от
    /// того, что нашлось в папке: её и проверяет тест, а не чужие папки.
    ///
    /// Случаев ровно три, и путать их нельзя. «Папки нет» — человек ещё не
    /// выгружал, ему нужен рецепт. «Папка есть, а png нет» — он что-то сделал,
    /// но не то, и рецепт нужен тем более. «Картинки есть» — их всё равно может
    /// не хватать на всё, и молчать об этом нечестно: пустая клетка при полной
    /// папке читается как поломка окна.
    /// </summary>
    public static string Say(IconEvidence e, IconFolder f)
    {
        if (f.Path is { } dir && f.Files > 0)
            return $"картинки беру из {dir} ({f.Files} шт.) — их один раз выгрузил " +
                   "графический клиент игры; сам бот рисовать не умеет и не научится. " +
                   "Чего в папке нет — показываю именем словом, чужую картинку не подставляю." +
                   // Живой случай 16.08: «и иконку фонаря не подхватил». Папка была
                   // полна (8582 файла), а фонаря, сундука, ящика и ведра в ней не
                   // было ни одного — их пропустила сама выгрузка. Не сказать об этом
                   // значит оставить человека думать, что сломан поиск файла.
                   //
                   // НО ГОВОРИМ ЭТО ТОЛЬКО ТОМУ, У КОГО ТАК И ЕСТЬ. Тот же
                   // заказчик по этому совету перевыгрузил папку в тот же день
                   // (8582 файла → 18560, фонарь на месте) — и безусловная
                   // строка звала бы его выгружать заново уже сделанное
                   (f.MadeByInv ? " " + ЧастаяПричина : "");

        string почему = f.Path is { } пустая
            ? $"папка {пустая} есть, но png в ней не нашлось"
            : Verdict(e);

        return $"{почему}. Картинки МОЖНО получить: их рисует графический клиент игры " +
               $"командой .blockitempngexport — {Recipe}";
    }

    /// <summary>Тот же вывод, но по этой машине. Для окна и журнала.</summary>
    public static string Why() => Say(Inspect(), Look());
}

// ======================================================================
//  ЧТО У БОТА В СУМКАХ
// ======================================================================

/// <summary>Одна вещь в инвентаре — так, как её видит человек.</summary>
/// <param name="InventoryId">Полный id инвентаря ("hotbar-&lt;uid&gt;").</param>
/// <param name="Slot">Номер места в этом инвентаре.</param>
/// <param name="SlotName">Как называется место («левая рука») или null.</param>
/// <param name="Code">Код предмета — правда, которую всегда можно назвать.</param>
/// <param name="Name">Имя по-человечески; если игра его не знает — тот же код.</param>
/// <param name="NameKnown">Имя настоящее, а не подставленный код.</param>
/// <param name="Count">Сколько штук в стопке.</param>
/// <param name="Durability">Остаток прочности; null — прочности не бывает.</param>
/// <param name="MaxDurability">Полная прочность; null — прочности не бывает.</param>
/// <param name="InHand">Это тот самый слот, который бот держит в руке.</param>
/// <param name="Block">
/// Это БЛОК, а не предмет — по реестрам сервера (<see cref="Bags.IsBlock"/>).
/// Нужно дважды: за именем (у блоков свой ключ перевода) и за картинкой —
/// игра кладёт их в РАЗНЫЕ папки, а коды, которые есть и там и там, не
/// редкость (candle, cheese, egg, leather, ore, paper…). Спутай их — и человек
/// увидит в рюкзаке не ту вещь, которая там лежит.
/// </param>
public sealed record BagItem(
    string InventoryId,
    int Slot,
    string? SlotName,
    string Code,
    string Name,
    bool NameKnown,
    int Count,
    int? Durability,
    int? MaxDurability,
    bool InHand,
    bool Block = false)
{
    /// <summary>Доля оставшейся прочности 0..1; null — прочности не бывает.</summary>
    public double? Wear => Durability is { } left && MaxDurability is > 0 and var max
        ? (double)left / max
        : null;

    /// <summary>
    /// КАРТИНКА ЭТОЙ ВЕЩИ — тем же способом, каким её спрашивают склад, запасы
    /// и задачи (<see cref="ItemIcons.Of"/>). Первый потребитель общего
    /// механизма: раньше окно собирало «есть ли картинка» и «блок или предмет»
    /// само, и склад, взявшись за то же, завёл бы вторую копию этих двух правил.
    ///
    /// Считается на месте, а не хранится: папку бот перечитывает на ходу, и
    /// запомнённое «картинки нет» пережило бы саму выгрузку.
    /// </summary>
    public ItemIcon Icon => ItemIcons.Of(Code, Block);

    /// <summary>
    /// ЭТО — В ЛЕВОЙ РУКЕ. Отдельно от <see cref="InHand"/> (та про ПРАВУЮ, то
    /// есть про выбранное место хотбара), потому что рук у игрока две и держать
    /// он может в обеих: игра и сама смотрит обе (EntityPlayer.LightHsv), и
    /// механизм света у нас смотрит обе (<c>HandLight.Lit</c>).
    ///
    /// ЖИВОЙ СЛУЧАЙ ЗАКАЗЧИКА: «в инвентаре фонарь не отобразился в левой руке
    /// (ошибка)». Бот в это время нёс lantern-large-up в слоте 11 и честно
    /// писал об этом в журнал, а окно про руки говорило только одно — про
    /// правую. Выходило, что бот с фонарём в руке показан с пустыми руками.
    ///
    /// Вещь в описи бывает только у НЕПУСТОГО места, поэтому спрашивать нечего,
    /// кроме самого места: занято оно по определению.
    /// </summary>
    public bool InOffHand => Bags.SlotRole(InventoryId, Slot) == Bags.RoleOffHand;

    /// <summary>
    /// ЭТО НА БОТЕ — надетая одежда или броня.
    ///
    /// ЖАЛОБА ЗАКАЗЧИКА ДОСЛОВНО: «в вебформе инвентаря нельзя снять вручную
    /// броню, ток надеть». Пункт «Снять» показывать надо ровно у надетого, и
    /// решать это обязан бот, а не вёрстка: окно отличало надетое по РУССКОМУ
    /// названию раздела («одежда и броня»), и на английском окне пункт пропал
    /// бы молча — ровно так однажды терялась левая рука вместе с фонарём.
    ///
    /// Надетое живёт в «character-&lt;uid&gt;» и больше нигде: туда его кладёт
    /// надевание (<see cref="Hands.WhereWorn"/>) и оттуда же берёт холод,
    /// переодеваясь (<see cref="BodyHeat.Worn"/>).
    /// </summary>
    public bool Worn => InventoryId.StartsWith("character", StringComparison.Ordinal);

    public override string ToString() =>
        $"{Name} ×{Count}" + (Durability is { } d && MaxDurability is { } m ? $" ({d}/{m})" : "");
}

/// <summary>Один инвентарь целиком.</summary>
/// <param name="Title">Как назвать его человеку («хотбар», «рюкзак»).</param>
/// <param name="InventoryId">Полный id — по нему панель отличает одинаковые.</param>
/// <param name="Slots">Сколько мест всего.</param>
/// <param name="Free">Сколько мест СВОБОДНО под вещи (см. IsStorageSlot).</param>
/// <param name="Items">Что лежит, по порядку мест.</param>
public sealed record BagSection(
    string Title,
    string InventoryId,
    int Slots,
    int Free,
    IReadOnlyList<BagItem> Items);

/// <summary>Всё, что у бота есть при себе.</summary>
/// <param name="Known">Инвентари приходили с сервера. Ложь — показывать нечего.</param>
/// <param name="Message">Что случилось словами (пусто — всё в порядке).</param>
/// <param name="Sections">Инвентари по порядку: рука, рюкзак, одежда, курсор.</param>
/// <param name="Kinds">Сколько РАЗНЫХ вещей.</param>
/// <param name="Pieces">Сколько штук всего.</param>
/// <param name="NamesFrom">Откуда имена ("ru", "en") или почему их нет.</param>
/// <param name="IconsWhy">Почему в списке нет картинок (см. <see cref="ItemIcons"/>).</param>
/// <param name="Hand">
/// Номер активного слота хотбара; −1 — СЕРВЕР ЕЩЁ НЕ СКАЗАЛ.
///
/// Отдельным числом, а не только признаком у вещи: «рука пуста» и «какая
/// рука — не знаю» это РАЗНЫЕ ответы, и по одному признаку InHand их не
/// различить. То же правило, что у самого снимка (Known).
/// </param>
public sealed record BagReport(
    bool Known,
    string Message,
    IReadOnlyList<BagSection> Sections,
    int Kinds,
    int Pieces,
    string NamesFrom,
    string IconsWhy,
    int Hand = -1);

/// <summary>
/// ЧТО У БОТА В СУМКАХ — одним снимком, для окна управления и для чата.
///
/// Зачем отдельно. Спрашивают об этом трое: человек в окне («покажи инвентарь»),
/// чат («!сумка») и сам бот в отчётах. Раньше каждый складывал ответ по-своему,
/// и расхождения были не в мелочах: один считал места по всем слотам подряд,
/// другой — только по годным под вещи. Здесь один снимок и одни правила.
///
/// ЧЕСТНОСТЬ. Пустой список и «инвентарь не присылали» — РАЗНОЕ. Пока бот не
/// в мире, сервер инвентарей не слал вовсе, и «у меня ничего нет» было бы
/// выдумкой: см. <see cref="BagReport.Known"/>.
///
/// КУРСОР ПОКАЗЫВАЕМ. Слот мыши — не хранилище, и обычно он пуст, но именно
/// он однажды стоил вечера: склад доложил «легло 112 шт.», а сундук был пуст —
/// стопка висела НА КУРСОРЕ. Вещь, которую человек в игре видит прилипшей к
/// мыши, обязана быть видна и в окне, иначе окно врёт вместе с ботом.
/// </summary>
public static class Bags
{
    /// <summary>Слот умения в хотбаре (ItemSlotSkill) — вещей туда не кладут.</summary>
    public const int SkillSlot = 10;

    /// <summary>Слот левой руки в хотбаре (ItemSlotOffhand).</summary>
    public const int OffHandSlot = 11;

    /// <summary>Сколько первых слотов «backpack» заняты САМИМИ сумками.</summary>
    public const int BagSlots = 4;

    /// <summary>
    /// Как назвать инвентарь человеку. Пустая строка — этот инвентарь в список
    /// не идёт (сетка крафта, творческий, чужой сундук: сундук бот открывает
    /// временно, и в «что у меня есть» ему не место).
    ///
    /// Чистое правило: id инвентарей задаёт игра, и опечатка здесь тихо
    /// прячет от человека целый рюкзак.
    /// </summary>
    public static string Title(string inventoryId)
    {
        if (inventoryId.StartsWith("hotbar", StringComparison.Ordinal)) return "хотбар";
        if (inventoryId.StartsWith("backpack", StringComparison.Ordinal)) return "рюкзак";
        if (inventoryId.StartsWith("character", StringComparison.Ordinal)) return "одежда и броня";
        if (inventoryId.StartsWith("mouse", StringComparison.Ordinal)) return "на курсоре";
        return "";
    }

    /// <summary>
    /// Порядок инвентарей в списке: сперва то, что в руках, потом запасы,
    /// потом надетое, и в самом конце курсор — он бывает занят редко, но
    /// пропустить его нельзя (см. оговорку у класса).
    /// </summary>
    public static int Order(string inventoryId) => Title(inventoryId) switch
    {
        "хотбар" => 0,
        "рюкзак" => 1,
        "одежда и броня" => 2,
        "на курсоре" => 3,
        _ => 9
    };

    /// <summary>Особое место: умение хотбара (ItemSlotSkill).</summary>
    public const string RoleSkill = "skill";

    /// <summary>Особое место: левая рука (ItemSlotOffhand).</summary>
    public const string RoleOffHand = "offhand";

    /// <summary>Особое место: сама сумка (первые места «backpack»).</summary>
    public const string RoleBag = "bag";

    /// <summary>Особые места брони.</summary>
    public const string RoleHead = "head", RoleBody = "body", RoleLegs = "legs";

    /// <summary>
    /// ЧТО ЭТО ЗА МЕСТО — ОДНИМ МАШИННЫМ СЛОВОМ. Пустая строка — обычное место,
    /// номера достаточно.
    ///
    /// ЗАЧЕМ РЯДОМ С <see cref="SlotName"/>. Русское имя места читает человек, а
    /// РАСКЛАДКУ по нему выбирала вёрстка: «если имяМеста непусто — уводи клетку
    /// в отдельную строку». Это ровно та ошибка, от которой уже завели отдельный
    /// «вид» инвентаря: переведи окно на английский — и левая рука, у которой
    /// имя стало «off hand», перестала бы находиться, а фонарь в ней исчез бы из
    /// окна МОЛЧА. Поэтому решает машинное слово, а имя остаётся человеку.
    ///
    /// Второго списка «слот 11 — это левая рука» при этом не заводится: имя
    /// собирается ИЗ ЭТОГО ЖЕ ответа (см. <see cref="SlotName"/>).
    ///
    /// Числа не выдуманы: слоты 10 и 11 хотбара — умение и левая рука
    /// (InventoryPlayerHotbar.NewSlot: 10 → ItemSlotSkill, 11 → ItemSlotOffhand),
    /// первые четыре места рюкзака — сами сумки (InventoryPlayerBackpacks),
    /// а 12/13/14 в одежде — броня головы, тела и ног (см. <see cref="ArmorInfo"/>).
    /// Прочим местам одежды роли не даём: придумывать им имена — та же выдумка,
    /// что и списки кодов.
    /// </summary>
    public static string SlotRole(string inventoryId, int slot)
    {
        if (inventoryId.StartsWith("hotbar", StringComparison.Ordinal))
            return slot switch
            {
                SkillSlot => RoleSkill,
                OffHandSlot => RoleOffHand,
                _ => ""
            };
        if (inventoryId.StartsWith("backpack", StringComparison.Ordinal))
            return slot < BagSlots ? RoleBag : "";
        if (inventoryId.StartsWith("character", StringComparison.Ordinal))
            return slot switch
            {
                ArmorInfo.HeadSlot => RoleHead,
                ArmorInfo.BodySlot => RoleBody,
                ArmorInfo.LegsSlot => RoleLegs,
                _ => ""
            };
        return "";
    }

    /// <summary>
    /// Как называется МЕСТО, если у него есть имя. null — обычное место, номера
    /// достаточно. Имя ЕДИНСТВЕННОЕ на весь проект и собирается из роли места
    /// (<see cref="SlotRole"/>), чтобы два списка не разошлись.
    /// </summary>
    public static string? SlotName(string inventoryId, int slot) =>
        SlotRole(inventoryId, slot) switch
        {
            RoleSkill => "умение",
            RoleOffHand => "левая рука",
            RoleBag => "сумка",
            RoleHead => "голова",
            RoleBody => "тело",
            RoleLegs => "ноги",
            _ => null
        };

    /// <summary>
    /// РИСУЕТ ЛИ САМА ИГРА КЛЕТКУ ЭТОГО МЕСТА.
    ///
    /// ЖАЛОБА ЗАКАЗЧИКА ДОСЛОВНО: «и в игре нет умений, лишнее окно в
    /// инвентаре». Проверено по сборкам 1.22.6, и он прав — слот умения в
    /// данных ЕСТЬ, а клетки под него в игре НЕТ НИГДЕ:
    ///   • HudHotbar.ComposeGuis кладёт в полосу ровно
    ///     <c>AddItemSlotGrid(hotbarInv, …, 10, new int[10]{0,…,9}, …, "hotbargrid")</c>,
    ///     левую руку — отдельной клеткой <c>new int[1]{11}</c> («offhandgrid»),
    ///     и места 10 в этом списке нет;
    ///   • GuiDialogInventory.ComposeSurvivalInvDialog показывает только рюкзак
    ///     (<c>AddItemSlotGridExcl(backpackInv, …, 6, …)</c>) и сетку крафта —
    ///     хотбара там нет вовсе.
    /// Само место при этом настоящее: <c>InventoryPlayerHotbar.NewSlot(10)</c> —
    /// это <c>ItemSlotSkill</c>, и в ванили в него кладётся ровно одна вещь,
    /// умение «timeswitch» (assets/survival/itemtypes/skill/timeswitch.json,
    /// storageFlags 1024 = Skill).
    ///
    /// Отсюда правило: пустую клетку умения не показываем — её нет и в игре;
    /// ЗАНЯТУЮ показать обязаны, иначе умение, лежащее у бота, пропало бы из
    /// окна молча. Поэтому здесь спрашивают ещё и «занято ли».
    /// </summary>
    public static bool ShownInGame(string inventoryId, int slot, bool occupied) =>
        occupied || SlotRole(inventoryId, slot) != RoleSkill;

    /// <summary>
    /// СКОЛЬКО КЛЕТОК В РЯД РИСУЕТ ИГРА. Ноль — у игры своей раскладки для
    /// этого инвентаря нет, и окно решает само.
    ///
    /// Числа спрошены у игры, а не подобраны на глаз:
    ///   • хотбар — <c>AddItemSlotGrid(hotbarInv, …, 10, …, "hotbargrid")</c>
    ///     (HudHotbar): одна строка ровно в десять мест;
    ///   • рюкзак — <c>AddItemSlotGridExcl(backpackInv, …, 6, …, "slotgrid")</c>
    ///     (GuiDialogInventory): шесть в ряд.
    /// Одежду игра рисует не сеткой, а куклой с клетками вокруг неё
    /// (GuiDialogCharacter: «leftSlots» и «rightSlots» по одной колонке плюс три
    /// клетки брони) — куклы у окна нет, и врать про «столько-то в ряд» тут
    /// нечем: пусть решает вёрстка.
    /// </summary>
    public static int Columns(string inventoryId)
    {
        if (inventoryId.StartsWith("hotbar", StringComparison.Ordinal)) return 10;
        if (inventoryId.StartsWith("backpack", StringComparison.Ordinal)) return 6;
        return 0;
    }

    /// <summary>
    /// Блок это или предмет — по реестрам сервера, а не по виду кода.
    /// Нужно только ради имени: у блоков и предметов ключи перевода разные,
    /// и «bread-spelt-perfect» (это БЛОК) по ключу предмета безымянный.
    /// </summary>
    public static bool IsBlock(WorldModel world, string code)
    {
        if (world.GetGameItemByCode(code) != null)
            return false;
        return world.GetGameBlockByCode(code) is { BlockId: > 0 };
    }

    /// <summary>
    /// Снимок: что у бота есть прямо сейчас. Ничего не меняет и ничего не ждёт —
    /// его можно звать хоть на каждый запрос окна.
    /// </summary>
    public static BagReport Snapshot(BotContext ctx)
    {
        // Имена читаем лениво: первый вызов разбирает файлы игры, дальше —
        // готовый словарь. Не получилось — не беда, коды покажем всё равно
        GameNames.Load();
        string namesFrom = GameNames.Ready
            ? (GameNames.Problem.Length > 0 ? GameNames.Problem : GameNames.Language)
            : GameNames.Problem;

        var own = ctx.Self.OwnInventories
            .Where(kv => Title(kv.Key).Length > 0)
            .OrderBy(kv => Order(kv.Key))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

        if (own.Count == 0)
            return new BagReport(false,
                "инвентарь ещё не присылали — бот не в мире; это не «пусто», а «не знаю»",
                [], 0, 0, namesFrom, ItemIcons.Why());

        int hand = ctx.Hands?.ActiveSlot ?? -1;
        var sections = new List<BagSection>();
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        int pieces = 0;

        foreach (var (invId, slots) in own)
        {
            var items = new List<BagItem>();
            int free = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                var content = slots[i];
                if (content.IsEmpty)
                {
                    if (SelfState.IsStorageSlot(invId, i))
                        free++;
                    continue;
                }
                string code = content.Code!;
                bool block = IsBlock(ctx.World, code);
                string? name = GameNames.Name(code, block);

                int? left = ctx.Wear?.Remaining(content);
                int max = ctx.Wear?.MaxDurability(code) ?? 0;

                items.Add(new BagItem(
                    invId, i, SlotName(invId, i), code,
                    name ?? code, name != null,
                    content.Count,
                    left, max > 0 ? max : null,
                    invId.StartsWith("hotbar", StringComparison.Ordinal) && i == hand,
                    block));

                kinds.Add(code);
                pieces += content.Count;
            }

            // Пустой курсор человеку не нужен — он у него пуст всегда;
            // пустой рюкзак нужен: это ответ на «а сколько у тебя места»
            if (items.Count == 0 && Title(invId) == "на курсоре")
                continue;

            sections.Add(new BagSection(Title(invId), invId, slots.Length, free, items));
        }

        return new BagReport(true, "", sections, kinds.Count, pieces, namesFrom,
            ItemIcons.Why(), hand);
    }

    /// <summary>
    /// Одна строка для чата: коротко и по делу — И С ТЕМ, ЧТО В РУКЕ.
    ///
    /// ЧТО В РУКЕ, ЗДЕСЬ ТЕРЯЛОСЬ. Снимок этот признак несёт
    /// (<see cref="BagItem.InHand"/>), а строка группировала вещи по имени и
    /// выбрасывала его — то есть НАРУЖУ бот про свою руку не говорил ВООБЩЕ.
    /// Живой случай 26.08: проверить пункт «после выстрела меч вернулся в
    /// руку» оказалось НЕЧЕМ — бой говорит только при НЕУДАЧЕ, а спросить
    /// снаружи было негде, и пункт остался непроверенным. Четыре попытки снять
    /// это косвенно (прочность копья, здоровье цели по тикам) сорвались.
    /// Теперь рука названа прямо, и живая проверка стала возможной.
    /// </summary>
    public static string Line(BagReport report)
    {
        if (!report.Known)
            return report.Message;
        if (report.Pieces == 0)
            return "в сумках пусто";
        // ТРИ РАЗНЫХ ОТВЕТА, И СВАЛИВАТЬ ИХ В ОДИН НЕЛЬЗЯ: в руке вещь, рука
        // пуста, про руку ничего не known. Последнее — не «пусто», а «не знаю»:
        // то же правило, по которому весь снимок отличает «инвентарь не
        // присылали» от «в сумках пусто»
        var вРуке = report.Sections.SelectMany(s => s.Items).FirstOrDefault(i => i.InHand);
        string рука = report.Hand < 0
            ? "какой слот активен, сервер ещё не сказал"
            : вРуке is { } взято ? взято.Name : "пусто";
        return $"{report.Kinds} видов, {report.Pieces} шт.: " + string.Join(", ",
            report.Sections
                .SelectMany(s => s.Items)
                .GroupBy(i => i.Name, StringComparer.Ordinal)
                .Select(g => $"{g.Key} ×{g.Sum(i => i.Count)}")
                .Take(12)) +
            $"; в руке: {рука}";
    }
}
