namespace VsBotKit;

/// <summary>
/// Лечение при ранении: когда здоровья мало, бот находит в инвентаре бинт
/// (любой предмет с лечебным поведением — работает и с модовыми) и применяет.
///
/// Лечилки в игре действуют ПОСТЕПЕННО: применение занимает пару секунд,
/// а здоровье прибывает следующие ~10 секунд. Поэтому после применения
/// бот ждёт результата и не хватается за второй бинт зря.
///
/// Применять нельзя в прыжке/падении и (у некоторых бинтов) в воде —
/// свойства читаются из реестра, бот их соблюдает.
/// </summary>
public class BehaviorHealWhenHurt : BotBehavior
{
    private readonly BotContext ctx;

    /// <summary>Доля здоровья, ниже которой пора НАЧАТЬ лечиться.</summary>
    public double HealthThreshold { get; set; } = 0.5;

    /// <summary>
    /// Начав лечиться, бинтоваться до ПОЛНОГО здоровья (сколько бинтов
    /// потребуется — столько и уйдёт, по одному за раз с ожиданием эффекта).
    /// </summary>
    public bool HealToFull { get; set; } = true;

    private bool healingCourse; // курс лечения начат — идём до полного

    /// <summary>Не лечиться, пока рядом враг (сначала отбиться).</summary>
    public bool WaitForSafety { get; set; } = true;

    /// <summary>Радиус проверки безопасности.</summary>
    public double DangerRadius { get; set; } = 8;

    /// <summary>Кого считать опасным (по умолчанию — список из способности боя).</summary>
    public Func<EntityInfo, bool>? IsHostile { get; set; }

    /// <summary>
    /// Применил лечилку И СЕРВЕР ЭТО ЗАСЧИТАЛ: бинт списан из сумки.
    /// Второе число — сколько хп обещает РЕЕСТР, а не сколько уже прибыло:
    /// здоровье в игре подтягивается ещё секунд десять после применения.
    /// </summary>
    public event Action<string, float>? OnHealed;

    /// <summary>Ранен, но лечиться нечем.</summary>
    public event Action? OnNoHealingItem;

    /// <summary>
    /// СХОДИТЬ ЗА ЛЕЧЕБНЫМ, КОГДА ЕГО НЕТ ПРИ СЕБЕ — по общей цепочке
    /// снабжения: сумки → склад этой работы → дом → крафт → добыть рядом.
    ///
    /// СЛОВА ЗАКАЗЧИКА: «За всем для жизни что ему нужно хилки и тд он должен
    /// решать здесь может добыть или надо домой за ними (если они там есть)».
    /// До этой правки бот отвечал одной строкой «ранен, а лечиться нечем» и
    /// оставался стоять — при том что бинты могли лежать в сундуке склада.
    ///
    /// Своего похода тут не написано ни строчки: решает и ходит
    /// <see cref="Supply.ForVitalAsync"/>, один на лечение, еду и тепло.
    /// Выключать стоит там, где отходить нельзя вовсе.
    /// </summary>
    public bool Restock { get; set; } = true;

    /// <summary>
    /// Пауза между походами за лечебным. Рана сама не проходит, и без паузы
    /// раненый бот пробовал бы сходить домой каждый тик — то есть не делал бы
    /// ничего другого вовсе.
    /// </summary>
    public TimeSpan RestockCooldown { get; set; } = TimeSpan.FromMinutes(2);

    private DateTime nextRestock = DateTime.MinValue;

    /// <summary>Чем кончился поход за лечебным — словами цепочки снабжения.</summary>
    public event Action<string>? OnRestocked;

    /// <summary>Полечиться не вышло: чем и почему. Молчать об этом нельзя.</summary>
    public event Action<string, string>? OnHealFailed;

    /// <summary>
    /// ПОЛЕЧИТЬСЯ НЕ ВЫХОДИТ — громко один раз, а не молчание и не сто строк в
    /// минуту. Ровно эта дыра стоила заказчику вечера в еде рядом: сервер
    /// молча не засчитывал применение, бот бодро писал «использовал poultice
    /// (+4 хп)» каждые двенадцать секунд, здоровье не росло, и в журнале это
    /// выглядело как «лечится и всё равно умирает».
    /// </summary>
    private readonly Alarm cannotHeal = new("полечиться не выходит");

    private DateTime healingUntil = DateTime.MinValue;
    private bool warnedNoItem;

