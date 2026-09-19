// Флаги хранения берём из игры точечно: тянуть весь Vintagestory.API.Common
// нельзя — там свой Func<>, и он конфликтует с системным (см. Hands.cs).
using EnumItemStorageFlags = Vintagestory.API.Common.EnumItemStorageFlags;

namespace VsBotKit;

/// <summary>
/// «Живой взгляд»: стоя на месте БЕЗ ДЕЛА, бот поворачивает голову к ближайшему
/// игроку, а если тот за пределами поворота шеи — доворачивает корпус. Во время
/// ходьбы не вмешивается (взглядом на ходу управляет Movement).
/// Никогда не «берёт ход» — чисто косметика, не блокирует другие способности.
///
/// ВЗГЛЯД У ДЕЛА, И ЭТО ГЛАВНОЕ ЗДЕСЬ ПРАВИЛО. Живой случай: бот копает карьер,
/// мимо идёт человек — эта способность разворачивала КОРПУС к нему, и бот
/// продолжал бить, стоя задом к забою. Заказчик дословно: «когда занят делом не
/// нужно оборачиваться на игрока, а то копает задом наперед».
///
/// Отнимать живость совсем было бы лечением хуже болезни: ради неё всё и
/// писалось. Поэтому мерка одна и она чужая — <see cref="Movement.WhoseLook"/>:
/// пока прицел держит работа (добыча, установка, применение предмета) или бой,
/// эта способность молчит; как только тело свободно — смотрит на людей, как
/// раньше. Приветствие и поворот к собеседнику живут отдельно (Meetings) и
/// сюда не заходят вовсе.
/// </summary>
public class BehaviorLookAlive : BotBehavior
{
    private readonly EntityModel entities;
    private readonly Movement movement;

    /// <summary>Радиус реакции на игроков, блоков.</summary>
    public double Radius { get; set; } = 8;

    /// <summary>Предел поворота головы от корпуса (у игрока ~85°).</summary>
    public float MaxHeadTurn { get; set; } = 1.45f;

    private long lastTargetId = -1;

    public BehaviorLookAlive(BotContext ctx)
        : this(ctx.Entities, ctx.Movement) { }

    public BehaviorLookAlive(EntityModel entities, Movement movement)
    {
        this.entities = entities;
        this.movement = movement;
    }

    /// <summary>
    /// Чей сейчас взгляд (для журнала и окна управления): «дело», «цель»,
    /// «ход» или «люди». Спрашивается у механизма, своего счёта нет.
    /// </summary>
    public Movement.Gaze Whose => movement.LookBelongsTo;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // ВЗГЛЯД ПРИНАДЛЕЖИТ ДЕЛУ. Одна строка вместо трёх проверок: «идёт»,
        // «в бою» и «целится» — это один и тот же вопрос, и отвечает на него
        // одно правило (см. Movement.WhoseLook). Раньше здесь спрашивали только
        // про ход и бой, а про работу не спрашивали вовсе — оттого и разворот
        // спиной к забою посреди удара
        if (movement.LookBelongsTo != Movement.Gaze.Люди || entities.Self is not { } self)
            return false;

        // Ближайший игрок, но «липко»: не переключаемся на другого, пока тот
        // не станет ЗАМЕТНО ближе (на 1.5 блока) — иначе при двух игроках на
        // равном расстоянии бот дёргается между ними
        var players = entities.Nearby(self.X, self.Y, self.Z, Radius)
            .Where(e => e.IsPlayer && e.Id != entities.OwnEntityId)
            .ToList();
        var target = players.FirstOrDefault();
        if (target != null && lastTargetId >= 0 && target.Id != lastTargetId)
        {
            var current = players.FirstOrDefault(e => e.Id == lastTargetId);
            if (current != null &&
                target.DistanceTo(self.X, self.Y, self.Z) + 1.5 > current.DistanceTo(self.X, self.Y, self.Z))
                target = current; // прежний собеседник ещё достаточно близко — не переключаемся
        }

        if (target == null)
        {
            lastTargetId = -1;
            return false;
        }

        double dx = target.X - self.X, dz = target.Z - self.Z;
        if (dx * dx + dz * dz < 0.5)
            return false; // вплотную — не дёргаемся

        float toTarget = (float)Math.Atan2(dx, dz);
        float diff = NormalizeAngle(toTarget - self.Yaw);

        if (Math.Abs(diff) > MaxHeadTurn || target.Id != lastTargetId)
        {
            // Новый собеседник или он за спиной — повернуться корпусом
            await movement.FaceAsync(target.X, target.Z);
            lastTargetId = target.Id;
        }
        else
        {
            // Следим головой, корпус на месте
            await movement.LookHeadAsync(toTarget);
        }
        return false; // косметика — не блокируем другие способности
    }

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }
}

/// <summary>
/// Источник света В РУКЕ.
///
/// КАК ЭТО УСТРОЕНО В ИГРЕ (проверено по её сборкам, а не по догадке):
/// - светит не «факел», а ЛЮБОЙ предмет с ненулевой яркостью: EntityPlayer.LightHsv
///   берёт LightHsv у предмета в ПРАВОЙ руке (активный слот хотбара) и у предмета
///   в ЛЕВОЙ (слот 11 хотбара, ItemSlotOffhand). Третья компонента — яркость;
/// - значит своего списка кодов заводить не нужно и НЕЛЬЗЯ: яркость каждого
///   предмета сервер прислал в реестре (BlockTypeNet/ItemTypeNet заполняют
///   LightHsv), и спрашивать надо игру. Факел, фонарь, модовая лампа — всё
///   найдётся само;
/// - ЛЕВАЯ РУКА — не хитрость, а штатное место: у факела в torch.json
///   storageFlags 257 = General|Offhand, то есть игра сама разрешает носить его
///   в левой руке, и живые игроки так и ходят. Правая при этом остаётся под
///   инструмент, и драки за руку с добычей не возникает вовсе;
/// - за левую руку игра БЕРЁТ ПЛАТУ: InventoryPlayerHotbar ставит игроку
///   модификатор «hungerrate» = OffHandHungerPenalty (0.2), то есть голод растёт
///   на 20% быстрее. Это честная цена, и она не бесплатна — поэтому фонарь
///   берётся не круглые сутки, а когда темно (см. BehaviorCarryLight).
///
/// Это механизм. Когда светить и чем — дело роли и способности.
/// </summary>
public sealed class HandLight
{
    private readonly BotContext ctx;

