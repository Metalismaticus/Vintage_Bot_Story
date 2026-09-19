using Vintagestory.API.Datastructures;

using DamageKind = Vintagestory.API.Common.EnumDamageType;
using DamageSourceKind = Vintagestory.API.Common.EnumDamageSource;

namespace VsBotKit;

/// <summary>
/// ПРАВИЛА МИРА ПРО СМЕРТЬ — те самые, по которым живёт сервер.
///
/// Сервер присылает НАСТРОЙКИ МИРА целиком, деревом атрибутов: в пакете 1
/// (ServerIdentification.WorldConfiguration) и ещё раз в пакете 21
/// (WorldMetaData.WorldConfiguration). Клиент их читает и живёт по ним ровно
/// так же — например, окно смерти считает оставшиеся жизни как
/// <c>Config.GetString("playerlives", "-1") − Deaths</c>
/// (GuiDialogDead.OnGameTick). То есть это не подглядывание, а то же знание,
/// что у живого игрока.
///
/// Зачем оно боту. Раньше срок жизни выпавших вещей был у нас ДОГАДКОЙ на 600
/// секунд с комментарием «клиенту это число не приходит». Приходит: ключ
/// <c>droppedItemsTimer</c>, и мир вправе поставить 300 или 3600. Бот, честно
/// решивший «прошло 620 с — вещей уже нет», на часовом сроке разворачивался
/// в двух шагах от своей кирки.
///
/// Ключи не выдуманы: они объявлены в самом моде выживания
/// (VSSurvivalMod, ModInfo.WorldConfig, категория «spawnndeath») и читаются
/// игрой под этими же именами.
/// </summary>
public sealed class DeathRules
{
    private ITreeAttribute? config;

    public DeathRules(BotClient bot)
    {
        bot.OnPacket += Handle;
        // НОВОЕ СОЕДИНЕНИЕ — ВОЗМОЖНО, ДРУГОЙ МИР. Панель разрешает сменить
        // сервер на ходу (ControlPanel → BotClient.UseServer), а правила смерти
        // читались ровно один раз за всю жизнь программы. Живой случай: с
        // сервера, где вещи остаются в сумках и срок им час, бота переводили на
        // сервер, где вещи падают и лежат пять минут, — и бот отказывался идти
        // за ними («этот мир вещи не роняет»), пока они протухали.
        // Сервер присылает настройки заново сразу после входа (пакеты 1 и 21),
        // так что забыть их безопасно, а помнить — нет
        bot.OnSessionReset += Reset;
    }

    private void Reset()
    {
        if (config == null)
            return;
        config = null;
        OnLog?.Invoke("новое соединение — правила мира про смерть забыты; " +
                      "до их прихода живу по значениям по умолчанию самой игры");
    }

    /// <summary>Что случилось с настройками мира — словами.</summary>
    public event Action<string>? OnLog;

    /// <summary>Настройки мира пришли и разобрались.</summary>
    public event Action? OnKnown;

    /// <summary>
    /// Настройки мира у нас есть. Пока нет — все ответы ниже это ЗНАЧЕНИЯ ПО
    /// УМОЛЧАНИЮ САМОЙ ИГРЫ, а не знание о конкретном сервере.
    /// </summary>
    public bool Known => config != null;

    private void Handle(Packet_Server p)
    {
        if (config != null)
            return;
        byte[]? bytes = p.Id switch
        {
            1 => p.Identification?.WorldConfiguration,
            21 => p.WorldMetaData?.WorldConfiguration,
            _ => null
        };
        if (bytes is not { Length: > 0 })
            return;

        try
        {
            var tree = new TreeAttribute();
            tree.FromBytes(bytes);
            config = tree;
        }
        catch (Exception e)
        {
            // Молчать нельзя: дальше бот будет жить по умолчаниям игры и может
            // разойтись с сервером на час срока вещей или на число жизней
            OnLog?.Invoke($"настройки мира не разобрались ({e.Message}) — " +
                          "живу по значениям по умолчанию самой игры");
            return;
        }
        OnLog?.Invoke($"правила мира про смерть: {this}");
        OnKnown?.Invoke();
    }

    /// <summary>Прочитать любую настройку мира как число (строки сервер шлёт строками).</summary>
    public int Int(string key, int fallback) => config?.GetAsInt(key, fallback) ?? fallback;

    /// <summary>Прочитать любую настройку мира как строку.</summary>
    public string? Text(string key) => config?.GetAsString(key);

    /// <summary>
    /// Сколько раз игроку вообще позволено умереть в этом мире. −1 — без
    /// предела (значение по умолчанию и самое частое). Ключ <c>playerlives</c>.
    /// </summary>
    public int PlayerLives => Int("playerlives", -1);

    /// <summary>Предел возрождений в мире есть (иначе «жизней» просто нет).</summary>
    public bool LivesLimited => PlayerLives >= 0;

