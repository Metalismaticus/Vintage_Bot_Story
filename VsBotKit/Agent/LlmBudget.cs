using System.Text;

namespace VsBotKit.Agent;

/// <summary>
/// Расход одного обращения к нейросети в токенах.
///
/// Поля названы так же, как их отдаёт SDK Anthropic (Message.Usage):
/// input_tokens / output_tokens / cache_creation_input_tokens /
/// cache_read_input_tokens. Кэш вынесен отдельно не для красоты: он считается
/// по другой цене (запись дороже обычного входа, чтение — сильно дешевле),
/// поэтому складывать его с обычным входом в одно число нельзя.
/// </summary>
public readonly record struct LlmUsage(
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens = 0,
    long CacheReadTokens = 0)
{
    /// <summary>Сколько токенов прошло через модель всего.</summary>
    public long Total => InputTokens + OutputTokens + CacheWriteTokens + CacheReadTokens;

    public static LlmUsage operator +(LlmUsage a, LlmUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheWriteTokens + b.CacheWriteTokens,
        a.CacheReadTokens + b.CacheReadTokens);

    public static LlmUsage operator -(LlmUsage a, LlmUsage b) => new(
        a.InputTokens - b.InputTokens,
        a.OutputTokens - b.OutputTokens,
        a.CacheWriteTokens - b.CacheWriteTokens,
        a.CacheReadTokens - b.CacheReadTokens);

    public override string ToString() =>
        $"вход {InputTokens}, выход {OutputTokens}" +
        (CacheWriteTokens + CacheReadTokens > 0
            ? $", кэш {CacheWriteTokens}/{CacheReadTokens}"
            : "");
}

/// <summary>
/// Сеанс, который умеет честно сказать, во что обошлись его обращения.
///
/// Это ОТДЕЛЬНЫЙ интерфейс, а не новые члены ILlmSession: чужая реализация
/// нейросети (локальная модель, заглушка в тестах) просто его не реализует, и
/// ничего не ломается. Роль спрашивает так: <c>session as ILlmUsageReporter</c>.
///
/// Счётчики МОНОТОННЫЕ и живут от создания сеанса: Reset() чистит разговор, но
/// не расход — деньги уже потрачены. Кто считает расход за ход, берёт разницу
/// значений до и после хода: тогда не теряются служебные обращения (например,
/// довесок с ответами инструментов после отказа модели).
/// </summary>
public interface ILlmUsageReporter
{
    /// <summary>Суммарный расход сеанса.</summary>
    LlmUsage TotalUsage { get; }

    /// <summary>Сколько обращений к нейросети сделано за сеанс.</summary>
    int Requests { get; }

    /// <summary>Расход последнего обращения (null — обращений ещё не было).</summary>
    LlmUsage? LastUsage { get; }
}

/// <summary>Что бюджет разрешает прямо сейчас.</summary>
public enum LlmBudgetState
{
    /// <summary>Тратим как обычно.</summary>
    Normal,

    /// <summary>Лимит близко: думать можно, но реже.</summary>
    Slowed,

    /// <summary>Думать нельзя: лимит исчерпан, слишком много неудач или выключено вручную.</summary>
    Stopped,
}

/// <summary>Ответ бюджета на вопрос «можно ли сейчас думать».</summary>
/// <param name="State">Состояние бюджета.</param>
/// <param name="Reason">Почему именно так — человеку в журнал.</param>
/// <param name="Pace">Во сколько раз реже думать, чем обычно (1 — как обычно).</param>
/// <param name="RetryAfter">Через сколько состояние может смениться само.
/// <see cref="TimeSpan.MaxValue"/> — само не сменится (выключено вручную).</param>
public sealed record LlmBudgetVerdict(
    LlmBudgetState State,
    string Reason,
    double Pace,
    TimeSpan RetryAfter)
{
    public bool CanThink => State != LlmBudgetState.Stopped;
}

