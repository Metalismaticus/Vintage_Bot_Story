using System.Collections.Concurrent;
using System.Reflection;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;

namespace VsBotKit;

/// <summary>
/// Лечебные свойства предмета (бинты, компрессы). Лечат постепенно:
/// Health хп растягиваются на EffectSeconds.
/// </summary>
/// <param name="Health">Сколько хп восстановит всего</param>
/// <param name="ApplySeconds">Сколько держать предмет, чтобы применить</param>
/// <param name="EffectSeconds">За сколько секунд подействует</param>
/// <param name="CancelInAir">Нельзя применять в прыжке/падении</param>
/// <param name="CancelWhileSwimming">Нельзя применять в воде</param>
public sealed record HealingInfo(
    float Health,
    float ApplySeconds,
    float EffectSeconds,
    bool CancelInAir,
    bool CancelWhileSwimming);

/// <summary>Блок-сущность (табличка, сундук, кровать...) с её данными.</summary>
public sealed class BlockEntityInfo
{
    public required string ClassName { get; init; }
    public required BlockPos Pos { get; init; }
    internal byte[]? RawData { get; init; }

    private ITreeAttribute? tree;

    /// <summary>Атрибуты блок-сущности (текст таблички и т.п.), лениво.</summary>
    public ITreeAttribute? Attributes
    {
        get
        {
            if (tree == null && RawData is { Length: > 0 })
            {
                var t = new TreeAttribute();
                using var ms = new MemoryStream(RawData);
                using var reader = new BinaryReader(ms);
                t.FromBytes(reader);
                tree = t;
            }
            return tree;
        }
    }

    /// <summary>Текст таблички (для Classname == "Sign"), иначе null.</summary>
    public string? SignText => Attributes?.GetString("text");

    // ---- форма тёсаного блока (см. MicroBlocks) ----
    //
    // Разбор кэшируется ПРИ САМОЙ СУЩНОСТИ, а не в общей карте по координатам,
    // потому что сервер присылает изменённую сущность целиком: новый объект —
    // новая форма, и старый разбор выбрасывать не надо, он умирает вместе со
    // своим объектом. Иначе кто-нибудь однажды забудет чистить кэш при
    // стёсывании, и бот будет ходить по форме, которой уже нет.
    private uint[]? cuboids;
    private VsBotKit.MicroBlockShape? shape;

    /// <summary>Коробки тёсаного блока (упакованные, как их шлёт игра).</summary>
    internal uint[] MicroCuboids => cuboids ??= MicroBlocks.Cuboids(Attributes);

    /// <summary>Разобранная форма: верх опоры и готовый блок для физики.</summary>
    internal MicroBlockShape MicroShape(int blockId, WorldModel world)
    {
        if (shape is { } s && s.BlockId == blockId)
            return s;
        var made = world.MicroShapeOf(blockId, MicroCuboids);
        shape = made;
        return made;
    }
}

/// <summary>
/// Разобранная форма тёсаного блока: верх опоры (для сетки маршрутов) и
/// настоящий объект блока с настоящими коробками (для игровой физики).
/// Один объект на одну ФОРМУ, а не на одну клетку: в доме сотни тёсаных
/// блоков, но резаны они десятком одинаковых способов.
/// </summary>
internal sealed record MicroBlockShape(
    int BlockId, float Top, float Bottom, Vintagestory.API.Common.Block? PhysicsBlock);

/// <summary>
/// Модель мира бота: блоки и блок-сущности из чанков, которые присылает сервер.
/// Чанки хранятся сжатыми (как пришли) и разжимаются лениво при первом чтении.
/// Разжатие — методом самой игры (ChunkData.UnpackBlocksTo, через reflection:
/// он internal, но статический и самодостаточный).
/// </summary>
public partial class WorldModel : IPathWorld
{
    public const int ChunkSize = 32;
    private const int BlocksPerChunk = ChunkSize * ChunkSize * ChunkSize;

    private sealed class ChunkSlot
    {
        public byte[]? BlocksCompressed;
        public byte[]? LiquidsCompressed;   // вода/лава лежат отдельным слоем
        public int Compver;
        public int[]? Decoded;
        public int[]? DecodedLiquids;
        public bool Empty;

        /// <summary>
        /// Блок-сущности ЭТОГО чанка (сундуки, таблички, кровати). Живут и
        /// умирают вместе с ним — так же, как в самой игре, где они лежат
        /// прямо в объекте чанка (ClientChunk.BlockEntities).
        /// </summary>
        public readonly HashSet<BlockPos> Entities = [];

        /// <summary>
        /// Замок на разжатие ЭТОГО чанка. Нужен потому, что разжимают лениво
        /// и из разных нитей: сеть кладёт точечную правку, а роль читает блок.
        /// Без него две нити разжимали каждая свою копию, и та, что записала
        /// свой массив второй, стирала уже применённые к первому точечные
        /// правки — блок, поставленный сервером, молча исчезал.
        /// </summary>
        public readonly object Unpacking = new();

        /// <summary>Когда чанк пришёл (Environment.TickCount64) — для выбора «кто старее».</summary>
        public long Stamp;

        /// <summary>
        /// В РАЗЖАТОМ МАССИВЕ ЕСТЬ ТО, ЧЕГО НЕТ В СЖАТЫХ БАЙТАХ: точечные правки
        /// сервера (пакеты 7/47/58) ложатся только в разжатый слой, обратно в
        /// <see cref="BlocksCompressed"/> их никто не пишет.
        ///
        /// Ради этого признака он и заведён. Разжатый массив — кэш, и выбросить
        /// его ради памяти можно БЕЗ ЕДИНОГО ВРАНЬЯ: он пересчитывается из
        /// сжатых байтов слово в слово. Но только пока правок не было. Выброси
        /// мы массив с правками — при следующем чтении из сжатых байтов
        /// поднялся бы ПРЕЖНИЙ мир: дверь снова закрыта, выкопанный блок снова
        /// на месте, поставленный сундук исчез. Это враньё худшего вида: бот
        /// уверенно называет то, чего уже нет.
        /// </summary>
        public bool Dirty;
    }

    private readonly Dictionary<(int cx, int cy, int cz), ChunkSlot> chunks = new();
    private readonly object chunksLock = new();

    private readonly ConcurrentDictionary<BlockPos, BlockEntityInfo> blockEntities = new();

    private string[] blockCodes = [];                       // blockId → код ("game:bed-wood-head-north")
    private readonly Dictionary<string, int> codeToId = new();
    private string[] itemCodes = [];                        // itemId → код предмета
    private readonly Dictionary<string, int> itemCodeToId = new();
    private bool[] blockHasCollision = [];                  // blockId → есть ли коллизия (стена/пол)
    private bool[] blockIsWater = [];                       // blockId → вода (можно плыть)
    private byte[] blockLiquidLevel = [];                   // blockId → сколько восьмых клетки залито (1..7)
    private byte[] blockHarm = [];                          // blockId → CellHarm: чем вредит стоящему
    private bool[] blockIsClimbable = [];                   // blockId → лестница/лоза (можно лезть)
    private float[] itemSatiety = [];                       // itemId → питательность (0 = несъедобно)
    private float[] itemFoodHealth = [];                    // itemId → хп от еды (минус = яд)
    private float[] blockSatiety = [];                      // blockId → питательность (хлеб — блок!)
    private float[] blockFoodHealth = [];                   // blockId → хп от еды (минус = яд: поганка −50)
    private float[] itemAttackPower = [];                   // itemId → урон (для выбора оружия)
    private HealingInfo?[] itemHealing = [];                // itemId → лечебные свойства
    private int[] itemToolType = [];                        // itemId → тип инструмента (-1 — не инструмент)
    private ArmorInfo?[] itemArmor = [];                    // itemId → броня (null — не броня)
    private bool[] itemIsBag = [];                          // itemId → сумка (поведение HeldBag)
    private int[] itemBagSlots = [];                        // itemId → сколько мест даёт сумка
    private float[] blockWalkSpeed = [];                    // blockId → множитель скорости ходьбы
    private float[] blockCollisionTop = [];                 // blockId → высота верха коллизии (1.0 — полный куб)
    private float[] blockCollisionBottom = [];              // blockId → высота НИЗА коллизии (0 — стоит на полу клетки)
    private bool[] blockIsDoor = [];                        // blockId → дверь/калитка/люк
    private byte[] blockDoorKind = [];                      // blockId → DoorKind: где хранится состояние
    private bool[] blockIsMultiblock = [];                  // blockId → служебный блок мультиблока
    private bool[] blockIsMicroBlock = [];                  // blockId → тёсаный блок (форма в блок-сущности)
    private bool[] blockIsPlacedThing = [];                 // blockId → у блока есть класс блок-сущности
    private bool[] blockIsTrapdoor = [];                    // blockId → люк (дверь в полу)
    private string?[] blockInvClass = [];                   // blockId → inventoryClassName контейнера
    private string[][] blockBehaviorNames = [];             // blockId → имена поведений из реестра сервера
    private bool[] blockPannable = [];                      // blockId → моется лотком (атрибут pannable)
    private string?[] blockPannedInto = [];                 // blockId → во что превращается, отдав горсть
    private Dictionary<string, string> panningDropsJson = new(StringComparer.Ordinal);  // код лотка → его таблица выпадений

    // Двери, которые не открылись (приват): на время выкидываем из маршрутов
    private readonly System.Collections.Concurrent.ConcurrentDictionary<BlockPos, DateTime> lockedDoors = new();

    private delegate void UnpackBlocksDelegate(int[] blocksOut, byte[] blocksCompressed, byte[]? lightSatCompressed, int chunkdataVersion);
    private static readonly UnpackBlocksDelegate UnpackBlocks =
        typeof(ChunkData).GetMethod("UnpackBlocksTo", BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<UnpackBlocksDelegate>();
    private static readonly object unpackLock = new(); // UnpackBlocksTo использует статические буферы

    /// <summary>Сработает, когда придёт реестр блоков (после этого коды блоков известны).</summary>
    public event Action? OnBlockRegistryReady;

    /// <summary>Количество загруженных чанков (диагностика).</summary>
    public int LoadedChunkCount { get { lock (chunksLock) return chunks.Count; } }

    public bool BlockRegistryReady => blockCodes.Length > 0;

    public WorldModel(BotClient bot)
    {
        // Клиент нужен и после подписки: берег памяти сам убавляет запрос
        // дальности к серверу (см. KeepChunkRadius) — просить больше, чем
        // помнишь, значит нарочно делать дыры в собственной карте
        owner = bot;
        bot.OnPacket += HandlePacket;
        // НОВОЕ СОЕДИНЕНИЕ — НОВЫЙ МИР. Реестр блоков сервер шлёт при каждом
        // входе заново, и после его перезапуска (или установки мода) те же
        // числа означают уже другие блоки: чанки, разжатые по старому реестру,
        // превратили бы память бота в выдумку — «гранит» там, где дверь.
        bot.OnSessionReset += ForgetWorld;
    }

    /// <summary>
    /// Забыть мир целиком: все чанки и все блок-сущности. Зовётся при обрыве
    /// связи — то, что бот видел в прошлой жизни, доказательством больше не
    /// является, и врать про это нельзя.
    /// </summary>
    public void ForgetWorld()
    {
        lock (chunksLock)
            chunks.Clear();
        // Карта поверхности — такое же знание о мире, как блоки: чужой мир —
        // чужая поверхность, и держать её после обрыва значило бы отвечать
        // «я тут в пещере» по высотам прошлой сессии
        lock (surfaceLock)
            surface.Clear();
        blockEntities.Clear();
        lockedDoors.Clear();
        // Счётчики уборки — про ЭТО соединение. Оставь их — и в окне висела бы
        // вчерашняя работа над миром, которого больше нет
        Interlocked.Exchange(ref forgottenFar, 0);
        Interlocked.Exchange(ref droppedDecoded, 0);
        Interlocked.Exchange(ref lastNoteAt, 0);
    }

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 19: // ServerAssets: реестр блоков
                if (p.Assets?.Blocks != null)
                {
                    int max = 0;
                    for (int i = 0; i < p.Assets.BlocksCount; i++)
                        max = Math.Max(max, p.Assets.Blocks[i].BlockId);
                    var codes = new string[max + 1];
                    var hasCollision = new bool[max + 1];
                    var bSatiety = new float[max + 1];
                    var bFoodHealth = new float[max + 1];
                    var isWater = new bool[max + 1];
                    var liquidLevel = new byte[max + 1];
                    var harm = new byte[max + 1];
                    var isClimbable = new bool[max + 1];
                    var walkSpeed = new float[max + 1];
                    var collisionTop = new float[max + 1];
                    var collisionBottom = new float[max + 1];
                    var isDoor = new bool[max + 1];
                    var doorKind = new byte[max + 1];
                    var isMultiblock = new bool[max + 1];
                    var isMicroBlock = new bool[max + 1];
                    var isPlacedThing = new bool[max + 1];
                    var isTrapdoor = new bool[max + 1];
                    var invClass = new string?[max + 1];
                    var behaviorNames = new string[max + 1][];
                    // Что моют лотком и чем моют — оба ответа даёт реестр, а не
                    // список кодов у нас (закон 6). См. Panning
                    var pannable = new bool[max + 1];
                    var pannedInto = new string?[max + 1];
                    var pans = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < p.Assets.BlocksCount; i++)
                    {
                        var bt = p.Assets.Blocks[i];
                        codes[bt.BlockId] = bt.Code;
                        hasCollision[bt.BlockId] = bt.CollisionBoxesCount > 0;

                        // ТЁСАНЫЙ БЛОК УЗНАЁТСЯ ПО КЛАССУ ЕГО БЛОК-СУЩНОСТИ,
                        // а не по коду: код у модовых тёсаных блоков любой,
                        // а класс игра регистрирует один на всех (см.
                        // MicroBlocks.IsMicroBlockEntity). Настоящую форму
                        // такого блока реестр НЕ несёт — только полный куб;
                        // отсюда «бот летит в воздухе» на тёсаной ступеньке
                        isMicroBlock[bt.BlockId] = MicroBlocks.IsMicroBlockEntity(bt.EntityClass);

                        // ПОСТАВЛЕННАЯ ВЕЩЬ — ЭТО ЗАЯВЛЕНИЕ САМОЙ ИГРЫ, А НЕ
                        // СПИСОК КОДОВ. Класс блок-сущности игра даёт ровно
                        // тому, у чего есть своё состояние: двери, сундуку,
                        // табличке, кровати, печи, бочке. В породе такого не
                        // бывает — камню и земле состояние хранить незачем.
                        //
                        // ЖИВОЙ СЛУЧАЙ 23.08, 11:01:57: бот стоял В ДВЕРЯХ дома
                        // заказчика и выламывал стену (rammed-light-plain) себе
                        // на столб. Застава спрашивала «а есть ли рядом
                        // постройка» у списка ПРИСЛАННЫХ блок-сущностей — а он
                        // приходит с чанком отдельно от блоков и вдобавок
                        // просеивается честным зрением: дверь за стеной в него
                        // не попадала вовсе. Реестр же знает про дверь всегда,
                        // и знание это не подглядывание: код блока бот читает
                        // и так
                        isPlacedThing[bt.BlockId] = bt.EntityClass is { Length: > 0 };

                        // Верх коллизии блока: у обычного куба 1.0, у пластины
                        // транслокатора 0.125, у плиты 0.5, у снега — по слоям.
                        // Без этого бот «стоит в воздухе» над тонкими блоками
                        float top = 0;
                        // НИЗ КОЛЛИЗИИ — ВТОРАЯ ПОЛОВИНА ТОЙ ЖЕ ПРАВДЫ, и без
                        // неё нельзя ответить на прямой вопрос заказчика «какой
                        // высоты препятствие». Верх говорит, на что бот встанет;
                        // низ — насколько потолок висит НАД ногами. У целого
                        // куба это 0, у перевёрнутой плиты 0,5, у люка в потолке
                        // 0,9375. Единица (ниже) означает «ничего не свисает»
                        float bottom = 1;
                        for (int b = 0; b < bt.CollisionBoxesCount; b++)
                            if (bt.CollisionBoxes?[b] is { } box)
                            {
                                top = Math.Max(top, CollectibleNet.DeserializeFloatVeryPrecise(box.Maxy));
                                bottom = Math.Min(bottom, CollectibleNet.DeserializeFloatVeryPrecise(box.Miny));
                            }
                        collisionTop[bt.BlockId] = top;
                        collisionBottom[bt.BlockId] = bottom;
                        isClimbable[bt.BlockId] = bt.Climbable > 0;
                        // Множитель скорости по блоку: тропинка 1.3, паутина 0.25.
                        // 0 в пакете означает «поле не слали» — это обычная 1.0
                        float ws = CollectibleNet.DeserializeFloatVeryPrecise(bt.WalkSpeedFloat);
                        walkSpeed[bt.BlockId] = ws > 0 ? ws : 1f;

                        // ИМЕНА ПОВЕДЕНИЙ БЛОКА — СОХРАНЯЕМ ИХ ЗДЕСЬ, а не
                        // выбрасываем.
                        //
                        // ЖИВОЙ СЛУЧАЙ формы «рукописный список вместо реестра»
                        // (19.08). Настоящие объекты блоков собираются с
                        // ЗАГЛУШКАМИ вместо незнакомых поведений (StubBehaviors:
                        // сервер шлёт и модовые классы, а голый реестр игры их
                        // не знает и падает), поэтому «BreakIfFloating» на
                        // объекте блока не найти вовсе — и карьер решал «выпадет
                        // ли блок целым» по списку приставок
                        // ["rock-","crackedrock-","ore-","meteorite-"]. Список
                        // неполон и в самой ванили 1.22.7: BreakIfFloating стоит
                        // ещё у rottenlog-*, termitemound-* и
                        // termitemound-harvested-*, а под приставки они не
                        // подходят. ИМЯ поведения сервер шлёт этим же пакетом —
                        // хранить его дешевле, чем угадывать по коду.
                        if (bt.BehaviorsCount > 0 && bt.Behaviors != null)
                        {
                            var names = new List<string>(bt.BehaviorsCount);
                            for (int b = 0; b < bt.BehaviorsCount; b++)
                                if (bt.Behaviors[b]?.Code is { Length: > 0 } bname)
                                    names.Add(bname);
                            if (names.Count > 0)
                                behaviorNames[bt.BlockId] = names.ToArray();
                        }

                        // Дверь/калитка/люк. Поведение «Door» ловит новые двери
                        // (в том числе модовые), код блока — старые
                        bool byBehavior = false, trapByBehavior = false;
                        for (int b = 0; b < bt.BehaviorsCount; b++)
                        {
                            if (bt.Behaviors?[b]?.Code is "Door" or "TrapDoor" or "FenceGate")
                                byBehavior = true;
                            // ЛЮКА СПРАШИВАЕМ У РЕЕСТРА, А НЕ У БУКВ КОДА. У люка
                            // своя геометрия створки (откидывается ВВЕРХ, вокруг
                            // оси X — BEBehaviorTrapDoor), и считать его дверным
                            // правилом значит держать открытый люк полом. Модовый
                            // люк, названный не «trapdoor», проскакивал мимо маски
                            // ниже и получал ровно это
                            if (bt.Behaviors?[b]?.Code is "TrapDoor")
                                trapByBehavior = true;
                        }
                        if (trapByBehavior)
                            isTrapdoor[bt.BlockId] = true;
                        if (bt.Code is { } dc)
                        {
                            // Маску по коду держим УЗКОЙ: «hatch» ловил бы
                            // соломенные крыши (thatch!) — сотни блоков разом
                            // стали бы «дверьми» и проходимыми насквозь
                            bool byCode = dc.Contains("door", StringComparison.OrdinalIgnoreCase) ||
                                          dc.Contains("gate", StringComparison.OrdinalIgnoreCase);
                            if (byBehavior)
                                // Поведение Door — это всегда новая дверь:
                                // состояние в блок-сущности, код неизменен
                                doorKind[bt.BlockId] = (byte)DoorKind.ByEntity;
                            else if (byCode)
                                // Старая дверь и калитка держат состояние в коде
                                doorKind[bt.BlockId] = (byte)(
                                    dc.Contains("opened", StringComparison.OrdinalIgnoreCase) ? DoorKind.OpenByCode :
                                    dc.Contains("closed", StringComparison.OrdinalIgnoreCase) ? DoorKind.ClosedByCode :
                                    DoorKind.ByEntity);
                            // Люк — дверь в ПОЛУ: в проём сквозь неё не ходят.
                            // Маска по коду осталась ВТОРОЙ сетью, а не первой:
                            // главный ответ даёт поведение «TrapDoor» из реестра
                            // (выше), а сюда попадают люки, у которых поведения
                            // нет вовсе
                            isTrapdoor[bt.BlockId] = isTrapdoor[bt.BlockId] ||
                                dc.Contains("trapdoor", StringComparison.OrdinalIgnoreCase) ||
                                dc.Contains("hatch-", StringComparison.OrdinalIgnoreCase);
                            isMultiblock[bt.BlockId] =
                                dc.StartsWith("multiblock-", StringComparison.OrdinalIgnoreCase);
                        }
                        // Имя инвентаря контейнера сервер строит из АТРИБУТА
                        // блока inventoryClassName, а не из его кода: у
                        // подписанного сундука код «labeledchest», а инвентарь
                        // «chest-x, y, z». Без этого пакеты перекладывания
                        // уходят на несуществующий инвентарь и молча гибнут
                        //
                        // ОТСЮДА ЖЕ БЕРЁТСЯ «insideDamage» — тот самый атрибут,
                        // которым сама игра решает, жечь ли стоящего в клетке
                        // (BlockForFluidsLayer.OnEntityInside) и куда МОЖНО
                        // встать (AiTaskBaseTargetable.FindDecentTeleportPos).
                        // Разбор строки стоит денег, поэтому он один на реестр,
                        // а не на каждую клетку маршрута
                        double insideDamage = 0;
                        if (!string.IsNullOrEmpty(bt.Attributes))
                        {
                            try
                            {
                                var ba = Newtonsoft.Json.Linq.JObject.Parse(bt.Attributes);
                                if (ba.GetValue("inventoryClassName", StringComparison.OrdinalIgnoreCase)
                                        ?.ToObject<string>() is { Length: > 0 } inv)
                                    invClass[bt.BlockId] = inv;
                                if (ba.GetValue("insideDamage", StringComparison.OrdinalIgnoreCase)
                                        ?.ToObject<double?>() is { } dmg)
                                    insideDamage = dmg;

                                // ПРОМЫВКА ЛОТКОМ — ОБА ЕЁ ФАКТА ЛЕЖАТ ЗДЕСЬ ЖЕ.
                                //
                                // «pannable» — ровно то поле, по которому решает
                                // сама игра (BlockPan.IsPannableMaterial:
                                // «attributes.IsTrue("pannable")»). В ванили
                                // 1.22.7 его носят bony.json, bony-layered.json,
                                // sand.json, sand-layered.json, sand-wavy.json,
                                // gravel.json и gravel-layered.json — но список
                                // этот наш ответ не строит и строить не должен:
                                // мод добавит свой песок, и рукописный список
                                // его потеряет (закон 6).
                                //
                                // «pannedBlock» — во что клетка превращается,
                                // когда лоток забрал из неё горсть
                                // (BlockPan.TryTakeMaterial). У костяной земли
                                // это «bonysoil-7», дальше слой убывает сам.
                                // По ЭТОМУ полю бот и узнаёт, что сервер горсть
                                // принял: слово «взял» без перемены блока —
                                // обещание без факта (закон 4)
                                if (ba.GetValue("pannable", StringComparison.OrdinalIgnoreCase)
                                        ?.ToObject<bool?>() == true)
                                    pannable[bt.BlockId] = true;
                                if (ba.GetValue("pannedBlock", StringComparison.OrdinalIgnoreCase)
                                        ?.ToObject<string>() is { Length: > 0 } panned)
                                    pannedInto[bt.BlockId] = panned;
                                // Сам ЛОТОК узнаётся по таблице выпадений: она
                                // лежит в его атрибутах и больше нигде
                                if (bt.Code is { Length: > 0 } panCode &&
                                    ba.GetValue("panningDrops", StringComparison.OrdinalIgnoreCase)
                                        is { } drops)
                                    pans[panCode] = drops.ToString();
                            }
                            catch { /* нестандартные атрибуты — останется значение по умолчанию */ }
                        }
                        isDoor[bt.BlockId] = doorKind[bt.BlockId] != 0;
                        if (bt.NutritionProps != null)
                        {
                            bSatiety[bt.BlockId] = CollectibleNet.DeserializeFloat(bt.NutritionProps.Satiety);
                            // ЯД У ГРИБОВ ЗАПИСАН У БЛОКА, а не у предмета:
                            // в ассетах это nutritionPropsByType самого
                            // mushroom (поганка — health −50). Бот читал яд
                            // только у предметов и считал поганку безопасной
                            bFoodHealth[bt.BlockId] = CollectibleNet.DeserializeFloat(bt.NutritionProps.Health);
                        }
                        // Свойства жидкостей считаем один раз при получении реестра,
                        // чтобы поиск пути не разбирал строки на каждой клетке
                        if (bt.Code is { } code)
                            codeToId[code] = bt.BlockId;
                        // СКОЛЬКО ВОСЬМЫХ КЛЕТКИ ЗАЛИТО — поле реестра, а не
                        // хвост кода. От него игра считает, где всплывёт тело
                        // (PModulePlayerInLiquid: «(int)Y + LiquidLevel/8 + …»),
                        // и живая беда 27.08 стояла именно в неполной воде:
                        // saltwater-n-6 под ногами и saltwater-n-5 над ними
                        if (bt.LiquidLevel is > 0 and <= 7)
                            liquidLevel[bt.BlockId] = (byte)bt.LiquidLevel;

                        // ЧЕМ КЛЕТКА ВРЕДИТ СТОЯЩЕМУ — СПРАШИВАЕМ У ИГРЫ, А НЕ
                        // У СПИСКА КОДОВ. Здесь стояло
                        // «isDangerous = boiling || code.Contains("lava")» —
                        // рукописный список ровно того вида, на котором проект
                        // горел трижды (закон 6), и он не знал ни про огонь, ни
                        // про костёр: бот сгорел в собственном костре, потеряв
                        // 48 стопок. Теперь решает одно правило на весь проект
                        // (HarmRules.HarmOf), и все его доводы — поля реестра
                        // сервера: insideDamage, материал блока и класс блока
                        harm[bt.BlockId] = (byte)HarmRules.HarmOf(
                            bt.BlockMaterial, bt.Blockclass, bt.Code, insideDamage);

                        // ВОДА, В КОТОРОЙ МОЖНО ПЛЫТЬ, — ДВА ФАКТА ОТ СЕРВЕРА,
                        // А НЕ КУСОК КОДА.
                        //
                        // Здесь стояло «code.Contains("water") &&
                        // !code.Contains("boiling")» — рукописная догадка по
                        // имени ровно того вида, на котором проект горел
                        // четырежды (закон 6). Под неё попадали waterlily
                        // (кувшинка), waterwheel (ВОДЯНОЕ КОЛЕСО, механизм),
                        // wateringcan (поставленная лейка) и
                        // aquatic-watercrowfoot (водяной лютик) — а этот ответ
                        // идёт в поиск пути, в «плыть», в «вода ловит — это не
                        // провал», в AvoidWater карьера и в мостостроение.
                        //
                        // ЧТО СПРАШИВАЕМ ВМЕСТО ЭТОГО. «Жидкость ли это» —
                        // ровно тем полем, каким это решает сама игра:
                        // CollectibleObject.IsLiquid() смотрит ТОЛЬКО на
                        // MatterState, и сервер шлёт его в реестре
                        // (BlockTypeNet.ToPacket). «Не вредно ли» — той самой
                        // таблицей вреда, что посчитана строкой выше: лава и
                        // кипяток отпадают там, и второго списка кодов для них
                        // не заводится.
                        //
                        // ОСТОРОЖНО ПРИ ПРАВКЕ: «LiquidCode == "water"» тут не
                        // годится — солёная вода зовётся saltwater, а плавают в
                        // ней так же.
                        isWater[bt.BlockId] =
                            bt.MatterState == (int)Vintagestory.API.Common.EnumMatterState.Liquid &&
                            (CellHarm)harm[bt.BlockId] != CellHarm.Always;
                    }
                    blockCodes = codes;
                    blockHasCollision = hasCollision;
                    blockSatiety = bSatiety;
                    blockFoodHealth = bFoodHealth;
                    blockIsWater = isWater;
                    blockLiquidLevel = liquidLevel;
                    blockHarm = harm;
                    blockIsClimbable = isClimbable;
                    blockWalkSpeed = walkSpeed;
                    blockCollisionTop = collisionTop;
                    blockCollisionBottom = collisionBottom;
                    blockIsDoor = isDoor;
                    blockDoorKind = doorKind;
                    blockIsMultiblock = isMultiblock;
                    blockIsMicroBlock = isMicroBlock;
                    blockIsPlacedThing = isPlacedThing;
                    blockIsTrapdoor = isTrapdoor;
                    blockInvClass = invClass;
                    blockBehaviorNames = behaviorNames;
                    blockPannable = pannable;
                    blockPannedInto = pannedInto;
                    panningDropsJson = pans;
                    BuildGameBlocks(p.Assets.Blocks, p.Assets.BlocksCount, max);

                    if (p.Assets.Items != null)
                    {
                        int maxItem = 0;
                        for (int i = 0; i < p.Assets.ItemsCount; i++)
                            maxItem = Math.Max(maxItem, p.Assets.Items[i].ItemId);
                        var icodes = new string[maxItem + 1];
                        var iSatiety = new float[maxItem + 1];
                        var iFoodHealth = new float[maxItem + 1];
                        var iAttack = new float[maxItem + 1];
                        var iHealing = new HealingInfo?[maxItem + 1];
                        var iBag = new bool[maxItem + 1];
                        var iBagSlots = new int[maxItem + 1];
                        var iTool = new int[maxItem + 1];
                        var iArmor = new ArmorInfo?[maxItem + 1];
                        for (int i = 0; i < p.Assets.ItemsCount; i++)
                        {
                            var it = p.Assets.Items[i];
                            icodes[it.ItemId] = it.Code;
                            if (it.NutritionProps != null)
                            {
                                iSatiety[it.ItemId] = CollectibleNet.DeserializeFloat(it.NutritionProps.Satiety);
                                // Отрицательное здоровье = ядовитая еда (поганка
                                // даёт сытость 80 и −50 хп при максимуме 15)
                                iFoodHealth[it.ItemId] = CollectibleNet.DeserializeFloat(it.NutritionProps.Health);
                            }
                            iAttack[it.ItemId] = CollectibleNet.DeserializeFloatPrecise(it.AttackPower);
                            iHealing[it.ItemId] = ReadHealing(it.Behaviors, it.BehaviorsCount);
                            iTool[it.ItemId] = it.Tool;   // -1 — не инструмент
                            iArmor[it.ItemId] = ReadArmor(it.Attributes);
                            iBag[it.ItemId] = HasBehavior(it.Behaviors, it.BehaviorsCount, "HeldBag");
                            iBagSlots[it.ItemId] = ReadBagSlots(it.Attributes);
                            if (it.Code != null) itemCodeToId[it.Code] = it.ItemId;
                        }
                        itemCodes = icodes;
                        itemSatiety = iSatiety;
                        itemFoodHealth = iFoodHealth;
                        itemAttackPower = iAttack;
                        itemHealing = iHealing;
                        itemToolType = iTool;
                        itemArmor = iArmor;
                        itemIsBag = iBag;
                        itemBagSlots = iBagSlots;
                        // Настоящие объекты предметов: скорость добычи, тир,
                        // прочность и размер стака — из игры, а не на глаз
                        BuildGameItems(p.Assets.Items, p.Assets.ItemsCount, maxItem);
                    }

                    OnBlockRegistryReady?.Invoke();
                }
                break;

