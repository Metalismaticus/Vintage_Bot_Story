using System.Text.Json.Serialization;

namespace VsBotKit;

/// <summary>
/// КОГДА БОТ ХОДИТ В БРОНЕ — И, ТЕМ САМЫМ, КОГДА ЕЁ СНИМАЕТ.
///
/// ЭТО ОДНА РУЧКА, А НЕ ДВЕ, И ЭТО ГЛАВНОЕ. Прежде решений было ЧЕТЫРЕ штуки в
/// разных углах боя (надеть перед стычкой, снять после, снять убегая, снять
/// когда врагов не видно), и каждое смотрело на свои условия. Живой случай
/// заказчика — «не снимал броню после шторма и опасности» — вышел ровно из
/// этого: снятие висело на ветке «врагов рядом нет вовсе», а всю временную бурю
/// вокруг бота бродили дрифтеры. Ветка не срабатывала НИ РАЗУ, и бот сидел в
/// броне часами. Теперь вопрос один: нужна ли броня прямо сейчас, — и ответ на
/// него один и тот же и для «надеть», и для «снять».
///
/// ЧЕГО СТОИТ БРОНЯ (цифры из ассетов игры 1.22.6,
/// assets/survival/itemtypes/wearable/seraph/armor.json, поле statModifiers;
/// применяет их <c>Vintagestory.GameContent.ModSystemWearableStats</c> из
/// VSSurvivalMod.dll — метод updateWearableStats складывает walkSpeed и
/// hungerrate всех надетых вещей и пишет их в статы «walkspeed» и «hungerrate»):
///
///   латы (plate)      — каждая часть: скорость −14 %, голод +24 %
///   чешуя (scale)     — каждая часть: скорость −7 %,  голод +12 %
///   бригандина        — каждая часть: скорость −5 %,  голод +12 %
///   кольчуга (chain)  — каждая часть: скорость −3 %,  голод +7,5 %
///
/// Комплект — это три части (голова, тело, ноги), то есть полные латы стоят
/// ПОЛОВИНЫ скорости (−42 %) и почти двойного расхода еды (+72 %). Столько
/// бот платит за защиту, и платить это круглосуточно, копая карьер, — чистый
/// убыток. Сами числа бот берёт не отсюда, а из реестра сервера
/// (<see cref="ArmorInfo.WalkSpeedPenalty"/>, <see cref="ArmorInfo.HungerRatePenalty"/>)
/// — тогда работают и модовые доспехи.
///
/// ИМЕНА РУССКИЕ И В СТРОКУ — по тому же правилу, что и у
/// <see cref="RevengePolicy"/>: человек ВИДИТ И НАБИРАЕТ это в окне управления.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WearArmor
{
    /// <summary>Не надевать вовсе: скорость и сытость дороже (гонец, торговец).</summary>
    Никогда,

    /// <summary>
    /// Только пока идёт размен ударов. Снимается, едва стычка кончилась, — и
    /// значит, бот бежит и работает налегке.
    /// </summary>
    ВБою,

    /// <summary>
    /// Пока рядом опасно: идёт стычка, недавно били, поблизости тварь или на
    /// дворе временная буря. Снимается, когда стало тихо, — и это ровно тот
    /// случай, которого заказчику не хватало после шторма.
    /// </summary>
    ПокаРядомОпасно,

    /// <summary>
    /// Не снимать никогда. Честный выбор для сторожа на посту, который никуда не
    /// бегает, — но помните про −42 % скорости и +72 % голода в полных латах.
    /// </summary>
    Всегда
}

/// <summary>
/// Броня по данным реестра сервера. Всё это приходит боту в JSON-атрибутах
/// предмета, так что работает и с модовой бронёй — ничего не захардкожено.
/// </summary>
/// <param name="Slot">Слот инвентаря персонажа: 12 голова, 13 тело, 14 ноги</param>
/// <param name="RelativeProtection">Доля поглощаемого урона (0..1)</param>
/// <param name="FlatDamageReduction">Сколько хп урона срезается сразу</param>
/// <param name="ProtectionTier">Тир защиты: против более сильного оружия броня слабеет</param>
/// <param name="HighDamageTierResistant">Потери за тир выше своего делятся пополам</param>
/// <param name="WalkSpeedPenalty">Отрицательное — бот медленнее</param>
/// <param name="HungerRatePenalty">Положительное — бот быстрее голодает</param>
public sealed record ArmorInfo(
    int Slot,
    float RelativeProtection,
    float FlatDamageReduction,
    int ProtectionTier,
    bool HighDamageTierResistant,
    float WalkSpeedPenalty,
    float HungerRatePenalty)
{
    public const int HeadSlot = 12;
    public const int BodySlot = 13;
    public const int LegsSlot = 14;

    /// <summary>Слоты брони в порядке важности: тело ловит 50% ударов, ноги 30%, голова 20%.</summary>
    public static readonly int[] SlotsByImportance = [BodySlot, LegsSlot, HeadSlot];

    /// <summary>
    /// Грубая оценка «сколько хп спасёт» против удара силой expectedHit.
    /// Порядок как в игре: сначала вычитается flat, потом доля.
    /// </summary>
    public double Value(double expectedHit = 5)
    {
        double after = Math.Max(0, expectedHit - FlatDamageReduction) * (1 - Math.Max(0, RelativeProtection));
        return expectedHit - after;
    }

    public override string ToString() =>
        $"слот {Slot}: -{FlatDamageReduction:0.#} и -{RelativeProtection:P0}, тир {ProtectionTier}";
}

