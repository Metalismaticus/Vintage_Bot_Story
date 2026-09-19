using EnumBlockMaterial = Vintagestory.API.Common.EnumBlockMaterial;

namespace VsBotKit;

/// <summary>
/// ЧЕМ КЛЕТКА ВРЕДИТ ТОМУ, КТО В НЕЙ ОКАЗАЛСЯ.
///
/// Порядок значений не случаен: чем больше число, тем хуже. По двум слоям
/// (жидкость и блок) берётся ХУДШЕЕ, и делается это одним сравнением, а не
/// разбором случаев.
/// </summary>
public enum CellHarm
{
    /// <summary>Клетка не вредит.</summary>
    None = 0,

    /// <summary>
    /// Достаётся только тому, кто ВБЕЖАЛ или УПАЛ: шипы, кактус.
    /// Игра бьёт за них лишь при беге с разгона
    /// (BlockDamageOnTouch.OnEntityInside: ServerControls.Sprint И движение).
    /// Стоящему смирно такая клетка не вредит — но и заводить в неё маршрут
    /// незачем.
    /// </summary>
    IfRunning = 1,

    /// <summary>
    /// Жжёт за одно то, что ты здесь: лава, кипяток, огонь, горящий костёр.
    /// С такой клетки надо УХОДИТЬ, а не терпеть.
    /// </summary>
    Always = 2
}

