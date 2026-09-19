using System.Text.Json;

using Anthropic;
using Anthropic.Models.Messages;

namespace VsBotKit.Agent;

/// <summary>
/// Нейросеть Claude через официальный SDK Anthropic.
///
/// Ключ берётся из переменной окружения ANTHROPIC_API_KEY (так же, как во всех
/// примерах Anthropic) либо передаётся явно.
///
/// <code>
/// var bot = new VsBot(host, port, "Продавец", uid);
/// bot.UseRole(new LlmRole(new ClaudeClient()) { Характер = "Ты продавец..." });
/// await bot.RunAsync();
/// </code>
/// </summary>
public sealed class ClaudeClient : ILlmClient
{
    private readonly AnthropicClient client;

    /// <summary>Модель. По умолчанию — самая способная из линейки Opus.</summary>
    public string Model { get; set; } = "claude-opus-5";

    /// <summary>
    /// Сколько усилий тратить на ход: "low" — быстро и дёшево (боту в игре
    /// обычно этого хватает), "medium"/"high" — для сложных решений.
    /// </summary>
    public string Effort { get; set; } = "low";

    /// <summary>Потолок ответа в токенах.</summary>
    public int MaxTokens { get; set; } = 2048;

    /// <summary>
    /// Сколько последних сообщений держать в разговоре. Бот живёт часами,
    /// и без ограничения история (а с ней и счёт) растёт бесконечно.
    /// </summary>
    public int HistoryLimit { get; set; } = 40;

    /// <summary>
    /// КУДА НА САМОМ ДЕЛЕ УХОДЯТ ЗАПРОСЫ. Спрашиваем сам клиент SDK, а не свою
    /// копию задуманного: адрес мог прийти тремя путями — доводом сюда,
    /// переменной окружения (её читает сам SDK) или остаться родным.
    ///
    /// ЗАЧЕМ ЭТО ВИДНО СНАРУЖИ. В списке поставщиков есть «совместимый»: любой
    /// сервис, отвечающий по протоколу Anthropic Messages, — адрес его кладут в
    /// ANTHROPIC_BASE_URL. Пути этого адреса в клиента в НАШЕМ коде не было
    /// вовсе, и по коду выходило, что окно пишет «готов думать», а запросы
    /// уходят не туда, куда человек велел. Проверка живьём показала другое:
    /// переменную читает САМ SDK (замер: ANTHROPIC_BASE_URL=https://probe…
    /// → AnthropicClient.BaseUrl = https://probe…). Но «работает по счастливой
    /// случайности внутри чужого пакета» — не то, на что можно опираться, и уж
    /// точно не то, что видно человеку. Поэтому адрес теперь и передаётся
    /// явно, и показывается наружу — фактом от клиента.
    /// </summary>
    public string BaseUrl => client.BaseUrl ?? "";

    /// <summary>
    /// Родной адрес Anthropic. Берём У САМОГО SDK, а не вписываем строкой:
    /// поменяй они его — и проверка «ходим ли мы к своему поставщику» врала бы
    /// молча.
    /// </summary>
    public static string HomeBaseUrl => Anthropic.Core.EnvironmentUrl.Production;

