namespace VsBotKit;

/// <summary>
/// Всё, что нужно навыку или способности для работы: единая точка доступа
/// к подсистемам бота. Передаётся в скиллы, чтобы каждый не собирал
/// зависимости по одной.
/// </summary>
public sealed class BotContext
{
    public required BotClient Bot { get; init; }
    public required WorldModel World { get; init; }
    public required EntityModel Entities { get; init; }
    public required SelfState Self { get; init; }
    public required Movement Movement { get; init; }
    public required Actions Actions { get; init; }

    /// <summary>Сеть транслокаторов, увиденная ботом.</summary>
    public required TranslocatorMap Translocators { get; init; }

    /// <summary>
    /// Подтверждения переноса от сервера (канал «tpManager»). null — некому
    /// слушать; тогда вход в транслокатор проверяется только по координатам и
    /// говорит об этом вслух.
    /// </summary>
    public TranslocatorWatch? Translocating { get; set; }

    /// <summary>Тело на игровой физике (клавиши, позиция).</summary>
    public Body? Body { get; set; }

    /// <summary>
    /// Очередь на тело: кто им сейчас распоряжается. Рефлексы выживания
    /// перебивают долгие дела, и перебитое дело обязано остановиться.
    ///
    /// ПРИХОДИТ СНАРУЖИ, А НЕ ЗАВОДИТСЯ ЗДЕСЬ, и это не мелочь: тот же самый
    /// распорядитель нужен ДВИЖЕНИЮ (<see cref="Movement"/> сверяет по нему,
    /// цел ли ещё хозяин у похода). Заведи его здесь — движение получило бы
    /// свой, второй, и «у ходьбы один хозяин» снова стало бы неправдой
    /// (разбор — у <see cref="BodyArbiter.Era"/>).
    /// </summary>
    public required BodyArbiter Turn { get; init; }

    private Resume? resume;

    /// <summary>
    /// Прервали — вернись и доделай: память прерванной работы и правило, когда
    /// к ней возвращаться.
    ///
    /// Живёт рядом с очередью на тело, потому что читает именно ЕЁ решения:
    /// отличить «карьер бросили ради обеда» от «человек сказал стоп» больше
    /// негде. Живой случай: рефлекс увёл бота на 67-й клетке из 720, и яма
    /// так и осталась недокопанной — возвращаться было некому.
    /// </summary>
    public Resume Resume => resume ??= new Resume(Turn);

    /// <summary>
    /// Читы: всё, чего живой игрок не может. Выключены, пока роль явно
    /// не включит (см. Cheats).
    /// </summary>
    public Cheats Cheats { get; } = new();

    /// <summary>
    /// ЧТО СЧИТАТЬ ОРУЖИЕМ — одна мерка на всех, кто её спрашивает.
    ///
    /// Живёт здесь, а не внутри одной способности, потому что спрашивают
    /// РАЗНЫЕ: самооборона — «драться или бежать безоружным», сбор оружия к
    /// ночи — «что брать в руку». Пока у каждой была своя копия числа, команда
    /// «!бой 3» правила одну из них и молчала про вторую (см.
    /// <see cref="WeaponStandard"/>).
    /// </summary>
    public WeaponStandard Weapons { get; } = new();

    /// <summary>Руки: дальность, предмет в руке, честное удержание кнопки.</summary>
    public Hands Hands { get; set; } = null!;

    /// <summary>
    /// Код предмета, ЛЕЖАЩЕГО на земле сущностью. Сервер прячет его в атрибутах,
    /// и без этого перевода бот видит рядом «сущность item» без имени.
    ///
    /// Живёт здесь, а не в подборе, потому что спрашивают разные: подбор — что
    /// брать, еда — съедобно ли, диагностика — что вообще выпало.
    /// </summary>
    /// <remarks>
    /// Стопка лежит в атрибуте ОСОБОГО типа (ItemstackAttribute), а не деревом.
    /// Прочитать её как дерево — получить пустоту, и живьём это стоило дорого:
    /// голодный бот не ел брошенное ему мясо, потому что не мог его назвать.
    /// </remarks>
    public string? DroppedItemCode(EntityInfo e)
    {
        var stack = e.WatchedAttributes.GetItemstack("itemstack");
        if (stack == null || stack.StackSize <= 0)
            return null;
        // Класс: 0 — блок, 1 — предмет; ровно как в слотах инвентаря
        return (int)stack.Class == 0 ? World.BlockIdToCode(stack.Id) : World.ItemIdToCode(stack.Id);
    }