/// <summary>
/// ВРЕДНАЯ КЛЕТКА: ОДНО ПРАВИЛО НА ВЕСЬ ПРОЕКТ, И ОНО СПРОШЕНО У ИГРЫ.
///
/// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО. Бот сам сложил костёр, сам пришёл к
/// нему греться — и ВСТАЛ В НЕГО. Сгорел, потеряв 48 стопок вещей. Заказчик
/// называл ту же беду и раньше, дословно: «на костре не стоять и на любых
/// других зонах с уроном — кипяток и лава например».
///
/// ПОЧЕМУ КОСТРА ХВАТИЛО, ЧТОБЫ УБИТЬ. У блока костра
/// (assets/survival/blocktypes/wood/firepit.json) <c>collisionbox: null</c> —
/// коллизии нет вовсе. Для тела костёр — это воздух: в него входят ногами,
/// сквозь него строят маршрут, на нём стоят. А <c>BlockFirepit.OnEntityInside</c>
/// в это время бьёт стоящего огнём, пока костёр горит.
///
/// СПИСКА КОДОВ ЗДЕСЬ НЕТ И БЫТЬ НЕ МОЖЕТ (закон 6). Спрашиваем у игры ровно
/// то, чем она сама решает, вредить ли стоящему. Разобрано ilspycmd по
/// 1.22.7, и в игре таких мест РОВНО ТРИ — все три перечислены ниже:
///
/// 1. <b>Атрибут блока insideDamage</b>. <c>BlockForFluidsLayer.OnEntityInside</c>
///    (VSEssentials): <c>if (InsideDamage > 0) entity.ReceiveDamage(...)</c>,
///    где <c>InsideDamage = Attributes["insideDamage"]</c>. Это лава (3, Fire)
///    и кипяток (1, Acid). ЭТИМ ЖЕ ВОПРОСОМ ИГРА САМА ВЫБИРАЕТ, КУДА ВСТАТЬ:
///    <c>AiTaskBaseTargetable.FindDecentTeleportPos</c> отбрасывает клетку,
///    если <c>attributes["insideDamage"].AsInt(0) > 0</c>. Мы спрашиваем то же
///    самое и там же.
///
/// 2. <b>Материал блока Fire и Lava</b> (<c>EnumBlockMaterial</c>, приходит в
///    реестре полем <c>Packet_BlockType.BlockMaterial</c>). Материал Fire во
///    всей ванили носит один блок — «fire», и у него insideDamage НЕТ: жжёт не
///    блок, а его блок-сущность, <c>BEBehaviorBurning.OnSlowServerTick</c> —
///    2 урона в секунду каждому, чья коробка задела клетку, плюс 12,5 % шанс
///    поджечь самого. Материал Lava — страховка на случай модовой лавы без
///    атрибута.
///
/// 3. <b>Классы блоков, которые бьют стоящего сами</b>. Их в игре три, и это
///    не догадка: <c>grep OnEntityInside</c> по VSSurvivalMod и VSEssentials
///    даёт ровно <c>BlockForFluidsLayer</c> (пункт 1),
///    <c>BlockFirepit</c> и <c>BlockDamageOnTouch</c>. Имя класса приходит в
///    реестре полем <c>Packet_BlockType.Blockclass</c> — сервер берёт его из
///    своей же таблицы <c>BlockClassToTypeMapping</c>, то есть это имя игры, а
///    не наше.
///
/// ЧЕГО ЭТО ПРАВИЛО НЕ ЛОВИТ, И ЭТО СКАЗАНО ВСЛУХ (закон 4). Блок с
/// поведением блок-сущности «Burning» (деревянная ось, наземное хранилище,
/// обжиговая яма) жжёт, ПОКА ГОРИТ, а «горит ли он прямо сейчас» держит
/// блок-сущность (<c>BEBehaviorBurning.IsBurning</c>), а не реестр. Считать
/// такие блоки вредными всегда нельзя: бот перестал бы ходить по осям и
/// хранилищам. Считать безвредными — значит не увидеть загоревшуюся ось.
/// Загоревшийся блок в игре обычно ставит рядом с собой блок «fire», и вот
/// ЕГО мы видим (пункт 2), — но это не то же самое, и полной защиты здесь нет.
///
/// ЗДЕСЬ ТОЛЬКО ЧИСТОЕ ПРАВИЛО, И ЭТО НЕ КРАСОТА, А УСЛОВИЕ СБОРКИ МОДА.
/// Мод VSAutoPilot компилирует ядро библиотеки списком файлов и знать не знает
/// ни про <c>BotContext</c>, ни про <c>Movement</c>. Пока способность
/// «уйти из огня» лежала в этом же файле, мод внести его к себе не мог — и
/// собирался с четырьмя ошибками «CellHarm не найден», то есть у заказчика
/// осталась бы старая DLL мода. Способность живёт отдельно, в
/// <see cref="BehaviorLeaveHarm"/>.
/// </summary>
public static class HarmRules
{
    /// <summary>
    /// Материал «лава» — <c>EnumBlockMaterial.Lava</c>.
    ///
    /// ЧИСЛО ВЗЯТО У ИГРЫ, А НЕ НАПИСАНО РУКОЙ (закон 6). Здесь стояло голое
    /// «17» с оговоркой «правило обязано проверяться стендом без единой
    /// игровой сборки» — оговорка была неверна: <c>VsBotKit.csproj</c> и без
    /// того ссылается на VintagestoryAPI, а соседний <c>BlockOwners.cs</c>
    /// зовёт это же перечисление по имени. Хуже другого: тест держал ТОТ ЖЕ
    /// рукописный литерал, и подменой такая пара не ловится — вставь игра
    /// материал в середину перечисления, оба остались бы зелёными, а бот
    /// считал бы лавой кирпич. Тип наружу остаётся <c>int</c>: сервер шлёт
    /// материал числом (<c>Packet_BlockType.BlockMaterial</c>).
    /// </summary>
    public const int MaterialLava = (int)EnumBlockMaterial.Lava;

    /// <summary>Материал «огонь» — <c>EnumBlockMaterial.Fire</c>, см. выше.</summary>
    public const int MaterialFire = (int)EnumBlockMaterial.Fire;

    /// <summary>Класс костра: <c>api.RegisterBlockClass("BlockFirepit", …)</c>.</summary>
    public const string FirepitClass = "BlockFirepit";

    /// <summary>
    /// Вариант горящего костра. Игра решает «горит ли» тем же словом:
    /// <c>BlockEntityFirepit.setBlockState("lit")</c> ставит вариант
    /// <c>burnstate = lit</c>, и она же читает его обратно
    /// (<c>Block.Variant["burnstate"]</c>).
    /// </summary>
    public const string LitVariant = "lit";

