using System.Collections.Concurrent;

using Vintagestory.API.Datastructures;

namespace VsBotKit;

/// <summary>
/// Содержимое слота инвентаря. Code == null — слот пуст.
///
/// Durability — ОСТАТОК прочности так, как его видит клиент игры:
/// CollectibleObject.GetRemainingDurability читает атрибут стака "durability",
/// а если атрибута нет — предмет ещё целый, и остаток равен максимуму.
/// У предмета, который вообще не изнашивается (руда, зерно, доски), здесь 0 —
/// это НЕ «сломан», а «прочности не бывает». Отличить одно от другого можно
/// только по реестру: WorldModel.Durability(code) == 0. Для этого есть ToolWear.
/// </summary>
public sealed record SlotContent(string? Code, int Count, int Durability = 0)
{
    public bool IsEmpty => Code == null || Count == 0;
    public override string ToString() =>
        IsEmpty ? "-" : Durability > 0 ? $"{Code} x{Count} ({Durability})" : $"{Code} x{Count}";
}

/// <summary>
/// Собственное состояние бота: здоровье, голод, позиция, инвентари.
/// Инвентари приходят в PlayerData(41) и InventoryContents(30), обновляются
/// пакетами InventoryUpdate(31) / InventoryDoubleUpdate(32). Сюда же попадёт
/// содержимое сундука, когда бот его откроет — сервер шлёт его тем же
/// InventoryContents.
/// </summary>
public class SelfState
{
    private readonly BotClient bot;
    private readonly EntityModel entities;
    private readonly WorldModel world;

    /// <summary>Инвентари по полному id ("hotbar-&lt;uid&gt;", "backpack-&lt;uid&gt;", сундуки...).</summary>
    private readonly ConcurrentDictionary<string, SlotContent[]> inventories = new();

    public event Action<string>? OnInventoryChanged;

    /// <summary>Бот погиб. При AutoRespawn (по умолчанию) сам возродится через 2 секунды.</summary>
    public event Action? OnDeath;

    /// <summary>Автоматически возрождаться после смерти.</summary>
    public bool AutoRespawn { get; set; } = true;

    /// <summary>Свой ClientId на сервере (из PlayerData).</summary>
    public int OwnClientId { get; private set; } = -1;

    /// <summary>
    /// В КАКОМ РЕЖИМЕ ИГРЫ СЕРВЕР ДЕРЖИТ БОТА — факт от сервера, а не догадка.
    /// null — сервер ещё не сказал (до входа в мир).
    ///
    /// Приходит в PlayerData(41) полем GameMode при входе и меняется пакетом
    /// ModeChange(46), когда режим переключают (ServerWorldPlayerData.ToPacket и
    /// Packet_PlayerMode, сверено по VintagestoryLib 1.22.7).
    ///
    /// ЗАЧЕМ ЭТО ЗНАТЬ. Отладочные команды (VintageBotStory/DiagnosticCommands.cs,
    /// подключаются флагом «--diag» из Program.cs, а НЕ ролью — живьём это был
    /// прогон роли «Выживальщик») слали при входе «/player &lt;имя&gt; gamemode
    /// survival» — страховку на случай, если прошлый прогон оставил бота в
    /// творческом. На сервере заказчика прав на эту команду нет, и в каждом
    /// прогоне с «--diag» журнал получал два безымянных отказа («у вас нет
    /// прав…», «For help, type /help player») — а спросить-то было не о чем:
    /// сервер сам сказал режим тремя секундами раньше.
    /// </summary>
    public Vintagestory.API.Common.EnumGameMode? GameMode { get; private set; }

    /// <summary>
    /// ЧТО СЕРВЕР ПОЗВОЛЯЕТ БОТУ — его собственные права, списком кодов
    /// («chat», «build», «gamemode»…). Пусто — сервер прав ещё не присылал.
    ///
    /// Сервер шлёт их лично игроку в том же PlayerData(41) при входе
    /// (ServerMain.BroadcastPlayerData(sendPrivileges: true)), и живой игрок
    /// видит их тем же способом. Знать их нужно ровно для одного: НЕ СЛАТЬ
    /// команду, на которую права заведомо нет, и сказать об этом вслух.
    /// </summary>
    public IReadOnlyCollection<string> Privileges => privileges;

    private volatile string[] privileges = [];

    /// <summary>
    /// Роль игрока на сервере, как её назвал сам сервер («suplayer», «admin»).
    /// null — не сказал.
    /// </summary>
    public string? RoleCode { get; private set; }

    /// <summary>
    /// ЕСТЬ ЛИ У БОТА ЭТО ПРАВО. null — сервер списка прав не присылал, и
    /// врать «есть»/«нет» нельзя: молчаливая догадка здесь и стоила бы команды,
    /// посланной вслепую.
    ///
    /// Коды прав — из <c>Vintagestory.API.Server.Privilege</c> самой игры
    /// («gamemode» — «Ability to set own game mode»).
    /// </summary>
    public bool? May(string privilege) =>
        privileges.Length == 0
            ? null
            : privileges.Contains(privilege, StringComparer.OrdinalIgnoreCase);

