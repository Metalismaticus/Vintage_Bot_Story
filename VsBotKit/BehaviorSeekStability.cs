namespace VsBotKit;

/// <summary>
/// УЙТИ ОТТУДА, ГДЕ ТЕМПОРАЛЬНАЯ СТАБИЛЬНОСТЬ ПАДАЕТ.
///
/// ЧТО БОТ ЗНАЕТ ТОЧНО (см. <see cref="TemporalStability"/>): своё число —
/// сервер шлёт его пять раз в секунду, — и где разломы, если мир разрешил их
/// видеть. Чего он НЕ знает: сколько стабильности будет вон в той стороне.
/// Это считает сервер по шуму от сида мира, а сида у клиента нет.
///
/// ПОЭТОМУ БОТ ДЕЛАЕТ РОВНО ТО ЖЕ, ЧТО ЧЕЛОВЕК:
/// 1. Разлом вплотную? Отойти. Это единственная поправка, которую бот считает
///    ТОЧНО: формула сервера известна (<see cref="StabilityRules.RiftPenalty"/>),
///    и она обнуляется в трёх блоках от края разлома. Отходить дальше незачем —
///    и незачем «жить подальше от разломов»: они появляются и умирают сами.
/// 2. Иначе — НАВЕРХ. В формуле стабильности места два слагаемых зависят от
///    высоты, и оба растут вверх (подтягивание к 0,8…1,5 у поверхности и вычет
///    глубины ниже уровня моря). То есть подъём стабильность места НЕ понижает,
///    а под землёй она заведомо ниже. Это единственное направление, о котором
///    можно судить без сида, — и бот идёт туда, а не гадает по сторонам.
/// 3. И СМОТРИТ, ЧТО ВЫШЛО. Помогло — стабильность пойдёт вверх, это видно по
///    её ходу (<see cref="TemporalStability.Trend"/>) и говорится вслух. Не
///    помогло — говорится и это, а не тишина.
///
/// БУРЯ — НЕ ЭТА СПОСОБНОСТЬ. Во время бури стабильность падает ВЕЗДЕ (сервер
/// вычитает из места 1,5 × силу бури), и бежать некуда: спасает укрытие, а не
/// дорога. Поэтому в бурю эта способность уступает
/// <see cref="BehaviorShelterFromStorm"/> и говорит об этом вслух.
///
/// ЧИСЛА ЗДЕСЬ — МЕХАНИЗМ, А НЕ ПОЛИТИКА: пороги ставит роль (см. ручки
/// «УходитьПриНизкойСтабильности», «УходитьПриСтабильностиНиже» у LivingRole).
/// По умолчанию способность ВЫКЛЮЧЕНА — бегать или терпеть, решает роль.
/// </summary>
public class BehaviorSeekStability : BotBehavior
{
    private readonly BotContext ctx;

    public BehaviorSeekStability(BotContext ctx) => this.ctx = ctx;

    /// <summary>Уходить из нестабильных мест (решает роль).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Ниже какой стабильности уходить. 0,2 — это те самые «двадцать процентов»:
    /// ровно с этого числа у живого игрока начинает рябить экран
    /// (<see cref="StabilityRules.GlitchBelow"/>), а урон начинается ещё ниже,
    /// на 0,13.
    /// </summary>
    public double RunBelow { get; set; } = StabilityRules.GlitchBelow;

    /// <summary>
    /// До какой стабильности считать, что обошлось. Строго выше порога ухода:
    /// у самого порога число дрожит, и без этого запаса бот дёргался бы
    /// «бегу — не бегу» по пять раз в секунду, не сделав ни шагу.
    /// </summary>
    public double CalmAt { get; set; } = 0.4;

    /// <summary>На сколько блоков дальше вредного радиуса отходить от разлома.</summary>
    public double RiftMargin { get; set; } = 3;

    /// <summary>Насколько выше нынешней высоты имеет смысл лезть (меньше — не дорога).</summary>
    public int ClimbAtLeast { get; set; } = 6;

    /// <summary>Как далеко вокруг искать место повыше, блоков.</summary>
    public int LookAround { get; set; } = 24;

    /// <summary>На сколько блоков вверх заглядывать в столбе.</summary>
    public int LookUp { get; set; } = 64;

    /// <summary>Сколько секунд отводится на один переход.</summary>
    public double TravelSeconds { get; set; } = 45;

    /// <summary>Пауза перед следующей попыткой, когда идти было некуда.</summary>
    public double RetrySeconds { get; set; } = 20;

