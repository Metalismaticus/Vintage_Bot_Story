namespace VsBotKit;

/// <summary>Найденная табличка: где висит и что написано.</summary>
public sealed record SignInfo(BlockPos Pos, string Text)
{
    /// <summary>Строки надписи без пустых (на табличке их до четырёх).</summary>
    public string[] Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                             StringSplitOptions.TrimEntries);

    /// <summary>
    /// Команды с таблички — строки, начинающиеся с «!». Роль сама решает,
    /// что они значат: табличка это просто текст в мире, а не приказ.
    /// </summary>
    public IEnumerable<string> Commands =>
        Lines.Where(l => l.StartsWith('!')).Select(l => l[1..].Trim());

    public override string ToString() => $"{Pos}: {string.Join(" / ", Lines)}";
}

/// <summary>Содержимое осмотренного контейнера.</summary>
public sealed record ContainerInfo(BlockPos Pos, string Kind, IReadOnlyList<SlotContent> Items)
{
    /// <summary>Занятые слоты (пустые в осмотре не интересны).</summary>
    public IEnumerable<SlotContent> Filled => Items.Where(s => s.Code != null && s.Count > 0);

    /// <summary>Сколько всего предметов с таким кодом (по подстроке).</summary>
    public int Count(string codePart) => Filled
        .Where(s => s.Code!.Contains(codePart, StringComparison.OrdinalIgnoreCase))
        .Sum(s => s.Count);

    public bool Has(string codePart) => Count(codePart) > 0;

    /// <summary>Краткая сводка «код×количество» для чата и логов.</summary>
    public string Describe(int limit = 8) => string.Join(", ", Filled
        .GroupBy(s => s.Code!)
        .Select(g => $"{g.Key}×{g.Sum(s => s.Count)}")
        .Take(limit));
}

/// <summary>
/// Чтение мира глазами: надписи на табличках и содержимое контейнеров.
/// <para>
/// Умение честное: содержимое контейнера бот узнаёт, только ОТКРЫВ его —
/// ровно как игрок. Заглядывать в закрытый сундук по данным из пакета мы
/// не хотим: это то, что игра показывает лишь в креативе.
/// </para>
/// </summary>
public sealed class Readables
{
    private readonly BotContext ctx;

    public Readables(BotContext ctx) => this.ctx = ctx;

    /// <summary>Диагностика (что прочитал, что не смог).</summary>
    public event Action<string>? OnLog;

