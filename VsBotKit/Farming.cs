namespace VsBotKit;

/// <summary>
/// Земледелие: вспахать, посеять, полить, собрать.
///
/// Механика игры, на которую опирается механизм:
/// - мотыга по земле («soil-...») делает грядку («farmland-...»);
/// - семена по грядке сажают культуру («crop-flax-1»);
/// - лейка поливает грядку удержанием кнопки;
/// - спелость видна в коде блока: последняя стадия семейства «crop-flax-N»,
///   а максимум N известен из реестра сервера (WorldModel.MaxStageOf).
///
/// Всё это — механизм. Что сеять, где и когда — политика роли.
/// </summary>
public class Farming
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Farming(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
    }

    public event Action<string>? OnLog;

    public string HoeCode { get; set; } = "hoe-";
    public string WateringCanCode { get; set; } = "wateringcan";
    public string SeedCode { get; set; } = "seeds-";

    /// <summary>Сколько держать мотыгу и лейку (в игре это удержание).</summary>
    public double TillSeconds { get; set; } = 1.0;
    public double WaterSeconds { get; set; } = 2.0;

    public bool IsSoil(BlockPos p) =>
        ctx.World.GetBlockCode(p) is { } c && c.StartsWith("soil", StringComparison.OrdinalIgnoreCase);

    public bool IsFarmland(BlockPos p) =>
        ctx.World.GetBlockCode(p) is { } c && c.StartsWith("farmland", StringComparison.OrdinalIgnoreCase);

    /// <summary>Что растёт на грядке (null — пусто).</summary>
    public string? CropOn(BlockPos farmland) =>
        ctx.World.GetBlockCode(new BlockPos(farmland.X, farmland.Y + 1, farmland.Z)) is { } c &&
        c.StartsWith("crop-", StringComparison.OrdinalIgnoreCase) ? c : null;

    /// <summary>Культура доросла до последней стадии.</summary>
    public bool IsRipe(string cropCode)
    {
        int stage = WorldModel.StageOf(cropCode);
        if (stage < 0)
            return false;
        int dash = cropCode.LastIndexOf('-');
        string family = cropCode[..(dash + 1)];      // «crop-flax-»
        int max = ctx.World.MaxStageOf(family);
        return max > 0 && stage >= max;
    }

    /// <summary>Вспахать землю мотыгой.</summary>
    public async Task<bool> TillAsync(BlockPos soil, CancellationToken ct = default)
    {
        if (!IsSoil(soil))
        {
            OnLog?.Invoke($"{soil}: тут не земля ({ctx.World.GetBlockCode(soil) ?? "пусто"})");
            return false;
        }
        // НАД ГРЯДКОЙ ДОЛЖНО БЫТЬ ПУСТО. Мотыга в игре прямо отказывается
        // работать, если сверху хоть что-то есть (ItemHoe.OnHeldInteractStart:
        // «Requires no block above»), а на лугу там почти всегда трава.
        // Поймано живьём: земля правильная, мотыга в руке, а «не вспахалось»
        var above = new BlockPos(soil.X, soil.Y + 1, soil.Z);
        if (ctx.World.GetBlockId(above.X, above.Y, above.Z) != 0)
        {
            string weed = ctx.World.GetBlockCode(above) ?? "?";
            var cleared = await mining.BreakAsync(above, ct);
            if (!cleared.Success)
            {
                OnLog?.Invoke($"над грядкой {weed} — не убрать: {cleared}");
                return false;
            }
            OnLog?.Invoke($"убрал сверху {weed}");
            await mining.CollectDropsAsync(3, 5, ct);
        }

        if (await hands.TakeToHandAsync(HoeCode, ct) is null)
        {
            OnLog?.Invoke("нет мотыги");
            return false;
        }
        if (!await hands.ReachForAsync(soil, ct: ct))
            return false;

        await hands.UseHeldAsync(TillSeconds, soil, ct: ct);
        await Task.Delay(500, ct).ContinueWith(_ => { });
        bool ok = IsFarmland(soil);
        OnLog?.Invoke(ok ? $"вспахал {soil}" : $"не вспахалось {soil}");
        return ok;
    }

    /// <summary>Посеять семена на грядку.</summary>
    public async Task<bool> SowAsync(BlockPos farmland, string? seedKind = null, CancellationToken ct = default)
    {
        if (!IsFarmland(farmland))
            return false;
        string mask = seedKind == null ? SeedCode : SeedCode + seedKind;
        if (await hands.TakeToHandAsync(mask, ct) is null)
        {
            OnLog?.Invoke($"нет семян ({mask})");
            return false;
        }
        if (!await hands.ReachForAsync(farmland, ct: ct))
            return false;

        await hands.UseHeldAsync(0.1, farmland, ct: ct);
        await Task.Delay(500, ct).ContinueWith(_ => { });
        bool ok = CropOn(farmland) != null;
        OnLog?.Invoke(ok ? $"посеял {CropOn(farmland)}" : "семена не легли");
        return ok;
    }

    /// <summary>Полить грядку из лейки.</summary>
    public async Task<bool> WaterAsync(BlockPos farmland, CancellationToken ct = default)
    {
        if (await hands.TakeToHandAsync(WateringCanCode, ct) is null)
        {
            OnLog?.Invoke("нет лейки");
            return false;
        }
        if (!await hands.ReachForAsync(farmland, ct: ct))
            return false;
        await hands.UseHeldAsync(WaterSeconds, farmland, ct: ct);
        return true;
    }

    /// <summary>Собрать спелую культуру (ломается, как в игре) и подобрать урожай.</summary>
    public async Task<bool> HarvestAsync(BlockPos farmland, CancellationToken ct = default)
    {
        if (CropOn(farmland) is not { } crop || !IsRipe(crop))
            return false;
        var cropPos = new BlockPos(farmland.X, farmland.Y + 1, farmland.Z);
        var result = await mining.BreakAsync(cropPos, ct);
        if (!result.Success)
        {
            OnLog?.Invoke($"{crop}: {result}");
            return false;
        }
        await mining.CollectDropsAsync(4, 8, ct);
        OnLog?.Invoke($"собрал {crop}");
        return true;
    }

    /// <summary>Грядки вокруг.</summary>
    public IEnumerable<BlockPos> FarmlandsNear(int radius = 16)
    {
        if (ctx.Self.Position is not { } p)
            return [];
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        return ctx.World.FindBlocks(c => c.StartsWith("farmland", StringComparison.OrdinalIgnoreCase),
            here, radius, height: 4, limit: 64).ToList();
    }

    /// <summary>
    /// Обойти свои грядки: спелое собрать, пустое засеять, сухое полить.
    /// Возвращает, сколько грядок обслужено.
    /// </summary>
    public async Task<int> TendAsync(int radius = 16, double maxSeconds = 240, CancellationToken ct = default)
    {
        int done = 0;
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);
        foreach (var bed in FarmlandsNear(radius))
        {
            if (DateTime.UtcNow > deadline || ct.IsCancellationRequested)
                break;
            string? crop = CropOn(bed);
            if (crop != null && IsRipe(crop))
            {
                if (await HarvestAsync(bed, ct)) done++;
                crop = null;
            }
            if (crop == null && hands.CountOf(SeedCode) > 0)
            {
                if (await SowAsync(bed, ct: ct)) done++;
            }
            if (hands.CountOf(WateringCanCode) > 0)
                await WaterAsync(bed, ct);
        }
        OnLog?.Invoke($"обошёл грядки: {done} дел");
        return done;
    }
}
