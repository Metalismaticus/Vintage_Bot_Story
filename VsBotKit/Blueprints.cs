namespace VsBotKit;

/// <summary>Один блок шаблона: смещение от точки привязки и код блока.</summary>
public sealed record BlueprintBlock(int DX, int DY, int DZ, string Code);

/// <summary>
/// Шаблон постройки: имя, размеры и список блоков со смещениями.
///
/// Хранится текстом, чтобы понравившийся дом можно было посмотреть глазами,
/// поправить в блокноте и передать другому боту. Формат простой:
/// первая строка — имя, дальше по строке на блок: «dx dy dz код».
/// </summary>
public sealed class Blueprint
{
    public required string Name { get; init; }
    public required List<BlueprintBlock> Blocks { get; init; }

    /// <summary>Габариты в блоках (по занятым клеткам).</summary>
    public (int X, int Y, int Z) Size => Blocks.Count == 0
        ? (0, 0, 0)
        : (Blocks.Max(b => b.DX) - Blocks.Min(b => b.DX) + 1,
           Blocks.Max(b => b.DY) - Blocks.Min(b => b.DY) + 1,
           Blocks.Max(b => b.DZ) - Blocks.Min(b => b.DZ) + 1);

    /// <summary>Сколько чего нужно на постройку (код → количество).</summary>
    public Dictionary<string, int> Materials()
    {
        var need = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in Blocks)
            need[b.Code] = need.TryGetValue(b.Code, out int had) ? had + 1 : 1;
        return need;
    }

    public override string ToString() =>
        $"{Name}: {Blocks.Count} блоков, {Size.X}×{Size.Y}×{Size.Z}";

    // ---------------- текстовый формат ----------------

    public string ToText()
    {
        var lines = new List<string> { Name };
        foreach (var b in Blocks.OrderBy(b => b.DY).ThenBy(b => b.DZ).ThenBy(b => b.DX))
            lines.Add($"{b.DX} {b.DY} {b.DZ} {b.Code}");
        return string.Join(Environment.NewLine, lines);
    }

    public static Blueprint FromText(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        if (lines.Count == 0)
            throw new FormatException("пустой шаблон");

        var blocks = new List<BlueprintBlock>();
        foreach (string line in lines.Skip(1))
        {
            var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], out int dx) ||
                !int.TryParse(parts[1], out int dy) ||
                !int.TryParse(parts[2], out int dz))
                continue;   // мусорную строку молча пропускаем, но не роняем шаблон
            blocks.Add(new BlueprintBlock(dx, dy, dz, parts[3].Trim()));
        }
        return new Blueprint { Name = lines[0], Blocks = blocks };
    }
}

/// <summary>Чем кончилась постройка.</summary>
public sealed record BuildReport(int Placed, int Total, int Missing, string Message)
{
    public bool Success => Placed >= Total;
    public override string ToString() => Message;
}