    /// <summary>
    /// Номер слота левой руки в хотбаре. Число из игры:
    /// InventoryPlayerHotbar.offHandSlotIndex = 11 (слот 10 — умение).
    ///
    /// ХРАНИТСЯ НЕ ЗДЕСЬ, а у <see cref="Bags.OffHandSlot"/> — то же число
    /// объявляли ТРИЖДЫ (тут, у сумок и голым литералом в
    /// <see cref="SelfState.IsStorageSlot"/>). Имя оставлено: им пользуются
    /// снаружи, и переименование ничего бы не починило — чинит именно то, что
    /// число теперь одно.
    /// </summary>
    public const int OffHandSlot = Bags.OffHandSlot;

    public HandLight(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    /// <summary>Сколько ждать, пока сервер покажет вещь в левой руке, миллисекунд.</summary>
    public int MoveSettleMs { get; set; } = 400;

    /// <summary>
    /// Яркость предмета по коду: 0..31, ноль — не светит. Берётся у настоящего
    /// объекта из реестра сервера (CollectibleObject.LightHsv, третья компонента —
    /// та самая, по которой игра решает, светит ли предмет в руке).
    /// </summary>
    public int Brightness(string? code) =>
        code is { Length: > 0 } ? ctx.World.GetCollectible(code)?.LightHsv[2] ?? 0 : 0;

    /// <summary>Разрешает ли игра носить этот предмет в левой руке.</summary>
    public bool FitsOffHand(string? code) =>
        code is { Length: > 0 } && ctx.World.GetCollectible(code) is { } c &&
        (c.StorageFlags & EnumItemStorageFlags.Offhand) != 0;

    /// <summary>
    /// МОЖНО ЛИ ВЗЯТЬ ЭТО В ЛЕВУЮ РУКУ — и почему нельзя, словами.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫМ ОТВЕТОМ. Заказчик просил кнопку («в инвентаре… нет
    /// кнопки его надеть»), а кнопка бывает и серой — и серая кнопка без
    /// причины читается как поломка окна. Спрашивать при этом надо ТОТ ЖЕ
    /// механизм, который потом и понесёт вещь (<see cref="ToOffHandAsync"/>):
    /// реши окно своей формулой — оно светило бы «можно», а бот отвечал бы
    /// «не кладу». Ровно так уже устроено «надеть» (<c>Hands.WhereWorn</c>).
    ///
    /// Порядок отказов тот же, что у переноса, и по той же причине: сперва то,
    /// что видно сразу (не из сумки, занято), потом ответ реестра игры.
    /// </summary>
    public (bool Can, string Why) WhyOffHand(string inventoryId, int slot, string? code)
    {
        if (code is not { Length: > 0 })
            return (false, "вещи нет — брать нечего");
        if (!SelfState.IsCarryBag(inventoryId) || !SelfState.IsStorageSlot(inventoryId, slot))
            return (false, "в левую руку кладут из сумок и хотбара; это место не из них " +
                           "(надетое, курсор, чужой сундук или сама рука) — сперва уберите вещь в сумку");
        if (!FitsOffHand(code))
            return (false, $"{code} в левую руку игра не кладёт: у него нет флага Offhand " +
                           "(storageFlags) — так же откажет и сервер");
        if (InOffHand is { Code: { } busy })
            return (false, $"левая рука занята: в ней {busy}");
        return (true, "");
    }

    /// <summary>
    /// Самый яркий из носимого. Чистое правило — закрыто тестом: при равной
    /// яркости берём по коду, чтобы выбор был предсказуем и бот не перекладывал
    /// два одинаковых факела туда-сюда каждый тик.
    /// </summary>
    public static string? Brightest(IEnumerable<(string Code, int Brightness)> carried)
    {
        string? best = null;
        int bestLight = 0;
        foreach (var (code, light) in carried)
        {
            if (light <= 0 || code is not { Length: > 0 })
                continue;
            if (light > bestLight ||
                (light == bestLight && string.CompareOrdinal(code, best) < 0))
            {
                bestLight = light;
                best = code;
            }
        }
        return best;
    }

    /// <summary>
    /// Самый яркий источник света в сумках (null — светить нечем).
    /// </summary>
    /// <param name="minBrightness">
    /// Тусклее этого в руку не берём. Ноль — брать всё, что светит хоть как-то.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ради которого мерка появилась (журнал 16.08, 03:17 и
    /// 09:01): «взял gear-temporal в руку (левая рука)». У временной шестерёнки
    /// в gear.json стоит lightHsv [32, 5, 2] — она и правда светит, но на ДВА
    /// блока, а левая рука стоит 20% скорости голода
    /// (InventoryPlayerHotbar.OffHandHungerPenalty). Бот платил голодом за
    /// светлячок и вдобавок занимал руку так, что настоящий фонарь в неё уже
    /// не помещался. Для сравнения: факел [4,4,14], масляная лампа [4,2,11],
    /// фонарь [7,3,18] и [7,3,20] — разница не на глаз.
    /// </param>
    /// <param name="offHandOnly">
    /// СЧИТАТЬ ТОЛЬКО ТО, ЧТО ИГРА ПУСКАЕТ В ЛЕВУЮ РУКУ.
    ///
    /// ЗАЧЕМ. Спрашивающий выбирает свет ПОД КОНКРЕТНУЮ РУКУ, и ответ «самое
    /// яркое вообще» для левой руки — не ответ, а ловушка: ярче фонаря [7,3,20]
    /// в игре есть и то, что в левую руку не кладётся вовсе — люстра
    /// (chandelier-candle6, [7,7,24]), зажжённый бумажный фонарик
    /// (paperlantern-on, [8,2,21]), связка свечей (bunchocandles, до [7,7,15]).
    /// У всех троих storageFlags не задан, а по умолчанию это
    /// CollectibleObject.StorageFlags = General, то есть флага Offhand нет.
    ///
    /// Без этой мерки выходил живой круг: бот видит в сумке «ярче», выкладывает
    /// из левой руки РАБОЧИЙ фонарь — и положить туда люстру не может, а правая
    /// занята киркой. Свет пропадал совсем и не возвращался, пока люстра лежит
    /// в сумке, — то есть ровно та жалоба, ради которой всё и правилось
    /// («фонарь не сразу начал использовать из инвентаря»), только навсегда.
    ///
    /// Мерка не своя: спрашивается тот же <see cref="FitsOffHand"/>, которым
    /// потом откажет и <see cref="ToOffHandAsync"/>, и сервер.
    /// </param>
    public string? BestCarried(int minBrightness = 0, bool offHandOnly = false) =>
        Brightest(ctx.Self.CarrySlots()
            .Where(s => !s.Content.IsEmpty && s.Content.Code is { Length: > 0 })
            .Where(s => !offHandOnly || FitsOffHand(s.Content.Code))
            .Select(s => (Code: s.Content.Code!, Brightness: Brightness(s.Content.Code)))
            .Where(s => s.Brightness >= minBrightness));

    /// <summary>Что лежит в левой руке (null — слот неизвестен или пуст).</summary>
    public SlotContent? InOffHand
    {
        get
        {
            var hotbar = ctx.Self.GetInventory("hotbar");
            return hotbar != null && hotbar.Length > OffHandSlot && !hotbar[OffHandSlot].IsEmpty
                ? hotbar[OffHandSlot]
                : null;
        }
    }

    /// <summary>
    /// Чем бот светит прямо сейчас: код предмета в любой из рук, если он светит
    /// (null — в руках темно). Смотрим обе руки ровно потому, что их смотрит
    /// и игра в EntityPlayer.LightHsv.
    /// </summary>
    public string? Lit
    {
        get
        {
            if (InOffHand?.Code is { } left && Brightness(left) > 0)
                return left;
            if (ctx.Hands.Held?.Code is { } right && Brightness(right) > 0)
                return right;
            return null;
        }
    }

    /// <summary>
    /// Переложить предмет в ЛЕВУЮ руку. Успех — только по факту: сервер должен
    /// показать вещь в слоте 11. Отдельного пакета «взять в левую руку» в игре
    /// нет — это обычный перенос вещи, как мышью в окне инвентаря.
    /// </summary>
    public async Task<bool> ToOffHandAsync(string code, CancellationToken ct = default)
    {
        string? hotbarId = ctx.Self.GetInventoryId("hotbar");
        var hotbar = ctx.Self.GetInventory("hotbar");
        if (hotbarId == null || hotbar == null || hotbar.Length <= OffHandSlot)
        {
            OnLog?.Invoke("левой руки не видно: хотбар ещё не пришёл целиком");
            return false;
        }
        if (!hotbar[OffHandSlot].IsEmpty)
        {
            OnLog?.Invoke($"в левой руке уже {hotbar[OffHandSlot].Code} — не трогаю");
            return false;
        }
        if (!FitsOffHand(code))
        {
            OnLog?.Invoke($"{code} в левую руку игра не кладёт (нет флага Offhand)");
            return false;
        }
        if (ctx.Self.FindCarried(c => string.Equals(c, code, StringComparison.Ordinal))
            is not { } found)
        {
            OnLog?.Invoke($"{code} в сумках не нашёлся");
            return false;
        }

        await ctx.Actions.MoveItemAsync(found.InventoryId, found.Slot,
            hotbarId, OffHandSlot, found.Content.Count);

        // Ждём ПО ФАКТУ, а не глухой паузой: сервер отвечает и за сорок
        // миллисекунд, а отказывает молча — тогда и надо сказать вслух
        var until = DateTime.UtcNow.AddMilliseconds(Math.Max(1, MoveSettleMs));
        while (true)
        {
            if (InOffHand?.Code is { } now && string.Equals(now, code, StringComparison.Ordinal))
                return true;
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested)
                break;
            await Task.Delay(20, ct).ContinueWith(_ => { });
        }
        OnLog?.Invoke($"{code} в левую руку не переехал — сервер перенос не показал");
        return false;
    }