    public BehaviorHealWhenHurt(BotContext ctx) => this.ctx = ctx;

    /// <summary>Сказать, что полечиться не вышло, — но только если это новость.</summary>
    private void НеПолечился(string code, string why)
    {
        if (cannotHeal.Raise($"{code}: {why}"))
            OnHealFailed?.Invoke(code, why);
    }

    /// <summary>Сколько такого сейчас в сумках — по нему и считаем, списал ли сервер бинт.</summary>
    private int CountOf(string code) =>
        ctx.Self.CarrySlots().Where(s => s.Content.Code == code).Sum(s => s.Content.Count);

    /// <summary>
    /// ПОРА ЛЕЧИТЬСЯ И ЕСТЬ ЧЕМ — вопрос без последствий, который можно задать
    /// снаружи. Нужен сну: пока бот лежит, тик лечения не зовётся вовсе, а
    /// вставать по одной опасности мало — рана никуда не денется.
    /// </summary>
    public bool NeedsHealing
    {
        get
        {
            if (ctx.Self.Health is not { } hp || ctx.Self.MaxHealth is not { } max || max <= 0)
                return false;
            if (hp >= max - 0.25)
                return false;
            if (!healingCourse && hp / max >= HealthThreshold)
                return false;
            bool swimming = ctx.Movement.IsSwimming;
            return ctx.Self.FindBestItem(slot =>
            {
                if (slot.Code is not { } code)
                    return 0;
                var heal = ctx.World.GetHealingByCode(code);
                return heal == null || (heal.CancelWhileSwimming && swimming) ? 0 : 1;
            }) != null;
        }
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (ctx.Self.Health is not { } hp || ctx.Self.MaxHealth is not { } max || max <= 0)
            return false;

        // Полностью здоров — курс лечения окончен
        if (hp >= max - 0.25)
        {
            healingCourse = false;
            warnedNoItem = false;
            cannotHeal.Clear();
            return false;
        }

        // Порог — только чтобы НАЧАТЬ курс; начатый курс идёт до полного:
        // раненым ходить незачем, бинтов должно уйти сколько нужно
        if (!healingCourse && hp / max >= HealthThreshold)
        {
            warnedNoItem = false;
            return false;
        }
        if (HealToFull)
            healingCourse = true;

        // Лечилка уже действует — ждём, второй бинт не нужен
        if (DateTime.UtcNow < healingUntil)
            return false;

        if (ctx.Entities.Self is not { } self)
            return false;

        // Сначала отбиться: под ударом бинт всё равно прервётся
        if (WaitForSafety && IsHostile != null &&
            ctx.Entities.Nearby(self.X, self.Y, self.Z, DangerRadius).Any(e => IsHostile(e)))
            return false;

        bool swimming = ctx.Movement.IsSwimming;
        // Лучшая лечилка: та, что восстановит больше (но не сверх нехватки)
        double missing = max - hp;
        var found = ctx.Self.FindBestItem(slot =>
        {
            if (slot.Code is not { } code)
                return 0;
            var heal = ctx.World.GetHealingByCode(code);
            if (heal == null || (heal.CancelWhileSwimming && swimming))
                return 0;
            // Чем ближе к недостающему здоровью, тем лучше — не тратим сильный бинт на царапину
            return Math.Max(0.01, heal.Health - Math.Max(0, heal.Health - missing) * 0.5);
        });

        if (found == null)
        {
            if (!warnedNoItem)
            {
                warnedNoItem = true;
                OnNoHealingItem?.Invoke();
            }
            return await RestockAsync(ct);
        }

        var info = ctx.World.GetHealingByCode(found.Content.Code!)!;
        if (info.CancelInAir && ctx.Movement.IsMoving)
        {
            // МОЛЧАНИЕ ЗДЕСЬ НЕДОПУСТИМО. Бот, который «лечится» и всё равно
            // умирает, не сказав ни слова, выглядит сломанным: ждать надо на
            // месте, и человек вправе знать, что бот ждёт именно этого
            НеПолечился(found.Content.Code!, "этот бинт срывается на ходу — жду остановки");
            return false;
        }

        // ТЕЛО РАДИ ЖИЗНИ. Лечение, как и еда, НИ РАЗУ не просило тела: бинт
        // применялся мимо распорядителя, пока телом распоряжалась долгая
        // работа или команда из чата. Ступень «жизнь» перебивает и то и
        // другое; прерванное дело помечается Preempted и вернётся само
        // Общий токен тика передаём внутрь владения: без него «стоп» от
        // человека и выход из мира гасили бы тик, а владение продолжало жить
        using var hold = ctx.Turn.TakeForLife($"лечение {found.Content.Code}", ct);
        if (hold == null)
        {
            // Отказать могла только еда — и об этом уже сказано вслух
            // (BodyArbiter.OnVitalRefused не глушится). Ждём следующего тика
            healingUntil = DateTime.MinValue;
            return false;
        }

        // Сколько бинтов было ДО. Успех считаем по факту списания, а не по
        // тому, что удержание кнопки дошло до конца: ровно эта дыра была
        // найдена и закрыта в еде рядом (BehaviorEatWhenHungry — «еда не убыла
        // — сервер не засчитал»), а лечение осталось как было
        string code = found.Content.Code!;
        int had = CountOf(code);

        // Держим предмет чуть дольше, чем нужно на применение.
        // Лечимся ИЗ РУКИ: бинт сперва переезжает в хотбар (применять из
        // рюкзака умеет только чит, но не настоящий клиент)
        healingUntil = DateTime.UtcNow.AddSeconds(info.ApplySeconds + info.EffectSeconds + 2);
        if (!await ctx.Hands.UseItemAsync(s => s.Code == code,
                info.ApplySeconds + 0.5, hold.Token))
        {
            healingUntil = DateTime.MinValue;
            НеПолечился(code, "рука не освободилась — применение не засчитано");
            return false;
        }

        // Сервер списывает бинт не мгновенно — даём ему мгновение
        var wait = DateTime.UtcNow.AddSeconds(1.2);
        while (DateTime.UtcNow < wait && CountOf(code) >= had && !hold.Token.IsCancellationRequested)
            await Task.Delay(100, hold.Token).ContinueWith(_ => { });

        if (CountOf(code) >= had)
        {
            // Не засчитано. Ждать двенадцать секунд под очередное «использовал
            // poultice (+4 хп)» нельзя: раненый должен пробовать снова
            healingUntil = DateTime.MinValue;
            НеПолечился(code, "бинт не убыл — сервер не засчитал применение");
            return false;
        }

        cannotHeal.Clear($"применил {code}");
        OnHealed?.Invoke(code, info.Health);
        return true; // лечение занимает ход
    }

