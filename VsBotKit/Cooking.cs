namespace VsBotKit;

/// <summary>
/// ЧИСТОЕ ПРАВИЛО ПЕРЕДЕЛА «СЫРОЕ → ГОТОВОЕ»: стоит ли класть эту вещь в огонь.
///
/// ЗАЧЕМ ОТДЕЛЬНОЕ ПРАВИЛО, РАЗ ЕСТЬ РЕЕСТР. Бот давно умел спросить «плавится
/// ли это» (<c>CombustibleProps.SmeltedStack != null</c>), но не различал ГОТОВКУ
/// и ПЛАВКУ: по этой мерке медная руда и кусок мяса — одно и то же. Игра их
/// различает сама, полем <c>smeltingType</c>, и переливать руду в костёр под
/// видом обеда нельзя.
///
/// И ЭТО НЕ УКРАШЕНИЕ, А ОБЯЗАТЕЛЬНОЕ ЗВЕНО ЦЕПОЧКИ. У сырого мяса
/// (<c>bushmeat-raw</c>, <c>redmeat-raw</c>, <c>poultry-raw</c>) в описаниях игры
/// 1.22.7 <c>nutritionProps</c> НЕТ ВОВСЕ — они появляются только у
/// <c>*-cooked</c> и <c>*-cured</c>. Значит <c>GetSatietyByCode("redmeat-raw")</c>
/// равна нулю, вся еда бота ищется по сытости — и убитый волк без готовки для
/// него ровно ноль калорий.
///
/// Числа и имена сюда не выдуманы: всё, что правило знает, ему передают из
/// реестра сервера. Поэтому его и можно проверить стендом без игры.
/// </summary>
public static class CookRules
{
    /// <summary>
    /// ЧТО ПОЛУЧИТСЯ ИЗ ЭТОГО НА КОСТРЕ, если готовить стоит; null — не стоит,
    /// и причину скажет <see cref="Почему"/>.
    /// </summary>
    /// <param name="готовка">
    /// Игра назвала передел ГОТОВКОЙ (<c>smeltingType == cook</c>), а не плавкой,
    /// обжигом или дублением.
    /// </param>
    /// <param name="воЧто">Код результата из реестра (пусто — реестр молчит).</param>
    /// <param name="сытостьСырого">Сытость исходной вещи по реестру.</param>
    /// <param name="сытостьГотового">Сытость результата по реестру.</param>
    public static string? ЧтоПолучится(bool готовка, string? воЧто,
        float сытостьСырого, float сытостьГотового) =>
        Почему(готовка, воЧто, сытостьСырого, сытостьГотового) == null ? воЧто : null;

    /// <summary>
    /// ПОЧЕМУ ГОТОВИТЬ НЕ СТОИТ; null — стоит. Отдельный ответ нарочно: молчать
    /// про отказ нельзя (правило 4), а склеивать «нельзя» с «почему» в одну
    /// строку значит терять одно из двух.
    /// </summary>
    public static string? Почему(bool готовка, string? воЧто,
        float сытостьСырого, float сытостьГотового)
    {
        if (!готовка)
            return "игра зовёт этот передел не готовкой — в костре из него еды не выйдет";
        if (воЧто is not { Length: > 0 })
            return "во что оно превратится, реестр сервера не сказал";
        if (сытостьГотового <= 0)
            return $"из этого выйдет {воЧто}, а оно несъедобно по реестру";
        if (сытостьСырого >= сытостьГотового)
            return $"сырое сытнее готового ({сытостьСырого:0.#} против {сытостьГотового:0.#}) — " +
                   "жечь на это дрова незачем";
        return null;
    }
}

