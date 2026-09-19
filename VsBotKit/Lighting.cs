using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.Common;

namespace VsBotKit;

/// <summary>
/// Освещённость клетки так, как её хранит игра: два независимых числа 0..31.
/// </summary>
/// <param name="Sun">
/// Солнечный свет — сколько неба «дотекает» до клетки. Это НЕ яркость: ночью
/// клетка с Sun=31 всё равно тёмная, игра домножает Sun на силу дневного света
/// (BlockAccessorBase.GetLightLevel, случай MaxTimeOfDayLight).
/// </param>
/// <param name="Block">Свет от блоков (факел, костёр, лава) — не зависит от времени суток.</param>
/// <param name="Stale">
/// true — с момента прихода чанка сервер менял в нём блоки, а пересчёт света
/// делает КЛИЕНТ у себя (ClientSystemRelight/ChunkIlluminator), не сервер.
/// Мы этот пересчёт не ведём, поэтому значение устарело и верить ему нельзя.
/// </param>
public readonly record struct LightSample(int Sun, int Block, bool Stale);

/// <summary>
/// Свет и факелы.
///
/// ЧЕСТНО ОБ ИСТОЧНИКЕ СВЕТА (проверено по исходникам, а не по догадке):
/// - освещённость ПРИХОДИТ клиенту в пакете чанка: Packet_ServerChunk.Light
///   (поле 2) — сжатый слой света, и Packet_ServerChunk.LightSat (поле 3) —
///   его палитра. Заполняет их сервер: ServerChunk.ToPacket →
///   ChunkData.CompressInto(ref blocks, ref light, ref lightPalette, ref fluids).
///   Настоящий клиент разбирает их в ClientWorldMap.LoadChunkFromPacket →
///   ClientChunk.CreateNewCompressed → ChunkData.DecompressFrom →
///   ChunkDataLayer.DecompressSeparate(lightCompressed, lightPaletteCompressed).
/// - формат значения: биты 0..4 — солнечный свет, биты 5..9 — свет блоков
///   (ChunkData.GetSunlight/GetBlocklight, BlockAccessorBase.GetLightLevel).
/// - НО: WorldModel этот слой не хранит (в ChunkSlot лежат только блоки и
///   жидкости), поэтому Lighting подписывается на пакеты сам и держит свой
///   слой света. Разжатие — методом самой игры через reflection, как в
///   WorldModel.
/// - ГЛАВНОЕ ОГРАНИЧЕНИЕ: после загрузки чанка сервер свет больше НЕ шлёт.
///   Когда блок меняется, настоящий клиент пересчитывает свет сам
///   (ClientWorldMap.UpdateLighting кладёт задачу в очередь, ClientSystemRelight
///   её считает через ChunkIlluminator). Мы иллюминатор не гоняем, поэтому
///   после любого изменения блоков в округе помечаем свет чанка устаревшим
///   (Stale) и не выдаём его за правду. Свежесть возвращается сама, когда
///   сервер пришлёт чанк заново (выход из зоны прогрузки и возврат).
///
/// Из-за этого поставленный факел НЕ виден в цифрах света. Поэтому здесь есть
/// и честный заменитель: считать не люксы, а факелы — их коды видны в модели
/// мира так же, как любой другой блок.
///
/// Это механизм. Где освещать, когда и сколько тратить факелов — дело роли.
/// </summary>
public class Lighting
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Lighting(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
        ctx.Bot.OnPacket += HandlePacket;
    }

    public event Action<string>? OnLog;

    /// <summary>Факел встал в клетку (позиция).</summary>
    public event Action<BlockPos>? OnTorchPlaced;

    // ---------------- настройки кодов ----------------

    /// <summary>
    /// Что берём в руку и ставим. Только ГОРЯЩИЙ факел светит: lightHsv в
    /// assets/survival/blocktypes/wood/torch.json задан лишь для "*-lit-*".
    /// Ориентацию в коде не указываем — сервер сам подставит ту грань,
    /// к которой факел прицепился (BlockGroundAndSideAttachable.TryAttachTo:
    /// CodeWithVariant("orientation", onBlockFace.Code)).
    /// </summary>
    public string TorchCode { get; set; } = "torch-basic-lit";

    /// <summary>Погашенный факел (крафтится именно такой: рецепт даёт torch-basic-extinct-up).</summary>
    public string UnlitTorchCode { get; set; } = "torch-basic-extinct";

    /// <summary>
    /// Прогоревший факел. Горящий факел живёт 48 игровых часов
    /// (attributes.transientPropsbyType "*-basic-lit-*" → convertTo
    /// "torch-basic-burnedout-*") и после этого не светит и ничего не роняет
    /// (dropsByType "*-burnedout-*": []).
    /// </summary>
    public string BurnedOutCode { get; set; } = "torch-basic-burnedout";

    /// <summary>Сколько секунд ждать подтверждения от сервера.</summary>
    public double ConfirmSeconds { get; set; } = 2.0;

    // ---------------- свет из чанков ----------------

    private sealed class LightSlot
    {
        public byte[]? Data;        // Packet_ServerChunk.Light
        public byte[]? Palette;     // Packet_ServerChunk.LightSat
        public int Compver;
        public bool Stale;
        public ChunkData? Decoded;
        public long Touched;        // для вытеснения редко используемых
    }

    private readonly Dictionary<(int cx, int cy, int cz), LightSlot> light = new();
    private readonly object lightLock = new();
    private long touchCounter;

    /// <summary>
    /// Сколько разжатых слоёв света держим в памяти. Каждый — до десятка
    /// массивов по 1024 int, поэтому лишние возвращаем в пул игры.
    /// </summary>
    public int DecodedChunkCache { get; set; } = 64;

    /// <summary>
    /// Радиус в блоках, в котором изменение блока портит наши цифры света.
    /// Свет распространяется максимум на 31 клетку, но факел светит на 14
    /// (lightHsv [4,4,14]), поэтому по умолчанию берём середину: меньше —
    /// начнём врать, больше — почти всё вокруг бота вечно «устарело».
    /// </summary>
    public int StaleRadius { get; set; } = 16;

    /// <summary>Пришёл ли хоть один чанк со слоем света.</summary>
    public bool LightAvailable { get; private set; }

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 10: // Chunks
                if (p.Chunks?.Chunks != null)
                    for (int i = 0; i < p.Chunks.ChunksCount; i++)
                        StoreLight(p.Chunks.Chunks[i]);
                break;

            case 11: // UnloadServerChunk
                if (p.UnloadChunk is { } u && u.X != null)
                    lock (lightLock)
                        for (int i = 0; i < u.XCount; i++)
                            if (light.Remove((u.X[i], u.Y[i], u.Z[i]), out var gone))
                                Release(gone);
                break;

            case 7: // SetBlock
                if (p.SetBlock is { } sb)
                    MarkStale(sb.X, sb.Y, sb.Z);
                break;

            case 58: // ExchangeBlock
                if (p.ExchangeBlock is { } ex)
                    MarkStale(ex.X, ex.Y, ex.Z);
                break;

            case 47 or 63 or 70: // SetBlocks / NoRelight / Minimal
                if (p.SetBlocks?.SetBlocks != null)
                {
                    var pair = BlockTypeNet.UnpackSetBlocks(p.SetBlocks.SetBlocks, out _);
                    foreach (var pos in pair.Key)
                        MarkStale(pos.X, pos.Y, pos.Z);
                }
                break;
        }
    }

    private void StoreLight(Packet_ServerChunk c)
    {
        // Пустой слой света бывает: ChunkData.CompressInto кладёт Array.Empty,
        // если lightLayer == null. Тогда света в этом чанке мы просто не знаем.
        bool has = c.Light is { Length: > 0 } && c.LightSat is { Length: > 0 };
        lock (lightLock)
        {
            if (light.TryGetValue((c.X, c.Y, c.Z), out var old))
                Release(old);
            light[(c.X, c.Y, c.Z)] = new LightSlot
            {
                Data = has ? c.Light : null,
                Palette = has ? c.LightSat : null,
                Compver = c.Compver,
                Stale = false
            };
        }
        if (has)
            LightAvailable = true;
    }

    private void MarkStale(int x, int y, int z)
    {
        int r = Math.Max(0, StaleRadius);
        lock (lightLock)
        {
            for (int cx = (x - r) / WorldModel.ChunkSize; cx <= (x + r) / WorldModel.ChunkSize; cx++)
                for (int cy = (y - r) / WorldModel.ChunkSize; cy <= (y + r) / WorldModel.ChunkSize; cy++)
                    for (int cz = (z - r) / WorldModel.ChunkSize; cz <= (z + r) / WorldModel.ChunkSize; cz++)
                        if (light.TryGetValue((cx, cy, cz), out var slot))
                            slot.Stale = true;
        }
    }

    /// <summary>Новая сессия/переподключение: чужой мир — чужой свет.</summary>
    public void Reset()
    {
        lock (lightLock)
        {
            foreach (var slot in light.Values)
                Release(slot);
            light.Clear();
        }
        LightAvailable = false;
    }

    // Разжатие слоя света методом самой игры: DecompressFrom — internal,
    // но обычный метод экземпляра, поэтому берётся открытым делегатом
    // (ровно тот же приём, что в WorldModel для UnpackBlocksTo).
    private delegate void DecompressFromDelegate(ChunkData self, byte[]? blocks,
        byte[] lightCompressed, byte[] lightPaletteCompressed, byte[]? fluids, int chunkdataVersion);

    private static readonly DecompressFromDelegate? Decompress =
        typeof(ChunkData).GetMethod("DecompressFrom", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.CreateDelegate<DecompressFromDelegate>();

    /// <summary>
    /// Пул массивов чанка. Игровой ChunkDataPool требует ServerMain (у нас его
    /// нет) и лезет в ServerMain.Logger — поэтому берём защищённый конструктор
    /// без сервера и подменяем логгер молчуном. Иначе редкая ветка
    /// «palette length != databits» в ChunkDataLayer.DecompressSeparate уронила
    /// бы разбор чанка по NullReferenceException.
    /// </summary>
    private sealed class BotChunkDataPool : ChunkDataPool
    {
        private sealed class SilentLogger : LoggerBase
        {
            protected override void LogImpl(EnumLogType logType, string format, params object[] args) { }
        }

        private readonly SilentLogger silent = new();

        public BotChunkDataPool() => chunksize = WorldModel.ChunkSize;

        public override ILogger Logger => silent;
        public override bool ShuttingDown => false;
    }

    private static readonly ChunkDataPool pool = new BotChunkDataPool();
    private static readonly object decodeLock = new(); // разжатие использует общие буферы

    private void Release(LightSlot slot)
    {
        if (slot.Decoded != null)
        {
            pool.Free(slot.Decoded);
            slot.Decoded = null;
        }
    }

    private ChunkData? Decoded(LightSlot slot)
    {
        if (slot.Decoded != null)
            return slot.Decoded;
        if (Decompress == null || slot.Data == null || slot.Palette == null)
            return null;
        // Старый формат (Compver 0) распаковывается только вместе с блоками
        // (ChunkData.OldStyleUnpack), а блоков у нас здесь нет — честно молчим.
        if (slot.Compver == 0)
            return null;

        var data = ChunkData.CreateNew(WorldModel.ChunkSize, pool);
        try
        {
            lock (decodeLock)
                Decompress(data, null, slot.Data, slot.Palette, null, slot.Compver);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"слой света не разобрался: {ex.Message}");
            slot.Data = null;   // второй раз не пробуем
            return null;
        }
        slot.Decoded = data;
        return data;
    }

    private void TrimCache()
    {
        // вызывается под lightLock
        int limit = Math.Max(1, DecodedChunkCache);
        int decoded = 0;
        foreach (var s in light.Values)
            if (s.Decoded != null) decoded++;
        while (decoded > limit)
        {
            LightSlot? oldest = null;
            foreach (var s in light.Values)
                if (s.Decoded != null && (oldest == null || s.Touched < oldest.Touched))
                    oldest = s;
            if (oldest == null)
                break;
            Release(oldest);
            decoded--;
        }
    }

    /// <summary>
    /// Свет в клетке или null, если чанк не приходил либо пришёл без слоя света.
    /// Смотрите на LightSample.Stale: true — цифры устарели (см. комментарий
    /// к классу), решать по ним нельзя.
    /// </summary>
    public LightSample? SampleAt(BlockPos pos) => SampleAt(pos.X, pos.Y, pos.Z);

    public LightSample? SampleAt(int x, int y, int z)
    {
        LightSlot? slot;
        lock (lightLock)
        {
            if (!light.TryGetValue((x / WorldModel.ChunkSize, y / WorldModel.ChunkSize,
                    z / WorldModel.ChunkSize), out slot))
                return null;
            slot.Touched = ++touchCounter;
        }

        ChunkData? data;
        lock (lightLock)
        {
            data = Decoded(slot);
            TrimCache();
        }
        if (data == null)
            return null;

        // Индекс тот же, что у игры: (y*32 + z)*32 + x внутри чанка
        int index = ((y % WorldModel.ChunkSize) * WorldModel.ChunkSize + z % WorldModel.ChunkSize)
                    * WorldModel.ChunkSize + x % WorldModel.ChunkSize;
        return new LightSample(data.GetSunlight(index), data.GetBlocklight(index), slot.Stale);
    }

    /// <summary>Свет от блоков (факелы, костры) — единственная цифра, не зависящая от времени суток.</summary>
    public int? BlockLightAt(BlockPos pos)
    {
        var s = SampleAt(pos);
        return s is { Stale: false } ? s.Value.Block : null;
    }

    /// <summary>
    /// Темно ли в клетке. null — не знаем (чанк без света или свет устарел),
    /// и это НЕ «светло»: вызывающий обязан решить сам (см. BehaviorKeepLit).
    ///
    /// Ночью считаем только свет блоков: солнечная составляющая ночью гасится
    /// силой дневного света (GetLightLevel, случай MaxTimeOfDayLight). Точную
    /// силу дневного света игра считает по положению солнца и луны
    /// (GameCalendar.GetDayLightStrength) — мы её не воспроизводим, поэтому
    /// огрубляем до «день/ночь» по GameClock и говорим об этом прямо.
    /// </summary>
    public bool? IsDark(BlockPos pos, int threshold = 6)
    {
        var sample = SampleAt(pos);
        if (sample is not { Stale: false })
            return null;
        var s = sample.Value;
        int level = ctx.Clock is { Ready: true, IsNight: true } ? s.Block : Math.Max(s.Sun, s.Block);
        return level < threshold;
    }

    // ---------------- факелы ----------------

    /// <summary>Горящий факел (только такой светит).</summary>
    public bool IsLitTorch(string? code) =>
        code != null && code.Contains("torch-", StringComparison.OrdinalIgnoreCase)
                     && code.Contains("-lit-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Прогоревший факел: не светит, при ломании ничего не даёт.</summary>
    public bool IsBurnedOutTorch(string? code) =>
        code != null && code.Contains(BurnedOutCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>Сколько горящих факелов в рюкзаке и хотбаре.</summary>
    public int TorchCount => hands.CountOf(TorchCode);

    /// <summary>Сколько погашенных факелов (их надо зажечь, чтобы от них был толк).</summary>
    public int UnlitTorchCount => hands.CountOf(UnlitTorchCode);

    /// <summary>Есть ли чем светить.</summary>
    public bool HasTorches => TorchCount > 0;

    /// <summary>
    /// Поставленные факелы вокруг точки. По умолчанию только горящие —
    /// прогоревший факел стоит, но не светит.
    /// </summary>
    public IEnumerable<BlockPos> TorchesNear(BlockPos center, int radius = 8,
        bool litOnly = true, int height = 4, int limit = 64) =>
        ctx.World.FindBlocks(
            code => litOnly ? IsLitTorch(code)
                            : code.Contains("torch-", StringComparison.OrdinalIgnoreCase),
            center, radius, height, limit);

    /// <summary>Прогоревшие факелы вокруг точки — кандидаты на замену.</summary>
    public IEnumerable<BlockPos> BurnedOutTorchesNear(BlockPos center, int radius = 8,
        int height = 4, int limit = 32) =>
        ctx.World.FindBlocks(IsBurnedOutTorch, center, radius, height, limit);

    /// <summary>Расстояние до ближайшего горящего факела (null — таких рядом нет).</summary>
    public double? DistanceToLitTorch(BlockPos pos, int radius = 16)
    {
        double best = double.MaxValue;
        foreach (var t in TorchesNear(pos, radius, litOnly: true, height: 4, limit: 64))
        {
            double d = Distance(pos, t);
            if (d < best) best = d;
        }
        return best == double.MaxValue ? null : best;
    }

    private static double Distance(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // Грани клетки в номерах игры: 0 север(-Z), 1 восток(+X), 2 юг(+Z),
    // 3 запад(-X), 4 верх, 5 низ.
    private static readonly (int Dx, int Dy, int Dz)[] FaceDir =
        [(0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0)];

    private static int Opposite(int face) => face switch
    {
        0 => 2, 2 => 0, 1 => 3, 3 => 1, 4 => 5, _ => 4
    };

    /// <summary>
    /// К каким граням клетки факел может прицепиться, в порядке предпочтения:
    /// сначала пол, потом стены.
    ///
    /// Почему так: сервер ставит блок в клетку, которую мы прислали (DidOffset),
    /// а опору ищет по грани — BlockGroundAndSideAttachable.TryAttachTo берёт
    /// blockpos + onBlockFace.Opposite. Значит, чтобы опорой стал пол (клетка
    /// снизу), грань клика должна быть «верх». К потолку факел не крепится:
    /// TryPlaceBlock перебирает ALLFACES кроме DOWN.
    /// </summary>
    public IEnumerable<int> AttachFaces(BlockPos pos)
    {
        if (ctx.World.IsSolid(pos.X, pos.Y - 1, pos.Z))
            yield return 4;                      // опора снизу → факел стоймя
        for (int dir = 0; dir < 4; dir++)        // горизонтальные соседи
        {
            var (dx, _, dz) = FaceDir[dir];
            if (ctx.World.IsSolid(pos.X + dx, pos.Y, pos.Z + dz))
                yield return Opposite(dir);      // опора со стороны dir → грань напротив
        }
    }

    /// <summary>
    /// Поставить факел в клетку. Успех — только если сервер прислал обратно
    /// блок, чей код действительно горящий факел.
    /// </summary>
    public async Task<bool> PlaceTorchAsync(BlockPos pos, CancellationToken ct = default)
    {
        if (!HasTorches)
        {
            OnLog?.Invoke(UnlitTorchCount > 0
                ? $"горящих факелов нет, есть {UnlitTorchCount} погашенных — их надо зажечь"
                : "факелов нет");
            return false;
        }
        if (ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) != 0 && !ctx.World.IsPassable(pos.X, pos.Y, pos.Z))
        {
            OnLog?.Invoke($"в {pos} уже что-то стоит");
            return false;
        }

        var faces = AttachFaces(pos).ToList();
        if (faces.Count == 0)
        {
            OnLog?.Invoke($"в {pos} факел не к чему прицепить (нет ни пола, ни стены)");
            return false;
        }

        foreach (int face in faces)
        {
            var result = await mining.PlaceAsync(pos, TorchCode, face, ct);
            if (!result.Success)
            {
                OnLog?.Invoke($"факел в {pos} гранью {face}: {result}");
                // Нечего ставить или не подойти — другая грань не поможет
                if (result.Why is MiningRefusal.Nothing or MiningRefusal.OutOfReach)
                    return false;
                continue;
            }

            // Подтверждение по коду блока: «сервер принял установку» ещё не
            // значит, что встал именно факел
            var code = ctx.World.GetBlockCode(pos);
            if (IsLitTorch(code))
            {
                OnTorchPlaced?.Invoke(pos);
                return true;
            }
            OnLog?.Invoke($"в {pos} после установки стоит {code ?? "неизвестно что"}, а не горящий факел");
            return false;
        }
        return false;
    }

    /// <summary>
    /// Подходит ли клетка под факел: пусто, есть за что зацепиться и рядом нет
    /// другого горящего факела ближе <paramref name="spacing"/>.
    /// </summary>
    public bool IsTorchSpot(BlockPos pos, double spacing)
    {
        if (!HasSpaceForTorch(pos))
            return false;
        var nearest = DistanceToLitTorch(pos, (int)Math.Ceiling(spacing));
        return nearest == null || nearest >= spacing;
    }

    /// <summary>Клетка пуста и есть за что зацепиться (без оглядки на другие факелы).</summary>
    public bool HasSpaceForTorch(BlockPos pos) =>
        ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) == 0 && AttachFaces(pos).Any();

    /// <summary>
    /// Найти место под факел вокруг точки. Обход идёт кольцами, поэтому
    /// возвращается ближайшее подходящее. null — некуда.
    /// </summary>
    /// <param name="onlyDarkWhenKnown">
    /// Если свет по этой клетке известен и свеж — ставить только там, где темно.
    /// Где свет неизвестен (обычное дело, см. комментарий к классу), решает
    /// расстояние между факелами.
    /// </param>
    public BlockPos? FindTorchSpot(BlockPos center, int radius = 8, double spacing = 6,
        int height = 2, bool onlyDarkWhenKnown = true, int darkThreshold = 6)
    {
        // Уже стоящие факелы собираем ОДИН раз: искать их заново для каждой
        // клетки-кандидата — это тысячи обходов чанков на один тик
        int look = radius + (int)Math.Ceiling(spacing);
        var torches = TorchesNear(center, look, litOnly: true, height: height + 2, limit: 128).ToList();

        for (int r = 0; r <= radius; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (r > 0 && Math.Abs(dx) != r && Math.Abs(dz) != r)
                        continue;               // внутренние кольца уже обошли
                    for (int dy = -height; dy <= height; dy++)
                    {
                        var pos = new BlockPos(center.X + dx, center.Y + dy, center.Z + dz);
                        if (!HasSpaceForTorch(pos))
                            continue;
                        if (torches.Any(t => Distance(pos, t) < spacing))
                            continue;           // сюда свет уже достаёт от соседнего факела
                        if (onlyDarkWhenKnown && IsDark(pos, darkThreshold) == false)
                            continue;           // здесь и так светло — свет знаем точно
                        return pos;
                    }
                }
        return null;
    }

    /// <summary>
    /// Зажечь погашенный факел от огня: взять его в руку и подержать правую
    /// кнопку на горящем блоке дольше двух секунд.
    ///
    /// Так это устроено в игре: BlockTorch помечен HeldPriorityInteract, его
    /// OnHeldInteractStep спрашивает у блока-цели IIgnitable.OnTryIgniteStack,
    /// и та возвращает IgniteNow при secondsIgniting > 2. Замена предмета в
    /// слоте происходит на СЕРВЕРЕ (ветка `world.Side == 2` — это Universal,
    /// а не сервер: EnumAppSide.Server = 1, Client = 2), поэтому результат
    /// виден в инвентаре и его можно честно подтвердить.
    ///
    /// ВАЖНО: цель должна гореть. По негорящему блоку тот же клик просто
    /// ПОСТАВИТ факел (сработает обычная установка блока).
    /// </summary>
    public async Task<bool> IgniteTorchAsync(BlockPos fireSource, double seconds = 2.5,
        CancellationToken ct = default)
    {
        var code = ctx.World.GetBlockCode(fireSource);
        if (code == null || !code.Contains("-lit", StringComparison.OrdinalIgnoreCase))
        {
            OnLog?.Invoke($"в {fireSource} нет огня ({code ?? "пусто"}) — зажигать не от чего");
            return false;
        }
        if (UnlitTorchCount == 0)
        {
            OnLog?.Invoke("погашенных факелов нет");
            return false;
        }

        int before = TorchCount;
        if (await hands.TakeToHandAsync(UnlitTorchCode, ct) is null)
        {
            OnLog?.Invoke("не смог взять погашенный факел в руку");
            return false;
        }
        if (!await hands.ReachForAsync(fireSource, ct: ct))
            return false;

        await hands.UseHeldAsync(seconds, fireSource, onFace: 4, ct: ct);

        var deadline = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (TorchCount > before)
                return true;
            await Task.Delay(80, ct).ContinueWith(_ => { });
        }
        OnLog?.Invoke("факел не загорелся: горящих в инвентаре не прибавилось");
        return false;
    }
}

