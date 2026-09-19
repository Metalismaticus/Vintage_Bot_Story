namespace VsBotKit;

/// <summary>
/// ЧИСТЫЕ ПРАВИЛА ОХОТЫ: кого живой игрок стал бы бить ради мяса, а кого нет.
///
/// НИ ОДНОГО ВЫДУМАННОГО СПИСКА КОДОВ (правило 6 проекта, на котором проект
/// горел трижды). Всё, чем меряется дичь, — это данные, которые сервер сам
/// прислал боту в <c>WatchedAttributes</c>, то есть ровно то же, что видит
/// настоящий клиент:
///
/// <list type="bullet">
/// <item><b>с него вообще что-то берут ножом</b> — поведение
/// <c>EntityBehaviorHarvestable</c> ставит зверю «вес животного»
/// (<c>animalWeight</c>) ещё при рождении, и живому тоже. У саранчи и звонаря
/// его нет вовсе. Меряется той же меркой, что и разделка
/// (<see cref="Butchering.CanBeButchered"/>) — второй мерки в проекте нет;</item>
/// <item><b>у него есть хозяин</b> — дерево <c>ownedby</c> с полем <c>uid</c>.
/// Его ставит <c>ModSystemEntityOwnership</c> и читает
/// <c>EntityBehaviorOwnable.IsOwner</c>; так игра узнаёт осёдланную лошадь и
/// прирученную собаку;</item>
/// <item><b>игра сама зовёт его дичью</b> — теги типа сущности из ассетов
/// игры, которые сервер шлёт клиенту пакетом реестра (<see cref="EntityTypes"/>).
/// У всякого зверя стоит <c>animal</c>, у добываемого — <c>huntable</c>;
/// у прирученного лося <c>tamed</c>/<c>semitamed</c>; у волка, медведя и
/// гиены — <c>predator</c> и <c>ferocious</c>. Сверено с
/// assets/survival/entities/ 1.22.7 поштучно;</item>
/// <item><b>оборона считает его врагом</b> — тот же <c>combat.IsHostile</c>,
/// которым бот решает, драться ли. Хищник — дело САМООБОРОНЫ, а не охоты, и
/// второй мерки враждебности здесь нет.</item>
/// </list>
///
/// ПОКОЛЕНИЕ («generation») ОТСЮДА УБРАНО, И ЭТО ПОПРАВКА ПО ПРАВИЛУ 6.
/// Стояло правило «поколение больше нуля — значит чей-то скот». Разбор
/// <c>VSEssentials.dll</c> 1.22.7 говорит обратное:
/// <code>
///   EntityBehaviorMultiply.GiveBirth:
///     ((TreeAttribute)val2.WatchedAttributes).SetInt("generation", num + 1);
/// </code>
/// — ни слова про неволю, загон, хозяина или гнездо: ЛЮБЫЕ роды в мире дают
/// детёнышу поколение родителя плюс один. А поведение <c>multiply</c> стоит и
/// у чистой дичи: <c>assets/survival/entities/animal/mammal/hare-adult.json</c>
/// заводит его прямо в описании зайца. Значит в мире, прожившем неделю, дикий
/// зайчонок имеет поколение 1 — и бот отказывался бы от него со словами «он
/// выращен в неволе». Свои ворота игра ставит совсем иначе и ПОШТУЧНО:
/// <c>pettable minGeneration: 1</c> у овцы, <c>ropetieable minGeneration: 2</c>
/// у зайца — а этих чисел сервер клиенту не шлёт вовсе.
///
/// ЧЕСТНО ПРО ГРАНИЦУ. Курица, которую сосед поймал дикой и держит в загоне, у
/// игры остаётся без хозяина и без тега <c>tamed</c> — от дикой она отличается
/// только забором. Забор боту не виден; видна ЗАЯВКА НА ЗЕМЛЮ, и по ней мы
/// такую курицу и не трогаем — по заявке, а не по забору. Незаявленный чужой
/// загон бот от леса не отличит — и это сказано вслух, а не замолчано.
/// </summary>
public static class HuntRules
{
    /// <summary>
    /// КАК У ЧЕЛОВЕКА ЗОВЁТСЯ «НЕ ОТХОДИ ЗА ОХОТОЙ ДАЛЬШЕ ЭТОГО».
    ///
    /// Имя лежит константой, а не буквами в тексте отказа, по одной причине:
    /// строку, написанную буквами, никто не сверяет с окном. Ровно так в этом
    /// же файле четырежды выживала ложь «ручка „ПорогОружия“» — ручки с таким
    /// именем не было НИ У ОДНОЙ роли. Константу сверяет с живым списком ручек
    /// стенд ИмяРучкиВОтказеНастоящееTests.
    /// </summary>
    public const string РучкаОхотитьсяНеДальше = "ОхотитьсяНеДальше";