    /// <summary>
    /// Классы, которые бьют вбежавшего: шипы гнезда локустов и серебряный
    /// кактус. Оба зарегистрированы игрой рядом
    /// (<c>RegisterBlockClass("BlockDamageOnTouch"…)</c>,
    /// <c>…("BlockPlantDamageOnTouch"…)</c>), второй наследует первому.
    /// </summary>
    public static readonly string[] TouchDamageClasses =
        ["BlockDamageOnTouch", "BlockPlantDamageOnTouch"];

    /// <summary>
    /// ЦЕНА ПРОХОДА, ПРИ КОТОРОЙ САМА ИГРА ОТКАЗЫВАЕТСЯ ТУТ ХОДИТЬ.
    ///
    /// Число не наше: <c>PathfinderTask</c> (VSEssentials) семь раз подряд
    /// пишет <c>if (traversalCost &gt; 10000f) return false</c> — это и есть её
    /// граница «сюда не веду». Блоки отвечают ей двумя круглыми числами:
    /// <c>99999f</c> — жёсткий отказ (<c>BlockLava</c>, кипящая
    /// <c>BlockWater</c>), <c>10000f</c> — «почти отказ»
    /// (<c>BlockFirepit</c>, <c>BlockCoalPile</c>, <c>BlockPitkiln</c> — пока
    /// горят; <c>BlockIngotMold</c>, <c>BlockToolMold</c> — пока горячее 300°).
    /// Берём <b>не строго больше, а не меньше</b> 10000 нарочно: игре круглые
    /// 10000 нужны как «обойди любой ценой», нам этого довода достаточно.
    /// </summary>
    public const float NeverPathCost = 10000f;

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: вредит ли такой БЛОК тому, кто в его клетке.
    ///
    /// Все доводы — из игры, ни один не выдуман. Ни мира, ни бота здесь нет,
    /// поэтому правило проверяется стендом целиком.
    /// </summary>
    /// <param name="blockMaterial">Поле реестра BlockMaterial (EnumBlockMaterial).</param>
    /// <param name="blockClass">Поле реестра Blockclass («BlockFirepit» и т. п.).</param>
    /// <param name="code">Код блока — нужен ровно на «горит ли костёр».</param>
    /// <param name="insideDamage">Атрибут блока insideDamage (0 — нет такого).</param>
    /// <param name="gamePathCost">
    /// <c>Block.GetTraversalCost(pos, Humanoid)</c> — СОБСТВЕННЫЙ ОТВЕТ ИГРЫ
    /// на тот же вопрос, но спросить его может не всякий: нужен живой объект
    /// блока с его блок-сущностью. У мода он есть, у безголового бота нет —
    /// тому сервер везёт только реестр, — поэтому довод необязательный, а не
    /// забытый. Здесь он закрывает ровно ту дыру, что названа в шапке
    /// <see cref="HarmRules"/>: «горит ли прямо сейчас» у угольной кучи,
    /// обжиговой ямы и наземного хранилища держит блок-сущность, и реестром
    /// это не спрашивается никак — а игра спрашивает и отвечает.
    /// </param>
    public static CellHarm HarmOf(int blockMaterial, string? blockClass, string? code,
                                  double insideDamage, float gamePathCost = 0f)
    {
        var byRegistry = ByRegistry(blockMaterial, blockClass, code, insideDamage);

        // ОТКАЗ ИГРЫ ХОДИТЬ — ЭТО «НЕ ЗАВОДИ СЮДА МАРШРУТ», А НЕ «БЕГИ
        // ОТСЮДА». Горячая форма для отливки не жжёт стоящего (её нет среди
        // трёх классов с OnEntityInside), но игра по ней не водит никого —
        // значит и нам незачем. Подняв это до Always, мы заставили бы бота
        // бросать работу и «спасаться» с безобидной клетки
        var byGame = gamePathCost >= NeverPathCost ? CellHarm.IfRunning : CellHarm.None;

        return byRegistry >= byGame ? byRegistry : byGame;
    }

