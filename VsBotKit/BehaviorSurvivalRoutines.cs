namespace VsBotKit;

/// <summary>
/// Оружие наготове к ночи: днём бот может ходить с киркой, но к темноте
/// в руке должно быть боевое оружие, а не инструмент. Прятаться ночью
/// заказчик не просил — снаружи с мечом безопасно.
/// </summary>
public class BehaviorWeaponAtNight : BotBehavior
{
    private readonly BotContext ctx;
    private string? armed;

    public BehaviorWeaponAtNight(BotContext ctx)
    {
        this.ctx = ctx;
    }

    /// <summary>За сколько игровых часов до темноты браться за оружие.</summary>
    public double PrepareHours { get; set; } = 1.0;

    /// <summary>
    /// Инструмент как оружие ценится вдвое дешевле — И ЭТО ТО ЖЕ САМОЕ ЧИСЛО,
    /// ЧТО В БОЮ: не «как в бою», а буквально оно (см.
    /// <see cref="BotContext.Weapons"/>). Здесь только дверь к нему.
    /// </summary>
    public float ToolAsWeaponFactor
    {
        get => ctx.Weapons.ToolFactor;
        set => ctx.Weapons.ToolFactor = value;
    }

    /// <summary>
    /// Ниже этой боевой ценности предмет оружием не считается. Без порога
    /// «лучшим оружием» оказывались лён и семена: голый урон есть почти
    /// у всего, и бот брал в руку первый попавшийся хлам.
    ///
    /// ХОЗЯИН У ЧИСЛА ОДИН — <see cref="BotContext.Weapons"/>. Пока здесь жила
    /// вторая копия, «!бой 3» правил только самооборону: к темноте бот брал в
    /// руку предмет с ценностью 2 (порог тут оставался 1,5), а самооборона тот
    /// же предмет оружием не считала и командовала убегать.
    /// </summary>
    public float MinWeaponPower
    {
        get => ctx.Weapons.MinPower;
        set => ctx.Weapons.MinPower = value;
    }

    /// <summary>Взял оружие в руку (код, боевая ценность).</summary>
    public event Action<string, float>? OnArmed;

    /// <summary>Оружия не нашлось.</summary>
    public event Action? OnNoWeapon;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!ctx.Clock.Ready || ctx.Movement.IsBusy)
            return false;

        bool soonDark = ctx.Clock.IsNight || ctx.Clock.HoursUntilNight <= PrepareHours;
        if (!soonDark)
        {
            armed = null;         // рассвело — можно снова браться за инструменты
            return false;
        }

        var (code, power) = BestWeapon();
        if (code != null && !ctx.Weapons.IsWeapon(power))
            code = null;      // это не оружие, а просто предмет с уроном
        if (code == null)
        {
            if (armed != "-")
            {
                armed = "-";
                OnNoWeapon?.Invoke();
            }
            return false;
        }
        if (ctx.Hands.Held?.Code == code)
        {
            armed = code;
            return false;
        }

        // РУКУ БЕРЁМ ЧЕРЕЗ РАСПОРЯДИТЕЛЯ ТЕЛА — ту же очередь, что решает, кому
        // сейчас принадлежит бот. Живой случай 16.08: человек дал «!дорога»,
        // тело держала «команда дорога» и копала лопатой, а эта способность
        // каждую секунду тянула в руку blade-falx-meteoriciron — и добыча
        // минутами спорила за лопату сама с собой («перехватов подряд 2 — это
        // спор за руку… сдаюсь», и через секунду всё сначала). Хозяин у руки
        // теперь тот же, что у тела: занято делом не легче — отказ, и он назван
        // вслух самим распорядителем, одной строкой
        if (await ctx.Hands.TakeToHandAsync(Title, BodyArbiter.Importance.Routine,
                s => s.Code == code, ct) is null)
        {
            // ПРО «ВЗЯЛ» ГОВОРИМ ТОЛЬКО ПО ФАКТУ (правило 4). Раньше строка
            // «к ночи взял blade-falx-meteoriciron» уходила в журнал независимо
            // от того, доехало ли оружие до руки, — в живом журнале она стоит
            // ровно над спором за руку, которого по её словам не было
            armed = null;
            return false;
        }
        if (armed != code)
        {
            armed = code;
            OnArmed?.Invoke(code, power);
        }
        return false; // рука занята оружием, но ход способность не забирает
    }

    /// <summary>
    /// Лучшее оружие в сумках. Обход ОДИН на весь проект и живёт у мерки
    /// оружия (<see cref="WeaponStandard.BestInBags"/>): здесь он был списан
    /// слово в слово вместе с охотой.
    /// </summary>
    private (string? Code, float Power) BestWeapon() =>
        ctx.Weapons.BestInBags(ctx.World, ctx.Self.OwnInventories);
}