    /// <summary>
    /// Мир НЕ роняет вещи при смерти (<c>deathPunishment = keep</c>). Тогда идти
    /// на место смерти незачем: там ничего не лежит.
    /// </summary>
    public bool KeepInventory =>
        string.Equals(Text("deathPunishment"), "keep", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Сколько живут выпавшие из игрока вещи, секунд. Ключ <c>droppedItemsTimer</c>
    /// подменяет в player.json атрибут <c>droppedItemsOnDeathTimer</c>
    /// (assets/survival/patches/keepinventory.json), значение по умолчанию — 600.
    /// </summary>
    public double DroppedItemsSeconds => Int("droppedItemsTimer", 600);

    /// <summary>
    /// Сколько ВОЗРОЖДЕНИЙ переживает своя точка возрождения, поставленная
    /// временной шестерёнкой. −1 — вечная, 0 — свою точку ставить нельзя вовсе.
    /// Ключ <c>temporalGearRespawnUses</c>; в наборах мира встречается и «20»,
    /// и «3», и «0», так что угадывать нечего — надо спрашивать.
    /// </summary>
    public int GearRespawnUses => Int("temporalGearRespawnUses", -1);

    /// <summary>Своя точка возрождения в этом мире разрешена.</summary>
    public bool OwnSpawnAllowed => GearRespawnUses != 0;

    /// <summary>
    /// Сколько ИГРОВЫХ часов лежащего игрока ещё можно поднять чужими руками
    /// (<c>playerRevivableHourAmount</c>, по умолчанию полчаса). Боту это
    /// нужно, чтобы не звать помощь, когда звать уже поздно.
    /// </summary>
    public double RevivableHours => config?.GetDecimal("playerRevivableHourAmount", 0.5) ?? 0.5;

    public override string ToString() =>
        (Known ? "" : "(настройки мира ещё не пришли, значения по умолчанию игры) ") +
        $"жизней: {(LivesLimited ? PlayerLives.ToString() : "без предела")}; " +
        $"вещи при смерти: {(KeepInventory ? "остаются в сумках" : "падают на землю")}; " +
        $"лежат {DroppedItemsSeconds:0} с; " +
        $"своя точка возрождения: {(GearRespawnUses < 0 ? "вечная" : GearRespawnUses == 0 ? "запрещена" : $"на {GearRespawnUses} возрождений")}";
}

/// <summary>
/// ОТ ЧЕГО УМЕР — так, как это записал сам сервер.
///
/// При смерти игры пишет в синхронизируемые атрибуты сущности
/// (Entity.Die): <c>deathTotalHours</c> — игровое время смерти,
/// <c>deathReason</c> — <see cref="DamageSourceKind"/>, <c>deathDamageType</c> —
/// <see cref="DamageKind"/>, <c>deathByEntity</c> — код убийцы,
/// <c>deathByPlayer</c> — имя игрока-убийцы. Всё это синхронизируется, то есть
/// доступно и живому игроку (его клиент этим же показывает «Killed by Player»).
///
/// Разбирать чат нельзя: сообщение о смерти сервер собирает СЛУЧАЙНЫМ выбором
/// из десятка локализованных строк (ServerSystemEntitySimulation.GetDeathMessage),
/// а на людном сервере вовсе не шлёт.
/// </summary>
/// <param name="Source">Откуда пришла смерть (падение, существо, голод...).</param>
/// <param name="Damage">Каким уроном добило.</param>
/// <param name="ByEntity">Код существа-убийцы или null.</param>
/// <param name="ByPlayer">Имя игрока-убийцы или null.</param>
public sealed record DeathCause(
    DamageSourceKind Source, DamageKind Damage, string? ByEntity, string? ByPlayer)
{
    /// <summary>
    /// Своими словами. Имена берём у ИГРОВЫХ перечислений: появится в игре
    /// новый вид урона — он назовётся своим именем, а не пропадёт молча.
    /// </summary>
    public override string ToString()
    {
        string who = ByPlayer is { Length: > 0 } p ? $"игрок {p}"
            : ByEntity is { Length: > 0 } e ? e
            : "";
        string what = Source switch
        {
            DamageSourceKind.Fall => "падение",
            DamageSourceKind.Drown => "утонул",
            DamageSourceKind.Block => "блок",
            DamageSourceKind.Explosion => "взрыв",
            DamageSourceKind.Void => "провалился за край мира",
            DamageSourceKind.Suicide => "команда /kill",
            DamageSourceKind.Weather => "погода",
            DamageSourceKind.Bleed => "кровотечение",
            DamageSourceKind.Machine => "механизм",
            DamageSourceKind.Internal => Damage switch
            {
                DamageKind.Hunger => "голод",
                DamageKind.Frost => "холод",
                DamageKind.Heat => "жара",
                DamageKind.Poison => "яд",
                DamageKind.Suffocation => "задохнулся",
                _ => $"своё тело ({Damage})"
            },
            DamageSourceKind.Entity or DamageSourceKind.Player =>
                who.Length > 0 ? who : Source.ToString(),
            DamageSourceKind.Unknown => "неизвестно",
            _ => Source.ToString()
        };
        return who.Length > 0 && !what.Contains(who) ? $"{what} ({who})" : what;
    }
}

/// <summary>
/// ОДНА СМЕРТЬ в журнале: когда, где, от чего и сколько с собой унесло.
///
/// Зачем журнал, а не один счётчик. Заказчик спрашивает «сколько возрождений
/// осталось», но ПОЛЕЗЕН ответ на другой вопрос: почему бот умирает. Три
/// смерти подряд от одного дрифтера в одной штольне — это не «осталось 17
/// жизней», это «туда ходить нельзя».
/// </summary>
/// <param name="Number">Номер смерти за эту сессию, с единицы.</param>
/// <param name="At">Настоящее время смерти.</param>
/// <param name="GameHours">Игровые часы календаря (null — часы не были готовы).</param>
/// <param name="Where">Где выпали вещи (null — места не знаем).</param>
/// <param name="Cause">От чего умер (null — сервер не сказал).</param>
/// <param name="LostStacks">Сколько стопок было в сумках перед смертью.</param>
/// <param name="LivesLeft">
/// Сколько возрождений осталось ПО СЛОВАМ СЕРВЕРА (пакет 45), −1 — без предела,
/// null — сервер про это не сказал.
/// </param>
public sealed record DeathRecord(
    int Number, DateTime At, double? GameHours, BlockPos? Where,
    DeathCause? Cause, int LostStacks, int? LivesLeft)
{
    public override string ToString() =>
        // Место смерти — ИГРОВОЕ: за вещами по этому адресу идёт человек, а не
        // бот, и «512062, 112, 512252» он на своём экране не найдёт. Скобки
        // здесь не нужны — их ставит BlockPos.ToString, а тут число идёт
        // строкой отчёта, поэтому берётся SayBare из того же правила
        $"смерть №{Number}: {(Where is { } w ? WorldOrigin.SayBare(w.X, w.Y, w.Z) : "место неизвестно")}, " +
        $"от чего: {(Cause is { } c ? c.ToString() : "сервер не сказал")}, " +
        $"в сумках было стопок: {LostStacks}" +
        (LivesLeft is { } left && left >= 0 ? $", осталось возрождений: {left}" : "");
}

/// <summary>Чем кончилась попытка вернуться за вещами.</summary>
public enum RecoveryOutcome
{
    /// <summary>Не помним ни одной смерти — идти некуда.</summary>
    NoGrave,
    /// <summary>До места смерти не дошли (не хватило времени, дороги нет).</summary>
    NotReached,
    /// <summary>Дошли, но вещей на земле нет (истёк срок, кто-то забрал, мир «keep inventory»).</summary>
    NothingFound,
    /// <summary>Часть подобрана, часть осталась лежать.</summary>
    Partial,
    /// <summary>На земле не осталось ничего — всё подобрано.</summary>
    Recovered
}

/// <summary>
/// Итог похода за вещами. Picked — сколько предметных сущностей исчезло
/// у нас под ногами (значит, подобраны), Left — сколько ещё лежит.
/// </summary>
public sealed record RecoveryResult(RecoveryOutcome Outcome, int Picked, int Left, string? Message = null)
{
    public bool Success => (Outcome is RecoveryOutcome.Recovered or RecoveryOutcome.Partial) && Picked > 0;

