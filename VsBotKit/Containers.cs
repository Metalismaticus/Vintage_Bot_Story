namespace VsBotKit;

/// <summary>
/// Хранилище, из которого бот берёт вещи и в которое кладёт: сундук, костёр,
/// разделанная туша.
///
/// ЗАЧЕМ ЭТОТ СЛОЙ. Глазами игрока «достать мясо из костра» и «забрать добычу
/// с туши» — одно и то же действие: подошёл, открыл, взял. А под ним игра
/// прячет ТРИ разных устройства, и в каждом мы уже спотыкались:
///
/// | что            | id инвентаря             | откуда содержимое        | чем открыть            | чем переносить      |
/// |----------------|--------------------------|--------------------------|------------------------|---------------------|
/// | сундук         | «chest-x, y, z»          | блок-сущность            | клик по блоку + пакеты | MoveItemstack(8)    |
/// | костёр         | «smelting-x/y/z»         | блок-сущность            | клик по блоку + пакеты | MoveItemstack(8)    |
/// | туша           | «harvestableContents-id» | WatchedAttributes зверя  | клик по существу       | внутри EntityPacket |
///
/// Обратите внимание: у сундука разделитель «, », у костра «/». Это не наша
/// вольность, а два разных места в коде игры — и на этом бот уже молча терял
/// переносы. Поэтому различия собраны здесь, в одной таблице, а всё, что выше
/// (взять всё, взять одно, сложить), написано ОДИН раз.
/// </summary>
public abstract class Container
{
    protected readonly BotContext Ctx;
    protected readonly Hands Hands;

    protected Container(BotContext ctx, Hands hands)
    {
        Ctx = ctx;
        Hands = hands;
        Answering = () => ctx.Bot.Joined;
    }

    /// <summary>Id инвентаря для переноса предметов.</summary>
    public abstract string InventoryId { get; }

    /// <summary>Как назвать это в журнале.</summary>
    public abstract string Name { get; }

    /// <summary>Что внутри (null — содержимое не видно).</summary>
    public abstract SlotContent[]? Contents();

    /// <summary>
    /// ОПИСЬ, КОТОРУЮ СЕРВЕР ПРИСЛАЛ НАМ ЛИЧНО В ОТВЕТ НА ОТКРЫТИЕ. null —
    /// ответа ещё (или вовсе) нет.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="Contents"/>. У блока-хранилища есть ВТОРОЙ
    /// источник — данные чанка, и он ЗАМОРОЖЕН: <c>WorldModel</c> кладёт
    /// блок-сущность в память один раз, когда приходит чанк, и больше её не
    /// трогает — ни на перенос, ни на чужие руки. Живой случай, ради которого
    /// это разделено: бот сам сложил в сундук 1007 предметов, через час пришёл
    /// за ножом — и доложил «пусто» про 15 сундуков и «забрано стопок 0» про
    /// шестнадцатый. Он читал не сундук, а слепок часовой давности.
    ///
    /// Правило: РЕШАТЬ, ЧТО ВЫНИМАТЬ, МОЖНО ТОЛЬКО ПО ЭТОЙ ОПИСИ. Данные чанка
    /// годятся на вопрос «идти ли туда», и никогда — на «что там сейчас».
    /// </summary>
    public abstract SlotContent[]? LiveContents();

    /// <summary>Подойти на длину руки.</summary>
    public abstract Task<bool> ReachAsync(CancellationToken ct);

    /// <summary>Открыть так, чтобы сервер разрешил перенос.</summary>
    public abstract Task OpenAsync(CancellationToken ct);

    /// <summary>Закрыть за собой.</summary>
    public abstract Task CloseAsync(CancellationToken ct);

    /// <summary>Перенести из этого хранилища в свой инвентарь.</summary>
    public abstract Task MoveOutAsync(int fromSlot, string toInventory, int toSlot, int count);

    /// <summary>Перенести из своего инвентаря сюда.</summary>
    public abstract Task MoveInAsync(string fromInventory, int fromSlot, int toSlot, int count);

    /// <summary>
    /// Сколько ждать после открытия, прежде чем переносить. ОСТАЛОСЬ ДЛЯ ТЕХ,
    /// КТО ОТКРЫВАЕТ САМ (готовка ставит и вынимает из костра своими руками).
    /// Внутри самого хранилища этим числом больше ничего не меряется — см.
    /// <see cref="OpenWaitMs"/> и почему глухой секундомер здесь врал.
    /// </summary>
    public virtual int OpenDelayMs => 600;

    /// <summary>Сколько ждать подтверждения переноса — для тех же внешних рук.</summary>
    public virtual int MoveDelayMs => 400;

    /// <summary>
    /// СКОЛЬКО ЖДАТЬ ОТВЕТА СЕРВЕРА НА ОТКРЫТИЕ, мс.
    ///
    /// Раньше здесь стоял глухой секундомер на 600 мс: «поспал — значит,
    /// содержимое пришло». Живой журнал показал, чем это кончается, — шестнадцать
    /// сундуков подряд за восемь секунд, и все «пусто»: бот ни разу не дождался
    /// описи и каждый раз читал вместо неё слепок из чанка. Ждём ФАКТА, а не
    /// времени: пришла опись — идём дальше сразу (обычно это меньше прежних
    /// 600 мс), не пришла за весь срок — так и говорим.
    /// </summary>
    public virtual int OpenWaitMs { get; set; } = 2500;

    /// <summary>
    /// Сколько ждать подтверждения ОДНОГО переноса, мс. Тоже по факту: ждём,
    /// пока изменится сумка, опись хранилища или курсор.
    /// </summary>
    public virtual int MoveWaitMs { get; set; } = 1500;

    /// <summary>Как часто переспрашивать, пока ждём ответа, мс.</summary>
    public int PollMs { get; set; } = 25;

    // ---------------- готовые действия, общие для всех ----------------

    public event Action<string>? OnLog;

    protected void Log(string message) => OnLog?.Invoke(message);

    /// <summary>
    /// ЕСТЬ ЛИ КОМУ ОТВЕЧАТЬ. Ждать ответа имеет смысл, только пока бот в мире:
    /// без соединения <see cref="BotClient.SendPacketAsync"/> честно пишет «нет
    /// соединения — действие не ушло на сервер» и НИЧЕГО не отправляет. Стоять
    /// после этого по две с половиной секунды у каждого из шестнадцати сундуков
    /// — сорок секунд ожидания от того, кому мы даже не написали.
    ///
    /// Отдельным свойством, а не прямым вопросом к клиенту, — ради стенда:
    /// подставной сервер отвечает пакетами, не открывая сокета, и «нет
    /// соединения» для него неправда.
    /// </summary>
    public Func<bool> Answering { get; set; }