    /// <summary>Пошёл спасаться: причина словами.</summary>
    public event Action<string>? OnRunning;

    /// <summary>Отхожу от разлома: сам разлом и расстояние до него.</summary>
    public event Action<RiftInfo, double>? OnLeavingRift;

    /// <summary>Поднимаюсь: куда и на сколько блоков выше.</summary>
    public event Action<BlockPos, int>? OnClimbing;

    /// <summary>
    /// Идти некуда: почему. Говорится один раз на причину, а не на каждый тик:
    /// тик зовётся по нескольку раз в секунду, и повтор залил бы журнал.
    /// </summary>
    public event Action<string>? OnNowhereToGo;

    /// <summary>Отпустило: причина словами (число уже в ней).</summary>
    public event Action<string>? OnCalm;

    /// <summary>Ушли — и вот что из этого вышло, словами и с числами.</summary>
    public event Action<string>? OnResult;

    private bool running;
    private DateTime nextTry = DateTime.MinValue;
    private double? whereItWas;

    /// <summary>
    /// САМАЯ ВЫСОКАЯ КЛЕТКА, ГДЕ МОЖНО СТОЯТЬ, В ЭТОМ СТОЛБЕ.
    ///
    /// Неизвестная клетка (чанк не пришёл) стоячей не считается — так устроен
    /// сам мир бота, и это правильно: обещать «там наверху есть пол», не видя
    /// его, значит звать бота в камень.
    /// </summary>
    public static int? TopStandable(IPathWorld world, int x, int z, int fromY, int toY)
    {
        for (int y = toY; y >= fromY; y--)
            if (world.IsStandable(x, y, z))
                return y;
        return null;
    }

    /// <summary>
    /// КУДА ПОДНИМАТЬСЯ: самая высокая стоячая клетка в круге вокруг бота, но
    /// не ниже чем на <paramref name="climbAtLeast"/> выше него. Из равных по
    /// высоте берём БЛИЖАЙШУЮ — дорога тоже чего-то стоит, а разница в
    /// стабильности между двумя клетками одной высоты боту не видна.
    ///
    /// null — подниматься отсюда некуда, и это честный ответ, а не «пойду
    /// куда-нибудь».
    /// </summary>
    public static BlockPos? WayUp(IPathWorld world, BlockPos from, int radius,
        int climbAtLeast, int lookUp)
    {
        BlockPos? best = null;
        // Ниже этого подниматься не стоит вовсе — и ниже этого мы не ищем:
        // столбов в круге тысячи, и просмотр каждого от пола до неба стоил бы
        // тика. Планка растёт по ходу поиска, но НЕ выше найденного — иначе
        // равные по высоте перестали бы находиться, и «ближайший из равных»
        // не работал бы никогда
        int bestY = from.Y + Math.Max(1, climbAtLeast);
        double bestDistance = double.MaxValue;

        for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                double distance = Math.Sqrt(dx * dx + dz * dz);
                if (distance > radius)
                    continue;
                int x = from.X + dx, z = from.Z + dz;
                if (TopStandable(world, x, z, bestY, from.Y + lookUp) is not { } y)
                    continue;
                if (y > bestY || (y == bestY && distance < bestDistance))
                {
                    best = new BlockPos(x, y, z);
                    bestY = y;
                    bestDistance = distance;
                }
            }
        return best;
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // «НЕ ВЕЛЕНО» СПРАШИВАЕМ ПЕРВЫМ. Роль включает эту способность, и пока
        // она выключена, механизма стабильности у бота может не быть вовсе
        // (собранный вручную контекст в чужих проверках) — трогать его нельзя.
        // Причина отказа при этом не теряется: её отдельно называет
        // StabilityRules.RunVerdict, когда человек спрашивает
        if (!Enabled)
        {
            running = false;
            return false;
        }

        var stability = ctx.Stability;
        var (run, why) = StabilityRules.RunVerdict(Enabled, stability.Own, RunBelow, CalmAt, running);
        if (!run)
        {
            if (running)
            {
                running = false;
                жалоба = null;
                OnCalm?.Invoke(why);
            }
            return false;
        }

        // БУРЯ БЬЁТ ВЕЗДЕ. Сервер вычитает 1,5 × силу бури из стабильности
        // ЛЮБОГО места, поэтому «уйти туда, где выше» в бурю невозможно —
        // спасает только укрытие, и оно стоит выше нас по важности
        if (ctx.Storms.ShelterNow)
        {
            Пожаловаться("стабильность падает из-за бури — от неё не уйдёшь, тут дело укрытия");
            return false;
        }

