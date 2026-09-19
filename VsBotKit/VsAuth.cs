using System.Net.Http;
using System.Text.Json;
using Vintagestory.API.Config;

namespace VsBotKit;

/// <summary>Чем кончился разговор с сервером аккаунтов.</summary>
public enum AuthOutcome
{
    /// <summary>Всё получилось.</summary>
    Ok,
    /// <summary>Нужен код двухфакторной проверки из приложения.</summary>
    NeedTotp,
    /// <summary>Вход с нового места: код выслан на почту.</summary>
    NeedEmailCode,
    /// <summary>Неверная почта или пароль.</summary>
    WrongPassword,
    /// <summary>Слишком много попыток, временная или постоянная блокировка.</summary>
    Blocked,
    /// <summary>Аккаунт забанен в игре.</summary>
    Banned,
    /// <summary>Сессия устарела — нужен обычный вход с паролем.</summary>
    SessionExpired,
    /// <summary>До сервера аккаунтов не достучались.</summary>
    Offline,
    /// <summary>Отказ, который мы не разобрали, — текст в сообщении.</summary>
    Rejected
}

/// <summary>
/// Ответ сервера аккаунтов, переведённый на человеческий.
/// </summary>
/// <param name="Reason">
/// Код причины, как его прислал сервер («wrongtotpcode»). Нужен, чтобы роль
/// могла отличить случаи, не разбирая русский текст.
/// </param>
/// <param name="PreLoginToken">
/// Промежуточный токен: приходит вместе с требованием кода 2FA и отправляется
/// обратно вторым запросом.
/// </param>
public sealed record AuthResult(AuthOutcome Outcome, string Message, string? Reason = null,
    string? PreLoginToken = null)
{
    public bool Ok => Outcome == AuthOutcome.Ok;

    public override string ToString() => Message;
}

/// <summary>
/// Сессия аккаунта: то, чем бот представляется серверам.
///
/// ЭТО УЧЁТНЫЕ ДАННЫЕ. <see cref="SessionKey"/> даёт полный доступ к игровому
/// аккаунту — его нельзя писать в журнал, показывать в чате и класть в
/// пресеты ролей. Поэтому <see cref="ToString"/> его не печатает, а файл
/// сессии кладётся с правами «только владельцу».
/// </summary>
public sealed class VsSession
{
    /// <summary>Ник аккаунта. Сервер сверяет его СТРОГО — своё имя не подставить.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>Uid аккаунта.</summary>
    public string PlayerUid { get; set; } = "";

    /// <summary>Ключ сессии. Секрет.</summary>
    public string SessionKey { get; set; } = "";

    /// <summary>Подпись ключа: игра проверяет ею целостность своего файла.</summary>
    public string SessionSignature { get; set; } = "";

    /// <summary>Почта — нужна только для выхода из аккаунта.</summary>
    public string Email { get; set; } = "";

    /// <summary>Привилегии аккаунта («vssupporter» и прочее).</summary>
    public string Entitlements { get; set; } = "";

    /// <summary>Когда сессию получили (для отчёта человеку).</summary>
    public DateTime ObtainedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Сессия заполнена настолько, чтобы пробовать ею пользоваться.</summary>
    public bool Usable =>
        PlayerName.Length > 0 && PlayerUid.Length > 0 && SessionKey.Length > 0;

    /// <summary>Без ключа: эту строку можно писать в журнал.</summary>
    public override string ToString() =>
        $"{PlayerName} ({PlayerUid})" +
        (Entitlements.Length > 0 ? $", привилегии: {Entitlements}" : "");
}