    /// <summary>
    /// Ждать ответа, пока есть кому отвечать (см. <see cref="Answering"/>).
    ///
    /// ОДИН ОПРОС ДЕЛАЕТСЯ ВСЕГДА, даже когда отвечать некому: мы только что
    /// отправили пакет, и не дать ответу дойти хотя бы за один опрос — значит
    /// мерить не сервер, а скорость собственного кода. Заодно это единственное
    /// место, где долгое дело уступает поток: без него весь рейс на склад
    /// проскакивает внутри одного тика планировщика, и, пока бот «идёт»,
    /// не тикают ни еда, ни лечение, ни бой.
    /// </summary>
    protected async Task<bool> WaitAsync(Func<bool> until, int ms, CancellationToken ct)
    {
        if (until())
            return true;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(0, ms));
        do
        {
            try { await Task.Delay(PollMs, ct); }
            catch (OperationCanceledException) { break; }
            if (until())
                return true;
        }
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && Answering());
        return until();
    }

    /// <summary>
    /// Сколько ЭТОГО добра лежит внутри. Единственная честная мерка успеха
    /// при складывании: сумка пустеет и от переноса, и от того, что стопка
    /// повисла на курсоре, а сундук наполняется только по-настоящему.
    /// </summary>
    public static int CountInside(SlotContent[] slots, string code) =>
        slots.Where(s => !s.IsEmpty && s.Code == code).Sum(s => s.Count);

    /// <summary>
    /// Сколько мест внутри свободно. Нужно ОТКАЗУ: «складывать некуда» без
    /// числа осмотренных сундуков и мест в них человек проверить не может —
    /// он не знает, чинить ему склад или бота.
    /// </summary>
    public static int FreeSlotsInside(SlotContent[] slots) => slots.Count(s => s.IsEmpty);

    /// <summary>Опись пришла, и в ней нет ни одной вещи.</summary>
    public static bool IsEmptyInside(SlotContent[] slots) => slots.All(s => s.IsEmpty);

    // ---------------- заглянуть внутрь ----------------

    /// <summary>Чем кончилась попытка ЗАГЛЯНУТЬ ВНУТРЬ.</summary>
    public enum PeekOutcome
    {
        /// <summary>Сервер прислал опись — вот она. Пустая опись это тоже ответ.</summary>
        Seen,
        /// <summary>Не дотянулись: открывать было нечего и нечем.</summary>
        NotReached,
        /// <summary>
        /// Открыли, а описи так и не дождались. ЭТО НЕ «ПУСТО». Разница та же,
        /// что между «не знаю» и «там воздух»: в первом случае чинят связь и
        /// открытие, во втором несут вещь в сундук.
        /// </summary>
        NoAnswer
    }

    /// <summary>Что бот увидел, открыв хранилище. Slots не null только при <see cref="PeekOutcome.Seen"/>.</summary>
    public sealed record Peek(PeekOutcome What, SlotContent[]? Slots, string Reason)
    {
        /// <summary>Опись пришла и она пуста — проверенная пустота, а не незнание.</summary>
        public bool Empty => Slots is { } s && IsEmptyInside(s);

        public override string ToString() => Reason;
    }

    /// <summary>
    /// ЧЕМ КОНЧИЛСЯ ВЗГЛЯД ВНУТРЬ — чистое правило, без сервера и без ожидания.
    ///
    /// Три ответа, и путать их нельзя: «не дотянулся», «не дождался описи» и
    /// «вот опись». Живой случай: бот доложил «пусто» про пятнадцать сундуков
    /// подряд — на деле он ни в один не заглянул, а взял замороженный слепок
    /// из чанка. Человек по такому журналу чинит склад вместо связи.
    /// </summary>
    public static PeekOutcome JudgePeek(bool reached, SlotContent[]? live) =>
        !reached ? PeekOutcome.NotReached
        : live is null ? PeekOutcome.NoAnswer
        : PeekOutcome.Seen;

    /// <summary>
    /// ПОДОЙТИ, ОТКРЫТЬ И ДОЖДАТЬСЯ ОПИСИ — одно место на всех, кто собрался
    /// что-то вынимать или класть.
    ///
    /// Порядок как у человека: сперва дотянуться, потом открыть, и только потом
    /// смотреть. Смотрим при этом ТОЛЬКО в <see cref="LiveContents"/> — то, что
    /// сервер прислал в ответ на это самое открытие.
    /// </summary>
    public async Task<Peek> OpenAndPeekAsync(CancellationToken ct = default)
    {
        if (!await ReachAsync(ct))
            return new Peek(PeekOutcome.NotReached, null,
                $"{Name}: не дотянуться — что внутри, не знаю");

        await OpenAsync(ct);
        await WaitAsync(() => LiveContents() != null, OpenWaitMs, ct);

        if (LiveContents() is not { } live || JudgePeek(true, live) != PeekOutcome.Seen)
            return new Peek(PeekOutcome.NoAnswer, null,
                $"{Name}: открыл, но опись из него так и не пришла за {OpenWaitMs} мс" +
                (Answering()
                    ? " — что внутри, НЕ ЗНАЮ (это не «пусто»)"
                    : " — я не в мире, отвечать некому"));

        return new Peek(PeekOutcome.Seen, live,
            IsEmptyInside(live)
                ? $"{Name}: пусто — сервер прислал опись, все {live.Length} мест свободны"
                : $"{Name}: внутри стопок {live.Count(s => !s.IsEmpty)} из {live.Length} мест");
    }

    /// <summary>
    /// Куда класть: сперва к такой же стопке, где ещё есть место, и только
    /// потом в пустой слот. −1 — места нет вовсе.
    ///
    /// Живой игрок складывает именно так, и разница не косметическая: в
    /// сундуке, где все двадцать слотов заняты початыми стопками руды, место
    /// ЕСТЬ, а бот, искавший только пустые слоты, отвечал «места больше нет» и
    /// уносил груз обратно в забой.
    ///
    /// Правило нарочно совпадает с обратным ходом (SelfState.FreeSlotFor):
    /// «влезает целиком или не влезает». Половинчатые переносы считать нечем —
    /// сервер их подтверждает одним и тем же пакетом.
    /// </summary>
    public static int TargetSlotInside(SlotContent[] slots, string code, int amount, int maxStack)
    {
        int empty = -1;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].IsEmpty)
            {
                if (empty < 0)
                    empty = i;
                continue;
            }
            if (string.Equals(slots[i].Code, code, StringComparison.Ordinal) &&
                slots[i].Count + amount <= Math.Max(1, maxStack))
                return i;
        }
        return empty;
    }

    /// <summary>Чем кончилась ОДНА перекладка стопки.</summary>
    public enum PutOutcome
    {
        /// <summary>Легло: содержимого хранилища прибавилось.</summary>
        Landed,
        /// <summary>Из сумки ушло, в хранилище не появилось — висит на курсоре.</summary>
        Cursor,
        /// <summary>Не двинулось никуда: сервер перенос не принял.</summary>
        Refused
    }

    /// <summary>
    /// ЧЕМ КОНЧИЛАСЬ ПЕРЕКЛАДКА — сразу по двум меркам, и порядок между ними
    /// решающий.
    ///
    /// Живой случай: склад доложил «легло 112 шт.», а сундук был ПУСТ. Успех
    /// мерили убылью своей сумки — а перенос в игре идёт ЧЕРЕЗ КУРСОР: стопка
    /// сперва поднимается в слот мыши и только потом кладётся. Из рюкзака она
    /// при этом уже ушла, и «сумка опустела» значило лишь «стопка где-то в
    /// пути», а вовсе не «лежит в сундуке».
    ///
    /// Поэтому первой спрашивается мерка человека — прибавилось ли В
    /// ХРАНИЛИЩЕ. Убыль сумки годится только на то, чтобы отличить застрявшую
    /// на курсоре стопку от переноса, которого сервер не принял вовсе.
    /// </summary>
    public static PutOutcome JudgePut(int insideBefore, int insideAfter,
        int bagBefore, int bagAfter) =>
        insideAfter > insideBefore ? PutOutcome.Landed
        : bagAfter < bagBefore ? PutOutcome.Cursor
        : PutOutcome.Refused;

    // ---------------- слот под курсором ----------------

    /// <summary>
    /// Инвентарь под курсором («mouse-&lt;uid&gt;») — один слот, в котором у
    /// живого игрока висит поднятая мышью стопка.
    /// </summary>
    public static bool IsCursorInventory(string inventoryId) =>
        inventoryId.StartsWith("mouse", StringComparison.Ordinal);

    /// <summary>Id инвентаря под курсором (null — сервер про него не рассказывал).</summary>
    public string? CursorInventoryId =>
        Ctx.Self.OwnInventories.FirstOrDefault(kv => IsCursorInventory(kv.Key)).Key;

    /// <summary>
    /// Что висит на курсоре (null — пусто).
    ///
    /// Спрашивать это приходится отдельно потому, что счёт вещей слот мыши НЕ
    /// ВИДИТ вовсе (Hands.CountOf пропускает «character» и «mouse»): для него
    /// стопка на курсоре — уже не наша вещь. Отсюда и «легло 112 шт.» при
    /// пустом сундуке.
    /// </summary>
    public SlotContent? OnCursor()
    {
        if (CursorInventoryId is not { } id ||
            !Ctx.Self.Inventories.TryGetValue(id, out var slots) ||
            slots.Length == 0 || slots[0].IsEmpty)
            return null;
        return slots[0];
    }

    /// <summary>
    /// СНЯТЬ СТОПКУ С КУРСОРА. true — курсор пуст (в том числе если он и был пуст).
    ///
    /// Без этого одна застрявшая стопка ломает всё дальнейшее: следующий
    /// перенос кладёт на курсор вторую поверх первой, и груз пропадает молча.
    /// Живой игрок в этом месте просто щёлкает по свободному слоту — ровно это
    /// здесь и делается, никакого волшебства.
    /// </summary>
    /// <param name="toBagFirst">
    /// Куда пробовать ПЕРВЫМ делом. Складывая — в хранилище (стопку затем и
    /// поднимали, чтобы её туда донести); ВЫНИМАЯ — к себе в сумку, иначе бот
    /// честно вернёт в сундук ровно то, за чем пришёл, и уйдёт ни с чем.
    /// </param>
    public async Task<bool> ClearCursorAsync(CancellationToken ct = default,
        bool toBagFirst = false)
    {
        if (OnCursor() is not { Code: { } code } hanging || CursorInventoryId is not { } cursor)
            return true;

        bool intoBag = await TryDropCursorAsync(cursor, code, hanging.Count, toBag: toBagFirst, ct);
        if (intoBag)
            return true;
        if (await TryDropCursorAsync(cursor, code, hanging.Count, toBag: !toBagFirst, ct))
            return true;

        Log($"{Name}: на курсоре застряло {code} ×{hanging.Count} — снять некуда " +
            "(и в хранилище, и в сумке места нет)");
        return false;
    }

    /// <summary>Одна попытка снять стопку с курсора — в сумку или в хранилище.</summary>
    private async Task<bool> TryDropCursorAsync(string cursor, string code, int count,
        bool toBag, CancellationToken ct)
    {
        string where;
        int slot;
        if (toBag)
        {
            if (Ctx.Self.FreeSlotFor(code, count) is not { } mine)
                return false;
            (where, slot) = (mine.InventoryId, mine.Slot);
        }
        else
        {
            // В хранилище — только по ЖИВОЙ описи: класть по слепку из чанка
            // значит целить в слот, который давно занят
            slot = LiveContents() is { } inside
                ? TargetSlotInside(inside, code, count, Ctx.World.MaxStackSize(code))
                : -1;
            if (slot < 0)
                return false;
            where = InventoryId;
        }

        await Ctx.Actions.MoveItemAsync(cursor, 0, where, slot, count);
        await WaitAsync(() => OnCursor() == null, MoveWaitMs, ct);
        if (OnCursor() != null)
            return false;

        Log($"{Name}: снял с курсора {code} ×{count} " + (toBag ? "в сумку" : "в хранилище"));
        return true;
    }

    /// <summary>Содержимое одного слота (null — пусто или слота нет).</summary>
    public SlotContent? SlotAt(int slot)
    {
        var all = Contents();
        if (all == null || slot < 0 || slot >= all.Length)
            return null;
        return all[slot].IsEmpty ? null : all[slot];
    }

    /// <summary>
    /// Забрать один слот себе. Успех — ТОЛЬКО по тому, что вещь ОКАЗАЛАСЬ У
    /// БОТА; отправленный пакет и опустевший слот ещё ничего не значат.
    /// </summary>
    public async Task<bool> TakeSlotAsync(int slot, CancellationToken ct = default)
    {
        var look = await OpenAndPeekAsync(ct);
        if (look.Slots is not { } inside)
        {
            Log(look.Reason);
            if (look.What != PeekOutcome.NotReached)
                await CloseAsync(ct);
            return false;
        }

        if (slot < 0 || slot >= inside.Length || inside[slot].Code is not { } code)
        {
            Log($"{Name}: в слоте {slot} пусто");
            await CloseAsync(ct);
            return false;
        }

        bool got = await TakeOneAsync(slot, code, inside[slot].Count, ct) == TakeOutcome.Taken;
        await CloseAsync(ct);
        return got;
    }

    /// <summary>Чем кончилось ОДНО вынимание стопки.</summary>
    public enum TakeOutcome
    {
        /// <summary>Вещь у бота: в сумках её прибавилось.</summary>
        Taken,
        /// <summary>Из хранилища ушла, в сумке не появилась — висит на курсоре.</summary>
        Cursor,
        /// <summary>Не двинулось никуда: сервер перенос не принял.</summary>
        Refused
    }

    /// <summary>
    /// ЧЕМ КОНЧИЛОСЬ ВЫНИМАНИЕ — зеркало <see cref="JudgePut"/>, и порядок мерок
    /// такой же решающий.
    ///
    /// Складывая, мы обожглись на том, что успех мерили УБЫЛЬЮ СУМКИ: «легло
    /// 112 шт.» при пустом сундуке. Вынимая, бот мерил ровно тем же способом
    /// наоборот — «слот в сундуке опустел, значит забрал». А перенос идёт ЧЕРЕЗ
    /// КУРСОР: слот пустеет в тот же миг, когда стопка поднимается в слот мыши,
    /// и до сумки может не дойти вовсе. Счёт вещей курсора не видит, и для бота
    /// добыча просто исчезала — с бодрым «забрал» в журнале.
    ///
    /// Поэтому первой спрашивается мерка человека: ПРИБАВИЛОСЬ ЛИ У МЕНЯ.
    /// Убыль хранилища годится только на то, чтобы отличить застрявшую на
    /// курсоре стопку от переноса, которого сервер не принял вовсе.
    /// </summary>
    public static TakeOutcome JudgeTake(int bagBefore, int bagAfter,
        int insideBefore, int insideAfter) =>
        bagAfter > bagBefore ? TakeOutcome.Taken
        : insideAfter < insideBefore ? TakeOutcome.Cursor
        : TakeOutcome.Refused;

    /// <summary>
    /// Одна стопка из хранилища — себе. Ждём ФАКТА (сумка, опись или курсор
    /// изменились), а не секунд, и снимаем стопку с курсора, если она застряла
    /// на нём: иначе следующий перенос кладёт вторую поверх первой.
    /// </summary>
    private async Task<TakeOutcome> TakeOneAsync(int slot, string code, int count,
        CancellationToken ct)
    {
        if (Ctx.Self.FreeSlotFor(code, count) is not { } where)
        {
            Log($"{Name}: некуда класть {code} — инвентарь полон");
            return TakeOutcome.Refused;
        }

        int bagBefore = InBag(code);
        int insideBefore = CountInside(LiveContents() ?? [], code);

        // ЧТО ИМЕННО БОТ ПОПРОБОВАЛ — в журнал ДО переноса. В живом журнале
        // заказчика от всего захода осталось одно «забрано стопок 0»: по нему
        // нельзя было понять даже, дошло ли дело до пакета
        Log($"{Name}: беру {code} ×{count} (слот {slot}) " +
            $"→ {where.InventoryId}[{where.Slot}]");
        await MoveOutAsync(slot, where.InventoryId, where.Slot, count);
        await WaitAsync(() => InBag(code) != bagBefore ||
                              CountInside(LiveContents() ?? [], code) != insideBefore ||
                              OnCursor() != null, MoveWaitMs, ct);

        var verdict = JudgeTake(bagBefore, InBag(code),
            insideBefore, CountInside(LiveContents() ?? [], code));

        if (verdict == TakeOutcome.Cursor)
        {
            Log($"{Name}: {code} ушёл из хранилища, но в сумке не появился — " +
                "стопка повисла на курсоре, снимаю себе");
            await ClearCursorAsync(ct, toBagFirst: true);
            verdict = JudgeTake(bagBefore, InBag(code),
                insideBefore, CountInside(LiveContents() ?? [], code));
        }

        Log(verdict switch
        {
            TakeOutcome.Taken => $"{Name}: забрал {code} ×{InBag(code) - bagBefore}",
            TakeOutcome.Cursor => $"{Name}: {code} застрял на курсоре — до сумки не дошёл",
            _ => $"{Name}: сервер не отдал {code} ×{count} — " +
                 "ни в сумке, ни в хранилище ничего не изменилось"
        });
        return verdict;
    }

    /// <summary>
    /// Забрать всё, что подходит под условие (без условия — всё подряд).
    /// Возвращает, сколько стопок реально переехало.
    /// </summary>
    public async Task<int> TakeAllAsync(Func<string, bool>? wanted = null,
        CancellationToken ct = default) =>
        (await TakeAllReportAsync(wanted, ct)).Stacks;

    /// <summary>Чем кончилось вынимание из ОДНОГО хранилища.</summary>
    /// <param name="Saw">увидели ли опись — и если нет, то почему</param>
    /// <param name="Stacks">сколько стопок реально переехало К БОТУ</param>
    /// <param name="Items">на сколько штук при этом прибавилось в сумках</param>
    /// <param name="Reason">чем кончилось — теми же словами, что и в журнале</param>
    public sealed record TakeReport(PeekOutcome Saw, int Stacks, int Items, string Reason)
    {
        /// <summary>
        /// Опись пришла. ТОЛЬКО ПОСЛЕ ЭТОГО «там этого нет» — ответ, а не
        /// догадка: см. живой случай «на складе Knife нет — искал в 16
        /// сундук(ах)» при ноже, лежавшем в первом же из них.
        /// </summary>
        public bool Read => Saw == PeekOutcome.Seen;

        public override string ToString() => Reason;
    }

    /// <summary>
    /// То же, но с отчётом: что увидели, сколько взяли и чем кончилось.
    /// Складу это нужно, чтобы отличить «этого там нет» от «я не прочитал».
    /// </summary>
    public async Task<TakeReport> TakeAllReportAsync(Func<string, bool>? wanted = null,
        CancellationToken ct = default)
    {
        // ПОДОЙТИ, ОТКРЫТЬ, ДОЖДАТЬСЯ ОПИСИ — и только потом смотреть. Раньше
        // здесь стоял глухой сон на 600 мс, после которого бот читал ЧТО
        // ПОПАЛО: не пришла опись — за неё сходил замороженный слепок из чанка.
        // Он пишется один раз, когда приходит чанк, и не меняется ни от наших
        // переносов, ни от чужих рук. Отсюда и «пусто» про сундук, в который
        // сам же бот час назад сложил 1007 предметов
        var look = await OpenAndPeekAsync(ct);
        if (look.Slots is not { } slots)
        {
            Log(look.Reason);
            if (look.What != PeekOutcome.NotReached)
                await CloseAsync(ct);
            return new TakeReport(look.What, 0, 0, look.Reason);
        }
        if (IsEmptyInside(slots))
        {
            await CloseAsync(ct);
            Log(look.Reason);
            return new TakeReport(PeekOutcome.Seen, 0, 0, look.Reason);
        }

        // СНАЧАЛА ОСВОБОДИТЬ КУРСОР — как и при складывании. Стопка могла
        // зависнуть на нём в прошлый раз; вынуть на него вторую поверх первой
        // значит потерять обе
        if (OnCursor() is { } hanging && !await ClearCursorAsync(ct, toBagFirst: true))
        {
            await CloseAsync(ct);
            string stuck = $"{Name}: на курсоре висит {hanging.Code} ×{hanging.Count}, " +
                           "снять некуда — не вынимаю, чтобы не потерять груз";
            Log(stuck);
            return new TakeReport(PeekOutcome.Seen, 0, 0, stuck);
        }

        int stacks = 0, items = 0, refusals = 0;
        // Почему перестали вынимать ДОСРОЧНО. null — прошли всю опись до конца
        string? stopped = null;

        for (int i = 0; i < slots.Length; i++)
        {
            if (ct.IsCancellationRequested)
            {
                stopped = $"{Name}: вынимание отменено";
                break;
            }

            // Опись перечитываем КАЖДЫЙ раз: снимок взят до первого переноса и
            // после него уже врёт — сервер шлёт поправки слотов (31/32)
            var fresh = LiveContents() ?? slots;
            if (i >= fresh.Length || fresh[i].Code is not { } code)
                continue;
            if (wanted != null && !wanted(code))
                continue;

            int count = fresh[i].Count;
            int had = InBag(code);
            var verdict = await TakeOneAsync(i, code, count, ct);

            if (verdict == TakeOutcome.Taken)
            {
                refusals = 0;
                stacks++;
                items += InBag(code) - had;
                continue;
            }
            if (verdict == TakeOutcome.Cursor)
            {
                stopped = $"{Name}: {code} застрял на курсоре — дальше не вынимаю, " +
                          "иначе поверх него ляжет следующая стопка";
                break;
            }
            // Отказ бывает честным (в сумке нет места ровно под этот вид), но
            // три подряд означают, что не отдают ВООБЩЕ
            if (++refusals >= RefusalsBeforeGivingUp)
            {
                stopped = $"{Name}: сервер не отдаёт содержимое ({refusals} отказа подряд) — " +
                          "дальше не пробую";
                break;
            }
        }

        await CloseAsync(ct);

        // ИТОГ НЕ ИМЕЕТ ПРАВА БЫТЬ ГЛУШЕ ДЕЛА. Прежнее «забрано стопок 0»
        // одинаково звучало и когда внутри нет искомого, и когда сервер не
        // отдал ни одной стопки, — по такой строке чинить нечего
        string reason = stopped
            ?? (refusals > 0
                ? $"{Name}: забрал стопок {stacks} ({items} шт.), " +
                  $"но {refusals} переносов сервер не принял"
                : stacks > 0
                    ? $"{Name}: забрал стопок {stacks} ({items} шт.)"
                    : $"{Name}: подходящего внутри нет — опись прочитана, " +
                      $"занятых мест в ней {slots.Count(s => !s.IsEmpty)}");
        Log(reason);
        return new TakeReport(PeekOutcome.Seen, stacks, items, reason);
    }

    /// <summary>Чем кончилась укладка в ОДНО хранилище.</summary>
    /// <param name="Stored">сколько предметов реально легло — по содержимому хранилища</param>
    /// <param name="FreeSlots">
    /// сколько мест в нём осталось; null — содержимого так и не увидели
    /// (не открылось, не дотянулись). Это число обязано попадать в отказ
    /// «складывать некуда»: без него человек не знает, тесно в сундуках или
    /// сломан перенос.
    /// </param>
    /// <param name="Reason">чем кончилось — теми же словами, что и в журнале</param>
    public sealed record PutReport(int Stored, int? FreeSlots, string Reason)
    {
        public override string ToString() => Reason;
    }

    /// <summary>
    /// Сколько подряд не принятых сервером переносов терпим, прежде чем
    /// перестать долбиться. Один отказ бывает честным (в ящик-крейт не лезет
    /// второй вид товара), но три подряд означают, что не принимают ВООБЩЕ, —
    /// и дальше мы только тратим по секунде на стопку.
    /// </summary>
    public int RefusalsBeforeGivingUp { get; set; } = 3;

    /// <summary>
    /// Сложить сюда всё своё, что подходит под условие. Возвращает число
    /// предметов, реально легших В ХРАНИЛИЩЕ (см. <see cref="JudgePut"/>).
    /// </summary>
    public async Task<int> PutAllAsync(Func<string, bool> wanted, CancellationToken ct = default) =>
        (await PutAllReportAsync(wanted, ct)).Stored;

    /// <summary>
    /// То же, но с отчётом: сколько легло, сколько мест осталось и чем
    /// кончилось. Складу это нужно, чтобы отказ «складывать некуда» называл
    /// числа, а не одно слово.
    ///
    /// «ДА» ЗДЕСЬ ЗНАЧИТ «ВСЮ СТОПКУ ЦЕЛИКОМ» — и это законный ответ для того,
    /// кто про количество не спрашивает вовсе: приказ «отнеси вот это»,
    /// выгрузка поручения. Кому нужно сдать ИЗЛИШЕК, а не всё, спрашивает
    /// соседний вход числом (<see cref="PutAllReportAsync(Func{string,int},CancellationToken)"/>).
    /// </summary>
    public Task<PutReport> PutAllReportAsync(Func<string, bool> wanted,
        CancellationToken ct = default) =>
        PutAllReportAsync(code => wanted(code) ? int.MaxValue : 0, ct);

    /// <summary>
    /// СЛОЖИТЬ СЮДА СТОЛЬКО, СКОЛЬКО СКАЗАНО, — а не всю стопку.
    ///
    /// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ. Укладка спрашивала про КОД («это твоё?»), и другого
    /// ответа, кроме «всё» и «ничего», у неё не было. Из-за этого склад не мог
    /// сдать ИЗЛИШЕК: у бота с двумя сотнями стрел при требовании в 64 либо
    /// уезжали все двести, либо не уезжало ни одной, — и рейс честно отвечал «в
    /// сумке только своё».
    ///
    /// СКОЛЬКО ЕЩЁ МОЖНО — СПРАШИВАЕМ НА КАЖДОЙ СТОПКЕ ЗАНОВО. Мерка склада
    /// считает по живой сумке (<see cref="Depot.Cargo"/>), и уже сданное она
    /// вычитает сама. Своего счётчика «осталось сдать» здесь поэтому нет: он
    /// разошёлся бы с сумкой на первом же непринятом переносе, и бот вынес бы
    /// из леса последние стрелы, считая, что сдал излишек.
    /// </summary>
    /// <param name="howMany">Код → сколько штук этого ещё можно сюда положить.</param>
    public async Task<PutReport> PutAllReportAsync(Func<string, int> howMany,
        CancellationToken ct = default)
    {
        var look = await OpenAndPeekAsync(ct);
        if (look.What == PeekOutcome.NotReached)
            return new PutReport(0, null, $"{Name}: не дотянуться — ничего не сложил");

        // ОПИСИ НЕТ — КЛАДЁМ ВСЛЕПУЮ И ГОВОРИМ ОБ ЭТОМ. Данные чанка тут ещё
        // годятся: успех всё равно меряется ПРИБАВКОЙ внутри (см. JudgePut), и
        // по замороженному слепку она никогда не покажется — выйдет честный
        // отказ, а не бодрое враньё. Но целиться в слот по такому слепку —
        // значит целиться в место, которое давно занято, и об этом человек
        // обязан прочитать в журнале
        var inside = look.Slots;
        if (inside == null)
        {
            Log(look.Reason);
            inside = Contents();
            if (inside == null)
            {
                string closed = $"{Name}: не открылся (нет {InventoryId})";
                Log(closed);
                await CloseAsync(ct);
                return new PutReport(0, null, closed);
            }
            Log($"{Name}: складываю по данным чанка — они могли устареть, " +
                "успех всё равно считаю по прибавке внутри");
        }

        // СНАЧАЛА ОСВОБОДИТЬ КУРСОР. Стопка могла зависнуть на нём в прошлый
        // раз; положить поверх неё вторую — значит потерять обе
        if (OnCursor() is { } hanging && !await ClearCursorAsync(ct))
        {
            await CloseAsync(ct);
            string stuck = $"{Name}: на курсоре висит {hanging.Code} ×{hanging.Count}, " +
                           "снять некуда — не складываю, чтобы не потерять груз";
            Log(stuck);
            return new PutReport(0, FreeSlotsInside(inside), stuck);
        }
        if (Contents() is { } afterCursor)
            inside = afterCursor;

        int stored = 0, refusals = 0;
        // Почему перестали складывать ДОСРОЧНО. null — прошли всю сумку до
        // конца; итог тогда собирается ниже, и молчаливого «всё сложил» при
        // не принятых переносах уже не выйдет
        string? stopped = null;

        foreach (var (myInv, mySlot, _) in Ctx.Self.CarrySlots().ToList())
        {
            if (ct.IsCancellationRequested)
            {
                stopped = $"{Name}: складывание отменено";
                break;
            }
            // Содержимое своего слота перечитываем КАЖДЫЙ раз: снимок сумки
            // взят до первого переноса и после него уже врёт
            if (Carried(myInv, mySlot) is not { Code: { } code } mine)
                continue;
            // СКОЛЬКО ИЗ ЭТОЙ СТОПКИ ВЕЛЕНО СДАТЬ. Ноль — вещь остаётся при
            // боте: либо она не груз вовсе, либо излишек этого рода уже сдан
            int allowed = howMany(code);
            if (allowed <= 0)
                continue;
            int сколько = Math.Min(mine.Count, allowed);

            int target = TargetSlotInside(inside, code, сколько, Ctx.World.MaxStackSize(code));
            if (target < 0)
            {
                stopped = $"{Name}: мест не осталось — {code} ×{сколько} несу обратно";
                Log(stopped);
                break;
            }

            int insideBefore = CountInside(inside, code);
            int bagBefore = InBag(code);

            await MoveInAsync(myInv, mySlot, target, сколько);
            // Ждём ФАКТА, а не секунд: изменилась опись, сумка или курсор.
            // Пришёл ответ через 80 мс — идём дальше, не дожидаясь остатка
            await WaitAsync(() => CountInside(LiveContents() ?? inside, code) != insideBefore ||
                                  InBag(code) != bagBefore ||
                                  OnCursor() != null, MoveWaitMs, ct);
            if (LiveContents() is { } fresh)
                inside = fresh;

            switch (JudgePut(insideBefore, CountInside(inside, code), bagBefore, InBag(code)))
            {
                case PutOutcome.Landed:
                    refusals = 0;
                    int moved = CountInside(inside, code) - insideBefore;
                    stored += moved;
                    Log($"{Name}: сложил {code} ×{moved}");
                    // ЗАЧЁТА ПЕРЕНОСА ЗДЕСЬ БОЛЬШЕ НЕТ, И ЭТО НЕ ПОТЕРЯ.
                    // Запас по расходу (Upkeep) слушает ОДНУ дверь —
                    // Actions.OnItemMoved, — а она под этой укладкой и лежит.
                    // Стояло здесь: «это единственная дверь, через которую вещи
                    // уходят из сумки в хранилище». Приёмка показала, что
                    // дверей семь: «!брось», перекладка в чужой сундук, товар
                    // на прилавок, закладка в костёр, материал в сетку крафта.
                    // Каждая незакрытая читалась как расход
                    continue;

                case PutOutcome.Cursor:
                    // Из сумки ушло, в хранилище не появилось. Молчать нельзя:
                    // со стороны это выглядит как пропажа груза
                    Log($"{Name}: {code} ушёл из сумки, но внутри не появился — " +
                        "стопка повисла на курсоре, снимаю");
                    bool freed = await ClearCursorAsync(ct);
                    if (Contents() is { } after)
                        inside = after;
                    int rescued = CountInside(inside, code) - insideBefore;
                    if (rescued > 0)
                    {
                        stored += rescued;
                        refusals = 0;
                        Log($"{Name}: {code} ×{rescued} лёг со второго раза (через курсор)");
                        continue;
                    }
                    stopped = freed
                        ? $"{Name}: {code} через курсор внутрь не идёт — вернул в сумку и больше не пробую"
                        : $"{Name}: {code} застрял на курсоре, снять некуда — дальше не складываю";
                    Log(stopped);
                    break;

                default:
                    refusals++;
                    Log($"{Name}: сервер не принял перенос {code} ×{сколько} — " +
                        "ни внутри, ни в сумке ничего не изменилось");
                    if (refusals < RefusalsBeforeGivingUp)
                        continue;
                    stopped = $"{Name}: сервер не принимает переносы ({refusals} подряд) — " +
                              "дальше не пробую";
                    Log(stopped);
                    break;
            }
            break;
        }

        await CloseAsync(ct);

        // ИТОГ НЕ ИМЕЕТ ПРАВА БЫТЬ БОДРЕЕ ДЕЛА. Пока здесь стояло «сложил всё,
        // что нёс», два не принятых сервером переноса подряд (до порога терпения
        // не дотянули) уходили наружу как удачный заход — ровно тот вид вранья,
        // с которого начался весь разбор
        string reason = stopped
            ?? (refusals > 0
                ? $"{Name}: прошёл сумку до конца, но {refusals} переносов сервер не принял"
                : stored > 0
                    ? $"{Name}: сложил всё, что нёс, — {stored} шт."
                    : $"{Name}: складывать было нечего");
        return new PutReport(stored, FreeSlotsInside(inside), reason);
    }

    /// <summary>
    /// Сколько этого добра лежит В СУМКАХ (хотбар и рюкзак). Считаем по точному
    /// коду и без слота мыши — иначе стопка на курсоре считалась бы «своей», а
    /// весь смысл проверки в том, чтобы её заметить.
    /// </summary>
    private int InBag(string code) =>
        Ctx.Self.CarrySlots().Where(s => s.Content.Code == code).Sum(s => s.Content.Count);

    /// <summary>Что лежит в своём слоте ПРЯМО СЕЙЧАС (null — пусто или слота нет).</summary>
    private SlotContent? Carried(string inventoryId, int slot) =>
        Ctx.Self.Inventories.TryGetValue(inventoryId, out var slots) &&
        slot >= 0 && slot < slots.Length && !slots[slot].IsEmpty
            ? slots[slot]
            : null;

    // ---------------- три настоящих устройства ----------------

    /// <summary>
    /// Сундук, бочка, корзина — всё, что стоит блоком.
    ///
    /// ИМЯ ИНВЕНТАРЯ БЕРЁТСЯ ИЗ АТРИБУТА БЛОКА «inventoryClassName», и это не
    /// придирка. Раньше здесь стояло имя КЛАССА БЛОК-СУЩНОСТИ, которое сервер
    /// присылает с чанком, — у обычного сундука это «GenericTypedContainer».
    /// Инвентарь же сервер зовёт «chest-X, Y, Z», и все наши пакеты
    /// перекладывания уходили на инвентарь, которого нет. Чтение при этом
    /// работало (содержимое приходит по координатам) — потому беда и жила
    /// незаметно. Ровно об эту подмену уже спотыкались при разборе пакета
    /// открытия (SelfState.StoreContainerFromOpen), там же записано правило;
    /// теперь оно тут одно на обе стороны.
    ///
    /// РЕГИСТР ИМЕНИ НЕ ТРОГАЕМ — и это тоже не придирка. Игра строит id
    /// инвентаря дословно: <c>InventoryClassName + "-" + Pos</c>
    /// (BlockEntityGenericTypedContainer.Initialize), а ищет его сервер обычным
    /// словарём, то есть посимвольно и с учётом регистра
    /// (PlayerInventoryManager.GetInventory → Inventories.TryGetValue). Здесь
    /// стояло приведение к нижнему регистру: на ванильных блоках оно ничего не
    /// меняло (там все inventoryClassName и так строчные), а на модовом ящике с
    /// именем вроде «ModStorage» бот ЧИТАЛ инвентарь под одним ключом
    /// (SelfState его не понижает), а КЛАЛ по другому — и модовый сундук отдавал
    /// ноль при полном содержимом. Одна механика — одно написание, дословное.
    /// </summary>
    public static Container Chest(BotContext ctx, Hands hands, BlockPos pos, string? className = null) =>
        new BlockContainer(ctx, hands, pos,
            Actions.ContainerInventoryId(pos,
                className ?? ctx.World.GetInventoryClass(pos.X, pos.Y, pos.Z)));

    /// <summary>
    /// Костёр. Id у него свой — «smelting-x/y/z» через косые черты, не как
    /// у сундука (BlockEntityFirepit.Initialize).
    /// </summary>
    /// <remarks>
    /// СОДЕРЖИМОЕ КОСТРА ВИДНО СНАРУЖИ — тогда чанк ему не слепок, а живая картинка.
    /// Верно ровно там, где игра его РИСУЕТ: кусок мяса на огне и готовое в
    /// выходе человек видит, просто стоя рядом, и сервер шлёт эти блок-сущности
    /// сам, пока костёр горит. У сундука такого нет: там опись приходит только
    /// в ответ на открытие, и подмена её слепком из чанка — та самая беда,
    /// из-за которой полный сундук читался как «пусто».
    /// </remarks>
    public static Container Firepit(BotContext ctx, Hands hands, BlockPos pos) =>
        new BlockContainer(ctx, hands, pos, $"smelting-{pos.X}/{pos.Y}/{pos.Z}",
            visibleOutside: true);

    /// <summary>Разделанная туша: живёт при существе, а не при блоке.</summary>
    public static Container Carcass(BotContext ctx, Hands hands, long entityId) =>
        new CarcassContainer(ctx, hands, entityId);
}

