using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

using RecipeIndex = System.Collections.Generic.OrderedDictionary<Vintagestory.API.Common.IRecipeIngredientBase, System.Collections.Generic.List<Vintagestory.API.Common.IRecipeBase>>;

using WorldModel = VsBotKit.WorldModel;

// Мост к игровой физике — отдельно от VsBotKit (см. GamePhysicsWorld.cs)
namespace BotGamePhysics;

/// <summary>
/// Минимальный «мир» для игрового кода физики. Из всего огромного
/// IWorldAccessor физике нужны буквально несколько вещей: доступ к блокам,
/// время, случайные числа и реестр классов. Остальное не вызывается —
/// и намеренно не реализовано, чтобы не делать вид, будто мы полноценный мир.
/// </summary>
// Игровой IWorldAccessor размечен «сюда можно null» почти во всех параметрах.
// Наш мир — заглушка: физика вызывает из него единицы методов, остальные стоят
// пустышками и никогда не выполняются. Повторять разметку в шести десятках
// подписей смысла нет — она ничего не защищает, но CS8767 сыпался на каждой
// сборке и прятал настоящие предупреждения.
#pragma warning disable CS8767 // разметка null в заглушке мира
internal sealed class BotPhysicsWorld : IWorldAccessor
{
    private readonly BotBlockAccessor blocks;
    private readonly ITreeAttribute config = new TreeAttribute();
    private readonly Random rand = new();
    private readonly ClassRegistry registry = new();
    private readonly ClassRegistryAPI registryApi;

    public BotPhysicsWorld(WorldModel world)
    {
        blocks = new BotBlockAccessor(world);
        registryApi = new ClassRegistryAPI(this, registry);
    }

    public ITreeAttribute Config => config;
    public IBlockAccessor BlockAccessor => blocks;
    public long ElapsedMilliseconds => Environment.TickCount64;
    public EnumAppSide Side => EnumAppSide.Client;
    public Random Rand => rand;
    public IClassRegistryAPI ClassRegistry => registryApi;
    public IPlayer PlayerByUid(string uid) => null!;
    public Block GetBlock(int id) => blocks.GetBlock(id);
    public Block GetBlock(AssetLocation code) => blocks.GetBlock(code);
    public Item GetItem(int id) => null!;
    public Item GetItem(AssetLocation code) => null!;
    public int SeaLevel => 110;

    // --- Дальше физика не ходит ---
    public EntityPos DefaultSpawnPosition => throw new NotSupportedException();
    public FrameProfilerUtil FrameProfiler => throw new NotSupportedException();
    public ICoreAPI Api => null!;
    public Vintagestory.API.Server.IChunkProvider ChunkProvider => throw new NotSupportedException();
    public ILandClaimAPI Claims => throw new NotSupportedException();
    public long[] LoadedChunkIndices => [];
    public long[] LoadedMapChunkIndices => [];
    public float[] BlockLightLevels => [];
    public float[] SunLightLevels => [];
    public int Seed => 0;
    public string SavegameIdentifier => "bot";
    public int SunBrightness => 22;
    public bool EntityDebugMode => false;
    public IAssetManager AssetManager => throw new NotSupportedException();
    public ILogger Logger => throw new NotSupportedException();
    public IBulkBlockAccessor BulkBlockAccessor => throw new NotSupportedException();
    public IGameCalendar Calendar => throw new NotSupportedException();
    public CollisionTester CollisionTester => throw new NotSupportedException();
    public List<CollectibleObject> Collectibles => throw new NotSupportedException();
    public IList<Block> Blocks => throw new NotSupportedException();
    public IList<Item> Items => throw new NotSupportedException();
    public List<EntityProperties> EntityTypes => throw new NotSupportedException();
    public List<string> EntityTypeCodes => throw new NotSupportedException();
    public List<GridRecipe> GridRecipes => throw new NotSupportedException();
    public int DefaultEntityTrackingRange => 128;
    public IPlayer[] AllOnlinePlayers => [];
    public IPlayer[] AllPlayers => [];
    public AABBIntersectionTest InteresectionTester => throw new NotSupportedException();
    public RecipeIndex FastSearchRecipesByIngredient => throw new NotSupportedException();