    public override string ToString() =>
        Message ?? $"{Outcome}: подобрано {Picked}, осталось {Left}";
}

/// <summary>
/// СМЕРТИ И ВОЗВРАТ ЗА ВЕЩАМИ — МЕХАНИЗМ: помнит, где, когда и от чего умер,
/// сколько возрождений осталось по словам сервера, и умеет дойти до могилы и
/// собрать выпавшее. Идти ли вообще, как далеко, сколько раз и что считать
/// ценным — решает роль (см. <see cref="BehaviorRecoverLoot"/>).
///
/// Почему позиция запоминается заранее: в момент возрождения сервер двигает
/// нас к точке спавна, и «текущая» позиция уже не та, где выпали вещи.
/// Поэтому Observe() (его зовёт тик способности) держит последнее известное
/// положение, а на смерти мы берём именно его.
///
/// Заодно Observe() запоминает содержимое своих сумок — по нему роль может
/// оценить, стоит ли поход того (потерянный меч или потерянная палка), — и
/// дописывает в журнал причину смерти, когда сервер её пришлёт.
///
/// ЧЕГО ЗДЕСЬ НЕТ НАРОЧНО: своего счётчика оставшихся жизней. Остаток считает
/// сервер (playerlives − смерти игрока) и присылает его в пакете 45; свой
/// счётчик разошёлся бы с сервером на первой же смерти, случившейся до входа
/// бота в мир, и «осталось 3» превратилось бы во враньё.
/// </summary>
public class DeathRecovery
{
    private readonly BotContext ctx;

    private BlockPos? lastKnownPos;
    private DateTime lastKnownAt = DateTime.MinValue;
    private SlotContent[] lastKnownItems = [];
    private bool itemsDirty = true;

    // Смерть приходит с двух сторон: пакетом 45 и «здоровье на нуле» (см.
    // BehaviorRespawnWhenDead — пакет можно и не получить). Записать её надо
    // РОВНО ОДИН РАЗ, иначе счётчик смертей врёт вдвое
    private bool deathRecorded;
    private DeathRecord? causePending;
    private DateTime causeUntil = DateTime.MinValue;
    private double? hoursBeforeDeath;

    private readonly List<DeathRecord> log = [];

    public DeathRecovery(BotContext ctx)
    {
        this.ctx = ctx;
        Rules = new DeathRules(ctx.Bot);
        Rules.OnLog += m => OnLog?.Invoke(m);
        // Смерть ловим СВОИМ разбором пакета 45, а не событием SelfState.OnDeath.
        // Причина в порядке: SelfState подписан раньше нас и поднимает OnDeath
        // из того же пакета — то есть ДО того, как мы прочтём оттуда остаток
        // возрождений. Запись о смерти уносила бы в журнал ПРОШЛЫЙ остаток и
        // показывала «осталось 3» на смерти, после которой осталось 2
        ctx.Self.OnInventoryChanged += _ => itemsDirty = true;
        ctx.Bot.OnPacket += HandlePacket;

        // ЧИСЛА ПРОШЛОГО МИРА В НОВОМ — ВРАНЬЁ. «Осталось возрождений: 2»
        // сервер сказал про тот мир, а этот про наш остаток ещё не говорил
        // вовсе. Могилу при этом НЕ забываем нарочно: обрыв связи и возврат на
        // тот же сервер — самый частый случай, и вещи там всё ещё лежат
        ctx.Bot.OnSessionReset += () =>
        {
            LivesLeft = null;
            DeathsOnServer = null;
        };
    }

    public event Action<string>? OnLog;

    /// <summary>Правила мира про смерть — то, что сервер сам про них сказал.</summary>
    public DeathRules Rules { get; }

    /// <summary>
    /// СКОЛЬКО ВОЗРОЖДЕНИЙ ОСТАЛОСЬ ПО СЛОВАМ СЕРВЕРА. −1 — предела нет,
    /// null — сервер ещё ни разу об этом не говорил (бот в этом мире не умирал).
    ///
    /// Число приходит в пакете 45 (PlayerDeath.LivesLeft) и считается на
    /// сервере как <c>playerlives − смерти игрока</c>
    /// (ServerSystemEntitySimulation.SendPlayerEntityDeaths). Своего счётчика
    /// мы не заводим НАРОЧНО: «осталось 3» имеет право говорить только тот,
    /// кто это решает.
    /// </summary>
    public int? LivesLeft { get; private set; }

    /// <summary>
    /// Сколько раз бот умирал В ЭТОМ МИРЕ по счёту сервера — приходит в своём
    /// PlayerData(41) полем Deaths и переживает перезапуск бота, в отличие от
    /// <see cref="Deaths"/>. null — пакет ещё не приходил.
    /// </summary>
    public int? DeathsOnServer { get; private set; }