    /// <summary>Класс блок-сущности — контейнер (сундук, ящик, корзина)?</summary>
    public static bool IsContainerClass(string className) =>
        className.Contains("Container", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Chest", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Crate", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Basket", StringComparison.OrdinalIgnoreCase) ||
        className.Contains("Shelf", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Таблички в радиусе. Текст приходит с чанком, как и любому клиенту:
    /// игрок тоже читает надпись, просто посмотрев на неё.
    /// </summary>
    public IReadOnlyList<SignInfo> SignsNear(double radius = 12)
    {
        if (ctx.Entities.Self is not { } self)
            return [];
        return NearbyEntities(self.X, self.Y, self.Z, radius)
            .Where(be => be.ClassName.Contains("Sign", StringComparison.OrdinalIgnoreCase))
            .Select(be => new SignInfo(be.Pos, be.SignText ?? ""))
            .Where(s => s.Text.Length > 0)
            .OrderBy(s => Dist2(s.Pos, self))
            .ToList();
    }

    /// <summary>Ближайшая табличка, чей текст содержит подстроку.</summary>
    public SignInfo? FindSign(string textPart, double radius = 12) =>
        SignsNear(radius).FirstOrDefault(s =>
            s.Text.Contains(textPart, StringComparison.OrdinalIgnoreCase));

    /// <summary>Контейнеры в радиусе (ещё не осмотренные — только где они).</summary>
    public IReadOnlyList<(BlockPos Pos, string Kind)> ContainersNear(double radius = 12)
    {
        if (ctx.Entities.Self is not { } self)
            return [];
        return NearbyEntities(self.X, self.Y, self.Z, radius)
            .Where(be => IsContainerClass(be.ClassName))
            .Select(be => (be.Pos, Kind: be.ClassName))
            .OrderBy(c => Dist2(c.Pos, self))
            .ToList();
    }

    /// <summary>
    /// Осмотреть контейнер: подойти в пределы руки, ОТКРЫТЬ и прочитать, что
    /// внутри. Открытие — обычное взаимодействие, поэтому под приватом сервер
    /// просто не пришлёт содержимое, и мы вернём null.
    /// </summary>
    public async Task<ContainerInfo?> InspectAsync(BlockPos pos, CancellationToken ct = default)
    {
        string kind = ctx.World.GetBlockEntity(pos)?.ClassName ?? "?";

        // В пределах руки игрока (PickingRange игры — 4.5 блока)
        if (ctx.Entities.Self is { } me &&
            me.DistanceTo(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5) > ReachDistance)
        {
            if (!await ctx.Movement.MoveToSmartAsync(pos.X + 0.5, pos.Z + 0.5, 1.8, ct))
            {
                OnLog?.Invoke($"к контейнеру {pos} не подойти");
                return null;
            }
        }

        await ctx.Movement.FaceAsync(pos.X + 0.5, pos.Z + 0.5);
        await ctx.Actions.UseBlockAsync(pos);
        // Сервер присылает содержимое отдельным пакетом инвентаря
        await Task.Delay(OpenDelayMs, ct).ContinueWith(_ => { });

        var items = ctx.World.GetContainerContents(pos);

        // ЗАКРЫВАЕМ ЗА СОБОЙ — как и дверь. У игрока сундук закрывается
        // вместе с окном интерфейса, поэтому бот обязан послать закрытие
        // сам, иначе оставляет сундуки открытыми по всему миру
        if (CloseAfterInspect)
        {
            await ctx.Actions.CloseContainerAsync(pos);
            await Task.Delay(150, ct).ContinueWith(_ => { });
        }

        if (items == null)
        {
            OnLog?.Invoke($"{kind} {pos} не открылся (приват?)");
            return null;
        }

        var info = new ContainerInfo(pos, kind, items);
        OnLog?.Invoke($"{kind} {pos}: занято слотов {info.Filled.Count()}");
        return info;
    }

    /// <summary>
    /// Осмотреть несколько контейнеров подряд — например все, что стоят рядом
    /// с табличкой. Роль сама выбирает, какие именно: библиотека даёт умение.
    /// </summary>
    public async Task<List<ContainerInfo>> InspectAllAsync(
        IEnumerable<BlockPos> positions, CancellationToken ct = default)
    {
        var found = new List<ContainerInfo>();
        foreach (var pos in positions)
        {
            if (ct.IsCancellationRequested)
                break;
            if (await InspectAsync(pos, ct) is { } info)
                found.Add(info);
        }
        return found;
    }

    /// <summary>Контейнеры вплотную к табличке (её «подписанные» сундуки).</summary>
    public IReadOnlyList<(BlockPos Pos, string Kind)> ContainersBySign(
        SignInfo sign, double radius = 3) =>
        NearbyEntities(sign.Pos.X + 0.5, sign.Pos.Y + 0.5, sign.Pos.Z + 0.5, radius)
            .Where(be => IsContainerClass(be.ClassName))
            // Сундук прилавка — тот, что ПОД табличкой: соседние таблички
            // висят рядом, и «просто ближайший» у них получался общим
            .OrderBy(be => be.Pos.X == sign.Pos.X && be.Pos.Z == sign.Pos.Z ? 0 : 1)
            .ThenBy(be => Math.Abs(be.Pos.X - sign.Pos.X) + Math.Abs(be.Pos.Z - sign.Pos.Z))
            .ThenBy(be => Math.Abs(be.Pos.Y - sign.Pos.Y))
            .Select(be => (be.Pos, Kind: be.ClassName))
            .ToList();

    /// <summary>
    /// Подписанные контейнеры вокруг: у сундука с табличкой надпись живёт
    /// в его же блок-сущности, и прилавком служит сам сундук — отдельной
    /// таблички рядом нет.
    /// </summary>
    public IReadOnlyList<(BlockPos Pos, string Kind, string Text)> LabeledContainersNear(double radius = 12)
    {
        if (ctx.Entities.Self is not { } self)
            return [];
        return NearbyEntities(self.X, self.Y, self.Z, radius)
            .Where(be => IsContainerClass(be.ClassName))
            .Select(be => (be.Pos, Kind: be.ClassName, Text: LabelOf(be) ?? ""))
            .Where(c => c.Text.Length > 0)
            .ToList();
    }

    /// <summary>Надпись на контейнере (у разных сундуков поле называется по-разному).</summary>
    public static string? LabelOf(BlockEntityInfo be)
    {
        var attrs = be.Attributes;
        if (attrs == null)
            return null;
        foreach (var key in (string[])["text", "label", "labelText"])
            if (attrs.GetString(key) is { Length: > 0 } s)
                return s;
        return null;
    }

    /// <summary>Блок-сущности вокруг точки — только то, что прислал сервер.</summary>
    private IEnumerable<BlockEntityInfo> NearbyEntities(double x, double y, double z, double radius)
    {
        double r2 = radius * radius;
        return ctx.World.AllBlockEntities.Where(be =>
            Math.Pow(be.Pos.X + 0.5 - x, 2) + Math.Pow(be.Pos.Y + 0.5 - y, 2) +
            Math.Pow(be.Pos.Z + 0.5 - z, 2) <= r2);
    }

    /// <summary>
    /// Дальность руки игрока. Своего числа здесь нет: длина руки одна на весь
    /// проект (<see cref="Hands.GameReach"/> — константа игры). Литерал 4.5 с
    /// припиской «PickingRange игры» и был второй копией.
    /// </summary>
    public double ReachDistance { get; set; } = Hands.GameReach;

    /// <summary>Сколько ждать содержимое после открытия.</summary>
    public int OpenDelayMs { get; set; } = 700;

    /// <summary>Закрывать контейнер после осмотра (как игрок закрывает окно).</summary>
    public bool CloseAfterInspect { get; set; } = true;

    private static double Dist2(BlockPos p, EntityInfo self) =>
        Math.Pow(p.X + 0.5 - self.X, 2) + Math.Pow(p.Y + 0.5 - self.Y, 2) +
        Math.Pow(p.Z + 0.5 - self.Z, 2);
}
