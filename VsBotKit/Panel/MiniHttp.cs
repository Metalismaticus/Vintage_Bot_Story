using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VsBotKit.Panel;

/// <summary>Запрос от браузера, разобранный на части.</summary>
public sealed record HttpRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Query,
    IReadOnlyDictionary<string, string> Headers,
    string Body)
{
    /// <summary>Значение из строки запроса («?k=…»).</summary>
    public string? Q(string name) => Query.TryGetValue(name, out var v) ? v : null;

    /// <summary>Заголовок без учёта регистра имени.</summary>
    public string? H(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>Ответ браузеру.</summary>
public sealed class HttpAnswer
{
    public int Code { get; init; } = 200;
    public string ContentType { get; init; } = "text/plain; charset=utf-8";
    public byte[] Body { get; init; } = [];

    /// <summary>Дополнительные заголовки (например, запрет кеша).</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static HttpAnswer Json(string json, int code = 200) => new()
    {
        Code = code,
        ContentType = "application/json; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(json)
    };

    public static HttpAnswer Html(string html) => new()
    {
        ContentType = "text/html; charset=utf-8",
        Body = Encoding.UTF8.GetBytes(html)
    };

    public static HttpAnswer Text(string text, int code = 200) => new()
    {
        Code = code,
        Body = Encoding.UTF8.GetBytes(text)
    };

    /// <summary>
    /// Готовый файл, а не живое состояние: картинка предмета.
    ///
    /// ЕДИНСТВЕННОЕ, ЧТО МОЖНО КЕШИРОВАТЬ. Всё остальное окно показывает
    /// живьём, и кеш там врал бы (см. «no-store» в <see cref="MiniHttp"/>).
    /// Картинка же — файл, который человек положил рядом с ботом один раз;
    /// без кеша браузер тянул бы её заново на каждую перерисовку сетки,
    /// то есть сотню картинок каждые несколько секунд, и клетки мигали бы.
    /// Час — чтобы подложенная взамен картинка всё-таки однажды сменилась
    /// сама, без чистки кеша руками.
    /// </summary>
    public static HttpAnswer Png(byte[] bytes)
    {
        var ответ = new HttpAnswer { ContentType = "image/png", Body = bytes };
        ответ.Headers["Cache-Control"] = "private, max-age=3600";
        return ответ;
    }

    public static HttpAnswer NotFound(string what = "нет такой страницы") => Text(what, 404);

    public static HttpAnswer Denied(string why = "не тот ключ доступа") => Text(why, 403);

    public static HttpAnswer Bad(string why) => Text(why, 400);

    /// <summary>
    /// Печенька браузера. Ставится ТОЛЬКО так и с этими оговорками:
    ///   • HttpOnly — скрипт на странице пропуск не прочитает, а значит и не
    ///     утащит; открытую вкладку панели оставляют на весь вечер;
    ///   • SameSite=Strict — чужая страница не сможет постучаться в панель от
    ///     имени вошедшего (браузер-то с этой же машины);
    ///   • без Secure — панель живёт на 127.0.0.1 по обычному http, и с этим
    ///     флагом браузер печеньку просто не сохранил бы.
    /// </summary>
    public HttpAnswer WithCookie(string name, string value, int seconds)
    {
        Headers["Set-Cookie"] =
            $"{name}={value}; Path=/; Max-Age={seconds}; HttpOnly; SameSite=Strict";
        return this;
    }
}

/// <summary>
/// Крошечный HTTP-сервер для окна управления.
///
/// ПОЧЕМУ СВОЙ, А НЕ ГОТОВЫЙ. HttpListener из .NET на Windows требует прав
/// администратора на каждый адрес, кроме единственного зарезервированного —
/// этот урок уже оплачен в моде (см. VSAutoPilot/src/ModMcpServer.cs). Обычный
/// TcpListener на петле работает у всех и не просит ничего.
///
/// ТОЛЬКО ПЕТЛЯ. Слушаем 127.0.0.1 и никогда — внешний адрес: панель управляет
/// ботом, и открыть её в сеть значит отдать бота первому встречному. На
/// удалённой машине панель смотрят через «ssh -L», а не через открытый порт.
///
/// Сервер занимается ТОЛЬКО разговором по HTTP. Что отвечать — дело того, кто
/// его завёл (см. <see cref="Handle"/>).
/// </summary>
public sealed class MiniHttp : IDisposable
{
    private TcpListener? listener;
    private CancellationTokenSource? life;

    /// <summary>Порт, который просили занять.</summary>
    public int WantedPort { get; }

    /// <summary>Порт, который заняли на самом деле (0 — не запускались).</summary>
    public int Port { get; private set; }

    /// <summary>Сколько соседних портов пробовать, если нужный занят.</summary>
    public int PortsToTry { get; set; } = 10;

    /// <summary>Потолок размера тела запроса — чтобы никто не занял память.</summary>
    public int MaxBodyBytes { get; set; } = 1 << 20;

    public event Action<string>? OnLog;

    /// <summary>Что отвечать. Не задано — всё будет «нет такой страницы».</summary>
    public Func<HttpRequest, Task<HttpAnswer>>? Handle { get; set; }

    public MiniHttp(int port) => WantedPort = port;

    public bool Running => listener != null;

    /// <summary>Открыть дверь. Возвращает занятый порт.</summary>
    public int Start()
    {
        if (Running)
            return Port;

        Exception? last = null;
        for (int i = 0; i < Math.Max(1, PortsToTry); i++)
        {
            int port = WantedPort + i;
            try
            {
                var tcp = new TcpListener(IPAddress.Loopback, port);
                tcp.Start();
                listener = tcp;
                // СПРАШИВАЕМ У РОЗЕТКИ, А НЕ ПОВТОРЯЕМ ЗАДУМАННОЕ. При порте 0
                // систему просят выдать любой свободный, и она выдаёт — но
                // прежний ответ («порт, который заняли») оставался нулём, и
                // ссылка на панель получалась «http://127.0.0.1:0/». Свойство
                // так и называется, «который заняли на самом деле», и врать
                // ему нельзя ни в одном случае
                Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                if (Port != WantedPort)
                    OnLog?.Invoke(WantedPort == 0
                        ? $"порт не задан — система дала {Port}"
                        : $"порт {WantedPort} занят — встал на {Port}");
                life = new CancellationTokenSource();
                _ = Task.Run(() => AcceptAsync(life.Token));
                return Port;
            }
            catch (SocketException e)
            {
                last = e;
            }
        }
        // Честный отказ: молча не открыться значит оставить человека гадать,
        // почему страница не грузится
        throw new IOException(
            $"не удалось занять ни один порт с {WantedPort} по {WantedPort + PortsToTry - 1}: " +
            (last?.Message ?? "причина неизвестна"), last);
    }

    public void Stop()
    {
        life?.Cancel();
        try
        {
            listener?.Stop();
        }
        catch
        {
            // Дверь уже закрыта — это не беда
        }
        listener = null;
        Port = 0;
    }

    public void Dispose() => Stop();

    private async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener is { } tcp)
        {
            try
            {
                var client = await tcp.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ServeAsync(client, ct), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                OnLog?.Invoke($"приём: {e.Message}");
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using var stream = client.GetStream();

                var request = await ReadAsync(stream, ct);
                if (request == null)
                    return;

                HttpAnswer answer;
                try
                {
                    answer = Handle is { } handle
                        ? await handle(request)
                        : HttpAnswer.NotFound();
                }
                catch (Exception e)
                {
                    // Беда внутри обработчика не должна ронять дверь: панель
                    // покажет причину, и человек увидит её сразу
                    OnLog?.Invoke($"{request.Method} {request.Path}: {e.Message}");
                    answer = HttpAnswer.Text($"беда внутри бота: {e.Message}", 500);
                }

                await WriteAsync(stream, answer, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                OnLog?.Invoke($"запрос: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Прочитать запрос: строку, заголовки, потом ровно столько байт тела,
    /// сколько обещано в Content-Length.
    ///
    /// Тело читается БАЙТАМИ, а не строкой. Кириллица в UTF-8 занимает по два
    /// байта, и счёт символов вместо счёта байтов на первом же русском имени
    /// пресета обрывал бы тело посередине.
    /// </summary>
    private async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var all = new MemoryStream();
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                return null;
            all.Write(buffer, 0, read);
            if (all.Length > MaxBodyBytes)
                return null;
            headerEnd = Find(all.GetBuffer(), (int)all.Length);
        }

        byte[] raw = all.GetBuffer();
        string head = Encoding.UTF8.GetString(raw, 0, headerEnd);
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        var first = lines[0].Split(' ');
        if (first.Length < 2)
            return null;
        string method = first[0];
        string target = first[1];

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim().ToLowerInvariant()] = lines[i][(colon + 1)..].Trim();
        }

        int length = headers.TryGetValue("content-length", out var cl) &&
                     int.TryParse(cl, out int n) ? n : 0;
        if (length > MaxBodyBytes)
            return null;

        int bodyStart = headerEnd + 4;
        int have = (int)all.Length - bodyStart;
        var body = new MemoryStream();
        if (have > 0)
            body.Write(raw, bodyStart, Math.Min(have, length));

        while (body.Length < length)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read <= 0)
                break;
            body.Write(buffer, 0, (int)Math.Min(read, length - body.Length));
        }

        // Путь и строка запроса
        string path = target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int question = target.IndexOf('?');
        if (question >= 0)
        {
            path = target[..question];
            foreach (string pair in target[(question + 1)..].Split('&',
                         StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                    query[Unescape(pair[..eq])] = Unescape(pair[(eq + 1)..]);
                else
                    query[Unescape(pair)] = "";
            }
        }

        return new HttpRequest(method, Unescape(path), query, headers,
            Encoding.UTF8.GetString(body.ToArray()));

        static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        static int Find(byte[] data, int length)
        {
            for (int i = 0; i + 3 < length; i++)
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            return -1;
        }
    }

    private static async Task WriteAsync(NetworkStream stream, HttpAnswer answer,
        CancellationToken ct)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(answer.Code).Append(' ').Append(Reason(answer.Code))
            .Append("\r\n");
        head.Append("Content-Type: ").Append(answer.ContentType).Append("\r\n");
        head.Append("Content-Length: ").Append(answer.Body.Length).Append("\r\n");
        // Панель показывает живое состояние — кешировать его нельзя. Своё
        // правило кеша задаёт только тот ответ, который живым состоянием НЕ
        // является (картинка предмета, см. HttpAnswer.Png). Двух заголовков
        // Cache-Control быть не должно: браузеры разбирают такое по-разному,
        // и «no-store, max-age=3600» читалось бы то так, то этак
        if (!answer.Headers.ContainsKey("Cache-Control"))
            head.Append("Cache-Control: no-store\r\n");
        head.Append("Connection: close\r\n");
        foreach (var (name, value) in answer.Headers)
            head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");

        byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
        await stream.WriteAsync(headBytes, ct);
        if (answer.Body.Length > 0)
            await stream.WriteAsync(answer.Body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        400 => "Bad Request",
        // 401 — «войди», 429 — «подожди»: их различает страница входа, и
        // подписать оба словом «OK» значило бы соврать браузеру о том, что
        // произошло
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "OK"
    };
}