/// <summary>Хранилище-блок: сундук, бочка, костёр.</summary>
internal sealed class BlockContainer : Container
{
    private readonly BlockPos pos;

    /// <summary>Содержимое видно снаружи (костёр) — см. Container.Firepit.</summary>
    private readonly bool visibleOutside;

    public BlockContainer(BotContext ctx, Hands hands, BlockPos pos, string inventoryId,
        bool visibleOutside = false)
        : base(ctx, hands)
    {
        this.pos = pos;
        InventoryId = inventoryId;
        this.visibleOutside = visibleOutside;
    }

    public override string InventoryId { get; }

    /// <summary>
    /// Как назвать это в журнале — БЕЗ ВЫДУМКИ.
    ///
    /// Живой случай: «air (512061, 112, 512255): не открылся». Никакого воздуха
    /// бот там не видел — он про этот кусок мира вообще ничего не знал, а
    /// модель мира отвечала на любой вопрос о невыгруженном чанке нулём, то
    /// есть воздухом. Человек, стоя у сундуков, читал в журнале прямую
    /// неправду и искал беду не там.
    /// </summary>
    public override string Name =>
        Ctx.World.IsKnown(pos)
            ? $"{Ctx.World.GetBlockCode(pos) ?? "контейнер"} {pos}"
            : $"{pos} (чанк не пришёл — что там стоит, не знаю)";

