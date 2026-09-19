using VsBotKit;

namespace VintageBotStory;

/// <summary>Прилавок: табличка с ценами и сундук под ней.</summary>
public sealed class Stall
{
    public required SignInfo Sign { get; init; }
    public required BlockPos Chest { get; init; }

    /// <summary>Название товара с таблички (строка после «!»).</summary>
    public required string ProductName { get; init; }

    /// <summary>Код предмета в реестре сервера, если удалось опознать.</summary>
    public string? ProductCode { get; set; }

    /// <summary>Цена, по которой МЫ ПРОДАЁМ (строка «buy» на табличке).</summary>
    public int WeSellFor { get; init; }

    /// <summary>Цена, по которой МЫ ПОКУПАЕМ (строка «sell» на табличке).</summary>
    public int WeBuyFor { get; init; }

    public override string ToString() =>
        $"{ProductName} ({ProductCode ?? "код не опознан"}) прод. {WeSellFor} / пок. {WeBuyFor} @ {Chest}";
}

/// <summary>
/// Склад: сундук с табличкой БЕЗ цен — только название товара. Таких на один
/// товар может быть много; из них бот берёт товар на продажу и туда же
/// складывает выкупленное.
/// </summary>
public sealed record Storage(BlockPos Chest, string ProductName, string? ProductCode);

/// <summary>
/// Касса: сундук с деньгами и три порога с её таблички.
/// <para>
/// <b>min</b> — неснижаемый остаток: ниже него бот кассу не тратит;
/// <b>low</b> — порог пополнения: просела ниже — бот идёт за шестернями
/// в резерв («!VAULT»);
/// <b>max</b> — потолок: излишек сверх него бот уносит обратно в резерв,
/// чтобы выручка не лежала на виду в проходном сундуке (0 — без потолка).
/// </para>
/// </summary>
public sealed record CashDesk(BlockPos Chest, int MinReserve, int LowWater, int MaxKeep);

/// <summary>
/// Резерв («!VAULT»): склад валюты, откуда бот пополняет кассу и куда уносит
/// излишек. Keep — сколько шестерён в нём обязательно оставить (строка «min N»).
/// </summary>
public sealed record Vault(BlockPos Chest, int Keep);

/// <summary>Что бот решил сделать с содержимым прилавка.</summary>
public enum DealKind
{
    Nothing,
    BuyFromPlayer,   // в сундуке лежит товар — выкупаем, платим шестернями
    SellToPlayer,    // в сундуке лежат шестерни — это заказ, выдаём товар
    WrongGoods,      // лежит не то, что на табличке
}

/// <summary>Разбор прилавка: что лежит и что с этим делать.</summary>
public sealed record Deal(Stall Stall, DealKind Kind, int Units, int Gears, string? FoundCode)
{
    /// <summary>Сумма сделки в шестернях.</summary>
    public int Price => Kind switch
    {
        DealKind.BuyFromPlayer => Units * Stall.WeBuyFor,
        DealKind.SellToPlayer => Units * Stall.WeSellFor,
        _ => 0,
    };
}

/// <summary>
/// Торговля: разметка магазина табличками и разбор прилавков.
/// <para>
/// Разметка (как на сервере заказчика). Табличка либо висит рядом с сундуком,
/// либо надпись сделана на самом сундуке — работает и так, и так:
/// </para>
/// <code>
/// !STORE                  граница магазина: только внутри него бот трогает сундуки
///
/// !CASH                   КАССА — из неё платим игрокам
/// min 300                 неснижаемый остаток: ниже него касса не тратится
/// low 800                 порог пополнения: просела ниже — донесу из резерва
/// max 2000                потолок: излишек сверх него унесу в резерв
///
/// !VAULT                  РЕЗЕРВ валюты (сундуков может быть несколько)
/// min 0                   сколько шестерён обязательно оставить в резерве
///
/// !GOLD INGOT             ПРИЛАВОК: товар и цены
/// buy 10                  по чём МЫ ПРОДАЁМ игроку
/// sell 5                  по чём МЫ ПОКУПАЕМ у игрока
///
/// !GOLD INGOT             СКЛАД товара: то же имя, но БЕЗ цен
/// </code>
/// <para>
/// Откуда берутся деньги: магазин дохода не создаёт, он их только
/// перекладывает. Поэтому касса — рабочий кошелёк, а запас лежит в резерве,
/// который наполняет хозяин магазина. Бот носит шестерни между ними как
/// обычный игрок и, когда платить нечем, ЧЕСТНО ОСТАНАВЛИВАЕТ ВЫКУП и
/// говорит об этом в чат, а не забирает товар в долг.
/// </para>
/// </summary>
public sealed class TraderShop
{
    private readonly BotContext ctx;
    private readonly Readables read;

    public TraderShop(BotContext ctx, Readables read)
    {
        this.ctx = ctx;
        this.read = read;
        Transfer = new ItemTransfer(ctx);
        Transfer.OnLog += m => OnLog?.Invoke(m);
    }

    public event Action<string>? OnLog;

    /// <summary>Перенос предметов по живому инвентарю.</summary>
    public ItemTransfer Transfer { get; }

    /// <summary>Валюта.</summary>
    public string CurrencyCode { get; set; } = "gear-rusty";

    /// <summary>Радиус магазина от таблички «!STORE».</summary>
    public double StoreRadius { get; set; } = 20;

    /// <summary>Насколько близко к табличке ищем её сундук.</summary>
    public double StallRadius { get; set; } = 2.5;

