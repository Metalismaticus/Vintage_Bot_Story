using VsBotKit.Panel;   // метка [KnobPlace] — где настройке стоять в окне управления

namespace VsBotKit.Agent;

/// <summary>
/// Роль, которой управляет нейросеть.
///
/// Всё, что бот умеет, уже написано в библиотеке — модель только решает, ЧТО
/// и КОГДА делать. Рефлексы (не висеть в воздухе, отбиваться, лечиться, есть,
/// прятаться от бури) остаются на детерминированных способностях: нейросеть
/// не должна спасать бота от волка через три секунды раздумий.
///
/// Бот целиком:
/// <code>
/// var bot = new VsBot("127.0.0.1", 42420, "Торговец", uid);
/// bot.UseRole(new LlmRole(new ClaudeClient())
/// {
///     Характер = "Ты продавец в магазине у спавна. Отвечай коротко и по делу.",
///     Цель = "Стой у прилавка, здоровайся с гостями, объясняй цены с табличек."
/// });
/// await bot.RunAsync();
/// </code>
/// </summary>
public class LlmRole : LivingRole
{
    private readonly ILlmClient llm;
    private ILlmSession? session;
    private VsBot? bot;
    private readonly SemaphoreSlim thinkLock = new(1, 1);
    private readonly Queue<string> events = new();

    public LlmRole(ILlmClient llm)
    {
        this.llm = llm;
    }

    public override string Name => "ИИ";

    // МЕСТО КАЖДОЙ РУЧКИ НАЗЫВАЕТ ОНА САМА. Без меток панель раскладывала их
    // догадкой по словам, и у роли «ИИ» это выглядело так: ХАРАКТЕР, ЦЕЛЬ и
    // ПРАВИЛА — три настройки, за которыми в панель и приходят, — падали в
    // «Прочее», а ограничитель зацикливания (ДействийЗаХод) уезжал в
    // «Безопасность», потому что в его описании есть слово «защита».
    // Выживальщику и Продавцу метки расставили, третьей роли — забыли.

    // ИМЕНА ЭТИХ СЕМИ РУЧЕК БЫЛИ АНГЛИЙСКИМИ, И ЭТО ЛОМАЛО ЗАКОН 1. Ручка —
    // это ПОЛИТИКА, и правит её человек: в окне управления и рукой в
    // presets/ai.json. Русские имена у пяти ролей из пяти, а у роли «ИИ» в том
    // же файле рядом стояли «Поставщик», «Модель», «ТокеновВСутки» — и тут же
    // «Personality», «MaxToolsPerTurn». Прошлый поток назвал это вслух и не
    // закрыл. Прежние имена метка [KnobWasNamed] понимает и сегодня: чужой
    // ai.json, написанный до переименования, продолжает работать, а бот
    // говорит про старое имя в журнал.

    /// <summary>Кто этот бот и как себя ведёт (характер, манера речи).</summary>
    [KnobWasNamed("Personality",
        Why = "то же самое: текст о том, кем себя считать; менять не надо")]
    [KnobPlace("ии", Order = 1, Main = true, RoleTab = true)]
    public string Характер { get; set; } = "Ты обычный игрок Vintage Story. Говоришь коротко и по-человечески.";

    /// <summary>Чем бот занят, пока его никто не трогает.</summary>
    [KnobWasNamed("Goal",
        Why = "то же самое: текст о том, чем заниматься; менять не надо")]
    [KnobPlace("ии", Order = 2, Main = true, RoleTab = true)]
    public string Цель { get; set; } = "Держись рядом со своим домом, следи за собой и отвечай игрокам.";

    /// <summary>Дополнительные правила от хозяина бота (запреты, границы).</summary>
    [KnobWasNamed("Rules",
        Why = "то же самое: текст с запретами и границами; менять не надо")]
    [KnobPlace("ии", Order = 3, Main = true, RoleTab = true)]
    public string Правила { get; set; } = "";