    /// <summary>
    /// Возрождений больше нет: сервер отказывает во вставании
    /// («Cannot revive! All lives used up.» в ServerSystemEntitySimulation
    /// .HandleSpecialKey). Проверять ОБЯЗАТЕЛЬНО перед тем, как долбить кнопку
    /// возрождения: иначе бот будет вечно слать запрос, который сервер молча
    /// выбрасывает, и в журнале это выглядит как «встаю» каждые четыре секунды.
    /// </summary>
    public bool NoLivesLeft => LivesLeft == 0;

    /// <summary>Журнал смертей за сессию: где, когда, от чего, сколько унесло.</summary>
    public IReadOnlyList<DeathRecord> Log => log;

    /// <summary>Сколько записей журнала держать: за сутки работы их набирается много.</summary>
    public int LogLimit { get; set; } = 50;

    /// <summary>Записана новая смерть.</summary>
    public event Action<DeathRecord>? OnDeathLogged;

    /// <summary>Сервер сказал, сколько возрождений осталось (−1 — без предела).</summary>
    public event Action<int>? OnLivesLeftChanged;

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            // Пакет 45 сервер РАССЫЛАЕТ ВСЕМ — про чужие смерти тоже, поэтому
            // сверяем номер клиента со своим
            case 45 when p.PlayerDeath is { } death && death.ClientId == ctx.Self.OwnClientId:
                if (LivesLeft != death.LivesLeft)
                {
                    LivesLeft = death.LivesLeft;
                    OnLivesLeftChanged?.Invoke(death.LivesLeft);
                    OnLog?.Invoke(death.LivesLeft < 0
                        ? "предела возрождений в этом мире нет"
                        : death.LivesLeft == 0
                            ? "ВОЗРОЖДЕНИЙ БОЛЬШЕ НЕТ — сервер меня больше не поднимет"
                            : $"осталось возрождений: {death.LivesLeft}");
                }
                // Только теперь — запись о смерти: в неё уйдёт свежий остаток
                NoticeDeath("сервер прислал извещение о смерти");
                break;

