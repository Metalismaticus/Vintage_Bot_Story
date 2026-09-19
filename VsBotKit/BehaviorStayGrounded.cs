namespace VsBotKit;

/// <summary>
/// Гравитация. Позиция бота клиент-авторитетна: сервер её не двигает, поэтому
/// бот, оказавшийся в воздухе, так и висит — а под ним нет опоры, и любое
/// движение сразу упирается в «стена или обрыв».
///
/// В воздухе бот оказывается легко: телепорт, перенос транслокатором,
/// возрождение, обвалившийся блок под ногами, толчок мобом с уступа.
/// Эта способность просто роняет его на первую опору, как живого игрока —
/// и должна стоять ПЕРВОЙ в списке: пока бот падает, ему не до еды и драк.
/// </summary>
public class BehaviorStayGrounded : BotBehavior
{
    private readonly BotContext ctx;

    /// <summary>На сколько блоков вниз искать опору.</summary>
    public int MaxFallDepth { get; set; } = 160;

    /// <summary>Бот падал (высота падения в блоках).</summary>
    public event Action<double>? OnFell;

    public BehaviorStayGrounded(BotContext ctx)
    {
        this.ctx = ctx;
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // Идёт своё движение (прыжок, лазание, маршрут) — не мешаем
        if (ctx.Movement.IsBusy || ctx.Movement.IsGrounded)
            return false;

        double before = ctx.Self.Position?.Y ?? 0;
        if (!await ctx.Movement.FallToGroundAsync(MaxFallDepth, ct))
            return false;

        OnFell?.Invoke(before - (ctx.Self.Position?.Y ?? before));
        return true; // падение занимает ход
    }
}
