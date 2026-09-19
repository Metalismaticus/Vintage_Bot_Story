using System.Text;

namespace VsBotKit;

/// <summary>
/// Настройки журнала. Отдельный тип, чтобы их можно было прочитать из конфига
/// (<see cref="BotLogConfig.ToOptions"/>) и не тащить десяток аргументов в конструктор.
/// </summary>
public sealed class BotLogOptions
{
    /// <summary>Папка журналов. Пустая строка — рядом с исполняемым файлом.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Имя основного файла. Архивы получают номер: bot.1.log, bot.2.log…</summary>
    public string FileName { get; set; } = "bot.log";

    /// <summary>
    /// Имя файла «важного». Туда попадает только то, что признано важным
    /// (см. <see cref="Important"/>) — чтобы после ночи не читать сто тысяч строк.
    /// Пусто — отдельного файла нет.
    /// </summary>
    public string ImportantFileName { get; set; } = "important.log";

    /// <summary>Порог ротации: файл больше этого — уезжает в архив. По умолчанию 8 МиБ.</summary>
    public long MaxFileBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>
    /// Сколько файлов держать всего (текущий + архивы). Самый старый удаляется.
    /// </summary>
    public int MaxFiles { get; set; } = 8;

    /// <summary>
    /// Как часто сбрасывать буфер на диск. Компромисс: чаще — меньше потеряем
    /// при падении процесса, реже — меньше дёргаем диск.
    /// </summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько строк держать в очереди максимум. Если писатель не успевает
    /// (диск занят, а бот сыплет сообщениями) — лишнее выбрасывается, но
    /// в файл уходит явная отметка о пропуске: молча терять нельзя.
    /// </summary>
    public int MaxQueuedLines { get; set; } = 20000;

    /// <summary>Формат отметки времени. Миллисекунды нужны: события идут пачками.</summary>
    public string TimeFormat { get; set; } = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Писать время по UTC (удобно сверять с логами сервера).</summary>
    public bool UseUtc { get; set; }

    /// <summary>Дублировать строки в консоль (тогда Program.cs может ничего не печатать сам).</summary>
    public bool MirrorToConsole { get; set; }

    /// <summary>
    /// Что считать важным. Это ПОЛИТИКА — библиотека не знает, что для роли
    /// «бой», «смерть» или «сделка», поэтому решение отдаётся наружу.
    /// null — важного нет, второй файл не создаётся.
    /// </summary>
    public Func<string, bool>? Important { get; set; }