    /// <summary>
    /// Что внутри — ДВУМЯ путями, и они не равноценны.
    ///
    /// Пока хранилище открыто, сервер шлёт его слоты нам лично (пакеты 5000,
    /// 31, 32) — это ровно то, что видит человек в окне сундука, и обновляется
    /// оно на каждый перенос. Это и есть мерка успеха.
    ///
    /// Закрытое хранилище мы знаем только по данным чанка: они приходят сами и
    /// годятся, чтобы решить «идти туда или нет», не сходя с места.
    ///
    /// Живой снимок нарочно забывается при открытии и при закрытии (см. ниже):
    /// иначе прошлое содержимое сойдёт за свежее, и бот скажет «пусто» про
    /// полный сундук.
    /// </summary>
    public override SlotContent[]? Contents() => LiveContents() ?? Ctx.World.GetContainerContents(pos);

    /// <summary>
    /// ТОЛЬКО ЖИВАЯ ОПИСЬ — та, что сервер прислал нам лично (5000, 30, 31, 32)
    /// и обновляет на каждый перенос, пока хранилище открыто.
    ///
    /// Второго источника здесь нарочно НЕТ. Данные чанка кладутся в память один
    /// раз, когда чанк приходит, и потом не меняются НИКОГДА: ни от наших
    /// переносов (их видит только инвентарный канал), ни от чужих рук. Живой
    /// случай: бот за час до того сложил в этот самый сундук 1007 предметов, а
    /// придя за ножом, прочитал слепок часовой давности и доложил «пусто».
    ///
    /// ЕДИНСТВЕННОЕ ИСКЛЮЧЕНИЕ — костёр: его содержимое игра РИСУЕТ, сервер шлёт
    /// эти блок-сущности сам, пока он горит, и слепком они не бывают. Требовать
    /// от костра описи по открытию значило бы сделать бота слепее человека,
    /// который видит мясо на огне, просто стоя рядом.
    /// </summary>
    public override SlotContent[]? LiveContents() =>
        Ctx.Self.Inventories.TryGetValue(InventoryId, out var live) ? live
        : visibleOutside ? Ctx.World.VisibleContents(pos)
        : null;

