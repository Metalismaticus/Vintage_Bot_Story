using System.Runtime.InteropServices;

namespace VsBotKit;

/// <summary>
/// Где на ЭТОЙ системе лежит игра и её данные.
///
/// Зачем отдельно. Раньше ответ был один и зашитый: «папка данных пользователя
/// плюс Vintagestory». На Windows это правда, на Linux — правда наполовину
/// (игру ставят пятью разными способами), а на macOS неправда вовсе: там игра
/// живёт внутри бандла Vintagestory.app, и по старому пути нет ничего. Бот на
/// маке падал бы ещё до первой строки — при загрузке игровых сборок.
///
/// Порядок один и тот же везде: сперва то, что сказал человек
/// (<see cref="EnvVar"/>), потом обычные места этой системы. Ничего не
/// угадываем: папка считается найденной, только если в ней лежит
/// <see cref="Marker"/>.
/// </summary>
public static class GameFolders
{
    /// <summary>Переменная окружения с путём к игре — её же просит вики модостроения.</summary>
    public const string EnvVar = "VINTAGE_STORY";

    /// <summary>По этому файлу узнаём папку игры: без него это просто папка.</summary>
    public const string Marker = "VintagestoryAPI.dll";

    /// <summary>Имя папки данных игры (сохранения, журналы, моды, настройки).</summary>
    public const string DataFolderName = "VintagestoryData";

    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string AppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Чем называется нативная библиотека в этой системе. Игра ищет свои
    /// libzstd и прочее именно с таким расширением.
    /// </summary>
    public static string NativeExtension =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib"
        : ".so";

    /// <summary>Где искать УСТАНОВКУ игры — по порядку, от вероятного к редкому.</summary>
    public static IEnumerable<string> GameCandidates()
    {
        if (Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } told)
            yield return told;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(AppData, "Vintagestory");
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // На маке игра — приложение-бандл, и сборки внутри него
            yield return "/Applications/Vintagestory.app/Contents/MacOS";
            yield return Path.Combine(Home, "Applications/Vintagestory.app/Contents/MacOS");
            yield return "/Applications/Vintagestory.app/Contents/Resources";
        }

        // Linux: пять обычных способов установки
        yield return Path.Combine(Home, ".config/Vintagestory");
        yield return Path.Combine(Home, ".local/share/vintagestory");
        yield return "/usr/share/vintagestory";
        yield return "/opt/vintagestory";
        // Flatpak прячет всё в своей песочнице
        yield return Path.Combine(Home, ".var/app/at.vintagestory.VintageStory/data/vintagestory");
        // На случай, если .NET на этой системе считает «данные приложения» иначе
        yield return Path.Combine(AppData, "Vintagestory");
    }

    /// <summary>Папка установленной игры или null, если её не нашли.</summary>
    public static string? FindGame()
    {
        foreach (string dir in GameCandidates())
        {
            try
            {
                if (dir.Length > 0 && File.Exists(Path.Combine(dir, Marker)))
                    return dir;
            }
            catch
            {
                // Путь может оказаться негодным (нет прав, кривая переменная) —
                // это не повод падать, просто идём к следующему кандидату
            }
        }
        return null;
    }

    /// <summary>Где искать ДАННЫЕ игры: сохранения, журналы, папку модов.</summary>
    public static IEnumerable<string> DataCandidates()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(AppData, DataFolderName);
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // С 1.21 игра переехала сюда и сама переносит старую папку
            yield return Path.Combine(Home, "Library/Application Support", DataFolderName);
            yield return Path.Combine(Home, ".config", DataFolderName);
            yield break;
        }

        yield return Path.Combine(Home, ".config", DataFolderName);
        yield return Path.Combine(Home, ".var/app/at.vintagestory.VintageStory/config", DataFolderName);
        yield return Path.Combine(AppData, DataFolderName);
    }

    /// <summary>
    /// Папка данных игры. Существующая — первая найденная; если ни одной нет,
    /// возвращается та, где ей полагается быть на этой системе.
    /// </summary>
    public static string DataFolder()
    {
        string? first = null;
        foreach (string dir in DataCandidates())
        {
            first ??= dir;
            try
            {
                if (Directory.Exists(dir))
                    return dir;
            }
            catch
            {
                // см. FindGame: негодный путь — не повод падать
            }
        }
        return first ?? Path.Combine(AppData, DataFolderName);
    }

    /// <summary>Папка модов игры — туда ставится VSAutoPilot.</summary>
    public static string ModsFolder() => Path.Combine(DataFolder(), "Mods");

    /// <summary>
    /// Одна строка для журнала: что мы нашли на этой машине. Папку СОСТОЯНИЯ
    /// называем здесь же — иначе человек, у которого настройки уехали (бот в
    /// /opt, контейнер, VSBOT_STATE), ищет bot.json там, где его нет.
    /// </summary>
    public static string Describe() =>
        $"система {RuntimeInformation.OSDescription}; " +
        $"игра: {FindGame() ?? "не найдена"}; данные игры: {DataFolder()}; " +
        BotFolders.StateReason;
}