    public RecipeRegistryBase GetRecipeRegistry(string code) => null!;
    public Block[] SearchBlocks(AssetLocation wildcard) => [];
    public Item[] SearchItems(AssetLocation wildcard) => [];
    public EntityProperties GetEntityType(AssetLocation entityCode) => null!;
    public Entity SpawnItemEntity(ItemStack itemstack, Vec3d position, Vec3d velocity) => null!;
    public Entity SpawnItemEntity(ItemStack itemstack, BlockPos pos, Vec3d velocity) => null!;
    public void SpawnEntity(Entity entity) { }
    public void SpawnPriorityEntity(Entity entity) { }
    public bool LoadEntity(Entity entity, long fromChunkIndex3d) => false;
    public void UpdateEntityChunk(Entity entity, long newChunkIndex3d) { }
    public Entity[] GetEntitiesAround(Vec3d position, float horRange, float vertRange, ActionConsumable<Entity> matches) => [];
    public Entity[] GetEntitiesInsideCuboid(BlockPos startPos, BlockPos endPos, ActionConsumable<Entity> matches) => [];
    public IPlayer[] GetPlayersAround(Vec3d position, float horRange, float vertRange, ActionConsumable<IPlayer> matches) => [];
    public Entity GetNearestEntity(Vec3d position, float horRange, float vertRange, ActionConsumable<Entity> matches) => null!;
    public Entity GetEntityById(long entityId) => null!;
    public Entity[] GetIntersectingEntities(BlockPos basePos, Cuboidf[] collisionBoxes, ActionConsumable<Entity> matches) => [];
    public IPlayer NearestPlayer(double x, double y, double z) => null!;

    public int PlaySoundAt(SoundAttributes sound, double x, double y, double z, int dimension, IPlayer dualCallByPlayer, float volumeMultiplier) => 0;
    public void PlaySoundAt(AssetLocation location, double posx, double posy, double posz, IPlayer dualCallByPlayer, bool randomizePitch, float range, float volume) { }
    public int PlaySoundAt(SoundAttributes sound, BlockPos pos, double yOffsetFromCenter, IPlayer dualCallByPlayer, float volumeMultiplier) => 0;
    public void PlaySoundAt(AssetLocation location, BlockPos pos, double yOffsetFromCenter, IPlayer dualCallByPlayer, bool randomizePitch, float range, float volume) { }
    public int PlaySoundAt(SoundAttributes sound, Entity atEntity, IPlayer dualCallByPlayer, float volumeMultiplier) => 0;
    public void PlaySoundAt(AssetLocation location, Entity atEntity, IPlayer dualCallByPlayer, bool randomizePitch, float range, float volume) { }
    public void PlaySoundAt(AssetLocation location, Entity atEntity, IPlayer dualCallByPlayer, float pitch, float range, float volume) { }
    public void PlaySoundAt(AssetLocation location, double posx, double posy, double posz, IPlayer dualCallByPlayer, float pitch, float range, float volume) { }
    public void PlaySoundAt(AssetLocation location, double posx, double posy, double posz, IPlayer dualCallByPlayer, EnumSoundType soundType, float pitch, float range, float volume) { }
    public int PlaySoundAt(SoundAttributes sound, IPlayer atPlayer, IPlayer dualCallByPlayer, float volumeMultiplier) => 0;
    public void PlaySoundAt(AssetLocation location, IPlayer atPlayer, IPlayer dualCallByPlayer, bool randomizePitch, float range, float volume) { }
    public int PlaySoundFor(SoundAttributes sound, IPlayer forPlayer, float volumeMultiplier) => 0;
    public void PlaySoundFor(AssetLocation location, IPlayer forPlayer, bool randomizePitch, float range, float volume) { }
    public void PlaySoundFor(AssetLocation location, IPlayer forPlayer, float pitch, float range, float volume) { }

    public void SpawnParticles(float quantity, int color, Vec3d minPos, Vec3d maxPos, Vec3f minVelocity, Vec3f maxVelocity, float lifeLength, float gravityEffect, float scale, EnumParticleModel model, IPlayer dualCallByPlayer) { }
    public void SpawnParticles(IParticlePropertiesProvider particlePropertiesProvider, IPlayer dualCallByPlayer) { }
    public void SpawnCubeParticles(BlockPos blockPos, Vec3d pos, float radius, int quantity, float scale, IPlayer dualCallByPlayer, Vec3f velocity) { }
    public void SpawnCubeParticles(Vec3d pos, ItemStack item, float radius, int quantity, float scale, IPlayer dualCallByPlayer, Vec3f velocity) { }