            case 10: // Chunks
                if (p.Chunks?.Chunks != null)
                    for (int i = 0; i < p.Chunks.ChunksCount; i++)
                        StoreChunk(p.Chunks.Chunks[i]);
                break;

            case 11: // UnloadServerChunk
                if (p.UnloadChunk is { } u && u.X != null)
                    lock (chunksLock)
                        for (int i = 0; i < u.XCount; i++)
                            ForgetChunk(u.X[i], u.Y[i], u.Z[i]);
                break;

            case 17: // MapChunk: карта поверхности столбца (см. StoreSurface)
                if (p.MapChunk is { } mc)
                    StoreSurface(mc);
                break;

            case 7: // SetBlock
                if (p.SetBlock is { } sb)
                    ApplySetBlock(sb.X, sb.Y, sb.Z, sb.BlockType);
                break;

            case 47 or 63 or 70: // SetBlocks / NoRelight / Minimal
                if (p.SetBlocks?.SetBlocks != null)
                {
                    var pair = BlockTypeNet.UnpackSetBlocks(p.SetBlocks.SetBlocks, out int[] liquids);
                    var positions = pair.Key;
                    var ids = pair.Value;
                    for (int i = 0; i < positions.Length; i++)
                    {
                        // ЧУЖОЕ ИЗМЕРЕНИЕ — НЕ НАШ МИР. Номер измерения игра
                        // пакует в высоту (y = n % 32768, измерение = n / 32768)
                        // и распаковывает обратно в BlockPos.dimension. Правка
                        // в мини-измерении легла бы в обычный мир на ту же
                        // высоту — то есть пробила бы в земле дыру там, где её
                        // нет, и бот бы её потом «перепрыгивал»
                        if (positions[i].dimension != 0)
                            continue;
                        SetBlockId(positions[i].X, positions[i].Y, positions[i].Z, ids[i]);
                        if (liquids != null && i < liquids.Length)
                            SetLiquidId(positions[i].X, positions[i].Y, positions[i].Z, liquids[i]);
                    }
                }
                break;

            case 58: // ExchangeBlock
                if (p.ExchangeBlock is { } ex)
                    SetBlockId(ex.X, ex.Y, ex.Z, ex.BlockType);
                break;

