namespace VsBotKit;

/// <summary>
/// ЧТО ИГРА ЗНАЕТ ПРО ЩИТ — целиком из реестра сервера, ни одного своего числа.
///
/// Все поля лежат в <c>attributes.shield</c> предмета, и читает их сама игра
/// ровно там же (<c>ModSystemWearableStats.applyShieldProtection</c>):
/// <c>itemAttributes["shield"]["damageAbsorption"]</c> и соседние. Поэтому
/// модовый щит найдётся сам, а список кодов («shield-crude», «shield-metal»…)
/// здесь не заведён нарочно — это ровно тот выдуманный список, на котором
/// проект горел трижды.
///
/// ЭТО НЕ ПРОЦЕНТЫ, А ШАНС СПИСАТЬ ПЛОСКОЕ ЧИСЛО, и путать нельзя:
/// <code>
///     if (rand &lt; chance) поглощено += absorption;
///     damage = max(0, damage − поглощено);
/// </code>
/// То есть щит либо срабатывает целиком, либо не срабатывает вовсе. У ванильных
/// шанс один на всех: пассивно 0,2, активно 0,9 (по снарядам — 0,2 и 1,0),
/// а вот сколько он списывает — разное: грубый 3, черностражий 5,5 в ближнем
/// и 12 по стрелам.
/// </summary>
/// <param name="Melee">Сколько хп списывает с ближнего удара, когда сработал.</param>
/// <param name="Projectile">Сколько хп списывает со стрелы или копья.</param>
/// <param name="ActiveChance">Шанс сработать с ЗАЖАТЫМ приседом (ближний бой).</param>
/// <param name="PassiveChance">Шанс сработать просто в руке (ближний бой).</param>
/// <param name="ActiveProjectileChance">Шанс сработать по снаряду с приседом.</param>
/// <param name="PassiveProjectileChance">Шанс сработать по снаряду без приседа.</param>
public sealed record ShieldInfo(
    float Melee,
    float Projectile,
    float ActiveChance,
    float PassiveChance,
    float ActiveProjectileChance,
    float PassiveProjectileChance)
{
    /// <summary>
    /// Грубая оценка «сколько хп спасёт за удар» — шанс, умноженный на
    /// списание. Нужна ровно затем, чтобы выбрать ЛУЧШИЙ щит из носимых и
    /// сказать человеку в журнале число, а не «надел щит».
    /// </summary>
    public double Value(bool raised = false) =>
        (raised ? ActiveChance : PassiveChance) * Melee;

    public override string ToString() =>
        $"−{Melee:0.#} в ближнем и −{Projectile:0.#} по стрелам, " +
        $"срабатывает {PassiveChance:P0} в руке и {ActiveChance:P0} поднятым";
}

/// <summary>
/// КОМУ ЛЕВАЯ РУКА ПРЯМО СЕЙЧАС. Ответов ровно три, и больше быть не может:
/// слот один.
/// </summary>
public enum OffHandOwner
{
    /// <summary>Никому: рука свободна, и за неё не платится 20 % голода.</summary>
    Никому,

    /// <summary>Щиту: рядом опасно.</summary>
    Щиту,

    /// <summary>Свету: темно, а драться не с кем.</summary>
    Свету
}