/// <summary>
/// ВХОД ПОД АККАУНТОМ Vintage Story.
///
/// Зачем. До сих пор бот умел входить только туда, где проверка аккаунта
/// выключена (serverconfig.json, VerifyPlayerAuth = false) — то есть на свой
/// испытательный сервер. На любом настоящем сервере его отвергали, и это
/// не чинилось никакими настройками: MpToken в конфиге — строка, а сервер
/// ждёт ОДНОРАЗОВЫЙ токен, выпущенный под конкретное соединение.
///
/// КАК ЭТО УСТРОЕНО НА САМОМ ДЕЛЕ (по коду игры):
/// 1. один раз — POST /v2/gamelogin с почтой и паролем: в ответ ключ сессии,
///    uid и НИК АККАУНТА;
/// 2. на каждое подключение — сервер игры присылает пакет 77 со своим
///    одноразовым serverlogintoken, и по нему запрашивается mptokenv2:
///    POST /v2.1/clientrequestmptoken;
/// 3. этот mptokenv2 и уходит в пакете представления (Id = 1).
/// Сервер игры затем сам спрашивает у сервера аккаунтов, годен ли токен, и
/// принимает игрока, только если ник в пакете ТОЧНО равен нику аккаунта.
///
/// Никаких игровых сборок для этого не нужно: сама игра ходит сюда обычным
/// HttpClient без особых заголовков.
///
/// ПРО СЕКРЕТЫ. Пароль здесь только проходит насквозь: он не хранится, не
/// пишется в журнал и не попадает в сообщения об ошибках. Хранится лишь
/// ключ сессии, и то — заботами <see cref="SessionStore"/>.
/// </summary>
public sealed class VsAuth : IDisposable
{
    /// <summary>Сервер аккаунтов. Тот же адрес, что и у игры.</summary>
    public const string DefaultHost = "https://auth3.vintagestory.at";

    private readonly HttpClient http;
    private readonly bool ownsHttp;

    public VsAuth(HttpClient? client = null, string host = DefaultHost)
    {
        Host = host.TrimEnd('/');
        ownsHttp = client == null;
        http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public string Host { get; }

    public event Action<string>? OnLog;

    /// <summary>
    /// Версия, которой представляемся серверу аккаунтов. Берём ту же, что и
    /// игра: сервер по ней решает, годится ли клиент.
    /// </summary>
    public string GameLoginVersion { get; set; } = GameVersion.ShortGameVersion;

    /// <summary>
    /// Сколько ждать перед единственным повтором, когда сервер аккаунтов не
    /// ответил. Игра ждёт ровно столько же.
    /// </summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(900);

    public void Dispose()
    {
        if (ownsHttp)
            http.Dispose();
    }

    // ---------------- вход ----------------

    /// <summary>
    /// Войти по почте и паролю. Второй заход (с кодом 2FA) отличается только
    /// заполненными <paramref name="totpCode"/> и <paramref name="preLoginToken"/>.
    /// </summary>
    /// <returns>
    /// Итог и сессия. Сессия не null только при <see cref="AuthOutcome.Ok"/>.
    /// </returns>
    public async Task<(AuthResult Result, VsSession? Session)> LoginAsync(
        string email, string password, string totpCode = "", string preLoginToken = "",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            return (new AuthResult(AuthOutcome.WrongPassword,
                "не заполнены почта или пароль", "missingemailorpassword"), null);

        // В журнал уходит ТОЛЬКО почта. Пароль не печатается нигде и никогда
        OnLog?.Invoke($"вхожу под {email}" + (totpCode.Length > 0 ? " с кодом 2FA" : ""));

        var (doc, offline) = await PostAsync("/v2/gamelogin", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["totpcode"] = totpCode,
            ["prelogintoken"] = preLoginToken,
            ["gameloginversion"] = GameLoginVersion
        }, ct);

        if (offline is { } why)
            return (new AuthResult(AuthOutcome.Offline, why, "cantconnect"), null);

        var root = doc!.RootElement;
        if (Num(root, "valid") != 1)
        {
            string reason = Str(root, "reason") ?? "";
            var result = Explain(reason, Str(root, "reasondata"), Str(root, "prelogintoken"));
            OnLog?.Invoke($"вход не удался: {result.Message}");
            return (result, null);
        }

        var session = new VsSession
        {
            PlayerName = Str(root, "playername") ?? "",
            PlayerUid = Str(root, "uid") ?? "",
            SessionKey = Str(root, "sessionkey") ?? "",
            SessionSignature = Str(root, "sessionsignature") ?? "",
            Entitlements = Str(root, "entitlements") ?? "",
            Email = email
        };
        doc.Dispose();

        if (!session.Usable)
            return (new AuthResult(AuthOutcome.Rejected,
                "сервер аккаунтов ответил «всё в порядке», но не прислал ключ сессии"), null);

        OnLog?.Invoke($"вошёл: {session}");
        return (new AuthResult(AuthOutcome.Ok, $"вход выполнен: {session.PlayerName}"), session);
    }