/// <summary>
/// Постройки по шаблонам: снять понравившийся дом в шаблон, сохранить в
/// библиотеку, построить в другом месте.
///
/// Всё честно: снимок — это чтение модели мира (то же, что видит игрок),
/// а постройка — обычная установка блоков через <see cref="Mining.PlaceAsync"/>,
/// то есть блок берётся в руку, бот подходит на длину руки и сервер
/// подтверждает каждую клетку. Ни телепортации, ни «наколдовать стену».
///
/// Библиотека — папка с текстовыми файлами: понравившийся дом можно
/// посмотреть, поправить руками и передать другому боту.
/// </summary>
public class Blueprints
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Blueprints(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
    }

    public event Action<string>? OnLog;

    /// <summary>Папка с шаблонами.</summary>
    public string Directory { get; set; } =
        Path.Combine(BotFolders.State(), "blueprints");

    /// <summary>Сколько блоков максимум снимать за раз (защита от «снял полконтинента»).</summary>
    public int MaxSnapshotBlocks { get; set; } = 20000;

    /// <summary>
    /// Какие блоки в снимок не попадают: воздух, трава, вода, снег.
    ///
    /// ЭТО КУСКИ КОДОВ, И ДВА ИЗ НИХ БЕРУТ ЛИШНЕЕ — названо вслух, чтобы не
    /// искали: «flower» совпадает с <c>flowerpot</c> (цветочный горшок, а это
    /// КОНТЕЙНЕР), «water» — с <c>waterwheel</c> (водяное колесо, механизм) и
    /// <c>wateringcan</c> (поставленная лейка). Такой блок в снимок дома не
    /// попадёт, и постройка по шаблону его не восстановит.
    ///
    /// ПОЧЕМУ ЭТО ТЕРПИМО, В ОТЛИЧИЕ ОТ ТОЙ ЖЕ ДОГАДКИ В ПОИСКЕ ПУТИ. Список
    /// работает в БЕЗОПАСНУЮ сторону («не снимать»), правит его человек прямо
    /// в этом свойстве, а цена ошибки — недостающий горшок в шаблоне, а не
    /// утонувший бот. Настоящей мерки «мусор ли это» у сервера нет: в реестре
    /// есть <c>replaceable</c>, но по нему трава и вода неотличимы от снега и
    /// от факела.
    /// </summary>
    public List<string> IgnoreMasks { get; } =
        ["tallgrass", "flower", "water", "snowlayer", "mushroom", "seashell", "looseflints", "loosestones"];

    private bool Ignored(string code) =>
        IgnoreMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase));

    // ---------------- снять шаблон ----------------

    /// <summary>
    /// Снять область в шаблон. Смещения считаются от угла с наименьшими
    /// координатами — так шаблон не зависит от того, где его сняли.
    /// </summary>
    public Blueprint Capture(string name, BlockPos from, BlockPos to)
    {
        int x0 = Math.Min(from.X, to.X), x1 = Math.Max(from.X, to.X);
        int y0 = Math.Min(from.Y, to.Y), y1 = Math.Max(from.Y, to.Y);
        int z0 = Math.Min(from.Z, to.Z), z1 = Math.Max(from.Z, to.Z);

        var blocks = new List<BlueprintBlock>();
        for (int y = y0; y <= y1 && blocks.Count < MaxSnapshotBlocks; y++)
        for (int z = z0; z <= z1 && blocks.Count < MaxSnapshotBlocks; z++)
        for (int x = x0; x <= x1 && blocks.Count < MaxSnapshotBlocks; x++)
        {
            if (ctx.World.GetBlockId(x, y, z) == 0)
                continue;
            if (ctx.World.GetBlockCode(new BlockPos(x, y, z)) is not { } code || Ignored(code))
                continue;
            blocks.Add(new BlueprintBlock(x - x0, y - y0, z - z0, code));
        }

        var print = new Blueprint { Name = name, Blocks = blocks };
        OnLog?.Invoke($"снял шаблон «{name}»: {print}");
        return print;
    }

    // ---------------- библиотека ----------------

    private string FileOf(string name) =>
        Path.Combine(Directory, MakeSafe(name) + ".txt");

    /// <summary>
    /// Имя шаблона приходит из игрового чата, то есть от любого игрока на
    /// сервере. Чистка одна на всех, кто берёт имена снаружи — см.
    /// <see cref="BotFolders.SafeName"/>.
    /// </summary>
    private static string MakeSafe(string name) => BotFolders.SafeName(name);

    /// <summary>Сохранить шаблон в библиотеку (перезаписывает одноимённый).</summary>
    public void Save(Blueprint print)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(FileOf(print.Name), print.ToText());
        OnLog?.Invoke($"шаблон «{print.Name}» сохранён: {FileOf(print.Name)}");
    }

    /// <summary>Загрузить шаблон по имени (null — такого нет).</summary>
    public Blueprint? Load(string name)
    {
        string file = FileOf(name);

        // РЕГИСТР. На Windows «Дом» и «дом» — один файл, на Linux и обычно на
        // macOS — разные. Без этого шаблон, снятый вечером, наутро «пропадал»
        // от одной заглавной буквы. Ищем как человек: по имени, не глядя на
        // регистр
        if (!File.Exists(file))
        {
            string wanted = MakeSafe(name);
            string? found = Names().FirstOrDefault(
                n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
            if (found == null)
                return null;
            file = Path.Combine(Directory, found + ".txt");
        }
        try
        {
            return Blueprint.FromText(File.ReadAllText(file));
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"шаблон «{name}» не прочитался: {ex.Message}");
            return null;
        }
    }

    /// <summary>Что есть в библиотеке.</summary>
    public IEnumerable<string> Names()
    {
        if (!System.IO.Directory.Exists(Directory))
            return [];
        return System.IO.Directory.GetFiles(Directory, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .OrderBy(n => n)!;
    }

    // ---------------- построить ----------------

    /// <summary>
    /// Чего не хватает на постройку: код → сколько недостаёт. Пусто — хватает
    /// всего. Спрашивать это ДО стройки честнее, чем начать и встать посреди
    /// стены.
    /// </summary>
    public Dictionary<string, int> Missing(Blueprint print)
    {
        var lack = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, need) in print.Materials())
        {
            int have = hands.CountOf(code);
            if (have < need)
                lack[code] = need - have;
        }
        return lack;
    }

    /// <summary>
    /// Построить по шаблону от точки привязки.
    ///
    /// Порядок снизу вверх — иначе бот ставит крышу над пустотой и не
    /// дотягивается до стен. Каждая клетка подтверждается сервером; чего нет
    /// в инвентаре, то честно считается пропущенным.
    /// </summary>
    public async Task<BuildReport> BuildAsync(Blueprint print, BlockPos at,
        CancellationToken ct = default)
    {
        int placed = 0, missing = 0, blocked = 0;
        var ordered = print.Blocks
            .OrderBy(b => b.DY)
            .ThenBy(b => Math.Abs(b.DX) + Math.Abs(b.DZ))
            .ToList();

        foreach (var b in ordered)
        {
            if (ct.IsCancellationRequested)
                break;
            var cell = new BlockPos(at.X + b.DX, at.Y + b.DY, at.Z + b.DZ);

            // Уже стоит нужное — идём дальше
            if (ctx.World.GetBlockCode(cell) is { } present &&
                present.Equals(b.Code, StringComparison.OrdinalIgnoreCase))
            {
                placed++;
                continue;
            }
            if (hands.CountOf(b.Code) <= 0)
            {
                missing++;
                continue;
            }

            var result = await mining.PlaceAsync(cell, b.Code, ct: ct);
            if (result.Success)
                placed++;
            else
            {
                blocked++;
                OnLog?.Invoke($"{cell} {b.Code}: {result}");
            }
        }

        string message = missing > 0
            ? $"построил {placed} из {print.Blocks.Count}, не хватило материалов на {missing}" +
              (blocked > 0 ? $", отказано на {blocked}" : "")
            : blocked > 0
                ? $"построил {placed} из {print.Blocks.Count}, отказано на {blocked}"
                : $"построил всё: {placed} блоков";
        OnLog?.Invoke(message);
        return new BuildReport(placed, print.Blocks.Count, missing, message);
    }
}