/// <summary>
/// ЩИТ — МЕХАНИЗМ. Когда его носить, решает роль; здесь только «как», и всё
/// «как» проверено по сборкам игры 1.22.7 (ilspycmd), а не по памяти.
///
/// ТРИ ТРЕБОВАНИЯ ЗАКАЗЧИКА, ДОСЛОВНО: «надевать щит (та же рука, где фонарь и
/// надевается только в момент угрозы как и броня)». Все три исполнены, и
/// каждое — не там, где было бы проще, а там, где эта механика уже живёт:
///
/// 1. ТА ЖЕ РУКА, ЧТО У ФОНАРЯ. Щит — не броня: в shield.json стоит
///    <c>storageFlags: 257 = General|Offhand</c>, то есть это обычный предмет
///    в левой руке, слот 11 хотбара (<see cref="Bags.OffHandSlot"/>) — ровно
///    тот, где живёт фонарь. Кладёт его туда ТОТ ЖЕ механизм, что и фонарь
///    (<see cref="HandLight.ToOffHandAsync"/>), второго хозяина у левой руки
///    в проекте нет. Спор за слот решается ОДНИМ правилом —
///    <see cref="WhoNeedsOffHand"/>, и его спрашивают обе стороны.
/// 2. ТОЛЬКО В МОМЕНТ УГРОЗЫ, КАК БРОНЯ. Спрашивается ТА ЖЕ функция, что у
///    брони, — <see cref="ArmorKit.NeedGear"/>, расширенная одним признаком, а
///    не скопированная (почему именно так — там же, в её док-строке).
/// 3. СНИМАТЬ, КОГДА УГРОЗА ПРОШЛА. Из того же одного правила: «нужен ли он
///    прямо сейчас» отвечает и на «надеть», и на «снять». Жалоба заказчика
///    «после шторма застрял — не хотел вылезать и не снимал броню» вышла
///    именно из разделённых решений, и повторять её щитом мы не станем.
///
/// ЗАЩИТА РАБОТАЕТ И БЕЗ ЕДИНОГО ЛИШНЕГО ПАКЕТА — это важно знать, чтобы не
/// изобретать «поднятие щита» там, где его нет. Анимация <c>raiseshield-left</c>
/// — ЧИСТАЯ КОСМЕТИКА клиента (<c>ItemShield.OnHeldIdle</c>); на защиту она не
/// влияет никак. Влияет одно:
/// <code>
///     text = (Controls.Sneak &amp;&amp; Attributes.GetInt("aiming") != 1) ? "active" : "passive";
/// </code>
/// то есть ЗАЖАТЫЙ ПРИСЕД поднимает шанс с 0,2 до 0,9 — вчетверо с лишним. И
/// он же назначает цену: на присяде тело идёт 0,35 обычной скорости. Это
/// решение роли, а не библиотеки (<see cref="Raised"/>).
///
/// ДВА КОНФЛИКТА, КОТОРЫЕ НАДО ЗНАТЬ И КОТОРЫЕ НАЗВАНЫ ВСЛУХ:
/// • НАТЯГ ЛУКА СБРАСЫВАЕТ ЩИТ В «passive»: та же строка игры проверяет
///   <c>aiming != 1</c>. Стрелять и держать щит поднятым одновременно игра не
///   даёт НИКОМУ, и обойти это нечем;
/// • ПРИСЕД ОПУСКАЕТ ГЛАЗА с 1,7 до 1,36, а из глаз выходит дуло лука
///   (<see cref="Archery.EyeHeight"/> спрашивает тело именно поэтому).
///
/// БОТ ДОЛЖЕН СМОТРЕТЬ НА НАПАДАЮЩЕГО. Щит ловит удар только в конусе ±60° по
/// рысканью (или ±30° по наклону для падающего сверху) — <c>1.0471976</c> и
/// <c>π/6</c> в том же методе игры. Бой это уже делает сам
/// (<c>Movement.FocusEntityId</c> плюс <c>FaceAsync</c> перед каждым ударом),
/// поэтому своей наводки здесь нет и заводить её нельзя.
/// </summary>
public sealed class Shielding
{
    private readonly BotContext ctx;
    private readonly HandLight hand;

    /// <param name="hand">
    /// МЕХАНИЗМ ЛЕВОЙ РУКИ — ОБЩИЙ, а не свой. Тот же самый носит фонарь и
    /// отвечает окну управления на «взять в левую руку». Заведи щит свой
    /// перенос — и две мерки «занята ли рука» разошлись бы в первый же вечер.
    /// </param>
    public Shielding(BotContext ctx, HandLight hand)
    {
        this.ctx = ctx;
        this.hand = hand;
    }

    /// <summary>Сказать вслух: что надел, что снял и почему.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// ЩИТ ЛИ ЭТО — вопрос к реестру сервера теми же словами, какими его
    /// задаёт сама игра: есть ли у предмета атрибут <c>shield</c>. Именно по
    /// нему <c>applyShieldProtection</c> и решает, считать ли вещь щитом, —
    /// а вовсе не по коду и не по <c>EnumTool.Shield</c> (в shield.json поля
    /// <c>tool</c> нет вовсе, и полагаться на него значило бы не найти ни
    /// одного ванильного щита).
    /// </summary>
    public ShieldInfo? Info(string? code)
    {
        var attrs = ctx.World.GetCollectible(code)?.Attributes?["shield"];
        if (attrs is not { Exists: true })
            return null;
        var chance = attrs["protectionChance"];
        return new ShieldInfo(
            // Умолчания — не наши: ровно те, что подставляет игра, когда поля
            // в ассете нет (AsFloat(2f) в applyShieldProtection)
            Melee: attrs["damageAbsorption"].AsFloat(2f),
            Projectile: attrs["projectileDamageAbsorption"].AsFloat(2f),
            ActiveChance: chance["active"].AsFloat(0f),
            PassiveChance: chance["passive"].AsFloat(0f),
            ActiveProjectileChance: chance["active-projectile"].AsFloat(0f),
            PassiveProjectileChance: chance["passive-projectile"].AsFloat(0f));
    }

