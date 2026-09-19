using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VsBotKit.Agent;

/// <summary>
/// Бот как MCP-сервер: любой MCP-клиент (Claude Code, редактор, свой скрипт)
/// подключается к боту и водит его по миру своими руками.
///
/// Зачем это нужно на практике: РАЗРАБОТКА МОДОВ. Мод поставили на сервер,
/// а дальше нужен живой игрок, который сходит к блоку, кликнет, посмотрит, что
/// стало, и повторит это двадцать раз подряд. Бот делает это сам, а MCP-клиент
/// говорит ему, что проверять — и видит настоящий результат, а не «пакет ушёл».
///
/// Инструменты берутся из того же BotToolbox, что и у нейросети: добавили
/// инструмент — он сразу виден и роли, и MCP-клиенту. Роль может дать свои
/// (например, «открой мой сундук мода» или «дёрни мой блок-механизм»).
///
/// Протокол: JSON-RPC 2.0 построчно (по одному сообщению на строку), обмен идёт
/// через stdin/stdout процесса — это транспорт stdio, самый простой из тех,
/// что понимают MCP-клиенты. Внешних пакетов не требуется.
///
/// <code>
/// // в Program.cs при флаге --mcp
/// var mcp = new McpServer(bot, BotToolbox.Standard(bot));
/// await mcp.RunAsync();   // читает stdin, пишет stdout
/// </code>
/// В настройках MCP-клиента бот прописывается как обычная команда запуска:
/// <code>
/// { "command": "dotnet", "args": ["run", "--project", "VintageBotStory", "--", "--mcp"] }
/// </code>
/// </summary>
public sealed class McpServer
{
    private readonly VsBot bot;
    private readonly BotToolbox tools;
    private readonly TextReader input;
    private readonly TextWriter output;

    /// <summary>Как бот представляется клиенту.</summary>
    public string ServerName { get; set; } = "vsbotkit";

    /// <summary>Версия сервера (видна клиенту).</summary>
    public string ServerVersion { get; set; } = "1.0";

    /// <summary>
    /// Версия протокола MCP. Клиент присылает свою; если она новее, мы честно
    /// отвечаем той, которую поддерживаем, — так требует спецификация.
    /// </summary>
    public string ProtocolVersion { get; set; } = "2024-11-05";

    /// <summary>Сколько ждать входа бота в мир, прежде чем выполнять инструменты.</summary>
    public TimeSpan JoinTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Диагностика (в stdout писать нельзя — там протокол).</summary>
    public event Action<string>? OnLog;

    public McpServer(VsBot bot, BotToolbox tools, TextReader? input = null, TextWriter? output = null)
    {
        this.bot = bot;
        this.tools = tools;
        this.input = input ?? Console.In;
        this.output = output ?? Console.Out;
    }