    public override Task<bool> ReachAsync(CancellationToken ct) => Hands.ReachForAsync(pos, ct: ct);

    public override Task OpenAsync(CancellationToken ct)
    {
        Ctx.Self.ForgetInventory(InventoryId);
        return Ctx.Actions.OpenBlockInventoryAsync(pos, InventoryId);
    }

    public override async Task CloseAsync(CancellationToken ct)
    {
        await Ctx.Actions.CloseBlockInventoryAsync(pos, InventoryId);
        // Закрытому сундуку живой снимок больше не хозяин: сервер его не
        // обновляет, а человек может выгрести содержимое руками
        Ctx.Self.ForgetInventory(InventoryId);
    }

    public override Task MoveOutAsync(int fromSlot, string toInventory, int toSlot, int count) =>
        Ctx.Actions.MoveItemAsync(InventoryId, fromSlot, toInventory, toSlot, count);

    public override Task MoveInAsync(string fromInventory, int fromSlot, int toSlot, int count) =>
        Ctx.Actions.MoveItemAsync(fromInventory, fromSlot, InventoryId, toSlot, count);
}

/// <summary>
/// Туша зверя. Отличается от блока во всём: содержимое лежит в
/// WatchedAttributes существа, открывается кликом ПО СУЩЕСТВУ, а переносы
/// заворачиваются в EntityPacket — общий инвентарный канал её не знает.
/// </summary>
internal sealed class CarcassContainer : Container
{
    private readonly long entityId;