    /// <summary>Сколько штук лежит в этой куче (сервер шлёт вместе с кодом).</summary>
    public int DroppedItemCount(EntityInfo e) =>
        e.WatchedAttributes.GetItemstack("itemstack")?.StackSize ?? 0;

    /// <summary>Добыча и установка блоков по правилам игры.</summary>
    public Mining Mining { get; set; } = null!;

    /// <summary>Игровые часы и календарь.</summary>
    public GameClock Clock { get; set; } = null!;

    /// <summary>Временные бури.</summary>
    public TemporalStorms Storms { get; set; } = null!;

    /// <summary>
    /// Своя темпоральная стабильность и разломы вокруг. Живёт здесь, а не
    /// внутри одной способности, потому что спрашивают разные: способность
    /// ухода — «пора ли бежать», панель и чат — «сколько её сейчас».
    /// </summary>
    public TemporalStability Stability { get; set; } = null!;

    /// <summary>Надписи на табличках и осмотр контейнеров.</summary>
    public Readables Read { get; set; } = null!;

    /// <summary>Износ инструментов: что вот-вот сломается и чем заменить.</summary>
    public ToolWear Wear { get; set; } = null!;

    /// <summary>Кровать, сон и точка возрождения.</summary>
    public Sleeping Sleeping { get; set; } = null!;

    /// <summary>Свет: факелы и тёмные места.</summary>
    public Lighting Lighting { get; set; } = null!;

    /// <summary>
    /// ЩИТ: что игра о нём знает, надет ли он и чья сейчас левая рука.
    ///
    /// Живёт здесь, а не внутри боя, потому что спрашивают РАЗНЫЕ и о разном:
    /// бой — «надеть или снять по угрозе», способность света — «уступать ли
    /// руку» (слот 11 у них общий), окно управления — «что у бота в левой
    /// руке». Заведи каждый свою копию — и спор за один слот решался бы тремя
    /// разными мерками, то есть не решался бы вовсе.
    /// </summary>
    public Shielding Shield { get; set; } = null!;

    /// <summary>Место смерти и возврат за вещами.</summary>
    public DeathRecovery Deaths { get; set; } = null!;

    /// <summary>
    /// Журнал урона сервера: сколько сняли и ОТ ЧЕГО. Живёт здесь, а не внутри
    /// боя, потому что спрашивают разные: бой — «меня ударили или я упал»,
    /// разбор прогона — «сколько бьёт этот моб».
    /// </summary>
    public DamageWatch Damage { get; set; } = null!;

    /// <summary>Температура тела и одежда.</summary>
    public BodyHeat Cold { get; set; } = null!;

    /// <summary>Камнетёсство: первые инструменты из камня.</summary>
    /// <summary>
    /// Реестр типов сущностей от сервера: класс, теги, атрибуты. Живёт здесь,
    /// а не внутри одной способности, потому что спрашивают разные: бой —
    /// «живое ли это или стрела», эмоции — «какие жесты знает игрок».
    /// </summary>
    public EntityTypes EntityTypes { get; set; } = null!;

    /// <summary>
    /// ОБЪЯВЛЕННЫЕ СКОРОСТИ ТВАРЕЙ из ассетов игры (см. <see cref="CreatureSpeeds"/>).
    ///
    /// Живёт здесь, а не внутри боя, потому что это ФАКТ ИГРЫ, а не мнение
    /// способности: тот же ответ понадобится и погоне, и разбору «оторвусь ли»,
    /// и человеку в окне. Пустой набор (ассетов не нашлось) — не беда молчком:
    /// <see cref="CreatureSpeeds.Why"/> называет причину вслух.
    /// </summary>
    public CreatureSpeeds Speeds { get; set; } = CreatureSpeeds.FromEntries(
        new Dictionary<string, double>());