    /// <summary>Как у человека зовётся «сколько секунд держать тело за охоту».</summary>
    public const string РучкаСколькоДержатьТелоЗаОхоту = "СколькоДержатьТелоЗаОхоту";

    /// <summary>
    /// ПОЧЕМУ ЭТО НЕ ДИЧЬ; null — дичь, можно охотиться.
    ///
    /// Ответ строкой, а не «да/нет», нарочно: голодный бот, который никого не
    /// бьёт, обязан сказать почему — иначе человек читает это как лень
    /// (правило 4).
    /// </summary>
    /// <param name="здоровьеЗнаю">
    /// Сервер ПРИСЛАЛ здоровье этой твари. Отдельным вопросом нарочно: «здоровья
    /// не прислали» и «мёртв» — РАЗНЫЕ вещи, и путать их дорого. Живой стенд
    /// 26.08 поймал это первым же заходом: бот перечислил соседей и про каждого
    /// сказал «он уже мёртв — это разделка, а не охота», хотя курица и волчонок
    /// были живёхоньки, — просто дерево health от сервера ещё не доехало. Ровно
    /// тем же местом бой обжигался дважды (см. <see cref="CombatTargets.CanBeFought"/>:
    /// «молчаливого „наверное, живой“ тут быть не должно»).
    /// </param>
    /// <param name="живой">Присланное здоровье выше нуля (мёртвый — это разделка, а не охота).</param>
    /// <param name="игрок">Это игрок.</param>
    /// <param name="вещь">Это лежащая на земле вещь.</param>
    /// <param name="неживое">Сервер объявил тип неживым (стрела, лодка, чучело).</param>
    /// <param name="разделывают">Есть «вес животного» — с туши будет что взять.</param>
    /// <param name="ручной">Есть хозяин (<c>ownedby</c>).</param>
    /// <param name="дичьПоИгре">
    /// Игра сама зовёт этот тип дичью: теги <c>animal</c> и <c>huntable</c>.
    /// Без них — не зверь для охоты: под <c>huntable</c> у игры ходит и
    /// СЕЛЯНИН (<c>villager</c>: «humanoid, villager, human, huntable»), и его
    /// спасает <c>animal</c>, которого у него нет.
    /// </param>
    /// <param name="прирученПоИгре">
    /// ИГРА ЗОВЁТ ЧЬИМ-ТО САМ ТИП: тег <c>tamed</c> или <c>semitamed</c> (так
    /// игра метит прирученного лося) ЛИБО поведение <c>commandable</c>. Это её
    /// собственные слова, а не наша догадка про поколение.
    ///
    /// Считается ОДНИМ местом на весь проект —
    /// <see cref="HostileRules.SomebodysByType"/>, тем же, которым меряет
    /// самооборона. До 27.08 охота собирала этот вопрос руками и про
    /// <c>commandable</c> не знала вовсе: мод, давший его дичи, получал бота,
    /// который на охоте убьёт чужого питомца, а в самообороне того же зверя
    /// не тронет.
    ///
    /// Отдельно от <c>ручной</c> нарочно: у того хозяин есть у ЭТОЙ особи, и
    /// отказ про него звучит своими словами.
    /// </param>
    /// <param name="опасный">
    /// Оборона считает его врагом (<c>combat.IsHostile</c>). Волк, медведь и
    /// гиена — законная дичь по всем прочим меркам, но охота на них НЕ МОЖЕТ
    /// состояться: подойдя на длину руки, бот первым же витком бросает заход
    /// со словами «рядом враг, тело нужно бою», и так по кругу. Хуже того,
    /// берётся ближний зверь — и один волк в тридцати блоках запирал охоту
    /// целиком, при олене в сорока шагах.
    /// </param>
    /// <param name="наЧужойЗемле">Зверь стоит на ЗАЯВКЕ игрока (приват), а не «где ходил игрок».</param>
    /// <param name="виден">
    /// Луч до него проходит. ГЛАВНОЕ ПРАВИЛО ЧЕСТНОСТИ (пункт 5 закона): бот не
    /// бьёт того, кого живой игрок бы не увидел, и не стреляет сквозь стены.
    /// </param>
    /// <param name="далеко">Дальше, чем роль отпустила от якоря.</param>
    public static string? WhyNotPrey(bool здоровьеЗнаю, bool живой, bool игрок, bool вещь,
        bool неживое, bool разделывают, bool ручной, bool дичьПоИгре, bool прирученПоИгре,
        bool опасный, bool наЧужойЗемле, bool виден, bool далеко)
    {
        if (игрок)
            return "это игрок";
        if (вещь || неживое)
            return "это не зверь";
        if (!здоровьеЗнаю)
            return "здоровья его сервер мне не прислал — жив он или нет, я не знаю, " +
                   "а бить вслепую не буду";
        if (!живой)
            return "он уже мёртв — это разделка, а не охота";
        if (!разделывают)
            return "с него ножом ничего не взять: сервер не дал ему «вес животного» " +
                   "(значит, поведения harvestable у него нет)";
        if (!дичьПоИгре)
            return "игра не зовёт его дичью: у типа нет тегов «animal» и «huntable» " +
                   "(так же отсеивается и селянин — он huntable, но не animal)";
        if (ручной)
            return "у него есть хозяин — это чужая скотина, а не дичь";
        if (прирученПоИгре)
            return "игра зовёт его чьим-то: у типа стоит «tamed»/«semitamed» или " +
                   "поведение «commandable» — это чужой зверь или чужая машина, а не дичь";
        if (опасный)
            return "это хищник — им занимается самооборона, а не охота: подойдя вплотную, " +
                   "я всё равно брошу заход и отдам тело бою";
        if (наЧужойЗемле)
            return "он стоит на чужой заявке — чужой двор я не трогаю";
        if (!виден)
            return "я его не вижу: между нами блоки, а бить сквозь стену живой игрок не может";
        if (далеко)
            return $"он дальше, чем роль отпустила от места (ручка «{РучкаОхотитьсяНеДальше}»)";
        return null;
    }
}

