using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace VsBotKit;

// ═══════════════════════════════════════════════════════════════════════════
//  СВОЯ ПАМЯТЬ ПРО ТРАНСЛОКАТОРЫ: «был тут → стал там»
//
//  ЗАКАЗ ДОСЛОВНО (17.08): «мы же сейчас их берем с вебкарты, а если вебкарты
//  нет у нас, то мы можем сами запоминать какие пары у тл и куда идут? так
//  можно было делать точно».
//
//  ОН ПРАВ, И ВОТ ЧЕМ ЭТО ДОКАЗЫВАЕТСЯ. Бот узнаёт пару САМ, без всякой карты,
//  и узнаёт двумя разными способами:
//
//  1. СЛОВОМ СЕРВЕРА. Блок-сущность StaticTranslocator приезжает вместе с
//     чанком и несёт в себе адрес выхода — teleX/teleY/teleZ при canTele
//     (см. TranslocatorMap.Read и BlockEntityStaticTranslocator
//     .FromTreeAttributes в VSSurvivalMod). То есть, стоя рядом с пластиной,
//     бот ЗНАЕТ, куда она ведёт, — и знал всегда. Беда была одна: знание жило
//     ровно столько, сколько чанк лежал в памяти. Отошёл на двести блоков —
//     сервер выгрузил чанк, и вместе с ним ушла пара; перезапустился — ушло всё.
//
//  2. НОГАМИ. Встал на пластину, дождался переноса, посмотрел, где очутился, —
//     это уже не слово, а ФАКТ. Проверка «туда ли перенесло» и так делается
//     (TranslocatorRules.CheckArrival), и её ответ — готовая запись в память.
//
//  ПАРА У ВАНИЛИ ВЗАИМНА, И ЭТО ПРОВЕРЕНО ПО СБОРКЕ, А НЕ ПО ПАМЯТИ.
//  BlockEntityStaticTranslocator.exitChunkLoaded, связывая концы, ставит адрес
//  СРАЗУ ОБОИМ: дальнему — «tpLocation = мой Pos, canTeleport = true», себе —
//  «tpLocation = exitPos». Поэтому узнанное в одну сторону кладётся в сеть
//  парой, как это делает и карта сервера. Но ЗАПОМИНАЕМ мы направление, в
//  котором убедились: «проверено ногами туда» и «сказано блоком» — разные
//  степени доверия, и путать их нельзя.
//
//  ЧЕГО ЗДЕСЬ НЕТ. Второго механизма пути через телепорты: он есть
//  (Teleporting.cs, Trek), и ему нужен был не второй путь, а ВТОРОЙ ИСТОЧНИК
//  ЗНАНИЯ. Карта сервера остаётся источником, когда она есть; эта память —
//  когда её нет, и добавкой, когда есть.
//
//  ЧЕСТНОСТЬ ЭТИХ ДВУХ ИСТОЧНИКОВ РАЗНАЯ, И МОЛЧАТЬ ОБ ЭТОМ НЕЛЬЗЯ.
//  Пройденное ногами честно целиком: живой игрок помнит, куда его вынесло,
//  ровно так же. А вот адрес из блок-сущности игрок увидеть НЕ МОЖЕТ — сервер
//  шлёт его вместе с чанком, и это ровно тот случай, что уже записан открытым
//  в ГОТОВНОСТЬ.md («знание, недоступное игроку — частично: адрес
//  транслокатора… остались»). Здесь это НЕ ЛЕЧИТСЯ и не усугубляется: адрес
//  читает TranslocatorMap.Destination, и читал его до этой памяти — она лишь
//  перестаёт терять уже прочитанное. Что записано ногами, а что со слов
//  сервера, видно в каждой записи (<see cref="TlPairSeen.ByFeet"/>) и в файле:
//  решать, годится ли второе, — не дело механизма.
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// ОДНА ЗАПОМНЕННАЯ ПАРА: где вход, куда ведёт, когда проверено и чем.
///
/// Координаты — МИРОВЫЕ клетки, те самые, которыми бот живёт внутри. Не
/// игровые и не картовые: сдвиг координат приходит только вместе с точкой
/// спавна, уже в мире, а запоминать бот начинает раньше — с первого же
/// увиденного транслокатора. Перевод в игровые делает тот, кому они нужны
/// (<see cref="TranslocatorRouting"/>), и делает в одном месте.
/// </summary>
/// <param name="From">Клетка пластины, на которую надо встать.</param>
/// <param name="To">Клетка пластины, куда переносит.</param>
/// <param name="CheckedUtc">Когда это подтвердилось в последний раз.</param>
/// <param name="ByFeet">
/// ПРОВЕРЕНО НОГАМИ: бот встал и очутился там. Ложь — знаем со слов
/// блок-сущности, то есть с сервера, но сами не ходили. Разница не
/// косметическая: слово блока верно на миг чтения, а пройденный переход —
/// это то, что уже случилось.
/// </param>
/// <param name="Times">Сколько раз подтверждалось.</param>
public readonly record struct TlPairSeen(BlockPos From, BlockPos To,
    DateTime CheckedUtc, bool ByFeet, int Times)
{
    /// <summary>Как это прочитает человек.</summary>
    public override string ToString() =>
        $"{From} → {To} ({(ByFeet ? "прошёл сам" : "со слов сервера")}, " +
        $"проверено {CheckedUtc.ToLocalTime():dd.MM HH:mm}" +
        (Times > 1 ? $", {Times} раз" : "") + ")";
}

