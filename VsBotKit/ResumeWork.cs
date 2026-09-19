namespace VsBotKit;

/// <summary>
/// НАРЯД С ВОЗВРАТОМ: прервали — вернись и донеси остаток.
///
/// ЖИВОЙ СЛУЧАЙ (журнал заказчика, 23:31-23:32):
///   23:31:39 [тело] «поесть fruit-pinkapple» важнее — прерываю «команда добудь»
///   23:31:39 [наряд] медь: принёс 0 из 10 … 19 с — наряд ОТМЕНЁН
///   23:32:29 то же самое ещё раз
/// Заказчик: «часто кружится когда бежит, как я понимаю его колбасит от задач
/// параллельных?» — да. Прерывание было ПРАВИЛЬНЫМ (бот и правда голоден, порог
/// еды трогать не надо), неправильным было то, что наряд после перерыва умирал
/// целиком и начинался с нуля: бот уходил на делянку, ел, возвращался к дому,
/// снова уходил — и так по кругу, ни разу не донеся руду.
///
/// ВТОРОГО МЕХАНИЗМА ВОЗВРАТА ЗДЕСЬ НЕТ. Когда возвращаться, сколько раз и что
/// сказать, бросая, знает общий <see cref="Resume"/> — тот самый, что уже
/// доделывает карьер («[возврат] «карьер …» перебил «поесть …» через 32 с —
/// вернусь и доделаю»). Здесь только СВОЯ половина наряда: чем продолжать
/// (остаток заказа, <see cref="Remaining"/>) и как сложить отчёты
/// (<see cref="Merge"/>) — ровно как у карьера в <c>Quarry.KeepQuarryingAsync</c>.
/// </summary>
public static class OrderResume
{
    /// <summary>
    /// СКОЛЬКО ЕЩЁ ПРИНЕСТИ. Чистое правило, и оно отвечает на «с какого места
    /// возвращаться»: у наряда место — это не клетка, а недостача.
    ///
    /// Считать надо именно остаток, а не весь заказ заново: «!добудь медь 10»,
    /// принёс 4, перебила еда — на втором заходе просить надо 6. Попроси он
    /// снова 10, и наряд кончился бы четырнадцатью слитками (или не кончился
    /// бы вовсе), а отчёт врал бы про обе цифры.
    /// </summary>
    public static int Remaining(int asked, int gotSoFar) =>
        Math.Max(0, asked - Math.Max(0, gotSoFar));

