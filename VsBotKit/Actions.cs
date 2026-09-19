using Vintagestory.Common;

namespace VsBotKit;

/// <summary>
/// Действия бота в мире: атака, использование блоков (кровать, дверь, рычаг),
/// контейнеры (сундуки), перекладывание предметов, еда.
///
/// Как это устроено на протоколе:
/// - атака/взаимодействие с сущностью — EntityInteraction(17), MouseButton 0/1;
/// - «правый клик по блоку» — HandInteraction(25) c EnumHandInteractNw
///   StartBlockUse(4)/StopBlockUse(6), MouseButton обязателен 2;
/// - открыть сундук — BlockEntityPacket(22) c Packetid=1000: сервер сам
///   откроет инвентарь и пришлёт содержимое (InventoryContents), 1001 — закрыть;
/// - еда — HandInteraction(25) StartHeldItemUse(0) → пауза реального времени
///   (сервер меряет длительность по своим часам) → StopHeldItemUse(2);
/// - перенос предметов — MoveItemstack(8). LastChanged шлём максимold — бот
///   всегда синхронен с сервером, защита от рассинхрона ему не нужна.
/// </summary>
public class Actions
{
    private readonly BotClient bot;
    private readonly EntityModel entities;
    private readonly SelfState self;

    public Actions(BotClient bot, EntityModel entities, SelfState self)
    {
        this.bot = bot;
        this.entities = entities;
        this.self = self;
        // Отказы сервера приходят отдельным пакетом и в общий разбор не входят:
        // подписываемся тут, чтобы у действия был ответ на вопрос «а почему»
        bot.OnPacket += NoteServerRefusal;
        // НОВОЕ СОЕДИНЕНИЕ — РУКИ ПУСТЫ. Прежняя сессия могла оборваться прямо
        // посреди удара: у сервера тот игрок уже отключён, а у нас остались
        // живые удержания, и первая же работа в новом мире нажала бы кнопку
        // поверх них. Заодно гаснут отказы: они были про прошлый мир
        bot.OnSessionReset += ForgetSession;
    }

    /// <summary>Отказ действию — словами. Роль подписывает сюда свой журнал.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// МЁРТВЫМ РУКИ НЕ РАБОТАЮТ — ОДНА ДВЕРЬ НА ВСЕ ДЕЙСТВИЯ РУКАМИ.
    ///
    /// Разбор живого случая и код сервера — в <see cref="SelfState.BodyIsDead"/>;
    /// здесь только исполнение. Стоит правило ИМЕННО ЗДЕСЬ, потому что через
    /// этот класс уходят все пакеты рук: клик по блоку (25), слом и установка
    /// (3), удар и взаимодействие с сущностью (17). Ставить проверку у каждого,
    /// кто их зовёт, значило бы завести десяток копий и забыть её в первом же
    /// новом деле — а забыл её в прошлый раз сам путь домой.
    ///
    /// КЛАВИШИ СЮДА НЕ ВХОДЯТ НАРОЧНО: «отпустил» после смерти обязан уйти,
    /// иначе бот остаётся лежать с зажатой кнопкой удара (см. HoldKeyAsync).
    ///
    /// Молчать нельзя: без строки в журнале «бот стоит и ничего не делает»
    /// и «бот шлёт впустую» выглядят одинаково — ровно так и выглядели десять
    /// отказов сервера подряд.
    /// </summary>
    /// <returns>Правда — тело лежит, действие не отправлено.</returns>
    private bool DeadHands(string what)
    {
        if (!self.Dead)
            return false;
        OnLog?.Invoke($"{what}: я мёртв — сервер мёртвому отказывает во всём " +
                      "(noprivilege-…-playerdead); жду возрождения");
        return true;
    }

    private void ForgetSession() => _ = Task.Run(async () =>
    {
        lock (refusalGate)
            refusal = null;
        try { await ReleaseAllHeldAsync(); }
        catch { /* соединения уже нет — гасить состояние всё равно надо */ }
    });

    // ---------------- клавиши, которые бот ДЕРЖИТ ----------------

    /// <summary>Левая кнопка мыши: удар и копание (EnumEntityAction.LeftMouseDown).</summary>
    public const int KeyLeftMouse = 9;

    /// <summary>Правая кнопка мыши: применение (EnumEntityAction.RightMouseDown).</summary>
    public const int KeyRightMouse = 10;

    /// <summary>Приседание (EnumEntityAction.Sneak).</summary>
    public const int KeySneak = 5;

    /// <summary>Клавиша Shift (EnumEntityAction.ShiftKey).</summary>
    public const int KeyShift = 14;