    public SelfState(BotClient bot, EntityModel entities, WorldModel world)
    {
        this.bot = bot;
        this.entities = entities;
        this.world = world;
        bot.OnPacket += HandlePacket;
    }

    /// <summary>Собственная сущность (появляется после входа).</summary>
    public EntityInfo? Entity => entities.Self;

    public (double X, double Y, double Z)? Position =>
        Entity is { } e ? (e.X, e.Y, e.Z) : null;

    public float? Health => Entity?.Health;
    public float? MaxHealth => Entity?.MaxHealth;
    public float? Saturation => Entity?.Saturation;
    public float? MaxSaturation => Entity?.MaxSaturation;

    public IReadOnlyDictionary<string, SlotContent[]> Inventories => inventories;

    /// <summary>
    /// Класс инвентаря «под ногами»: всё, что туда переложено, падает на землю.
    /// Так живой игрок и выбрасывает вещи — отдельного пакета «бросить» нет.
    ///
    /// ЭТО ИМЯ КЛАССА, А НЕ ID. В пакет переноса его класть НЕЛЬЗЯ — см.
    /// <see cref="GroundInventoryId"/>.
    /// </summary>
    public const string GroundInventory = "ground";

    /// <summary>
    /// Настоящий id инвентаря «под ногами» — «ground-&lt;uid&gt;».
    ///
    /// ПОЧЕМУ ЭТО ОТДЕЛЬНОЕ СВОЙСТВО. Сервер ищет инвентарь по ТОЧНОМУ ключу
    /// (PlayerInventoryManager.GetInventory — обычный поиск в словаре, без
    /// дописывания uid), и на голое «ground» не находит ничего. Тогда
    /// InventoryBase.GetSlotsIfExists отдаёт пару пустых слотов,
    /// TryMoveItemStack возвращает ложь, сервер шлёт обратно прежнее
    /// содержимое — и выброс не происходит МОЛЧА. Ровно этим бот и не мог
    /// выбросить ни одного кома земли.
    ///
    /// Сервер шлёт этот инвентарь вместе с остальными своими
    /// (ServerWorldPlayerData: в пакет идут все InventoryBasePlayer), поэтому
    /// обычно он просто находится по префиксу. Собственное имя — на случай,
    /// когда пакет ещё не пришёл, но uid уже известен.
    /// </summary>
    public string? GroundInventoryId =>
        GetInventoryId(GroundInventory) ??
        (string.IsNullOrEmpty(bot.PlayerUid) ? null : $"{GroundInventory}-{bot.PlayerUid}");

    /// <summary>
    /// Забыть содержимое инвентаря контейнера. Нужно ПЕРЕД открытием сундука:
    /// иначе бот видит прошлый список и принимает его за свежий — сервер
    /// снимок не обновляет, пока сундук не открыт заново.
    /// </summary>
    public void ForgetInventory(string inventoryId) => inventories.TryRemove(inventoryId, out _);

    /// <summary>
    /// КУДА БОТ ЗАГЛЯДЫВАЛ САМ и когда в последний раз. Заполняется в тот
    /// единственный миг, когда содержимое приходит честно, — на пакете
    /// открытия контейнера (5000).
    ///
    /// Зачем это нужно. Сервер шлёт содержимое сундуков вместе с чанком, и по
    /// этим данным бот пересчитывал чужие закрытые сундуки, не подходя к ним.
    /// Живой игрок так не может: он знает, что внутри, только если открывал
    /// сам. Вот эта память и есть мерка «открывал сам» — по ней
    /// <see cref="WorldModel.MayLookInside"/> и решает, честно ли смотреть.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<BlockPos, DateTime> lookedInside = new();

    /// <summary>
    /// Бот ткнул в блок в этой точке — если там контейнер, он открылся, и
    /// человек в этот миг увидел бы, что внутри. Зовёт
    /// <see cref="Actions.UseBlockAsync"/>, одно место на все открытия.
    /// </summary>
    public void NoteLookedInside(BlockPos pos) => lookedInside[pos] = DateTime.UtcNow;

    /// <summary>
    /// Когда бот в последний раз ЗАГЛЯДЫВАЛ внутрь контейнера в этой точке
    /// (UTC). null — не заглядывал ни разу.
    /// </summary>
    public DateTime? LookedInsideAt(BlockPos pos) =>
        lookedInside.TryGetValue(pos, out var when) ? when : null;

    /// <summary>
    /// Свежо ли ещё то, что бот там видел. Секунды, а не часы: человек, отойдя
    /// от сундука, помнит его содержимое, но выдавать эту память за нынешнее
    /// положение дел нельзя — из сундука могли всё вынести.
    /// </summary>
    public bool SawInsideRecently(BlockPos pos, double seconds) =>
        LookedInsideAt(pos) is { } when && (DateTime.UtcNow - when).TotalSeconds <= seconds;

    /// <summary>Бот мёртв и ещё не возродился.</summary>
    public bool IsDead { get; private set; }