    /// <summary>
    /// Сложить два отчёта наряда в один: работа шла в несколько заходов, а
    /// человеку нужен ОДИН итог.
    ///
    /// ЧИСТОЕ ПРАВИЛО. «Сколько просили» берётся у ПЕРВОГО отчёта (второй заход
    /// просил остаток, и показать «принёс 6 из 6» после первых четырёх — та же
    /// ложь, что и «принёс 0 из 10» с молчанием следом). «Чем кончилось» — у
    /// последнего, всё считаемое складывается.
    /// </summary>
    public static OrderReport Merge(OrderReport first, OrderReport then)
    {
        var gained = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, n) in first.Gained)
            gained[code] = gained.GetValueOrDefault(code) + n;
        foreach (var (code, n) in then.Gained)
            gained[code] = gained.GetValueOrDefault(code) + n;

        return new OrderReport(then.Success, then.Message, first.Wanted,
            first.Asked,
            first.Got + then.Got,
            first.Veins + then.Veins,
            first.Hauls + then.Hauls,
            first.Searches + then.Searches,
            first.Seconds + then.Seconds,
            gained,
            // Единица счёта у заходов одна и та же (её решает Ores.UnitFor), но
            // берём у последнего: реестр мог прийти уже после первого захода, и
            // тогда «штуки» первого — это не выбор человека, а незнание
            then.Unit)
        {
            // ПОМЕХА — У ПОСЛЕДНЕГО, И ТОЛЬКО У НЕГО. Она про то, что мешает
            // ПРЯМО СЕЙЧАС: игрок, сошедший с клетки между заходами, помехой
            // быть перестал (то же правило у RoadResume.Merge)
            Hindrance = then.Hindrance
        };
    }

    /// <summary>
    /// Что сказать про судьбу наряда одной строкой. ЧИСТОЕ ПРАВИЛО и
    /// обязательное: молчание про брошенную работу — это и есть та беда, с
    /// которой всё началось.
    ///
    /// Причину остановки самого наряда НЕ ЗАТИРАЕМ, а дописываем к ней приговор
    /// возврата: человеку нужно знать и почему наряд встал, и вернутся ли к нему.
    /// </summary>
    public static string Say(string reason, ResumeReport run)
    {
        string back = run.Back > 0
            ? $"; возвращался после перерыва {Resume.Times(run.Back)}"
            : "";
        // Тело не отбирали — приговор возврата человеку не нужен: там нет
        // ничего, кроме пересказа того, что наряд уже сказал сам
        return run.Finished || run.End == BodyArbiter.Ending.None
            ? reason + back
            : reason + back + "; " + run.Say;
    }

    /// <summary>
    /// Вести наряд так, чтобы рефлекс выживания не убивал его насовсем.
    ///
    /// Тело держит и отдаёт <see cref="Resume"/>; здесь только остаток заказа
    /// и общий срок. СРОК ОБЩИЙ НА ВЕСЬ НАРЯД, А НЕ НА ЗАХОД: иначе наряд с
    /// потолком в полчаса, прерываемый едой каждую минуту, жил бы вечно —
    /// каждый заход начинал бы отсчёт заново.
    /// </summary>
    /// <param name="log">Куда сказать про возврат. null — молча (для стенда).</param>
    public static async Task<OrderReport> KeepFetchingAsync(BotContext ctx, string wanted,
        int count, BodyArbiter.Importance level = BodyArbiter.Importance.Command,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var order = ctx.Order;
        double whole = order.MaxSeconds;
        var deadline = DateTime.UtcNow.AddSeconds(whole);
        OrderReport? total = null;

        var run = await ctx.Resume.RunAsync($"наряд {wanted} ×{count}", level,
            async (attempt, token) =>
            {
                int left = Remaining(count, total?.Got ?? 0);
                if (left == 0)
                    return (true, Setback.None);   // всё донесли ещё прошлым заходом

                if (attempt > 1)
                    log?.Invoke($"возвращаюсь к наряду «{wanted}»: осталось принести {left} " +
                                $"из {count} (заход {attempt})");

                double leftSeconds = (deadline - DateTime.UtcNow).TotalSeconds;
                if (leftSeconds <= 0)
                {
                    // СРОК ВСЕГО НАРЯДА — ЭТО И ВПРАВДУ СРОК, а не симптом:
                    // тут не помеха мешала, а времени больше нет. Слово
                    // честное и постоянное — второго захода не будет
                    log?.Invoke($"на наряд «{wanted}» отведённые {whole:0} с вышли — " +
                                "нового захода не начинаю");
                    return (false, Setback.OutOfTime);
                }

                // Потолок наряда подменяем на ОСТАТОК общего срока и возвращаем
                // как было: тот же приём, которым сам наряд урезает время
                // разведке и поручению
                order.MaxSeconds = Math.Max(30, leftSeconds);
                OrderReport one;
                try
                {
                    // РАЗВЕДКИ — ТОЖЕ НА ВЕСЬ НАРЯД, а не на заход. Ровно та же
                    // причина, что и у срока строкой выше: перебиваемый едой
                    // наряд получал по три новые разведки на каждый заход и
                    // обходил округу по четвёртому кругу («бегал вперед назад»
                    // словами заказчика, журнал 12:45). Сколько их было, знает
                    // сложенный отчёт — второй памяти об этом не заводим.
                    //
                    // И ПОДГОТОВКА — ТОЖЕ НА ВЕСЬ НАРЯД. «attempt > 1» здесь
                    // значит «это возврат»: разбирать запасы и бегать за ними
                    // домой на каждом заходе стоило заказчику целого захода
                    // (журнал 16.08, 12:56:32-12:57:00 — двадцать пять секунд
                    // из двадцати пяти ушли на поход за топором вместо меди).
                    // Разбор нужен снова только тогда, когда в перерыве
                    // сломалось то, без чего наряд бессмыслен, — и решает это
                    // сам наряд (MiningOrder.ShouldReplenish), потому что
                    // только он знает, что у него MustHave
                    one = await order.FetchAsync(wanted, left, token, total?.Searches ?? 0,
                        resumed: attempt > 1);
                }
                finally
                {
                    order.MaxSeconds = whole;
                }

                total = total is null ? one : Merge(total, one);
                // ОТВЕЧАЕМ ЗА СЕБЯ САМИ: возврат знает только, отбирали ли тело,
                // а «донёс сколько просили» — мерка самого наряда.
                //
                // И ПОМЕХУ НАЗЫВАЕМ ВСЛУХ — это и есть просьба заказчика «если
                // мешает игрок, то пробовать заново, а не сбрасывать, и вообще
                // на все бы задачи так». Своего решения тут нет: возврат
                // спросит одно чистое правило (Resume.Passing), наряд только
                // переводит своё слово в общее.
                //
                // СРОК ВСЕГО НАРЯДА ГАСИТ ПОМЕХУ ЗАХОДА (Resume.WithinBudget):
                // «игрок на клетке» стоит повтора, только пока на этот повтор
                // есть время. Иначе второй заход упёрся бы в тот же истёкший
                // срок в первую же секунду — и так до потолка повторов
                return (one.Success,
                    Resume.WithinBudget(one.Hindrance,
                        (deadline - DateTime.UtcNow).TotalSeconds));
            }, ct);

        if (total is null)
            // До первого шага не дошло: тела не дали или оборвали сразу.
            // Молчать тут нельзя — человек ждёт ответа на своё «!добудь»
            return new OrderReport(false, run.Say, wanted, count, 0, 0, 0, 0, 0,
                new Dictionary<string, int>(), ctx.Ores?.UnitFor(wanted) ?? MetalCount.Штуки);

        // Успех меряем ПО ФАКТУ в сумке и в сундуке, а не по тому, что ответил
        // последний заход: он-то просил остаток
        return total with
        {
            Success = total.Got >= count,
            Message = Say(total.Message, run)
        };
    }

    /// <summary>
    /// НАРЯД ИЗ НЕСКОЛЬКИХ СТРОК: «медь 20, олово 8» — одним заданием, одним
    /// выходом из дома.
    ///
    /// Заказчик 17.08: «добавить бы допустим в добыче и еще где логично
    /// возможность добавить несколько вещей, а не один ресурс». Второго
    /// механизма возврата тут нет: каждая строка идёт тем же
    /// <see cref="KeepFetchingAsync"/>, что и одиночный наряд, — со своим
    /// возвратом после обеда и своим остатком. Здесь только ПОРЯДОК: строки
    /// берутся по одной, и брошенная на полпути не отменяет остальных.
    ///
    /// ПЕРВАЯ НЕУДАЧА НЕ ОСТАНАВЛИВАЕТ ВЕСЬ НАРЯД, и это нарочно: «меди тут
    /// нет» — не повод не принести олово, за которым бот всё равно уже вышел.
    /// А вот отмена человеком («стоп») останавливает: это смена цели, а не
    /// неудача.
    /// </summary>
    public static async Task<IReadOnlyList<OrderReport>> KeepFetchingAllAsync(BotContext ctx,
        IReadOnlyList<OrderLine> lines, BodyArbiter.Importance level = BodyArbiter.Importance.Command,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var reports = new List<OrderReport>(lines.Count);
        if (lines.Count > 1)
            log?.Invoke($"наряд из {lines.Count} строк: {OrderLines.Join(lines)} — " +
                        "беру по одной, в этом порядке");

        foreach (var line in lines)
        {
            if (ct.IsCancellationRequested)
            {
                // МОЛЧАТЬ ПРО НЕТРОНУТЫЕ СТРОКИ НЕЛЬЗЯ: человек, заказавший три
                // вещи и получивший отчёт про одну, решит, что бот забыл
                reports.Add(new OrderReport(false, "не начата: наряд остановлен",
                    line.What, line.Count, 0, 0, 0, 0, 0, new Dictionary<string, int>(),
                    ctx.Ores?.UnitFor(line.What) ?? MetalCount.Штуки));
                continue;
            }
            reports.Add(await KeepFetchingAsync(ctx, line.What, line.Count, level, log, ct));
        }
        return reports;
    }
}

