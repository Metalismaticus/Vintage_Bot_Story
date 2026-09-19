namespace VsBotKit;

/// <summary>Сообщение игрового чата.</summary>
/// <param name="Text">Текст (может содержать HTML-разметку вроде &lt;strong&gt;)</param>
/// <param name="GroupId">Чат-группа: 0 — общий чат</param>
/// <param name="Data">Служебные данные сервера (например, "from: 123,withoutPrefix:...")</param>
public record ChatMessage(string Text, int GroupId, string? Data)
{
    /// <summary>Текст без HTML-тегов.</summary>
    public string PlainText =>
        System.Text.RegularExpressions.Regex.Replace(Text, "<[^>]+>", "");
}