    /// <summary>
    /// ЛЕЖИТ ЛИ ТЕЛО — ЧИСТОЕ ПРАВИЛО, И ОБА ЕГО ПРИЗНАКА ЕСТЬ ФАКТ ОТ СЕРВЕРА.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 19.08, 18:34:16 → 18:34:19). Десять строк подряд:
    ///   18:34:16 погиб, возрождаюсь
    ///   18:34:16 ОТКАЗ СЕРВЕРА: noprivilege-use-playerdead   (и ещё девять)
    /// Бот умер на пороге собственного дома, а начатая при жизни ходьба
    /// продолжала кликать по двери — пять заходов по два пакета
    /// (StartBlockUse + StopBlockUse), оттого отказы и шли парами.
    ///
    /// ЧТО ОТВЕЧАЕТ СЕРВЕР, ФАКТОМ ИЗ ЕГО КОДА: <c>HandleBlockInteract</c>
    /// (VintagestoryLib 1.22.7, ServerSystemBlockSimulation) спрашивает
    /// <c>TestBlockAccess(…, EnumBlockAccessFlags.Use)</c>, получает
    /// <c>PlayerDead</c> и шлёт <c>noprivilege-use-playerdead</c> — по
    /// языковому файлу игры это «No privilege to use blocks. You are dead.»
    /// Мёртвому не дают ни использовать, ни ломать, ни ставить.
    ///
    /// ДВА ПРИЗНАКА, А НЕ ОДИН, И ОБА НУЖНЫ:
    /// - извещение о смерти (пакет 45) приходит РАЗ, и переподключившийся бот
    ///   его теряет — тогда <see cref="IsDead"/> так и остаётся ложью;
    /// - здоровье на нуле сервер шлёт постоянно, и оно же САМО СНИМАЕТ запрет,
    ///   когда сервер и правда поднял бота. Просить об этом кнопку возрождения
    ///   нельзя: она — наша заявка, а не ответ сервера.
    /// Догадок здесь нет ни одной: «здоровье неизвестно» (null) — это НЕ
    /// смерть, иначе бот с ещё не пришедшим состоянием стоял бы столбом.
    /// </summary>
    public static bool BodyIsDead(bool deathNotice, float? health) =>
        deathNotice || health <= 0;

    /// <summary>
    /// Тело лежит: серверу слать действия руками нельзя, он на всё ответит
    /// отказом. Спрашивают <see cref="Actions"/> и <see cref="Hands"/> —
    /// правило одно на всех, см. <see cref="BodyIsDead"/>.
    /// </summary>
    public bool Dead => BodyIsDead(IsDead, Health);

    /// <summary>
    /// Через сколько возрождаться. Ровно две секунды каждый раз — машинная
    /// примета; человек жмёт кнопку когда придётся, поэтому берём случайную
    /// задержку в этих пределах.
    /// </summary>
    public (double Min, double Max) RespawnDelaySeconds { get; set; } = (2.5, 6.0);

    /// <summary>Забыть инвентари: после переподключения сервер пришлёт их заново.</summary>
    public void Reset()
    {
        inventories.Clear();
        lookedInside.Clear();
        IsDead = false;
        OwnClientId = -1;
        // Мир мог смениться, а с ним и права, и режим: помнить старое —
        // значит принять решение по чужому серверу
        GameMode = null;
        privileges = [];
        RoleCode = null;
    }

    /// <summary>Первый инвентарь, id которого начинается с префикса ("hotbar", "backpack"...).</summary>
    public SlotContent[]? GetInventory(string idPrefix) =>
        inventories.FirstOrDefault(kv => kv.Key.StartsWith(idPrefix)).Value;

    /// <summary>Id инвентаря по префиксу ("hotbar" → "hotbar-&lt;uid&gt;").</summary>
    public string? GetInventoryId(string idPrefix) =>
        inventories.Keys.FirstOrDefault(k => k.StartsWith(idPrefix));

    /// <summary>
    /// Свои инвентари (хотбар, рюкзаки, одежда) — в отличие от чужих,
    /// вроде открытого сундука. Свои заканчиваются на uid игрока.
    /// </summary>
    public IEnumerable<KeyValuePair<string, SlotContent[]>> OwnInventories =>
        inventories.Where(kv => kv.Key.EndsWith(bot.PlayerUid, StringComparison.Ordinal));

