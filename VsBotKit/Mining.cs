using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace VsBotKit;

/// <summary>Почему блок не сломать.</summary>
public enum MiningRefusal
{
    None,
    /// <summary>Клетка пустая — ломать нечего.</summary>
    Nothing,
    /// <summary>Не дотянуться и не удалось подойти.</summary>
    OutOfReach,
    /// <summary>Нужен инструмент выше тиром (голыми руками камень не берётся).</summary>
    NeedBetterTool,
    /// <summary>Блок не поддался: приват, защита, сервер отклонил.</summary>
    Rejected,

    /// <summary>
    /// ПОЩАДИЛ: это чужая постройка, двор хозяина или блок, чей хозяин мне
    /// непонятен. Отличается от <see cref="Claimed"/> тем, что сервер тут
    /// ничего не запрещал — не стал сам бот, и у отказа есть ИМЯ РУЧКИ,
    /// которой человек может его снять.
    /// </summary>
    Spared,
    /// <summary>Блок не ломается вовсе: игра ставит таким сопротивление 99999.</summary>
    Unbreakable,

    // ---- отказы установки: разные причины лечатся по-разному ----

    /// <summary>
    /// Клетка занята блоком, который не заменить. Повторять бессмысленно —
    /// сначала надо сломать то, что там стоит (сервер отвечает «notreplaceable»).
    /// </summary>
    Occupied,

    /// <summary>
    /// Блок встал бы В САМОГО БОТА: его коробка накрывает тело
    /// (сервер отвечает «entityintersecting»). Лечится шагом в сторону.
    /// </summary>
    SelfInTheWay,

    /// <summary>
    /// Ответа от сервера не было вовсе. Так выглядит и лаг сети, и потерянный
    /// пакет — то есть причина ПОПРАВИМАЯ, и повторить стоит.
    /// </summary>
    NoAnswer,

    /// <summary>
    /// Сервер сказал прямо: тут нет права строить и ломать (чужая заявка).
    /// Повторять бессмысленно, сколько ни пробуй.
    /// </summary>
    Claimed
}

/// <summary>
/// Что делать, когда инструмент забрали из руки посреди удара
/// (см. <see cref="Mining.WhenHandTaken"/>).
/// </summary>
public enum HandTheft
{
    /// <summary>Рука та же — бить дальше.</summary>
    None,
    /// <summary>Взять инструмент обратно и начать удар заново.</summary>
    Retake,
    /// <summary>Забирают снова — честно бросить удар, а не домахивать оружием.</summary>
    Drop
}

/// <summary>Результат добычи.</summary>
public sealed record MiningResult(bool Success, MiningRefusal Why = MiningRefusal.None, string? Message = null)
{
    public override string ToString() => Success ? "сломан" : (Message ?? Why.ToString());
}

/// <summary>Самый быстрый известный игре инструмент под материал.</summary>
/// <param name="Code">Код предмета ("shovel-steel").</param>
/// <param name="Kind">Как назвать его по-человечески ("лопатой").</param>
/// <param name="Speed">Скорость добычи этого материала им.</param>
public readonly record struct KnownTool(string Code, string Kind, float Speed);

/// <summary>
/// ЧЕМ БОТ БЕРЁТ БЛОК И ЧЕМ СТОИЛО БЫ. Чистая запись: ни мира, ни сервера —
/// только коды, секунды и признак «есть ли оно у меня».
///
/// Ради неё всё и затевалось. Живой случай (журнал 23:40–23:41): ход в семь
/// клеток, четырнадцать блоков земли, тридцать секунд. В журнале раз за разом
/// стояло «ломаю soil-low-none: 1,8 с (blade-blackguard-iron)» — то есть бот
/// копал землю МЕЧОМ. Лопатой та же клетка берётся за 0,3 с, вшестеро быстрее,
/// а строка выглядела как норма: про инструмент в ней не было ни слова упрёка.
/// Молчаливый убыток — тот же молчаливый отказ, который запрещает правило 3.
/// </summary>
/// <param name="Block">Код блока.</param>
/// <param name="InHand">Что реально в руке ("рукой", если пусто).</param>
/// <param name="Seconds">Сколько держать кнопку тем, что в руке.</param>
/// <param name="Better">Код предмета, которым было бы быстрее (null — быстрее нечем).</param>
/// <param name="Kind">Человеческое имя этого предмета ("лопатой").</param>
/// <param name="BetterSeconds">Сколько держать кнопку им.</param>
/// <param name="Have">Он лежит в сумках (но в руку не встал), а не «его нет вовсе».</param>
/// <param name="Also">Сколько блоков уже прошло с той же жалобой (0 — первая).</param>
public readonly record struct ToolVerdict(string Block, string InHand, double Seconds,
    string? Better, string? Kind, double BetterSeconds, bool Have, int Also = 0)
{
    /// <summary>Во сколько раз правильный инструмент быстрее того, что в руке.</summary>
    public double Ratio => BetterSeconds > 0 && Seconds > 0 ? Seconds / BetterSeconds : 1;

    /// <summary>
    /// Сколько секунд на этом блоке потеряно из-за инструмента. Когда сравнить
    /// не с чем, потери нет: «столько же, сколько лучшим из возможных» — это
    /// ноль, а не всё время удара.
    /// </summary>
    public double Lost => Better is { Length: > 0 } ? Math.Max(0, Seconds - BetterSeconds) : 0;

    /// <summary>
    /// Стоит ли жаловаться. Два условия сразу, и оба нужны: во сколько РАЗ
    /// дольше и сколько СЕКУНД потеряно. Без первого бот молчал бы про землю
    /// мечом (6 раз, но всего 1,5 с потери на клетку — если смотреть на секунды
    /// поодиночке, кажется мелочью), без второго заваливал бы журнал воплями
    /// про траву: рвать её ножом вчетверо быстрее, чем рукой, а разница — сотые
    /// доли секунды, и живой игрок ради неё в сумку не полезет.
    /// </summary>
    public bool Slow(double warnRatio, double minLost) =>
        Better is { Length: > 0 } && Ratio >= warnRatio && Lost >= minLost;

    /// <summary>Жалоба словами: что в руке, что надо и насколько это дороже.</summary>
    public string Say()
    {
        string tool = Kind is { Length: > 0 } k ? $"{k} ({Better})" : $"{Better}";
        string have = Have
            ? "он у меня есть, но в руку не встал"
            : "а его у меня нет";
        string also = Also > 0 ? $"; так уже {Also + 1} блоков подряд" : "";
        return $"{Block}: в руке {InHand} — {Seconds:0.#} с; {tool} это {BetterSeconds:0.#} с, " +
               $"быстрее в {Ratio:0.#} раз, {have}{also}";
    }

    public override string ToString() => Say();
}

/// <summary>
/// СЧЁТ НАКЛАДНЫХ РАСХОДОВ: сколько бот честно держал кнопку и сколько ушло на
/// всё остальное — смену руки, поворот, подход, подтверждение от сервера.
///
/// Заказчик сказал «копает в разы дольше живого игрока, потому что не соседние
/// блоки и долго между ними переключается». Спорить об этом на глаз
/// бессмысленно: плохой инструмент и возня между ударами выглядят одинаково,
/// а лечатся по-разному. Здесь это не мнение, а два числа рядом.
/// </summary>
public sealed class MiningTally
{
    /// <summary>Сколько блоков ломали (и удачно, и нет).</summary>
    public int Blocks { get; private set; }

    /// <summary>Сколько раз пришлось менять предмет в руке.</summary>
    public int Swaps { get; private set; }

    /// <summary>Сколько раз пришлось идти к блоку, а не бить с места.</summary>
    public int Walks { get; private set; }

    /// <summary>Секунды, которые игра требует держать кнопку.</summary>
    public double Digging { get; private set; }

    /// <summary>Всё время от входа в добычу до ответа сервера.</summary>
    public double Total { get; private set; }

    /// <summary>Всё, что не удары: смена руки, поворот, подход, подтверждение.</summary>
    public double Overhead => Math.Max(0, Total - Digging);

    /// <summary>Доля накладных расходов, 0..1.</summary>
    public double OverheadShare => Total > 0 ? Overhead / Total : 0;

    public void Note(double digging, double total, bool swapped, bool walked)
    {
        Blocks++;
        Digging += Math.Max(0, digging);
        Total += Math.Max(0, total);
        if (swapped) Swaps++;
        if (walked) Walks++;
    }

    public void Reset()
    {
        Blocks = Swaps = Walks = 0;
        Digging = Total = 0;
    }

    public override string ToString() => Blocks == 0
        ? "блоков не ломали"
        : $"блоков {Blocks}: удары {Digging:0.#} с, накладные {Overhead:0.#} с " +
          $"({OverheadShare * 100:0} %), смен руки {Swaps}, подходов {Walks}";
}

/// <summary>Что решено про блок, стоящий на дороге.</summary>
public enum GrowthVerdict
{
    /// <summary>Мягкое и быстрое — это подлесок, ломаем.</summary>
    Soft,
    /// <summary>Клетка пуста — ломать нечего.</summary>
    Empty,
    /// <summary>Материал не подлесковый: камень, бревно, руда, стекло.</summary>
    Hard,
    /// <summary>Материал подходит, а возни дольше порога — это уже не подлесок.</summary>
    Slow,
    /// <summary>Голыми руками не берётся — нужен инструмент тира.</summary>
    NeedsTool,
    /// <summary>Подлесок, но беречь: грядка, саженец, ягодник.</summary>
    Spared,
    /// <summary>Расчистка выключена настройкой роли.</summary>
    Disabled,
    /// <summary>Не дотянуться, а уходить с дороги ради этого не станем.</summary>
    OutOfReach,
    /// <summary>За этот раз уже сломали сколько положено.</summary>
    Enough,
    /// <summary>Ударили, а блок остался: сервер не принял или помешали.</summary>
    Failed
}

/// <summary>Что стало с одной клеткой на дороге. Причина обязательна.</summary>
public readonly record struct GrowthNote(BlockPos Pos, string Code, GrowthVerdict Verdict,
    bool Broken = false, string? Message = null)
{
    /// <summary>Человеческая причина, если своей не написали.</summary>
    public static string Reason(GrowthVerdict verdict) => verdict switch
    {
        GrowthVerdict.Soft => "подлесок",
        GrowthVerdict.Empty => "пусто",
        GrowthVerdict.Hard => "не подлесок",
        GrowthVerdict.Slow => "ломать слишком долго",
        GrowthVerdict.NeedsTool => "руками не берётся",
        GrowthVerdict.Spared => "берегу",
        GrowthVerdict.Disabled => "расчистка выключена",
        GrowthVerdict.OutOfReach => "не дотянуться",
        GrowthVerdict.Enough => "хватит на этот раз",
        GrowthVerdict.Failed => "не поддался",
        _ => verdict.ToString()
    };

    public override string ToString()
    {
        string where = Code.Length > 0 ? $"{Code} в {Pos}" : $"{Pos}";
        return Broken ? $"{where}: сломал" : $"{where}: {Message ?? Reason(Verdict)}";
    }
}

/// <summary>
/// Отчёт о расчистке: по клетке на каждую, куда заглянули. Молчаливое
/// «готово» здесь запрещено — вызывающий должен видеть, что осталось стоять
/// и почему (правило 3 проекта).
/// </summary>
public sealed record GrowthReport(IReadOnlyList<GrowthNote> Notes)
{
    /// <summary>Сколько блоков сломано.</summary>
    public int Broken => Notes.Count(n => n.Broken);

    /// <summary>Что осталось стоять (пустые клетки сюда не считаются).</summary>
    public IEnumerable<GrowthNote> Left =>
        Notes.Where(n => !n.Broken && n.Verdict != GrowthVerdict.Empty);

    /// <summary>Все клетки, куда заглянули, теперь пусты.</summary>
    public bool Clear => !Left.Any();

    public override string ToString()
    {
        if (Notes.Count == 0)
            return "расчищать нечего";
        var parts = new List<string>();
        if (Notes.Any(n => n.Broken))
            parts.Add("сломал: " + string.Join(", ", Notes.Where(n => n.Broken).Select(n => n.Code)));
        if (Left.Any())
            parts.Add("не тронул: " + string.Join("; ", Left));
        return parts.Count > 0 ? string.Join("; ", parts) : "на пути было пусто";
    }
}

/// <summary>
/// Добыча и установка блоков — честно, по правилам игры.
///
/// Мгновенное ломание одним пакетом было главным читом библиотеки: живой игрок
/// держит кнопку столько, сколько велят сопротивление блока и скорость
/// инструмента, и без нужного тира не ломает вовсе. Здесь ровно это:
/// сопротивление и материал берутся у настоящего блока, скорость — у
/// настоящего предмета (см. WorldModel.MiningSeconds), тир сверяется так же,
/// как его сверяет сервер.
/// </summary>
public class Mining
{
    private readonly BotContext ctx;
    private readonly Hands hands;

    public Mining(BotContext ctx, Hands hands)
    {
        this.ctx = ctx;
        this.hands = hands;
    }

    public event Action<string>? OnLog;

    /// <summary>
    /// ВСЁ, ЧТО ГОВОРИТ ДОБЫЧА, ИДЁТ ЧЕРЕЗ ЭТУ ДВЕРЬ — и другой у неё нет.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал заказчика): «за 20 секунд боя ТРИДЦАТЬ одинаковых
    /// строк „тупик …" и десятки „не дойти до …" в секунду». В движении дверь
    /// уже поставлена, а добыча всё ещё писала россыпью — и в карьере на пять
    /// сотен клеток гранита это пять сотен строк «ломаю rock-granite: 1,5 с
    /// (pickaxe-copper)», слово в слово одинаковых. Прочесть между ними
    /// настоящую беду нельзя ровно так же, как между тупиками.
    ///
    /// Заглушка стоит ЗДЕСЬ, а не у каждой строки: правило «одно событие — одна
    /// строка» должно быть ОДНО на всю добычу (правило 1 проекта), иначе
    /// следующая новая жалоба снова поедет россыпью, а приёмка не заметит.
    /// Само правило — общее с движением (<see cref="LogRepeats"/>): второй такой
    /// механики в проекте быть не должно.
    ///
    /// Ключ — ГОТОВЫЙ ТЕКСТ, поэтому другой блок и другой инструмент остаются
    /// отдельными новостями: «ломаю soil-low-none» не глушит «ломаю rock-granite».
    /// </summary>
    private void Say(string message)
    {
        if (repeats.Say(message, DateTime.UtcNow) is { } line)
            OnLog?.Invoke(line);
    }

    /// <summary>
    /// ДОСКАЗАТЬ НЕДОСКАЗАННОЕ: работа кончилась — значит кончился и приступ
    /// повторов, и число промолчанных строк обязано выйти наружу. Иначе
    /// заглушка из «тихо» превращается в «скрыл», а это тот же молчаливый
    /// отказ, который запрещает правило 3.
    /// </summary>
    private void SayTail()
    {
        if (repeats.Tail(DateTime.UtcNow) is { } tail)
            OnLog?.Invoke(tail);
    }

    /// <summary>
    /// Насколько глушить повторы в журнале добычи, секунд (0 — не глушить).
    /// Роль откручивает обратно, когда разбирает беду по живому журналу.
    /// </summary>
    public double QuietRepeatSeconds
    {
        get => repeats.QuietSeconds;
        set => repeats.QuietSeconds = value;
    }

    private readonly LogRepeats repeats = new();

    /// <summary>Ждать подтверждения от сервера столько секунд.</summary>
    public double ConfirmSeconds { get; set; } = 2.0;

    /// <summary>
    /// Как часто переспрашивать мир, пока ждём ответа сервера, миллисекунд.
    ///
    /// Это не спешка и не чит: пакет уже ушёл, мы только смотрим на СВОЮ копию
    /// мира. Раньше здесь стояло 60 мс, и на каждом блоке бот в среднем ждал
    /// лишние 30 мс уже ПОСЛЕ того, как сервер ответил. На четырнадцати клетках
    /// земли это почти полсекунды из ничего.
    /// </summary>
    public int ConfirmPollMs { get; set; } = 15;

    /// <summary>
    /// Шаг проверки во время самого удара, миллисекунд.
    ///
    /// Сон нужен, чтобы замечать чужое вмешательство (блок исчез сам, руку
    /// забрали), но спать ДОЛЬШЕ, чем осталось держать кнопку, незачем: лишний
    /// сон — это чистая переработка сверх того, что просит игра. Поэтому шаг
    /// урезается остатком (см. BreakAsync).
    /// </summary>
    public int MinePollMs { get; set; } = 50;

    /// <summary>
    /// Во сколько раз медленнее «как надо» бот согласен копать МОЛЧА.
    ///
    /// Полтора — чтобы не придираться к мелочам вроде кирки на ступень хуже,
    /// но ловить настоящие беды: земля мечом — это шесть раз.
    /// </summary>
    public double SlowToolWarnRatio { get; set; } = 1.5;

    /// <summary>
    /// Сколько секунд надо терять НА КЛЕТКЕ, чтобы жаловаться.
    ///
    /// Без этого порога журнал завалило бы травой: рвать её ножом вчетверо
    /// быстрее, чем рукой, но разница — сотые доли секунды, и живой игрок ради
    /// неё в сумку не полезет.
    /// </summary>
    public double SlowToolWarnLost { get; set; } = 0.4;

    /// <summary>
    /// Как часто повторять ОДНУ И ТУ ЖЕ жалобу на инструмент, секунд.
    ///
    /// Первый раз говорим сразу — молчать нельзя. Но ход в четырнадцать клеток
    /// земли дал бы четырнадцать одинаковых строк подряд, и человек перестал бы
    /// их читать ровно тогда, когда они важнее всего. Поэтому повтор — с
    /// прибавкой «так уже N блоков подряд».
    /// </summary>
    public double ToolComplaintEverySeconds { get; set; } = 30;