    /// <summary>Как часто думать самому, без обращений (секунды). 0 — только по событиям.</summary>
    [KnobWasNamed("IdleSeconds",
        Why = "единицы те же — СЕКУНДЫ между раздумьями, 0 значит «только по событиям»")]
    [KnobPlace("ии", Order = 10, RoleTab = true)]
    public double ДуматьРазВСекунд { get; set; } = 45;

    /// <summary>Отвечать на сообщения в чате.</summary>
    [KnobWasNamed("ReactToChat",
        Why = "то же самое, флаг: отвечать ли на чужие сообщения в чате")]
    [KnobPlace("люди", Order = 10, Main = true)]
    public bool ОтвечатьВЧате { get; set; } = true;

    /// <summary>Сколько инструментов подряд разрешено за один ход (защита от зацикливания).</summary>
    [KnobWasNamed("MaxToolsPerTurn",
        Why = "единицы те же — ШТУКИ действий за один ход модели")]
    [KnobPlace("ии", Order = 11, RoleTab = true)]
    public int ДействийЗаХод { get; set; } = 12;

    /// <summary>Набор инструментов. По умолчанию стандартный; роль может дополнить свой.</summary>
    public BotToolbox Tools { get; } = [];

    /// <summary>Подключать стандартные способности выживания (рефлексы).</summary>
    // ЧИТАЕТСЯ РОВНО ОДИН РАЗ — в Configure ниже, при сборке бота, и снять уже
    // подключённые рефлексы нечем. До сих пор об этом узнавал только журнал
    // (AiTraderRole.OnKnobsChanged жалуется туда), то есть не тот, кто крутит
    // ручку в браузере. Теперь это говорит сама ручка, и окно повторяет её
    // слова человеку рядом с полем
    [KnobWasNamed("UseSurvivalReflexes",
        Why = "то же самое, флаг: подключать ли рефлексы выживания при сборке бота")]
    [KnobPlace("безопасность", Order = 40,
        AtStartOnly = "рефлексы выживания подключаются один раз, при сборке бота, " +
                      "и снять уже подключённые нечем")]
    public bool РефлексыВыживания { get; set; } = true;

    /// <summary>
    /// Потолок расходов на нейросеть. Роль спрашивает его перед каждым ходом:
    /// пока бюджет говорит «нельзя», обращений к модели не будет вообще.
    /// Настройки менять до <see cref="VsBot.RunAsync"/>.
    /// </summary>
    public LlmBudget Budget { get; } = new();

    /// <summary>Что модель сказала и что сделала — для журнала.</summary>
    public event Action<string>? OnThought;

    /// <summary>Бюджет сменил состояние (стал думать реже, встал, снова можно).</summary>
    public event Action<LlmBudgetVerdict>? OnBudget;

    public override void Configure(VsBot bot)
    {
        this.bot = bot;

        if (РефлексыВыживания)
            bot.Survive();
        bot.CommonCommands();

        if (Tools.Count == 0)
            Tools.AddRange(BotToolbox.Standard(bot));

        // Сообщения игроков — главный повод подумать. Разбираем так же, как
        // роутер команд: «Игрок: текст», своё эхо и служебные строки — мимо
        bot.OnChat += line =>
        {
            if (!ОтвечатьВЧате)
                return;
            string text = line.PlainText;
            int sep = text.IndexOf(": ", StringComparison.Ordinal);
            if (sep <= 0)
                return;                       // служебное сообщение сервера
            string sender = text[..sep];
            if (sender == bot.Name)
                return;                       // собственное эхо
            string body = text[(sep + 2)..].Trim();
            if (body.Length == 0 || body[0] == bot.Commands.Prefix)
                return;                       // команды разбирает роутер команд
            Push($"{sender} пишет в чат: {body}");
        };

        // Бюджет сам говорит, когда состояние сменилось. Пересказываем это
        // хозяину: молча переставший думать бот выглядит как сломанный
        Budget.OnChanged += verdict =>
        {
            string text = verdict.State switch
            {
                LlmBudgetState.Stopped =>
                    $"бюджет исчерпан ({verdict.Reason}); не думаю"
                    + (verdict.RetryAfter == TimeSpan.MaxValue
                        ? ", пока не включат вручную"
                        : $", следующая попытка через {Round(verdict.RetryAfter)}"),
                LlmBudgetState.Slowed =>
                    $"бюджет на исходе ({verdict.Reason}); думаю раз в {PaceBaseSeconds * verdict.Pace:0} с",
                _ => $"бюджет снова в норме ({verdict.Reason})",
            };
            bot.Log("ИИ: " + text);
            OnThought?.Invoke(text);
            OnBudget?.Invoke(verdict);
        };

        // Важные события мира — их модель должна узнавать сразу
        bot.Storms.OnChanged += (active, strength) =>
            Push(active ? $"Началась временная буря ({strength})." : "Временная буря кончилась.");
        bot.Self.OnDeath += () => Push("Я погиб и возрождаюсь. Вещи остались на месте смерти.");

        // ОБЩИЕ РУЧКИ. Пресет выживания подключает и роли ИИ фонарь в руке,
        // приветствие эмоцией, «что делать без дела» и точку возрождения на
        // временной шестерёнке. Пока выключателей не было, модель ничего об
        // этом не знала, а хозяин не мог ни отключить, ни объяснить, почему
        // бот вдруг машет прохожим посреди её собственного дела
        ОбщиеНастройкиГотовы(bot);
    }