    /// <summary>
    /// Слоты, в которые предмет вообще можно положить.
    ///
    /// В хотбаре их ДЕСЯТЬ, хотя инвентарь длиннее: слот 10 — это умение
    /// (ItemSlotSkill), слот 11 — левая рука (ItemSlotOffhand), и обычный
    /// предмет туда не кладётся. Сервер такой перенос молча отвергает —
    /// именно так жареное мясо не забиралось из костра.
    /// В рюкзаке первые четыре слота — сами сумки, их тоже не трогаем.
    /// </summary>
    /// <remarks>
    /// ЧИСЛА СЛОТОВ — НЕ СВОИ, А ОБЩИЕ (<see cref="Bags"/>). Здесь они стояли
    /// голыми («&lt; 10», «&gt;= 4»), и это была ТРЕТЬЯ копия одного и того же
    /// факта игры: те же номера объявлены у <see cref="Bags"/> и у
    /// <see cref="HandLight"/>. Три копии одного числа держатся вместе ровно до
    /// первого мода, двигающего раскладку хотбара, — а разъехавшись, они врут
    /// по-разному: окно рисует свободным место, куда сервер вещь не примет.
    /// </remarks>
    public static bool IsStorageSlot(string inventoryId, int slot)
    {
        if (inventoryId.StartsWith("hotbar", StringComparison.Ordinal))
            return slot >= 0 && slot < Bags.SkillSlot;
        if (inventoryId.StartsWith("backpack", StringComparison.Ordinal))
            return slot >= Bags.BagSlots;
        return slot >= 0;
    }

    /// <summary>
    /// Сколько всего мест в «backpack»: четыре слота под сами сумки плюс то,
    /// что дают надетые сумки.
    ///
    /// Сервер шлёт по сети ТОЛЬКО четыре слота
    /// (InventoryPlayerBackpacks.CountForNetworkPacket = 4) — содержимое сумок
    /// лежит внутри самих предметов-сумок, и клиент разворачивает его сам.
    /// Поэтому «сколько слотов вижу» и «сколько их есть» — разные числа, и бот,
    /// считая по первому, честно докладывал «инвентарь полон», имея две сумки.
    /// </summary>
    public int BackpackSlotCount()
    {
        var bags = GetInventory("backpack");
        if (bags == null)
            return 0;
        int total = 4;
        for (int i = 0; i < Math.Min(4, bags.Length); i++)
            if (bags[i].Code is { } code)
                total += world.BagSlotsByCode(code);
        return total;
    }

    /// <summary>Найденный предмет: где лежит и что это.</summary>
    public sealed record FoundItem(string InventoryId, int Slot, SlotContent Content);

    /// <summary>
    /// Сумки, из которых бот берёт и в которые кладёт: хотбар и рюкзак.
    /// Одежда, мышь, сетка крафта и творческий инвентарь сюда не входят.
    /// </summary>
    public static bool IsCarryBag(string inventoryId) =>
        inventoryId.StartsWith("hotbar", StringComparison.Ordinal) ||
        inventoryId.StartsWith("backpack", StringComparison.Ordinal);

    /// <summary>Перебрать свои сумки по слотам, годным под вещи.</summary>
    public IEnumerable<(string InventoryId, int Slot, SlotContent Content)> CarrySlots()
    {
        foreach (var (invId, slots) in OwnInventories)
        {
            if (!IsCarryBag(invId))
                continue;
            for (int i = 0; i < slots.Length; i++)
                if (IsStorageSlot(invId, i))
                    yield return (invId, i, slots[i]);
        }
    }

    /// <summary>
    /// Сколько в сумках свободных мест.
    ///
    /// Считаем по <see cref="CarrySlots"/>, а не по всем слотам подряд: слоты
    /// под сами сумки, умение и левая рука вещей не принимают, и сервер такой
    /// перенос молча отвергает. Раньше это число считали трое (карьер, штольня,
    /// склад) и каждый по-своему — отсюда и расхождения в отчётах.
    /// </summary>
    public int FreeCarrySlots() => CarrySlots().Count(s => s.Content.IsEmpty);

    /// <summary>
    /// ПЕРВОЕ СВОБОДНОЕ МЕСТО В СУМКАХ — куда вообще можно что-то положить.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫМ ПРАВИЛОМ. Раньше «первое пустое место» каждый искал сам,
    /// перебирая <see cref="OwnInventories"/> и отсеивая по имени «character»,
    /// «mouse», «creative». Под такое сито проходил «ground-&lt;uid&gt;» —
    /// инвентарь ПОД НОГАМИ, тот самый, через который вещи и выбрасываются на
    /// землю. У него ровно одно место, и оно пусто всегда, пока бот ничего не
    /// уронил; порядок обхода словаря — какой сложился. Живой случай: человек
    /// жмёт в окне «Надеть (тело)» на новой кирасе при надетой старой, старая
    /// «снимается» — и улетает под ноги, а через несколько минут исчезает
    /// совсем. Второй исход того же сита — хотбар 10/11 (умение, левая рука) и
    /// рюкзак 0..3 (места под сами сумки): туда сервер перенос молча отвергает,
    /// и бот врёт в другую сторону — «в сумках нет свободного места» при
    /// полупустой сумке.
    ///
    /// Поэтому спрашивать надо ЗДЕСЬ и только здесь: <see cref="CarrySlots"/>
    /// уже знает и про сумки (<see cref="IsCarryBag"/>), и про негодные места
    /// (<see cref="IsStorageSlot"/>).
    /// </summary>
    public (string InventoryId, int Slot)? FirstFreeCarrySlot()
    {
        foreach (var (invId, slot, content) in CarrySlots())
            if (content.IsEmpty)
                return (invId, slot);
        return null;
    }

