using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace VsBotKit;

/// <summary>
/// Файл сессии: чтобы не спрашивать пароль при каждом запуске.
///
/// ЭТО УЧЁТНЫЕ ДАННЫЕ. В файле лежит ключ сессии, а он даёт полный доступ к
/// игровому аккаунту — не меньше пароля. Поэтому: пароль здесь не хранится
/// никогда, файл кладётся с правами «только владельцу», а в журнал уходит имя
/// игрока, но не ключ.
/// </summary>
public sealed class SessionStore
{
    /// <summary>Имя файла по умолчанию — рядом с ботом, как и его настройки.</summary>
    public const string DefaultFileName = "botsession.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    /// <summary>Куда положен файл сессии.</summary>
    public string Path { get; }

    public SessionStore(string? path = null)
    {
        string p = path ?? DefaultFileName;
        Path = System.IO.Path.IsPathRooted(p)
            ? System.IO.Path.GetFullPath(p)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(BotFolders.State(), p));
    }

    public event Action<string>? OnLog;

    /// <summary>Прочитать сохранённую сессию. null — файла нет или он испорчен.</summary>
    public VsSession? Load()
    {
        if (!File.Exists(Path))
            return null;
        try
        {
            var session = JsonSerializer.Deserialize<VsSession>(
                File.ReadAllText(Path, Encoding.UTF8), Json);
            return session is { Usable: true } ? session : null;
        }
        catch (Exception e)
        {
            // Не молчим и не падаем: испорченный файл — повод войти заново,
            // а не повод гадать, почему бота не пускают
            OnLog?.Invoke($"файл сессии не прочитался ({e.Message}) — нужен вход заново");
            return null;
        }
    }

    /// <summary>Сохранить сессию, закрыв файл от чужих глаз.</summary>
    public void Save(VsSession session)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(session, Json), new UTF8Encoding(false));
        Protect(Path);
        OnLog?.Invoke($"сессия сохранена: {session.PlayerName} ({Path})");
    }

    /// <summary>Забыть сессию: файл удаляется.</summary>
    public void Forget()
    {
        if (File.Exists(Path))
            File.Delete(Path);
        OnLog?.Invoke("сессия забыта");
    }

    /// <summary>
    /// Закрыть файл от других пользователей машины.
    ///
    /// На Linux и macOS это делается правами 600 — иначе ключ сессии лежит
    /// доступным всем, кто есть на машине. На Windows отдельного шага не
    /// нужно: файл наследует права папки профиля.
    /// </summary>
    private static void Protect(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Файловая система может не уметь права (сетевой диск, FAT).
            // Это не повод не работать, но и молчать об этом не будем
        }
    }

    /// <summary>
    /// Взять сессию из настроек САМОЙ ИГРЫ (clientsettings.json).
    ///
    /// Зачем. Если человек уже вошёл в игру на этой машине, спрашивать у него
    /// пароль второй раз незачем: сессия уже есть, лежит в его собственном
    /// файле и в открытом виде. Бот берёт оттуда только имя, uid и ключ.
    ///
    /// Это делается ТОЛЬКО по прямой просьбе (см. настройку account.fromGame):
    /// молча читать чужие ключи библиотека не должна.
    /// </summary>
    public static VsSession? FromGameSettings(string? file = null)
    {
        file ??= System.IO.Path.Combine(GameFolders.DataFolder(), "clientsettings.json");
        if (!File.Exists(file))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("stringSettings", out var s))
                return null;

            string? Get(string key) =>
                s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;

            var session = new VsSession
            {
                PlayerName = Get("playername") ?? "",
                PlayerUid = Get("playeruid") ?? "",
                SessionKey = Get("sessionkey") ?? "",
                SessionSignature = Get("sessionsignature") ?? "",
                Email = Get("useremail") ?? "",
                Entitlements = Get("entitlements") ?? ""
            };
            return session.Usable ? session : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Вход бота под аккаунтом — от начала до конца.
///
/// Порядок такой же, как у самой игры, и выбран не случайно:
/// 1. есть сохранённая сессия — проверяем её у сервера аккаунтов;
/// 2. годна — пароль не нужен вовсе;
/// 3. не годна — просим пароль у того, кто нас позвал (консоль, панель,
///    переменная окружения) и входим заново;
/// 4. попросили код двухфакторной проверки — спрашиваем и повторяем.
///
/// ПАРОЛЬ ЗДЕСЬ НЕ ЖИВЁТ. Он приходит вызовом, уходит в один HTTP-запрос и
/// забывается: ни в поле, ни в файле, ни в журнале его нет. Сохраняется только
/// ключ сессии, и то заботами <see cref="SessionStore"/>.
/// </summary>
public sealed class BotAccount
{
    private readonly VsAuth auth;
    private readonly SessionStore store;

    public BotAccount(VsAuth? auth = null, SessionStore? store = null)
    {
        this.auth = auth ?? new VsAuth();
        this.store = store ?? new SessionStore();
        this.auth.OnLog += m => OnLog?.Invoke(m);
        this.store.OnLog += m => OnLog?.Invoke(m);
    }

    public event Action<string>? OnLog;

    /// <summary>Сессия, под которой работаем (null — входа не было).</summary>
    public VsSession? Session { get; private set; }

    /// <summary>Где лежит файл сессии — чтобы человек знал, что беречь.</summary>
    public string SessionPath => store.Path;