/// <summary>ЧЕМ КОНЧИЛАСЬ ОХОТА — числами и причиной, как у обхода туш.</summary>
/// <param name="Killed">Сколько зверей завалено.</param>
/// <param name="Butchered">Сколько туш разделано.</param>
/// <param name="Looted">Сколько стопок вынуто из туш.</param>
/// <param name="Why">Что вышло или на чём упёрлись.</param>
public readonly record struct HuntReport(int Killed, int Butchered, int Looted, string Why)
{
    /// <summary>Хоть что-то сделано — значит дело сдвинулось.</summary>
    public bool Any => Killed > 0 || Butchered > 0 || Looted > 0;

    public override string ToString() => Any
        ? $"добыл зверей: {Killed}, разделал туш: {Butchered}, забрал стопок: {Looted}" +
          (Why.Length > 0 ? $" ({Why})" : "")
        : Why.Length > 0 ? Why : "охотиться не на кого";
}

/// <summary>
/// ОХОТА РАДИ ЕДЫ: найти дикого зверя, свалить, разделать.
///
/// ЭТО ДРУГОЕ ДЕЛО, ЧЕМ САМООБОРОНА, И ПОТОМУ ОНО ЗДЕСЬ. У обороны
/// (<see cref="BehaviorAttackHostiles"/>) решается «драться или бежать»: там
/// есть отход, закладывание прохода, месть, броня и счёт «вытяну ли размен» —
/// и там же прямо записано «дальше не пойдём: мы обороняемся, а не охотимся»
/// (<c>RateFight</c>: «это уже охота, а не оборона»). У охоты обратная задача:
/// зверь не нападает, он УБЕГАЕТ, бросать дело не от кого, а сдаться надо по
/// часам, а не по здоровью.
///
/// ЧТО ЗДЕСЬ НЕ ПЕРЕПИСАНО ЗАНОВО, А СПРОШЕНО У ХОЗЯИНА (правило 2):
/// • «что считать оружием» — <see cref="BotContext.Weapons"/>, та же мерка, что
///   у обороны и у сбора оружия к ночи;
/// • «между нами стена» — <see cref="Sight.LookAtCreature"/>, тот же луч, каким
///   игра решает видимость, и та же проверка ПЕРЕД КАЖДЫМ УДАРОМ;
/// • «повержен» — <see cref="CombatTargets.Defeated"/>;
/// • сам удар — <see cref="Actions.AttackEntityAsync"/>;
/// • погоня — <see cref="Movement.PursueAsync"/>;
/// • разделка и вынимание — <see cref="Butchering"/>, ровно та же, что зовёт
///   добыча с убитых.
/// Числа замаха и досягаемости тоже НЕ СВОИ: их ставит сборка из боя, у него
/// они и живут (см. <see cref="SwingIntervalMs"/>, <see cref="AttackReach"/>).
///
/// БРОСАЕМ ОХОТУ ОТ ОПАСНОСТИ. Тело берётся рефлексом — той же ступенью, что и
/// бой, — а равный равного не перебивает (<see cref="BodyArbiter"/>). Значит
/// длинная охота заперла бы самооборону, и от этого бережёт <see cref="Danger"/>:
/// увидели врага — заход кончился, тело свободно.
/// </summary>
public sealed class Hunting
{
    private readonly BotContext ctx;
    private readonly Hands hands;
    private readonly Butchering butchering;

