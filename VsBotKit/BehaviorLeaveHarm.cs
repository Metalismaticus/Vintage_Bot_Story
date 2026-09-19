namespace VsBotKit;

/// <summary>
/// УЙТИ ОТТУДА, ГДЕ ТЕБЯ ЖГУТ.
///
/// ЗАКАЗ ЗАКАЗЧИКА ДОСЛОВНО: «на костре не стоять и на любых других зонах с
/// уроном - кипяток и лава например». Беда старая и названная: бот развёл
/// собственный костёр, пришёл к нему греться, встал В НЕГО и сгорел вместе с
/// 48 стопками вещей.
///
/// ПОЧЕМУ ЭТО СПАСЕНИЕ, А НЕ УДОБСТВО. Холод — неудобство: он копится часами и
/// лечится одеждой. Огонь под ногами — смерть за десяток секунд, и лечится он
/// ровно одним: ШАГОМ В СТОРОНУ. Поэтому вес у способности такой же, как у
/// «не висеть в воздухе» (<c>BehaviorWeights.Rescue</c>), а тело она берёт
/// ступенью <c>Vital</c> — той самой, что описана как «выбраться из воды и
/// лавы» и перебивает даже бой. Драться, стоя в огне, нельзя: сгоришь
/// победителем.
///
/// ВТОРОЙ КОПИИ ПРАВИЛА ЗДЕСЬ НЕТ. «Что такое вредная клетка» знает
/// <see cref="HarmRules"/> — один вопрос к реестру игры; «куда сойти» умеет
/// <see cref="Movement.StepAsideFromAsync"/> — тот же приём, которым карьер
/// сходит с клетки под нестабильным потолком. Здесь только повод и слова.
///
/// «НЕ ВСТАВАТЬ ТУДА ЗАРАНЕЕ» — НЕ ЗДЕСЬ. Это правило живёт в самом мире:
/// <see cref="WorldModel.IsStandable"/>, <see cref="WorldModel.IsEnterable"/> и
/// <see cref="WorldModel.CanHangAt"/> спрашивают <see cref="HarmRules.HurtsToEnter"/>,
/// и маршрут через огонь не строится вовсе. Эта способность — последняя
/// защита: на клетке, которая ЗАГОРЕЛАСЬ УЖЕ ПОД БОТОМ, никакой маршрут не
/// поможет.
/// </summary>
public class BehaviorLeaveHarm : BotBehavior
{
    private readonly BotContext ctx;

    public BehaviorLeaveHarm(BotContext ctx) => this.ctx = ctx;

    /// <summary>Уходить с клеток, которые жгут (решает роль).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Как далеко искать место, куда сойти, клеток.</summary>
    public int LookAround { get; set; } = 6;

    // СКОЛЬКО СЕКУНД ДАВАТЬ НА ШАГ В СТОРОНУ — здесь не спрашивается нарочно:
    // это уже ручка самого приёма схода (Movement.StepAsideSeconds), одна на
    // всех, кто с клетки сходит. Второй такой ручки заводить незачем

    /// <summary>Ухожу: где жжёт и чем именно (код блока).</summary>
    public event Action<BlockPos, string>? OnLeaving;

    /// <summary>Ушёл: с какой клетки и куда.</summary>
    public event Action<BlockPos, BlockPos>? OnLeft;

    /// <summary>
    /// Сойти не вышло: полная строка с числами. Говорится ОДИН РАЗ НА ПОВОД
    /// (см. <see cref="Пожар"/>), а не каждый тик.
    /// </summary>
    public event Action<string>? OnStuckInHarm;

    /// <summary>
    /// ПОВОД, А НЕ ГОТОВАЯ СТРОКА. Тик зовётся по нескольку раз в секунду, и
    /// горящий бот залил бы журнал сотней одинаковых строк за те самые
    /// секунды, что ему осталось жить. Сравнивать при этом ПОЛНУЮ строку
    /// нельзя: в ней стоят координаты клетки, а бот в огне дёргается — каждое
    /// дрожание числа считалось бы новостью. На этом уже обожглись и добыча с
    /// убитых, и грение у костра, поэтому тревога меряет ПОВОД
    /// («некуда сойти»), а человеку уходит полная строка.
    /// </summary>
    public Alarm Пожар { get; } = new("огонь");

    private bool leaving;

    private void НеУйти(string повод, string строка)
    {
        if (Пожар.Raise(повод))
            OnStuckInHarm?.Invoke(строка);
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || ctx.Movement.FeetCell is not { } feet)
            return false;

        if (HarmRules.WhereItBurns(ctx.World, feet) is not { } burns)
        {
            if (leaving)
            {
                leaving = false;
                Пожар.Clear();
            }
            return false;
        }

        // ЖИЗНЬ, А НЕ РЕФЛЕКС. Ступень Vital описана в распорядителе тела ровно
        // этим случаем — «выбраться из воды и лавы», — и она единственная
        // перебивает бой: боту, стоящему в огне, шаг в сторону нужнее удара
        using var hold = ctx.Turn.TryTake("уйти из огня", BodyArbiter.Importance.Vital, ct);
        if (hold == null)
        {
            // Молчать нельзя (закон 4): со стороны «горю и стою» ничем не
            // отличается от поломки
            НеУйти("тело занято",
                $"стою в огне ({ctx.World.HarmCodeAt(burns.X, burns.Y, burns.Z) ?? "?"} в {burns}), " +
                "а телом сейчас распоряжается дело поважнее — уйти не могу");
            return false;
        }
        ct = hold.Token;

        leaving = true;
        OnLeaving?.Invoke(burns, ctx.World.HarmCodeAt(burns.X, burns.Y, burns.Z) ?? "?");

        // Сходим ТЕМ ЖЕ приёмом, что и карьер из-под нестабильного потолка.
        // Место годится, только если оно не жжёт само: в тесной пещере с лавой
        // «ближайшая свободная клетка» легко оказывается второй лужей той же
        // лавы. Клетку головы спрашиваем отдельно — рост игрока две клетки
        bool ok = await ctx.Movement.StepAsideFromAsync(
            feet, LookAround,
            spot => !ctx.World.HurtsToEnter(spot.X, spot.Y, spot.Z) &&
                    !ctx.World.HurtsToEnter(spot.X, spot.Y + 1, spot.Z),
            ct);

        if (ok && ctx.Movement.FeetCell is { } now)
        {
            leaving = false;
            Пожар.Clear();
            OnLeft?.Invoke(feet, now);
            return true;
        }

        // ОТКАЗ ЧЕСТНЫЙ И ГРОМКИЙ. Своих слов о том, ЧТО именно проверено,
        // здесь нет нарочно: их уже сказал сам приём схода
        // (Movement.StepAsideFromAsync перечисляет и радиус, и число мест)
        НеУйти("некуда сойти",
            $"стою в огне ({ctx.World.HarmCodeAt(burns.X, burns.Y, burns.Z) ?? "?"} в {burns}) — " +
            $"сойти некуда в {LookAround} кл вокруг, продолжаю гореть");
        return true;   // ход всё равно наш: гореть и копать одновременно нельзя
    }
}
