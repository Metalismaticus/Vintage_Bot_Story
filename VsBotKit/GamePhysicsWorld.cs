using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

// Тянем ровно один свой тип: всё остальное здесь — игровое
using WorldModel = VsBotKit.WorldModel;

// Мост к игровой физике живёт ОТДЕЛЬНО от VsBotKit: у библиотеки есть свой
// BlockPos, и вложенное пространство имён всё равно видело бы его первым.
// Здесь все имена должны означать ИГРОВЫЕ типы
namespace BotGamePhysics;

/// <summary>
/// Доступ к блокам в том виде, в каком его ждёт код игры. Внутри — наша
/// модель мира (чанки от сервера) и НАСТОЯЩИЕ объекты Block, собранные
/// из реестра тем же кодом, что и у клиента игры.
///
/// Нужен, чтобы физику считал сам игровой код: ему всё равно, откуда блоки,
/// лишь бы отвечал IBlockAccessor.
/// </summary>
internal sealed class BotBlockAccessor : IBlockAccessor
{
    private readonly WorldModel world;

    public BotBlockAccessor(WorldModel world) => this.world = world;

    /// <summary>
    /// Блок для ФИЗИКИ — всегда настоящий объект игры со своими боксами.
    /// Никаких подмен: у обычных дверей и калиток каждое состояние это
    /// отдельный блок, и его бокс уже верный (закрытая — створка поперёк
    /// проёма, открытая — прижата к косяку). Подменять их воздухом значило бы
    /// выкинуть реальное тело створки и снова играть в свою физику.
    /// Исключения ровно два, и оба — блоки, чью форму игра держит НЕ в типе,
    /// а в блок-сущности:
    ///   двери (створка повёрнута, см. DoorPhysicsBlock);
    ///   тёсаные блоки-микроблоки (форма — набор вокселей, см.
    ///   MicroBlockPhysicsBlock). Тип у тёсаного отдаёт полный куб, и без
    ///   подмены тело вставало на полблока выше настоящей ступеньки —
    ///   «на чизл блоке летит в воздухе».
    ///
    /// САМ ПОРЯДОК ПОДМЕН ЖИВЁТ В <see cref="WorldModel.PhysicsBlock"/>, а не
    /// здесь, и это переезд, а не копия: пока он стоял строкой в физике, о нём
    /// не знала диагностика — команда «!блок» печатала у распахнутой двери
    /// коробку закрытой створки и стоила человеку получаса живого прогона.
    /// </summary>
    private Block At(int x, int y, int z) => world.PhysicsBlock(x, y, z);

    private Block AtLayer(int x, int y, int z, int layer) =>
        layer == 2 ? world.GetGameLiquid(x, y, z) : At(x, y, z);

    public Block GetBlock(BlockPos pos) => At(pos.X, pos.Y, pos.Z);
    public Block GetBlock(BlockPos pos, int layer) => AtLayer(pos.X, pos.Y, pos.Z, layer);
    public Block GetBlock(int x, int y, int z) => At(x, y, z);
    public Block GetBlock(int x, int y, int z, int layer) => AtLayer(x, y, z, layer);
    public Block GetBlockRaw(int x, int y, int z, int layer = 0) => AtLayer(x, y, z, layer);
    public Block GetBlockOrNull(int x, int y, int z, int layer = 1) => AtLayer(x, y, z, layer);
    public Block GetBlock(int blockId) => world.GetGameBlockById(blockId);
    public Block GetBlock(AssetLocation code) => world.GetGameBlockByCode(code?.ToShortString());
    public Block GetMostSolidBlock(BlockPos pos) => At(pos.X, pos.Y, pos.Z);
    public Block GetMostSolidBlock(int x, int y, int z) => At(x, y, z);
    public Block GetBlockAbove(BlockPos pos, int n = 1, int layer = 0) => AtLayer(pos.X, pos.Y + n, pos.Z, layer);
    public Block GetBlockBelow(BlockPos pos, int n = 1, int layer = 0) => AtLayer(pos.X, pos.Y - n, pos.Z, layer);

    // «Непроходимо» в игре означает НЕПРОГРУЖЕННЫЙ ЧАНК, а не твёрдый блок —
    // твёрдость проверяет CollisionTester. Наш мир для физики всегда считается
    // прогруженным: за краем видимого бот просто не строит маршрутов
    public bool IsNotTraversable(double x, double y, double z) => false;
    public bool IsNotTraversable(double x, double y, double z, int dim) => false;
    public bool IsNotTraversable(BlockPos pos) => false;

    public bool IsSideSolid(int x, int y, int z, BlockFacing f) =>
        At(x, y, z).SideIsSolid(new BlockPos(x, y, z, 0), f.Index);

    public int GetRainMapHeightAt(BlockPos pos) => GetRainMapHeightAt(pos.X, pos.Z);
    public int GetRainMapHeightAt(int x, int z) => world.FindStandableY(x, 150, z, up: 100, down: 200) ?? 0;
    public Vec3d GetWindSpeedAt(Vec3d pos) => new();
    public Vec3d GetWindSpeedAt(BlockPos pos) => new();

    public void WalkBlocks(BlockPos minPos, BlockPos maxPos, Action<Block, int, int, int> onBlock, bool centerOrder = false)
    {
        for (int y = Math.Min(minPos.Y, maxPos.Y); y <= Math.Max(minPos.Y, maxPos.Y); y++)
        for (int x = Math.Min(minPos.X, maxPos.X); x <= Math.Max(minPos.X, maxPos.X); x++)
        for (int z = Math.Min(minPos.Z, maxPos.Z); z <= Math.Max(minPos.Z, maxPos.Z); z++)
            onBlock(At(x, y, z), x, y, z);
    }