/// <summary>
/// Временная буря: спрятаться и переждать. Это самая важная способность —
/// ставится первой, потому что во время бури всё остальное не имеет смысла.
///
/// Порядок: дом с дверью, если он задан и близко; иначе — ниша в земле
/// с замуровыванием. Когда буря кончилась, бот разбирает стенку и
/// возвращается к делам.
/// </summary>
public class BehaviorShelterFromStorm : BotBehavior
{
    private readonly BotContext ctx;
    private readonly Shelter shelter;
    private bool hiding;

    public BehaviorShelterFromStorm(BotContext ctx, Shelter shelter)
    {
        this.ctx = ctx;
        this.shelter = shelter;
    }

    /// <summary>Дом, куда уходить (если есть). То же, что Shelter.Home.</summary>
    public BlockPos? Home
    {
        get => shelter.Home;
        set => shelter.Home = value;
    }

    /// <summary>Прятаться (можно выключить и пережидать бурю с оружием).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Пауза перед новой попыткой укрыться ИЛИ ВЫБРАТЬСЯ, если предыдущая не
    /// удалась.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ради которого пауза стала общей на оба случая. Выход из
    /// укрытия паузы не знал вовсе: буря кончилась 16.08 в 14:35:41, разбор
    /// стенки не удался — и способность звала его СНОВА на каждом тике. Строка
    /// «[укрытие] стенка не поддалась» шла два с половиной раза в секунду, пять
    /// тысяч триста пятьдесят девять раз подряд, пережив переподключение к
    /// серверу. Двадцать секунд между попытками — это ещё и время, за которое
    /// мир успевает перемениться: обвалится порода, уйдёт хозяин заявки, бот
    /// доест и возьмёт в руку кирку.
    /// </summary>
    public double RetrySeconds { get; set; } = 20;

    private DateTime nextTry = DateTime.MinValue;

    public event Action<string>? OnHiding;
    public event Action? OnStormOver;

    /// <summary>Укрыться не вышло — роль может решить, что делать (уйти, драться).</summary>
    public event Action? OnShelterFailed;