    /// <summary>Живые удержания: по ним отпускается всё разом при потере тела.</summary>
    private readonly List<KeyHold> holds = [];

    /// <summary>
    /// ЗАЖАТЬ КЛАВИШУ ТАК, ЧТОБЫ ОНА ОТПУСТИЛАСЬ САМА.
    ///
    /// ЖИВОЙ СЛУЧАЙ ЗАКАЗЧИКА (прогон 15.08): «баг анимации, во время копания
    /// захотел есть и при поедании хлеба была анимация копания». На сервере
    /// клавиши игрока — ЭТО СОСТОЯНИЕ, а не событие: ServerMain
    /// .HandleMoveKeyChange кладёт наше «нажал» прямо в Controls игрока и
    /// рассылает их окружающим. Пока не придёт «отпустил», для всего мира бот
    /// продолжает махать киркой — чем бы он на самом деле ни занимался.
    ///
    /// А удар держится СЕКУНДАМИ, и ровно в эти секунды тело забирает рефлекс:
    /// поесть, полечиться, отбиться. Поэтому «отпустить в конце удачного пути»
    /// мало — отпускать надо В ТОТ ЖЕ МИГ, когда тело перестало быть нашим.
    /// Здесь это сделано подпиской на токен владения: погас токен — ушёл пакет
    /// «отпустил», и никакой код после этого клавишу уже не нажмёт
    /// (<see cref="KeyHold.Over"/>).
    /// </summary>
    /// <param name="key">Индекс EnumEntityAction (см. константы Key*).</param>
    /// <param name="ct">Токен владения телом: погас — клавиша отпущена.</param>
    /// <param name="say">Куда говорить о сбоях отпускания (null — молча).</param>
    public async Task<KeyHold> HoldKeyAsync(int key, CancellationToken ct = default,
        Action<string>? say = null)
    {
        var hold = new KeyHold(this, key, ct, say);
        lock (holds)
        {
            // Погасшие не копим: список живёт всю жизнь бота, а за смену их
            // набегают тысячи — по одному на каждый сломанный блок
            holds.RemoveAll(h => h.Over);
            holds.Add(hold);
        }
        await hold.PressAsync();
        return hold;
    }

    /// <summary>
    /// Отпустить клавишу, кто бы её ни держал, и запретить дальнейшие нажатия
    /// этого удержания.
    ///
    /// Нужно тому, кто начинает несовместимое дело: живой игрок не может
    /// одновременно долбить киркой и жевать хлеб, и клиент игры такой пары
    /// нажатий не шлёт. Пакет «отпустил» безвреден и когда никто не держал:
    /// сервер сверяет клавиши с прежними и на «ничего не изменилось» не делает
    /// ничего (ServerMain.HandleMoveKeyChange).
    /// </summary>
    /// <returns>Сколько зажатых удержаний этой клавиши пришлось погасить.</returns>
    public async Task<int> ReleaseKeyAsync(int key)
    {
        KeyHold[] mine;
        lock (holds)
            mine = [.. holds.Where(h => h.Key == key)];
        int held = 0;
        foreach (var hold in mine)
        {
            if (hold.Down)
                held++;
            await hold.LetGoAsync();
        }
        await SendMoveKeyAsync(key, down: false);
        return held;
    }

    /// <summary>
    /// Отпустить ВСЁ, что бот держал: удар, применение, приседание. Зовётся,
    /// когда тело перестало быть телом — смерть, обрыв связи, возвращение в мир.
    /// </summary>
    public async Task<int> ReleaseAllHeldAsync()
    {
        KeyHold[] all;
        lock (holds)
            all = [.. holds];
        int held = 0;
        foreach (var hold in all)
        {
            if (hold.Down)
                held++;
            await hold.LetGoAsync();
        }
        return held;
    }

    /// <summary>Сколько клавиш бот держит прямо сейчас (для окна управления и тестов).</summary>
    public int HeldKeys
    {
        get { lock (holds) return holds.Count(h => h.Down); }
    }

    internal void Forget(KeyHold hold)
    {
        lock (holds)
            holds.Remove(hold);
    }

    /// <summary>
    /// ЗАЖАТАЯ КЛАВИША КАК ВЕЩЬ, КОТОРУЮ НАДО ВЕРНУТЬ.
    ///
    /// Всё удержание живёт здесь целиком: и нажатие, и отпускание, и запрет
    /// нажимать снова после потери тела. Раньше эти три вещи были раскиданы по
    /// вызывающему — и он же отвечал за то, чтобы не забыть последнюю.
    /// </summary>
    public sealed class KeyHold : IAsyncDisposable
    {
        private readonly Actions actions;
        private readonly Action<string>? say;
        private readonly CancellationTokenRegistration link;
        /// <summary>Очередь на отправку: «нажал» и «отпустил» не должны разъехаться.</summary>
        private readonly SemaphoreSlim turn = new(1, 1);
        private volatile bool down;
        private volatile bool over;

