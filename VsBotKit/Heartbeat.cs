namespace VsBotKit;

/// <summary>
/// ЧТО БОТ ДЕЛАЛ, ПОКА МОЛЧАЛ, — одной строкой.
/// </summary>
/// <param name="Quiet">Сколько секунд не было сказано ни слова.</param>
/// <param name="Busy">Чем занято тело (пусто — ничем).</param>
/// <param name="Skill">Какой навык идёт (пусто — никакой).</param>
/// <param name="Behavior">Кто взял ход последним (пусто — никто).</param>
/// <param name="Where">Где стоит (пусто — своей позиции нет).</param>
public readonly record struct Pulse(double Quiet, string Busy, string Skill,
    string Behavior, string Where)
{
    /// <summary>
    /// СЛОВАМИ. Занят — называем дело; не занят — говорим и это: «жив, но
    /// без дела» и «умер молча» со стороны выглядят одинаково, а разница
    /// между ними и есть весь смысл пульса.
    /// </summary>
    public string Say()
    {
        var что = new List<string>();
        if (Busy.Length > 0)
            что.Add($"тело держит «{Busy}»");
        if (Skill.Length > 0)
            что.Add($"навык «{Skill}»");
        if (Behavior.Length > 0)
            что.Add($"ход у {Behavior}");
        string дело = что.Count > 0
            ? string.Join(", ", что)
            : "ничем не занят — никто не взял ни тело, ни ход";
        return $"молчу {Quiet:0} с, а живу: {дело}" +
               (Where.Length > 0 ? $"; стою {Where}" : "");
    }

    public override string ToString() => Say();
}

/// <summary>
/// ПУЛЬС: ДЛИННОЕ МОЛЧАНИЕ РАБОТАЮЩЕГО БОТА ЗАПРЕЩЕНО.
///
/// ЖИВОЙ СЛУЧАЙ, ради которого механизм появился (ночь 18.08, слова заказчика):
/// «и только что бот затупил выходить через дверь и стал искать выход и ломать
/// дом изнутри, в логах записи не было, почему-то они у бота не писались
/// 20 минут целых, что странно».
///
/// Разбор журнала показал сразу ДВЕ причины молчания, и вторая — вот эта.
/// Первая жила в окне управления (закладка журнала не переживала обрезку
/// кольца, см. <c>ControlPanel.LogSlice</c>). А вторая — в самом боте:
/// с 00:24:59 до 00:41:16 в файле журнала стоят 25 строк на семнадцать минут,
/// и в минутах 00:26, 00:30, 00:31, 00:36, 00:38, 00:40 нет НИ ОДНОЙ. Бот при
/// этом работал: ходил, пытался разделать тело, ел. Просто ни один из этих
/// заходов ничего не говорил, потому что ничего НОВОГО не случилось.
///
/// ПОЧЕМУ ЭТО НАРУШЕНИЕ ПРАВИЛА 4, а не мелочь. Правило требует честных
/// отказов вместо молчания. Молчаливая РАБОТА ничем не лучше молчаливого
/// отказа: со стороны «бот занят делом», «бот встал в цикле», «поток журнала
/// умер» и «процесс висит» выглядят ОДИНАКОВО — как пустой журнал. Человек
/// не может отличить их ничем, и именно это и случилось.
///
/// ЧЕМ ЭТО НЕ <see cref="LogRepeats"/>. Тот ГЛУШИТ повторяющуюся строку, когда
/// её слишком много. Пульс — ровно наоборот: он ГОВОРИТ, когда строк нет
/// совсем. Один прячет шум, другой запрещает тишину; сойтись в одном
/// механизме им негде.
///
/// ЧЕМ ЭТО НЕ <see cref="Hopeless"/>. Тот ловит БЕСПЛОДНЫЙ круг — работу,
/// которая не двигается. Пульс не судит о пользе работы вовсе: он меряет
/// одно — сколько времени бот не произнёс ни слова.
///
/// Времени своего у механизма нет: его приносят снаружи (правило 7 проекта),
/// поэтому правило проверяется стендом без всякого мира и сервера.
/// </summary>
public sealed class Heartbeat
{
    private readonly object gate = new();
    private DateTime lastWord;
    private DateTime lastPulse;
    private long beats;

