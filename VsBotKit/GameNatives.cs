using System.Runtime.InteropServices;

namespace VsBotKit;

/// <summary>
/// НАТИВНАЯ БИБЛИОТЕКА СЖАТИЯ (libzstd) — откуда она берётся на ЭТОЙ машине.
///
/// ЧТО ЭТО ЗА ЗАВИСИМОСТЬ (разобрано по сборкам игры, а не предположено).
/// Сервер шлёт крупные пакеты сжатыми zstd — в том числе реестр блоков.
/// Разжимает их код самой игры: <c>Vintagestory.Common.Compression</c> →
/// <c>CompressionZSTD</c> → <c>Vintagestory.Common.Convert.ZstdNative</c>.
/// Последний — восемь P/Invoke (ZSTD_createDCtx, ZSTD_decompressDCtx и прочие)
/// в модуль с именем <c>libzstd</c>, внутри VintagestoryLib.dll. Ни в каком
/// пакете NuGet этого не заменить: подменять было бы нечего — вызовы объявлены
/// ВНУТРИ игровой сборки, и чистый .NET-порт (ZstdSharp.Port) до них не
/// достаёт. Через него же идут и данные чанков, а значит и свет.
///
/// КАК ИГРА ЕЁ ИЩЕТ. У <c>ZstdNative</c> есть свой резолвер, поставленный в
/// статическом конструкторе (<c>NativeLibrary.SetDllImportResolver</c>).
/// Разобранный порядок поиска:
///   1. только на Linux: <c>libzstd.so.1</c> — то есть СИСТЕМНЫЙ пакет;
///   2. только на Linux: <c>libzstd</c> — обычный поиск загрузчика;
///   3. везде: <c>&lt;папка запущенной программы&gt;/Lib/libzstd.{dll|so|dylib}</c>;
///   4. иначе IntPtr.Zero — дальше решает .NET (на Windows это PATH).
///
/// Свой резолвер поставить ПОВЕРХ нельзя: .NET разрешает ровно один на сборку,
/// и второй вызов роняет игровой статический конструктор
/// («A resolver is already set for the assembly») — проверено делом. Значит,
/// единственная дверь для Windows и macOS — пункт 3, папка Lib рядом с ботом.
///
/// ОТСЮДА РЕШЕНИЕ. Раньше нужную библиотеку клала СБОРКА, и потому собрать
/// бота под Linux с Windows-машины было нечем: в установке игры под Windows
/// лежит только .dll. Но игра обязана стоять на той машине, ГДЕ БОТ
/// ЗАПУСКАЕТСЯ (иначе неоткуда взять её сборки), — а там лежит библиотека
/// РОВНО ТОЙ системы. Поэтому берём её при запуске, а не при сборке: копия
/// делается один раз, рядом с ботом, и сборка под все системы идёт с любой
/// машины.
/// </summary>
public static class GameNatives
{
    /// <summary>Имя модуля, как оно записано в P/Invoke игры.</summary>
    public const string LibraryName = "libzstd";

    /// <summary>Что делать, когда известно только про файлы.</summary>
    public enum Plan
    {
        /// <summary>Уже лежит рядом с ботом — трогать нечего.</summary>
        Готово,
        /// <summary>Взять из установленной игры.</summary>
        Копировать,
        /// <summary>Своей копии не будет, но на Linux игра сама возьмёт системную.</summary>
        НадеятьсяНаСистему,
        /// <summary>Взять негде и запасного пути нет.</summary>
        Негде,
    }

