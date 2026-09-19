using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

// Утилиты игры берём ТОЧЕЧНО. Тянуть весь Vintagestory.API.Common нельзя:
// там свой Func<>, и он конфликтует с системным (та же оговорка в Hands.cs).
using SerializerUtil = Vintagestory.API.Util.SerializerUtil;

namespace VsBotKit;

// ======================================================================
//  ПАМЯТЬ ВСТРЕЧ
// ======================================================================

/// <summary>
/// Запись об одном человеке: кого видел, когда впервые, когда в последний раз,
/// сколько раз и где.
///
/// ЭТО ЛИЧНЫЕ ДАННЫЕ ДРУГОГО ЧЕЛОВЕКА, и полей здесь ровно столько, сколько
/// заказано: имя, время, место. Ни во что играл, ни что нёс в руках, ни с кем
/// стоял рядом — сюда не пишется и не будет: файл лежит на чужом компьютере,
/// а человек о нём не знает и согласия не давал. По той же причине книга
/// встреч НИКУДА НЕ ОТДАЁТСЯ — ни в чат, ни ИИ-фасаду, ни наружу с машины.
///
/// ЕДИНСТВЕННОЕ ИСКЛЮЧЕНИЕ И ЕГО ГРАНИЦЫ (заказ 11.08: «где бот, где дом, где
/// запомненные места — руда, встречи»). Карта в окне управления умеет показать
/// место встречи, и ровно при трёх условиях сразу:
///   • это отдельный выключатель (<see cref="PanelConfig.MapPeople"/>), он
///     ВЫКЛЮЧЕН по умолчанию и отделён от выключателя самой карты — иначе
///     человек включал бы чужие имена, желая посмотреть, где его бот;
///   • уходит ТОЛЬКО <see cref="Name"/> и клетка. <see cref="Times"/>,
///     <see cref="FirstSeen"/>, <see cref="LastSeen"/> и
///     <see cref="LastGreeted"/> не уходят никуда и никогда: «сколько раз
///     виделись» и «когда» — это уже слежка, а не карта;
///   • окно живёт на петле (127.0.0.1) за своей дверью и никуда ничего не
///     отправляет; включение пишется в журнал отдельной громкой строкой.
/// Обрезка полей сделана в одном месте (<c>ControlPanel.MeetingPlaces</c>) и
/// сторожится тестом <c>PanelMapWiringTests</c> — при попытке вынести наружу
/// время или счёт встреч он краснеет.
/// </summary>
public sealed class Meeting
{
    /// <summary>Имя игрока, как его показывает сервер.</summary>
    public string Name { get; set; } = "";

    /// <summary>Когда увидел впервые (UTC).</summary>
    public DateTime FirstSeen { get; set; }

    /// <summary>Когда видел в последний раз (UTC).</summary>
    public DateTime LastSeen { get; set; }

    /// <summary>Сколько РАЗ встречались (не сколько тиков видел, см. MeetingBook.Seen).</summary>
    public int Times { get; set; }

    /// <summary>Когда здоровался в последний раз (null — ещё ни разу).</summary>
    public DateTime? LastGreeted { get; set; }

    /// <summary>Где виделись в последний раз — клетка мира.</summary>
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    public override string ToString() =>
        $"{Name}: раз {Times}, последний {LastSeen:yyyy-MM-dd HH:mm} в ({X}, {Y}, {Z})";
}

/// <summary>
/// Книга встреч: кого бот видел и когда с кем здоровался.
///
/// Правила («пора ли здороваться», «это новая встреча или та же самая»,
/// «кого забыть первым») вынесены в СТАТИЧЕСКИЕ функции и закрыты тестами:
/// цена ошибки здесь не «медленно», а «бот машет одному и тому же человеку
/// каждые полсекунды» — ровно то, за что живого игрока молча заносят в чёрный
/// список.
///
/// Это механизм. Кому и как часто здороваться, решает роль настройками.
/// </summary>
public sealed class MeetingBook
{
    private readonly Dictionary<string, Meeting> byName = new(StringComparer.Ordinal);

    /// <summary>
    /// Все записи. ЦЕЛИКОМ наружу отдавать нельзя — ни в чат, ни ИИ-фасаду, ни
    /// в ответ окна: единственное исключение и его границы описаны у
    /// <see cref="Meeting"/> (карта окна берёт отсюда ИМЯ И КЛЕТКУ и ничего
    /// больше, и только по отдельному выключателю).
    /// </summary>
    public IReadOnlyCollection<Meeting> All => byName.Values;

    public int Count => byName.Count;

    /// <summary>Что-то изменилось с последней записи на диск.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Пометить книгу записанной (зовёт хранилище после Save).</summary>
    public void Saved() => Dirty = false;

    /// <summary>Запись про человека или null.</summary>
    public Meeting? Find(string name) =>
        byName.TryGetValue(name, out var m) ? m : null;