            // Пакет 41 приходит и про других игроков — сверяем uid
            case 41 when p.PlayerData is { } pd && pd.PlayerUID == ctx.Bot.PlayerUid:
                DeathsOnServer = pd.Deaths;
                break;
        }
    }

    /// <summary>Запомнили место смерти (позиция, что было в сумках).</summary>
    public event Action<BlockPos, IReadOnlyList<SlotContent>>? OnDeathRemembered;

    /// <summary>Где выпали вещи (null — идти некуда).</summary>
    public BlockPos? Grave { get; private set; }

    /// <summary>Что лежало в сумках перед смертью (последний снимок до неё).</summary>
    public IReadOnlyList<SlotContent> LostItems { get; private set; } = [];

    /// <summary>Реальное время смерти — по нему считается срок жизни выпавших вещей.</summary>
    public DateTime DeathUtc { get; private set; }

    /// <summary>Игровое время смерти (часы календаря), если часы были готовы.</summary>
    public double? DeathTotalHours { get; private set; }

    /// <summary>Сколько раз пробовали сходить за этой могилой.</summary>
    public int Attempts { get; private set; }

    /// <summary>Сколько раз бот умирал за сессию.</summary>
    public int Deaths { get; private set; }

    /// <summary>Есть куда идти.</summary>
    public bool HasGrave => Grave != null;

    /// <summary>Сколько реальных секунд прошло со смерти.</summary>
    public double SecondsSinceDeath =>
        Grave == null ? 0 : (DateTime.UtcNow - DeathUtc).TotalSeconds;

    /// <summary>Сколько ИГРОВЫХ часов прошло со смерти (null — время смерти неизвестно).</summary>
    public double? GameHoursSinceDeath =>
        DeathTotalHours is { } h && ctx.Clock.Ready ? ctx.Clock.TotalHours - h : null;

    private double? dropLifetime;

    /// <summary>
    /// Сколько живут вещи, выпавшие из игрока при смерти. Основа — атрибут
    /// droppedItemsOnDeathTimer из assets/game/entities/humanoid/player.json
    /// (он же GlobalConstants.TimeToDespawnPlayerInventoryDrops = 600);
    /// InventoryPlayerBackpacks.DropAll передаёт его в спавн предметной
    /// сущности, и он перебивает minSeconds из item.json.
    ///
    /// ЧИСЛО СПРАШИВАЕМ У МИРА, А НЕ ВЫДУМЫВАЕМ: настройка мира
    /// <c>droppedItemsTimer</c> подменяет этот атрибут
    /// (assets/survival/patches/keepinventory.json) и бывает и 300, и 3600 —
    /// а настройки мира сервер клиенту ПРИСЫЛАЕТ (см. <see cref="DeathRules"/>).
    /// Пока они не пришли, отвечаем умолчанием самой игры.
    ///
    /// Оценкой это всё равно остаётся: сам счётчик deathTime живёт в
    /// НЕсинхронизируемых Attributes предметной сущности, и тикает он только
    /// пока чанк с вещами симулируется. Поэтому роль вправе поставить своё
    /// число — записанное сюда значение перебивает настройку мира.
    /// </summary>
    public double DropLifetimeSeconds
    {
        get => dropLifetime ?? Rules.DroppedItemsSeconds;
        set => dropLifetime = value;
    }

    /// <summary>
    /// Сколько секунд осталось до исчезновения вещей — по нашей оценке.
    /// Оценка ПЕССИМИСТИЧНАЯ: сервер тикает счётчик только пока чанк с вещами
    /// симулируется, поэтому на деле вещи могут прожить дольше.
    /// </summary>
    public double SecondsLeft => Math.Max(0, DropLifetimeSeconds - SecondsSinceDeath);

    /// <summary>Скорее всего, вещей уже нет (срок вышел). Проверяется только приходом на место.</summary>
    public bool LikelyDespawned => HasGrave && SecondsLeft <= 0;

    /// <summary>Расстояние до могилы (null — не знаем своей позиции или могилы).</summary>
    public double? Distance
    {
        get
        {
            if (Grave is not { } g || ctx.Self.Position is not { } me)
                return null;
            double dx = g.X + 0.5 - me.X, dy = g.Y - me.Y, dz = g.Z + 0.5 - me.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    /// <summary>В каком радиусе от могилы искать и собирать вещи (их раскидывает при выпадении).</summary>
    public double CollectRadius { get; set; } = 8;

    /// <summary>Сколько секунд ходить по вещам за один заход.</summary>
    public double CollectSeconds { get; set; } = 30;

    /// <summary>Позиция считается «свежей» столько секунд — иначе на смерти возьмём текущую.</summary>
    public double FreshPositionSeconds { get; set; } = 5;

    /// <summary>
    /// Запомнить, где мы сейчас и что у нас в сумках. Вызывать регулярно
    /// (способность делает это каждый тик) — иначе к моменту смерти
    /// последнее известное место окажется старым.
    /// </summary>
    public void Observe()
    {
        // Причину смерти сервер дописывает в атрибуты СУЩНОСТИ, и приходит она
        // отдельным пакетом — то есть ПОЗЖЕ извещения о смерти, а иногда и
        // позже возрождения. Ловим её здесь, ДО выхода по «мёртв»: возрождение
        // эти атрибуты не стирает (Entity.Revive их не трогает), так что
        // прочитать их можно и на ногах
        CatchDeathCause();

        if (ctx.Self.IsDead || ctx.Self.Position is not { } p)
            return;

        // Жив — значит следующая смерть будет новой записью в журнале
        if ((ctx.Self.Health ?? 1) > 0)
        {
            deathRecorded = false;
            hoursBeforeDeath = DeathHoursAttribute();
        }

        lastKnownPos = new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z));
        lastKnownAt = DateTime.UtcNow;

        if (!itemsDirty)
            return;
        itemsDirty = false;
        // Броня и одежда («character») в снимок входят: при смерти они падают
        // вместе с остальным. Не берём только слот под курсором («mouse») —
        // это не вещи, а промежуточное состояние перекладывания
        lastKnownItems = ctx.Self.OwnInventories
            .Where(kv => !kv.Key.StartsWith("mouse"))
            .SelectMany(kv => kv.Value)
            .Where(s => !s.IsEmpty)
            .ToArray();
    }

    /// <summary>Забыть могилу: вещи забрали, протухли или роль решила не ходить.</summary>
    public void Forget()
    {
        Grave = null;
        Attempts = 0;
    }

    /// <summary>
    /// Задать место смерти вручную — например, если координаты подсказал игрок
    /// в чате или бот умер до того, как способность успела что-то запомнить.
    /// </summary>
    public void RememberDeathAt(BlockPos pos)
    {
        Grave = pos;
        Attempts = 0;
        DeathUtc = DateTime.UtcNow;
        DeathTotalHours = ctx.Clock.Ready ? ctx.Clock.TotalHours : null;
        OnDeathRemembered?.Invoke(pos, LostItems);
    }

    /// <summary>
    /// «Бот умер» — с любой стороны, откуда это стало известно.
    ///
    /// Сторон две, и обе нужны: извещение сервера (пакет 45) и здоровье на
    /// нуле (см. <see cref="BehaviorRespawnWhenDead"/> — извещение приходит
    /// РАЗ, и переподключавшийся бот его теряет). Записать смерть надо ровно
    /// один раз, иначе журнал и счётчик врут вдвое, — за это отвечает флаг
    /// внутри, а не тот, кто зовёт.
    /// </summary>
    public void NoticeDeath(string how)
    {
        if (deathRecorded)
            return;
        deathRecorded = true;
        Deaths++;

        // Помним ТОЛЬКО последнюю могилу: пока бот шёл к старой и умер снова,
        // прежние вещи почти наверняка уже дотикали свой срок
        bool fresh = lastKnownPos != null &&
                     (DateTime.UtcNow - lastKnownAt).TotalSeconds <= FreshPositionSeconds;
        var pos = fresh ? lastKnownPos : CurrentBlockPos();

        var record = new DeathRecord(
            Deaths, DateTime.UtcNow, ctx.Clock.Ready ? ctx.Clock.TotalHours : null,
            pos, null, lastKnownItems.Length, LivesLeft);
        log.Add(record);
        while (log.Count > Math.Max(1, LogLimit))
            log.RemoveAt(0);
        // Причина придёт следующими пакетами — дописать её в запись
        causePending = record;
        causeUntil = DateTime.UtcNow.AddSeconds(CauseWaitSeconds);
        OnDeathLogged?.Invoke(record);

        if (pos is not { } grave)
        {
            // Молчаливого «стою» тут быть не должно: человек обязан понять,
            // почему бот не пошёл за вещами
            OnLog?.Invoke($"умер ({how}), но где — неизвестно: возвращаться некуда");
            return;
        }

        LostItems = lastKnownItems;
        Grave = grave;
        Attempts = 0;
        DeathUtc = DateTime.UtcNow;
        DeathTotalHours = ctx.Clock.Ready ? ctx.Clock.TotalHours : null;
        OnLog?.Invoke($"умер на {WorldOrigin.SayBare(grave.X, grave.Y, grave.Z)} ({how}); " +
                      $"в сумках было {LostItems.Count} стопок" +
                      (Rules.KeepInventory
                          ? " — но этот мир вещи не роняет, идти за ними некуда"
                          : ""));
        OnDeathRemembered?.Invoke(grave, LostItems);
    }

    /// <summary>
    /// Сколько секунд ЖИЗНИ ждать от сервера причину смерти, прежде чем честно
    /// сказать «не сказал». Пока бот лежит, отсчёт не идёт: возрождение может
    /// затянуться на минуту (сервер поднимает игрока только после загрузки
    /// чанков), и торопиться с приговором нечестно.
    /// </summary>
    public double CauseWaitSeconds { get; set; } = 60;

    private double? DeathHoursAttribute()
    {
        var self = ctx.Entities.Self;
        if (self == null || !self.WatchedAttributes.HasAttribute("deathTotalHours"))
            return null;
        return self.WatchedAttributes.GetDouble("deathTotalHours");
    }

    /// <summary>
    /// Дописать в последнюю запись журнала причину смерти, как её записал
    /// сервер. Признак «это про НОВУЮ смерть» — изменившийся deathTotalHours:
    /// остальные атрибуты (deathByEntity и прочие) сервер после возрождения не
    /// стирает, и без этой сверки бот сообщал бы про позапрошлого дрифтера.
    ///
    /// Той же несмываемости обязано и правило <see cref="Killer"/>: сверка по
    /// часам говорит «смерть новая», но НЕ говорит, что новы все атрибуты —
    /// про каждого убийцу отдельно решает оно.
    /// </summary>
    private void CatchDeathCause()
    {
        if (causePending is not { } record)
            return;

        var self = ctx.Entities.Self;
        double? hours = DeathHoursAttribute();
        if (self != null && hours is { } h && h != hoursBeforeDeath)
        {
            var tree = self.WatchedAttributes;
            var source = (DamageSourceKind)tree.GetInt("deathReason", (int)DamageSourceKind.Unknown);
            var damage = (DamageKind)tree.GetInt("deathDamageType", (int)DamageKind.BluntAttack);
            var (byEntity, byPlayer) = Killer(source,
                tree.GetString("deathByEntity"), tree.GetString("deathByPlayer"));
            var cause = new DeathCause(source, damage, byEntity, byPlayer);
            Replace(record with { Cause = cause });
            causePending = null;
            hoursBeforeDeath = h;
            OnLog?.Invoke($"причина смерти по словам сервера: {cause}");
            return;
        }

        // Пока бот лежит, срок не течёт: сервер поднимает игрока только после
        // загрузки чанков, и объявить «причины нет» через пятнадцать секунд
        // лежания значило бы соврать раньше времени
        if (ctx.Self.IsDead || (ctx.Self.Health ?? 1) <= 0)
        {
            causeUntil = DateTime.UtcNow.AddSeconds(CauseWaitSeconds);
            return;
        }
        if (DateTime.UtcNow < causeUntil)
            return;
        causePending = null;
        OnLog?.Invoke("от чего умер — сервер не сказал (атрибуты причины не пришли)");
    }

    /// <summary>
    /// КТО УБИЛ — ТОЛЬКО ТОТ, КОГО СЕРВЕР НАЗВАЛ ЭТОЙ СМЕРТЬЮ.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 19.08, 18:34). В чате сервера стояло
    /// «Player MuraSlav got killed by a drifter», а бот записал
    /// «причина смерти по словам сервера: игрок Metalismatic» — то есть свалил
    /// смерть от дрифтера на человека, по чьей строке заказчик и решает, кто
    /// виноват.
    ///
    /// ПОЧЕМУ ТАК ВЫХОДИЛО, ФАКТОМ ИЗ КОДА ИГРЫ (Entity.Die, VintagestoryAPI
    /// 1.22.7): при смерти сервер пишет <c>deathByEntity</c> тогда, когда у
    /// урона ЕСТЬ существо-источник, и <c>deathByPlayer</c> — только когда это
    /// существо оказалось игроком. СТИРАТЬ их он не умеет: ни одной ветки,
    /// удаляющей эти атрибуты, в Die нет. Значит имя игрока, убившего бота
    /// когда-то раньше, лежит в атрибутах вечно — и прошлая проверка «источник
    /// существо ИЛИ игрок» пропускала его в любую смерть от твари, а
    /// <see cref="DeathCause.ToString"/> предпочитает игрока существу.
    ///
    /// Отсюда правило односторонее: имя игрока имеет право появиться ТОЛЬКО
    /// при <see cref="DamageSourceKind.Player"/> — сервер и ставит его ровно в
    /// этом случае. Смерть от твари называет тварь, всё остальное — никого.
    /// </summary>
    /// <param name="source">Что сервер записал в <c>deathReason</c>.</param>
    /// <param name="deathByEntity">Код существа-убийцы из атрибутов (может быть с прошлой смерти).</param>
    /// <param name="deathByPlayer">Имя игрока-убийцы из атрибутов (может быть с прошлой смерти).</param>
    public static (string? ByEntity, string? ByPlayer) Killer(
        DamageSourceKind source, string? deathByEntity, string? deathByPlayer) => source switch
        {
            // Убил игрок: сервер положил и код сущности («game:player»), и имя
            DamageSourceKind.Player => (Short(deathByEntity), Named(deathByPlayer)),
            // Убила тварь: её код свежий, а имя игрока — с какой-то прошлой смерти
            DamageSourceKind.Entity => (Short(deathByEntity), null),
            // Падение, голод, холод, пустота: существа-источника не было вовсе
            _ => (null, null)
        };

    /// <summary>Пустое имя — это «не назвал», а не «игрок без имени».</summary>
    private static string? Named(string? name) => name is { Length: > 0 } ? name : null;

    /// <summary>Код существа без домена: «game:drifter-normal» человеку читать незачем.</summary>
    private static string? Short(string? code) =>
        code is { Length: > 0 } ? code[(code.IndexOf(':') + 1)..] : null;

    private void Replace(DeathRecord updated)
    {
        int at = log.FindIndex(r => r.Number == updated.Number);
        if (at >= 0)
            log[at] = updated;
    }

    /// <summary>
    /// Что бот честно знает про свои смерти и возрождения — одной строкой для
    /// журнала и панели. Ни одного выдуманного числа: «осталось» говорит
    /// только сервер, и если он молчал, так и написано.
    /// </summary>
    public string Report()
    {
        string lives = LivesLeft switch
        {
            null when !Rules.Known =>
                "настройки мира ещё не пришли — про предел возрождений сказать нечего",
            null when Rules.LivesLimited =>
                $"по настройкам мира жизней {Rules.PlayerLives}, но сервер про остаток ещё не говорил",
            null => "предела возрождений в этом мире нет",
            < 0 => "предела возрождений в этом мире нет",
            0 => "ВОЗРОЖДЕНИЙ НЕ ОСТАЛОСЬ",
            var n => $"осталось возрождений: {n}"
        };
        string deaths = Deaths == 0
            ? "за сессию не умирал"
            : $"смертей за сессию: {Deaths}" +
              (DeathsOnServer is { } all ? $" (всего в этом мире по счёту сервера: {all})" : "") +
              "; " + string.Join("; ", log.TakeLast(3));
        return $"{lives}; {deaths}";
    }

    private BlockPos? CurrentBlockPos() =>
        ctx.Self.Position is { } p
            ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
            : null;

    /// <summary>
    /// Суммарная ценность потерянного по оценке роли. Библиотека не знает,
    /// что ценно: оценку даёт делегат (0 и меньше — не ценно).
    /// </summary>
    public double LostValue(Func<SlotContent, double> value) =>
        LostItems.Sum(s => Math.Max(0, value(s)));

    /// <summary>
    /// Лежащие вещи рядом с точкой. Это ровно то, что видит живой игрок:
    /// предметные сущности приходят с сервера вместе со своим itemstack.
    /// </summary>
    /// <param name="onlyMine">
    /// Только выпавшее из нас: у предметной сущности есть синхронизируемый
    /// атрибут byPlayerUid (EntityItem.ByPlayerUid) — сервер шлёт его всем,
    /// так что это не подглядывание.
    /// </param>
    public List<EntityInfo> DropsNear(BlockPos pos, double radius, bool onlyMine = false)
    {
        var found = ctx.Entities.Nearby(pos.X + 0.5, pos.Y + 0.5, pos.Z + 0.5, radius)
            .Where(e => e.IsItem);
        if (onlyMine)
            found = found.Where(e =>
                e.WatchedAttributes.GetString("byPlayerUid") == ctx.Bot.PlayerUid);
        return found.ToList();
    }

    /// <summary>Что лежит в предметной сущности (код и количество), null — не разобрали.</summary>
    public SlotContent? DropContent(EntityInfo drop)
    {
        var stack = drop.WatchedAttributes.GetItemstack("itemstack");
        if (stack == null || stack.StackSize <= 0)
            return null;
        // ItemClass: 0 = блок, 1 = предмет — как и в слотах инвентаря
        string? code = (int)stack.Class == 0
            ? ctx.World.BlockIdToCode(stack.Id)
            : ctx.World.ItemIdToCode(stack.Id);
        return new SlotContent(code ?? $"?{(int)stack.Class}:{stack.Id}", stack.StackSize);
    }

    /// <summary>
    /// Сходить за вещами: дойти до места смерти и собрать выпавшее ногами
    /// (Mining.CollectDropsAsync — бот проходит по вещам, как игрок).
    ///
    /// Возвращает ЧЕСТНЫЙ итог: что подобрано и что осталось лежать. Могилу
    /// сама не забывает — решает роль (может захотеть сходить второй раз
    /// с пустым рюкзаком).
    /// </summary>
    public async Task<RecoveryResult> RecoverAsync(double maxSeconds = 180, CancellationToken ct = default)
    {
        if (Grave is not { } grave)
            return new RecoveryResult(RecoveryOutcome.NoGrave, 0, 0, "не помню, где умер");

        Attempts++;
        var deadline = DateTime.UtcNow.AddSeconds(maxSeconds);

        if ((Distance ?? double.MaxValue) > CollectRadius)
        {
            OnLog?.Invoke($"иду за вещами на {WorldOrigin.SayBare(grave.X, grave.Y, grave.Z)} " +
                          $"({Distance:0} бл, до исчезновения ~{SecondsLeft:0} с)");
            double travelSeconds = Math.Max(5, (deadline - DateTime.UtcNow).TotalSeconds - CollectSeconds);
            await ctx.Movement.TravelToAsync(grave, travelSeconds, ct);
            // TravelToAsync мог вернуть false, но подвести вплотную — судим
            // по расстоянию, а не по его слову
            if ((Distance ?? double.MaxValue) > CollectRadius)
                return new RecoveryResult(RecoveryOutcome.NotReached, 0, DropsNear(grave, CollectRadius).Count,
                    $"до места смерти не дошёл, осталось {Distance:0} бл");
        }

        int seen = DropsNear(grave, CollectRadius).Count;
        double collectSeconds = Math.Max(3, Math.Min(CollectSeconds, (deadline - DateTime.UtcNow).TotalSeconds));
        int picked = await ctx.Mining.CollectDropsAsync(CollectRadius, collectSeconds, ct);
        int left = DropsNear(grave, CollectRadius).Count;

        if (seen == 0 && picked == 0)
            return new RecoveryResult(RecoveryOutcome.NothingFound, 0, 0,
                "на месте смерти пусто: вещей нет (истёк срок, забрали или мир их не роняет)");
        if (left == 0)
            return new RecoveryResult(RecoveryOutcome.Recovered, picked, 0,
                $"забрал вещи: {picked} шт.");
        return new RecoveryResult(RecoveryOutcome.Partial, picked, left,
            $"подобрал {picked}, на земле осталось {left} (место в сумках?)");
    }
}