    /// <summary>
    /// Сколько единиц товара всегда остаётся в сундуке как образец: по нему
    /// бот узнаёт прилавок, даже когда товар кончился.
    /// </summary>
    public int SampleUnits { get; set; } = 1;

    /// <summary>Табличка магазина, если найдена.</summary>
    public SignInfo? StoreSign => read.FindSign("!STORE", 64) is { } s ? s : null;

    /// <summary>
    /// Числовая настройка с таблички: строка вида «min 300». Нет строки —
    /// возвращаем значение по умолчанию, а не выдуманное число.
    /// </summary>
    private static int Setting(SignInfo s, string word, int fallback)
    {
        foreach (var line in s.Lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Equals(word, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(parts[^1], out int n))
                return n;
        }
        return fallback;
    }

    /// <summary>Табличка помечена этой командой («!cash», «!vault»)?</summary>
    private static bool Marked(SignInfo s, string command) => s.Commands.Any(c =>
        c.Equals(command, StringComparison.OrdinalIgnoreCase));

    /// <summary>Служебная разметка — не прилавок и не склад товара.</summary>
    private static bool IsService(string name) =>
        name.Equals("cash", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("vault", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("STORE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Разметка в радиусе: и подписанные сундуки, и таблички рядом с ними.</summary>
    private IEnumerable<(SignInfo Sign, BlockPos? OwnChest)> MarkedSigns()
    {
        foreach (var (pos, _, text) in read.LabeledContainersNear(StoreRadius * 2))
            yield return (new SignInfo(pos, text), pos);
        foreach (var s in read.SignsNear(StoreRadius * 2))
            yield return (s, null);
    }

    /// <summary>
    /// Касса: табличка «!CASH» рядом с сундуком ИЛИ подпись прямо на сундуке.
    /// Пороги — строки «min N» (не тратить ниже), «low N» (ниже — пополнить
    /// из резерва), «max N» (излишек унести в резерв).
    /// </summary>
    public CashDesk? Cash()
    {
        foreach (var (sign, own) in MarkedSigns())
        {
            if (!Marked(sign, "cash"))
                continue;
            var chest = own ?? read.ContainersBySign(sign, StallRadius)
                .Select(c => (BlockPos?)c.Pos).FirstOrDefault();
            if (chest is not { } at)
                continue;
            int min = Setting(sign, "min", 0);
            // Порога пополнения нет — считаем им неснижаемый остаток: тогда бот
            // идёт в резерв ровно тогда, когда тратить уже нечего
            int low = Setting(sign, "low", min);
            int max = Setting(sign, "max", 0);
            // Порог пополнения выше потолка — беготня «донёс-унёс» без конца.
            // Главным считаем потолок: он про то, сколько денег держать на виду
            if (max > 0 && low > max)
                low = max;
            return new CashDesk(at, min, low, max);
        }
        return null;
    }

    /// <summary>Касса — только координаты (для отчётов).</summary>
    public BlockPos? CashChest() => Cash()?.Chest;

    /// <summary>
    /// Резервы валюты («!VAULT»). Их может быть несколько; из них бот доносит
    /// шестерни в кассу и туда же уносит излишек выручки.
    /// </summary>
    public List<Vault> Vaults()
    {
        var store = StoreSign;
        var found = new List<Vault>();
        foreach (var (sign, own) in MarkedSigns())
        {
            if (!Marked(sign, "vault"))
                continue;
            // Резерв — подсобка, а не торговый зал: пускаем его дальше границы
            // магазина. Но не дальше, чем бот вообще видит таблички со своего
            // поста, иначе он «не найдёт» резерв ровно в тот момент, когда тот
            // понадобится
            if (store != null && Dist(store.Pos, sign.Pos) > StoreRadius * 2)
                continue;
            var chest = own ?? read.ContainersBySign(sign, StallRadius)
                .Select(c => (BlockPos?)c.Pos).FirstOrDefault();
            if (chest is { } at && found.All(v => v.Chest != at))
                found.Add(new Vault(at, Setting(sign, "min", 0)));
        }
        return found;
    }

    /// <summary>
    /// Прилавки магазина. Учитываются только таблички ВНУТРИ радиуса от
    /// «!STORE»: чужие сундуки и склады бота не касаются.
    /// </summary>
    public List<Stall> Stalls()
    {
        var store = StoreSign;
        var found = new List<Stall>();

        bool InStore(BlockPos p) => store == null || Dist(store.Pos, p) <= StoreRadius;

        // Обычные прилавки: табличка на стене, сундук под ней
        foreach (var sign in read.SignsNear(StoreRadius * 2))
        {
            if (!InStore(sign.Pos))
                continue;
            if (ParseStall(sign) is { } stall)
                found.Add(stall);
        }

        // ПОДПИСАННЫЕ СУНДУКИ: надпись на самом сундуке, он же и прилавок —
        // ресурс ищем прямо в нём, отдельной таблички рядом нет
        foreach (var (pos, _, text) in read.LabeledContainersNear(StoreRadius * 2))
        {
            if (!InStore(pos))
                continue;
            if (ParseStall(new SignInfo(pos, text), pos) is { } stall)
                found.Add(stall);
        }
        return found;
    }

    /// <summary>
    /// Разбор таблички прилавка: «!ИМЯ», «buy N», «sell N». Служебные
    /// таблички (STORE, cash) прилавками не считаются.
    /// </summary>
    /// <param name="ownChest">
    /// Сундук прилавка, если он известен заранее — так разбирается ПОДПИСАННЫЙ
    /// СУНДУК: надпись на нём самом, и искать сундук рядом не нужно.
    /// </param>
    public Stall? ParseStall(SignInfo sign, BlockPos? ownChest = null)
    {
        string? name = sign.Commands.FirstOrDefault();
        if (name == null)
            return null;
        if (IsService(name))
            return null;

        int sell = 0, buy = 0;
        foreach (var line in sign.Lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !int.TryParse(parts[^1], out int price))
                continue;
            if (parts[0].Equals("buy", StringComparison.OrdinalIgnoreCase))
                sell = price;   // «buy» на табличке — по чём МЫ ПРОДАЁМ
            else if (parts[0].Equals("sell", StringComparison.OrdinalIgnoreCase))
                buy = price;    // «sell» — по чём МЫ ПОКУПАЕМ
        }
        // Нет цен — это НЕ прилавок, а склад (см. Storages)
        if (sell == 0 && buy == 0)
            return null;

        BlockPos? chest = ownChest;
        if (chest == null)
        {
            var chests = read.ContainersBySign(sign, StallRadius);
            if (chests.Count == 0)
                return null;
            chest = chests[0].Pos;
        }

        return new Stall
        {
            Sign = sign,
            Chest = chest.Value,
            ProductName = name,
            ProductCode = ResolveCode(name),
            WeSellFor = sell,
            WeBuyFor = buy,
        };
    }

    /// <summary>
    /// Название с таблички → код предмета в реестре сервера.
    /// «GOLD INGOT» → «ingot-gold»: сравниваем наборы слов, поэтому порядок
    /// и разделители не важны, а точное совпадение выигрывает у частичного.
    /// </summary>
    public string? ResolveCode(string name)
    {
        var words = name.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(w => w.ToLowerInvariant())
                        .ToArray();
        if (words.Length == 0)
            return null;

        string? best = null;
        int bestLen = int.MaxValue;
        foreach (var code in ctx.World.SearchItemCodes(words[0], 400)
                     .Concat(ctx.World.SearchBlockCodes(words[0], 400)))
        {
            if (!words.All(w => code.Contains(w, StringComparison.OrdinalIgnoreCase)))
                continue;
            // Из подходящих берём самый короткий код: он самый «базовый»
            if (code.Length < bestLen)
            {
                best = code;
                bestLen = code.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Что лежит на прилавке и что с этим делать. Сундук должен быть уже
    /// осмотрен (Readables.InspectAsync) — здесь только решение.
    /// </summary>
    public Deal Examine(Stall stall, ContainerInfo box)
    {
        var mine = LedgerOf(stall.Chest);
        // Вычитаем СВОЁ: монеты, которые мы сами выложили сдачей или оплатой,
        // и товар, который мы сами выдали покупателю
        int gears = Math.Max(0, box.Count(CurrencyCode) - mine.GearsWePlaced);
        var goods = box.Filled
            .Where(s => !s.Code!.Contains(CurrencyCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Лежит товар — выкупаем, но только если он совпадает с табличкой
        if (goods.Count > 0 && stall.WeBuyFor > 0)
        {
            var ours = goods.Where(s => Matches(s.Code!, stall)).ToList();
            if (ours.Count == 0)
                return new Deal(stall, DealKind.WrongGoods, 0, 0, goods[0].Code);

            int total = Math.Max(0, ours.Sum(s => s.Count) - mine.UnitsWeDelivered);
            // ОБРАЗЕЦ ОСТАЁТСЯ В СУНДУКЕ: по нему бот узнаёт прилавок, даже
            // когда товар кончился, и продолжает покупать
            int units = total;
            if (units > 0)
                return new Deal(stall, DealKind.BuyFromPlayer, units, units * stall.WeBuyFor, ours[0].Code);
        }

        // Лежат шестерни — это заказ: игрок платит, мы выдаём товар
        if (gears > 0 && stall.WeSellFor > 0)
        {
            int units = gears / stall.WeSellFor;
            if (units > 0)
                return new Deal(stall, DealKind.SellToPlayer, units, units * stall.WeSellFor, stall.ProductCode);
        }

        return new Deal(stall, DealKind.Nothing, 0, 0, null);
    }

    /// <summary>Товар из сундука — тот, что назван на табличке?</summary>
    public bool Matches(string code, Stall stall)
    {
        if (stall.ProductCode is { } expect &&
            code.Equals(expect, StringComparison.OrdinalIgnoreCase))
            return true;
        // Реестр мог не дать точного кода — сверяем по словам названия
        return stall.ProductName
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .All(w => code.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Склады: подписанные сундуки и таблички БЕЗ цен. Один товар — сколько
    /// угодно складов; отсюда берётся товар на продажу и сюда уходит выкуп.
    /// </summary>
    public List<Storage> Storages()
    {
        var store = StoreSign;
        var found = new List<Storage>();

        bool InStore(BlockPos p) => store == null || Dist(store.Pos, p) <= StoreRadius;

        void Consider(SignInfo sign, BlockPos? own)
        {
            if (!InStore(sign.Pos))
                return;
            if (sign.Commands.FirstOrDefault() is not { } name || IsService(name))
                return;
            // Цены есть — это прилавок, не склад
            if (sign.Lines.Any(l =>
                    l.StartsWith("buy", StringComparison.OrdinalIgnoreCase) ||
                    l.StartsWith("sell", StringComparison.OrdinalIgnoreCase)))
                return;

            var chest = own ?? read.ContainersBySign(sign, StallRadius)
                .Select(c => (BlockPos?)c.Pos).FirstOrDefault();
            if (chest is { } at)
                found.Add(new Storage(at, name, ResolveCode(name)));
        }

        foreach (var sign in read.SignsNear(StoreRadius * 2))
            Consider(sign, null);
        foreach (var (pos, _, text) in read.LabeledContainersNear(StoreRadius * 2))
            Consider(new SignInfo(pos, text), pos);
        return found;
    }

    /// <summary>
    /// Пост продавца: клетка С ОПОРОЙ рядом с табличкой «!STORE».
    /// <para>
    /// Табличка висит на стене, поэтому её собственная клетка — воздух, и
    /// вставать «в неё» нельзя: бот бесконечно не находил путь к точке,
    /// которой не существует. Ищем настоящий пол вокруг таблички
    /// (WorldModel.IsStandable: снизу опора, ноги и голова свободны, не лава)
    /// и берём ближайшую такую клетку, ПРЕДПОЧИТАЯ клетки на уровне таблички:
    /// иначе пост «проваливается» в погреб под магазином.
    /// </para>
    /// <para>Дальше StoreRadius не уходим — пост обязан быть в магазине.</para>
    /// </summary>
    public BlockPos? PostCell()
    {
        if (StoreSign is not { } sign)
            return null;

        int radius = (int)Math.Min(PostSearchRadius, Math.Max(1, StoreRadius));
        BlockPos? best = null;
        double bestScore = double.MaxValue;

        for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                int x = sign.Pos.X + dx, z = sign.Pos.Z + dz;
                if (ctx.World.FindStandableY(x, sign.Pos.Y, z, up: 1, down: PostDrop) is not { } y)
                    continue;
                var cell = new BlockPos(x, y, z);
                if (Dist(sign.Pos, cell) > StoreRadius)
                    continue;

                int dy = y - sign.Pos.Y;
                // Спуск по вертикали «дороже» шага в сторону: продавец стоит
                // у своей таблички, а не этажом ниже под ней
                double score = dx * dx + dz * dz + 4.0 * dy * dy;
                // На сундуках не стоим: сверху их не открыть соседям, да и
                // сам бот загораживает свой же прилавок
                if (ctx.World.GetBlockEntity(new BlockPos(x, y - 1, z)) is { } under &&
                    Readables.IsContainerClass(under.ClassName))
                    score += 100;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = cell;
                }
            }
        return best;
    }

    /// <summary>Насколько далеко от таблички «!STORE» ищем место для поста.</summary>
    public int PostSearchRadius { get; set; } = 5;

    /// <summary>
    /// На сколько блоков ниже таблички «!STORE» соглашаемся искать пол.
    /// Таблички вешают высоко, поэтому меньшего не хватает.
    /// </summary>
    public int PostDrop { get; set; } = 8;

    /// <summary>Склады под конкретный товар прилавка.</summary>
    public List<Storage> StoragesFor(Stall stall) => Storages()
        .Where(s => s.ProductName.Equals(stall.ProductName, StringComparison.OrdinalIgnoreCase))
        .ToList();

    /// <summary>Обойти прилавки и вернуть то, где есть что делать.</summary>
    public async Task<List<Deal>> SurveyAsync(CancellationToken ct = default)
    {
        var deals = new List<Deal>();
        foreach (var stall in Stalls())
        {
            if (ct.IsCancellationRequested)
                break;
            // ЧИТАЕМ ЖИВОЙ ИНВЕНТАРЬ, а не снимок блок-сущности: сервер тот
            // снимок после изменений не пересылает, поэтому бот видел товар,
            // который уже забрал, и не замечал только что положенный игроком
            if (await LiveBoxAsync(stall.Chest, ct) is not { } box)
                continue;
            lastSeen[stall.Chest] = box;
            // Здесь уже не вышло, и с тех пор ничего не изменилось — молчим.
            // Стоит игроку что-то положить или забрать — пробуем сразу же
            if (IsStalled(stall.Chest, box))
                continue;
            var deal = Examine(stall, box);
            if (deal.Kind != DealKind.Nothing)
            {
                deals.Add(deal);
                OnLog?.Invoke(deal.Kind switch
                {
                    DealKind.BuyFromPlayer => $"{stall.ProductName}: выкупаю {deal.Units} за {deal.Price}",
                    DealKind.SellToPlayer => $"{stall.ProductName}: заказ на {deal.Units} за {deal.Price}",
                    _ => $"{stall.ProductName}: лежит не то ({deal.FoundCode})",
                });
            }
        }
        return deals;
    }

    /// <summary>
    /// ПАМЯТЬ ПО ПРИЛАВКУ: что бот положил туда САМ и покупатель ещё не забрал.
    /// Без неё бот выдаёт товар, через круг видит его же и «выкупает» обратно —
    /// бесконечный цикл и вымывание кассы. Обмен живой: пока человек не забрал
    /// своё, это не предложение о продаже.
    /// </summary>
    private sealed class Ledger
    {
        public int GearsWePlaced;
        public int UnitsWeDelivered;
    }

    private readonly Dictionary<BlockPos, Ledger> ledgers = new();

    private Ledger LedgerOf(BlockPos chest)
    {
        if (!ledgers.TryGetValue(chest, out var l))
            ledgers[chest] = l = new Ledger();
        return l;
    }

    /// <summary>Сообщение покупателю в чат (роль подключает свой канал).</summary>
    public event Func<string, Task>? OnAnnounce;

    // Прилавки, где сделка не прошла. Ждать по часам — глупо: игрок может
    // положить товар через секунду. Поэтому помним НЕ время, а СОСТОЯНИЕ
    // сундука: молчим ровно до тех пор, пока в нём ничего не изменилось.
    // Любое изменение — повод попробовать снова, без всякой задержки
    private readonly Dictionary<BlockPos, string> stalled = new();

    /// <summary>Отпечаток содержимого: изменился — значит есть смысл повторить.</summary>
    private static string Fingerprint(ContainerInfo box) =>
        string.Join("|", box.Items.Select(s => s.Code == null || s.Count <= 0 ? "-" : $"{s.Code}x{s.Count}"));

    /// <summary>Прилавок на паузе: сделка не вышла, а содержимое с тех пор то же.</summary>
    public bool IsStalled(BlockPos chest, ContainerInfo box) =>
        stalled.TryGetValue(chest, out var seen) && seen == Fingerprint(box);

    /// <summary>Отложить прилавок до ИЗМЕНЕНИЯ его содержимого.</summary>
    private void Postpone(BlockPos chest, ContainerInfo? box = null)
    {
        if (box == null)
            lastSeen.TryGetValue(chest, out box);
        if (box != null)
            stalled[chest] = Fingerprint(box);
    }

    /// <summary>Сделка прошла — снова следим за прилавком.</summary>
    private void Resume(BlockPos chest) => stalled.Remove(chest);

    /// <summary>Снять все паузы.</summary>
    public void ClearStalled() => stalled.Clear();

    // Последний осмотр прилавка — им пользуется Postpone, когда сундук уже закрыт
    private readonly Dictionary<BlockPos, ContainerInfo> lastSeen = new();

    private Task Say(string text) => OnAnnounce?.Invoke(text) ?? Task.CompletedTask;

    /// <summary>
    /// Провести сделку. Порядок жёсткий: СНАЧАЛА ЗАБИРАЕМ ОПЛАТУ, и только
    /// потом отдаём встречную часть — иначе прерывание оставит нас без денег
    /// или без товара. Встречная часть всегда кладётся ТУДА, ОТКУДА ВЗЯЛИ.
    /// Возит партиями: сколько унесёт, столько и переносит за раз.
    /// </summary>
    public async Task ExecuteAsync(Deal deal, CancellationToken ct = default)
    {
        var stall = deal.Stall;
        switch (deal.Kind)
        {
            case DealKind.WrongGoods:
                Postpone(stall.Chest);
            await Say($"В сундуке «{stall.ProductName}» лежит не тот товар " +
                          $"({deal.FoundCode}) — не выкупаю, забирайте обратно.");
                return;

            case DealKind.BuyFromPlayer:
                await BuyAsync(deal, ct);
                return;

            case DealKind.SellToPlayer:
                await SellAsync(deal, ct);
                return;
        }
    }

    /// <summary>
    /// Сколько в кассе шестерён, которые МОЖНО потратить (сверх неснижаемого
    /// остатка). Считается по живому инвентарю: снимок блок-сущности отстаёт.
    /// </summary>
    private async Task<int> SpendableAsync(CashDesk cash, CancellationToken ct) =>
        await LiveBoxAsync(cash.Chest, ct) is { } box
            ? Math.Max(0, box.Count(CurrencyCode) - cash.MinReserve)
            : 0;

    /// <summary>Резерв пуст — уже говорили. Чтобы не повторяться каждый круг.</summary>
    private bool vaultEmptyReported;

    /// <summary>Как часто бот сам заглядывает в кассу (поход к сундуку — время).</summary>
    public TimeSpan CashCheckInterval { get; set; } = TimeSpan.FromSeconds(60);

    private DateTime nextCashCheck = DateTime.MinValue;

    /// <summary>
    /// ОБСЛУЖИВАНИЕ КАССЫ. Магазин ничего не производит: он только
    /// перекладывает шестерни между кассой (рабочий кошелёк) и резервом
    /// «!VAULT» (запас, который наполняет хозяин). Просела ниже «low» —
    /// доносим из резерва; поднялась выше «max» — уносим излишек обратно.
    /// <para>
    /// Возвращает true, если что-то ПЕРЕЕХАЛО (по факту, а не по намерению) —
    /// значит ход занят и звать снова прямо сейчас незачем.
    /// </para>
    /// </summary>
    /// <param name="force">
    /// Проверить кассу немедленно, не глядя на CashCheckInterval — так делает
    /// выкуп, когда денег не хватило прямо сейчас.
    /// </param>
    public async Task<bool> MaintainCashAsync(CancellationToken ct = default, bool force = false)
    {
        // Проверка кассы — это ПОХОД к сундуку. Без выдержки бот ходил бы к
        // ней каждый круг вместо того, чтобы стоять за прилавком
        if (!force && DateTime.UtcNow < nextCashCheck)
            return false;
        nextCashCheck = DateTime.UtcNow + CashCheckInterval;

        if (Cash() is not { } cash)
            return false;
        if (await LiveBoxAsync(cash.Chest, ct) is not { } box)
        {
            OnLog?.Invoke($"кассу {cash.Chest} не открыть — обслуживание отложено");
            return false;
        }
        int have = box.Count(CurrencyCode);
        var vaults = Vaults();

        // Излишек выручки — в резерв: в проходном сундуке деньгам не место
        if (cash.MaxKeep > 0 && have > cash.MaxKeep && vaults.Count > 0)
        {
            int surplus = have - cash.MaxKeep;
            int hidden = 0;
            foreach (var v in vaults)
            {
                if (hidden >= surplus || ct.IsCancellationRequested)
                    break;
                hidden += await Transfer.MoveAsync(cash.Chest, v.Chest,
                    CurrencyCode, surplus - hidden, cash.MaxKeep, ct);
            }
            if (hidden > 0)
            {
                OnLog?.Invoke($"убрал в резерв {hidden} шестерён (в кассе было {have}, потолок {cash.MaxKeep})");
                return true;
            }
        }

        // «>», а не «>=»: при low == min касса на самом дне (тратить нечего)
        // обязана пополняться, иначе выкуп встанет навсегда
        if (have > cash.LowWater)
            return false;

        // Доливаем до потолка, а если потолка нет — сколько влезет: хозяин не
        // назвал предела, а деньги всё равно его и лежат в его же магазине
        int room = Transfer.RoomFor([.. box.Items], CurrencyCode);
        int need = cash.MaxKeep > 0 ? Math.Min(cash.MaxKeep - have, room) : room;
        if (need <= 0)
            return false; // класть некуда — это полная касса, а не пустой резерв

        if (vaults.Count == 0)
        {
            if (!vaultEmptyReported)
            {
                vaultEmptyReported = true;
                OnLog?.Invoke("касса просела, а резерва «!VAULT» в магазине нет — пополнять неоткуда");
            }
            return false;
        }

        int brought = 0;
        foreach (var v in vaults)
        {
            if (brought >= need || ct.IsCancellationRequested)
                break;
            // leaveInSource: неприкосновенный остаток резерва — его хозяин
            // держит на что-то своё, и трогать его мы не вправе
            brought += await Transfer.MoveAsync(v.Chest, cash.Chest,
                CurrencyCode, need - brought, v.Keep, ct);
        }

        if (brought > 0)
        {
            vaultEmptyReported = false;
            OnLog?.Invoke($"донёс в кассу {brought} шестерён из резерва (было {have}, порог {cash.LowWater})");
            return true;
        }

        if (!vaultEmptyReported)
        {
            vaultEmptyReported = true;
            await Say($"Резерв пуст: в кассе {have} шестерён, а брать неоткуда. " +
                      $"Выкуп у игроков приостановлен, пока хозяин не пополнит «!VAULT». " +
                      $"Продажа товара работает как обычно.");
        }
        return false;
    }

    /// <summary>Выкуп у игрока: забираем товар, платим шестернями из кассы.</summary>
    private async Task BuyAsync(Deal deal, CancellationToken ct)
    {
        var stall = deal.Stall;
        if (Cash() is not { } cash)
        {
            Postpone(stall.Chest);
            await Say($"Кассу не нахожу — «{stall.ProductName}» пока выкупить не могу.");
            return;
        }
        var stores = StoragesFor(stall);
        if (stores.Count == 0 || stall.ProductCode == null)
        {
            Postpone(stall.Chest);
            await Say($"Склада по «{stall.ProductName}» у меня нет — выкупить не могу.");
            return;
        }

        // СНАЧАЛА ДЕНЬГИ, ПОТОМ ТОВАР. Раньше бот уносил товар на склад и
        // только после этого шёл считать кассу: если платить было нечем,
        // игрок оставался и без товара, и без денег. Теперь пустая касса
        // означает, что товар мы просто НЕ ТРОГАЕМ
        int spendable = await SpendableAsync(cash, ct);
        if (spendable < stall.WeBuyFor)
        {
            await MaintainCashAsync(ct, force: true);   // может, резерв ещё не пуст
            spendable = await SpendableAsync(cash, ct);
        }
        if (spendable < stall.WeBuyFor)
        {
            Postpone(stall.Chest);
            await Say($"«{stall.ProductName}»: выкуп приостановлен — в кассе нет свободных шестерён " +
                      $"(нужно {stall.WeBuyFor} за штуку, доступно {spendable}). " +
                      $"Товар не забираю, он ваш; загляните позже.");
            return;
        }

        // Берём ровно столько, за сколько можем расплатиться
        int units = Math.Min(deal.Units, spendable / stall.WeBuyFor);
        if (units < deal.Units)
            await Say($"«{stall.ProductName}»: касса тянет только {units} из {deal.Units} — " +
                      $"остальное оставляю вам, заберу, когда пополнюсь.");

        int taken = await Transfer.MoveAsync(stall.Chest, stores[0].Chest,
            stall.ProductCode, units, 0, ct);
        if (taken <= 0)
        {
            Postpone(stall.Chest);
            await Say($"«{stall.ProductName}»: забрать не смог — либо товар уже разобрали, " +
                      $"либо на складе нет места. Разберусь и вернусь.");
            return;
        }

        // Платим РОВНО за то, что реально забрали, и не глубже неснижаемого
        // остатка: его стережёт сам перенос (leaveInSource)
        int pay = taken * stall.WeBuyFor;
        int paid = await Transfer.MoveAsync(cash.Chest, stall.Chest,
            CurrencyCode, pay, cash.MinReserve, ct);
        if (paid < pay)
        {
            // Не хватило на месте — доносим из резерва и доплачиваем: долг
            // перед игроком закрывать нужно сразу, а не «когда-нибудь»
            if (await MaintainCashAsync(ct, force: true))
                paid += await Transfer.MoveAsync(cash.Chest, stall.Chest,
                    CurrencyCode, pay - paid, cash.MinReserve, ct);
        }
        LedgerOf(stall.Chest).GearsWePlaced += paid;

        if (paid >= pay)
        {
            Resume(stall.Chest);
            await Say($"Купил {taken} × {stall.ProductName} за {pay} шестерён — деньги в том же сундуке.");
        }
        else
        {
            Postpone(stall.Chest);
            await Say($"Купил {taken} × {stall.ProductName} на {pay} шестерён, но донёс только {paid}: " +
                      $"остальное не влезло в сундук или кончилось в кассе. Разберите — принесу остаток.");
        }
    }

    /// <summary>Заказ игрока: забираем шестерни, выдаём товар со склада.</summary>
    private async Task SellAsync(Deal deal, CancellationToken ct)
    {
        var stall = deal.Stall;
        if (Cash() is not { } cash || stall.ProductCode == null)
        {
            Postpone(stall.Chest);
            await Say($"Кассу не нахожу — «{stall.ProductName}» пока не выдам.");
            return;
        }
        var stores = StoragesFor(stall);
        if (stores.Count == 0)
        {
            Postpone(stall.Chest);
            await Say($"Склада по «{stall.ProductName}» у меня нет — товар выдать не могу.");
            return;
        }


        // ОПЛАТА ПЕРВОЙ И ТОЛЬКО ПО ФАКТУ: товар не отдаём, пока деньги
        // не в кассе. Сдача (остаток сверх кратного цене) остаётся игроку
        int paid = await Transfer.MoveAsync(stall.Chest, cash.Chest, CurrencyCode, deal.Gears, 0, ct);
        var mine = LedgerOf(stall.Chest);
        mine.GearsWePlaced = Math.Max(0, mine.GearsWePlaced - paid);

        int units = stall.WeSellFor > 0 ? paid / stall.WeSellFor : 0;
        if (units <= 0)
        {
            Postpone(stall.Chest);
            await Say($"«{stall.ProductName}»: оплаты в сундуке не нашёл — возможно, её уже забрали. " +
                      $"Товар не выдаю.");
            return;
        }

        int given = 0;
        foreach (var store in stores)
        {
            if (given >= units || ct.IsCancellationRequested)
                break;
            // Со СКЛАДА оставляем образец: по нему бот узнаёт, что это за
            // склад, даже когда товар кончился
            given += await Transfer.MoveAsync(store.Chest, stall.Chest,
                stall.ProductCode, units - given, SampleUnits, ct);
        }
        mine.UnitsWeDelivered += given;

        // Не выдали всё — возвращаем деньгами: товара нет, деньги не наши
        int refund = (units - given) * stall.WeSellFor;
        if (refund > 0)
            mine.GearsWePlaced += await Transfer.MoveAsync(cash.Chest, stall.Chest,
                CurrencyCode, refund, 0, ct);

        if (given >= units)
        {
            Resume(stall.Chest);
            await Say($"Продал {given} × {stall.ProductName} за {paid} шестерён. Забирайте!");
        }
        else
        {
            Postpone(stall.Chest);
            await Say($"«{stall.ProductName}»: на складе было только {given} из {units}. " +
                      $"За недостающее вернул {refund} шестерён.");
        }
    }

    /// <summary>
    /// Содержимое сундука ПО ЖИВОМУ инвентарю: открыть, прочитать, закрыть.
    /// Единственный достоверный источник — снимок блок-сущности отстаёт.
    /// </summary>
    private async Task<ContainerInfo?> LiveBoxAsync(BlockPos chest, CancellationToken ct)
    {
        if (!await Transfer.OpenAsync(chest, ct))
            return null;
        var slots = Transfer.Slots(chest);
        await Transfer.CloseAsync(chest);
        if (slots == null)
            return null;
        string kind = ctx.World.GetBlockEntity(chest)?.ClassName ?? "контейнер";
        return new ContainerInfo(chest, kind, slots);
    }

    /// <summary>Сколько такого предмета лежит в сундуке (с открытием).</summary>
    private async Task<int> CountAsync(BlockPos chest, string codePart, CancellationToken ct) =>
        await read.InspectAsync(chest, ct) is { } box ? box.Count(codePart) : 0;

    /// <summary>
    /// Перенести до <paramref name="want"/> предметов из одного сундука в
    /// первый подходящий из целевых, партиями по вместимости рюкзака.
    /// Место кончилось — сообщаем, сколько донесли: остаток заберём позже,
    /// когда игрок разберёт.
    /// </summary>
    private async Task<int> HaulAsync(BlockPos from, List<BlockPos> to, string? codePart,
        int want, CancellationToken ct)
    {
        if (codePart == null || want <= 0 || to.Count == 0)
            return 0;

        int moved = 0;
        for (int trip = 0; trip < MaxTrips && moved < want && !ct.IsCancellationRequested; trip++)
        {
            int inBag = await TakeIntoBagAsync(from, codePart, want - moved, ct);
            if (inBag <= 0)
                break;
            int dropped = 0;
            foreach (var target in to)
            {
                if (dropped >= inBag)
                    break;
                dropped += await PutFromBagAsync(target, codePart, inBag - dropped, ct);
            }
            moved += dropped;
            if (dropped < inBag)
                break; // всё занято — остальное донесём позже
        }
        return moved;
    }

    /// <summary>Сколько рейсов максимум за одну сделку.</summary>
    public int MaxTrips { get; set; } = 6;

    /// <summary>Взять предметы из сундука в рюкзак.</summary>
    private async Task<int> TakeIntoBagAsync(BlockPos chest, string codePart, int want, CancellationToken ct)
    {
        if (await read.InspectAsync(chest, ct) is not { } box)
            return 0;
        string? bag = ctx.Self.GetInventoryId("hotbar");
        if (bag == null)
            return 0;

        // Открываем и ТОЛЬКО ПОТОМ спрашиваем настоящий id: до открытия
        // сервер этот инвентарь нам ещё не регистрировал
        await ctx.Actions.OpenContainerAsync(chest);
        await Task.Delay(400, ct).ContinueWith(_ => { });
        string src = InventoryIdAt(chest);

        int got = 0;
        for (int i = 0; i < box.Items.Count && got < want; i++)
        {
            var slot = box.Items[i];
            if (slot.Code == null || slot.Count <= 0 ||
                !slot.Code.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                continue;
            if (FreeBagSlot() is not { } free)
                break;
            int take = Math.Min(slot.Count, want - got);
            await ctx.Actions.MoveItemAsync(src, i, bag, free, take);
            await Task.Delay(220, ct).ContinueWith(_ => { });
            got += take;
        }
        await ctx.Actions.CloseContainerAsync(chest, ClassAt(chest));

        // Считаем ФАКТ, а не «сколько отправили»: сервер мог отказать молча,
        // и раньше бот верил себе на слово, из-за чего провал был не виден
        int really = CountInCargo(codePart);
        if (really < got)
            OnLog?.Invoke($"сундук {chest}: просил {got}, взял {really}");
        return really;
    }

    /// <summary>Выложить предметы из рюкзака в сундук.</summary>
    private async Task<int> PutFromBagAsync(BlockPos chest, string codePart, int want, CancellationToken ct)
    {
        if (await read.InspectAsync(chest, ct) is not { } box)
            return 0;
        string? bag = ctx.Self.GetInventoryId("hotbar");
        var bagSlots = ctx.Self.GetInventory("hotbar");
        if (bag == null || bagSlots == null)
            return 0;

        await ctx.Actions.OpenContainerAsync(chest);
        await Task.Delay(400, ct).ContinueWith(_ => { });
        string dst = InventoryIdAt(chest);

        int put = 0;
        for (int i = 0; i < Math.Min(CargoSlots, bagSlots.Length) && put < want; i++)
        {
            var slot = bagSlots[i];
            if (slot.Code == null || slot.Count <= 0 ||
                !slot.Code.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                continue;
            // Куда класть: СНАЧАЛА В УЖЕ ЛЕЖАЩИЙ ТАКОЙ ЖЕ СТАК (доливаем),
            // и только потом в пустой слот. Раньше искался лишь пустой —
            // и в полную кассу монеты положить было некуда, бот забирал
            // оплату и таскал её с собой, не донеся до кассы
            int free = -1;
            for (int j = 0; j < box.Items.Count; j++)
                if (box.Items[j].Code is { } tc && box.Items[j].Count > 0 &&
                    tc.Equals(slot.Code, StringComparison.OrdinalIgnoreCase))
                {
                    free = j;
                    break;
                }
            if (free < 0)
                for (int j = 0; j < box.Items.Count; j++)
                    if (box.Items[j].Code == null || box.Items[j].Count <= 0)
                    {
                        free = j;
                        break;
                    }
            if (free < 0)
            {
                OnLog?.Invoke($"в сундуке {chest} некуда положить {slot.Code}");
                break;
            }
            int give = Math.Min(slot.Count, want - put);
            await ctx.Actions.MoveItemAsync(bag, i, dst, free, give);
            await Task.Delay(160, ct).ContinueWith(_ => { });
            put += give;
        }
        await ctx.Actions.CloseContainerAsync(chest, ClassAt(chest));
        return put;
    }

    /// <summary>Сколько такого предмета лежит в грузовых слотах хотбара.</summary>
    private int CountInCargo(string codePart)
    {
        var bag = ctx.Self.GetInventory("hotbar");
        if (bag == null)
            return 0;
        int n = 0;
        for (int i = 0; i < Math.Min(CargoSlots, bag.Length); i++)
            if (bag[i].Code is { } c && bag[i].Count > 0 &&
                c.Contains(codePart, StringComparison.OrdinalIgnoreCase))
                n += bag[i].Count;
        return n;
    }

    /// <summary>
    /// Свободный слот для перевозки. Возим ХОТБАРОМ: «backpack» в игре — это
    /// четыре слота ПОД СУМКИ (ItemSlotBackpack), обычный предмет туда не
    /// кладётся вовсе, и сервер молча откатывал перенос. В хотбаре грузовые
    /// слоты 0..9 (10 — умение, 11 — левая рука, их не трогаем).
    /// </summary>
    private int? FreeBagSlot()
    {
        var bag = ctx.Self.GetInventory("hotbar");
        if (bag == null)
            return null;
        for (int i = 0; i < Math.Min(CargoSlots, bag.Length); i++)
            if (bag[i].Code == null || bag[i].Count <= 0)
                return i;
        return null;
    }

    /// <summary>Сколько слотов хотбара занимаем под перевозку.</summary>
    public int CargoSlots { get; set; } = 10;

    /// <summary>
    /// НАСТОЯЩИЙ id инвентаря открытого контейнера — берём из того, что
    /// прислал сервер, а не угадываем по коду блока. Именно на этом ломался
    /// перенос: у подписанного сундука код «labeledchest-west», и собранное
    /// из него имя серверу неизвестно, поэтому пакет молча выбрасывался.
    /// Ищем среди известных инвентарей тот, чей id заканчивается координатами.
    /// </summary>
    private string InventoryIdAt(BlockPos pos) =>
        $"{ctx.World.GetInventoryClass(pos.X, pos.Y, pos.Z)}-{pos.X}, {pos.Y}, {pos.Z}";

    /// <summary>Класс инвентаря контейнера — как его называет сервер.</summary>
    private string ClassAt(BlockPos pos) =>
        ctx.World.GetInventoryClass(pos.X, pos.Y, pos.Z);

    private static double Dist(BlockPos a, BlockPos b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
}