    public Knapping Knapping { get; set; } = null!;

    /// <summary>
    /// Промывка лотком. Живёт здесь, а не внутри одной способности, потому что
    /// спрашивают её разные: снабжение — «чем закрыть волокна», команда окна —
    /// «помой N раз», отказ лука — «сколько промывок до тетивы».
    /// </summary>
    public Panning Panning { get; set; } = null!;

    /// <summary>Сбор лежащего на земле: камни, палки, самородки.</summary>
    public Gathering Gathering { get; set; } = null!;

    /// <summary>
    /// Дикая еда: ягодники обирают удержанием кнопки, грибы и дикие овощи
    /// ломают. Лежит здесь затем же, зачем и остальные: очередь задач
    /// подкручивает потолок времени сбора через контекст, не зная роли.
    /// </summary>
    public Foraging Foraging { get; set; } = null!;

    /// <summary>
    /// Подбор выпавшего. «Лежит» и «выпало» — разные механики: первое блоки,
    /// второе сущности, и путать их значит терять всю добычу
    /// </summary>
    public Pickup Pickup { get; set; } = null!;

    /// <summary>Взять высоту: дорогой, а где дороги нет — столбом под собой.</summary>
    public Climbing Climbing { get; set; } = null!;

    /// <summary>Руда: найти видимую жилу и выработать её.</summary>
    public Ores Ores { get; set; } = null!;

    /// <summary>
    /// СКОЛЬКО МЕТАЛЛА В ВЕЩИ — по реестру сервера. Живёт здесь, а не внутри
    /// руды, потому что спрашивают разные: наряд — «набрал ли я двадцать меди»,
    /// отчёт — «в чём я считал», окно управления — «что бот понял под словом».
    /// </summary>
    public Metals Metal { get; set; } = null!;

    /// <summary>
    /// Память встреченного: что видел, где, когда и сколько. Живёт здесь, а не
    /// внутри руды, потому что спрашивают разные: наряд — «куда сходить вместо
    /// разведки», команда «!помню» — «где ты это видел», разведка — «тут уже
    /// смотрели».
    /// </summary>
    public ResourceMemory Resources { get; set; } = null!;

    /// <summary>
    /// Чужие владения: где нельзя работать и на сколько от них держаться.
    /// Заявки присылает сервер, приметы чужого жилья бот подмечает сам.
    /// </summary>
    public Claims Claims { get; set; } = null!;

    /// <summary>Стройка ради подъёма: лестница по стене, столб под ногами.</summary>
    public Scaffolding Scaffolding { get; set; } = null!;

    /// <summary>
    /// Временные леса: что поставлено на время работы и должно быть снято в
    /// конце. Живёт здесь, а не внутри стройки, потому что докладывают разные:
    /// стройка — про лестницу и подпорку, добыча — про столб, а снимает всё это
    /// одна уборка.
    /// </summary>
    public TempBlocks Temp { get; set; } = null!;

    /// <summary>
    /// Сверка готовой постройки с замыслом: лишние блоки снять, недостающие
    /// назвать. Спрашивают разные — дорога, карьер, — а сравнение одно.
    /// </summary>
    public BuildCheck Check { get; set; } = null!;

    /// <summary>Крафт по рецептам игры.</summary>
    public Crafting Crafting { get; set; } = null!;

    /// <summary>Поручения: «добудь столько-то того-то».</summary>
    public Errands Errands { get; set; } = null!;

    /// <summary>Склад: куда носить добытое и куда возвращаться работать.</summary>
    public Depot Depot { get; set; } = null!;

    /// <summary>
    /// Снабжение: где взять недостающее — сумки, склад работы, дом, крафт,
    /// добыть рядом. Живёт здесь, а не внутри одной работы, ровно потому, что
    /// спрашивают ВСЕ: дорога, карьер, штольня, факелы, инструменты, любой
    /// крафт.
    /// </summary>
    public Supply Supply { get; set; } = null!;

    /// <summary>Запасы: что держать при себе и чем это пополнять.</summary>
    public Stock Stock { get; set; } = null!;