/// <summary>
/// Куда бот вправе ПИСАТЬ.
///
/// Настройки, журналы, шаблоны и записи прогона бот кладёт рядом с собой.
/// На Windows это обычно домашняя папка, и всё в порядке. На Linux бота часто
/// кладут в /opt или /usr/local — а там запись запрещена, и бот падал бы на
/// создании файла настроек, не сказав ничего внятного про права.
///
/// А ЕСЛИ ЧЕЛОВЕК СКАЗАЛ, ГДЕ ДЕРЖАТЬ, — слушаем его и никого больше
/// (<see cref="StateEnvVar"/>), тем же порядком, что и у игры
/// (<see cref="GameFolders.EnvVar"/>).
/// </summary>
public static class BotFolders
{
    /// <summary>
    /// «СОСТОЯНИЕ ДЕРЖИ ЗДЕСЬ» — переменная окружения, которую слушают ПЕРВОЙ.
    ///
    /// ЗАЧЕМ ОНА ЗАВЕДЕНА, живым случаем. В контейнере папка рядом с ботом
    /// (/app) ЗАПИСЫВАЕМА — она внутри образа. Правило «рядом, если можно»
    /// поэтому срабатывало всегда, и bot.json, ключ сессии, очередь задач и
    /// журналы ложились в слой образа: пересборка их стирала, а подключённый
    /// том стоял пустым. Догадаться об этом было нельзя — Dockerfile обещал
    /// ровно обратное.
    ///
    /// Обычной установки это не касается: переменной нет — всё как было.
    /// </summary>
    public const string StateEnvVar = "VSBOT_STATE";

    /// <summary>Папка рядом с исполняемым файлом.</summary>
    public static string Beside => AppContext.BaseDirectory;

    /// <summary>Как .NET зовёт каталог собранного — по нему выходная папка и узнаётся.</summary>
    public const string BuildFolderName = "bin";

