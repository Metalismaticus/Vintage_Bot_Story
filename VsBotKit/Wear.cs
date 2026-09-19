// Тип инструмента берём из игры поимённо: тянуть весь Vintagestory.API.Common
// нельзя — там свой Func<>, он конфликтует с системным (см. Hands.cs)
using EnumTool = Vintagestory.API.Common.EnumTool;

namespace VsBotKit;

/// <summary>
/// Изношенный предмет: где лежит, сколько прочности осталось и сколько её всего.
/// </summary>
public sealed record WornItem(string InventoryId, int Slot, SlotContent Content, int Remaining, int Max)
{
    public string? Code => Content.Code;

    /// <summary>Доля оставшейся прочности, 0..1.</summary>
    public double Fraction => Max > 0 ? (double)Remaining / Max : 0;

    public override string ToString() => $"{Content.Code} {Remaining}/{Max} ({Fraction * 100:0}%)";
}

/// <summary>
/// Износ инструментов — МЕХАНИЗМ: сколько прочности осталось у предмета в руке
/// и по инвентарю, доживёт ли он до конца работы, есть ли замена.
///
/// Откуда цифры. Остаток кладёт в слот SelfState (атрибут стака "durability",
/// как его читает CollectibleObject.GetRemainingDurability), максимум —
/// поле Durability игрового объекта из реестра сервера (WorldModel.Durability).
/// Ничего сверх того, что видит живой игрок в подсказке предмета, тут нет.
///
/// Главная тонкость: остаток 0 у руды и досок — это НЕ «сломано», а «прочности
/// у предмета не бывает». Единственный признак — максимум из реестра: если он
/// нулевой, изнашиваться нечему. Поэтому все запросы про остаток возвращают
/// null для таких предметов, а не 0.
/// </summary>
public class ToolWear
{
    private readonly BotContext ctx;

    public ToolWear(BotContext ctx) => this.ctx = ctx;

    /// <summary>
    /// Инвентари, которые не считаем «своей сумкой»: курсор мыши — это не
    /// хранение, одежда и броня изнашиваются отдельно и в руку не берутся.
    /// </summary>
    private static bool IsStorage(string inventoryId) =>
        !inventoryId.StartsWith("mouse", StringComparison.Ordinal) &&
        !inventoryId.StartsWith("character", StringComparison.Ordinal);

    // ---------------- есть ли вообще прочность ----------------

    /// <summary>Полная прочность предмета по коду; 0 — предмет не изнашивается.</summary>
    public int MaxDurability(string? code) => ctx.World.Durability(code);

    /// <summary>Бывает ли у этого предмета прочность вообще (руда и зерно — нет).</summary>
    public bool HasDurability(string? code) => MaxDurability(code) > 0;

    /// <summary>
    /// Остаток прочности слота. null — «прочности не бывает» (пустой слот,
    /// ресурс, еда). Ноль вернётся только у предмета, которому износ положен:
    /// сервер такие обычно уничтожает сразу, но у части рецептов износ до нуля
    /// разрешён без поломки (DamageItem с destroyOnZeroDurability = false).
    /// </summary>
    public int? Remaining(SlotContent? slot)
    {
        if (slot == null || slot.IsEmpty || !HasDurability(slot.Code))
            return null;
        return Math.Max(0, slot.Durability);
    }

    /// <summary>Доля оставшейся прочности 0..1; null — прочности не бывает.</summary>
    public double? Fraction(SlotContent? slot)
    {
        if (Remaining(slot) is not { } left)
            return null;
        int max = MaxDurability(slot!.Code);
        return max > 0 ? (double)left / max : null;
    }

    // ---------------- предмет в руке ----------------

    /// <summary>Что бот держит (null, пока хотбар не пришёл с сервера).</summary>
    public SlotContent? Held => ctx.Hands.Held;

    /// <summary>Остаток прочности в руке; null — в руке нечему изнашиваться.</summary>
    public int? HeldRemaining => Remaining(Held);

    /// <summary>Доля прочности в руке; null — в руке нечему изнашиваться.</summary>
    public double? HeldFraction => Fraction(Held);

    /// <summary>
    /// Хватит ли предмета на <paramref name="uses"/> применений. Одно
    /// применение — минус единица прочности: и слом блока
    /// (CollectibleObject.OnBlockBrokenWith → DamageItem без аргумента), и удар
    /// по существу (OnAttackingWith) снимают ровно 1, если предмет вообще
    /// портится от такого источника (DamagedBy).
    /// Предмет без прочности не ломается никогда — false.
    /// </summary>
    public bool WillBreakWithin(SlotContent? slot, int uses) =>
        Remaining(slot) is { } left && left <= uses;