        internal KeyHold(Actions actions, int key, CancellationToken ct, Action<string>? say)
        {
            this.actions = actions;
            this.say = say;
            Key = key;
            // Отмена приходит из чужого потока — и это правильно: тело забирают
            // не спрашивая. Уже погасший токен зовёт обработчик прямо здесь,
            // и удержание рождается мёртвым — то есть клавиша не нажмётся вовсе
            link = ct.CanBeCanceled
                ? ct.Register(static s => ((KeyHold)s!).LetGoFromOutside(), this)
                : default;
        }

        /// <summary>Индекс клавиши (EnumEntityAction).</summary>
        public int Key { get; }

        /// <summary>Клавиша сейчас зажата.</summary>
        public bool Down => down;

        /// <summary>
        /// Удержание кончилось насовсем: тело потеряно или дело закрыто.
        /// После этого <see cref="PressAsync"/> НИЧЕГО не шлёт — иначе перебитая
        /// работа успевала бы нажать клавишу заново поверх чужого дела.
        /// </summary>
        public bool Over => over;

        /// <summary>Нажать (или подтвердить нажатие). Ложь — тело уже не наше.</summary>
        public async Task<bool> PressAsync()
        {
            await turn.WaitAsync();
            try
            {
                if (over || down)
                    return !over;
                down = true;
                await actions.SendMoveKeyAsync(Key, down: true);
                return true;
            }
            finally { turn.Release(); }
        }

        /// <summary>Отпустить, но удержание оставить живым (можно нажать снова).</summary>
        public async Task ReleaseAsync()
        {
            await turn.WaitAsync();
            try
            {
                if (!down)
                    return;
                down = false;
                await actions.SendMoveKeyAsync(Key, down: false);
            }
            finally { turn.Release(); }
        }

        /// <summary>Отпустить НАСОВСЕМ: клавиша вверх, нажать её больше нельзя.</summary>
        public async Task LetGoAsync()
        {
            await turn.WaitAsync();
            try
            {
                bool wasDown = down;
                over = true;
                down = false;
                if (wasDown)
                    await actions.SendMoveKeyAsync(Key, down: false);
            }
            finally { turn.Release(); }
        }

        /// <summary>
        /// Тело забрали.
        ///
        /// ЗАПРЕТ НА НАЖАТИЕ СТАВИМ ПРЯМО ЗДЕСЬ, не откладывая: отмена приходит
        /// из чужого потока и запросто обгоняет наше собственное «нажать» —
        /// а нажатие после потери тела и есть та самая залипшая анимация.
        /// Отправка пакета уже может идти вдогонку: она встанет в очередь за
        /// нажатием и придёт следом, в правильном порядке.
        /// </summary>
        private void LetGoFromOutside()
        {
            over = true;
            _ = Task.Run(async () =>
            {
                try { await LetGoAsync(); }
                catch (Exception ex)
                {
                    say?.Invoke($"клавишу {Key} отпустить не вышло: {ex.GetType().Name} {ex.Message}");
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            await LetGoAsync();
            link.Dispose();
            actions.Forget(this);
            // Очередь НЕ закрываем нарочно: отмена могла уйти в свой поток и
            // ещё стоять в ней с последним «отпустил». Закрытая очередь уронила
            // бы его исключением — то есть съела бы то самое отпускание
        }
    }

    /// <summary>
    /// Ударить сущность (предметом в активном слоте хотбара).
    ///
    /// Пакет урона (17) отвечает ТОЛЬКО за урон — анимацию замаха сервер
    /// запускает сам, увидев у игрока зажатую левую кнопку. Поэтому вокруг
    /// удара шлём нажатие и отпускание клавиши (EnumEntityAction.LeftMouseDown
    /// = 9) и держим её дольше одного серверного цикла: пара «нажал-отпустил»
    /// в одном пакетном заходе применится между тиками, и замаха не будет.
    ///
    /// Удержание — через <see cref="HoldKeyAsync"/>, и это не украшение: без
    /// него оборванный посреди замаха удар (обрыв связи, смерть, перехват тела)
    /// оставлял кнопку зажатой навсегда — бот «махал» мечом, стоя столбом.
    /// </summary>
    public async Task AttackEntityAsync(long entityId, CancellationToken ct = default)
    {
        if (DeadHands("ударить не могу"))
            return;
        await using var swing = await HoldKeyAsync(KeyLeftMouse, ct);
        // Тело уже не наше — бить нечем. Урон без замаха сервер бы засчитал, и
        // это был бы удар «из ниоткуда»: бот дерётся, пока его уже уводят
        if (swing.Over)
            return;
        await Task.Delay(90, ct).ContinueWith(_ => { });
        await InteractEntityAsync(entityId, mouseButton: 0);
        await Task.Delay(90, ct).ContinueWith(_ => { });
    }

    /// <summary>Нажать/отпустить клавишу (индекс EnumEntityAction, 0..16).</summary>
    internal Task SendMoveKeyAsync(int key, bool down) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 21,
            MoveKeyChange = new Packet_MoveKeyChange { Key = key, Down = down ? 1 : 0 }
        });

