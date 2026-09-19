using DressType = Vintagestory.API.Common.EnumCharacterDressType;

namespace VsBotKit;

/// <summary>
/// Одёжная вещь по данным реестра сервера. Категория и тепло приходят боту
/// в JSON-атрибутах предмета (Packet_ItemType.Attributes), поэтому модовая
/// одежда работает так же, как ванильная — ничего не захардкожено.
/// </summary>
/// <param name="Slot">Слот инвентаря "character-&lt;uid&gt;" (он же EnumCharacterDressType)</param>
/// <param name="Category">Строка clothescategory из ассета ("upperbodyover", "head"...)</param>
/// <param name="MaxWarmth">
/// Атрибут warmth: сколько градусов вещь добавляет к температуре окружения.
/// Это МАКСИМУМ: игра считает warmth = min(max, condition*2*max), а condition
/// живёт в атрибутах конкретного стака и боту в списке слотов не приходит.
/// </param>
public sealed record ClothingInfo(int Slot, string Category, float MaxWarmth)
{
    /// <summary>Слоты 12/13/14 — броня; игра НЕ засчитывает её тепло (IsArmorType).</summary>
    public bool IsArmor => Slot is 12 or 13 or 14;

    public override string ToString() => $"{Category} (слот {Slot}), до +{MaxWarmth:0.#}°C";
}

/// <summary>
/// Холод: мёрзнет ли бот и что с этим делать по части одежды.
///
/// Как это устроено в игре (EntityBehaviorBodyTemperature из VSSurvivalMod,
/// а не догадки):
/// - температура тела лежит в WatchedAttributes, поддерево "bodyTemp",
///   ключ "bodytemp" (float). Нормальная — 37 (defaultBodyTemperature),
///   значение зажато в [31; 45];
/// - НО ЧЕЛОВЕКУ ЭТО ЧИСЛО ПОКАЗЫВАЮТ НЕ ТАКИМ. Экран персонажа (клавиша C,
///   строка «Температура тела») ужимает всё, что выше 37, вдесятеро, и потому
///   выше 37,8 °C живой игрок не видит НИКОГДА — см.
///   <see cref="BodyHeat.ShownTemperature"/>;
/// - там же "nearHeatSourceStrength" — насколько сильно греет ближний
///   источник тепла; пересчитывается сервером раз в ~3 секунды;
/// - в корне WatchedAttributes лежат "freezingEffectStrength"
///   = clamp((37 - bodytemp)/4 - 0.5, 0, 1) — СИЛА эффекта замерзания, и
///   "wetness" (мокрая одежда греет хуже). Эта доля не значит «на экране
///   иней»: инеем её МНОЖАТ на мороз вокруг (EntityShapeRenderer.getFrostAlpha:
///   clamp((max(0, -погода) - 5)/5, 0, 1) × доля — то есть в тепле инея нет
///   вовсе), а дрожь (анимация coldidle) включается лишь выше 0.4
///   (updateFreezingAnimState). Сама доля видна человеку не как картинка, а
///   как то, ЧТО С НИМ БУДЕТ;
/// - урон от холода: пока 37 - bodytemp > 4, копится счётчик часов, и после
///   трёх ИГРОВЫХ часов такого холода сервер бьёт по 0.2 хп каждые 10 секунд
///   (тип урона Frost) — при выключенном harshWinters не бьёт вовсе;
/// - одежда прибавляет к температуре окружения сумму warmth всех НЕбронных
///   надетых вещей (updateWearableConditions: IsArmorType отсеивает 12/13/14).
///
/// Всё, что здесь есть — механизм. Когда мёрзнуть уже нельзя и куда идти
/// греться, решает роль своими настройками.
/// </summary>
public class BodyHeat
{
    private readonly BotContext ctx;

    public BodyHeat(BotContext ctx)
    {
        this.ctx = ctx;
    }

    public event Action<string>? OnLog;

    /// <summary>Падение температуры, с которого игра начинает показывать замерзание.</summary>
    public const float FreezingStartsAtDrop = 2f;

    /// <summary>Падение температуры, с которого копятся «часы обморожения».</summary>
    public const float DamageStartsAtDrop = 4f;

    /// <summary>Сколько игровых часов такого холода до первого урона.</summary>
    public const float DamageAfterGameHours = 3f;

    /// <summary>
    /// ТОЧКА ОТСЧЁТА ИГРЫ — 37 °C, и написана она ЗДЕСЬ ОДИН РАЗ.
    ///
    /// В игре это атрибут типа сущности defaultBodyTemperature со значением по
    /// умолчанию 37 (в player.json его нет вовсе, значит берётся умолчание
    /// EntityBehaviorBodyTemperature, VSSurvivalMod 1.22.7). Вторым литералом
    /// это же число стояло в <see cref="FreezingFractionAt"/> — то есть правка
    /// нормы (мод, другая версия игры) сдвинула бы пороги и оставила формулу
    /// считать по-старому, МОЛЧА.
    /// </summary>
    public const float NormalBodyTemperature = 37f;

    /// <summary>
    /// Нормальная температура тела этого бота. По сети приходит только текущая
    /// температура, поэтому норму держим настройкой; заводское значение —
    /// <see cref="NormalBodyTemperature"/>.
    /// </summary>
    public float NormalTemperature { get; set; } = NormalBodyTemperature;

    /// <summary>
    /// НИЖЕ ЭТОГО ТЕЛО НЕ ОСТЫВАЕТ — предел самой игры, не наша догадка:
    /// EntityBehaviorBodyTemperature.updateBodyTemperature пишет
    /// <c>CurBodyTemperature = GameMath.Clamp(CurBodyTemperature + …, 31f, 45f)</c>
    /// (VSSurvivalMod 1.22.7 — разобрано ilspycmd из установки заказчика; ниже
    /// в этом же файле стоит та же версия, и расходиться им нельзя).
    ///
    /// Зачем это здесь. Окно управления рисует температуру полоской, а у
    /// полоски обязаны быть КРАЯ, и выдумывать их нельзя: нарисуй окно шкалу
    /// «от нуля до ста», и 34 °C выглядели бы третью полоски — то есть почти
    /// смертью, — тогда как на деле это лёгкий озноб. Края берутся отсюда,
    /// одни на всех, кто станет их показывать.
    /// </summary>
    public const float ColdestTemperature = 31f;