    /// <summary>
    /// Готовый признак важности «строка содержит одно из слов» — чтобы роль
    /// писала свой список слов, а не лямбду. Регистр не важен.
    /// </summary>
    public static Func<string, bool> ByWords(params string[] words) =>
        line => words.Any(w => line.Contains(w, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Журнал бота в файл: подписывается на <see cref="VsBot.OnLog"/> и пишет всё
/// с отметкой времени, с ротацией по размеру и по числу файлов.
///
/// Почему так устроено:
/// - сообщения приходят из РАЗНЫХ потоков (цикл способностей, сетевой поток,
///   поток тела), поэтому запись идёт через очередь под замком, а на диск
///   пишет один выделенный поток — иначе строки перемешивались бы посимвольно;
/// - буфер сбрасывается по таймеру и обязательно при выходе процесса
///   (ProcessExit / Ctrl+C / необработанное исключение), иначе после ночного
///   прогона в файле не окажется как раз последних, самых нужных строк;
/// - «важное» дополнительно пишется во второй файл и сбрасывается на диск
///   немедленно: если процесс упадёт, событие боя/смерти уже на диске;
/// - кодировка UTF-8 (с BOM у нового файла), чтобы кириллица открывалась
///   в блокноте, а не превращалась в «Ð±Ð¾Ñ‚».
///
/// Это механизм. Что писать и что считать важным — дело роли.
/// </summary>
public sealed class BotLogFile : IDisposable
{
    private readonly BotLogOptions opt;
    private readonly object gate = new();
    // Второй замок: он делает запись на диск последовательной. Без него Flush()
    // мог вернуться, пока поток-писатель ещё дописывал свою пачку, — и вызвавший
    // получал бы «уже на диске» о том, чего на диске ещё нет (правило «не врать»).
    // Порядок захвата всегда drainGate → gate, иначе будет взаимная блокировка.
    private readonly object drainGate = new();
    private readonly Queue<(bool Important, string Line)> pending = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread writer;
    private readonly EventHandler processExit;
    private readonly UnhandledExceptionEventHandler crash;
    private readonly ConsoleCancelEventHandler cancelKey;

    private StreamWriter? main;
    private StreamWriter? important;
    private long mainBytes;
    private long importantBytes;
    private int dropped;          // сколько строк выброшено из-за переполнения очереди
    private bool stopping;
    private bool disposed;

    private VsBot? attachedTo;
    private Action<string>? attachedHandler;

    public BotLogFile(BotLogOptions? options = null)
    {
        opt = options ?? new BotLogOptions();

        Path = FullPath(opt.FileName);
        ImportantPath = string.IsNullOrWhiteSpace(opt.ImportantFileName)
            ? null
            : FullPath(opt.ImportantFileName);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        if (ImportantPath != null)
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ImportantPath)!);

        // Отметка старта: по ней в склеенном за неделю файле видно границы прогонов
        Write($"=== запуск, процесс {Environment.ProcessId} ===");

        writer = new Thread(WriterLoop)
        {
            Name = "журнал бота",
            // Фоновый: не должен держать процесс живым. Досброс делает ProcessExit
            IsBackground = true
        };
        writer.Start();

        processExit = (_, _) => Dispose();
        crash = (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                WriteImportant($"[падение] {ex}");
            Flush();
        };
        cancelKey = (_, _) => Flush();

        AppDomain.CurrentDomain.ProcessExit += processExit;
        AppDomain.CurrentDomain.UnhandledException += crash;
        Console.CancelKeyPress += cancelKey;
    }

    /// <summary>Полный путь основного файла (пригодится, чтобы сказать, куда смотреть).</summary>
    public string Path { get; }

    /// <summary>Полный путь файла «важного» или null, если он выключен.</summary>
    public string? ImportantPath { get; }

    /// <summary>Не удалось записать на диск (кончилось место, файл занят). Не молчим.</summary>
    public event Action<Exception>? OnError;

    /// <summary>
    /// Подписать журнал на поток сообщений бота. Возвращает сам журнал —
    /// его надо держать до конца работы (using) и не терять ссылку.
    /// </summary>
    public static BotLogFile Attach(VsBot bot, BotLogOptions? options = null)
    {
        var log = new BotLogFile(options);
        log.attachedTo = bot;
        log.attachedHandler = log.Write;
        bot.OnLog += log.attachedHandler;
        return log;
    }

    /// <summary>Записать строку (потокобезопасно, не блокирует вызывающего на диске).</summary>
    public void Write(string message)
    {
        if (message is null)
            return;
        bool imp = ImportantPath != null && opt.Important?.Invoke(message) == true;
        Enqueue(imp, message);
    }

    /// <summary>
    /// Записать строку и в основной файл, и в «важный», сбросив её на диск сразу.
    /// Для того, что должно пережить падение процесса.
    /// </summary>
    public void WriteImportant(string message)
    {
        if (message is null)
            return;
        Enqueue(ImportantPath != null, message);
        Signal();
    }

    /// <summary>Дописать всё накопленное на диск прямо сейчас.</summary>
    public void Flush()
    {
        lock (gate)
        {
            if (disposed && main == null && important == null)
                return;
        }
        Drain();
        lock (drainGate)
            lock (gate)
            {
                SafeFlush(main);
                SafeFlush(important);
            }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            stopping = true;
        }

        if (attachedTo != null && attachedHandler != null)
            attachedTo.OnLog -= attachedHandler;

        AppDomain.CurrentDomain.ProcessExit -= processExit;
        AppDomain.CurrentDomain.UnhandledException -= crash;
        Console.CancelKeyPress -= cancelKey;

        Enqueue(false, "=== остановка ===");
        Signal();
        // Ждём писателя недолго: журнал не имеет права задерживать выход.
        // Дописываем сами в любом случае: писатель мог уже выйти по признаку
        // остановки, и тогда последние строки остались бы в очереди
        writer.Join(TimeSpan.FromSeconds(3));
        Drain();

        lock (gate)
        {
            SafeFlush(main);
            main?.Dispose();
            main = null;
            SafeFlush(important);
            important?.Dispose();
            important = null;
        }
        wake.Dispose();
    }

    // --- внутреннее ---

    private void Enqueue(bool imp, string message)
    {
        string line = $"[{Stamp(Now())}] {message}";
        bool overflow = false;
        lock (gate)
        {
            if (pending.Count >= opt.MaxQueuedLines)
            {
                // Очередь переполнена: выбрасываем самое старое, но считаем потери
                pending.Dequeue();
                dropped++;
                overflow = true;
            }
            pending.Enqueue((imp, line));
        }
        if (overflow || imp)
            Signal();
        if (opt.MirrorToConsole)
            Console.WriteLine(line);
    }

    private void WriterLoop()
    {
        while (true)
        {
            bool last;
            lock (gate)
                last = stopping && pending.Count == 0;
            if (last)
                return;

            wake.WaitOne(opt.FlushInterval);
            Drain();
            lock (gate)
            {
                SafeFlush(main);
                SafeFlush(important);
            }
        }
    }

    /// <summary>Вынуть всё накопленное и записать. Вызывается писателем и при выходе.</summary>
    private void Drain()
    {
        lock (drainGate)
            DrainCore();
    }

    private void DrainCore()
    {
        while (true)
        {
            (bool Important, string Line)[] batch;
            int lost;
            lock (gate)
            {
                if (pending.Count == 0)
                {
                    if (dropped > 0 && (main != null || TryOpen(ref main, Path, ref mainBytes)))
                    {
                        lost = dropped;
                        dropped = 0;
                        WriteLine(false, $"[{Stamp(Now())}] [журнал] потеряно строк из-за переполнения очереди: {lost}");
                    }
                    return;
                }
                batch = pending.ToArray();
                pending.Clear();
            }

            foreach (var (imp, line) in batch)
                WriteLine(imp, line);
        }
    }