    public void RayTraceForSelection(Vec3d fromPos, Vec3d toPos, ref BlockSelection blockSelection, ref EntitySelection entitySelection, BlockFilter bfilter, EntityFilter efilter) { }
    public void RayTraceForSelection(IWorldIntersectionSupplier supplier, Vec3d fromPos, Vec3d toPos, ref BlockSelection blockSelection, ref EntitySelection entitySelection, BlockFilter bfilter, EntityFilter efilter) { }
    public void RayTraceForSelection(Vec3d fromPos, float pitch, float yaw, float range, ref BlockSelection blockSelection, ref EntitySelection entitySelection, BlockFilter bfilter, EntityFilter efilter) { }
    public void RayTraceForSelection(Ray ray, ref BlockSelection blockSelection, ref EntitySelection entitySelection, BlockFilter filter, EntityFilter efilter) { }

    public long RegisterGameTickListener(Action<float> onGameTick, int millisecondInterval, int initialDelayOffsetMs) => 0;
    public void UnregisterGameTickListener(long listenerId) { }
    public long RegisterCallback(Action<float> OnTimePassed, int millisecondDelay) => 0;
    public long RegisterCallbackUnique(Action<IWorldAccessor, BlockPos, float> OnGameTick, BlockPos pos, int millisecondInterval) => 0;
    public long RegisterCallback(Action<IWorldAccessor, BlockPos, float> OnTimePassed, BlockPos pos, int millisecondDelay) => 0;
    public bool PlayerHasPrivilege(int clientid, string privilege) => false;
    public void UnregisterCallback(long listenerId) { }
    public void HighlightBlocks(IPlayer player, int highlightSlotId, List<BlockPos> blocks, List<int> colors, Vintagestory.API.Client.EnumHighlightBlocksMode mode, EnumHighlightShape shape, float scale) { }
    public void HighlightBlocks(IPlayer player, int highlightSlotId, List<BlockPos> blocks, Vintagestory.API.Client.EnumHighlightBlocksMode mode, EnumHighlightShape shape) { }
    public IBlockAccessor GetBlockAccessor(bool synchronize, bool relight, bool strict, bool debug) => blocks;
    public IBulkBlockAccessor GetBlockAccessorBulkUpdate(bool synchronize, bool relight, bool debug) => throw new NotSupportedException();
    public IBulkBlockAccessor GetBlockAccessorBulkMinimalUpdate(bool synchronize, bool debug) => throw new NotSupportedException();
    public IBulkBlockAccessor GetBlockAccessorMapChunkLoading(bool synchronize, bool debug) => throw new NotSupportedException();
    public IBlockAccessorRevertable GetBlockAccessorRevertable(bool synchronize, bool relight, bool debug) => throw new NotSupportedException();
    public IBlockAccessorPrefetch GetBlockAccessorPrefetch(bool synchronize, bool relight) => throw new NotSupportedException();
    public ICachingBlockAccessor GetCachingBlockAccessor(bool synchronize, bool relight) => throw new NotSupportedException();
    public IBlockAccessor GetLockFreeBlockAccessor() => blocks;
}
#pragma warning restore CS8767

/// <summary>
/// Сам игрок для игровой физики. Наследник EntityPlayer, который НЕ вызывает
/// Initialize (тот тянет за собой рендер, анимации и ICoreAPI) — вместо этого
/// выставляет напрямую то немногое, что физике нужно.
/// </summary>
internal sealed class BotEntity : EntityPlayer
{
    public BotEntity(IWorldAccessor world, EntityProperties props)
    {
        World = world;
        Properties = props;
        servercontrols = controls;
        CollisionBox = props.SpawnCollisionBox.Clone();
        OriginCollisionBox = CollisionBox.Clone();
        SelectionBox = CollisionBox.Clone();
        OriginSelectionBox = SelectionBox.Clone();
        State = EnumEntityState.Active;
        Alive = true;
        PrevFrameCanStandUp = true;
        // Как в Entity.Initialize игры: «LocalEyePos.Y = Properties.EyeHeight».
        // Иначе глаза начинают жизнь в НОГАХ и доезжают до роста почти секунду
        // (присед в игре двигает их плавно, см. BodySize.CrouchFitPerSecond) —
        // а из глаз выходит луч руки, и первую секунду бот мерил бы дальность
        // от пола
        LocalEyePos.Y = props.EyeHeight;
    }

    // Анимаций и рендера у headless-бота нет
    public override bool AdjustCollisionBoxToAnimation => false;
    public override double LadderFixDelta => 0.0;
    public override IAnimationManager AnimManager { get => null!; set { } }
}