            case 48: // BlockEntities
                if (p.BlockEntities?.BlockEntitites != null)
                    for (int i = 0; i < p.BlockEntities.BlockEntititesCount; i++)
                        StoreBlockEntity(p.BlockEntities.BlockEntitites[i]);
                break;
        }
    }

    /// <summary>
    /// ГДЕ ЖИВЁТ КЛЕТКА: ключ её чанка и место внутри него. Одно правило на
    /// всю модель мира.
    ///
    /// Раньше эти три строки были переписаны в ЧЕТЫРЁХ местах — чтение блока,
    /// чтение жидкости и две записи. Разойдись они хоть однажды, запись легла
    /// бы не туда, куда потом смотрит чтение, и внешне это выглядело бы ровно
    /// как «бот сам себе противоречит»: сундук есть, а в клетке воздух.
    ///
    /// Деление берём ВНИЗ, а не к нулю. В Vintage Story координаты мира
    /// неотрицательны, но «x / 32» при x = −1 даёт чанк 0 и место −1 — выход
    /// за массив; честнее посчитать правильно, чем надеяться на входные данные.
    /// </summary>
    private static ((int cx, int cy, int cz) Key, int Index) Cell(int x, int y, int z)
    {
        int cx = Down(x), cy = Down(y), cz = Down(z);
        int lx = x - cx * ChunkSize, ly = y - cy * ChunkSize, lz = z - cz * ChunkSize;
        // Порядок (y, z, x) — не наша выдумка, а раскладка самой игры:
        // WorldChunk.GetLocalBlockAtBlockPos считает (ly * 32 + lz) * 32 + lx
        return ((cx, cy, cz), (ly * ChunkSize + lz) * ChunkSize + lx);

        static int Down(int v) => v >= 0 ? v / ChunkSize : ~(~v / ChunkSize);
    }

    /// <summary>Чанк этой клетки (null — не приходил) и место клетки в нём.</summary>
    private ChunkSlot? SlotAt(int x, int y, int z, out int index)
    {
        var (key, i) = Cell(x, y, z);
        index = i;
        lock (chunksLock)
            return chunks.TryGetValue(key, out var slot) ? slot : null;
    }

    /// <summary>
    /// ТОЧЕЧНАЯ ПРАВКА ИЗ ПАКЕТА 7: ОДНО ПОЛЕ — ДВА РАЗНЫХ СЛОЯ.
    ///
    /// В целом чанке блоки и жидкости приходят ОТДЕЛЬНЫМИ слоями (Blocks и
    /// Liquids). В точечной правке отдельного поля под жидкость нет: сервер
    /// пакует её в тот же BlockType, но со знаком минус —
    /// BlockAccessorBase.SetFluidBlockInternal шлёт «−fluidBlockid − 1», а
    /// клиент игры разбирает это обратно (GeneralPacketHandler.HandleSetBlock:
    /// blockType &lt; 0 → SetBlock(−(blockType+1), pos, слой 2 = жидкости)).
    ///
    /// ВОТ ГДЕ БОТ НАЧИНАЛ САМ СЕБЕ ПРОТИВОРЕЧИТЬ. Знак не разбирался, и любое
    /// движение воды рядом (разлив, осушение, ведро, дождевая лужа, таяние
    /// льда) клало ОТРИЦАТЕЛЬНОЕ число прямо в слой твёрдых блоков той клетки.
    /// Дальше одна и та же клетка отвечала разное разным спрашивающим:
    ///   • GetBlockId → 0, то есть ВОЗДУХ — журнал склада печатал «air (x, y, z)»
    ///     ровно там, где стоял сундук, и там же говорил «не открылся»;
    ///   • GetBlockCode → null, «сказать нечего»;
    ///   • IsPassable → «непроходимо», потому что отрицательное значение
    ///     неотличимо от «чанка нет», — отсюда дыры в карте у поиска пути,
    ///     «перепрыгиваю яму» на ровном месте и хождение кругами.
    /// Хуже всего осушение: жидкость 0 даёт ровно −1, то есть в точности
    /// <see cref="UnknownBlock"/>. Клетка посреди полностью известного чанка
    /// навсегда становилась «я про это место ничего не знаю».
    /// </summary>
    private void ApplySetBlock(int x, int y, int z, int blockType)
    {
        if (blockType < 0)
            SetLiquidId(x, y, z, -(blockType + 1));
        else
            SetBlockId(x, y, z, blockType);
    }

    /// <summary>
    /// СКОЛЬКО КЛЕТОК МИРА ПЕРЕМЕНИЛОСЬ ПО ФАКТУ ОТ СЕРВЕРА за эту сессию.
    ///
    /// Зачем счётчик наружу. «Мир изменился» — единственная честная причина
    /// забыть свой же отказ («туда отсюда мне не дойти»): бот выкопал ступеньку,
    /// снял завал, кто-то открыл проход, — и прежний ответ больше не про этот
    /// мир. Спрашивать об этом было НЕЧЕМ, и всякий, кому такая память
    /// понадобилась бы, завёл бы свою мерку «а не поменялось ли» — по таймеру
    /// или по собственным ударам, то есть по догадке.
    ///
    /// СЧИТАЕМ ТОЛЬКО ТО, ЧТО ПРИСЛАЛ СЕРВЕР (правило 4 проекта): свои удары
    /// киркой сюда не попадают, пока сервер не подтвердит их пакетом. Целые
    /// чанки не в счёт — там мир не менялся, а просто доехал до нас.
    ///
    /// СЧЁТ ОБЩИЙ, А НЕ «ВОКРУГ МЕНЯ», и это нарочно: где именно стоит бот,
    /// модель мира не знает и знать не должна. Ошибается такой счёт всегда в
    /// одну сторону — в пользу «попробуй ещё раз»: чужая правка за полкарты
    /// заставит бота лишний раз пересчитать дорогу, но НИКОГДА не заставит его
    /// отказаться там, где на самом деле уже можно пройти.
    /// </summary>
    public long BlockChanges => Interlocked.Read(ref blockChanges);

    private long blockChanges;

    /// <summary>Точечное обновление блока (изменения мира от сервера).</summary>
    private void SetBlockId(int x, int y, int z, int blockId)
    {
        // ОТРИЦАТЕЛЬНЫХ НОМЕРОВ В СЛОЕ БЛОКОВ НЕ БЫВАЕТ, и молча записать такой
        // нельзя: −1 в массиве означает то же, что «чанк не приходил», и одна
        // клетка внутри известного чанка притворилась бы неизвестной навсегда.
        // Раз сюда всё-таки пришло отрицательное — мы чего-то не понимаем в
        // пакете, и человек обязан узнать об этом сразу, а не по кривому
        // маршруту через неделю
        if (blockId < 0)
        {
            OnParseError?.Invoke(
                $"({x}, {y}, {z}): сервер прислал отрицательный номер блока {blockId} — " +
                "не записываю (жидкости приходят своим слоем, см. ApplySetBlock)");
            return;
        }

        var slot = SlotAt(x, y, z, out int index);
        if (slot == null)
            return; // чанк не загружен — обновлять нечего

        lock (slot.Unpacking)
        {
            if (slot.Decoded == null)
            {
                if (slot.Empty || slot.BlocksCompressed == null)
                {
                    // пустой чанк перестаёт быть пустым — создаём пустой массив
                    slot.Decoded = new int[BlocksPerChunk];
                    slot.Empty = false;
                }
                else
                {
                    var decoded = new int[BlocksPerChunk];
                    lock (unpackLock)
                        UnpackBlocks(decoded, slot.BlocksCompressed, null, slot.Compver);
                    slot.Decoded = decoded;
                }
            }
            // Считаем только НАСТОЯЩУЮ перемену: сервер шлёт SetBlock и на то,
            // что уже стоит (соседние правки, повторные пакеты), а «мир стал
            // другим» из этого не следует — и тот, кто по этому счёту забывает
            // свои отказы, забывал бы их впустую
            if (slot.Decoded[index] != blockId)
                Interlocked.Increment(ref blockChanges);
            slot.Decoded[index] = blockId;
            // Правки в сжатые байты не возвращаются — значит разжатый массив
            // больше не пересчитывается из них, и выбрасывать его нельзя
            slot.Dirty = true;
        }
    }

    /// <summary>Точечное обновление жидкости (разлив/осушение).</summary>
    private void SetLiquidId(int x, int y, int z, int liquidId)
    {
        var slot = SlotAt(x, y, z, out int index);
        if (slot == null)
            return;

        lock (slot.Unpacking)
        {
            if (slot.DecodedLiquids == null)
            {
                var decoded = new int[BlocksPerChunk];
                if (!slot.Empty && slot.LiquidsCompressed != null)
                    lock (unpackLock)
                        UnpackBlocks(decoded, slot.LiquidsCompressed, null, slot.Compver);
                slot.DecodedLiquids = decoded;
            }
            // В «пустом» чанке появилась жидкость. Блоков это не касается:
            // снимая пометку, надо оставить и пустой слой блоков, иначе
            // чтение блока полезет разжимать то, чего не присылали
            if (slot.Empty)
            {
                slot.Decoded ??= new int[BlocksPerChunk];
                slot.Empty = false;
            }
            slot.DecodedLiquids[index] = liquidId;
            slot.Dirty = true;   // та же причина, что и у блоков
        }
    }

    private void StoreChunk(Packet_ServerChunk c)
    {
        var slot = new ChunkSlot
        {
            BlocksCompressed = c.Blocks,
            LiquidsCompressed = c.Liquids,
            Compver = c.Compver,
            Empty = c.Empty > 0,
            Stamp = Environment.TickCount64
        };
        lock (chunksLock)
        {
            // ПРЕЖНИЕ БЛОК-СУЩНОСТИ ЭТОГО ЧАНКА ЗАБЫВАЕМ ЦЕЛИКОМ. Сервер шлёт
            // с чанком ПОЛНЫЙ их список, и клиент игры делает ровно это
            // (ClientChunk.PreLoadBlockEntitiesFromPacket начинается с
            // BlockEntities.Clear()). Без этого сундук, который человек сломал,
            // пока бота тут не было, остаётся в памяти бота навсегда — и склад
            // потом ходит открывать пустое место
            ForgetChunk(c.X, c.Y, c.Z);
            chunks[(c.X, c.Y, c.Z)] = slot;
        }

        if (c.BlockEntities != null)
            for (int i = 0; i < c.BlockEntitiesCount; i++)
                StoreBlockEntity(c.BlockEntities[i]);

        // МИР ПОДРОС — ПРОВЕРИТЬ БЕРЕГА. Именно приход чанка и есть тот
        // единственный миг, когда карта становится больше; отдельного
        // будильника ради этого заводить незачем (см. раздел «берега памяти»)
        ForgetFarChunks();
    }

    /// <summary>
    /// Забыть чанк ВМЕСТЕ С ЕГО БЛОК-СУЩНОСТЯМИ. Звать только под chunksLock.
    ///
    /// Иначе выходит самое обидное враньё: чанк выгружен, блоки в нём читаются
    /// воздухом, а сундуки из него по-прежнему числятся стоящими — бот берёт
    /// их под склад, идёт и упирается в пустоту.
    /// </summary>
    private void ForgetChunk(int cx, int cy, int cz)
    {
        if (!chunks.Remove((cx, cy, cz), out var slot))
            return;
        foreach (var pos in slot.Entities)
            blockEntities.TryRemove(pos, out _);
    }

    private void StoreBlockEntity(Packet_BlockEntity be)
    {
        var pos = new BlockPos(be.PosX, be.PosY, be.PosZ);
        lock (chunksLock)
        {
            // БЛОК-СУЩНОСТЬ ЖИВЁТ ПРИ ЧАНКЕ. Нет чанка — жить ей негде, и
            // класть её в общую память нельзя. Игра поступает так же:
            // GeneralPacketHandler.HandleBlockEntities молча пропускает те,
            // чей чанк не загружен
            if (!chunks.TryGetValue(Cell(pos.X, pos.Y, pos.Z).Key, out var slot))
                return;
            slot.Entities.Add(pos);
        }
        blockEntities[pos] = new BlockEntityInfo
        {
            ClassName = be.Classname ?? "",
            Pos = pos,
            RawData = be.Data
        };
    }

    // ======================================================================
    //  ПОВЕРХНОСТЬ МИРА: ДОСТАЁТ ЛИ СЮДА НЕБО И НАСКОЛЬКО ГЛУБОКО Я СИЖУ
    //
    //  ЧЕСТНО ОБ ИСТОЧНИКЕ (разобрано по сборкам игры, а не по догадке):
    //  • сервер шлёт карту столбца ОТДЕЛЬНЫМ пакетом 17 и по одному разу на
    //    колонку каждому клиенту: ServerMapChunk.ToPacket ставит Id = 17 и
    //    кладёт RainHeightMap = ArrayConvert.UshortToByte(RainHeightMap), а
    //    ServerSystemLoadAndSaveGame шлёт её перед самими чанками и помечает
    //    ConnectedClient.SetMapChunkSent, чтобы больше не слать;
    //  • настоящий клиент кладёт её к себе ровно так же и БОЛЬШЕ НИКОГДА НЕ
    //    ПЕРЕСЧИТЫВАЕТ: ClientMapChunk.UpdateFromPacket — единственное место
    //    во всей игре, где полю rainheightmap что-то присваивается. Значит
    //    наша карта поверхности так же свежа, как у живого игрока, и ни на
    //    блок точнее: срубил кто-то дерево — у обоих останется прежняя высота,
    //    пока сервер не пришлёт колонку заново (ResendMapChunk).
    //  • ЧТО В НЕЙ ЛЕЖИТ — словами самой игры (IMapChunk.RainHeightMap): «the
    //    position of the last block that is not rain permeable before the
    //    first airblock», то есть верх того, что закрывает колонку от дождя:
    //    земля, крыша дома, листва. Отсюда и мерка «небо достаёт»: игра всюду
    //    сравнивает GetRainMapHeightAt(pos) <= pos.Y — и в грядке
    //    (BlockEntityFarmland.skyExposed), и в снегопаде, и в намокании игрока
    //    (EntityBehaviorBodyTemperature).
    //
    //  ЭТО МЕХАНИЗМ И ТОЛЬКО ОН. Сколько блоков под поверхностью считать
    //  «пещерой» — вопрос не к миру, а к тому, кто спрашивает: в открытом
    //  карьере в три слоя солнце достаёт до дна, а в штольне на двадцати
    //  блоках — нет. Это число живёт ручкой роли.
    // ======================================================================

    private readonly Dictionary<(int cx, int cz), ushort[]> surface = new();
    private readonly object surfaceLock = new();

    /// <summary>Сколько колонок карты поверхности помним (для окна управления).</summary>
    public int SurfaceColumnCount { get { lock (surfaceLock) return surface.Count; } }

    private void StoreSurface(Packet_ServerMapChunk mc)
    {
        // Короче колонки — врать про поверхность нечем: лучше «не знаю»
        if (mc.RainHeightMap is not { } raw || raw.Length < ChunkSize * ChunkSize * 2)
            return;
        var map = new ushort[ChunkSize * ChunkSize];
        // Игра переводит байты в числа простым копированием памяти
        // (ArrayConvert.ByteToUshort → Buffer.MemoryCopy), то есть порядком
        // байт машины. BitConverter.ToUInt16 делает ровно то же самое
        for (int i = 0; i < map.Length; i++)
            map[i] = BitConverter.ToUInt16(raw, i * 2);
        lock (surfaceLock)
            surface[(mc.ChunkX, mc.ChunkZ)] = map;
    }

    /// <summary>
    /// ВЫСОТА ПОВЕРХНОСТИ НАД ЭТОЙ КОЛОНКОЙ — верх последнего блока, который
    /// не пропускает дождь (крыша, листва, земля). null — карту этой колонки
    /// сервер ещё не прислал, и это НЕ «поверхность на нуле»: спрашивающий
    /// обязан решить сам.
    /// </summary>
    public int? SurfaceYAt(int x, int z)
    {
        // Ключ колонки и место в ней берём тем же единственным правилом, что
        // и клетку блока (см. Cell): при y = 0 его индекс — это ровно
        // z * 32 + x, то есть та же раскладка, что читает сама игра
        // (BlockAccessorBase.GetRainMapHeightAt: posZ % 32 * 32 + posX % 32)
        var (key, index) = Cell(x, 0, z);
        lock (surfaceLock)
            return surface.TryGetValue((key.cx, key.cz), out var map) && index < map.Length
                ? map[index]
                : null;
    }

    /// <summary>
    /// НА СКОЛЬКО БЛОКОВ КЛЕТКА НИЖЕ ПОВЕРХНОСТИ. Ноль и меньше — небо
    /// достаёт сюда (это ровно сравнение самой игры,
    /// <c>GetRainMapHeightAt(pos) &lt;= pos.Y</c>). null — карта колонки не
    /// приходила.
    /// </summary>
    public int? DepthUnderSky(int x, int y, int z) =>
        SurfaceYAt(x, z) is { } top ? top - y : null;

    /// <summary>То же по клетке.</summary>
    public int? DepthUnderSky(BlockPos pos) => DepthUnderSky(pos.X, pos.Y, pos.Z);

    /// <summary>Достаёт ли небо до клетки (null — не знаем).</summary>
    public bool? UnderSky(int x, int y, int z) =>
        DepthUnderSky(x, y, z) is { } depth ? depth <= 0 : null;

    /// <summary>То же по клетке.</summary>
    public bool? UnderSky(BlockPos pos) => UnderSky(pos.X, pos.Y, pos.Z);

    // ======================================================================
    //  БЕРЕГА ПАМЯТИ
    //
    //  ЗАМЕРЕНО, А НЕ ПРИДУМАНО (VsBotKit.Tests, 11.08). Один чанк 32×32×32:
    //    • как пришёл, сжатый и не читанный — 1103 Б (из них 613 Б сами байты);
    //    • после ПЕРВОГО ЖЕ чтения любой клетки — 132 873 Б, потому что
    //      разжатие рождает int[32768] = 131 072 Б;
    //    • первый вопрос про воду добавляет второй такой же массив.
    //  То есть 99,2 % веса чанка — это РАЗЖАТЫЙ КЭШ, и он никогда не
    //  выбрасывался: разжали однажды — держим до выгрузки чанка сервером.
    //
    //  ОТСЮДА ДВА РАЗНЫХ РЫЧАГА, и путать их нельзя:
    //
    //  1. ВЫБРОСИТЬ РАЗЖАТОЕ (<see cref="DecodedChunkBudget"/>) — БЕЗ ЕДИНОГО
    //     ВРАНЬЯ. Чанк остаётся известным, <see cref="IsKnown"/> по-прежнему
    //     «да», ответы те же до последнего блока: массив пересчитывается из
    //     сжатых байтов, которые никуда не делись. Платим временем на повторное
    //     разжатие, а не правдой. Это главный рычаг: 130 КБ → 1,1 КБ.
    //
    //  2. ЗАБЫТЬ ЧАНК ЦЕЛИКОМ (<see cref="KeepChunkRadius"/>) — это уже потеря
    //     знания, и она обязана быть ЧЕСТНОЙ: забытое место отвечает
    //     «я про него не знаю» (<see cref="UnknownBlock"/>, IsKnown = false,
    //     GetBlockCode = null, IsPassable = false), а НЕ «там пусто». Забывание
    //     идёт через <see cref="ForgetChunk"/>, то есть вместе с чанком уходят
    //     и его сундуки, — иначе склад пошёл бы открывать пустое место.
    // ======================================================================

    /// <summary>
    /// Меньше этого радиус памяти не бывает: 2 чанка = 64 блока вокруг бота.
    ///
    /// Ниже начинается вред, а не экономия. Незнание для поиска пути означает
    /// «туда нельзя» (см. <see cref="IsPassable"/>), поэтому урезанный до
    /// одного чанка берег дал бы бота, который упирается в собственную слепоту
    /// в тридцати шагах от себя, — а выглядело бы это как поломка поиска пути,
    /// и искали бы её не там. 64 блока — это ещё и вдвое больше, чем видит
    /// шаг физики и локальный обход препятствий.
    /// </summary>
    public const int MinKeepChunkRadius = 2;

    private int keepChunkRadius;
    private readonly BotClient owner;
    private long droppedDecoded;
    private long forgottenFar;
    private long lastNoteAt;

    /// <summary>
    /// РАДИУС ПАМЯТИ В ЧАНКАХ — он же то, сколько мира бот ПРОСИТ у сервера.
    /// 0 (по умолчанию) — не забывать ничего, прежнее поведение.
    ///
    /// ОДНО ЧИСЛО, А НЕ ДВА, и это здесь главное. Попроси у сервера больше,
    /// чем держишь, — и лишнее придётся выбрасывать; а второй раз тех же
    /// чанков сервер НЕ ПРИШЛЁТ: он помнит, что уже отдал их, и шлёт заново
    /// только после собственной выгрузки. Посреди прогруженной местности
    /// осталась бы вечная дыра «не знаю» — честная, но взявшаяся из ничего.
    /// Поэтому берег памяти сам убавляет запрос к серверу
    /// (<see cref="BotClient.ViewDistance"/>), и просить бот будет ровно
    /// столько, сколько собирается помнить.
    ///
    /// РАДИУС ГОРИЗОНТАЛЬНЫЙ, И ЭТО НЕ НЕДОДЕЛКА, А ТО ЖЕ САМОЕ ПРАВИЛО.
    /// Запрошенная дальность (ViewDistance, блоки) — величина горизонтальная:
    /// вертикального запроса в протоколе нет вовсе, сервер отдаёт чанки
    /// ПОЛНЫМИ СТОЛБАМИ по всей высоте мира (MapSizeY 256 = 8 слоёв по 32).
    /// Значит забывать по высоте — это как раз просить больше, чем помнишь:
    /// выброшенный слой сервер не пришлёт заново (он его не выгружал, а
    /// перезапроса чанков у бота нет), и бот, спустившийся в карьер и
    /// поднявшийся обратно, навсегда ослеп бы над головой. Поэтому берег
    /// меряется только по X и Z (<see cref="BeyondKeep"/>), а столб держится
    /// целиком. Памяти это стоит ровно высоты мира: (2r+1)² столбов × 8 слоёв,
    /// и потолок веса всё равно держит <see cref="DecodedChunkBudget"/>.
    ///
    /// Ставить ДО входа на сервер: запрошенная дальность уходит в самом
    /// первом пакете опознания.
    /// </summary>
    public int KeepChunkRadius
    {
        get => keepChunkRadius;
        set
        {
            keepChunkRadius = value <= 0 ? 0 : Math.Max(MinKeepChunkRadius, value);
            if (keepChunkRadius > 0)
                owner.ViewDistance = keepChunkRadius * ChunkSize;
        }
    }

    /// <summary>
    /// Сколько чанков держать РАЗЖАТЫМИ. 0 (по умолчанию) — сколько угодно.
    ///
    /// Правдой за это не платят вовсе (см. заголовок раздела): выброшенный
    /// массив пересчитывается из сжатых байтов при первом же чтении. Платят
    /// временем — и только тем, кто ушёл дальше всех.
    /// </summary>
    public int DecodedChunkBudget { get; set; }

    /// <summary>
    /// Где сейчас бот. От этой точки считается, какие чанки «дальние»: и кого
    /// забыть по <see cref="KeepChunkRadius"/>, и чей разжатый массив выбросить
    /// первым по <see cref="DecodedChunkBudget"/>.
    ///
    /// НЕ ЗАДАНО ИЛИ ОТВЕТИЛО null («я не знаю, где я») — забывание по берегу
    /// НЕ ИДЁТ ВОВСЕ, а бюджет разжатых считает дальним того, кто пришёл
    /// раньше всех. Так нарочно: центр «наверное, ноль» стёр бы весь мир
    /// разом, потому что от начала координат далеко решительно всё. Бот не
    /// знает, где стоит, ровно в те секунды после входа, когда чанки и
    /// приезжают, — цена ошибки тут наибольшая.
    /// </summary>
    public Func<BlockPos?>? Center { get; set; }

    /// <summary>Сказать вслух про уборку. Молчаливая уборка неотличима от потери памяти.</summary>
    public event Action<string>? OnNote;

    /// <summary>Сколько чанков забыто по берегу за сеанс.</summary>
    public long ForgottenFarChunks => Interlocked.Read(ref forgottenFar);

    /// <summary>Сколько разжатых слоёв выброшено за сеанс.</summary>
    public long DroppedDecodedLayers => Interlocked.Read(ref droppedDecoded);

    /// <summary>
    /// ЗА БЕРЕГОМ ЛИ ЧАНК — чистое правило, поэтому и проверяется тестом.
    /// Мерка «в клетку», как у самой игры: дальше radius чанков по X или по Z.
    /// Ноль и меньше означает «берега нет» — не за берегом никто.
    ///
    /// ВЫСОТЫ ЗДЕСЬ НЕТ НАРОЧНО, и координата cy в правило не входит. Берег
    /// обязан совпадать с тем, что бот ПРОСИТ у сервера, а просит он одну
    /// горизонтальную дальность (<see cref="BotClient.ViewDistance"/>) —
    /// вертикальной в протоколе нет, и чанки приходят полными столбами по всей
    /// высоте мира. Забыть слой столба значило бы выбросить то, чего второй раз
    /// не дадут: сервер этот чанк не выгружал, перезапроса чанков у бота нет
    /// (в исходящих только пакеты 33 и 34), и над головой у спустившегося в
    /// карьер бота осталась бы вечная слепота — а незнание для поиска пути
    /// значит «туда нельзя» (<see cref="IsPassable"/>).
    /// </summary>
    public static bool BeyondKeep(int cx, int cz, int centreCx, int centreCz, int radius) =>
        radius > 0 &&
        (Math.Abs(cx - centreCx) > radius ||
         Math.Abs(cz - centreCz) > radius);

    /// <summary>Чанк, в котором лежит клетка (деление вниз, как в <see cref="Cell"/>).</summary>
    public static (int cx, int cy, int cz) ChunkOf(BlockPos pos) => Cell(pos.X, pos.Y, pos.Z).Key;

    /// <summary>Сколько чанков сейчас разжато (диагностика для окна).</summary>
    public int DecodedChunkCount
    {
        get
        {
            lock (chunksLock)
            {
                int n = 0;
                foreach (var slot in chunks.Values)
                    if (slot.Decoded != null || slot.DecodedLiquids != null)
                        n++;
                return n;
            }
        }
    }

    /// <summary>
    /// СКОЛЬКО БАЙТ ДЕРЖИТ МОДЕЛЬ МИРА — не оценка, а подсчёт того, что лежит:
    /// сжатые байты как есть плюс по 131 072 Б за каждый разжатый слой.
    /// Окно управления показывает это число, чтобы «отжирает по оперативке»
    /// перестало быть ощущением и стало величиной.
    /// </summary>
    public (int Chunks, int Decoded, int Entities, long Bytes) Footprint()
    {
        lock (chunksLock)
        {
            long bytes = 0;
            int decoded = 0;
            foreach (var slot in chunks.Values)
            {
                bytes += slot.BlocksCompressed?.Length ?? 0;
                bytes += slot.LiquidsCompressed?.Length ?? 0;
                if (slot.Decoded != null) { bytes += BlocksPerChunk * 4; decoded++; }
                if (slot.DecodedLiquids != null) bytes += BlocksPerChunk * 4;
            }
            return (chunks.Count, decoded, blockEntities.Count, bytes);
        }
    }

    /// <summary>
    /// ЗАБЫТЬ ЧАНКИ ЗА БЕРЕГОМ. Возвращает, сколько забыто.
    ///
    /// Забытое место честно отвечает «не знаю»: чанк уходит через
    /// <see cref="ForgetChunk"/> вместе со своими блок-сущностями, и дальше
    /// весь мир отвечает про него ровно то же, что про место, куда бот никогда
    /// не смотрел. Это ТО ЖЕ САМОЕ забывание, что при выгрузке чанка сервером
    /// (пакет 11) и при обрыве связи, — второго правила забывания в модели
    /// мира нет и заводить его нельзя.
    /// </summary>
    public int ForgetFarChunks()
    {
        if (keepChunkRadius <= 0 || Center?.Invoke() is not { } где)
            return 0;
        var (ccx, ccy, ccz) = ChunkOf(где);

        // КАРТА ПОВЕРХНОСТИ УХОДИТ ТЕМ ЖЕ БЕРЕГОМ, что и блоки, и той же
        // меркой (BeyondKeep): второго правила забывания в модели мира нет и
        // заводить его нельзя. Иначе за смену в памяти осела бы карта всех
        // колонок, мимо которых бот прошёл; забытая колонка честно отвечает
        // «не знаю», ровно как забытый чанк.
        //
        // ВЕРНЁТСЯ ЛИ ЗНАНИЕ. Да, и по той же дороге, что у чанков: уводя
        // колонку из зоны прогрузки, сервер шлёт выгрузку (пакет 11) и тут же
        // забывает, что уже отдавал нам её карту (ConnectedClient
        // .RemoveMapChunkSent в SendOutOfRangeChunkUnloads). Вернулся бот —
        // карта приходит заново вместе с чанками. Поэтому берег здесь ровно
        // тот же: помним столько, сколько просим
        lock (surfaceLock)
        {
            List<(int cx, int cz)>? колонки = null;
            foreach (var key in surface.Keys)
                if (BeyondKeep(key.cx, key.cz, ccx, ccz, keepChunkRadius))
                    (колонки ??= []).Add(key);
            if (колонки != null)
                foreach (var key in колонки)
                    surface.Remove(key);
        }

        List<(int, int, int)>? далёкие = null;
        lock (chunksLock)
        {
            foreach (var key in chunks.Keys)
                if (BeyondKeep(key.cx, key.cz, ccx, ccz, keepChunkRadius))
                    (далёкие ??= []).Add(key);
            if (далёкие == null)
                return 0;
            foreach (var (cx, cy, cz) in далёкие)
                ForgetChunk(cx, cy, cz);
        }
        Interlocked.Add(ref forgottenFar, далёкие.Count);
        // «По горизонтали» в строке не для красоты: иначе человек, увидевший
        // забытые чанки, ищет пропажу и над головой тоже, а её там нет
        Note($"забыл {далёкие.Count} дальних чанков (берег {keepChunkRadius} чанк. по горизонтали " +
             $"вокруг ({ccx * ChunkSize}, {ccz * ChunkSize}), высота столба целиком, " +
             $"бот на высоте {ccy * ChunkSize}) — про них теперь честно «не знаю»");
        return далёкие.Count;
    }

    /// <summary>
    /// ВЫБРОСИТЬ РАЗЖАТОЕ СВЕРХ БЮДЖЕТА. Возвращает, сколько слоёв выброшено.
    ///
    /// Уходят самые дальние от бота (а без <see cref="Center"/> — самые старые),
    /// и только те, чей массив В ТОЧНОСТИ пересчитывается из сжатых байтов.
    /// Чанк с точечными правками сервера (<see cref="ChunkSlot.Dirty"/>) не
    /// трогаем никогда: у него в сжатых байтах лежит ПРЕЖНИЙ мир, и подъём
    /// оттуда вернул бы закрытую дверь и невыкопанный камень.
    /// </summary>
    public int DropColdDecoded()
    {
        int бюджет = DecodedChunkBudget;
        if (бюджет <= 0)
            return 0;

        (int cx, int cy, int cz)? центр = Center?.Invoke() is { } где ? ChunkOf(где) : null;

        List<((int cx, int cy, int cz) Key, ChunkSlot Slot, long Rank)> разжатые = [];
        lock (chunksLock)
        {
            foreach (var (key, slot) in chunks)
            {
                if (slot.Decoded == null && slot.DecodedLiquids == null)
                    continue;
                разжатые.Add((key, slot, Rank(key, slot)));
            }
            if (разжатые.Count <= бюджет)
                return 0;
        }

        // Кто дальше (или старше) — тот первым; чанк под ногами не тронем
        разжатые.Sort((a, b) => b.Rank.CompareTo(a.Rank));
        int выбросить = разжатые.Count - бюджет;
        int выброшено = 0;
        foreach (var (_, slot, _) in разжатые)
        {
            if (выбросить <= 0)
                break;
            // Замок чанка берём ОТДЕЛЬНО от общего замка карты: обратный
            // порядок («свой» под «общим») замкнул бы кольцо с разжатием
            lock (slot.Unpacking)
            {
                if (slot.Dirty || slot.BlocksCompressed == null)
                    continue;   // из сжатых байтов это уже не поднять слово в слово
                if (slot.Decoded != null) { slot.Decoded = null; выброшено++; }
                if (slot.DecodedLiquids != null) { slot.DecodedLiquids = null; выброшено++; }
            }
            выбросить--;
        }
        if (выброшено > 0)
        {
            Interlocked.Add(ref droppedDecoded, выброшено);
            Note($"выбросил {выброшено} разжатых слоёв сверх бюджета {бюджет} — " +
                 "знание цело, при следующем чтении разожму заново");
        }
        return выброшено;

        long Rank((int cx, int cy, int cz) key, ChunkSlot slot) =>
            центр is { } c
                ? Math.Max(Math.Abs(key.cx - c.cx),
                    Math.Max(Math.Abs(key.cy - c.cy), Math.Abs(key.cz - c.cz)))
                : -slot.Stamp;   // без центра «дальний» = «пришёл раньше всех»
    }

    /// <summary>Разжали ещё один чанк — проверить берега. Звать ВНЕ замка чанка.</summary>
    private void AfterDecoded()
    {
        if (DecodedChunkBudget > 0)
            DropColdDecoded();
    }

    /// <summary>
    /// Сказать про уборку, но не чаще раза в минуту: уборка идёт постоянно, и
    /// строка на каждый чанк утопила бы журнал — а тонущий журнал не читают.
    /// </summary>
    private void Note(string what)
    {
        if (OnNote == null)
            return;
        long теперь = Environment.TickCount64;
        long было = Interlocked.Read(ref lastNoteAt);
        if (было != 0 && теперь - было < 60_000)
            return;
        Interlocked.Exchange(ref lastNoteAt, теперь);
        OnNote.Invoke(what);
    }

    /// <summary>
    /// Ответ «я про эту клетку ничего не знаю»: чанк к боту ещё не приходил
    /// или уже выгружен. Это НЕ воздух — см. <see cref="BlockIdOrUnknown"/>.
    /// </summary>
    public const int UnknownBlock = -1;

    /// <summary>
    /// ЧЕСТНЫЙ ID БЛОКА: <see cref="UnknownBlock"/> (−1), если этого куска мира
    /// у бота нет, 0 — если там ТОЧНО воздух.
    ///
    /// Зачем это отдельный ответ. Живой случай: склад доложил «под склад взято
    /// сундуков: 16», а через восемь секунд про ту же клетку сказал «air …:
    /// не открылся». «Воздух» был вымыслом: чанк выгрузили, и модель мира на
    /// любой вопрос про него отвечала нулём — то есть пустотой. По той же
    /// причине поиск пути строил дорогу «по пустой карте»: неизвестные клетки
    /// он считал проходимыми и прыгал через ямы, которых не существует.
    ///
    /// Все вопросы о проходимости идут теперь отсюда, а
    /// <see cref="GetBlockId(int,int,int)"/> оставлен прежним (0 за незнание)
    /// ради тех, кому разница не важна.
    /// </summary>
    public int BlockIdOrUnknown(int x, int y, int z)
    {
        var slot = SlotAt(x, y, z, out int index);
        if (slot == null)
            return UnknownBlock;
        if (slot.Empty)
            return 0;

        var decoded = slot.Decoded;
        if (decoded == null)
        {
            if (slot.BlocksCompressed == null)
                return 0;
            bool разжали = false;
            lock (slot.Unpacking)
            {
                // Пока ждали замок, разжать мог кто-то другой — и в его массив
                // уже могли лечь точечные правки. Свой второй экземпляр их бы
                // стёр, поэтому берём чужой
                decoded = slot.Decoded;
                if (decoded == null)
                {
                    decoded = new int[BlocksPerChunk];
                    lock (unpackLock)
                        UnpackBlocks(decoded, slot.BlocksCompressed, null, slot.Compver);
                    slot.Decoded = decoded;
                    разжали = true;
                }
            }
            // Берега проверяем ВНЕ замка чанка: сама уборка берёт общий замок
            // карты, и порядок «свой замок → общий» замкнул бы кольцо
            if (разжали)
                AfterDecoded();
        }
        return decoded[index];
    }

    /// <summary>Id блока в мировых координатах; 0 (воздух), если чанк не загружен.</summary>
    public int GetBlockId(int x, int y, int z)
    {
        int id = BlockIdOrUnknown(x, y, z);
        return id < 0 ? 0 : id;
    }

    public int GetBlockId(BlockPos pos) => GetBlockId(pos.X, pos.Y, pos.Z);

    /// <summary>
    /// Пришёл ли к боту этот кусок мира. Спрашивать обязан всякий, кому важна
    /// разница между «там пусто» и «я туда не смотрел»: склад, поиск пути,
    /// добыча — все три от этой разницы уже пострадали.
    /// </summary>
    public bool IsKnown(int x, int y, int z) => SlotAt(x, y, z, out _) != null;

    /// <inheritdoc cref="IsKnown(int,int,int)"/>
    public bool IsKnown(BlockPos pos) => IsKnown(pos.X, pos.Y, pos.Z);

    /// <summary>
    /// Id жидкости в клетке (0 — нет). Вода и лава лежат отдельным слоем
    /// чанка и сжаты тем же кодеком, что и блоки.
    /// </summary>
    public int GetLiquidId(int x, int y, int z)
    {
        var slot = SlotAt(x, y, z, out int index);
        if (slot == null || slot.Empty)
            return 0;

        var decoded = slot.DecodedLiquids;
        if (decoded == null)
        {
            if (slot.LiquidsCompressed == null)
                return 0;
            bool разжали = false;
            lock (slot.Unpacking)
            {
                decoded = slot.DecodedLiquids;
                if (decoded == null)
                {
                    decoded = new int[BlocksPerChunk];
                    lock (unpackLock)
                        UnpackBlocks(decoded, slot.LiquidsCompressed, null, slot.Compver);
                    slot.DecodedLiquids = decoded;
                    разжали = true;
                }
            }
            if (разжали)
                AfterDecoded();
        }
        return decoded[index];
    }

    /// <summary>Код жидкости в клетке ("water-still-7", "lava-still-7") или null.</summary>
    public string? GetLiquidCode(int x, int y, int z)
    {
        int id = GetLiquidId(x, y, z);
        return id > 0 && id < blockCodes.Length ? blockCodes[id] : null;
    }

    /// <summary>
    /// МОЕТСЯ ЛИ ЭТОТ БЛОК ЛОТКОМ — по атрибуту реестра «pannable», ровно тому,
    /// по которому решает сама игра (<c>BlockPan.IsPannableMaterial</c>).
    /// </summary>
    public bool IsPannable(string? code) =>
        code != null && BlockCodeToId(code) is { } id &&
        id > 0 && id < blockPannable.Length && blockPannable[id];

    /// <summary>
    /// Во что превращается клетка, когда лоток забрал из неё горсть (атрибут
    /// «pannedBlock»; null — у блока его нет, и слой убывает по своему коду).
    /// </summary>
    public string? PannedInto(string? code) =>
        code != null && BlockCodeToId(code) is { } id &&
        id > 0 && id < blockPannedInto.Length ? blockPannedInto[id] : null;

    /// <summary>
    /// Коды блоков-ЛОТКОВ и их таблицы выпадений строкой JSON — как прислал
    /// сервер. Разбирает её <see cref="Panning"/>: реестр только хранит.
    /// </summary>
    public IReadOnlyDictionary<string, string> PanningDrops => panningDropsJson;

    /// <summary>Вода, в которой можно плавать (не лава и не кипяток).</summary>
    public bool IsWater(int x, int y, int z)
    {
        int id = GetLiquidId(x, y, z);
        return id > 0 && id < blockIsWater.Length && blockIsWater[id];
    }

    /// <summary>
    /// Виден ли блок ГЛАЗАМИ: хоть одна его грань выходит в прозрачную клетку.
    ///
    /// Зачем это нужно. Сервер шлёт чанк целиком, и бот знает КАЖДЫЙ блок
    /// внутри камня — включая руду, которую живой игрок увидеть не может
    /// никак. Это самое крупное преимущество бота над человеком, и оно было
    /// у нас включено молча, даже не попав в список читов.
    ///
    /// Игрок находит руду по выходам: в пещере, на обрыве, в стене шахты.
    /// Ровно это здесь и проверяется — есть ли у блока открытая грань.
    /// </summary>
    public bool IsExposed(int x, int y, int z)
    {
        return See(x + 1, y, z) || See(x - 1, y, z) ||
               See(x, y + 1, z) || See(x, y - 1, z) ||
               See(x, y, z + 1) || See(x, y, z - 1);

        // Сквозь воду и листву видно, сквозь камень нет
        bool See(int bx, int by, int bz)
        {
            int id = BlockIdOrUnknown(bx, by, bz);
            // НЕИЗВЕСТНАЯ КЛЕТКА — НЕ ОКНО. Пока незнание считалось воздухом,
            // честное зрение работало наизнанку: у выгруженного чанка все шесть
            // граней «открыты», и сундуки, которых там давно нет, бот считал
            // видимыми — а стоило чанку прийти, они честно прятались за стеной
            if (id < 0)
                return false;
            return id == 0 || IsWater(bx, by, bz) || IsPassable(bx, by, bz);
        }
    }

    /// <summary>
    /// ЧЕМ КЛЕТКА ВРЕДИТ ТОМУ, КТО В НЕЙ ОКАЗАЛСЯ — единственный вопрос об
    /// этом на весь проект. Что считать вредом, решает <see cref="HarmRules"/>
    /// один раз при получении реестра; здесь только два слоя.
    ///
    /// СЛОЁВ ИМЕННО ДВА, и это не перестраховка. Жидкость игра держит ОТДЕЛЬНО
    /// от блока: в одной и той же клетке бывают и лава (слой жидкости), и
    /// костёр (слой блоков). Спроси только про блок — и лава станет
    /// безопасной; спроси только про жидкость (а именно так и было) — и в
    /// собственном костре можно сгореть, что и случилось.
    /// </summary>
    public CellHarm HarmAt(int x, int y, int z) => (CellHarm)WorstHarm(x, y, z).Harm;

    /// <summary>
    /// КАК ЗОВЁТСЯ ТО, ЧТО В ЭТОЙ КЛЕТКЕ ВРЕДИТ (null — не вредит ничто).
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО. Бот в луже лавы писал в журнал
    /// «стою в огне (air в (x, y, z)) — ухожу», и это враньё ровно про то, что
    /// заказчик назвал первым делом. Причина: имя брали через
    /// <see cref="GetBlockCode(int,int,int)"/>, а тот читает ТОЛЬКО слой блоков — лава же и
    /// кипяток лежат в слое жидкостей, и в слое блоков над ними честный
    /// воздух. Вред при этом считался по обоим слоям, а имя — по одному.
    ///
    /// ВТОРОГО ПРАВИЛА ЗДЕСЬ НЕТ: какой слой хуже, решает тот же
    /// <see cref="WorstHarm"/>, что отвечает и на <see cref="HarmAt"/>. Имя
    /// берётся у ТОГО ЖЕ слоя, который дал ответ, — разойтись им нечем.
    /// </summary>
    public string? HarmCodeAt(int x, int y, int z)
    {
        var (harm, id) = WorstHarm(x, y, z);
        return harm == 0 || id <= 0 || id >= blockCodes.Length ? null : blockCodes[id];
    }

    /// <summary>Худший из двух слоёв: сам вред и id блока, который его даёт.</summary>
    private (byte Harm, int Id) WorstHarm(int x, int y, int z)
    {
        int block = BlockIdOrUnknown(x, y, z);
        int liquid = GetLiquidId(x, y, z);
        byte worst = 0;
        int who = 0;
        if (block > 0 && block < blockHarm.Length && blockHarm[block] > 0)
        {
            worst = blockHarm[block];
            who = block;
        }
        if (liquid > 0 && liquid < blockHarm.Length && blockHarm[liquid] > worst)
        {
            worst = blockHarm[liquid];
            who = liquid;
        }
        return (worst, who);
    }

    /// <summary>
    /// Опасная ЖИДКОСТЬ в клетке: лава, кипяток. Вопрос узкий нарочно — им
    /// живут копатели: «вскроешь этот блок, и оттуда польётся». Костёр,
    /// стоящий рядом с забоем, штольню не затопит, и в этот ответ он попадать
    /// не должен.
    ///
    /// ВТОРОЙ КОПИИ ПРАВИЛА ТУТ НЕТ: «вредно ли это» решает та же таблица
    /// <see cref="HarmAt"/>, просто спрошенная про один слой — слой жидкости.
    /// «Кому ходить нельзя» спрашивают <see cref="HarmRules.HurtsToEnter"/> и
    /// <see cref="HarmRules.HurtsToStandIn"/>.
    /// </summary>
    public bool IsDangerousLiquid(int x, int y, int z)
    {
        int id = GetLiquidId(x, y, z);
        return id > 0 && id < blockHarm.Length &&
               (CellHarm)blockHarm[id] == CellHarm.Always;
    }

    /// <summary>
    /// Опасная жидкость В клетке или ВПЛОТНУЮ к ней — по всем шести граням.
    ///
    /// Зачем вопрос именно такой. Тому, кто собрался ломать блок, важна не
    /// только сама клетка: за камнем может стоять лава, и вскрытие пустит её
    /// внутрь. Это правило одинаково для карьера, штольни и любой другой
    /// копки, поэтому живёт здесь, в вопросах о мире, а не в каждом копателе
    /// своей копией.
    ///
    /// Возвращает клетку с жидкостью и признак «это лава» (иначе вода).
    /// </summary>
    /// <param name="water">Считать ли опасной и обычную воду (карьер зальёт).</param>
    public (BlockPos At, bool Lava)? LiquidNear(BlockPos p, bool water = true)
    {
        if (IsDangerousLiquid(p.X, p.Y, p.Z))
            return (p, true);
        if (water && IsWater(p.X, p.Y, p.Z))
            return (p, false);

        (int X, int Y, int Z)[] faces =
            [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        foreach (var (dx, dy, dz) in faces)
        {
            int x = p.X + dx, y = p.Y + dy, z = p.Z + dz;
            if (IsDangerousLiquid(x, y, z))
                return (new BlockPos(x, y, z), true);
            if (water && IsWater(x, y, z))
                return (new BlockPos(x, y, z), false);
        }
        return null;
    }

    /// <summary>
    /// Поверхность воды: здесь можно плыть — вода под ногами, голова над водой
    /// (сверху воздух, а не вода и не потолок).
    /// </summary>
    public bool IsWaterSurface(int x, int y, int z) =>
        IsWater(x, y, z) && !IsWater(x, y + 1, z) && IsPassable(x, y + 1, z);

    /// <summary>
    /// Код блока ("game:soil-medium-normal"). null — сказать нечего: либо
    /// реестр ещё не пришёл, либо ЧАНК НЕ ПРИХОДИЛ.
    ///
    /// Второй случай раньше отвечал «air», и это было прямое враньё: заказчик
    /// стоял у сундуков и читал в журнале «air (512061, 112, 512255)». Кому
    /// нужна именно эта разница — спрашивает <see cref="IsKnown(int,int,int)"/>.
    /// </summary>
    public string? GetBlockCode(int x, int y, int z)
    {
        int id = BlockIdOrUnknown(x, y, z);
        return id >= 0 && id < blockCodes.Length ? blockCodes[id] : null;
    }

    public string? GetBlockCode(BlockPos pos) => GetBlockCode(pos.X, pos.Y, pos.Z);

    public string? BlockIdToCode(int id) => id >= 0 && id < blockCodes.Length ? blockCodes[id] : null;

    public int? BlockCodeToId(string code) => codeToId.TryGetValue(code, out int id) ? id : null;

    /// <summary>Как у блока хранится состояние двери (считается один раз по реестру).</summary>
    private enum DoorKind : byte
    {
        None = 0,
        OpenByCode = 1,    // старая дверь: «-opened-» в коде, при повороте блок подменяется
        ClosedByCode = 2,  // старая дверь: «-closed-» в коде
        ByEntity = 3,      // новая дверь: код неизменен, состояние в блок-сущности
    }

    /// <summary>
    /// Позиция «хозяина» мультиблока. Дверь высотой 2 ставит в верхнюю клетку
    /// служебный блок multiblock-monolithic-{dx}-{dy}-{dz}, где смещение
    /// записано ОТ ХОЗЯИНА к этой клетке: «n1» = −1, «p1» = +1, «0» = 0
    /// (см. BlockBehaviorDoor.placeMultiblockParts в исходниках игры).
    /// </summary>
    private BlockPos? MultiblockMaster(int x, int y, int z)
    {
        if (GetBlockCode(x, y, z) is not { } code ||
            !code.StartsWith("multiblock-", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = code.Split('-');
        if (parts.Length < 5 ||
            !TryOffset(parts[^3], out int dx) ||
            !TryOffset(parts[^2], out int dy) ||
            !TryOffset(parts[^1], out int dz))
            return null;
        return new BlockPos(x - dx, y - dy, z - dz);

        static bool TryOffset(string s, out int value)
        {
            value = 0;
            if (s.Length == 0)
                return false;
            int sign = s[0] switch { 'n' or 'N' => -1, 'p' or 'P' => 1, _ => 0 };
            if (!int.TryParse(sign == 0 ? s : s[1..], out int n))
                return false;
            value = sign == 0 ? n : sign * n;
            return true;
        }
    }

    /// <summary>
    /// Дверь в клетке и где лежит её «хозяин». Верхняя половина новой двери —
    /// это служебный блок мультиблока, а не дверь: без такого разыменования
    /// бот считает верх дверного проёма глухой стеной и не проходит.
    /// </summary>
    private (DoorKind Kind, BlockPos Owner)? DoorAt(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        if (id > 0 && id < blockDoorKind.Length && blockDoorKind[id] != 0)
            return ((DoorKind)blockDoorKind[id], new BlockPos(x, y, z));

        if (MultiblockMaster(x, y, z) is { } m)
        {
            int mid = GetBlockId(m.X, m.Y, m.Z);
            if (mid > 0 && mid < blockDoorKind.Length && blockDoorKind[mid] != 0)
                return ((DoorKind)blockDoorKind[mid], m);
        }
        return null;
    }

    /// <summary>Дверь, калитка или люк в этой клетке.</summary>
    public bool IsDoor(int x, int y, int z) => DoorAt(x, y, z) != null;

    /// <summary>
    /// Открыта ли дверь. У старых дверей состояние вморожено в код блока и
    /// меняется подменой блока, у новых код неизменен, а состояние живёт
    /// в блок-сущности. Тип определён по реестру заранее, поэтому гадать
    /// по строке на каждый вызов не нужно.
    /// </summary>
    public bool IsDoorOpen(int x, int y, int z)
    {
        if (DoorAt(x, y, z) is not { } d)
            return false;
        return d.Kind switch
        {
            DoorKind.OpenByCode => true,
            DoorKind.ClosedByCode => false,
            _ => GetBlockEntity(d.Owner)?.Attributes?.GetBool("opened") == true,
        };
    }

    /// <summary>
    /// Где стоять НА ТОРЦЕ лестницы. Плита тонкая (0.1875) и прижата к одной
    /// грани клетки: встав в центр, опоры под ногами не найдёшь. Берём центр
    /// НАСТОЯЩЕГО бокса блока — он уже повёрнут под вариант, поэтому гадать
    /// по суффиксу кода («-north»/«-west») не нужно и моды тоже сработают.
    /// </summary>
    public (double X, double Z) LadderStandPoint(int x, int y, int z)
    {
        if (GetGameBlock(x, y, z)?.CollisionBoxes is { Length: > 0 } boxes)
        {
            var b = boxes[0];
            return (x + (b.X1 + b.X2) / 2.0, z + (b.Z1 + b.Z2) / 2.0);
        }
        return (x + 0.5, z + 0.5);
    }

    /// <summary>Куда кликать: в клетку хозяина двери, а не в служебный блок.</summary>
    public BlockPos DoorClickPos(BlockPos door) =>
        DoorAt(door.X, door.Y, door.Z)?.Owner ?? door;

    // ---------------- дверной проём: коробки, а не «пустая клетка» ----------------

    /// <summary>
    /// КОРОБКИ СТВОРКИ В ТОЙ ПОЗЕ, В КОТОРОЙ БОТ БУДЕТ ЧЕРЕЗ НЕЁ ИДТИ, — то есть
    /// у РАСПАХНУТОЙ двери, даже если сейчас она закрыта: закрытую бот по дороге
    /// откроет, и мерить проём по закрытой створке значит мерить не то.
    ///
    /// Пусто — мерить нечем, и спрашивающий обязан считать, что проём свободен
    /// (так было всегда): у старых дверей и калиток каждое состояние — отдельный
    /// блок, и коробки распахнутого варианта из закрытого не вывести иначе как
    /// подменой строк в коде («closed» → «opened»), а выдуманные списки и
    /// приставки в этом проекте запрещены — на них он горел трижды.
    /// </summary>
    private Vintagestory.API.MathTools.Cuboidf[] OpenDoorBoxes(int x, int y, int z)
    {
        if (DoorAt(x, y, z) is not { } d || d.Kind != DoorKind.ByEntity)
            return [];
        if (GetBlockEntity(d.Owner)?.Attributes is not { } attrs)
            return [];
        int ownerId = GetBlockId(d.Owner.X, d.Owner.Y, d.Owner.Z);
        // У ЛЮКА ГОРИЗОНТАЛЬНОГО ПРОЁМА НЕТ ВОВСЕ: сквозь него ходят ВВЕРХ и
        // ВНИЗ, а не вбок. Мерить его створкой ту полосу, по которой обходят
        // дверь плечом, значит отвечать не на тот вопрос
        if (IsTrapdoorId(ownerId))
            return [];
        int rot = (int)Math.Round(attrs.GetFloat("rotateYRad") / (Math.PI / 2)) & 3;
        var open = doorBlockCache.GetOrAdd(
            (ownerId, rot, true, attrs.GetBool("invertHandles"), НеЛюк), BuildDoorBlock);
        return open?.CollisionBoxes ?? [];
    }

    /// <summary>
    /// СВОБОДНАЯ ПОЛОСА В КЛЕТКЕ ДВЕРНОГО ПРОЁМА, в долях клетки: (X0…X1) × (Z0…Z1).
    ///
    /// ЖИВОЙ СЛУЧАЙ, ЖУРНАЛ ЗАКАЗЧИКА 23.08 (10:59:57 — 11:02, двенадцать раз):
    /// <code>
    ///   упёрся на точке 4/9 (61, 112, 251), стою (61,5, 112, 252,3)
    ///   до точки (61, 112, 251) было 2 бл по прямой — её поставило спрямление,
    ///            а тело там не проходит. Дальше строю без спрямления
    ///   открыл дверь (61, 112, 252) … упёрся на точке 6/24 (61, 112, 251) …
    /// </code>
    /// Бот минутами открывал и закрывал одну дверь, упираясь в одну клетку.
    ///
    /// ПОЧЕМУ. Про дверь в проекте было ДВА ответа. Тело спрашивало настоящие
    /// коробки (<see cref="PhysicsBlock"/> → <see cref="DoorPhysicsBlock"/>:
    /// створка 0,125 у грани, повёрнутая на rotateYRad и ещё на ∓90° у
    /// открытой). А поиск пути, спрямление и <see cref="IsEnterable"/>
    /// спрашивали <see cref="IsPassableDoor"/> — и тот отвечал «да» у ЛЮБОЙ
    /// незапертой двери, ни разу не взглянув на коробку; вдобавок
    /// <see cref="CollisionTopOf"/> у открытой створки возвращает 0, то есть
    /// «клетка пуста». Для луча руки коробка была, для маршрута — нет.
    ///
    /// Отсюда и вторая половина беды: ходьба целится в СЕРЕДИНУ клетки
    /// (<c>wp.X + 0.5</c>), а в проёме свободна не середина, а полоса рядом со
    /// створкой. Створки с двух сторон (двустворчатая дверь заказчика в
    /// (61,112,252) и (62,112,252)) оставляют посередине 0,75 при ширине тела
    /// 0,6 — три четверти дециметра запаса на каждое плечо, и любое смещение
    /// это «упёрся».
    ///
    /// Считаем как в игре: створка — ТОНКАЯ ПЛИТА, ПРИЖАТАЯ К ГРАНИ клетки.
    /// Прижата к малой грани — просвет начинается за ней, к большой — кончается
    /// перед ней. Коробку, не прижатую ни к одной (створка поперёк клетки),
    /// честно объявляем непроходимой, а не гадаем.
    /// </summary>
    public (double X0, double X1, double Z0, double Z1) DoorwayLane(int x, int y, int z)
    {
        // Ноги и голова: створка высотой в два блока, и мешать может любая
        var feet = OpenDoorBoxes(x, y, z);
        var head = OpenDoorBoxes(x, y + 1, z);
        // Мерить нечем — отвечаем как отвечали всегда: проём свободен. Заодно
        // это самый частый случай (обычная дверь, калитка), и лишней работы на
        // каждой клетке маршрута он не стоит
        if (feet.Length == 0 && head.Length == 0)
            return (0, 1, 0, 1);
        return LaneFromBoxes(feet.Concat(head));
    }

    /// <summary>
    /// ЧИСТАЯ ПОЛОВИНА <see cref="DoorwayLane"/>: коробки на вход — свободная
    /// полоса на выход. Без мира, без сети, без блок-сущностей — её и стерегут
    /// стенды. Числа коробок берутся из реестра игры (у двери 1.22.7 створка
    /// это плита толщиной 0,125 у грани клетки), а не выписываются здесь.
    /// </summary>
    public static (double X0, double X1, double Z0, double Z1) LaneFromBoxes(
        IEnumerable<Vintagestory.API.MathTools.Cuboidf> boxes)
    {
        double x0 = 0, x1 = 1, z0 = 0, z1 = 1;
        const double Edge = 1e-3;
        foreach (var b in boxes)
        {
            bool wideX = b.X2 - b.X1 >= 1 - Edge;
            bool wideZ = b.Z2 - b.Z1 >= 1 - Edge;
            if (wideX && wideZ)
                return (0.5, 0.5, 0.5, 0.5);   // клетка занята целиком
            if (!wideX)
            {
                if (b.X1 <= Edge) x0 = Math.Max(x0, b.X2);
                else if (b.X2 >= 1 - Edge) x1 = Math.Min(x1, b.X1);
                else return (0.5, 0.5, 0.5, 0.5);   // створка поперёк — прохода нет
            }
            if (!wideZ)
            {
                if (b.Z1 <= Edge) z0 = Math.Max(z0, b.Z2);
                else if (b.Z2 >= 1 - Edge) z1 = Math.Min(z1, b.Z1);
                else return (0.5, 0.5, 0.5, 0.5);
            }
        }
        return (x0, x1, z0, z1);
    }

    /// <summary>
    /// ВЛЕЗАЕТ ЛИ ТЕЛО В ЭТУ ПОЛОСУ — коробка <see cref="BodySize.Width"/>
    /// против просвета. Своего числа ширины тут нет: оно одно на весь проект.
    /// </summary>
    public static bool BodyFitsInLane((double X0, double X1, double Z0, double Z1) lane) =>
        lane.X1 - lane.X0 >= BodySize.Width && lane.Z1 - lane.Z0 >= BodySize.Width;

    /// <summary>
    /// ВЛЕЗАЕТ ЛИ ТЕЛО В ЭТУ КЛЕТКУ ПРОЁМА — коробка <see cref="BodySize.Width"/>
    /// против настоящих коробок распахнутой створки. Не дверь — вопрос не наш,
    /// отвечаем «да».
    /// </summary>
    public bool BodyFitsInDoorway(int x, int y, int z) => BodyFitsInLane(DoorwayLane(x, y, z));

    /// <summary>
    /// КУДА ЦЕЛИТЬСЯ ТЕЛУ В ЭТОЙ КЛЕТКЕ МАРШРУТА — один ответ на весь проект.
    ///
    /// Обычно это середина клетки, и так было всегда. В дверном проёме —
    /// середина СВОБОДНОЙ ПОЛОСЫ (см. <see cref="DoorwayLane"/>): именно там
    /// проходит живой игрок, обходя створку плечом, и именно этого бот не умел.
    /// </summary>
    public (double X, double Z) WalkAimIn(int x, int y, int z)
    {
        if (!IsDoor(x, y, z))
            return (x + 0.5, z + 0.5);
        var (dx0, dx1, dz0, dz1) = DoorwayLane(x, y, z);
        return (x + (dx0 + dx1) / 2, z + (dz0 + dz1) / 2);
    }

    /// <summary>
    /// Дверь, через которую бот рассчитывает пройти: открыта или открываемая.
    /// Запертые приватом (открыть не вышло) на время выпадают из маршрутов.
    /// </summary>
    public bool IsPassableDoor(int x, int y, int z)
    {
        if (DoorAt(x, y, z) is not { } d)
            return false;
        // Люк — дверь в ПОЛУ: сквозь неё не проходят вбок, и открывать её
        // у себя под ногами точно не надо
        int id = GetBlockId(d.Owner.X, d.Owner.Y, d.Owner.Z);
        if (id > 0 && id < blockIsTrapdoor.Length && blockIsTrapdoor[id])
            return false;
        if (IsDoorLocked(d.Owner))
            return false;
        // И ГЛАВНОЕ — ВЛЕЗАЕТ ЛИ СЮДА ТЕЛО. Здесь стояло голое «дверь не
        // заперта — значит проходима», без единого взгляда на коробку створки:
        // ровно из-за этого клетка, в которую тело не входит, ПОПАДАЛА В
        // МАРШРУТ, бот упирался в неё двенадцать раз подряд и открывал-закрывал
        // одну дверь минутами (журнал 23.08, 10:59:57–11:02). Разбор — в
        // DoorwayLane
        return BodyFitsInDoorway(x, y, z);
    }

    /// <summary>
    /// Имя класса инвентаря контейнера — так его называет сервер. Берётся из
    /// атрибута блока inventoryClassName (у подписанного сундука это «chest»,
    /// хотя код блока «labeledchest»). Полный id: «класс-X, Y, Z».
    /// </summary>
    public string GetInventoryClass(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        if (id > 0 && id < blockInvClass.Length && blockInvClass[id] is { Length: > 0 } cls)
            return cls;
        return "chest"; // как значение по умолчанию в самой игре
    }

    /// <summary>Люк (дверь в полу, а не в стене).</summary>
    public bool IsTrapdoor(int x, int y, int z)
    {
        if (DoorAt(x, y, z) is not { } d)
            return false;
        int id = GetBlockId(d.Owner.X, d.Owner.Y, d.Owner.Z);
        return id > 0 && id < blockIsTrapdoor.Length && blockIsTrapdoor[id];
    }

    /// <summary>
    /// Пометить дверь как запертую (приват) — обход на 2 минуты. Ключ всегда
    /// по ХОЗЯИНУ двери: иначе пометка ставится на одну створку, а маршрут
    /// продолжает лезть через соседнюю клетку той же двери — вечный цикл
    /// </summary>
    public void MarkDoorLocked(BlockPos pos) =>
        lockedDoors[DoorClickPos(pos)] = DateTime.UtcNow.AddMinutes(2);

    /// <summary>
    /// Заперта ли дверь ПО НАШЕЙ ПАМЯТИ. Срок вышел — забываем и отвечаем
    /// «нет»: приват мог смениться, хозяин мог добавить бота в заявку.
    ///
    /// Ключ и на поиск, и на забывание — ХОЗЯИН двери. Раньше искали по
    /// хозяину, а удаляли по той клетке, которую спросили: у двухблочной двери
    /// это разные клетки, удаление не срабатывало, и просроченная пометка
    /// отвечала «заперта» снова и снова. Спасало только то, что поиск пути
    /// спрашивает по хозяину и чистил запись за всех.
    /// </summary>
    public bool IsDoorLocked(BlockPos pos)
    {
        var key = DoorClickPos(pos);
        if (!lockedDoors.TryGetValue(key, out var until))
            return false;
        if (until > DateTime.UtcNow)
            return true;
        lockedDoors.TryRemove(key, out _);
        return false;
    }

    /// <summary>
    /// Забыть все запертые двери — «попробуй ещё раз прямо сейчас».
    ///
    /// Нужно потому, что приват меняется не по расписанию: хозяин добавил бота
    /// в заявку — и ждать две минуты незачем. Зовётся из Movement.Forget вместе
    /// с остальной дорожной памятью, то есть и при самоперезапуске, и по
    /// команде человека.
    ///
    /// Возвращает, сколько дверей забыто: молчаливое «готово» тут бесполезно —
    /// человек хочет знать, было ли что забывать.
    /// </summary>
    public int ForgetLockedDoors()
    {
        int had = lockedDoors.Count;
        lockedDoors.Clear();
        return had;
    }

    /// <summary>
    /// Можно ли войти в клетку телом: она проходима, это лестница ИЛИ дверь,
    /// которую бот может открыть. У лестниц тонкая коллизия, но игрок в них
    /// заходит; дверь бот откроет по дороге.
    ///
    /// В ЛАВУ, КИПЯТОК, ОГОНЬ И ГОРЯЩИЙ КОСТЁР ВОЙТИ НЕЛЬЗЯ. Это одна правка
    /// вместо двадцати: «войти в клетку» спрашивают все — шаг, голова, трасса
    /// прыжка, лазание, проходы. Пока опасная клетка считалась проходимой (а
    /// она проходима — у лавы и у костра коллизии нет вовсе), бот планировал
    /// маршруты сквозь огонь и смотрел только под ноги.
    ///
    /// СПРАШИВАЕМ ПРО ВЕСЬ ВРЕД, А НЕ ПРО ЖИДКОСТЬ. Здесь стояло
    /// «!IsDangerousLiquid», и ровно поэтому маршрут к костру шёл В костёр:
    /// костёр — не жидкость.
    /// </summary>
    public bool IsEnterable(int x, int y, int z) =>
        (IsPassable(x, y, z) || IsClimbable(x, y, z) || IsPassableDoor(x, y, z)) &&
        !this.HurtsToEnter(x, y, z);

    /// <summary>
    /// Высота верха коллизии блока: 1.0 у полного куба, 0.5 у плиты,
    /// 0.125 у пластины транслокатора, 0 — блок без коллизии.
    /// </summary>
    public float GetCollisionTop(int x, int y, int z) =>
        CollisionTopOf(BlockIdOrUnknown(x, y, z), x, y, z);

    /// <summary>
    /// То же, но по уже известному id: спрашивающий блок дважды не читает.
    /// Неизвестная клетка (id &lt; 0) телом не мешает — но и опорой не служит,
    /// поэтому «пройти сквозь неё» решает не этот метод, а
    /// <see cref="IsPassable"/>.
    /// </summary>
    private float CollisionTopOf(int id, int x, int y, int z)
    {
        if (id <= 0 || id >= blockHasCollision.Length || !blockHasCollision[id])
            return 0;
        // ОТКРЫТАЯ ДВЕРЬ — ПРОХОД, А НЕ СТЕНА. Мы храним только высоту верха
        // коллизии, а у двери створка тонкая: открытая прижата к косяку и
        // проём свободен, но по высоте она всё равно 1.0. Без этой поправки
        // модель считает открытую дверь сплошной стеной — бот в неё «бежит»,
        // пытается перепрыгнуть и заносит проём в тупики.
        // Разыменование двери недёшево, поэтому лезем в него только у самих
        // дверных и служебных блоков — на остальной мир это не влияет
        if (((id < blockDoorKind.Length && blockDoorKind[id] != 0) ||
             (id < blockIsMultiblock.Length && blockIsMultiblock[id])) &&
            IsDoorOpen(x, y, z))
            return 0;
        // ЗАКРЫТЫЙ ЛЮК — ТОЖЕ НЕ ТО, ЧТО В РЕЕСТРЕ. В реестре у люка лежит
        // плита у НИЖНЕЙ грани клетки (y2 = 0,1875), а куда она встанет на
        // самом деле, решает грань, к которой он прибит: под потолком та же
        // плита оказывается у ВЕРХА клетки, на стене — стоймя. Врать про это
        // тем опаснее, что 0,1875 меньше шага (0,6), и закрытый люк читался
        // как «проходимо» — маршрут шёл сквозь запертый пол. Спрашиваем
        // настоящие коробки, как у тёсаных
        if (IsTrapdoorId(id) &&
            DoorPhysicsBlock(x, y, z)?.CollisionBoxes is { Length: > 0 } hatch)
        {
            float top = 0;
            foreach (var b in hatch)
                top = Math.Max(top, b.Y2);
            return top;
        }
        // ТЁСАНЫЙ БЛОК — ФОРМА НЕ В ТИПЕ, А В БЛОК-СУЩНОСТИ. Реестр про него
        // говорит «полный куб» (в ассетах у chiseledblock боксы не заданы,
        // значит Cuboidf.Default), и без этой поправки полублок-ступенька
        // читается целым кубом: бот встаёт на 1.0 вместо 0.5 и висит в
        // воздухе — ровно то, что видел заказчик. Сущности ещё нет —
        // отвечаем как сама игра: боксами типа, то есть полным кубом
        if (id < blockIsMicroBlock.Length && blockIsMicroBlock[id] &&
            MicroShapeAt(id, x, y, z) is { } micro)
            return micro.Top;
        // Старый реестр (или блок без разобранных боксов) — считаем полным кубом
        return id < blockCollisionTop.Length && blockCollisionTop[id] > 0
            ? blockCollisionTop[id]
            : 1f;
    }

    /// <summary>Тёсаный блок (микроблок) в этой клетке — форму игра держит в блок-сущности.</summary>
    public bool IsMicroBlock(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        return id > 0 && id < blockIsMicroBlock.Length && blockIsMicroBlock[id];
    }

    /// <summary>
    /// Разобранная форма тёсаного блока в клетке; null — блок-сущность к боту
    /// ещё не приехала (или её нет вовсе), и правды о форме у нас нет.
    ///
    /// БЛОК-СУЩНОСТЬ БЕРЁТСЯ НАПРЯМУЮ, минуя <see cref="GetBlockEntity"/>.
    /// Иначе получается то самое кольцо, что чуть не убило бота 09.08:
    /// IsExposed → IsPassable → GetCollisionTop → сюда → GetBlockEntity →
    /// IsExposed → … Да и по существу: форма пола под ногами — не «надпись на
    /// табличке», подглядывать тут нечего, тело обязано знать, на чём стоит.
    /// </summary>
    private MicroBlockShape? MicroShapeAt(int id, int x, int y, int z) =>
        blockEntities.TryGetValue(new BlockPos(x, y, z), out var be)
            ? be.MicroShape(id, this)
            : null;

    /// <summary>
    /// Настоящий объект блока ДЛЯ ФИЗИКИ у тёсаного блока: тот же тип, но с
    /// коробками из его блок-сущности. Тем же швом, что и
    /// <see cref="DoorPhysicsBlock"/>: игровому коду столкновений всё равно,
    /// откуда блок, — лишь бы GetCollisionBoxes говорил правду.
    /// null — подменять нечего, пусть отвечает тип (полный куб), как и в игре.
    /// </summary>
    public Vintagestory.API.Common.Block? MicroBlockPhysicsBlock(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        if (id <= 0 || id >= blockIsMicroBlock.Length || !blockIsMicroBlock[id])
            return null;
        return MicroShapeAt(id, x, y, z)?.PhysicsBlock;
    }

    // Одна разобранная форма на КАЖДЫЙ СПОСОБ РЕЗКИ, а не на каждую клетку:
    // тёсаный пол в доме — это сотни клеток и одна-две формы, а собирать
    // объект блока из пакета недёшево (текстуры, поведения, звуки)
    private readonly ConcurrentDictionary<(int Id, string Shape),
        MicroBlockShape> microShapes = new();

    internal MicroBlockShape MicroShapeOf(int blockId, uint[] cuboids)
    {
        string key = string.Join(',', cuboids);
        return microShapes.GetOrAdd((blockId, key), _ => new MicroBlockShape(
            blockId, MicroBlocks.Top(cuboids), MicroBlocks.Bottom(cuboids),
            BuildMicroBlock(blockId, cuboids)));
    }

    private Vintagestory.API.Common.Block? BuildMicroBlock(int blockId, uint[] cuboids)
    {
        if (!microPackets.TryGetValue(blockId, out var packet) ||
            rebuildRegistry == null || rebuildAccessor == null)
            return null;
        try
        {
            // Отдельный экземпляр: боксы правим только у него, общий объект
            // из реестра трогать нельзя — он один на все тёсаные блоки мира
            var block = Vintagestory.Common.BlockTypeNet.ReadBlockTypePacket(
                packet, rebuildAccessor, rebuildRegistry);
            block.BlockId = blockId;
            var boxes = MicroBlocks.Boxes(cuboids);
            block.CollisionBoxes = boxes;
            block.SelectionBoxes = boxes;
            // Частицы игре не нужны, но пустой массив тут честнее старого куба
            block.ParticleCollisionBoxes = boxes;
            return block;
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"тёсаный блок {blockId}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Высота шага игрока: на что можно взойти, не прыгая.
    ///
    /// СВОЕГО ЧИСЛА ЗДЕСЬ НЕТ И БЫТЬ НЕ ДОЛЖНО — оно одно на весь проект
    /// (<see cref="BodySize.StepUp"/>). Литерал 0,6 стоял тут своей копией,
    /// и таких копий по дереву было четыре.
    /// </summary>
    public const float StepHeight = (float)BodySize.StepUp;

    /// <summary>
    /// На какой высоте над ногами игра проверяет воду, решая «плывёт».
    ///
    /// Формула игры (<c>Entity.SwimmingOffsetY</c>, VintagestoryAPI 1.22.7):
    /// <c>SelectionBox.Y1 + SelectionBox.Y2 · 0.66</c>, то есть тело должно
    /// быть погружено на две трети РОСТА. Рост спрашивается у
    /// <see cref="BodySize"/>, а не выписан числом 1,221: перепиши кто-нибудь
    /// коробку тела — и эта мерка обязана поехать вместе с ней, иначе бот
    /// решит, что плывёт, стоя по пояс.
    /// </summary>
    public static readonly double SwimmingOffsetY = JumpPhysics.SwimmingOffsetY;

    /// <summary>
    /// Плывёт ли тело с ногами в этой точке — ровно как считает игра:
    /// смотрит НЕ на клетку ног, а на клетку в 1.221 блока выше.
    /// </summary>
    public bool IsSwimmingAt(double x, double y, double z) =>
        IsWater((int)Math.Floor(x), (int)Math.Floor(y + SwimmingOffsetY), (int)Math.Floor(z));

    /// <summary>
    /// НАСКОЛЬКО КЛЕТКА ЗАЛИТА, долей блока: у полной воды 7/8, у ручейка 1/8.
    ///
    /// Уровень приходит В РЕЕСТРЕ СЕРВЕРА (<c>Packet_BlockType.LiquidLevel</c>) —
    /// тем же полем, каким его читает сама игра (<c>Block.LiquidLevel</c>), и
    /// раскладывается по блокам один раз при получении реестра: разбирать код
    /// вида «saltwater-n-6» на каждой клетке маршрута было бы и дорого, и тем
    /// самым сочинительством по строкам, на котором проект горел четырежды.
    ///
    /// СПРАШИВАЕТ ЕГО ЧЕЛОВЕК — команда «!столб» печатает уровень рядом с
    /// кодом (разбирая затопленный ход 27.08, я час читал «113: air, 114: air»
    /// и считал коридор сухим, а обе клетки были залиты), — И ТЕПЕРЬ ЕЩЁ
    /// ПЛАВУЧЕСТЬ. Прежде здесь стояло «ни одно правило по этому числу НЕ
    /// решает», и это было правдой ровно до замера: по уровню считаются и линия
    /// воды (<see cref="JumpPhysics.WaterLine"/>), и поблажка игры «упёрся в
    /// стену в ПОЛНОЙ воде — ползи вверх», а разница между полной и неполной
    /// водой стоит целой клетки выхода на берег (см. <see cref="Buoyancy"/>).
    /// </summary>
    public double LiquidFill(int x, int y, int z)
    {
        int id = GetLiquidId(x, y, z);
        return id > 0 && id < blockLiquidLevel.Length ? blockLiquidLevel[id] / 8.0 : 0;
    }

    /// <summary>
    /// ГДЕ ПОТОЛОК НАД ЭТОЙ КЛЕТКОЙ, блоков (низ первой коллизии выше её пола).
    /// <c>double.PositiveInfinity</c> — в окне взгляда потолка нет.
    ///
    /// ЗАЧЕМ ЭТО ОТДЕЛЬНО ОТ <see cref="ClearanceAt"/>. Тот меряет ПРОСВЕТ от
    /// ног стоящего, а здесь нужна САМА ВЫСОТА потолка: у плывущего ноги не
    /// стоят — они висят на 1,85 ниже макушки, и «докуда я всплыву» считается
    /// от потолка вниз, а не от пола вверх (см.
    /// <see cref="JumpPhysics.LeaveWater"/>).
    ///
    /// СЧЁТ ОДИН НА ВЕСЬ ПРОЕКТ — <see cref="VsBotKit.Ceiling.Above"/>: там же
    /// и окно взгляда, и ответ на «лестница потолком не бывает».
    /// </summary>
    public double CeilingOver(int x, int y, int z)
    {
        // Замыкания на клетку колонны, а не на каждый зов: правило спрашивают
        // из поиска пути на КАЖДУЮ клетку с водой, и лишний мусор тут заметен
        return Ceiling.Above(y, y, y + Ceiling.Look,
            cy => GetCollisionBottom(x, cy, z),
            cy => BodySlipsThrough(x, cy, z));
    }

    /// <summary>
    /// ТЕЛО ПРОХОДИТ СКВОЗЬ КЛЕТКУ, НЕСМОТРЯ НА КОЛЛИЗИЮ. Тот же вопрос, что
    /// задаёт <see cref="IsEnterable"/> («пройти можно?», а не «пусто?»), но
    /// без вреда: горящий костёр над головой макушку и правда не держит,
    /// однако лезть туда решает не мерка потолка.
    ///
    /// У лестницы коллизия во всю высоту клетки и тонка она по Z; у открытой
    /// створки — та же история. Ни та, ни другая потолком не бывают.
    /// </summary>
    private bool BodySlipsThrough(int x, int y, int z) =>
        IsClimbable(x, y, z) || IsPassableDoor(x, y, z);

    /// <summary>Верхний блок воды в колонне около указанной высоты (null — воды нет).</summary>
    public int? WaterTopY(int x, int y, int z, int lookUp = 3)
    {
        if (!IsWater(x, y, z))
            return null;
        int top = y;
        for (int i = 1; i <= lookUp && IsWater(x, top + 1, z); i++)
            top++;
        return top;
    }

    /// <summary>
    /// В клетке можно плыть: вода и в ней, и над ней (рост игрока 1.85
    /// в один блок воды не помещается — там брод, а не плавание).
    /// </summary>
    public bool IsSwimmable(int x, int y, int z) =>
        IsWater(x, y, z) && IsWater(x, y + 1, z) &&
        !IsDangerousLiquid(x, y, z) && !IsDangerousLiquid(x, y + 1, z);

    /// <summary>
    /// Уровень поверхности воды, в которой НАДО ПЛЫТЬ (глубина от двух
    /// блоков), около указанной высоты. null — воды нет или она по пояс,
    /// там идут вброд по дну.
    ///
    /// Решение принимается по глубине колонны, а не по текущей высоте бота:
    /// иначе выходит замкнутый круг — чтобы игра сочла тебя плывущим, надо
    /// уже быть погружённым, и бот так и остаётся стоять на поверхности.
    /// </summary>
    public int? SwimSurfaceY(int x, int nearY, int z, int look = 3)
    {
        for (int y = nearY + look; y >= nearY - look; y--)
        {
            if (!IsWater(x, y, z) || IsWater(x, y + 1, z))
                continue;              // это не верхний блок воды
            if (IsDangerousLiquid(x, y, z))
                return null;
            return IsWater(x, y - 1, z) ? y : null;   // нужна глубина от двух блоков
        }
        return null;
    }

    /// <summary>Мелко: вода по пояс, идём вброд по дну (в игре это медленнее).</summary>
    public bool IsWadable(int x, int y, int z) =>
        IsWater(x, y, z) && !IsWater(x, y + 1, z) && IsStandable(x, y, z);

    /// <summary>
    /// Можно ли пройти сквозь блок. Низкие блоки (пластина транслокатора,
    /// нажимная плита, слой снега) телу не мешают: игрок на них просто
    /// всходит, как на ступеньку — поэтому для прохода они «пустые».
    ///
    /// НЕИЗВЕСТНАЯ КЛЕТКА НЕПРОХОДИМА. Это и есть починка «маршрута по пустой
    /// карте»: пока незнание считалось воздухом, поиск пути охотно вёл сквозь
    /// невыгруженные чанки, а на месте оказывалось, что идти некуда — отсюда
    /// «перепрыгиваю яму до …(5 бл)» и хождение кругами. Живой игрок в туман
    /// тоже не шагает как в пустую комнату; придёт чанк — придёт и дорога.
    /// </summary>
    public bool IsPassable(int x, int y, int z)
    {
        int id = BlockIdOrUnknown(x, y, z);
        if (id < 0)
            return false;
        return CollisionTopOf(id, x, y, z) <= StepHeight;
    }

    /// <summary>Блок целиком перекрывает клетку (стена, обычный куб).</summary>
    public bool IsSolid(int x, int y, int z) => GetCollisionTop(x, y, z) > StepHeight;

    /// <summary>
    /// Точная высота ног, если стоять в этой клетке: на полном кубе снизу —
    /// ровно граница клетки, на тонком блоке В САМОЙ клетке — его верх.
    /// Без этого бот висел бы над пластиной транслокатора на целый блок.
    ///
    /// САМО ПРАВИЛО ЗДЕСЬ НЕ ЖИВЁТ, и это нарочно: оно одно на бота и на
    /// клиентский мод (<see cref="Footing.FeetY"/>, файл линкуется в мод).
    /// Здесь остаётся только мирская половина — ответ про блок в клетке.
    /// Пока копий было три, починка «дорожная плита под ногами» досталась
    /// одному боту, а маршрут для игрока считал мод по старой мерке.
    /// </summary>
    public double FeetY(int x, int y, int z) =>
        Footing.FeetY(y, StepHeight, cy => GetCollisionTop(x, cy, z));

    /// <summary>
    /// ВЫСОТА НИЗА КОЛЛИЗИИ БЛОКА В КЛЕТКЕ: 0 у целого куба и у обычной плиты
    /// (обе лежат на полу клетки), 0,5 у ПЕРЕВЁРНУТОЙ плиты, 1 — коллизии нет
    /// вовсе, то есть из этой клетки ничего не свисает.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫЙ ВОПРОС, КОГДА ЕСТЬ <see cref="GetCollisionTop"/>. Верх
    /// отвечает «на что бот встанет», низ — «насколько потолок висит над
    /// ногами». У потолочного полублока верх равен 1 (как у стены), и по нему
    /// клетка неотличима от сплошной породы, — а тело туда проходит в присяде.
    /// Пока этого вопроса не было, бот не мог ответить заказчику на прямую
    /// просьбу «научи видеть высоту препятствия».
    /// </summary>
    public float GetCollisionBottom(int x, int y, int z)
    {
        int id = BlockIdOrUnknown(x, y, z);
        // Неизвестная клетка — как и в IsPassable, считается ЗАКРЫТОЙ: врать
        // про просвет в невыгруженном чанке нельзя
        if (id < 0)
            return 0;
        if (id == 0 || id >= blockHasCollision.Length || !blockHasCollision[id])
            return 1;
        // Открытая дверь проёма не занимает — тот же разбор, что в CollisionTopOf
        if (((id < blockDoorKind.Length && blockDoorKind[id] != 0) ||
             (id < blockIsMultiblock.Length && blockIsMultiblock[id])) &&
            IsDoorOpen(x, y, z))
            return 1;
        // Закрытый люк — по настоящим коробкам, тот же разбор, что в CollisionTopOf
        if (IsTrapdoorId(id) &&
            DoorPhysicsBlock(x, y, z)?.CollisionBoxes is { Length: > 0 } hatch)
        {
            float bottom = 1;
            foreach (var b in hatch)
                bottom = Math.Min(bottom, b.Y1);
            return bottom;
        }
        if (id < blockIsMicroBlock.Length && blockIsMicroBlock[id] &&
            MicroShapeAt(id, x, y, z) is { } micro)
            return micro.Bottom;
        return id < blockCollisionBottom.Length ? blockCollisionBottom[id] : 0;
    }

    /// <summary>
    /// СКОЛЬКО СВОБОДНОЙ ВЫСОТЫ НАД НОГАМИ, стоя в этой клетке, блоков.
    ///
    /// Мирская половина правила <see cref="Walker.PoseForClearance"/>: она
    /// решает, какой позой тело влезает, а это меряет саму щель. Считаем от
    /// НАСТОЯЩЕЙ высоты ног (<see cref="FeetY"/> — на плите ноги выше пола
    /// клетки) до низа первой коллизии, которая и правда над ногами.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО. Заказчик просил дословно: «научи
    /// видеть высоту препятствия, некоторые можно пройти в присяд». До этого
    /// весь ответ бота про голову умещался в <see cref="IsStandable"/>: «клетка
    /// ног проходима И клетка над ней проходима», где проходимость — это
    /// «верх коллизии ниже шага 0,6». Такая мерка не различает целый куб над
    /// головой и перевёрнутую плиту: обе «непроходимы», хотя под второй тело
    /// проходит пригнувшись.
    ///
    /// ВЫШЕ РОСТА НЕ СМОТРИМ: телу всё равно, три там блока или тридцать, а
    /// каждый лишний вопрос к модели мира — это чтение чанка.
    /// </summary>
    public double ClearanceAt(int x, int y, int z)
    {
        double feet = FeetY(x, y, z);
        // На две клетки вверх от ног хватает: рост 1,85, и потолок выше
        // (ноги + 2) телу уже не мешает
        int top = (int)Math.Floor(feet + BodySize.Height) + 1;
        // СЧЁТ ПОТОЛКА ОДИН НА ВЕСЬ ПРОЕКТ (Ceiling.Above) — здесь он лишь
        // отсчитывается от НОГ, а не от пола клетки. И лестница здесь тоже не
        // потолок: стоя у подножия, бот терял просвет над головой и объявлял
        // себя не влезающим — та самая ловушка, что и у CeilingOver
        double at = Ceiling.Above(feet, y, top,
            cy => GetCollisionBottom(x, cy, z),
            cy => BodySlipsThrough(x, cy, z));
        return double.IsPositiveInfinity(at) ? BodySize.Height
                                             : Math.Max(0, at - feet);
    }

    /// <summary>
    /// Можно ли стоять ногами в клетке: снизу опора (или в самой клетке
    /// невысокий блок вроде пластины), ноги и голова свободны, и это
    /// не лава/кипяток (мелкая вода — можно, это брод).
    ///
    /// СНЕГ: ЗДЕСЬ ВТОРОЙ ОТВЕТ НА ВОПРОС «ВЛЕЗЕТ ЛИ ГОЛОВА», И СНЕГА ОН НЕ
    /// ВИДИТ. Говорю вслух, потому что чинить это отсюда НЕЛЬЗЯ, и я пробовал.
    ///
    /// Жалоба заказчика 23.08 дословно: «снег не всегда видит как препятствие».
    /// Разбор по реестру игры (assets/survival/blocktypes/liquid/snowlayer.json,
    /// 1.22.7): у слоёв snowlayer-1…7 коробка коллизии 0 / 0,125 / 0,25 — телу
    /// они не мешают (шаг 0,6), и проходимость отвечает про них верно. Но НОГИ
    /// НА СНЕГУ СТОЯТ ВЫШЕ ПОЛА КЛЕТКИ (<see cref="FeetY"/>), а голова, значит,
    /// выше на столько же: под сугробом 0,25 и потолком в двух блоках телу
    /// остаётся 1,75 при росте 1,85 — оно НЕ ВЛЕЗАЕТ. Мерка просвета, которая
    /// это знает, в проекте одна и давно есть (<see cref="ClearanceAt"/>,
    /// считает от настоящей высоты ног до низа первой коллизии). Сюда её не
    /// зовут: здесь стоят два вопроса про КЛЕТКИ («ноги проходимы и голова
    /// проходима»), и высоты ног они не знают вовсе.
    ///
    /// ПОЧЕМУ ПРОСТО ДОБАВИТЬ СЮДА ПРОСВЕТ НЕЛЬЗЯ. Я это сделал и получил
    /// красное — тот самый живой случай 19.08 с занесённым проёмом. Сторожит
    /// это теперь чистое правило без тела и без часов,
    /// <c>СнегДорогуНеЗапираетTests.Занесённая_клетка_под_потолком_остаётся_дорогой</c>
    /// (прежний свидетель, живой стенд <c>ЗанесённаяСтупеньМирTests</c>, снят
    /// как НЕВОСПРОИЗВОДИМЫЙ — не как ошибочный: мир его и правда было не
    /// пройти стоя, но ступень в 1,25 тело брало через раз; разбор — на месте
    /// снятого класса в SnowInTheWayTests).
    /// Сейчас планировщик СЧИТАЕТ занесённую клетку
    /// проходимой нарочно: он ведёт туда маршрут, тело упирается, и приём
    /// «смахнуть мелочь с дороги» (<c>Movement.SweepWayAheadAsync</c>) сносит
    /// снежинку — один блок, без всякой стройки. Запрети клетку здесь — маршрут
    /// туда не пойдёт вовсе, приёму нечего будет смахивать (он берёт клетку ПО
    /// КУРСУ, а курса больше нет), и бот встанет перед снегом, как 19.08.
    ///
    /// ЧТО ЗДЕСЬ НА САМОМ ДЕЛЕ НУЖНО: планировщик обязан узнать про смахиваемую
    /// мелочь ТО ЖЕ, что он уже знает про листву — «клетка проходима, но это
    /// РАБОТА, и она стоит надбавки» (<c>PathOptions.IsLeaves</c> +
    /// <c>LeafBreakCost</c>, <c>AStarPathFinder.LeafCost</c>). Тогда просвет
    /// можно спрашивать честно: занесённая клетка станет клеткой-работой, а не
    /// стеной. Это правка в <c>PathOptions</c>, <c>AStarPathFinder</c> и новый
    /// шов из VsBot к <c>Mining.JudgeGrowth</c> — назвал её в отчёте волны и не
    /// делал молча.
    /// </summary>
    /// <remarks>
    /// НА ВРЕДНОЙ КЛЕТКЕ СТОЯТЬ НЕЛЬЗЯ — ни ногами, ни головой. Голова
    /// спрашивается отдельно нарочно: рост игрока две клетки, и костёр,
    /// горящий на уровне лица, жжёт так же, как костёр под ногами
    /// (BEBehaviorBurning бьёт по коробке, а не по клетке ног).
    /// </remarks>
    public bool IsStandable(int x, int y, int z) =>
        (IsSolid(x, y - 1, z) || GetCollisionTop(x, y, z) > 0) &&
        (IsPassable(x, y, z) || IsPassableDoor(x, y, z)) &&
        (IsPassable(x, y + 1, z) || IsPassableDoor(x, y + 1, z)) &&
        !this.HurtsToEnter(x, y, z) && !this.HurtsToEnter(x, y + 1, z);

    /// <summary>
    /// В КАКОЙ КЛЕТКЕ СТОЯТ НОГИ ТЕЛА, НАХОДЯЩЕГОСЯ В ЭТОЙ ТОЧКЕ, — ОДИН ВХОД
    /// НА ВЕСЬ ПРОЕКТ.
    ///
    /// Само правило чистое и живёт в <see cref="Walker.StandingCell"/>; здесь к
    /// нему прикладывается мир — верх коробки в клетке ног
    /// (<see cref="GetCollisionTop"/>). Врозь эти две половины и разъехались:
    /// движение, дорога, карьер и добыча писали ОДНУ И ТУ ЖЕ склейку из четырёх
    /// <c>floor</c>'ов каждый у себя, и четыре копии уже спорили о запасе
    /// <paramref name="sink"/> — у дороги и карьера его не было вовсе, у добычи
    /// и памяти тупиков стояло 0,01. Спор о запасе — это спор об ЭТАЖЕ: за тик
    /// физика вдавливает тело в опору на доли блока, и без запаса клетка мигает
    /// вниз.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ради которого правило вообще заведено (bot.log 16.08,
    /// 20:36:38): «стою (512066,5, 110,9, 512237,7)» — бот на СВОЁМ дорожном
    /// полотне stonepath-free, верх коробки 0,9375 при шаге 0,6. Голый
    /// <c>floor(Y)</c> давал клетку самой тропы, куда мир тело не пускает
    /// (<see cref="IsPassable"/> там ложь): оттуда строился маршрут, туда же
    /// дорога мерила «дошёл ли я до полотна» — отсюда живое «дорога до … есть,
    /// а тело по ней не идёт».
    /// </summary>
    /// <param name="sink">
    /// Насколько приподнять точку перед счётом клетки. Это не вкус, а ИСТОЧНИК
    /// числа: положение от сущности сервера уже округлено (запас 0), а живая
    /// игровая физика вдавливает тело в опору — там 0,01.
    /// </param>
    public BlockPos StandingCellAt(double x, double y, double z, double sink = 0)
    {
        int fx = (int)Math.Floor(x), fy = (int)Math.Floor(y + sink), fz = (int)Math.Floor(z);
        return new BlockPos(fx, Walker.StandingCell(y, fy, GetCollisionTop(fx, fy, fz)), fz);
    }

    /// <summary>
    /// КАКАЯ КЛЕТКА ПОД НОГАМИ СВОБОДНА — тот же мир, но ДРУГОЙ вопрос, и путать
    /// его с <see cref="StandingCellAt"/> нельзя: там «где я стою», здесь «куда
    /// бросить блок под ноги и что выбить, чтобы спуститься». Правило чистое, в
    /// <see cref="Walker.CellUnderFeet"/>; склейка с миром была написана в
    /// добыче дважды подряд — столбом вверх (<c>PillarUpAsync</c>) и спуском
    /// вниз (<c>PillarDownAsync</c>).
    /// </summary>
    public int CellUnderFeetAt(double x, double y, double z, double sink = 0.01)
    {
        int fx = (int)Math.Floor(x), fy = (int)Math.Floor(y + sink), fz = (int)Math.Floor(z);
        return Walker.CellUnderFeet(y, fy, GetCollisionTop(fx, fy, fz));
    }

    /// <summary>
    /// Ближайший уровень в колонне, где можно стоять: сначала вниз (до down блоков),
    /// затем вверх (до up). null — пола нет.
    /// </summary>
    public int? FindStandableY(int x, int startY, int z, int up = 1, int down = 3)
    {
        for (int y = startY + up; y >= startY - down; y--)
            if (IsStandable(x, y, z))
                return y;
        return null;
    }

    /// <summary>
    /// По блоку можно лезть вверх/вниз (лестница, верёвочная лестница, лоза).
    /// Флаг приходит из реестра сервера, поэтому модовые лестницы тоже опознаются.
    /// </summary>
    public bool IsClimbable(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        return id > 0 && id < blockIsClimbable.Length && blockIsClimbable[id];
    }

    /// <summary>
    /// Опора для ног: твёрдый пол, поверхность воды (плывём) или лестница
    /// (висим на ней). Именно по опорам строится маршрут.
    /// </summary>
    public bool IsSupport(int x, int y, int z) =>
        IsStandable(x, y, z) || IsWaterSurface(x, y, z) || CanHangAt(x, y, z);

    /// <summary>
    /// Можно ли держаться в клетке за лестницу. Лестница засчитывается и на
    /// уровне ног, и на уровне головы — игрок ростом почти два блока, поэтому
    /// лестницы, поставленные ЧЕРЕЗ ОДИН блок, работают как сплошные
    /// (обычный приём игроков ради экономии материалов).
    /// </summary>
    public bool CanHangAt(int x, int y, int z) =>
        (IsClimbable(x, y, z) || IsClimbable(x, y + 1, z)) &&
        // в саму лестницу зайти можно, даже если у неё есть тонкая коллизия
        (IsPassable(x, y, z) || IsClimbable(x, y, z)) &&
        (IsPassable(x, y + 1, z) || IsClimbable(x, y + 1, z)) &&
        !this.HurtsToEnter(x, y, z) && !this.HurtsToEnter(x, y + 1, z);

    /// <summary>
    /// САМЫЙ ВЕРХНИЙ уровень с опорой в окне. Годится, когда ищут «поверхность»
    /// (куда встать снаружи), и НЕ годится, когда высота уже названа: крыша,
    /// крона дерева и карниз тоже опоры.
    /// </summary>
    public int? FindSupportY(int x, int startY, int z, int up = 1, int down = 3)
    {
        for (int y = startY + up; y >= startY - down; y--)
            if (IsSupport(x, y, z))
                return y;
        return null;
    }

    /// <summary>
    /// БЛИЖАЙШИЙ к запрошенной высоте уровень с опорой — ищем, расходясь от
    /// startY вверх и вниз.
    ///
    /// Живьём: «!иди 38 144 412» тихо превращалось в «иди на 152», потому что
    /// поиск шёл сверху и первой находил крышу. Названную высоту надо уважать.
    /// </summary>
    /// <summary>
    /// ОДНО ЛИ ЭТО МЕСТО ПО ВЕРТИКАЛИ: пройдёт ли тело по колонне от одной
    /// отметки до другой, не продираясь сквозь блок. Концы не спрашиваем —
    /// спрашиваем ровно то, что МЕЖДУ ними.
    ///
    /// ЗАЧЕМ. «Ближайшая опора в колонне цели» (<see cref="NearestSupportY"/>)
    /// сама по себе не отличает ПОЛ ПОД НАЗВАННОЙ КЛЕТКОЙ от совсем другого
    /// места, случайно оказавшегося в той же колонне. Живой случай, журнал
    /// заказчика 23.08, 10:59:50:
    /// <code>
    ///   [склад] иду на склад (59, 112, 257): беру fat
    ///   [движение] дорога: цель уточнил по высоте 112 → 117
    /// </code>
    /// Сундуки у него стоят СТОПКОЙ на 112…115, и в клетку сундука встать
    /// нельзя — значит ближайшей «опорой» в этой колонне оказалась КРЫША на
    /// 117, через три сундука и перекрытие. Бот пошёл на крышу за вещью,
    /// которая под ней, не дошёл и отказался. Между 112 и 117 сплошняк —
    /// и вот его-то и надо было спросить.
    /// </summary>
    public bool OpenBetween(int x, int yA, int yB, int z)
    {
        int lo = Math.Min(yA, yB), hi = Math.Max(yA, yB);
        for (int y = lo + 1; y < hi; y++)
            if (!IsEnterable(x, y, z))
                return false;
        return true;
    }

    public int? NearestSupportY(int x, int startY, int z, int up = 8, int down = 8)
    {
        if (IsSupport(x, startY, z))
            return startY;
        for (int d = 1; d <= Math.Max(up, down); d++)
        {
            if (d <= down && IsSupport(x, startY - d, z))
                return startY - d;
            if (d <= up && IsSupport(x, startY + d, z))
                return startY + d;
        }
        return null;
    }

    public string? ItemIdToCode(int id) => id >= 0 && id < itemCodes.Length ? itemCodes[id] : null;

    public int? ItemCodeToId(string code) => itemCodeToId.TryGetValue(code, out int id) ? id : null;

    /// <summary>Питательность предмета/блока по коду (0 — несъедобно). Работает и для модов.</summary>
    public float GetSatietyByCode(string code)
    {
        if (itemCodeToId.TryGetValue(code, out int itemId) && itemId < itemSatiety.Length && itemSatiety[itemId] > 0)
            return itemSatiety[itemId];
        if (codeToId.TryGetValue(code, out int blockId) && blockId < blockSatiety.Length)
            return blockSatiety[blockId];
        return 0;
    }

    /// <summary>
    /// Сколько здоровья даёт (или отнимает) еда по коду. Отрицательное — яд:
    /// поганка сытная, но убивает. Значение из реестра сервера, работает и
    /// с модовой едой.
    /// </summary>
    public float GetFoodHealthByCode(string code)
    {
        if (itemCodeToId.TryGetValue(code, out int itemId) && itemId < itemFoodHealth.Length &&
            itemFoodHealth[itemId] != 0)
            return itemFoodHealth[itemId];
        if (codeToId.TryGetValue(code, out int blockId) && blockId < blockFoodHealth.Length)
            return blockFoodHealth[blockId];
        return 0;
    }

    /// <summary>Еда безопасна: питательна и не отнимает здоровье.</summary>
    public bool IsSafeFood(string code) =>
        GetSatietyByCode(code) > 0 && GetFoodHealthByCode(code) >= 0;

    /// <summary>
    /// Лечебные свойства предмета по коду (null — не лечилка).
    /// Определяются по поведению HealingItem в реестре, поэтому работают
    /// и для модовых бинтов.
    /// </summary>
    public HealingInfo? GetHealingByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemHealing.Length ? itemHealing[id] : null;

    /// <summary>
    /// Множитель скорости ходьбы У САМОГО БЛОКА (1.0 — обычный): тропинка
    /// ускоряет до 1.3, паутина тормозит до 0.25, слой снега — до 0.65.
    ///
    /// Это ответ ПРО БЛОК, а не про идущего: сколько блоков спрашивать и как
    /// их перемножать, решает <see cref="WalkSpeedAt"/> по правилу игры.
    /// </summary>
    public float GetWalkSpeedMultiplier(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        return id > 0 && id < blockWalkSpeed.Length && blockWalkSpeed[id] > 0 ? blockWalkSpeed[id] : 1f;
    }

    /// <summary>
    /// КАК БЫСТРО ИДЁТСЯ ТЕЛУ, У КОТОРОГО НОГИ НА ЭТОЙ ВЫСОТЕ, — ОДИН ВХОД НА
    /// ВЕСЬ ПРОЕКТ, и считается ровно как в игре.
    ///
    /// Правило чистое и живёт в <see cref="GroundSpeed"/>; здесь к нему
    /// прикладывается мир.
    ///
    /// ЖИВАЯ БЕДА, СЛОВАМИ ЗАКАЗЧИКА: «возможно проблемы из-за снега… плоохо
    /// стал идти». Мира это касается двумя способами сразу, и оба видны только
    /// отсюда:
    ///   • у <c>snowlayer-1</c> коробки коллизии нет вовсе, ноги стоят на земле
    ///     под ним — блок «под ногами» это земля, и её единица начисто съедала
    ///     снежные 0,95: снега, покрывшего осенью всю карту, бот НЕ ВИДЕЛ;
    ///   • у слоёв потолще коробка есть (0,125 и 0,25), и ноги поднимаются В
    ///     САМ СНЕГ. Тогда «под ногами» — уже снег (0,8…0,65), а вовсе не
    ///     дорожное полотно под ним. Каменная тропа заказчика с её 1,3
    ///     превращается под снегом в САМУЮ МЕДЛЕННУЮ землю в округе, и это не
    ///     поломка, а игра: так же тормозит и живой игрок.
    /// </summary>
    public float WalkSpeedAt(double x, double feetY, double z)
    {
        int fx = (int)Math.Floor(x), fz = (int)Math.Floor(z);
        var (below, inside) = GroundSpeed.CellsAt(feetY);
        return GroundSpeed.Of(GetWalkSpeedMultiplier(fx, below, fz),
                              GetWalkSpeedMultiplier(fx, inside, fz),
                              below == inside);
    }

    /// <summary>
    /// ТОТ ЖЕ ВОПРОС ПРО КЛЕТКУ, В КОТОРОЙ СТОЯТ НОГИ, — его задаёт поиск пути,
    /// у которого тела нет, а есть клетка маршрута.
    ///
    /// Высоту ног в клетке считает <see cref="FeetY"/>, и второй такой мерки
    /// быть не должно: именно она знает, что на плите и на снегу ноги стоят
    /// ВЫШЕ дна клетки, а на целом кубе — ровно на его границе.
    ///
    /// ПОЧЕМУ НЕ «БЛОК ПОД КЛЕТКОЙ», как считали раньше. На ровной земле это
    /// одно и то же, а на снегу — нет: в клетке ног лежит снег, и игра берёт
    /// его, а <c>cell.Y − 1</c> показывает на землю (или на закопанную тропу)
    /// под ним. Из-за этого дорога считала занесённое снегом полотно самым
    /// быстрым путём в округе, тогда как тело шло по нему медленнее всего.
    /// </summary>
    public float StandingWalkSpeed(int x, int y, int z) =>
        WalkSpeedAt(x + 0.5, FeetY(x, y, z), z + 0.5);

    /// <summary>
    /// Куски кода блоков, которые бот считает рукотворной дорогой. Список
    /// открыт: добавьте своё, если в вашем мире дороги мостят иначе.
    /// </summary>
    public HashSet<string> RoadBlockParts { get; } =
        new(Paving.PavedParts, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ПОХОЖ ЛИ БЛОК НА ДОРОГУ — ОДНО ПРАВИЛО НА БОТА И НА МОД
    /// (<see cref="Paving.IsRoad"/>).
    ///
    /// Здесь стоял СВОЙ ответ (только по списку кусков кода), а у мода —
    /// СВОЙ (только по множителю скорости из реестра), и на мощёных дорогах
    /// заказчика они строили разные маршруты по одному и тому же миру. Теперь
    /// вопрос один, а мир каждый даёт свой: реестр отвечает про скорость, код —
    /// про то, что это мостил человек.
    ///
    /// Из списка при этом убраны два ВЫДУМАННЫХ кода («gravelpath»,
    /// «flagstone») — блоков с такими кодами в игре нет вовсе, см.
    /// <see cref="Paving.PavedParts"/>.
    /// </summary>
    public bool IsRoadLike(int x, int y, int z) =>
        Paving.IsRoad(GetWalkSpeedMultiplier(x, y, z), GetBlockCode(x, y, z), RoadBlockParts);

    /// <summary>
    /// Сумка ли это (рюкзак, мешок, корзина). Узнаём по поведению HeldBag из
    /// реестра сервера, а не по списку названий — тогда работают и моды.
    /// </summary>
    public bool IsBagByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemIsBag.Length && itemIsBag[id];

    /// <summary>
    /// Сколько мест даёт сумка (0 — не сумка). Число лежит в атрибутах
    /// предмета: attributes.backpack.quantitySlots (льняной мешок — 5,
    /// рюкзак — 6, крепкий — 8).
    /// </summary>
    public int BagSlotsByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemBagSlots.Length ? itemBagSlots[id] : 0;

    private static int ReadBagSlots(string? attributesJson)
    {
        if (string.IsNullOrEmpty(attributesJson))
            return 0;
        try
        {
            var attrs = Newtonsoft.Json.Linq.JObject.Parse(attributesJson);
            return attrs.GetValue("backpack", StringComparison.OrdinalIgnoreCase)
                is Newtonsoft.Json.Linq.JObject bag
                ? bag.GetValue("quantitySlots", StringComparison.OrdinalIgnoreCase)?.ToObject<int>() ?? 0
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// ЕСТЬ ЛИ У БЛОКА ТАКОЕ ПОВЕДЕНИЕ — по имени, которое прислал сам сервер.
    ///
    /// ЕДИНСТВЕННЫЙ ЧЕСТНЫЙ ОТВЕТ НА «ЧТО ЭТОТ БЛОК УМЕЕТ». На собранном
    /// объекте блока поведения спрашивать нельзя: незнакомые классы (а модовые
    /// незнакомы все) подменяются заглушкой (<see cref="BotGamePhysics.StubBehaviors"/>), и
    /// <c>BreakIfFloating</c> там не найдётся даже у ванильного гранита. Имя же
    /// приходит строкой в том же пакете реестра — его и храним.
    ///
    /// Сравнение ТОЧНОЕ и по буквам: имена поведений игра регистрирует именно
    /// так (RegisterBlockBehaviorClass("BreakIfFloating", …)), а вхождение
    /// подстроки склеило бы разные поведения одного семейства.
    ///
    /// Пустой ответ означает «реестр ещё не пришёл ИЛИ поведений нет» — эти два
    /// случая различает <see cref="GameBlocksReady"/>, и различать их обязан
    /// зовущий: молча ответить «нет» до реестра значит объявить, что таких
    /// блоков в мире не бывает.
    /// </summary>
    public bool HasBlockBehavior(int blockId, string name)
    {
        if (blockId <= 0 || blockId >= blockBehaviorNames.Length ||
            blockBehaviorNames[blockId] is not { } names)
            return false;
        foreach (string have in names)
            if (string.Equals(have, name, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>То же по коду блока.</summary>
    public bool HasBlockBehavior(string code, string name) =>
        BlockCodeToId(code) is { } id && HasBlockBehavior(id, name);

    /// <summary>Есть ли у предмета такое поведение (по имени из реестра).</summary>
    private static bool HasBehavior(Packet_Behavior[]? behaviors, int count, string name)
    {
        if (behaviors == null)
            return false;
        for (int i = 0; i < count; i++)
            if (behaviors[i]?.Code?.Contains(name, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        return false;
    }

    /// <summary>Что бот знает о броне по коду предмета (null — не броня).</summary>
    public ArmorInfo? GetArmorByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemArmor.Length ? itemArmor[id] : null;

    /// <summary>
    /// Разбор брони из JSON-атрибутов предмета (реестр шлёт их целиком).
    /// Ключи ищем без учёта регистра: в ассетах пишут clothesCategoryByType,
    /// и после раскрытия byType регистр префикса сохраняется.
    /// </summary>
    private static ArmorInfo? ReadArmor(string? attributesJson)
    {
        if (string.IsNullOrEmpty(attributesJson))
            return null;
        try
        {
            var attrs = Newtonsoft.Json.Linq.JObject.Parse(attributesJson);
            string? category = attrs.GetValue("clothescategory", StringComparison.OrdinalIgnoreCase)?
                .ToObject<string>();
            // Новая броня хранит категорию иначе: attachableToEntity.categoryCode.
            // Без этого «штаны» части комплектов не распознавались бронёй вовсе
            if (category == null &&
                attrs.GetValue("attachableToEntity", StringComparison.OrdinalIgnoreCase)
                    is Newtonsoft.Json.Linq.JObject attachable)
                category = attachable.GetValue("categoryCode", StringComparison.OrdinalIgnoreCase)?
                    .ToObject<string>();
            int slot = category?.ToLowerInvariant() switch
            {
                "armorhead" => ArmorInfo.HeadSlot,
                "armorbody" => ArmorInfo.BodySlot,
                "armorlegs" => ArmorInfo.LegsSlot,
                _ => -1
            };
            if (slot < 0)
                return null;

            var prot = attrs.GetValue("protectionModifiers", StringComparison.OrdinalIgnoreCase)
                as Newtonsoft.Json.Linq.JObject;
            var stats = attrs.GetValue("statModifiers", StringComparison.OrdinalIgnoreCase)
                as Newtonsoft.Json.Linq.JObject;
            T Read<T>(Newtonsoft.Json.Linq.JObject? o, string name, T fallback)
            {
                var token = o?.GetValue(name, StringComparison.OrdinalIgnoreCase);
                return token == null ? fallback : token.ToObject<T>() ?? fallback;
            }

            return new ArmorInfo(
                slot,
                Read(prot, "relativeProtection", 0f),
                Read(prot, "flatDamageReduction", 0f),
                Read(prot, "protectionTier", 0),
                Read(prot, "highDamageTierResistant", false),
                Read(stats, "walkSpeed", 0f),
                Read(stats, "hungerrate", 0f));
        }
        catch { return null; }
    }

    /// <summary>
    /// Собрать настоящие объекты Block из реестра сервера — тем же методом,
    /// которым это делает клиент игры. Благодаря этому физику можно считать
    /// игровым кодом, а не своим: у блоков настоящие коллизии и свойства.
    /// </summary>
    private void BuildGameBlocks(Packet_BlockType[] packets, int count, int maxId)
    {
        try
        {
            var registry = new Vintagestory.Common.ClassRegistry();
            // Реестр знает только ванильные классы, а сервер шлёт и модовые
            // (у нас это выживание). Физике поведения блоков не нужны —
            // подставляем пустышки, чтобы разбор не падал на первом же
            BotGamePhysics.StubBehaviors.RegisterAll(registry, packets, count);

            var accessorWorld = new BotGamePhysics.BotPhysicsWorld(this);
            // НОВЫЙ РЕЕСТР — НОВЫЕ НОМЕРА БЛОКОВ. Заготовки и разобранные
            // формы относились к прежним: оставь их — и физика получит чужую
            // створку или чужую резку под тем же номером. Это та же причина,
            // по которой при новом соединении забывается весь мир
            doorPackets.Clear();
            microPackets.Clear();
            doorBlockCache.Clear();
            microShapes.Clear();
            var built = new Vintagestory.API.Common.Block[maxId + 1];
            for (int i = 0; i < count; i++)
            {
                var bt = packets[i];
                if (bt == null)
                    continue;
                var block = Vintagestory.Common.BlockTypeNet.ReadBlockTypePacket(bt, accessorWorld, registry);
                block.BlockId = bt.BlockId;
                if (bt.BlockId >= 0 && bt.BlockId < built.Length)
                    built[bt.BlockId] = block;

                // Дверям, чью коллизию игра держит в блок-сущности, пакет
                // нужен и позже: по нему собираются повёрнутые створки
                if (bt.BlockId > 0 && bt.BlockId < blockDoorKind.Length &&
                    (DoorKind)blockDoorKind[bt.BlockId] == DoorKind.ByEntity)
                    doorPackets[bt.BlockId] = bt;

                // Тёсаным — по той же причине: их форму несёт блок-сущность,
                // и объект блока с настоящими коробками собирается уже по месту
                if (bt.BlockId > 0 && bt.BlockId < blockIsMicroBlock.Length &&
                    blockIsMicroBlock[bt.BlockId])
                    microPackets[bt.BlockId] = bt;
            }
            rebuildRegistry = registry;
            rebuildAccessor = accessorWorld;
            gameBlocks = built;
            airBlock = built.Length > 0 ? built[0] : null;
            OnGameBlocksReady?.Invoke();
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"реестр блоков игры: {ex.GetType().Name} {ex.Message}");
        }
    }

    // Заготовки для блоков, чью коллизию игра держит в блок-сущности:
    // двери (створка повёрнута) и тёсаные блоки (форма — набор вокселей).
    // Реестр и мир общие: пересобрать блок из пакета — одна и та же работа
    private readonly Dictionary<int, Packet_BlockType> doorPackets = new();
    private readonly Dictionary<int, Packet_BlockType> microPackets = new();
    private Vintagestory.Common.ClassRegistry? rebuildRegistry;
    private BotGamePhysics.BotPhysicsWorld? rebuildAccessor;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Id, int Rot, bool Open, bool Inv, int Face),
        Vintagestory.API.Common.Block?> doorBlockCache = new();

    /// <summary>
    /// «ЭТО НЕ ЛЮК» — в ключе заготовки. Отдельным числом, а не флагом: у люка
    /// то же место занимает <c>attachedFace</c> (0…5), к которой он прибит, и
    /// нуля-«севера» среди её значений хватает своего.
    /// </summary>
    private const int НеЛюк = -1;

    /// <summary>
    /// Настоящий блок двери ДЛЯ ФИЗИКИ, если её коллизию игра считает в
    /// блок-сущности. Повторяем ровно то, что делает BEBehaviorDoor:
    /// боксы блока поворачиваются на rotateYRad, а у открытой — ещё на ∓90°
    /// вокруг центра клетки. Без этого закрытая дверь стоит «не той стороной»,
    /// а верх проёма (служебный блок мультиблока, у него боксов нет вовсе)
    /// вообще не имеет тела — и бот проходит сквозь дверь.
    /// Возвращает null, если восстанавливать нечего: у обычных дверей и
    /// калиток каждое состояние это отдельный блок с уже верными боксами.
    /// </summary>
    public Vintagestory.API.Common.Block? DoorPhysicsBlock(int x, int y, int z)
    {
        if (DoorAt(x, y, z) is not { } d || d.Kind != DoorKind.ByEntity)
            return null;
        if (GetBlockEntity(d.Owner)?.Attributes is not { } attrs)
            return null;

        int ownerId = GetBlockId(d.Owner.X, d.Owner.Y, d.Owner.Z);

        // ЛЮК — НЕ ДВЕРЬ, И СЧИТАТЬ ЕГО ДВЕРНЫМ ПРАВИЛОМ БЫЛО ГЛАВНОЙ БЕДОЙ
        // ЛЮКА. Живая жалоба заказчика 01.09, дословно: «люком не научился
        // пользоваться и если он поверх тоннеля с лестницей, то даже с
        // ОТКРЫТЫМ не может вылезти». Журнал того же вечера:
        //   «лезу вверх: Y 115,1 → 117 (выход на кромку)»
        //   «[тело] врос в блок на Y=117,96 — выбираюсь»   ← и так семь раз
        //
        // ПОЧЕМУ. Створка ДВЕРИ отходит ВБОК — вокруг оси Y (BEBehaviorDoor), и
        // ровно это считалось ниже: боксы крутились на rotateYRad и ещё на ∓90°
        // вокруг Y. Створка ЛЮКА откидывается ВВЕРХ — вокруг оси X
        // (BEBehaviorTrapDoor.UpdateHitBoxes: RotatedCopy(90, 0, 0) и лишь потом
        // матрица места). У люка в реестре коробка — ПЛИТА ВО ВСЮ КЛЕТКУ
        // (assets/…/woodtyped/trapdoor.json: y2 = 0,1875 при x2 = z2 = 1), и
        // поворот такой плиты вокруг Y оставляет её ТОЙ ЖЕ ПЛИТОЙ. То есть для
        // физики бота открытый люк оставался ПОЛОМ — тело упиралось в него
        // головой и «врастало», сколько бы раз его ни открывали.
        //
        // И привычек у люка своих две: он помнит, к какой грани прибит
        // (attachedFace), и свой поворот (rotDeg) — про rotateYRad с
        // invertHandles он не знает вовсе, и дверное правило читало у него
        // пустоту, то есть нули
        if (IsTrapdoorId(ownerId))
            return doorBlockCache.GetOrAdd(
                (ownerId,
                 Vintagestory.API.MathTools.GameMath.Mod(attrs.GetInt("rotDeg"), 360) / 90,
                 attrs.GetBool("opened"),
                 false,
                 Math.Clamp(attrs.GetInt("attachedFace"), 0, 5)),
                BuildDoorBlock);

        // Дверь ставится только в четырёх ориентациях — округляем до них
        int rot = (int)Math.Round(attrs.GetFloat("rotateYRad") / (Math.PI / 2)) & 3;
        var key = (ownerId, rot, attrs.GetBool("opened"), attrs.GetBool("invertHandles"), НеЛюк);
        return doorBlockCache.GetOrAdd(key, BuildDoorBlock);
    }

    /// <summary>Люк ли это по реестру (у люка своя геометрия створки).</summary>
    private bool IsTrapdoorId(int id) =>
        id > 0 && id < blockIsTrapdoor.Length && blockIsTrapdoor[id];

    /// <summary>
    /// КУДА И КАК ПОВЁРНУТА СТВОРКА ЛЮКА — матрица его места, число в число по
    /// <c>BEBehaviorTrapDoor.getTfMatrix</c> (VSSurvivalMod 1.22.7). Своей
    /// геометрии тут нет ни на строку: и порядок поворотов, и то, что у
    /// «отрицательной» вертикальной грани добавляется разворот на 180°, взято
    /// у самой игры.
    /// </summary>
    /// <param name="attachedFace">Грань, к которой прибит люк (индекс BlockFacing.ALLFACES).</param>
    /// <param name="rotDeg">Его собственный поворот, градусы.</param>
    public static Vintagestory.API.Client.Matrixf TrapDoorMatrix(int attachedFace, int rotDeg)
    {
        var face = Vintagestory.API.MathTools.BlockFacing.ALLFACES[
            Math.Clamp(attachedFace, 0, 5)];
        var m = new Vintagestory.API.Client.Matrixf().Translate(0.5f, 0.5f, 0.5f);
        if (face.IsVertical)
            m = m.RotateYDeg(rotDeg).RotateZDeg(face.Negative ? 180 : 0);
        else
            m = m.RotateYDeg(face.HorizontalAngleIndex * 90).RotateYDeg(90).RotateZDeg(rotDeg);
        return m.Translate(-0.5f, -0.5f, -0.5f);
    }

    private Vintagestory.API.Common.Block? BuildDoorBlock(
        (int Id, int Rot, bool Open, bool Inv, int Face) key)
    {
        if (!doorPackets.TryGetValue(key.Id, out var packet) ||
            rebuildRegistry == null || rebuildAccessor == null)
            return null;
        try
        {
            // Отдельный экземпляр блока: боксы правим только у него,
            // общий объект из реестра трогать нельзя
            var block = Vintagestory.Common.BlockTypeNet.ReadBlockTypePacket(
                packet, rebuildAccessor, rebuildRegistry);
            block.BlockId = key.Id;
            if (block.CollisionBoxes is not { Length: > 0 } src)
                return block;

            var centre = new Vintagestory.API.MathTools.Vec3d(0.5, 0.5, 0.5);
            var boxes = new Vintagestory.API.MathTools.Cuboidf[src.Length];
            // Матрица места у люка одна на все его коробки — считаем её раз
            var люк = key.Face >= 0 ? TrapDoorMatrix(key.Face, key.Rot * 90) : null;
            for (int i = 0; i < src.Length; i++)
            {
                var box = src[i];
                if (люк is { } tf)
                {
                    // ПОРЯДОК ТОТ ЖЕ, ЧТО У ИГРЫ, И ОН ВАЖЕН: сперва створку
                    // ОТКИДЫВАЕМ (поворот вокруг X на 90° около середины
                    // клетки), и лишь потом ставим на место матрицей грани и
                    // поворота — BEBehaviorTrapDoor.UpdateHitBoxes
                    if (key.Open)
                        box = box.RotatedCopy(90f, 0f, 0f, centre);
                    boxes[i] = box.TransformedCopy(tf.Values);
                    continue;
                }
                if (key.Rot != 0)
                    box = box.RotatedCopy(0, key.Rot * 90f, 0, centre);
                if (key.Open)
                    box = box.RotatedCopy(0, key.Inv ? 90f : -90f, 0, centre);
                boxes[i] = box;
            }
            block.CollisionBoxes = boxes;
            block.SelectionBoxes = boxes;
            return block;
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"дверь {key.Id}: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>Ошибка разбора реестра (диагностика).</summary>
    public event Action<string>? OnParseError;

    /// <summary>Настоящие объекты блоков собраны — можно запускать физику игры.</summary>
    public event Action? OnGameBlocksReady;

    /// <summary>Разбор поведения HealingItem из реестра предметов.</summary>
    private static HealingInfo? ReadHealing(Packet_Behavior[]? behaviors, int count)
    {
        if (behaviors == null)
            return null;
        for (int i = 0; i < count; i++)
        {
            var b = behaviors[i];
            if (b?.Code == null || !b.Code.Contains("Healing", StringComparison.OrdinalIgnoreCase))
                continue;

            // Значения по умолчанию — как в CollectibleBehaviorHealingItem
            float health = 1f, applySec = 2f, effectSec = 10f;
            bool cancelInAir = true, cancelSwimming = false;
            if (!string.IsNullOrEmpty(b.Attributes))
            {
                try
                {
                    var attrs = Newtonsoft.Json.Linq.JObject.Parse(b.Attributes);
                    T Read<T>(string name, T fallback)
                    {
                        var token = attrs.GetValue(name, StringComparison.OrdinalIgnoreCase);
                        return token == null ? fallback : token.ToObject<T>() ?? fallback;
                    }
                    health = Read("health", health);
                    applySec = Read("applicationTimeSec", applySec);
                    effectSec = Read("effectDurationSec", effectSec);
                    cancelInAir = Read("cancelInAir", cancelInAir);
                    cancelSwimming = Read("cancelWhileSwimming", cancelSwimming);
                }
                catch { /* нестандартный формат — берём значения по умолчанию */ }
            }
            return new HealingInfo(health, applySec, effectSec, cancelInAir, cancelSwimming);
        }
        return null;
    }

    /// <summary>Найти коды предметов по части названия (для поиска в реестре сервера).</summary>
    public IEnumerable<string> SearchItemCodes(string mask, int limit = 20) =>
        itemCodes.Where(c => c != null && c.Contains(mask, StringComparison.OrdinalIgnoreCase))
                 .Take(limit)!;

    /// <summary>Найти коды блоков по части названия.</summary>
    public IEnumerable<string> SearchBlockCodes(string mask, int limit = 20) =>
        blockCodes.Where(c => c != null && c.Contains(mask, StringComparison.OrdinalIgnoreCase))
                  .Take(limit)!;

    /// <summary>Тип инструмента (EnumTool из игры) или null, если не инструмент.</summary>
    public int? GetToolTypeByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemToolType.Length && itemToolType[id] >= 0
            ? itemToolType[id]
            : null;

    /// <summary>
    /// Снаряжение «для рук»: инструмент или оружие. Такие предметы бот держит
    /// в хотбаре — из рюкзака ими не поработать (в руке только активный слот).
    /// </summary>
    public bool IsHandGearByCode(string code) =>
        GetToolTypeByCode(code) != null || GetAttackPowerByCode(code) > 1f;

    /// <summary>Урон предмета по коду (0 — не оружие/нет данных).</summary>
    public float GetAttackPowerByCode(string code) =>
        itemCodeToId.TryGetValue(code, out int id) && id < itemAttackPower.Length ? itemAttackPower[id] : 0;

    /// <summary>
    /// Блок-сущность в точке (табличка, сундук...) — с честной проверкой
    /// «а видно ли её отсюда», или null.
    ///
    /// ЧЕСТНОЕ ЗРЕНИЕ ДЕЙСТВУЕТ И ТУТ, той же меркой, что у
    /// <see cref="AllBlockEntities"/>: отдаём только то, у чьего блока есть
    /// открытая грань. Раньше здесь фильтра не было вовсе, и один этот метод
    /// сводил на нет всю честность списка выше — сундук, кровать и табличку за
    /// глухой стеной бот читал поштучно, зная координаты. Читать надпись
    /// сквозь стену живой игрок не может.
    ///
    /// ОСТОРОЖНО С ЭТОЙ ПРОВЕРКОЙ: она чуть не убила бота насмерть. Живой
    /// прогон 09.08 кончался переполнением стека на входе в мир — две тысячи
    /// витков вот такого кольца:
    ///
    ///   IsExposed → IsPassable → GetCollisionTop → IsDoorOpen →
    ///   GetBlockEntity → IsExposed → …
    ///
    /// Кольцо замыкала открытая дверь: чтобы понять, проходима ли клетка,
    /// мир спрашивает «не открытая ли это дверь», состояние новой двери лежит
    /// в её блок-сущности, а та отдавалась только «если видно» — и проверка
    /// видимости снова упиралась в проходимость соседних клеток.
    ///
    /// Поэтому у честности здесь ОДИН уровень: пока мы отвечаем на вопрос
    /// «видно ли», внутрь этого же вопроса второй раз не заходим и отдаём
    /// данные как есть. Подглядеть таким путём нечего: состояние створки
    /// видно снаружи любому, кто на дверь смотрит.
    /// </summary>
    public BlockEntityInfo? GetBlockEntity(BlockPos pos)
    {
        if (!blockEntities.TryGetValue(pos, out var be))
            return null;
        if (SeeThroughWalls?.Invoke() ?? !OnlyVisibleBlocks)
            return be;
        if (askingWhatIsVisible)
            return be;

        askingWhatIsVisible = true;
        try
        {
            return IsExposed(pos.X, pos.Y, pos.Z) ? be : null;
        }
        finally
        {
            askingWhatIsVisible = false;
        }
    }

    /// <summary>
    /// Мы уже внутри вопроса «видно ли это». Поле, а не довод, потому что
    /// кольцо замыкается через четыре чужих метода, и тащить признак через
    /// все — значит поменять полдюжины подписей ради одной двери.
    /// </summary>
    [ThreadStatic]
    private static bool askingWhatIsVisible;

    /// <summary>
    /// МОЖНО ЛИ ЧЕСТНО ЗАГЛЯНУТЬ ВНУТРЬ контейнера в этой точке. Подключает
    /// VsBot — тем же швом, что и <see cref="SeeThroughWalls"/>: сам мир про
    /// честность не знает, он только отдаёт данные чанка.
    ///
    /// Не подключено — отдаём всё: голый WorldModel живёт и в стенде, где
    /// никакой роли и никаких читов нет.
    /// </summary>
    public Func<BlockPos, bool>? MayLookInside { get; set; }

    /// <summary>
    /// Содержимое контейнера (сундук, корзина) из данных его блок-сущности.
    ///
    /// СЕРВЕР ШЛЁТ ЭТО ВМЕСТЕ С ЧАНКОМ, ОТКРЫВАТЬ НИЧЕГО НЕ НАДО — и в этом
    /// вся беда. Живой игрок узнаёт содержимое сундука ТОЛЬКО открыв его, а
    /// бот пересчитывал чужие закрытые сундуки и решал на этом «туда не иду».
    /// Поэтому вход сюда сторожит <see cref="MayLookInside"/>: без него
    /// подглядывание было молчаливым читом, а флаг
    /// <see cref="Cheats.SeeInsideContainers"/> был пустой надписью.
    ///
    /// null — контейнера в точке нет ИЛИ заглядывать в него нечестно.
    /// Идентификатор инвентаря для операций записи — "chest-x, y, z".
    /// </summary>
    public SlotContent[]? GetContainerContents(BlockPos pos)
    {
        if (MayLookInside is { } may && !may(pos))
            return null;
        return ContentsFromChunk(pos);
    }

    /// <summary>
    /// СОДЕРЖИМОЕ, КОТОРОЕ ВИДНО СНАРУЖИ — без открытия и без чита.
    ///
    /// Такое бывает: игра РИСУЕТ содержимое костра (FirepitContentsRenderer) —
    /// кусок мяса на огне и готовое в выходе человек видит, просто стоя рядом,
    /// и требовать за это открытия значило бы сделать бота слепее живого
    /// игрока. Готовка тем и живёт: ждёт готового, поглядывая на костёр.
    ///
    /// ЗВАТЬ ЭТО ДЛЯ СУНДУКА НЕЛЬЗЯ: у сундука содержимое не нарисовано, и
    /// честный путь к нему один — <see cref="GetContainerContents"/>.
    /// </summary>
    public SlotContent[]? VisibleContents(BlockPos pos) => ContentsFromChunk(pos);

    private SlotContent[]? ContentsFromChunk(BlockPos pos)
    {
        var tree = GetBlockEntity(pos)?.Attributes?.GetTreeAttribute("inventory");
        return tree == null ? null : ReadInventoryTree(tree);
    }

    /// <summary>
    /// ЕСТЬ ЛИ В ЭТОЙ ТОЧКЕ КОНТЕЙНЕР — по реестру блоков, а не по содержимому.
    ///
    /// Разница принципиальная. «У блока есть inventoryClassName» — это про ТИП
    /// блока, то есть ровно то, что живой игрок видит глазами: вот сундук, вот
    /// корзина, вот ящик мода. «Сервер прислал слоты» — это уже заглядывание
    /// внутрь, и спрашивать им «а сундук ли тут» значит подглядывать ради
    /// ответа, который виден снаружи.
    /// </summary>
    public bool IsContainerBlock(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        return id > 0 && id < blockInvClass.Length && blockInvClass[id] is { Length: > 0 };
    }

    /// <summary>
    /// Прочитать инвентарь из дерева атрибутов — сундук, костёр, туша.
    ///
    /// Формат один и тот же везде, где игра хранит инвентарь в атрибутах
    /// (InventoryBase.ToTreeAttributes): «qslots» — сколько слотов, «slots» —
    /// поддерево со стаками по номеру. Раньше этот разбор был переписан в трёх
    /// местах слово в слово; поменяйся формат — чинить пришлось бы трижды.
    /// </summary>
    public SlotContent[] ReadInventoryTree(Vintagestory.API.Datastructures.ITreeAttribute tree)
    {
        int qslots = Math.Max(0, tree.GetInt("qslots"));
        var slotsTree = tree.GetTreeAttribute("slots");
        var result = new SlotContent[qslots];
        for (int i = 0; i < qslots; i++)
            result[i] = ReadStack(slotsTree?.GetItemstack(i.ToString()));
        return result;
    }

    /// <summary>Стак игры → содержимое слота (код по своим реестрам).</summary>
    public SlotContent ReadStack(Vintagestory.API.Common.ItemStack? stack)
    {
        if (stack == null || stack.StackSize <= 0)
            return new SlotContent(null, 0);
        return new SlotContent(CodeOfStack(stack) ?? $"?{(int)stack.Class}:{stack.Id}",
            stack.StackSize);
    }

    /// <summary>
    /// КОД ВЕЩИ В СТОПКЕ — одно место на весь проект.
    ///
    /// Сервер шлёт стопку двумя числами: класс (EnumItemClass: Block=0, Item=1)
    /// и id, а имена лежат в двух РАЗНЫХ реестрах. Перепутать их — не «немного
    /// не тот код», а совсем другая вещь: id 42 среди блоков и id 42 среди
    /// предметов не имеют друг к другу отношения.
    ///
    /// Этот же вопрос задают четверо: содержимое слота, выпадения блока
    /// (<see cref="DropsOfBlock"/>), бросовая порода (<see cref="Hands"/>) и
    /// поручения (<see cref="Errands.DropToolsFor"/>). Написанный четыре раза,
    /// он и был написан четыре раза — слово в слово.
    /// </summary>
    public string? CodeOfStack(Vintagestory.API.Common.ItemStack? stack) =>
        stack == null ? null
        : (int)stack.Class == 0 ? BlockIdToCode(stack.Id) : ItemIdToCode(stack.Id);

    /// <summary>
    /// ЧТО ВЫПАДАЕТ ИЗ БЛОКА — коды вещей по реестру сервера (Block.Drops).
    ///
    /// ЗАКАЗЧИК ПРАВ, что бот это знает: в журнале стоит «подобрал
    /// looseores-nativecopper-andesite-free (+2 nugget-nativecopper)». Знание
    /// это ОДНО на весь проект и живёт здесь — у того, кто держит реестр.
    /// Сбор (<see cref="Gathering.DropsOf"/>) добавляет к нему только память
    /// на время сессии, а руда (<see cref="Ores.StoneDrops"/>) и поручения
    /// спрашивают через них же. Ни одного списка кодов в коде нет: с модовым
    /// блоком это работает так же.
    /// </summary>
    public string[] DropsOfBlock(string? code)
    {
        if (GetGameBlockByCode(code) is not { BlockId: > 0, Drops: { Length: > 0 } drops })
            return [];
        var list = new List<string>(drops.Length);
        foreach (var d in drops)
            if (CodeOfStack(d?.ResolvedItemstack) is { Length: > 0 } c)
                list.Add(c);
        return list.ToArray();
    }

    // --- Настоящие объекты блоков игры (для её же физики) ---
    private Vintagestory.API.Common.Block[] gameBlocks = [];
    private Vintagestory.API.Common.Block? airBlock;

    /// <summary>
    /// Блок игры по нашему id. Именно такие объекты ждёт игровой код физики:
    /// у них настоящие коллизии, LiquidLevel, Climbable, WalkSpeedMultiplier.
    /// Собираются из реестра сервера тем же методом, что и у клиента игры.
    /// </summary>
    public Vintagestory.API.Common.Block GetGameBlockById(int id) =>
        id > 0 && id < gameBlocks.Length && gameBlocks[id] != null ? gameBlocks[id] : airBlock!;

    /// <summary>Блок игры в точке мира.</summary>
    public Vintagestory.API.Common.Block GetGameBlock(int x, int y, int z) =>
        GetGameBlockById(GetBlockId(x, y, z));

    /// <summary>
    /// БЛОК, КОТОРЫЙ В ЭТОЙ КЛЕТКЕ ВИДИТ ФИЗИКА, — единственный ответ на этот
    /// вопрос на весь проект.
    ///
    /// Отличается от <see cref="GetGameBlock"/> ровно у тех двух видов блоков,
    /// чью форму игра держит НЕ в типе, а в блок-сущности: у дверей (створка
    /// повёрнута, <see cref="DoorPhysicsBlock"/>) и у тёсаных блоков (форма —
    /// набор вокселей, <see cref="MicroBlockPhysicsBlock"/>). У всех прочих
    /// ответы совпадают.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЭТО ОДНА ФУНКЦИЯ, А НЕ ДВЕ (прогон 19.08,
    /// 00:53–01:05). Порядок подмен знала только физика
    /// (<c>BotBlockAccessor.At</c>), а команда «!блок» спрашивала сырой тип — и
    /// у РАСПАХНУТОЙ двери печатала коробку ЗАКРЫТОЙ створки
    /// <c>[0..1 0..1 0,88..1]</c>. Человек полчаса искал причину застревания в
    /// проёме по показаниям, которые врали: тело в это время проходило сквозь
    /// ту же клетку насквозь. Диагностика, показывающая не то, чем живёт
    /// физика, хуже отсутствующей — по ней делают выводы.
    /// </summary>
    public Vintagestory.API.Common.Block PhysicsBlock(int x, int y, int z) =>
        DoorPhysicsBlock(x, y, z) ??
        MicroBlockPhysicsBlock(x, y, z) ??
        GetGameBlock(x, y, z);

    /// <summary>Жидкость игры в точке мира (слой жидкостей).</summary>
    public Vintagestory.API.Common.Block GetGameLiquid(int x, int y, int z) =>
        GetGameBlockById(GetLiquidId(x, y, z));

    /// <summary>Блок игры по коду (нужен игровому коду изредка).</summary>
    public Vintagestory.API.Common.Block GetGameBlockByCode(string? code) =>
        code != null && codeToId.TryGetValue(code, out int id) ? GetGameBlockById(id) : airBlock!;

    /// <summary>Готовы ли настоящие объекты блоков (реестр пришёл и разобран).</summary>
    public bool GameBlocksReady => gameBlocks.Length > 0 && airBlock != null;

    /// <summary>
    /// Все блок-сущности, которые бот когда-либо видел.
    ///
    /// ЧЕСТНОЕ ЗРЕНИЕ РАБОТАЕТ И ЗДЕСЬ. Сервер присылает сундуки, таблички,
    /// кровати и костры вместе с чанком — включая те, что стоят в запертом
    /// доме за глухой стеной. Читать надпись сквозь стену и пересчитывать
    /// чужие сундуки живой игрок не может, а это ещё и социально худший вид
    /// подглядывания: он касается других людей, а не камня.
    ///
    /// Поэтому по умолчанию отдаются только те, у чьего блока есть открытая
    /// грань. Роль-читер снимает это через <see cref="Cheats.SeeThroughWalls"/>.
    /// </summary>
    public IEnumerable<BlockEntityInfo> AllBlockEntities =>
        (SeeThroughWalls?.Invoke() ?? !OnlyVisibleBlocks)
            ? blockEntities.Values
            : blockEntities.Values.Where(be => IsExposed(be.Pos.X, be.Pos.Y, be.Pos.Z));

    /// <summary>
    /// ПОСТАВЛЕННАЯ ЛИ ЭТО ВЕЩЬ — дверь, сундук, табличка, кровать, печь.
    /// Отвечает РЕЕСТР ИГРЫ: у такого блока есть класс блок-сущности
    /// (<c>Block.EntityClass</c>), потому что ему есть что хранить. У камня и
    /// земли его нет.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="BlockEntitiesIn"/>. Тот перечисляет
    /// ПРИСЛАННЫЕ сервером блок-сущности и нарочно просеивается честным
    /// зрением: читать надпись сквозь стену живой игрок не может. А этот
    /// вопрос — про ТИП блока, и он не подглядывание: код блока бот читает и
    /// так, а «дверь ли это» видно с порога. Живой случай 23.08: застава
    /// спрашивала первого и не видела двери, в которой бот стоял.
    /// </summary>
    public bool IsPlacedThing(int x, int y, int z)
    {
        int id = GetBlockId(x, y, z);
        return id > 0 && id < blockIsPlacedThing.Length && blockIsPlacedThing[id];
    }

    /// <summary>
    /// Сколько поставленных вещей в объёме (границы включительно).
    ///
    /// СПРАШИВАЕМ ОБА УЧЁТА, И ЭТО ОДИН ВОПРОС, А НЕ ДВА ПРАВИЛА. Реестр знает
    /// ТИП блока («у двери есть класс блок-сущности»), а список присланных
    /// блок-сущностей знает ЭКЗЕМПЛЯР («вот тут стоит сундук с добром»).
    /// Каждый по отдельности слеп на своём месте: реестр молчит, если сервер
    /// объявил блок без класса, а присланные — если чанк с ними ещё не дошёл
    /// или их скрыло честное зрение. Оба промаха ловились стендами живьём.
    /// Клетку считаем ОДИН раз, каким бы учётом её ни назвали.
    /// </summary>
    public int PlacedThingsIn(BlockPos min, BlockPos max)
    {
        var sent = new HashSet<(int, int, int)>();
        foreach (var be in BlockEntitiesIn(min, max))
            sent.Add((be.Pos.X, be.Pos.Y, be.Pos.Z));

        int count = 0;
        for (int x = min.X; x <= max.X; x++)
            for (int y = min.Y; y <= max.Y; y++)
                for (int z = min.Z; z <= max.Z; z++)
                    if (IsPlacedThing(x, y, z) || sent.Contains((x, y, z)))
                        count++;
        return count;
    }

    /// <summary>Все известные блок-сущности в объёме (границы включительно).</summary>
    public IEnumerable<BlockEntityInfo> BlockEntitiesIn(BlockPos min, BlockPos max) =>
        AllBlockEntities.Where(be =>
            be.Pos.X >= min.X && be.Pos.X <= max.X &&
            be.Pos.Y >= min.Y && be.Pos.Y <= max.Y &&
            be.Pos.Z >= min.Z && be.Pos.Z <= max.Z);
}