    /// <summary>
    /// ВЫШЕ ЭТОГО ТЕЛО НЕ НАГРЕВАЕТСЯ — тот же зажим игры. И это же число, а
    /// НЕ норма 37, — обычное показание у согретого игрока.
    ///
    /// Почему так, дословно по updateBodyTemperature (VSSurvivalMod 1.22.7):
    /// пока «ощущаемая» температура выше запаса терпения
    /// (bodyTemperatureResistance), tempChange выходит ПОЛОЖИТЕЛЬНЫМ, и тело
    /// каждую игровую минуту прибавляет градусы, упираясь в этот потолок.
    /// Игрок, который просто стоит в тёплый день, живёт на 45 °C; на 41 °C он
    /// появляется в мире (Initialize: NormalBodyTemperature + 4), а до 37 °C
    /// опускается только по дороге к настоящему холоду.
    ///
    /// ЭТО НЕ ГРАДУСНИК ПОД МЫШКОЙ, А ЗАПАС ТЕПЛА. Окно управления рисовало
    /// шкалу с «нормой» 37 и красило всё, что выше, цветом беды — то есть
    /// здоровый бот стоял с полной КРАСНОЙ полоской «45 °C». Заказчик так и
    /// сказал: «температура отображается неправильно у героя». Смысл числа
    /// теперь называется здесь, чтобы показывать его было нечем, кроме правды.
    /// </summary>
    public const float HottestTemperature = 45f;

    /// <summary>
    /// ВО СКОЛЬКО РАЗ ЭКРАН ИГРЫ УЖИМАЕТ ЗАПАС ТЕПЛА, прежде чем написать
    /// число человеку. Ровно десять — литерал из
    /// <see cref="ShownTemperature"/>, где и назван его источник.
    /// </summary>
    public const float ShownCompression = 10f;

    /// <summary>
    /// СТОЛЬКО ГРАДУСОВ ВИДИТ ЖИВОЙ ИГРОК — и это НЕ то число, что лежит в
    /// атрибутах сущности.
    ///
    /// Дословно из игры (VSEssentials 1.22.7, CharacterExtraDialogs —
    /// этим собирается строка «Температура тела» на экране персонажа, тот
    /// самый экран по клавише C):
    /// <code>
    /// private string getBodyTempText(ITreeAttribute tempTree)
    /// {
    ///     float num = tempTree.GetFloat("bodytemp");
    ///     if (num &gt; 37f)
    ///     {
    ///         num = 37f + (num - 37f) / 10f;
    ///     }
    ///     return $"{num:0.#}°C";
    /// }
    /// </code>
    /// Холод показывается один к одному (34,6 так и остаётся 34,6), а весь
    /// запас тепла от 37 до потолка 45 ужат в 37…37,8 °C.
    ///
    /// ЗАЧЕМ ЭТО ЗАВЕДЕНО. Заказчик 17.08 на наш доклад «тело живёт на 45»:
    /// «температура в игре другая же, 38 мне казалось максимальная». Он прав,
    /// и число у него точное: 45 сырых — это 37,8 на экране, то есть его 38.
    /// Окно управления писало сырые 45 рядом со значком «°C» — те же единицы,
    /// другое число, и человек сверял окно с игрой и видел расхождение в семь
    /// градусов. Сырое число остаётся в <see cref="Temperature"/> (по нему
    /// считает сам сервер), а всё, что читает ЧЕЛОВЕК, берётся отсюда.
    ///
    /// ПОЧЕМУ ЗДЕСЬ <see cref="NormalBodyTemperature"/>, А НЕ РУЧКА РОЛИ
    /// <see cref="NormalTemperature"/>: в самой игре в этой строке стоит
    /// ЛИТЕРАЛ 37 — экран не спрашивает у сущности её defaultBodyTemperature.
    /// Подставь сюда норму роли — и окно разошлось бы с игрой ровно там, где
    /// роль эту норму сдвинула.
    /// </summary>
    public static float ShownTemperature(float temperature) =>
        temperature > NormalBodyTemperature
            ? NormalBodyTemperature + (temperature - NormalBodyTemperature) / ShownCompression
            : temperature;

    /// <summary>
    /// ВЕРХ ШКАЛЫ, КОТОРУЮ ВИДИТ ЧЕЛОВЕК — 37,8 °C: потолок игры
    /// <see cref="HottestTemperature"/>, пропущенный через
    /// <see cref="ShownTemperature"/>. Полоске окна нужны края, и брать их
    /// нужно от ТОЙ ЖЕ шкалы, в которой она пишет число: оставь краем 45, а
    /// подписью 37,8 — и согретый бот стоял бы с полоской, залитой на треть.
    ///
    /// Нижний край отдельного двойника не завёл: ниже 37 показ совпадает с
    /// сырым числом, и <see cref="ColdestTemperature"/> годится обеим шкалам.
    /// </summary>
    public static readonly float HottestShownTemperature = ShownTemperature(HottestTemperature);

    /// <summary>
    /// ОБРАТНЫЙ ПЕРЕВОД: человек назвал градусы ТАК, КАК ИХ ПИШЕТ ИГРА, — а
    /// правилам нужны сырые, те самые, по которым считает сервер.
    ///
    /// Зачем это заведено. Порог «докуда греться» стоит в панели и в пресете, и
    /// стоять он обязан в тех же единицах, что человек читает на экране
    /// персонажа: напиши он там 45 — и настройка разошлась бы с игрой на семь
    /// градусов ровно так же, как раньше расходилось окно (см.
    /// <see cref="ShownTemperature"/>). Перевод туда и обратно живёт ОДНОЙ
    /// парой в одном месте: заведи вторую — и «показать» с «понять» разъедутся
    /// молча.
    ///
    /// Тождество точное: <c>RawTemperature(ShownTemperature(x)) == x</c> в
    /// пределах точности float — на этом стоит стенд.
    /// </summary>
    public static float RawTemperature(float shown) =>
        shown > NormalBodyTemperature
            ? NormalBodyTemperature + (shown - NormalBodyTemperature) * ShownCompression
            : shown;

    /// <summary>Ниже какого падения температуры считаем, что всё в порядке.</summary>
    public float SafeDropCelsius { get; set; } = FreezingStartsAtDrop;