/// <summary>
/// Держать рабочую зону освещённой: расставлять факелы с заданным шагом
/// и менять прогоревшие.
///
/// Почему по шагу, а не «где темно»: цифры света приходят только с чанком и
/// устаревают после первого же изменения блоков (подробно — в комментарии к
/// Lighting). Там, где свет известен и свеж, способность им пользуется;
/// где нет — работает по расстоянию между факелами, как это делает живой
/// игрок, который просто ставит факел каждые N шагов.
/// </summary>
public class BehaviorKeepLit : BotBehavior
{
    private readonly BotContext ctx;
    private readonly Lighting lighting;
    private DateTime nextTry = DateTime.MinValue;
    private bool complainedNoTorches;

    public BehaviorKeepLit(BotContext ctx, Lighting lighting)
    {
        this.ctx = ctx;
        this.lighting = lighting;
    }

    /// <summary>Выключатель: роль может гасить способность, не убирая её из списка.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Центр освещаемой зоны. null — вокруг самого бота (годится для шахты,
    /// где зона едет вместе с ним).
    /// </summary>
    public BlockPos? Center { get; set; }

    /// <summary>Радиус зоны в блоках.</summary>
    public int Radius { get; set; } = 8;

    /// <summary>Шаг между факелами: ближе этого второй факел не ставим.</summary>
    public double Spacing { get; set; } = 6;