    /// <summary>
    /// ЧТО ПЕРЕЖИВЁТ ПЕРЕСБОРКУ, когда программу запустили ИЗ ПАПКИ СБОРКИ.
    /// Чистое правило от одной строки — потому и закрыто стендом: чужих
    /// установок на стенде нет, а ошибиться здесь стоит хозяйства заказчика.
    ///
    /// ЖИВАЯ ПОТЕРЯ, РАДИ КОТОРОЙ ПРАВИЛО ЕСТЬ. У заказчика 18 560 выгруженных
    /// картинок, журналы, <c>bot.json</c> и <c>botsession.json</c> лежали в
    /// <c>VintageBotStory\bin\Debug\net10.0\</c> — то есть ВНУТРИ каталога
    /// сборки, который пересборка (а у хозяина бота — распаковка новой версии)
    /// стирает целиком. Их стёрли, и возвращать было нечего. Собственные слова
    /// заказчика после этого: «иконки нужно все же в проекте хранить».
    ///
    /// ВОЗВРАЩАЕМ ПАПКУ НАД ПРОЕКТОМ, А НЕ САМУ ПАПКУ ПРОЕКТА, и это не вкус.
    /// Всё, что лежит внутри папки проекта, MSBuild по заводскому правилу
    /// подбирает в <c>None</c> (<c>**\*</c> за вычетом <c>bin</c> и <c>obj</c>).
    /// Восемнадцать тысяч png внутри проекта — это восемнадцать тысяч лишних
    /// записей на КАЖДУЮ сборку и на каждое открытие решения. Рядом с проектом
    /// (там же, где лежит <c>presets</c>) их не видит ни один проект.
    ///
    /// «BIN» БЕЗ ЦЕЛЕВОЙ ПЛАТФОРМЫ НЕ СЧИТАЕТСЯ. Иначе бот, положенный в
    /// <c>/usr/local/bin/vsbot</c>, объявил бы своим хозяйством <c>/usr/local</c>
    /// — а это чужая папка, и звать туда человека нельзя. Настоящая выходная
    /// папка всегда несёт ниже <c>bin</c> имя платформы (<c>net10.0</c>).
    ///
    /// null — программа запущена НЕ из папки сборки (обычная установка,
    /// распакованный архив, контейнер). Тогда выдумывать нечего: хозяйство и
    /// так лежит там, где его никто не стирает.
    /// </summary>
    public static string? OutsideBuild(string? dir)
    {
        if (dir is not { Length: > 0 })
            return null;

        string начало;
        try
        {
            // Хвостовую косую снимаем СРАЗУ: именно ею и кончается
            // AppContext.BaseDirectory, а с ней Path.GetFileName отдаёт пустое
            // имя — и правило молча решало бы, что дошло до корня диска
            начало = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        }
        catch
        {
            // Путь с чужими буквами, слишком длинный, несуществующий диск —
            // это «правила не применить», а не повод падать
            return null;
        }

        bool виделиПлатформу = false;
        for (string? шаг = начало; шаг is { Length: > 0 }; шаг = Path.GetDirectoryName(шаг))
        {
            string имя = Path.GetFileName(шаг);
            if (имя.Length == 0)
                break;                       // дошли до корня диска
            if (виделиПлатформу &&
                имя.Equals(BuildFolderName, StringComparison.OrdinalIgnoreCase))
            {
                if (Path.GetDirectoryName(шаг) is not { Length: > 0 } проект)
                    return null;
                string? над = Path.GetDirectoryName(проект);
                // ПРОЕКТ ЛЕЖИТ ПРЯМО В КОРНЕ ДИСКА — над ним только сам корень.
                // Звать человека класть 18 560 картинок в «C:\» нельзя: там
                // общее хозяйство машины. Тогда отдаём сам проект — он всё
                // равно переживёт пересборку, а это главное
                return над is { Length: > 0 } && над != Path.GetPathRoot(проект)
                    ? над : проект;
            }
            if (LooksLikeFramework(имя))
                виделиПлатформу = true;
        }
        return null;
    }

    /// <summary>
    /// Похоже ли имя папки на целевую платформу: <c>net10.0</c>, <c>net472</c>,
    /// <c>netstandard2.1</c>, <c>netcoreapp3.1</c>. Именно она и отличает
    /// настоящую выходную папку от чужой папки с именем <c>bin</c>.
    /// </summary>
    private static bool LooksLikeFramework(string name) =>
        name.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
        name.Any(char.IsAsciiDigit);

    /// <summary>
    /// Устойчивое место ДЛЯ ЭТОГО ЗАПУСКА: <see cref="OutsideBuild"/> от папки
    /// рядом с программой. null — запущено не из сборки, и переезжать некуда.
    /// </summary>
    public static string? Lasting => OutsideBuild(Beside);

