using System.Text.Json;

namespace VsBotKit.Agent;

/// <summary>Вызов инструмента, который затребовала модель.</summary>
public sealed record LlmToolCall(string Id, string Name, JsonElement Input);

/// <summary>Ответ модели: что сказать и что сделать.</summary>
public sealed record LlmReply(string Text, IReadOnlyList<LlmToolCall> Calls)
{
    public bool WantsTools => Calls.Count > 0;
    public static readonly LlmReply Empty = new("", []);
}

/// <summary>Описание инструмента для модели.</summary>
public sealed record LlmToolSpec(string Name, string Description, string Schema);

/// <summary>
/// Диалог с моделью. Историю держит сам сеанс — так каждая нейросеть хранит её
/// в своём формате, а бот об этом не думает.
/// </summary>
public interface ILlmSession
{
    /// <summary>Сказать модели что-то от лица мира и получить ответ.</summary>
    Task<LlmReply> SayAsync(string text, CancellationToken ct = default);

    /// <summary>Вернуть модели результаты инструментов, которые она просила.</summary>
    Task<LlmReply> ToolResultsAsync(IReadOnlyList<(string Id, string Result)> results,
        CancellationToken ct = default);

    /// <summary>Начать разговор заново (например, после смерти или переподключения).</summary>
    void Reset();
}

/// <summary>
/// Нейросеть, которая может управлять ботом. Интерфейс намеренно крошечный:
/// подставить свою модель (локальную, чужого поставщика, заглушку для тестов) —
/// значит реализовать два метода.
/// </summary>
public interface ILlmClient
{
    ILlmSession Start(string systemPrompt, IReadOnlyList<LlmToolSpec> tools);
}