    /// <summary>
    /// НИЖЕ ЭТОЙ ТЕМПЕРАТУРЫ ИГРА СЧИТАЕТ ТЕЛО МЁРЗНУЩИМ — 35 °C при норме 37.
    ///
    /// Это ровно та точка, где freezingEffectStrength игры отрывается от нуля
    /// (см. <see cref="FreezingFractionAt"/>), то есть первая граница на шкале,
    /// за которой с телом ВООБЩЕ ЧТО-ТО происходит. Собственного числа окно
    /// себе не заводит: разойдись оно с этим — полоска светила бы спокойным
    /// цветом, пока бот уже бросил дела и пошёл одеваться.
    ///
    /// ЧЕГО ЗДЕСЬ НЕ НАПИСАНО — «отсюда живой игрок видит иней и дрожь».
    /// Проверено по сборкам, а не по памяти: иней рисуется долей, УМНОЖЕННОЙ
    /// на мороз вокруг (getFrostAlpha), и в тёплую погоду его нет ни при какой
    /// температуре тела; дрожь (coldidle) включается только выше доли 0.4,
    /// то есть ближе к 33,4 °C. Написать здесь «видит иней» значило бы
    /// подпереть верное число неверной причиной — а причину читают и правят.
    /// </summary>
    public float FreezingBelow => NormalTemperature - FreezingStartsAtDrop;

    /// <summary>
    /// НИЖЕ ЭТОЙ ТЕМПЕРАТУРЫ СЕРВЕР КОПИТ ЧАСЫ ДО ОБМОРОЖЕНИЯ — 33 °C при норме 37.
    /// После <see cref="DamageAfterGameHours"/> игровых часов такого холода
    /// идёт урон Frost по 0.2 хп каждые 10 секунд.
    /// </summary>
    public float FrostbiteBelow => NormalTemperature - DamageStartsAtDrop;

    /// <summary>
    /// НАСКОЛЬКО ИГРА СЧИТАЕТ ТЕЛО ЗАМЁРЗШИМ при такой температуре, 0..1 —
    /// формула самой игры, а не наша прикидка:
    /// <c>GameMath.Clamp((NormalBodyTemperature − CurBodyTemperature) / 4f − 0.5f, 0, 1)</c>
    /// (EntityBehaviorBodyTemperature.updateBodyTemperature, VSSurvivalMod 1.22.7).
    ///
    /// Записана она здесь через <see cref="FreezingStartsAtDrop"/> (это игровые
    /// 0.5 × 4) и <see cref="DamageStartsAtDrop"/> (это игровая 4) — тождество
    /// точное, зато число игры лежит В ОДНОМ месте, а не тремя литералами.
    /// </summary>
    public static float FreezingFractionAt(float temperature,
                                           float normal = NormalBodyTemperature) =>
        Math.Clamp((normal - temperature - FreezingStartsAtDrop) / DamageStartsAtDrop, 0f, 1f);

    /// <summary>Слоты одежды в порядке «сколько тепла обычно даёт вещь».</summary>
    public static readonly DressType[] ClothingSlots =
    [
        DressType.UpperBodyOver, DressType.UpperBody, DressType.LowerBody, DressType.Foot,
        DressType.Head, DressType.Hand, DressType.Shoulder, DressType.Arm,
        DressType.Neck, DressType.Waist, DressType.Face, DressType.Emblem
    ];

    private Vintagestory.API.Datastructures.ITreeAttribute? Tree =>
        ctx.Self.Entity?.WatchedAttributes.GetTreeAttribute("bodyTemp");

    /// <summary>Сервер уже прислал поддерево температуры (до входа в мир его нет).</summary>
    public bool Ready => Temperature != null;

    /// <summary>
    /// СЫРАЯ температура тела в градусах, как её держит сервер (null — сервер
    /// ещё не прислал). По ней считают ВСЕ правила — и наши, и серверные;
    /// человеку её показывать нельзя, для этого есть
    /// <see cref="TemperatureShown"/>.
    /// </summary>
    public float? Temperature => Tree?.TryGetFloat("bodytemp");

    /// <summary>
    /// Температура тела ТАК, КАК ЕЁ ПИШЕТ ИГРА ЖИВОМУ ИГРОКУ на экране
    /// персонажа (<see cref="ShownTemperature"/>). Отсюда берут число окно
    /// управления и журнал — всё, что человек станет сверять с игрой.
    ///
    /// Пороги <see cref="FreezingBelow"/> и <see cref="FrostbiteBelow"/>
    /// пересчёта не требуют и НЕ ДОЛЖНЫ его получать: они ниже 37, а там показ
    /// совпадает с сырым числом один к одному. Пропусти их через пересчёт
    /// дважды — и метки на шкале не сдвинутся, зато правило станет неверным
    /// при первой же попытке его обобщить.
    /// </summary>
    public float? TemperatureShown =>
        Temperature is { } t ? ShownTemperature(t) : null;

    /// <summary>
    /// Сила ближнего источника тепла. У горящего кострища вплотную это 10,
    /// у тлеющего 0.25, в чистом поле 0 (BlockEntityFirepit.GetHeatStrength
    /// с поправкой на расстояние).
    /// </summary>
    public float NearHeatStrength => Tree?.GetFloat("nearHeatSourceStrength", 0f) ?? 0f;

    /// <summary>Промокшесть 0..1: мокрый мёрзнет заметно быстрее.</summary>
    public float Wetness => ctx.Self.Entity?.WatchedAttributes.GetFloat("wetness", 0f) ?? 0f;

    /// <summary>
    /// Насколько мёрзну, 0..1 — та самая сила эффекта замерзания игры
    /// (freezingEffectStrength). 0 — не мёрзну, 1 — предел переохлаждения.
    ///
    /// НОЛЬ ЗДЕСЬ БЫЛ ДОГАДКОЙ. Сервер пишет freezingEffectStrength в КОРЕНЬ
    /// наблюдаемых атрибутов, и пишет его только внутри updateBodyTemperature —
    /// а поддерево «bodyTemp» с самой температурой заводится РАНЬШЕ, ещё в
    /// Initialize. Между этими мигами бот знал температуру, но на вопрос
    /// «мёрзнешь?» отвечал твёрдое «нет», хотя ответа у него не было (правило
    /// 4). Теперь, когда числа сервера ещё нет, доля СЧИТАЕТСЯ ПО ЕГО ЖЕ
    /// ФОРМУЛЕ (<see cref="FreezingFractionAt"/>) из температуры, которая уже
    /// пришла: это не выдумка, а тот же расчёт, что делает сервер.
    /// </summary>
    public float ColdFraction =>
        ctx.Self.Entity?.WatchedAttributes.TryGetFloat("freezingEffectStrength")
        ?? (Temperature is { } t ? FreezingFractionAt(t, NormalTemperature) : 0f);

    /// <summary>На сколько градусов тело остыло ниже нормы (0, если не остыло).</summary>
    public float Deficit => Temperature is { } t ? Math.Max(0f, NormalTemperature - t) : 0f;