/// <summary>
/// ДОРОГА С ВОЗВРАТОМ: прервали — вернись и домости остаток.
///
/// Та же беда, что и у наряда, только дороже: дорога на сотню рядов идёт
/// минутами, и рефлекс еды или боя перебивает её почти наверняка. Раньше
/// перебитая дорога умирала на том ряду, где её застали, и человек видел
/// полотно в треть длины без единого слова о том, что его бросили.
///
/// Механизм возврата общий (<see cref="Resume"/>), здесь только своя половина:
/// с какого ряда класть дальше (<see cref="ResumeFrom"/>) и как сложить отчёты
/// (<see cref="Merge"/>).
/// </summary>
public static class RoadResume
{
    /// <summary>
    /// С КАКОЙ КЛЕТКИ КЛАСТЬ ДАЛЬШЕ. Чистое правило.
    ///
    /// <paramref name="from"/> — начало ТОГО ЗАХОДА, что сейчас кончился, а не
    /// начало всей дороги, и это важно. Ряды считаются от начала отрезка
    /// (<see cref="RoadPlan.Rows"/>), поэтому «положено 12 рядов» указывает на
    /// 13-й ряд только в раскладке, посчитанной от той же точки. Взяли бы
    /// начало всей дороги — на третьем возврате бот перескочил бы через
    /// несколько рядов и оставил в полотне дыру.
    ///
    /// Ряды кончились — возвращаем конец дороги: класть больше нечего, и
    /// пусть это скажет сама дорога, а не молчание.
    ///
    /// ВЫСОТУ БЕРЁМ У ДОРОГИ, А НЕ У ОСЕВОЙ ЛИНИИ, и это правка приёмки.
    /// <see cref="RoadPlan.Rows"/> раздаёт клеткам Y ПРЯМОЙ от начала к концу —
    /// в осевой линии это честно названо подсказкой, где искать землю. Пока
    /// полотно держало ту же прямую, подсказка совпадала с делом. Теперь
    /// полотно идёт ПО ЗЕМЛЕ, и на живом рельефе настоящий уровень уходит от
    /// прямой на метры: второй заход закреплял бы начало на высоте, где дороги
    /// нет, и упирался в «до начала дороги не дошёл» (журнал 19.08, пять раз
    /// подряд). Дорога свой последний уровень знает и отдаёт его отчётом
    /// (<see cref="RoadReport.LastLevel"/>) — берём оттуда.
    ///
    /// Клетка X/Z от этого не меняется НИ НА ШАГ: ось считается по X и Z, а Y в
    /// ней — только подсказка. Меняется ровно то, что и должно, — высота.
    ///
    /// <paramref name="lastLevel"/> не сказан (<see cref="RoadReport.Unknown"/>)
    /// — остаётся прежняя подсказка прямой: соврать про высоту хуже, чем
    /// оставить приблизительную.
    /// </summary>
    public static BlockPos ResumeFrom(BlockPos from, BlockPos to, int width, int rowsDone,
        int lastLevel = RoadReport.Unknown)
    {
        if (rowsDone <= 0)
            return from;
        var rows = RoadPlan.Rows(from, to, width);
        var centre = rowsDone < rows.Count ? rows[rowsDone].Centre : to;
        return lastLevel == RoadReport.Unknown
            ? centre
            : new BlockPos(centre.X, RoadPlan.FeetCell(lastLevel), centre.Z);
    }

