using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VsBotKit;

/// <summary>
/// Настоящие объекты предметов (Item) из реестра сервера — так же, как их
/// собирает клиент игры. Благодаря этому скорость добычи, тир инструмента,
/// прочность и размер стака берутся ИЗ ИГРЫ, а не из выдуманных таблиц
/// (правило 1 проекта).
/// </summary>
public partial class WorldModel
{
    private Vintagestory.API.Common.Item[] gameItems = [];

    /// <summary>Настоящие объекты предметов собраны.</summary>
    public bool GameItemsReady => gameItems.Length > 0;

    /// <summary>Игровой предмет по id реестра (null, если такого нет).</summary>
    public Vintagestory.API.Common.Item? GetGameItem(int id) =>
        id > 0 && id < gameItems.Length ? gameItems[id] : null;

    /// <summary>Игровой предмет по коду ("bandage-clean", "pickaxe-copper").</summary>
    public Vintagestory.API.Common.Item? GetGameItemByCode(string? code) =>
        code != null && ItemCodeToId(code) is { } id ? GetGameItem(id) : null;

    /// <summary>
    /// Игровой «предмет или блок» по коду: в инвентаре лежат и те, и другие,
    /// а свойства (инструмент, прочность, стак) живут в общем базовом классе.
    /// </summary>
    public CollectibleObject? GetCollectible(string? code)
    {
        if (code == null)
            return null;
        if (GetGameItemByCode(code) is { } item)
            return item;
        var block = GetGameBlockByCode(code);
        return block is { BlockId: > 0 } ? block : null;
    }

    /// <summary>
    /// ПОЛНАЯ СТОПКА ПО РЕЕСТРУ ИЛИ 0, ЕСЛИ ИГРА ЭТОГО ПРЕДМЕТА НЕ ЗНАЕТ.
    ///
    /// Отличать «стопка в одну штуку» от «не знаю» обязательно, и стоило это
    /// дорого. <see cref="MaxStackSize"/> в обоих случаях отвечал «1», а правило
    /// выброса (<see cref="Hands.DropRank"/>) как раз и охраняло себя условием
    /// «полную стопку не знаем — не гадаем». Условие было недостижимо: боевой
    /// путь нуля не давал НИКОГДА.
    ///
    /// Кого это губило. BuildGameItems ловит исключение разбора и оставляет
    /// в массиве null, а код предмета остаётся настоящим (itemCodeToId
    /// заполняется отдельно) — GetCollectible вернёт null, стопка «станет» 1,
    /// и любая такая вещь уходила на землю со второй штуки.
    /// </summary>
    public int MaxStackSizeOrUnknown(string? code) => GetCollectible(code)?.MaxStackSize ?? 0;

    /// <summary>
    /// Максимальный размер стака по коду (нужно для переносов партиями).
    /// Незнакомая вещь считается нескладываемой — по одной штуке в слот:
    /// перенос от этого лишь осторожнее, а вот выброс так мерить нельзя
    /// (см. <see cref="MaxStackSizeOrUnknown"/>).
    /// </summary>
    public int MaxStackSize(string? code) => Math.Max(1, MaxStackSizeOrUnknown(code));

    /// <summary>Тип инструмента ("Pickaxe", "Axe"...), если предмет — инструмент.</summary>
    public EnumTool? ToolOf(string? code) => GetCollectible(code)?.Tool;

    /// <summary>Тир инструмента: чем выше, тем более твёрдые блоки берёт.</summary>
    public int ToolTier(string? code) => GetCollectible(code)?.ToolTier ?? 0;

    /// <summary>Запас прочности предмета (0 — не изнашивается).</summary>
    public int Durability(string? code) => GetCollectible(code)?.Durability ?? 0;

    /// <summary>
    /// Скорость добычи материала этим предметом — из таблицы самого предмета.
    /// 1.0 — «голыми руками» (подходящей записи нет).
    /// </summary>
    public float MiningSpeed(string? itemCode, EnumBlockMaterial material)
    {
        var speeds = GetCollectible(itemCode)?.MiningSpeed;
        if (speeds != null && speeds.TryGetValue(material, out float s))
            return s * GlobalConstants.ToolMiningSpeedModifier;
        return 1f;
    }