    public Hunting(BotContext ctx, Hands hands, Butchering butchering)
    {
        this.ctx = ctx;
        this.hands = hands;
        this.butchering = butchering;
    }

    /// <summary>Пошёл за зверем: кто и в скольких блоках.</summary>
    public event Action<string, double>? OnStalking;

    /// <summary>Зверь повержен.</summary>
    public event Action<string>? OnKilled;

    /// <summary>Как далеко искать зверя (от якоря, если он задан). Ручка роли.</summary>
    public double Radius { get; set; } = 32;

    /// <summary>
    /// Сколько секунд отводится на ОДИН заход. Заход держит тело, поэтому
    /// длинная охота означала бы, что бот всё это время не отбивается и не
    /// лечится: лучше несколько коротких. Ручка роли.
    /// </summary>
    public double SecondsPerTrip { get; set; } = 25;

    /// <summary>
    /// С какого расстояния бот достаёт оружием. ЧИСЛО НЕ СВОЁ: его ставит сборка
    /// из боя (<see cref="BehaviorAttackHostiles.AttackReach"/>) — хозяин у
    /// длины руки один.
    /// </summary>
    public double AttackReach { get; set; } = 2.3;

    /// <summary>
    /// Пауза между ударами, мс. Тоже число боя, а не своё
    /// (<see cref="BehaviorAttackHostiles.SwingIntervalMs"/>).
    /// </summary>
    public int SwingIntervalMs { get; set; } = 900;

