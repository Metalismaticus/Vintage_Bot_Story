namespace VsBotKit;

/// <summary>
/// Снаряжение под рукой: оружие и инструменты бот сам перекладывает из рюкзака
/// в хотбар. Причина простая — работать ими можно только из руки (в руке
/// активный слот хотбара), в отличие от еды и бинтов, которые применяются
/// прямо из рюкзака.
///
/// Что считать снаряжением, берётся из реестра сервера (тип инструмента или
/// урон), поэтому модовые топоры и мечи подхватываются сами.
/// </summary>
public class BehaviorKeepGearReady : BotBehavior
{
    private readonly BotContext ctx;

    /// <summary>Сколько слотов хотбара оставлять свободными под всякое.</summary>
    public int KeepFreeSlots { get; set; } = 1;

    /// <summary>Сколько слотов хотбара использовать (обычные — первые 10).</summary>
    public int UsableHotbarSlots { get; set; } = 10;

    /// <summary>Как часто проверять инвентарь, секунд.</summary>
    public double CheckIntervalSeconds { get; set; } = 3;

    /// <summary>Переложил снаряжение в хотбар (код предмета, слот).</summary>
    public event Action<string, int>? OnMovedToHotbar;

    private DateTime lastCheck = DateTime.MinValue;

    public BehaviorKeepGearReady(BotContext ctx) => this.ctx = ctx;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - lastCheck).TotalSeconds < CheckIntervalSeconds)
            return false;
        lastCheck = DateTime.UtcNow;

        var hotbar = ctx.Self.GetInventory("hotbar");
        string? hotbarId = ctx.Self.GetInventoryId("hotbar");
        if (hotbar == null || hotbarId == null)
            return false;

        int usable = Math.Min(UsableHotbarSlots, hotbar.Length);
        var freeSlots = new List<int>();
        for (int i = 0; i < usable; i++)
            if (hotbar[i].IsEmpty)
                freeSlots.Add(i);
        if (freeSlots.Count <= KeepFreeSlots)
            return false; // некуда класть, не трогаем чужое

        // Снаряжение, лежащее не в хотбаре
        foreach (var (invId, slots) in ctx.Self.OwnInventories)
        {
            if (invId == hotbarId || invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            for (int slot = 0; slot < slots.Length; slot++)
            {
                if (slots[slot].Code is not { } code || !ctx.World.IsHandGearByCode(code))
                    continue;

                int target = freeSlots[0];
                await ctx.Actions.MoveItemAsync(invId, slot, hotbarId, target, slots[slot].Count);
                OnMovedToHotbar?.Invoke(code, target);
                return true; // по одному предмету за тик — сервер успеет ответить
            }
        }
        return false;
    }
}