    /// <summary>
    /// То, что игра отдаёт правой кнопкой, — ПОДНИМАТЬ, а не ломать.
    /// Выключать стоит только там, где нужен именно удар.
    /// </summary>
    public bool PickUpWhatIsPickable { get; set; } = true;

    /// <summary>
    /// Пробивать потолок, когда столбишься вверх (см. PillarUpAsync).
    ///
    /// Живой игрок на дне шахты бьёт киркой вверх и поднимается — иначе выход
    /// из-под сплошной породы невозможен вовсе. Выключатель оставлен роли,
    /// которой нельзя дырявить чужие крыши: караванщику, жильцу, торгашу.
    /// </summary>
    public bool BreakCeiling { get; set; } = true;

    /// <summary>Сколько лишних клеток потолка терпим сверх высоты подъёма.</summary>
    public int CeilingRetries { get; set; } = 4;

    /// <summary>Инструмент под этот блок: самый быстрый из тех, что берут его тир.</summary>
    public SelfState.FoundItem? BestToolFor(BlockPos pos) =>
        BestToolFor(ctx.World.BlockMaterial(pos.X, pos.Y, pos.Z),
                    ctx.World.RequiredMiningTier(pos.X, pos.Y, pos.Z));

    /// <summary>
    /// Самый быстрый предмет В СУМКАХ под этот материал (null — быстрее голых
    /// рук ничего нет).
    ///
    /// Скорость спрашивается У ПРЕДМЕТА ПО МАТЕРИАЛУ БЛОКА — так её считает и
    /// сама игра (CollectibleObject.GetMiningSpeed: таблица miningspeed по
    /// EnumBlockMaterial, помноженная на ToolMiningSpeedModifier). Меч в эту
    /// таблицу не входит вовсе (assets/survival/itemtypes/tool/blade.json — ни
    /// одной строки miningspeed), поэтому по земле он даёт ровно единицу, то
    /// есть скорость голой руки. Отсюда и «ломаю soil-low-none: 1,8 с» из
    /// журнала: 1,8 — это сопротивление земли, делённое на единицу.
    /// </summary>
    public SelfState.FoundItem? BestToolFor(EnumBlockMaterial material, int need) =>
        ctx.Self.FindBestItem(slot =>
        {
            if (slot.Code == null)
                return 0;
            if (ctx.World.ToolTier(slot.Code) < need)
                return 0;
            float speed = ctx.World.MiningSpeed(slot.Code, material);
            // Ровно единица — это «как рукой»: лезть за таким предметом в сумку
            // незачем, и уж тем более незачем считать его инструментом
            return speed <= 1f ? 0 : speed;
        });

    /// <summary>Сколько секунд бот будет ломать блок тем, что сейчас в руке.</summary>
    public double SecondsToBreak(BlockPos pos, string? toolCode = null) =>
        ctx.World.MiningSeconds(pos.X, pos.Y, pos.Z, toolCode ?? hands.Held?.Code);

    // ---------------- «а чем это берут вообще?» ----------------

    /// <summary>
    /// Как назвать инструмент по-человечески (творительный падеж: «лопатой»).
    /// Пустая строка — имени не знаем, тогда в жалобе останется голый код.
    ///
    /// Это СЛОВА, а не физика: сам список орудий и их скорости берутся из
    /// реестра игры, здесь только перевод для журнала. Незнакомое орудие
    /// (модовое) остаётся английским именем игры — соврать про него хуже,
    /// чем показать как есть.
    /// </summary>
    public static string ToolWord(EnumTool? tool) => tool switch
    {
        null => "",
        EnumTool.Shovel => "лопатой",
        EnumTool.Pickaxe => "киркой",
        EnumTool.Axe => "топором",
        EnumTool.Knife => "ножом",
        EnumTool.Saw => "пилой",
        EnumTool.Scythe => "косой",
        EnumTool.Sickle => "серпом",
        EnumTool.Shears => "ножницами",
        EnumTool.Hammer => "молотом",
        EnumTool.Chisel => "зубилом",
        EnumTool.Hoe => "мотыгой",
        EnumTool.Crowbar => "ломом",
        EnumTool.Drill => "буром",
        EnumTool.Sword => "мечом",
        EnumTool.Club => "дубиной",
        _ => tool.Value.ToString()
    };

    // Ответ реестра не меняется за сессию, а перебирать несколько тысяч кодов
    // на каждый удар киркой — расточительство: спрашиваем один раз на материал
    private readonly Dictionary<(EnumBlockMaterial Material, int Tier), KnownTool?> knownTools = [];

    /// <summary>
    /// ЧЕМ ЭТОТ МАТЕРИАЛ БЕРУТ БЫСТРЕЕ ВСЕГО — по реестру предметов сервера,
    /// а не по списку в коде. Нужно ровно для одного: сказать вслух, чего у
    /// бота НЕТ. Пока правильного инструмента нет в сумках, <see
    /// cref="BestToolFor(EnumBlockMaterial,int)"/> о нём и не подозревает — а
    /// молчать про «копаю землю мечом» нельзя.
    ///
    /// null — реестр ещё не пришёл или материал ничем не берётся быстрее рук.
    /// </summary>
    public KnownTool? BestKnownToolFor(EnumBlockMaterial material, int need = 0)
    {
        if (knownTools.TryGetValue((material, need), out var cached))
            return cached;

        KnownTool? best = null;
        // Пустая маска — это «все коды реестра»: другого способа спросить у
        // модели мира весь список предметов нет
        foreach (string code in ctx.World.SearchItemCodes("", int.MaxValue))
        {
            if (ctx.World.ToolTier(code) < need)
                continue;
            float speed = ctx.World.MiningSpeed(code, material);
            if (speed <= 1f || (best is { } b && speed <= b.Speed))
                continue;
            var kind = ctx.World.GetToolTypeByCode(code) is { } t ? (EnumTool)t : (EnumTool?)null;
            best = new KnownTool(code, ToolWord(kind), speed);
        }

        knownTools[(material, need)] = best;
        return best;
    }

    /// <summary>
    /// Сколько времени ушло на удары и сколько на возню между ними. Роль может
    /// обнулить счёт перед работой и показать его в отчёте.
    /// </summary>
    public MiningTally Tally { get; } = new();

    // Когда в последний раз жаловались на эту пару «блок + что в руке» и
    // сколько блоков с тех пор прошло молча
    private readonly Dictionary<string, (DateTime When, int Count)> complaints = new(StringComparer.Ordinal);

    /// <summary>
    /// Сказать вслух, что копаем не тем. Повтор — не чаще, чем велит
    /// <see cref="ToolComplaintEverySeconds"/>, но и не реже: пока беда длится,
    /// человек должен видеть её в журнале, а не догадываться по секундам.
    /// </summary>
    private void Complain(ToolVerdict verdict)
    {
        if (!verdict.Slow(SlowToolWarnRatio, SlowToolWarnLost))
        {
            // Беда кончилась — забываем, чтобы следующая такая же прозвучала
            // сразу, а не «через полминуты после прошлой»
            complaints.Remove(verdict.Block + "|" + verdict.InHand);
            return;
        }

        string key = verdict.Block + "|" + verdict.InHand;
        var (when, count) = complaints.TryGetValue(key, out var seen) ? seen : (DateTime.MinValue, 0);
        if ((DateTime.UtcNow - when).TotalSeconds < ToolComplaintEverySeconds)
        {
            complaints[key] = (when, count + 1);
            return;
        }
        // Наружу — через общую дверь (см. Say): она и считает, сколько раз
        // строка вышла бы слово в слово. Здесь же решается ДРУГОЙ вопрос —
        // новость ли это вообще: жалоба на инструмент есть СОСТОЯНИЕ («иду по
        // ходу земли с мечом»), и её мерка — блоки подряд и полминуты, а не
        // «эту строку только что говорили»
        var told = verdict with { Also = count };
        Say(told.Say());
        complaints[key] = (DateTime.UtcNow, 0);
    }

    /// <summary>
    /// ВЗЯТЬ В РУКУ ТО, ЧЕМ ЭТОТ БЛОК БЕРЁТСЯ БЫСТРЕЕ ВСЕГО, а если такого нет —
    /// сказать об этом вслух.
    ///
    /// Отдельным методом, потому что здесь сходятся три разных отказа, и раньше
    /// два из них уходили в тишину: «нужного тира нет» (это отказ), «нужный тир
    /// есть, но в руку не встал» (тоже отказ, а результат TakeToHandAsync никто
    /// не смотрел) и «тир не нужен, а копаю чем попало» (убыток вшестеро, и о
    /// нём не было ни строчки).
    /// </summary>
    /// <returns>
    /// Отказ, если ломать нечем (null — можно бить), и было ли на это потрачено
    /// переключение руки: это пакет и пауза, и в счёт накладных расходов оно
    /// идёт отдельно от самого удара.
    /// </returns>
    private async Task<(MiningResult? Refusal, bool Swapped)> ArrangeHandAsync(BlockPos pos,
        string code, CancellationToken ct)
    {
        var material = ctx.World.BlockMaterial(pos.X, pos.Y, pos.Z);
        int need = ctx.Cheats.On(ctx.Cheats.IgnoreToolTier, nameof(Cheats.IgnoreToolTier))
            ? 0
            : ctx.World.RequiredMiningTier(pos.X, pos.Y, pos.Z);

        var best = BestToolFor(material, need);
        if (need > 0 && best is null)
            return (new MiningResult(false, MiningRefusal.NeedBetterTool,
                $"{code} требует инструмент тира {need}, такого нет"), false);

        // УЖЕ В РУКЕ — И ПАКЕТА НЕ НАДО. Соседний блок того же материала бьётся
        // сразу, как у живого игрока: ни выбора слота, ни задержки на смену
        // руки. Раньше сюда безусловно шёл TakeToHandAsync, и хотя он в этом
        // случае тоже молчал, знать наверняка было неоткуда
        bool swapped = false;
        if (best is { } tool && hands.Held?.Code != tool.Content.Code)
        {
            swapped = await hands.TakeToHandAsync(s => s.Code == tool.Content.Code, ct) != null;
            // Тир обязателен, а инструмент в руку не встал — это отказ, а не
            // «покопаю чем есть»: сервер такой удар не засчитает вовсе, и бот
            // будет бесконечно махать по камню
            if (!swapped && need > 0)
                return (new MiningResult(false, MiningRefusal.NeedBetterTool,
                    $"{tool.Content.Code} есть, но в руку не встал — {code} тира {need} брать нечем"), false);
        }

        // ОРУЖИЕМ НЕ КОПАЮТ. Сюда мы доходим, только когда правильный
        // инструмент в руку не встал (или его нет вовсе), — значит меч в руке
        // не даёт НИЧЕГО: ни скорости (у клинка нет таблицы miningspeed, игра
        // возвращает единицу — ту же, что у кулака), ни пользы. Он только
        // тратит свою прочность и со стороны выглядит обманом: словами
        // заказчика — «после боя продолжит копать оружием, это же как чит».
        //
        // Меняем руку РОВНО ОДИН РАЗ и только внутри хотбара: разгребать сумки
        // ради этого нельзя — это тормоз на ровном месте, а выгоды сверх голой
        // руки никакой.
        //
        // ПОЧЕМУ НЕ ТОЛЬКО ПУСТОЙ СЛОТ (так было, и это была дыра). Хотбар у
        // работающего бота полон почти всегда: руда, факелы, еда, блоки под
        // столб. Пустого слота нет — и прежний код МОЛЧА оставлял меч в руке,
        // то есть ровно то, на что жаловался заказчик, только в самом частом
        // случае. Пустой слот по-прежнему лучший (в руке совсем ничего), но
        // если его нет, годится любой слот с вещью, которую реестр НЕ числит
        // орудием: стопка земли или факелы копают ровно с той же скоростью, что
        // и кулак (у них нет таблицы miningspeed), и прочности им терять
        // нечего. Цена та же — один пакет выбора слота.
        //
        // УСЛОВИЕ ИМЕННО «НЕ ТОТ ИНСТРУМЕНТ В РУКЕ», а не «инструмента нет
        // вовсе»: раньше стояло best == null, и случай «лопата в сумках есть,
        // но в руку не встала, а тир не обязателен» проваливался мимо — бот
        // копал землю мечом при живой лопате. Если же оружие И ЕСТЬ лучшее
        // (модовый клинок с настоящей miningspeed по этому материалу), то оно
        // и есть инструмент, ready == true, и вынимать его нельзя.
        bool ready = best is { } got && hands.Held?.Code == got.Content.Code;
        if (!ready && hands.Held?.Code is { } armed && IsWeapon(armed))
        {
            if (BareHandSlot() is { } bare)
            {
                // ГОВОРИМ ПОСЛЕ ДЕЛА, А НЕ ВМЕСТО НЕГО. Живой случай 01:33:52,
                // две строки подряд из журнала:
                //   «blade-falx-meteoriciron — оружие, а не инструмент: убираю
                //    из руки, tallgrass-medium-free возьму голой рукой»
                //   «ломаю tallgrass-medium-free: 0,5 с (blade-falx-meteoriciron)»
                // Меч из руки не ушёл: за руку в этот миг шёл спор («перехватов
                // подряд 2 — это спор за руку, сдаюсь»). Обещание, сказанное ДО
                // попытки, стало враньём ровно тогда, когда попытка не удалась,
                // — а закон один: успех только по факту
                await hands.SelectAsync(bare);
                string now = hands.Held?.Code ?? "";
                swapped = now.Length == 0 || !IsWeapon(now);
                Say(swapped
                    ? $"{armed} — оружие, а не инструмент: убрал из руки, {code} ломаю " +
                      (now.Length == 0 ? "голой рукой" : $"{now} — он копает не хуже голой руки")
                    : $"{armed} — оружие, а не инструмент, но из руки он не ушёл: " +
                      $"руку держит {now}, им {code} и ломаю");
            }
            else
            {
                // Деть меч НЕКУДА: весь хотбар в орудиях. Молчать про это
                // нельзя — со стороны бот копает оружием, и человек вправе
                // знать, почему (правило 3). Повторы соберёт общая дверь
                Say($"{armed} — оружие, а не инструмент, но деть его некуда: " +
                    $"в хотбаре ни пустого слота, ни простой вещи. Копаю {code} им");
            }
        }

        // Что в руке ТЕПЕРЬ — и во сколько раз это дороже, чем могло быть
        string inHand = hands.Held?.Code ?? "рукой";
        if (!ready)
        {
            // Лучшее в сумках не в руке (или его нет вовсе) — сравниваем с тем,
            // что знает про этот материал реестр игры
            var better = best is { } mine
                ? new KnownTool(mine.Content.Code ?? "?",
                    ToolWord(ctx.World.GetToolTypeByCode(mine.Content.Code ?? "") is { } t
                        ? (EnumTool)t : null),
                    ctx.World.MiningSpeed(mine.Content.Code, material))
                : BestKnownToolFor(material, need);
            if (better is { } b)
                Complain(new ToolVerdict(code, inHand, SecondsToBreak(pos),
                    b.Code, b.Kind, SecondsToBreak(pos, b.Code), Have: best != null));
        }

        return (null, swapped);
    }

    /// <summary>
    /// ОРУЖИЕ ЛИ ЭТО — по реестру игры, а не по списку кодов.
    ///
    /// Признак ровно один: игра числит вещь орудием ЭТОГО рода (меч, дубина,
    /// копьё, лук, праща). Урона тут мало: у кирки он тоже есть, но кирка —
    /// инструмент, и вынимать её из руки нельзя. Модовый клинок, о котором мы
    /// никогда не слышали, определится так же, как ванильный: реестр про него
    /// знает всё то же самое.
    /// </summary>
    public bool IsWeapon(string code) => ctx.World.GetToolTypeByCode(code) is { } t &&
        (EnumTool)t is EnumTool.Sword or EnumTool.Club or EnumTool.Spear or EnumTool.Bow
                    or EnumTool.Sling;

    /// <summary>
    /// КУДА ДЕТЬ ОРУЖИЕ ИЗ РУКИ, чтобы копать «как голой рукой» (null — некуда).
    ///
    /// Сначала пустой слот: в руке не остаётся вообще ничего, и это честнее
    /// всего. Нет пустого — берём слот с ПРОСТОЙ вещью: такой, которую реестр
    /// игры не числит орудием никакого рода (земля, факелы, руда, еда). Копает
    /// она ровно как кулак — таблицы miningspeed у неё нет, игра возвращает
    /// единицу, — а терять ей нечего: прочности у стопки земли не бывает.
    ///
    /// Орудия ПРОПУСКАЕМ, даже не-оружие: кирка на земле не быстрее кулака
    /// (сюда мы попадаем только когда best == null), зато прочность на каждом
    /// ударе теряет. Менять шило на мыло незачем.
    ///
    /// НЕ <see cref="Hands.FreeHandAsync"/> НАРОЧНО: тот ради пустой руки
    /// разгребает сумки и в крайнем случае выбрасывает лишнее — и правильно
    /// делает там, где пустая рука НУЖНА (забрать готовое из костра). Здесь она
    /// не нужна, а всего лишь не хуже меча: платить за неё возню с сумками
    /// значит вернуть те самые тормоза. Сколько слотов считать своими, знает
    /// <see cref="Hands.UsableHotbarSlots"/> — второй такой мерки быть не должно.
    /// </summary>
    private int? BareHandSlot()
    {
        var hotbar = ctx.Self.GetInventory("hotbar");
        if (hotbar == null)
            return null;
        int usable = Math.Min(hotbar.Length, hands.UsableHotbarSlots);
        int? plain = null;
        for (int i = 0; i < usable; i++)
        {
            if (hotbar[i].IsEmpty)
                return i;                       // пусто — лучше не бывает
            if (plain == null && hotbar[i].Code is { Length: > 0 } code &&
                ctx.World.GetToolTypeByCode(code) == null)
                plain = i;                      // простая вещь: не орудие вовсе
        }
        return plain;
    }