    /// <summary>
    /// ЗАМУРОВАН И НЕ ВЫБРАЛСЯ — беда, о которой надо сказать ГРОМКО И ОДИН РАЗ.
    ///
    /// Тревога, а не строка в журнал, ровно потому, что случай долгий: пока бот
    /// сидит в коробке, беда не меняется, и повторять её каждые двадцать секунд
    /// значит снова залить журнал (см. <see cref="Alarm"/> — второго такого
    /// счёта в проекте нет). Отбой звучит, когда бот выбрался.
    /// </summary>
    public Alarm Trapped { get; } = new("укрытие");

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled)
            return false;

        if (ctx.Storms.ShelterNow)
        {
            if (hiding)
                // Уже спрятались. Ход НЕ забираем: если в укрытие пролезет
                // тварь, бот должен отбиваться и лечиться, а не «пережидать»
                return false;
            if (DateTime.UtcNow < nextTry)
                return false;

            // ТЕЛА НЕ ДАЛИ — ЗНАЧИТ НЕ ДАЛИ, И РЫТЬ МЫ НЕ ИДЁМ.
            //
            // Здесь стояло «shelterHold?.Token ?? ct»: тело просили, получали
            // отказ — и всё равно копали. Это и есть ВТОРАЯ ДВЕРЬ к телу мимо
            // распорядителя, от которой он и заведён: бой стоит на той же
            // ступени Reflex, и «равный не перебивает» — значит во время
            // стычки бой уводил бота от дрифтера, а укрытие в те же секунды
            // рыло под ним яму. Все прочие способности проекта на отказ
            // уходят молчаливым false (бой, разгрузка, подбор, стабильность);
            // одно укрытие шло напролом.
            //
            // Вслух об отказе говорит сам распорядитель («„укрытие в бурю“ не
            // берёт тело: занято „бой“», с заглушкой повторов), второй раз
            // повторять незачем. А вот СКАЗАТЬ «прячусь» и поставить hiding,
            // не тронув тела, нельзя вовсе: следующий тик поверил бы флагу и
            // всю бурю простоял бы в чистом поле, считая себя укрытым
            //
            // И БЕРЁМ ЕГО ДВЕРЬЮ СПАСЕНИЯ, а не голой ступенью рефлекса. Здесь
            // стоял TryTake, и начатая секунду назад работа держала укрытие
            // выдержкой начатого дела (BodyArbiter.SettleSeconds): стенд
            // приёмки напечатал на это null. Буря идёт минутами и бьёт
            // насмерть — ждать выдержку в поле нельзя ни секунды. Ступень при
            // этом та же: жизнь (еда, лечение, выход из огня) выше нас
            using var shelterHold = ctx.Turn.TakeForRescue(
                "укрытие в бурю", $"буря силой {ctx.Storms.Strength:0.##} — прячусь", ct);
            if (shelterHold == null)
                return false;

            OnHiding?.Invoke(ctx.Storms.Strength);
            // Правило 4: «спрятался» — только если укрытие получилось.
            // Раньше флаг ставился ДО попытки, и бот, не нашедший ни дома,
            // ни стены, всю бурю стоял в чистом поле, доложив об укрытии
            hiding = await shelter.HideAsync(ct: shelterHold.Token);
            if (!hiding)
            {
                nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
                OnShelterFailed?.Invoke();
            }
            return true; // укрытие — важнее всего, пока ищем и роем
        }

        // Буря кончилась. Разбираем стенку — и пробуем, пока не выберемся.
        //
        // СПРАШИВАЕМ УКРЫТИЕ ЦЕЛИКОМ (Shelter.Hidden), а не один свой флажок.
        // Приёмов у укрытия два, и второй — это СВОЯ ЗАКРЫТАЯ ДВЕРЬ, о которой
        // здесь стоял только WalledIn («замурован блоками»). Память о двери не
        // стиралась НИКОГДА: буря кончилась, бот ушёл работать — а бой всё
        // отвечал «я нарочно спрятался, и укрытие держится» и запрещал себе
        // и драку, и бегство
        if (!hiding && !shelter.Hidden)
            return false;

        // «БУРЯ ПРОШЛА» — ЭТО ФАКТ О МИРЕ, и он не зависит от того, поддалась ли
        // стенка. Говорим сразу и один раз: раньше эта строка стояла ПОСЛЕ
        // разбора, и в живом журнале выходила вперемешку с воплями о стенке
        if (hiding)
        {
            hiding = false;
            OnStormOver?.Invoke();
        }

        if (shelter.WalledIn)
        {
            // НЕ ЧАЩЕ РАЗА В RetrySeconds. Вот эта строка и есть лечение живого
            // случая: прежде разбор звался КАЖДЫЙ тик и, провалившись, держал
            // ход за собой (return shelter.WalledIn), не пуская к телу ни еду,
            // ни лечение, ни бой
            if (DateTime.UtcNow < nextTry)
                return false;

            // ТЕЛО БЕРЁМ ЧЕСТНО, И ОТКАЗ ЗНАЧИТ ОТКАЗ. Разбор стенки — это
            // удары киркой и шаги; делать их, пока телом распоряжается другое
            // дело, значит топтаться вдвоём на одних клавишах. Отказать тут
            // может дело ЖИЗНИ (еда, лечение, спасение из воды — ступень выше)
            // или равный рефлекс, и в обоих случаях бот сейчас занят тем, что
            // важнее стенки: замурованный не умирает, а голодный умирает.
            //
            // Прежде здесь стояло «hold?.Token ?? ct» — тело просили и, получив
            // отказ, всё равно долбили стену: жующий бот отходил от еды бить
            // кирку. Слова «берём честно» в комментарии при этом уже стояли
            using var hold = ctx.Turn.TryTake(
                "выбраться из укрытия", BodyArbiter.Importance.Reflex, ct);
            if (hold == null)
                return false;

            // СРОК СЛЕДУЮЩЕЙ ПОПЫТКИ СТАВИМ ТОЛЬКО КОГДА ПОПЫТКА И ПРАВДА
            // БЫЛА: иначе чужое дело жизни на двадцать секунд отодвигало бы
            // разбор, ничего взамен не сделав
            nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
            var выход = await shelter.BreakOutAsync(hold.Token);
            if (!выход.Success)
            {
                Trapped.Raise(выход.Why);
                return true;   // ход был занят делом, и это правда
            }
            Trapped.Clear("выбрался наружу");
        }

        // Стенка разобрана, дверь забыта: с этой секунды бот отвечает за
        // себя как обычно, а не как спрятавшийся
        shelter.StopHiding();
        return false;
    }
}

