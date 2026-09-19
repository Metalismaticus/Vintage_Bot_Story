namespace VsBotKit;

/// <summary>
/// Еда при голоде: когда сытость падает ниже порога, бот находит самую
/// питательную еду в хотбаре (по данным реестра — работает и с модовой едой)
/// и съедает её. Кулдаун защищает от зацикливания, если еда «не пошла».
/// </summary>
public class BehaviorEatWhenHungry : BotBehavior
{
    private readonly SelfState self;
    private readonly WorldModel world;
    private readonly Hands hands;
    private readonly BotContext? ctx;

    /// <summary>Порог голода: доля от максимальной сытости (0.4 = есть при 40%).</summary>
    public double HungerThreshold { get; set; } = 0.4;

    /// <summary>Пауза между попытками поесть, сек.</summary>
    public int CooldownSeconds { get; set; } = 12;

    /// <summary>Бот поел (код еды, слот).</summary>
    public event Action<string, int>? OnAte;

    /// <summary>Голоден, но еды в хотбаре нет.</summary>
    public event Action? OnNoFood;

    private DateTime lastAttempt = DateTime.MinValue;
    private float? lastHealth;
    private DateTime hungerHurtAt = DateTime.MinValue;

    /// <summary>
    /// ПОЕСТЬ НЕ ВЫХОДИТ — громко один раз, а не сто строк в минуту и не
    /// молчание. Тревога сама решает, новость это или уже сказанное: пока
    /// причина та же — молчит, причина сменилась — говорит сразу, а про долгую
    /// беду напоминает раз в пять минут (иначе бот, час назад сказавший «еды
    /// нет», выглядит здоровым).
    /// </summary>
    private readonly Alarm cannotEat = new("поесть не выходит");

    /// <summary>Голоден, а еды нет вовсе — та же мерка, но своё событие.</summary>
    private readonly Alarm noFood = new("еды нет");

    public BehaviorEatWhenHungry(BotContext ctx)
        : this(ctx.Self, ctx.World, ctx.Hands) => this.ctx = ctx;

    /// <summary>
    /// Сказать, что поесть не вышло — но только если это новость.
    /// Молчать нельзя: именно эта строка объясняет, почему бот умирает с
    /// хлебом в кармане. Повторяться тоже нельзя: способность тикает дважды в
    /// секунду, и в шуме заказчик настоящей беды не увидел.
    /// </summary>
    private void НеПоел(string code, string why)
    {
        if (cannotEat.Raise($"{code}: {why}"))
            OnEatFailed?.Invoke(code, why);
    }

    /// <summary>
    /// Подбирать еду, лежащую рядом. Живой случай: заказчик бросил голодному
    /// боту мясо под ноги, а тот продолжал голодать — он умел есть только из
    /// сумки, а поднять с земли не догадывался. Игрок в такой ситуации просто
    /// наступает на предмет.
    /// </summary>
    public bool PickUpFoodNearby { get; set; } = true;

    /// <summary>Насколько далеко идти за лежащей едой, блоков.</summary>
    public double PickUpRadius { get; set; } = 12;

    /// <summary>Бот пошёл поднимать еду с земли.</summary>
    public event Action<double>? OnGoingForDrop;

    /// <summary>Поесть не вышло: что и почему. Молчать об этом нельзя.</summary>
    public event Action<string, string>? OnEatFailed;

    /// <summary>Через сколько секунд пробовать снова после НЕУДАЧНОЙ попытки.</summary>
    public double RetryAfterFailSeconds { get; set; } = 3;

    /// <summary>
    /// Урон от голода за последние секунды. Здоровье падает по многим причинам,
    /// но нам важно не «кто ударил», а «нельзя молчать»: если бот теряет
    /// здоровье и при этом ничего не ест, надо хотя бы попытаться.
    /// </summary>
    public double HungerDamageMemorySeconds { get; set; } = 20;

