using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace VsBotKit;

/// <summary>
/// ТЁСАНЫЙ БЛОК (микроблок): его форма лежит НЕ в типе блока, а в блок-сущности.
///
/// Живой случай заказчика: «на чизл блоке летит в воздухе, он не чувствует его
/// границы, я думал у нас физ модель». Так и было — физ-модель есть, врали ей
/// данные. Тип «chiseledblock» в ассетах игры описан голым кубом (в
/// survival/blocktypes/chiseled/chiseledblock.json нет ни collisionbox, ни
/// selectionbox — значит берётся <c>Cuboidf.Default()</c>, весь блок целиком),
/// и ровно этот куб сервер шлёт в реестре. Поэтому ступенька в полблока
/// приезжала к боту как ЦЕЛЫЙ КУБ: тело вставало на 1.0 вместо 0.5 и зависало
/// в воздухе на полблока — а позиция у бота клиент-авторитетна, так что
/// заказчик это и видел.
///
/// КАК СПРАШИВАЕТ САМА ИГРА (ilspycmd по Mods/VSSurvivalMod.dll, 1.22.6):
///   BlockMicroBlock.GetCollisionBoxes(ba, pos):
///       блок-сущность есть  → BlockEntityMicroBlock.GetCollisionBoxes(...)
///       блок-сущности нет   → боксы САМОГО ТИПА (полный куб)
///   BlockEntityMicroBlock.GetCollisionBoxes → selectionBoxesMetaMode ?? selectionBoxesStd,
///   а оба собирает RegenSelectionBoxes из списка VoxelCuboids — по коробке на
///   каждый упакованный uint, <c>CuboidWithMaterial.ToCuboidf()</c> (÷16).
///   В мета-ветке selectionBoxesStd отсеивает невыбираемые мета-боксы, но
///   КОЛЛИЗИЯ берёт selectionBoxesMetaMode, где лежат ВСЕ коробки, — значит
///   для столкновений правило одно: все cuboids, без отбора.
///
/// BlockChisel (тёсаный игроком) — наследник BlockMicroBlock, а его
/// BlockEntityChisel — наследник BlockEntityMicroBlock: правило общее.
/// Классы блок-сущностей игра регистрирует в SurvivalCoreSystem как
/// «Chisel» и «MicroBlock» — под этими же именами они приезжают к боту
/// в поле EntityClass реестра и в Classname блок-сущности.
///
/// Здесь только ЧИСТЫЕ ПРАВИЛА разбора — их можно проверить тестом без
/// сервера. Кто и когда их спрашивает — дело <see cref="WorldModel"/>.
/// </summary>
public static class MicroBlocks
{
    /// <summary>Вокселей на блок по каждой оси (сетка тесания в игре — 16×16×16).</summary>
    public const int VoxelsPerBlock = 16;

    /// <summary>
    /// Имена классов блок-сущностей, в которых игра держит форму микроблока.
    /// Взяты из регистрации самой игры (SurvivalCoreSystem.RegisterBlockEntityClass:
    /// «Chisel» → BlockEntityChisel, «MicroBlock» → BlockEntityMicroBlock),
    /// а не выдуманы по коду блока: у модовых тёсаных блоков код любой.
    /// </summary>
    public static bool IsMicroBlockEntity(string? entityClass) =>
        entityClass is "Chisel" or "MicroBlock";

    /// <summary>
    /// Полный куб одной коробкой — ровно то, чем игра подменяет отсутствующий
    /// список (BlockEntityMicroBlock.GetVoxelCuboids: <c>ToUint(0,0,0,16,16,16,0)</c>).
    /// </summary>
    public static uint[] FullCube => [Pack(0, 0, 0, 16, 16, 16)];

    /// <summary>Упаковать коробку так, как это делает игра (ToUint).</summary>
    public static uint Pack(int x1, int y1, int z1, int x2, int y2, int z2) =>
        (uint)(x1 | (y1 << 4) | (z1 << 8) | (x2 - 1 << 12) | (y2 - 1 << 16) | (z2 - 1 << 20));

    /// <summary>
    /// Список коробок микроблока из атрибутов его блок-сущности.
    ///
    /// Повторяет BlockEntityMicroBlock.GetVoxelCuboids ОДИН В ОДИН, включая
    /// разницу между «поля нет» и «поле пустое»:
    ///   поля «cuboids» нет вовсе → полный куб (так игра читает старые записи);
    ///   поле есть, но пустое     → коробок НЕТ, то есть блок ничему не мешает.
    /// Эта разница не придирка: пустой список — законное состояние (всё
    /// стёсано), и считать его полным кубом значит поставить боту стену
    /// посреди воздуха.
    /// </summary>
    public static uint[] Cuboids(ITreeAttribute? tree)
    {
        if (tree?["cuboids"] is IntArrayAttribute ints)
            return ints.AsUint;
        // Вторая дорога — та же, что у игры: длинный массив от старых миров
        if (tree?["cuboids"] is LongArrayAttribute longs)
            return longs.AsUint;
        return FullCube;
    }

    /// <summary>Развернуть упакованную коробку в доли блока (FromUint + ToCuboidf).</summary>
    public static Cuboidf Box(uint packed)
    {
        const float v = VoxelsPerBlock;
        return new Cuboidf(
            (packed & 0xF) / v,
            ((packed >> 4) & 0xF) / v,
            ((packed >> 8) & 0xF) / v,
            (((packed >> 12) & 0xF) + 1) / v,
            (((packed >> 16) & 0xF) + 1) / v,
            (((packed >> 20) & 0xF) + 1) / v);
    }

    /// <summary>Все коробки столкновений микроблока (пустой список — блок пустой).</summary>
    public static Cuboidf[] Boxes(uint[] cuboids)
    {
        var boxes = new Cuboidf[cuboids.Length];
        for (int i = 0; i < cuboids.Length; i++)
            boxes[i] = Box(cuboids[i]);
        return boxes;
    }

    /// <summary>
    /// Верх опоры микроблока: самая высокая точка его коробок. Мерка ровно та
    /// же, что модель мира применяет к обычным блокам (максимум по Maxy всех
    /// боксов реестра), — иначе у плиты и у тёсаной плиты «верх» означал бы
    /// разное. 0 — стоять не на чем.
    /// </summary>
    public static float Top(uint[] cuboids)
    {
        float top = 0;
        foreach (uint c in cuboids)
            top = Math.Max(top, (((c >> 16) & 0xF) + 1) / (float)VoxelsPerBlock);
        return top;
    }

    /// <summary>
    /// НИЗ микроблока: самая низкая точка его коробок. Мерка та же, что модель
    /// мира применяет к обычным блокам (минимум по Miny всех боксов реестра).
    /// Единица — коробок нет вовсе, то есть НИЧЕГО НЕ СВИСАЕТ.
    ///
    /// Спрашивают это ради просвета над головой: тёсаный полублок под потолком
    /// оставляет ровно те 1,5 бл, в которые тело проходит только пригнувшись
    /// (см. <c>WorldModel.ClearanceAt</c> и <c>Walker.PoseForClearance</c>).
    /// Верх на этот вопрос не отвечает: у потолочного полублока он равен 1.
    /// </summary>
    public static float Bottom(uint[] cuboids)
    {
        float bottom = 1;
        foreach (uint c in cuboids)
            bottom = Math.Min(bottom, ((c >> 4) & 0xF) / (float)VoxelsPerBlock);
        return bottom;
    }
}