/// <summary>
/// Способность «сходить за своими вещами». Механизм лежит в DeathRecovery,
/// здесь — только политика: идти ли, как далеко, сколько раз, при какой
/// погоде и ради чего. Всё это настройки роли, потому что ответ разный:
/// торговцу с полным сундуком дома возвращаться за палкой незачем,
/// а за железной киркой — обязательно.
/// </summary>
public class BehaviorRecoverLoot : BotBehavior
{
    private readonly BotContext ctx;
    private readonly DeathRecovery recovery;
    private DateTime nextTry = DateTime.MinValue;
    private bool gaveUp;

    public BehaviorRecoverLoot(BotContext ctx, DeathRecovery recovery)
    {
        this.ctx = ctx;
        this.recovery = recovery;
        // Новая смерть — новая попытка: прошлый отказ к ней отношения не имеет
        recovery.OnDeathRemembered += (_, _) =>
        {
            gaveUp = false;
            nextTry = DateTime.MinValue;
        };
    }

    /// <summary>Ходить ли за вещами вообще.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Дальше этого не идём: за тридевять земель вещи всё равно протухнут.</summary>
    public double MaxDistance { get; set; } = 250;

    /// <summary>Сколько заходов делать, прежде чем махнуть рукой.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Сколько секунд отводится на один заход (потом ход вернётся другим способностям).</summary>
    public double TimeoutSeconds { get; set; } = 180;