    /// <summary>Мёрзну: игра уже показывает эффект замерзания.</summary>
    public bool IsCold => Ready && Deficit > FreezingStartsAtDrop;

    /// <summary>Холод уже опасен: с этого момента копятся часы до обморожения.</summary>
    public bool IsDangerousCold => Ready && Deficit > DamageStartsAtDrop;

    /// <summary>Уровень безопасный. Пока сервер не прислал температуру — false (не знаем).</summary>
    public bool IsSafe => Ready && Deficit <= SafeDropCelsius;

    /// <summary>
    /// Что это за одежда по коду предмета (null — не одежда).
    /// Категорию читаем ровно так же, как игра в ItemSlotCharacter.IsDressType:
    /// сначала clothescategory, потом attachableToEntity.categoryCode.
    /// </summary>
    public ClothingInfo? ClothingOf(string? code)
    {
        if (code == null)
            return null;
        var attrs = ctx.World.GetCollectible(code)?.Attributes;
        if (attrs == null)
            return null;

        string? category = attrs["clothescategory"].AsString()
                           ?? attrs["attachableToEntity"]["categoryCode"].AsString();
        if (category == null)
            return null;
        // Сравнение имени категории с DressType — тоже как в игре, без учёта регистра
        if (!Enum.TryParse(category, ignoreCase: true, out DressType type) || type == DressType.Unknown)
            return null;

        return new ClothingInfo((int)type, category, attrs["warmth"].AsFloat(0f));
    }

    /// <summary>Что сейчас надето из одежды (броня сюда не входит — она не греет).</summary>
    public IEnumerable<(int Slot, string Code, ClothingInfo Info)> Worn()
    {
        var character = ctx.Self.GetInventory("character");
        if (character == null)
            yield break;
        foreach (DressType type in ClothingSlots)
        {
            int slot = (int)type;
            if (slot >= character.Length || character[slot].Code is not { } code)
                continue;
            // Слот совпал с категорией вещи — иначе это чужая вещь в чужом слоте
            if (ClothingOf(code) is { IsArmor: false } info && info.Slot == slot)
                yield return (slot, code, info);
        }
    }

    /// <summary>
    /// Суммарное тепло надетого — та самая прибавка к температуре окружения.
    /// ВЕРХНЯЯ оценка: игра умножает warmth на изношенность вещи, а condition
    /// хранится в атрибутах стака, которых у бота в списке слотов нет.
    /// </summary>
    public float WornWarmth => Worn().Sum(w => w.Info.MaxWarmth);

    /// <summary>Самая тёплая вещь на этот слот, лежащая в сумках (null — нет такой).</summary>
    public SelfState.FoundItem? WarmestFor(int slot) =>
        ctx.Self.FindBestItem(s =>
            s.Code is { } c && ClothingOf(c) is { } info && info.Slot == slot && !info.IsArmor
                ? info.MaxWarmth
                : 0);

    /// <summary>
    /// Одеться теплее: по каждому слоту одежды взять из сумок вещь теплее
    /// надетой. Возвращает, сколько вещей РЕАЛЬНО надето — успех считается
    /// по содержимому слота после переноса, а не по факту отправки пакета.
    /// </summary>
    public async Task<int> DressWarmestAsync(CancellationToken ct = default)
    {
        string? characterId = ctx.Self.GetInventoryId("character");
        if (characterId == null)
            return 0;

        int dressed = 0;
        foreach (DressType type in ClothingSlots)
        {
            if (ct.IsCancellationRequested)
                break;
            int slot = (int)type;
            var character = ctx.Self.GetInventory("character");
            if (character == null || slot >= character.Length)
                break;

            float wornWarmth = character[slot].Code is { } wornCode && ClothingOf(wornCode) is { } wornInfo
                ? wornInfo.MaxWarmth
                : 0f;

            var candidate = WarmestFor(slot);
            if (candidate?.Content.Code is not { } code)
                continue;
            float gain = ClothingOf(code)?.MaxWarmth ?? 0f;
            if (gain <= wornWarmth)
                continue;

            // В занятый слот вещь не влезет — сначала снимаем старую
            if (!character[slot].IsEmpty && !await UndressSlotAsync(slot, ct))
            {
                OnLog?.Invoke($"слот {slot} занят, а положить снятое некуда");
                continue;
            }

            await ctx.Actions.MoveItemAsync(candidate.InventoryId, candidate.Slot, characterId, slot, 1);
            if (await WaitSlotAsync(slot, code, ct))
            {
                dressed++;
                OnLog?.Invoke($"надел {code} (+{gain:0.#}°C вместо +{wornWarmth:0.#}°C)");
            }
            else
                OnLog?.Invoke($"сервер не принял {code} в слот {slot}");
        }
        if (dressed > 0)
            OnLog?.Invoke($"суммарное тепло одежды: до +{WornWarmth:0.#}°C");
        return dressed;
    }

    /// <summary>
    /// СНЯТЬ НАДЕТОЕ В СУМКУ — единственный такой механизм на весь проект.
    ///
    /// Его зовут трое, и все они пришли сюда нарочно, чтобы копий не заводилось:
    /// сам холод (переодеться в тёплое), <see cref="Hands.WearAsync"/> (окно
    /// управления, «Надеть» поверх занятого слота) и рейс на склад из окна
    /// (человек вправе показать на кирасу, которая сейчас на боте).
    ///
    /// КУДА КЛАСТЬ, СПРАШИВАЕМ У <see cref="SelfState.FirstFreeCarrySlot"/>, а не
    /// перебираем инвентари сами. Своё сито здесь однажды уже стоило заказчику
    /// брони: оно отсеивало лишь «character», «mouse» и «creative», а под него
    /// проходил «ground-&lt;uid&gt;» — инвентарь под ногами, через который вещи
    /// выбрасываются на землю. Место в нём пусто всегда, и снятая кираса
    /// улетала под ноги, пока бот докладывал «надел».
    ///
    /// УСПЕХ — ТОЛЬКО ПО ФАКТУ: слот одежды опустел. Пакет 8 (MoveItemstack)
    /// сервер вправе отвергнуть молча, и «пакет ушёл» успехом не считается.
    /// </summary>
    public async Task<bool> UndressSlotAsync(int slot, CancellationToken ct = default)
    {
        string? characterId = ctx.Self.GetInventoryId("character");
        if (characterId == null)
            return false;

        // Некуда положить — пусть остаётся надетой, и это честный отказ сразу,
        // а не две секунды ожидания переноса, который сервер и не примет
        if (ctx.Self.FirstFreeCarrySlot() is not { } куда)
            return false;

        await ctx.Actions.MoveItemAsync(characterId, slot, куда.InventoryId, куда.Slot, 1);
        return await WaitSlotAsync(slot, null, ct);
    }