    /// <summary>
    /// Запас по расходу: «чем пользуюсь — то и держу». Делает список требований
    /// подвижным — сколько стрел носить, знает только тот, кто видел, сколько
    /// их истрачено.
    ///
    /// Может быть ещё не собран: сумки и хранилища заводятся раньше запасов, а
    /// зовут его именно они (<see cref="Upkeep.Moved"/>).
    /// </summary>
    public Upkeep Upkeep { get; set; } = null!;

    /// <summary>Разведка: уйти под землю и искать руду там, где её не видно.</summary>
    public Prospecting Prospecting { get; set; } = null!;

    /// <summary>Наряд: «принеси столько-то того-то» — от слов до сумки.</summary>
    public MiningOrder Order { get; set; } = null!;

    /// <summary>Добыча камня шахматкой.</summary>
    public Quarry Quarry { get; set; } = null!;

    /// <summary>Штольня: прокопать ход туда, куда пешком не пройти.</summary>
    public Tunnel Tunnel { get; set; } = null!;

    /// <summary>
    /// ЗАСТАВА МОЛОТКА: единственный ответ на «можно ли это ломать».
    ///
    /// Живёт здесь, а не внутри одной работы, ровно потому, что спрашивают
    /// ВСЕ: столб, ступени, расчистка пути, дорога, карьер, штольня, руда,
    /// костёр, грядка. Три раза подряд заказчик писал «зачем ломать, если был
    /// путь» — и каждый раз чинили ОДНУ из дорог к молотку, потому что общего
    /// места у них не было.
    ///
    /// Заводится сама при первом вопросе: ни один сборщик бота о ней знать не
    /// обязан, а спросить её вправе любой, у кого есть контекст.
    /// </summary>
    public BreakGuard Break => застава ??= new BreakGuard(this);

    private BreakGuard? застава;

    /// <summary>Дороги: замостить путь, по которому ходить быстрее.</summary>
    public Roads Roads { get; set; } = null!;
}

/// <summary>Результат выполнения навыка.</summary>
public sealed record SkillResult(bool Success, string? Message = null)
{
    public static SkillResult Ok(string? message = null) => new(true, message);
    public static SkillResult Fail(string message) => new(false, message);
}

/// <summary>
/// Активный навык — целевое действие с началом и концом («приготовь еду»,
/// «сходи туда», «принеси из сундука»). В отличие от способностей (BotBehavior,
/// фоновых рефлексов), навык запускается явно, выполняется до результата
/// и его можно отменить.
///
/// Свой навык = класс-наследник (или DelegateSkill из лямбды) + регистрация
/// в SkillRunner. Библиотека намеренно не знает список навыков заранее.
/// </summary>
public abstract class BotSkill
{
    /// <summary>Имя для запуска (латиница/кириллица, без пробелов).</summary>
    public abstract string Name { get; }

    /// <summary>Краткое описание с форматом аргументов — пригодится и людям, и ИИ-фасаду.</summary>
    public virtual string Description => "";

    public abstract Task<SkillResult> ExecuteAsync(BotContext ctx, string args, CancellationToken ct);
}

/// <summary>Навык из лямбды — быстрый способ добавить своё без класса.</summary>
public sealed class DelegateSkill : BotSkill
{
    private readonly Func<BotContext, string, CancellationToken, Task<SkillResult>> body;

    public override string Name { get; }
    public override string Description { get; }

    public DelegateSkill(string name, string description,
        Func<BotContext, string, CancellationToken, Task<SkillResult>> body)
    {
        Name = name;
        Description = description;
        this.body = body;
    }

    public override Task<SkillResult> ExecuteAsync(BotContext ctx, string args, CancellationToken ct) =>
        body(ctx, args, ct);
}

/// <summary>
/// Исполнитель навыков: реестр по имени + гарантия «бот делает одно дело
/// за раз» (новый навык не стартует, пока идёт текущий; текущий можно отменить).
/// </summary>
public class SkillRunner
{
    private readonly BotContext ctx;
    private readonly Dictionary<string, BotSkill> skills = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? currentCts;

    /// <summary>Имя выполняемого навыка (null — бот свободен).</summary>
    public string? CurrentSkill { get; private set; }