    public CarcassContainer(BotContext ctx, Hands hands, long entityId)
        : base(ctx, hands) => this.entityId = entityId;

    // ИМЯ ИНВЕНТАРЯ ТУШИ СЧИТАЕТСЯ В ОДНОМ МЕСТЕ (закон 2). Строка
    // «harvestableContents-{id}» была написана здесь ВТОРОЙ раз — своими
    // буквами, рядом с готовым Butchering.CarcassInventoryId. Разъехаться им
    // было нечем только на глаз: перенос из туши шлётся с одним именем, а
    // ищется по другому, и добыча с убитых пропала бы молча
    public override string InventoryId => Butchering.CarcassInventoryId(entityId);

    public override string Name =>
        $"туша {Ctx.Entities.Get(entityId)?.Code ?? entityId.ToString()}";

    public override SlotContent[]? Contents() => LiveContents();

    /// <summary>
    /// У туши источник ОДИН — WatchedAttributes самого зверя, и сервер шлёт их
    /// живыми (пакет сущности). Второго, замороженного, здесь нет и быть не
    /// может: туша не блок, чанк её содержимого не носит. Поэтому «живое» и
    /// «всё, что знаем» тут совпадают, и <see cref="Contents"/> отдаёт то же.
    /// </summary>
    public override SlotContent[]? LiveContents() =>
        Ctx.Entities.Get(entityId)?.WatchedAttributes.GetTreeAttribute("harvestableInv")
            is { } tree ? Ctx.World.ReadInventoryTree(tree) : null;