    private CancellationTokenSource? loopCts;

    /// <summary>
    /// РОЛЬ СНИМАЮТ — ГАСИМ КРУГ РАЗДУМИЙ. Он крутится отдельной задачей и
    /// полем бота не является, поэтому механический откат следа его не видит:
    /// оставленный, он продолжает дёргать умения живого бота и ТРАТИТЬ КЛЮЧ
    /// ЗАКАЗЧИКА уже под чужой ролью. Два мозга на одном теле — и второй за
    /// деньги.
    /// </summary>
    public override IEnumerable<string> Снять(VsBot bot)
    {
        if (loopCts is null)
            yield break;
        loopCts.Cancel();
        loopCts = null;
        session = null;
        yield return "круг раздумий нейросети погашен";
    }

    public override Task OnJoinedAsync(VsBot bot)
    {
        // Вход бывает не один раз: после обрыва связи бот возвращается сам.
        // Старый цикл надо погасить, иначе N возвращений = N параллельных
        // циклов на одну очередь событий и один платный сеанс модели
        loopCts?.Cancel();
        loopCts = new CancellationTokenSource();
        var ct = loopCts.Token;

        session = llm.Start(SystemPrompt(bot), Tools
            .Select(t => new LlmToolSpec(t.Name, t.Description, t.Schema))
            .ToList());

        lock (events)
            events.Clear();

        // ОБЩЕЙ РУЧКОЙ «Здороваться», А НЕ МИМО НЕЁ. База сказала бы фразу
        // сама (LivingRole.OnJoinedAsync), но здесь здоровается НЕ БОТ, а
        // модель: скажи мы своё «привет» из кода — в чате вышло бы два
        // приветствия подряд, наше и её. Поэтому base здесь не зовётся, а
        // ручка доносится единственным доступным способом — словами в первой
        // мысли. Это ПРОСЬБА, а не выключатель, и обещать иное нельзя
        Push(FirstThought(Здороваться));
        _ = Task.Run(() => LoopAsync(ct), ct);
        return Task.CompletedTask;
    }

    /// <summary>
    /// ПЕРВАЯ МЫСЛЬ БОТА В МИРЕ — чистое правило, потому его и можно проверить
    /// на стенде: ручка «Здороваться» у думающего бота обязана менять слова, с
    /// которыми он входит в мир, а не лежать в поле мёртвым грузом.
    ///
    /// Правило одно на всех наследников: разойдись оно копиями по ролям — у
    /// одной «не здороваться» значило бы молчание, у другой ничего.
    /// </summary>
    /// <param name="greet">Просить ли модель поздороваться.</param>
    public static string FirstThought(bool greet) =>
        greet
            ? "Ты вошёл в мир. Поздоровайся в чате одной короткой фразой, осмотрись и займись делом."
            : "Ты вошёл в мир. Здороваться не надо — молча осмотрись и займись делом.";