/// <summary>
/// КОНЧИЛАСЬ ЕДА — СДЕЛАТЬ ТАК, ЧТОБЫ ЕДА БЫЛА. Это РУКИ ГОЛОДА и первый
/// заполненный бланк общего механизма подцелей (<see cref="BehaviorMeetNeed"/>).
///
/// ЧТО ЗДЕСЬ ОСТАЛОСЬ И ЧЕГО НЕ ОСТАЛОСЬ. Своего цикла у этой способности
/// больше нет: пройти способы по порядку и после каждого спросить мир — работа
/// <see cref="NeedQuest"/>. Здесь только ОБЪЯВЛЕНИЕ: кто такая нужда, чем
/// меряется её закрытие и какими способами её закрывают. Следующая нужда
/// (лечебное, тепло, инструмент) подключается таким же объявлением, а не копией
/// этого класса.
///
/// ПОРЯДОК СПОСОБОВ — ЭТО ПЛАН, И НАЗВАЛ ЕГО ЗАКАЗЧИК. Слова дословно: «За всем
/// для жизни что ему нужно хилки и тд он должен решать здесь может добыть или
/// надо домой за ними (если они там есть)». Кладовые первыми: ягодник в
/// двадцати шагах бот обирает минуту, а хлеб из своего же сундука берёт разом.
/// Готовка вторая: сырое мясо, уже лежащее в сумке, дешевле любого похода.
/// Дикая еда третья, охота последняя — она дольше и опаснее всех.
///
/// ГРАНИЦЫ БОЛЬШЕ НЕТ. До 26.08 здесь стояло дословно: «ЧЕСТНО ПРО ГРАНИЦУ:
/// охота и готовка — работа отдельная и здесь не начата», и цепочка обрывалась
/// на «дикой еды в 32 бл не видно» — при том что убивать, разделывать и
/// готовить бот умел. Заказчик это и поправил: «у нас есть базовая готовка… и
/// охота тоже… так что доделывай».
/// </summary>
public class BehaviorForageWhenNoFood : BehaviorMeetNeed
{
    private readonly BotContext ctx;
    private readonly Foraging foraging;
    private readonly Cooking? cooking;
    private readonly Hunting? hunting;

    public BehaviorForageWhenNoFood(BotContext ctx, Foraging foraging,
        Cooking? cooking = null, Hunting? hunting = null)
        : base(ctx, new NeedDeclaration
        {
            Нужда = typeof(BehaviorEatWhenHungry),
            Имя = "еда"
        })
    {
        this.ctx = ctx;
        this.foraging = foraging;
        this.cooking = cooking;
        this.hunting = hunting;

        // МЕРКИ НУЖДЫ. «Съедобно» считает ИГРА (сытость из реестра), а не наш
        // список кодов: так находится и модовая еда. Ставятся здесь, а не в
        // инициализаторе выше, потому что читают СВОИ ручки — до вызова базового
        // конструктора трогать свои поля язык не даёт
        Need.Годится = code => ctx.World.GetSatietyByCode(code) > 0;
        Need.Сколько = 1;
        Need.Созрела = () =>
            ctx.Self.Saturation is { } sat && ctx.Self.MaxSaturation is { } max && max > 0 &&
            sat / max <= HungerFraction;
        // ЕДА В СУМКЕ — ЗАБОТА САМОЙ ЕДЫ (BehaviorEatWhenHungry), а не её рук.
        // Мерка та же самая, что у неё: FindBestItem по сытости из реестра
        Need.Закрыта = () =>
            ctx.Self.FindBestItem(s => s.Code != null ? ctx.World.GetSatietyByCode(s.Code) : 0) != null;

        Ways.Add(Кладовые());
        Ways.Add(Готовка());
        Ways.Add(ДикаяЕда());
        Ways.Add(Охота());
    }