/// <summary>
/// Надеть/снять броню. Механизм библиотеки: роль решает КОГДА (в бою, всегда,
/// никогда), а как именно ходят пакеты — забота этого класса.
///
/// Броня живёт в обычном инвентаре персонажа "character-&lt;uid&gt;", слоты 12/13/14.
/// Перекладывание — обычный MoveItemstack. Важно: в ЗАНЯТЫЙ слот брони положить
/// нельзя (броня не стакается, сервер вернёт неудачу) — сначала снимаем старую.
/// </summary>
public static class ArmorKit
{
    /// <summary>
    /// НУЖНА ЛИ БРОНЯ ПРЯМО СЕЙЧАС — ЧИСТОЕ ПРАВИЛО. Ни мира, ни бота: четыре
    /// ответа «да/нет» на входе, один на выходе, и потому оно проверяется стендом.
    ///
    /// ЭТО ЖЕ ПРАВИЛО РЕШАЕТ И «СНЯТЬ». Раньше решений было четыре в разных
    /// углах боя, и снятие висело на самой узкой из веток — «врагов не видно
    /// вовсе». Всю бурю вокруг бота ходили дрифтеры, ветка не срабатывала, и
    /// броня не снималась часами: ровно то, на что заказчик и жаловался.
    ///
    /// УБЕГАЯ — БЕЗ БРОНИ, что бы ни разрешала роль (кроме прямого «Всегда»).
    /// Это не осторожность, а арифметика: полный доспех из лат отнимает 42 %
    /// скорости (walkSpeed −0,14 на каждую из трёх частей, см.
    /// <see cref="WearArmor"/>), а в побеге важна ровно она. Догнанному в латах
    /// защита уже не поможет.
    /// </summary>
    /// <param name="when">Политика роли: когда бот вообще носит броню.</param>
    /// <param name="fighting">Прямо сейчас идёт размен ударов.</param>
    /// <param name="fleeing">Прямо сейчас бот уходит от боя.</param>
    /// <param name="dangerNear">
    /// Рядом опасно: тварь поблизости, недавно били или идёт временная буря.
    /// </param>
    public static bool NeedArmor(WearArmor when, bool fighting, bool fleeing, bool dangerNear) =>
        NeedGear(when, fighting, fleeing, dangerNear, dropWhenFleeing: true);