    /// <summary>Положить готовую запись (чтение с диска).</summary>
    public void Put(Meeting meeting)
    {
        if (meeting.Name is { Length: > 0 })
            byName[meeting.Name] = meeting;
    }

    /// <summary>Забыть человека. Возвращает, был ли он в книге.</summary>
    public bool Forget(string name)
    {
        bool had = byName.Remove(name);
        if (had)
            Dirty = true;
        return had;
    }

    // ---------------- чистые правила ----------------

    /// <summary>
    /// Пора ли здороваться с ЭТИМ человеком.
    ///
    /// Заказчик просил «раз в 10 минут НА ЧЕЛОВЕКА»: общий на всех перерыв
    /// означал бы, что второй подошедший остаётся без приветствия вовсе, —
    /// со стороны это выглядит как «бот со мной не здоровается».
    ///
    /// Отдельно про часы назад: если запись оказалась ИЗ БУДУЩЕГО (перевели
    /// системное время, файл приехал с другой машины), разность отрицательная
    /// и обычная проверка «прошло ли достаточно» не сработает НИКОГДА. Тогда
    /// честнее поздороваться: молчать вечно из-за кривых часов — хуже.
    /// </summary>
    public static bool TimeToGreet(DateTime? lastGreeted, DateTime now, TimeSpan cooldown)
    {
        if (lastGreeted is not { } was)
            return true;                 // ещё ни разу
        if (now < was)
            return true;                 // запись из будущего — часы переставили
        return now - was >= cooldown;
    }

    /// <summary>
    /// Считать ли это НОВОЙ встречей.
    ///
    /// Способности тикают дважды в секунду, и без этого правила «сколько раз
    /// виделись» превращается в «сколько тиков человек стоял рядом»: постоял
    /// у прилавка минуту — счётчик сто двадцать. Встреча считается новой, если
    /// человека не было видно дольше <paramref name="gap"/>.
    /// </summary>
    public static bool StartsNewMeeting(DateTime lastSeen, DateTime now, TimeSpan gap) =>
        now < lastSeen || now - lastSeen >= gap;

    /// <summary>
    /// Кого забыть — в порядке забывания.
    ///
    /// Порядок такой:
    /// 1) сперва все, кого не видели дольше <paramref name="maxAge"/> — книга
    ///    не архив, а память о том, кто ходит по этому серверу СЕЙЧАС;
    /// 2) если и после этого записей больше <paramref name="limit"/>, забываем
    ///    «давних и редких»: сначала по давности последней встречи, при равной
    ///    давности — тех, с кем виделись реже, при равном ещё и этом — по имени
    ///    (чтобы порядок был предсказуем, а не зависел от порядка в словаре).
    ///
    /// Почему давность важнее числа встреч: предел существует ради того, чтобы
    /// файл не рос без края на людном сервере, а завсегдатай всё равно вернётся
    /// в книгу при следующей же встрече.
    /// </summary>
    public static List<Meeting> Forgotten(IEnumerable<Meeting> all, DateTime now,
        TimeSpan maxAge, int limit)
    {
        var list = all.ToList();
        var doomed = new List<Meeting>();

        if (maxAge > TimeSpan.Zero)
        {
            // Записи из будущего (кривые часы) не считаем старыми: их не за что
            // выбрасывать, а now - LastSeen у них отрицательное
            doomed.AddRange(list.Where(m => now - m.LastSeen >= maxAge));
            list = list.Except(doomed).ToList();
        }

        if (limit > 0 && list.Count > limit)
        {
            var extra = list
                .OrderBy(m => m.LastSeen)
                .ThenBy(m => m.Times)
                .ThenBy(m => m.Name, StringComparer.Ordinal)
                .Take(list.Count - limit);
            doomed.AddRange(extra);
        }
        return doomed;
    }

    // ---------------- работа с книгой ----------------

    /// <summary>
    /// Записать, что видел человека. Возвращает запись и признак «встреча
    /// новая» (по <see cref="StartsNewMeeting"/>).
    /// </summary>
    public (Meeting Record, bool NewMeeting) Seen(string name, DateTime now,
        int x, int y, int z, TimeSpan newMeetingGap)
    {
        if (!byName.TryGetValue(name, out var m))
        {
            m = new Meeting { Name = name, FirstSeen = now, LastSeen = now, Times = 1, X = x, Y = y, Z = z };
            byName[name] = m;
            Dirty = true;
            return (m, true);
        }

        bool fresh = StartsNewMeeting(m.LastSeen, now, newMeetingGap);
        if (fresh)
            m.Times++;
        m.LastSeen = now;
        m.X = x;
        m.Y = y;
        m.Z = z;
        // Пишем на диск не каждый тик, но и не «когда-нибудь»: время последней
        // встречи и место меняются постоянно, поэтому книга грязная всегда,
        // когда человек рядом. За частотой записи следит хранилище
        Dirty = true;
        return (m, fresh);
    }

    /// <summary>Пора ли здороваться с этим человеком (перерыв — на человека).</summary>
    public bool ShouldGreet(string name, DateTime now, TimeSpan cooldown) =>
        TimeToGreet(Find(name)?.LastGreeted, now, cooldown);