    /// <summary>Пауза между заходами.</summary>
    public double RetrySeconds { get; set; } = 15;

    /// <summary>Ждать рассвета: ночью дорога через лес — способ умереть ещё раз.</summary>
    public bool WaitForDaylight { get; set; }

    /// <summary>Не выходить в бурю (укрытие важнее вещей).</summary>
    public bool AvoidStorm { get; set; } = true;

    /// <summary>
    /// Не идти, если вещи, по нашей оценке, уже исчезли (DropLifetimeSeconds).
    /// Оценка приблизительная, поэтому это выключаемо.
    /// </summary>
    public bool SkipIfDespawned { get; set; } = true;

    /// <summary>
    /// Чего стоит стопка — решает РОЛЬ. null — идти за чем угодно.
    /// Возвращайте 0 для хлама и больше для нужного (например, ценность
    /// инструмента по World.ToolTier, еды по GetSatietyByCode).
    /// </summary>
    public Func<SlotContent, double>? ValueOf { get; set; }

    /// <summary>Ниже этой суммарной ценности потерянного поход не начинаем.</summary>
    public double MinValue { get; set; }

    /// <summary>Пошли за вещами (место, расстояние).</summary>
    public event Action<BlockPos, double>? OnHeadingToGrave;

    /// <summary>Сходили: итог честный, в т.ч. «пришёл, а там пусто».</summary>
    public event Action<RecoveryResult>? OnRecovered;