    /// <summary>Сломается ли то, что в руке, за столько применений.</summary>
    public bool HeldWillBreakWithin(int uses) => WillBreakWithin(Held, uses);

    // ---------------- по всему инвентарю ----------------

    /// <summary>
    /// Все свои предметы, у которых бывает прочность (хотбар и рюкзаки).
    /// Одежда и броня сюда не попадают — их носит ArmorKit, а не рука.
    /// </summary>
    public IEnumerable<WornItem> All()
    {
        foreach (var (invId, slots) in ctx.Self.OwnInventories)
        {
            if (!IsStorage(invId))
                continue;
            for (int i = 0; i < slots.Length; i++)
            {
                if (Remaining(slots[i]) is not { } left)
                    continue;
                yield return new WornItem(invId, i, slots[i], left, MaxDurability(slots[i].Code));
            }
        }
    }

    /// <summary>Изношенные сильнее порога (доля прочности ниже заданной), худшие первыми.</summary>
    public IEnumerable<WornItem> WornBelow(double fraction) =>
        All().Where(w => w.Fraction < fraction).OrderBy(w => w.Fraction);

    /// <summary>Суммарный остаток прочности всех предметов с таким куском кода.</summary>
    public int TotalRemaining(string codePart) =>
        All().Where(w => w.Code != null &&
                         w.Code.Contains(codePart, StringComparison.OrdinalIgnoreCase))
             .Sum(w => w.Remaining);

    // ---------------- замена ----------------

    /// <summary>
    /// Что считаем «тем же инструментом»: сперва тип из реестра (Pickaxe, Axe —
    /// тогда медная кирка заменяет каменную), а если предмет вовсе не инструмент
    /// (щит, кресало), то замена — только точно такой же код.
    /// </summary>
    private bool SameKind(string? code, string? otherCode)
    {
        if (code == null || otherCode == null)
            return false;
        EnumTool? tool = ctx.World.ToolOf(code);
        return tool != null ? ctx.World.ToolOf(otherCode) == tool : otherCode == code;
    }

    /// <summary>
    /// Запасной инструмент того же типа, у которого прочности не меньше
    /// <paramref name="minRemaining"/>. Лучший — с наибольшим тиром (иначе бот
    /// поменяет медную кирку на каменную и не пробьёт то, что бил), при равном
    /// тире — с наибольшим остатком. Слот <paramref name="exceptSlot"/>
    /// в инвентаре <paramref name="exceptInventoryId"/> пропускается: это тот
    /// самый изношенный предмет, который мы меняем.
    /// </summary>
    public WornItem? FindSpare(string? forCode, int minRemaining = 1,
        string? exceptInventoryId = null, int exceptSlot = -1)
    {
        WornItem? best = null;
        int bestTier = -1;
        foreach (var w in All())
        {
            if (w.InventoryId == exceptInventoryId && w.Slot == exceptSlot)
                continue;
            if (w.Remaining < minRemaining || !SameKind(forCode, w.Code))
                continue;
            int tier = ctx.World.ToolTier(w.Code);
            if (best == null || tier > bestTier || (tier == bestTier && w.Remaining > best.Remaining))
            {
                best = w;
                bestTier = tier;
            }
        }
        return best;
    }

    /// <summary>
    /// Заменить предмет в руке на запасной того же типа.
    /// Возвращает взятый предмет или null, если менять нечего или не вышло.
    /// Успехом считается только подтверждённая серверными обновлениями смена
    /// содержимого руки, а не отправленный пакет.
    /// </summary>
    public async Task<WornItem?> SwapHeldAsync(int minSpareRemaining = 1, CancellationToken ct = default)
    {
        if (Held is not { } held || Remaining(held) is null)
            return null;

        string? hotbarId = ctx.Self.GetInventoryId("hotbar");
        var spare = FindSpare(held.Code, minSpareRemaining, hotbarId, ctx.Hands.ActiveSlot);
        if (spare == null)
            return null;

        // Запасной уже в рабочей части хотбара — достаточно выбрать слот,
        // как игрок колесом мыши; перекладывать ничего не надо.
        // ЧТО ЗНАЧИТ «УЖЕ ПОД РУКОЙ», СПРАШИВАЕМ У РУК (Hands.InRightHandReach):
        // здесь стояла своя копия того же условия, а правая рука — это и есть
        // выбранное место хотбара (см. Hands.RightHandIsHotbar), и разойдись
        // копии — смена инструмента и окно управления считали бы «под рукой»
        // по-разному
        if (spare.InventoryId == hotbarId && ctx.Hands.InRightHandReach(hotbarId, spare.Slot))
        {
            await ctx.Hands.SelectAsync(spare.Slot);
        }
        else
        {
            // Из рюкзака — руками Hands: они переложат в свободный слот хотбара
            // и подтвердят, что предмет действительно доехал
            int? taken = await ctx.Hands.TakeToHandAsync(
                s => s.Code == spare.Content.Code && Remaining(s) >= minSpareRemaining, ct);
            if (taken == null)
                return null;
        }

        // Проверяем по состоянию, а не по факту отправки
        var nowHeld = Held;
        if (nowHeld == null || nowHeld.Code != spare.Content.Code)
            return null;
        return spare with { Content = nowHeld, Remaining = Remaining(nowHeld) ?? spare.Remaining };
    }
}