    /// <summary>
    /// Сколько секунд молчания терпеть. Ноль и меньше — пульс выключен
    /// (законный выбор: так работал бот до этой правки).
    ///
    /// Заводские шестьдесят — не круглое число ради круглого: это ЧЕТВЕРТЬ
    /// самого короткого молчания, которое заказчик уже счёл поломкой. Реже —
    /// и двадцатиминутная дыра осталась бы дырой из двадцати строк вместо
    /// одной; чаще — пульс полез бы в журнал посреди живой работы.
    /// </summary>
    public double QuietSeconds { get; set; } = 60;

    /// <summary>Сколько раз пульс уже говорил за запуск.</summary>
    public long Beats { get { lock (gate) return beats; } }

    /// <summary>Куда говорить. Не подключено — пульс молчит и не мешает.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// БОТ ЧТО-ТО СКАЗАЛ. Зовётся на КАЖДУЮ строку журнала — в этом весь
    /// смысл: пульс не знает и не должен знать, кто именно говорил.
    /// </summary>
    public void Heard(DateTime now)
    {
        lock (gate)
            lastWord = now;
    }

    /// <summary>
    /// ПОРА ЛИ ГОВОРИТЬ — чистое правило.
    ///
    /// Считаем от ПОСЛЕДНЕГО СЛОВА, а не от последнего пульса: пока бот
    /// говорит сам, пульсу сказать нечего. От последнего пульса отсчёт нужен
    /// вторым: сам пульс — тоже слово, и без этого он повторялся бы каждый тик.
    ///
    /// Первое слово молчанием не считается: у только что поднятого бота
    /// <paramref name="lastWord"/> ещё нулевой, и без этой оговорки пульс
    /// закричал бы «молчу 738000 часов» на первом же тике.
    /// </summary>
    /// <param name="now">Текущее время.</param>
    /// <param name="lastWord">Когда бот сказал последнее слово (default — ещё не говорил).</param>
    /// <param name="quietSeconds">Сколько секунд молчания терпеть (0 и меньше — не пульсировать).</param>
    public static (bool Due, double Quiet) DueRule(DateTime now, DateTime lastWord,
        double quietSeconds)
    {
        if (quietSeconds <= 0 || lastWord == default)
            return (false, 0);
        double quiet = (now - lastWord).TotalSeconds;
        return (quiet >= quietSeconds, quiet);
    }

    /// <summary>
    /// Спросить пульс. Зовётся каждый заход цикла способностей; вернул строку —
    /// её надо сказать, вернул null — молчать ещё рано.
    /// </summary>
    /// <param name="now">Текущее время.</param>
    /// <param name="busy">Чем занято тело (BodyArbiter.Busy).</param>
    /// <param name="skill">Какой навык идёт (Skills.CurrentSkill).</param>
    /// <param name="behavior">Кто взял ход последним.</param>
    /// <param name="where">Где стоит.</param>
    public Pulse? Check(DateTime now, string busy = "", string skill = "",
        string behavior = "", string where = "")
    {
        lock (gate)
        {
            // Пульс — тоже слово: без этого он звучал бы каждый тик, пока
            // молчание длится, и вместо дыры вышла бы стена
            var since = lastPulse > lastWord ? lastPulse : lastWord;
            var (due, quiet) = DueRule(now, since, QuietSeconds);
            if (!due)
                return null;
            lastPulse = now;
            beats++;
            return new Pulse(quiet, busy ?? "", skill ?? "", behavior ?? "", where ?? "");
        }
    }

    /// <summary>Спросить и сразу сказать. Возвращает сказанное или null.</summary>
    public string? Beat(DateTime now, string busy = "", string skill = "",
        string behavior = "", string where = "")
    {
        if (Check(now, busy, skill, behavior, where) is not { } pulse)
            return null;
        string line = pulse.Say();
        OnLog?.Invoke(line);
        return line;
    }
}