    /// <summary>
    /// СХОДИТЬ ЗА ЛЕЧЕБНЫМ. Ход забираем только на время самого похода: цепочка
    /// уводит с места, и бой, голод и буря обязаны иметь возможность его
    /// прервать.
    ///
    /// ЧТО СЧИТАТЬ ЛЕЧЕБНЫМ, РЕШАЕТ ИГРА, А НЕ МЫ: спрашивается тот же реестр
    /// (<see cref="WorldModel.GetHealingByCode"/>), по которому бот выбирает
    /// бинт из сумки. Выдуманного списка кодов тут нет, и потому механика
    /// работает и с модовыми бинтами.
    /// </summary>
    private async Task<bool> RestockAsync(CancellationToken ct)
    {
        if (!Restock || ctx.Supply is not { } снабжение || DateTime.UtcNow < nextRestock)
            return false;
        // Под ударом за бинтом не ходят — сперва отбиться. Мерка та же, что и у
        // самого лечения: спор о том, что считать опасностью, здесь заводить
        // негде
        if (WaitForSafety && IsHostile != null && ctx.Entities.Self is { } self &&
            ctx.Entities.Nearby(self.X, self.Y, self.Z, DangerRadius)
                .Any(e => IsHostile(e)))
            return false;

        nextRestock = DateTime.UtcNow + RestockCooldown;
        using var hold = ctx.Turn.TakeForLife("поход за лечебным", ct);
        if (hold == null)
            return false;

        bool swimming = ctx.Movement.IsSwimming;
        var итог = await снабжение.ForVitalAsync("лечебное",
            code => ctx.World.GetHealingByCode(code) is { } heal &&
                    !(heal.CancelWhileSwimming && swimming),
            1, hold.Token);
        OnRestocked?.Invoke(итог.Reason);
        if (итог.Got)
        {
            // Принесли — значит на следующем тике будет чем лечиться, и жаловаться
            // «лечиться нечем» заново надо будет уже по-настоящему
            warnedNoItem = false;
            nextRestock = DateTime.MinValue;
        }
        return итог.Got;
    }
}