    /// <summary>
    /// Дольше этого — считаем блок неразрушимым и даже не начинаем.
    ///
    /// Живьём: карьер захватил транслокатор, игра дала ему сопротивление 99999,
    /// и бот честно собрался держать кнопку 99999 секунд. Со стороны это
    /// выглядит как «завис навсегда» посреди работы.
    /// </summary>
    public double UnbreakableSeconds { get; set; } = 60;

    /// <summary>
    /// Сколько секунд блок продержится, если взяться за него ЛУЧШИМ из того,
    /// что есть в сумках. Именно это и сделает <see cref="BreakAsync"/>, а
    /// «долго рукой» и «не ломается вовсе» — разные вещи, и путать их нельзя.
    /// </summary>
    public double FastestSeconds(BlockPos pos)
    {
        double best = BestToolFor(pos) is { } tool
            ? SecondsToBreak(pos, tool.Content.Code)
            : double.PositiveInfinity;
        return Math.Min(best, SecondsToBreak(pos, null));
    }

    /// <summary>Блок вообще не ломается (коренная порода, транслокатор, защита).</summary>
    public bool IsUnbreakable(BlockPos pos) => FastestSeconds(pos) >= UnbreakableSeconds;

    /// <summary>Можно ли вообще сломать блок имеющимися инструментами.</summary>
    public bool CanBreak(BlockPos pos)
    {
        if (IsUnbreakable(pos))
            return false;
        int need = ctx.World.RequiredMiningTier(pos.X, pos.Y, pos.Z);
        return need == 0 || BestToolFor(pos) != null;
    }

    /// <summary>
    /// Сломать блок: подобрать инструмент, подойти, повернуться, держать кнопку
    /// столько, сколько требует игра, и проверить, что блок ДЕЙСТВИТЕЛЬНО исчез.
    /// </summary>
    /// <param name="withTool">
    /// Держать в руке именно этот предмет (часть кода). Нужно, когда выпадение
    /// блока ЗАВИСИТ ОТ ИНСТРУМЕНТА: сухая трава падает с травы только под нож
    /// или косу (в ассетах у выпадения стоит tool: "knife"), а рукой бот может
    /// косить луг вечно и не получить ни травинки — так и было поймано живьём.
    /// </param>
    /// <param name="why">
    /// ЗАЧЕМ ЗАМАХНУЛИСЬ. Заводское — <see cref="BreakWhy.Work"/>: работа,
    /// которую назначил человек (дорога, карьер, руда, лес, грядка). Столб,
    /// ступени, потолок, пол и расчистка помехи говорят
    /// <see cref="BreakWhy.Way"/> — им застава строже, потому что «ломать
    /// крайняя мера же» сказано именно про них.
    /// </param>
    public async Task<MiningResult> BreakAsync(BlockPos pos, CancellationToken ct = default,
        string? withTool = null, BreakWhy why = BreakWhy.Work)
    {
        int before = ctx.World.GetBlockId(pos.X, pos.Y, pos.Z);
        if (before == 0)
            return new MiningResult(false, MiningRefusal.Nothing, "тут пусто");

        string code = ctx.World.GetBlockCode(pos) ?? "?";

        // ЧЕЙ ЭТО БЛОК — СПРАШИВАЕМ ЗДЕСЬ, И ЭТО ЕДИНСТВЕННАЯ ДВЕРЬ К МОЛОТКУ.
        //
        // ТРЕТИЙ ЖИВОЙ СЛУЧАЙ (23.08, 11:01:57): бот стоял В ДВЕРЯХ дома
        // заказчика и выламывал пол (soil-medium-normal) и стену
        // (rammed-light-plain) себе на столб. Дважды до этого — стена и крыша
        // из debarkedlog-oak. Каждый раз чинили ОДНУ дорогу к кирке, а их было
        // шесть: застава стояла у проходки, ступеней, уборки, пола, потолка и
        // добычи на столб, но дорога, карьер, руда, костёр, грядка, укрытие и
        // поручения били ОТСЮДА напрямую, никого не спросив (прямых
        // вызывающих у BreakAsync около тридцати).
        //
        // Теперь спрашивают все, потому что мимо этой строки удара нет.
        // Заранее спросившие спрашивают ту же заставу — чтобы не идти к стене
        // через полкарты ради отказа вплотную.
        var (may, whose) = ctx.Break.MayBreak(pos, why);
        if (!may)
        {
            // ВСЛУХ, А НЕ МОЛЧА. Пощажённый дом, о котором не сказали, со
            // стороны неотличим от «бот завис»: заказчик читает журнал и
            // вправе видеть, ЧТО именно уцелело и КАКОЙ ручкой это снять.
            // Повторы глушит общая дверь добычи (см. Say)
            Say($"{code} в {pos} не трону: {whose}");
            return new MiningResult(false, MiningRefusal.Spared, whose);
        }

        // ВЗГЛЯД — ДЕЛУ, НА ВСЮ РАБОТУ ЦЕЛИКОМ: и на подход, и на удар, и на
        // подъём россыпи. Живой случай: бот копает карьер, мимо идёт человек,
        // «живой взгляд» доворачивает корпус к нему — и бот бьёт, стоя задом
        // к забою. Прицел отпускается сам, как только удар кончился любым
        // исходом, — иначе бот ослеп бы на прохожих навсегда
        using var aim = ctx.Movement.HoldAim($"ломаю {code}");

        // ЭТО НЕ ПОРОДА — ЭТО ПОДНИМАЕТСЯ.
        //
        // Россыпь на земле (looseores-…, камни, палки) киркой не берётся: игра
        // отдаёт её правой кнопкой, как поднимают вещь. Бот же лупил по ней
        // левой, сервер удар не засчитывал, блок оставался на месте — и цикл
        // добычи крутился вхолостую, потому что «сломан» никогда не наступало.
        // Заказчик поймал это живьём: «камушек он ранит, и процесс копания
        // зацикливается без результата».
        //
        // Спрашиваем РЕЕСТР СЕРВЕРА, а не список кодов: что подбирается
        // руками, знает Gathering, и знает по тому же реестру, что и игра.
        // Заодно это чинит все прочие мягкие блоки, которые бить бессмысленно.
        if (PickUpWhatIsPickable && ctx.Gathering is { RegistryReady: true } gathering &&
            gathering.IsPickable(code))
        {
            Say($"{code} не ломают, а поднимают — беру рукой");
            if (await gathering.PickUpAsync(pos, ct))
            {
                ctx.Pickup?.Note(pos);
                return new MiningResult(true);
            }
            // Не поднялось — честно скажем и НЕ станем добивать киркой:
            // по этому блоку удары не засчитываются вовсе
            return new MiningResult(false, MiningRefusal.Rejected,
                $"{code} надо поднимать рукой, а он не поднялся");
        }

        var startedWhole = DateTime.UtcNow;
        bool swapped = false;

        if (withTool is { Length: > 0 } wanted)
        {
            // Просили именно этот предмет — значит от него зависит выпадение
            // (сухая трава падает только под нож), и подменять его «более
            // быстрым» нельзя: быстро и впустую хуже, чем медленно и с толком.
            // А если он уже в руке, то и пакета не надо: соседний блок бьётся
            // сразу, как у живого игрока
            if (hands.Held?.Code?.Contains(wanted, StringComparison.OrdinalIgnoreCase) != true)
            {
                if (await hands.TakeToHandAsync(wanted, ct) is null)
                    return new MiningResult(false, MiningRefusal.Nothing,
                        $"{code} без «{wanted}» нужного не отдаст, а его нет");
                swapped = true;
            }
        }
        else
        {
            var (refusal, tookTool) = await ArrangeHandAsync(pos, code, ct);
            if (refusal != null)
                return refusal;
            swapped = tookTool;
        }

        // Подход считается отдельно: он и есть самая дорогая часть работы,
        // и без этого числа спор «в разы дольше живого игрока» упирается
        // в догадки
        var (reached, walked) = await hands.ReachForCountingAsync(pos, ct: ct);
        if (!reached)
        {
            Tally.Note(0, (DateTime.UtcNow - startedWhole).TotalSeconds, swapped, walked);
            return new MiningResult(false, MiningRefusal.OutOfReach, $"до {code} не дотянуться");
        }

        double seconds = ctx.Cheats.On(ctx.Cheats.InstantMining, nameof(Cheats.InstantMining)) ? 0 : SecondsToBreak(pos);
        if (double.IsInfinity(seconds))
            return new MiningResult(false, MiningRefusal.NeedBetterTool, $"{code} этим не сломать");
        if (seconds >= UnbreakableSeconds)
            return new MiningResult(false, MiningRefusal.Unbreakable,
                $"{code} не ломается (игра просит {seconds:0} с)");

        Say($"ломаю {code}: {seconds:0.#} с ({hands.Held?.Code ?? "рукой"})");

        // Чем именно бьём — запоминаем ДО удара: руку могут забрать посреди
        // него, и тогда мы будем «копать» тем, что подсунули
        string? inHand = hands.Held?.Code;

        // ЧЬИМ ТЕЛОМ И ЧЬЕЙ РУКОЙ мы бьём — запоминаем ТОЖЕ до удара. Тело и
        // рука ходят по одной очереди (BodyArbiter), и если посреди удара
        // хозяин СМЕНИЛСЯ, то удар не «промахнулся» — его перебило дело
        // поважнее, и у этого дела есть имя. Сравниваем именно с прежним
        // хозяином: свою же отмену (человек сказал «стоп», роль перезапустила
        // наряд) нельзя выдавать за чужой перехват
        string wasOwner = ctx.Turn.Busy;

        // КНОПКА УДАРА НАЖАТА ВСЁ ВРЕМЯ ДОБЫЧИ — по ней сервер гонит анимацию
        // окружающим. Держим её удержанием, которое отпускается САМО в тот миг,
        // когда тело перестало быть нашим (см. Actions.HoldKeyAsync): удар
        // длится секундами, и ровно в эти секунды его перебивает голод. Живой
        // случай заказчика — «во время копания захотел есть, и при поедании
        // хлеба была анимация копания»
        await using var hit = await ctx.Actions.HoldKeyAsync(Actions.KeyLeftMouse, ct, Say);
        // Миг отправки слома запоминаем ЗАРАНЕЕ: по нему потом отличают ответ
        // сервера на НАШ слом от чужой ошибки, пришедшей секундой раньше
        var sentAt = DateTime.MaxValue;
        try
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            bool retaken = false;
            while (DateTime.UtcNow < until)
            {
                if (ct.IsCancellationRequested)
                    // ПЕРЕБИЛИ — ЭТО ФАКТ, А НЕ ПРОМАХ, и у факта есть имя.
                    // Живой случай 16.08: пока «команда дорога» копала, у неё
                    // отбирали руку, а добыча читала это как случайность и
                    // билась заново по кругу — «беру shovel-steel и бью
                    // заново», и так минутами. Спрашиваем распорядителя, кто
                    // хозяин теперь, и называем его; молчим только тогда, когда
                    // хозяин не менялся — это наша собственная отмена
                    return new MiningResult(false, MiningRefusal.Rejected,
                        ctx.Turn.Busy is { Length: > 0 } who && who != wasOwner
                            ? $"удар по {code} бросаю: тело и руку забрал «{who}» — это важнее"
                            : "прервано");
                // Блок мог исчезнуть сам (сосед сломал, обвал) — тогда хватит
                if (ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) != before)
                    break;

                // РУКУ ЗАБРАЛИ ПОСРЕДИ УДАРА — решает одно правило на всех,
                // см. <see cref="WhenHandTaken"/>. Здесь только исполнение
                switch (WhenHandTaken(inHand, hands.Held?.Code, retaken))
                {
                    case HandTheft.Drop:
                        Say($"руку забирают снова ({hands.Held?.Code ?? "пусто"} вместо " +
                            $"{inHand}) — бросаю удар по {code}, оружием не копают");
                        return new MiningResult(false, MiningRefusal.Rejected,
                            $"{inHand} забирают из руки посреди удара — {code} так не сломать");

                    case HandTheft.Retake:
                        retaken = true;
                        Say($"руку забрали посреди удара ({hands.Held?.Code ?? "пусто"} " +
                            $"вместо {inHand}) — беру {inHand} и бью заново");
                        await hit.ReleaseAsync();
                        if (await hands.TakeToHandAsync(s => s.Code == inHand, ct) is null)
                            return new MiningResult(false, MiningRefusal.Rejected,
                                $"{inHand} из руки забрали, а обратно не встал — удар по {code} впустую");
                        // Пока возвращали инструмент, тело могли забрать совсем:
                        // удержание об этом знает и жать заново не станет. Тогда
                        // и домахивать нечем — выходим, а не бьём вслепую
                        if (!await hit.PressAsync())
                            return new MiningResult(false, MiningRefusal.Rejected,
                                $"тело забрали, пока я возвращал {inHand} — удар по {code} брошен");
                        until = DateTime.UtcNow.AddSeconds(seconds);
                        break;
                }

                // Спать дольше, чем осталось держать кнопку, — это переработка
                // сверх того, что просит игра. На блоке в 0,3 с фиксированные
                // 50 мс давали до шестой части лишнего времени
                await Task.Delay(LeftMs(until, MinePollMs), ct).ContinueWith(_ => { });
            }