    /// <summary>
    /// Сколько РЕАЛЬНОГО времени займёт добыча блока указанным предметом.
    /// Повторяет расчёт игры: сопротивление блока делится на скорость добычи
    /// (CollectibleObject.OnBlockBreaking: remaining -= GetMiningSpeed * dt).
    /// Свою формулу не изобретаем — берём поля игровых объектов.
    /// </summary>
    public double MiningSeconds(int x, int y, int z, string? toolCode)
    {
        var block = GetGameBlock(x, y, z);
        if (block == null || block.BlockId == 0)
            return 0;
        float speed = MiningSpeed(toolCode, block.BlockMaterial);
        return speed <= 0 ? double.PositiveInfinity : block.Resistance / speed;
    }

    /// <summary>Тир инструмента, без которого блок не сломать (0 — можно руками).</summary>
    public int RequiredMiningTier(int x, int y, int z) =>
        GetGameBlock(x, y, z)?.RequiredMiningTier ?? 0;

    /// <summary>Материал блока (камень, дерево, земля...) — по нему считается скорость.</summary>
    public EnumBlockMaterial BlockMaterial(int x, int y, int z) =>
        GetGameBlock(x, y, z)?.BlockMaterial ?? EnumBlockMaterial.Air;

    /// <summary>
    /// ПОРОГ, ЗА КОТОРЫМ ИГРА САМА СЧИТАЕТ БЛОК НЕ ПРЕГРАДОЙ, А МУСОРОМ.
    ///
    /// Число не наше и не выдумано: это порог из описания поля
    /// <c>Block.Replaceable</c> в VintagestoryAPI 1.22.7 — «Any block with
    /// replaceable value above 6000 will replaced when the player tries to
    /// place a block». То есть живой игрок такой блок даже не ломает нарочно:
    /// он ставит блок ПРЯМО СКВОЗЬ него, и тот исчезает. Примеры оттуда же:
    /// 0 — коренная порода, 6000 — высокая трава, 9999 — воздух.
    ///
    /// Проверено по ассетам игры: snowlayer 6100, tallgrass 6000, lichen 6000,
    /// waterlily 6001, sand-layered 6001 — и при этом snowblock (целый куб
    /// снега) 990, то есть настоящая стена сюда не попадает.
    /// </summary>
    public const int ReplacedByPlacing = 6000;

    /// <summary>
    /// НАСКОЛЬКО ИГРА САМА СЧИТАЕТ БЛОК ЗАМЕНЯЕМЫМ — поле <c>Block.Replaceable</c>
    /// из реестра сервера (см. <see cref="ReplacedByPlacing"/>).
    ///
    /// Спрашивает отсюда правило «никчёмная мелочь» (<see cref="Mining.JudgeGrowth"/>):
    /// приставок кодов там нет и быть не может, а вот собственный ответ игры
    /// «под это не подкапываются, это затирают» — есть, и он верен и для
    /// модовых блоков, о которых мы никогда не слышали.
    ///
    /// Реестр ещё не пришёл — ноль, то есть «незаменяемо»: правило от этого
    /// станет только осторожнее.
    /// </summary>
    public int ReplaceableOf(int x, int y, int z) =>
        GetGameBlock(x, y, z)?.Replaceable ?? 0;

    /// <summary>
    /// ЭТО ЖИДКОСТЬ — по реестру сервера (<c>Block.IsLiquid()</c>, то есть
    /// у блока задан LiquidCode).
    ///
    /// Спрашивается ровно затем, чтобы правило «мелочь» не приняло за мусор
    /// воду и лаву: у них <c>Replaceable</c> 9500 и 9000, выпадений нет, и по
    /// одному лишь порогу заменяемости бот полез бы «смахивать» лаву с дороги.
    /// </summary>
    public bool IsLiquidBlock(int x, int y, int z) =>
        GetGameBlock(x, y, z) is { BlockId: > 0 } block && block.IsLiquid();

