namespace VsBotKit;

/// <summary>Что задумано в клетке готовой постройки.</summary>
public enum Meant
{
    /// <summary>
    /// ПУСТО: ход над дорожным полотном, объём карьера, прокопанная штольня.
    /// Блок тут — ЛИШНИЙ: по дороге с недорубленным кустом не пройти, а
    /// «выкопанная» яма с забытым столбом посреди — это не выкопанная яма.
    /// </summary>
    Clear,

    /// <summary>
    /// ТВЕРДЬ: полотно дороги, ступени пандуса. Пусто тут — НЕДОСТАЧА.
    /// </summary>
    Solid
}

/// <summary>Клетка раскладки: где она и что в ней задумано.</summary>
public sealed record MeantCell(BlockPos Where, Meant Meant)
{
    public override string ToString() =>
        $"{Where}: {(Meant == Meant.Solid ? "должен стоять блок" : "должно быть пусто")}";
}

/// <summary>Чем расходится задуманное со стоящим.</summary>
public enum FaultKind
{
    /// <summary>ЛИШНИЙ блок: задумано пусто, а что-то стоит.</summary>
    Extra,

    /// <summary>НЕДОСТАЧА: задуман блок, а клетка пуста.</summary>
    Missing
}

/// <summary>Одно расхождение «задумано против стоит».</summary>
/// <param name="What">Что стоит (у лишнего) или чего нет (у недостачи).</param>
public sealed record BuildFault(BlockPos Where, FaultKind Kind, string What)
{
    public override string ToString() => Kind == FaultKind.Extra
        ? $"лишний {What} в {Where}"
        : $"нет блока в {Where}";
}

/// <summary>
/// Что бот ВИДИТ в клетке. Три состояния, а не два: «не видно» — это не
/// «пусто», и валить их вместе значит объявить недостачей всё, что осталось
/// за границей прогруженных чанков.
/// </summary>
/// <param name="Known">Клетка вообще известна боту (чанк пришёл).</param>
/// <param name="Code">Что стоит (null — пусто).</param>
public readonly record struct Standing(bool Known, string? Code)
{
    /// <summary>Чанка нет — про клетку не известно ничего.</summary>
    public static Standing Unseen => new(false, null);

    /// <summary>Клетка видна и пуста.</summary>
    public static Standing Empty => new(true, null);

    /// <summary>Клетка видна, в ней такой блок.</summary>
    public static Standing Block(string code) => new(true, code);
}

/// <summary>Чем кончилась сверка «задумано против стоит».</summary>
/// <param name="What">Что проверяли: «дорога», «карьер».</param>
/// <param name="Planned">Сколько клеток в раскладке.</param>
/// <param name="Unseen">Сколько клеток разглядеть не вышло (чанк не пришёл).</param>
/// <param name="Extra">Сколько лишних блоков нашлось.</param>
/// <param name="Removed">Сколько лишних снято ПО ФАКТУ.</param>
/// <param name="Left">Лишние, которые снять не вышло, — с причиной.</param>
/// <param name="Missing">Недостающие клетки.</param>
public sealed record CheckReport(string What, int Planned, int Unseen, int Extra, int Removed,
    IReadOnlyList<string> Left, IReadOnlyList<BuildFault> Missing, double Seconds)
{
    /// <summary>
    /// Успех — постройка совпала с замыслом ЦЕЛИКОМ. Непроверенные клетки
    /// успехом не считаются: «я не смотрел» и «там всё в порядке» — разные
    /// новости, и выдавать первую за вторую значит врать.
    /// </summary>
    public bool Success => Left.Count == 0 && Missing.Count == 0 && Unseen == 0;

    public override string ToString() =>
        BuildCheck.Say(What, Planned, Unseen, Extra, Removed, Left, Missing) +
        $"; {Seconds:0} с";
}