    /// <summary>
    /// Крутить обмен, пока клиент не закроет вход. Запускать надо ПАРАЛЛЕЛЬНО
    /// с VsBot.RunAsync: подключение к игре живёт своей жизнью.
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        OnLog?.Invoke($"MCP: слушаю stdio, инструментов {tools.Count}");
        while (!ct.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(ct);
            if (line == null)
                break;                       // клиент закрыл вход
            // Некоторые клиенты (и консоль Windows) шлют в начале потока
            // метку кодировки BOM — без её отсечки первый же запрос падал
            // с «'0xEF' is an invalid start of a value»
            line = line.Trim().TrimStart('﻿', '​');
            if (line.Length == 0)
                continue;

            JsonNode? request;
            try { request = JsonNode.Parse(line); }
            catch (Exception ex)
            {
                await SendAsync(Error(null, -32700, $"не разобрал JSON: {ex.Message}"));
                continue;
            }
            if (request is not JsonObject req)
                continue;

            var response = await HandleAsync(req, ct);
            if (response != null)            // на уведомления ответа не бывает
                await SendAsync(response);
        }
        OnLog?.Invoke("MCP: клиент отключился");
    }

    private async Task<JsonObject?> HandleAsync(JsonObject req, CancellationToken ct)
    {
        string method = req["method"]?.GetValue<string>() ?? "";
        var id = req["id"];

        // Уведомления (без id) ответа не требуют — например notifications/initialized
        if (id == null)
            return null;

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion
                        },
                        // Клиент показывает это человеку — сразу объясняем,
                        // с чем он имеет дело
                        ["instructions"] =
                            $"Бот Vintage Story «{bot.Name}». Инструменты водят живого бота по миру: " +
                            "он честно ходит, тратит время на действия и не может того, чего не может " +
                            "игрок. Координаты — как на экране игрока. Начинай с look."
                    });

                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = ToolList() });

                case "tools/call":
                    return await CallAsync(id, req["params"] as JsonObject, ct);

                case "ping":
                    return Result(id, new JsonObject());

                default:
                    return Error(id, -32601, $"метод {method} не поддерживается");
            }
        }
        catch (Exception ex)
        {
            return Error(id, -32603, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private JsonArray ToolList()
    {
        var list = new JsonArray();
        foreach (var tool in tools)
            list.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = JsonNode.Parse(tool.Schema)
            });
        return list;
    }

    private async Task<JsonObject> CallAsync(JsonNode id, JsonObject? p, CancellationToken ct)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        var tool = tools.FirstOrDefault(t => t.Name == name);
        if (tool == null)
            return Error(id, -32602, $"инструмента «{name}» нет");

        // Бот мог ещё не войти в мир: клиент подключается сразу после запуска
        if (!await WaitJoinedAsync(ct))
            return ToolText(id, "бот ещё не в игре — подключение не состоялось", isError: true);

        var args = p?["arguments"] is JsonObject a
            ? JsonSerializer.Deserialize<JsonElement>(a.ToJsonString())
            : JsonSerializer.Deserialize<JsonElement>("{}");

        try
        {
            string result = await tool.RunAsync(args, ct);
            OnLog?.Invoke($"MCP: {name} → {result}");
            return ToolText(id, result, isError: false);
        }
        catch (Exception ex)
        {
            // Ошибку инструмента отдаём как результат с пометкой, а не как сбой
            // протокола: клиенту важно увидеть, ЧТО именно не вышло
            OnLog?.Invoke($"MCP: {name} → ошибка: {ex.Message}");
            return ToolText(id, $"ошибка: {ex.Message}", isError: true);
        }
    }

    private async Task<bool> WaitJoinedAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow + JoinTimeout;
        while (!bot.Joined && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            await Task.Delay(250, ct).ContinueWith(_ => { });
        return bot.Joined;
    }

    private static JsonObject ToolText(JsonNode id, string text, bool isError) =>
        Result(id, new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        });

    private static JsonObject Result(JsonNode id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };

    private readonly SemaphoreSlim writeLock = new(1, 1);

    private async Task SendAsync(JsonObject message)
    {
        // Одно сообщение — одна строка. Писать в stdout из разных мест нельзя:
        // строки перемешаются, и клиент потеряет протокол
        await writeLock.WaitAsync();
        try
        {
            await output.WriteLineAsync(message.ToJsonString());
            await output.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// Инструменты для разработки модов: то, чего нет в обычном наборе, но без
    /// чего мод не проверить — посмотреть блок и его блок-сущность целиком,
    /// подождать изменения, выдать себе предмет (только если сервер разрешает).
    /// Добавляются поверх стандартного набора.
    /// </summary>
    public static BotToolbox ModTestingTools(VsBot bot)
    {
        var extra = new BotToolbox();

        extra.Add("inspect_block", "Всё, что бот знает о блоке: код, коллизия, проходимость, " +
                                   "и данные блок-сущности (для проверки своего мода).",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"}},"required":["x","y","z"]}""",
            (args, _) =>
            {
                var (wx, wy, wz) = BotToolbox.ToWorld(bot,
                    BotToolbox.Num(args, "x") ?? 0, BotToolbox.Num(args, "y"), BotToolbox.Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                string code = bot.World.GetBlockCode(pos) ?? "воздух";
                var be = bot.World.GetBlockEntity(pos);
                string entity = be == null
                    ? "блок-сущности нет"
                    : $"блок-сущность {be.ClassName}: {be.Attributes?.ToJsonToken() ?? "(пусто)"}";
                // У тёсаного блока форму держит блок-сущность, а тип врёт
                // полным кубом — про это надо сказать вслух, иначе «верх
                // коллизии 0.5» у блока с кубическим типом выглядит опиской
                string chisel = bot.World.IsMicroBlock(pos.X, pos.Y, pos.Z)
                    ? "; тёсаный: форма из блок-сущности, а не из типа"
                    : "";
                return Task.FromResult(
                    $"{code}; верх коллизии {bot.World.GetCollisionTop(pos.X, pos.Y, pos.Z):0.###}{chisel}; " +
                    $"проходим: {bot.World.IsPassable(pos.X, pos.Y, pos.Z)}; " +
                    $"материал {bot.World.BlockMaterial(pos.X, pos.Y, pos.Z)}; {entity}");
            });

        extra.Add("watch_block", "Подождать, пока блок изменится (проверка реакции мода). " +
                                 "Возвращает, что было и что стало.",
            """{"type":"object","properties":{"x":{"type":"number"},"y":{"type":"number"},"z":{"type":"number"},"seconds":{"type":"number"}},"required":["x","y","z"]}""",
            async (args, ct) =>
            {
                var (wx, wy, wz) = BotToolbox.ToWorld(bot,
                    BotToolbox.Num(args, "x") ?? 0, BotToolbox.Num(args, "y"), BotToolbox.Num(args, "z") ?? 0);
                var pos = new BlockPos((int)Math.Floor(wx), (int)Math.Round(wy), (int)Math.Floor(wz));
                string before = bot.World.GetBlockCode(pos) ?? "воздух";
                double seconds = Math.Clamp(BotToolbox.Num(args, "seconds") ?? 10, 1, 120);
                var until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
                {
                    await Task.Delay(200, ct).ContinueWith(_ => { });
                    string now = bot.World.GetBlockCode(pos) ?? "воздух";
                    if (now != before)
                        return $"{before} → {now}";
                }
                return $"за {seconds:0} с блок не менялся ({before})";
            });

        extra.Add("server_command", "Отправить команду сервера от лица бота (/gamemode, /giveitem…). " +
                                    "Сработает только если у бота есть права — это обычный чат, а не чит.",
            """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""",
            async (args, _) =>
            {
                string cmd = BotToolbox.Str(args, "command") ?? "";
                if (!cmd.StartsWith('/'))
                    return "команда сервера должна начинаться с /";
                await bot.SayAsync(cmd);
                return $"отправил: {cmd} (ответ сервера смотри в журнале бота)";
            });

        // ПЕРВЫЙ ВОПРОС РАЗРАБОТЧИКА МОДА — «мой мод вообще доехал?». Без этого
        // инструмента ответ добывался чтением журнала бота, а в режиме MCP
        // журнала на экране нет вовсе (stdout занят протоколом)
        extra.Add("server_mods", "Какие моды стоят на сервере и какие у них каналы связи. " +
                                 "Показывает и ГРАНИЦУ: какие каналы бот получает, но не разбирает — " +
                                 "их содержимое известно только самому моду.",
            _ => Task.FromResult(bot.Mods.Describe()));

        extra.Add("chat_tail", "Последние сообщения игрового чата — там сервер отвечает на команды " +
                               "и туда пишет мод.",
            """{"type":"object","properties":{"count":{"type":"number"}}}""",
            (args, _) =>
            {
                int n = (int)Math.Clamp(BotToolbox.Num(args, "count") ?? 15, 1, 100);
                var lines = ChatHistory.Last(n);
                return Task.FromResult(lines.Count == 0 ? "чат пуст" : string.Join("\n", lines));
            });

        return extra;
    }

    /// <summary>
    /// Небольшая память чата: MCP-клиенту нужно видеть, что ответил сервер на
    /// команду мода, а сообщения приходят асинхронно.
    /// </summary>
    public static class ChatHistory
    {
        private static readonly Queue<string> lines = new();
        private const int Limit = 200;

        public static void Attach(VsBot bot) => bot.OnChat += m =>
        {
            lock (lines)
            {
                lines.Enqueue($"{DateTime.Now:HH:mm:ss} {m.PlainText}");
                while (lines.Count > Limit)
                    lines.Dequeue();
            }
        };

        public static List<string> Last(int count)
        {
            lock (lines)
                return lines.Reverse().Take(count).Reverse().ToList();
        }
    }
}