    /// <summary>
    /// Убрать вещь из левой руки в сумку. Нужно дважды: когда факел прогорел
    /// (горящий живёт 48 игровых часов и превращается в «burnedout», который
    /// уже не светит) и когда рассвело — левая рука не бесплатна, за неё берут
    /// 20% скорости голода.
    ///
    /// Успех — только по факту: сервер должен показать слот 11 пустым.
    /// </summary>
    public async Task<bool> ClearOffHandAsync(CancellationToken ct = default)
    {
        string? hotbarId = ctx.Self.GetInventoryId("hotbar");
        if (hotbarId == null || InOffHand is not { Code: { } code } held)
            return true;                 // и так пусто — убирать нечего

        if (ctx.Self.FreeSlotFor(code, held.Count) is not { } spot)
        {
            OnLog?.Invoke($"{code} из левой руки убрать некуда: в сумках нет места");
            return false;
        }

        await ctx.Actions.MoveItemAsync(hotbarId, OffHandSlot,
            spot.InventoryId, spot.Slot, held.Count);

        var until = DateTime.UtcNow.AddMilliseconds(Math.Max(1, MoveSettleMs));
        while (true)
        {
            if (InOffHand == null)
                return true;
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested)
                break;
            await Task.Delay(20, ct).ContinueWith(_ => { });
        }
        OnLog?.Invoke($"{code} из левой руки не убрался — сервер перенос не показал");
        return false;
    }
}