    /// <summary>
    /// Что и сколько лежит в сумках: код → штук. По разнице двух таких
    /// снимков считается честная добыча — по факту, а не по числу ударов.
    /// </summary>
    public Dictionary<string, int> CarrySnapshot()
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, _, content) in CarrySlots())
            if (content.Code is { } code)
                total[code] = total.GetValueOrDefault(code) + content.Count;
        return total;
    }

    /// <summary>Что прибавилось между двумя снимками (убыль не считаем — это расход).</summary>
    public static Dictionary<string, int> Gained(Dictionary<string, int> before,
        Dictionary<string, int> after)
    {
        var gained = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, count) in after)
        {
            int delta = count - before.GetValueOrDefault(code);
            if (delta > 0)
                gained[code] = delta;
        }
        return gained;
    }

    /// <summary>Где лежит первый предмет, чей код подходит под условие.</summary>
    public FoundItem? FindCarried(Func<string, bool> match)
    {
        foreach (var (invId, slot, content) in CarrySlots())
            if (!content.IsEmpty && content.Code is { } code && match(code))
                return new FoundItem(invId, slot, content);
        return null;
    }

    /// <summary>Где лежит предмет с таким куском кода.</summary>
    public FoundItem? FindCarried(string codePart) =>
        FindCarried(c => c.Contains(codePart, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Куда положить: к такому же в стак либо в пустой слот (null — некуда).
    ///
    /// Эта функция была переписана слово в слово в готовке, разделке, крафте и
    /// поручениях. Именно в ней жила ошибка со слотом умения, и чинить её
    /// пришлось бы во всех копиях сразу — поэтому она тут одна.
    /// </summary>
    public (string InventoryId, int Slot)? FreeSlotFor(string code, int amount)
    {
        int max = world.MaxStackSize(code);
        (string, int)? empty = null;
        foreach (var (invId, slot, content) in CarrySlots())
        {
            if (content.IsEmpty)
            {
                empty ??= (invId, slot);
                continue;
            }
            if (string.Equals(content.Code, code, StringComparison.Ordinal) &&
                content.Count + amount <= max)
                return (invId, slot);
        }
        return empty;
    }

    /// <summary>
    /// Искать предмет по всем своим инвентарям (хотбар + рюкзаки), выбирая
    /// лучший по оценке. Оценка &lt;= 0 — предмет не подходит.
    /// </summary>
    public FoundItem? FindBestItem(Func<SlotContent, double> score)
    {
        FoundItem? best = null;
        double bestScore = 0;
        foreach (var (invId, slots) in OwnInventories)
        {
            // Одежду и слоты брони не трогаем — оттуда есть/лечиться нельзя
            if (invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].IsEmpty)
                    continue;
                double value = score(slots[i]);
                if (value > bestScore)
                {
                    bestScore = value;
                    best = new FoundItem(invId, i, slots[i]);
                }
            }
        }
        return best;
    }

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 41: // PlayerData — собственные инвентари (пакет приходит и про других игроков)
                if (p.PlayerData != null && p.PlayerData.PlayerUID == bot.PlayerUid)
                {
                    OwnClientId = p.PlayerData.ClientId;
                    GameMode = (Vintagestory.API.Common.EnumGameMode)p.PlayerData.GameMode;
                    // Права сервер кладёт сюда только в СВОЁМ пакете игроку
                    // (sendPrivileges), в пакете про других игроков их нет —
                    // поэтому пустой список не затирает уже известный
                    if (p.PlayerData.Privileges is { } list && p.PlayerData.PrivilegesCount > 0)
                        privileges = list.Take(p.PlayerData.PrivilegesCount).ToArray();
                    if (p.PlayerData.RoleCode is { Length: > 0 } role)
                        RoleCode = role;
                    if (p.PlayerData.InventoryContents != null)
                        for (int i = 0; i < p.PlayerData.InventoryContentsCount; i++)
                            StoreInventory(p.PlayerData.InventoryContents[i]);
                }
                break;

            case 46: // ModeChange — режим игры переключили (свой или чужой)
                if (p.ModeChange is { } mode && mode.PlayerUID == bot.PlayerUid)
                    GameMode = (Vintagestory.API.Common.EnumGameMode)mode.GameMode;
                break;

            case 45: // PlayerDeath
                if (p.PlayerDeath != null && p.PlayerDeath.ClientId == OwnClientId)
                {
                    IsDead = true;
                    OnDeath?.Invoke();
                    if (AutoRespawn)
                    {
                        var (min, max) = RespawnDelaySeconds;
                        int ms = (int)(1000 * (min + Random.Shared.NextDouble() * Math.Max(0, max - min)));
                        _ = Task.Delay(ms).ContinueWith(_ => RespawnAsync());
                    }
                }
                break;

            case 30: // InventoryContents (в т.ч. открытый сундук)
                if (p.InventoryContents != null)
                    StoreInventory(p.InventoryContents);
                break;

            case 31: // InventoryUpdate — один слот
                if (p.InventoryUpdate is { } u && u.InventoryId != null)
                    UpdateSlot(u.InventoryId, u.SlotId, u.ItemStack);
                break;

            case 32: // InventoryDoubleUpdate — перенос между двумя слотами
                if (p.InventoryDoubleUpdate is { } d)
                {
                    if (d.InventoryId1 != null) UpdateSlot(d.InventoryId1, d.SlotId1, d.ItemStack1);
                    if (d.InventoryId2 != null) UpdateSlot(d.InventoryId2, d.SlotId2, d.ItemStack2);
                }
                break;

            case 44: // BlockEntityMessage — контейнер шлёт своё содержимое (PacketId 5000)
                if (p.BlockEntityMessage is { PacketId: 5000, Data: { } data })
                    StoreContainerFromOpen(new BlockPos(p.BlockEntityMessage.X, p.BlockEntityMessage.Y, p.BlockEntityMessage.Z), data);
                break;
        }
    }

    /// <summary>Возродиться после смерти (кнопка «Respawn»).</summary>
    public Task RespawnAsync()
    {
        IsDead = false;
        return SendRespawnAsync();
    }

    private Task SendRespawnAsync() =>
        bot.SendPacketAsync(new Packet_Client
        {
            Id = 12,
            SpecialKey_ = new Packet_ClientSpecialKey { Key_ = 0 } // 0 = respawn
        });

    /// <summary>
    /// Разбор пакета 5000 (BlockEntityContainerOpen): string BlockEntity,
    /// string DialogTitle, byte Columns, TreeAttribute Tree (инвентарь).
    /// Сохраняем под id "&lt;класс&gt;-x, y, z", чтобы последующие точечные
    /// обновления (31/32) применялись к тому же инвентарю.
    /// </summary>
    private void StoreContainerFromOpen(BlockPos pos, byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            reader.ReadString();  // BlockEntity
            reader.ReadString();  // DialogTitle
            reader.ReadByte();    // Columns
            var tree = new TreeAttribute();
            tree.FromBytes(reader);

            var slots = InventoryTreeToSlots(tree);
            // ИМЯ ИНВЕНТАРЯ В ПАКЕТЕ НЕ ПРИХОДИТ — мы обязаны построить его
            // так же, как сервер: из атрибута блока inventoryClassName.
            // Резать код блока нельзя: у подписанного сундука код
            // «labeledchest-west», а инвентарь сервер зовёт «chest-X, Y, Z».
            // Из-за этой подмены все наши пакеты перекладывания уходили на
            // несуществующий инвентарь и молча пропадали — при том, что
            // чтение работало (содержимое приходит по координатам)
            string invId = Actions.ContainerInventoryId(
                pos, world.GetInventoryClass(pos.X, pos.Y, pos.Z));
            inventories[invId] = slots;
            // ВОТ ЗДЕСЬ И ТОЛЬКО ЗДЕСЬ бот заглянул внутрь честно: сервер
            // прислал содержимое в ответ на открытие. Помечаем точку — по этой
            // отметке мир и решает, можно ли читать её содержимое из чанка
            lookedInside[pos] = DateTime.UtcNow;
            OnInventoryChanged?.Invoke(invId);
        }
        catch { /* неизвестный вариант — пропускаем */ }
    }

    /// <summary>Инвентарь-дерево (qslots + slots) → массив слотов.</summary>
    private SlotContent[] InventoryTreeToSlots(ITreeAttribute tree)
    {
        int qslots = tree.GetInt("qslots");
        var slotsTree = tree.GetTreeAttribute("slots");
        var slots = new SlotContent[qslots];
        for (int i = 0; i < qslots; i++)
        {
            var stack = slotsTree?.GetItemstack(i.ToString());
            if (stack == null || stack.StackSize <= 0)
            {
                slots[i] = new SlotContent(null, 0);
                continue;
            }
            string? code = (int)stack.Class == 0 ? world.BlockIdToCode(stack.Id) : world.ItemIdToCode(stack.Id);
            code ??= $"?{(int)stack.Class}:{stack.Id}";
            slots[i] = new SlotContent(code, stack.StackSize, RemainingDurability(code, stack.Attributes));
        }
        return slots;
    }

    /// <summary>
    /// Остаток прочности стака — повторяем CollectibleObject.GetRemainingDurability:
    /// (int)Attributes.GetDecimal("durability", GetMaxDurability(stack)). Максимум
    /// берём из реестра сервера (CollectibleObject.Durability), поэтому свежий,
    /// ни разу не битый инструмент показывает полную прочность, хотя атрибута
    /// "durability" у него ещё нет — сервер его дописывает только при первом уроне.
    /// Поведения предмета (CollectibleBehavior) могут менять и максимум, и остаток,
    /// но у нас нет ItemStack, чтобы их прокрутить — расхождение возможно только
    /// на модовых предметах с такими поведениями.
    /// </summary>
    private int RemainingDurability(string? code, ITreeAttribute? attributes)
    {
        int max = world.Durability(code);
        if (max <= 0)
            return 0; // предмет не изнашивается вовсе — прочности у него нет
        return (int)(attributes?.GetDecimal("durability", max) ?? max);
    }

    /// <summary>
    /// Атрибуты стака из пакета — это те же байты TreeAttribute, что читает
    /// клиент (Vintagestory.Common.StackConverter.FromPacket). Пустой массив
    /// значит «атрибутов нет», а не ошибку.
    /// </summary>
    private static ITreeAttribute? ParseStackAttributes(byte[]? data)
    {
        if (data == null || data.Length == 0)
            return null;
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            var tree = new TreeAttribute();
            tree.FromBytes(reader);
            return tree;
        }
        catch
        {
            return null; // неизвестный набор атрибутов не должен ронять разбор слота
        }
    }

    private void StoreInventory(Packet_InventoryContents inv)
    {
        if (inv.InventoryId == null)
            return;
        int n = inv.ItemstacksCount;
        var slots = new SlotContent[n];
        for (int i = 0; i < n; i++)
            slots[i] = ToSlot(inv.Itemstacks?[i]);

        // СУМКИ РАЗВОРАЧИВАЕМ САМИ. По сети приходят только четыре слота ПОД
        // сумки (InventoryPlayerBackpacks.CountForNetworkPacket = 4), а вещи
        // внутри лежат в атрибутах самих сумок: backpack → slots → «0», «1»…
        // Клиент игры разворачивает их точно так же (BagInventory), и без
        // этого бот не видел собственных запасов: положенное в сумку мясо для
        // него просто исчезало — «нет redmeat-raw» с полной сумкой мяса
        if (inv.InventoryId.StartsWith("backpack", StringComparison.Ordinal))
            slots = ExpandBags(slots, inv, inventories.TryGetValue(inv.InventoryId, out var was) ? was : null);

        inventories[inv.InventoryId] = slots;
        OnInventoryChanged?.Invoke(inv.InventoryId);
    }

    /// <summary>
    /// Дописать к слотам под сумки содержимое самих сумок — так же, как это
    /// делает клиент игры. Порядок как в игре: сумка за сумкой, слот за слотом.
    /// </summary>
    private SlotContent[] ExpandBags(SlotContent[] bagSlots, Packet_InventoryContents inv,
        SlotContent[]? known)
    {
        var all = new List<SlotContent>(bagSlots.Take(4));
        while (all.Count < 4)
            all.Add(new SlotContent(null, 0));

        for (int bag = 0; bag < Math.Min(4, bagSlots.Length); bag++)
        {
            if (bagSlots[bag].Code is not { } bagCode)
                continue;
            int capacity = world.BagSlotsByCode(bagCode);
            if (capacity <= 0)
                continue;

            int start = all.Count;
            var contents = ParseStackAttributes(inv.Itemstacks?[bag]?.Attributes)?
                .GetTreeAttribute("backpack")?.GetTreeAttribute("slots");

            // «АТРИБУТОВ НЕТ» — ЭТО НЕ «СУМКА ПУСТА». Пакет со всем инвентарём
            // приходит и без вложенного содержимого сумок (так бывает при
            // входе в мир), и записать туда пустоту значит соврать себе:
            // бот переставал видеть собственные запасы. Живьём это кончилось
            // смертью от голода с полной сумкой хлеба. Нет данных — сохраняем
            // то, что знали раньше
            if (contents == null)
            {
                for (int i = 0; i < capacity; i++)
                    all.Add(known is { } old && start + i < old.Length
                        ? old[start + i]
                        : new SlotContent(null, 0));
                continue;
            }

            for (int i = 0; i < capacity; i++)
                all.Add(contents[BagSlotKey(i)]?.GetValue() is Vintagestory.API.Common.ItemStack st &&
                        st.StackSize > 0
                    ? new SlotContent(StackCode(st), st.StackSize)
                    : new SlotContent(null, 0));
        }
        return all.ToArray();
    }

    /// <summary>
    /// ИМЯ КЛЮЧА, ПОД КОТОРЫМ ИГРА ХРАНИТ ВЕЩЬ В СУМКЕ, — «slot-N», А НЕ «N».
    ///
    /// ЗДЕСЬ ЖИЛА ТРЕТЬЯ ГОЛОДНАЯ СМЕРТЬ. Содержимое сумки лежит в атрибутах
    /// самого предмета-сумки: backpack → slots → …, и ключи туда пишет
    /// CollectibleBehaviorHeldBag.Store — дословно «"slot-" + slot.SlotIndex».
    /// Читает их оттуда же GetOrCreateSlots, разбирая ключ как
    /// key.Split("-")[1], то есть голое «0» игра не поняла бы вовсе.
    ///
    /// Мы же спрашивали «0», «1», «2»… — и КАЖДЫЙ раз получали пустоту. Пока
    /// сервер слал точечные обновления (пакеты 31 и 32 приходят с настоящим
    /// НОМЕРОМ СКВОЗНОГО слота и настоящим стаком), бот видел свои запасы
    /// правильно. Но стоило прийти полному снимку инвентаря (пакет 30) — а он
    /// приходит в том числе В ОТВЕТ НА ОТВЕРГНУТЫЙ ПЕРЕНОС, — как все места
    /// внутри сумок разом становились «пустыми».
    ///
    /// Живой случай заказчика, слово в слово: «РЮКЗАК МЕСТ 36, СВОБОДНО 32»
    /// при четырёх набитых сумках; бот выносит торф в «пустые» места 4, 5, 6, 7
    /// — сервер отвергает все четыре (они заняты) и в ответ шлёт снимок,
    /// который стирает и хлеб в 21-м месте. Дальше обмен честно докладывает
    /// «bread-rice-perfect уехал из слота 21» — вещь никуда не уезжала, это мы
    /// её потеряли из виду. Бот умер от голода, стоя с хлебом в сумке.
    /// </summary>
    public static string BagSlotKey(int slotIndex) => "slot-" + slotIndex;

    /// <summary>Код предмета из готового стака игры (внутри сумки они такие).</summary>
    private string? StackCode(Vintagestory.API.Common.ItemStack stack) =>
        (int)stack.Class == 0 ? world.BlockIdToCode(stack.Id) : world.ItemIdToCode(stack.Id);

    private void UpdateSlot(string inventoryId, int slotId, Packet_ItemStack? stack)
    {
        if (!inventories.TryGetValue(inventoryId, out var slots) || slotId < 0)
            return;

        // ИНВЕНТАРЬ УМЕЕТ РАСТИ. Надетая сумка добавляет свои слоты в тот же
        // «backpack»: сперва там только 4 слота ПОД сумки, а с прочным рюкзаком
        // становится 12. Раньше обновления слотов сверх известной длины молча
        // выбрасывались — и бот не видел собственного грузового места,
        // считая, что возить некуда
        if (slotId >= slots.Length)
        {
            var grown = new SlotContent[slotId + 1];
            Array.Copy(slots, grown, slots.Length);
            for (int i = slots.Length; i < grown.Length; i++)
                grown[i] = new SlotContent(null, 0);
            slots = grown;
            inventories[inventoryId] = slots;
        }

        slots[slotId] = ToSlot(stack);

        // ОБНОВИЛАСЬ САМА СУМКА — значит изменилось и то, что внутри неё:
        // вещи лежат в атрибутах сумки, и сервер шлёт их именно так, одним
        // обновлением слота 0..3. Не развернув их заново, бот держит в голове
        // прошлое содержимое: живьём он считал сумки пустыми и клал жареное
        // мясо в занятый слот, а сервер это молча отвергал
        if (slotId < 4 && inventoryId.StartsWith("backpack", StringComparison.Ordinal))
        {
            slots = RefillBagContents(slots, slotId, stack);
            inventories[inventoryId] = slots;
        }

        OnInventoryChanged?.Invoke(inventoryId);
    }

    /// <summary>
    /// Переписать участок слотов, принадлежащий одной сумке, из её атрибутов.
    /// Порядок участков тот же, что в игре: сумка за сумкой, слот за слотом.
    /// </summary>
    private SlotContent[] RefillBagContents(SlotContent[] slots, int bagIndex, Packet_ItemStack? bagStack)
    {
        // Надетая сумка не только меняет содержимое — она УДЛИНЯЕТ инвентарь.
        // Считаем полную длину заново: четыре слота под сумки плюс их места
        int total = 4;
        for (int b = 0; b < Math.Min(4, slots.Length); b++)
            total += slots[b].Code is { } c ? world.BagSlotsByCode(c) : 0;
        if (total > slots.Length)
        {
            var grown = new SlotContent[total];
            Array.Copy(slots, grown, slots.Length);
            for (int i = slots.Length; i < total; i++)
                grown[i] = new SlotContent(null, 0);
            slots = grown;
        }

        int start = 4;
        for (int b = 0; b < bagIndex; b++)
            start += slots[b].Code is { } c ? world.BagSlotsByCode(c) : 0;

        int capacity = slots[bagIndex].Code is { } code ? world.BagSlotsByCode(code) : 0;
        var contents = ParseStackAttributes(bagStack?.Attributes)?
            .GetTreeAttribute("backpack")?.GetTreeAttribute("slots");
        if (contents == null)
            return slots;   // нет данных — прежнее знание вернее пустоты

        for (int i = 0; i < capacity && start + i < slots.Length; i++)
            slots[start + i] =
                contents[BagSlotKey(i)]?.GetValue() is Vintagestory.API.Common.ItemStack st && st.StackSize > 0
                    ? new SlotContent(StackCode(st), st.StackSize)
                    : new SlotContent(null, 0);
        return slots;
    }

    private SlotContent ToSlot(Packet_ItemStack? stack)
    {
        if (stack == null || stack.StackSize <= 0)
            return new SlotContent(null, 0);

        // ItemClass: 0 = блок, 1 = предмет
        string? code = stack.ItemClass == 0
            ? world.BlockIdToCode(stack.ItemId)
            : world.ItemIdToCode(stack.ItemId);
        code ??= $"?{stack.ItemClass}:{stack.ItemId}";
        return new SlotContent(code, stack.StackSize,
            RemainingDurability(code, ParseStackAttributes(stack.Attributes)));
    }
}