    // ---------------- отказ сервера его же словами ----------------

    /// <summary>
    /// ОТКАЗ СЕРВЕРА, СКАЗАННЫЙ ИМ САМИМ (пакет 68 IngameError).
    ///
    /// Ради одного: отличить «не дали строить» от «ответа не было». Сервер на
    /// чужой заявке отвечает не молчанием, а ошибкой с кодом
    /// noprivilege-buildbreak-… и именем хозяина в LangParams
    /// (ServerSystemBlockSimulation.HandleBlockPlaceOrBreak), тогда как лаг сети
    /// не приносит ничего. Повторять первое бессмысленно, второе — обязательно.
    /// </summary>
    public readonly record struct ServerRefusal(string Code, string Message, string Who, DateTime At)
    {
        /// <summary>Отказ именно в праве строить и ломать: чужая заявка, чужой дом.</summary>
        public bool NoBuildRights =>
            Code.StartsWith("noprivilege-buildbreak", StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            Who is { Length: > 0 } who ? $"{Code} (хозяин {who})" : Code;
    }

    private ServerRefusal? refusal;
    private readonly object refusalGate = new();

    /// <summary>Последний отказ сервера (null — сервер ни на что не жаловался).</summary>
    public ServerRefusal? LastRefusal
    {
        get { lock (refusalGate) return refusal; }
    }

    /// <summary>Отказ сервера ПОСЛЕ указанного мига — то есть ответ на наше действие.</summary>
    public ServerRefusal? RefusalSince(DateTime since)
    {
        lock (refusalGate)
            return refusal is { } r && r.At >= since ? r : null;
    }

    private void NoteServerRefusal(Packet_Server packet)
    {
        if (packet.Id != Packet_ServerIdEnum.IngameError || packet.IngameError is not { } error)
            return;
        // Имя хозяина заявки сервер кладёт в параметры перевода. Массив бывает
        // длиннее заполненного — берём ровно столько, сколько он насчитал сам
        string who = error.GetLangParamsCount() > 0 && error.GetLangParams() is { Length: > 0 } args
            ? args[0] ?? ""
            : "";
        lock (refusalGate)
            refusal = new ServerRefusal(error.Code ?? "", error.Message ?? "", who, DateTime.UtcNow);
    }

    /// <summary>Взаимодействовать с сущностью (правый клик).</summary>
    public Task UseEntityAsync(long entityId) =>
        DeadHands("к сущности не притронусь")
            ? Task.CompletedTask
            : InteractEntityAsync(entityId, mouseButton: 1);

    private Task InteractEntityAsync(long entityId, int mouseButton) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 17,
            EntityInteraction = new Packet_EntityInteraction
            {
                EntityId = entityId,
                MouseButton = mouseButton,
                // попадание в центр туловища; внимание: тут «обычная» точность
                HitX = CollectibleNet.SerializeDouble(0),
                HitY = CollectibleNet.SerializeDouble(0.8),
                HitZ = CollectibleNet.SerializeDouble(0),
                OnBlockFace = 0,
                SelectionBoxIndex = 0
            }
        });

    /// <summary>
    /// «Правый клик» по блоку: кровать, дверь, рычаг, люк, сундук...
    /// Бот должен стоять достаточно близко (дальность руки ~4.5 блока).
    ///
    /// ЗДЕСЬ ЖЕ ЗАСЧИТЫВАЕТСЯ «Я ЗАГЛЯНУЛ ВНУТРЬ». Клик по контейнеру — это и
    /// есть его открытие: у человека в этот миг появляется окно с содержимым.
    /// Отметка нужна затем, что содержимое сундуков сервер шлёт ВМЕСТЕ С
    /// ЧАНКОМ, и без неё бот пересчитывал чужие закрытые сундуки за глухой
    /// стеной, ни разу к ним не подойдя (см. WorldModel.MayLookInside).
    ///
    /// Место одно на все открытия нарочно: и сундук, и костёр, и осмотр
    /// табличкой идут через этот клик. Заводить отметку в каждом открывающем
    /// значило бы забыть её в первом же новом. Клик по двери или рычагу
    /// отмечает точку, в которой контейнера нет, — и это никому не мешает.
    /// </summary>
    public async Task UseBlockAsync(BlockPos pos)
    {
        // Отметку «заглянул внутрь» тоже НЕ ставим: мёртвому сервер окно не
        // откроет, и запомнить чужое содержимое было бы враньём самому себе
        if (DeadHands($"по {pos} не кликну"))
            return;
        self.NoteLookedInside(pos);
        await bot.SendPacketAsync(BuildBlockUse(pos, 4)); // StartBlockUse
        await bot.SendPacketAsync(BuildBlockUse(pos, 6)); // StopBlockUse
    }