    /// <summary>Навык завершился (имя, результат).</summary>
    public event Action<string, SkillResult>? OnSkillCompleted;

    /// <summary>Что стоит сказать по дороге: например, во что обошлось дело.</summary>
    public event Action<string>? OnLog;

    public SkillRunner(BotContext ctx)
    {
        this.ctx = ctx;
    }

    public void Register(BotSkill skill) => skills[skill.Name] = skill;

    public IReadOnlyCollection<BotSkill> Registered => skills.Values;

    /// <summary>
    /// Запустить навык по имени. Возвращает результат; если бот занят другим
    /// навыком — сразу Fail (сначала CancelCurrent или дождитесь завершения).
    /// </summary>
    public async Task<SkillResult> RunAsync(string name, string args = "", CancellationToken ct = default)
    {
        if (!skills.TryGetValue(name, out var skill))
            return SkillResult.Fail($"навык \"{name}\" не зарегистрирован");
        if (CurrentSkill != null)
            return SkillResult.Fail($"занят навыком \"{CurrentSkill}\"");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        currentCts = cts;
        CurrentSkill = skill.Name;
        // Счёт «удары / возня» ведётся НА ДЕЛО, а не за всю жизнь бота: иначе
        // числа не с чем сравнить. Место одно на все навыки — заводить сброс в
        // каждом значило бы забыть его в первом же новом
        ctx.Mining?.Tally.Reset();
        try
        {
            var result = await skill.ExecuteAsync(ctx, args, cts.Token);
            SayTally();
            OnSkillCompleted?.Invoke(skill.Name, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            var cancelled = SkillResult.Fail("отменён");
            OnSkillCompleted?.Invoke(skill.Name, cancelled);
            return cancelled;
        }
        catch (Exception ex)
        {
            var failed = SkillResult.Fail($"ошибка: {ex.Message}");
            OnSkillCompleted?.Invoke(skill.Name, failed);
            return failed;
        }
        finally
        {
            CurrentSkill = null;
            currentCts = null;
        }
    }

    /// <summary>
    /// ПРЕРВАТЬ ТЕКУЩЕЕ ДЕЛО — И ГАСИТЬ ВОЗВРАТ К НЕМУ. Оба, и обязательно
    /// в одном месте.
    ///
    /// ЖИВОЙ СЛУЧАЙ. Рефлекс увёл бота есть (возврат ждёт тело до трёх минут).
    /// Человек в это время жмёт «Остановить»: окно отвечает «остановил», задача
    /// в очереди помечается остановленной — а прерывать в этот миг НЕЧЕГО,
    /// работа тела не держит, она ждёт. Рефлекс доел, возврат взял НОВОЕ
    /// владение, и бот молча докопал план до конца. Возврат сделал эту дыру
    /// дороже, чем она была: раньше работа хотя бы умирала сама.
    ///
    /// Гасить возврат умеет только <see cref="Resume.StopAll"/>, и звать его
    /// обязаны все «стопы» разом — окно, очередь задач и чат. Поэтому он
    /// живёт здесь, за одной дверью с отменой навыка: заводить его в каждом
    /// «стопе» отдельно значит забыть в первом же новом.
    /// </summary>
    /// <param name="why">Кто и почему остановил — это уйдёт человеку в журнал.</param>
    public void CancelCurrent(string why = "прервали текущее дело")
    {
        currentCts?.Cancel();
        ctx.Resume.StopAll(why);
    }

    /// <summary>
    /// Сказать, во что обошлось дело: сколько блоков, сколько времени ушло на
    /// удары и сколько на возню вокруг них.
    ///
    /// Счёт вёлся, но его НИКТО НЕ ЧИТАЛ — а именно он отвечает на вопрос «бот
    /// копает медленно из-за инструмента или из-за беготни?». Молчим, только
    /// если не сломано ни одного блока: у похода или разгрузки такой строки и
    /// быть не должно.
    /// </summary>
    private void SayTally()
    {
        if (ctx.Mining is { Tally.Blocks: > 0 } mining)
            OnLog?.Invoke(mining.Tally.ToString());
    }
}