    /// <summary>
    /// Дождаться, пока сервер подтвердит содержимое слота одежды.
    /// Без этой проверки «надел» было бы враньём: сервер вправе отказать
    /// (ItemSlotCharacter.CanHold пускает в слот только свою категорию).
    ///
    /// Мерка публичная затем, что она одна на всех, кто трогает слоты одежды:
    /// <see cref="ArmorKit.EquipBestAsync"/> раньше считал надетым то, на что
    /// просто отправил пакет, и докладывал «надел N вещей», не надев ни одной.
    /// </summary>
    public async Task<bool> WaitSlotAsync(int slot, string? expectedCode, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(150, ct).ContinueWith(_ => { });
            var character = ctx.Self.GetInventory("character");
            if (character == null || slot >= character.Length)
                continue;
            if (character[slot].Code == expectedCode)
                return true;
        }
        return false;
    }

    // Градусы здесь ВИДИМЫЕ: строку читает человек и сверяет с экраном игры
    public override string ToString() =>
        Ready
            ? $"тело {TemperatureShown:0.#}°C (мёрзну на {ColdFraction:P0}), одежда до +{WornWarmth:0.#}°C, " +
              $"тепло рядом {NearHeatStrength:0.#}, мокрый на {Wetness:P0}"
            : "температура тела ещё не пришла";
}

/// <summary>
/// Не замёрзнуть: сначала одеться потеплее, а если и это не помогает —
/// греться у огня. Пороги и радиусы задаёт роль, здесь только механика.
///
/// Порядок именно такой, потому что одежда действует всегда и бесплатно,
/// а костёр — это остановка всех дел, дрова и огниво.
/// </summary>
public class BehaviorKeepWarm : BotBehavior
{
    private readonly BotContext ctx;
    private readonly BodyHeat heat;
    private readonly Fire fire;

    private bool freezing;

    /// <summary>
    /// ГРЕНИЕ НАЧАТО: бот стоит у настоящего источника тепла и добирает запас
    /// до <see cref="WarmUpTo"/>. Пока флаг поднят, «хватит» решает ГРАДУСНИК,
    /// а не доля замерзания — см. разбор у <see cref="WarmUpTo"/>.
    /// </summary>
    private bool warming;

    /// <summary>
    /// Самая высокая температура за это грение. По ней считается терпение
    /// (<see cref="MaxWarmSeconds"/>): пока теплее становится — ждём дальше,
    /// перестало — уходим и говорим об этом.
    /// </summary>
    private float? warmestSoFar;

    private DateTime nextDress = DateTime.MinValue;
    private DateTime nextFireTry = DateTime.MinValue;
    private DateTime? warmingUntil;

    /// <summary>
    /// ПОЧЕМУ ТЕПЛА НЕТ — СКАЗАТЬ ОДИН РАЗ НА ПОВОД, А НЕ КАЖДЫЕ ДВАДЦАТЬ
    /// СЕКУНД.
    ///
    /// Три отказа ниже (<see cref="OnNoFire"/>) были честными и НЕМЫМИ: их не
    /// слушал никто, потому что подписать их как есть значило вернуть тот самый
    /// шум — <see cref="FireRetry"/> ровно двадцать секунд, и мёрзнущий бот
    /// выдал бы восемнадцать одинаковых строк за шесть минут. Молчание тут не
    /// лучше шума (правило 4): бот, который час не может согреться и не говорит
    /// об этом ни слова, выглядит здоровым.
    ///
    /// ВТОРОЙ КОПИИ ПРАВИЛА ЗДЕСЬ НЕТ. «Сказать один раз на причину и
    /// напоминать, пока беда держится» умеет <see cref="Alarm"/> — тот же, что
    /// у планировщика, у безделья и внутри безнадёги. Тревога одна, а не по
    /// одной на повод: за тик сюда попадает РОВНО ОДИН отказ, и смена повода
    /// («костра нет» → «дошёл, но не греет») — это новость, её <see
    /// cref="Alarm"/> скажет сразу.
    ///
    /// ПОВОД И СТРОКА РАЗВЕДЕНЫ НАРОЧНО. В строке стоит сила источника
    /// («тепла не чувствую (0,73)»), а она тикает: сравни тревога ГОТОВУЮ
    /// СТРОКУ — и каждое дрожание числа считалось бы новостью. Ровно на этом
    /// уже обожглась добыча с убитых, где в строке тикало расстояние до тела
    /// (<see cref="BehaviorLootKills"/>). Поэтому тревога меряет ПОВОД, а
    /// человеку уходит полная строка с числом.
    /// </summary>
    private readonly Alarm безОгня = new("тепло");

    public BehaviorKeepWarm(BotContext ctx, BodyHeat heat, Fire fire)
    {
        this.ctx = ctx;
        this.heat = heat;
        this.fire = fire;
    }

    /// <summary>Способность включена (роль может отключить, например в тёплом климате).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Доля замерзания (0..1), с которой пора одеваться.</summary>
    public double DressAtFraction { get; set; } = 0.01;

    /// <summary>Доля замерзания, с которой пора бросать дела и идти к огню.</summary>
    public double FireAtFraction { get; set; } = 0.3;

    /// <summary>
    /// Доля замерзания, ниже которой считаем, что мёрзнуть перестали.
    ///
    /// ЭТО «ПЕРЕСТАЛ МЁРЗНУТЬ», А НЕ «СОГРЕЛСЯ», и путать их дорого. Доля
    /// замерзания игры обнуляется уже на 35 °C (см.
    /// <see cref="BodyHeat.FreezingFractionAt"/>), то есть в двух градусах от
    /// нормы и в десяти от потолка. Пока по ней решали и «пора к огню», и
    /// «можно расходиться», бот у костра отходил, едва перестав мёрзнуть, — и
    /// мёрз снова через минуту. Куда греться НАЧАВ греться, решает
    /// <see cref="WarmUpTo"/>.
    /// </summary>
    public double WarmFraction { get; set; } = 0.05;