    public bool IsValidPos(int x, int y, int z) => y is >= 0 and < 1024;
    public bool IsValidPos(BlockPos pos) => IsValidPos(pos.X, pos.Y, pos.Z);
    public int GetBlockId(BlockPos pos) => world.GetBlockId(pos.X, pos.Y, pos.Z);
    public int GetBlockId(int x, int y, int z) => world.GetBlockId(x, y, z);

    // --- Остальное физике не нужно: бот мир не меняет и не рисует ---
    public int ChunkSize => 32;
    public int RegionSize => 512;
    public int MapSizeX => 1024000;
    public int MapSizeY => 1024;
    public int MapSizeZ => 1024000;
    public int RegionMapSizeX => throw new NotSupportedException();
    public int RegionMapSizeY => throw new NotSupportedException();
    public int RegionMapSizeZ => throw new NotSupportedException();
    public bool UpdateSnowAccumMap { get => false; set { } }
    public Vec3i MapSize => new(MapSizeX, MapSizeY, MapSizeZ);

    public IWorldChunk GetChunk(int chunkX, int chunkY, int chunkZ) => null!;
    public IWorldChunk GetChunk(long chunkIndex3D) => null!;
    public IMapRegion GetMapRegion(int regionX, int regionZ) => null!;
    public IWorldChunk GetChunkAtBlockPos(BlockPos pos) => null!;
    public IWorldChunk GetChunkAtBlockPos(int posX, int posY, int posZ) => null!;
    public void SearchBlocks(BlockPos minPos, BlockPos maxPos, ActionConsumable<Block, BlockPos> onBlock, Action<int, int, int> onChunkMissing) { }
    public void SearchFluidBlocks(BlockPos minPos, BlockPos maxPos, ActionConsumable<Block, BlockPos> onBlock, Action<int, int, int> onChunkMissing) { }
    public void WalkStructures(BlockPos pos, Action<GeneratedStructure> onStructure) { }
    public void WalkStructures(BlockPos minpos, BlockPos maxpos, Action<GeneratedStructure> onStructure) { }
    public void SetBlock(int blockId, BlockPos pos) { }
    public void SetBlock(int blockId, BlockPos pos, int layer) { }
    public void SetBlock(int blockId, BlockPos pos, ItemStack byItemstack) { }
    public void ExchangeBlock(int blockId, BlockPos pos) { }
    public void BreakBlock(BlockPos pos, IPlayer byPlayer, float dropQuantityMultiplier) { }
    public void DamageBlock(BlockPos pos, BlockFacing facing, float damage) { }
    public void SpawnBlockEntity(string classname, BlockPos position, ItemStack byItemStack) { }
    public void SpawnBlockEntity(BlockEntity be) { }
    public void RemoveBlockEntity(BlockPos position) { }
    public BlockEntity GetBlockEntity(BlockPos position) => null!;
    public T GetBlockEntity<T>(BlockPos position) where T : BlockEntity => null!;
    public List<BlockUpdate> Commit() => new();
    public void Rollback() { }
    public void MarkBlockEntityDirty(BlockPos pos) { }
    public void TriggerNeighbourBlockUpdate(BlockPos pos) { }
    public void MarkBlockDirty(BlockPos pos, IPlayer skipPlayer) { }
    public void MarkBlockModified(BlockPos pos) { }
    public void MarkBlockDirty(BlockPos pos, Action OnRetesselated) { }
    public int GetLightLevel(BlockPos pos, EnumLightLevelType type) => 15;
    public int GetLightLevel(int x, int y, int z, EnumLightLevelType type) => 15;
    public Vec4f GetLightRGBs(int posX, int posY, int posZ) => new();
    public Vec4f GetLightRGBs(BlockPos pos) => new();
    public int GetLightRGBsAsInt(int posX, int posY, int posZ) => 0;
    public int GetTerrainMapheightAt(BlockPos pos) => GetRainMapHeightAt(pos);
    public int GetDistanceToRainFall(BlockPos pos, int horziontalSearchWidth, int verticalSearchWidth) => 0;
    public IMapChunk GetMapChunk(Vec2i chunkPos) => null!;
    public IMapChunk GetMapChunk(int chunkX, int chunkZ) => null!;
    public IMapChunk GetMapChunkAtBlockPos(BlockPos pos) => null!;
    public ClimateCondition GetClimateAt(BlockPos pos, EnumGetClimateMode mode, double totalDays) => null!;
    public ClimateCondition GetClimateAt(BlockPos pos, ClimateCondition baseClimate, EnumGetClimateMode mode, double totalDays) => null!;
    public ClimateCondition GetClimateAt(BlockPos pos, int climate) => null!;
    public void MarkAbsorptionChanged(int oldAbsorption, int newAbsorption, BlockPos pos) { }
    public void RemoveBlockLight(byte[] oldLightHsV, BlockPos pos) { }
    public bool SetDecor(Block block, BlockPos position, BlockFacing onFace) => false;
    public bool SetDecor(Block block, BlockPos position, int decorIndex) => false;
    public Block[] GetDecors(BlockPos position) => [];
    public Dictionary<int, Block> GetSubDecors(BlockPos position) => new();
    public Block GetDecor(BlockPos pos, int decorIndex) => null!;
    public bool BreakDecor(BlockPos pos, BlockFacing side, int? decorIndex) => false;
    public void MarkChunkDecorsModified(BlockPos pos) { }
    public IMiniDimension CreateMiniDimension(Vec3d position) => null!;
    public void RedrawNeighbouringChunk(BlockPos pos, BlockFacing side) { }
}
