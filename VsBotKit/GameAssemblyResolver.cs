using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VsBotKit;

/// <summary>
/// Подгружает сборки игры из папки установки Vintage Story (и её Lib),
/// чтобы бот работал без копирования игровых DLL к себе.
/// ModuleInitializer гарантирует регистрацию до загрузки любого типа
/// библиотеки (типы VsBotKit ссылаются на игровые типы уже в полях —
/// более поздняя регистрация опоздала бы).
///
/// Где именно стоит игра, спрашиваем у <see cref="GameFolders"/>: на трёх
/// системах это три разных места, и раньше здесь был зашит только путь
/// Windows. На macOS по нему нет вообще ничего, и бот падал бы ещё до
/// первой своей строки — самый неудобный вид поломки.
/// </summary>
internal static class GameAssemblyResolver
{
    /// <summary>Найденная папка игры (null — не нашли; тогда и грузить неоткуда).</summary>
    internal static string? GamePath { get; private set; }

#pragma warning disable CA2255 // намеренно: библиотека и есть точка интеграции с DLL игры
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        GamePath = GameFolders.FindGame();

        // БИБЛИОТЕКА СЖАТИЯ — ЗДЕСЬ И БОЛЬШЕ НИГДЕ.
        //
        // Раньше её клала сборка, и потому собрать бота под чужую систему было
        // нечем. Теперь она берётся из установленной игры при запуске: игра всё
        // равно обязана стоять на этой машине, и в ней лежит библиотека ровно
        // этой системы. Место одно — модульный инициализатор: пропустить его
        // нельзя ниоткуда, а отдельную строчку в запуске завтра обошли бы
        // вторым способом запустить бота (так уже было с паролем панели).
        //
        // Делается ДО первого игрового типа: статический конструктор ZstdNative
        // ищет библиотеку в момент первого сжатия, и опоздать нельзя
        GameNatives.Last = GameNatives.Ensure(GamePath);

        if (GamePath == null)
            return;   // молча: внятно об этом скажет тот, кто первым спросит игровой тип

        string libDir = Path.Combine(GamePath, "Lib");

        // Нативные библиотеки игры (libzstd и прочее) лежат в Lib.
        //
        // ТОЛЬКО НА WINDOWS. Здесь поиск нативных библиотек и правда идёт по
        // PATH. На Linux и macOS смотрят LD_LIBRARY_PATH и DYLD_*, а менять их
        // из уже запущенного процесса поздно — загрузчик читает их при старте.
        // Зато прежний код склеивал PATH точкой с запятой, и на Unix вся
        // переменная превращалась в один бессмысленный кусок: чистый вред
        // без всякой пользы. Там нужное берёт GameNatives выше.
        //
        // Это ЗАПАСНОЙ путь, а не основной: он вытаскивает и прочие нативные
        // библиотеки игры, если те вдруг понадобятся безголовому боту. Из-за
        // него же на Windows сжатие работает даже без своей копии — потому и
        // проверять «архив уедет на macOS» надо не сжатием, а наличием файла
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            char sep = Path.PathSeparator;
            Environment.SetEnvironmentVariable("PATH",
                $"{GamePath}{sep}{libDir}{sep}{Environment.GetEnvironmentVariable("PATH")}");
        }

        string[] dirs = [GamePath, libDir, Path.Combine(GamePath, "Mods")];

        AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            foreach (string dir in dirs)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            return null;
        };
    }
}