    private static Packet_Client BuildBlockUse(BlockPos pos, int stage) =>
        new()
        {
            Id = 25,
            HandInteraction = new Packet_ClientHandInteraction
            {
                UseType = 2,     // HeldItemInteract — обязателен ненулевой
                MouseButton = 2, // правая кнопка — сервер требует именно её
                EnumHandInteract = stage,
                X = pos.X,
                Y = pos.Y,
                Z = pos.Z,
                OnBlockFace = 4, // верхняя грань
                HitX = CollectibleNet.SerializeDoublePrecise(0.5),
                HitY = CollectibleNet.SerializeDoublePrecise(0.5),
                HitZ = CollectibleNet.SerializeDoublePrecise(0.5),
                UsingCount = 1,
                FirstEvent = stage == 4 ? 1 : 0
            }
        };

    /// <summary>
    /// Id инвентаря контейнера для операций MoveItemstack.
    /// Формат: "&lt;класс&gt;-x, y, z" (напр. "chest-512, 110, 512").
    /// Класс по умолчанию "chest"; для корзин/бочек передайте свой.
    /// </summary>
    public static string ContainerInventoryId(BlockPos pos, string className = "chest") =>
        $"{className}-{pos.X}, {pos.Y}, {pos.Z}";

    /// <summary>
    /// Открыть контейнер (сундук) правым кликом. Это заставляет сервер
    /// зарегистрировать инвентарь И прислать его содержимое пакетом 5000
    /// (см. SelfState). Бот должен стоять в пределах руки (~4.5 блока).
    /// Содержимое появится в SelfState.Inventories под id "&lt;класс&gt;-x, y, z".
    /// </summary>
    public Task OpenContainerAsync(BlockPos pos) => UseBlockAsync(pos);