    /// <summary>
    /// ДОКУДА ГРЕТЬСЯ, РАЗ УЖ ВСТАЛ К ОГНЮ — сырые градусы тела, те самые, по
    /// которым считает сервер (человеку их показывают другими, см.
    /// <see cref="BodyHeat.ShownTemperature"/> и <see cref="BodyHeat.RawTemperature"/>).
    ///
    /// ПРЯМОЙ ЗАКАЗ 20.08, дословно: «логику нужно починить, что если герой
    /// замерз и стал греться, то греемся до макс температуры своей».
    ///
    /// ЧТО БЫЛО. Уйти от костра решала <see cref="WarmFraction"/> — доля
    /// замерзания 0.05, а это 34,8 °C. Живой журнал 19–20.08 повторяет эту
    /// строку раз за разом, одним и тем же числом:
    ///     мёрзну на 6 % (тело 34,8 °C) — одеваюсь и ищу тепло
    /// (18:33:51, 21:13:07, и снова 00:06:07 «на 8 %»). Бот доводил тело ровно
    /// до порога, разворачивался и мёрз опять: между «пора греться» (0.01…0.3)
    /// и «хватит» (0.05) лежала сотая доля, и запаса тепла он не набирал
    /// НИКОГДА.
    ///
    /// ПОЧЕМУ ПОТОЛОК ДОСТИЖИМ, а не «стоять вечно». Сервер считает так
    /// (updateBodyTemperature, VSSurvivalMod 1.22.7):
    /// <c>tempChange = nearHeatSourceStrength + …</c>, и всё, что больше 0.5,
    /// удваивается, после чего <c>тело += tempChange × прошедшие игровые
    /// часы</c> с зажимом в [31; 45]. У горящего кострища вплотную сила 10,
    /// то есть +20 °C за игровой час, — потолок берётся за доли часа. Слабый
    /// источник греет медленнее, и на этот случай терпение считается не от
    /// начала грения, а от ПОСЛЕДНЕЙ ПРИБАВКИ (см. <see cref="MaxWarmSeconds"/>):
    /// греет — ждём, перестало — уходим и говорим почему.
    ///
    /// ЗАСЛОНКА СВЕРХУ НЕ УКРАШЕНИЕ. Выше 45 тело не нагревается ни при каких
    /// условиях (тот же зажим игры), и цель 45,000004 — а именно столько даёт
    /// перевод человеческих «37,8» — не была бы достигнута НИКОГДА: бот стоял
    /// бы у костра до конца терпения и уходил с честным «теплее не становится».
    /// </summary>
    public float WarmUpTo
    {
        get;
        set => field = Math.Clamp(value, BodyHeat.ColdestTemperature, BodyHeat.HottestTemperature);
    } = BodyHeat.HottestTemperature;

    /// <summary>
    /// Сила ближнего источника тепла, при которой греться уже имеет смысл.
    /// Единица примерно соответствует «костёр горит в паре шагов».
    /// </summary>
    public float EnoughHeatStrength { get; set; } = 1.0f;

    /// <summary>
    /// НА СКОЛЬКО БЛОКОВ ПОДХОДИТЬ К КОСТРУ — И ЭТО «У КОСТРА», А НЕ «В
    /// КОСТРЕ». Разница между двумя стоит бота и 48 стопок вещей: он сложил
    /// костёр, пришёл греться и ВСТАЛ В НЕГО.
    ///
    /// ПОЧЕМУ ЭТО ВООБЩЕ БЫЛО ВОЗМОЖНО. У блока костра нет коллизии
    /// (firepit.json: <c>collisionbox: null</c>) — тело входит в него, как в
    /// воздух, а <c>BlockFirepit.OnEntityInside</c> в это время жжёт стоящего.
    ///
    /// ЧЕМ РАЗНИЦА ДЕРЖИТСЯ ТЕПЕРЬ, ТРЕМЯ РАЗНЫМИ ЗАМКАМИ:
    /// 1. Горящий костёр перестал быть клеткой, где можно стоять
    ///    (<see cref="HarmRules"/> + <see cref="WorldModel.IsStandable"/>) —
    ///    маршрут в него не строится.
    /// 2. Подход останавливается не ближе <see cref="NeverCloserToFire"/> от
    ///    ЦЕНТРА костра — зажим, который эту ручку перебивает.
    /// 3. Если бот всё же оказался в огне (костёр разожгли под ним, лава
    ///    натекла), его уводит спасение <see cref="BehaviorLeaveHarm"/>.
    /// </summary>
    /// <remarks>
    /// Ручка роли — «ПодходитьККостру»; здесь только механизм (закон 1).
    /// Пока ручки не было, это число не значило ничего: заказчик его не видел,
    /// а зажим <see cref="NeverCloserToFire"/> при заводских 1,6 не мог
    /// сработать НИ ПРИ КАКОЙ настройке — то есть был мёртвым.
    /// </remarks>
    public double WarmDistance { get; set; } = 1.6;

    /// <summary>
    /// БЛИЖЕ ЭТОГО К ЦЕНТРУ ОГНЯ НЕ ПОДХОДИМ НИ ПРИ КАКОЙ НАСТРОЙКЕ.
    ///
    /// ЧИСЛО ВЫВЕДЕНО ИЗ ТОГО, ЧЕМ ИГРА РЕШАЕТ, КОГО ЖЕЧЬ, и прежнее (1,2) было
    /// выведено не оттуда. Прежний разбор ссылался на <c>fireCuboid</c>
    /// (<c>BEBehaviorBurning</c>, коробка на ⅛ шире клетки) — но это коробка
    /// блока <c>fire</c>, а костёр бьёт совсем другим местом:
    /// <c>BlockFirepit.OnEntityInside</c>, по СВОЕЙ клетке, без всякого
    /// расширения. Арифметика в том разборе не сходилась и сама с собой
    /// («остаётся 0,2 блока запаса» — на деле 0,075).
    ///
    /// КАК СЧИТАЕТСЯ ПРАВИЛЬНО. <c>PhysicsBehaviorBase</c> зовёт
    /// <c>OnEntityInside</c> НЕ у клетки ног, а у КАЖДОЙ клетки, которую задела
    /// коробка тела: тройной цикл от <c>floor(entityBox.X1)</c> до
    /// <c>floor(entityBox.X2)</c> и так по трём осям. Значит, чтобы костёр не
    /// достал, коробка тела не должна задевать его клетку ВООБЩЕ: полширины
    /// тела (<see cref="BodySize.HalfWidth"/> = 0,3) плюс полклетки (0,5) от
    /// центра костра. Подход меряет расстояние по прямой, а худший случай —
    /// подход вдоль оси, где вся эта прямая ложится на одну ось.
    ///
    /// РУЧКОЙ НЕ ДЕЛАЕТСЯ НАРОЧНО: это не «насколько бот любит тепло» (на то
    /// есть <see cref="LivingRole.ПодходитьККостру"/>), а граница, за которой игра
    /// начинает жечь.
    /// </summary>
    public const double NeverCloserToFire = BodySize.HalfWidth + 0.5;