    /// <summary>
    /// Годится ли сохранённая сессия. Это единственный способ узнать, что она
    /// протухла, — сама по себе она выглядит как обычная строка.
    /// </summary>
    public async Task<AuthResult> ValidateAsync(VsSession session, CancellationToken ct = default)
    {
        if (!session.Usable)
            return new AuthResult(AuthOutcome.SessionExpired, "сохранённой сессии нет");

        var (doc, offline) = await PostAsync("/clientvalidate", new Dictionary<string, string>
        {
            ["uid"] = session.PlayerUid,
            ["sessionkey"] = session.SessionKey
        }, ct);

        if (offline is { } why)
            return new AuthResult(AuthOutcome.Offline, why, "cantconnect");

        var root = doc!.RootElement;
        if (Num(root, "valid") == 1)
        {
            // Привилегии могли смениться — обновляем заодно
            if (Str(root, "entitlements") is { Length: > 0 } ent)
                session.Entitlements = ent;
            doc.Dispose();
            return new AuthResult(AuthOutcome.Ok, "сессия годна");
        }

        string reason = Str(root, "reason") ?? "";
        doc.Dispose();
        return new AuthResult(AuthOutcome.SessionExpired,
            $"сессия больше не годится ({(reason.Length > 0 ? reason : "без причины")}) — " +
            "нужен вход с паролем", reason);
    }

    /// <summary>
    /// Одноразовый токен на ОДНО подключение.
    ///
    /// Его нельзя запасти впрок: он выпускается под <paramref name="serverLoginToken"/>,
    /// который игровой сервер придумал именно для этого соединения (пакет 77).
    /// Отсюда и вся возня — статичной строкой в конфиге тут не обойтись.
    /// </summary>
    public async Task<(AuthResult Result, string? MpToken)> RequestMpTokenAsync(
        VsSession session, string serverLoginToken, CancellationToken ct = default)
    {
        if (!session.Usable)
            return (new AuthResult(AuthOutcome.SessionExpired, "сессии нет — сперва надо войти"), null);
        if (string.IsNullOrWhiteSpace(serverLoginToken))
            return (new AuthResult(AuthOutcome.Rejected,
                "сервер игры не прислал свой токен подключения"), null);

        var body = new Dictionary<string, string>
        {
            ["uid"] = session.PlayerUid,
            ["serverlogintoken"] = serverLoginToken,
            ["sessionkey"] = session.SessionKey
        };

        var (doc, offline) = await PostAsync("/v2.1/clientrequestmptoken", body, ct);

        // Один повтор при недоступности — ровно как делает игра. Сервер
        // аккаунтов иногда моргает, и терять из-за этого вход обидно
        if (offline != null)
        {
            OnLog?.Invoke("сервер аккаунтов не ответил — пробую ещё раз");
            await Task.Delay(RetryDelay, ct).ContinueWith(_ => { }, CancellationToken.None);
            (doc, offline) = await PostAsync("/v2.1/clientrequestmptoken", body, ct);
        }
        if (offline is { } why)
            return (new AuthResult(AuthOutcome.Offline, why, "cantconnect"), null);

        var root = doc!.RootElement;
        if (Num(root, "valid") != 1)
        {
            string reason = Str(root, "reason") ?? "";
            doc.Dispose();
            return (new AuthResult(AuthOutcome.SessionExpired,
                $"токен подключения не выдан ({(reason.Length > 0 ? reason : "без причины")}) — " +
                "похоже, сессия устарела", reason), null);
        }

        string? token = Str(root, "mptokenv2");
        doc.Dispose();
        if (string.IsNullOrEmpty(token))
            return (new AuthResult(AuthOutcome.Rejected,
                "сервер аккаунтов ответил «годен», но токена не прислал"), null);

        return (new AuthResult(AuthOutcome.Ok, "токен подключения получен"), token);
    }

    /// <summary>Выйти из аккаунта: сессия перестаёт быть годной.</summary>
    public async Task<AuthResult> LogoutAsync(VsSession session, CancellationToken ct = default)
    {
        if (!session.Usable || session.Email.Length == 0)
            return new AuthResult(AuthOutcome.Ok, "выходить не из чего");

        var (doc, offline) = await PostAsync("/gamelogout", new Dictionary<string, string>
        {
            ["email"] = session.Email,
            ["sessionkey"] = session.SessionKey
        }, ct);
        doc?.Dispose();
        return offline is { } why
            ? new AuthResult(AuthOutcome.Offline, why, "cantconnect")
            : new AuthResult(AuthOutcome.Ok, "вышел из аккаунта");
    }

