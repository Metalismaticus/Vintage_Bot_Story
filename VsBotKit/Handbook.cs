namespace VsBotKit;

/// <summary>Строка руководства: что это и что о нём известно.</summary>
public sealed record HandbookEntry(
    string Code,
    string Kind,
    string Summary,
    IReadOnlyList<string> Recipes,
    IReadOnlyList<string> DroppedBy,
    IReadOnlyList<string> UsedIn)
{
    public override string ToString() => $"{Code} ({Kind}): {Summary}";
}

/// <summary>
/// Руководство — то же, что игрок открывает клавишей H.
///
/// Важное наблюдение: НИКАКИХ НОВЫХ ДАННЫХ для этого не нужно. Руководство в
/// игре не приходит с сервера отдельной книгой — клиент собирает его из тех же
/// реестров, что бот уже получил при входе (пакет 19): реестра блоков, реестра
/// предметов, рецептов сетки и рецептов тёски. То есть «научить читать
/// руководство» — это не новая механика, а СПРАВОЧНЫЙ СЛОЙ поверх того, что
/// уже лежит в модели мира.
///
/// Отсюда и польза для автоматизации: бот может ответить на вопросы, которые
/// раньше приходилось зашивать в код —
/// «из чего это делается», «что из этого получится», «откуда это берётся»,
/// «что я вообще могу сделать прямо сейчас».
/// </summary>
public class Handbook
{
    private readonly BotContext ctx;
    private readonly Crafting crafting;
    private readonly Knapping knapping;
    private readonly Errands errands;

    public Handbook(BotContext ctx, Crafting crafting, Knapping knapping, Errands errands)
    {
        this.ctx = ctx;
        this.crafting = crafting;
        this.knapping = knapping;
        this.errands = errands;
    }

    /// <summary>Реестры пришли — руководству есть что читать.</summary>
    public bool Ready => crafting.Ready || knapping.Ready;

    /// <summary>Сколько всего известно: блоков, предметов, рецептов.</summary>
    public (int Blocks, int Items, int GridRecipes, int KnappingRecipes) Volume() =>
        (ctx.World.SearchBlockCodes("", int.MaxValue).Count(),
         ctx.World.SearchItemCodes("", int.MaxValue).Count(),
         crafting.All.Count,
         knapping.Recipes.Count);

    /// <summary>Найти по части названия — как поиск в руководстве.</summary>
    public IEnumerable<string> Search(string part, int limit = 20) =>
        ctx.World.SearchItemCodes(part, limit)
            .Concat(ctx.World.SearchBlockCodes(part, limit))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit);

    /// <summary>
    /// Полная страница о предмете: чем является, из чего делается, откуда
    /// падает, куда идёт дальше.
    /// </summary>
    public HandbookEntry About(string code)
    {
        string kind = ctx.World.SearchItemCodes(code, 1).Any(c =>
            string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) ? "предмет" : "блок";

        var facts = new List<string>();
        if (ctx.World.GetSatietyByCode(code) is > 0 and var satiety)
        {
            float health = ctx.World.GetFoodHealthByCode(code);
            facts.Add(health < 0
                ? $"еда на {satiety:0} сытости, но ОТНИМАЕТ {-health:0.#} хп — яд"
                : $"еда на {satiety:0} сытости");
        }
        if (ctx.World.GetToolTypeByCode(code) is { } tool)
            facts.Add($"инструмент типа {tool}, прочность {ctx.World.Durability(code)}");
        if (ctx.World.GetAttackPowerByCode(code) is > 1f and var power)
            facts.Add($"урон {power:0.#}");
        if (ctx.World.GetArmorByCode(code) is { } armor)
            facts.Add($"броня: {armor}");
        if (ctx.World.IsBagByCode(code))
            facts.Add($"сумка на {ctx.World.BagSlotsByCode(code)} мест");
        if (ctx.World.MaxStackSize(code) is var stack and > 0)
            facts.Add($"стак {stack}");

        return new HandbookEntry(
            code, kind,
            facts.Count > 0 ? string.Join(", ", facts) : "обычный предмет",
            RecipesFor(code),
            errands.Sources(code).Take(8).ToList(),
            UsedIn(code));
    }

    /// <summary>Как это сделать: рецепты сетки и тёски одним списком.</summary>
    public IReadOnlyList<string> RecipesFor(string code)
    {
        var list = new List<string>();
        foreach (var recipe in crafting.RecipesFor(code))
            list.Add("сетка: " + Describe(recipe));
        foreach (var k in knapping.Recipes)
            if (k.OutputCode.Contains(code, StringComparison.OrdinalIgnoreCase))
                list.Add($"тёска: {k.OutputCode} из {k.IngredientCode}");
        return list;
    }

    /// <summary>
    /// РЕЦЕПТ СЛОВАМИ — И ЧЕРТА КЛАССА В ТЕХ ЖЕ СЛОВАХ, ЕСЛИ ОНА ЕСТЬ.
    ///
    /// ПРИЁМКА 27.08, НАХОДКА 4. Здесь стоял один список составляющих, и
    /// «!руководство bow-crude» отвечало «делается: сетка: stick ×3 + rope ×3»,
    /// а «!какдобыть bow-crude» на тот же вопрос — «требует черту класса
    /// bowyer». Два ответа на один вопрос, и один из них лгал умолчанием:
    /// человек нёс боту три палки и три верёвки, а бот их не брал.
    ///
    /// ПРЯЧЕМ ЛИ МЫ ЗАПЕРТЫЙ РЕЦЕПТ? НЕТ, И ЭТО НАРОЧНО. Справочник отвечает
    /// ЧЕЛОВЕКУ, а у человека черта может быть — в отличие от бота, который
    /// своего класса не знает ни одной строкой. Врёт не показ рецепта, а
    /// умолчание о его цене; цену и называем.
    /// </summary>
    private static string Describe(CraftRecipe recipe) =>
        string.Join(" + ", recipe.Ingredients
            .Select(i => i.Code ?? "по тегам")
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)) +
        (Crafting.LockedByTrait(recipe)
            ? $" — требует черту класса «{recipe.RequiresTrait}», мне такой сбор недоступен"
            : "");

    /// <summary>Во что это идёт: рецепты, где предмет — ингредиент.</summary>
    public IReadOnlyList<string> UsedIn(string code, int limit = 12)
    {
        var result = new List<string>();
        foreach (var recipe in crafting.All)
        {
            if (result.Count >= limit)
                break;
            if (recipe.Ingredients.Any(i =>
                    i.Code?.Contains(code, StringComparison.OrdinalIgnoreCase) == true))
                result.Add(recipe.OutputCode);
        }
        return result;
    }

    /// <summary>
    /// Что бот может сделать ПРЯМО СЕЙЧАС из того, что у него есть. Это и
    /// есть «руководство под рукой»: не список всех рецептов игры, а список
    /// доступных шагов.
    /// </summary>
    public IEnumerable<string> CanMakeNow(int limit = 30) =>
        crafting.All
            .Select(r => r.OutputCode)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(output => crafting.CanCraft(output))
            .Take(limit);
}