    /// <summary>
    /// НА СКОЛЬКО И ПРАВДА ОСТАНОВИМСЯ — ручка, поджатая границей ожога.
    ///
    /// Отдельным именем нарочно: пока зажим стоял выражением прямо в вызове
    /// подхода, проверить его было НЕЧЕМ. Приёмка 26.08 сняла <c>Math.Max</c>
    /// целиком — набор остался зелёным; удали константу вместе с абзацем
    /// разбора, и никто бы не заметил. Теперь у зажима есть имя, и стенд
    /// спрашивает его напрямую.
    /// </summary>
    public double StopDistance => Math.Max(WarmDistance, NeverCloserToFire);

    /// <summary>Как далеко искать уже готовый костёр.</summary>
    public double FireSearchRadius { get; set; } = 16;

    /// <summary>Разводить свой костёр, если чужого рядом нет.</summary>
    public bool MayBuildFire { get; set; } = true;

    /// <summary>
    /// СКОЛЬКО ТЕРПЕТЬ У ОГНЯ, ПОКА ТЕПЛЕЕ НЕ СТАНОВИТСЯ (реальные секунды).
    ///
    /// Раньше это был глухой срок «столько стоять и не больше», и с ним
    /// «греться до потолка» (<see cref="WarmUpTo"/>) стало бы ложью: слабый
    /// источник не успевает за три минуты, а сильный успевает за полминуты, и
    /// один и тот же срок значил бы для них разное. Теперь отсчёт начинается
    /// заново с КАЖДОЙ прибавкой градуса: греет — стоим сколько нужно, три
    /// минуты не греет — уходим и говорим об этом вслух, а не молча.
    /// </summary>
    public double MaxWarmSeconds { get; set; } = 180;