    /// <summary>Записать одну готовую строку. Только под замком или из писателя.</summary>
    private void WriteLine(bool imp, string line)
    {
        lock (gate)
        {
            int bytes = Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (TryOpen(ref main, Path, ref mainBytes))
            {
                RotateIfNeeded(ref main, Path, ref mainBytes, bytes);
                if (Emit(main, line))
                    mainBytes += bytes;
            }

            if (imp && ImportantPath != null && TryOpen(ref important, ImportantPath, ref importantBytes))
            {
                RotateIfNeeded(ref important, ImportantPath, ref importantBytes, bytes);
                if (Emit(important, line))
                {
                    importantBytes += bytes;
                    // Важное — сразу на диск: смысл файла в том, чтобы он пережил падение
                    SafeFlush(important);
                }
            }
        }
    }

    private bool Emit(StreamWriter? w, string line)
    {
        if (w == null)
            return false;
        try
        {
            w.WriteLine(line);
            return true;
        }
        catch (Exception e)
        {
            Report(e);
            return false;
        }
    }

    private bool TryOpen(ref StreamWriter? w, string path, ref long size)
    {
        if (w != null)
            return true;
        try
        {
            var info = new FileInfo(path);
            size = info.Exists ? info.Length : 0;
            // ReadWrite|Delete — чтобы журнал можно было открывать и переносить,
            // пока бот работает: иначе Windows не даст даже прочитать файл
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            // BOM пишется только у пустого файла: StreamWriter в режиме дописывания
            // не повторяет преамбулу
            w = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
            {
                AutoFlush = false
            };
            return true;
        }
        catch (Exception e)
        {
            Report(e);
            return false;
        }
    }

    /// <summary>
    /// Ротация: текущий файл закрывается, архивы сдвигаются (bot.1.log → bot.2.log…),
    /// самый старый удаляется. Всего файлов остаётся не больше MaxFiles.
    /// </summary>
    private void RotateIfNeeded(ref StreamWriter? w, string path, ref long size, int incoming)
    {
        if (opt.MaxFileBytes <= 0 || size + incoming <= opt.MaxFileBytes || size == 0)
            return;
        try
        {
            SafeFlush(w);
            w?.Dispose();
            w = null;

            int keep = Math.Max(1, opt.MaxFiles);
            for (int i = keep - 1; i >= 1; i--)
            {
                string from = Archive(path, i);
                string to = Archive(path, i + 1);
                if (!File.Exists(from))
                    continue;
                if (i + 1 >= keep)
                {
                    File.Delete(from);          // старше некуда — удаляем
                    continue;
                }
                File.Delete(to);
                File.Move(from, to);
            }
            if (keep > 1)
            {
                File.Delete(Archive(path, 1));
                File.Move(path, Archive(path, 1));
            }
            else
            {
                File.Delete(path);
            }
            size = 0;
        }
        catch (Exception e)
        {
            Report(e);
        }
        finally
        {
            TryOpen(ref w, path, ref size);
        }
    }

    private static string Archive(string path, int index)
    {
        string dir = System.IO.Path.GetDirectoryName(path) ?? "";
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        string ext = System.IO.Path.GetExtension(path);
        return System.IO.Path.Combine(dir, $"{name}.{index}{ext}");
    }

    private void SafeFlush(StreamWriter? w)
    {
        try
        {
            w?.Flush();
        }
        catch (Exception e)
        {
            Report(e);
        }
    }

    private void Report(Exception e)
    {
        // Ошибку журнала нельзя писать в журнал — уйдёт в бесконечность
        try
        {
            OnError?.Invoke(e);
        }
        catch
        {
            // Обработчик ошибок сам упал — дальше жаловаться некому
        }
    }

    private DateTime Now() => opt.UseUtc ? DateTime.UtcNow : DateTime.Now;

    private string Stamp(DateTime t) => t.ToString(opt.TimeFormat);

    /// <summary>
    /// Имя файла → полный путь: относительное имя кладём в папку журналов,
    /// относительную папку — рядом с исполняемым файлом (рабочий каталог
    /// у службы/ярлыка может быть каким угодно, а логи должны находиться).
    /// </summary>
    private string FullPath(string name)
    {
        string p = name;
        if (!System.IO.Path.IsPathRooted(p) && !string.IsNullOrWhiteSpace(opt.Directory))
            p = System.IO.Path.Combine(opt.Directory, p);
        if (!System.IO.Path.IsPathRooted(p))
            // Не «рядом с ботом», а туда, куда боту РАЗРЕШЕНО писать: на Linux
            // бота часто кладут в /opt, где журнал завести не выйдет
            p = System.IO.Path.Combine(BotFolders.State(), p);
        return System.IO.Path.GetFullPath(p);
    }

    /// <summary>Разбудить писателя. После Dispose дескриптор уже закрыт — это не беда.</summary>
    private void Signal()
    {
        try
        {
            wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