    // ---------------- разбор ответов ----------------

    /// <summary>
    /// Перевести код отказа на человеческий.
    ///
    /// Это не украшательство. «Не вышло» заставляет человека гадать, а тут
    /// разница принципиальная: неверный пароль правится за секунду, бан —
    /// никак, а «нужен код 2FA» вообще не ошибка, а второй шаг входа.
    /// </summary>
    public static AuthResult Explain(string reason, string? data = null,
        string? preLoginToken = null)
    {
        string extra = string.IsNullOrEmpty(data) ? "" : $" ({data})";
        return reason switch
        {
            "missingemailorpassword" => new AuthResult(AuthOutcome.WrongPassword,
                "не заполнены почта или пароль", reason),
            "invalidemailorpassword" => new AuthResult(AuthOutcome.WrongPassword,
                "неверная почта или пароль", reason),
            "requiretotpcode" => new AuthResult(AuthOutcome.NeedTotp,
                "нужен код двухфакторной проверки из приложения", reason, preLoginToken),
            "wrongtotpcode" => new AuthResult(AuthOutcome.NeedTotp,
                "код двухфакторной проверки не подошёл", reason, preLoginToken),
            "requirelogincode" => new AuthResult(AuthOutcome.NeedEmailCode,
                "вход с нового места: код доступа выслан на почту", reason, preLoginToken),
            "wronglogincode" => new AuthResult(AuthOutcome.NeedEmailCode,
                "код доступа не подошёл; новый можно запросить примерно через час",
                reason, preLoginToken),
            "ipchanged" => new AuthResult(AuthOutcome.Offline,
                "адрес сменился посреди входа — надо просто повторить", reason, preLoginToken),
            "bruteforceprotected" => new AuthResult(AuthOutcome.Blocked,
                "слишком много неудачных попыток — ждать около двадцати минут", reason),
            "temporarilyblocked" => new AuthResult(AuthOutcome.Blocked,
                $"адрес временно заблокирован{extra} мин", reason),
            "blocked" => new AuthResult(AuthOutcome.Blocked,
                $"адрес заблокирован навсегда{extra} — нужна поддержка игры", reason),
            "banned" => new AuthResult(AuthOutcome.Banned, "аккаунт забанен", reason),
            "serverbanned" => new AuthResult(AuthOutcome.Banned,
                "аккаунт забанен на игровых серверах", reason),
            "missingaccount" or "badplayeruid" or "missingmptoken" or "missingmptokenv2" =>
                new AuthResult(AuthOutcome.SessionExpired,
                    "игровая сессия испортилась — надо войти заново", reason),
            "" => new AuthResult(AuthOutcome.Rejected,
                "сервер аккаунтов отказал и не сказал почему"),
            _ => new AuthResult(AuthOutcome.Rejected,
                $"сервер аккаунтов отказал: {reason}{extra}", reason)
        };
    }

    /// <summary>
    /// Отправить форму и разобрать ответ. Второе значение — текст беды со
    /// связью; если он не null, JSON не пришёл вовсе.
    /// </summary>
    private async Task<(JsonDocument? Doc, string? Offline)> PostAsync(string path,
        Dictionary<string, string> form, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await http.PostAsync(Host + path, content, ct);
            string text = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
                return (null, $"сервер аккаунтов ответил {(int)response.StatusCode}");
            if (text.Length == 0)
                return (null, "сервер аккаунтов ответил пустотой");

            return (JsonDocument.Parse(text), null);
        }
        catch (JsonException)
        {
            // Тело ответа НЕ показываем: в нём может оказаться что угодно,
            // включая эхо отправленных полей
            return (null, "сервер аккаунтов ответил не JSON-ом");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "сервер аккаунтов не ответил вовремя");
        }
        catch (HttpRequestException e)
        {
            return (null, $"до сервера аккаунтов не достучаться: {e.Message}");
        }
    }

    /// <summary>Строка из ответа; сервер иногда шлёт null вместо поля.</summary>
    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// Число из ответа. Сервер шлёт «valid» то числом, то строкой, то
    /// логическим — берём все три вида, иначе годная сессия читается как отказ.
    /// </summary>
    private static int Num(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out int i) ? i : 0,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String => int.TryParse(v.GetString(), out int s) ? s : 0,
            _ => 0
        };
    }
}