    /// <summary>
    /// РЯДОМ ОПАСНО — спрашиваем у того, кто это решает, то есть у боя
    /// (<c>combat.IsHostile</c>). Тем же способом спрашивает сон
    /// (<c>sleep.IsDangerous</c>); своей мерки враждебности здесь нет.
    /// </summary>
    public Func<EntityInfo, bool>? Danger { get; set; }

    /// <summary>
    /// ОХОТИТЬСЯ НЕ НА КОГО — громко один раз, а не сто строк в минуту.
    /// Причина у долгой беды не меняется («вокруг ни одного дикого зверя»), и
    /// повторять её каждые полсекунды значит залить журнал.
    /// </summary>
    public Alarm НеНаКого { get; } = new("охота");

    /// <summary>
    /// ДИЧЬ РЯДОМ — ближние первыми, с причиной отказа по каждому отвергнутому.
    /// </summary>
    /// <param name="anchor">Откуда мерить радиус (null — от себя).</param>
    public (List<EntityInfo> Prey, List<string> Refused) Look(BlockPos? anchor = null)
    {
        var дичь = new List<EntityInfo>();
        var отказы = new List<string>();
        if (ctx.Self.Position is not { } me)
            return (дичь, отказы);

        double ax = anchor?.X ?? me.X, ay = anchor?.Y ?? me.Y, az = anchor?.Z ?? me.Z;
        // Ищем ВОКРУГ СЕБЯ, а предел «далеко» меряем от якоря: иначе бот
        // «уползает» за добычей, как когда-то уползал кустами на 250 блоков
        foreach (var e in ctx.Entities.Nearby(me.X, me.Y, me.Z, Radius)
                     .Where(e => e.Id != ctx.Entities.OwnEntityId)
                     .OrderBy(e => e.DistanceTo(me.X, me.Y, me.Z)))
        {
            string? нет = WhyNot(e, ax, ay, az);
            if (нет == null)
            {
                дичь.Add(e);
                continue;
            }
            // ПРО ВЕЩИ, ИГРОКОВ И СТРЕЛЫ ГОВОРИТЬ НЕЧЕГО: их вокруг десятки, и
            // тремя строками отказа они вытеснили бы единственную важную —
            // «вот эту овцу я не трогаю, потому что она чья-то»
            if (e.IsPlayer || e.IsItem || (ctx.EntityTypes?.Inanimate(e.Code) ?? false))
                continue;
            if (отказы.Count < 3)
                отказы.Add($"{e.Code}: {нет}");
        }
        return (дичь, отказы);
    }

    /// <summary>Почему этот зверь не дичь; null — дичь. Вся мерка — в <see cref="HuntRules"/>.</summary>
    public string? WhyNot(EntityInfo e, double ax, double ay, double az)
    {
        var где = new BlockPos((int)Math.Floor(e.X), (int)Math.Floor(e.Y), (int)Math.Floor(e.Z));
        // Луч спрашиваем ПОСЛЕДНИМ из дешёвых проверок незачем — он дороже всех,
        // а отсеивают куда больше первые. Поэтому сперва всё дешёвое, и только
        // если оно прошло, тратимся на трассировку
        bool дичь = Huntable(e), приручён = Чейто(e), опасен = Dangerous(e);
        bool дёшевоОтказали = e.IsPlayer || e.IsItem ||
                              (ctx.EntityTypes?.Inanimate(e.Code) ?? false) ||
                              e.Health is not { } hp || hp <= 0 ||
                              !Butchering.CanBeButchered(e) ||
                              !дичь || Owned(e) || приручён || опасен;
        var луч = дёшевоОтказали ? null : ctx.Hands?.Sight.LookAtCreature(e.X, e.Y, e.Z);

        return HuntRules.WhyNotPrey(
            // ЗДОРОВЬЕ И ЕГО ОТСУТСТВИЕ — ДВА РАЗНЫХ ОТВЕТА, и спрашиваются они
            // порознь (см. HuntRules.WhyNotPrey)
            здоровьеЗнаю: e.Health is not null,
            живой: e.Health is { } h && h > 0,
            игрок: e.IsPlayer,
            вещь: e.IsItem,
            неживое: ctx.EntityTypes?.Inanimate(e.Code) ?? false,
            разделывают: Butchering.CanBeButchered(e),
            ручной: Owned(e),
            дичьПоИгре: дичь,
            прирученПоИгре: приручён,
            опасный: опасен,
            // ЗАЯВКА, А НЕ ВСЯКАЯ ПРИМЕТА ЧУЖОГО. Раньше спрашивалось
            // Claims.Inside — а он отвечает и про «тут ходил игрок» (помнится
            // пять минут), и про чужой сундук, и про отказ сервера сломать
            // блок. Прошёл мимо живой человек — и вся дичь в его коробке на
            // пять минут переставала быть дичью, причём отказ ВРАЛ: «он стоит
            // на чужой заявке», хотя никакой заявки нет
            наЧужойЗемле: ctx.Claims?.ClaimAt(где) != null,
            // Считать было нечем (реестр блоков не пришёл) — преграды НЕ
            // ПРИДУМЫВАЕМ, ровно как в бою: врать про стену так же нельзя, как
            // бить сквозь неё
            виден: луч is null || !луч.Known || луч.Visible,
            далеко: Dist(e, ax, ay, az) > Radius);
    }