/// <summary>
/// Фонарь в руке вне дела — чтобы бот выглядел живым игроком, а не факелом
/// на ножках, который зажигает свет только когда копает.
///
/// Заказчик дословно: «Симуляция живого игрока путем ношения фонаря в руке вне
/// действий», «Взялся за дело — рука освобождается под инструмент».
///
/// КАК ЭТО СДЕЛАНО И ПОЧЕМУ ИМЕННО ТАК:
/// - по умолчанию свет едет в ЛЕВУЮ руку (см. <see cref="HandLight"/>). Тогда
///   «рука освобождается под инструмент» выполняется само собой: правая вообще
///   не трогается. Это не обход задачи, а то, ради чего игра и завела левую
///   руку — факел там носить РАЗРЕШЕНО его же storageFlags;
/// - правая рука оставлена как запасной путь (PreferOffHand = false или предмет
///   в левую руку не кладётся). Там уже приходится спрашивать очередь на тело
///   (BodyArbiter) и ждать после смены слота: смена руки — это пакет, и цена
///   ей — Hands.HandChangeSettleMs;
/// - в правой руке бот НЕ отбирает инструмент и оружие у самого себя. Без этого
///   получается вечная перепалка с BehaviorWeaponAtNight: тот к ночи берёт меч,
///   этот тут же меняет меч на факел, и так дважды в секунду до рассвета;
/// - свет берётся, когда ТЕМНО. Днём фонарь в руке не нужен, а левая рука
///   стоит 20% скорости голода (InventoryPlayerHotbar.OffHandHungerPenalty) —
///   платить за это на солнцепёке незачем.
///
/// «ТЕМНО» — ЭТО НЕ ТОЛЬКО ЧАСЫ, и вот почему это пришлось разбирать отдельно.
/// Заказчик: «Фонарь нужно использовать ночью и в пещерах». Под землёй темно
/// круглые сутки, а цифры света из чанка там почти всегда устаревшие (свет
/// пересчитывает клиент у себя, и после первого взмаха киркой мы честно
/// отвечаем «не знаю» — см. Lighting). Поэтому <see cref="IsDarkHere"/>
/// спрашивает три источника подряд: свежий свет чанка, глубину под
/// поверхностью мира (<see cref="WorldModel.DepthUnderSky(BlockPos)"/> — она из пакета
/// сервера) и только потом часы.
///
/// ПРО «ПОДНЯТУЮ РУКУ С ФОНАРЁМ» (спрашивал заказчик). Это анимация САМОЙ
/// ИГРЫ, и мы для неё не шлём ничего: у фонаря в lantern.json прописаны
/// heldLeftTpIdleAnimation «holdinglanternlefthand» и heldRightTpIdleAnimation
/// «holdinglanternrighthand», а запускает их сервер сам —
/// EntityPlayer.OnGameTick на серверной стороне зовёт
/// HandleSeraphHandAnimations, тот берёт у предмета в руке
/// GetHeldTpIdleAnimation и включает StartLeftHeldIdleAnim. Живой игрок с
/// фонарём выглядит ровно так же; убирать тут нечего.
/// </summary>
public class BehaviorCarryLight : BotBehavior
{
    /// <summary>
    /// ТЕМНО ЛИ ЗДЕСЬ И ПОЧЕМУ ТАК РЕШЕНО.
    ///
    /// ПРИЧИНА ЕДЕТ ВМЕСТЕ С ОТВЕТОМ, И ЭТО НЕ УКРАШЕНИЕ. В журнале 16.08 бот
    /// полсотни раз написал «убрал lantern-large-up из левой руки: рассвело» —
    /// в 12:52, В ПОЛДЕНЬ, сидя в свежевыкопанной норе. Слово «рассвело» стояло
    /// в коде намертво, потому что причину придумывал тот, кто её НЕ ЗНАЛ:
    /// решение принимали три разных источника (свет чанка, глубина под небом,
    /// часы), а называл его один и всегда одинаково. Врал не бот — врала
    /// строка, и по ней невозможно было понять, что дребезжит не время суток,
    /// а МЕСТО.
    ///
    /// Теперь причину называет тот источник, который ответил, и своими словами:
    /// «здесь темно», «ушёл под землю — 20 бл ниже поверхности», «на дворе
    /// ночь», «вышел на свет».
    /// </summary>
    /// <param name="Dark">Темно ли; null — НЕ ЗНАЕМ (это не «светло»).</param>
    /// <param name="Why">Кто ответил и почему — словами и с числами.</param>
    public readonly record struct DarkVerdict(bool? Dark, string Why);

    private readonly BotContext ctx;
    private readonly HandLight light;

    private DateTime nextTry = DateTime.MinValue;
    private bool saidNoLight;
    private bool saidUnknownDark;
    private string saidToolInHand = "";
    private string saidOffHandTaken = "";

    /// <summary>
    /// О чём мы уже сказали, уступая руку щиту. Отдельно от
    /// <see cref="saidOffHandTaken"/> нарочно: там речь про «в руке лежит
    /// чужое», здесь — про «руку ждёт щит», и оба повода бывают разом.
    /// </summary>
    private string saidShieldWants = "";

    /// <summary>
    /// В левой руке лежит то, что положил туда БОТ. Нужно, чтобы не отбирать
    /// щит, положенный человеком: своё убираем, чужое — нет.
    /// </summary>
    private bool offHandIsMine;

    /// <summary>
    /// ПРЕЖНИЙ ОТВЕТ «ТЕМНО ЛИ ЗДЕСЬ» — память гистерезиса, и другой памяти у
    /// него нет. null — ответа ещё не было (первый даётся по порогу зажигания,
    /// см. <see cref="Hysteresis.Holds"/>).
    ///
    /// Память ОДНА на все три источника нарочно: отвечает в каждый миг ровно
    /// один из них, а держать надо не «прежний ответ источника», а прежний
    /// ответ БОТА — иначе переход «вышел из норы под дневное небо» опять
    /// начинал бы с чистого листа, ровно там, где дребезг и был.
    /// </summary>
    private bool? wasDark;

    public BehaviorCarryLight(BotContext ctx) : this(ctx, new HandLight(ctx)) { }

    public BehaviorCarryLight(BotContext ctx, HandLight light)
    {
        this.ctx = ctx;
        this.light = light;
    }

    /// <summary>Механизм «свет в руке» — роль может спросить его напрямую.</summary>
    public HandLight Light => light;

    /// <summary>Выключатель: роль может гасить способность, не убирая её из списка.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Класть свет в ЛЕВУЮ руку, если игра это разрешает (правая остаётся под инструмент).</summary>
    public bool PreferOffHand { get; set; } = true;

    /// <summary>Брать свет только когда темно (см. оговорку о цене левой руки).</summary>
    public bool OnlyWhenDark { get; set; } = true;

    /// <summary>
    /// СЧИТАТЬ ПОДЗЕМЕЛЬЕ ТЕМНОТОЙ КРУГЛЫЕ СУТКИ.
    ///
    /// Заказчик дословно: «Фонарь нужно использовать ночью и в пещерах».
    /// Одними часами это не решается: в штольне темно и в полдень, а цифры
    /// света из чанка под землёй почти всегда устаревшие — свет пересчитывает
    /// клиент у себя, и после первого же взмаха киркой мы честно отвечаем «не
    /// знаю» (подробно — в Lighting). Поэтому третий, независимый источник:
    /// достаёт ли сюда небо (<see cref="WorldModel.DepthUnderSky(BlockPos)"/>).
    /// </summary>
    public bool TakeUnderground { get; set; } = true;

    /// <summary>
    /// С КАКОЙ ГЛУБИНЫ ПОД ПОВЕРХНОСТЬЮ СЧИТАЕМ, ЧТО МЫ В ПЕЩЕРЕ, блоков.
    ///
    /// Ноль тут не годится, и это не осторожность, а устройство света в игре:
    /// солнечный свет идёт вниз по открытой колонне БЕЗ ослабления, поэтому на
    /// дне карьера в три слоя днём светло, как наверху, — а «ниже поверхности»
    /// он уже на три блока. Пещера же закрыта породой, и туда небо не доходит
    /// вовсе. Само число — политика: его крутит роль.
    /// </summary>
    public int UndergroundDepth { get; set; } = 6;

    /// <summary>
    /// Убирать свет из левой руки, когда рассвело или когда факел прогорел.
    /// Выключить это — значит платить 20% скорости голода круглые сутки
    /// и ходить с прогоревшим факелом, который уже ничего не освещает.
    /// Убирается только то, что бот положил туда САМ: щит, положенный
    /// человеком, — не наше дело.
    /// </summary>
    public bool PutAwayWhenLight { get; set; } = true;

