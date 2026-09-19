namespace VsBotKit;

/// <summary>
/// Укрытие: пересидеть опасное время (временную бурю) там, где до бота
/// не доберутся. Два способа, как и просил заказчик:
///
/// 1. уйти в дом и закрыть за собой дверь;
/// 2. если дома рядом нет — выкопать нишу в земле и замуровать себя,
///    а после — разобрать стенку и выйти.
///
/// Ниша копается и заделывается ЧЕСТНО: теми же добычей и установкой, что
/// и всё остальное (с инструментом, за реальное время, в пределах руки).
/// </summary>
public class Shelter
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Shelter(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// Насколько глушить ПОВТОРЫ в журнале укрытия, секунд (0 — не глушить).
    ///
    /// ЗАЧЕМ. Выбраться бот пробует раз в двадцать секунд (см.
    /// <see cref="BehaviorShelterFromStorm.RetrySeconds"/>), а замурованным
    /// может просидеть час: без заглушки это тысячи одинаковых строк — та же
    /// беда, из которой всё и началось, только в двадцать раз реже. Механизм
    /// тот же, что у добычи и движения (<see cref="LogRepeats"/>): повтор молчит
    /// и СЧИТАЕТСЯ, а число промолчанных приходит со следующей такой же строкой.
    /// Минута — потому что попытка идёт раз в двадцать секунд.
    /// </summary>
    public double QuietRepeatSeconds
    {
        get => repeats.QuietSeconds;
        set => repeats.QuietSeconds = value;
    }

    private readonly LogRepeats repeats = new() { QuietSeconds = 60 };

    /// <summary>Сказать то, что может повториться сотню раз, — через счётчик.</summary>
    private void Say(string message)
    {
        if (repeats.Say(message, DateTime.UtcNow) is { } line)
            OnLog?.Invoke(line);
    }

    /// <summary>Дом бота: точка, куда уходить (обычно ставится ролью).</summary>
    public BlockPos? Home { get; set; }

    /// <summary>
    /// ЧЕМ ЗАКЛАДЫВАТЬ ВХОД — первое, что найдётся в сумке.
    ///
    /// ИМЕНА ЗДЕСЬ СВЕРЕНЫ ПО АССЕТАМ ИГРЫ 1.22.7, и это не формальность.
    /// Последним в списке стояло <c>"dirt"</c> — БЛОКА С ТАКИМ КОДОМ В VINTAGE
    /// STORY НЕТ ВОВСЕ (земля зовётся <c>soil</c>); имя пришло из другой игры.
    /// Вреда оно не сделало по случайности: ищется код ПО ВХОЖДЕНИЮ, и «dirt»
    /// совпадал с настоящими <c>packeddirt</c> и <c>drypackeddirt</c> —
    /// твёрдыми блоками, которыми и правда можно замуроваться. Заодно он
    /// совпадал с <c>dirtyclaypot</c> и <c>dirtygravel</c>.
    ///
    /// Запись «случайно работает» — худший вид записи: её никто не проверит.
    /// Поэтому здесь стоит <c>packeddirt</c>, и одной строки хватает на оба
    /// блока: <c>drypackeddirt</c> содержит её как часть кода.
    /// </summary>
    public List<string> WallMaterials { get; } =
        ["cobblestone", "soil", "stone", "plank", "packeddirt"];

    /// <summary>Замурован ли бот прямо сейчас.</summary>
    public bool WalledIn { get; private set; }

    /// <summary>
    /// ЧЕМ Я СЕБЯ ЗАЛОЖИЛ — ТОЛЬКО ПОДТВЕРЖДЁННОЕ СЕРВЕРОМ, и все клетки, а не
    /// одна.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 16.08, 14:27:27). Бот закапывался, крышка не встала
    /// («рядом нет соседа, по грани которого щёлкнуть»), — а «чем замуровался»
    /// записалось всё равно, потому что запись стояла ПОСЛЕ попытки и на её
    /// успех не смотрела. Следом он обложился коробкой из двух блоков, и вот она
    /// не записалась вовсе: у коробки такой памяти не было. Через восемь минут,
    /// когда буря кончилась, разбор пошёл ломать ту самую невставшую крышку —
    /// то есть ВОЗДУХ в трёх блоках над головой, — получал «тут пусто» и повторял
    /// это пять тысяч триста пятьдесят девять раз.
    ///
    /// Отсюда два правила разом: класть сюда только то, что сервер подтвердил
    /// (правило 4), и класть КАЖДУЮ такую клетку — заложенный вход это две
    /// клетки, а коробка все девять.
    /// </summary>
    private readonly List<BlockPos> sealedCells = [];

    // Дверь, которую бот закрыл ЗА СОБОЙ, уйдя под крышу, точка, где он при
    // этом стоял, и БОК ДВЕРИ, с которого он на неё смотрел. Держатся вместе:
    // без точки «моя закрытая дверь» осталась бы правдой и в тридцати блоках
    // от дома, а без бока — на пороге снаружи, в двух шагах от той же двери
    private BlockPos? shutDoor;
    private (double X, double Y, double Z)? shutDoorFrom;
    private (int Axis, int Sign) shutDoorSide;

    /// <summary>
    /// Насколько можно отойти от места, где спрятался, и всё ещё считаться
    /// спрятавшимся. Дом бывает и в пять блоков шириной; дальше это уже не
    /// «сижу за своей дверью», а «гуляю».
    /// </summary>
    public double HideHoldRadius { get; set; } = 6;

    /// <summary>
    /// УКРЫТИЕ ДЕРЖИТСЯ — ОДИН ФАКТ НА ОБА ПРИЁМА, И ИМЕННО ФАКТ.
    ///
    /// ЗАЧЕМ ЭТО ПОЯВИЛОСЬ. Заказчик написал про два случая сразу: «не лезть в
    /// бой если ты в доме с закрытой дверью … ты спрятался от шторма». Бой
    /// спрашивал про укрытие ровно одно — <see cref="WalledIn"/>, то есть
    /// «замурован блоками». А ПЕРВЫЙ его случай — дом с дверью — этого признака
    /// не ставит вовсе: <see cref="GoHomeAndCloseDoorAsync"/> возвращала true и
    /// молчала. Выходило, что бот, спрятавшийся самым обычным способом, для боя
    /// не прятался никуда, и удержать его дома могла только преграда, увиденная
    /// лучом в эту самую секунду.
    ///
    /// ПОЧЕМУ ЭТО НЕ ФЛАЖОК «Я РЕШИЛ СПРЯТАТЬСЯ». Намерение живёт ровно до
    /// первой неожиданности: дверь открыл сосед, бот вышел за дровами, буря
    /// кончилась и он ушёл работать. Поэтому спрашивается МИР: моя дверь всё
    /// ещё дверь, всё ещё закрыта, я всё ещё рядом с ней И ВСЁ ЕЩЁ С ТОГО ЖЕ ЕЁ
    /// БОКА (<see cref="DoorDebt.SameDoorSide"/>). Перестало быть правдой — укрытия нет,
    /// и говорить о нём бот перестаёт сам.
    ///
    /// А «буря кончилась» миру не видно вовсе: это решение, и стирает память о
    /// двери тот, кто его принимает, — <see cref="StopHiding"/>.
    /// </summary>
    public bool Hidden => WalledIn || BehindMyShutDoor();

    /// <summary>
    /// СЧИТАТЬ ЭТУ ДВЕРЬ СВОИМ УКРЫТИЕМ — если она и правда закрыта.
    ///
    /// Отдельным шагом, а не строкой внутри похода домой, ровно по правилу
    /// «одна механика в одном месте»: спросить «спрятался ли я за дверью»
    /// нужно и походу домой, и бою (через <see cref="Hidden"/>), и стенду,
    /// который проверяет это делом. Условие тут одно на всех и живёт здесь.
    ///
    /// Возвращает false и НИЧЕГО НЕ ЗАПОМИНАЕТ, если дверь открыта или это
    /// вовсе не дверь: соврать про укрытие — значит оставить бота стоять под
    /// бурей с мыслью, что он в домике.
    /// </summary>
    public bool HideBehindDoor(BlockPos door)
    {
        StopHiding();
        var owner = ctx.World.DoorClickPos(door);
        if (ctx.Self.Position is not { } me)
            return false;
        if (!ctx.World.IsDoor(owner.X, owner.Y, owner.Z) ||
            ctx.World.IsDoorOpen(owner.X, owner.Y, owner.Z))
            return false;
        var side = DoorDebt.DoorSide(owner, me.X, me.Z);
        // СТОЮ РОВНО НА ОСИ ДВЕРИ — значит бока у меня нет, и «за дверью» я
        // окажусь по любую её сторону. Живьём так не бывает (створка твёрдая),
        // но соврать про укрытие из-за арифметического края нельзя
        if (side.Sign == 0)
            return false;
        shutDoor = owner;
        shutDoorFrom = (me.X, me.Y, me.Z);
        shutDoorSide = side;
        return true;
    }

    /// <summary>
    /// БОЛЬШЕ НЕ ПРЯЧУСЬ ЗА СВОЕЙ ДВЕРЬЮ — забыть её.
    ///
    /// Нужно потому, что дверь — это ПАМЯТЬ, а не блок под ногами: сама по себе
    /// она не перестаёт быть закрытой оттого, что буря кончилась. Зовёт это
    /// конец бури (<see cref="BehaviorShelterFromStorm"/>) и приход домой
    /// заново — то есть тот, кто РЕШАЕТ, прячется бот или работает.
    /// </summary>
    public void StopHiding()
    {
        shutDoor = null;
        shutDoorFrom = null;
        shutDoorSide = default;
    }

    private bool BehindMyShutDoor()
    {
        if (shutDoor is not { } door || shutDoorFrom is not { } spot ||
            ctx.Self.Position is not { } now)
            return false;
        double dx = now.X - spot.X, dy = now.Y - spot.Y, dz = now.Z - spot.Z;
        if (dx * dx + dy * dy + dz * dz > HideHoldRadius * HideHoldRadius)
            return false;   // ушёл — значит больше не прячусь
        // ВЫШЕЛ ЗА ПОРОГ — тоже больше не прячусь, даже если до порога два шага
        if (!DoorDebt.SameDoorSide(door, shutDoorSide, now.X, now.Z))
            return false;
        return ctx.World.IsDoor(door.X, door.Y, door.Z) &&
               !ctx.World.IsDoorOpen(door.X, door.Y, door.Z);
    }

    /// <summary>Материал для заделки, который реально есть в инвентаре.</summary>
    public string? AvailableWallMaterial() =>
        WallMaterials.FirstOrDefault(m => hands.CountOf(m) > 0);

    /// <summary>
    /// Спрятаться: сначала дом с дверью, если он рядом; иначе — ниша.
    /// Возвращает false, если не вышло ни то, ни другое (тогда роль решает,
    /// что делать: убегать, драться, звать на помощь).
    /// </summary>
    public async Task<bool> HideAsync(double maxSeconds = 120, CancellationToken ct = default)
    {
        if (await GoHomeAndCloseDoorAsync(maxSeconds, ct))
            return true;
        // Ниша в стене — самое дешёвое: ломать два блока и заложить один вход.
        // Дальше по убыванию удобства: закопаться (материал из своей же ямы)
        // и, если копать нечего, обложиться коробкой
        if (await DigInAsync(ct))
            return true;
        if (await BurrowAsync(2, ct))
            return true;
        return await BuildBoxAsync(ct);
    }

    /// <summary>
    /// Уйти домой и закрыть за собой дверь.
    ///
    /// ДВЕРЬ ИЩЕТСЯ ИМЕННО ДВЕРЬЮ. Здесь стояло
    /// <c>FindNearestBlock(_ => true, …)</c> — «любой блок в трёх шагах», — а
    /// подходит под это первым воздух под ногами: у воздуха тоже есть код, и
    /// поиск честно отдавал его. Дальше <c>IsDoor</c> отвечал «нет», и ветка
    /// закрывания не срабатывала НИ РАЗУ, но строка «дома, дверь закрыта»
    /// печаталась всегда. Бот докладывал о закрытой двери, ни разу её не
    /// тронув, — а бой, который теперь спрашивает про укрытие, поверил бы этой
    /// строке ровно так же, как поверил человек.
    ///
    /// Итог проверяется ФАКТОМ ОТ СЕРВЕРА (новое состояние двери), как это
    /// делает и открывание двери в движении: клик — это переключатель, а не
    /// «закрыть», и верить ему на слово нельзя.
    /// </summary>
    public async Task<bool> GoHomeAndCloseDoorAsync(double maxSeconds = 120, CancellationToken ct = default)
    {
        if (Home is not { } home)
            return false;
        // Скобки ставит сам BlockPos.ToString — свои привели бы к «домой ((61,
        // 112, 257))», и это ровно то, что стоит в журнале заказчика 19.08
        OnLog?.Invoke($"иду домой {home}");
        if (!await ctx.Movement.TravelToAsync(home, maxSeconds, ct))
        {
            OnLog?.Invoke("до дома не дошёл");
            return false;
        }

        // Прошлая дверь к этому приходу отношения не имеет: домой можно прийти
        // и в другой дом. Забываем её ДО попытки, чтобы «спрятался» не осталось
        // правдой по памяти о позапрошлой буре
        StopHiding();
        if (ctx.Self.Position is not { } p)
            return true;   // дошёл, но где стою — не знаю; врать про дверь не буду

        var here = Feet(p);
        // Двери бот закрывает за собой сам (Movement.CloseDoorsBehind), но
        // рассчитывать на это нельзя: он мог войти в уже открытую чужую.
        // Ищем СВОЮ клетку двери — по хозяину, как её видит мир
        //
        // СТВОРКА, В ПРОЁМЕ КОТОРОЙ Я СТОЮ, НЕ БЕРЁТСЯ ВОВСЕ — и вот почему это
        // не лишняя строка. Поиск идёт кольцами ОТ НОГ, значит первой отдаёт
        // клетку под ногами: у бота, пришедшего домой и вставшего ровно в
        // проёме, «ближайшая дверь» — та самая, в которой он стоит. Закрыть её
        // значит прищемить себя, и никакого укрытия из этого не выходит.
        // Спрашивается это ОДНИМ правилом на весь проект (DoorDebt.InDoorway) —
        // тем же, которым спрашивает долг за открытые двери; у самого закрытия
        // (Movement.CloseDoorAsync) стоит та же застава, но выбрать дверь,
        // которую и правда можно закрыть, — дело того, кто выбирает
        BlockPos? выбрана = null;
        bool стоюВПроёме = false;
        foreach (var cell in ctx.World.FindBlocks(
                     (cell, _) => ctx.World.IsDoor(cell.X, cell.Y, cell.Z),
                     here, radius: 3, height: 2, limit: 8, throughWalls: true))
        {
            var хозяин = ctx.World.DoorClickPos(cell);
            if (DoorDebt.InDoorway(хозяин, p.X, p.Y, p.Z))
            {
                стоюВПроёме = true;
                continue;
            }
            выбрана = хозяин;
            break;
        }
        if (выбрана is not { } door)
        {
            // Две разные беды — и называть их одинаково нельзя: «двери нет»
            // человек лечит дверью, а «стою в единственной» — шагом в сторону
            OnLog?.Invoke(стоюВПроёме
                ? "дома, но стою в самом проёме единственной двери — закрыв её, прищемил бы " +
                  "себя, а другой створки рядом нет"
                : "дома, но двери рядом нет — от твари меня тут ничто не отделяет");
            return true;
        }

        // ЗАКРЫВАЕТ ОДИН МЕХАНИЗМ НА ВЕСЬ ПРОЕКТ. Здесь стояла вторая копия
        // закрывания: свой клик, своя полусекундная выдержка и своя проверка
        // состояния — слово в слово то же, что делает движение, отдавая долг за
        // открытые двери. Приёмка волны назвала эту копию дважды. Теперь и
        // «закрой за собой по дороге», и «уйти домой и запереться» идут в
        // Movement.CloseDoorAsync: он же не кликает по уже закрытой (клик —
        // переключатель) и он же спрашивает ответ у мира, а не у намерения.
        //
        // Взгляд на створку остаётся здесь: он про то, КУДА смотрит бот, а не
        // про дверь, и живой игрок перед тем, как закрыться от твари, на дверь
        // смотрит
        await hands.ReachForAsync(door, ct: ct);
        await ctx.Movement.CloseDoorAsync(door, ct);

        // «Закрыл» — это НЕ «кликнул»: судим по новому состоянию двери, и
        // укрытие записывается тем же одним условием, что спрашивает бой
        if (!HideBehindDoor(door))
        {
            OnLog?.Invoke($"дома, а дверь {door} не закрылась — укрытие ненадёжно");
            return true;
        }
        OnLog?.Invoke($"дома, дверь {door} закрыта");
        return true;
    }

    /// <summary>
    /// Выкопать нишу в ближайшей стене (или вниз) и замуровать себя.
    /// Бот встаёт внутрь и закладывает за собой вход.
    /// </summary>
    public async Task<bool> DigInAsync(CancellationToken ct = default)
    {
        if (ctx.Self.Position is not { } p)
            return false;
        if (AvailableWallMaterial() is not { } material)
        {
            OnLog?.Invoke("нечем заложить вход — нужен любой строительный блок");
            return false;
        }

        var me = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

        // Ищем сторону, где есть твердь: туда и копаем нишу в два блока высотой
        foreach (var (dx, dz) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            var feet = new BlockPos(me.X + dx, me.Y, me.Z + dz);
            var head = new BlockPos(feet.X, feet.Y + 1, feet.Z);
            if (!ctx.World.IsSolid(feet.X, feet.Y, feet.Z) || !ctx.World.IsSolid(head.X, head.Y, head.Z))
                continue;
            if (!mining.CanBreak(feet) || !mining.CanBreak(head))
                continue;

            OnLog?.Invoke($"копаю нишу в сторону {feet}");
            if (!(await mining.BreakAsync(feet, ct)).Success)
                continue;
            if (!(await mining.BreakAsync(head, ct)).Success)
                continue;

            // Заходим внутрь
            await ctx.Movement.MoveToCellAsync(feet, ct);
            await Task.Delay(400, ct).ContinueWith(_ => { });

            // Закладываем вход за собой (клетка, из которой пришли)
            var entry = new BlockPos(me.X, me.Y, me.Z);
            var entryTop = new BlockPos(me.X, me.Y + 1, me.Z);
            // Замурован — только если ЗАКРЫТЫ ОБЕ клетки: с открытым верхом
            // через проём проходит и бот, и то, от чего он прячется
            bool sealedLow = (await mining.PlaceAsync(entry, material, ct: ct)).Success;
            bool sealedHigh = (await mining.PlaceAsync(entryTop, material, ct: ct)).Success;
            WalledIn = sealedLow && sealedHigh;
            // Запоминаем ТОЛЬКО вставшее и ОБЕ клетки: разбирать придётся ровно
            // то, что стоит, а не то, что задумывалось
            sealedCells.Clear();
            if (sealedLow) sealedCells.Add(entry);
            if (sealedHigh) sealedCells.Add(entryTop);
            OnLog?.Invoke(WalledIn
                ? "замуровался, пережидаю"
                : $"вход заложен не полностью (низ {(sealedLow ? "да" : "нет")}, " +
                  $"верх {(sealedHigh ? "да" : "нет")}) — укрытие ненадёжно");
            return WalledIn;
        }

        OnLog?.Invoke("вокруг нет стены, в которую можно врыться");
        return false;
    }

    /// <summary>
    /// ЗАКОПАТЬСЯ. В чистом поле стены нет, зато под ногами земля: бот роет
    /// себе яму и закрывает её крышкой СВЕРХУ. Материал берётся из той же
    /// ямы — это главное: раньше бот с пустыми сумками просто бегал под
    /// молнией и повторял «нечем заложить вход».
    /// </summary>
    /// <param name="depth">На сколько блоков уйти вниз (2 — по макушку).</param>
    public async Task<bool> BurrowAsync(int depth = 2, CancellationToken ct = default)
    {
        if (ctx.Self.Position is not { } start)
            return false;
        var me = Feet(start);

        int dug = 0;
        for (int i = 0; i < depth && !ct.IsCancellationRequested; i++)
        {
            if (ctx.Self.Position is not { } now)
                break;
            var here = Feet(now);
            var under = new BlockPos(here.X, here.Y - 1, here.Z);

            // Копать имеет смысл только вниз по земле: в камень без кирки
            // и в воду закапываться нечего
            if (!ctx.World.IsSolid(under.X, under.Y, under.Z) || !mining.CanBreak(under))
                break;
            if (!(await mining.BreakAsync(under, ct)).Success)
                break;
            dug++;
            // Провалились на клетку вниз — даём телу упасть
            await Task.Delay(500, ct).ContinueWith(_ => { });
        }

        if (dug == 0)
        {
            OnLog?.Invoke("под ногами не земля — закопаться не вышло");
            return false;
        }

        // Добыча из ямы лежит рядом: подбираем ЕЁ ЖЕ и ею закрываемся
        await ctx.Pickup.CollectAsync(4, maxSeconds: 12, ct: ct, takeBody: false);

        if (ctx.Self.Position is not { } deep)
            return false;
        var bottom = Feet(deep);
        var lid = new BlockPos(bottom.X, bottom.Y + 2, bottom.Z);

        string? material = AvailableWallMaterial();
        if (material == null)
        {
            OnLog?.Invoke($"выкопал {dug} бл, но добыча не далась — крышку класть нечем");
            return false;
        }

        bool closed = (await mining.PlaceAsync(lid, material, ct: ct)).Success;
        WalledIn = closed;
        // КРЫШКА ЗАПОМИНАЕТСЯ, ТОЛЬКО ЕСЛИ ВСТАЛА. Ровно здесь и родилась беда
        // 16.08: запись стояла безусловно, крышка не встала — и разбор после
        // бури восемь минут спустя ломал воздух над головой
        sealedCells.Clear();
        if (closed) sealedCells.Add(lid);
        OnLog?.Invoke(closed
            ? $"закопался на {dug} бл и закрылся сверху ({material})"
            : $"выкопал {dug} бл, но крышка не встала — укрытие открыто сверху");
        return closed;
    }

    /// <summary>
    /// КОРОБКА ВОКРУГ СЕБЯ. Когда копать некуда (камень, лёд, чужой участок),
    /// живой игрок обкладывается блоками по кругу и накрывается сверху.
    ///
    /// Материал, которого не хватает, бот докапывает ТУТ ЖЕ — из земли под
    /// ногами по соседству, а не бежит за ним на другой конец карты.
    /// </summary>
    public async Task<bool> BuildBoxAsync(CancellationToken ct = default)
    {
        if (ctx.Self.Position is not { } p)
            return false;
        var me = Feet(p);

        // Что закрывать: четыре стороны на уровне ног и головы плюс потолок
        var need = new List<BlockPos>();
        foreach (var (dx, dz) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1)])
        {
            need.Add(new BlockPos(me.X + dx, me.Y, me.Z + dz));
            need.Add(new BlockPos(me.X + dx, me.Y + 1, me.Z + dz));
        }
        need.Add(new BlockPos(me.X, me.Y + 2, me.Z));          // крышка
        var open = need.Where(c => ctx.World.IsPassable(c.X, c.Y, c.Z)).ToList();
        if (open.Count == 0)
        {
            // ЗАКРЫТО НЕ МНОЙ — И РАЗБИРАТЬ МНЕ НЕЧЕГО. Своих клеток тут нет ни
            // одной, и врать про них нельзя: выход будет искать по стенам,
            // потолку и полу, как и положено в чужой каморке
            OnLog?.Invoke("вокруг и так закрыто");
            sealedCells.Clear();
            WalledIn = true;
            return true;
        }

        // Хватает ли блоков; чего не хватает — докопаем рядом
        if (!await HaveBlocksAsync(open.Count, ct))
        {
            OnLog?.Invoke($"на коробку нужно {open.Count} бл, а взять негде");
            return false;
        }

        int placed = 0;
        // ЧТО ПОСТАВИЛ — ТО И ЗАПОМНИЛ. Коробка не помнила НИ ОДНОЙ своей
        // клетки, и после бури бот разбирал не её, а то, что осталось в памяти
        // от предыдущей неудавшейся попытки закопаться
        sealedCells.Clear();
        foreach (var cell in open.OrderBy(c => c.Y))   // снизу вверх, крышка последней
        {
            if (ct.IsCancellationRequested)
                break;
            if (AvailableWallMaterial() is not { } material)
                break;
            if (!(await mining.PlaceAsync(cell, material, ct: ct)).Success)
                continue;
            placed++;
            sealedCells.Add(cell);
        }

        WalledIn = placed == open.Count;
        OnLog?.Invoke(WalledIn
            ? $"обложился коробкой ({placed} бл)"
            : $"коробка неполная: поставил {placed} из {open.Count}");
        return WalledIn;
    }

    /// <summary>
    /// Набрать строительных блоков ТУТ ЖЕ: копаем землю по соседству снизу,
    /// чтобы не провалиться самим, и подбираем выпавшее.
    /// </summary>
    public async Task<bool> HaveBlocksAsync(int need, CancellationToken ct = default)
    {
        if (CountWallBlocks() >= need)
            return true;
        if (ctx.Self.Position is not { } p)
            return false;
        var me = Feet(p);

        // Копаем ПОД СОСЕДНИМИ клетками: под собой копать нельзя — упадём,
        // а сбоку на уровне ног — получим дыру в будущей стене
        foreach (var (dx, dz) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (-1, -1)])
        {
            for (int down = 1; down <= 2 && CountWallBlocks() < need; down++)
            {
                if (ct.IsCancellationRequested)
                    break;
                var cell = new BlockPos(me.X + dx * 2, me.Y - down, me.Z + dz * 2);
                if (!ctx.World.IsSolid(cell.X, cell.Y, cell.Z) || !mining.CanBreak(cell))
                    continue;
                if (!(await mining.BreakAsync(cell, ct)).Success)
                    continue;
                await ctx.Pickup.CollectAsync(5, maxSeconds: 8, ct: ct, takeBody: false);
            }
            if (CountWallBlocks() >= need)
                break;
        }
        int got = CountWallBlocks();
        if (got < need)
            OnLog?.Invoke($"накопал {got} бл из нужных {need}");
        return got >= need;
    }

    /// <summary>Сколько подходящего для стройки лежит в сумках.</summary>
    private int CountWallBlocks() => WallMaterials.Sum(m => hands.CountOf(m));

    private static BlockPos Feet((double X, double Y, double Z) p) =>
        new((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));

    /// <summary>
    /// ВЫБРАТЬСЯ ИЗ УКРЫТИЯ — ПЕРЕБИРАЯ ПУТИ, А НЕ ДОЛБЯ ОДНУ ТОЧКУ.
    ///
    /// ЖИВОЙ СЛУЧАЙ, СЛОВАМИ ЗАКАЗЧИКА: «после шторма застрял — не хотел
    /// вылезать». В журнале 16.08 это выглядит так: в 14:35:41 буря кончилась,
    /// разбор не удался, и строка «[укрытие] стенка не поддалась» пошла два с
    /// половиной раза в секунду — пять тысяч триста пятьдесят девять раз, пережив
    /// даже переподключение к серверу. Причин было три, и все три чинятся здесь:
    ///
    /// 1. РАЗБИРАЛАСЬ НЕ ТА КЛЕТКА. Память о том, чем бот заложился, писалась
    ///    даже при неудачной установке (см. <see cref="sealedCells"/>), и разбор
    ///    ломал воздух. Сервер честно отвечал «тут пусто» — а мы этот ответ
    ///    ВЫБРАСЫВАЛИ и печатали безликое «стенка не поддалась».
    /// 2. ПРИЧИНА МОЛЧАЛА. Теперь каждый отказ называется словами добычи: «тут
    ///    пусто», «ломать не дают: …» (чужая заявка), «этим не сломать» (нужен
    ///    инструмент выше тиром), «не ломается» (укрепление, коренная порода).
    ///    Разобрать беду по журналу без этих слов было нельзя вовсе.
    /// 3. ПУТЬ БЫЛ ОДИН. Не поддалась своя же заложенная клетка — и всё, тупик.
    ///    Заказчик прав: «он обязан назвать беду вслух и перебрать другие пути».
    ///    Их четыре — свои клетки, любая из четырёх стен, потолок, пол, — и
    ///    перебираются они тем же правилом, что и выход наверх: приём, который
    ///    отсюда не открыл хода, вычёркивается, и бот считает заново
    ///    (<see cref="TriedWays{TWay}"/>, второй копии этого счёта в проекте нет).
    ///
    /// УСПЕХ — ТОЛЬКО ПО ФАКТУ: не «сломал блок», а «отсюда есть куда выйти»
    /// (<see cref="WayOutOpen"/>). Сломанный потолок при глухих стенах — это
    /// ещё не свобода, и говорить о ней рано.
    /// </summary>
    public async Task<ShelterExitReport> BreakOutAsync(CancellationToken ct = default)
    {
        if (!WalledIn)
            return new ShelterExitReport(true, ShelterExit.None, 0, "я не замурован");
        if (ctx.Self.Position is not { } start)
            return new ShelterExitReport(false, ShelterExit.None, 0,
                "где я стою — не знаю, а ломать вокруг себя вслепую нельзя");

        if (WayOutOpen(Feet(start)))
        {
            // Стенку могло не стать и без нас: осыпалось, сосед разобрал,
            // взорвалось. Бить в открытую дверь незачем
            Freed();
            OnLog?.Invoke("ход наружу открыт — ломать нечего");
            return new ShelterExitReport(true, ShelterExit.None, 0, "");
        }

        var ways = new TriedWays<ShelterExit>(allowance: 3, empty: "хода наружу не открыл");
        var trouble = new List<string>();
        int broken = 0;

        while (ways.MayTry && !ct.IsCancellationRequested)
        {
            if (ctx.Self.Position is not { } now)
                break;
            var me = Feet(now);
            var how = ChooseExit(me, ways.Lost);
            if (how == ShelterExit.None)
                break;

            Say($"выбираюсь наружу: {ЧтоЗаПуть(how)}");
            var (opened, dug) = await OpenAsync(how, me, trouble, ct);
            broken += dug;

            // МЕРЯЕМ ПО ФАКТУ — открывшимися клетками, а не «сломал ли блок»:
            // тот же урок, что у выхода наверх (там мерили собственной высотой)
            if (!ways.Note(how, opened))
            {
                Say(ways.SayCrossedOut());
                continue;
            }
            if (WayOutOpen(me))
            {
                Freed();
                // ВЫБРАЛСЯ — новость, а не повтор: говорится всегда и полностью
                OnLog?.Invoke($"выбрался наружу ({ЧтоЗаПуть(how)}, разобрал {broken} бл)");
                return new ShelterExitReport(true, how, broken, "");
            }
            Say($"открыл {opened} кл, а хода наружу всё нет — считаю заново");
        }

        // ОТКАЗ НАЗЫВАЕТ БЕДУ. «Не поддалась» без причины в журнале не
        // разбирается никак — на этом и погорел прошлый заход
        string why = trouble.Count > 0
            ? string.Join("; ", trouble.Distinct().Take(4))
            : "ломать вокруг нечего: всё, до чего дотягиваюсь, уже пусто или не берётся";
        string crossed = ways.CrossedOut(ЧтоЗаПуть);
        string failed = $"замурован и не выбрался (разобрал {broken} бл)" +
                        (crossed.Length > 0 ? $", вычеркнул: {crossed}" : "") + $" — {why}";
        // Само это слово наружу НЕ печатаем: оно уходит в отчёт, а вслух его
        // говорит тот, кто звал разбор (BehaviorShelterFromStorm.Trapped —
        // тревога, а не строка, и она не повторяется чаще раза в пять минут).
        // Печатать здесь значило бы говорить одно и то же дважды
        return new ShelterExitReport(false, ShelterExit.None, broken, failed);
    }

    /// <summary>Больше не замурован: и признак, и память о своей кладке.</summary>
    private void Freed()
    {
        WalledIn = false;
        sealedCells.Clear();
    }

    /// <summary>
    /// ЕСТЬ ЛИ ОТСЮДА ХОД НАРУЖУ — вопрос к МИРУ, а не к своей памяти.
    ///
    /// Ход — это либо соседняя колонна, куда тело пролезет целиком (ноги и
    /// голова), либо дыра над головой ТОЖЕ В ПОЛНЫЙ РОСТ. Одной проходимой
    /// клетки мало ни там, ни там: в щель по пояс не вылезают, и обещать
    /// свободу по ней нельзя.
    ///
    /// ПРО ВЕРХ ЭТО ПРАВИЛО СТОЯЛО НАПОЛОВИНУ, и вот чем оно кончалось. Мерка
    /// «есть куда выйти» решает не только, что печатать: по ней разбор зовёт
    /// <see cref="Freed"/> — снимает «замурован», гасит тревогу и отпускает
    /// тело. То есть на ложное «выбрался» бот ПЕРЕСТАЁТ ПЫТАТЬСЯ, и это тот же
    /// заказ («после шторма застрял»), только вывернутый наизнанку: не пять
    /// тысяч строк, а тишина в яме.
    ///
    /// Приводил сюда сам же перебор путей. Бот ломает пол, проваливается на
    /// клетку вниз — и клетка, где секунду назад была его голова, оказывается
    /// ровно «дырой над головой». На втором круге разбор докладывал «выбрался
    /// наружу (ухожу через пол)», закопав бота ГЛУБЖЕ, чем он был. Ниша в один
    /// блок над потолком (пробили крышку, а над ней порода) давала то же самое.
    /// </summary>
    public bool WayOutOpen(BlockPos me)
    {
        foreach (var (dx, dz) in Tunnel.Sides)
            if (ctx.World.IsPassable(me.X + dx, me.Y, me.Z + dz) &&
                ctx.World.IsPassable(me.X + dx, me.Y + 1, me.Z + dz))
                return true;
        return ctx.World.IsPassable(me.X, me.Y + 2, me.Z) &&
               ctx.World.IsPassable(me.X, me.Y + 3, me.Z);
    }

    /// <summary>
    /// ЧЕМ ВЫБИРАТЬСЯ ПРЯМО СЕЙЧАС. Порядок по возрастанию цены и риска: свою
    /// кладку бот сам ставил (она точно ломается тем, что у него есть, и точно
    /// не чужая), стены и потолок — уже чужая порода, пол последним, потому что
    /// проваливаться вниз в бурю хуже всего.
    ///
    /// Вычеркнутое (<paramref name="lost"/>) не предлагается: считать его силы
    /// заново значит выбрать тот же путь второй раз и снова простоять впустую.
    /// </summary>
    private ShelterExit ChooseExit(BlockPos me, IReadOnlySet<ShelterExit> lost)
    {
        foreach (var way in ShelterExits)
        {
            if (lost.Contains(way))
                continue;
            if (CellsFor(way, me).Any(c => !ctx.World.IsPassable(c.X, c.Y, c.Z)))
                return way;
        }
        return ShelterExit.None;
    }

    /// <summary>Пути наружу по возрастанию цены — порядок разбора.</summary>
    private static readonly ShelterExit[] ShelterExits =
        [ShelterExit.Own, ShelterExit.Side, ShelterExit.Ceiling, ShelterExit.Floor];

    /// <summary>Какие клетки трогает этот путь.</summary>
    private IEnumerable<BlockPos> CellsFor(ShelterExit way, BlockPos me)
    {
        switch (way)
        {
            case ShelterExit.Own:
                foreach (var cell in sealedCells)
                    yield return cell;
                break;
            case ShelterExit.Side:
                foreach (var (dx, dz) in Tunnel.Sides)
                {
                    yield return new BlockPos(me.X + dx, me.Y, me.Z + dz);
                    yield return new BlockPos(me.X + dx, me.Y + 1, me.Z + dz);
                }
                break;
            case ShelterExit.Ceiling:
                yield return new BlockPos(me.X, me.Y + 2, me.Z);
                break;
            case ShelterExit.Floor:
                yield return new BlockPos(me.X, me.Y - 1, me.Z);
                break;
        }
    }

    /// <summary>
    /// ПРОБИТЬ ЭТОТ ПУТЬ. Возвращает, сколько клеток СТАЛО ПРОХОДИМО и сколько
    /// блоков при этом сломано: первое — мерка успеха, второе — расход.
    ///
    /// СТЕНЫ ПЕРЕБИРАЮТСЯ ПО ОДНОЙ и разбор кончается на первой поддавшейся:
    /// заказчик просил именно этого — «другая стена». Ломать все четыре, когда
    /// одна уже открылась, значит без нужды разбирать чужой дом.
    /// </summary>
    private async Task<(int Opened, int Broken)> OpenAsync(ShelterExit way, BlockPos me,
        List<string> trouble, CancellationToken ct)
    {
        if (way != ShelterExit.Side)
            return await DigAsync(CellsFor(way, me), trouble, ct);

        int opened = 0, broken = 0;
        foreach (var (dx, dz) in Tunnel.Sides)
        {
            if (ct.IsCancellationRequested)
                break;
            var (o, b) = await DigAsync(
                [new BlockPos(me.X + dx, me.Y, me.Z + dz),
                 new BlockPos(me.X + dx, me.Y + 1, me.Z + dz)], trouble, ct);
            opened += o;
            broken += b;
            if (WayOutOpen(me))
                break;   // эта стена поддалась — соседние трогать незачем
        }
        return (opened, broken);
    }

    /// <summary>
    /// Разобрать клетки, называя причину КАЖДОГО отказа словами добычи.
    /// Пустые пропускаем молча: ломать воздух — это и была та самая беда.
    ///
    /// ШАХМАТКИ («ШахматкойРадиЦельныхБлоков») ЗДЕСЬ НЕТ, И ЭТО СЧЁТ, А НЕ
    /// ЗАБЫВЧИВОСТЬ. Она даёт целый блок только тому, у кого КАЖДЫЙ сосед
    /// клетки либо уже воздух, либо лежит в том же списке и уйдёт раньше:
    /// игра отдаёт целый блок лишь при всех шести свободных гранях
    /// (BreakIfFloating, разбор — в <see cref="WholeStone"/>). Укрытие ломает
    /// по ОДНОЙ-ДВЕ клетки за раз — стену сбоку, потолок, пол, свою же кладку,
    /// — и у такого списка соседи стоят нетронутой породой ВСЕГДА. Целых ноль
    /// при любом порядке.
    ///
    /// И ГЛАВНОЕ — ТУТ ДРУГАЯ ЦЕНА. Это не добыча, а выход из-под завала: бот
    /// замурован и пробует выбраться раз в двадцать секунд. Отложить клетку
    /// «ради целого блока» здесь значит отложить собственное освобождение,
    /// и заплатил бы за камень человек, чей бот сидит в норе.
    /// </summary>
    private async Task<(int Opened, int Broken)> DigAsync(IEnumerable<BlockPos> cells,
        List<string> trouble, CancellationToken ct)
    {
        int opened = 0, broken = 0;
        foreach (var cell in cells)
        {
            if (ct.IsCancellationRequested)
                break;
            if (ctx.World.IsPassable(cell.X, cell.Y, cell.Z))
                continue;

            string what = ctx.World.GetBlockCode(cell) ?? "порода";
            var result = await mining.BreakAsync(cell, ct);
            if (!result.Success)
            {
                // ПРИЧИНА ОТ ДОБЫЧИ, А НЕ НАША ДОГАДКА: она различает пустоту,
                // нехватку инструмента, чужую заявку и неломаемый блок.
                // Через счётчик повторов: замурованный бот пробует раз в двадцать
                // секунд, и одна и та же стена иначе выдала бы сотни строк
                string беда = $"{what} в {cell} не поддался: {result}";
                Say(беда);
                trouble.Add(беда);
                continue;
            }
            broken++;
            if (ctx.World.IsPassable(cell.X, cell.Y, cell.Z))
                opened++;
        }
        return (opened, broken);
    }

    /// <summary>Как назвать путь наружу человеку — словом, а не именем из кода.</summary>
    private static string ЧтоЗаПуть(ShelterExit way) => way switch
    {
        ShelterExit.Own => "разбираю свою же кладку",
        ShelterExit.Side => "ломаю стену",
        ShelterExit.Ceiling => "пробиваю потолок",
        ShelterExit.Floor => "ухожу через пол",
        _ => "никак"
    };
}

/// <summary>Каким путём выбираться из укрытия.</summary>
public enum ShelterExit
{
    /// <summary>Никаким: все пути вычеркнуты или ломать нечего.</summary>
    None,
    /// <summary>Своя же кладка — то, чем бот заложился сам.</summary>
    Own,
    /// <summary>Стена сбоку: четыре стороны, по одной.</summary>
    Side,
    /// <summary>Потолок над головой.</summary>
    Ceiling,
    /// <summary>Пол под ногами.</summary>
    Floor
}

/// <summary>
/// Чем кончился выход из укрытия. Молчаливого «не вышло» здесь быть не может:
/// именно оно и стоило заказчику пяти тысяч строк в журнале (правило 4).
/// </summary>
/// <param name="Broken">Сколько блоков разобрано.</param>
public sealed record ShelterExitReport(bool Success, ShelterExit Way, int Broken, string Why)
{
    public override string ToString() => Success
        ? (Broken > 0 ? $"выбрался наружу, разобрал {Broken} бл" : "выбираться не пришлось")
        : $"из укрытия не выбраться: {Why}";
}