    /// <summary>
    /// У ЗВЕРЯ ЕСТЬ ХОЗЯИН. Дерево <c>ownedby</c> с полем <c>uid</c> — оттуда же
    /// его читают и сама игра (<c>EntityBehaviorOwnable.IsOwner</c>), и её ИИ
    /// (<c>AiTaskComeToOwner</c>).
    /// </summary>
    public static bool Owned(EntityInfo e) =>
        e.WatchedAttributes.GetTreeAttribute("ownedby")?.GetString("uid") is { Length: > 0 };

    /// <summary>
    /// ИГРА САМА ЗОВЁТ ЭТОТ ТИП ДИЧЬЮ: теги <c>animal</c> И <c>huntable</c>.
    ///
    /// Оба нужны разом. Одного <c>huntable</c> мало: им же помечен СЕЛЯНИН
    /// (assets/survival/entities/humanoid/villager.json — «humanoid, villager,
    /// human, huntable»), и без <c>animal</c> голодный бот пошёл бы на него.
    ///
    /// Реестра типов ещё нет — отвечаем «не дичь»: незнание тут не разрешение.
    /// </summary>
    public bool Huntable(EntityInfo e) =>
        ctx.EntityTypes is { } types &&
        types.HasTag(e.Code, "animal") && types.HasTag(e.Code, "huntable");

    /// <summary>
    /// ИГРА ЗОВЁТ ЭТОТ ТИП ЧЬИМ-ТО. Правило одно на весь проект и живёт у
    /// самообороны (<see cref="HostileRules.SomebodysByType"/>): здесь стояла
    /// его вторая копия — сперва два слова про приручённость, выписанные
    /// заново, а после починки 27.08 всё равно ПОЛОВИНА вопроса: про
    /// <c>commandable</c> охота не знала вовсе.
    ///
    /// Цена расхождения живая и названа приёмкой: мод, давший
    /// <c>commandable</c> дичи, получал бота, который на охоте убьёт чужого
    /// питомца, а в самообороне того же зверя не тронет.
    /// </summary>
    public bool Чейто(EntityInfo e) =>
        ctx.EntityTypes is { } types && HostileRules.SomebodysByType(types.Get(e.Code));

    /// <summary>
    /// ОБОРОНА СЧИТАЕТ ЕГО ВРАГОМ. Мерка не своя: <see cref="Danger"/> — это
    /// <c>combat.IsHostile</c>, тот же, которым бот решает, драться ли.
    /// Не подключили — считаем «не враг», как было.
    /// </summary>
    public bool Dangerous(EntityInfo e) => Danger is { } опасно && опасно(e);

    private static double Dist(EntityInfo e, double x, double y, double z) => e.DistanceTo(x, y, z);