    /// <summary>Сколько факелов держим в зоне — верхняя граница расточительности.</summary>
    public int MaxTorches { get; set; } = 6;

    /// <summary>
    /// Работать только ночью. По умолчанию нет: под землёй темно круглые
    /// сутки, а время суток — это уже политика роли.
    /// </summary>
    public bool OnlyAtNight { get; set; }

    /// <summary>Ниже этого уровня считаем, что темно (когда свет известен и свеж).</summary>
    public int DarkThreshold { get; set; } = 6;

    /// <summary>Сколько факелов оставить себе неприкосновенными (например, на дорогу назад).</summary>
    public int KeepInReserve { get; set; }

    /// <summary>Убирать прогоревшие факелы, чтобы освободить место под новый.</summary>
    public bool ReplaceBurnedOut { get; set; } = true;

    /// <summary>Пауза после неудачи, секунды: не долбиться в одно и то же место.</summary>
    public double RetrySeconds { get; set; } = 30;

    /// <summary>Факел поставлен (позиция).</summary>
    public event Action<BlockPos>? OnTorchPlaced;

    /// <summary>Факелы кончились (сообщается один раз, пока не появятся снова).</summary>
    public event Action? OnOutOfTorches;

    /// <summary>Зона освещена: ставить больше нечего или незачем.</summary>
    public event Action? OnZoneLit;

