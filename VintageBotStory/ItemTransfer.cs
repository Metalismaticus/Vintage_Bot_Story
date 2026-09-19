using VsBotKit;

namespace VintageBotStory;

/// <summary>
/// Перенос предметов между контейнерами — по ЖИВОМУ инвентарю, а не по снимку.
/// <para>
/// Главное, что выяснилось про сервер: содержимое блок-сущности сундука он
/// после перекладывания клиенту НЕ пересылает. Планировать по нему нельзя —
/// именно из-за этого бот бил по замороженным номерам слотов («просил 15,
/// взял 1», следом «просил 14, взял 0»).
/// </para>
/// <para>
/// Правильный канал: правым кликом открыть сундук — сервер пришлёт его
/// инвентарь целиком и дальше будет править по одному слоту. Поэтому здесь:
/// открыли один раз, НЕ закрываем до конца работы (закрытие снимает инвентарь
/// с регистрации, и перекладывание начинает гибнуть молча), после каждого
/// пакета ждём подтверждения и считаем ФАКТ по разнице, а не «сколько просили».
/// </para>
/// </summary>
public sealed class ItemTransfer
{
    private readonly BotContext ctx;

    public ItemTransfer(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    /// <summary>Сколько ждать ответа сервера на одно перекладывание.</summary>
    public int ConfirmMs { get; set; } = 700;

    /// <summary>Сколько ждать инвентарь после открытия сундука.</summary>
    public int OpenMs { get; set; } = 900;

    /// <summary>Id инвентаря так, как его называет сервер: «класс-X, Y, Z».</summary>
    public string InventoryId(BlockPos p) =>
        $"{ctx.World.GetInventoryClass(p.X, p.Y, p.Z)}-{p.X}, {p.Y}, {p.Z}";

    /// <summary>
    /// Живые слоты контейнера; null — сервер инвентарь не прислал.
    /// Имя класса угадывать НЕ обязательно: ищем любой инвентарь, чей id
    /// заканчивается координатами этого сундука — так работает с любым
    /// типом контейнера, включая модовые.
    /// </summary>
    public SlotContent[]? Slots(BlockPos p)
    {
        if (ctx.Self.Inventories.TryGetValue(InventoryId(p), out var byGuess))
            return byGuess;
        string tail = $"{p.X}, {p.Y}, {p.Z}";
        foreach (var (id, slots) in ctx.Self.Inventories)
            if (id.EndsWith(tail, StringComparison.Ordinal))
                return slots;
        return null;
    }

    /// <summary>Настоящий id инвентаря сундука (для пакетов перекладывания).</summary>
    public string RealId(BlockPos p)
    {
        string tail = $"{p.X}, {p.Y}, {p.Z}";
        foreach (var id in ctx.Self.Inventories.Keys)
            if (id.EndsWith(tail, StringComparison.Ordinal))
                return id;
        return InventoryId(p);
    }

    /// <summary>
    /// Открыть контейнер и дождаться его инвентаря. Держим открытым, пока
    /// идёт работа: закрытие отбирает у нас право писать в него.
    /// </summary>
    public async Task<bool> OpenAsync(BlockPos p, CancellationToken ct)
    {
        // ПОДХОДИМ ВСЕГДА, а не «если по расчёту далеко»: сундук за стеной
        // может быть в четырёх блоках по прямой, но открыть его нельзя.
        // И кэш прошлого осмотра НЕ считается открытием — сервер принимает
        // записи только в контейнер, который мы честно открыли, стоя рядом
        if (StandNear(p) is { } spot)
        {
            var self = ctx.Entities.Self;
            double away = self == null ? 99
                : Math.Sqrt(Math.Pow(spot.X + 0.5 - self.X, 2) +
                            Math.Pow(spot.Z + 0.5 - self.Z, 2));
            if (away > 1.0 && !await ctx.Movement.MoveToCellAsync(spot, ct))
            {
                OnLog?.Invoke($"к сундуку {p} не подойти (вставал бы на {spot})");
                return false;
            }
        }

        // Не дотянулись — ещё одна попытка подойти вплотную, и только потом
        // сдаёмся: слишком строгая проверка отсекала достижимые сундуки
        if (ctx.Entities.Self is { } me &&
            me.DistanceTo(p.X + 0.5, p.Y + 0.5, p.Z + 0.5) > ReachDistance &&
            StandNear(p) is { } again)
        {
            await ctx.Movement.MoveToCellAsync(again, ct);
            if (ctx.Entities.Self is { } after &&
                after.DistanceTo(p.X + 0.5, p.Y + 0.5, p.Z + 0.5) > ReachDistance)
            {
                OnLog?.Invoke($"до сундука {p} не дотянуться " +
                              $"(стою в {WorldOrigin.SayFlat(after.X, after.Z)})");
                return false;
            }
        }

        await ctx.Movement.FaceAsync(p.X + 0.5, p.Z + 0.5);
        // Забываем прошлое содержимое, иначе примем его за свежее и увидим
        // товар, которого там уже нет
        ctx.Self.ForgetInventory(InventoryId(p));
        await ctx.Actions.OpenContainerAsync(p);
        // Даём серверу зарегистрировать открытие: без этой паузы мы писали
        // в контейнер, который для него ещё закрыт
        await Task.Delay(OpenSettleMs, ct).ContinueWith(_ => { });

        var deadline = DateTime.UtcNow.AddMilliseconds(OpenMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (Slots(p) != null)
                return true;
            await Task.Delay(50, ct).ContinueWith(_ => { });
        }
        OnLog?.Invoke($"сундук {p}: инвентаря нет. Ждал «{InventoryId(p)}», " +
                      $"а знаю: {string.Join(", ", ctx.Self.Inventories.Keys)}");
        return false;
    }

    /// <summary>Сколько ждать после клика, пока сервер оформит открытие.</summary>
    public int OpenSettleMs { get; set; } = 450;

    /// <summary>
    /// Дальность руки игрока — ближе неё сундук уже доступен. Число берётся у
    /// руки (<see cref="Hands.GameReach"/> — константа игры), а не пишется
    /// здесь: третья копия одного и того же 4.5 стояла именно тут.
    /// </summary>
    public double ReachDistance { get; set; } = Hands.GameReach;

    /// <summary>
    /// Где встать, чтобы дотянуться до сундука: ближайшая проходимая клетка
    /// вокруг него. Саму клетку сундука брать нельзя — она сплошная.
    /// </summary>
    private BlockPos? StandNear(BlockPos chest)
    {
        BlockPos? best = null;
        double bestDist = double.MaxValue;
        var self = ctx.Entities.Self;
        foreach (var (dx, dz) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1), (1, -1), (-1, 1)])
        {
            int x = chest.X + dx, z = chest.Z + dz;
            if (ctx.World.FindStandableY(x, chest.Y, z, up: 1, down: 2) is not { } y)
                continue;
            double d = self == null ? 0
                : Math.Pow(x + 0.5 - self.X, 2) + Math.Pow(z + 0.5 - self.Z, 2);
            if (d < bestDist)
            {
                bestDist = d;
                best = new BlockPos(x, y, z);
            }
        }
        return best;
    }

    /// <summary>Закрыть контейнер — только когда с ним всё закончено.</summary>
    public Task CloseAsync(BlockPos p) =>
        ctx.Actions.CloseContainerAsync(p, ctx.World.GetInventoryClass(p.X, p.Y, p.Z));

    /// <summary>Сколько предметов с таким кодом лежит в живом инвентаре.</summary>
    public static int Count(SlotContent[]? slots, string codePart) =>
        slots == null ? 0 : slots
            .Where(s => s.Code is { } c && s.Count > 0 &&
                        c.Contains(codePart, StringComparison.OrdinalIgnoreCase))
            .Sum(s => s.Count);

    /// <summary>
    /// Сколько ещё предметов с таким кодом влезет в контейнер по ЖИВЫМ слотам:
    /// пустые слоты по полному стаку плюс место в неполных стаках того же
    /// предмета. Нужно, чтобы честно сказать «в кассе нет места», а не гадать.
    /// </summary>
    public int RoomFor(SlotContent[]? slots, string codePart)
    {
        if (slots == null)
            return 0;

        // Размер стака спрашиваем у НАЙДЕННОГО в сундуке предмета: снаружи нам
        // дают часть кода («gear-rusty»), а в реестре код может быть длиннее.
        // Ничего похожего не лежит — спрашиваем по самой строке
        string full = slots.FirstOrDefault(s => s.Code is { } c && s.Count > 0 &&
            c.Contains(codePart, StringComparison.OrdinalIgnoreCase))?.Code ?? codePart;
        int max = ctx.World.MaxStackSize(full);

        int room = 0;
        foreach (var s in slots)
        {
            if (s.Code == null || s.Count <= 0)
                room += max;
            else if (max > 1 && s.Count < max && s.Durability <= 0 &&
                     s.Code.Equals(full, StringComparison.OrdinalIgnoreCase))
                room += max - s.Count;
        }
        return room;
    }

    /// <summary>
    /// Перенести до <paramref name="want"/> предметов между открытыми
    /// контейнерами. Возвращает СКОЛЬКО РЕАЛЬНО ПЕРЕЕХАЛО (по разнице в
    /// приёмнике), а не сколько было запрошено.
    /// </summary>
    /// <param name="leaveInSource">
    /// Сколько обязательно оставить в источнике: образец на прилавке,
    /// неснижаемый остаток в кассе.
    /// </param>
    public async Task<int> MoveAsync(BlockPos from, BlockPos to, string codePart,
        int want, int leaveInSource, CancellationToken ct)
    {
        if (want <= 0)
            return 0;

        // СЕРВЕР ДЕРЖИТ ОТКРЫТЫМ ТОЛЬКО ОДИН КОНТЕЙНЕР. Открыв приёмник, мы
        // теряем источник, и перекладывание между двумя сундуками напрямую
        // невозможно — именно поэтому «слот 0→0 не принял». Носим через себя,
        // как живой игрок: набрал в руки, дошёл, выложил
        int total = 0;
        for (int trip = 0; trip < MaxTrips && total < want && !ct.IsCancellationRequested; trip++)
        {
            int took = await TakeAsync(from, codePart, want - total, leaveInSource, ct);
            if (took <= 0)
                break;
            int put = await PutAsync(to, codePart, took, ct);
            total += put;
            if (put < took)
            {
                // Не влезло — возвращаем остаток туда, откуда взяли
                await PutAsync(from, codePart, took - put, ct);
                OnLog?.Invoke($"{codePart}: в {to} не влезло {took - put}, вернул в {from}");
                break;
            }
        }
        if (total < want)
            OnLog?.Invoke($"перенос {codePart}: просил {want}, переехало {total}");
        return total;
    }

    /// <summary>Сколько рейсов максимум на один перенос.</summary>
    public int MaxTrips { get; set; } = 8;

    private string BagId => ctx.Self.GetInventoryId("hotbar") ?? "hotbar";
    private SlotContent[]? Bag => ctx.Self.GetInventory("hotbar");

    /// <summary>Грузовые слоты хотбара (10 и 11 — умение и левая рука).</summary>
    public int CargoSlots { get; set; } = 10;

    private int InBag(string codePart)
    {
        var bag = Bag;
        if (bag == null)
            return 0;
        int n = 0;
        for (int i = 0; i < Math.Min(CargoSlots, bag.Length); i++)
            if (bag[i].Code is { } c && bag[i].Count > 0 &&
                c.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                n += bag[i].Count;
        return n;
    }

    /// <summary>Набрать предметы из сундука себе в руки. Возвращает факт.</summary>
    private async Task<int> TakeAsync(BlockPos chest, string codePart, int want,
        int leaveInSource, CancellationToken ct)
    {
        if (!await OpenAsync(chest, ct))
            return 0;
        string srcId = RealId(chest);
        int got = 0;

        for (int guard = 0; guard < 32 && got < want && !ct.IsCancellationRequested; guard++)
        {
            var src = Slots(chest);
            var bag = Bag;
            if (src == null || bag == null)
                break;
            int available = Count(src, codePart) - leaveInSource;
            if (available <= 0)
                break;

            int si = -1;
            for (int i = 0; i < src.Length; i++)
                if (src[i].Code is { } c && src[i].Count > 0 &&
                    c.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                {
                    si = i;
                    break;
                }
            if (si < 0)
                break;

            if (FirstPutTarget(bag, src[si].Code!, CargoSlots, src[si]) is not { } slot)
                break; // руки полны — унесём этот рейс
            int bi = slot.Slot;

            // Больше, чем влезет в слот, просить нельзя: сервер перенесёт
            // сколько влезло, а мы решим, что «отдалось не всё», и сдадимся
            int qty = Math.Min(Math.Min(want - got, available),
                               Math.Min(src[si].Count, slot.Space));
            int before = InBag(codePart);
            await ctx.Actions.MoveItemAsync(srcId, si, BagId, bi, qty);
            int delta = await AwaitBagDeltaAsync(codePart, before, ct);
            if (delta <= 0)
            {
                OnLog?.Invoke($"{codePart}: из {chest} слот {si} не отдался");
                break;
            }
            got += delta;
        }

        await CloseAsync(chest);
        return got;
    }

    /// <summary>Выложить предметы из рук в сундук. Возвращает факт.</summary>
    private async Task<int> PutAsync(BlockPos chest, string codePart, int want, CancellationToken ct)
    {
        if (!await OpenAsync(chest, ct))
            return 0;
        string dstId = RealId(chest);
        int put = 0;
        var refused = new HashSet<int>();

        for (int guard = 0; guard < 32 && put < want && !ct.IsCancellationRequested; guard++)
        {
            var dst = Slots(chest);
            var bag = Bag;
            if (dst == null || bag == null)
                break;

            int bi = -1;
            for (int i = 0; i < Math.Min(CargoSlots, bag.Length); i++)
                if (bag[i].Code is { } c && bag[i].Count > 0 &&
                    c.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                {
                    bi = i;
                    break;
                }
            if (bi < 0)
                break;

            // ПЕРЕБИРАЕМ ВСЕ ПОДХОДЯЩИЕ СЛОТЫ, а не только первый: сундук
            // мог быть занят чужим добром, и первый выбор ничего не значит.
            // Заведомо безнадёжные (полный стак, чужой предмет, инструмент
            // поверх инструмента) в перебор уже не попадают — см. PutTargets
            int delta = 0;
            bool offered = false;
            foreach (var (di, space) in PutTargets(dst, bag[bi].Code!, dst.Length, bag[bi], refused))
            {
                offered = true;
                int qty = Math.Min(Math.Min(want - put, bag[bi].Count), space);
                if (qty <= 0)
                    continue;
                int before = Count(Slots(chest), codePart);
                await ctx.Actions.MoveItemAsync(BagId, bi, dstId, di, qty);
                delta = await AwaitDeltaAsync(chest, codePart, before, ct);
                if (delta > 0)
                    break;
                refused.Add(di); // этот слот не берёт — больше не предлагаем
            }
            if (!offered)
            {
                // Мест нет вовсе — это не поломка, а полный сундук. Говорим
                // именно так, иначе разбирающийся идёт искать ошибку в адресах
                OnLog?.Invoke($"в {chest} некуда класть {bag[bi].Code}: слотов {dst.Length}, " +
                              $"свободных подходящих нет" +
                              (refused.Count > 0 ? $" (ещё {refused.Count} отказались принимать)" : ""));
                break;
            }
            if (delta <= 0)
            {
                // Отказали ВСЕ слоты — значит дело не в них, а в адресе:
                // пакет уходит на инвентарь, которого сервер не знает
                OnLog?.Invoke($"в {chest} не принял НИ ОДИН слот. Писал по адресу «{dstId}», " +
                              $"слотов там {dst.Length}, знаю адреса: {string.Join(", ", ctx.Self.Inventories.Keys)}");
                break;
            }
            put += delta;
        }

        await CloseAsync(chest);
        return put;
    }

    /// <summary>
    /// Куда предмет РЕАЛЬНО поместится, по порядку предпочтения, вместе с
    /// местом в слоте.
    /// <para>
    /// СНАЧАЛА ПУСТОЙ СЛОТ, и только если пустых нет — доливка в такой же
    /// неполный стак. Раньше было наоборот, и на инструментах перенос
    /// возвращал ноль: сервер (ItemSlot.TryPutInto → CanTakeFrom →
    /// CollectibleObject.GetMergableQuantity) отдаёт 0, если стак-приёмник
    /// уже набран до MaxStackSize, а у инструмента MaxStackSize == 1 —
    /// значит ЛЮБОЙ занятый слот для него безнадёжен.
    /// </para>
    /// <para>
    /// Размер стака берётся из реестра сервера (WorldModel.MaxStackSize →
    /// CollectibleObject.MaxStackSize), а не из таблицы в коде. Неизвестный
    /// код даёт 1 — то есть «только пустой слот», это безопасная сторона.
    /// </para>
    /// <para>
    /// Изношенные предметы не сливаются даже при MaxStackSize &gt; 1:
    /// GetMergableQuantity сравнивает стаки через
    /// ItemStack.Equals(..., GlobalConstants.IgnoredStackAttributes), а
    /// «durability» в списке игнорируемых атрибутов НЕТ.
    /// </para>
    /// </summary>
    private IEnumerable<(int Slot, int Space)> PutTargets(SlotContent[] slots, string code,
        int limit, SlotContent source, HashSet<int>? refused = null)
    {
        int max = ctx.World.MaxStackSize(code);
        int n = Math.Min(limit, slots.Length);

        for (int i = 0; i < n; i++)
            if ((refused == null || !refused.Contains(i)) &&
                (slots[i].Code == null || slots[i].Count <= 0))
                yield return (i, max);

        // Не стакается или изношен — доливать некуда, и предлагать занятые
        // слоты бессмысленно: каждая такая попытка стоит пакета и ожидания
        if (max <= 1 || source.Durability > 0)
            yield break;

        for (int i = 0; i < n; i++)
            if ((refused == null || !refused.Contains(i)) &&
                slots[i].Code is { } c && slots[i].Count > 0 && slots[i].Count < max &&
                slots[i].Durability <= 0 &&
                c.Equals(code, StringComparison.OrdinalIgnoreCase))
                yield return (i, max - slots[i].Count);
    }

    /// <summary>Первый подходящий слот-приёмник или null, если некуда.</summary>
    private (int Slot, int Space)? FirstPutTarget(SlotContent[] slots, string code,
        int limit, SlotContent source)
    {
        foreach (var t in PutTargets(slots, code, limit, source))
            return t;
        return null;
    }

    /// <summary>Дождаться прироста в своих руках.</summary>
    private async Task<int> AwaitBagDeltaAsync(string codePart, int before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ConfirmMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(40, ct).ContinueWith(_ => { });
            int now = InBag(codePart);
            if (now > before)
                return now - before;
        }
        return 0;
    }

    /// <summary>
    /// Дождаться подтверждения от сервера и вернуть НАСТОЯЩИЙ прирост.
    /// Сервер правит слоты отдельными пакетами, поэтому ждём события, а не
    /// «спим и надеемся» — прежний код именно так и обманывал сам себя.
    /// </summary>
    private async Task<int> AwaitDeltaAsync(BlockPos where, string codePart, int before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ConfirmMs);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(40, ct).ContinueWith(_ => { });
            int now = Count(Slots(where), codePart);
            if (now > before)
                return now - before;
        }
        return 0;
    }
}