    /// <summary>
    /// Сложить два отчёта дороги в один. ЧИСТОЕ ПРАВИЛО.
    ///
    /// «Сколько рядов у дороги» и «сколько клеток полотна» берутся у ПЕРВОГО
    /// отчёта: это про всю дорогу. Второй заход считал остаток, и показать его
    /// числа значило бы объявить дорогу короче, чем её заказали, — ровно та
    /// ошибка, из-за которой урезанный заход когда-то отчитывался «341 из 341».
    /// </summary>
    public static RoadReport Merge(RoadReport first, RoadReport then) =>
        new(then.Stop, then.Reason, then.StoppedAt,
            then.Material.Length > 0 && then.Material != "—" ? then.Material : first.Material,
            first.Rows,
            first.RowsDone + then.RowsDone,
            first.Cells,
            first.Paved + then.Paved,
            first.Steps + then.Steps,
            first.Cleared + then.Cleared,
            first.Skipped + then.Skipped,
            first.Seconds + then.Seconds)
        {
            // ВЫСОТА — У ПОСЛЕДНЕГО, КТО КЛАЛ. Второй заход мог не положить
            // ни ряда (встал сразу): тогда дорога кончается там же, где и
            // кончалась, и высоту отдаёт первый
            LastLevel = then.LastLevel != RoadReport.Unknown ? then.LastLevel : first.LastLevel,
            // ПОМЕХА — У ПОСЛЕДНЕГО, И ТОЛЬКО У НЕГО. Она про то, что мешает
            // ПРЯМО СЕЙЧАС: игрок, ушедший с клетки между заходами, помехой
            // быть перестал, и тащить его в итог значило бы объявить дорогу
            // остановленной тем, чего уже нет
            Hindrance = then.Hindrance
        };