    /// <summary>Щит ли это (коротко).</summary>
    public bool IsShield(string? code) => Info(code) != null;

    /// <summary>
    /// Лучший щит из носимого. null — щита нет ни в хотбаре, ни в сумках.
    ///
    /// «Лучший» меряется ПОДНЯТЫМ или нет — по тому, как бот собирается его
    /// держать: у ванильных шанс одинаков, и выбор решает списание, но модовый
    /// щит вполне может брать шансом.
    /// </summary>
    /// <remarks>
    /// ИЩЕМ ТАМ ЖЕ, ОТКУДА ПОТОМ БЕРЁМ. Перебор идёт по <see cref="SelfState.CarrySlots"/>
    /// — по сумкам и годным слотам хотбара, — а не по всем инвентарям подряд,
    /// и это не придирка: общий перебор проходит и по «ground-&lt;uid&gt;» (то,
    /// что под ногами), и по слотам 10/11, куда сервер перенос молча отвергает.
    /// Нашли бы там — а <see cref="HandLight.ToOffHandAsync"/> ищет по сумкам и
    /// честно ответил бы «в сумках не нашёлся». Две мерки на один вопрос — и
    /// отказ приходит на щит, который «есть».
    /// </remarks>
    public SelfState.FoundItem? Best(bool raised = false)
    {
        SelfState.FoundItem? best = null;
        double bestValue = 0;
        foreach (var (invId, slot, content) in ctx.Self.CarrySlots())
        {
            if (content.IsEmpty || Info(content.Code) is not { } info)
                continue;
            // Единица снизу нарочно: щит без списания (модовый, декоративный)
            // — всё равно щит, и «лучшего» из таких надо уметь выбрать
            double value = 1 + info.Value(raised);
            if (value > bestValue)
            {
                bestValue = value;
                best = new SelfState.FoundItem(invId, slot, content);
            }
        }
        return best;
    }

    /// <summary>
    /// Щит, который держит бот ПРЯМО СЕЙЧАС (в левой руке), или null.
    /// Смотрим только левую: правая у бота под инструмент и оружие, и класть
    /// туда щит значило бы отобрать у себя кирку.
    /// </summary>
    public string? InHand =>
        hand.InOffHand?.Code is { } code && IsShield(code) ? code : null;