    /// <summary>
    /// ОДИН ЗАХОД: найти дичь, подойти, свалить, разделать и забрать.
    ///
    /// УСПЕХ — ТОЛЬКО ПО ФАКТУ ОТ СЕРВЕРА: «повержен» читается из здоровья,
    /// «разделал» — из флага <c>harvested</c>, «забрал» — из описи туши. Ни
    /// одного из этих слов бот не говорит потому, что отправил пакет.
    /// </summary>
    public async Task<HuntReport> HuntAsync(BlockPos? anchor = null, CancellationToken ct = default)
    {
        var (дичь, отказы) = Look(anchor);
        if (дичь.Count == 0)
        {
            string why = отказы.Count > 0
                ? $"дикого зверя в {Radius:0} бл не нашёл; кто рядом был: {string.Join("; ", отказы)}"
                : $"дикого зверя в {Radius:0} бл не нашёл";
            НеНаКого.Raise(why);
            return new HuntReport(0, 0, 0, why);
        }
        НеНаКого.Clear();

        // ЧЕМ БИТЬ. Мерка «оружие ли это» — общая (ctx.Weapons), и отказ
        // называет ТО, ЧЕМ ЕЁ ПРАВДА КРУТЯТ.
        //
        // ЗДЕСЬ ЧЕТЫРЕЖДЫ СТОЯЛА ЛОЖЬ: «это ручка „ПорогОружия“, она же
        // „!бой N“». Ручки с таким именем нет ни у одной нашей роли — приёмка
        // сверила с живым списком RoleKnobs.Of: 290 ручек, и этой среди них
        // НЕТ. Человек читал имя, шёл в окно и не находил, а «бот не слушается»
        // — самая дорогая из всех поломок, потому что чинят её не там.
        //
        // Ручки у порога нет НАРОЧНО, и решение это записано не здесь: команда
        // «!бой N» пишет прямо в мерку, а RoleKnobs.SayWhoKeeps честно говорит
        // человеку, что в окне этого не будет (см. DiagnosticCommands, команда
        // «бой», и стенд ChatVsRoleTests.Настройка_без_ручки_роли_всё_равно_действует).
        // Библиотека про роли не знает (закон 1) и утверждать «ручки нет ни у
        // кого» не вправе — поэтому она не поминает ручек вовсе, а называет то
        // единственное, что знает наверняка: слово, которым мерку двигают
        var (код, сила) = BestWeapon();
        if (код == null || !ctx.Weapons.IsWeapon(сила))
            return new HuntReport(0, 0, 0,
                $"охотиться нечем: годного оружия нет (лучшее, что есть, — {код ?? "ничего"} " +
                $"ценностью {сила:0.#}, а оружием считается от {ctx.Weapons.MinPower:0.#} — " +
                "эту мерку двигают словом в чат «!бой N»)");
        if (await hands.TakeToHandAsync(код, ct) is null)
            return new HuntReport(0, 0, 0, $"{код} в руку не взялся — охотиться нечем");

        var зверь = дичь[0];
        double далеко = ctx.Self.Position is { } p ? зверь.DistanceTo(p.X, p.Y, p.Z) : 0;
        OnStalking?.Invoke(зверь.Code, далеко);

        var срок = DateTime.UtcNow.AddSeconds(Math.Max(1, SecondsPerTrip));
        if (await ChaseAndStrikeAsync(зверь, срок, ct) is { Length: > 0 } беда)
            return new HuntReport(0, 0, 0, беда);

        OnKilled?.Invoke(зверь.Code);

        // РАЗДЕЛКА — ТА ЖЕ САМАЯ, что зовёт добыча с убитых. Радиус берём
        // короткий: туша лежит там, где мы стоим, а собирать по округе — дело
        // отдельной способности со своей ручкой
        var разбор = await butchering.ButcherNearbyAsync(Math.Max(4, AttackReach + 2), ct);
        return new HuntReport(1, разбор.Butchered, разбор.Looted,
            разбор.Any ? разбор.ToString() : $"свалил {зверь.Code}, но {разбор}");
    }