            sentAt = DateTime.UtcNow;
            await ctx.Actions.BreakBlockAsync(pos);
        }
        finally
        {
            // Отпускаем ЗДЕСЬ ЖЕ, не дожидаясь конца метода: дальше идёт
            // ожидание ответа сервера, а махать киркой в это время незачем
            await hit.LetGoAsync();
        }

        // Правило «не врать о результате»: успех — только по факту от сервера
        var deadline = DateTime.UtcNow.AddSeconds(ConfirmSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // СЕРВЕР ОТВЕТИЛ СЛОВАМИ — гадать больше не о чем. На чужой заявке
            // он шлёт ошибку noprivilege-buildbreak-… с именем хозяина
            // (ServerSystemBlockSimulation.HandleBlockPlaceOrBreak): это не
            // «возможная примета», а прямой ответ, и запоминается он сразу
            if (ctx.Actions.RefusalSince(sentAt) is { NoBuildRights: true } no)
            {
                Tally.Note(seconds, (DateTime.UtcNow - startedWhole).TotalSeconds, swapped, walked);
                ctx.Claims?.NoteBreakRefused(pos);
                return new MiningResult(false, MiningRefusal.Claimed,
                    $"{code} ломать не дают: {no}");
            }
            if (ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) != before)
            {
                // Здесь лежит наша добыча. Один этот шов кормит всех, кто
                // ломает блоки: карьер, рубку, покос, поручения — им не нужно
                // ни знать про подбор, ни звать его
                ctx.Pickup?.Note(pos);
                Tally.Note(seconds, (DateTime.UtcNow - startedWhole).TotalSeconds, swapped, walked);
                return new MiningResult(true);
            }
            await Task.Delay(LeftMs(deadline, ConfirmPollMs), ct).ContinueWith(_ => { });
        }
        Tally.Note(seconds, (DateTime.UtcNow - startedWhole).TotalSeconds, swapped, walked);
        // ОТКАЗ СЕРВЕРА НА СЛОМ — ВОЗМОЖНАЯ примета чужого, и только возможная.
        // Заявка есть далеко не у каждого дома, а «бил и не сломалось» бот
        // видит сам, как живой игрок. Но за две секунды подтверждения не
        // приходит и от лага сети посреди собственного карьера, поэтому
        // считает повторы Claims: одиночный отказ приметой не становится
        ctx.Claims?.NoteRefusal(pos, ForeignKind.BreakRefused);
        return new MiningResult(false, MiningRefusal.Rejected, $"{code} на месте — сервер не принял");
    }

    /// <summary>
    /// Сколько миллисекунд спать: шаг опроса, но не дольше, чем осталось ждать.
    /// Чистая арифметика — вынесена, чтобы одинаково считалось в обоих циклах.
    /// </summary>
    public static int LeftMs(DateTime until, int step) =>
        (int)Math.Clamp((until - DateTime.UtcNow).TotalMilliseconds, 1, Math.Max(1, step));

    /// <summary>
    /// ЧТО ДЕЛАТЬ, КОГДА РУКУ ЗАБРАЛИ ПОСРЕДИ УДАРА, — правило целиком, без
    /// мира и сервера, поэтому проверяется стендом (правило 6 проекта).
    ///
    /// ЖИВОЙ СЛУЧАЙ, словами заказчика: «пару раз проигралась анимация меча,
    /// как будто им копал, а не киркой». Удар держится СЕКУНДАМИ, а рефлексы за
    /// это время успевают всё: отбиться от дрифтера, поесть, поставить факел —
    /// и в руке оказывается меч. Игра считает урон блоку по тому, что в руке
    /// ПРЯМО СЕЙЧАС, так что дальше бот честно машет мечом по камню без всякого
    /// толку — и это ровно тот «чит», о котором шла речь, только наоборот: не
    /// выгода, а обман зрителя.
    ///
    /// ПЕРВЫЙ РАЗ — случайность: берём инструмент обратно и бьём заново.
    /// ВТОРОЙ — уже не случайность, а спор за тело, и решать его должен тот,
    /// кто спорит. Наше дело — честно бросить удар (правило 3: отказ, а не
    /// молчание), а не домахивать оружием.
    ///
    /// Дыра, которая тут была: сторож стоял под условием «ещё не возвращали»,
    /// то есть после первого возврата ЗАМОЛКАЛ НАВСЕГДА, и второй захват руки
    /// проходил незамеченным — бот дожимал кнопку с мечом до конца расчётного
    /// срока и слал слом, которого сервер не засчитывал.
    /// </summary>
    /// <param name="wanted">Чем начали удар (null — начинали голой рукой).</param>
    /// <param name="nowInHand">Что в руке сейчас.</param>
    /// <param name="alreadyRetaken">Инструмент уже возвращали в этом ударе.</param>
    public static HandTheft WhenHandTaken(string? wanted, string? nowInHand, bool alreadyRetaken) =>
        wanted == null || nowInHand == wanted ? HandTheft.None
        : alreadyRetaken ? HandTheft.Drop
        : HandTheft.Retake;

    // ---------------- порядок клеток ----------------

    /// <summary>
    /// Насколько близко клетка должна лежать к предыдущей, чтобы считаться
    /// «той же кучей» (по каждой оси, в клетках).
    ///
    /// Единица — это соседи, включая диагонали и этаж выше-ниже: ровно то, до
    /// чего живой игрок дотягивается, не сходя с места. Заказчик увидел
    /// обратное: «копает он долго, потому что не соседние блоки и долго между
    /// ними переключается».
    /// </summary>
    public int StickRadius { get; set; } = 1;

    private static int SqDistance(BlockPos a, BlockPos b)
    {
        int dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static bool Sticks(BlockPos a, BlockPos b, int radius) =>
        Math.Abs(a.X - b.X) <= radius && Math.Abs(a.Y - b.Y) <= radius &&
        Math.Abs(a.Z - b.Z) <= radius;

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО ПОРЯДКА: какую клетку брать следующей. Три условия,
    /// строго по старшинству.
    ///
    /// 1. НЕ БРОСАТЬ НАЧАТУЮ КУЧУ. Есть сосед у только что сломанной клетки —
    ///    берём его, даже когда под ногами лежит клетка ближе. Уйти с начатого
    ///    места значит потом вернуться: два похода вместо нуля.
    /// 2. БЛИЖАЙШАЯ К НОГАМ. Куча кончилась — за дальней надо идти, а ходьба
    ///    стоит дороже любого удара.
    /// 3. СНАЧАЛА ТУПИКИ. Из равных берём ту, у которой соседей осталось меньше
    ///    всего: иначе жадный обход уходит вдоль ряда и роняет за спиной по
    ///    клетке через одну, а за каждой из них потом отдельный поход.
    ///
    /// Ровно этого не было, когда заказчик увидел, что бот «прыгает между
    /// несоседними блоками и долго между ними переключается».
    ///
    /// Ни мира, ни сервера — только координаты, поэтому правило проверяется
    /// стендом (правило 6 проекта).
    /// </summary>
    /// <param name="from">Где стоят ноги.</param>
    /// <param name="last">Последняя сломанная клетка (null — куча ещё не начата).</param>
    /// <param name="cells">Что осталось сломать.</param>
    /// <param name="stickRadius">Что считать «той же кучей».</param>
    public static BlockPos? NextCell(BlockPos from, BlockPos? last, IEnumerable<BlockPos> cells,
        int stickRadius = 1)
    {
        var list = cells as IReadOnlyList<BlockPos> ?? cells.ToList();
        if (list.Count == 0)
            return null;

        // 1. НАСКОЛЬКО ХОРОШ ЛУЧШИЙ. Клетка своей кучи бьёт любую чужую, как бы
        // близко та ни лежала; среди равных решает расстояние до ног
        bool bestSticks = false;
        int bestNear = int.MaxValue;
        foreach (var cell in list)
        {
            bool sticks = last is { } prev && Sticks(cell, prev, stickRadius);
            if (!sticks && bestSticks)
                continue;
            int near = SqDistance(cell, from);
            if (sticks && !bestSticks)
            {
                bestSticks = true;
                bestNear = near;
            }
            else if (near < bestNear)
            {
                bestNear = near;
            }
        }

        // 2. КОГО ИЗ РАВНЫХ. Берём ту, у которой соседей осталось МЕНЬШЕ ВСЕГО:
        // она и есть тупик, за которым потом придётся возвращаться. Без этого
        // правила жадный обход раз за разом уходил вдоль ряда и бросал за
        // спиной по клетке через одну — ровно то, что заказчик увидел как
        // «прыгает между несоседними блоками».
        BlockPos? best = null;
        int bestFriends = int.MaxValue;
        foreach (var cell in list)
        {
            bool sticks = last is { } prev && Sticks(cell, prev, stickRadius);
            if (sticks != bestSticks || SqDistance(cell, from) != bestNear)
                continue;
            int friends = 0;
            foreach (var other in list)
                if (other != cell && Sticks(other, cell, stickRadius))
                    friends++;
            if (best is null || friends < bestFriends)
            {
                best = cell;
                bestFriends = friends;
            }
        }
        return best;
    }

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: весь список в разумном порядке — от ближайшей к телу и
    /// дальше по соседям. То же, что <see cref="NextCell"/>, только до конца:
    /// после каждой клетки тело считается стоящим у неё, потому что после удара
    /// оно там и оказывается.
    ///
    /// Отдаётся тем, кто СОСТАВЛЯЕТ списки (жила, слой карьера, пролёт хода):
    /// пусть каждый решает, ЧТО ломать, а КАК обходить — одно на всех.
    /// </summary>
    public static IReadOnlyList<BlockPos> NearestFirst(BlockPos from, IEnumerable<BlockPos> cells,
        int stickRadius = 1)
    {
        var left = cells.Distinct().ToList();
        var order = new List<BlockPos>(left.Count);
        BlockPos? last = null;
        var at = from;

        while (left.Count > 0)
        {
            if (NextCell(at, last, left, stickRadius) is not { } cell)
                break;
            left.Remove(cell);
            order.Add(cell);
            last = cell;
            at = cell;   // сломав клетку, игрок стоит у неё, а не там, где начал
        }
        return order;
    }

    // ---------------- шахматка: «через одну» у самого удара ----------------

    /// <summary>
    /// БРАТЬ КАМЕНЬ «ЧЕРЕЗ ОДНУ» РАДИ ЦЕЛЫХ БЛОКОВ. Ставит роль
    /// (<c>LivingRole.ШахматкойРадиЦельныхБлоков</c>), а не работа.
    ///
    /// ПОЧЕМУ ЭТО ЖИВЁТ У УДАРА, А НЕ У РАБОТЫ, — СЛОВА ЗАКАЗЧИКА (03.09):
    /// «копать шахматкой я имел в виду ВСЕГДА, ДЛЯ ЛЮБЫХ РАБОТ». Подключать её
    /// к карьеру, дороге и штольне порознь нельзя: проект трижды получал беду
    /// «сделали в одном месте, забыли в другом», и шахматка была ровно таким
    /// случаем — она жила внутри карьера, а дорога и штольня крошили камень
    /// рядом, ничего о ней не зная.
    ///
    /// «ДЛЯ ЛЮБЫХ РАБОТ» ЧИТАЕТСЯ ТАК: ручка стоит у удара, и её видит КАЖДЫЙ,
    /// кто отдаёт сюда список. Что она при этом ДАСТ, решает не работа, а
    /// правило игры — и оно кормится ОБЪЁМОМ, а не срезом
    /// (<see cref="Надвое"/>). Кто снимает объём, получает целые блоки: яма
    /// карьера и оболочка жилы. Кто снимает срез или одну клетку, не получает
    /// ничего и не платит ничего — разбор с числами лежит там же.
    /// </summary>
    public bool ЧерезОдну { get; set; } = true;

    /// <summary>
    /// СТОИТ ЛИ БРАТЬ ЭТУ КЛЕТКУ ЦЕЛЫМ БЛОКОМ — спрошено у РЕЕСТРА СЕРВЕРА, а
    /// не по списку материалов: целым выпадает то, у чего есть поведение
    /// <c>BreakIfFloating</c> (см. <see cref="WholeStone.DropsWholeWhenFloating(WorldModel, int)"/>).
    /// Так модовая порода опознаётся наравне с ванильной, а земля и песок,
    /// которым шахматка не даёт ничего, в неё не попадают.
    /// </summary>
    private bool БерётсяЦелым(BlockPos c) =>
        WholeStone.DropsWholeWhenFloating(ctx.World, ctx.World.GetBlockId(c.X, c.Y, c.Z));

    /// <summary>
    /// ОЧЕРЕДЬ СПИСКА «ЧЕРЕЗ ОДНУ» — ЕДИНСТВЕННЫЙ ВХОД ДЛЯ ВСЕХ, КТО СНИМАЕТ
    /// ОБЪЁМ. Работа отдаёт сюда свой список и получает его же, целиком, ни
    /// одной клетки не потеряв, — только сперва жертвы, а целые последними.
    /// К тому времени у целых не остаётся ни одной твёрдой грани, и каждая
    /// выпадает ЦЕЛЫМ БЛОКОМ вместо двух-трёх кусков щебня.
    ///
    /// ПОРЯДОК ВНУТРИ КАЖДОЙ ПОЛОВИНЫ — ТОТ, С КОТОРЫМ ПРИШЛИ. Это не
    /// вежливость, а условие безопасности: у окапывания жилы порядок выстрадан
    /// живым случаем (ближние к телу первыми — иначе бот мечется по выработке),
    /// и перетасовать его шахматка не вправе. Она вправе только отложить часть
    /// клеток на потом.
    ///
    /// ЕЙ НУЖЕН ОБЪЁМ, А НЕ СРЕЗ, И ЭТО ГЛАВНОЕ ОГРАНИЧЕНИЕ ПРАВИЛА. РАЗБОР,
    /// ПРОВЕРЕННЫЙ ЧИСЛАМИ (стенд <c>ШахматкаПорядкомTests</c>).
    ///
    /// Игра отдаёт целый блок только тому, у кого свободны ВСЕ ШЕСТЬ граней
    /// (BreakIfFloating; разбор — в <see cref="WholeStone"/>). Значит клетка
    /// выйдет целой, лишь когда КАЖДЫЙ её сосед либо уже воздух, либо лежит в
    /// ЭТОМ ЖЕ списке и уйдёт раньше. Отсюда два случая, и их надо различать:
    ///
    ///   • ОБЪЁМ — тело с толщиной по всем трём осям (слой карьера, оболочка
    ///     жилы). Внутренние клетки окружены своими же, и целые блоки идут.
    ///     Скала 5×5×5 в воздухе: 39 целых из 125, ход 3×3×8 объёмом — 3 из 72;
    ///
    ///   • СРЕЗ — плоский кусок толщиной в одну клетку ПОПЕРЁК движения работы:
    ///     ряд дороги, шаг штольни. У всех его клеток координата вдоль забоя
    ///     ОДНА, чередовать поперечные слои нечего, а сосед ВПЕРЁД в список не
    ///     входит и стоит нетронутой породой — то есть у КАЖДОЙ клетки среза
    ///     остаётся твёрдая грань. Целых НОЛЬ, и не при узкой ширине, а при
    ///     ЛЮБОЙ: тот же ход 3×3×8, поданный срезами, дал 0 целых из 72.
    ///
    /// ШИРИНА ТУТ НИ ПРИ ЧЁМ, И ЭТО ИСПРАВЛЕНИЕ ПРЕЖНЕЙ ЗАПИСИ. Здесь стояло
    /// «у выработки толщиной в одну клетку целых не будет — сделайте шире», и
    /// человек, поставивший дороге ширину 3, вынимал ВТРОЕ БОЛЬШЕ ПОРОДЫ и не
    /// получал ни одного целого блока. Дело было в размере куска, а не в
    /// ширине.
    ///
    /// ЧЕМ ЭТО КОНЧАЕТСЯ ДЛЯ ТОГО, КОМУ НЕЧЕГО ВЗЯТЬ: половина «целые»
    /// оказывается пустой, список возвращается слово в слово, и работа не
    /// платит за шахматку ни одним лишним ударом. Поэтому те, кто кормит
    /// срезом (дорога, штольня) или одной клеткой (укрытие), сюда и не
    /// заходят вовсе — звать правило, которое заведомо вернёт своё же, значит
    /// заводить рычаг, который ничего не двигает.
    /// </summary>
    /// <remarks>
    /// ЗАБОЙ СЧИТАЕТСЯ ПО САМОМУ СПИСКУ (<see cref="WholeStone.ЗабойОбъёма"/>),
    /// И ДОВОДА «НАЗОВИ СВОЙ ЗАБОЙ» ЗДЕСЬ НЕТ — ЕГО СНЯЛИ.
    ///
    /// Плоскость и правда берётся от забоя работы (слова заказчика 03.09: «она
    /// не только бывает вертикальной, но и горизонтальной, если мы роем
    /// тоннель»), но НАЗЫВАЕТ его тот, кто раскладывает объём планом, — и это
    /// один карьер: он зовёт <see cref="WholeStone.ЧерезОдну"/> напрямую и
    /// приносит туда свою ось Y (<c>Quarry.ЗабойЯмы</c>). Сюда же приходят те,
    /// кто своего забоя не знает вовсе (оболочка жилы — не полоса и не яма, а
    /// кожура вокруг выработки), и после того как дорога со штольней отсюда
    /// ушли, довод остался БЕЗ ЕДИНОГО ЖИВОГО ЗНАЧЕНИЯ: все вызовы передавали
    /// null. Открытый довод, в который никто ничего не кладёт, — это дверь
    /// ровно для одной подстановки, и стенд её не поймает (тем же порядком
    /// сняли высоту у <c>Tunnel.BodyCells</c>).
    /// </remarks>
    public (IReadOnlyList<BlockPos> Жертвы, IReadOnlyList<BlockPos> Целые) Надвое(
        IEnumerable<BlockPos> клетки)
    {
        var все = клетки as IReadOnlyList<BlockPos> ?? клетки.ToList();
        if (!ЧерезОдну || все.Count == 0)
            return (все, []);

        var где = WholeStone.ЗабойОбъёма(все, Feet ?? все[0]);
        int чётность = WholeStone.ЛучшаяЧётность(все, где, Твёрдо);
        return WholeStone.ЧерезОдну(все, где, чётность, БерётсяЦелым, Твёрдо);
    }

    /// <summary>
    /// То же одним списком — для того, кто просто идёт по клеткам подряд
    /// (окапывание жилы). Второго правила здесь нет: обе половины считает
    /// <see cref="Надвое"/>, тут только склейка.
    /// </summary>
    public IReadOnlyList<BlockPos> Очередь(IEnumerable<BlockPos> клетки)
    {
        var (жертвы, целые) = Надвое(клетки);
        if (целые.Count == 0)
            return жертвы;
        var очередь = new List<BlockPos>(жертвы.Count + целые.Count);
        очередь.AddRange(жертвы);
        очередь.AddRange(целые);
        return очередь;
    }

    /// <summary>
    /// Упирается ли грань в твёрдую сторону соседа СЕЙЧАС. Правило игры целиком
    /// лежит у цельного камня (<see cref="WholeStone.SideSolidAt"/>) — здесь
    /// только адрес мира.
    /// </summary>
    private bool Твёрдо(BlockPos n, BlockFacing f) =>
        WholeStone.SideSolidAt(ctx.World, n.X, n.Y, n.Z, f.Opposite.Index);

    /// <summary>
    /// Где стоят ноги (null — тела ещё нет). Клетку называет мир одним входом
    /// (<see cref="WorldModel.StandingCellAt"/>): стоя на своей каменной тропе
    /// (верх коробки 0.9375) тело находится на 110,94, и <c>floor</c> дал бы
    /// клетку самой тропы — ту, в которую мир войти не даёт. Запас 0,01 — не
    /// вкус, а источник: здесь спрашивается ЖИВАЯ ФИЗИКА, а она за тик
    /// вдавливает тело в опору.
    /// </summary>
    public BlockPos? Feet => ctx.Body?.Physics is { } p
        ? ctx.World.StandingCellAt(p.X, p.Y, p.Z, sink: 0.01)
        : null;

    /// <summary>
    /// Какую клетку брать следующей ОТ НАСТОЯЩЕГО ПОЛОЖЕНИЯ НОГ.
    ///
    /// Ради тех, кто ведёт свой список сам (жила идёт волной по соседям, карьер
    /// снимает слой): им незачем знать ни про ноги, ни про ширину кучи — пусть
    /// решают, ЧТО ломать, а порядок берут отсюда. Второй такой же механики
    /// рядом быть не должно.
    /// </summary>
    public BlockPos? NextCellFromHere(BlockPos? last, IEnumerable<BlockPos> cells)
    {
        var list = cells as IReadOnlyList<BlockPos> ?? cells.ToList();
        if (list.Count == 0)
            return null;
        return NextCell(Feet ?? last ?? list[0], last, list, StickRadius);
    }

    // ---------------- подлесок на дороге ----------------

    /// <summary>
    /// Ломать ли подлесок, мешающий пройти. Выключатель у роли: караванщику
    /// по чужим угодьям лучше не махать руками вовсе.
    /// </summary>
    public bool ClearGrowth { get; set; } = true;

    /// <summary>
    /// Материалы, которые считаем подлеском. Именно материал, а не код: он
    /// один для ванильных и модовых листьев, и по нему же игра считает
    /// скорость добычи.
    /// </summary>
    public List<EnumBlockMaterial> GrowthMaterials { get; } =
        [EnumBlockMaterial.Leaves, EnumBlockMaterial.Plant];

    /// <summary>
    /// Дольше этого блок подлеском не считается.
    ///
    /// Ради этого порога всё и затевалось: живой игрок сносит листву голыми
    /// руками за доли секунды и идёт дальше, а рубить по дороге лес (бревно
    /// это секунды даже с топором) не станет — он его обойдёт.
    /// </summary>
    public double GrowthSeconds { get; set; } = 1.0;

    /// <summary>Сколько блоков ломаем за один вызов — чтобы не встать на просеку.</summary>
    public int MaxGrowthBlocks { get; set; } = 4;

    /// <summary>
    /// Сколько клеток по высоте расчищаем над целевой. Две — рост игрока:
    /// ноги и голова. Роль может поднять, если ей нужен потолок повыше.
    /// </summary>
    public int GrowthHeight { get; set; } = 2;

    /// <summary>
    /// Что беречь, даже если оно мягкое: чужая грядка и саженец мешают не
    /// больше травы, а вреда от них на годы вперёд.
    /// </summary>
    public List<string> SpareMasks { get; } = ["crop", "sapling"];

    /// <summary>
    /// Живой ответ на вопрос «этот блок беречь?» — для тех, кто знает про блок
    /// больше, чем добыча. Ягодники в этот список вписывает сбор дикой еды
    /// (см. <see cref="Foraging"/>): маски кустов живут там одни, а здесь их
    /// только спрашивают.
    /// </summary>
    // System.Func названо полностью: у игры есть свой Func, и с обоими
    // «using» имя становится неоднозначным
    public System.Func<string, bool>? SpareGrowth { get; set; }

    private bool Spared(string code) =>
        SpareMasks.Any(m => code.Contains(m, StringComparison.OrdinalIgnoreCase)) ||
        (SpareGrowth?.Invoke(code) ?? false);

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: НИКЧЁМНАЯ МЕЛОЧЬ ЭТО ИЛИ НАСТОЯЩАЯ ПРЕГРАДА. Ни мира, ни
    /// сервера — только свойства блока из реестра, поэтому решение проверяется
    /// стендом, а не живым лесом.
    ///
    /// ПРАВИЛО ОДНО НА ТРИ РАЗНЫЕ БЕДЫ, и это не экономия, а прямое требование
    /// заказчика («снег нужно чинить на всех видах»). Спрашивают его трое:
    ///   • ходьба — «мелочь под ногами, куда я шагаю» (Movement.WalkTry.Sweep);
    ///   • рука — «мелочь на луче до цели» (Hands.SweepAside);
    ///   • штольня — «мелочь под будущими ногами ступени» (Tunnel.LookAround).
    /// Три заплатки на одну причину разъехались бы на первой же правке.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ради которого правило расширено (прогон 19.08, 21:12:14).
    /// Бот стоял в (512059,4, 113,1, 512281,6), под ним лежал snowlayer-2
    /// (коробка 0,125), а руда была в клетке ПРЯМО ПОД НОГАМИ. Луч из глаз до
    /// неё упирался в этот самый снег, и восемь раз подряд за четыре секунды
    /// звучало «до (512059, 112, 512281) нет прямой видимости: мешает
    /// snowlayer-2». Снег бот не смахнул НИ РАЗУ, а живой игрок сбивает его
    /// одним ударом, потому что снег и есть ближайший блок на луче.
    ///
    /// ПОЧЕМУ СНЕГ НЕ ПОДХОДИЛ ПОД СТАРОЕ ПРАВИЛО. Оно спрашивало ОДИН
    /// признак — материал, — а у снега материал <c>Snow</c>, и в списке
    /// подлеска (листва, растения) его нет и быть не должно: сугроб в рост
    /// человека это не подлесок. Признак нужен был ДРУГОЙ, и он у игры есть.
    ///
    /// ЧТО ТАКОЕ «МУСОР» ПО ОТВЕТУ САМОЙ ИГРЫ (ни одной приставки кода):
    ///   • <c>Block.Replaceable</c> выше <see cref="WorldModel.ReplacedByPlacing"/> —
    ///     игра прямо говорит «под это не подкапываются, его затирают, ставя
    ///     блок» (snowlayer 6100, tallgrass 6000, а снежный КУБ snowblock 990);
    ///   • из блока не выпадает ничего тем, что сейчас в руке
    ///     (<see cref="WorldModel.DropsAnythingWith"/>) — то есть смахнуть его
    ///     не стоит боту ни зёрнышка;
    ///   • это не жидкость: у воды и лавы заменяемость тоже высокая, а
    ///     «смахнуть лаву с дороги» — не приём, а гибель.
    /// К этому по-прежнему прибавляются сопротивление (через
    /// <paramref name="seconds"/>) и тир инструмента: мусор, который ломается
    /// дольше секунды, — уже не мусор.
    /// </summary>
    /// <param name="code">Код блока (пусто — клетка свободна).</param>
    /// <param name="material">Материал блока из реестра игры.</param>
    /// <param name="seconds">Сколько его ломать тем, что есть у бота.</param>
    /// <param name="requiredTier">Тир инструмента, без которого блок не берётся.</param>
    /// <param name="replaceable">
    /// <c>Block.Replaceable</c> из реестра сервера. Ноль (умолчание) — «реестра
    /// не спрашивали», и тогда правило работает ровно как до этой правки: по
    /// одному материалу.
    /// </param>
    /// <param name="dropsAnything">
    /// Выпадет ли хоть что-нибудь ТЕМ, ЧТО В РУКЕ. Умолчание «да» нарочно:
    /// не спросив реестр, объявлять блок никчёмным нельзя.
    /// </param>
    /// <param name="liquid">Это вода или лава (тоже из реестра).</param>
    public GrowthVerdict JudgeGrowth(string? code, EnumBlockMaterial material, double seconds,
        int requiredTier = 0, int replaceable = 0, bool dropsAnything = true,
        bool liquid = false)
    {
        if (!ClearGrowth)
            return GrowthVerdict.Disabled;
        if (code is not { Length: > 0 })
            return GrowthVerdict.Empty;
        if (Spared(code))
            return GrowthVerdict.Spared;
        // ЖИДКОСТЬ НЕ СМАХИВАЮТ. У воды Replaceable 9500, у лавы 9000, и
        // выпадений нет ни у той, ни у другой: без этой строки правило назвало
        // бы лаву «мусором на дороге»
        if (liquid)
            return GrowthVerdict.Hard;
        bool undergrowth = GrowthMaterials.Contains(material);
        bool litter = replaceable >= WorldModel.ReplacedByPlacing && !dropsAnything;
        if (!undergrowth && !litter)
            return GrowthVerdict.Hard;
        // Мелочь берётся голыми руками. Раз игра просит тир — это порода
        if (requiredTier > 0)
            return GrowthVerdict.NeedsTool;
        // Через отрицание: бесконечность и NaN тоже «слишком долго»
        return seconds <= GrowthSeconds ? GrowthVerdict.Soft : GrowthVerdict.Slow;
    }

    /// <summary>Что бот думает про блок в этой клетке — без единого удара.</summary>
    public GrowthNote GrowthAt(BlockPos pos)
    {
        // Блок есть, а имени ему в реестре не нашлось — это НЕ пустая клетка.
        // Сказать «пусто» про занятую клетку значит соврать, поэтому даём
        // безымянному блоку знак вопроса: пусть судят по материалу
        string? code = ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) == 0
            ? null
            : ctx.World.GetBlockCode(pos) ?? "?";
        var material = ctx.World.BlockMaterial(pos.X, pos.Y, pos.Z);
        double seconds = FastestSeconds(pos);
        int tier = ctx.World.RequiredMiningTier(pos.X, pos.Y, pos.Z);
        // ОТВЕТЫ РЕЕСТРА, А НЕ ПРИСТАВКИ КОДА. Заменяемость — собственное
        // заявление игры «это затирают, а не ломают»; выпадение спрашивается
        // ПОД ТО, ЧТО В РУКЕ (у травы drygrass падает только под нож); жидкость
        // отсекается отдельно, иначе лава с её Replaceable 9000 попала бы
        // в «мусор на дороге»
        int replaceable = ctx.World.ReplaceableOf(pos.X, pos.Y, pos.Z);
        bool drops = ctx.World.DropsAnythingWith(pos.X, pos.Y, pos.Z,
            ctx.World.ToolOf(hands.Held?.Code));
        bool liquid = ctx.World.IsLiquidBlock(pos.X, pos.Y, pos.Z);

        var verdict = JudgeGrowth(code, material, seconds, tier, replaceable, drops, liquid);
        string? why = verdict switch
        {
            // Причина «не мелочь» обязана называть ОБА ответа реестра: без
            // числа заменяемости человек не отличит «игра считает это стеной»
            // от «игра считает мусором, но оно роняет добро»
            GrowthVerdict.Hard when liquid => $"{code} — жидкость, её не смахивают",
            GrowthVerdict.Hard => $"{material} — не подлесок, и мусором игра его не " +
                                  $"зовёт (заменяемость {replaceable} из " +
                                  $"{WorldModel.ReplacedByPlacing}" +
                                  (drops ? ", да и выпадение с него есть" : "") + ")",
            GrowthVerdict.Slow => $"ломать {seconds:0.#} с, дольше {GrowthSeconds:0.#}",
            GrowthVerdict.NeedsTool => $"нужен инструмент тира {tier}",
            _ => null
        };
        return new GrowthNote(pos, code ?? "", verdict, Message: why);
    }

    /// <summary>
    /// СМАХНУТЬ ОДНУ КЛЕТКУ МЕЛОЧИ — общая середина всех трёх уборок, и второй
    /// такой середины в проекте нет нарочно.
    ///
    /// Здесь же стоит вопрос «ЧЬЁ ЭТО», и стоит он ДО удара. Уборка — тоже
    /// ломка, и разойдись она с правилом «своё ломать можно, чужую постройку
    /// нет» (<see cref="BlockOwners"/>), бот сносил бы чужую грядку под видом
    /// травинки. Спрашиваем ТУ ЖЕ заставу, что и проходка, и дорога, и карьер
    /// (<see cref="BreakGuard"/>), и спрашиваем ради прохода: снег и трава для
    /// неё — заменяемый мусор из реестра сервера, и разрешение приходит само
    /// собой даже во дворе хозяина, а на чужом привате его не будет и у
    /// травинки.
    /// </summary>
    private async Task<GrowthNote> SweepOneAsync(BlockPos cell, CancellationToken ct)
    {
        var note = GrowthAt(cell);
        if (note.Verdict != GrowthVerdict.Soft)
            return note;
        // Прерывание — первым: иначе в отчёте будет «не дотянулся» там,
        // где на самом деле бота просто забрали на другое дело
        if (ct.IsCancellationRequested)
            return note with { Verdict = GrowthVerdict.Failed, Message = "прервано" };
        if (ctx.Break.MayBreak(cell, BreakWhy.Way) is { May: false } whose)
            return note with { Verdict = GrowthVerdict.Spared, Message = whose.Why };
        // Тело сейчас у того, кто нас позвал. Уйти «подходить к блоку»
        // значило бы отобрать у него дорогу — и уехать с маршрута ради
        // травинки. Дотянулись — ломаем, нет — так и говорим
        if (!hands.InReach(cell))
            return note with { Verdict = GrowthVerdict.OutOfReach };

        var result = await BreakAsync(cell, ct, why: BreakWhy.Way);
        return result.Success
            ? note with { Broken = true }
            : note with { Verdict = GrowthVerdict.Failed, Message = result.ToString() };
    }

    /// <summary>
    /// СМАХНУТЬ РОВНО ОДНУ МЕЛОЧЬ — ту, что мешает, и ни одной сверх неё.
    ///
    /// Зовёт отсюда рука, упёршаяся лучом в снег (<see cref="Hands.SweepAside"/>).
    /// Цена приёма нарочно посчитана штучно: заказчик просил «убирать ровно то,
    /// что мешает, а не всё вокруг», и списком клеток тут пользоваться нельзя —
    /// на луче мешает ПЕРВАЯ, и только она.
    ///
    /// ОБ УДАЧЕ ГОВОРИТ ВЫЗЫВАЮЩИЙ, А НЕ МЫ, и это не мелочь. Добыча знает
    /// ТОЛЬКО «что за блок и во что он обошёлся»; ЗАЧЕМ его сняли — знает
    /// позвавший (рука сбила помеху с прицела, ходьба — с дороги, штольня — со
    /// ступени), и без этого «зачем» строка в журнале не читается. Скажи оба —
    /// и на одну снежинку выйдет три строки подряд.
    ///
    /// А вот ОТКАЗ говорим здесь: причина у него наша (не мелочь, не дотянуться,
    /// чужое место), и молчать о ней нельзя — правило 4 проекта.
    /// </summary>
    public async Task<bool> SweepAsideAsync(BlockPos cell, CancellationToken ct = default)
    {
        var note = await SweepOneAsync(cell, ct);
        if (note.Broken)
            return true;
        // Пустую клетку не поминаем: «смахивать было нечего» — не новость
        if (note.Verdict != GrowthVerdict.Empty)
            Say($"мелочь в {cell} не смахнул: {note}");
        return false;
    }

    /// <summary>
    /// Клетки, которые надо освободить, чтобы встать в эту: ноги и голова.
    /// Тело занимает две клетки, и листва на уровне головы держит так же
    /// крепко, как под ногами.
    /// </summary>
    public IReadOnlyList<BlockPos> GrowthCells(BlockPos where)
    {
        var cells = new List<BlockPos>();
        for (int i = 0; i < GrowthHeight; i++)
            cells.Add(new BlockPos(where.X, where.Y + i, where.Z));
        return cells;
    }

    /// <summary>
    /// СЛОМАТЬ ПОДЛЕСОК, МЕШАЮЩИЙ ВСТАТЬ В КЛЕТКУ.
    ///
    /// Живой игрок сквозь подлесок идёт напролом: листва и трава ломаются
    /// голыми руками мгновенно, и он их сносит, не задумываясь. Бот же либо
    /// обходил заросли кругами, либо утыкался в них и стоял.
    ///
    /// Это МЕХАНИЗМ, а не привычка: сам по дороге он никого не зовёт —
    /// зовёт тот, кто ведёт бота (см. Movement). Ломается только мягкое и
    /// быстрое (см. <see cref="JudgeGrowth"/>), рубить лес по пути бот не
    /// станет.
    /// </summary>
    /// <param name="where">Клетка, в которую бот собирается шагнуть (ноги).</param>
    public Task<GrowthReport> ClearGrowthAsync(BlockPos where, CancellationToken ct = default) =>
        ClearGrowthAsync(GrowthCells(where), ct);

    /// <summary>
    /// То же для нескольких клеток разом — например, для пары ближайших шагов
    /// маршрута. Клетки берутся как есть, поэтому ноги и голову для каждой
    /// надо посчитать самому (см. <see cref="GrowthCells"/>).
    /// </summary>
    public async Task<GrowthReport> ClearGrowthAsync(IEnumerable<BlockPos> cells,
        CancellationToken ct = default)
    {
        var notes = new List<GrowthNote>();
        int broken = 0;

        foreach (var cell in cells)
        {
            // ПОТОЛОК НА ЗАХОД — единственное, чего нет у штучной уборки: он
            // про «не встать на просеку», а на луче мешает ровно одна клетка.
            // Спрашивается ДО общей середины, чтобы не бить и лишь потом
            // спохватываться
            if (broken >= MaxGrowthBlocks && GrowthAt(cell) is { Verdict: GrowthVerdict.Soft } over)
            {
                notes.Add(over with { Verdict = GrowthVerdict.Enough });
                continue;
            }
            // ОДНА СЕРЕДИНА НА ОБЕ УБОРКИ (см. SweepOneAsync): и суд по реестру,
            // и вопрос «чьё это», и длина руки, и сам удар. Второй копии этих
            // четырёх шагов рядом быть не должно — разъедься они, и «чужое не
            // ломаю» действовало бы на луче, но не на дороге
            var note = await SweepOneAsync(cell, ct);
            notes.Add(note);
            if (note.Broken)
                broken++;
        }

        var report = new GrowthReport(notes);
        // Молчать не о чем только когда и ломать было нечего
        if (report.Broken > 0 || report.Left.Any())
            Say($"расчистка {report}");
        SayTail();   // расчистка кончилась — досказываем промолчанное
        return report;
    }

    // ---------------- установка блока и её отказы ----------------

    /// <summary>
    /// Сколько РАЗ ЕЩЁ пробовать поставить блок, когда причина отказа
    /// поправимая (лаг сети, собственное тело в клетке).
    ///
    /// Живой случай заказчика (прогон 15.08): «повысить в задачах
    /// отказоустойчивость, если не получилось из-за лага или установки на себя
    /// поставить блок, то попробовать еще несколько раз». Ноль — не повторять
    /// вовсе, как было раньше.
    ///
    /// ПОЧЕМУ ДВА, А НЕ ДЕСЯТЬ. Каждая попытка ждёт ответа сервера
    /// <see cref="ConfirmSeconds"/>, и цену платит не только дорога: свет
    /// перебирает шесть граней под факел, и на каждую уходит своё ожидание.
    /// Два повтора закрывают лаг и собственное тело — то, ради чего заказ и
    /// делался, — а бесконечное долбление невозможного стоило бы минут.
    /// </summary>
    public int PlaceRetries { get; set; } = 2;

    /// <summary>Пауза перед повтором установки, секунд: дать сети догнать.</summary>
    public double PlaceRetryPauseSeconds { get; set; } = 0.3;

    /// <summary>Насколько далеко искать место, чтобы сойти с дороги своему же блоку.</summary>
    public int StepAsideRadius { get; set; } = 2;

    /// <summary>
    /// Ширина тела игрока в блоках — ОДНО число на весь проект
    /// (<see cref="BodySize.Width"/>), а не своя копия: разойдись они, и
    /// «встал бы в меня самого» отвечало бы про тело не той ширины, чем ходит.
    /// </summary>
    public const float PlayerWidth = (float)BodySize.Width;

    /// <summary>Рост тела игрока в блоках (<see cref="BodySize.Height"/>).</summary>
    public const float PlayerHeight = (float)BodySize.Height;

    /// <summary>
    /// СТОИТ ЛИ ПОВТОРЯТЬ УСТАНОВКУ ПОСЛЕ ТАКОГО ОТКАЗА — чистое правило,
    /// поэтому и проверяется стендом (правило 6 проекта).
    ///
    /// Разные причины лечатся по-разному, и в этом вся соль заказа. Лаг сети —
    /// это «ответа не было», он проходит сам, и повтор через треть секунды даёт
    /// блок. «Встал бы в меня самого» лечится шагом в сторону: игра отказывает
    /// потому, что коробка блока накрывает наше тело
    /// (Block.CanPlaceBlock → entityintersecting), и отойдя, бот ставит тот же
    /// блок с первого раза. А вот чужая заявка, занятая клетка и пустая сумка
    /// от повторов не меняются НИКАК: долбить сервер отказами — это шум в
    /// журнале, лишние приметы «чужого места» и потерянное время.
    /// </summary>
    public static bool WorthRetryingPlace(MiningRefusal why) =>
        why is MiningRefusal.NoAnswer or MiningRefusal.SelfInTheWay;

    /// <summary>
    /// ВСТАЛ БЫ БЛОК В МЕНЯ САМОГО — считаем ровно так же, как сервер.
    ///
    /// Проверка не выдумана: сервер сверяет коробки блока с коробкой ВЫБОРА
    /// сущности (CollisionTester.AabbIntersect в GameMain.GetIntersectingEntities,
    /// а зовёт её Block.CanPlaceBlock и отвечает «entityintersecting»). Своего
    /// игрока он при этом НЕ исключает — исключается только чужой
    /// (ServerSystemBlockSimulation.IsAnyPlayerInBlock ловит других). То есть
    /// «поставить блок в себя» — законный отказ, а не сбой связи, и повторять
    /// его, не сходя с места, бессмысленно.
    ///
    /// Блок без коробок столкновения (факел, трава, ковёр) телу не мешает
    /// вовсе — игра и не проверяет.
    /// </summary>
    /// <param name="boxes">Коробки столкновения ставимого блока.</param>
    /// <param name="where">Клетка, куда ставим.</param>
    /// <param name="feetX">Ноги тела: X.</param>
    /// <param name="feetY">Ноги тела: Y (низ коробки).</param>
    /// <param name="feetZ">Ноги тела: Z.</param>
    public static bool BodyInTheWay(Cuboidf[]? boxes, BlockPos where,
        double feetX, double feetY, double feetZ,
        float width = PlayerWidth, float height = PlayerHeight)
    {
        if (boxes is not { Length: > 0 })
            return false;
        var body = new Cuboidf(-width / 2, 0, -width / 2, width / 2, height, width / 2);
        var feet = new Vec3d(feetX, feetY, feetZ);
        foreach (var box in boxes)
            if (CollisionTester.AabbIntersect(box, where.X, where.Y, where.Z, body, feet))
                return true;
        return false;
    }

    /// <summary>Коробки столкновения блока по коду (пусто — реестр ещё не пришёл).</summary>
    private Cuboidf[]? BoxesOf(string? code) =>
        code is { Length: > 0 } && ctx.World.GameBlocksReady
            ? ctx.World.GetGameBlockByCode(code)?.CollisionBoxes
            : null;

    /// <summary>Имена четырёх горизонтальных граней — теми же словами, что в кодах блоков.</summary>
    private static readonly string[] Стороны4 = ["north", "east", "south", "west"];

    /// <summary>
    /// КАКОЙ СТОРОНОЙ ВСТАНЕТ БЛОК, КОГДА СТОРОНУ ВЫБИРАЕТ САМА ИГРА, —
    /// её же правило, число в число (<c>Block.SuggestedHVOrientation</c>,
    /// VintagestoryAPI 1.22.7). Угол считается от ТОЧКИ КЛИКА ДО ГЛАЗ, и
    /// сторона выходит ТА, ЧТО ДАЛЬШЕ ОТ ТЕЛА: стоишь севернее середины —
    /// блок ляжет на ЮЖНУЮ грань клетки.
    ///
    /// Своего правила тут нет ни на строку: <c>HORIZONTALS_ANGLEORDER</c> у
    /// игры это (восток, север, запад, юг), и порядок здесь тот же. Считаем
    /// во float с тем же приведением, что и она: на границе в 45° двойная
    /// точность дала бы соседнюю сторону.
    /// </summary>
    /// <param name="eyeX">Глаза тела: X. У игрока это X ног (LocalEyePos сдвигает только по высоте).</param>
    /// <param name="eyeZ">Глаза тела: Z.</param>
    /// <param name="hitX">Точка клика: X. Мы всегда бьём в середину грани (Actions.PlaceBlockAsync).</param>
    /// <param name="hitZ">Точка клика: Z.</param>
    public static string SideGamePicks(double eyeX, double eyeZ, double hitX, double hitZ)
    {
        float radians = (float)Math.Atan2(eyeX - hitX, eyeZ - hitZ) + (float)Math.PI / 2f;
        int i = GameMath.Mod((int)Math.Round(radians * (180f / (float)Math.PI) / 90f), 4);
        return i switch { 0 => "east", 1 => "north", 2 => "west", _ => "south" };
    }

    /// <summary>
    /// ЧТО ВСТАНЕТ В КЛЕТКУ НА САМОМ ДЕЛЕ — а не то, что лежит в руке.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО (мой стенд 30.08, 01:43:26; бот на
    /// дне ямы 1×1 глубиной 3, в сумке ladder-stick-north ×6 — ровно то, чего
    /// ему не хватало в жалобе заказчика 29.08):
    ///   «[стройка] строю лестницу на 3 бл вдоль стены юг»
    ///   «[добыча] ladder в (-42, 108, -55): ladder-stick-north встал бы в меня
    ///    самого — пробую ещё раз (1 из 3)»
    ///   «[движение] с (-42, 108, -55) не сойти: в радиусе 2 нет ни одного
    ///    места, где можно встать»
    ///   «[стройка] этот приём отсюда не поднял ни на блок — вычёркиваю его»
    /// Лестница вычеркнулась, НЕ ОТПРАВИВ НА СЕРВЕР НИ ОДНОГО ПАКЕТА: отказ был
    /// НАШ СОБСТВЕННЫЙ, и судил он не тот блок.
    ///
    /// ПОЧЕМУ НЕ ТОТ. В руке лежит <c>ladder-stick-north</c>, и его коробка
    /// прижата к СЕВЕРНОЙ грани клетки (assets/survival/blocktypes/wood/
    /// woodtyped/ladder.json: <c>collisionbox z2 = 0,1875</c> при
    /// <c>rotateYByType «*-north»: 0</c>). А сервер ставит СОВСЕМ ДРУГУЮ
    /// разновидность — сторону он выбирает сам
    /// (<c>BlockBehaviorLadder.TryPlaceBlock</c>: по вертикальной грани —
    /// <c>SuggestedHVOrientation</c>, по боковой — <c>Face.Opposite</c>) — и
    /// ЛИШЬ ПОТОМ спрашивает «не встал бы он в тело»
    /// (<c>Block.CanPlaceBlock</c> → <c>entityintersecting</c>).
    ///
    /// И ВЫБРАННАЯ ИГРОЙ СТОРОНА В ТЕЛО НЕ ПОПАДАЕТ ПОЧТИ НИКОГДА: она всегда
    /// дальняя от тела (<see cref="SideGamePicks"/>). То есть живой игрок в
    /// такой яме лестницу вешает, а бот отказывался — заказчик про это и
    /// сказал: «застрял и не стал вылезать, хотя есть лестницы с собой».
    ///
    /// ГРАНИЦЫ, ЗА КОТОРЫЕ ЭТО ПРАВИЛО НЕ ЛЕЗЕТ, — нарочно узкие:
    ///   • только КЛИК ПО ПОЛУ ИЛИ ПОТОЛКУ (грани 4 и 5). По ним обе игровые
    ///     повадки поворота — лестница и «горизонтально ориентируемый»
    ///     (<c>BlockBehaviorHorizontalOrientable</c>) — спрашивают ОДНО И ТО ЖЕ
    ///     <c>SuggestedHVOrientation</c>, и гадать не о чем. По боковой грани
    ///     они расходятся, и там мы ничего не предсказываем;
    ///   • только то, что УЖЕ стоит стороной: последняя часть кода — имя
    ///     горизонтальной грани. Иначе это не поворачиваемый блок;
    ///   • только если такая разновидность ЕСТЬ В РЕЕСТРЕ сервера. Нет — берём
    ///     то, что в руке, как и раньше.
    /// Подставляем сторону тем же зовом игры (<c>RegistryObject.CodeWithParts</c>),
    /// которым это делает и она сама.
    ///
    /// ХУЖЕ ОТ ЭТОГО НЕ СТАНЕТ НИКОМУ: ошибись предсказание — уйдёт пакет,
    /// который сервер отклонит, и повтор пойдёт обычным чередом. А молчаливый
    /// отказ по чужой коробке отнимал у бота приём целиком.
    /// </summary>
    /// <param name="held">Код того, что сейчас в руке.</param>
    /// <param name="onFace">Грань, по которой собираемся щёлкнуть (4 — верх, 5 — низ).</param>
    /// <param name="pos">Клетка, куда ставим.</param>
    public string? CodeThatWillStand(string? held, int onFace, BlockPos pos)
    {
        if (held is not { Length: > 0 } || !ctx.World.GameBlocksReady)
            return held;
        int dash = held.LastIndexOf('-');
        if (dash < 0 || !Стороны4.Contains(held[(dash + 1)..]))
            return held;
        if (ctx.World.GetGameBlockByCode(held) is not { BlockId: > 0 } block)
            return held;

        string side;
        if (onFace is 4 or 5)
        {
            if (Feet3d is not { } eye)
                return held;
            // Точка клика у нас всегда середина грани соседа, а он ровно под
            // клеткой (или над ней) — значит X и Z у него те же, что у клетки
            side = SideGamePicks(eye.X, eye.Z, pos.X + 0.5, pos.Z + 0.5);
        }
        else if (onFace is >= 0 and <= 3 &&
                 ctx.World.HasBlockBehavior(held, ПоведениеЛестницы))
        {
            // ПО БОКОВОЙ ГРАНИ У ЛЕСТНИЦЫ ГАДАТЬ НЕ О ЧЕМ: игра берёт
            // Face.Opposite и ничего не спрашивает у тела
            // (BlockBehaviorLadder.TryPlaceBlock, VSSurvivalMod 1.22.7).
            // Границы прежние и такие же узкие: спрашиваем РЕЕСТР, есть ли у
            // блока поведение «Ladder», а не гадаем по буквам кода — у
            // «горизонтально ориентируемого» правило по боковой грани ДРУГОЕ,
            // и путать их нельзя
            side = Стороны4[(onFace + 2) % 4];
        }
        else
            return held;

        string? code = block.CodeWithParts(side)?.ToShortString();
        return ctx.World.GetGameBlockByCode(code) is { BlockId: > 0 } ? code : held;
    }

    /// <summary>
    /// Имя поведения игры, которым помечена лестница в реестре сервера
    /// (assets/survival/blocktypes/wood/woodtyped/ladder.json: behaviors
    /// [{ name: "Ladder" }]). Точным именем, а не подстрокой кода: по коду
    /// «ladder» модовая полка ladder-shelf была бы лестницей.
    /// </summary>
    public const string ПоведениеЛестницы = "Ladder";

    /// <summary>Где сейчас ноги тела (null — своего положения бот не знает).</summary>
    private (double X, double Y, double Z)? Feet3d =>
        ctx.Body?.Physics is { } p ? (p.X, p.Y, p.Z)
        : ctx.Self.Position is { } s ? (s.X, s.Y, s.Z)
        : null;

    /// <summary>
    /// Поставить блок из своего инвентаря в клетку. Блок сначала берётся
    /// в руку — сервер ставит именно то, что в активном слоте.
    ///
    /// ОТКАЗ ОТКАЗУ РОЗНЬ, и здесь это главное. Один заход отвечает, ЧТО именно
    /// помешало (<see cref="MiningRefusal"/>), а этот метод решает, лечится ли
    /// помеха и как: подождать (лаг), отойти («встал бы в меня»), или честно
    /// сдаться (пусто в сумке, занятая клетка, чужая заявка).
    /// </summary>
    /// <param name="codePart">Часть кода блока: "soil", "cobblestone", "firewood".</param>
    /// <param name="onFace">Грань, «по которой кликнули»: 0=север,1=восток,2=юг,3=запад,4=верх,5=низ.</param>
    public async Task<MiningResult> PlaceAsync(BlockPos pos, string codePart, int onFace = 4,
        CancellationToken ct = default)
    {
        int tries = 1 + Math.Max(0, PlaceRetries);
        var last = new MiningResult(false, MiningRefusal.Rejected, "не пробовал");

        for (int attempt = 1; attempt <= tries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new MiningResult(false, MiningRefusal.Rejected, "прервано");

            // ОПОЗДАВШИЙ БЛОК — ЭТО НАШ БЛОК, А НЕ «КЛЕТКА ЗАНЯТА». Ради лага мы
            // и повторяем; лаг же означает, что ответ на прошлую попытку мог
            // прийти во время паузы. Не спроси мы об этом здесь, бот доложил бы
            // «клетка занята cobblestone-granite» про собственную же работу — и
            // это была бы ложь ровно того сорта, что запрещает правило 3
            // Пустая часть кода («поставь чем есть») сюда не годится: под неё
            // подходит ЛЮБОЙ блок, и опозданием оказался бы чужой камень
            if (attempt > 1 && codePart is { Length: > 0 } &&
                ctx.World.GetBlockCode(pos) is { Length: > 0 } now &&
                now.Contains(codePart, StringComparison.OrdinalIgnoreCase))
            {
                Say($"{codePart} в {pos} всё-таки встал — сервер ответил с опозданием");
                return new MiningResult(true);
            }

            last = await PlaceOnceAsync(pos, codePart, onFace, ct);
            if (last.Success)
            {
                // Молчать о том, что вышло не сразу, нельзя: именно по этим
                // строкам видно, лагает ли сервер или бот сам себе мешает
                if (attempt > 1)
                    Say($"{codePart} в {pos} встал с {attempt}-й попытки");
                return last;
            }
            if (!WorthRetryingPlace(last.Why) || attempt == tries)
                break;

            Say($"{codePart} в {pos}: {last.Message} — пробую ещё раз ({attempt} из {tries})");
            if (last.Why == MiningRefusal.SelfInTheWay)
                // Отходим от той коробки, которая И ПРАВДА встанет (см.
                // CodeThatWillStand): по чужой мы отходили бы не в ту сторону
                await StepOutOfTheWayAsync(pos, BoxesOf(
                    CodeThatWillStand(hands.Held?.Code ?? codePart,
                        hands.NeighbourToClickOn(pos) ?? onFace, pos)), ct);
            else
                await Task.Delay((int)Math.Max(1, PlaceRetryPauseSeconds * 1000), ct)
                    .ContinueWith(_ => { });
        }

        // ЧУЖОЕ, НАЗВАННОЕ САМИМ СЕРВЕРОМ, СОМНЕНИЙ НЕ ТРЕБУЕТ: тут не догадка
        // по молчанию, а прямой ответ «нет права строить» — запоминаем сразу
        if (last.Why == MiningRefusal.Claimed)
            ctx.Claims?.NotePlaceRefused(pos);
        // А ВОТ МОЛЧАНИЕ — ЛИШЬ ВОЗМОЖНАЯ примета, и считаем её ОДИН РАЗ НА ВСЮ
        // УСТАНОВКУ, а не по разу на попытку: иначе три повтора подряд сами
        // насчитали бы «отказов больше трёх — это приват»
        // (Claims.RefusalsToBelieve) и бот выгнал бы себя с собственной стройки
        // за один лаг сети
        else if (last.Why is MiningRefusal.NoAnswer or MiningRefusal.Rejected)
            ctx.Claims?.NoteRefusal(pos, ForeignKind.PlaceRefused);
        return last;
    }

    /// <summary>
    /// Один заход установки: взять блок, дотянуться, кликнуть, дождаться факта.
    /// Возвращает НАЗВАННУЮ причину отказа — по ней <see cref="PlaceAsync"/>
    /// и решает, повторять ли.
    /// </summary>
    private async Task<MiningResult> PlaceOnceAsync(BlockPos pos, string codePart, int onFace,
        CancellationToken ct)
    {
        // ЗАВЕДОМО ЗАНЯТУЮ КЛЕТКУ ДАЖЕ НЕ ПРОБУЕМ: стену сервер не заменит
        // ничем. А вот клетку с проходимой мелочью (трава, снег) пробуем: часть
        // такой мелочи игра заменяет сама, и запрет здесь стоил бы дороге
        // честных клеток. Сторожем от вранья тут служит не эта проверка, а
        // подтверждение ниже — оно требует СМЕНЫ блока
        if (ctx.World.GetBlockId(pos.X, pos.Y, pos.Z) != 0 && !ctx.World.IsPassable(pos.X, pos.Y, pos.Z))
            return new MiningResult(false, MiningRefusal.Occupied,
                $"клетка занята ({ctx.World.GetBlockCode(pos) ?? "чем-то"})");

        // Установка — такое же прицельное дело, что и удар: игрок ставит блок,
        // наведясь на грань соседа. Отвернувшись посреди этого, бот поставил бы
        // блок не туда или не поставил вовсе
        using var aim = ctx.Movement.HoldAim($"ставлю {codePart}");

        if (await hands.TakeToHandAsync(codePart, ct) is null)
            return new MiningResult(false, MiningRefusal.Nothing, $"нет блока {codePart}");

        // ГРАНЬ, ПО КОТОРОЙ ЩЁЛКНЕМ, — ПРОСИМАЯ ЗВАВШИМ, И ЭТО НЕ МЕЛОЧЬ.
        //
        // ЖИВОЙ СЛУЧАЙ ЗАКАЗЧИКА 01.09, 22:05, ПРИ ШЕСТИДЕСЯТИ СЕМИ ЛЕСТНИЦАХ
        // В СУМКЕ, ДОСЛОВНО:
        //   «наверх на 4 бл: лестниц 67 — хватит на 134 бл»
        //   «строю лестницу на 4 бл: опора есть (стена юг)»
        //   «ladder в (25, 108, 239): ответа на установку не было — пробую (1 из 3)»
        //   «лестницей: перекладина на 108 не встала: ответа на установку не было»
        //   «поднялся столбом на 4 бл (soil-medium-normal)»
        // Вылез он только землёй, подобранной минутой раньше. Сервер молча не
        // принял установку ТРИЖДЫ.
        //
        // ПОЧЕМУ. Стройка находит стену и называет её грань
        // (Scaffolding.WallSide), а сюда эта грань приходила ЗАПАСНЫМ ответом:
        // `NeighbourToClickOn(pos) ?? onFace` брал первого попавшегося соседа,
        // а первым в его списке стоит ПОЛ ПОД КЛЕТКОЙ. То есть бот всегда
        // щёлкал в пол. А игра по грани клика и выбирает сторону лестницы:
        // грань вертикальная — сторону берёт сама, ДАЛЬНЮЮ ОТ ТЕЛА
        // (SuggestedHVOrientation); грань боковая — сторона равна
        // Face.Opposite (BlockBehaviorLadder.TryPlaceBlock). Стоя в колодце
        // 1×1, бот оказывается в середине клетки, «дальняя от тела» выходит
        // случайной, и лестница получает сторону, у которой стены нет. А
        // держаться ей тогда не за что: HasSupport требует твердь СО СТОРОНЫ
        // СВОЕЙ ГРАНИ (или пол/потолок вплотную, или лестницу-продолжение), и
        // сервер отвечает «cantattachladder» — то есть молчанием, потому что
        // это не отказ прав, а обычная неудача установки.
        //
        // Первая перекладина живёт за счёт пола под собой и иногда встаёт;
        // вторая, у которой под ногами пустота, не встаёт уже никогда.
        //
        // Теперь грань идёт ПРОСИМОЙ: щёлкаем по стене, к которой и вешаем, —
        // ровно как это делает живой игрок. Просимая грань не приказ: нет по
        // ней соседа — берётся прежний порядок
        int Грань() => hands.NeighbourToClickOn(pos, onFace) ?? onFace;

        // ЧТО В РУКЕ — ЕЩЁ НЕ ТО, ЧТО ВСТАНЕТ: у блоков, которые крепятся к
        // стене, сторону выбирает сам сервер, и судить «встал бы в меня» надо
        // по НЕЙ. Разбор живого случая с лестницей — у CodeThatWillStand.
        // Спрашиваем ЗАНОВО после подхода: и грань, и своё место переменились
        string? Встанет() => CodeThatWillStand(hands.Held?.Code, Грань(), pos);

        string? встанет = Встанет();
        if (Feet3d is { } feet && BodyInTheWay(BoxesOf(встанет), pos, feet.X, feet.Y, feet.Z))
            return new MiningResult(false, MiningRefusal.SelfInTheWay,
                $"{встанет ?? codePart} встал бы в меня самого");

        if (!await hands.ReachForPlacementAsync(pos, ct: ct))
            return new MiningResult(false, MiningRefusal.OutOfReach, "не дотянуться");

        // Пока шли к месту, тело могло встать ровно туда, куда собирались
        // ставить, — это и есть тот самый живой случай «установки на себя»
        встанет = Встанет();
        if (Feet3d is { } now && BodyInTheWay(BoxesOf(встанет), pos, now.X, now.Y, now.Z))
            return new MiningResult(false, MiningRefusal.SelfInTheWay,
                $"подошёл и сам встал в клетку — {встанет ?? codePart} так не поставить");

        // ЧТО В КЛЕТКЕ ДО КЛИКА — мерка, по которой потом узнаётся успех.
        // Снимаем её последним делом перед пакетом: пока шли и брали блок в
        // руку, мир мог смениться
        int было = ctx.World.BlockIdOrUnknown(pos.X, pos.Y, pos.Z);

        // Кликаем по грани СОСЕДА — так ставит игрок. Грань считаем по тому,
        // с какой стороны нашлась опора, а не берём наугад «верх»
        var sentAt = DateTime.UtcNow;
        await ctx.Actions.PlaceBlockAsync(pos, Грань());

        var deadline = sentAt.AddSeconds(ConfirmSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // УСПЕХ — ЭТО СМЕНА БЛОКА, А НЕ «В КЛЕТКЕ ЧТО-ТО ЕСТЬ».
            //
            // Живой случай, из-за которого правило переписано: у высокой травы,
            // цветка и снега НЕТ коробки коллизии, проверку занятости выше они
            // проходят, и первая же итерация ожидания видела «id != 0» — то
            // есть саму траву — и отдавала Success ЕЩЁ ДО ЛЮБОГО ОТВЕТА
            // СЕРВЕРА. Дорога считала клетку замощённой (tally.Paved++,
            // RoadStop.Done), а в полотне оставалась дыра.
            //
            // Брод меряется той же меркой и без особого случая: вода лежит
            // ОТДЕЛЬНЫМ слоем чанка, блока в такой клетке нет (id 0), и
            // «замощено» там значит ровно одно — появился новый блок.
            //
            // Сменился id — значит блок положил СЕРВЕР: своей рукой модель мира
            // не правится, всё в ней приходит пакетом
            int стало = ctx.World.BlockIdOrUnknown(pos.X, pos.Y, pos.Z);
            if (стало > 0 && стало != было)
                return new MiningResult(true);

            // СЕРВЕР ОТВЕТИЛ СЛОВАМИ — и это конец разговора. На чужой заявке он
            // шлёт не молчание, а ошибку noprivilege-buildbreak-… с именем
            // хозяина (ServerSystemBlockSimulation.HandleBlockPlaceOrBreak);
            // повторять такое — только злить сервер
            if (ctx.Actions.RefusalSince(sentAt) is { NoBuildRights: true } no)
                return new MiningResult(false, MiningRefusal.Claimed,
                    $"сюда ставить не дают: {no}");

            await Task.Delay(LeftMs(deadline, ConfirmPollMs), ct).ContinueWith(_ => { });
        }
        // Ответа не было вовсе. Это ЕЩЁ НЕ «сервер не принял»: так же выглядит
        // лаг сети посреди собственной стройки, поэтому причина отдельная и
        // повторяемая
        return new MiningResult(false, MiningRefusal.NoAnswer,
            "ответа на установку не было");
    }

    /// <summary>
    /// СОЙТИ С ДОРОГИ СОБСТВЕННОМУ БЛОКУ. Место выбираем такое, где тело блоку
    /// уже не помеха, — и проверяем это ПО ФАКТУ, а не по тому, что шаг сделан:
    /// шагнуть можно и в другую клетку, всё ещё накрываемую коробкой блока.
    /// </summary>
    private async Task<bool> StepOutOfTheWayAsync(BlockPos pos, Cuboidf[]? boxes,
        CancellationToken ct)
    {
        await ctx.Movement.StepAsideFromAsync(pos, StepAsideRadius,
            spot => !BodyInTheWay(boxes, pos, spot.X + 0.5, spot.Y, spot.Z + 0.5), ct);

        if (Feet3d is not { } feet)
            return false;
        bool clear = !BodyInTheWay(boxes, pos, feet.X, feet.Y, feet.Z);
        if (!clear)
            Say($"отойти от {pos} не вышло — тело всё ещё в клетке");
        return clear;
    }

    /// <summary>Сколько ждать блока под ногами после установки, секунд.</summary>
    public double PillarConfirmSeconds { get; set; } = 1.5;

    /// <summary>
    /// СПУСТИТЬСЯ ПО СВОЕМУ ЖЕ СТОЛБУ: сломать блок под ногами, упасть на одну
    /// клетку, повторить. Обратный ход к <see cref="PillarUpAsync"/>.
    ///
    /// Зачем отдельно. Столб — гладкая колонна один на один: ни лестниц, ни
    /// уступов, и окно спуска у поиска пути кончается на трёх блоках. Бот
    /// поднимался на двадцать и оставался там навсегда, а живьём — разбился.
    /// Разбирать свой столб он умеет ровно потому, что сам его и ставил.
    ///
    /// Возвращает, на сколько блоков спустился.
    /// </summary>
    public async Task<int> PillarDownAsync(int levels = 64, CancellationToken ct = default)
    {
        if (ctx.Body?.Physics is not { } phys)
            return 0;

        int down = 0;
        for (int i = 0; i < levels && !ct.IsCancellationRequested; i++)
        {
            int fx = (int)Math.Floor(phys.X), fz = (int)Math.Floor(phys.Z);
            // ЛОМАТЬ НАДО ТО, НА ЧЁМ СТОИШЬ, а на частичном блоке (каменная
            // тропа, плита) это САМА клетка ног, а не та, что под ней: спуск
            // считал floor(Y) - 1 и бил блок ПОД тропой, оставаясь стоять
            int baseY = ctx.World.CellUnderFeetAt(phys.X, phys.Y, phys.Z);
            var under = new BlockPos(fx, baseY - 1, fz);

            // Пол под ногами кончился — значит мы уже внизу или висим
            if (ctx.World.IsPassable(under.X, under.Y, under.Z))
            {
                if (phys.OnGround)
                    break;
                await Task.Delay(200, ct).ContinueWith(_ => { });
                continue;
            }
            if (!CanBreak(under))
            {
                Say($"под ногами {ctx.World.GetBlockCode(under)} — дальше не разобрать, " +
                              $"спустился на {down}");
                break;
            }

            // ЧЕЙ ЭТО ПОЛ — СПРАШИВАЕМ ДО УДАРА, ровно как у потолка выше.
            //
            // Разбор своего столба — своё дело: блок, который бот сам и
            // поставил, помнит TempBlocks, и застава его пропускает. Но спуск
            // зовут и оттуда, куда бот забрался ногами (Scaffolding.GetDownAsync
            // -> WayDown.Pillar), а под ногами там бывает ЧУЖАЯ КРЫША. Прежде
            // здесь спрашивали только про инструмент, и дом заказчика уехал бы
            // вниз так же, как уезжал вверх
            var (мой, чейПол) = ctx.Break.MayBreak(under, BreakWhy.Way);
            if (!мой)
            {
                Say($"под ногами {ctx.World.GetBlockCode(under)}, и разбирать его я не " +
                    $"стану: {чейПол}; спустился на {down}");
                break;
            }

            double was = phys.Y;
            if (!(await BreakAsync(under, ct, why: BreakWhy.Way)).Success)
                break;

            // Ждём, пока тело провалится на клетку: без этого следующий шаг
            // считает пол по старой высоте и ломает воздух
            var wait = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < wait && phys.Y > was - 0.7 && !ct.IsCancellationRequested)
                await Task.Delay(60, ct).ContinueWith(_ => { });
            if (phys.Y > was - 0.7)
            {
                Say("блок сломан, а тело не опустилось — стою на чём-то ещё");
                break;
            }
            // СВОЙ ЖЕ ЛЕС, СНЯТЫЙ НЕ УБОРКОЙ. Не забудь мы его здесь — уборка
            // потом пришла бы ломать пустую клетку и честно доложила бы «тут
            // пусто», выдав чистую работу за недоделку
            ctx.Temp.Forget(under);
            down++;
        }
        if (down > 0)
            Say($"спустился по столбу на {down} бл");
        return down;
    }

    /// <summary>
    /// Столбиться вверх: прыжок — блок под себя — повторить.
    ///
    /// Это ровно то, чем игрок забирается туда, куда не ведёт лестница, и то,
    /// чем выбираются из ямы. Обоих случаев бот раньше не умел: доходил до
    /// верха лестницы и честно докладывал «ближе 13 бл не подобрался», а из
    /// собственного карьера сообщал «из ямы не выбраться».
    ///
    /// Механика честная: обычный прыжок телом на игровой физике и обычная
    /// установка блока в клетку, где были ноги. Никакой телепортации —
    /// если блок не встал, бот падает обратно и говорит об этом.
    ///
    /// Возвращает, на сколько блоков поднялся.
    /// </summary>
    /// <param name="keep">
    /// ДОРОГА ЭТО ИЛИ ЛЕСА — <see cref="ClimbKeep"/>. Решает не столб: он не
    /// знает, зачем его позвали. Разбор — у самого перечисления.
    /// </param>
    public async Task<int> PillarUpAsync(int levels, string blockCode = "",
        CancellationToken ct = default, ClimbKeep keep = ClimbKeep.Road)
    {
        if (ctx.Body?.Physics is not { } phys)
            return 0;
        if (levels <= 0)
            return 0;

        // Чем столбиться: что сказали, иначе любой блок из сумок
        string? code = blockCode.Length > 0 ? blockCode : SpareBlock();
        if (code == null)
        {
            Say("столбиться нечем — в сумках нет блоков");
            return 0;
        }

        int built = 0, ceilings = 0;
        for (int i = 0; i < levels; i++)
        {
            if (ct.IsCancellationRequested)
            {
                // Молча вернуть ноль — значит соврать: со стороны это «не умеет
                // столбиться», а на деле бота отвлекли на драку или еду
                Say($"столб прервали на {built} бл — тело забрали под другое дело");
                break;
            }
            if (await hands.TakeToHandAsync(code, ct) is null)
            {
                Say($"{code} кончился — поднялся на {built}");
                break;
            }

            int fx = (int)Math.Floor(phys.X), fz = (int)Math.Floor(phys.Z);
            // КЛЕТКА НОГ И КЛЕТКА, СВОБОДНАЯ ПОД НОГАМИ, — РАЗНЫЕ, когда под
            // ногами частичный блок. Живой случай 16.08, 20:36:38: бот стоял на
            // каменной тропе (верх 0.9375) на высоте 110,94, floor давал 110 —
            // клетку самой тропы, — и столб честно отвечал «столбиться в
            // (512076, 110, 512237) нельзя: клетка занята (stonepath-free)».
            // Свободна была клетка 111, и живой игрок кидает блок именно туда
            int baseY = ctx.World.CellUnderFeetAt(phys.X, phys.Y, phys.Z);
            var under = new BlockPos(fx, baseY, fz);
            // СТОЛБ — ЭТО ЛЕСА: живой игрок ставит его, чтобы подняться, и
            // разбирает, когда слезает. Спрашиваем разрешение ДО прыжка: в
            // чужой заявке блок под ноги не встанет и человеку, а лишний
            // отказ сервера бот запишет себе в «приметы чужого» на ровном месте.
            //
            // ЗАСТАВА ЗДЕСЬ ОДНА, И ЭТО ВАЖНО. До сведения потоков их стояло
            // ДВЕ подряд, обе про одно и то же («в клетке уже что-то есть»):
            // своя, по IsPassable, и она же внутри MayPlace. Своя срабатывала
            // первой всегда — из-за чего ветка «клетка занята» общего правила
            // не включалась в живой игре ни разу, а человек читал про один
            // случай два разных слова. Живой случай, с которого всё пошло:
            // 01:33:46 «под ногами уже stonepath-free — столбиться некуда».
            // Трава и цветы блоку по-прежнему не мешают — игра их заменяет, и
            // проходимую клетку MayPlace занятой не считает
            var (mayTemp, whyNotTemp) = ctx.Temp.MayPlace(under);
            if (!mayTemp)
            {
                // СВОЙ ЖЕ ЛЕС В КЛЕТКЕ НОГ — НЕ ПРИГОВОР, А УБОРКА. Живой случай
                // 12:53:14: «столбиться в (512184, 105, 512278) нельзя: клетка
                // занята (ladder-wood-acacia-north)» — перекладина, которую бот
                // сам повесил себе под ноги секундой раньше. Снимаем и растём
                // дальше; чужое и незнакомое отсюда по-прежнему не трогаем —
                // PullInTheWayAsync берёт только то, что числится за нами
                if (await ctx.Temp.PullInTheWayAsync([under],
                        $"столбиться в {under} мешает", ct) > 0)
                {
                    i--;        // уровень не потрачен: мы только расчистили место
                    continue;
                }
                Say($"столбиться в {under} нельзя: {whyNotTemp}; поднялся на {built}");
                break;
            }

            // Потолок. Прыгать в него бессмысленно: голова упирается, тело
            // поднимается на считанные сантиметры, и «прыжок не вышел» звучит
            // загадкой. Живьём так и было — бот столбился внутри дома
            // Потолок считается от СВОБОДНОЙ клетки, а не от клетки ног: стоя на
            // тропе, бот поднимается на клетку выше, и упереться головой он
            // может ровно на клетку выше прежнего
            var ceiling = new BlockPos(fx, baseY + 2, fz);
            if (!ctx.World.IsPassable(ceiling.X, ceiling.Y, ceiling.Z))
            {
                // НО «ПОТОЛОК» — ЭТО НЕ «НЕЛЬЗЯ». Заказчик поймал живьём: бот
                // сидел на дне своей же шахты под сплошным андезитом, звал
                // выход наверх и девять раз подряд отвечал «сначала надо
                // сломать потолок, поднялся на 0». Кирка при этом была в руках.
                // Живой игрок в такой яме бьёт вверх и столбится дальше —
                // это и есть «выкопать себе лесенку», о котором он говорил.
                //
                // Ломаем ОДНУ клетку над головой и продолжаем этот же уровень:
                // не «пробить шахту вверх», а ровно столько, сколько нужно
                // прыжку.
                string what = ctx.World.GetBlockCode(ceiling) ?? "что-то";
                if (!BreakCeiling || !CanBreak(ceiling))
                {
                    Say($"над головой {what} — сначала надо сломать потолок, " +
                                  $"а он не берётся; поднялся на {built}");
                    break;
                }

                // ЧЕЙ ЭТО ПОТОЛОК — СПРАШИВАЕМ ДО УДАРА.
                //
                // Здесь СТОЯЛА НЕПРАВДА: «бережёное (чужие постройки, защита)
                // отсекает CanBreak». Не отсекает — CanBreak знает только про
                // инструмент и тир (см. выше), про хозяина он не знает ничего.
                // А комментарий двумя строками выше сам называет живой случай:
                // «бот столбился ВНУТРИ ДОМА».
                //
                // Это ТРЕТЬЯ дорога к кирке ради выхода, и до этой правки она
                // была без заставы. Ступени проходят через Tunnel.ClearAsync,
                // добыча блоков на столб — через Scaffolding.MineForPillarAsync,
                // а столб, упёршийся в потолок, бил Mining.BreakAsync напрямую:
                // Scaffolding.GetOutAsync -> WayOut.Pillar -> сюда. Крыша дома
                // заказчика (пять брёвен debarkedlog-oak, ночь 18.08) уехала бы
                // тем же вечером, просто третьей дорогой.
                //
                // Второго решения тут не заводится: чей блок — знает
                // BlockOwners, а факты собирает общая застава (BreakGuard) —
                // та же, что у проходки, дороги, карьера и столбовой добычи.
                // Спрашиваем заранее, чтобы отказ прозвучал ДО подхода
                var (свой, чей) = ctx.Break.MayBreak(ceiling, BreakWhy.Way);
                if (!свой)
                {
                    Say($"над головой {what}, и пробивать его я не стану: {чей}; " +
                        $"поднялся на {built}");
                    break;
                }

                // Осыпь сверху может засыпать вскрытое, и тогда «пробить
                // потолок» повторяется вечно. Считаем удары и честно встаём
                if (++ceilings > levels + CeilingRetries)
                {
                    Say($"потолок над головой всё не кончается ({ceilings} клеток) — " +
                                  $"похоже, сверху сыплется; поднялся на {built}");
                    break;
                }

                Say($"над головой {what} — пробиваю потолок, чтобы столбиться дальше");
                if (!(await BreakAsync(ceiling, ct, why: BreakWhy.Way)).Success)
                {
                    Say($"потолок {what} не поддался — поднялся на {built}");
                    break;
                }
                i--;            // уровень не потрачен: мы только расчистили место
                continue;
            }

            // Прыжок и установка в высшей точке: пока тело выше пола, клетка
            // под ним свободна, и блок встаёт ровно туда, где были ноги
            // Смена руки — это пакет. Прыгать сразу после него нельзя: сервер
            // ещё держит в руке прежнее, и установка молча уходит в никуда
            await Task.Delay(200, ct).ContinueWith(_ => { });

            phys.Controls.Jump = true;
            bool placed = false;
            double peak = phys.Y;
            var deadline = DateTime.UtcNow.AddSeconds(1.2);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(30, ct).ContinueWith(_ => { });
                if (phys.Y > peak) peak = phys.Y;
                // СЕЛ ОБРАТНО, А БЛОК НЕ ВСТАЛ — ЖМЁМ ПРЫЖОК ЗАНОВО, ЯВНО.
                //
                // Слова тут не говорятся нарочно, и это не забывчивость: заход
                // крутится каждые 30 мс, и строка на каждую посадку залила бы
                // журнал. Про неудачу столба уже сказано ниже, целиком и с
                // числами («прыжок поднял только на …», «прыжок не вышел»).
                //
                // Раньше вторую попытку делала за нас зажатая клавиша: игра
                // прыгает сама, пока прыжок не отпущен, а её кулдаун (500 мс)
                // короче полёта (JumpPhysics.AirTime). На это никто не рассчитывал, оно
                // просто случалось. Теперь клавишу отпускает тело в тот же тик,
                // когда ноги коснулись земли (JumpKey — так лечится «лишний
                // прыжок у кромки»), и второй прыжок обязан быть попрошен
                if (!placed && phys.OnGround)
                    phys.Controls.Jump = true;
                // Не «повыше», а ПОЛНОСТЬЮ выше клетки. Пока ноги внутри неё,
                // блок пересекается с телом, и сервер ставить откажется.
                // Прыжок даёт 1.27, так что окно есть — но короткое
                if (phys.Y < baseY + 1.02)
                    continue;
                // Бьём весь пролёт, а не один раз: попадание в клетку зависит
                // от того, дошла ли до сервера наша высота. Лишние пакеты
                // безвредны — во вторую клетку блок уже не встанет
                await ctx.Actions.PlaceBlockAsync(under, 4);
                placed = true;
            }
            phys.Controls.Jump = false;
            if (peak < baseY + 1.02)
                Say($"прыжок поднял только на {peak - baseY:0.##} — клетку не освободить");
            if (!placed)
            {
                Say("прыжок не вышел — столб не растёт");
                break;
            }

            // Успех — только если блок правда появился И мы на нём стоим
            var wait = DateTime.UtcNow.AddSeconds(PillarConfirmSeconds);
            bool standing = false;
            while (DateTime.UtcNow < wait && !ct.IsCancellationRequested)
            {
                if (ctx.World.GetBlockId(under.X, under.Y, under.Z) != 0 &&
                    phys.OnGround && phys.Y >= baseY + 0.9)
                {
                    standing = true;
                    break;
                }
                await Task.Delay(50, ct).ContinueWith(_ => { });
            }
            if (!standing)
            {
                Say($"блок под ногами не встал (высота {phys.Y:0.#}) — остановился на {built}");
                break;
            }
            // Числим за собой ТОЛЬКО подтверждённое: «стоим на нём» — это и
            // есть факт от сервера, по которому цикл выше и признал успех.
            //
            // …И ТОЛЬКО ЛЕСА. Строка эта стояла БЕЗ УСЛОВИЯ, и всякий столб,
            // включая тот, которым бот вылез из ямы, ложился в долг на снос.
            // Заказчик 01.09, дословно: «тогда нет смысла убирать те леса, по
            // которым он вылез, это уже ошибка» — и он прав буквально: снять
            // такой столб значит вернуть себя в ту же яму, из которой только
            // что выбрался. Живьём долг за него висел и после: бот ушёл на
            // двадцать блоков, вспомнил про него, вернулся, «не дотянуться до
            // (25, 111, 239), до цели 20,6 бл», был перебит едой — и дёргался к
            // яме при каждом безделье. Разбор двух случаев — у ClimbKeep
            if (keep == ClimbKeep.Scaffold)
                ctx.Temp.Note(under, code, "добыча", $"столб наверх на {levels} бл");
            built++;
        }

        if (built > 0)
            // МОЛЧАТЬ ПРО ЭТО НЕЛЬЗЯ (закон 4): столб, оставленный стоять, —
            // это блок в мире, за который бот больше не отвечает, и человек
            // вправе прочесть, почему он там остался
            Say($"поднялся столбом на {built} бл ({code})" + (keep == ClimbKeep.Road
                ? " — это дорога из ямы, разбирать её не стану"
                : " — это леса, разберу их на спуске"));
        return built;
    }

    /// <summary>
    /// Слоты с блоками, которые можно ПОСТАВИТЬ: не инструменты (у тех есть
    /// прочность) и известны серверу как блоки.
    ///
    /// Здесь, в добыче, потому что ставит блоки именно она; спрашивают об этом
    /// трое — столб, заделка пола и закладка течи, — и ответ обязан быть один.
    /// «Что такое блок» отвечает РЕЕСТР СЕРВЕРА, а не список кодов: осколки
    /// камня stone-* в игре ПРЕДМЕТ, и написанный на глазок список рано или
    /// поздно решил бы, что ими можно столбиться.
    ///
    /// Это ответ на вопрос «что я вообще умею поставить», а не «что мне не
    /// жалко»: второй вопрос задаёт <see cref="SpareSlots"/>, и путать их
    /// нельзя — дорогу бот кладёт полотном сознательно.
    /// </summary>
    public IEnumerable<(string InventoryId, int Slot, SlotContent Content)> PlaceableSlots() =>
        ctx.Self.CarrySlots()
            .Where(s => !s.Content.IsEmpty && s.Content.Code is { } c &&
                        ctx.World.GetGameBlockByCode(c) is { BlockId: > 0 } &&
                        ctx.World.Durability(c) == 0);

    /// <summary>
    /// ЧТО БОТ ТРАТИТ НА ВРЕМЯНКУ — чистое правило, и все три числа в нём из
    /// РЕЕСТРА ИГРЫ, а не из выдуманного списка кодов.
    ///
    /// ЗАКАЗ ЧЕЛОВЕКА ДОСЛОВНО: «ставил леса временные (нужно проставить
    /// установку только из блоков земли) и сам застрял из-за них».
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 16.08, 12:53–12:56). Бот вёз с собой три с лишним
    /// сотни stonepath-free — покрытие для дороги, которую ему заказали. Ответ
    /// «блоков 351 — хватит на столб» считал их расходным материалом, и бот
    /// подкладывал дорожное полотно себе под ноги, а следующей строкой ломал
    /// его же (12:54:50 «ломаю stonepath-free»). Лестницы уходили туда же: своя
    /// перекладина встала в клетку ног, и столб отказался расти — «клетка
    /// занята (ladder-wood-acacia-north)».
    ///
    /// Три заставы, и каждая берётся у реестра:
    ///   • ЦЕННОЕ — материал Ore/Metal или код руды. Правило одно на весь
    ///     проект и живёт у дороги (<see cref="RoadPlan.IsPrecious"/>): там его
    ///     завели, когда бот выложил бы 81 клетку полотна медной рудой;
    ///   • ДОРОЖНОЕ ПОЛОТНО — игра САМА говорит, что по такому блоку ходят
    ///     быстрее (walkspeedmultiplier 1.30 у stonepath). Ничего изобретать не
    ///     пришлось: по этому же полю дорога отличает полотно от простого пола;
    ///   • ЗЕМЛЯ — материал Soil, Gravel, Sand или Stone. «Земля» тут прочитана
    ///     как ВЫКОПАННОЕ, А НЕ СДЕЛАННОЕ: булыжник живой игрок кидает себе под
    ///     ноги наравне с землёй, и отнимать это у бота значило бы оставить его
    ///     на дне каменной шахты. Дерево (а с ним и лестницы — ladder в реестре
    ///     Wood), кирпич, обожжённая глина, стекло, ткань, лёд сюда не попадают:
    ///     всё это сделано руками и в яме под ногой не нужно.
    /// </summary>
    /// <param name="code">Код блока — нужен второму рубежу распознавания руды.</param>
    /// <param name="material">Материал блока из реестра сервера.</param>
    /// <param name="walkSpeed">Множитель хода по блоку из реестра сервера.</param>
    /// <param name="metalInside">
    /// Ответ реестра «есть ли внутри металл» — второй рубеж ценного
    /// (<see cref="RoadPlan.IsPrecious"/>). Приносит его зовущий: у чистого
    /// правила реестра под рукой нет, а без этого ответа под ноги уезжал бы
    /// метеорит — материалом он «камень», а стоит пятидесяти единиц железа.
    /// </param>
    public static bool SpareFits(string code, EnumBlockMaterial material, float walkSpeed,
        bool metalInside) =>
        !RoadPlan.IsPrecious(material, metalInside) &&
        walkSpeed <= 1f &&
        material is EnumBlockMaterial.Soil or EnumBlockMaterial.Gravel
                 or EnumBlockMaterial.Sand or EnumBlockMaterial.Stone;

    /// <summary>
    /// ПОЧЕМУ ЭТОТ БЛОК НЕ ПОЙДЁТ ВО ВРЕМЯНКУ — словами, для журнала. Пустая
    /// строка значит «пойдёт». Молчаливое исключение из запасов было бы худшим
    /// исходом: человек читал бы «блоков 0» при полной сумке полотна и шёл бы
    /// носить боту то, чего у него и так триста.
    /// </summary>
    public static string WhyNotSpare(string code, EnumBlockMaterial material, float walkSpeed,
        bool metalInside)
    {
        if (RoadPlan.IsPrecious(material, metalInside))
            return $"ценное ({material}), под ноги такое не кладут";
        if (walkSpeed > 1f)
            return $"дорожное полотно (ход по нему ×{walkSpeed:0.##}) — его кладут " +
                   "в дорогу, а не под ноги на минуту";
        if (!SpareFits(code, material, walkSpeed, metalInside))
            return $"не земля и не порода ({material}) — сделанное руками под ноги не кладут";
        return "";
    }

    /// <summary>
    /// ЧТО НЕ ЖАЛКО ПОТРАТИТЬ: те же слоты, что и <see cref="PlaceableSlots"/>,
    /// но пропущенные через <see cref="SpareFits"/>. Спрашивают отсюда все, кто
    /// ставит блок НА ВРЕМЯ или ПОД СЕБЯ: столб, опора под ступень, мост,
    /// заделка пола, закладка течи.
    /// </summary>
    public IEnumerable<(string InventoryId, int Slot, SlotContent Content)> SpareSlots() =>
        PlaceableSlots().Where(s => s.Content.Code is { } c &&
                                    SpareFits(c, ctx.World.MaterialOf(c), ctx.World.WalkSpeedOf(c),
                                        МеталлВнутри(c)));

    /// <summary>
    /// ЕСТЬ ЛИ ВНУТРИ МЕТАЛЛ — ответ реестра, одно место на весь проект
    /// (<see cref="Metals.MetalInside"/>). Спрашивают отсюда все правила
    /// «ценное ли это»: без этого ответа метеорит уезжал бы под ноги.
    /// </summary>
    private bool МеталлВнутри(string code) => ctx.Metal?.MetalInside(code) ?? false;

    /// <summary>Сколько в сумках блоков, которые не жалко потратить под себя.</summary>
    public int SpareBlocks() => SpareSlots().Sum(s => s.Content.Count);

    /// <summary>
    /// Чем столбиться, заделывать пол и закладывать течь. Своя же порода из
    /// хода подходит лучше всего — её и берём первой (самая большая стопка).
    /// </summary>
    public string? SpareBlock() =>
        SpareSlots()
            .OrderByDescending(s => s.Content.Count)
            .Select(s => s.Content.Code)
            .FirstOrDefault();

    /// <summary>
    /// ЧТО ЛЕЖИТ В СУМКАХ БЛОКАМИ, НО ВО ВРЕМЯНКУ НЕ ПОЙДЁТ — с причиной на
    /// каждый. Нужно ровно затем, чтобы отказ «блоков под столб 0» можно было
    /// договорить до конца: «а лестниц 4 и полотна 351, только их я под ноги
    /// не кладу».
    /// </summary>
    public IEnumerable<(string Code, int Count, string Why)> SpareRejected() =>
        PlaceableSlots()
            .Where(s => s.Content.Code is { } c &&
                        !SpareFits(c, ctx.World.MaterialOf(c), ctx.World.WalkSpeedOf(c),
                            МеталлВнутри(c)))
            .GroupBy(s => s.Content.Code!)
            .Select(g => (Code: g.Key, Count: g.Sum(s => s.Content.Count),
                Why: WhyNotSpare(g.Key, ctx.World.MaterialOf(g.Key),
                    ctx.World.WalkSpeedOf(g.Key), МеталлВнутри(g.Key))));

    /// <summary>
    /// ЗАБРАТЬ ТО, ЧТО САМ ЖЕ ВЫБИЛ — ЭТО ЧАСТЬ ДОБЫЧИ, А НЕ ОТДЕЛЬНОЕ ДЕЛО.
    ///
    /// Здесь чинится живая беда «не стал добывать метеорит» (прогон 19.08).
    /// Бот с железной киркой честно разбил оба блока meteorite-iron, а в отчёте
    /// написал «принёс 0 из 1 — больше рядом нет». Восемь выпавших
    /// stone-meteorite-iron при этом лежали в четырёх-пяти блоках от него — их
    /// видела его же команда «!вещи». В журнале ровно одна причина, дважды:
    ///     01:28:04 [тело] «подбор» не берёт тело: занято «команда добудь»
    ///     01:28:25 [тело] «подбор» не берёт тело: занято «команда добудь»
    ///
    /// Подбор просил тело ЗАНОВО и самой низкой важностью (Routine), а тело в
    /// этот миг держала сама работа («команда добудь», Command). Равный не
    /// перебивает равного, а низший — тем более, и распорядитель отвечал
    /// законным отказом. Второе владение тут не нужно и вредно: у ВСЕХ, кто
    /// зовёт этот вход (поручение, карьер, грядка, сбор, разбор своей смерти),
    /// тело уже в руках — они как раз им и копали. Поэтому подбор идёт ПОД
    /// ЧУЖИМ владением и слушается токена работы: прервали работу — прервётся
    /// и он.
    ///
    /// Отдельное владение остаётся у того подбора, который САМ решает пойти
    /// (BehaviorPickUpDrops, «!подбери») — он и вправду отдельное дело.
    /// </summary>
    /// <remarks>
    /// Сама механика живёт в <see cref="Pickup"/> — одна на всех, кто роняет
    /// вещи на землю. Здесь остался только удобный вход для тех, у кого под
    /// рукой добыча, а не весь бот.
    /// </remarks>
    /// <param name="callerHoldsBody">
    /// Держит ли тело тот, кто зовёт (см. <see cref="PickupTakesOwnBody"/>).
    /// </param>
    public async Task<int> CollectDropsAsync(double radius = 6, double maxSeconds = 20,
        CancellationToken ct = default, bool callerHoldsBody = true)
    {
        var got = await ctx.Pickup.CollectAsync(radius, maxSeconds: maxSeconds, ct: ct,
            takeBody: PickupTakesOwnBody(callerHoldsBody));
        if (got.Items > 0)
            Say(got.ToString());
        return got.Items;
    }

    /// <summary>
    /// БРАТЬ ЛИ ПОДБОРУ СВОЁ ВЛАДЕНИЕ ТЕЛОМ — чистое правило.
    ///
    /// Держит тело зовущий — второго владения не бывает: распорядитель откажет
    /// (равный не перебивает равного), и добыча останется лежать на земле.
    /// Не держит — владение нужно, иначе подбор потянет бота с чужой дороги.
    /// </summary>
    public static bool PickupTakesOwnBody(bool callerHoldsBody) => !callerHoldsBody;
}