    public override async Task<bool> ReachAsync(CancellationToken ct)
    {
        if (Ctx.Entities.Get(entityId) is not { } target)
            return false;
        if (Hands.DistanceToPoint(target.X, target.Y + 0.5, target.Z) <= Hands.Reach)
            return true;
        await Ctx.Movement.ApproachAsync(() =>
        {
            var live = Ctx.Entities.Get(entityId);
            return (live?.X ?? target.X, live?.Y ?? target.Y, live?.Z ?? target.Z);
        }, stopDistance: 1.5, maxSeconds: 30, ct: ct);
        return Hands.DistanceToPoint(target.X, target.Y + 0.5, target.Z) <= Hands.Reach;
    }

    /// <summary>
    /// Клик по туше заставляет сервер зарегистрировать её инвентарь
    /// (EntityBehaviorHarvestable.OnInteract зовёт OpenInventory), а вторым
    /// шагом настоящий клиент шлёт ещё и Inventory.Open — шлём оба.
    /// </summary>
    public override async Task OpenAsync(CancellationToken ct)
    {
        await Ctx.Actions.UseEntityAsync(entityId);
        await Task.Delay(400, ct).ContinueWith(_ => { });
        await Ctx.Actions.OpenInventoryByIdAsync(InventoryId);
    }