/// <summary>
/// ПАМЯТЬ ПАР ТРАНСЛОКАТОРОВ. Механизм и только он: ни чисел политики, ни
/// сети, ни диска (диск — у <see cref="TlMemoryStore"/>).
///
/// КЛЮЧ — КЛЕТКА ВХОДА. Двух транслокаторов в одной клетке не бывает, а вот
/// один и тот же вход сервер вполне может перепривязать: тогда новая запись
/// вытесняет старую, и это правильно — свежая правда старше несвежей.
///
/// ПОЧЕМУ ЕСТЬ ПОТОЛОК. Бот живёт неделями и за это время проходит мимо сотен
/// пластин. Память без берега — утечка; когда тесно, уходит самое старое, ровно
/// как в <see cref="ResourceMemory"/>. Но потолок здесь большой нарочно: одна
/// запись весит десятки байт, а потерянная пара стоит человеку похода через
/// полмира.
/// </summary>
public sealed class TlMemory
{
    private readonly object замок = new();
    private readonly Dictionary<BlockPos, TlPairSeen> пары = [];

    /// <summary>Часы — подменяются в тестах, чтобы «когда проверено» было проверяемым.</summary>
    public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

    /// <summary>Сколько пар держать. 0 и меньше — без предела.</summary>
    public int Capacity { get; set; } = 4096;

    /// <summary>Что стоит сказать вслух: узнал, поправил, забыл.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// ЗНАНИЕ ИЗМЕНИЛОСЬ: появилась пара, сменился адрес, пластина забыта.
    /// По этому и записывают файл (<see cref="VsBot"/>).
    ///
    /// Отдельно от <see cref="OnLog"/> НАРОЧНО: слова говорятся и на повторное
    /// подтверждение той же пары, а писать из-за него файл незачем.
    /// </summary>
    public event Action? OnNews;

    /// <summary>
    /// Память менялась с последней записи на диск. Тем же способом, что и книга
    /// встреч (<see cref="MeetingBook.Dirty"/>): писать файл на каждую
    /// увиденную пластину незачем, а терять узнанное при выходе нельзя.
    /// </summary>
    public bool Dirty { get; private set; }

    /// <summary>Сколько пар помним.</summary>
    public int Count
    {
        get { lock (замок) return пары.Count; }
    }

    /// <summary>Всё, что помним, — как есть (для файла, окна и отчётов).</summary>
    public IReadOnlyList<TlPairSeen> All
    {
        get { lock (замок) return [.. пары.Values]; }
    }

    /// <summary>Память записана на диск — считать её чистой.</summary>
    public void Saved()
    {
        lock (замок) Dirty = false;
    }

    /// <summary>
    /// ЧТО СИЛЬНЕЕ — чистое правило, поэтому и закрыто тестом. Отвечает на
    /// вопрос «эту запись заменять новой или нет».
    ///
    /// ПРОЙДЕННОЕ НОГАМИ НЕ ЗАТИРАЕТСЯ СЛОВОМ БЛОКА, пока адрес тот же: иначе
    /// первый же взгляд на пластину понижал бы проверенную пару до
    /// «со слов сервера», и человек, спросивший «а ты там был?», получал бы
    /// «нет», хотя бот был.
    ///
    /// А ВОТ ДРУГОЙ АДРЕС ЗАМЕНЯЕТ ВСЕГДА, даже слово блока против ног: сервер
    /// перепривязывает пары на ходу, и вчерашний пройденный путь — это уже не
    /// сегодняшняя правда.
    /// </summary>
    public static bool Replaces(TlPairSeen было, BlockPos новыйВыход, bool ногами) =>
        было.To != новыйВыход || ногами || !было.ByFeet;