    /// <summary>
    /// ТО ЖЕ САМОЕ ПРАВИЛО, НО ПРО ЛЮБОЕ СНАРЯЖЕНИЕ ПО УГРОЗЕ — броню и ЩИТ.
    ///
    /// ЗАЧЕМ ПОНАДОБИЛОСЬ РАСШИРЕНИЕ, А НЕ ВТОРАЯ ТАКАЯ ЖЕ ФУНКЦИЯ. Заказчик
    /// просит щит, который «надевается только в момент угрозы, как и броня».
    /// Вопрос у щита ТОТ ЖЕ САМЫЙ — «нужно ли это прямо сейчас», — и второй
    /// такой же вопрос рядом означал бы ровно ту беду, из-за которой это
    /// правило и собирали в одно: четыре решения про броню в разных углах боя,
    /// каждое со своими условиями, и бот, восемь минут просидевший в латах
    /// после бури. Заведи щит своё правило — и через месяц он точно так же
    /// останется на руке после стычки.
    ///
    /// ЧТО У НИХ РАЗНОЕ — РОВНО ОДНО, И ЭТО ЧИСЛА, А НЕ ФОРМА. Броня в побеге
    /// СНИМАЕТСЯ: полные латы отнимают 42 % скорости (walkSpeed −0,14 на каждую
    /// из трёх частей), а в побеге важна ровно она — догнанному в латах защита
    /// уже не поможет. У щита скорость не отнимается ВООБЩЕ (в shield.json нет
    /// ни walkSpeed, ни statModifier), зато есть <c>projectileDamageAbsorption</c>
    /// — то самое, что спасает спину убегающего от стрел боуторна. Поэтому
    /// снимать его в побеге не только незачем, но и вредно.
    ///
    /// Цена щита другая и названа вслух в другом месте: любой предмет в
    /// офф-хенде без своего statModifier даёт <c>hungerrate +0,2</c>
    /// (<c>InventoryPlayerHotbar.OffHandHungerPenalty</c>), и он же занимает
    /// руку, в которой живёт фонарь (см. <see cref="Shielding"/>).
    /// </summary>
    /// <param name="dropWhenFleeing">
    /// Убегая — сбрасывать. Правда для брони (спасает скорость), ложь для щита
    /// (скорости он не стоит, а стрелу в спину ловит).
    /// </param>
    public static bool NeedGear(WearArmor when, bool fighting, bool fleeing, bool dangerNear,
        bool dropWhenFleeing) =>
        when switch
        {
            WearArmor.Никогда => false,
            WearArmor.Всегда => true,
            _ when fleeing && dropWhenFleeing => false,
            WearArmor.ВБою => fighting || (fleeing && !dropWhenFleeing),
            WearArmor.ПокаРядомОпасно => fighting || dangerNear || (fleeing && !dropWhenFleeing),
            _ => false
        };

    /// <summary>
    /// ЧЕГО СТОИТ НАДЕТОЕ — по реестру сервера, а не по нашей памяти о ванильных
    /// доспехах: доли скорости и расхода еды, сложенные по всем надетым частям
    /// ровно так, как их складывает сама игра
    /// (<c>ModSystemWearableStats.updateWearableStats</c>).
    ///
    /// Нужно затем, что «снял броню» без чисел — пустая строка: человек не
    /// увидит, ЧТО бот этим выиграл. С числами она читается сама.
    /// </summary>
    public static (double Walk, double Hunger) Cost(BotContext ctx)
    {
        double walk = 0, hunger = 0;
        foreach (var (_, _, info) in Worn(ctx))
        {
            walk += info.WalkSpeedPenalty;
            hunger += info.HungerRatePenalty;
        }
        return (walk, hunger);
    }

    /// <summary>Что сейчас надето (слот → код предмета).</summary>
    public static IEnumerable<(int Slot, string Code, ArmorInfo Info)> Worn(BotContext ctx)
    {
        var character = ctx.Self.GetInventory("character");
        if (character == null)
            yield break;
        foreach (int slot in ArmorInfo.SlotsByImportance)
        {
            if (slot >= character.Length || character[slot].Code is not { } code)
                continue;
            if (ctx.World.GetArmorByCode(code) is { } info)
                yield return (slot, code, info);
        }
    }

    /// <summary>
    /// Надеть лучшую броню из рюкзаков и хотбара. Возвращает, сколько
    /// предметов надел. Уже надетое меняет только на строго лучшее.
    /// </summary>
    public static async Task<int> EquipBestAsync(BotContext ctx, CancellationToken ct = default)
    {
        string? characterId = ctx.Self.GetInventoryId("character");
        var character = ctx.Self.GetInventory("character");
        if (characterId == null || character == null)
            return 0;

        int equipped = 0;
        foreach (int slot in ArmorInfo.SlotsByImportance)
        {
            if (ct.IsCancellationRequested || slot >= character.Length)
                break;

            double wornValue = character[slot].Code is { } wornCode &&
                               ctx.World.GetArmorByCode(wornCode) is { } wornInfo
                ? wornInfo.Value()
                : 0;

            // Ищем в своих сумках кандидата именно на этот слот
            var candidate = ctx.Self.FindBestItem(s =>
                s.Code is { } c && ctx.World.GetArmorByCode(c) is { } info && info.Slot == slot
                    ? info.Value()
                    : 0);
            if (candidate == null || ctx.World.GetArmorByCode(candidate.Content.Code!)!.Value() <= wornValue)
                continue;

            // Занятый слот сначала освобождаем — иначе сервер откажет
            if (!character[slot].IsEmpty && !await UnequipSlotAsync(ctx, slot, ct))
                continue;

            string код = candidate.Content.Code!;
            await ctx.Actions.MoveItemAsync(candidate.InventoryId, candidate.Slot, characterId, slot, 1);

            // НАДЕЛ — ЭТО СОДЕРЖИМОЕ СЛОТА ПОСЛЕ ПЕРЕНОСА, а не «пакет ушёл».
            // Раньше здесь стояла глухая пауза и безусловное equipped++: сервер
            // молча отвергал перенос (слот занят, вещь не той категории), а бот
            // докладывал в бою «надел 3 вещи», не надев ни одной, — и шёл на
            // моба голым, считая себя одетым
            if (ctx.Cold is { } cold && await cold.WaitSlotAsync(slot, код, ct))
                equipped++;
        }
        return equipped;
    }