/// <summary>
/// Потолок расходов на нейросеть.
///
/// Зачем: бот живёт сутками, а каждое «подумать» — платный запрос. Без
/// потолка счёт растёт молча, а сломавшаяся модель (например, кончился ключ)
/// долбится в API раз в секунду.
///
/// Что умеет:
/// - лимиты: запросов в час, токенов в сутки, неудач подряд;
/// - учёт потраченного по скользящим окнам (час и сутки), а не по календарю:
///   иначе в полночь лимит обнулялся бы рывком;
/// - мягкая деградация: когда истрачено больше <see cref="SoftLimit"/> доли
///   лимита, бюджет просит думать реже (<see cref="SlowdownFactor"/>);
/// - жёсткий стоп с событием <see cref="OnStopped"/>;
/// - ручной выключатель <see cref="TurnOff"/>/<see cref="TurnOn"/>;
/// - сводку <see cref="Summary"/> «потрачено столько-то».
///
/// Это МЕХАНИЗМ: он не знает, чем занят бот, и ничего не решает за роль — он
/// только отвечает на вопрос «можно ли сейчас потратиться». Решает роль.
/// </summary>
public sealed class LlmBudget
{
    // Окна намеренно не настраиваются: имена лимитов обещают «в час» и «в сутки»,
    // и расходиться с именами нельзя
    private static readonly TimeSpan RequestWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan TokenWindow = TimeSpan.FromDays(1);

    private readonly record struct Spend(DateTime When, int Requests, LlmUsage Usage);

    private readonly object gate = new();
    private readonly Queue<Spend> spends = new();

    private bool enabled = true;
    private string offReason = "";
    private int failuresInRow;
    private string lastFailure = "";
    private DateTime failurePauseUntil;
    private LlmBudgetVerdict? announced;

    /// <summary>Сколько обращений разрешено за последний час. 0 — без лимита.</summary>
    public int RequestsPerHour { get; set; } = 60;

    /// <summary>Сколько токенов разрешено за последние сутки. 0 — без лимита.</summary>
    public long TokensPerDay { get; set; } = 1_000_000;

    /// <summary>После скольких неудач подряд встать. 0 — не считать неудачи.</summary>
    public int MaxFailuresInRow { get; set; } = 5;

    /// <summary>Сколько ждать после серии неудач, прежде чем попробовать снова.</summary>
    public TimeSpan FailurePause { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Доля лимита, после которой начинается мягкая деградация.</summary>
    public double SoftLimit { get; set; } = 0.8;

    /// <summary>Во сколько раз реже думать при деградации.</summary>
    public double SlowdownFactor { get; set; } = 4;

    /// <summary>Цена входных токенов за миллион. 0 — цену не знаем и не показываем.</summary>
    public double InputPricePerMillion { get; set; }

    /// <summary>Цена выходных токенов за миллион. 0 — цену не знаем и не показываем.</summary>
    public double OutputPricePerMillion { get; set; }

    /// <summary>Валюта для сводки (просто подпись).</summary>
    public string Currency { get; set; } = "$";

    /// <summary>Включён ли расход вообще (ручной выключатель).</summary>
    public bool Enabled
    {
        get { lock (gate) return enabled; }
    }

    /// <summary>Когда бюджет начал считать.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Всего обращений с запуска.</summary>
    public long TotalRequests { get; private set; }

    /// <summary>Всего потрачено токенов с запуска.</summary>
    public LlmUsage TotalUsage { get; private set; }

    /// <summary>Состояние сменилось (реже/нельзя/снова можно).</summary>
    public event Action<LlmBudgetVerdict>? OnChanged;

    /// <summary>Жёсткий стоп: тратить больше нельзя. Аргумент — причина.</summary>
    public event Action<string>? OnStopped;

    /// <summary>Спросить бюджет перед тем, как тратиться.</summary>
    public LlmBudgetVerdict Ask()
    {
        LlmBudgetVerdict verdict;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            Trim(now);
            verdict = Judge(now);
        }
        Announce(verdict);
        return verdict;
    }