/// <summary>
/// Способность: не работать сломанным. Когда у предмета в руке прочности почти
/// не осталось, бот сам берёт запасной такой же — как игрок, заметивший красную
/// полоску. Порог задаётся и в долях, и в абсолютных единицах: 5 % от каменного
/// топора и от стального — разные числа ударов.
///
/// Что делать, когда замены нет, решает роль: способность только сообщает
/// об этом событием OnToolWorn (код предмета, остаток) и больше не мешает.
/// </summary>
public class BehaviorSwapWornTool : BotBehavior
{
    private readonly BotContext ctx;
    private readonly ToolWear wear;

    /// <summary>Менять, когда осталась эта доля прочности или меньше.</summary>
    public double WornFraction { get; set; } = 0.05;

    /// <summary>Менять, когда осталось столько ударов или меньше (что раньше наступит).</summary>
    public int WornRemaining { get; set; } = 15;

    /// <summary>Запасной годится, если у него прочности хотя бы столько.</summary>
    public int MinSpareRemaining { get; set; } = 30;

    /// <summary>Как часто смотреть на руку, секунд.</summary>
    public double CheckIntervalSeconds { get; set; } = 2;

    /// <summary>
    /// Как часто повторять предупреждение об одном и том же изношенном
    /// предмете, секунд. Иначе событие сыпалось бы каждый тик.
    /// </summary>
    public double WarnIntervalSeconds { get; set; } = 60;

    /// <summary>Инструмент почти кончился: код предмета и остаток прочности.</summary>
    public event Action<string, int>? OnToolWorn;

    /// <summary>Инструмент заменён: что было и что взяли.</summary>
    public event Action<string, string>? OnToolSwapped;

    private DateTime lastCheck = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> lastWarn = new(StringComparer.Ordinal);

    public BehaviorSwapWornTool(BotContext ctx, ToolWear wear)
    {
        this.ctx = ctx;
        this.wear = wear;
    }

    public BehaviorSwapWornTool(BotContext ctx) : this(ctx, new ToolWear(ctx)) { }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - lastCheck).TotalSeconds < CheckIntervalSeconds)
            return false;
        lastCheck = DateTime.UtcNow;

        // Рука ещё не известна или в руке ресурс — изнашиваться нечему
        if (ctx.Hands.Held is not { } held || held.Code is not { } code)
            return false;
        if (wear.Remaining(held) is not { } left)
            return false;
        if (wear.Fraction(held) is not { } fraction)
            return false;

        if (fraction > WornFraction && left > WornRemaining)
            return false;

        // РУКА — ЧАСТЬ ТЕЛА, И ХОЗЯИН У НЕЁ ТОТ ЖЕ. Подмена сточенного
        // инструмента — такое же распоряжение рукой, как «оружие к ночи»:
        // пока телом занят кто-то не легче (приказ из чата, бой), менять ему
        // предмет в руке посреди удара нельзя — добыча читает это как кражу
        // руки и бросает удар («руку забрали посреди удара»). Дождёмся своей
        // очереди; сточенный инструмент доломается, и добыча сама возьмёт
        // следующий из хотбара
        using var hold = ctx.Turn.TryTake(Title, BodyArbiter.Importance.Routine, ct);
        if (hold == null)
            return false;   // кто держит тело — уже сказано вслух распорядителем

        var spare = await wear.SwapHeldAsync(MinSpareRemaining, hold.Token);
        if (spare != null)
        {
            OnToolSwapped?.Invoke(code, spare.Content.Code ?? "?");
            return true; // ход потрачен на смену инструмента
        }

        // Замены нет — сказать об этом и не мешать остальным способностям
        if (!lastWarn.TryGetValue(code, out var when) ||
            (DateTime.UtcNow - when).TotalSeconds >= WarnIntervalSeconds)
        {
            lastWarn[code] = DateTime.UtcNow;
            OnToolWorn?.Invoke(code, left);
        }
        return false;
    }
}