    /// <summary>Отметить, что поздоровались.</summary>
    public void Greeted(string name, DateTime now)
    {
        if (!byName.TryGetValue(name, out var m))
        {
            m = new Meeting { Name = name, FirstSeen = now, LastSeen = now, Times = 1 };
            byName[name] = m;
        }
        m.LastGreeted = now;
        Dirty = true;
    }

    /// <summary>Применить забывание. Возвращает, скольких забыли.</summary>
    public int Tidy(DateTime now, TimeSpan maxAge, int limit)
    {
        var doomed = Forgotten(byName.Values, now, maxAge, limit);
        foreach (var m in doomed)
            byName.Remove(m.Name);
        if (doomed.Count > 0)
            Dirty = true;
        return doomed.Count;
    }
}

/// <summary>
/// Книга встреч на диске: botmeetings.json там же, где botsession.json
/// (путь считает <see cref="BotFolders.State"/>).
///
/// Зачем файл: заказчик просил «память встреч», а бот перезапускается после
/// каждой правки роли. Без файла «видимся впервые» случалось бы по десять раз
/// за вечер, и приветствие раз в десять минут превратилось бы в приветствие
/// после каждого перезапуска.
///
/// Формат нарочно плоский и бедный — см. оговорку о личных данных в
/// <see cref="Meeting"/>. Читатель этого файла (человек, антивирус, бэкап)
/// должен видеть, что там нет ничего, кроме имени, времени и точки.
/// </summary>
public sealed class MeetingStore
{
    /// <summary>Имя файла по умолчанию — по образцу botsession.json и botjobs.json.</summary>
    public const string DefaultFileName = "botmeetings.json";

    /// <summary>Версия формата: чужую читать не берёмся, а говорим об этом.</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// Единственные поля, которые вообще бывают в этом файле.
    ///
    /// Список нужен не для разбора, а для проверки тестом: если кто-нибудь
    /// однажды допишет в <see cref="Meeting"/> «что было в руках» или «с кем
    /// стоял», тест ПОКРАСНЕЕТ, и разговор о чужих личных данных состоится
    /// до того, как файл уедет на чужой компьютер.
    /// </summary>
    public static readonly string[] AllowedFields =
        ["name", "firstSeen", "lastSeen", "times", "lastGreeted", "x", "y", "z"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Что на диске: версия и записи.</summary>
    private sealed class FileShape
    {
        public int Version { get; set; } = FormatVersion;
        public List<Meeting> Meetings { get; set; } = new();
    }

    /// <summary>Куда положен файл.</summary>
    public string Path { get; }

    /// <summary>Что случилось с файлом — словами. Молчания тут быть не должно.</summary>
    public event Action<string>? OnLog;

    public MeetingStore(string? path = null)
    {
        string p = path is { Length: > 0 } ? path : DefaultFileName;
        Path = System.IO.Path.IsPathRooted(p)
            ? System.IO.Path.GetFullPath(p)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(BotFolders.State(), p));
    }

    /// <summary>
    /// Прочитать книгу. Файла нет — пустая книга МОЛЧА: это первый запуск.
    /// Всё остальное (битый файл, чужая версия) — пустая книга И жалоба
    /// с причиной: тихий ноль означал бы «я никого не встречал», хотя встречал
    /// и просто не прочитал.
    /// </summary>
    public MeetingBook Load()
    {
        if (!File.Exists(Path))
            return new MeetingBook();

        string text;
        try
        {
            text = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"книга встреч {Path} не прочиталась ({e.Message}) — начинаю с пустой");
            return new MeetingBook();
        }