    /// <summary>
    /// Ходит ли клиент на СВОЙ адрес, а не на родной Anthropic. Это и есть
    /// ответ на «доехал ли мой ANTHROPIC_BASE_URL» — по факту, а не по
    /// намерению.
    /// </summary>
    public bool OwnBaseUrl =>
        BaseUrl.Length > 0 &&
        !string.Equals(BaseUrl.TrimEnd('/'), HomeBaseUrl.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Имя переменной окружения с адресом поставщика.</summary>
    public const string BaseUrlVariable = "ANTHROPIC_BASE_URL";

    /// <param name="apiKey">Ключ. Пусто — берёт сам SDK из ANTHROPIC_API_KEY.</param>
    /// <param name="baseUrl">
    /// Адрес поставщика. Пусто — читаем ANTHROPIC_BASE_URL, а если пусто и там,
    /// остаётся родной адрес Anthropic.
    /// </param>
    public ClaudeClient(string? apiKey = null, string? baseUrl = null)
    {
        // Адрес у SDK задаётся ТОЛЬКО в инициализаторе (свойство init-only),
        // поэтому все четыре сочетания «ключ есть/нет × адрес есть/нет»
        // собираются здесь, а не присваиванием после создания
        string адрес = (baseUrl ?? Environment.GetEnvironmentVariable(BaseUrlVariable) ?? "").Trim();
        bool свой = адрес.Length > 0;

        client = apiKey is { Length: > 0 }
            ? свой ? new AnthropicClient { ApiKey = apiKey, BaseUrl = адрес }
                   : new AnthropicClient { ApiKey = apiKey }
            : свой ? new AnthropicClient { BaseUrl = адрес }
                   : new AnthropicClient();
    }

    public ILlmSession Start(string systemPrompt, IReadOnlyList<LlmToolSpec> tools) =>
        new Session(this, systemPrompt, tools);

    private sealed class Session : ILlmSession, ILlmUsageReporter
    {
        private readonly ClaudeClient owner;
        private readonly string system;
        private readonly List<ToolUnion> tools;
        private readonly List<MessageParam> messages = [];

        public Session(ClaudeClient owner, string system, IReadOnlyList<LlmToolSpec> specs)
        {
            this.owner = owner;
            this.system = system;
            tools = specs.Select(ToTool).ToList();
        }

        /// <summary>Суммарный расход сеанса (см. ILlmUsageReporter).</summary>
        public LlmUsage TotalUsage { get; private set; }

        /// <summary>Сколько запросов ушло в API за сеанс.</summary>
        public int Requests { get; private set; }

        /// <summary>Расход последнего запроса.</summary>
        public LlmUsage? LastUsage { get; private set; }

        // Reset чистит разговор, но НЕ счётчики расхода: потраченное уже
        // потрачено, и делать вид, что нет, — враньё о результате
        public void Reset() => messages.Clear();

        public Task<LlmReply> SayAsync(string text, CancellationToken ct = default)
        {
            messages.Add(new MessageParam { Role = Role.User, Content = text });
            return AskAsync(ct);
        }

        public Task<LlmReply> ToolResultsAsync(IReadOnlyList<(string Id, string Result)> results,
            CancellationToken ct = default)
        {
            // Результат обязан прийти на КАЖДЫЙ запрошенный инструмент, одним
            // сообщением — иначе следующий запрос отклоняется
            var blocks = results
                .Select(r => (ContentBlockParam)new ToolResultBlockParam
                {
                    ToolUseID = r.Id,
                    Content = r.Result.Length > 0 ? r.Result : "готово"
                })
                .ToList();
            messages.Add(new MessageParam { Role = Role.User, Content = blocks });
            return AskAsync(ct);
        }

        private async Task<LlmReply> AskAsync(CancellationToken ct)
        {
            Trim();

            var response = await owner.client.Messages.Create(new MessageCreateParams
            {
                Model = owner.Model,
                MaxTokens = owner.MaxTokens,
                System = system,
                Messages = messages.ToList(),
                Tools = tools,
                OutputConfig = new OutputConfig { Effort = ToEffort(owner.Effort) }
            }, cancellationToken: ct);

            // Расход берём из ответа SDK. Имена полей — как в пакете Anthropic
            // 12.39.0 (Anthropic.Models.Messages.Usage): InputTokens и
            // OutputTokens не обнуляемые, кэшевые — long?, поэтому ?? 0
            var spent = new LlmUsage(
                response.Usage.InputTokens,
                response.Usage.OutputTokens,
                response.Usage.CacheCreationInputTokens ?? 0,
                response.Usage.CacheReadInputTokens ?? 0);
            LastUsage = spent;
            TotalUsage += spent;
            Requests++;

            // Ответ модели возвращается ей же следующим ходом — иначе она не
            // помнит, что просила инструмент
            var echo = new List<ContentBlockParam>();
            var calls = new List<LlmToolCall>();
            string text = "";

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? t))
                {
                    text += t.Text;
                    echo.Add(new TextBlockParam { Text = t.Text });
                }
                else if (block.TryPickThinking(out ThinkingBlock? think))
                {
                    // Подпись обязана вернуться нетронутой
                    echo.Add(new ThinkingBlockParam { Thinking = think.Thinking, Signature = think.Signature });
                }
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? red))
                {
                    echo.Add(new RedactedThinkingBlockParam { Data = red.Data });
                }
                else if (block.TryPickToolUse(out ToolUseBlock? use))
                {
                    echo.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    calls.Add(new LlmToolCall(use.ID, use.Name, ToElement(use.Input)));
                }
            }

            if (echo.Count > 0)
                messages.Add(new MessageParam { Role = Role.Assistant, Content = echo });

            // Отказ модели — это штатный ответ, а не ошибка. Но если в том же
            // ответе остались запросы инструментов, их нельзя бросить: без
            // ответа на каждый следующий запрос отобьётся ошибкой
            if (response.StopReason == "refusal")
            {
                if (calls.Count > 0)
                    await ToolResultsAsync(
                        calls.Select(c => (c.Id, "не выполнено: ход прерван")).ToList(), ct);
                return new LlmReply(text.Length > 0 ? text : "(модель отказалась отвечать)", []);
            }

            return new LlmReply(text.Trim(), calls);
        }

        /// <summary>
        /// Обрезка истории. Резать можно ТОЛЬКО по границе обычного хода
        /// пользователя: если оставить сообщение с результатами инструментов
        /// без запроса, на который оно отвечает, API отвергнет весь разговор.
        /// </summary>
        private void Trim()
        {
            while (messages.Count > owner.HistoryLimit)
            {
                // Ищем следующую границу — простое текстовое сообщение от нас
                int cut = 1;
                while (cut < messages.Count && !IsPlainUser(messages[cut]))
                    cut++;
                if (cut >= messages.Count)
                    break; // резать негде: весь хвост — незавершённый обмен
                messages.RemoveRange(0, cut);
            }
        }

        private static bool IsPlainUser(MessageParam m) =>
            m.Role == Role.User && m.Content.Value is string;

        private static JsonElement ToElement(IReadOnlyDictionary<string, JsonElement> input) =>
            JsonSerializer.SerializeToElement(input);

        private static Effort ToEffort(string value) => value.ToLowerInvariant() switch
        {
            "low" => Anthropic.Models.Messages.Effort.Low,
            "medium" => Anthropic.Models.Messages.Effort.Medium,
            "max" => Anthropic.Models.Messages.Effort.Max,
            _ => Anthropic.Models.Messages.Effort.High
        };

        private static ToolUnion ToTool(LlmToolSpec spec)
        {
            using var doc = JsonDocument.Parse(spec.Schema);
            var properties = new Dictionary<string, JsonElement>();
            if (doc.RootElement.TryGetProperty("properties", out var props))
                foreach (var p in props.EnumerateObject())
                    properties[p.Name] = p.Value.Clone();

            var required = new List<string>();
            if (doc.RootElement.TryGetProperty("required", out var req))
                foreach (var r in req.EnumerateArray())
                    if (r.GetString() is { } name)
                        required.Add(name);

            return new Tool
            {
                Name = spec.Name,
                Description = spec.Description,
                InputSchema = new() { Properties = properties, Required = required }
            };
        }
    }
}
