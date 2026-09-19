namespace VsBotKit;

/// <summary>Найденная руда: где, что и сколько её видно.</summary>
/// <param name="Code">Полный код блока (ore-poor-cassiterite-andesite и т.п.).</param>
/// <param name="Metal">Металл: cassiterite, nativecopper… — по нему бот и решает, нужна ли.</param>
/// <param name="Richness">poor / medium / rich — насколько густо в блоке.</param>
public readonly record struct OreFind(BlockPos Pos, string Code, string Metal, string Richness,
    double Distance)
{
    // Богатство есть не у всякой находки: у простой породы (rock-granite) его
    // нет вовсе, и «granite () в (1, 2, 3)» в журнале выглядело поломкой
    public override string ToString() =>
        $"{Metal}{(Richness.Length > 0 ? $" ({Richness})" : "")} в {Pos}, {Distance:0} бл";
}

/// <param name="Broken">Сколько блоков руды выломано.</param>
/// <param name="Gained">Что реально прибавилось в сумках.</param>
public record VeinResult(int Broken, int Skipped, IReadOnlyDictionary<string, int> Gained,
    string Why)
{
    public override string ToString()
    {
        string what = Gained.Count == 0
            ? "ничего"
            : string.Join(", ", Gained.OrderByDescending(g => g.Value).Select(g => $"{g.Key} x{g.Value}"));
        return $"выломано {Broken} бл руды" + (Skipped > 0 ? $", пропущено {Skipped}" : "") +
               $"; получено: {what}" + (Why.Length > 0 ? $" ({Why})" : "");
    }
}

/// <summary>
/// Камень и руда: найти и выработать залежь.
///
/// ЧЕСТНОСТЬ ЗДЕСЬ ГЛАВНОЕ. Сервер присылает чанк целиком, и в памяти бота
/// лежит каждый блок внутри породы. Искать руду по этой памяти — значит
/// видеть сквозь камень, чего живой игрок не может никак. Поэтому поиск идёт
/// через общий фильтр видимости (<see cref="WorldModel.IsExposed"/>): бот
/// находит руду там же, где человек, — по выходам в пещере, на обрыве, в
/// стене шахты. Роль-читер снимает это флагом <see cref="Cheats.SeeThroughWalls"/>.
///
/// Жила вырабатывается «по соседям»: вскрыв один блок, игрок видит соседние
/// и идёт по ним. Так и здесь — обход в ширину по прилегающей руде того же
/// металла, а не слепой перебор области.
///
/// ПРОСТАЯ ПОРОДА идёт тем же путём, что и руда, и это не натяжка: «добудь
/// камень 64» — такая же работа киркой по соседним клеткам, только искать
/// выходы не надо, камня вокруг сколько угодно. Разница ровно в двух местах:
/// что считать целью (<see cref="IsStoneBlock"/>) и что считать добытым
/// (<see cref="CountFor"/>) — и то, и другое спрашивается у реестра сервера.
///
/// ЧТО ВИДЕЛ — запоминается: всё найденное здесь уходит в
/// <see cref="ResourceMemory"/>, если она подключена. Бот всё время проходит
/// мимо того, что сейчас не берёт, и без памяти это забывалось в ту же секунду.
/// </summary>
public class Ores
{
    private readonly BotContext ctx;

    /// <summary>Порода ли это, по коду блока: реестр спрашивается один раз на код.</summary>
    private readonly Dictionary<string, bool> stoneByCode = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Что падает с породы в ЭТОМ мире — считается один раз по реестру.</summary>
    private HashSet<string>? stoneDrops;

    /// <summary>О том, что реестр ещё не пришёл, говорим один раз, а не каждый поиск.</summary>
    private bool saidRegistryNotReady;

    /// <summary>
    /// Запомненные клетки, к которым уже ходили и не подобрались.
    ///
    /// Без этого «сходи туда, где видел» превращается в маятник: память
    /// исправно отдаёт одну и ту же ближайшую медь в стене за водой, бот
    /// исправно до неё не доходит — и так до конца отведённого времени.
    /// Забывать её нельзя: она ТАМ, просто не отсюда.
    /// </summary>
    private readonly HashSet<BlockPos> couldNotReach = [];

    public Ores(BotContext ctx) => this.ctx = ctx;

    public event Action<string>? OnLog;

    /// <summary>
    /// Память встреченного. Пусто — не запоминать (библиотеку можно собрать и
    /// без неё). Подключается ботом один раз, см. <see cref="ResourceMemory"/>.
    ///
    /// ПОДКЛЮЧАЯСЬ, ПАМЯТЬ ПОЛУЧАЕТ НАШУ МЕРКУ «подходит ли это под слово»
    /// (<see cref="Suits(string, string)"/>). Без этого «вижу» и «помню»
    /// отвечали бы про одно и то же по-разному: осмотр спрашивает реестр
    /// выпадений и находит метеорит по «stone-meteorite-iron», а память
    /// сравнивала бы буквы кода и та же запись ей бы не подошла.
    /// </summary>
    public ResourceMemory? Memory
    {
        get => memory;
        set
        {
            memory = value;
            if (value != null)
                value.Suits = Suits;
        }
    }

    private ResourceMemory? memory;

    /// <summary>Сколько блоков жилы брать за один заход.</summary>
    public int MaxVeinBlocks { get; set; } = 64;

    /// <summary>Насколько далеко от первого блока уходить по жиле.</summary>
    public int VeinSpread { get; set; } = 12;

    /// <summary>
    /// ВЫШЕ И НИЖЕ СЕБЯ ДАЛЬШЕ ЭТОГО НЕ СМОТРИМ, блоков.
    ///
    /// Осмотр перебирал КУБ со стороной 2×радиус: вверх и вниз столько же,
    /// сколько вокруг. Замер на стенде (радиус против времени одного взгляда):
    /// 32 — 16 мс, 48 — 57 мс, 96 — 497 мс, 250 — 7,6 СЕКУНДЫ. Растёт как куб,
    /// потому что вопрос о каждой клетке стоит около 60 нс (замок карты мира и
    /// поиск чанка), а клеток на радиусе 250 — сто двадцать пять миллионов.
    /// Семь секунд на взгляд — это вставший бот: осмотр в разведке случается
    /// после каждых «ОсматриватьсяЧерезКлеток» клеток хода.
    ///
    /// Почему именно вертикаль, а не общий радиус: мир Vintage Story высотой
    /// 256 блоков, и «смотреть на 250 вниз» — это смотреть сквозь дно мира.
    /// Человек, вписавший 250, просит смотреть ВОКРУГ, а не сверлить взглядом
    /// планету. Залежи генератор кладёт полосами по высоте, и руда, до которой
    /// вообще можно дойти, лежит в десятках блоков от своего уровня.
    ///
    /// Число 64 выбрано так, чтобы НИЧЕГО СЕГОДНЯШНЕГО не сузить: честный
    /// осмотр ходит на 10–32, читерский по умолчанию на 48 — все меньше 64 и
    /// работают ровно как раньше. Ограничение включается только там, где до
    /// него докрутили руками, и там оно меняет рост с кубического на
    /// квадратичный (250: около 2 с вместо 7,6).
    /// </summary>
    public int MaxLookHeight { get; set; } = 64;

    /// <summary>
    /// Сколько клеток ОДНОГО ЧУЖОГО ВИДА запоминать за один осмотр.
    ///
    /// Осмотр ищет заказанный металл, но глаза-то видят всё: олово и уголь, до
    /// которых бот дошёл вплотную по дороге за медью, обязаны попасть и в
    /// память, и на карту в окне управления — ради этого память и заводилась.
    /// А вот записывать ВСЮ чужую жилу нельзя: в игре это сотня блоков, и с
    /// читом одна кварцевая вытеснила бы из памяти всё редкое. Человеку на
    /// карте нужна метка залежи, а не её обвод по клеткам, поэтому берётся
    /// несколько ближайших клеток каждого вида — их хватает и на «где видел»,
    /// и на «сходи туда» (память отдаёт ближайшую, см. ResourceMemory.Recall).
    ///
    /// Ноль и меньше — не запоминать чужое вовсе.
    /// </summary>
    public int PassingMemoryPerKind { get; set; } = 3;

    /// <summary>
    /// Смотреть сквозь камень ИМЕННО ЗДЕСЬ, не включая чит целиком.
    ///
    /// Тонкая настройка нужна: одно дело — разведчик, которому позволено
    /// чувствовать руду в породе, и совсем другое — тот же бот, читающий
    /// чужие таблички сквозь стену. Здесь три состояния: null — как решено
    /// глобально (по умолчанию честно), true — рентген только для руды,
    /// false — честно даже при включённом общем чите.
    /// </summary>
    public bool? SeeOreThroughStone { get; set; }