    /// <summary>
    /// КОМУ ЛЕВАЯ РУКА — ЧИСТОЕ ПРАВИЛО, ЕДИНСТВЕННОЕ НА ВЕСЬ ПРОЕКТ.
    ///
    /// ЗАЧЕМ ОНО ВООБЩЕ. Слот один, желающих двое, и заказчик сам поставил
    /// вопрос: «в темноте с дрифтером что важнее? Реши, объясни причиной и
    /// сделай ручкой, а не молча».
    ///
    /// РЕШЕНО ТАК: ПРИ УГРОЗЕ РУКА У ЩИТА. Причина не вкусовая, а
    /// арифметическая и временнáя:
    /// • угроза длится десятки секунд, темнота — часы. Отдав руку щиту на время
    ///   стычки, бот теряет свет ненадолго; отдав её фонарю, он теряет защиту
    ///   ровно тогда, когда она нужна;
    /// • фонарь в темноте НЕ СПАСАЕТ ОТ УДАРА вовсе — он только показывает
    ///   дорогу, а дорогу бот и так знает по карте чанков (свет ему нужен не
    ///   чтобы видеть, а чтобы не спавнились твари и чтобы выглядеть живым);
    /// • щит списывает 3–12 хп с удара при полном запасе в 15. Это разница
    ///   между «отбился» и «умер», и её нечем заменить.
    ///
    /// ПОВЕРНУТЬ ЭТО МОЖНО, и ручка названа: <c>ВТемнотеФонарьВажнееЩита</c>.
    /// Кому дорог свет (бот-осветитель в штольне, где мобов не бывает) —
    /// поставит правду, и тогда в темноте рука остаётся у фонаря, а щит ждёт
    /// в сумке.
    ///
    /// ПУСТАЯ РУКА — ТОЖЕ ОТВЕТ, и не худший: за левую руку игра берёт 20 %
    /// скорости голода с ЛЮБОГО предмета без своего statModifier
    /// (<c>InventoryPlayerHotbar.OffHandHungerPenalty = 0.2f</c>). Держать там
    /// щит круглые сутки — это лишняя треть съеденного хлеба за смену.
    /// </summary>
    /// <param name="shieldNeeded">Правило угрозы сказало «щит нужен».</param>
    /// <param name="haveShield">Щит есть в сумках или уже в руке.</param>
    /// <param name="lightNeeded">Свет нужен: темно и способность света включена.</param>
    /// <param name="haveLight">Есть чем светить.</param>
    /// <param name="lightWinsInDark">Ручка роли: в темноте фонарь важнее щита.</param>
    public static (OffHandOwner Кому, string Почему) WhoNeedsOffHand(
        bool shieldNeeded, bool haveShield, bool lightNeeded, bool haveLight,
        bool lightWinsInDark)
    {
        bool shieldCan = shieldNeeded && haveShield;
        bool lightCan = lightNeeded && haveLight;

        if (shieldCan && lightCan)
            return lightWinsInDark
                ? (OffHandOwner.Свету, "темно, и по ручке «ВТемнотеФонарьВажнееЩита» " +
                                       "свет здесь дороже защиты — щит ждёт в сумке")
                : (OffHandOwner.Щиту, "рядом опасно: щит важнее фонаря, пока не станет тихо " +
                                      "(ручка «ВТемнотеФонарьВажнееЩита»)");
        if (shieldCan)
            return (OffHandOwner.Щиту, "рядом опасно");
        if (lightCan)
            return (OffHandOwner.Свету, "темно, а драться не с кем");
        // Ни то, ни другое: рука свободна, и это выигрыш, а не пустота
        return (OffHandOwner.Никому, shieldNeeded && !haveShield
            ? "щита нет в сумках — рука свободна (ручка «НоситьЩит»)"
            : "рука свободна: за занятую игра берёт 20 % скорости голода");
    }

    /// <summary>
    /// В ТЕМНОТЕ ФОНАРЬ ВАЖНЕЕ ЩИТА — ручка роли. Разбор решения и цена обеих
    /// сторон — в <see cref="WhoNeedsOffHand"/>.
    /// </summary>
    public bool LightWinsInDark { get; set; }

    /// <summary>Политика роли: когда вообще носить щит (та же шкала, что у брони).</summary>
    public WearArmor When { get; set; } = WearArmor.ПокаРядомОпасно;

    private bool threatNeedsShield;
    private bool darkNow;

    /// <summary>
    /// БОЙ ГОВОРИТ: угроза есть (или её нет). Считает это ОН и только он — он
    /// один знает про свежие удары, про тварей в радиусе ответа и про
    /// временную бурю; второй такой счёт в способности света разошёлся бы с
    /// первым молча и в самый неподходящий миг.
    /// </summary>
    public void NoteThreat(bool fighting, bool fleeing, bool dangerNear) =>
        threatNeedsShield = ArmorKit.NeedGear(When, fighting, fleeing, dangerNear,
            // Убегая, щит НЕ снимаем: скорости он не стоит, а стрелу в спину
            // ловит (разбор — в NeedGear)
            dropWhenFleeing: false);

    /// <summary>
    /// СПОСОБНОСТЬ СВЕТА ГОВОРИТ: темно (или светло). Считает это ОНА и только
    /// она — три источника, три пары порогов и вся разобранная история с
    /// «рассвело» в полдень живут там (<see cref="BehaviorCarryLight.IsDarkHere"/>).
    /// Сюда приходит готовый ответ, чтобы спор за руку решался ОДНИМ правилом,
    /// а не двумя половинками в разных файлах.
    /// </summary>
    public void NoteDark(bool dark) => darkNow = dark;

    /// <summary>
    /// ЧЬЯ СЕЙЧАС ЛЕВАЯ РУКА — один ответ на весь проект. Обе стороны спора
    /// (бой и свет) кладут сюда СВОЙ факт (<see cref="NoteThreat"/>,
    /// <see cref="NoteDark"/>) и обе читают ОТСЮДА один и тот же вывод.
    /// </summary>
    public (OffHandOwner Кому, string Почему) Owner => WhoNeedsOffHand(
        threatNeedsShield,
        InHand != null || Best() != null,
        darkNow,
        hand.BestCarried(1, offHandOnly: true) != null,
        LightWinsInDark);