        var (book, problems) = Read(text);
        foreach (string p in problems)
            OnLog?.Invoke($"книга встреч ({System.IO.Path.GetFileName(Path)}): {p}");
        return book;
    }

    /// <summary>
    /// Разбор текста — отдельно от диска, чтобы закрыть его тестами.
    /// </summary>
    public static (MeetingBook Book, IReadOnlyList<string> Problems) Read(string? text)
    {
        var problems = new List<string>();
        var book = new MeetingBook();

        if (text is not { Length: > 0 } || text.AsSpan().Trim().IsEmpty)
        {
            problems.Add("файл пуст — начинаю с пустой книги");
            return (book, problems);
        }

        FileShape? shape;
        try
        {
            shape = JsonSerializer.Deserialize<FileShape>(text, Json);
        }
        catch (Exception e)
        {
            problems.Add($"файл испорчен ({e.Message}) — начинаю с пустой книги");
            return (book, problems);
        }

        if (shape == null)
        {
            problems.Add("файл разобрался в пустоту — начинаю с пустой книги");
            return (book, problems);
        }

        if (shape.Version != FormatVersion)
        {
            problems.Add($"версия файла {shape.Version}, а я умею только {FormatVersion} — " +
                         "читать не берусь, чтобы не выдумать чужие встречи");
            return (new MeetingBook(), problems);
        }

        int skipped = 0;
        foreach (var m in shape.Meetings)
        {
            if (m.Name is not { Length: > 0 })
            {
                skipped++;
                continue;
            }
            // Записи «ниоткуда» чиним, а не выбрасываем: имя — единственное,
            // без чего запись бессмысленна
            if (m.FirstSeen == default) m.FirstSeen = m.LastSeen;
            if (m.LastSeen == default) m.LastSeen = m.FirstSeen;
            if (m.Times <= 0) m.Times = 1;
            book.Put(m);
        }
        if (skipped > 0)
            problems.Add($"записей без имени: {skipped} — пропущены");

        book.Saved();
        return (book, problems);
    }

    /// <summary>Как выглядит книга на диске (та же строка, что уйдёт в файл).</summary>
    public static string Write(MeetingBook book) =>
        JsonSerializer.Serialize(
            new FileShape
            {
                Version = FormatVersion,
                // Порядок в файле — по имени: иначе каждая запись переставляет
                // строки местами, и в системе контроля версий (а бота держат
                // в папке проекта) файл «меняется» целиком без причины
                Meetings = book.All.OrderBy(m => m.Name, StringComparer.Ordinal).ToList()
            }, Json);

    /// <summary>
    /// Записать книгу. Сначала во временный файл, потом подменой: пропадёт
    /// питание посреди записи — старая книга останется целой, а не превратится
    /// в половину файла.
    /// </summary>
    public void Save(MeetingBook book)
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);

        string temp = Path + ".tmp";
        File.WriteAllText(temp, Write(book), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
        book.Saved();
    }
}

// ======================================================================
//  ЭМОЦИИ
// ======================================================================

/// <summary>
/// Эмоции игрока: помахать, кивнуть, поклониться.
///
/// КАК ЭТО УСТРОЕНО В ИГРЕ (проверено по её же сборкам, а не по догадке):
/// - отдельного пакета «сделай эмоцию» у клиента НЕТ. Эмоция — это СЕРВЕРНАЯ
///   команда чата: VSEssentials, ModSystemEmotes.StartServerSide регистрирует
///   <c>/emote &lt;имя&gt;</c> с правом <c>Privilege.chat</c>;
/// - список допустимых имён сервер берёт у типа сущности:
///   <c>Properties.Attributes["emotes"]</c>. У игрока это
///   assets/game/entities/humanoid/player.json →
///   <c>emotes: ["wave", "cheer", "shrug", "cry", "nod", "facepalm", "bow",
///   "laugh", "rage"]</c>. Своего списка мы не выдумываем: те же типы сущностей
///   сервер шлёт нам пакетом реестра (ServerAssets, поле Entities), оттуда
///   и читаем;
/// - ЭМОЦИЯ ТРЕБУЕТ СВОБОДНОЙ ПРАВОЙ РУКИ. Дословно из команды: если предмет
///   в правой руке имеет анимацию удержания (GetHeldTpIdleAnimation != null),
///   сервер отвечает «Only with free hands» и НИЧЕГО не делает. Горящий факел
///   такую анимацию имеет (torch.json: heldRightTpIdleAnimationbyType
///   "*-lit-*": "holdinglanternrighthand"), фонарь тоже
///   ("holdinglanternrighthand"). То есть «махать с фонарём в правой руке»
///   игра не разрешает никому — ни боту, ни человеку;
/// - УСПЕХ ВИДЕН ПО ФАКТУ. Приняв команду, сервер рассылает ВСЕМ (в том числе
///   нам самим) Packet_EntityPacket с packetId 197 и именем эмоции внутри
///   (ServerMain.BroadcastEntityPacket → Packet_Server.Id = 67). Именно по
///   этому пакету EntityPlayer.OnReceivedServerPacket запускает анимацию.
///   Поэтому «помахал» здесь означает «сервер разослал мою эмоцию», а не
///   «я отправил строчку в чат».
///
/// Это механизм. Кому и когда махать — дело роли и способности.
/// </summary>
public sealed class Emotes
{
    private readonly BotContext ctx;

    /// <summary>Пакет «сыграй эмоцию» (Entity.OnReceivedServerPacket, ветка 197).</summary>
    public const int PlayEmotePacketId = 197;

    /// <summary>Код типа сущности игрока в реестре сервера.</summary>
    public const string PlayerEntityCode = "player";

    private string[] known = [];
    private readonly object gate = new();
    private DateTime confirmedAt = DateTime.MinValue;
    private string confirmedName = "";
    private DateTime lastChatAt = DateTime.MinValue;
    private string lastChatText = "";