    // ---------------- ПОРОДА КАК РЕСУРС ----------------

    /// <summary>
    /// Какими словами человек просит ПРОСТОЙ КАМЕНЬ.
    ///
    /// В игре нет кода «камень»: породы называются rock-granite, rock-andesite
    /// и так далее, а «камень» — слово человека. Ровно та же беда, что с
    /// металлами, где «медь» — это пять разных минералов (см.
    /// <see cref="OreCode.MetalGroups"/>). Из-за неё наряд «добудь камень 64»
    /// уходил в общее поручение, там искали блок с кодом «камень», не находили
    /// и честно отвечали «не знаю, откуда берётся камень», — то есть работа
    /// не начиналась вовсе.
    /// </summary>
    public static readonly string[] StoneWords =
        ["камень", "камня", "камни", "камней", "порода", "породы", "булыжник", "stone", "rock"];

    /// <summary>
    /// РУДА ЛИ ЭТОТ БЛОК — ЕДИНСТВЕННЫЙ ВОПРОС ЭТОГО РОДА НА ВЕСЬ ПРОЕКТ.
    ///
    /// Отвечает реестр (<see cref="Metals.IsOreBlock"/>), а не приставка кода:
    /// живой случай с метеоритом разобран там же. Здесь только честный отказ,
    /// когда спрашивать ещё некого: молча ответить «не руда» значило бы
    /// объявить, что руды в этом мире не бывает, — ровно та же беда, что уже
    /// была у породы (<see cref="IsStoneBlock"/>).
    /// </summary>
    public bool IsOre(string? code)
    {
        if (ctx.Metal is not { } metals)
        {
            if (!saidMetalNotReady)
            {
                saidMetalNotReady = true;
                OnLog?.Invoke("счёт металла не подключён — руду от породы отличить не могу");
            }
            return false;
        }
        if (metals.IsOreBlock(code))
            return true;

        // «НЕТ» ДО РЕЕСТРА — ЭТО НЕ «НЕ РУДА», А «ПОКА НЕ ЗНАЮ», и молчать об
        // этом нельзя: со стороны неготовый реестр выглядит как «руды вокруг
        // нет», и человек уходит искать её в другое место (та же охрана и та же
        // причина, что у породы в IsStoneBlock)
        if (!ctx.World.GameBlocksReady && !saidOreRegistryNotReady)
        {
            saidOreRegistryNotReady = true;
            OnLog?.Invoke("реестр блоков игры ещё не пришёл — руду от породы отличить не могу");
        }
        return false;
    }

    /// <summary>О том, что спросить про металл некого, говорим один раз, а не каждый блок.</summary>
    private bool saidMetalNotReady;

    /// <summary>О неготовом реестре — тоже один раз: осмотр спрашивает это миллион раз за взгляд.</summary>
    private bool saidOreRegistryNotReady;

