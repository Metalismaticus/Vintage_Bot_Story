namespace VsBotKit;

/// <summary>
/// Поиск блоков вокруг — общий механизм для всего, что «найди и сходи»:
/// ягодный куст, костёр, грядка, сундук, дверь.
/// </summary>
public partial class WorldModel
{
    /// <summary>
    /// Забыть мир: после переподключения чанки придут заново, а старые могут
    /// быть уже неверны (мир жил без нас). Реестры блоков и предметов не
    /// трогаем — сервер тот же и пришлёт их снова.
    /// </summary>
    public void Reset()
    {
        lock (chunksLock)
            chunks.Clear();
        blockEntities.Clear();
    }

    /// <summary>
    /// Искать только то, что ВИДНО ГЛАЗАМИ — у чего есть открытая грань.
    ///
    /// Стоит правдой по умолчанию: сервер шлёт чанк целиком, и без этого
    /// фильтра бот находит руду внутри сплошного камня, чего живой игрок
    /// не может в принципе. Роль-читер снимает это через
    /// <see cref="Cheats.SeeThroughWalls"/>.
    /// </summary>
    public bool OnlyVisibleBlocks { get; set; } = true;

    /// <summary>
    /// Живой ответ на вопрос «включён ли чит видения сквозь стены». Ставится
    /// ботом один раз; спрашивается каждый поиск, потому что роль может
    /// включить чит когда угодно.
    /// </summary>
    public Func<bool>? SeeThroughWalls { get; set; }

    /// <summary>
    /// Блоки в кубе вокруг точки, чей код подходит под условие. Обход идёт
    /// кольцами от центра, поэтому ближайшее находится первым.
    /// </summary>
    /// <param name="throughWalls">
    /// Искать и внутри породы. По умолчанию берётся <see cref="OnlyVisibleBlocks"/>:
    /// честный бот видит только выходы.
    /// </param>
    public IEnumerable<BlockPos> FindBlocks(Func<string, bool> matchCode, BlockPos center,
        int radius = 16, int height = 8, int limit = 64, bool? throughWalls = null) =>
        FindBlocks((_, code) => matchCode(code), center, radius, height, limit, throughWalls);

    /// <summary>
    /// То же самое, но условию видна и КЛЕТКА, а не только код блока.
    ///
    /// Заведено ради того, кто по дороге ЗАПОМИНАЕТ встреченное
    /// (<see cref="Ores.Find"/> → <see cref="ResourceMemory"/>): бот всё время
    /// проходит мимо того, что сейчас не берёт, и без клетки записать «видел»
    /// некуда. Обход колец при этом остаётся ОДИН — второй такой же поиск,
    /// заведённый рядом, однажды разошёлся бы с этим.
    /// </summary>
    public IEnumerable<BlockPos> FindBlocks(Func<BlockPos, string, bool> matchCode, BlockPos center,
        int radius = 16, int height = 8, int limit = 64, bool? throughWalls = null) =>
        FindBlocksNamed(matchCode, center, radius, height, limit, throughWalls)
            .Select(найдено => найдено.Cell);

    /// <summary>
    /// ТО ЖЕ, НО С КОДОМ, КОТОРЫЙ И ПОДОШЁЛ. Обход колец при этом ОДИН — тот
    /// самый, что у <see cref="FindBlocks(Func{BlockPos,string,bool},BlockPos,int,int,int,bool?)"/>.
    ///
    /// ЗАЧЕМ КОД ОТДЕЛЬНО. В клетке два слоя, и подойти может ЛЮБОЙ. Спроси
    /// потом слой блоков — и на «!рядом water» человек прочтёт список из
    /// «air»: ровно это и вышло 27.08 в out/разведка-лук4, где вода нашлась,
    /// а названа была воздухом, стоящим в том же месте.
    /// </summary>
    public IEnumerable<(BlockPos Cell, string Code)> FindBlocksNamed(
        Func<BlockPos, string, bool> matchCode, BlockPos center,
        int radius = 16, int height = 8, int limit = 64, bool? throughWalls = null)
    {
        bool seeAll = throughWalls ?? (!OnlyVisibleBlocks || (SeeThroughWalls?.Invoke() ?? false));
        int found = 0;
        for (int r = 0; r <= radius && found < limit; r++)
        {
            for (int dx = -r; dx <= r && found < limit; dx++)
                for (int dz = -r; dz <= r && found < limit; dz++)
                {
                    // только внешнее кольцо — внутренние уже обошли
                    if (r > 0 && Math.Abs(dx) != r && Math.Abs(dz) != r)
                        continue;
                    for (int dy = -height; dy <= height && found < limit; dy++)
                    {
                        int x = center.X + dx, y = center.Y + dy, z = center.Z + dz;
                        if (Suits(x, y, z, matchCode) is not { } подошёл)
                            continue;
                        // Замурованное в камне бот «видит» только с читом
                        if (!seeAll && !IsExposed(x, y, z))
                            continue;
                        found++;
                        yield return (new BlockPos(x, y, z), подошёл);
                    }
                }
        }
    }