    /// <summary>Чем кончилось дело: <see cref="Ready"/> — проверено загрузкой, а не обещано.</summary>
    public readonly record struct Report(bool Ready, string Message)
    {
        public override string ToString() => Message;
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: что делать, зная три факта. Вынесено отдельно, чтобы
    /// решение можно было проверить тестом, не трогая ни файлов, ни системы.
    /// </summary>
    public static Plan Decide(bool besideBot, bool inGame, bool linux) =>
        besideBot ? Plan.Готово
        : inGame ? Plan.Копировать
        : linux ? Plan.НадеятьсяНаСистему   // игра пробует libzstd.so.1 сама
        : Plan.Негде;

    /// <summary>Путь, по которому игра ищет библиотеку рядом с запущенной программой.</summary>
    public static string BesidePath(string programDir) =>
        Path.Combine(programDir, "Lib", LibraryName + GameFolders.NativeExtension);

    /// <summary>Путь к библиотеке внутри установленной игры.</summary>
    public static string InGamePath(string gamePath) =>
        Path.Combine(gamePath, "Lib", LibraryName + GameFolders.NativeExtension);

    /// <summary>
    /// Положить библиотеку туда, где игра её ищет, и ПРОВЕРИТЬ ЗАГРУЗКОЙ.
    ///
    /// Проверка настоящая: пробуем поднять библиотеку ровно в том порядке, в
    /// каком это будет делать сам игровой резолвер. «Файл скопирован» ещё не
    /// значит «загрузится» — битый или чужой разрядности файл именно так и
    /// выглядит.
    /// </summary>
    /// <param name="programDir">Папка запущенной программы (то же, что видит игра).</param>
    /// <param name="gamePath">Папка установленной игры или null.</param>
    /// <param name="linux">Считать систему Linux (у неё есть запасной путь).</param>
    /// <param name="tryLoad">Чем пробовать загрузку — подменяется в тестах.</param>
    public static Report Ensure(string programDir, string? gamePath, bool linux,
                                Func<string, bool> tryLoad)
    {
        string beside = BesidePath(programDir);
        string? inGame = gamePath is null ? null : InGamePath(gamePath);

        var plan = Decide(File.Exists(beside), inGame != null && File.Exists(inGame), linux);
        string copied;

        switch (plan)
        {
            case Plan.Готово:
                copied = $"библиотека сжатия уже лежит рядом с ботом: {beside}";
                break;

            case Plan.Копировать:
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(beside)!);
                    File.Copy(inGame!, beside, overwrite: true);
                    copied = $"библиотека сжатия взята из игры: {inGame} → {beside}";
                }
                catch (Exception e)
                {
                    // Обычная причина — папка бота только для чтения (/opt, /usr/local).
                    // Молчать нельзя: без библиотеки бот не войдёт в мир
                    copied = $"не удалось положить библиотеку сжатия рядом с ботом " +
                             $"({e.GetType().Name}: {e.Message}); искал в игре: {inGame}";
                }
                break;

            case Plan.НадеятьсяНаСистему:
                copied = gamePath is null
                    ? "игры не нашёл, своей копии libzstd не будет — надежда на системный пакет"
                    : $"в установке игры нет {inGame} — надежда на системный пакет";
                break;

            default:
                copied = gamePath is null
                    ? $"игры не нашёл, и рядом с ботом нет {beside}"
                    : $"нет ни {beside}, ни {inGame}";
                break;
        }

        // ПРОВЕРКА ДЕЛОМ — тем же порядком, каким пойдёт игровой резолвер
        foreach (string candidate in LoadOrder(beside, linux))
            if (tryLoad(candidate))
                return new Report(true, $"{copied}; загрузилась: {candidate}");

        return new Report(false,
            $"{copied}. Сжатие НЕ РАБОТАЕТ: сервер шлёт реестр блоков сжатым, " +
            $"и бот не войдёт в мир. " +
            (linux
                ? "Поставьте системный пакет (Debian/Ubuntu: apt install libzstd1; " +
                  "Fedora: dnf install libzstd) или положите libzstd.so рядом с ботом в папку Lib."
                : $"Положите {LibraryName + GameFolders.NativeExtension} " +
                  $"из установленной игры (папка Lib) рядом с ботом в папку Lib."));
    }

    /// <summary>
    /// Порядок, в котором игровой резолвер пробует поднять библиотеку.
    /// Держится здесь одним списком, чтобы проверка и жизнь не разошлись.
    /// </summary>
    public static IEnumerable<string> LoadOrder(string besidePath, bool linux)
    {
        if (linux)
        {
            yield return LibraryName + ".so.1";   // системный пакет — первым, как у игры
            yield return LibraryName;
        }
        yield return besidePath;
    }

    /// <summary>Настоящий запуск: настоящая система, настоящая загрузка.</summary>
    public static Report Ensure(string? gamePath) =>
        Ensure(AppDomain.CurrentDomain.BaseDirectory, gamePath,
               RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
               static name => NativeLibrary.TryLoad(name, out _));

    /// <summary>Чем кончилась проверка при запуске — её печатает запуск бота.</summary>
    public static Report Last { get; internal set; } =
        new(false, "проверка библиотеки сжатия ещё не выполнялась");
}