    private bool NoticeHungerDamage()
    {
        float? now = self.Health;
        if (now is { } hp && lastHealth is { } was && hp < was - 0.01f &&
            self.Saturation is { } s && self.MaxSaturation is { } m && m > 0 &&
            s / m < 0.05)
            hungerHurtAt = DateTime.UtcNow;
        // Сытость неизвестна — любой урон засчитываем как возможный голодный:
        // хуже сделать нельзя, а не поесть — смертельно
        else if (now is { } hp2 && lastHealth is { } was2 && hp2 < was2 - 0.01f &&
                 self.Saturation is null)
            hungerHurtAt = DateTime.UtcNow;
        lastHealth = now;
        return (DateTime.UtcNow - hungerHurtAt).TotalSeconds < HungerDamageMemorySeconds;
    }

    /// <summary>
    /// Сказать причину молчания. Прежний свой счётчик «не чаще раза в полминуты»
    /// убран: тем же самым занимается <see cref="cannotEat"/>, и держать две
    /// заглушки на одну беду значит однажды рассинхронизировать их.
    /// </summary>
    private void Explain(string why) => НеПоел("—", why);

    /// <summary>Сколько такой еды сейчас в сумках — по ней и считаем, съели ли.</summary>
    private int CountOf(string code) =>
        self.CarrySlots().Where(s => s.Content.Code == code).Sum(s => s.Content.Count);

    /// <summary>
    /// ПОРА ЕСТЬ — вопрос без последствий, который можно задать снаружи.
    ///
    /// Нужен сну: пока бот лежит, тик еды не зовётся ВООБЩЕ (сон весит больше
    /// и держит ход за собой), а сон в игре гонит время и опустошает сытость.
    /// Живьём это и значило «терять здоровье с хлебом в кармане, пока не
    /// ударит»: подниматься бот умел только по опасности, а голод в список
    /// причин подъёма не входил.
    ///
    /// Своего состояния не трогает: <see cref="NoticeHungerDamage"/> с его
    /// памятью об ударах остаётся делом самого тика.
    /// </summary>
    public bool NeedsFood
    {
        get
        {
            if (self.Saturation is { } sat && self.MaxSaturation is { } max && max > 0)
                return sat / max < HungerThreshold ||
                       (DateTime.UtcNow - hungerHurtAt).TotalSeconds < HungerDamageMemorySeconds;
            // Сытость неизвестна — верим последнему голодному удару: число из
            // атрибутов может и запоздать, и не прийти вовсе, а урон не врёт
            return (DateTime.UtcNow - hungerHurtAt).TotalSeconds < HungerDamageMemorySeconds;
        }
    }

    /// <summary>Есть ли чем поесть прямо сейчас (в сумках, а не «где-то в мире»).</summary>
    public bool HasFood =>
        self.FindBestItem(slot => slot.Code is { } code ? world.GetSatietyByCode(code) : 0) != null;