    /// <summary>
    /// Догнать и бить, пока не свалится или пока не выйдет срок. Возвращает
    /// причину неудачи (пусто — зверь повержен).
    /// </summary>
    private async Task<string> ChaseAndStrikeAsync(
        EntityInfo зверь, DateTime срок, CancellationToken ct)
    {
        var последнийУдар = DateTime.MinValue;
        try
        {
            ctx.Movement.FocusEntityId = зверь.Id;
            while (DateTime.UtcNow < срок && !ct.IsCancellationRequested)
            {
                if (ctx.Entities.Get(зверь.Id) is not { } живой)
                    return "зверь пропал из виду — либо ушёл из прогруженных клеток, либо его убрали";
                if (CombatTargets.Defeated(живой.Health))
                    return "";
                if (ctx.Self.Position is not { } я)
                    return "своей позиции не знаю — охотиться вслепую нельзя";

                // РЯДОМ ВРАГ — ОХОТА КОНЧИЛАСЬ. Тело нужно бою, а равный равного
                // не перебивает: держать его дальше значит запереть самооборону
                if (Danger is { } опасно &&
                    ctx.Entities.Nearby(я.X, я.Y, я.Z, AttackReach + 6).Any(e => опасно(e)))
                    return "бросаю охоту: рядом враг, тело нужно бою";

                double зазор = живой.DistanceTo(я.X, я.Y, я.Z);
                if (зазор > AttackReach)
                {
                    await ctx.Movement.PursueAsync(
                        () => ctx.Entities.Get(зверь.Id) is { } t ? (t.X, t.Z) : null,
                        stopDistance: Math.Max(0.8, AttackReach - 0.5), maxSeconds: 5, ct: ct);
                    continue;
                }

                await ctx.Movement.FaceAsync(живой.X, живой.Z, ct);
                if ((DateTime.UtcNow - последнийУдар).TotalMilliseconds >= SwingIntervalMs)
                {
                    // ПЕРЕД КАЖДЫМ УДАРОМ СПРАШИВАЕМ, ЧТО МЕЖДУ НАМИ. Сервер
                    // засчитает удар по одному расстоянию, а живой игрок сквозь
                    // блок не бьёт — это ровно тот чит, который запрещён пунктом
                    // 5 закона. Луч тот же, что в бою, второго механизма нет
                    var видно = ctx.Hands?.Sight.LookAtCreature(живой.X, живой.Y, живой.Z);
                    if (видно is { Known: true, Visible: false })
                        return $"между нами {видно.BlockedByCode ?? "блок"} — " +
                               "сквозь него живой игрок не бьёт, охоту прекращаю";

                    последнийУдар = DateTime.UtcNow;
                    await ctx.Actions.AttackEntityAsync(живой.Id, ct);
                }
                await Task.Delay(120, ct).ContinueWith(_ => { });
            }
        }
        catch (OperationCanceledException)
        {
            return "охоту прервали — тело понадобилось другому делу";
        }
        finally
        {
            ctx.Movement.FocusEntityId = null;
        }
        return ct.IsCancellationRequested
            ? "охоту прервали — тело понадобилось другому делу"
            : $"не успел свалить за отпущенные {SecondsPerTrip:0} с " +
              $"(ручка «{HuntRules.РучкаСколькоДержатьТелоЗаОхоту}»)";
    }

    /// <summary>
    /// Лучшее оружие в сумках. Обход ОДИН на весь проект и живёт у мерки
    /// оружия (<see cref="WeaponStandard.BestInBags"/>): здесь он был списан
    /// у сбора оружия к ночи слово в слово, вместе с пропуском «character» и
    /// «mouse», — и это была третья такая копия в проекте.
    /// </summary>
    private (string? Code, float Power) BestWeapon() =>
        ctx.Weapons.BestInBags(ctx.World, ctx.Self.OwnInventories);
}