    private bool reportedLit;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || ctx.Movement.IsBusy || ctx.Self.IsDead)
            return false;
        if (DateTime.UtcNow < nextTry)
            return false;
        if (OnlyAtNight && ctx.Clock.Ready && !ctx.Clock.IsNight)
            return false;

        if (lighting.TorchCount - KeepInReserve <= 0)
        {
            if (!complainedNoTorches)
            {
                complainedNoTorches = true;
                OnOutOfTorches?.Invoke();
            }
            return false;
        }
        complainedNoTorches = false;

        var center = Center ?? Here();
        if (center == null)
            return false;

        // Прогоревший факел занимает клетку и не светит — сначала снимаем его
        if (ReplaceBurnedOut)
        {
            var dead = lighting.BurnedOutTorchesNear(center.Value, Radius, height: 3, limit: 4)
                .Cast<BlockPos?>().FirstOrDefault();
            if (dead != null)
            {
                var broken = await ctx.Mining.BreakAsync(dead.Value, ct);
                if (!broken.Success)
                    nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
                return true; // ход потрачен в любом случае: бот ходил и махал
            }
        }

        int lit = lighting.TorchesNear(center.Value, Radius, litOnly: true, height: 3,
            limit: MaxTorches + 1).Count();
        if (lit >= MaxTorches)
        {
            Report();
            return false;
        }

        var spot = lighting.FindTorchSpot(center.Value, Radius, Spacing, height: 2,
            onlyDarkWhenKnown: true, darkThreshold: DarkThreshold);
        if (spot == null)
        {
            Report();
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            return false;
        }

        reportedLit = false;
        if (await lighting.PlaceTorchAsync(spot.Value, ct))
        {
            OnTorchPlaced?.Invoke(spot.Value);
            return true;
        }
        nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        return false;
    }

    private void Report()
    {
        if (reportedLit)
            return;
        reportedLit = true;
        OnZoneLit?.Invoke();
    }

    private BlockPos? Here() =>
        ctx.Self.Position is { } p
            ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
            : null;
}