    /// <summary>
    /// ВЫПАДЕТ ЛИ ИЗ БЛОКА ХОТЬ ЧТО-НИБУДЬ, ЕСЛИ УДАРИТЬ ТЕМ, ЧТО СЕЙЧАС В РУКЕ.
    ///
    /// Спрашивать надо ИМЕННО ТАК, а не «есть ли у блока выпадения вообще».
    /// В реестре у высокой травы выпадение есть — drygrass, — но у него стоит
    /// <c>Tool: knife</c>: «If set, then the given tool is required to make this
    /// block drop anything» (BlockDropItemStack, VintagestoryAPI 1.22.7). С
    /// киркой в руке трава не роняет НИЧЕГО, и снесённая с дороги травинка не
    /// стоит боту ни зёрнышка. Спроси мы про выпадения без инструмента —
    /// заснеженная трава (у неё материал Snow и коробка 0,125, как у сугроба)
    /// осталась бы «ценной» и продолжала бы держать бота на месте.
    ///
    /// Реестр ещё не пришёл — отвечаем «выпадет»: правило «мелочь» на такое
    /// скажет «не мелочь», и бот ничего не тронет.
    /// </summary>
    /// <param name="tool">Что сейчас в руке (<see cref="ToolOf"/>); null — голая рука.</param>
    public bool DropsAnythingWith(int x, int y, int z, EnumTool? tool)
    {
        if (GetGameBlock(x, y, z) is not { BlockId: > 0 } block)
            return true;
        if (block.Drops is not { Length: > 0 } drops)
            return false;
        foreach (var drop in drops)
            if (drop != null && (drop.Tool == null || drop.Tool == tool))
                return true;
        return false;
    }

    /// <summary>
    /// Материал блока ПО КОДУ — тот же вопрос, что и <see cref="BlockMaterial"/>,
    /// только про вещь в сумке, а не про клетку мира. Спрашивает его тот, кто
    /// выбирает, что потратить: полотно, руду и лестницу под ноги не кладут
    /// (см. <see cref="Mining.SpareFits"/>).
    ///
    /// Незнакомый серверу код — это Air, и правило расхода на такое отвечает
    /// «нет»: гадать о материале блока, которого в реестре нет, нельзя.
    /// </summary>
    public EnumBlockMaterial MaterialOf(string? code) =>
        code != null && GetGameBlockByCode(code) is { BlockId: > 0 } block
            ? block.BlockMaterial
            : EnumBlockMaterial.Air;

    /// <summary>
    /// Насколько игра ускоряет ход по такому блоку. Это и есть её собственный
    /// ответ на вопрос «дорожное ли это полотно»: у stonepath в реестре
    /// walkspeedmultiplier 1.30, у земли и гравия — единица.
    /// </summary>
    public float WalkSpeedOf(string? code) =>
        code != null && GetGameBlockByCode(code) is { BlockId: > 0 } block
            ? block.WalkSpeedMultiplier
            : 1f;

    /// <summary>
    /// Собрать настоящие Item из реестра сервера. Незнакомые классы предметов
    /// (модовые) подменяются базовым Item — так же, как это сделано для блоков.
    /// </summary>
    private void BuildGameItems(Packet_ItemType[] packets, int count, int maxId)
    {
        try
        {
            var registry = new Vintagestory.Common.ClassRegistry();
            var probe = new Vintagestory.API.Common.Item();
            var seenClasses = new HashSet<string>(StringComparer.Ordinal);
            var seenBehaviors = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                var it = packets[i];
                if (it == null)
                    continue;
                if (it.ItemClass is { } cls && seenClasses.Add(cls))
                {
                    try { registry.CreateItem(cls); }
                    catch { registry.RegisterItemClass(cls, typeof(Vintagestory.API.Common.Item)); }
                }
                if (it.Behaviors == null)
                    continue;
                for (int b = 0; b < it.BehaviorsCount; b++)
                {
                    if (it.Behaviors[b]?.Code is not { } code || !seenBehaviors.Add(code))
                        continue;
                    try { registry.CreateCollectibleBehavior(probe, code); }
                    catch { registry.RegisterCollectibleBehaviorClass(code, typeof(BotGamePhysics.StubCollectibleBehavior)); }
                }
            }

            var world = new BotGamePhysics.BotPhysicsWorld(this);
            var built = new Vintagestory.API.Common.Item[maxId + 1];
            for (int i = 0; i < count; i++)
            {
                var it = packets[i];
                if (it == null || it.ItemId <= 0 || it.ItemId >= built.Length)
                    continue;
                try { built[it.ItemId] = Vintagestory.Common.ItemTypeNet.ReadItemTypePacket(it, world, registry); }
                catch (Exception ex) { OnParseError?.Invoke($"предмет {it.Code}: {ex.GetType().Name} {ex.Message}"); }
            }
            gameItems = built;
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"реестр предметов игры: {ex.GetType().Name} {ex.Message}");
        }
    }
}