    /// <summary>
    /// ЗАКРЫТЬ ТУШУ — И ЭТО НЕ ВЕЖЛИВОСТЬ, А ЗАВЕРШАЮЩИЙ ХОД РАЗДЕЛКИ. Здесь
    /// стояло <c>Task.CompletedTask</c> — «туше закрывать нечего», — и ровно
    /// на этом заказчик 20.08 поймал бота: «такое чувство, что разделка у нас
    /// идёт как чит — когда игрок разделывает и если туша пустая, то она
    /// исчезает, а после разделки бота никто не исчезает». Живой журнал
    /// подтверждает построчно: «[разделка] разделал drifter-normal», «туша
    /// drifter-normal: пусто — сервер прислал опись, все 4 мест свободны», и
    /// туша остаётся лежать.
    ///
    /// Как это устроено в игре (разобрано по сборкам 1.22.7, а не по памяти):
    /// закрывая окно туши, клиент шлёт «инвентарь закрыт» (пакет 30,
    /// Opened = 0), сервер зовёт <c>inv.Close(player)</c>, а на его событие
    /// подписана сама разделка —
    /// <c>EntityBehaviorHarvestable.Inv_OnInventoryClosed</c>: пусто и есть
    /// поведение распада → <c>EntityBehaviorDeadDecay.DecayNow()</c>, и туша
    /// рассыпается частицами тут же. Не шлёшь закрытие — событие не приходит
    /// НИКОГДА, и обобранная туша лежит до естественного распада (у дрифтера
    /// это три игровых часа).
    ///
    /// Пакет собирается ОДИН РАЗ на весь проект
    /// (<see cref="Actions.CloseInventoryByIdAsync"/>) — тем же, которым
    /// закрываются сундук и костёр. У блока к нему добавляется второй,
    /// блочный; туше он не нужен и не шлётся: туша не блок.
    /// </summary>
    public override Task CloseAsync(CancellationToken ct) =>
        Ctx.Actions.CloseInventoryByIdAsync(InventoryId);

    public override Task MoveOutAsync(int fromSlot, string toInventory, int toSlot, int count) =>
        Ctx.Actions.MoveItemFromEntityAsync(entityId, InventoryId, fromSlot, toInventory, toSlot, count);

    public override Task MoveInAsync(string fromInventory, int fromSlot, int toSlot, int count) =>
        Ctx.Actions.MoveItemFromEntityAsync(entityId, fromInventory, fromSlot, InventoryId, toSlot, count);
}