    /// <summary>
    /// Надеть сумки. Без них у бота ровно ДЕСЯТЬ слотов хотбара, и он честно
    /// упирается в «некуда класть»: у голого игрока в игре так же.
    ///
    /// Сумки живут в первых четырёх слотах инвентаря «backpack-&lt;uid&gt;» — это
    /// не хранилище, а места под сами сумки; вещи появляются в слотах с 4-го,
    /// когда сумка надета. Возвращает, сколько сумок надел.
    /// </summary>
    public static async Task<int> EquipBagsAsync(BotContext ctx, CancellationToken ct = default)
    {
        string? bagsId = ctx.Self.GetInventoryId("backpack");
        var bags = ctx.Self.GetInventory("backpack");
        if (bagsId == null || bags == null)
            return 0;

        int worn = 0;
        for (int slot = 0; slot < Math.Min(4, bags.Length); slot++)
        {
            if (ct.IsCancellationRequested || !bags[slot].IsEmpty)
                continue;
            // Сумку узнаём по поведению HeldBag из реестра сервера, а не по
            // списку названий: так работают и сумки из модов
            var candidate = ctx.Self.FindBestItem(s =>
                s.Code is { } c && ctx.World.IsBagByCode(c) ? 1 : 0);
            if (candidate == null)
                break;

            await ctx.Actions.MoveItemAsync(candidate.InventoryId, candidate.Slot, bagsId, slot, 1);
            await Task.Delay(300, ct).ContinueWith(_ => { });
            if (ctx.Self.GetInventory("backpack") is { } after && slot < after.Length && !after[slot].IsEmpty)
                worn++;
        }
        return worn;
    }

    /// <summary>
    /// СНЯТЬ ВСЮ БРОНЮ — ЕДИНСТВЕННАЯ ДВЕРЬ НА ВЕСЬ ПРОЕКТ.
    ///
    /// Сюда ходят все, кому надо раздеть бота целиком: рефлекс боя (по правилу
    /// <see cref="NeedArmor"/>) и кнопка «снять броню» в окне управления. Второй
    /// такой двери заводить нельзя — иначе мерки разойдутся, как они уже
    /// расходились у <see cref="EquipBestAsync"/> и снятия одного слота (см.
    /// <see cref="UnequipSlotAsync"/>).
    ///
    /// Снятие ОДНОГО слота — тоже единственное и тоже не здесь:
    /// <see cref="BodyHeat.UndressSlotAsync"/>, и этот метод зовёт именно его.
    /// Возвращает, сколько вещей СНЯЛОСЬ ПО ФАКТУ (слот опустел), а не сколько
    /// пакетов ушло.
    /// </summary>
    public static async Task<int> UnequipAllAsync(BotContext ctx, CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var (slot, _, _) in Worn(ctx).ToList())
        {
            if (ct.IsCancellationRequested)
                break;
            if (await UnequipSlotAsync(ctx, slot, ct))
                removed++;
        }
        return removed;
    }

    /// <summary>
    /// Снять один слот в первое свободное место в сумках.
    ///
    /// СВОЕГО ТЕЛА У ЭТОГО МЕТОДА БОЛЬШЕ НЕТ — он зовёт
    /// <see cref="BodyHeat.UndressSlotAsync"/>, единственный такой механизм.
    /// Копий было две, и мерки у них разошлись: здесь успехом считалось «пакет
    /// ушёл» (перенос и глухие 200 мс), и <see cref="EquipBestAsync"/>, получив
    /// безусловное true, слал следующий перенос в ЗАНЯТЫЙ слот — сервер отвергал
    /// его молча, а бот докладывал «снял N» и «надел N», не сняв и не надев
    /// ничего. Сито «куда класть» тут тоже было своё и дырявое: снятая броня
    /// могла уехать в «ground-&lt;uid&gt;», то есть под ноги.
    /// </summary>
    private static Task<bool> UnequipSlotAsync(BotContext ctx, int slot, CancellationToken ct) =>
        ctx.Cold is { } cold ? cold.UndressSlotAsync(slot, ct) : Task.FromResult(false);
}