    /// <summary>
    /// ЗАЖИГАТЬ, КОГДА СВЕТ НИЖЕ ЭТОГО УРОВНЯ (когда свет известен и свеж).
    /// Это порог ОДНОГО направления — «пора брать»; обратно, «пора убрать»,
    /// отвечает <see cref="LightThreshold"/>, и числа у них разные нарочно.
    /// </summary>
    public int DarkThreshold { get; set; } = 6;

    /// <summary>
    /// УБИРАТЬ, КОГДА СВЕТ ПОДНЯЛСЯ ДО ЭТОГО УРОВНЯ — второй порог, отодвинутый
    /// от <see cref="DarkThreshold"/>.
    ///
    /// ЖИВОЙ СЛУЧАЙ (bot.log 16.08, 12:52–13:26): порог был ОДИН, и бот в
    /// штольне, которую сам же освещал факелами, мотал фонарь туда-сюда — 58
    /// пар «взял → убрал», медиана 0,9 с. Уровень света у самой границы ходит
    /// вверх-вниз от шага к шагу, и одно число на оба направления переворачивало
    /// ответ на каждом шагу. С двумя порогами полоса между ними держит прежний
    /// ответ (<see cref="Hysteresis"/>).
    ///
    /// Меньше <see cref="DarkThreshold"/> ставить бессмысленно — запаса не
    /// будет вовсе; это разобрано там, где числа берутся (см. Dark).
    /// </summary>
    public int LightThreshold { get; set; } = 10;

    /// <summary>
    /// ПОВЕРХНОСТЬ — ЭТО НЕ ГЛУБЖЕ СТОЛЬКИХ БЛОКОВ ПОД НЕБОМ. Второй порог к
    /// <see cref="UndergroundDepth"/>: под землю бот «уходит» на своей глубине,
    /// а «выходит» — только поднявшись сюда.
    ///
    /// ЖИВОЙ СЛУЧАЙ, И ОН ГЛАВНЫЙ В ЭТОЙ ЖАЛОБЕ. 16.08 бот бил наклонную
    /// штольню вверх (журнал: «прошёл вверх 15 ступеней»), и глубина под небом
    /// у него ходила вокруг шести: шагнул под кровлю — пещера, шагнул обратно —
    /// уже нет, а дальше отвечали часы, и в полдень выходило «светло, убрать».
    /// Отсюда и полсотни «рассвело» в 12:52.
    /// </summary>
    public int SurfaceDepth { get; set; } = 3;

    /// <summary>
    /// ПОСЛЕ РАССВЕТА ДЕРЖАТЬ ФОНАРЬ ЕЩЁ СТОЛЬКО ИГРОВЫХ ЧАСОВ (и ровно
    /// настолько же раньше брать его к ночи).
    ///
    /// Время идёт только вперёд, но час суток бот между пакетами досчитывает
    /// сам, а пришедший пакет правит его и назад — ровно на 6:00 ответ
    /// «ночь ли» успевает перевернуться несколько раз (см.
    /// <see cref="GameClock.NightWithin"/>). Это третий порог, и запас у него
    /// свой: он мерится в часах, а не в уровнях света и не в блоках.
    /// </summary>
    public double DawnMarginHours { get; set; } = 1;

    /// <summary>
    /// ПАУЗА МЕЖДУ ПЕРЕИГРЫВАНИЯМИ РЕШЕНИЯ О РУКЕ, секунды — и после неудачи,
    /// и ПОСЛЕ УДАЧИ. Запас по ВРЕМЕНИ, поверх запасов по числам.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ЗАМЕРЕННЫЙ ПО ЖУРНАЛУ (bot.log за 16.08, 12:52–13:26).
    /// Пауза стояла только на неудаче, и бот в штольне мотал фонарь туда-сюда:
    /// 58 пар «взял → убрал», из них 44 короче двух секунд, медиана 0,9 с,
    /// самая быстрая 0,49 с. Каждая пара — это два переноса вещи на сервер,
    /// то есть бот дважды в секунду дёргал сервер и сам себе мешал.
    ///
    /// ЗАЧЕМ ОНА, ЕСЛИ ЕСТЬ ЗАПАСЫ ПО ЧИСЛАМ. Запас по числу спасает от
    /// дрожания ОДНОГО источника у своей границы. А источников три, и они
    /// сменяют друг друга: свет чанка устаревает после взмаха киркой и умолкает
    /// (см. Lighting), карта поверхности приходит и уходит вместе с чанком.
    /// Смена источника — это скачок, и никакой полосой он не гасится; гасит его
    /// только время. Цена честная и названа вслух: прогоревший факел пролежит в
    /// руке до конца паузы, и фонарь после ошибочной уборки вернётся не мгновенно.
    /// </summary>
    public double RetrySeconds { get; set; } = 20;

    /// <summary>Взял свет в руку: код предмета и какая рука.</summary>
    public event Action<string, string>? OnLightInHand;

    /// <summary>Убрал свет из левой руки: код предмета и почему.</summary>
    public event Action<string, string>? OnPutAway;

    /// <summary>Светить нечем (говорится один раз, пока не появится).</summary>
    public event Action? OnNothingToLight;