    /// <summary>Ниже какой доли сытости идти за едой.</summary>
    public double HungerFraction { get; set; } = 0.5;

    /// <summary>Как далеко искать дикую еду (от места, где бот стоял в начале вылазки).</summary>
    public int Radius { get; set; } = 32;

    /// <summary>
    /// Сколько времени отводится на один заход за дикой едой. Способность
    /// держит тело, пока работает, поэтому длинная вылазка означала бы, что бот
    /// не отбивается и не лечится всё это время. Лучше несколько коротких.
    /// </summary>
    public double SecondsPerTrip { get; set; } = 25;

    /// <summary>
    /// ОТКУДА МЕРИТСЯ «ДАЛЕКО» — место, где бот стоял в начале ЭТОЙ вылазки.
    ///
    /// СТАВИТСЯ И СНИМАЕТСЯ РОВНО НА ОДИН ПОХОД (<see cref="ПередПоходом"/> и
    /// <see cref="ПослеПохода"/>), и это поправка приёмки. Прежде якорь
    /// ставился один раз через <c>??=</c> и не сбрасывался НИКЕМ И НИКОГДА:
    /// пока он ограничивал только сбор ягод, беда была невелика, а с приходом
    /// охоты стала настоящей. Охота ищет зверя ВОКРУГ СЕБЯ, а предел «далеко»
    /// мерит ОТ ЯКОРЯ — стоило боту переехать на новый забой дальше, чем
    /// «ОхотитьсяНеДальше», и вся дичь отсеивалась навсегда, а заголовок отказа
    /// врал: «дикого зверя в 32 бл не нашёл» при олене в пяти шагах.
    /// </summary>
    public BlockPos? Anchor { get; set; }

    /// <summary>
    /// Сходить за едой в кладовые, прежде чем идти за дикой. Роль вправе
    /// запретить — например, боту, которому склад делить с людьми нельзя.
    /// </summary>
    public bool FromStores { get; set; } = true;

    /// <summary>Сколько еды приносить из кладовых за раз.</summary>
    public int StoresBatch { get; set; } = 4;

    /// <summary>
    /// ГОТОВИТЬ СЫРОЕ. Без этого звена убитый зверь для бота — ноль калорий: у
    /// сырого мяса в игре нет nutritionProps вовсе, и еда его не видит.
    /// </summary>
    public bool Cook { get; set; } = true;

    /// <summary>ОХОТИТЬСЯ, когда еды нет нигде.</summary>
    public bool Hunt { get; set; } = true;

    /// <summary>
    /// Как далеко от якоря отпускать охоту, блоков. ЧИСЛО НЕ ЗДЕСЬ ЖИВЁТ, а у
    /// самой охоты (<see cref="Hunting.Radius"/>) — здесь только дверь к нему,
    /// ровно как у «оружия к ночи» дверь к общей мерке оружия. Двух копий
    /// одного числа в проекте не бывает: они расходятся молча.
    /// </summary>
    public double HuntRadius
    {
        get => hunting?.Radius ?? 0;
        set { if (hunting != null) hunting.Radius = value; }
    }

    /// <summary>Сколько держать тело за один заход охоты, секунд (см. <see cref="Hunting.SecondsPerTrip"/>).</summary>
    public double HuntSeconds
    {
        get => hunting?.SecondsPerTrip ?? 0;
        set { if (hunting != null) hunting.SecondsPerTrip = value; }
    }

    /// <summary>
    /// Сколько ждать готового у костра, секунд. Тоже дверь, а не копия: срок
    /// живёт у кухни (<see cref="Cooking.CookTimeoutSeconds"/>), и её же зовёт
    /// команда из чата.
    /// </summary>
    public double CookWaitSeconds
    {
        get => cooking?.CookTimeoutSeconds ?? 0;
        set { if (cooking != null) cooking.CookTimeoutSeconds = value; }
    }

    /// <summary>Набрал дикой еды: в скольких местах.</summary>
    public event Action<int>? OnGathered;

    /// <summary>Чем кончился поход за едой в кладовые — словами цепочки снабжения.</summary>
    public event Action<string>? OnFromStores;

    // ---------------- способы ----------------