    /// <summary>Пауза между попытками переодеться.</summary>
    public TimeSpan DressCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Пауза после неудачной попытки согреться у огня.</summary>
    public TimeSpan FireRetry { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Начал мёрзнуть (аргумент — доля 0..1).</summary>
    public event Action<double>? OnFreezing;

    /// <summary>Согрелся.</summary>
    public event Action? OnWarm;

    /// <summary>Оделся теплее (аргумент — сколько вещей надел).</summary>
    public event Action<int>? OnDressed;

    /// <summary>
    /// Согреться у огня не вышло — роль решает, что делать (уйти, терпеть).
    ///
    /// Говорится ОДИН РАЗ НА ПОВОД (см. <see cref="безОгня"/>), поэтому
    /// подписчику не нужна своя заглушка повторов: подписывайте прямо в журнал.
    /// </summary>
    public event Action<string>? OnNoFire;

    /// <summary>
    /// Сказать про отсутствие тепла — один раз на повод.
    /// </summary>
    /// <param name="повод">
    /// СТАБИЛЬНАЯ причина, по которой тревога решает «новость или повтор». Без
    /// тикающих чисел: они живут в <paramref name="строка"/>.
    /// </param>
    /// <param name="строка">Что читает человек — с числами и местом.</param>
    private void НетТепла(string повод, string строка)
    {
        if (безОгня.Raise(повод))
            OnNoFire?.Invoke(строка);
    }

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        if (!Enabled || !heat.Ready)
            return false;

        double cold = heat.ColdFraction;

        // ХВАТИТ — ЭТО ГРАДУСНИК, А НЕ ДОЛЯ ЗАМЕРЗАНИЯ. Пока грение идёт
        // (warming), доля молчит: она обнуляется на 35 °C, и по ней бот
        // отходил от костра, не набрав ни градуса запаса (см. WarmUpTo)
        bool согрелся = heat.Temperature is { } тело && тело >= WarmUpTo;

        if (согрелся || (!warming && cold <= WarmFraction))
        {
            if (freezing || warming)
            {
                freezing = false;
                warming = false;
                warmestSoFar = null;
                warmingUntil = null;
                // Беда прошла — тревогу снимаем МОЛЧА: про «согрелся» человеку
                // уже сказали строкой ниже, а второй отбой был бы повтором
                безОгня.Clear();
                OnWarm?.Invoke();
            }
            return false;
        }

        if (!freezing && cold >= Math.Min(DressAtFraction, FireAtFraction))
        {
            freezing = true;
            OnFreezing?.Invoke(cold);
        }

        // 1. Одежда: действует всегда и ничего не стоит
        if (cold >= DressAtFraction && DateTime.UtcNow >= nextDress)
        {
            nextDress = DateTime.UtcNow + DressCooldown;
            int dressed = await heat.DressWarmestAsync(ct);
            if (dressed > 0)
            {
                OnDressed?.Invoke(dressed);
                return true; // ход забираем: дальше смотрим, помогло ли
            }
        }

        // 2. Огонь — БЕРЁМСЯ за него, когда мёрзнем всерьёз, а БРОСАЕМ по
        //    градуснику: начатое грение доводится до потолка (см. WarmUpTo).
        //    Без второго условия ход возвращался бы сюда только пока доля
        //    замерзания выше 0.3, то есть до 34,8 °C, — и запас тепла бот не
        //    набрал бы никогда
        if (!warming && cold < FireAtFraction)
            return false;

        // ТЕЛО БЕРЁМ, А НЕ ОТСТУПАЕМ ПЕРЕД ЗАНЯТЫМ. Здесь стояло
        // «if (ctx.Movement.IsBusy) return false;», и это молча отменяло
        // грение на всё время работы: пока идёт дорога, движение занято ВСЕГДА.
        //
        // ЖИВОЙ СЛУЧАЙ 23.08, СЛОВА ЗАКАЗЧИКА: «еще и замерз, а домой греться
        // не идет, потому что копает дорогу». Так и было — и не потому, что
        // тепло сочли неважным (вес у него 75, выше работы), а потому что оно
        // само уступало, никого не спросив и ничего не сказав.
        //
        // РЕФЛЕКС, А НЕ ЖИЗНЬ, И ЭТО ВЗВЕШЕНО. Жизнь (Vital) перебивает ВСЁ,
        // включая бой: так берут еду и лечение, потому что жевать можно и под
        // ударом. Грение — нет: чтобы согреться, надо ДОЙТИ до костра и стоять
        // у него, а бросать драку ради похода к огню — это умереть согретым.
        // Рефлекс же перебивает наряд и хозяйство, чего и не хватало.
        //
        // Захват на один заход: тело отпускается тут же, и бой получает его
        // со следующего тика, не дожидаясь конца грения.
        //
        // ДВЕРЬ — СПАСЕНИЯ, А НЕ ГОЛОЙ СТУПЕНИ РЕФЛЕКСА. Здесь стоял TryTake, и
        // выходило ровно то, на что жаловался заказчик, только этажом ниже:
        // тело брать было МОЖНО (рефлекс выше дороги), но начатая секунду назад
        // дорога держала грение выдержкой начатого дела
        // (BodyArbiter.SettleSeconds) — стенд приёмки напечатал на это null.
        // «Замёрзну через пять секунд» — не та мерка, по которой стоит ждать
        using var hold = ctx.Turn.TakeForRescue("греться",
            $"мёрзну ({cold:0.##}) — иду к огню", ct);
        if (hold == null)
        {
            // МОЛЧАТЬ НЕЛЬЗЯ (правило 4): со стороны «мёрзну и стою» ничем не
            // отличается от поломки. Кем занято тело, распорядитель уже сказал
            НетТепла("тело занято", "мёрзну, а телом сейчас распоряжается " +
                                    "дело поважнее — грение отложил");
            return false;
        }
        ct = hold.Token;

        // Уже стоим у огня: греемся до потолка, но не бесконечно — иначе бот
        // встанет у костра навсегда, если тепла костра на такой мороз не хватает
        if (heat.NearHeatStrength >= EnoughHeatStrength)
        {
            // Только что бросили это грение по терпению — не начинаем его тут
            // же заново: иначе бот у слабого костра крутил бы один и тот же
            // трёхминутный заход без единой строки в журнале
            if (!warming && DateTime.UtcNow < nextFireTry)
                return false;

            warming = true;
            // Терпение считаем от ПОСЛЕДНЕЙ ПРИБАВКИ, а не от начала: сильный
            // источник берёт потолок за полминуты, слабому нужны минуты, и
            // общий глухой срок обманул бы обоих (см. MaxWarmSeconds)
            if (heat.Temperature is { } сейчас &&
                (warmestSoFar is not { } было || сейчас > было))
            {
                warmestSoFar = сейчас;
                warmingUntil = DateTime.UtcNow.AddSeconds(MaxWarmSeconds);
            }
            warmingUntil ??= DateTime.UtcNow.AddSeconds(MaxWarmSeconds);
            if (DateTime.UtcNow < warmingUntil)
                return true;

            // ТЕРПЕНИЕ ВЫШЛО, А ПОТОЛОК НЕ ВЗЯТ. Молчать тут нельзя (правило
            // 4): со стороны это «бот постоял у костра и ушёл мёрзнуть»,
            // и человеку надо знать, что костра на такой мороз не хватило
            НетТепла("теплее не становится",
                $"грелся у огня, но за {MaxWarmSeconds:0} с теплее " +
                $"{(heat.TemperatureShown is { } гр ? $"{гр:0.#} °C" : "чем было")} " +
                "не стало — грение бросаю");
            warming = false;
            warmestSoFar = null;
            warmingUntil = null;
            nextFireTry = DateTime.UtcNow + FireRetry;
            return false;
        }

        if (DateTime.UtcNow < nextFireTry)
            return false;
        nextFireTry = DateTime.UtcNow + FireRetry;

        // ПОДЖИГ ЗДЕСЬ ТОЛЬКО У НАЙДЕННОГО КОСТРА. Своё, только что сложенное
        // кострище поджигает сам Fire.MakeCampfireAsync («развести» — это и
        // сложить, и поджечь), и второй зов был чистым повтором: живьём 19.08
        // в 00:12:53 «нечем поджечь (нужно firestarter)» вышло дважды подряд
        // за одну миллисекунду именно отсюда
        var pit = fire.NearestFirepit(FireSearchRadius);
        if (pit is { } найден && !fire.IsBurning(найден))
            await fire.IgniteAsync(найден, ct);
        else if (pit == null && MayBuildFire)
            pit = await fire.MakeCampfireAsync(ct);
        if (pit is not { } p)
        {
            НетТепла("костра нет", "костра нет и развести не вышло");
            return false;
        }

        if (!fire.IsBurning(p))
        {
            НетТепла("костёр не горит", $"костёр {p} не горит");
            return false;
        }

        // Подходим вплотную: сила источника падает с расстоянием, на вытянутой
        // руке (4.5 бл) от костра остаётся меньше пятой части тепла. НО НЕ В
        // САМ КОСТЁР: зажим NeverCloserToFire — это и есть разница между «у
        // костра» и «в костре», разобранная у WarmDistance
        //
        // ТОКЕН — ОТ ВЗЯТОГО ТЕЛА, А НЕ ОТ ТИКА. Здесь стоял тиковый ct, и
        // подход к костру (до минуты бега) пережил бы потерю тела: перебей
        // грение бой или еда — владение ушло, а ноги ещё минуту несли бы бота
        // к огню чужим телом. Токен владения гаснет и от перехвата, и от
        // внешней отмены разом (BodyArbiter.Hold), так что он строго сильнее
        await ctx.Movement.ApproachAsync(
            () => (p.X + 0.5, p.Y + 0.5, p.Z + 0.5),
            stopDistance: StopDistance,
            maxSeconds: 60, ct: hold.Token);

        // Сервер пересчитывает силу источника раз в ~3 секунды — ждём и
        // проверяем по факту, а не «дошёл, значит греюсь»
        await Task.Delay(4000, ct).ContinueWith(_ => { });
        if (heat.NearHeatStrength >= EnoughHeatStrength)
        {
            // ГРЕНИЕ НАЧАЛОСЬ — с этого мига уходить решает градусник, а не
            // доля замерзания (см. WarmUpTo). Начальную высоту запоминаем
            // здесь же: терпение считается от прибавок к НЕЙ
            warming = true;
            warmestSoFar = heat.Temperature;
            warmingUntil = DateTime.UtcNow.AddSeconds(MaxWarmSeconds);
            nextFireTry = DateTime.MinValue;
            // Дошли и греемся — прошлый отказ больше не правда
            безОгня.Clear();
        }
        else
            НетТепла("тепла не чувствую",
                $"дошёл до {p}, но тепла не чувствую ({heat.NearHeatStrength:0.##})");
        return true;
    }
}