    /// <summary>
    /// Превратить имя, пришедшее снаружи, в безопасное имя файла.
    ///
    /// Именно снаружи: имена шаблонов и пресетов приходят из игрового чата и
    /// из панели, то есть от людей, которым бот не обязан доверять. Чистить их
    /// по <c>Path.GetInvalidFileNameChars()</c> нельзя — этот набор РАЗНЫЙ на
    /// разных системах: на Windows около сорока символов, на Linux ровно два.
    /// То есть «../../что-то» на Windows безобидно, а на Linux уезжает писать
    /// в чужую папку. Поэтому список свой и один на все системы.
    /// </summary>
    public static string SafeName(string name)
    {
        var safe = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_');
        string result = safe.ToString().Trim();
        return result.Length == 0 ? "без-имени" : result;
    }

    /// <summary>Можно ли писать в эту папку — проверяем делом, а не по правам.</summary>
    public static bool Writable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".проба-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? state;
    private static string stateReason = "";

    /// <summary>
    /// Где бот держит своё хозяйство: куда велели (<see cref="StateEnvVar"/>),
    /// иначе рядом с собой, а если и туда нельзя — в обычном месте для
    /// состояния программ этой системы.
    ///
    /// Ответ считается один раз: проверка идёт настоящей записью файла, а
    /// спрашивают об этом каждый раз, когда достраивается относительный путь.
    /// </summary>
    public static string State() => state ??= FindState();

    /// <summary>
    /// ПОЧЕМУ СОСТОЯНИЕ ЛЕЖИТ ИМЕННО ТАМ — одной строкой для журнала запуска.
    /// Молчаливый выбор папки неотличим от потерянных настроек: человек ищет
    /// bot.json там, где ждёт его увидеть, и не находит.
    /// </summary>
    public static string StateReason
    {
        get
        {
            State();
            return stateReason;
        }
    }

    /// <summary>
    /// ГДЕ ДЕРЖАТЬ СОСТОЯНИЕ — чистое правило, поэтому и проверяется тестом.
    ///
    /// Порядок один и тот же везде: сказанное человеком
    /// (<see cref="StateEnvVar"/>), потом папка рядом с программой, потом
    /// обычное место этой системы. «Можно ли туда писать» спрашивается СНАРУЖИ:
    /// на стенде ни контейнера, ни /opt настоящей записью не изобразишь, а
    /// проверять надо именно порядок — из-за него и уезжали настройки.
    ///
    /// Возвращает папку и строку «почему именно эта» — второе не украшение:
    /// молчаливый выбор папки неотличим от потерянных настроек.
    /// </summary>
    public static (string Folder, string Why) ChooseState(
        string? told, string beside, string fallback, Func<string, bool> writable)
    {
        string жалоба = "";
        if (told is { Length: > 0 })
        {
            if (writable(told))
                return (told, $"состояние бота: {told} — так велит {StateEnvVar}");
            // Уехать молча в другое место нельзя: человек будет искать свои
            // настройки и ключ сессии ровно там, где велел их держать
            жалоба = $"в {told} писать не выходит, хотя {StateEnvVar} велит держать " +
                     "состояние там; ";
        }

        return writable(beside)
            ? (beside, $"{жалоба}состояние бота: {beside} — рядом с программой")
            : (fallback, $"{жалоба}состояние бота: {fallback} — рядом с программой " +
                         $"({beside}) писать нельзя");
    }

    private static string FindState()
    {
        string? сказано = Environment.GetEnvironmentVariable(StateEnvVar) is { Length: > 0 } told
            ? Path.GetFullPath(told)
            : null;

        string fallback = Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } xdg
            ? Path.Combine(xdg, "vsbotkit")
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "vsbotkit");

        var (папка, почему) = ChooseState(сказано, Beside, fallback, Writable);
        stateReason = почему;
        Directory.CreateDirectory(папка);
        return папка;
    }
}