        if (ctx.Movement.IsBusy || DateTime.UtcNow < nextTry)
            return false;
        if (ctx.Self.Position is not { } p)
            return false;

        if (!running)
        {
            running = true;
            whereItWas = stability.Own;
            OnRunning?.Invoke(why);
        }

        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

        // ТЕЛО БЕРЁМ ДВЕРЬЮ СПАСЕНИЯ, А НЕ ПРОСТО СТУПЕНЬЮ РЕФЛЕКСА. Ступень
        // прежняя (выше нас стоят еда и лечение, и отбирать у них тело нельзя),
        // а вот ждать нам нельзя вовсе: здесь стоял голый TryTake, и начатая
        // секунду назад дорога держала нас выдержкой начатого дела
        // (BodyArbiter.SettleSeconds) — стенд приёмки напечатал на это null.
        // Уход от нестабильности, простоявший лишние секунды, — это осыпавшийся
        // под ботом блок и падение в разлом
        using var hold = ctx.Turn.TakeForRescue("уйти от нестабильности",
            $"нестабильность гонит с места ({why})", ct);
        if (hold == null)
            return false;
        var token = hold.Token;

        // ---- 1. разлом вплотную ----
        if (stability.NearestHarmful(p.X, p.Y, p.Z) is { } near)
        {
            var (rift, distance) = near;
            OnLeavingRift?.Invoke(rift, distance);
            var (tx, tz) = StabilityRules.AwayFromRift(p.X, p.Z, rift.X, rift.Z, rift.Size, RiftMargin);
            var target = new BlockPos((int)Math.Floor(tx), here.Y, (int)Math.Floor(tz));
            // Пол под целью может быть выше или ниже — ищем его так же, как
            // это делает движение, а не ставим бота в воздух
            if (ctx.World.FindStandableY(target.X, target.Y, target.Z, up: 4, down: 8) is { } y)
                target = new BlockPos(target.X, y, target.Z);
            bool ok = await ctx.Movement.TravelToAsync(target, TravelSeconds, token);
            Итог(ok
                ? $"отошёл от {rift} (было {distance:0.#} бл)"
                : $"уйти от {rift} не вышло — дороги нет");
            if (!ok)
                nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            return true;
        }

        // ---- 2. наверх ----
        if (WayUp(ctx.World, here, LookAround, ClimbAtLeast, LookUp) is not { } up)
        {
            // Честный отказ: выше идти некуда. Это не поломка — так бывает и у
            // человека, стоящего под открытым небом на холме
            Пожаловаться($"выше подниматься некуда: в {LookAround} бл вокруг нет места " +
                         $"хотя бы на {ClimbAtLeast} бл выше меня");
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            return false;
        }

        OnClimbing?.Invoke(up, up.Y - here.Y);
        bool climbed = await ctx.Movement.TravelToAsync(up, TravelSeconds, token);
        Итог(climbed
            ? $"поднялся на {up.Y - here.Y} бл"
            : $"подняться на {up.Y - here.Y} бл не вышло — дороги нет");
        if (!climbed)
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        return true;
    }

    /// <summary>
    /// ЧТО ВЫШЛО ИЗ ПЕРЕХОДА — по факту от сервера, а не по намерению.
    /// Числа обе: с чего ушли и что стало. Без них строка «поднялся на 12 бл»
    /// не отвечает на единственный важный вопрос — помогло ли.
    /// </summary>
    private void Итог(string что)
    {
        var stability = ctx.Stability;
        string число = stability.Own is { } now && whereItWas is { } was
            ? $"; стабильность {was:0.##} → {now:0.##}" +
              (stability.Rising ? " (растёт)" : stability.Falling ? " (всё ещё падает)" : "")
            : "";
        whereItWas = stability.Own;
        жалоба = null;   // после перехода прежний отказ — уже не новость
        OnResult?.Invoke(что + число);
    }

    /// <summary>
    /// Пожаловаться вслух, но только если это новость: тик зовётся по нескольку
    /// раз в секунду, и без этой проверки один и тот же отказ залил бы журнал.
    /// </summary>
    private void Пожаловаться(string строка)
    {
        if (жалоба == строка)
            return;
        жалоба = строка;
        OnNowhereToGo?.Invoke(строка);
    }

    private string? жалоба;
}