    public BehaviorEatWhenHungry(SelfState self, WorldModel world, Hands hands)
    {
        this.self = self;
        this.world = world;
        this.hands = hands;
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // ГОЛОД ПО ФАКТУ. Игра бьёт уроном только когда сытость на нуле —
        // значит падающее здоровье это правда о голоде, а число в атрибутах
        // может и запоздать, и не прийти вовсе. Живьём бот умирал от голода,
        // молча выходя на первой же проверке: «сытость мне неизвестна».
        bool starving = NoticeHungerDamage();

        if (self.Saturation is not { } sat || self.MaxSaturation is not { } max || max <= 0)
        {
            if (!starving)
            {
                Explain("сытость неизвестна — сервер не прислал дерево hunger");
                return false;
            }
            sat = 0;
            max = 1;   // урон от голода не врёт: считаем себя пустым
        }
        else if (sat / max >= HungerThreshold && !starving)
        {
            // СЫТ — отбой обеим тревогам: следующая беда должна прозвучать
            // как первая, а не утонуть в «об этом уже говорил»
            noFood.Clear();
            cannotEat.Clear();
            return false;
        }
        else if (starving && sat / max >= HungerThreshold)
        {
            Explain($"сервер морит голодом, а по моим данным сытость {sat:0}/{max:0} — " +
                    "верю урону, а не числу");
        }
        if ((DateTime.UtcNow - lastAttempt).TotalSeconds < CooldownSeconds)
            return false;

        // Самая питательная еда во всём инвентаре (хотбар + рюкзаки).
        // Есть её бот будет ИЗ РУКИ: сначала предмет переезжает в хотбар.
        // Применять прямо из рюкзака нельзя — настоящий клиент так не умеет
        var food = self.FindBestItem(slot => slot.Code is { } code ? world.GetSatietyByCode(code) : 0);
        if (food == null)
        {
            // В сумке пусто — но еда может лежать под ногами. Поднять её
            // ГОРАЗДО дешевле, чем идти собирать дикую
            var (got, why) = await TryPickUpNearbyAsync(ct);
            if (got)
            {
                lastAttempt = DateTime.MinValue;   // подняли — едим сразу же
                noFood.Clear();
                return true;
            }
            // ГРОМКО ОДИН РАЗ И НАПОМИНАТЬ. Прежний флаг «уже жаловался»
            // замолкал НАВСЕГДА до первой сытости: бот, сказавший «еды нет» и
            // с тех пор молчавший час, выглядел здоровым.
            //
            // И НАЗЫВАТЬ ПОМЕХУ. «Ни в сумках, ни под ногами» — ложь, когда еда
            // под ногами есть, но занесена в чёрный список недостижимых: живьём
            // бот стоял в двух шагах от мяса и докладывал, что еды нет вовсе
            if (noFood.Raise(why ?? "ни в сумках, ни под ногами"))
                OnNoFood?.Invoke();
            return false;
        }
        noFood.Clear();

        // Сколько было ДО: успех считаем по факту, а не по тому, что пакет ушёл.
        // Живьём бот бодро писал «поел» и умирал от голода с полными сумками
        string code = food.Content.Code!;
        int had = CountOf(code);

        lastAttempt = DateTime.UtcNow;

        // ТЕЛО РАДИ ЖИЗНИ — ГЛАВНАЯ СТРОКА ЭТОЙ СПОСОБНОСТИ.
        //
        // Раньше еда НЕ ПРОСИЛА ТЕЛА ВООБЩЕ: путь доходил прямо до
        // hands.UseItemAsync, а тело в это время держала «команда карьер».
        // Живьём это выглядело так: «Потеряно 0,13 хп от hunger» каждые шесть
        // секунд, хлеб в кармане — и бот умер. Ступень «жизнь» перебивает и
        // команду, и бой, и укрытие в бурю; прерванная работа помечается
        // Preempted, то есть карьер вернётся и доделает.
        //
        // Отказать этому может только другое дело жизни (лечение), и такой
        // отказ звучит вслух — BodyArbiter.OnVitalRefused не глушится никогда
        using var hold = ctx?.Turn.TakeForLife($"поесть {code}", ct);
        if (ctx != null && hold == null)
        {
            lastAttempt = DateTime.UtcNow.AddSeconds(-Math.Max(0, CooldownSeconds - RetryAfterFailSeconds));
            НеПоел(code, "тела не дали — им занято другое дело жизни");
            return false;
        }
        // Токен владения гаснет, когда дело перебили: жевать после этого
        // значит врать о том, где бот и чем занят
        var token = hold?.Token ?? ct;

        bool applied = await hands.UseItemAsync(s => s.Code == code, 2.5, token);
        if (!applied)
        {
            // Провал не должен стоить полной паузы: голодному надо пробовать
            // снова, а не ждать двенадцать секунд из-за чужой руки
            lastAttempt = DateTime.UtcNow.AddSeconds(-Math.Max(0, CooldownSeconds - RetryAfterFailSeconds));
            НеПоел(code, "рука не освободилась — применение не засчитано");
            return false;
        }

        // Сервер списывает еду не мгновенно — даём ему мгновение
        var wait = DateTime.UtcNow.AddSeconds(1.2);
        while (DateTime.UtcNow < wait && CountOf(code) >= had && !token.IsCancellationRequested)
            await Task.Delay(100, token).ContinueWith(_ => { });

        if (CountOf(code) >= had)
        {
            lastAttempt = DateTime.UtcNow.AddSeconds(-Math.Max(0, CooldownSeconds - RetryAfterFailSeconds));
            НеПоел(code, "еда не убыла — сервер не засчитал");
            return false;
        }

        OnAte?.Invoke(code, food.Slot);
        cannotEat.Clear($"поел {code}");
        return true; // ели — этот тик наш
    }