    /// <summary>Записать потраченное. Вызывать ПОСЛЕ обращения, по факту ответа.</summary>
    public void Record(LlmUsage usage, int requests = 1)
    {
        if (requests <= 0 && usage.Total <= 0)
            return;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            int count = Math.Max(0, requests);
            var spent = new LlmUsage(
                Math.Max(0, usage.InputTokens),
                Math.Max(0, usage.OutputTokens),
                Math.Max(0, usage.CacheWriteTokens),
                Math.Max(0, usage.CacheReadTokens));
            spends.Enqueue(new Spend(now, count, spent));
            TotalRequests += count;
            TotalUsage += spent;
            Trim(now);
        }
    }

    /// <summary>Обращение не удалось (сеть, ключ, отказ сервиса).</summary>
    public void RecordFailure(string why)
    {
        lock (gate)
        {
            failuresInRow++;
            lastFailure = why;
            if (MaxFailuresInRow > 0 && failuresInRow >= MaxFailuresInRow)
                failurePauseUntil = DateTime.UtcNow + FailurePause;
        }
    }

    /// <summary>Обращение удалось — счётчик неудач подряд сбрасывается.</summary>
    public void RecordSuccess()
    {
        lock (gate)
        {
            failuresInRow = 0;
            lastFailure = "";
            failurePauseUntil = default;
        }
    }

    /// <summary>Ручной выключатель: больше не тратиться, пока не включат.</summary>
    public void TurnOff(string reason = "выключено вручную")
    {
        lock (gate)
        {
            enabled = false;
            offReason = reason;
        }
    }

    /// <summary>Снова разрешить тратиться. Потраченное не забывается.</summary>
    public void TurnOn()
    {
        lock (gate)
        {
            enabled = true;
            offReason = "";
            failuresInRow = 0;
            failurePauseUntil = default;
        }
    }

    /// <summary>Забыть потраченное (лимиты начнут считаться заново).</summary>
    public void Forget()
    {
        lock (gate)
        {
            spends.Clear();
            failuresInRow = 0;
            failurePauseUntil = default;
        }
    }

    /// <summary>Сводка «потрачено столько-то» — человеку в чат или в журнал.</summary>
    public string Summary()
    {
        lock (gate)
        {
            var now = DateTime.UtcNow;
            Trim(now);
            int perHour = RequestsIn(RequestWindow, now);
            long perDay = TokensIn(TokenWindow, now);

            var text = new StringBuilder();
            text.Append("Нейросеть: за час ").Append(perHour);
            if (RequestsPerHour > 0)
                text.Append(" из ").Append(RequestsPerHour);
            text.Append(" обращений, за сутки ").Append(perDay.ToString("N0"));
            if (TokensPerDay > 0)
                text.Append(" из ").Append(TokensPerDay.ToString("N0"));
            text.Append(" токенов. Всего с запуска: ")
                .Append(TotalRequests).Append(" обращений, ")
                .Append(TotalUsage.InputTokens.ToString("N0")).Append(" входных и ")
                .Append(TotalUsage.OutputTokens.ToString("N0")).Append(" выходных токенов");
            if (TotalUsage.CacheWriteTokens + TotalUsage.CacheReadTokens > 0)
                text.Append(" (кэш: запись ")
                    .Append(TotalUsage.CacheWriteTokens.ToString("N0")).Append(", чтение ")
                    .Append(TotalUsage.CacheReadTokens.ToString("N0")).Append(')');
            text.Append('.');

            // Цену считаем только если хозяин её задал: выдумывать тарифы нельзя.
            // Кэш по прейскуранту Anthropic: запись 1.25 цены входа, чтение 0.1
            if (InputPricePerMillion > 0 || OutputPricePerMillion > 0)
            {
                double cost =
                    (TotalUsage.InputTokens
                     + TotalUsage.CacheWriteTokens * 1.25
                     + TotalUsage.CacheReadTokens * 0.1) / 1e6 * InputPricePerMillion
                    + TotalUsage.OutputTokens / 1e6 * OutputPricePerMillion;
                text.Append(" Примерно ").Append(cost.ToString("0.00")).Append(' ').Append(Currency).Append('.');
            }

            if (!enabled)
                text.Append(" Выключено вручную").Append(offReason.Length > 0 ? $" ({offReason})" : "").Append('.');
            else if (failuresInRow > 0)
                text.Append(' ').Append(failuresInRow).Append(" неудач подряд.");

            return text.ToString();
        }
    }

    // --- внутренности ---

    /// <summary>Решение по текущему состоянию. Вызывается под замком.</summary>
    private LlmBudgetVerdict Judge(DateTime now)
    {
        if (!enabled)
            return new LlmBudgetVerdict(LlmBudgetState.Stopped,
                offReason.Length > 0 ? offReason : "выключено вручную",
                SlowdownFactor, TimeSpan.MaxValue);

        // Предохранитель: если модель отвечает ошибкой раз за разом, долбиться
        // в неё каждую секунду бессмысленно и платно
        if (MaxFailuresInRow > 0 && failuresInRow >= MaxFailuresInRow && now < failurePauseUntil)
            return new LlmBudgetVerdict(LlmBudgetState.Stopped,
                $"{failuresInRow} неудач подряд ({lastFailure})",
                SlowdownFactor, failurePauseUntil - now);

        int requests = RequestsIn(RequestWindow, now);
        long tokens = TokensIn(TokenWindow, now);

        if (RequestsPerHour > 0 && requests >= RequestsPerHour)
            return new LlmBudgetVerdict(LlmBudgetState.Stopped,
                $"обращений за час: {requests} из {RequestsPerHour}",
                SlowdownFactor, FreeAfter(RequestWindow, now));

        if (TokensPerDay > 0 && tokens >= TokensPerDay)
            return new LlmBudgetVerdict(LlmBudgetState.Stopped,
                $"токенов за сутки: {tokens:N0} из {TokensPerDay:N0}",
                SlowdownFactor, FreeAfter(TokenWindow, now));

        double requestShare = RequestsPerHour > 0 ? (double)requests / RequestsPerHour : 0;
        double tokenShare = TokensPerDay > 0 ? (double)tokens / TokensPerDay : 0;
        double share = Math.Max(requestShare, tokenShare);

        if (SoftLimit > 0 && share >= SoftLimit)
            return new LlmBudgetVerdict(LlmBudgetState.Slowed,
                $"истрачено {share:P0} лимита (обращений {requests}, токенов {tokens:N0})",
                Math.Max(1, SlowdownFactor), TimeSpan.Zero);

        return new LlmBudgetVerdict(LlmBudgetState.Normal,
            $"обращений {requests}, токенов {tokens:N0}", 1, TimeSpan.Zero);
    }

    /// <summary>Сообщить о смене состояния. Вне замка: обработчик может быть долгим.</summary>
    private void Announce(LlmBudgetVerdict verdict)
    {
        LlmBudgetVerdict? was;
        lock (gate)
        {
            was = announced;
            // Сравниваем только состояние: в причине лежат счётчики, они меняются
            // после каждого хода, и по ним журнал был бы завален
            if (was is { } prev && prev.State == verdict.State)
                return;
            announced = verdict;
        }

        // Первое же «всё в норме» — не новость, о ней молчим
        if (was is null && verdict.State == LlmBudgetState.Normal)
            return;

        OnChanged?.Invoke(verdict);
        if (verdict.State == LlmBudgetState.Stopped && was?.State != LlmBudgetState.Stopped)
            OnStopped?.Invoke(verdict.Reason);
    }

    /// <summary>Выбросить записи старше самого длинного окна. Под замком.</summary>
    private void Trim(DateTime now)
    {
        var horizon = RequestWindow > TokenWindow ? RequestWindow : TokenWindow;
        while (spends.Count > 0 && now - spends.Peek().When > horizon)
            spends.Dequeue();
    }

    private int RequestsIn(TimeSpan window, DateTime now)
    {
        int sum = 0;
        foreach (var s in spends)
            if (now - s.When < window)
                sum += s.Requests;
        return sum;
    }

    private long TokensIn(TimeSpan window, DateTime now)
    {
        long sum = 0;
        foreach (var s in spends)
            if (now - s.When < window)
                sum += s.Usage.Total;
        return sum;
    }

    /// <summary>Через сколько из окна выпадет самая старая запись (значит, чуть-чуть освободится).</summary>
    private TimeSpan FreeAfter(TimeSpan window, DateTime now)
    {
        foreach (var s in spends)          // очередь упорядочена по времени
            if (now - s.When < window)
                return s.When + window - now;
        return TimeSpan.Zero;
    }
}