/// <summary>
/// СВЕРКА ГОТОВОЙ ПОСТРОЙКИ С ЗАМЫСЛОМ: пройти по своей же раскладке и
/// сравнить, что стоит, с тем, что задумано.
///
/// ЗАКАЗ ЧЕЛОВЕКА, дословно: «надо проверять объекты потом на лишние блоки».
///
/// ВТОРОЙ РАСКЛАДКИ ЗДЕСЬ НЕТ. Раскладку считают те же чистые правила, что
/// строили: <see cref="RoadPlan.MeantCells"/> у дороги (она сложена из
/// <see cref="RoadPlan.Layers"/> и <see cref="RoadPlan.Trench"/>),
/// <see cref="Quarry.MeantCells"/> у карьера (из его же
/// <see cref="QuarryPlan"/>). Здесь только СРАВНЕНИЕ и уборка лишнего —
/// то, чего в проекте не было ни у кого.
///
/// СРАВНЕНИЕ — ЧИСТОЕ ПРАВИЛО (<see cref="Faults"/>): ни мира, ни сервера, ни
/// тела. Живьём дорогу не проверить — сперва надо её построить, а это полчаса
/// на отрезок; правило же считается на стенде за миг, и тест краснеет ровно
/// тогда, когда сравнение перестаёт замечать расхождение.
///
/// ТРИ ИСХОДА, А НЕ ДВА. Клетка бывает не только «как задумано» и «не как
/// задумано», но и НЕ ВИДНА: чанк не пришёл, бот туда не подходил. Считать
/// такую клетку пустой значит объявить недостачей полдороги, а считать целой —
/// выдать «проверено» за «не смотрел». Она считается отдельно и называется
/// отдельно.
///
/// Класс — МЕХАНИЗМ. Проверять ли постройку после работы, решает роль
/// (ручка <c>ПроверятьПостройкуПослеРаботы</c>).
/// </summary>
public sealed class BuildCheck
{
    private readonly BotContext ctx;