    private void Push(string message)
    {
        lock (events)
        {
            // Копить бесконечно нечего: модель всё равно увидит свежий снимок мира
            if (events.Count > 20)
                events.Dequeue();
            events.Enqueue(message);
        }
    }

    private string SystemPrompt(VsBot bot) =>
        $"""
         Ты управляешь ботом в игре Vintage Story. Бот — обычный игрок в режиме выживания.

         КТО ТЫ
         {Характер}

         ТВОЁ ДЕЛО
         {Цель}
         {(Правила.Length > 0 ? "\nПРАВИЛА ХОЗЯИНА\n" + Правила : "")}

         КАК ЭТО РАБОТАЕТ
         - Ты действуешь только через инструменты. Текст без инструмента никто в игре не услышит:
           чтобы заговорить, вызови say.
         - Координаты везде такие же, какие видит игрок на экране (x, y, z от точки спавна по x/z).
         - Тело бота само не висит в воздухе, отбивается от врагов, лечится, ест и прячется от бури —
           за это отвечают рефлексы. Не трать на это ходы, если всё в порядке.
         - Бот не умеет и не должен делать того, чего не может человек: телепортироваться,
           видеть содержимое закрытого сундука, ломать блок мгновенно. Инструменты честные,
           поэтому действия занимают время.
         - Если инструмент вернул отказ («не дотянуться», «нет предмета») — это факт, а не совет
           попробовать ещё раз тем же способом. Меняй план.
         - Отвечай людям коротко, как игрок в чате, а не как ассистент.

         Когда делать нечего — коротко скажи, что собираешься делать дальше, и заверши ход.
         """;

