namespace VsBotKit;

/// <summary>
/// Огонь: развести костёр, подкидывать дрова, жарить еду, потушить.
///
/// Как это устроено в игре (по её исходникам, а не на глаз):
/// - костёр начинается с КУЧКИ ДРОВ на земле: 4 полена в наземное хранилище,
///   потом сухая трава с приседом — она превращает кучку в «firepit-construct1»
///   (ItemDryGrass.OnHeldInteractStart);
/// - дальше поленья достраивают кострище до готового (BlockFirepit.TryConstruct);
/// - топливо и еда кладутся правым кликом с предметом в руке;
/// - поджиг — огниво, удерживаемое дольше 3 секунд
///   (BlockEntityFirepit.GetIgnitableState: до 3 с «можно поджечь», после —
///   «поджигаю»).
///
/// Всё это — механизм. Когда бот решает греться и что жарить — дело роли.
/// </summary>
public class Fire
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Mining mining;

    public Fire(BotContext ctx, Hands hands, Mining mining)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.mining = mining;
        // Безнадёга немой быть не может (правило 4): и отказ, и отбой уходят в
        // тот же журнал, что и всё остальное про огонь
        Гиблое.OnSay += m => OnLog?.Invoke(m);
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// ЧЕГО ДЛЯ КОСТРА НЕТ И НЕ ПОЯВИЛОСЬ — чтобы не долбить это каждые
    /// двадцать секунд. Ключ — код материала («firewood», «firestarter»,
    /// «drygrass»).
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО (журнал 19.08, с 18:27:54 до
    /// 18:33:39 — шесть минут). Каждые двадцать секунд одни и те же четыре
    /// строки:
    ///     [руки] в руку брать нечего: подходящего предмета нет ни в хотбаре, ни в сумках
    ///     [огонь] дров нет
    ///     [руки] в руку брать нечего: подходящего предмета нет ни в хотбаре, ни в сумках
    ///     [огонь] нечем поджечь (нужно firestarter)
    /// Бот мёрз на 100 % (тело 31 °C — нижний зажим игры) и раз за разом шёл к
    /// кострищу, которого развести нечем. Отказ честный, а повторялся вечно —
    /// и за ним в журнале не видно было ничего другого.
    ///
    /// ВТОРОЙ КОПИИ ПРАВИЛА ТУТ НЕТ: это тот же <see cref="Hopeless{TKey}"/>,
    /// которым уже живут снабжение, подбор с земли и разделка. Глушится не
    /// строка, а сама ПОПЫТКА: без огнива бот к костру теперь и не идёт.
    ///
    /// ЗАБЫВАЕТСЯ ДВУМЯ СПОСОБАМИ, и оба честные: материал появился в сумках
    /// (спрашивается у <see cref="Hands.CountOf(string)"/> перед каждой
    /// попыткой) или вышел срок <see cref="Hopeless{TKey}.TryAgainAfter"/> —
    /// его держит роль («ПробоватьКостёрСноваЧерезМин»), потому что «сколько
    /// ждать чужого топора» библиотека не знает.
    /// </summary>
    public Hopeless Гиблое { get; } = new();

    /// <summary>
    /// «КОСТЁР НЕ СЛОЖИЛСЯ» — ТОЖЕ ОДИН РАЗ, А НЕ КАЖДЫЕ ДВАДЦАТЬ СЕКУНД.
    ///
    /// <see cref="Гиблое"/> держит нехватку МАТЕРИАЛА, и этого мало: гейты
    /// дров, растопки и огнива бот проходит с полной сумкой — и упирается уже
    /// в МЕСТО. Живой пример у копателя прямой: в штреке потолок в один блок,
    /// <see cref="MakeCampfireAsync"/> перебирает кольцо, ни одна клетка не
    /// подходит, и «не нашёл места под костёр» уходит в журнал КАЖДЫЙ заход
    /// тепла — восемнадцать строк за шесть минут, ровно та беда, ради которой
    /// заведена безнадёга, только с другого конца.
    ///
    /// ЗДЕСЬ ГЛУШИТСЯ ТОЛЬКО СЛОВО, А НЕ ПОПЫТКА, и это разница по существу.
    /// Нехватка дров не меняется от того, что бот прошёл десять шагов, — а
    /// место меняется ровно от этого. Запрети попытку, и бот, вышедший из
    /// штрека на поляну, костра бы не развёл. Перебор кольца стоит одного
    /// осмотра памяти мира, снабжение он не дёргает — платить за него молчанием
    /// не надо.
    ///
    /// ПОВОД, А НЕ ГОТОВАЯ СТРОКА: в строке перечислены пробованные клетки, и
    /// они меняются с каждым шагом бота. Сравнивай <see cref="Alarm"/> строку —
    /// и любое дрожание координат считалось бы новостью (той же ценой уже
    /// платила добыча с убитых, где в строке тикало расстояние до тела).
    /// </summary>
    private readonly Alarm неСложился = new("костёр");

    /// <summary>Код дров (топливо и материал кострища).</summary>
    public string FirewoodCode { get; set; } = "firewood";

    /// <summary>Код растопки: сухая трава.</summary>
    public string TinderCode { get; set; } = "drygrass";

    /// <summary>Чем поджигаем (огниво; факел тоже годится).</summary>
    public string IgniterCode { get; set; } = "firestarter";

    /// <summary>
    /// Сколько держать огниво. Порог игры — «дольше трёх секунд», и держать
    /// ровно 3,5 рискованно: сервер считает секунды по своим часам, а сеть
    /// может съесть полсекунды. Живьём попадались длинные полосы неудач
    /// подряд, чего при честной четверти шанса почти не бывает.
    /// </summary>
    public double IgniteSeconds { get; set; } = 4.2;

    /// <summary>
    /// Сколько раз крутить огниво. Каждый заход зажигает с шансом 1/4
    /// (ItemFirestarter кидает кубик на отпускании), поэтому двадцать попыток —
    /// это 99,7 % успеха. Живой игрок крутит его ровно так же, пока не займётся.
    /// </summary>
    public int IgniteAttempts { get; set; } = 20;

    /// <summary>
    /// ХОДИТЬ ЛИ ЗА ЭТИМ ВООБЩЕ: true — уже сдались, и с тех пор ничего не
    /// изменилось.
    ///
    /// Обстановка меняется РОВНО ОДНИМ способом, который бот видит сам: нужного
    /// стало ДОСТАТОЧНО (хозяин положил, снабжение принесло, подобрали с
    /// земли). Спрашиваем об этом ПЕРЕД каждой попыткой — тогда отбой звучит в
    /// тот же миг, а не в конце отпущенного срока.
    /// </summary>
    /// <param name="need">
    /// СКОЛЬКО НАДО, а не «хоть сколько-нибудь». Мерка тут не придирка: на
    /// новое кострище уходит ЧЕТЫРЕ полена, а в горящий костёр — одно. Спроси
    /// здесь «есть ли хоть одно», и бот с единственным поленом в сумке считал
    /// бы обстановку изменившейся каждые двадцать секунд — то есть заново гонял
    /// бы всю цепочку снабжения (осмотр округи, опись склада, реестр рецептов)
    /// ради того же самого отказа.
    /// </param>
    private bool НетИНеПоявилось(string code, int need = 1)
    {
        if (hands.CountOf(code) >= need)
        {
            Гиблое.TryAgain(code, "нашлось — пробую снова");
            return false;
        }
        return Гиблое.Given(code);
    }

    /// <summary>
    /// ВЗЯТЬ МАТЕРИАЛ В РУКУ ИЛИ СДАТЬСЯ — один вход на все места, где костру
    /// нужны дрова и огниво.
    ///
    /// Слова отказа про один и тот же код держим ОДНИ И ТЕ ЖЕ нарочно:
    /// <see cref="Alarm"/> внутри безнадёги считает сменившуюся причину
    /// новостью и говорит её сразу, так что «дров нет» и «нечем достраивать —
    /// дров нет» вернули бы половину того самого шума.
    /// </summary>
    private async Task<bool> ВРукуAsync(string code, string отказ, CancellationToken ct)
    {
        if (НетИНеПоявилось(code))
            return false;
        if (await hands.TakeToHandAsync(code, ct) is not null)
            return true;
        Гиблое.GiveUp(code, отказ);
        return false;
    }

    /// <summary>Отказ про дрова — одними словами и на достройку, и на топливо.</summary>
    private const string ДровНет = "дров нет";

    /// <summary>
    /// Сколько поленьев уходит на новое кострище. Число игры
    /// (BlockFirepit.TryConstruct добирает стадии до четвёртой), и стоит оно
    /// здесь ОДИН раз: по нему и запасаются, и решают, изменилась ли обстановка.
    /// </summary>
    private const int ПоленНаКострище = 4;

    /// <summary>
    /// Готовый костёр рядом. Именно ГОТОВЫЙ: недостроенная кучка тоже
    /// называется «firepit-construct2», но в неё нельзя ни положить дров, ни
    /// поджечь. Живьём бот на такую наткнулся и полминуты честно крутил над
    /// ней огниво.
    /// </summary>
    public BlockPos? NearestFirepit(double radius = 12) => NearestFirepit(radius, finishedOnly: true);

    /// <summary>Костёр рядом; <paramref name="finishedOnly"/> = false — считая стройку.</summary>
    public BlockPos? NearestFirepit(double radius, bool finishedOnly)
    {
        if (ctx.Self.Position is not { } p)
            return null;
        var here = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        // Костёр узнаём по КЛАССУ из реестра (см. IsFirepitCode), а не по
        // рукописному префиксу кода: класс присылает сервер, префикс сочинили мы
        return ctx.World.FindNearestBlock(
            c => IsFirepitCode(c) &&
                 (!finishedOnly || !c.Contains("construct", StringComparison.OrdinalIgnoreCase)),
            here, (int)radius, height: 3);
    }

    /// <summary>
    /// Достроить брошенное кострище поленьями. Бывает своё же, недоделанное:
    /// бросать его жалко — четыре полена уже вложены.
    /// </summary>
    public async Task<bool> FinishFirepitAsync(BlockPos at, CancellationToken ct = default)
    {
        if (IsFinished(at))
            return true;
        if (!IsFirepit(at))
            return false;
        if (!await ВРукуAsync(FirewoodCode, ДровНет, ct))
            return false;
        for (int i = 0; i < 5 && !IsFinished(at) && !ct.IsCancellationRequested; i++)
        {
            if (!await hands.ReachForAsync(at, ct: ct))
                break;
            await hands.UseBlockAsync(at, ct: ct);
            await Task.Delay(400, ct).ContinueWith(_ => { });
        }
        bool ok = IsFinished(at);
        OnLog?.Invoke(ok ? $"достроил кострище: {ctx.World.GetBlockCode(at)}"
                         : $"достроить не вышло, стадия {ctx.World.GetBlockCode(at)}");
        return ok;
    }

    /// <summary>
    /// ГОРИТ ЛИ КОСТЁР — ТЕМ ЖЕ ПРАВИЛОМ, ЧТО РЕШАЕТ, ЖЖЁТ ЛИ ЭТА КЛЕТКА.
    ///
    /// Здесь стояла своя проверка — <c>code.Contains("lit")</c>, — и это была
    /// вторая копия правила с известной дырой: «lit» подстрокой сидит и в
    /// «monolith», и в «lightbulb». Ровно от неё
    /// <see cref="HarmRules.IsLit"/> и заводился: он читает ЗНАЧЕНИЕ ВАРИАНТА
    /// (<c>burnstate</c> в firepit.json — construct1…4, extinct, lit, cold),
    /// как читает его сама игра. Разойдись эти два ответа — бот считал бы
    /// костёр погасшим и лез бы в него греться, а правило вреда в тот же миг
    /// звало бы его оттуда уходить.
    /// </summary>
    public bool IsBurning(BlockPos pos) =>
        HarmRules.IsLit(ctx.World.GetBlockCode(pos));

    /// <summary>
    /// КОСТЁР ЛИ ЭТО ВООБЩЕ — ПО КЛАССУ БЛОКА ИЗ РЕЕСТРА СЕРВЕРА.
    ///
    /// Здесь стоял рукописный префикс <c>code.StartsWith("firepit")</c>
    /// (закон 6). Имя класса присылает сам сервер полем
    /// <c>Packet_BlockType.Blockclass</c>, взяв его из своей таблицы
    /// <c>BlockClassToTypeMapping</c>, — это слово игры, а не наше, и модовый
    /// костёр на классе <c>BlockFirepit</c> попадёт сюда сам.
    /// </summary>
    public bool IsFirepit(BlockPos pos) => IsFirepitCode(ctx.World.GetBlockCode(pos));

    private bool IsFirepitCode(string? code) =>
        code is { Length: > 0 } &&
        ctx.World.GetGameBlockByCode(code)?.Class == HarmRules.FirepitClass;

    /// <summary>
    /// Кострище готово, а не стадия стройки.
    ///
    /// «construct» тут остаётся подстрокой нарочно, и это не недосмотр: у
    /// стройки ЧЕТЫРЕ значения варианта (construct1…construct4), и сама игра
    /// отбирает их такой же маской — <c>behaviorsByType: "*-construct*"</c> в
    /// firepit.json. Класс блока у стройки тот же <c>BlockFirepit</c>, отличить
    /// её можно только по варианту.
    /// </summary>
    public bool IsFinished(BlockPos pos) =>
        ctx.World.GetBlockCode(pos) is { } c && IsFirepitCode(c) &&
        !c.Contains("construct", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Сложить костёр на указанной клетке: кучка дров → растопка → достройка.
    /// Возвращает позицию готового кострища или null с объяснением в логе.
    /// </summary>
    /// <remarks>
    /// Рецепт взят из ItemDryGrass.OnHeldInteractStart: сухая трава в приседе
    /// кликается по ОПОРЕ, а кострище появляется в клетке НАД точкой клика
    /// (`val = blockSel.Position + Face`, опора проверяется под `val`).
    /// Прежняя версия кликала по пустой клетке и сначала городила кучку дров —
    /// в игре так делается угольная яма, а не костёр, и не получалось ничего.
    /// </remarks>
    public async Task<BlockPos?> BuildFirepitAsync(BlockPos at, CancellationToken ct = default)
    {
        var support = new BlockPos(at.X, at.Y - 1, at.Z);
        if (!ctx.World.IsSolid(support.X, support.Y, support.Z))
        {
            OnLog?.Invoke($"под костром нет твёрдой опоры ({ctx.World.GetBlockCode(support) ?? "пусто"})");
            return null;
        }
        if (ctx.World.GetBlockId(at.X, at.Y, at.Z) != 0)
        {
            // Трава и цветы — не препятствие, их просто срезают. На лугу
            // свободной клетки не найти вовсе, и бот честно, но бесполезно
            // докладывал «место занято» посреди чистого поля
            string occupant = ctx.World.GetBlockCode(at) ?? "?";
            // Сначала подойти: место могло найтись под горой, и «не убрать
            // траву» означало бы всего лишь «не дотянулся»
            if (!await hands.ReachForAsync(at, ct: ct))
                return null;
            if (!ctx.World.IsPassable(at.X, at.Y, at.Z))
            {
                OnLog?.Invoke($"клетка под костёр занята: {occupant}");
                return null;
            }
            var cleared = await mining.BreakAsync(at, ct);
            if (!cleared.Success)
            {
                OnLog?.Invoke($"не убрать {occupant} с места под костёр: {cleared}");
                return null;
            }
            OnLog?.Invoke($"расчистил место: убрал {occupant}");
        }
        // ТОПЛИВА НЕТ — ЭТО НЕ ПРИГОВОР, А ЗАДАЧА СНАБЖЕНИЮ.
        //
        // Слова заказчика про жизненные нужды: «может добыть здесь или надо
        // домой за ними (если они там есть)». Мёрзнущий бот отвечал «нет
        // растопки (drygrass) — костёр не начать» и оставался мёрзнуть, при том
        // что и трава растёт кругом, и поленья лежат дома. Решает это ОДНА
        // цепочка на весь бот (сумки → склад работы → дом → крафт → добыть
        // рядом), а не своя вылазка за дровами: поленья пилят из брёвен, трава
        // косится в поле, и всё это уже умеет снабжение.
        if (!await ЗапастисьAsync(TinderCode, 1, "растопка", ct))
            return null;
        if (!await ЗапастисьAsync(FirewoodCode, ПоленНаКострище, "поленья", ct))
            return null;
        if (!await hands.ReachForAsync(support, ct: ct))
            return null;

        // 1. Сухая трава в приседе по ВЕРХНЕЙ ГРАНИ опоры → firepit-construct1 в at
        if (await hands.TakeToHandAsync(TinderCode, ct) is null)
            return null;
        await hands.UseHeldAsync(0.1, support, onFace: 4, sneak: true, ct);
        await Task.Delay(600, ct).ContinueWith(_ => { });

        if (!IsFirepit(at))
        {
            OnLog?.Invoke($"кострище не появилось (в клетке {ctx.World.GetBlockCode(at) ?? "пусто"})");
            return null;
        }

        // 2. Достраиваем поленьями до готового (BlockFirepit.TryConstruct).
        // Подходим ПЕРЕД КАЖДЫМ поленом: кострище появляется на клетку выше
        // опоры, и до него бывает дальше руки, хотя до опоры бот дотянулся.
        // Ловилось живьём — «не кликаю по (…,114,…): далеко: 5,3 бл» пять раз
        // подряд и кострище, застрявшее на firepit-construct1
        if (await hands.TakeToHandAsync(FirewoodCode, ct) is not null)
            for (int i = 0; i < 5 && !IsFinished(at) && !ct.IsCancellationRequested; i++)
            {
                if (!await hands.ReachForAsync(at, ct: ct))
                    break;
                await hands.UseBlockAsync(at, ct: ct);
                await Task.Delay(400, ct).ContinueWith(_ => { });
            }

        // Правило 4: докладываем об успехе только по факту
        if (!IsFinished(at))
        {
            OnLog?.Invoke($"кострище не достроено, стадия: {ctx.World.GetBlockCode(at)}");
            return null;
        }
        OnLog?.Invoke($"кострище сложено: {ctx.World.GetBlockCode(at)}");
        return at;
    }

    /// <summary>
    /// Подложить топливо (полено) в костёр. ТОЛЬКО В ПРИСЕДЕ: BlockFirepit
    /// принимает предмет в слот топлива лишь при зажатом Shift, иначе клик
    /// просто открывает диалог. Успех — по убыли дров у бота.
    /// </summary>
    public async Task<bool> AddFuelAsync(BlockPos pit, int pieces = 1, CancellationToken ct = default)
    {
        if (!await ВРукуAsync(FirewoodCode, ДровНет, ct))
            return false;
        if (!await hands.ReachForAsync(pit, ct: ct))
            return false;

        int before = hands.CountOf(FirewoodCode);
        for (int i = 0; i < pieces && !ct.IsCancellationRequested; i++)
        {
            await hands.UseBlockAsync(pit, sneak: true, ct: ct);
            await Task.Delay(400, ct).ContinueWith(_ => { });
        }
        int spent = before - hands.CountOf(FirewoodCode);
        if (spent <= 0)
        {
            OnLog?.Invoke("костёр не принял дрова");
            return false;
        }
        OnLog?.Invoke($"подложил дров: {spent}");
        return true;
    }

    /// <summary>
    /// Поджечь костёр огнивом. Держим кнопку дольше трёх секунд — ровно как
    /// игрок, и проверяем по коду блока, что огонь занялся.
    /// </summary>
    public async Task<bool> IgniteAsync(BlockPos pit, CancellationToken ct = default)
    {
        if (IsBurning(pit))
            return true;
        // НЕЧЕМ ПОДЖИГАТЬ — К КОСТРУ И НЕ ИДЁМ. Спрашиваем ПЕРВЫМ ДЕЛОМ, до
        // достройки и до дороги: живьём 19.08 бот каждые двадцать секунд шёл
        // к кострищу, доставал пустую руку и уходил — шесть минут подряд.
        // Дорога стоит секунд, а ответ «огнива нет» известен заранее
        if (НетИНеПоявилось(IgniterCode))
            return false;
        // Недостроенная кучка огня не примет — сначала достроить
        if (!IsFinished(pit) && !await FinishFirepitAsync(pit, ct))
            return false;
        // Без топлива огонь не займётся — игра прямо запрещает поджиг.
        // Дрова и огниво берутся ДО подхода: обе руки пустые — идти незачем
        await AddFuelAsync(pit, 1, ct);

        // Код огнива в строку не вписываем: он и так стоит в ней ключом
        // безнадёги — «firestarter: нечем поджечь»
        if (!await ВРукуAsync(IgniterCode, "нечем поджечь", ct))
            return false;
        if (!await hands.ReachForAsync(pit, ct: ct))
            return false;
        // ОГНИВО — ЭТО БРОСОК КУБИКА. В ItemFirestarter.OnHeldInteractStop
        // сервер сначала кидает Rand.NextDouble() > 0.25 и в трёх случаях из
        // четырёх молча выходит. Живой игрок этого не замечает — он просто
        // крутит огниво снова и снова. Бот должен делать то же самое, иначе
        // честный однократный заход почти всегда даёт «поджечь не вышло»
        for (int attempt = 1; attempt <= IgniteAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
                break;
            await hands.UseHeldAsync(IgniteSeconds, pit, ct: ct);
            await Task.Delay(600, ct).ContinueWith(_ => { });
            if (IsBurning(pit))
            {
                OnLog?.Invoke($"костёр горит (с {attempt}-го раза)");
                return true;
            }
        }
        OnLog?.Invoke($"поджечь не вышло за {IgniteAttempts} попыток: {ctx.World.GetBlockCode(pit) ?? "пусто"}");
        return false;
    }

    /// <summary>
    /// Развести костёр рядом с собой: найти место, сложить, поджечь.
    ///
    /// ПОДЖИГАЕТ ЗДЕСЬ, И ВТОРОГО РАЗА НЕ НАДО. Живьём 19.08 в 00:12:53
    /// «нечем поджечь (нужно firestarter)» вышло ДВАЖДЫ подряд за одну
    /// миллисекунду: костёр сложился, здесь его подожгли, а
    /// <see cref="BehaviorKeepWarm"/> следом позвал поджиг ещё раз — на всякий
    /// случай. Тепло теперь поджигает только НАЙДЕННЫЙ чужой костёр, а своему
    /// свежесложенному верит; у кухни тот же зов остался, но он про костёр,
    /// который ей ПЕРЕДАЛИ готовым, и повтором быть не может — а если всё же
    /// придётся, безнадёга (<see cref="Гиблое"/>) ответит молча и мгновенно.
    /// </summary>
    public async Task<BlockPos?> MakeCampfireAsync(CancellationToken ct = default)
    {
        // Своя же брошенная стройка рядом дороже нового места: в неё уже
        // вложены полено и растопка, достроить дешевле, чем начинать заново
        if (NearestFirepit(6, finishedOnly: false) is { } existing)
        {
            if (IsFinished(existing) || await FinishFirepitAsync(existing, ct))
            {
                неСложился.Clear();                        // кострище есть — прошлый отказ не правда
                if (!IsBurning(existing))
                    await IgniteAsync(existing, ct);
                return existing;
            }
        }
        if (ctx.Self.Position is not { } p)
            return null;

        // НЕЧЕМ СКЛАДЫВАТЬ — НЕ БРОДИТЬ ПО КЛЕТКАМ. Стоит именно ЗДЕСЬ, после
        // брошенной стройки: ту достраивают одними поленьями, растопка в неё
        // уже вложена. А новое кострище без травы и четырёх полен не начать
        // вовсе, и без этой проверки бот перебирал три места вокруг себя,
        // у каждого дёргал снабжение и выдавал «костёр не сложился, пробовал
        // места: …» каждые двадцать секунд
        if (НетИНеПоявилось(TinderCode) || НетИНеПоявилось(FirewoodCode, ПоленНаКострище))
            return null;

        // Место под костёр: клетка, куда можно поставить блок (пустая ИЛИ с
        // травой-цветком — их игра заменяет), и твёрдая опора под ней.
        // Требовать строго «воздух» нельзя: бот стоит на лугу, вокруг трава,
        // и он честно докладывал «не нашёл места» посреди чистого поля
        var me = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        var tried = new List<string>();
        foreach (var (dx, dz) in Ring())
        {
            int x = me.X + dx, z = me.Z + dz;
            // Высоту НЕ берём от своей: ноги бота могут стоять на доли блока
            // ниже границы клетки, и тогда «своя» высота — это сам пол.
            // Спрашиваем у мира, где в этой колонне стоят
            if (ctx.World.FindStandableY(x, me.Y + 1, z, up: 1, down: 3) is not { } y)
                continue;
            var spot = new BlockPos(x, y, z);
            if (!ctx.World.IsPassable(spot.X, spot.Y + 1, spot.Z))
                continue;                                  // низкий потолок
            if (ctx.World.IsWater(spot.X, spot.Y, spot.Z))
                continue;                                  // в воде костёр не горит

            tried.Add(spot.ToString());
            if (await BuildFirepitAsync(spot, ct) is { } pit)
            {
                неСложился.Clear();                        // сложился — молча, про успех сказано выше
                await IgniteAsync(pit, ct);
                return pit;
            }
            if (tried.Count >= 3)
                break;                                     // три отказа подряд — дело не в месте
        }
        // ПОВОД СТАБИЛЬНЫЙ, СТРОКА — С МЕСТАМИ (см. неСложился)
        if (неСложился.Raise(tried.Count == 0 ? "места нет" : "не сложился"))
            OnLog?.Invoke(tried.Count == 0
                ? "не нашёл места под костёр: вокруг нет ровной клетки с опорой"
                : $"костёр не сложился, пробовал места: {string.Join(", ", tried)}");
        return null;
    }

    /// <summary>
    /// ХВАТАЕТ ЛИ ЭТОГО, А НЕТ — ВЗЯТЬ ПО ЦЕПОЧКЕ СНАБЖЕНИЯ. true — хватает
    /// (и после похода тоже); false — не вышло, и об этом СКАЗАНО отказом
    /// безнадёги, который называет каждое оборвавшееся звено.
    ///
    /// Своего похода тут нет ни строчки: <see cref="Supply"/> — один механизм
    /// на всех, кому нужен материал, и «сходить ли домой» решает он.
    ///
    /// ОТКАЗ ГОВОРИТСЯ ЗДЕСЬ, А НЕ У ВЫЗЫВАЮЩЕГО. Раньше цепочка возвращала
    /// причину строкой, а печатал её <see cref="BuildFirepitAsync"/> — и
    /// печатал КАЖДЫЙ раз, потому что про заглушку повторов там не знали.
    /// Теперь и слово, и молчание держит одна безнадёга.
    /// </summary>
    private async Task<bool> ЗапастисьAsync(string code, int need, string чего,
        CancellationToken ct)
    {
        // РАЗ ЦЕПОЧКА УЖЕ СХОДИЛА ВПУСТУЮ — не гонять её каждые двадцать
        // секунд: она осматривает округу, читает опись склада и лезет в реестр
        // рецептов, то есть тратит те самые секунды, которых мёрзнущему боту
        // и не хватает. «Набралось ли» меряется НУЖНЫМ числом (см. довод need)
        if (НетИНеПоявилось(code, need))
            return false;
        if (hands.CountOf(code) >= need)
            return true;
        if (ctx.Supply is not { } снабжение)
        {
            Гиблое.GiveUp(code, $"{чего}: снабжение к боту не подключено — принесите сами");
            return false;
        }

        // ИМЯ ДЕЛА ДЛЯ ЦЕПОЧКИ КОРОТКОЕ («растопка», «поленья») — оно уходит в
        // её собственные строки; длинный отказ собирается из него ниже
        OnLog?.Invoke($"{чего}: не хватает {code} ×{need - hands.CountOf(code)} — ищу");
        var итог = await снабжение.ForVitalAsync(чего, c => string.Equals(
                Stock.WithoutDomain(c), Stock.WithoutDomain(code),
                StringComparison.OrdinalIgnoreCase) ||
                Stock.InFamily(c, code), need, ct);
        // УСПЕХ ТОЛЬКО ПО ФАКТУ: смотрим в сумку, а не на бодрый ответ цепочки
        if (hands.CountOf(code) >= need)
            return true;
        Гиблое.GiveUp(code, $"{чего} для костра: надо {need}, есть {hands.CountOf(code)} — {итог.Reason}");
        return false;
    }

    /// <summary>Клетки вокруг бота, от ближних к дальним.</summary>
    private static IEnumerable<(int dx, int dz)> Ring()
    {
        for (int r = 1; r <= 3; r++)
            for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                    if (Math.Abs(dx) == r || Math.Abs(dz) == r)
                        yield return (dx, dz);
    }

    // ВТОРОЙ ГОТОВКИ ЗДЕСЬ БОЛЬШЕ НЕТ (правило 2: одна механика в одном
    // месте). До 26.08 тут жил свой CookAsync — «положить в костёр и
    // подождать», — которого НЕ ЗВАЛ НИКТО во всём дереве, и он ещё и врал:
    // в нём стояла строка «еда в костре; забрать её бот пока не умеет —
    // нужен перенос из инвентаря костра». Неправда с тех пор, как появился
    // Cooking.TakeOutAsync. Готовка живёт в Cooking, и «приготовил» она
    // говорит только тогда, когда готовое лежит в сумке.

    /// <summary>Потушить: разобрать горящий костёр (в игре огонь гаснет сам без топлива).</summary>
    public async Task<bool> ExtinguishAsync(BlockPos pit, CancellationToken ct = default) =>
        (await mining.BreakAsync(pit, ct)).Success;
}