    /// <summary>
    /// ПОДХОДИТ ЛИ КЛЕТКА — ПО ОБОИМ СЛОЯМ, а не по одному.
    ///
    /// ЖИВАЯ СЛЕПОТА, СТОИВШАЯ ДВУХ ВОЛН. Здесь стояло только
    /// <c>GetBlockCode</c> — слой БЛОКОВ. Вода же лежит СВОИМ слоем (см.
    /// <see cref="GetLiquidCode"/>), а в слое блоков на её месте воздух, и
    /// воздух этот отсеивался первой же строкой. Из-за этого «!ищиблок water 96»
    /// отвечал «не вижу» ВЕЗДЕ — и две волны подряд записали в отчёт «воды в
    /// мире нет», хотя в том же прогоне бот тонул: журнал 27.08 в одну минуту
    /// говорит «воздух на исходе (35 %) — всплываю» и «Блоков по маске water в
    /// радиусе 96 не вижу». Промывка лотком без воды невозможна, и отменена она
    /// была именно этой слепотой, а не пустым миром.
    ///
    /// Клетка выдаётся ОДИН РАЗ, даже когда подошли оба слоя: под водой стоит
    /// и трава, и запрос «найди water» не должен считать её дважды.
    /// </summary>
    private string? Suits(int x, int y, int z, Func<BlockPos, string, bool> matchCode)
    {
        var cell = new BlockPos(x, y, z);
        if (GetBlockCode(x, y, z) is { Length: > 0 } solid && matchCode(cell, solid))
            return solid;
        return GetLiquidCode(x, y, z) is { Length: > 0 } liquid && matchCode(cell, liquid)
            ? liquid
            : null;
    }

    /// <summary>Блоки, чей код содержит любую из подстрок.</summary>
    public IEnumerable<BlockPos> FindBlocks(BlockPos center, int radius, params string[] codeParts) =>
        FindBlocks(code => codeParts.Any(p => code.Contains(p, StringComparison.OrdinalIgnoreCase)),
            center, radius);

    /// <summary>Ближайший подходящий блок (null — не нашёлся).</summary>
    public BlockPos? FindNearestBlock(Func<string, bool> matchCode, BlockPos center,
        int radius = 16, int height = 8) =>
        FindBlocks(matchCode, center, radius, height, limit: 1).Cast<BlockPos?>().FirstOrDefault();

    /// <summary>
    /// Максимальный номер стадии для семейства блоков вида «crop-flax-1..7»:
    /// берётся из реестра сервера — то же знание, что у клиента игры.
    /// </summary>
    public int MaxStageOf(string prefix)
    {
        int max = 0;
        // Маска ищется подстрокой, звёздочка тут не нужна: с ней «crop-flax-*»
        // не совпадал ни с чем, MaxStageOf всегда возвращал 0, и спелость
        // культуры не определялась вовсе (нашли автотестом)
        foreach (var code in SearchBlockCodes(prefix, limit: 64))
        {
            int dash = code.LastIndexOf('-');
            if (dash > 0 && int.TryParse(code.AsSpan(dash + 1), out int n))
                max = Math.Max(max, n);
        }
        return max;
    }

    /// <summary>Номер стадии в коде блока («crop-flax-4» → 4; -1, если её нет).</summary>
    public static int StageOf(string code)
    {
        int dash = code.LastIndexOf('-');
        return dash > 0 && int.TryParse(code.AsSpan(dash + 1), out int n) ? n : -1;
    }
}