    /// <summary>
    /// ЗАПОМНИТЬ ПАРУ. Возвращает true, если это НОВОЕ знание (новая пластина
    /// или сменившийся адрес) — тому, кто зовёт, это нужно, чтобы сказать
    /// вслух и записать файл; повтор увиденного молчит.
    /// </summary>
    /// <param name="byFeet">Проверено ногами (см. <see cref="TlPairSeen.ByFeet"/>).</param>
    public bool Learn(BlockPos from, BlockPos to, bool byFeet)
    {
        if (from == to)
            return false;   // сам в себя — это не переход, а точка

        bool новость = Запомнить(from, to, byFeet);
        if (новость)
            OnNews?.Invoke();
        return новость;
    }

    private bool Запомнить(BlockPos from, BlockPos to, bool byFeet)
    {
        lock (замок)
        {
            if (пары.TryGetValue(from, out var было))
            {
                if (!Replaces(было, to, byFeet))
                    return false;

                bool адресДругой = было.To != to;
                пары[from] = было with
                {
                    To = to,
                    CheckedUtc = Now(),
                    // Просто byFeet, и понижения этим не бывает: слово блока
                    // против уже пройденного отсеивает сам Replaces — сюда
                    // такой случай не доходит. Оговорка «а вдруг понизим»
                    // была бы недостижимой веткой, то есть враньём о правилах
                    ByFeet = byFeet,
                    Times = адресДругой ? 1 : было.Times + 1
                };
                Dirty = true;
                if (адресДругой)
                    OnLog?.Invoke($"транслокатор {from} вёл к {было.To}, а теперь к {to} — " +
                                  "сервер пересобрал пару, помню новое");
                else if (byFeet && !было.ByFeet)
                    OnLog?.Invoke($"прошёл {from} → {to} сам: раньше знал это только со слов сервера");
                return адресДругой;
            }

            пары[from] = new TlPairSeen(from, to, Now(), byFeet, 1);
            Dirty = true;
            OnLog?.Invoke($"запомнил пару {from} → {to} " +
                          $"({(byFeet ? "прошёл сам" : "со слов сервера")}); всего помню {пары.Count}");
            Trim();
            return true;
        }
    }

    /// <summary>Куда ведёт эта пластина по нашей памяти. null — не помним.</summary>
    public TlPairSeen? ExitOf(BlockPos from)
    {
        lock (замок)
            return пары.TryGetValue(from, out var есть) ? есть : null;
    }

    /// <summary>
    /// ЗАБЫТЬ ПЛАСТИНУ: пришли, а её нет — сломали, застроили, разобрали.
    /// Возвращает, была ли запись.
    /// </summary>
    public bool Forget(BlockPos from, string why)
    {
        TlPairSeen было;
        lock (замок)
        {
            if (!пары.Remove(from, out было))
                return false;
            Dirty = true;
        }
        OnLog?.Invoke($"забыл пару {было.From} → {было.To}: {why}");
        OnNews?.Invoke();
        return true;
    }

    /// <summary>
    /// ЗАБЫТЬ ВСЁ — другой мир, другой сервер. Молчать нельзя: со стороны
    /// «бот вдруг разучился ходить через телепорты» неотличимо от поломки.
    /// </summary>
    public int Clear(string why)
    {
        int было;
        lock (замок)
        {
            было = пары.Count;
            пары.Clear();
            if (было > 0)
                Dirty = true;
        }
        OnLog?.Invoke(было > 0
            ? $"забыл все пары транслокаторов ({было}): {why}"
            : $"забывать нечего — пар в памяти не было ({why})");
        return было;
    }

    /// <summary>Поднять память из файла (см. <see cref="TlMemoryStore"/>).</summary>
    public void Put(TlPairSeen пара)
    {
        lock (замок)
        {
            пары[пара.From] = пара;
            Trim();
        }
    }