    /// <summary>
    /// Подойти к лежащей еде и поднять её. Предметы в игре подбираются
    /// НОГАМИ — просто пройдя сверху, поэтому бот именно идёт к ним.
    ///
    /// ЦЕЛЬ ВЫБИРАЕТ ТОТ ЖЕ, КТО ХОДИТ. Раньше здесь был свой перебор сущностей:
    /// ближайшая по горизонтали, без памяти о неудачах и без предела по высоте.
    /// А <see cref="Pickup.TakeAsync"/> эту память ЧИТАЕТ и мгновенно молча
    /// отказывает — и живьём это выглядело так: недостижимая кучка тремя
    /// блоками ниже оставалась «ближайшей» и после занесения в чёрный список,
    /// бот двенадцать секунд подряд выбирал её же, получал молчаливое «нет»
    /// и докладывал «еды нет: ни в сумках, ни под ногами», стоя в двух шагах
    /// от куска мяса, к которому теперь не шёл вовсе. Спрашиваем
    /// <see cref="Pickup.Around(double?, Func{string, bool}, bool)"/>: он и
    /// чёрный список знает, и высоту меряет.
    /// </summary>
    /// <returns>
    /// Подняли ли еду, и — если нет — что мешает. Причину обязан узнать
    /// заказчик: «еды нет» при лежащей рядом еде это неправда.
    /// </returns>
    private async Task<(bool Got, string? Why)> TryPickUpNearbyAsync(CancellationToken ct)
    {
        if (!PickUpFoodNearby || ctx is not { } c || self.Position is not { } me)
            return (false, null);

        bool Едят(string code) => world.GetSatietyByCode(code) > 0;

        var lying = c.Pickup.Around(PickUpRadius, Едят);
        if (lying.Count == 0)
        {
            // Пусто ли под ногами на самом деле — или мы сами закрыли эту еду?
            var closed = c.Pickup.Around(PickUpRadius, Едят, includeGivenUp: true);
            return (false, closed.Count == 0
                ? null
                : $"в сумках нет, а лежащий рядом {closed[0].Code} недостижим — " +
                  $"туда я уже ходил и не дошёл, подожду до {c.Pickup.RetryFailedSeconds:0} с");
        }

        var drop = lying[0];
        double away = Math.Sqrt((drop.X - me.X) * (drop.X - me.X) + (drop.Z - me.Z) * (drop.Z - me.Z));
        OnGoingForDrop?.Invoke(away);
        lastAttempt = DateTime.UtcNow;

        // Ходьба и ожидание — общий подбор: он знает и про радиус сбора 1.5,
        // и про секунду неприкосновенности у брошенного игроком. Здесь
        // остаётся только политика: что считать едой
        await c.Pickup.TakeAsync(drop.Id, maxSeconds: 20, ct: ct,
            importance: BodyArbiter.Importance.Reflex, owner: "поднять еду");

        // Успех — только если еда ДЕЙСТВИТЕЛЬНО в сумке
        return (self.FindBestItem(s => s.Code is { } code ? world.GetSatietyByCode(code) : 0) != null,
            null);
    }
}