    public BuildCheck(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    /// <summary>
    /// Сверять ли постройку, когда работа кончилась. Одна задвижка на всех, кто
    /// сверяется (дорога, карьер): заведи каждый свою — и «проверять ли
    /// постройку» пришлось бы выключать в двух местах, а забытое второе место
    /// выглядело бы как самовольство бота.
    /// </summary>
    public bool AfterWork { get; set; } = true;

    /// <summary>
    /// Сколько лишних блоков снимать за одну сверку. Предел МЕХАНИЗМА, а не
    /// решение «стоит ли»: сотня лишних блоков — это не сверка, а вторая
    /// стройка, и человек должен узнать об этом числом, а не по тому, что бот
    /// не вернулся до утра.
    /// </summary>
    public int MaxRemove { get; set; } = 64;

    /// <summary>Потолок по времени на всю сверку, секунд.</summary>
    public double MaxSeconds { get; set; } = 180;

    /// <summary>Сколько секунд отводится на подбор своего же выломанного.</summary>
    public double PickUpSeconds { get; set; } = 4;

    // ---------------- чистое правило ----------------

    /// <summary>
    /// ЗАДУМАНО ПРОТИВ СТОИТ — чистое правило. На вход раскладка и «что видно»,
    /// на выход список расхождений и число неразглядённых клеток.
    ///
    /// ПРАВИЛ РОВНО ТРИ, и каждое закрыто тестом:
    ///   1) клетку не видно — она не расхождение, а НЕПРОВЕРЕННАЯ, и считается
    ///      отдельно;
    ///   2) задумано пусто, а блок стоит — ЛИШНИЙ (его и просил снимать человек);
    ///   3) задуман блок, а пусто — НЕДОСТАЧА (её просили назвать).
    ///
    /// ЧЕМ ИМЕННО замощена клетка, правило НЕ проверяет, и это осознанно: у
    /// дороги покрытие выбирается на ходу (кончился булыжник — пошла дорожная
    /// плита), у карьера пандус какой был, такой и остался. Объявить чужой, но
    /// годный блок «неправильным» — значит переложить дорогу заново из-за смены
    /// материала, о которой бот честно докладывал в журнал.
    /// </summary>
    /// <param name="plan">Раскладка: та же, по которой строили.</param>
    /// <param name="look">Что видно в клетке.</param>
    public static (IReadOnlyList<BuildFault> Faults, int Unseen) Faults(
        IEnumerable<MeantCell> plan, Func<BlockPos, Standing> look)
    {
        var faults = new List<BuildFault>();
        int unseen = 0;
        foreach (var cell in plan)
        {
            var now = look(cell.Where);
            if (!now.Known)
            {
                unseen++;
                continue;
            }
            bool solid = now.Code is { Length: > 0 };
            if (cell.Meant == Meant.Clear && solid)
                faults.Add(new BuildFault(cell.Where, FaultKind.Extra, now.Code!));
            else if (cell.Meant == Meant.Solid && !solid)
                faults.Add(new BuildFault(cell.Where, FaultKind.Missing, ""));
        }
        return (faults, unseen);
    }

    /// <summary>
    /// СЛОЖИТЬ РАСКЛАДКУ, где твердь ПОБЕЖДАЕТ пустоту — чистое правило.
    ///
    /// ЖИВОЙ РАЗБОР, ради которого правило и понадобилось. У дороги ход над
    /// полотном (<see cref="RoadPlan.Trench"/>) отсчитывается ОТ КЛЕТКИ НОГ, а
    /// верх ступеньки (<see cref="RoadPlan.Layers"/>) в этой самой клетке ног и
    /// лежит. Сложи раскладку как попало — и собственный полублок ступеньки
    /// окажется «лишним блоком в ходе», а сверка снесла бы его на каждой
    /// ступеньке дороги, то есть разобрала бы построенное только что.
    /// </summary>
    public static IReadOnlyList<MeantCell> Merge(IEnumerable<MeantCell> cells)
    {
        var byCell = new Dictionary<BlockPos, Meant>();
        var order = new List<BlockPos>();
        foreach (var cell in cells)
        {
            if (!byCell.TryGetValue(cell.Where, out var was))
                order.Add(cell.Where);
            else if (was == Meant.Solid)
                continue;                   // твердь уже назначена — не отменяем
            byCell[cell.Where] = cell.Meant;
        }
        return order.Select(p => new MeantCell(p, byCell[p])).ToList();
    }

    /// <summary>
    /// ЧТО СКАЗАТЬ ПРО СВЕРКУ — чистое правило, и молчаливого ответа среди
    /// возможных нет. Лишнее и недостающее называются КООРДИНАТАМИ: по фразе
    /// «есть расхождения» человек не найдёт на дороге ничего.
    /// </summary>
    public static string Say(string what, int planned, int unseen, int extra, int removed,
        IReadOnlyList<string> left, IReadOnlyList<BuildFault> missing)
    {
        const int show = 6;
        var parts = new List<string>
        {
            $"сверил {what} по раскладке: клеток {planned}" +
            (unseen > 0 ? $", из них НЕ РАЗГЛЯДЕЛ {unseen} (чанк не пришёл)" : "")
        };

        parts.Add(extra == 0
            ? "лишних блоков нет"
            : $"лишних блоков {extra}, снял {removed}");
        if (left.Count > 0)
            parts.Add($"НЕ СНЯЛ {left.Count}: " + string.Join("; ", left.Take(show)) +
                      (left.Count > show ? $"; и ещё {left.Count - show}" : ""));

        if (missing.Count > 0)
            parts.Add($"НЕДОСТАЁТ блоков {missing.Count}: " +
                      string.Join(", ", missing.Take(show).Select(m => m.Where.ToString())) +
                      (missing.Count > show ? $" и ещё {missing.Count - show}" : ""));
        else
            parts.Add("недостающих нет");

        return string.Join("; ", parts);
    }

    // ---------------- живая половина ----------------

    /// <summary>
    /// Что видно боту в этой клетке — мирская половина <see cref="Faults"/>.
    ///
    /// ПУСТО — ЭТО <c>GetBlockId == 0</c>, А НЕ ПУСТОЙ КОД. Приёмка поймала это
    /// живьём на стенде: у пустой известной клетки <c>GetBlockCode</c> отдаёт
    /// не null, а «air» — это законный код воздуха из реестра сервера, и
    /// null он отдаёт только тогда, когда чанк не приходил вовсе. Сверка,
    /// спрашивавшая «код не пуст?», объявляла ЛИШНИМ БЛОКОМ каждую пустую
    /// клетку хода — то есть весь ход дороги целиком — и шла ломать воздух;
    /// а недостачу не находила НИКОГДА, потому что «air» проходил за твердь.
    /// «Пусто» в проекте спрашивают одним и тем же способом — так его
    /// спрашивает и <see cref="Mining.BreakAsync"/> перед ударом.
    /// </summary>
    public Standing Look(BlockPos where) =>
        !ctx.World.IsKnown(where) ? Standing.Unseen
        : ctx.World.GetBlockId(where.X, where.Y, where.Z) == 0 ? Standing.Empty
        : Standing.Block(ctx.World.GetBlockCode(where) ?? "чем-то");

    /// <summary>
    /// ПРОЙТИ ПО СВОЕЙ ЖЕ РАСКЛАДКЕ И СВЕРИТЬ. Лишнее — назвать и снять,
    /// недостающее — назвать.
    ///
    /// ЛИШНЕЕ СНИМАЕТСЯ ПО ФАКТУ: клетка проверяется ПОСЛЕ слома, и всё, что
    /// осталось стоять, попадает в отчёт с координатами и причиной. Выпавшее
    /// подбираем — это тот же камень, которого дороге потом и не хватает.
    ///
    /// ЧУЖОЕ НЕ ТРОГАЕМ. Лишний блок внутри чужой заявки — это чужая
    /// постройка, попавшая в наш ход: живой игрок её не снесёт, потому что ему
    /// не дадут, и бот не станет пробовать. Такой блок называется вслух и
    /// остаётся стоять.
    /// </summary>
    public async Task<CheckReport> CheckAsync(string what, IReadOnlyList<MeantCell> plan,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var deadline = started.AddSeconds(MaxSeconds);

        var (faults, unseen) = Faults(plan, Look);
        var extra = faults.Where(f => f.Kind == FaultKind.Extra).ToList();
        var missing = faults.Where(f => f.Kind == FaultKind.Missing).ToList();

        OnLog?.Invoke($"сверяю {what} с раскладкой: клеток {plan.Count}, " +
                      $"лишних {extra.Count}, недостающих {missing.Count}" +
                      (unseen > 0 ? $", не видно {unseen}" : ""));

        int removed = 0;
        var left = new List<string>();

        // БЛИЖНИЕ ПЕРВЫМИ: порядок обхода в проекте один и живёт у добычи —
        // заводить рядом второй значит получить два разных маршрута по одной
        // и той же куче клеток
        var order = ctx.Movement.FeetCell is { } feet
            ? Mining.NearestFirst(feet, extra.Select(f => f.Where))
            : extra.Select(f => f.Where).ToList();

        int taken = 0;
        foreach (var where in order)
        {
            var fault = extra.First(f => f.Where == where);
            if (ct.IsCancellationRequested)
            {
                left.Add($"{fault} — сверку прервали");
                continue;
            }
            if (DateTime.UtcNow > deadline)
            {
                left.Add($"{fault} — вышло отведённое на сверку время ({MaxSeconds:0} с)");
                continue;
            }
            if (++taken > Math.Max(0, MaxRemove))
            {
                left.Add($"{fault} — за одну сверку снимаю не больше {MaxRemove}");
                continue;
            }
            if (ctx.Claims?.Inside(where) is { } foreign)
            {
                left.Add($"{fault} — это чужое место ({foreign.Why}), сносить не берусь");
                continue;
            }
            if (!ctx.Mining.CanBreak(where))
            {
                left.Add($"{fault} — не берётся тем, что в руках");
                continue;
            }

            var broke = await ctx.Mining.BreakAsync(where, ct);
            if (!broke.Success)
            {
                left.Add($"{fault} — {broke.Message}");
                continue;
            }
            // ПО ФАКТУ, А НЕ ПО ОТВЕТУ: сюда могло осыпаться сверху. Пусто
            // спрашиваем по id, а не по коду: у пустой клетки код — «air», и
            // проверка «код не пуст» засчитывала неудачей КАЖДЫЙ удачный слом
            // (снял 0 из 1, «сломал, а в клетке остался air») — см. Look
            if (ctx.World.GetBlockId(where.X, where.Y, where.Z) != 0)
            {
                left.Add($"{fault} — сломал, а в клетке остался " +
                         $"{ctx.World.GetBlockCode(where) ?? "что-то"}");
                continue;
            }
            removed++;
            await ctx.Mining.CollectDropsAsync(4, PickUpSeconds, ct);
        }

        var report = new CheckReport(what, plan.Count, unseen, extra.Count, removed,
            left, missing, (DateTime.UtcNow - started).TotalSeconds);
        OnLog?.Invoke(report.ToString());
        return report;
    }
}