/// <summary>
/// Готовка на костре: пожарить кусок мяса, сварить похлёбку в горшке, забрать
/// готовое. Это последнее звено выживания: добыл — развёл огонь — приготовил.
///
/// Механика игры, на которую опирается механизм (BlockEntityFirepit,
/// InventorySmelting, BlockFirepit.OnBlockInteractStart):
/// - у костра СЕМЬ слотов: 0 — топливо, 1 — вход, 2 — выход,
///   3..6 — слоты горшка (работают, только когда в слоте входа стоит горшок);
/// - положить сырое = ПРИСЕСТЬ и ткнуть предметом в костёр. Сервер сам решит,
///   куда он пойдёт: с температурой плавления — во вход, горючее — в топливо;
/// - горшок (BlockSmeltingContainer) кладётся в вход БЕЗ приседания;
/// - ингредиенты в горшок раскладываются уже через открытый костёр —
///   обычный перенос предметов, как в сундуке;
/// - готовность видна по слоту выхода, а не по таймеру: пока там пусто —
///   не готово. Костёр присылает своё состояние сам, потому что клиент рисует
///   огонь и содержимое (FirepitContentsRenderer).
///
/// Всё это — механизм. ЧТО готовить и когда — политика роли.
/// </summary>
public class Cooking
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Fire fire;

    public Cooking(BotContext ctx, Hands hands, Fire fire)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.fire = fire;
    }

    public event Action<string>? OnLog;

    /// <summary>Слот топлива.</summary>
    public const int FuelSlot = 0;

    /// <summary>Слот входа: сырое мясо, руда или горшок.</summary>
    public const int InputSlot = 1;

    /// <summary>Слот выхода: готовое.</summary>
    public const int OutputSlot = 2;

    /// <summary>Четыре слота внутри горшка.</summary>
    public static readonly int[] PotSlots = [3, 4, 5, 6];

    /// <summary>Сколько ждать готовности, если роль не сказала иначе.</summary>
    public double CookTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Id инвентаря костра для переноса предметов. У костра он свой:
    /// «smelting-x/y/z» через косые черты, не как у сундука
    /// (BlockEntityFirepit.Initialize: LateInitialize("smelting-" + x + "/" ...)).
    /// Ошибиться тут — значит молча ничего не перенести.
    /// </summary>
    public static string FirepitInventoryId(BlockPos pit) =>
        $"smelting-{pit.X}/{pit.Y}/{pit.Z}";

    /// <summary>
    /// Что лежит в костре: семь слотов или null, если это не костёр.
    ///
    /// Спрашиваем ВИДИМОЕ СНАРУЖИ, а не «загляни внутрь»: игра рисует
    /// содержимое костра (FirepitContentsRenderer, см. разбор у класса), и
    /// человек видит мясо на огне и готовое в выходе, просто стоя рядом.
    /// Требовать за это открытия — сделать бота слепее живого игрока, а
    /// готовка только тем и живёт, что поглядывает на костёр, пока ждёт.
    /// Сундук такого не позволяет, и его содержимое спрашивают иначе.
    /// </summary>
    public SlotContent[]? Contents(BlockPos pit) => ctx.World.VisibleContents(pit);

    /// <summary>Содержимое одного слота костра (null — пусто).</summary>
    public SlotContent? SlotAt(BlockPos pit, int slot)
    {
        var all = Contents(pit);
        if (all == null || slot < 0 || slot >= all.Length)
            return null;
        return all[slot].Code == null ? null : all[slot];
    }

    /// <summary>Что уже готово и лежит в выходе.</summary>
    public SlotContent? Ready(BlockPos pit) => SlotAt(pit, OutputSlot);

    /// <summary>Что жарится прямо сейчас.</summary>
    public SlotContent? Cooking_(BlockPos pit) => SlotAt(pit, InputSlot);

    /// <summary>
    /// Положить предмет из рук в костёр приседанием — ровно как игрок.
    /// Сервер сам разложит: сырое во вход, дрова в топливо. Проверяем по
    /// инвентарю бота, что предмет действительно ушёл.
    /// </summary>
    public async Task<bool> PutInAsync(string code, BlockPos pit, CancellationToken ct = default)
    {
        if (await hands.TakeToHandAsync(code, ct) is null)
        {
            OnLog?.Invoke($"нет {code}");
            return false;
        }
        if (!await hands.ReachForAsync(pit, ct: ct))
            return false;

        int before = hands.CountOf(code);
        await hands.UseBlockAsync(pit, sneak: true, ct: ct);
        await Task.Delay(500, ct).ContinueWith(_ => { });

        if (hands.CountOf(code) >= before)
        {
            OnLog?.Invoke($"костёр не принял {code} (не жарится?)");
            return false;
        }
        OnLog?.Invoke($"положил {code} в костёр");
        return true;
    }

    /// <summary>
    /// Дождаться готового в слоте выхода. Заодно подкидывает дрова: без огня
    /// готовка стоит на месте, а мы бы просто ждали до упора.
    /// </summary>
    public async Task<SlotContent?> WaitReadyAsync(
        BlockPos pit, double maxSeconds = 0, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds > 0 ? maxSeconds : CookTimeoutSeconds);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (Ready(pit) is { } done)
            {
                OnLog?.Invoke($"готово: {done.Code} ×{done.Count}");
                return done;
            }
            if (!fire.IsBurning(pit))
            {
                OnLog?.Invoke("костёр погас — подкидываю дров");
                if (!await fire.AddFuelAsync(pit, 2, ct) || !await fire.IgniteAsync(pit, ct))
                {
                    OnLog?.Invoke("огонь не поддержать, готовка встала");
                    return null;
                }
            }
            await Task.Delay(2000, ct).ContinueWith(_ => { });
        }
        OnLog?.Invoke($"не дождался готового (в выходе {Ready(pit)?.Code ?? "пусто"})");
        return null;
    }

    /// <summary>Костёр как хранилище — общий слой (см. Containers).</summary>
    private Container Pit(BlockPos pit)
    {
        var box = Container.Firepit(ctx, hands, pit);
        box.OnLog += m => OnLog?.Invoke(m);
        return box;
    }

    /// <summary>Забрать готовое из костра.</summary>
    public Task<bool> TakeOutAsync(BlockPos pit, int slot = OutputSlot, CancellationToken ct = default) =>
        Pit(pit).TakeSlotAsync(slot, ct);

    /// <summary>
    /// Пожарить одну вещь целиком: развести огонь, положить, дождаться, забрать.
    ///
    /// ПРИЧИНА ВОЗВРАЩАЕТСЯ, А НЕ ОСТАЁТСЯ В ЖУРНАЛЕ, и это поправка приёмки.
    /// Прежде отказ был голым <c>null</c>, а «без костра не приготовить»,
    /// «костёр не принял», «огонь не поддержать» уходили только в журнал. В
    /// журнале рядом эти строки и правда найдутся — но человек, спросивший
    /// «!готовь» или «!добудьеду» в ЧАТЕ, получал ответ «почему — смотри выше»
    /// и не видел причины вовсе. Правило 4 требует называть причину в самом
    /// отказе.
    /// </summary>
    /// <returns>Что получилось и почему; <c>Cooked == null</c> — не вышло.</returns>
    public async Task<CookOutcome> CookAsync(
        string rawCode, BlockPos? at = null, CancellationToken ct = default)
    {
        if ((at ?? await fire.MakeCampfireAsync(ct)) is not { } pit)
        {
            OnLog?.Invoke("без костра не приготовить");
            return new CookOutcome(null, "без костра не приготовить");
        }
        if (!fire.IsBurning(pit) && !await fire.IgniteAsync(pit, ct))
            return new CookOutcome(null, $"костёр в {pit} не разжечь");
        if (!await PutInAsync(rawCode, pit, ct))
            return new CookOutcome(null, $"костёр не принял {rawCode}");

        var done = await WaitReadyAsync(pit, ct: ct);
        if (done == null)
            return new CookOutcome(null,
                $"за отпущенные {CookTimeoutSeconds:0} с готового не дождался " +
                $"(в выходе {Ready(pit)?.Code ?? "пусто"}; ручка «СколькоЖдатьГотовки»)");
        await TakeOutAsync(pit, OutputSlot, ct);
        return new CookOutcome(done.Code, $"приготовил {done.Code}");
    }

    /// <summary>
    /// Поставить горшок в костёр.
    ///
    /// Приседанием он туда НЕ попадёт: сервер кладёт во вход только то, у чего
    /// есть температура плавления (руда, сырое мясо) или что является тиглем
    /// (BlockSmeltingContainer). Глиняный горшок — BlockCookingContainer, и у
    /// него ни того, ни другого, поэтому клик по костру просто открывает окно.
    /// Значит и бот делает как игрок: открывает костёр и кладёт горшок мышью.
    /// </summary>
    public async Task<bool> PutPotAsync(BlockPos pit, string potCode = "claypot", CancellationToken ct = default)
    {
        if (SlotAt(pit, InputSlot) is { } busy)
        {
            OnLog?.Invoke($"во входе уже {busy.Code}");
            return false;
        }
        // «-cooked» — это горшок С ЕДОЙ, его в костёр ставить незачем
        if (ctx.Self.FindCarried(c => c.Contains(potCode, StringComparison.OrdinalIgnoreCase) &&
                                      !c.Contains("-cooked", StringComparison.OrdinalIgnoreCase)) is not { } pot)
        {
            OnLog?.Invoke($"нет пустого горшка ({potCode})");
            return false;
        }
        if (!await hands.ReachForAsync(pit, ct: ct))
            return false;

        var box = Pit(pit);
        await box.OpenAsync(ct);
        await Task.Delay(box.OpenDelayMs, ct).ContinueWith(_ => { });
        await box.MoveInAsync(pot.InventoryId, pot.Slot, InputSlot, 1);
        await Task.Delay(400, ct).ContinueWith(_ => { });
        await ctx.Actions.CloseBlockInventoryAsync(pit, FirepitInventoryId(pit));

        bool ok = SlotAt(pit, InputSlot) != null;
        OnLog?.Invoke(ok ? $"горшок в костре: {SlotAt(pit, InputSlot)?.Code}" : "горшок не встал");
        return ok;
    }

    /// <summary>
    /// Разложить ингредиенты по слотам горшка. Это обычный перенос предметов
    /// в открытый инвентарь — то же самое, что игрок делает мышью.
    /// Возвращает, сколько ингредиентов легло.
    /// </summary>
    public async Task<int> FillPotAsync(
        BlockPos pit, IEnumerable<string> ingredients, CancellationToken ct = default)
    {
        if (SlotAt(pit, InputSlot) is null)
        {
            OnLog?.Invoke("горшка в костре нет — некуда класть");
            return 0;
        }
        if (!await hands.ReachForAsync(pit, ct: ct))
            return 0;

        var box = Pit(pit);
        await box.OpenAsync(ct);
        await Task.Delay(box.OpenDelayMs, ct).ContinueWith(_ => { });

        int put = 0;
        foreach (string code in ingredients)
        {
            if (ct.IsCancellationRequested || put >= PotSlots.Length)
                break;
            if (ctx.Self.FindCarried(code) is not { } from)
            {
                OnLog?.Invoke($"нет ингредиента {code}");
                continue;
            }
            int target = PotSlots[put];
            if (SlotAt(pit, target) != null)
            {
                put++;
                continue;
            }
            await box.MoveInAsync(from.InventoryId, from.Slot, target, 1);
            await Task.Delay(300, ct).ContinueWith(_ => { });
            if (SlotAt(pit, target) != null)
            {
                OnLog?.Invoke($"в горшок: {code}");
                put++;
            }
            else
                OnLog?.Invoke($"{code} в горшок не лёг");
        }
        await ctx.Actions.CloseBlockInventoryAsync(pit, FirepitInventoryId(pit));
        OnLog?.Invoke($"в горшке {put} ингредиентов");
        return put;
    }

    /// <summary>
    /// Сварить похлёбку: горшок в костёр, ингредиенты в горшок, огонь, ждать.
    /// Готовый горшок с едой окажется в выходе — его забирают целиком.
    /// </summary>
    public async Task<string?> CookMealAsync(
        IEnumerable<string> ingredients, BlockPos? at = null,
        string potCode = "claypot", CancellationToken ct = default)
    {
        if ((at ?? await fire.MakeCampfireAsync(ct)) is not { } pit)
            return null;
        if (SlotAt(pit, InputSlot) is null && !await PutPotAsync(pit, potCode, ct))
            return null;
        if (await FillPotAsync(pit, ingredients, ct) == 0)
        {
            OnLog?.Invoke("пустой горшок варить нечего");
            return null;
        }
        if (!fire.IsBurning(pit) && !await fire.IgniteAsync(pit, ct))
            return null;

        var done = await WaitReadyAsync(pit, ct: ct);
        if (done == null)
            return null;
        await TakeOutAsync(pit, OutputSlot, ct);
        return done.Code;
    }

    // ---------------- передел «сырое → готовое» ----------------

    /// <summary>
    /// ВО ЧТО ЭТО ПРЕВРАТИТСЯ НА КОСТРЕ (null — готовить незачем).
    ///
    /// Спрашивается РЕЕСТР СЕРВЕРА, а не наш список кодов: так находится и
    /// модовая еда, и ни одного выдуманного имени тут нет (правило 6). Решает
    /// же <see cref="CookRules"/> — то самое правило, которое отличает готовку
    /// от плавки.
    /// </summary>
    public string? CookedOf(string code)
    {
        var melt = ctx.World.GetCollectible(code)?.CombustibleProps;
        bool готовка = melt?.SmeltingType == Vintagestory.API.Common.EnumSmeltType.Cook;
        string? воЧто = melt?.SmeltedStack?.Code?.ToShortString();
        return CookRules.ЧтоПолучится(готовка, воЧто,
            ctx.World.GetSatietyByCode(code),
            воЧто is { Length: > 0 } ? ctx.World.GetSatietyByCode(воЧто) : 0);
    }

    /// <summary>
    /// ЧТО ИЗ ЛЕЖАЩЕГО В СУМКАХ ОГОНЬ ПРЕВРАТИТ В НУЖНОЕ.
    ///
    /// Нужда называется свойством («что съесть»), поэтому и спрашивают её
    /// свойством: <paramref name="годится"/> — та же мерка, какой ищет еду сама
    /// еда и снабжение. Своего списка «что считать мясом» здесь нет и не будет.
    /// </summary>
    public (string Raw, string Cooked)? RawInBagsFor(Func<string, bool> годится)
    {
        foreach (var slot in ctx.Self.CarrySlots())
        {
            if (slot.Content.Code is not { Length: > 0 } code || slot.Content.Count <= 0)
                continue;
            if (CookedOf(code) is { } готовое && годится(готовое))
                return (code, готовое);
        }
        return null;
    }

    /// <summary>
    /// ПРИГОТОВИТЬ ТО, ЧТО ЕСТЬ, ВО ЧТО-ТО ГОДНОЕ ДЛЯ НУЖДЫ.
    ///
    /// ПРИГОТОВИЛ — ТОЛЬКО КОГДА ЕДА В СУМКЕ. Прямые слова заказа: «„приготовил“
    /// — только когда еда в сумке и она съедобна по реестру, а не когда положили
    /// в костёр». Именно этим и врала мёртвая вторая готовка в <see cref="Fire"/>:
    /// она докладывала «жарю» и на том кончалась.
    /// </summary>
    /// <summary>За сколько блоков искать свой (или чужой) готовый костёр.</summary>
    public double FirepitLookRadius { get; set; } = 8;

    /// <summary>Чем кончился один заход готовки.</summary>
    /// <param name="Ok">Готовое лежит В СУМКЕ и годится нужде.</param>
    /// <param name="Moved">Дело сдвинулось: положили, подкинули, довели.</param>
    /// <param name="Why">Что вышло — всегда словами.</param>
    public readonly record struct CookStep(bool Ok, bool Moved, string Why);

    /// <summary>
    /// Чем кончилась готовка ОДНОЙ вещи от огня до сумки.
    /// </summary>
    /// <param name="Cooked">Что получилось; null — не вышло.</param>
    /// <param name="Why">
    /// Причина словами, и она есть ВСЕГДА — в том числе у удачи. Отказ, который
    /// отсылает «смотри в журнал выше», человек в чате прочесть не может
    /// (правило 4).
    /// </param>
    public readonly record struct CookOutcome(string? Cooked, string Why);

    /// <summary>
    /// ПРИГОТОВИТЬ ТО, ЧТО ЕСТЬ, ВО ЧТО-ТО ГОДНОЕ ДЛЯ НУЖДЫ — ОДИН ЗАХОД.
    ///
    /// ПРИГОТОВИЛ — ТОЛЬКО КОГДА ЕДА В СУМКЕ. Прямые слова заказа: «„приготовил“
    /// — только когда еда в сумке и она съедобна по реестру, а не когда положили
    /// в костёр». Именно этим и врала мёртвая вторая готовка в <see cref="Fire"/>:
    /// она докладывала «жарю» и на том кончалась.
    ///
    /// ГОТОВКА ИДЁТ ЗАХОДАМИ, А НЕ ОДНИМ СТОЯНИЕМ У ОГНЯ. Тот, кто зовёт готовку
    /// ради нужды, ДЕРЖИТ ТЕЛО, и берёт он его рефлексом — той же ступенью, что
    /// и самооборона. Равный равного не перебивает (<see cref="BodyArbiter"/>),
    /// значит стояние у костра три минуты — это три минуты, когда бот не может
    /// отбиться. Поэтому за один заход бот ждёт <see cref="CookTimeoutSeconds"/>
    /// секунд, а не поспело — отходит и вернётся: мясо из костра никуда не
    /// денется, а огонь он тут же и поддержал.
    ///
    /// ВТОРОЙ ГОТОВКИ ЗДЕСЬ НЕТ. Три случая («уже готово», «жарится», «ещё не
    /// клали») разбираются теми же самыми <see cref="TakeOutAsync"/>,
    /// <see cref="WaitReadyAsync"/> и <see cref="CookAsync"/> — просто в разном
    /// порядке, как это и делает игрок, подошедший к своему костру.
    /// </summary>
    public async Task<CookStep> MakeEdibleAsync(
        Func<string, bool> годится, CancellationToken ct = default)
    {
        var pit = fire.NearestFirepit(FirepitLookRadius);

        // 1. У КОСТРА УЖЕ ГОТОВО. Это может быть и наш прошлый заход, и просто
        // чужой костёр с едой — забрать её значит закрыть нужду прямо сейчас
        if (pit is { } готовый && Ready(готовый) is { Code: { } код } && годится(код))
        {
            int былоГотового = hands.CountOf(код);
            await TakeOutAsync(готовый, OutputSlot, ct);
            return hands.CountOf(код) > былоГотового
                ? new CookStep(true, true, $"забрал из костра {код}")
                : new CookStep(false, false,
                    $"{код} в костре готов, а в сумку не переехал — почему, сказала кухня выше");
        }

        // 2. УЖЕ ЖАРИТСЯ — доводим, а не кладём второй кусок
        if (pit is { } горящий && Cooking_(горящий) is { Code: { } сырое } &&
            CookedOf(сырое) is { } станет && годится(станет))
        {
            var дошло = await WaitReadyAsync(горящий, ct: ct);
            if (дошло == null)
                return new CookStep(false, true,
                    $"{сырое} на огне, за отпущенные {CookTimeoutSeconds:0} с не поспело " +
                    "(ручка «СколькоЖдатьГотовки») — отойду и вернусь");
            int былоГотового = hands.CountOf(станет);
            await TakeOutAsync(горящий, OutputSlot, ct);
            return hands.CountOf(станет) > былоГотового
                ? new CookStep(true, true, $"приготовил {станет}")
                : new CookStep(false, true, $"{станет} поспел, а в сумку не переехал");
        }

        // 3. НИЧЕГО НЕ ЖАРИТСЯ — кладём своё сырьё
        if (RawInBagsFor(годится) is not { } пара)
            return new CookStep(false, false,
                "готовить нечего: в сумках нет ничего, что огонь превратил бы в нужное");

        int было = hands.CountOf(пара.Cooked);
        OnLog?.Invoke($"готовлю {пара.Raw} → {пара.Cooked}");
        var сготовил = await CookAsync(пара.Raw, pit, ct);

        int стало = hands.CountOf(пара.Cooked);
        if (стало > было)
            return new CookStep(true, true, $"приготовил {пара.Cooked} (было {было}, стало {стало})");

        // Не поспело — но если мясо ушло в костёр, дело всё-таки сдвинулось:
        // за ним бот вернётся через секунды, а не через перерыв
        bool вОгне = fire.NearestFirepit(FirepitLookRadius) is { } теперь &&
                     Cooking_(теперь)?.Code is { } что && CookedOf(что) is { } выйдет &&
                     годится(выйдет);
        return new CookStep(false, вОгне, вОгне
            ? $"{пара.Raw} на огне, жду {пара.Cooked}"
            : $"{пара.Raw} готовил, а {пара.Cooked} в сумке не прибавилось " +
              $"(как было {было}): {сготовил.Why}");
    }
}