    /// <summary>
    /// Войти. <paramref name="askPassword"/> и <paramref name="askCode"/>
    /// зовутся, только если без них никак; вернут null — вход честно
    /// прекращается, а не крутится в попытках.
    /// </summary>
    public async Task<AuthResult> SignInAsync(string email,
        Func<Task<string?>>? askPassword = null,
        Func<string, Task<string?>>? askCode = null,
        CancellationToken ct = default)
    {
        // 1. Сохранённая сессия: самый частый и самый дешёвый случай
        if (store.Load() is { } saved)
        {
            var check = await auth.ValidateAsync(saved, ct);
            if (check.Ok)
            {
                Session = saved;
                store.Save(saved);   // привилегии могли обновиться
                return new AuthResult(AuthOutcome.Ok, $"вошёл по сохранённой сессии: {saved.PlayerName}");
            }
            if (check.Outcome == AuthOutcome.Offline)
            {
                // Сервер аккаунтов молчит. Пробуем сессией как есть: если она
                // жива, игровой сервер её примет, а если нет — откажет внятно
                Session = saved;
                OnLog?.Invoke($"{check.Message} — пробую сохранённой сессией");
                return new AuthResult(AuthOutcome.Ok, "сервер аккаунтов молчит, иду сохранённой сессией");
            }
            OnLog?.Invoke(check.Message);
            store.Forget();
        }

        // 2. Пароль. Спрашиваем ровно один раз и не запоминаем
        if (askPassword == null)
            return new AuthResult(AuthOutcome.SessionExpired,
                "сохранённой сессии нет, а спросить пароль некому");
        if (string.IsNullOrWhiteSpace(email))
            return new AuthResult(AuthOutcome.WrongPassword, "не задана почта аккаунта");

        string? password = await askPassword();
        if (string.IsNullOrEmpty(password))
            return new AuthResult(AuthOutcome.WrongPassword, "пароль не введён — вход отменён");

        var (result, session) = await auth.LoginAsync(email, password, ct: ct);

        // 3. Двухфакторность: тот же запрос, но с кодом и промежуточным токеном
        if (result.Outcome is AuthOutcome.NeedTotp or AuthOutcome.NeedEmailCode && askCode != null)
        {
            string? code = await askCode(result.Message);
            if (string.IsNullOrWhiteSpace(code))
                return new AuthResult(result.Outcome, "код не введён — вход отменён", result.Reason);
            (result, session) = await auth.LoginAsync(email, password, code.Trim(),
                result.PreLoginToken ?? "", ct);
        }

        if (!result.Ok || session == null)
            return result;

        Session = session;
        store.Save(session);
        return result;
    }

    /// <summary>Войти сессией, взятой из настроек установленной игры.</summary>
    public async Task<AuthResult> SignInFromGameAsync(CancellationToken ct = default)
    {
        if (SessionStore.FromGameSettings() is not { } fromGame)
            return new AuthResult(AuthOutcome.SessionExpired,
                "в настройках игры сохранённой сессии нет — похоже, в игру никто не входил");

        var check = await auth.ValidateAsync(fromGame, ct);
        if (!check.Ok && check.Outcome != AuthOutcome.Offline)
            return check;

        Session = fromGame;
        store.Save(fromGame);
        return new AuthResult(AuthOutcome.Ok,
            $"взял сессию из настроек игры: {fromGame.PlayerName}");
    }

    /// <summary>
    /// Сколько отказов сервера аккаунтов подряд терпеть, прежде чем бросить
    /// переподключаться.
    ///
    /// Без этого счётчика выходило вот что: сессия протухла, токен не выдают,
    /// сервер игры не пускает, бот переподключается — и так СУТКАМИ с паузой
    /// в две минуты, ни разу не попробовав войти заново и ни разу не сказав,
    /// что дело в сессии. Отказ по аккаунту ничем не отличался от «сервер
    /// перезапускают», а разница между ними принципиальная.
    /// </summary>
    public int MaxAuthFailures { get; set; } = 2;

    private int authFailures;

    /// <summary>Сессия признана негодной — дальше нужен вход с паролем.</summary>
    public event Action<string>? OnSessionLost;

    /// <summary>
    /// Привязать аккаунт к боту: настоящее имя, настоящий uid и добыча
    /// одноразового токена на каждое подключение.
    /// </summary>
    public void Attach(BotClient client)
    {
        if (Session is not { Usable: true } session)
            return;

        authFailures = 0;
        client.UseAccount(session.PlayerName, session.PlayerUid);
        client.MpTokenProvider = async (serverToken, ct) =>
        {
            var (result, token) = await auth.RequestMpTokenAsync(session, serverToken, ct);
            if (result.Ok)
            {
                authFailures = 0;
                return token;
            }

            OnLog?.Invoke(result.Message);

            // Сервер аккаунтов молчит — это может пройти само, и повторы тут
            // осмысленны. А вот «сессия негодна» само не пройдёт
            if (result.Outcome == AuthOutcome.SessionExpired &&
                ++authFailures >= MaxAuthFailures)
            {
                store.Forget();
                Session = null;
                client.AutoReconnect = false;
                string why = "сессия больше не годится, а стучаться в сервер без токена " +
                             "бессмысленно — нужен вход заново (окно управления или " +
                             "перезапуск с паролем)";
                OnLog?.Invoke(why);
                OnSessionLost?.Invoke(why);
            }
            return token;
        };
    }

    /// <summary>Забыть сессию: следующий запуск попросит пароль.</summary>
    public void SignOut()
    {
        Session = null;
        store.Forget();
    }
}