    public Emotes(BotContext ctx)
    {
        this.ctx = ctx;
        ctx.Bot.OnPacket += HandlePacket;
        ctx.Bot.OnChat += HandleChat;
        // ТИПЫ СУЩНОСТЕЙ РАЗБИРАЕТ ОДИН МЕХАНИЗМ НА ВЕСЬ ПРОЕКТ
        // (см. <see cref="EntityTypes"/>). Здесь стоял ВТОРОЙ разбор того же
        // пакета 19 — со своим «отрезать game:» и своим перебором типов, — и
        // когда бою понадобилось спросить у реестра «стрела это или тварь»,
        // копий стало бы три
        ctx.EntityTypes.OnReady += (_, _) => TakeEmotesFromRegistry();
        TakeEmotesFromRegistry();   // реестр мог прийти и до нас
    }

    /// <summary>
    /// Забрать список жестов из реестра типов. Молча пустой он не бывает:
    /// когда сервер прислал тип игрока без списка, об этом видно по Known.
    /// </summary>
    private void TakeEmotesFromRegistry()
    {
        var found = ReadEmotes(ctx.EntityTypes.Get(PlayerEntityCode)?.Attributes);
        lock (gate)
            known = found;
        if (found.Length > 0)
            OnLog?.Invoke($"сервер знает эмоции игрока: {string.Join(", ", found)}");
    }

    public event Action<string>? OnLog;

    /// <summary>Эмоция ушла и сервер её разослал (имя эмоции).</summary>
    public event Action<string>? OnEmoted;

    /// <summary>
    /// Какие эмоции знает СЕРВЕР для игрока. Пусто — реестр сущностей ещё
    /// не пришёл или в нём нет списка: тогда проверять нам нечем, и мы просто
    /// пробуем, а отказ приходит от сервера.
    /// </summary>
    public IReadOnlyList<string> Known
    {
        get { lock (gate) return known; }
    }

    /// <summary>Сколько ждать рассылки своей эмоции, секунд.</summary>
    public double ConfirmSeconds { get; set; } = 2.5;

    /// <summary>
    /// Освобождать руку, если предмет в ней мешает эмоции. Выключить это —
    /// значит гарантированно получать «Only with free hands» с факелом в руке.
    /// </summary>
    public bool FreeHandIfNeeded { get; set; } = true;

    // ---------------- чистые правила ----------------

    /// <summary>
    /// Вытащить список эмоций из атрибутов типа сущности.
    ///
    /// Отдельной функцией — чтобы закрыть тестом: формат тут не наш, а игры
    /// (JSON-атрибуты сущности), и сломается он молча.
    /// </summary>
    public static string[] ReadEmotes(string? attributesJson)
    {
        if (attributesJson is not { Length: > 0 })
            return [];
        try
        {
            var attrs = Newtonsoft.Json.Linq.JObject.Parse(attributesJson);
            if (attrs.GetValue("emotes", StringComparison.OrdinalIgnoreCase)
                is not Newtonsoft.Json.Linq.JArray list)
                return [];
            return list.Select(t => t.ToObject<string>())
                       .Where(s => s is { Length: > 0 })
                       .Select(s => s!)
                       .ToArray();
        }
        catch
        {
            return [];   // нестандартные атрибуты — просто нечего проверять
        }
    }

    /// <summary>
    /// Помешает ли предмет в руке эмоции. Повторяем проверку сервера
    /// (ModSystemEmotes: GetHeldTpIdleAnimation для правой руки), но по ПОЛЮ
    /// предмета из реестра: чужие поведения предметов у нас не исполняются,
    /// поэтому наш ответ — это «наверняка помешает», а не «наверняка не
    /// помешает». Настоящий отказ всё равно приходит от сервера.
    /// </summary>
    public bool BlocksEmote(string? heldCode) =>
        heldCode is { Length: > 0 } &&
        ctx.World.GetCollectible(heldCode)?.HeldRightTpIdleAnimation is { Length: > 0 };

    // ---------------- дело ----------------