    private static CellHarm ByRegistry(int blockMaterial, string? blockClass, string? code,
                                       double insideDamage)
    {
        // 1. Игра сама спрашивает этот атрибут, выбирая, куда встать
        if (insideDamage > 0)
            return CellHarm.Always;

        // 2. Огонь и лава — по материалу самой игры
        if (blockMaterial is MaterialFire or MaterialLava)
            return CellHarm.Always;

        // 3. Костёр: жжёт, только пока горит. Погасший — обычная мебель, и
        //    ходить вокруг него бот обязан свободно (иначе он не сможет ни
        //    сложить его, ни подкинуть дров)
        if (blockClass == FirepitClass)
            return IsLit(code) ? CellHarm.Always : CellHarm.None;

        // 4. Шипы и кактус: бьют вбежавшего, стоящему смирно — нет
        foreach (string c in TouchDamageClasses)
            if (blockClass == c)
                return CellHarm.IfRunning;

        return CellHarm.None;
    }

    /// <summary>
    /// Горит ли костёр — по варианту кода, ровно как читает его сама игра
    /// (<c>Variant["burnstate"] == "lit"</c>). Код приходит целиком
    /// («firepit-lit», у модов бывает и с доменом «game:firepit-lit»), поэтому
    /// ищем ЗНАЧЕНИЕ ВАРИАНТА среди частей кода, а не подстроку: «lit» как
    /// подстрока сидит и в «monolith», и в «lightbulb».
    /// </summary>
    public static bool IsLit(string? code)
    {
        if (code is not { Length: > 0 })
            return false;
        int from = 0;
        while (from < code.Length)
        {
            int dash = code.IndexOf('-', from);
            int end = dash < 0 ? code.Length : dash;
            if (end - from == LitVariant.Length &&
                string.Compare(code, from, LitVariant, 0, LitVariant.Length,
                               StringComparison.OrdinalIgnoreCase) == 0)
                return true;
            if (dash < 0)
                break;
            from = dash + 1;
        }
        return false;
    }

    /// <summary>
    /// В КЛЕТКУ НЕЛЬЗЯ ВСТАВАТЬ ЗАРАНЕЕ: любой вред, хоть «только вбежавшему».
    /// Этим вопросом живут маршрут, выбор места работы и выбор места стоянки.
    /// </summary>
    public static bool HurtsToEnter(this IPathWorld world, int x, int y, int z) =>
        world.HarmAt(x, y, z) != CellHarm.None;

    /// <summary>
    /// С КЛЕТКИ НАДО УХОДИТЬ ПРЯМО СЕЙЧАС: она жжёт стоящего смирно.
    /// Этим вопросом живёт спасение (<see cref="BehaviorLeaveHarm"/>) — и
    /// только оно: сойти с шипов, на которые никто не бежит, незачем.
    /// </summary>
    public static bool HurtsToStandIn(this IPathWorld world, int x, int y, int z) =>
        world.HarmAt(x, y, z) == CellHarm.Always;

    /// <summary>
    /// ГДЕ ИМЕННО ЖЖЁТ ТЕЛО, СТОЯЩЕЕ НОГАМИ В ЭТОЙ КЛЕТКЕ: клетка ног или
    /// клетка головы (null — не жжёт нигде).
    ///
    /// ДВЕ КЛЕТКИ, А НЕ ОДНА, — и это не осторожность, а рост игрока: он
    /// занимает две клетки по высоте, и лава по грудь убивает ровно так же,
    /// как лава по колено. Клетку ПОД ногами сюда не берём нарочно: огонь
    /// игры бьёт по коробке от <c>y</c> до <c>y+1</c>
    /// (<c>BEBehaviorBurning.fireCuboid</c>), и стоящий НА блоке огня её не
    /// задевает — а вот «стою на костре» именно и значит «ноги в клетке
    /// костра», потому что коллизии у костра нет.
    ///
    /// Ни бота, ни сервера здесь нет — только вопросы к миру, поэтому правило
    /// проверяется стендом на подставном мире.
    /// </summary>
    public static BlockPos? WhereItBurns(IPathWorld world, BlockPos feet)
    {
        if (world.HurtsToStandIn(feet.X, feet.Y, feet.Z))
            return feet;
        var head = new BlockPos(feet.X, feet.Y + 1, feet.Z);
        return world.HurtsToStandIn(head.X, head.Y, head.Z) ? head : null;
    }
}