    /// <summary>
    /// ЧТО У НАС В ПАМЯТИ — одной строкой человеку и в журнал. Всегда говорит
    /// хоть что-то: «не помню ни одной» — тоже ответ.
    /// </summary>
    public string Tell()
    {
        var все = All;
        if (все.Count == 0)
            return "своей памяти о транслокаторах нет: ни одной пары я ещё не видел";
        int ногами = 0;
        foreach (var п in все)
            if (п.ByFeet)
                ногами++;
        return $"своя память: {все.Count} пар(ы) транслокаторов, из них {ногами} прошёл сам";
    }

    /// <summary>Держать память в берегах. Звать под уже взятым замком.</summary>
    private void Trim()
    {
        if (Capacity <= 0 || пары.Count <= Capacity)
            return;
        int лишних = пары.Count - Capacity;
        var старые = пары.Values.OrderBy(п => п.CheckedUtc).Take(лишних)
            .Select(п => п.From).ToList();
        foreach (var где in старые)
            пары.Remove(где);
        Dirty = true;
        OnLog?.Invoke($"память транслокаторов полна ({Capacity} пар) — забыл {старые.Count} самых старых");
    }
}

/// <summary>
/// ПАМЯТЬ ТРАНСЛОКАТОРОВ НА ДИСКЕ: bottranslocators.json там же, где
/// botsession.json и botmeetings.json (путь считает <see cref="BotFolders.State"/>).
///
/// ЗАЧЕМ ФАЙЛ. Затем же, зачем он книге встреч: бот перезапускается после
/// каждой правки роли, а пары от этого не меняются. Без файла всё, что бот
/// узнал ногами за вечер, пропадало бы к утру — и он снова шёл бы пешком через
/// полмира мимо транслокатора, которым уже ходил.
///
/// ЧУЖОЙ МИР ЧИТАТЬ НЕ БЕРЁМСЯ. В файле записан сервер, на котором это узнано.
/// Совпал — читаем; не совпал — начинаем с пустой памяти И ГОВОРИМ ОБ ЭТОМ.
/// Молчаливое чтение чужих координат отправило бы бота стоять на пустом месте
/// за десять тысяч блоков: у другого мира и генерация другая, а числа на вид
/// приличные.
/// </summary>
public sealed class TlMemoryStore
{
    /// <summary>Имя файла — по образцу botsession.json и botmeetings.json.</summary>
    public const string DefaultFileName = "bottranslocators.json";