    /// <summary>
    /// КЛАДОВЫЕ. Решение «здесь или домой» принимает ОДНО правило на весь бот
    /// (<see cref="Supply.ForVitalAsync"/>) — то же, что и у лечения. Крафт и
    /// «добыть рядом» выключены нарочно: готовка стоит отдельным способом ниже,
    /// а «добыть рядом» для еды и есть вылазка со своим сроком и своим якорем.
    /// </summary>
    private NeedWay Кладовые() => new()
    {
        Имя = "кладовые",
        Разрешён = () => FromStores && ctx.Supply != null,
        ПочемуНеВелено = () => ctx.Supply == null
            ? "снабжение к боту не подключено — в кладовые идти нечем"
            : "в кладовые за едой не хожу (ручка «ЕдаИзКладовых»)",
        Сделать = async (ask, ct) =>
        {
            var итог = await ctx.Supply.ForVitalAsync(ask.Имя, ask.Годится,
                Math.Max(1, StoresBatch), ct, craft: false, nearby: false);
            OnFromStores?.Invoke(итог.Reason);
            return new NeedTry(итог.Brought > 0, итог.Reason);
        }
    };

    /// <summary>
    /// ГОТОВКА. «Приготовил» — только когда готовое лежит в сумке и съедобно по
    /// реестру (<see cref="Cooking.MakeEdibleAsync"/>), а не когда положили в
    /// костёр.
    /// </summary>
    private NeedWay Готовка() => new()
    {
        Имя = "готовка",
        Разрешён = () => Cook && cooking != null,
        ПочемуНеВелено = () => cooking == null
            ? "кухня к этой способности не подключена — готовить нечем"
            : "готовить не велено (ручка «ГотовитьЕду»)",
        Сделать = async (ask, ct) =>
        {
            var шаг = await cooking!.MakeEdibleAsync(ask.Годится, ct);
            // Своё «получилось» кухня говорит честно, но решает не она: закрыта
            // ли нужда, спросят у сумки (см. NeedQuest). Здесь её ответ нужен
            // только затем, чтобы бот вернулся к делу через секунды
            return new NeedTry(шаг.Ok || шаг.Moved, шаг.Why);
        }
    };

    /// <summary>
    /// ДИКАЯ ЕДА. Радиус мерится от якоря вылазки (см. <see cref="Anchor"/>):
    /// иначе бот «уползает» кустами — в живом прогоне 28.07 он так ушёл от
    /// лавки на 250 блоков и потерял треть здоровья.
    /// </summary>
    private NeedWay ДикаяЕда() => new()
    {
        Имя = "дикая еда",
        Разрешён = () => true,
        ПочемуНеВелено = () => "",
        Сделать = async (_, ct) =>
        {
            var сбор = await foraging.GatherAsync(Radius, maxPlaces: 3, SecondsPerTrip, Anchor, ct);
            if (сбор.Places > 0)
                OnGathered?.Invoke(сбор.Places);
            return new NeedTry(сбор.Places > 0, сбор.Places > 0
                ? $"набрал в {сбор.Places} местах"
                : $"дикой еды в {Radius} бл не видно");
        }
    };

    /// <summary>
    /// ОХОТА. Последней: она дольше и опаснее всех прочих. Кого бить — решает
    /// <see cref="HuntRules"/>, и чужую скотину бот не трогает.
    /// </summary>
    private NeedWay Охота() => new()
    {
        Имя = "охота",
        Разрешён = () => Hunt && hunting != null,
        ПочемуНеВелено = () => hunting == null
            ? "охота к этой способности не подключена"
            : "охотиться не велено (ручка «Охотиться»)",
        Сделать = async (_, ct) =>
        {
            var добыча = await hunting!.HuntAsync(Anchor, ct);
            return new NeedTry(добыча.Any, добыча.ToString());
        }
    };

    /// <summary>
    /// ЯКОРЬ СТАВИТСЯ ДО ПЕРВОГО СПОСОБА, А НЕ ВНУТРИ НЕГО.
    ///
    /// Прежде его ставил тот способ, которому он понадобился первым, — то есть
    /// «дикая еда», уже ПОСЛЕ рейса в кладовые. Якорь вставал там, где кончился
    /// поход за хлебом, а не там, где бот работает.
    /// </summary>
    protected override void ПередПоходом() =>
        Anchor = ctx.Self.Position is { } p
            ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
            : null;

    /// <summary>Вылазка кончилась — мерка «далеко» кончилась вместе с ней.</summary>
    protected override void ПослеПохода() => Anchor = null;
}