    /// <summary>
    /// Открыть инвентарь блока ПО-НАСТОЯЩЕМУ: клик, затем регистрация
    /// инвентаря у сервера.
    ///
    /// Одного клика мало. У настоящего клиента открытие окна — это ТРИ шага
    /// (BlockEntityOpenableContainer.toggleInventoryDialogClient):
    ///   клик по блоку → Inventory.Open(player) → BlockEntityPacket(1000).
    /// Средний шаг и есть регистрация: сервер кладёт инвентарь в
    /// InventoryManager игрока, и только после этого MoveItemstack оттуда
    /// вообще возможен. Без него сервер молча отвергает перенос — так готовое
    /// мясо и осталось лежать в костре, хотя «положить» работало.
    ///
    /// Читать содержимое можно и без этого: оно приходит с чанком. Открытие
    /// нужно, когда собираемся ЗАБИРАТЬ.
    /// </summary>
    public async Task OpenBlockInventoryAsync(BlockPos pos, string inventoryId)
    {
        await UseBlockAsync(pos);
        await bot.SendPacketAsync(new Packet_Client
        {
            Id = 30,
            InvOpenedClosed = new Packet_InvOpenClose { InventoryId = inventoryId, Opened = 1 }
        });
        await bot.SendPacketAsync(new Packet_Client
        {
            Id = 22,
            BlockEntityPacket = new Packet_BlockEntityPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Packetid = 1000 // EnumBlockEntityPacketId.Open
            }
        });
    }

    /// <summary>
    /// Отправить операцию с инвентарём ВНУТРЬ СУЩЕСТВА.
    ///
    /// Инвентарь туши, торговца или сундука-в-телеге не ходит по общему
    /// инвентарному каналу: он живёт при сущности, и операции с ним клиент
    /// заворачивает в EntityPacket (пакет 31) — id операции плюс сериализованный
    /// обычный клиентский пакет внутри
    /// (ClientMain.SendEntityPacket → EntityBehavior.OnReceivedClientPacket →
    /// inv.InvNetworkUtil.HandleClientPacket). Отправить MoveItemstack напрямую
    /// нельзя: сервер такого инвентаря в списке игрока не найдёт.
    /// </summary>
    public Task SendEntityInventoryPacketAsync(long entityId, Packet_Client inner) =>
        bot.SendPacketAsync(EntityInventoryPacket(entityId, inner));

    /// <summary>
    /// ТОТ ЖЕ ПАКЕТ, НО СОБРАННЫЙ, А НЕ ОТПРАВЛЕННЫЙ, — чтобы его можно было
    /// СПРОСИТЬ.
    ///
    /// Зачем это отдельно. Отправка требует сокета, и на стенде
    /// <see cref="BotClient.SendPacketAsync"/> честно уходит в «нет соединения»
    /// ещё до того, как поднимет событие об отправке: значит проверить
    /// исходящий пакет через отправку НЕЛЬЗЯ вовсе. А проверить его надо: в
    /// журнале фактов проекта до сих пор стоит «добыча из туши не забирается —
    /// перенос сервер молча не принимает», и разъехавшийся адрес или обёртка
    /// выглядели бы точь-в-точь так же. Тем же приёмом (сборка отдельно от
    /// отправки) давно живёт обмен слотами — <see cref="Hands.FlipPacket"/>.
    /// Второй копии сборки при этом нет: отправка зовёт именно эту.
    /// </summary>
    public static Packet_Client EntityInventoryPacket(long entityId, Packet_Client inner)
    {
        var wrapper = new Packet_EntityPacket
        {
            EntityId = entityId,
            Packetid = inner.Id
        };
        wrapper.SetData(Packet_ClientSerializer.SerializeToBytes(inner));
        return new Packet_Client { Id = 31, EntityPacket = wrapper };
    }

    /// <summary>
    /// Пакет переноса ВНУТРИ СУЩНОСТИ — собранный, но не отправленный
    /// (см. <see cref="EntityInventoryPacket"/>). Отправка
    /// (<see cref="MoveItemFromEntityAsync"/>) зовёт его же.
    /// </summary>
    public static Packet_Client EntityMovePacket(long entityId, string fromInventoryId,
        int fromSlot, string toInventoryId, int toSlot, int quantity) =>
        EntityInventoryPacket(entityId, new Packet_Client
        {
            Id = 8,
            MoveItemstack = new Packet_MoveItemstack
            {
                SourceInventoryId = fromInventoryId,
                SourceSlot = fromSlot,
                TargetInventoryId = toInventoryId,
                TargetSlot = toSlot,
                Quantity = quantity,
                MouseButton = 0,
                Modifiers = 0,
                Priority = 1,
                SourceLastChanged = long.MaxValue,
                TargetLastChanged = long.MaxValue
            }
        });

    /// <summary>
    /// Сказать серверу «я открыл этот инвентарь» по его id. Сам по себе клик
    /// регистрирует инвентарь не всегда — клиент шлёт ещё и это.
    /// </summary>
    public Task OpenInventoryByIdAsync(string inventoryId) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 30,
            InvOpenedClosed = new Packet_InvOpenClose { InventoryId = inventoryId, Opened = 1 }
        });

    /// <summary>
    /// Сказать серверу «я закрыл этот инвентарь» по его id — ЗЕРКАЛО
    /// <see cref="OpenInventoryByIdAsync"/> и, для инвентаря при СУЩНОСТИ, весь
    /// пакет целиком (у блока к нему добавляется ещё и BlockEntityPacket,
    /// см. <see cref="CloseBlockInventoryAsync"/>).
    ///
    /// ЭТО НЕ ВЕЖЛИВОСТЬ, А ЗАВЕРШАЮЩИЙ ХОД. Разобрано по сборкам 1.22.7:
    /// клиент на закрытии окна зовёт
    /// <c>InventoryManager.CloseInventoryAndSync(inv)</c>, та шлёт ровно этот
    /// пакет (<c>InventoryNetworkUtil.DidClose</c> — id 30, Opened = 0), сервер
    /// принимает его в <c>ServerSystemInventory.HandleInvOpenClose</c> и зовёт
    /// <c>inv.Close(player)</c>, а тот поднимает событие
    /// <c>OnInventoryClosed</c>. И вот на это событие подписана разделка:
    /// <c>EntityBehaviorHarvestable.Inv_OnInventoryClosed</c> — «пусто и есть
    /// поведение распада» → <c>EntityBehaviorDeadDecay.DecayNow()</c>, туша
    /// исчезает С ЧАСТИЦАМИ ТУТ ЖЕ.
    ///
    /// Без этого пакета обобранная дочиста туша лежит до естественного распада,
    /// и заказчик 20.08 увидел разницу своими глазами: «когда игрок разделывает
    /// и если туша пустая, то она исчезает, а после разделки бота никто не
    /// исчезает». Живой игрок закрывает окно всегда — значит и бот обязан
    /// (правило 5).
    /// </summary>
    public Task CloseInventoryByIdAsync(string inventoryId) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 30,
            InvOpenedClosed = new Packet_InvOpenClose { InventoryId = inventoryId, Opened = 0 }
        });

    /// <summary>Переложить предметы внутри инвентаря сущности (туша, торговец).</summary>
    public Task MoveItemFromEntityAsync(long entityId, string fromInventoryId, int fromSlot,
        string toInventoryId, int toSlot, int quantity) =>
        bot.SendPacketAsync(EntityMovePacket(entityId, fromInventoryId, fromSlot,
            toInventoryId, toSlot, quantity));

    /// <summary>
    /// Закрыть инвентарь блока по его id (зеркало открытия).
    ///
    /// Сам пакет «я закрыл» СОБИРАЕТСЯ НЕ ЗДЕСЬ, а в
    /// <see cref="CloseInventoryByIdAsync"/> — он один на все инвентари, и
    /// второй его копии в этом файле быть не должно. У блока к нему добавляется
    /// ещё один, свой: блочная сущность ведёт СВОЙ счёт открывших (сундук
    /// закрывает крышку и глушит звук именно по нему).
    /// </summary>
    public async Task CloseBlockInventoryAsync(BlockPos pos, string inventoryId)
    {
        await CloseInventoryByIdAsync(inventoryId);
        await bot.SendPacketAsync(new Packet_Client
        {
            Id = 22,
            BlockEntityPacket = new Packet_BlockEntityPacket
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Packetid = 1001 // EnumBlockEntityPacketId.Close
            }
        });
    }

    /// <summary>Закрыть ранее открытый контейнер.</summary>
    public Task CloseContainerAsync(BlockPos pos, string className = "chest") =>
        CloseBlockInventoryAsync(pos, ContainerInventoryId(pos, className));

    /// <summary>
    /// Переложить предметы между слотами любых доступных инвентарей
    /// (свой хотбар/рюкзак, открытый сундук).
    /// </summary>
    /// <summary>
    /// ВЕЩЬ ПЕРЕЛОЖЕНА: откуда, куда и сколько просили.
    ///
    /// ЕДИНСТВЕННАЯ ДВЕРЬ, ЧЕРЕЗ КОТОРУЮ ВЕЩИ ВООБЩЕ ХОДЯТ между описями, — и
    /// потому подписываться надо ровно на неё. Живой разбор приёмки нашёл, что
    /// «сумка худеет не в расход» зачитывалось ДВУМЯ вызовами руками
    /// (укладка в хранилище и выброс), а дверей на деле СЕМЬ: ещё «!брось» и
    /// кнопка окна, перекладка в чужой сундук, выкладка товара на прилавок
    /// лавочником, закладка в костёр и материал в сетку крафта. Каждая
    /// незакрытая читалась как расход, и бот шёл делать ещё стопку того, что
    /// сам же и сдал. Заводить по вызову на дверь — значит однажды забыть
    /// восьмую; здесь дверь одна и забыть её нельзя.
    /// </summary>
    /// <param>Откуда (опись и слот), куда (опись) и сколько штук просили.</param>
    public event Action<string, int, string, int>? OnItemMoved;

    public Task MoveItemAsync(string fromInventoryId, int fromSlot,
        string toInventoryId, int toSlot, int quantity)
    {
        // ГОВОРИМ ДО ОТПРАВКИ: слот источника сейчас ещё полон, и по нему
        // подписчик узнает КОД вещи. После ответа сервера там будет пусто
        OnItemMoved?.Invoke(fromInventoryId, fromSlot, toInventoryId, quantity);
        return MoveItemPacketAsync(fromInventoryId, fromSlot, toInventoryId, toSlot, quantity);
    }

    private Task MoveItemPacketAsync(string fromInventoryId, int fromSlot,
        string toInventoryId, int toSlot, int quantity) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 8,
            MoveItemstack = new Packet_MoveItemstack
            {
                SourceInventoryId = fromInventoryId,
                SourceSlot = fromSlot,
                TargetInventoryId = toInventoryId,
                TargetSlot = toSlot,
                Quantity = quantity,
                MouseButton = 0,
                Modifiers = 0,
                Priority = 1, // проверено: с этим значением сервер переносит, с 2 отвергает всё
                SourceLastChanged = long.MaxValue,
                TargetLastChanged = long.MaxValue
            }
        });

    /// <summary>
    /// Присесть/встать. Нужно не только для скрытности: часть блоков
    /// (лестницы, некоторые механизмы) ставится только в присяде —
    /// сервер иначе отвечает отказом «sneaktoplace».
    ///
    /// Шлём два действия: Sneak (приседание тела) и ShiftKey (сама клавиша) —
    /// в игре это РАЗНЫЕ флаги, и проверки блоков смотрят именно на ShiftKey.
    ///
    /// ЭТО НЕ ТО ЖЕ, ЧТО <see cref="Body.HoldCrouch"/>, И ПУТАТЬ ИХ НЕЛЬЗЯ.
    /// Здесь — ФЛАГ ДЛЯ СЕРВЕРА на один клик: «я приседаю, ставь лестницу, а не
    /// открывай диалог». Там — ПОЗА ТЕЛА: коробка укорачивается до 1,48, тело
    /// не сходит с кромки, шаг втрое медленнее, и держится это тиками физики.
    /// Первое живёт в руках (костёр, разделка, лестницы), второе — в ходьбе
    /// (низкий проход, мост над пропастью).
    ///
    /// ЧТО ОСТАЛОСЬ НЕ СВЕДЁННЫМ, И ЭТО НАДО ЗНАТЬ: клавиша Sneak (номер 5)
    /// уходит серверу ИЗ ДВУХ МЕСТ — отсюда напрямую и из тела, которое шлёт
    /// изменения своих клавиш. Пока их не зовут одновременно (руки приседают
    /// стоя на месте, ходьба — на ходу), расхождения нет; позовут вместе —
    /// сервер получит «отпустил» от рук, а тело об этом не узнает и повторно
    /// не нажмёт, потому что для него ничего не изменилось.
    /// </summary>
    public async Task SetSneakAsync(bool on)
    {
        foreach (int key in new[] { KeySneak, KeyShift })
            await SendMoveKeyAsync(key, on);
    }

    /// <summary>
    /// Поставить блок из активного слота хотбара в указанную (пустую) клетку.
    /// Сервер берёт блок из активного слота — сначала SelectHotbarSlotAsync.
    /// </summary>
    /// <param name="onFace">
    /// Грань, «по которой кликнули»: 0=север,1=восток,2=юг,3=запад,4=верх,5=низ.
    /// Для лестниц и других настенных блоков важна — от неё зависит поворот.
    /// </param>
    public Task PlaceBlockAsync(BlockPos pos, int onFace = 4) =>
        DeadHands($"блок в {pos} не поставлю")
        ? Task.CompletedTask
        : bot.SendPacketAsync(new Packet_Client
        {
            Id = 3,
            BlockPlaceOrBreak = new Packet_ClientBlockPlaceOrBreak
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Mode = 1, // поставить
                OnBlockFace = onFace,
                HitX = CollectibleNet.SerializeDouble(0.5),
                HitY = CollectibleNet.SerializeDouble(0.5),
                HitZ = CollectibleNet.SerializeDouble(0.5),
                DidOffset = 1
            }
        });

    /// <summary>Сломать блок.</summary>
    public Task BreakBlockAsync(BlockPos pos) =>
        DeadHands($"{pos} не сломаю")
        ? Task.CompletedTask
        : bot.SendPacketAsync(new Packet_Client
        {
            Id = 3,
            BlockPlaceOrBreak = new Packet_ClientBlockPlaceOrBreak
            {
                X = pos.X, Y = pos.Y, Z = pos.Z,
                Mode = 0, // сломать
                OnBlockFace = 4,
                HitX = CollectibleNet.SerializeDouble(0.5),
                HitY = CollectibleNet.SerializeDouble(0.5),
                HitZ = CollectibleNet.SerializeDouble(0.5)
            }
        });

    /// <summary>Выбрать активный слот хотбара (0..9).</summary>
    public Task SelectHotbarSlotAsync(int slot) =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 13,
            SelectedHotbarSlot = new Packet_SelectedHotbarSlot { SlotNumber = slot }
        });

    // ВНИМАНИЕ: здесь БЫЛ метод UseItemAsync(inventoryId, slot, seconds).
    // Он делал сразу два запрещённых правилом 2 дела: заполнял поле пакета
    // InventoryId, которого настоящий клиент не шлёт НИКОГДА (то есть применял
    // еду и бинты прямо из рюкзака, не беря их в руку), и слал выдуманный
    // UsingCount = 200 — сервер по нему прокручивал около четырёх секунд
    // использования сверх реально отдержанного.
    //
    // Замена — Hands.UseHeldAsync / Hands.UseItemAsync: предмет сначала
    // переезжает в хотбар и берётся в руку, кнопка держится настоящее время,
    // число шагов равно числу настоящих тиков тела.
}