    /// <summary>Версия формата: чужую читать не берёмся, а говорим об этом.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Одна строка файла — нарочно плоская, чтобы её читал и человек.</summary>
    public sealed class Line
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public int ToX { get; set; }
        public int ToY { get; set; }
        public int ToZ { get; set; }
        public DateTime Checked { get; set; }
        public bool ByFeet { get; set; }
        public int Times { get; set; } = 1;
    }

    /// <summary>Что лежит на диске.</summary>
    public sealed class FileShape
    {
        public int Version { get; set; } = FormatVersion;

        /// <summary>Сервер, на котором это узнано («хост:порт»).</summary>
        public string Server { get; set; } = "";

        public List<Line> Pairs { get; set; } = [];
    }

    /// <summary>Куда положен файл.</summary>
    public string Path { get; }

    /// <summary>Чей это мир («хост:порт»). Пусто — сверять не с чем.</summary>
    public string Server { get; }

    /// <summary>Что случилось с файлом — словами. Молчания тут быть не должно.</summary>
    public event Action<string>? OnLog;

    public TlMemoryStore(string server, string? path = null)
    {
        Server = server ?? "";
        string p = path is { Length: > 0 } ? path : DefaultFileName;
        Path = System.IO.Path.IsPathRooted(p)
            ? System.IO.Path.GetFullPath(p)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(BotFolders.State(), p));
    }

    /// <summary>
    /// Прочитать память. Файла нет — пустая память МОЛЧА: это первый запуск.
    /// Всё остальное (битый файл, чужая версия, чужой сервер) — пустая память
    /// И причина словами.
    /// </summary>
    public TlMemory Load()
    {
        if (!File.Exists(Path))
            return new TlMemory();

        string текст;
        try
        {
            текст = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"память транслокаторов {Path} не прочиталась ({e.Message}) — начинаю с пустой");
            return new TlMemory();
        }

        var (память, беды) = Read(текст, Server);
        foreach (string б in беды)
            OnLog?.Invoke($"память транслокаторов ({System.IO.Path.GetFileName(Path)}): {б}");
        return память;
    }

    /// <summary>
    /// РАЗБОР ТЕКСТА — отдельно от диска, чтобы закрыть его тестами. Чистая
    /// функция: строка и имя сервера на вход, память и жалобы на выход.
    /// </summary>
    /// <param name="server">
    /// Сервер, на котором бот СЕЙЧАС. Пусто — сверять не с чем, читаем как есть
    /// и говорим об этом: «не знаю, тот ли это мир» и «мир не тот» — разное.
    /// </param>
    public static (TlMemory Memory, IReadOnlyList<string> Problems) Read(string? text, string server)
    {
        var беды = new List<string>();
        var память = new TlMemory();

        if (text is not { Length: > 0 } || text.AsSpan().Trim().IsEmpty)
        {
            беды.Add("файл пуст — начинаю с пустой памяти");
            return (память, беды);
        }

        FileShape? форма;
        try
        {
            форма = JsonSerializer.Deserialize<FileShape>(text, Json);
        }
        catch (Exception e)
        {
            беды.Add($"файл испорчен ({e.Message}) — начинаю с пустой памяти");
            return (память, беды);
        }

        if (форма == null)
        {
            беды.Add("файл разобрался в пустоту — начинаю с пустой памяти");
            return (память, беды);
        }

        if (форма.Version != FormatVersion)
        {
            беды.Add($"версия файла {форма.Version}, а я умею только {FormatVersion} — " +
                     "читать не берусь, чтобы не выдумать чужие переходы");
            return (new TlMemory(), беды);
        }

        if (server.Length > 0 && форма.Server.Length > 0 &&
            !string.Equals(форма.Server, server, StringComparison.OrdinalIgnoreCase))
        {
            беды.Add($"память записана на сервере «{форма.Server}», а я на «{server}» — " +
                     "не читаю её вовсе: в другом мире те же координаты означают другое место");
            return (new TlMemory(), беды);
        }

        if (форма.Server.Length == 0)
            беды.Add("в файле не записано, с какого он сервера — читаю, но за чужой мир не ручаюсь");

        int пропущено = 0;
        foreach (var с in форма.Pairs)
        {
            var откуда = new BlockPos(с.X, с.Y, с.Z);
            var куда = new BlockPos(с.ToX, с.ToY, с.ToZ);
            // Вход, равный выходу, — не переход. Такое в файл попасть не могло,
            // но файл правит и человек
            if (откуда == куда)
            {
                пропущено++;
                continue;
            }
            память.Put(new TlPairSeen(откуда, куда, с.Checked, с.ByFeet, Math.Max(1, с.Times)));
        }
        if (пропущено > 0)
            беды.Add($"пар, ведущих сами в себя: {пропущено} — пропущены");

        память.Saved();
        return (память, беды);
    }

    /// <summary>Как память выглядит на диске (та же строка, что уйдёт в файл).</summary>
    public static string Write(TlMemory memory, string server) =>
        JsonSerializer.Serialize(new FileShape
        {
            Version = FormatVersion,
            Server = server,
            // Порядок в файле — по координатам входа: иначе каждая новая пара
            // переставляла бы строки местами, и файл «менялся» бы целиком
            Pairs = memory.All
                .OrderBy(п => п.From.X).ThenBy(п => п.From.Y).ThenBy(п => п.From.Z)
                .Select(п => new Line
                {
                    X = п.From.X, Y = п.From.Y, Z = п.From.Z,
                    ToX = п.To.X, ToY = п.To.Y, ToZ = п.To.Z,
                    Checked = п.CheckedUtc,
                    ByFeet = п.ByFeet,
                    Times = п.Times
                }).ToList()
        }, Json);

    /// <summary>
    /// Записать память. Сначала во временный файл, потом подменой — той же
    /// осторожностью, что и книга встреч: пропадёт питание посреди записи,
    /// старая память останется целой, а не превратится в половину файла.
    /// </summary>
    public void Save(TlMemory memory)
    {
        string? папка = System.IO.Path.GetDirectoryName(Path);
        if (папка is { Length: > 0 })
            Directory.CreateDirectory(папка);

        string temp = Path + ".tmp";
        File.WriteAllText(temp, Write(memory, Server), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
        memory.Saved();
    }
}