    /// <summary>
    /// Сделать эмоцию. Правда — сервер её РАЗОСЛАЛ (пакет 197 про нашу
    /// сущность). Ложь — всегда с причиной вслух.
    /// </summary>
    public async Task<bool> EmoteAsync(string emote, CancellationToken ct = default)
    {
        if (emote is not { Length: > 0 })
        {
            OnLog?.Invoke("эмоция без имени — нечего показывать");
            return false;
        }

        var list = Known;
        if (list.Count > 0 && !list.Contains(emote, StringComparer.OrdinalIgnoreCase))
        {
            OnLog?.Invoke($"эмоции «{emote}» у игрока нет. Сервер знает: {string.Join(", ", list)}");
            return false;
        }

        // Рука. Сервер молча ничего не сделает и ответит текстом, поэтому
        // проще освободить руку заранее — как это делает живой игрок
        if (BlocksEmote(ctx.Hands.Held?.Code))
        {
            string held = ctx.Hands.Held?.Code ?? "?";
            if (!FreeHandIfNeeded)
            {
                OnLog?.Invoke($"с {held} в руке игра эмоции не даёт (нужна свободная рука)");
                return false;
            }
            if (!await ctx.Hands.FreeHandAsync(ct) || BlocksEmote(ctx.Hands.Held?.Code))
            {
                OnLog?.Invoke($"руку под эмоцию не освободить (в руке {held}) — " +
                              "игра требует свободную");
                return false;
            }
        }

        DateTime mark = DateTime.UtcNow;
        await ctx.Bot.SendChatAsync($"/emote {emote}");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0.1, ConfirmSeconds));
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            lock (gate)
                if (confirmedAt >= mark)
                {
                    string name = confirmedName;
                    OnEmoted?.Invoke(name.Length > 0 ? name : emote);
                    return true;
                }
            await Task.Delay(40, ct).ContinueWith(_ => { });
        }

        // Не дождались рассылки. Сервер на отказ отвечает СЛОВАМИ в чат
        // («Only with free hands», «Choose emote: ...») — эту строку и
        // показываем: она объясняет причину лучше любой нашей догадки
        string reason;
        lock (gate)
            reason = lastChatAt >= mark && lastChatText.Length > 0
                ? $"сервер ответил: {lastChatText}"
                : "сервер ничего не разослал и ничего не ответил " +
                  "(нет мода эмоций или нет права chat)";
        OnLog?.Invoke($"эмоция «{emote}» не прошла: {reason}");
        return false;
    }

    /// <summary>Новая сессия: список эмоций придёт заново вместе с реестром.</summary>
    public void Reset()
    {
        lock (gate)
        {
            known = [];
            confirmedAt = DateTime.MinValue;
            confirmedName = "";
            lastChatAt = DateTime.MinValue;
            lastChatText = "";
        }
    }

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 67: // EntityPacket — им же рассылается эмоция
                if (p.EntityPacket is not { } ep || ep.Packetid != PlayEmotePacketId)
                    break;
                if (ep.EntityId != ctx.Entities.OwnEntityId)
                    break;   // чужая эмоция: чужие жесты нас не подтверждают
                string name = "";
                try
                {
                    if (ep.Data is { Length: > 0 })
                        name = SerializerUtil.Deserialize<string>(ep.Data) ?? "";
                }
                catch
                {
                    // Имя эмоции нужно только для журнала. Сам факт рассылки
                    // подтверждён уже тем, что пакет про НАШУ сущность пришёл
                }
                lock (gate)
                {
                    confirmedAt = DateTime.UtcNow;
                    confirmedName = name;
                }
                break;
        }
    }

    private void HandleChat(ChatMessage m)
    {
        lock (gate)
        {
            lastChatAt = DateTime.UtcNow;
            lastChatText = m.PlainText;
        }
    }
}

// ======================================================================
//  ПРИВЕТСТВИЕ
// ======================================================================

/// <summary>
/// Поздороваться с игроком, которого видишь.
///
/// Дословно от заказчика: «Махать эмоцией в приветствии игроку которого
/// видешь раз в 10 минут». Перерыв — НА ЧЕЛОВЕКА: общий на всех означал бы,
/// что второй подошедший остаётся без приветствия.
///
/// ЧЕСТНОЕ ЗРЕНИЕ. Здороваемся только с тем, кого ВИДНО: сервер шлёт нам
/// позиции всех игроков в округе, включая тех, кто за стеной дома, и махать
/// им — это показать, что бот видит сквозь стены (правило 4). Луч считает
/// тот же <see cref="Sight"/>, что и руки, только дальность у него своя:
/// здороваются издали, а не с расстояния вытянутой руки.
/// </summary>
public class BehaviorGreetPlayers : BotBehavior
{
    private readonly BotContext ctx;
    private readonly Emotes emotes;
    private readonly MeetingBook book;
    private readonly MeetingStore? store;
    private readonly Sight sight;

    private DateTime lastSaveAt = DateTime.MinValue;
    private DateTime lastTidyAt = DateTime.MinValue;
    private bool warnedNoName;

    /// <summary>
    /// Чем было занято тело, когда мы в последний раз сказали «здороваюсь
    /// позже». Пусто — про нынешнее занятие ещё не говорили.
    /// </summary>
    private string waitingOn = "";

    public BehaviorGreetPlayers(BotContext ctx, Emotes emotes, MeetingBook book,
        MeetingStore? store = null)
    {
        this.ctx = ctx;
        this.emotes = emotes;
        this.book = book;
        this.store = store;
        sight = new Sight(ctx);
    }

    /// <summary>Выключатель: роль может гасить способность, не убирая её из списка.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>За сколько блоков замечаем человека и здороваемся.</summary>
    public double Radius { get; set; } = 12;

    /// <summary>Как часто здороваться С ОДНИМ И ТЕМ ЖЕ человеком, минут.</summary>
    public double GreetEveryMinutes { get; set; } = 10;

    /// <summary>
    /// Через сколько минут разлуки встреча считается НОВОЙ (иначе «сколько раз
    /// виделись» станет счётчиком тиков).
    /// </summary>
    public double NewMeetingAfterMinutes { get; set; } = 10;

    /// <summary>Какой эмоцией здороваться. Имя проверяется по реестру сервера.</summary>
    public string Emote { get; set; } = "wave";