    /// <summary>Не пойдём (или больше не пойдём) — с причиной, чтобы роль решила, что делать.</summary>
    public event Action<string>? OnGaveUp;

    public override async Task<bool> TickAsync(CancellationToken ct)
    {
        // Место запоминаем ВСЕГДА, даже когда сама способность выключена:
        // иначе включённая после смерти настройка окажется бесполезной
        recovery.Observe();

        if (!Enabled || gaveUp || !recovery.HasGrave || ctx.Self.IsDead)
            return false;
        if (ctx.Movement.IsBusy || DateTime.UtcNow < nextTry)
            return false;
        if (AvoidStorm && ctx.Storms.ShelterNow)
            return false;
        if (WaitForDaylight && ctx.Clock.Ready && ctx.Clock.IsNight)
            return false;

        // МИР МОЖЕТ ВООБЩЕ НЕ РОНЯТЬ ВЕЩИ. При deathPunishment = keep всё
        // остаётся в сумках, и поход на место смерти — это дорога через
        // полкарты за пустым местом. Сервер про эту настройку сказал сам,
        // гадать не о чем
        if (recovery.Rules.KeepInventory)
        {
            GiveUp("в этом мире вещи при смерти остаются в сумках — идти не за чем");
            return false;
        }

        if (recovery.Distance is not { } distance)
            return false; // ещё не знаем, где стоим

        if (distance > MaxDistance)
        {
            GiveUp($"до вещей {distance:0} бл — дальше {MaxDistance:0}, не иду");
            return false;
        }
        if (SkipIfDespawned && recovery.LikelyDespawned)
        {
            GiveUp($"прошло {recovery.SecondsSinceDeath:0} с — вещи, скорее всего, уже исчезли");
            return false;
        }
        if (ValueOf is { } value && recovery.LostValue(value) < MinValue)
        {
            GiveUp("потерянное того не стоит");
            return false;
        }
        if (recovery.Attempts >= MaxAttempts)
        {
            GiveUp($"{recovery.Attempts} захода(ов) без толку");
            return false;
        }

        // ПОХОД ЗА ВЕЩАМИ — ЭТО ХОДЬБА НА МИНУТЫ, и тело обязано быть за нами.
        // Без владения выходило две пары рук на одних клавишах: наряд роли вёл
        // бота на делянку, а возврат — на могилу, и он топтался между ними, не
        // доходя никуда. Уровень «дело роли», а не рефлекс: за вещами идут,
        // когда никто не бьёт и ничего не горит.
        using var hold = ctx.Turn.TryTake("за вещами", BodyArbiter.Importance.Routine, ct);
        if (hold == null)
            return false;   // тело занято; про сам отказ говорит очередь на тело

        OnHeadingToGrave?.Invoke(recovery.Grave!.Value, distance);

        var result = await recovery.RecoverAsync(TimeoutSeconds, hold.Token);
        // Пауза отсчитывается ОТ КОНЦА захода, а не от начала: заход длинный,
        // и иначе следующий стартовал бы сразу, не дав поесть и полечиться
        nextTry = DateTime.UtcNow.AddSeconds(RetrySeconds);
        OnRecovered?.Invoke(result);

        // Забываем могилу только когда ходить туда больше незачем: пусто или
        // всё подобрано. «Часть осталась» — повод зайти ещё раз (в сумках
        // могло не хватить места), но не больше MaxAttempts раз
        if (result.Outcome is RecoveryOutcome.Recovered or RecoveryOutcome.NothingFound)
        {
            recovery.Forget();
            gaveUp = false;
        }
        return true; // поход занимает бота целиком, ход был наш
    }

    private void GiveUp(string reason)
    {
        gaveUp = true;
        OnGaveUp?.Invoke(reason);
    }
}