    /// <summary>Приговор возврата дописывается к причине остановки — см. <see cref="OrderResume.Say"/>.</summary>
    public static string Say(string reason, ResumeReport run) => OrderResume.Say(reason, run);

    /// <summary>
    /// Мостить так, чтобы рефлекс выживания не убивал дорогу насовсем.
    ///
    /// Срок общий на всю дорогу, а не на заход: см. разбор у
    /// <see cref="OrderResume.KeepFetchingAsync"/>.
    /// </summary>
    public static async Task<RoadReport> KeepBuildingAsync(BotContext ctx, BlockPos from,
        BlockPos to, int width, BodyArbiter.Importance level = BodyArbiter.Importance.Command,
        Action<string>? log = null, CancellationToken ct = default)
    {
        var roads = ctx.Roads;
        double whole = roads.MaxSeconds;
        var deadline = DateTime.UtcNow.AddSeconds(whole);
        RoadReport? total = null;
        var start = from;

        // СЛОВО, КОТОРОЕ УШЛО ВОЗВРАТУ, — ОНО ЖЕ УЙДЁТ И ЧЕЛОВЕКУ.
        //
        // Здесь этой переменной не было, и наружу шла помеха от Merge, то есть
        // СЫРОЕ слово последнего захода. Живой замер приёмки 10.09: дорога,
        // истратившая ВЕСЬ отведённый срок, докладывала наверх ПРОХОДЯЩЕЕ слово
        // «мир ещё не доехал — он придёт», то есть «берись заново», — после
        // того как её собственный возврат уже решил OutOfTime и работу
        // похоронил. Одна остановка, два разных слова, и второе врёт.
        //
        // Ровно эту дыру карьер закрыл в ту же ночь (Quarry.KeepQuarryingAsync,
        // Hindrance = помеха) и назвал её настоящей ложью. Держать одну механику
        // в двух местах и дать им разъехаться — закон 2, и разъехались они здесь
        var помеха = Setback.None;

        var run = await ctx.Resume.RunAsync($"дорога {from}..{to}", level,
            async (attempt, token) =>
            {
                if (attempt > 1)
                    log?.Invoke($"возвращаюсь на дорогу: положено {total?.RowsDone ?? 0} рядов " +
                                $"из {total?.Rows ?? 0}, кладу дальше с {start} (заход {attempt})");

                double leftSeconds = (deadline - DateTime.UtcNow).TotalSeconds;
                if (leftSeconds <= 0)
                {
                    log?.Invoke($"на дорогу отведённые {whole:0} с вышли — нового захода не начинаю");
                    помеха = Setback.OutOfTime;
                    return (false, помеха);
                }

                roads.MaxSeconds = Math.Max(15, leftSeconds);
                RoadReport one;
                try
                {
                    // «ЗАХОД НЕ ПЕРВЫЙ» ГОВОРИМ САМОЙ ДОРОГЕ, а не догадываемся
                    // за неё. Без этого слова каждый заход считал полотно
                    // неначатым и уходил домой за камнем — семь заходов подряд
                    // по нулю рядов (журнал 30.08; разбор у Roads.BuildAsync,
                    // параметр resumed). Ровно так же и по той же причине
                    // говорит наряду свой возврат: «resumed: attempt > 1»
                    one = await roads.BuildAsync(start, to, width, token,
                        resumed: attempt > 1);
                }
                finally
                {
                    roads.MaxSeconds = whole;
                }

                // Следующий заход считает ряды ОТ СВОЕГО начала — потому и
                // спрашиваем остаток от начала этого захода, а не всей дороги
                start = ResumeFrom(start, to, width, one.RowsDone, one.LastLevel);
                total = total is null ? one : Merge(total, one);
                // И ПОМЕХУ НАЗЫВАЕМ ВСЛУХ — ЭТО И ЕСТЬ ПРОСЬБА ЗАКАЗЧИКА «если
                // мешает игрок, то пробовать заново, а не сбрасывать». Своего
                // решения тут нет: возврат спросит одно чистое правило
                // (Resume.Passing), а дорога только переводит своё слово в общее.
                //
                // ОДНО УТОЧНЕНИЕ, И ОНО ЧЕСТНОЕ. Setback.Unfinished ОБЕЩАЕТ
                // возврату, что заход сделал свою долю, — на этом обещании
                // стоит обнуление счёта повторов (иначе дорога в сто рядов
                // бросалась бы на четвёртой сотне клеток). Заход, не положивший
                // НИ ОДНОГО ряда, обещания не выполняет: продолжать его нечем
                // (ResumeFrom вернёт ту же клетку), и повтор вышел бы вечным
                // кругом до конца отведённого дороге срока
                //
                // И ЕЩЁ ОДНО, ПРО СРОК. У дороги он общий на всё полотно, и
                // помеха одного захода не вправе его пережить: заход, встав по
                // временной помехе за секунду до истечения срока, получал бы
                // повтор, который упрётся в тот же срок в первую же клетку, —
                // и так до потолка (Resume.WithinBudget)
                //
                // КУДА ЭТО ВЕДЁТ, СКАЗАНО ПРЯМО, ЧТОБЫ ЧИТАТЕЛЬ НЕ ЖДАЛ ЛИШНЕГО.
                // Пара «временная помеха дожила до срока → возврат дал второй
                // заход» у дороги ПУСТА ПО УСТРОЙСТВУ, и вот арифметика: срок
                // считается от начала всей дороги (Roads.BuildAsync, deadline),
                // а заходу отдаётся ОСТАТОК этого же срока (строка ниже,
                // Math.Max(15, leftSeconds)). Значит всякий RoadStop.Timeout
                // приходит сюда с истрачённым бюджетом, WithinBudget гасит
                // слово ВСЕГДА, и повтора по сроку не бывает. Замерено приёмкой
                // 10.09: заходов 1, слово захода NotMine, наверх OutOfTime.
                //
                // ЭТО НЕ БЕДА, А ГРАНИЦА: время кончилось совсем, а не у
                // захода, и продолжать вправду нечем. Повтор у дороги живёт на
                // ДРУГИХ словах — на тех, что приходят ДО срока (игрок на
                // клетке, недоехавший мир, переменившийся мир). Тот же разбор и
                // тем же выводом стоит у карьера (Quarry.KeepQuarryingAsync):
                // если понадобится второй заход ПОСЛЕ срока, мерить его надо
                // РАБОТОЙ (потолок рядов на вызов, как Roads.JudgeRun), а не
                // часами, и третьего числа для этого заводить не придётся
                помеха = Resume.WithinBudget(
                    one.Hindrance == Setback.Unfinished && one.RowsDone <= 0
                        ? Setback.None
                        : one.Hindrance,
                    (deadline - DateTime.UtcNow).TotalSeconds);
                return (one.Success, помеха);
            }, ct);

        // ДОРОГА КОНЧИЛАСЬ СОВСЕМ — И ЛЕСА ПОСЛЕ НЕЁ ОСТАВАТЬСЯ НЕ ДОЛЖНЫ.
        //
        // Один заход снимает свои леса только тогда, когда довёл дорогу до
        // конца: иначе он снял бы подпорку, по которой сам же полезет
        // следующим заходом. А вот ЗДЕСЬ заходов больше не будет, и всё, что
        // осталось стоять, останется навсегда. Забытые леса посреди чужой
        // постройки хуже, чем не построенные вовсе, — поэтому убираем даже
        // после неудачной дороги, и недоубранное называем вслух
        if (ctx.Temp.TakeDownAfterWork && ctx.Temp.Count > 0)
        {
            var cleanup = await ctx.Temp.TakeDownAsync(ct: ct);
            if (!cleanup.Success)
                log?.Invoke(cleanup.ToString());
        }

        if (total is null)
            return new RoadReport(RoadStop.Cancelled, run.Say, to, "—", 0, 0, 0, 0, 0, 0, 0, 0);

        // ПОМЕХА — ТА, ЧТО УШЛА ВОЗВРАТУ, а не сырое слово последнего захода:
        // разбор у самой переменной, живой замер приёмки 10.09
        return total with { Reason = Say(total.Reason, run), Hindrance = помеха };
    }
}