    /// <summary>
    /// Если эмоция не прошла (нет мода эмоций, нет права chat) — поздороваться
    /// СЛОВОМ. Это не жест, и в журнале так и говорится.
    /// </summary>
    public bool SayHelloWhenNoEmote { get; set; } = true;

    /// <summary>Чем здороваться словом. {0} — имя человека.</summary>
    public string HelloText { get; set; } = "Привет, {0}!";

    /// <summary>Требовать прямую видимость человека (см. оговорку у класса).</summary>
    public bool RequireLineOfSight { get; set; } = true;

    /// <summary>
    /// Сколько ближайших людей осматривать за тик. Луч зрения не бесплатный,
    /// а на людной площади в радиусе стоят десятки.
    /// </summary>
    public int WatchAtMost { get; set; } = 5;

    /// <summary>Не записывать книгу на диск чаще, чем раз в столько секунд.</summary>
    public double SaveEverySeconds { get; set; } = 30;

    /// <summary>Забывать тех, кого не видели дольше стольких суток (0 — не забывать по сроку).</summary>
    public double ForgetAfterDays { get; set; } = 30;

    /// <summary>Верхний предел записей в книге (0 — без предела).</summary>
    public int RememberAtMost { get; set; } = 500;

    /// <summary>Поздоровался: имя и чем именно («эмоция wave» / «словом»).</summary>
    public event Action<string, string>? OnGreeted;

    /// <summary>Увидел человека: запись и признак «встреча новая».</summary>
    public event Action<Meeting, bool>? OnMet;

    /// <summary>Поздороваться не вышло: имя и причина.</summary>
    public event Action<string, string>? OnGreetFailed;

    /// <summary>Забыл столько-то давних записей.</summary>
    public event Action<int>? OnForgot;