    /// <summary>Щит просит левую руку прямо сейчас (короткий вид <see cref="Owner"/>).</summary>
    public bool WantsOffHand => Owner.Кому == OffHandOwner.Щиту;

    /// <summary>
    /// ДОВЕСТИ РЕШЕНИЕ ДО РУКИ: надеть щит или убрать его. Возвращает правду,
    /// если рука ПО ФАКТУ переменилась (сервер показал новое содержимое слота).
    ///
    /// «По факту» здесь не для красоты: перенос в занятый слот сервер отвергает
    /// МОЛЧА, и раньше на этом уже погорела броня — бот докладывал «надел 3
    /// предмета», не надев ни одного, и шёл на моба голым. Ждёт факта тот же
    /// механизм левой руки (<see cref="HandLight.ToOffHandAsync"/>).
    /// </summary>
    public async Task<bool> FitAsync(bool need, CancellationToken ct = default)
    {
        string? worn = InHand;

        if (!need)
        {
            if (worn == null)
                return false;   // и так не надет — снимать нечего
            if (!await hand.ClearOffHandAsync(ct))
                return false;
            OnLog?.Invoke($"убрал {worn} из левой руки: рядом тихо, а занятая рука " +
                          "стоит 20 % скорости голода");
            return true;
        }

        if (worn != null)
            return false;   // уже держим — переодевать не в чем

        // ЗАНЯТА ЧУЖИМ (фонарём) — НЕ ОТБИРАЕМ САМИ. Освобождает руку тот, кто
        // её занял: у способности света своя память «моё ли это» и своя пауза
        // от дребезга. Полезь мы туда напрямую — завелась бы вторая мерка
        // «чья рука», и фонарь с щитом принялись бы выталкивать друг друга
        if (hand.InOffHand is { Code: { } busy })
        {
            OnLog?.Invoke($"щит не надеть: левая рука занята ({busy}) — жду, пока освободят");
            return false;
        }

        if (Best() is not { } shield || shield.Content.Code is not { } code)
        {
            OnLog?.Invoke("щита нет ни в хотбаре, ни в сумках — надевать нечего " +
                          "(ручка «НоситьЩит» просит его носить)");
            return false;
        }

        if (!await hand.ToOffHandAsync(code, ct))
            return false;

        OnLog?.Invoke($"надел {code} в левую руку: {Info(code)} — но рядом опасно");
        return true;
    }

    /// <summary>
    /// ПОДНЯТЬ ЩИТ, пока владение живо: держит присед, и ровно это игра
    /// называет «active».
    ///
    /// ЧТО ЭТО ДАЁТ И ЧЕГО СТОИТ, ЧИСЛАМИ ИГРЫ. Шанс сработать растёт с 0,2 до
    /// 0,9 — то есть грубый щит спасает в среднем не 0,6 хп с удара, а 2,7 при
    /// запасе здоровья в 15. Цена: на присяде тело идёт 0,35 обычной скорости,
    /// и это значит, что отскок после удара (<c>KiteAfterHit</c>) и побег
    /// перестают работать. Поэтому поднимать щит имеет смысл РОВНО пока идёт
    /// размен ударов вплотную, и ничего дольше.
    ///
    /// ЖМЁТ ПРИСЕД ТЕЛО, А НЕ МЫ. <see cref="Body.HoldCrouch"/> — единственный
    /// такой механизм: он давит клавишу КАЖДЫЙ тик (её сбрасывает любой
    /// <c>Physics.Stop()</c>, а его зовут и подход, и расчистка) и считает
    /// вложенные владения. Своей отправки клавиши 5 здесь нет и быть не должно:
    /// в проекте уже разобрано, что присед уходит серверу из двух мест и
    /// расходится (см. <c>Actions.SetSneakAsync</c>).
    ///
    /// null — поднимать нечего (щита в руке нет) или тело ещё не готово; тогда
    /// и приседать незачем.
    /// </summary>
    public IDisposable? Raised()
    {
        if (InHand == null || ctx.Body is not { Ready: true } body)
            return null;
        return body.HoldCrouch();
    }
}