    /// <summary>Не взял: причина.</summary>
    public event Action<string>? OnSkipped;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || ctx.Self.IsDead)
            return false;

        // «Темно ли» спрашиваем один раз на тик: ответ нужен и чтобы взять
        // свет, и чтобы вовремя его убрать
        DarkVerdict dark = OnlyWhenDark
            ? Dark()
            : new DarkVerdict(true, "темнота не в счёт: свет велено носить всегда");

        // ПАМЯТЬ ГИСТЕРЕЗИСА ОБНОВЛЯЕТСЯ ТОЛЬКО НАСТОЯЩИМ ОТВЕТОМ. «Не знаю»
        // прежнего ответа не стирает: иначе бот, у которого на минуту пропали
        // и свет чанка, и карта поверхности, вернулся бы к чистому листу и
        // начал бы решать заново — с того самого дребезга
        if (dark.Dark is { } ответ)
            wasDark = ответ;

        // КЛАДЁМ СВОЙ ФАКТ НА ВЕСЫ СПОРА ЗА ЛЕВУЮ РУКУ. Темноту считает эта
        // способность и только она (три источника, три пары порогов и вся
        // история с «рассвело» в полдень — выше по файлу), угрозу считает бой.
        // Сам спор решает ОДНО правило на обе стороны, и обе читают из него же
        // (см. Shielding.Owner) — иначе фонарь и щит выталкивали бы друг друга
        // из слота, каждый по своей мерке
        ctx.Shield?.NoteDark(dark.Dark == true);
        // Спрашиваем правило ОДИН раз на тик: и «чья рука», и «почему» — это
        // один ответ, а два вызова подряд успели бы разойтись между собой
        var чьяРука = ctx.Shield?.Owner;
        string? щитПросит = чьяРука is { Кому: OffHandOwner.Щиту } спор ? спор.Почему : null;

        // ---- прибраться в левой руке ----
        //
        // Живой случай, ради которого это здесь: горящий факел живёт 48 игровых
        // часов и превращается в burnedout (torch.json, transientProps). Он
        // остаётся в левой руке, не светит НИЧЕГО, а голод всё равно растёт
        // на 20% быстрее. Без уборки бот так и ходил бы с погасшей палкой.
        if (light.InOffHand is { Code: { } inLeft })
        {
            bool shines = light.Brightness(inLeft) > 0;

            // В СУМКЕ ЛЕЖИТ ЯРЧЕ — ЗНАЧИТ РУКУ НАДО ОСВОБОДИТЬ ПОД НЕГО.
            //
            // ЖИВОЙ СЛУЧАЙ, ради которого это здесь. Заказчик: «фонарь не сразу
            // начал использовать из инвентаря (специально дал)». В журнале
            // видно, отчего: в 03:17 и в 09:01 бот брал в левую руку
            // gear-temporal (яркость 2 — светлячок), и дальше проверка «уже
            // светим» отвечала «да», так что настоящий фонарь (яркость 20),
            // лежащий рядом в сумке, не мог попасть в руку до самого рассвета.
            // Сравниваем с тем, что в сумках И ЧТО ИГРА ПУСКАЕТ В ЭТУ ЖЕ РУКУ
            // (offHandOnly). Рука в перебор не входит сама — CarrySlots не
            // отдаёт слоты 10 и 11, — поэтому перекладывать одно и то же
            // туда-сюда не с чем.
            //
            // ПОЧЕМУ ИМЕННО «В ЭТУ ЖЕ РУКУ». Освобождать руку ради вещи,
            // которую в неё положить нельзя, — значит остаться без света
            // совсем: люстра [7,7,24] и зажжённый бумажный фонарик [8,2,21]
            // ярче фонаря, но флага Offhand у них нет, а правая рука занята
            // киркой. Мерка тут одна и та же на оба вопроса — «чем светить»
            // и «что вынимать», иначе они разойдутся молча
            string? ярче = offHandIsMine && shines &&
                           light.BestCarried(light.Brightness(inLeft) + 1, offHandOnly: true) is { } b
                ? b
                : null;

            if (WhyFreeOffHand(offHandIsMine, shines, ярче, PutAwayWhenLight, dark, щитПросит)
                is { } зачем)
            {
                if (DateTime.UtcNow < nextTry)
                    return false;
                if (!await light.ClearOffHandAsync(ct))
                {
                    nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
                    return false;
                }
                offHandIsMine = false;
                // Решение принято — не переигрываем его следующим же тиком
                // (см. RetrySeconds: 58 пар «взял → убрал» за смену)
                nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
                OnPutAway?.Invoke(inLeft, зачем);
                return true;
            }
            if (!offHandIsMine && !shines && saidOffHandTaken != inLeft)
            {
                // Щит или что-то другое, положенное человеком. Своё место
                // в левой руке бот не отвоёвывает
                saidOffHandTaken = inLeft;
                OnSkipped?.Invoke($"в левой руке {inLeft} — не моё, не трогаю");
            }
        }
        else
        {
            saidOffHandTaken = "";
        }

        // РУКУ ЖДЁТ ЩИТ — ФОНАРЬ В НЕЁ НЕ ЛЕЗЕТ. Без этой строки выходил бы
        // круг: щит просит руку, мы её освобождаем, а следующим же тиком сами
        // кладём туда фонарь обратно — и так до конца стычки, дважды в секунду,
        // ровно тем дребезгом, от которого лечились RetrySeconds
        if (щитПросит is { Length: > 0 })
        {
            // Своя память на эту строку, а не общая с «в руке чужое»: у них
            // разные поводы, и одна память на двоих переворачивалась бы каждый
            // тик — щит в руке чужой И щит просит руку, обе строки сразу
            if (saidShieldWants != щитПросит)
            {
                saidShieldWants = щитПросит;
                OnSkipped?.Invoke($"фонарь в левую руку не беру: {щитПросит}");
            }
            return false;
        }
        saidShieldWants = "";

        // Уже светим — больше ничего не надо
        if (light.Lit != null)
        {
            saidNoLight = false;
            saidToolInHand = "";
            return false;
        }

        if (OnlyWhenDark)
        {
            if (dark.Dark == null)
            {
                // Причину называет тот, кто её знает, — сама проверка темноты.
                // Своей копии этих слов здесь больше нет: она разошлась бы с
                // правилом при первой же правке источников
                if (!saidUnknownDark)
                {
                    saidUnknownDark = true;
                    OnSkipped?.Invoke(dark.Why);
                }
                return false;
            }
            saidUnknownDark = false;
            if (dark.Dark == false)
                return false;
        }

        // ТУСКЛЕЕ СОБСТВЕННОГО ПОРОГА ТЕМНОТЫ В РУКУ НЕ БЕРЁМ, и мерка тут не
        // выдумана: DarkThreshold — это уровень, ниже которого МЫ САМИ зовём
        // клетку тёмной. Предмет, который светит слабее, не выведет из темноты
        // даже клетку под собой, а левая рука всё равно возьмёт свои 20%
        // скорости голода. Ровно так бот и взял gear-temporal (яркость 2)
        // вместо фонаря (20) — см. BestCarried
        //
        // СПРАШИВАЕМ ПРО ТУ РУКУ, В КОТОРУЮ СОБИРАЕМСЯ КЛАСТЬ, а не «что вообще
        // ярче в сумке». Разница не умозрительная: люстра (chandelier-candle6,
        // [7,7,24]) и зажжённый бумажный фонарик (paperlantern-on, [8,2,21])
        // ярче фонаря, но флага Offhand у них нет. Спроси «самое яркое вообще» —
        // и бот выберет люстру, в левую руку её не положит, а правая занята
        // киркой: света не будет ни в какой руке, хотя рядом в сумке лежит
        // годный фонарь. Поэтому сперва ищем лучшее ИЗ ТОГО, ЧТО ЛОЖИТСЯ В
        // ЛЕВУЮ, и только если там пусто — падаем на общий перебор под правую
        int порог = Math.Max(1, DarkThreshold);
        string? вЛевую = PreferOffHand ? light.BestCarried(порог, offHandOnly: true) : null;
        if ((вЛевую ?? light.BestCarried(порог)) is not { } best)
        {
            if (!saidNoLight)
            {
                saidNoLight = true;
                OnNothingToLight?.Invoke();
            }
            return false;
        }
        saidNoLight = false;

        if (DateTime.UtcNow < nextTry)
            return false;

        if (вЛевую != null)
        {
            // Левая рука активного слота не трогает, драться за тело не с кем
            if (!await light.ToOffHandAsync(best, ct))
            {
                nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
                return false;
            }
            offHandIsMine = true;
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            OnLightInHand?.Invoke(best, "левая рука");
            return true;
        }

        // Правая рука: тут уже спорят все
        if (ctx.Hands.Held?.Code is { } held && ctx.World.IsHandGearByCode(held))
        {
            if (saidToolInHand != held)
            {
                saidToolInHand = held;
                OnSkipped?.Invoke($"в руке {held} — инструмент и оружие не отбираю");
            }
            return false;
        }
        saidToolInHand = "";

        // ЗА РУКУ СПОРИТ РАСПОРЯДИТЕЛЬ, А НЕ МЫ САМИ. Здесь стояли ДВЕ мерки на
        // одно правило: своя проверка «телом занято — не спорю» и настоящее
        // взятие тела строкой ниже. Вторая копия правила молчала о том, кого
        // перебила, и расходилась бы с первой при любой правке весов. Теперь
        // дверь одна — она же берёт тело и она же говорит отказ вслух.
        //
        // Смена слота — это пакет, и после неё нужна пауза; и то, и другое
        // уже оплачено внутри Hands (HandChangeSettleMs), своей не заводим
        if (await ctx.Hands.TakeToHandAsync(Title, BodyArbiter.Importance.Routine,
                s => string.Equals(s.Code, best, StringComparison.Ordinal), ct) is null)
        {
            OnSkipped?.Invoke($"{best} в правую руку взять не вышло");
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            return false;
        }
        nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        OnLightInHand?.Invoke(best, "правая рука");
        return true;
    }

    /// <summary>
    /// НАДО ЛИ ОСВОБОДИТЬ ЛЕВУЮ РУКУ И ЗАЧЕМ — чистое правило, без мира и
    /// без сервера. Возвращает причину для журнала или null («не трогаем»).
    ///
    /// ПОЧЕМУ ОТДЕЛЬНОЙ ФУНКЦИЕЙ. Проверить это на стенде «как в жизни» нельзя:
    /// перекладывание вещи подтверждает СЕРВЕР, а его в тестах нет. Значит
    /// либо правило живёт отдельно и закрыто проверкой, либо не закрыто вовсе —
    /// а ошибиться тут дорого: лишний «да» отберёт у человека положенный им щит,
    /// лишний «нет» оставит бота со светлячком вместо фонаря.
    /// </summary>
    /// <param name="mine">В левой руке лежит то, что положил туда БОТ.</param>
    /// <param name="shines">Оно ещё светит (прогоревший факел — уже нет).</param>
    /// <param name="brighterCarried">
    /// Что в сумках светит ЯРЧЕ того, что в руке (null — ничего ярче нет).
    /// </param>
    /// <param name="putAwayWhenLight">Убирать ли свет, когда стало светло.</param>
    /// <param name="dark">
    /// Темно ли здесь И ПОЧЕМУ так решено (см. <see cref="IsDarkHere"/>).
    /// Причина приходит готовой нарочно: своих слов про темноту у этого правила
    /// нет и быть не должно — именно так и родилось «рассвело» в полдень.
    /// </param>
    /// <param name="shieldWants">
    /// ЩИТ ПРОСИТ ЭТУ ЖЕ РУКУ — и вот почему параметр здесь, а не отдельная
    /// проверка в бою. Слот левой руки ОДИН (11), желающих двое, и решать спор
    /// должен КТО-ТО ОДИН: реши его бой — он отбирал бы фонарь своей меркой, не
    /// зная ни про «моё ли это», ни про паузу от дребезга, ни про то, что
    /// прогоревший факел тоже надо убрать. Поэтому само решение живёт в одном
    /// правиле (<see cref="Shielding.WhoNeedsOffHand"/>), а СЮДА приходит уже
    /// готовым — как приходит и «темно ли».
    ///
    /// Пусто (обычный случай) — щит руки не просит, и всё как было.
    /// </param>
    public static string? WhyFreeOffHand(bool mine, bool shines, string? brighterCarried,
        bool putAwayWhenLight, DarkVerdict dark, string? shieldWants = null)
    {
        // ЧУЖОЕ НЕ ТРОГАЕМ ВОВСЕ. Щит, положенный человеком, — не наше дело,
        // и это первое условие, а не последнее: иначе «прогорел» отобрал бы
        // у человека его же погасший факел
        if (!mine)
            return null;
        // ЩИТ ВПЕРЕДИ ВСЕГО ОСТАЛЬНОГО, И ЭТО НЕ ПОРЯДОК РАДИ ПОРЯДКА. Пока
        // рука занята нашим же фонарём, щит в неё НЕ ПОЛОЖИТЬ: сервер такой
        // перенос отвергает молча, и бот стоял бы под дрифтером со «щитом в
        // сумке» и строкой «рядом опасно» в журнале. Освобождать руку обязан
        // тот, кто её занял, — то есть мы
        if (shieldWants is { Length: > 0 } зачемЩит)
            return зачемЩит;
        if (!shines)
            return "прогорел";
        // ЯРЧЕ В СУМКЕ — РУКУ ПОД НЕГО. Живой случай 16.08: в руке
        // gear-temporal (светит на 2 блока), в сумке фонарь (20), и старое
        // правило отвечало «уже светим» — фонарь ждал до самой ночи
        if (brighterCarried is { Length: > 0 })
            return $"в сумке есть ярче — {brighterCarried}";
        // «Не знаю, темно ли» — это НЕ «стало светло»: убирать по незнанию нельзя
        if (putAwayWhenLight && dark.Dark == false)
            return dark.Why;
        return null;
    }

    /// <summary>
    /// ТЕМНО ЛИ ЗДЕСЬ — чистое правило из трёх ответов, по убыванию точности,
    /// И У КАЖДОГО ПОРОГ С ЗАПАСОМ. Отдельной функцией затем, что порядок
    /// источников тут и есть весь смысл, а проверить его на живом сервере
    /// нельзя: свет чанка там устаревает.
    ///
    /// ПОЧЕМУ ЗАПАС НУЖЕН КАЖДОМУ ИЗ ТРЁХ. Живая жалоба 16.08 звучала как
    /// «дребезг фонаря»: полсотни пар «взял → убрал» подряд, медиана 0,9 с, и
    /// каждая пара — два переноса вещи на сервер. Ни один из трёх источников
    /// не застрахован от этого сам по себе:
    /// - свет чанка ходит у самой границы, пока бот идёт по штольне, которую
    ///   сам же и освещает факелами;
    /// - глубина под небом ходит у границы, пока бот бьёт наклонный ход вверх
    ///   («прошёл вверх 15 ступеней»): шаг под кровлю — пещера, шаг обратно —
    ///   уже нет;
    /// - час суток бот между пакетами досчитывает сам, а пакет правит его и
    ///   назад — на самом рассвете ответ успевает перевернуться.
    /// Поэтому у каждого источника ДВА порога, а не один, и между ними держится
    /// прежний ответ (<see cref="Hysteresis"/>).
    ///
    /// СРАВНИВАТЬ С ПОРОГАМИ УМЕЕТ ТОТ, КТО МЕРИТ. Свет и часы приходят сюда
    /// уже парой готовых ответов: <see cref="Lighting.IsDark"/> знает про ночь
    /// и про устаревшие цифры, <see cref="GameClock.NightWithin"/> — про длину
    /// суток этого мира. Своих копий этих сравнений тут нет. Глубина —
    /// исключение, и оно названо: сравнивать её больше некому, вся мерка
    /// «пещера ли» живёт здесь.
    /// </summary>
    /// <param name="darkByLight">
    /// Цифры света из чанка говорят «темно»: true/false — знаем точно, null —
    /// не знаем (чанк без слоя света либо свет устарел, см. Lighting).
    /// </param>
    /// <param name="stillDarkByLight">
    /// Они же по ОТПУЩЕННОМУ порогу: «света ещё не набралось столько, чтобы
    /// убирать фонарь». Ответ на тот же вопрос с другим числом.
    /// </param>
    /// <param name="depthUnderSky">
    /// На сколько блоков клетка ниже поверхности мира; null — карта колонки не
    /// приходила (<see cref="WorldModel.DepthUnderSky(BlockPos)"/>).
    /// </param>
    /// <param name="undergroundFrom">
    /// С какой глубины считаем, что бот УШЁЛ в пещеру. Ноль и меньше — «пещер
    /// не признаём», то есть подземелье этим правилом не рассматривается вовсе.
    /// </param>
    /// <param name="surfaceTo">
    /// До какой глубины надо подняться, чтобы считаться ВЫШЕДШИМ на поверхность.
    /// </param>
    /// <param name="night">Ночь ли по часам сервера; null — часы не пришли.</param>
    /// <param name="stillNight">Ночь ли по часам с запасом (сумерки раздвинуты).</param>
    /// <param name="was">Прежний ответ бота; null — ответа ещё не было.</param>
    public static DarkVerdict IsDarkHere(bool? darkByLight, bool? stillDarkByLight,
        int? depthUnderSky, int undergroundFrom, int surfaceTo,
        bool? night, bool? stillNight, bool? was)
    {
        // 1. Настоящие цифры света бьют всё: там, где они свежие, гадать не о чем
        if (darkByLight is { } поСвету)
        {
            bool темно = Hysteresis.Holds(поСвету, stillDarkByLight ?? поСвету, was);
            return new DarkVerdict(темно, темно ? "здесь темно" : "вышел на свет");
        }

        // 2. ПЕЩЕРА ТЕМНА КРУГЛЫЕ СУТКИ. Небо сюда не достаёт — время суток
        //    ничего не меняет, и это ответ, а не догадка: глубину прислал
        //    сервер. Обратное («мелко — значит светло») отсюда НЕ следует:
        //    наверху бывает ночь, и её разбирает третий источник
        if (undergroundFrom > 0 && depthUnderSky is { } глубина)
        {
            if (Hysteresis.Holds(глубина >= undergroundFrom, глубина > surfaceTo, was))
                return new DarkVerdict(true, $"ушёл под землю — {глубина} бл ниже поверхности");
        }

        // 3. Часы. Самый грубый ответ: под открытым небом днём светло, ночью нет
        if (night is { } поЧасам)
        {
            if (Hysteresis.Holds(поЧасам, stillNight ?? поЧасам, was))
                return new DarkVerdict(true, "на дворе ночь");
            // ПРИЧИНА НАЗЫВАЕТ И МЕСТО, А НЕ ОДНИ ЧАСЫ. Это тот самый полдень
            // 16.08: бот вышел из-под кровли своей норы, и «светло» тут значит
            // «небо сюда достаёт», а вовсе не «рассвело»
            return new DarkVerdict(false, undergroundFrom > 0 && depthUnderSky is { } d
                ? $"вышел на свет — {d} бл ниже поверхности, а на дворе день"
                : "на дворе день");
        }

        return new DarkVerdict(null, "темно ли — неизвестно: света чанка нет, " +
                                     "карта поверхности не приходила, часы не пришли");
    }

    /// <summary>
    /// Темно ли здесь и почему. Dark = null — НЕ ЗНАЕМ, и это не «светло»: свет
    /// чанка после первой же перестановки блоков устаревает (подробно — в
    /// Lighting), а часы могут ещё не прийти. Врать в такую сторону нельзя.
    /// </summary>
    private DarkVerdict Dark()
    {
        if (ctx.Self.Position is not { } p)
            return new DarkVerdict(null, "темно ли — неизвестно: где я, сервер ещё не сказал");

        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        // Оба могут быть не заведены вовсе (у бота они ставятся отдельно), а
        // часы — ещё и не пришедшими: спрашивать их тогда нельзя, но и молчать
        // об этом не надо — «не знаю» дальше скажется вслух
        Lighting? свет = ctx.Lighting;
        GameClock? часы = ctx.Clock is { Ready: true } c ? c : null;

        // ВЫВЕРНУТЫЕ ПОРОГИ ЗДЕСЬ НЕ ЛЕЧАТСЯ, И ЛЕЧИТЬ ИХ НЕ НАДО — это не
        // недосмотр, а разобранный случай. Поставьте порог отпускания ТУЖЕ
        // основного (убирать от 3, зажигать ниже 6) — и «ещё темно» станет
        // подмножеством «темно»: Holds(on, stillOn, was) вырождается в один
        // порог входа, ровно как было до запаса. Дребезг вернётся, но НИЧЕГО
        // не вывернется наизнанку. Мерку «второй порог не туже первого» держит
        // роль — там она видна человеку рядом с самим числом (см. LivingRole)
        return IsDarkHere(
            свет?.IsDark(here, DarkThreshold),
            свет?.IsDark(here, LightThreshold),
            TakeUnderground ? ctx.World.DepthUnderSky(here) : null,
            TakeUnderground ? UndergroundDepth : 0,
            SurfaceDepth,
            часы?.IsNight,
            часы?.NightWithin(DawnMarginHours),
            wasDark);
    }
}