    /// <summary>
    /// Стоит ли ГОВОРИТЬ про то, что здороваться сейчас некогда.
    ///
    /// Живой случай, за который взялись: пятнадцать одинаковых строк за минуту —
    /// «„приветствие“ не берёт тело: занято „команда комне“». Приветствие
    /// тикает дважды в секунду и каждый раз лезло за телом, а очередь на тело
    /// глушит повтор всего на три секунды — вот и двадцать строк в минуту про
    /// одно и то же. Ждать надо МОЛЧА, но не немо: занятие называется один раз,
    /// и второй раз — только когда занятие СМЕНИЛОСЬ (был «комне», стал
    /// «карьер»). Молчать вовсе нельзя: человек стоит перед носом, и «почему
    /// бот мне не машет» должно иметь ответ в журнале.
    ///
    /// Чистое правило: ни времени, ни состояния — только «о чём уже сказали»
    /// и «чем занято сейчас».
    /// </summary>
    public static bool WorthSayingWait(string saidAbout, string busyNow) =>
        busyNow.Length > 0 && !string.Equals(saidAbout, busyNow, StringComparison.Ordinal);

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || ctx.Self.IsDead || ctx.Entities.Self is not { } self)
            return false;

        // Тело освободилось — забываем, о чём жаловались: следующее занятие
        // (даже названное так же) заслуживает своей строки
        if (ctx.Turn.Busy.Length == 0)
            waitingOn = "";

        Tidy();

        var now = DateTime.UtcNow;
        var newMeetingGap = TimeSpan.FromMinutes(Math.Max(0, NewMeetingAfterMinutes));
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, GreetEveryMinutes));

        // Кандидаты: игроки в радиусе, ближайшие первыми
        var people = ctx.Entities.Nearby(self.X, self.Y, self.Z, Radius)
            .Where(e => e.IsPlayer && e.Id != ctx.Entities.OwnEntityId)
            .Take(Math.Max(1, WatchAtMost))
            .ToList();

        EntityInfo? greet = null;
        string greetName = "";
        bool sawUnnamed = false;
        foreach (var person in people)
        {
            if (person.PlayerName is not { Length: > 0 } name)
            {
                // Имени нет — сервер ещё не прислал нашлёпку. Запомнить такого
                // нельзя (память ведётся по имени), и молчать об этом нельзя.
                // Но и повторять каждые полсекунды незачем: жалоба одна на всё
                // время, пока рядом есть безымянный
                sawUnnamed = true;
                if (!warnedNoName)
                {
                    warnedNoName = true;
                    OnGreetFailed?.Invoke($"#{person.Id}", "сервер ещё не прислал его имя");
                }
                continue;
            }

            if (!CanSee(person))
                continue;

            var (record, fresh) = book.Seen(name, now,
                (int)Math.Floor(person.X), (int)Math.Floor(person.Y), (int)Math.Floor(person.Z),
                newMeetingGap);
            OnMet?.Invoke(record, fresh);

            if (greet == null && MeetingBook.TimeToGreet(record.LastGreeted, now, cooldown))
            {
                greet = person;
                greetName = name;
            }
        }

        if (!sawUnnamed)
            warnedNoName = false;

        SaveIfDue(now);

        if (greet == null)
            return false;

        // ЖДЁМ МОЛЧА, А НЕ ДОЛБИМСЯ. Приветствие — самое неважное дело
        // (Routine), поэтому любой живой хозяин тела его не пустит: лезть за
        // телом каждые полсекунды значит только засорять журнал отказами.
        // Спрашиваем ЗАРАНЕЕ, чем занято тело, и один раз называем занятие.
        //
        // Человек при этом не теряется: перерыв приветствия ему не ставится,
        // и как только тело освободится, бот помашет тому же самому — тому,
        // кто всё это время стоял и ждал
        if (ctx.Turn.Busy is { Length: > 0 } busy)
        {
            if (WorthSayingWait(waitingOn, busy))
                OnGreetFailed?.Invoke(greetName,
                    $"поздороваюсь позже: тело занято «{busy}»");
            waitingOn = busy;
            return false;
        }

        // Приветствие — дело на секунду-другую (повернуться, махнуть), поэтому
        // спрашиваем очередь на тело: воевать за руку с добычей нельзя.
        // Между проверкой выше и этой строкой тело мог занять кто-то другой —
        // тогда отказ придёт от очереди, но это редкий случай, а не каждый тик
        using var hold = ctx.Turn.TryTake("приветствие", BodyArbiter.Importance.Routine, ct);
        if (hold == null)
            return false;

        await ctx.Movement.FaceAsync(greet.X, greet.Z, hold.Token);

        if (await emotes.EmoteAsync(Emote, hold.Token))
        {
            book.Greeted(greetName, DateTime.UtcNow);
            SaveNow();
            OnGreeted?.Invoke(greetName, $"эмоция {Emote}");
            return true;
        }

        if (!SayHelloWhenNoEmote)
        {
            OnGreetFailed?.Invoke(greetName, $"эмоция «{Emote}» не прошла, а словом здороваться не велено");
            // Перерыв всё равно ставим: долбиться эмоцией каждые полсекунды —
            // это тот же спам, только в журнале
            book.Greeted(greetName, DateTime.UtcNow);
            SaveNow();
            return false;
        }

        // Подстановка имени РУЧНАЯ, а не через string.Format: текст приходит из
        // настроек роли, то есть от человека, и лишняя фигурная скобка в нём
        // уронила бы способность целиком вместо приветствия
        // ВЕЖЛИВОСТЬ, А НЕ ОТЧЁТ: радиотишина глушит рассказы бота о своих
        // делах, а не «здравствуйте». Молчащий в ответ на встречу бот выглядит
        // не живее, а мертвее — выключить приветствие можно ручкой роли
        // «МахатьИгрокам», и второй ручки для того же заводить незачем
        await ctx.Bot.SendChatAsync(HelloText.Replace("{0}", greetName),
            reason: SayReason.Вежливость);
        book.Greeted(greetName, DateTime.UtcNow);
        SaveNow();
        // Называем своим именем: это НЕ жест, а слово — эмоция не прошла
        OnGreeted?.Invoke(greetName, "словом (эмоция не прошла)");
        return true;
    }

    /// <summary>
    /// Видно ли человека честно. Целимся в клетку его головы: ноги бывают
    /// за забором, а голова видна — и наоборот.
    /// </summary>
    private bool CanSee(EntityInfo person)
    {
        if (!RequireLineOfSight)
            return true;
        sight.Range = Radius;
        var head = new BlockPos(
            (int)Math.Floor(person.X),
            (int)Math.Floor(person.Y + 1.6),   // высота глаз игрока из player.json
            (int)Math.Floor(person.Z));
        var look = sight.LookAt(head);
        // Реестр блоков ещё не пришёл — проверять нечем; тогда верим глазам
        // сервера, как это делают и руки (см. Hands.InReach)
        return !look.Known || look.Visible;
    }

    private void Tidy()
    {
        if (DateTime.UtcNow - lastTidyAt < TimeSpan.FromMinutes(5))
            return;
        lastTidyAt = DateTime.UtcNow;
        int forgot = book.Tidy(DateTime.UtcNow,
            TimeSpan.FromDays(Math.Max(0, ForgetAfterDays)), RememberAtMost);
        if (forgot > 0)
            OnForgot?.Invoke(forgot);
    }

    private void SaveIfDue(DateTime now)
    {
        if (store == null || !book.Dirty)
            return;
        if (now - lastSaveAt < TimeSpan.FromSeconds(Math.Max(1, SaveEverySeconds)))
            return;
        SaveNow();
    }

    private void SaveNow()
    {
        if (store == null || !book.Dirty)
            return;
        lastSaveAt = DateTime.UtcNow;
        try
        {
            store.Save(book);
        }
        catch (Exception e)
        {
            // Молчать нельзя: без файла память живёт до перезапуска, и
            // «здороваюсь как впервые» после каждой правки роли объясняется
            // именно этим
            OnGreetFailed?.Invoke("книга встреч", $"не записалась: {e.Message}");
        }
    }
}