    private async Task LoopAsync(CancellationToken ct)
    {
        var nextIdle = DateTime.UtcNow.AddSeconds(ДуматьРазВСекунд);
        var nextTurn = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && bot is { Joined: true })
        {
            // Спрашиваем бюджет ДО того, как тратиться: он единственный, кто
            // знает про лимиты, неудачи и ручной выключатель
            var verdict = Budget.Ask();
            var now = DateTime.UtcNow;

            if (!verdict.CanThink)
            {
                // Тратиться нельзя. Копить повод «пора подумать» бессмысленно:
                // когда разрешат, мир будет уже другим — считаем время заново
                nextIdle = now.AddSeconds(ДуматьРазВСекунд);
                nextTurn = now;
            }
            else if (now >= nextTurn)
            {
                // Деградация: при Slowed между ЛЮБЫМИ ходами держим паузу, иначе
                // «думать реже» касалось бы только скуки, но не разговоров
                double pace = Math.Max(1, verdict.Pace);

                string? next = null;
                lock (events)
                    if (events.Count > 0)
                        next = string.Join("\n", DrainLocked());

                if (next != null)
                {
                    await ThinkAsync(next, ct);
                }
                else if (ДуматьРазВСекунд > 0 && now >= nextIdle)
                {
                    await ThinkAsync("Ничего нового. Осмотрись и продолжай своё дело.", ct);
                }
                else
                {
                    pace = 0; // ходить не пришлось — сроки не двигаем
                }

                if (pace > 0)
                {
                    var done = DateTime.UtcNow;
                    nextIdle = done.AddSeconds(ДуматьРазВСекунд * pace);
                    nextTurn = done.AddSeconds(PaceBaseSeconds * (pace - 1));
                }
            }

            // Проверяем события часто, а думаем — редко: иначе сообщение
            // игрока ждало ответа до минуты
            await Task.Delay(1000, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>
    /// Опора для пауз при деградации. При ДуматьРазВСекунд = 0 бот думает только по
    /// событиям, но «реже» всё равно должно что-то значить — берём полминуты.
    /// </summary>
    private double PaceBaseSeconds => ДуматьРазВСекунд > 0 ? ДуматьРазВСекунд : 30;

    /// <summary>Округлённый срок для журнала: «через 4 мин».</summary>
    private static string Round(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{Math.Ceiling(span.TotalMinutes):0} мин"
            : $"{Math.Ceiling(span.TotalSeconds):0} с";

    private List<string> DrainLocked()
    {
        var list = new List<string>();
        while (events.Count > 0)
            list.Add(events.Dequeue());
        return list;
    }

    /// <summary>Один ход: сказать модели, что случилось, и выполнить, что она попросит.</summary>
    private async Task ThinkAsync(string what, CancellationToken ct = default)
    {
        if (session is not { } chat || bot is not { } b)
            return;
        if (!await thinkLock.WaitAsync(0, ct))
            return; // уже думаем — событие подождёт следующего хода

        // Расход считаем по РАЗНИЦЕ счётчиков сеанса: за один ход обращений
        // бывает несколько (ответы инструментов, довесок после отказа модели),
        // и разница ловит их все. Чужая реализация ILlmSession может не уметь
        // считать расход — тогда счётчик обращений ведём сами
        var meter = chat as ILlmUsageReporter;
        LlmUsage usageBefore = meter?.TotalUsage ?? default;
        int requestsBefore = meter?.Requests ?? 0;
        bool failed = false;
        bool aborted = false;

        try
        {
            var reply = await chat.SayAsync($"{what}\n\nОбстановка:\n{BotSnapshot.Describe(b)}", ct);
            int used = 0;

            while (reply.WantsTools)
            {
                // ГЛАВНОЕ ПРАВИЛО ПРОТОКОЛА: на КАЖДЫЙ запрос инструмента
                // обязан уйти ответ. Оборванный запрос остаётся в истории
                // навсегда, и каждый следующий ход отбивается ошибкой 400.
                // Поэтому лимит не обрывает цикл, а превращает оставшиеся
                // вызовы в честные отказы
                bool overLimit = used >= ДействийЗаХод;
                var results = new List<(string Id, string Result)>();

                foreach (var call in reply.Calls)
                {
                    if (overLimit)
                    {
                        results.Add((call.Id,
                            $"не выполнено: за один ход можно не больше {ДействийЗаХод} действий. " +
                            "Заверши ход, продолжишь следующим."));
                        continue;
                    }
                    used++;
                    var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
                    if (tool == null)
                    {
                        results.Add((call.Id, $"инструмента {call.Name} нет"));
                        continue;
                    }
                    try
                    {
                        string result = await tool.RunAsync(call.Input, ct);
                        OnThought?.Invoke($"{call.Name} → {result}");
                        results.Add((call.Id, result));
                    }
                    catch (OperationCanceledException)
                    {
                        results.Add((call.Id, "прервано: бот отключился"));
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Ни одна ошибка не роняет бота: модель узнаёт о ней и решает сама
                        results.Add((call.Id, $"ошибка: {ex.Message}"));
                        OnThought?.Invoke($"{call.Name} → ошибка: {ex.Message}");
                    }
                }

                reply = await chat.ToolResultsAsync(results, ct);
                if (overLimit)
                {
                    OnThought?.Invoke($"лимит действий за ход ({ДействийЗаХод}) — заканчиваю ход");
                    break;
                }
            }

            if (reply.Text.Length > 0)
                OnThought?.Invoke("мысль: " + reply.Text);
        }
        catch (OperationCanceledException)
        {
            // штатное завершение при отключении — это не неудача модели
            aborted = true;
        }
        catch (Exception ex)
        {
            failed = true;
            Budget.RecordFailure(ex.Message);
            OnThought?.Invoke($"нейросеть недоступна: {ex.Message}");
            // Разговор мог сломаться (оборванный запрос, отказ) — начинаем заново
            session?.Reset();
            await Task.Delay(5000, ct).ContinueWith(_ => { });
        }
        finally
        {
            if (meter != null)
                Budget.Record(meter.TotalUsage - usageBefore, meter.Requests - requestsBefore);
            else if (!aborted)
                Budget.Record(default, 1); // расход неизвестен, но обращение было

            if (!failed && !aborted)
                Budget.RecordSuccess();

            thinkLock.Release();
        }
    }
}