    /// <summary>
    /// РУДНОЕ ЛИ ЭТО СЛОВО — то есть вести ли заказ РУДНОЙ ДОРОГОЙ (жилы,
    /// разведка сквозь камень, проходка, счёт металла) или обычным поручением.
    ///
    /// ЖИВОЙ СЛУЧАЙ (19.08, читы включены). Правило было рукописным: «слово из
    /// списка минералов ИЛИ начинается на ore-». Заказ «stone-meteorite-iron»
    /// не подошёл ни туда, ни туда, наряд написал «не руда, беру обычным
    /// поручением» — а поручение смотрит честными глазами на восемь клеток по
    /// высоте и не копает вовсе. Итог: «принёс 0 из 3».
    ///
    /// Теперь спрашивается РЕЕСТР, и вопрос ровно один: есть ли в мире блок,
    /// который даёт названное И который реестр зовёт рудоподобным. «Какие блоки
    /// дают названное» уже умеют поручения (<see cref="Errands.Sources"/>) —
    /// они читают выпадения из реестра; второй такой перебор здесь заводить
    /// нельзя, он же и стоит перебора всех блоков сервера.
    ///
    /// Русские слова металлов идут ВПЕРЁД реестра и это не поблажка: «медь» —
    /// слово человека, кода с такими буквами в игре нет ни одного, и спросить
    /// про него реестр нечем (см. <see cref="OreCode.MetalGroups"/>).
    /// </summary>
    public bool IsOreOrder(string? wanted)
    {
        if (wanted is not { Length: > 0 })
            return false;
        string слово = wanted.Trim();
        if (слово.Length == 0 || IsStoneOrder(слово))
            return false;
        if (OreCode.MetalGroups.Any(g =>
                g.Name.Equals(слово, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (oreOrder.TryGetValue(слово, out bool было))
            return было;
        if (ctx.Errands is not { } errands)
            return false;

        var блоки = errands.Sources(слово);
        if (блоки.Length == 0)
            return false;   // реестр мог и не прийти — пустоту не запоминаем
        bool руда = блоки.Any(IsOre);
        oreOrder[слово] = руда;
        return руда;
    }

    /// <summary>Рудное ли слово — спрашивается один раз на слово (перебор реестра дорог).</summary>
    private readonly Dictionary<string, bool> oreOrder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Просят ли простую породу, а не руду.</summary>
    public static bool IsStoneOrder(string? wanted) =>
        wanted is { Length: > 0 } &&
        Array.Exists(StoneWords, w => string.Equals(w, wanted.Trim(),
            StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Семейства блоков, которые считаются ПРИРОДНОЙ породой.
    ///
    /// Почему маска, а не просто «блок каменного материала»: в игре материал
    /// Stone стоит у блока ПО УМОЛЧАНИЮ (Block.BlockMaterial), и каменными
    /// оказываются заодно кирпичные стены, мостовая и обожжённый горшок
    /// (crock-*). Ломать чужой дом за наряд «принеси камень» бот не должен.
    /// Список — настройка: своё семейство (мод, другая порода) добавляется
    /// одной строкой, а не правкой кода.
    /// </summary>
    public List<string> StoneMasks { get; } = ["rock-", "crackedrock-", "loosestones-"];

    /// <summary>
    /// Природная ли это порода. Два вопроса подряд, и ни на один ответ не
    /// выдуман: семейство — из <see cref="StoneMasks"/>, материал — из реестра
    /// игры. Руда породой не считается: разменять жилу меди на «принеси
    /// камень» — это убыток, а не выполненный наряд.
    /// </summary>
    public bool IsStoneBlock(string? code)
    {
        if (code is not { Length: > 0 } || IsOre(code))
            return false;
        if (stoneByCode.TryGetValue(code, out bool known))
            return known;

        // Без реестра порода от кирпичной стены не отличается — и молчать об
        // этом нельзя: со стороны это выглядит как «камня вокруг нет»
        if (!ctx.World.GameBlocksReady)
        {
            if (!saidRegistryNotReady)
            {
                saidRegistryNotReady = true;
                OnLog?.Invoke("реестр блоков игры ещё не пришёл — породу отличить не могу");
            }
            return false;
        }

        bool byMask = StoneMasks.Any(m => code.StartsWith(m, StringComparison.OrdinalIgnoreCase));
        var block = byMask ? ctx.World.GetGameBlockByCode(code) : null;
        bool stone = block is { BlockId: > 0 } &&
                     block.BlockMaterial == Vintagestory.API.Common.EnumBlockMaterial.Stone;

        // Имя похоже на породу, а игра говорит другое — про такое лучше знать:
        // молча выкинутое семейство выглядит как «камня вокруг нет»
        if (byMask && !stone)
            OnLog?.Invoke($"{code} похож на породу, но игра зовёт его " +
                          $"{(block is { BlockId: > 0 } ? block.BlockMaterial.ToString() : "неизвестным")} " +
                          "— за камень не считаю");

        stoneByCode[code] = stone;
        return stone;
    }

    /// <summary>
    /// Что в этом мире падает с породы — СПРОШЕНО У РЕЕСТРА СЕРВЕРА, а не
    /// выписано списком кодов.
    ///
    /// С rock-granite падает предмет stone-granite, а блок, у которого убрали
    /// всех шестерых соседей, падает ЦЕЛЫМ (см. <see cref="Quarry"/>). В зачёт
    /// наряда идёт и то, и другое: человеку принесли камень в обоих случаях.
    /// </summary>
    public IReadOnlyCollection<string> StoneDrops()
    {
        if (stoneDrops is { } ready)
            return ready;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string mask in StoneMasks)
            foreach (string code in ctx.World.SearchBlockCodes(mask, int.MaxValue))
            {
                if (!IsStoneBlock(code))
                    continue;
                set.Add(code);
                if (ctx.Gathering is { } gathering)
                    foreach (string drop in gathering.DropsOf(code))
                        set.Add(drop);
            }

        // Пустое НЕ запоминаем: реестр мог ещё не прийти, и запомнить пустоту
        // значило бы навсегда решить, что камня в этом мире не бывает
        if (set.Count == 0)
        {
            OnLog?.Invoke("в реестре сервера не нашлось ни одной породы " +
                          $"({string.Join(", ", StoneMasks)}) — считать камень нечем");
            return set;
        }
        stoneDrops = set;
        return set;
    }

    /// <summary>Сколько из добытого — камень (по выпадениям из реестра).</summary>
    public int CountStone(IReadOnlyDictionary<string, int> gained)
    {
        var drops = StoneDrops();
        int total = 0;
        foreach (var (code, n) in gained)
            if (drops.Contains(code))
                total += n;
        return total;
    }

    /// <summary>
    /// В ЧЁМ СЧИТАТЬ МЕТАЛЛ: штуками того, что выпало, единицами металла игры
    /// или слитками. ПОЛИТИКА РОЛИ — механизм только считает.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ради которого настройка заведена (заказчик, дословно):
    /// «я пишу добудь 20 меди, а он добывает 20 кусков самородков, а не
    /// самородной меди, он не смотрит сколько и всего». И правда: журнал 16.08,
    /// 12:56:28 — «медь: принёс 29 из 30», а в сумке при этом
    /// «ore-poor-nativecopper-andesite ×24, ore-poor-nativecopper-peridotite ×5».
    /// Двадцать девять ШТУК бедной руды — это 435 единиц металла, то есть
    /// четыре слитка с хвостиком. Заказ «тридцать меди» бот считал выполненным
    /// на 97%, а человек получил меньше пяти слитков.
    ///
    /// Умолчание — ШТУКИ, и это нарочно: библиотека не решает за роль, что
    /// значит «двадцать». Копатель ставит сюда слитки (см. ручку
    /// «ВЧёмСчитатьМеталл»), и он же говорит об этом вслух до выхода.
    /// </summary>
    public MetalCount CountIn { get; set; } = MetalCount.Штуки;

    /// <summary>
    /// Ответ реестра «есть ли в этом слове металл» — спрашивается один раз на
    /// слово. Запоминается ИМЕННО ОТВЕТ РЕЕСТРА, а не готовая единица счёта:
    /// ручку «ВЧёмСчитатьМеталл» человек крутит на живом боте, и запомни мы
    /// единицу — поворот ползунка не менял бы ничего до перезапуска. Молча.
    /// </summary>
    private readonly Dictionary<string, bool> metalByOrder = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// В ЧЁМ НА САМОМ ДЕЛЕ СЧИТАЕТСЯ ЭТОТ ЗАКАЗ. Одно место на всех: считает
    /// <see cref="CountFor"/>, а надписывает число отчёт наряда — и разойдись
    /// они, под числом в штуках стояло бы слово «слитков».
    ///
    /// Уголь, кварц и самоцветы идут тем же путём, что и металлы (все они
    /// «ore-*»), а единиц металла у них нет вовсе: кварц по ассетам плавится в
    /// СТЕКЛО. Для них честный ответ — штуки, иначе «наряд на 20 угля» не
    /// кончился бы никогда.
    /// </summary>
    public MetalCount UnitFor(string wanted)
    {
        if (IsStoneOrder(wanted) || CountIn == MetalCount.Штуки || ctx.Metal is not { } metals)
            return MetalCount.Штуки;
        if (metalByOrder.TryGetValue(wanted, out bool было))
            return было ? CountIn : MetalCount.Штуки;
        // Реестр ещё не пришёл — НЕ запоминаем: иначе бот на всю сессию решит,
        // что меди в этом мире не бывает (та же охрана, что и у StoneDrops)
        if (!metals.Ready)
            return MetalCount.Штуки;
        bool metal = metals.PlanFor(wanted, 1, CountIn).IsMetal;
        metalByOrder[wanted] = metal;
        return metal ? CountIn : MetalCount.Штуки;
    }

    /// <summary>
    /// Сколько из добытого идёт в зачёт того, что просили: для руды считает
    /// <see cref="CountOf"/> или металл (<see cref="CountIn"/>), для породы —
    /// реестр (<see cref="CountStone"/>). Спрашивать надо ИМЕННО ЭТО: чистая
    /// <see cref="CountOf"/> про камень ничего не знает и честно ответит нулём.
    /// </summary>
    public int CountFor(IReadOnlyDictionary<string, int> gained, string wanted)
    {
        if (IsStoneOrder(wanted))
            return CountStone(gained);
        var unit = UnitFor(wanted);
        if (unit == MetalCount.Штуки || ctx.Metal is not { } metals)
            return CountOf(gained, wanted);
        return MetalRules.FromUnits(metals.Count(gained, code => Mine(code, wanted)).Units, unit);
    }

    /// <summary>
    /// Сколько единиц металла в добытом и что из него бот не опознал. Нужно
    /// отчёту: «принёс 19 из 20» без слова о том, что это слитки, человек
    /// прочтёт как «почти набрал двадцать самородков».
    /// </summary>
    public MetalTally MetalIn(IReadOnlyDictionary<string, int> gained, string wanted) =>
        ctx.Metal is { } metals
            ? metals.Count(gained, code => Mine(code, wanted))
            : new MetalTally(CountOf(gained, wanted), 0, []);

    // ---------------- поиск ----------------

    /// <summary>Что видно вокруг: руда — или простая порода, если спросили о ней.</summary>
    /// <param name="metal">
    /// Часть названия металла или группа («медь»); пусто — любая руда;
    /// «камень» и прочие <see cref="StoneWords"/> — простая порода.
    /// </param>
    public List<OreFind> Find(int radius = 24, string metal = "", int limit = 24)
    {
        if (ctx.Self.Position is not { } me)
            return [];
        var here = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
        bool stone = IsStoneOrder(metal);

        // ЧТО ИЩЕМ — СПРАШИВАЕТСЯ У КАЖДОГО КОДА ПРЯМО ПРИ ПЕРЕБОРЕ, А НЕ ПОСЛЕ
        // НЕГО. Здесь были ДВЕ беды сразу, и обе стоили заказчику живого прогона.
        //
        // ПЕРВАЯ: отбор шёл по подстроке в коде блока (c.Contains(metal)), а
        // человек и панель называют металл по-русски. «медь» не встречается ни
        // в одном коде игры, поэтому Find(32, "медь") честно возвращал ПУСТО —
        // и «!руда медь» в чате отвечал «руды на виду нет», стоя на жиле.
        // Из-за этого все зовущие приучились передавать пустое слово и отбирать
        // сами, чем родилась вторая беда.
        //
        // ВТОРАЯ, и это ровно жалоба «медь не нашёл с читами, я проверил, рядом
        // с домом есть»: перебор кольцами останавливается, набрав limit*4 клеток,
        // и с пустым словом этот запас забирает СЕБЕ ЛЮБАЯ ближняя руда. Пока
        // чит выключен, беда не видна — руда с открытой гранью редка, запас не
        // выбирается никогда. С читом бот видит руду ВНУТРИ камня, и одна
        // кварцевая жила под ногами (ore-quartz-* — тоже «ore-») съедает весь
        // запас в первом же кольце. Медь в двадцати блоках до отбора не доходила
        // вовсе: включённый чит не «не помогал» — он ЛОМАЛ поиск.
        //
        // Правило одно и то же, что у отбора после (<see cref="SuitsCode"/>): им
        // же меряет память встреченного, и разойтись двум копиям тут нельзя.
        //
        // ОТВЕТ ПРО КОД ЗАПОМИНАЕТСЯ НА ВРЕМЯ ОДНОГО ОСМОТРА. Перебор шара
        // радиусом 48 — это около миллиона вопросов, а разных кодов среди них
        // десятки. Без памятки SuitsCode на каждый блок руды звал бы Describe,
        // а тот режет код на части (code.Split) — то есть выбрасывал бы мусор
        // сборщику миллионами строк на один взгляд. Дешёвая проверка «это
        // вообще руда?» стоит перед памяткой нарочно: обычная клетка мира —
        // камень или воздух, и ей быстрее сравнить четыре буквы, чем хэш.
        var решено = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool ГодитсяРуда(string code)
        {
            if (решено.TryGetValue(code, out bool было))
                return было;
            bool да = Suits(code, metal);
            решено[code] = да;
            return да;
        }

        // ЧУЖОЙ МЕТАЛЛ ПО ДОРОГЕ — В ПАМЯТЬ, НО НЕ В ОТВЕТ.
        //
        // Отбор по слову спас выборку (ближняя кварцевая жила больше не съедает
        // весь предел), но заодно ОСЛЕПИЛ ПАМЯТЬ: чужая руда отсеивалась ДО
        // того, как находка станет находкой, и до Remember не доходила вовсе.
        // А память — не только про наряд: из неё же берутся метки руды на карте
        // в окне управления. Получалось «сходил за медью — на карте одна медь»,
        // и олово, мимо которого бот прошёл вплотную, не появлялось никогда.
        //
        // Помним ПО НЕСКОЛЬКУ КЛЕТОК НА ВИД, а не всё подряд: жила в игре — это
        // сотня блоков, и с читом одна такая вытеснила бы из памяти всё редкое,
        // ради чего память и заводилась. Человеку на карте нужна метка залежи,
        // а не её обвод по клеткам.
        // ЗАПАС СЧИТАЕТСЯ ПО ЗАПИСЯМ, А НЕ ПО ПОПЫТКАМ, и это не мелочь.
        // Перебор колец предлагает клетки подряд, и почти все они ЗАМУРОВАНЫ:
        // честный бот (чит выключен — умолчание) такую клетку не видел и в
        // память её не кладёт (ResourceMemory.Judge → NotSeen). Пока счётчик
        // рос ДО записи, три оловянные клетки внутри камня съедали весь запас,
        // записей выходило ноль, и единственная клетка олова с ОТКРЫТОЙ гранью
        // — та самая, которую честно и можно запомнить, — до памяти уже не
        // доходила. На карте в окне управления попутного олова не появлялось
        // вовсе, ради чего память и заводилась
        var попутно = new Dictionary<string, int>(StringComparer.Ordinal);
        void ЗапомнитьПопутно(BlockPos pos, string code)
        {
            if (Memory == null)
                return;
            попутно.TryGetValue(code, out int было);
            if (было >= PassingMemoryPerKind)
                return;
            double dx = pos.X - here.X, dy = pos.Y - here.Y, dz = pos.Z - here.Z;
            // Спрашивать «а видно ли её» приходится у каждой чужой клетки, пока
            // запас не набран, — это шесть вопросов к соседям (IsExposed), то
            // есть доли микросекунды при миллионе клеток самого перебора
            if (Remember(pos, code, Math.Sqrt(dx * dx + dy * dy + dz * dz))
                is Remembered.Written or Remembered.Refreshed)
                попутно[code] = было + 1;
        }

        bool Подходит(BlockPos pos, string code)
        {
            if (stone)
                return IsStoneBlock(code);
            if (!IsOre(code))
                return false;
            if (metal.Length == 0 || ГодитсяРуда(code))
                return true;
            ЗапомнитьПопутно(pos, code);
            return false;
        }

        var found = new List<OreFind>();
        foreach (var pos in ctx.World.FindBlocks(Подходит,
                     here, radius, height: Math.Min(radius, Math.Max(1, MaxLookHeight)),
                     limit: limit * 4, throughWalls: SeeOreThroughStone))
        {
            if (ctx.World.GetBlockCode(pos) is not { } code)
                continue;
            var (m, rich) = Describe(code);
            double dx = pos.X - here.X, dy = pos.Y - here.Y, dz = pos.Z - here.Z;
            double away = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            // ЧТО ВИДЕЛ — ТО И ПОМНЮ. Записывается здесь, а не у того, кто
            // добывает: мимо руды бот проходит куда чаще, чем берёт её
            Remember(pos, code, away);

            found.Add(new OreFind(pos, code, m, rich, away));
            if (found.Count >= limit)
                break;
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found;
    }

    /// <summary>
    /// ОГЛЯДЕТЬСЯ И ЗАПОМНИТЬ: пройтись глазами по округе и сложить в память
    /// то, что видно, — не собираясь ничего добывать прямо сейчас.
    ///
    /// Это то самое «шёл мимо и заметил». Возвращает, сколько клеток прибавилось
    /// в памяти (0 — вокруг ничего нет либо память не подключена).
    /// </summary>
    /// <param name="withStone">
    /// Запоминать и простую породу. По умолчанию нет, и это не забывчивость:
    /// камня вокруг бесконечно много, и он вытеснил бы из памяти редкое — ту
    /// самую медь, ради которой память и заводилась. Породу стоит помнить в
    /// одном случае: когда за ней и пришли (тогда её запишет сам поиск камня).
    /// </param>
    public int LookAround(int radius = 24, int limit = 64, bool withStone = false)
    {
        if (Memory == null)
        {
            OnLog?.Invoke("память ресурсов не подключена — запоминать некуда");
            return 0;
        }
        int was = Memory.Count;
        Find(radius, "", limit);
        if (withStone)
            Find(radius, "камень", limit);
        return Memory.Count - was;
    }

    /// <summary>
    /// Записать увиденное в память — вместе с честным признаком «видел ли».
    /// Возвращает ИТОГ памяти: по нему считается запас попутных записей, а
    /// «попытался» и «записалось» — разные вещи (см. ЗапомнитьПопутно).
    /// </summary>
    private Remembered Remember(BlockPos pos, string code, double distance)
    {
        if (Memory is not { } memory)
            return Remembered.NotSeen;
        return memory.Remember(pos, code, ctx.World.IsExposed(pos.X, pos.Y, pos.Z), distance,
            ctx.Cheats.On(ctx.Cheats.SeeThroughWalls, nameof(Cheats.SeeThroughWalls)));
    }

    /// <summary>
    /// СХОДИТЬ ТУДА, ГДЕ УЖЕ ВИДЕЛ. Ближайшее место из памяти, проверка на
    /// месте и выработка залежи.
    ///
    /// Ради этого память и заводилась: наряд, не найдя ничего на виду, раньше
    /// уходил в разведку заново — по тем же местам, мимо которых уже проходил.
    /// </summary>
    public async Task<VeinResult> MineRememberedAsync(string wanted, CancellationToken ct = default)
    {
        var nothing = new Dictionary<string, int>();
        if (Memory is not { } memory)
            return new VeinResult(0, 0, nothing, "память ресурсов не подключена");
        if (Feet is not { } here)
            return new VeinResult(0, 0, nothing, "не знаю, где стою");

        // Читу — своё: с включённым зрением сквозь стены бот вправе вспоминать
        // и то, что подсмотрел, но по умолчанию память отдаёт только виденное
        bool cheating = ctx.Cheats.On(ctx.Cheats.SeeThroughWalls, nameof(Cheats.SeeThroughWalls));
        var помню = memory.Recall(wanted, here, limit: 8, onlyHonest: !cheating);
        string что = wanted.Length > 0 ? wanted : "ничего";
        if (помню.Count == 0)
            return new VeinResult(0, 0, nothing, $"{что} я и не помню, где видел");

        // ЧУЖОЕ СПРАШИВАЕТСЯ И У ПАМЯТИ. Записи копятся, пока бот ходит по
        // миру, — в том числе мимо соседского огорода. Идти по такой записи
        // значит своими ногами вернуться туда, откуда роль велела держаться
        int уЧужого = 0;
        if (ctx.Claims is { } owners)
        {
            int было = помню.Count;
            помню = помню.Where(s => owners.Suits(s.Pos)).ToList();
            уЧужого = было - помню.Count;
            if (помню.Count == 0)
                return new VeinResult(0, 0, nothing,
                    $"всё запомненное про {что} ({уЧужого} кл.) лежит у чужого — не пойду");
        }

        // К одной и той же недосягаемой клетке второй раз не ходим
        if (помню.FirstOrDefault(s => !couldNotReach.Contains(s.Pos)) is not { Code.Length: > 0 } spot)
            return new VeinResult(0, 0, nothing,
                $"всё запомненное про {что} ({помню.Count} кл.) отсюда недосягаемо" +
                (уЧужого > 0 ? $"; ещё {уЧужого} кл. лежит у чужого" : ""));

        OnLog?.Invoke($"помню {spot} — {ResourceMemory.DistanceBetween(spot.Pos, here):0} бл, " +
                      $"{ResourceMemory.AgeWords(spot.AgeHours(memory.NowHours))}; иду туда");
        if (!await ReachAsync(spot.Pos, ct))
        {
            couldNotReach.Add(spot.Pos);
            return new VeinResult(0, 0, nothing, $"до запомненного {spot.Pos} не подобраться");
        }

        // МИР ЖИВЁТ БЕЗ НАС: пока шли, жилу мог выработать сосед
        string? now = ctx.World.GetBlockCode(spot.Pos);
        if (now == null || !string.Equals(now, spot.Code, StringComparison.OrdinalIgnoreCase))
        {
            memory.Refute(spot.Pos, now);
            return new VeinResult(0, 0, nothing,
                $"в {spot.Pos} помнил {spot.Code}, а там {now ?? "пусто"} — забыл");
        }

        return await MineVeinAsync(spot.Pos, ct);
    }

    /// <summary>
    /// Сколько лишних блоков не жалко сломать, чтобы дотянуться до ОДНОЙ клетки
    /// жилы.
    ///
    /// Живьём это стоило целой добычи: руда лежала в стене за куском сланца,
    /// бот честно докладывал «нет прямой видимости: мешает rock-shale», отходил,
    /// подходил снова — и так минутами, между двумя клетками, пока его не
    /// остановили. Живой игрок в этом месте просто бьёт мешающий камень.
    /// </summary>
    public int MaxDigThrough { get; set; } = 8;

    /// <summary>
    /// На сколько слоёв камня ОКАПЫВАТЬ жилу, когда видимая руда кончилась.
    ///
    /// Жила в игре — это клякса внутри породы, и соседние куски руды часто
    /// разделены одним-двумя камнями. Обход «по соседям» их не видит и уходит,
    /// оставив половину залежи в стене. Игрок в этом месте обкапывает выработку
    /// по кругу и добирает остальное — ровно это и делается здесь.
    /// </summary>
    public int DigOutMargin { get; set; } = 1;

    /// <summary>
    /// СКОЛЬКО БЛОКОВ НЕ ЖАЛКО СНЯТЬ, ОКАПЫВАЯ ОДНУ ЖИЛУ, — сколько бы кругов
    /// окапывание ни сделало.
    ///
    /// ЖИВОЙ СЛУЧАЙ 20.08, из-за которого число перестало быть внутренним.
    /// Наряд «stone-meteorite-iron ×3»: бот выломал ЧЕТЫРЕ клетки метеорита и
    /// получил пятнадцать кусков — втрое больше заказанного. После этого
    /// видимая руда кончилась, и дальше в журнале:
    ///     [руда] жила на виду кончилась — окапываю 41 камней
    ///     [руда] окопал 41 камней — продолжения жилы нет
    ///     [руда] выломано 4 бл руды; получено: soil-medium-none x43,
    ///            stone-meteorite-iron x15 (пробито 1 камней, окопано 41)
    /// Сорок один блок ради нуля новой руды — и не камня, а ЗЕМЛИ: метеорит
    /// лежал у поверхности, и оболочкой выработки оказался грунт. Заказчик
    /// увидел это как «зачем столько лишней земли вокруг руды».
    ///
    /// ПОЧЕМУ ЧИСЛО РАЗДУВАЛОСЬ. Оболочку строят ВСЕ 26 соседей каждой добытой
    /// клетки (<see cref="Around"/>), то есть четыре клетки жилы дают под
    /// шесть десятков кандидатов — прежний потолок 64 не отсекал почти ничего.
    /// Сам потолок при этом до роли НЕ ДОЕЗЖАЛ: в панели стояли «ОкапыватьЖилу»
    /// (да/нет) и «ПробиватьсяЧерез» (а это другая механика — пробить помеху к
    /// ВИДИМОЙ клетке, <see cref="MaxDigThrough"/>), и человеку было нечем
    /// сказать «столько земли ради жилы не копай». Теперь есть — ручка роли
    /// «ОкапыватьНеБольше».
    ///
    /// Заводское число остаётся мерой библиотеки «сколько бы я снял, если меня
    /// не спросили»; соразмерность выбирает роль.
    /// </summary>
    public int MaxMarginBlocks { get; set; } = 64;

    /// <summary>
    /// Сколько клеток подряд можно не суметь достать, прежде чем бросить жилу.
    ///
    /// Предохранитель от того, что заказчик увидел живьём: жила из двадцати
    /// клеток, до каждой не дотянуться, и на каждую бот честно ходил туда-сюда
    /// по десять секунд. Со стороны это ровно «стоит и тупит», хотя в журнале
    /// шла бурная деятельность. Если подряд не вышло несколько клеток — дело
    /// не в клетках, а в том, что к этой жиле отсюда не подобраться.
    /// </summary>
    public int MaxUnreachableInRow { get; set; } = 3;

    /// <summary>
    /// Выработать залежь от указанного блока: идём по прилегающей руде того же
    /// металла, пока она есть и пока хватает сил, а потом окапываем выработку,
    /// чтобы добрать то, что пряталось за камнем.
    ///
    /// От простой породы идём так же — по соседним каменным клеткам. Окапывать
    /// её незачем (вокруг камня камень), а вот за лавой смотреть надо строже:
    /// в жилу упираются редко, а вглубь породы бот уходит целыми кубометрами.
    /// </summary>
    public async Task<VeinResult> MineVeinAsync(BlockPos start, CancellationToken ct = default)
    {
        var before = Snapshot();
        int broken = 0, skipped = 0, margin = 0, dugThrough = 0, unreachable = 0;
        string why = "";

        if (ctx.World.GetBlockCode(start) is not { } startCode)
            return new VeinResult(0, 0, new Dictionary<string, int>(), $"в {start} пусто");

        bool stone = !IsOre(startCode);
        if (stone && !IsStoneBlock(startCode))
            return new VeinResult(0, 0, new Dictionary<string, int>(),
                $"{startCode} — ни руда, ни порода");

        string metal = Describe(startCode).Metal;

        // ЧТО СЧИТАТЬ ПРОДОЛЖЕНИЕМ ЗАЛЕЖИ. У руды это тот же металл: соседний
        // касситерит в жиле меди трогать незачем. У породы — любая порода:
        // в шахте гранит переходит в андезит, и человек не бросает забой из-за
        // того, что камень сменился
        bool SameKind(string code) => stone
            ? IsStoneBlock(code)
            : IsOre(code) && Describe(code).Metal == metal;

        OnLog?.Invoke(stone
            ? $"беру породу от {start} ({startCode})"
            : $"иду по жиле {metal} от {start}");

        var seen = new HashSet<BlockPos> { start };
        Queue<BlockPos> queue = new();
        var mined = new List<BlockPos>();
        queue.Enqueue(start);

        bool InSpread(BlockPos p) =>
            Math.Abs(p.X - start.X) <= VeinSpread &&
            Math.Abs(p.Y - start.Y) <= VeinSpread &&
            Math.Abs(p.Z - start.Z) <= VeinSpread;

        void Notice(BlockPos cell)
        {
            foreach (var next in Around(cell))
            {
                if (!seen.Add(next))
                    continue;
                if (InSpread(next))
                    queue.Enqueue(next);
            }
        }

        while (!ct.IsCancellationRequested)
        {
            // --- 1. Всё, что видно как руда нужного металла ---
            while (queue.Count > 0 && broken < MaxVeinBlocks && !ct.IsCancellationRequested)
            {
                // ПОРЯДОК ОБХОДА — один на всех и живёт в добыче (Mining.NextCell):
                // ближайшая к ногам, начатую кучу не бросаем. Простая очередь
                // уводила бота вширь по фронту жилы: он прыгал между блоками,
                // которые даже не соседи, и каждый раз шёл к ним заново
                var pending = queue.ToList();
                var cell = ctx.Mining.NextCellFromHere(mined.Count > 0 ? mined[^1] : null, pending)
                           ?? pending[0];
                queue = new Queue<BlockPos>(pending.Where(p => p != cell));

                if (ctx.World.GetBlockCode(cell) is not { } code || !SameKind(code))
                    continue;

                // ЗА КАМНЕМ МОЖЕТ СТОЯТЬ ЛАВА. Для руды это редкий случай, а
                // копая породу вглубь, в неё упираются всерьёз: вскрыл — залило
                if (stone && ctx.World.LiquidNear(cell, water: false) is { } hot)
                {
                    OnLog?.Invoke($"{cell} не трогаю: рядом лава в {hot.At}");
                    skipped++;
                    continue;
                }

                var result = await ctx.Mining.BreakAsync(cell, ct);

                // НЕ ДОТЯНУТЬСЯ — не повод бросать: обычно мешает один камень,
                // и его видно. Раньше клетка просто уходила в пропуск, а бот
                // оставался ходить кругами
                if (!result.Success && result.Why == MiningRefusal.OutOfReach &&
                    dugThrough < MaxDigThrough)
                {
                    int spent = await ClearWayToAsync(cell, MaxDigThrough - dugThrough, ct);
                    dugThrough += spent;
                    if (spent > 0)
                        result = await ctx.Mining.BreakAsync(cell, ct);
                }

                if (!result.Success)
                {
                    skipped++;
                    // Нечем брать — дальше по жиле будет то же самое
                    if (result.Why == MiningRefusal.NeedBetterTool)
                    {
                        why = result.Message ?? "нужен инструмент выше тиром";
                        queue.Clear();
                        break;
                    }
                    // Не достать несколько клеток подряд — значит не достать
                    // жилу отсюда вовсе. Ходить к каждой по очереди бессмысленно
                    if (result.Why == MiningRefusal.OutOfReach &&
                        ++unreachable >= MaxUnreachableInRow)
                    {
                        why = $"до жилы отсюда не подобраться ({unreachable} клетки подряд)";
                        queue.Clear();
                        break;
                    }
                    continue;
                }
                unreachable = 0;
                broken++;
                mined.Add(cell);

                // ЧТО ВЫКОПАЛ — ТОГО БОЛЬШЕ НЕТ. Иначе бот пришёл бы сюда
                // второй раз по собственной же памяти
                Memory?.Refute(cell, ctx.World.GetBlockCode(cell));

                // Вскрыли клетку — теперь видны соседи, как и человеку
                Notice(cell);

                // Добычу подбираем по ходу: она падает под ноги
                if (broken % 6 == 0)
                    await ctx.Pickup.CollectAsync(5, maxSeconds: 4, ct: ct, takeBody: false);
            }

            if (why.Length > 0 || broken >= MaxVeinBlocks || ct.IsCancellationRequested)
                break;

            // --- 2. Окапывание: снять камень вокруг выработки и посмотреть,
            //        не открылось ли продолжение жилы ---
            //
            // Породу не окапывают: вокруг камня стоит камень, и «слой камня
            // ради того, что за ним» превратился бы в бесконечную выработку
            if (stone || DigOutMargin <= 0 || margin >= MaxMarginBlocks || mined.Count == 0)
                break;

            int opened = await DigOutAsync(mined, metal, seen, queue,
                MaxMarginBlocks - margin, ct);
            margin += opened;
            if (queue.Count == 0)
                break;   // окапывание ничего не открыло — жила кончилась честно
        }

        // ВЫБРАТЬСЯ ИЗ СВОЕЙ ЖЕ ЯМЫ — до подбора, а не после: добыча падает
        // и наверх, к устью, а из ямы её не достать. Заказчик увидел это
        // живьём: «не мог вылезти из ямы и лез вверх-вниз»
        await ClimbOutToAsync(start.Y, ct);

        await ctx.Pickup.CollectAsync(8, maxSeconds: 10, ct: ct, takeBody: false);
        var gained = Diff(before, Snapshot());
        var report = new VeinResult(broken, skipped, gained,
            why + (dugThrough > 0 ? $"{(why.Length > 0 ? "; " : "")}пробито {dugThrough} камней" : "") +
            (margin > 0 ? $"{(why.Length > 0 || dugThrough > 0 ? ", " : "")}окопано {margin}" : ""));
        OnLog?.Invoke(report.ToString());
        return report;
    }

    /// <summary>
    /// Пробиться взглядом к клетке: сломать то, что её заслоняет.
    /// Возвращает, сколько блоков пришлось сломать (0 — не вышло).
    ///
    /// Ломаем только ТО, ЧТО МЕШАЕТ, и только пока до него самого дотягиваемся:
    /// это подкоп в одну клетку, а не право рушить стены на расстоянии.
    /// </summary>
    private async Task<int> ClearWayToAsync(BlockPos cell, int budget, CancellationToken ct)
    {
        int spent = 0;
        while (spent < budget && !ct.IsCancellationRequested)
        {
            if (ctx.Hands.InReach(cell))
                return spent;

            if (ctx.Hands.Look(cell).BlockedBy is not { } wall || wall == cell)
                return spent;   // мешает не блок, а расстояние — киркой не помочь
            if (!ctx.Hands.InReach(wall))
                return spent;   // до самой помехи не достать: надо подходить, а не копать

            // За камнем может стоять лава: вскрывать вслепую нельзя
            if (ctx.World.LiquidNear(wall, water: false) is { } danger)
            {
                OnLog?.Invoke($"{wall} не трогаю: рядом лава в {danger.At}");
                return spent;
            }

            OnLog?.Invoke($"до {cell} мешает {ctx.World.GetBlockCode(wall)} в {wall} — убираю");
            if (!(await ctx.Mining.BreakAsync(wall, ct)).Success)
                return spent;
            spent++;
        }
        return spent;
    }

    /// <summary>
    /// Окопать выработку: снять слой камня вокруг уже добытых клеток и
    /// поставить в очередь руду, которая под ним открылась.
    /// Возвращает, сколько камня сломано.
    /// </summary>
    private async Task<int> DigOutAsync(List<BlockPos> mined, string metal,
        HashSet<BlockPos> seen, Queue<BlockPos> queue, int budget, CancellationToken ct)
    {
        // Оболочка выработки: соседи добытых клеток, которые ещё камень.
        // Ближние к нам первыми — так меньше беготни
        var shell = new HashSet<BlockPos>();
        foreach (var cell in mined)
            foreach (var near in Around(cell))
            {
                if (!ctx.World.IsSolid(near.X, near.Y, near.Z))
                    continue;
                // Приставка «ore-» стояла здесь ВТОРОЙ КОПИЕЙ правила «руда ли
                // это», и метеорит она пропускала мимо так же, как первая
                if (IsOre(ctx.World.GetBlockCode(near)))
                    continue;   // это руда, её возьмёт обход, а не окапывание
                shell.Add(near);
            }

        var wall = shell.OrderBy(p => ctx.Hands.DistanceTo(p)).Take(budget).ToList();
        if (wall.Count == 0)
            return 0;
        // «КАМНЕЙ» ЗДЕСЬ БЫЛО НЕПРАВДОЙ. Оболочка — это ЛЮБОЙ твёрдый сосед,
        // не руда: у метеорита на поверхности (живой случай 20.08) в ней стоял
        // грунт, и журнал докладывал «окапываю 41 камней», а в сумку легло
        // «soil-medium-none x43». Человек по такой строке ищет камень, которого
        // не было
        OnLog?.Invoke($"жила на виду кончилась — окапываю {wall.Count} блоков вокруг " +
                      $"(предел {MaxMarginBlocks})");

        // ПОРЯДОК «ЧЕРЕЗ ОДНУ» — У САМОГО УДАРА, И ОКАПЫВАНИЕ ЕГО ТЕПЕРЬ
        // СПРАШИВАЕТ. Слова заказчика (03.09): «копать шахматкой я имел в виду
        // ВСЕГДА, ДЛЯ ЛЮБЫХ РАБОТ». До этой волны ручка
        // «ШахматкойРадиЦельныхБлоков» доезжала до ямы карьера и НЕ доезжала
        // сюда: человек включал её и получал целые блоки в яме, а с окапывания
        // жилы — щебень, как и раньше.
        //
        // ЗДЕСЬ ЕЙ ЕСТЬ ЧТО ДЕЛАТЬ, И ЭТО НЕ ОЧЕВИДНО. Оболочку строят ВСЕ 26
        // соседей каждой добытой клетки (Ores.Around), то есть она не
        // плёнка в одну клетку, а тело с толщиной: в складках выработки
        // попадаются камни, у которых все шесть соседей — либо уже вынутая
        // руда, либо та же оболочка. Такой камень и выпадет ЦЕЛЫМ, если снять
        // его последним. Шахматка это видит сама и ни одной клетки из списка не
        // теряет: меняется только очерёдность (Mining.Очередь).
        //
        // ПОРЯДОК «БЛИЖНИЕ ПЕРВЫМИ» СОХРАНЯЕТСЯ ДОСЛОВНО внутри каждой
        // половины — он выстрадан живым случаем «жила из двадцати клеток, и на
        // каждую бот ходил туда-сюда по десять секунд».
        //
        // НИЧЕГО НЕ ОБЕЩАЕМ ЗАРАНЕЕ (закон 4): сколько вышло целыми, скажет
        // сумка — прибавка считается по ней (Diff/Snapshot), и целый блок
        // виден там своим кодом (rock-granite), а щебень своим (stone-granite).
        int broke = 0, found = 0;
        foreach (var stone in ctx.Mining.Очередь(wall))
        {
            if (ct.IsCancellationRequested)
                break;
            // За камнем может стоять лава: вскрывать вслепую нельзя
            if (ctx.World.LiquidNear(stone, water: false) != null)
                continue;
            if (!(await ctx.Mining.BreakAsync(stone, ct)).Success)
                continue;
            broke++;

            // Что открылось за камнем — проверяем сразу
            foreach (var next in Around(stone))
            {
                if (!seen.Add(next))
                    continue;
                if (ctx.World.GetBlockCode(next) is { } code && IsOre(code) &&
                    Describe(code).Metal == metal)
                {
                    queue.Enqueue(next);
                    found++;
                }
            }
        }

        OnLog?.Invoke(found > 0
            ? $"окопал {broke} блоков — открылось ещё {found} кл. {metal}"
            : $"окопал {broke} блоков — продолжения жилы нет");
        return broke;
    }

    /// <summary>Найти ближайшую видимую руду и выработать её жилу.</summary>
    public async Task<VeinResult> MineNearestAsync(int radius = 24, string metal = "",
        CancellationToken ct = default)
    {
        if (Find(radius, metal, limit: 1) is not [var first, ..])
            return new VeinResult(0, 0, new Dictionary<string, int>(),
                metal.Length > 0 ? $"{metal} на виду нет" : "руды на виду нет");

        OnLog?.Invoke($"вижу {first}");
        if (!await ReachAsync(first.Pos, ct))
            OnLog?.Invoke("вплотную не подошёл — попробую ломать с той дистанции, что есть");
        return await MineVeinAsync(first.Pos, ct);
    }

    /// <summary>
    /// Разрешено ли прокапываться к руде, до которой не дойти ногами.
    ///
    /// Это самая частая беда рудокопа, и раньше она кончалась ничем: руда
    /// видна в стене, дороги к ней нет, бот честно докладывал «ближе 1 бл не
    /// подобраться» — и вставал. Живой игрок в этом месте берёт кирку.
    /// </summary>
    public bool MayTunnel { get; set; } = true;

    /// <summary>Сколько клеток хода бот согласен прокопать ради одной жилы.</summary>
    public int TunnelBudget { get; set; } = 48;

    /// <summary>
    /// Подобраться к руде: сперва ногами, а куда ногами нельзя — прокопаться.
    /// Успех — это «до неё можно дотянуться рукой», а не «я где-то рядом».
    /// </summary>
    public async Task<bool> ReachAsync(BlockPos ore, CancellationToken ct = default)
    {
        if (ctx.Hands.InReach(ore))
            return true;

        // 1. Ногами. Самый дешёвый способ, и чаще всего его хватает
        await ctx.Movement.TravelToAsync(ore, 120, ct);
        if (ctx.Hands.InReach(ore))
            return true;

        // 1б. ВЫБРАТЬСЯ. Выработка уводит вниз, и цель часто оказывается НАД
        // ямой, которую бот сам себе и выкопал. Живьём это выглядело так: он
        // метался внизу, строя маршрут наверх по восемь раз за пять секунд.
        // Прыжок берёт один блок, а яма глубже — значит нужен столб
        if (await ClimbOutToAsync(ore.Y, ct) && ctx.Hands.InReach(ore))
            return true;

        if (!MayTunnel || ctx.Tunnel == null)
            return false;

        // 2. Киркой. Ход ведём ДО руды, но саму жилу не трогаем: её возьмёт
        // тот, кто умеет идти по соседям и не разбивать её вслепую
        OnLog?.Invoke($"до {ore} пешком не подобраться — прокапываюсь");
        int wasSteps = ctx.Tunnel.MaxSteps;
        bool wasStop = ctx.Tunnel.StopWhenInReach;
        bool wasBreak = ctx.Tunnel.BreakTarget;
        try
        {
            ctx.Tunnel.MaxSteps = TunnelBudget;
            ctx.Tunnel.StopWhenInReach = true;
            ctx.Tunnel.BreakTarget = false;
            var dug = await ctx.Tunnel.DigToAsync(ore, ct);
            if (!dug.Success)
                OnLog?.Invoke($"ход не дошёл: {dug.Reason}");
        }
        finally
        {
            ctx.Tunnel.MaxSteps = wasSteps;
            ctx.Tunnel.StopWhenInReach = wasStop;
            ctx.Tunnel.BreakTarget = wasBreak;
        }
        return ctx.Hands.InReach(ore);
    }

    /// <summary>
    /// НАРЯД: добывать, пока не наберём столько-то.
    ///
    /// Ровно то, чего просят от рудокопа словами: «принеси двадцать меди».
    /// Считаем не удары киркой и не выломанные блоки, а ПРИБАВКУ В СУМКЕ —
    /// это единственное число, которое человек может проверить сам.
    ///
    /// Название металла принимается и по-русски, и по-игровому: «медь» и
    /// «nativecopper» ведут к одному и тому же (см. <see cref="OreCode"/>) —
    /// игроку всё равно, какая именно медь ему попалась.
    /// </summary>
    /// <param name="metal">Металл, группа или порода: «медь», «олово», «cassiterite», «камень».</param>
    /// <param name="count">Сколько единиц набрать.</param>
    /// <param name="radius">В каком радиусе искать выходы руды.</param>
    public async Task<VeinResult> MineUntilAsync(string metal, int count, int radius = 32,
        CancellationToken ct = default)
    {
        if (count <= 0)
            return new VeinResult(0, 0, new Dictionary<string, int>(), "количество должно быть больше нуля");

        var before = Snapshot();
        int broken = 0, skipped = 0, veins = 0, dugDown = 0, idle = 0;
        string why = "набрал сколько просили";
        var tried = new HashSet<BlockPos>();
        bool stone = IsStoneOrder(metal);

        while (!ct.IsCancellationRequested)
        {
            int got = CountFor(Diff(before, Snapshot()), metal);
            if (got >= count)
                break;

            // Ищем то, что подходит под названное, — и по игровому коду,
            // и по русской группе, и по слову «камень». Слово передаётся В САМ
            // ПЕРЕБОР, а не отбирается после: с пустым словом запас выборки
            // забирала ближняя чужая жила (см. Find), и «медь» терялась
            var seen = Find(radius, metal, limit: 32)
                .Where(o => Suits(o.Code, metal) && !tried.Contains(o.Pos))
                .ToList();
            if (seen.Count == 0)
            {
                // ПОРОДА ЕСТЬ ВЕЗДЕ, просто не всегда на виду: на лугу под
                // ногами дёрн, а камень начинается ниже. Живой игрок в этом
                // месте копает вниз, а не разводит руками
                if (stone && dugDown < DigDownToStone)
                {
                    var (went, said) = await DigDownForStoneAsync(dugDown, ct);
                    if (went <= 0)
                    {
                        why = said;
                        break;
                    }
                    dugDown += went;
                    continue;
                }

                why = veins == 0
                    ? $"{metal} на виду нет — надо искать глубже или в другом месте"
                    : $"поблизости {metal} кончился, набрал {got} из {count}";
                break;
            }

            var next = seen[0];
            tried.Add(next.Pos);
            OnLog?.Invoke($"беру {next} (набрано {got} из {count})");

            if (!await ReachAsync(next.Pos, ct))
            {
                skipped++;
                continue;   // не подобраться — эта помечена, идём к следующей
            }

            var vein = await MineVeinAsync(next.Pos, ct);
            broken += vein.Broken;
            skipped += vein.Skipped;
            veins++;

            // Нечем брать — дальше будет ровно то же самое, и бегать
            // по жилам с негодной киркой бессмысленно
            if (vein.Broken == 0 && vein.Why.Length > 0)
            {
                why = vein.Why;
                break;
            }

            // ЛОМАЮ, А В СУМКЕ НЕ ПРИБАВЛЯЕТСЯ. С рудой это почти не встречалось
            // (жила кончалась раньше сумки), а камня вокруг бесконечно много:
            // с полной сумкой бот копал бы породу до самого конца наряда и
            // отчитался бы «принёс 0». Два круга подряд без прибавки — стоп
            if (CountFor(Diff(before, Snapshot()), metal) == got && vein.Broken > 0)
            {
                if (++idle >= 2)
                {
                    why = $"выломал {vein.Broken} бл, а {metal} в сумке не прибавилось — " +
                          "похоже, класть уже некуда";
                    break;
                }
            }
            else
                idle = 0;
        }

        if (ct.IsCancellationRequested)
            why = "отменено";

        var gained = Diff(before, Snapshot());
        int total = CountFor(gained, metal);
        var report = new VeinResult(broken, skipped, gained,
            $"{why}: {metal} ×{total} из {count}, залежей пройдено {veins}" +
            (dugDown > 0 ? $", вниз прокопано {dugDown} кл." : ""));
        OnLog?.Invoke(report.ToString());
        return report;
    }

    /// <summary>
    /// На сколько клеток прокапываться ВНИЗ, если породы не видно вовсе.
    ///
    /// Случай не выдуманный, а самый обычный: бот стоит на лугу, под ногами
    /// дёрн и земля, камень начинается ниже — и «камня на виду нет» было бы
    /// правдой ровно до первого удара киркой. Ноль — не копать, отвечать
    /// отказом.
    /// </summary>
    public int DigDownToStone { get; set; } = 8;

    /// <summary>
    /// ПРОКОПАТЬСЯ ВНИЗ ЗА ПОРОДОЙ — один шаг, не больше четырёх клеток за раз.
    ///
    /// Вынесено сюда потому, что спрашивают об этом ДВОЕ: своя выработка
    /// (<see cref="MineUntilAsync"/>) и наряд (<see cref="MiningOrder"/>).
    /// Пока правило жило только в первой, ручка «КопатьВнизЗаКамнем» была
    /// мертва: наряд до неё не доходил ни при каком значении, и бот на лугу
    /// отвечал «камень на виду нет» с восьмёркой в настройках.
    ///
    /// Шагами по четыре, а не сразу на всю глубину: после каждого шага стоит
    /// заново посмотреть, не показалась ли порода, — иначе бот роет колодец
    /// сквозь уже найденный гранит.
    /// </summary>
    /// <param name="alreadyDug">Сколько уже прокопано вниз за это дело.</param>
    /// <returns>На сколько клеток опустились (0 — не вышло) и почему — словами.</returns>
    public async Task<(int Dug, string Why)> DigDownForStoneAsync(int alreadyDug,
        CancellationToken ct = default)
    {
        int left = DigDownToStone - Math.Max(0, alreadyDug);
        if (DigDownToStone <= 0)
            return (0, "породы на виду нет, а копать вниз за ней не велено " +
                       "(КопатьВнизЗаКамнем = 0)");
        if (left <= 0)
            return (0, $"породы на виду нет; вниз прокопано {alreadyDug} кл. " +
                       $"из дозволенных {DigDownToStone}");
        if (ctx.Mining is not { } mining)
            return (0, "породы на виду нет, а копать вниз нечем: добыча не подключена");

        int step = Math.Min(4, left);
        OnLog?.Invoke($"породы на виду нет — копаю вниз (ещё {step} кл. из {left} дозволенных)");
        int went = await mining.PillarDownAsync(step, ct);
        return went > 0
            ? (went, $"опустился на {went} кл.")
            : (0, "вниз не пробиться, а породы на виду нет");
    }

    /// <summary>Выбираться из собственной выработки, когда нужное оказалось выше.</summary>
    public bool MayClimbOut { get; set; } = true;

    /// <summary>Клетка, в которой стоят ноги.</summary>
    private BlockPos? Feet => ctx.Self.Position is { } p
        ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
        : null;

    /// <summary>
    /// Подняться до нужной высоты столбом или лестницей. Возвращает true,
    /// если что-то построили и поднялись.
    ///
    /// ЗДЕСЬ ТОЛЬКО РАЗРЕШЕНИЕ И СЛОВА. Сам подъём — <see cref="Scaffolding.ClimbOutToAsync"/>,
    /// и он же считает порог «меньше двух блоков — запрыгну сам» и меряет
    /// успех по факту подъёма. Своей копии тут больше нет: она стояла рядом с
    /// такой же копией карьера, и двойной механикой их называли три волны
    /// подряд.
    /// </summary>
    private async Task<bool> ClimbOutToAsync(int wantedY, CancellationToken ct)
    {
        if (!MayClimbOut || ctx.Scaffolding is not { } scaffolding)
            return false;

        var climb = await scaffolding.ClimbOutToAsync(wantedY, () => Feet, ct,
            need => OnLog?.Invoke($"нужное на {need} бл выше — выбираюсь наверх"));
        return climb.Up;
    }

    /// <summary>
    /// ПОДХОДИТ ЛИ БЛОК ПОД ТО, ЧТО НАЗВАЛ ЧЕЛОВЕК, — ЖИВАЯ МЕРКА, СО СПРОСОМ
    /// У РЕЕСТРА. Ею и отбирает всё, что бот ищет на самом деле.
    ///
    /// ЖИВОЙ СЛУЧАЙ (19.08). Человек назвал КОД ВЫПАДЕНИЯ — «stone-meteorite-iron»,
    /// — а в мире стоит БЛОК «meteorite-iron»: общей подстроки у них нет, и
    /// сравнением букв это не решается никак. Ровно та же беда была у камня и
    /// уже решена: с «loosestones-granite-free» падает «stone-granite», и знает
    /// об этом реестр выпадений (<see cref="Gathering.Gives(string, string)"/>). Второй такой
    /// мерки здесь не завожу — зову ту же.
    ///
    /// Чистая половина (имя против имени) осталась в <see cref="SuitsCode"/>:
    /// её задаёт память встреченного, у которой реестра под рукой может и не
    /// быть вовсе.
    /// </summary>
    public bool Suits(string code, string wanted)
    {
        if (wanted.Length == 0)
            return true;
        if (IsStoneOrder(wanted))
            return IsStoneBlock(code);
        return SuitsCode(code, wanted) ||
               (ctx.Gathering is { } gathering && gathering.Gives(code, wanted));
    }

    /// <summary>
    /// ПОДХОДИТ ЛИ БЛОК ПОД ТО, ЧТО НАЗВАЛ ЧЕЛОВЕК, — чистое правило по одному
    /// лишь коду, без мира и без реестра.
    ///
    /// Отдельной функцией потому, что этот же вопрос задаёт память встреченного
    /// (<see cref="ResourceMemory.Choose"/>): она хранит коды, а не находки, и
    /// две копии правила «медь — это ещё и малахит» разошлись бы молча.
    ///
    /// Порода здесь узнаётся ПО СЛОВАРЮ ЧЕЛОВЕКА (<see cref="OreCode.NamesAMineral"/>),
    /// и это ПОСЛЕДНИЙ РУБЕЖ, а не мерка живого бота: у живого на «руда ли это»
    /// отвечает реестр (<see cref="IsOre"/>), и подключённая память спрашивает
    /// именно его — см. <see cref="Memory"/>.
    /// </summary>
    public static bool SuitsCode(string code, string wanted)
    {
        if (wanted.Length == 0)
            return true;
        if (IsStoneOrder(wanted))
            return !OreCode.NamesAMineral(code);
        return code.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
               OreCode.Matches(wanted, Describe(code).Metal);
    }

    /// <summary>
    /// Сколько из добытого относится к названному металлу.
    ///
    /// Считаем по коду предмета: из руды падают самородки и куски, и в их
    /// коде стоит имя минерала («nugget-nativecopper», «ore-poor-cassiterite»).
    /// </summary>
    public static int CountOf(IReadOnlyDictionary<string, int> gained, string metal)
    {
        int total = 0;
        foreach (var (code, n) in gained)
            if (Mine(code, metal))
                total += n;
        return total;
    }

    /// <summary>
    /// ИДЁТ ЛИ ЭТА ВЕЩЬ В ЗАЧЁТ ЗАКАЗА — чистое правило по одному коду.
    ///
    /// Вынесено из <see cref="CountOf"/> потому, что спрашивают об этом ДВОЕ:
    /// счёт штук и счёт МЕТАЛЛА (<see cref="MetalIn"/>). Вторая такая же мерка
    /// «медь — это ещё и малахит» разошлась бы с первой в тот же вечер, и тогда
    /// «принёс 19 из 20» и «в сумке прибавилось …» перестали бы сходиться —
    /// ровно то, на что заказчик и жаловался.
    /// </summary>
    public static bool Mine(string code, string metal) =>
        metal.Length == 0 ||
        code.Contains(metal, StringComparison.OrdinalIgnoreCase) ||
        OreCode.MetalGroups.Any(g =>
            g.Name.Equals(metal, StringComparison.OrdinalIgnoreCase) &&
            g.Minerals.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Металл и богатство из кода блока. Сам разбор — в <see cref="OreCode"/>:
    /// им пользуется ещё и клиентский мод, а писать его дважды значит
    /// разойтись в мелочах.
    /// </summary>
    public static (string Metal, string Richness) Describe(string code) => OreCode.Describe(code);

    private static IEnumerable<BlockPos> Around(BlockPos c)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (dx != 0 || dy != 0 || dz != 0)
                        yield return new BlockPos(c.X + dx, c.Y + dy, c.Z + dz);
    }

    // Что лежит в сумках и что прибавилось — вопросы к своему состоянию,
    // и ответ на них один на всех (см. SelfState)
    private Dictionary<string, int> Snapshot() => ctx.Self.CarrySnapshot();

    private static Dictionary<string, int> Diff(Dictionary<string, int> before,
        Dictionary<string, int> after) => SelfState.Gained(before, after);
}
