namespace VsBotKit;

/// <summary>
/// Бот целиком: подключение, восприятие мира, тело, способности, навыки, команды.
/// Всё внутри уже связано — снаружи остаётся описать роль.
///
/// <code>
/// var bot = new VsBot("127.0.0.1", 42420, "Кузнец", uid);
/// bot.UseRole(new МояРоль());
/// await bot.RunAsync();
/// </code>
///
/// Любую системную часть можно заменить своей: например, свой поиск пути —
/// <c>bot.Movement.PathFinder = new МойПоиск();</c>
/// </summary>
public class VsBot : IDisposable
{
    /// <summary>Сетевое ядро: пакеты, чат, вход в мир.</summary>
    public BotClient Client { get; }

    /// <summary>Блоки, жидкости, реестры предметов, содержимое контейнеров.</summary>
    public WorldModel World { get; }

    /// <summary>Кто вокруг: игроки, мобы, брошенные предметы.</summary>
    public EntityModel Entities { get; }

    /// <summary>Своё состояние: здоровье, сытость, позиция, инвентари.</summary>
    public SelfState Self { get; }

    /// <summary>
    /// Сколько секунд считать свежим то, что бот видел в ОТКРЫТОМ им сундуке.
    ///
    /// Минута — потому что за минуту бот успевает закрыть сундук, отойти на
    /// шаг и пересчитать, что туда легло, а вот выдавать увиденное полчаса
    /// назад за нынешнее положение дел уже нельзя: из сундука могли всё
    /// вынести, и решение «мест там 15» окажется выдумкой.
    /// </summary>
    public double RememberInsideSeconds { get; set; } = 60;

    /// <summary>Тело: ходьба, бег, плавание, прыжки, поиск пути.</summary>
    public Movement Movement { get; }

    /// <summary>Руки: атака, блоки, контейнеры, предметы, еда.</summary>
    public Actions Actions { get; }

    /// <summary>Глаза: надписи на табличках и осмотр контейнеров.</summary>
    public Readables Read { get; }

    /// <summary>Руки: что держим, дотягиваемся ли, честное удержание кнопки.</summary>
    public Hands Hands { get; }

    /// <summary>Добыча и установка блоков по правилам игры.</summary>
    public Mining Mining { get; }

    /// <summary>Игровые часы: время суток, сезон.</summary>
    public GameClock Clock { get; }

    /// <summary>Временные бури: идёт ли, когда следующая.</summary>
    public TemporalStorms Storms { get; }

    /// <summary>
    /// Своя темпоральная стабильность (то самое число под шестерёнкой у живого
    /// игрока) и разломы вокруг.
    /// </summary>
    public TemporalStability Stability { get; }

    /// <summary>
    /// Моды сервера и их каналы связи: что стоит и о чём сервер с нами говорит.
    /// Нужно ровно затем, чтобы не врать про поддержку: новые блоки бот берёт
    /// из реестра сам, а пакеты чужих модов не разбирает — и говорит об этом.
    /// </summary>
    public ServerMods Mods { get; }

    /// <summary>Костёр: развести, поджечь, топить, жарить.</summary>
    public Fire Fire { get; }

    /// <summary>Готовка на костре: пожарить, сварить в горшке, забрать готовое.</summary>
    public Cooking Cooking { get; }

    /// <summary>Разделка туш: мясо, шкура, жир с убитого зверя.</summary>
    public Butchering Butchering { get; }

    /// <summary>Дикая еда: ягоды, грибы, растения.</summary>
    public Foraging Foraging { get; }

    /// <summary>Земледелие: вспахать, посеять, полить, собрать.</summary>
    public Farming Farming { get; }

    /// <summary>Укрытие: дом с дверью или ниша, замурованная изнутри.</summary>
    public Shelter Shelter { get; }

    /// <summary>Температура тела и одежда: мёрзнет ли бот и во что одеться.</summary>
    public BodyHeat Cold { get; }

    /// <summary>Износ инструментов: что вот-вот сломается.</summary>
    public ToolWear Wear { get; }

    /// <summary>Кровать, сон и точка возрождения.</summary>
    public Sleeping Sleeping { get; }

    /// <summary>Свет: факелы и тёмные места.</summary>
    public Lighting Lighting { get; }

    /// <summary>
    /// СВЕТ В РУКЕ и вообще ЛЕВАЯ РУКА: что в ней лежит, что туда пускает игра
    /// и как туда положить.
    ///
    /// ХОЗЯИН У ЛЕВОЙ РУКИ ОДИН — ЗДЕСЬ. Раньше <see cref="BehaviorCarryLight"/>
    /// заводил механизм себе внутрь, и спросить «а что у бота в левой руке»
    /// со стороны было не у кого: окно управления, которому это понадобилось
    /// (кнопка «взять в левую руку»), либо завело бы вторую копию, либо
    /// работало бы только у тех ролей, где эта способность включена. Поэтому
    /// механизм живёт у бота, а способность им пользуется.
    /// </summary>
    public HandLight Light { get; }

    /// <summary>
    /// ЩИТ: надеть по угрозе, снять, когда стало тихо, и спор за левую руку с
    /// фонарём. Хозяин один и тот же, что у левой руки, и по той же причине
    /// (см. <see cref="Light"/>): щит спрашивают и бой, и способность света, и
    /// окно управления.
    /// </summary>
    public Shielding Shield { get; }

    /// <summary>Место смерти и возврат за вещами (решение — за ролью).</summary>
    public DeathRecovery Deaths { get; }

    /// <summary>
    /// ОТ ЧЕГО УБАВЛЯЕТСЯ ЗДОРОВЬЕ — по словам сервера, а не по догадке.
    /// Общая на весь бот: спрашивают и бой («меня ударили или я упал»), и
    /// разбор живого прогона.
    /// </summary>
    public DamageWatch Damage { get; }

    /// <summary>Прямая видимость: то, что бот на самом деле видит из глаз.</summary>
    public Sight Sight => Hands.Sight;

    /// <summary>
    /// Реестр типов сущностей сервера: класс, теги («inanimate», «projectile»),
    /// атрибуты. По нему бой отличает стрелу от твари, а эмоции узнают жесты.
    /// </summary>
    public EntityTypes EntityTypes { get; }

    /// <summary>Камнетёсство: топор и нож из камня, с чего начинается выживание.</summary>
    public Knapping Knapping { get; }

    /// <summary>Промывка лотком: горсть земли в лоток, лоток в воду.</summary>
    public Panning Panning { get; }

    /// <summary>Сбор лежащего на земле: камни, палки, самородки.</summary>
    public Gathering Gathering { get; }

    /// <summary>Подбор ВЫПАВШЕГО: добыча, брошенное, вещи с могилы.</summary>
    public Pickup Pickup { get; }

    /// <summary>Взять высоту: дорогой, а где дороги нет — столбом под собой.</summary>
    public Climbing Climbing { get; }

    /// <summary>Руда: найти видимую жилу и выработать её.</summary>
    public Ores Ores { get; }

    /// <summary>Память встреченного: что видел, где, когда и сколько.</summary>
    public ResourceMemory Resources { get; }

    /// <summary>Чужие владения: заявки с сервера и подмеченные признаки чужого жилья.</summary>
    public Claims Claims { get; }

    /// <summary>Эмоции: помахать, кивнуть, поклониться (серверная команда /emote).</summary>
    public Emotes Emotes { get; }

    /// <summary>Память встреч: кого видел, когда впервые, когда в последний раз и сколько раз.</summary>
    public MeetingBook Meetings { get; }

    /// <summary>Книга встреч на диске (botmeetings.json рядом с botsession.json).</summary>
    public MeetingStore MeetingsFile { get; }

    /// <summary>
    /// СВОЯ ПАМЯТЬ ПАР ТРАНСЛОКАТОРОВ: где вход, куда ведёт, когда проверено.
    /// Второй источник знания о сети — тот, который работает без веб-карты.
    /// </summary>
    public TlMemory Translocations { get; }

    /// <summary>Эта память на диске (bottranslocators.json рядом с botsession.json).</summary>
    public TlMemoryStore TranslocationsFile { get; }

    /// <summary>Стройка ради подъёма: лестница по стене, столб под ногами.</summary>
    public Scaffolding Scaffolding { get; }

    /// <summary>Временные леса: что поставлено на время и снимается в конце работы.</summary>
    public TempBlocks Temp => Context.Temp;

    /// <summary>Сверка готовой постройки с замыслом: лишнее снять, недостающее назвать.</summary>
    public BuildCheck Check => Context.Check;

    /// <summary>Крафт по рецептам сервера.</summary>
    public Crafting Crafting { get; }

    /// <summary>Добыча камня шахматкой.</summary>
    public Quarry Quarry { get; }

    /// <summary>
    /// Возврат к прерванному: что прервали, вернутся ли и почему нет.
    /// Живёт в контексте (рядом с очередью на тело) — здесь только удобный
    /// вход для роли и окна управления.
    /// </summary>
    public Resume Resume => Context.Resume;

    /// <summary>Штольня: прокопать ход туда, куда пешком не пройти.</summary>
    public Tunnel Tunnel { get; }

    /// <summary>Дороги: замостить путь, по которому ходить быстрее.</summary>
    public Roads Roads { get; }

    /// <summary>Склад: куда носить добытое и куда возвращаться работать.</summary>
    public Depot Depot { get; }

    /// <summary>
    /// Снабжение: где взять недостающее — сумки, склад работы, дом, крафт,
    /// добыть рядом. Одна цепочка на все работы бота.
    /// </summary>
    public Supply Supply { get; }

    /// <summary>Запасы: что держать при себе и чем это пополнять.</summary>
    public Stock Stock { get; }

    /// <summary>
    /// Запас по расходу: «чем пользуюсь — то и держу про запас». Стрелы к луку,
    /// факелы к тьме, бинты к ранам.
    /// </summary>
    public Upkeep Upkeep { get; }

    /// <summary>Разведка: уйти под землю и искать руду там, где её не видно.</summary>
    public Prospecting Prospecting { get; }

    /// <summary>Наряд: «принеси столько-то того-то» — от слов до сумки.</summary>
    public MiningOrder Order { get; }

    /// <summary>Поручения: «добудь столько-то того-то и принеси».</summary>
    public Errands Errands { get; }

    /// <summary>Шаблоны построек: снять понравившееся, сохранить, построить.</summary>
    public Blueprints Blueprints { get; }

    /// <summary>Руководство игры (клавиша H): рецепты, источники, что можно сделать.</summary>
    public Handbook Handbook { get; }

    /// <summary>
    /// Читы. Выключены; включаются явно и громко пишут о себе в журнал.
    /// Читер в две строки: <c>bot.Cheats.Enabled = true;</c> и нужный флаг.
    /// </summary>
    public Cheats Cheats => Context.Cheats;


    /// <summary>Всё вышеперечисленное одним объектом — для способностей и навыков.</summary>
    /// <summary>
    /// Координаты в том виде, в каком их показывает игра игроку: отсчёт от
    /// точки спавна мира. Внутри бот живёт в абсолютных мировых координатах
    /// (там середина карты — 512000), и без перевода бот с человеком говорят
    /// о разных местах.
    /// </summary>
    public string PlayerCoords(double x, double y, double z) =>
        WorldOrigin.SayStanding(x, y, z);

    /// <summary>
    /// Сдвиг между координатами, которые видит игрок, и внутренними мировыми.
    ///
    /// СПРАШИВАЕТСЯ У СВОЕГО КЛИЕНТА, А НЕ У ОБЩЕГО НАЧАЛА ОТСЧЁТА, и разница
    /// тут не теоретическая. <see cref="WorldOrigin"/> хранит начало отсчёта
    /// ОДНО НА ПРОЦЕСС — иначе <see cref="BlockPos.ToString"/> печатать нечем
    /// (клетка не знает, чья она). Но вопрос «а вошёл ли Я в мир» —
    /// ПРО ЭТОГО БОТА, и общим полем на него не ответишь: сосед по процессу,
    /// вошедший в свой мир, отвечал бы «да» и за нас.
    ///
    /// ЖИВОЙ СЛУЧАЙ, НА КОТОРОМ ЭТО И ПОЙМАНО (стенд, но беда настоящая):
    /// проверки «до входа в мир склад не ставится» и «карта отказывается»
    /// краснели и зеленели ПО ОЧЕРЕДИ, в зависимости от того, какой соседний
    /// стенд успел прислать своему боту пакет спавна. То есть ответ на вопрос
    /// «я в мире?» приходил от чужого бота — молча, и в живом процессе с двумя
    /// ботами он приходил бы точно так же.
    ///
    /// АРИФМЕТИКА ПРИ ЭТОМ ОДНА: клетку из присланной сервером точки считает
    /// <see cref="WorldOrigin.Cell"/> — тот же расчёт, которым живёт и общее
    /// начало отсчёта. Своего отсечения дроби здесь нет и быть не может.
    /// </summary>
    public (int X, int Z) CoordOffset =>
        Client.SpawnPosition is { } s ? WorldOrigin.Cell(s.X, s.Z) : (0, 0);

    /// <summary>
    /// Клетка, названная ПО-ИГРОВОМУ (как показывает игра игроку), во
    /// внутренних координатах мира. Обратный ход к <see cref="PlayerCoords"/>.
    ///
    /// Высота у игры абсолютная и не сдвигается — сдвигаются только X и Z.
    /// </summary>
    public BlockPos WorldCell(double x, double y, double z)
    {
        // Прибавляет WorldOrigin.ToWorld — своё сложение здесь было второй
        // копией правила, а копии переводов и разъезжались: панель клала в
        // ручку «Склад» игровую точку, а снабжение искало по ней сундук
        var (wx, wy, wz) = WorldOrigin.ToWorld(
            (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z), CoordOffset);
        return new BlockPos(wx, wy, wz);
    }

    /// <summary>Своя позиция так, как её видит игрок в игре.</summary>
    public string PlayerCoordsHere() =>
        Self.Position is { } p ? PlayerCoords(p.X, p.Y, p.Z) : "неизвестно";

    /// <summary>
    /// Тело на игровой физике: единственный владелец позиции. Пока движение
    /// переводится на него, старый Movement ещё работает параллельно.
    /// </summary>
    public Body Body { get; }

    /// <summary>Сеть транслокаторов: что видел бот и куда оно ведёт.</summary>
    public TranslocatorMap Translocators { get; }

    /// <summary>Слово сервера о переносе: «тебя перенесли» по каналу «tpManager».</summary>
    public TranslocatorWatch Translocating { get; }

    /// <summary>
    /// МАРШРУТЫ ПО СЕТИ ТРАНСЛОКАТОРОВ с карты сервера: «через 3 транслокатора,
    /// пешком 240 бл, около 4 мин» — и мнение карты о том, куда ведёт каждая
    /// пластина.
    ///
    /// Сама по себе молчит и никуда не ходит: адрес карты, файл сети и цены
    /// пути — ручки роли (см. <see cref="LivingRole"/>), потому что адреса и
    /// пороги в этом проекте политика, а не механизм. Пока роль их не задала,
    /// маршрут честно отказывает словами и объясняет, что вписать.
    /// </summary>
    public TranslocatorRouting Routes { get; }

    public BotContext Context { get; }

    /// <summary>Фоновые рефлексы (поесть, отбиться, поглядывать по сторонам).</summary>
    public BehaviorRunner Behaviors { get; }

    /// <summary>Активные навыки: целевые действия по команде.</summary>
    public SkillRunner Skills { get; }

    /// <summary>Команды чата.</summary>
    public ChatCommands Commands { get; }

    /// <summary>Роль, под которой работает бот (если задана).</summary>
    public BotRole? Role { get; private set; }

    /// <summary>
    /// КТО ЗАПИСЫВАЕТ НАКРУЧЕННЫЕ РУЧКИ РОЛИ в файл готового бота. Ставится
    /// снаружи, потому что «куда писать» знает не бот, а тот, кто держит
    /// библиотеку пресетов и настройки запуска: у приложения это запуск
    /// (Program.cs), а окно управления, если оно поднято, ставит своего —
    /// у окна библиотека и настройки могут быть другими.
    ///
    /// ЗАЧЕМ СЮДА ВООБЩЕ ЗАВЕДЕНА ДВЕРЬ. Жалоба заказчика дословно:
    /// «запоминание не работает у дома и склада». Дом и склад можно задать
    /// ДВУМЯ способами — в окне управления и словом в чате («!дом тут»,
    /// «!склад тут»), — а записывающий ручки был ровно один и сидел внутри
    /// окна. Всё, что крутили мимо окна, доживало до перезапуска и пропадало:
    /// человек говорил боту «!дом тут», бот отвечал «Запомнил дом», и после
    /// перезапуска дома не было.
    ///
    /// ВТОРОГО МЕХАНИЗМА ЗАПИСИ ТУТ НЕТ И БЫТЬ НЕ ДОЛЖНО: это ссылка на тот же
    /// самый, единственный <see cref="PresetLibrary.ЗапомнитьРучки"/>. Своя
    /// запись ручек в роли разошлась бы с оконной в первый же вечер — другой
    /// пресет, другое имя роли, другой ответ человеку.
    /// </summary>
    public Func<BotRole, string>? ЗапомнитьРучкиРоли { get; set; }

    /// <summary>
    /// ЗАПОМНИТЬ НАКРУЧЕННОЕ — словами для человека, и всегда словами.
    ///
    /// Молчаливое «не записалось» — тот же дефект, что и молчаливая пропажа
    /// настройки: со стороны оно неотличимо от «записалось», и правду человек
    /// узнаёт только на следующем запуске.
    /// </summary>
    public string ЗапомнитьРоль() =>
        Role is not { } роль
            ? "роли нет — записывать нечего"
            : ЗапомнитьРучкиРоли is { } записать
                ? записать(роль)
                // Коротко НАРОЧНО: в чат уходит не больше двух сотен букв
                // (BotVoice.MaxChatLength), и причина обязана уместиться целиком —
                // обрезанный отказ читается как поломка, а не как отказ.
                //
                // У бота, поднятого запуском, записывающий стоит ВСЕГДА
                // (Program.cs), окно тут ни при чём. Пусто здесь бывает у бота,
                // собранного кодом в обход запуска, — и врать про --panel нельзя
                : "в файл НЕ записал: боту не задали, куда писать настройки роли — " +
                  "сказанное словом доживёт только до перезапуска";

    public string Name => Client.PlayerName;
    public bool Joined => Client.Joined;

    /// <summary>Единый поток сообщений о происходящем (для консоли или файла).</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// СТОРОЖ МОЛЧАНИЯ: молчал дольше положенного — скажи, чем живёшь.
    ///
    /// Живёт ЗДЕСЬ, а не внутри одной способности, ровно потому, что молчание
    /// не принадлежит никому: за него не отвечает ни движение, ни штольня, ни
    /// склад. Отвечает единый поток слов, а он — вот этот
    /// <see cref="OnLog"/>. Разбор живого случая — в <see cref="Heartbeat"/>.
    /// </summary>
    public Heartbeat Pulse { get; } = new();

    /// <summary>Бот вошёл в мир.</summary>
    public event Action? OnJoined;

    /// <summary>Сообщение чата (уже после команд — для своей логики).</summary>
    public event Action<ChatMessage>? OnChat;

    private readonly CancellationTokenSource lifetime = new();

    /// <summary>Слушатель <see cref="BotClient.OnOriginLearned"/> — держим, чтобы снять.</summary>
    private readonly Action<WorldOrigin.Spawn> сказатьОНачалеОтсчёта;

    public VsBot(string host, int port, string playerName, string playerUid,
        string? mpToken = null, string? serverPassword = null)
    {
        Client = new BotClient(host, port, playerName, playerUid, mpToken, serverPassword);
        World = new WorldModel(Client);
        // ЧУЖИЕ ЗАЯВКИ. Сервер присылает их все целиком сразу после входа
        // (пакет 75), и подписчика надо завести ДО RunAsync — иначе список
        // пролетит мимо, а бот будет думать, что приватов на сервере нет
        Claims = new Claims(Client);
        Entities = new EntityModel(Client);
        Self = new SelfState(Client, Entities, World);
        // РАСПОРЯДИТЕЛЬ ТЕЛА — ОДИН НА ВСЕГО БОТА, и заводится он ДО движения
        // нарочно: движение сверяет по нему, цел ли хозяин у похода, и второго
        // такого распорядителя быть не должно (разбор — у BodyArbiter.Era)
        var turn = new BodyArbiter();
        Movement = new Movement(Client, Entities, World, turn);
        Actions = new Actions(Client, Entities, Self);
        Body = new Body(Client, Entities, World);
        Movement.Attach(Body); // движение жмёт клавиши, тело двигает
        Movement.UseBlock = pos => Actions.UseBlockAsync(pos); // двери по дороге
        // Раненый без бинта не должен рисковать прыжками — движение спрашивает
        // об этом у инвентаря
        Movement.HasHealingItem = () =>
            Self.FindBestItem(s => s.Code != null && World.GetHealingByCode(s.Code) != null ? 1 : 0) != null;
        Translocators = new TranslocatorMap(World);
        // ПОДТВЕРЖДЕНИЕ ПЕРЕНОСА ОТ СЕРВЕРА. Канал «tpManager» объявляется
        // сразу после входа (пакет 56), поэтому слушателя заводим ДО RunAsync —
        // иначе объявление пролетит мимо и бот навсегда останется без слова
        // сервера о том, что его перенесли
        Translocating = new TranslocatorWatch(Client);
        // МАРШРУТЫ ПО КАРТЕ СЕРВЕРА. Сдвиг координат берётся ЖИВЫМ (функцией):
        // он приходит с точкой спавна уже в мире, а бот собирается до входа —
        // взятый сейчас, он был бы нулём, то есть промахом на полмиллиона
        // блоков. В сеть эта штука не полезет, пока роль не задаст адрес карты
        // или файл сети
        Routes = new TranslocatorRouting(() => CoordOffset);
        // СВОЯ ПАМЯТЬ ПАР — ДО первого чанка. Пары приезжают вместе с чанками
        // сразу после входа, и заведи мы память позже, первые из них ушли бы
        // мимо. Имя сервера кладём в файл: в другом мире те же координаты
        // означают другое место, и читать чужую память нельзя
        TranslocationsFile = new TlMemoryStore($"{host}:{port}");
        Translocations = TranslocationsFile.Load();
        Translocators.Memory = Translocations;
        Routes.Memory = Translocations;
        Context = new BotContext
        {
            Bot = Client,
            World = World,
            Entities = Entities,
            Self = Self,
            Movement = Movement,
            Actions = Actions,
            Translocators = Translocators,
            Body = Body,
            Turn = turn
        };
        Context.Translocating = Translocating;
        Context.Claims = Claims;
        Claims.OnLog += m => OnLog?.Invoke($"[владения] {m}");
        // НАЧАЛО ОТСЧЁТА — ОДНОЙ СТРОКОЙ, КАК ТОЛЬКО ЕГО СКАЗАЛ СЕРВЕР. С этого
        // мига все клетки в журнале, в чате и в окне управления называются
        // ИГРОВЫМИ; подменить смысл всех чисел молча нельзя — человек должен
        // прочитать, откуда они взялись.
        //
        // СЛУШАЕМ СВОЙ КЛИЕНТ, А НЕ ВЕСЬ ПРОЦЕСС. Прежде подписка висела на
        // статическом WorldOrigin.OnLearned, и о начале отсчёта говорил каждый
        // бот процесса — включая того, который в этот мир не входил. Снимать
        // подписку в Dispose приходилось по той же причине; теперь событие
        // принадлежит клиенту бота и умирает вместе с ним
        сказатьОНачалеОтсчёта = s => OnLog?.Invoke(
            $"[координаты] начало отсчёта мира — клетка спавна X={s.X}, Z={s.Z}. " +
            "Дальше клетки называю ИГРОВЫМИ: ровно те числа, что показывает окно " +
            "координат в игре (высота у неё своя, её никто не сдвигает)");
        Client.OnOriginLearned += сказатьОНачалеОтсчёта;
        Read = new Readables(Context);
        Hands = new Hands(Context);
        Mining = new Mining(Context, Hands);
        Clock = new GameClock(Client);
        Storms = new TemporalStorms(Client, Clock);
        // Список модов приходит ПЕРВЫМ же пакетом знакомства (1), каналы —
        // сразу за ним (56). Подписываться надо здесь, до RunAsync, иначе оба
        // пролетят мимо и бот на модовом сервере будет уверять, что модов нет
        Mods = new ServerMods(Client);
        // Каналы, которые библиотека РАЗБИРАЕТ: их типы лежат в сборках самой
        // игры и потому доступны нам без сборок модов. Отметка ставится отсюда,
        // а не внутри ServerMods: список «что понимаем» обязан расти вместе с
        // разборами, а не жить отдельным честным словом
        Mods.MarkUnderstood("temporalstability");
        Mods.MarkUnderstood(TranslocatorWatch.Channel);
        Fire = new Fire(Context, Hands, Mining);
        Cooking = new Cooking(Context, Hands, Fire);
        Butchering = new Butchering(Context, Hands);
        Foraging = new Foraging(Context, Hands, Mining);
        Context.Foraging = Foraging;
        Farming = new Farming(Context, Hands, Mining);
        Shelter = new Shelter(Context, Hands, Mining);
        Cold = new BodyHeat(Context);
        Wear = new ToolWear(Context);
        Sleeping = new Sleeping(Context, Hands);
        Lighting = new Lighting(Context, Hands, Mining);
        Light = new HandLight(Context);
        // ЩИТ ДЕЛИТ СЛОТ С ФОНАРЁМ, поэтому и механизм у них общий: щит не
        // заводит своего переноса в левую руку, а берёт тот же самый
        Shield = new Shielding(Context, Light);
        Context.Shield = Shield;
        Shield.OnLog += m => OnLog?.Invoke($"[щит] {m}");
        // ТИПЫ СУЩНОСТЕЙ ЛОВЯТСЯ ПЕРВЫМИ ИЗ ВСЕГО, ЧТО ЖИВЁТ НА ПАКЕТЕ 19:
        // по ним и бой отличает стрелу от твари, и эмоции узнают жесты игрока.
        // Создавать надо ДО RunAsync — реестр приходит сразу после входа
        // и пролетел бы мимо
        EntityTypes = new EntityTypes(Client);
        Context.EntityTypes = EntityTypes;
        EntityTypes.OnReady += (types, tags) =>
            OnLog?.Invoke($"[мир] реестр сущностей: {types} типов, {tags} признаков");
        EntityTypes.OnParseError += m => OnLog?.Invoke($"[мир] {m}");
        // ОБЪЯВЛЕННЫЕ СКОРОСТИ ТВАРЕЙ — с диска, из ассетов игры: серверных
        // задач ИИ (а с ними и movespeed) реестр по сети не шлёт вовсе
        // (см. CreatureSpeeds). Молчать об этом нельзя: без них бой при
        // неудавшемся замере отказывается и гнаться, и убегать
        Context.Speeds = CreatureSpeeds.Load();
        OnLog?.Invoke(Context.Speeds.Why is { } беда
            ? $"[мир] {беда}"
            : $"[мир] объявленных скоростей тварей: {Context.Speeds.Count}");
        // Эмоции берут список жестов игрока ИЗ ТОГО ЖЕ реестра: второго
        // разбора пакета 19 под сущности в проекте нет
        Emotes = new Emotes(Context);
        MeetingsFile = new MeetingStore();
        Meetings = MeetingsFile.Load();
        Deaths = new DeathRecovery(Context);
        // СТАБИЛЬНОСТЬ СОЗДАЁТСЯ ПОСЛЕ СМЕРТЕЙ НАРОЧНО: она спрашивает у их
        // правил мира, включена ли временная стабильность в этом мире вообще
        // (настройка мира приходит пакетами 1 и 21). И ДО RunAsync — канал
        // разломов объявляется сразу после входа и пролетел бы мимо
        Stability = new TemporalStability(Client, Entities, Deaths.Rules);
        // ЖУРНАЛ УРОНА СЕРВЕРА. Слушает чат-группу −5 и должен быть подписан
        // ДО входа в мир: первые удары приходят сразу, а пропущенный удар —
        // это пропущенная причина, из-за которой бой снова начнёт гадать
        Damage = new DamageWatch(Client);
        // ВСЯ ПОЛОСА ЗДОРОВЬЯ — РАЗБОРУ ЧАТА, чтобы он отличил возрождение от
        // заживления. Живой случай 23.08: «вылечился на 10012 хп» — это
        // сервер восстановил здоровье целиком после смерти, сложенный с
        // двадцатью каплями по 0,7
        Damage.MaxHealth = () => Self.MaxHealth;
        // Эти четверо ловят реестр сервера (пакет 19), который приходит
        // сразу после входа — создавать их надо ДО RunAsync, иначе рецепты
        // пролетят мимо и механизмы окажутся молча пустыми
        Knapping = new Knapping(Context);
        // ЛОТОК ЛОВИТ ТОТ ЖЕ ПАКЕТ 19: таблица выпадений приходит в атрибутах
        // блока, и без неё «чем закрыть flaxfibers» ответить нечем
        Panning = new Panning(Context, Hands, Mining);
        Gathering = new Gathering(Context, Hands, Mining);
        Pickup = new Pickup(Context);
        Context.Pickup = Pickup;
        Climbing = new Climbing(Context);
        Context.Climbing = Climbing;
        Ores = new Ores(Context);
        Context.Ores = Ores;
        // СКОЛЬКО МЕТАЛЛА В ВЕЩИ. Стоит рядом с рудой и до неё: наряд
        // спрашивает металл на каждой пересчётке, а руда — при первом же счёте
        Context.Metal = new Metals(Context);
        // ПАМЯТЬ ВСТРЕЧЕННОГО. Часы игровые: мир меняется в игровом времени,
        // а не в том, сколько бот простоял выключенным
        Resources = new ResourceMemory(() => Clock.TotalHours);
        Context.Resources = Resources;
        Ores.Memory = Resources;
        Scaffolding = new Scaffolding(Context);
        Context.Scaffolding = Scaffolding;
        // ЛЕСА СТОЯТ ПЕРЕД ВСЕМИ, КТО ИХ СТАВИТ. Докладывают сюда и стройка
        // (лестница, подпорка), и добыча (столб), а снимает всё это одна
        // уборка — иначе у каждой работы завёлся бы свой список забытых блоков
        Context.Temp = new TempBlocks(Context);
        Context.Temp.OnLog += m => OnLog?.Invoke($"[леса] {m}");
        Context.Check = new BuildCheck(Context);
        Context.Check.OnLog += m => OnLog?.Invoke($"[сверка] {m}");
        Crafting = new Crafting(Context);
        Errands = new Errands(Context, Hands, Mining, Gathering, Foraging);
        Blueprints = new Blueprints(Context, Hands, Mining);
        Handbook = new Handbook(Context, Crafting, Knapping, Errands);
        // Движение получает те же читы, что и весь бот: одна правда на всех
        Movement.Cheats = Context.Cheats;
        // Зрение тоже: пока «видеть сквозь стены» выключено, поиск блоков
        // находит только то, у чего есть открытая грань — как у человека.
        // Спрашиваем ЖИВОЙ флаг, а не копию: роль может включить чит позже
        // Через On(), а не напрямую: иначе чит можно включить и не заметить —
        // в журнале не будет ни строчки, и «честный» прогон окажется враньём
        World.SeeThroughWalls = () =>
            Context.Cheats.On(Context.Cheats.SeeThroughWalls, nameof(Cheats.SeeThroughWalls));
        // ЗАГЛЯДЫВАТЬ В КОНТЕЙНЕРЫ — ТОЛЬКО В ТЕ, ЧТО ОТКРЫВАЛ САМ. Сервер
        // присылает содержимое сундуков вместе с чанком, и бот пересчитывал
        // чужие закрытые сундуки за глухой стеной, не подходя к ним, — а флаг
        // SeeInsideContainers при этом уверял, что «подглядывать физически не
        // во что». Честная мерка одна: мы это открывали, и с тех пор прошло
        // немного (см. SelfState.SawInsideRecently)
        World.MayLookInside = pos =>
            Self.SawInsideRecently(pos, RememberInsideSeconds) ||
            Context.Cheats.On(Context.Cheats.SeeInsideContainers, nameof(Cheats.SeeInsideContainers));
        Context.Cheats.OnCheatUsed += m => OnLog?.Invoke($"[читы] {m}");
        Quarry = new Quarry(Context);
        // Штольня стоит ПОСЛЕ карьера нарочно: она спрашивает у него про
        // нестабильность потолка — правило обвалов живёт в одном месте
        Tunnel = new Tunnel(Context);
        Context.Tunnel = Tunnel;
        Tunnel.OnLog += m => OnLog?.Invoke($"[штольня] {m}");
        Roads = new Roads(Context);
        Context.Roads = Roads;
        Roads.OnLog += m => OnLog?.Invoke($"[дорога] {m}");
        Context.Errands = Errands;
        Depot = new Depot(Context);
        Context.Depot = Depot;
        Depot.OnLog += m => OnLog?.Invoke($"[склад] {m}");
        // Отказ склада читает ЧЕЛОВЕК, а он видит игровые числа. Живой случай:
        // склад «(512258, 111, 511740)» в отказе — это адрес, которого нет ни
        // на одном экране игрока, и понять по нему, куда бот собрался, нельзя
        Depot.PointName = c => "(" + PlayerCoords(c.X, c.Y, c.Z) + ")";
        // СНАБЖЕНИЕ — ПОСЛЕ СКЛАДА, КРАФТА И ПОРУЧЕНИЙ: оно ими и ходит, своей
        // походки, описи и рецептов у него нет
        Supply = new Supply(Context);
        Context.Supply = Supply;
        Supply.OnLog += m => OnLog?.Invoke($"[снабжение] {m}");
        // Запасы спрашивают и у крафта, и у поручений — поэтому после обоих
        Stock = new Stock(Context);
        Context.Stock = Stock;
        Stock.OnLog += m => OnLog?.Invoke($"[запасы] {m}");
        // Запас по расходу — СРАЗУ ЗА ЗАПАСАМИ: он подписывается на их опрос и
        // ставит им требования, своего списка не заводя
        Upkeep = new Upkeep(Context);
        Context.Upkeep = Upkeep;
        Upkeep.OnLog += m => OnLog?.Invoke($"[запас по расходу] {m}");
        // Разведка стоит последней: она пользуется и проходкой, и рудой
        Prospecting = new Prospecting(Context);
        Context.Prospecting = Prospecting;
        Prospecting.OnLog += m => OnLog?.Invoke($"[разведка] {m}");
        // Наряд последним: он распоряжается всеми остальными
        Order = new MiningOrder(Context);
        Context.Order = Order;
        Order.OnLog += m => OnLog?.Invoke($"[наряд] {m}");
        Context.Knapping = Knapping;
        Context.Panning = Panning;
        Context.Gathering = Gathering;
        Context.Crafting = Crafting;
        Context.Quarry = Quarry;
        Knapping.OnLog += m => OnLog?.Invoke($"[тесать] {m}");
        Panning.OnLog += m => OnLog?.Invoke($"[лоток] {m}");
        Gathering.OnLog += m => OnLog?.Invoke($"[земля] {m}");
        Crafting.OnLog += m => OnLog?.Invoke($"[крафт] {m}");
        Quarry.OnLog += m => OnLog?.Invoke($"[карьер] {m}");
        // ШАПКА НЕЙТРАЛЬНАЯ, ПОТОМУ ЧТО ЗА НЕЙ ОБА НАПРАВЛЕНИЯ. Здесь стояло
        // «[подъём]», и живьём человек читал «[подъём] дороги ВНИЗ нет
        // (104 → 98)»: тот же род вранья, что чинили в самом ответе («!слезь»
        // говорил «наверх не вышло»), только в шапке. Climbing — это и
        // ClimbToAsync, и DescendToAsync, и слово над ними обязано молчать
        // про направление, раз его называет каждая строка сама
        Climbing.OnLog += m => OnLog?.Invoke($"[высота] {m}");
        Ores.OnLog += m => OnLog?.Invoke($"[руда] {m}");
        Resources.OnLog += m => OnLog?.Invoke($"[память] {m}");
        Scaffolding.OnLog += m => OnLog?.Invoke($"[стройка] {m}");
        // Подбор говорит и об удачах, и об отказах: «не взял, потому что…»
        // важнее, чем тишина, — именно тишина прятала потерянную добычу
        Pickup.OnPicked += (code, n) => OnLog?.Invoke($"[подбор] взял {code} x{n}");
        Pickup.OnSkipped += (code, why) => OnLog?.Invoke($"[подбор] не взял {code}: {why}");
        Context.Wear = Wear;
        Context.Sleeping = Sleeping;
        Context.Lighting = Lighting;
        Context.Deaths = Deaths;
        Context.Cold = Cold;
        Cold.OnLog += m => OnLog?.Invoke($"[холод] {m}");
        Sleeping.OnLog += m => OnLog?.Invoke($"[сон] {m}");
        Lighting.OnLog += m => OnLog?.Invoke($"[свет] {m}");
        Emotes.OnLog += m => OnLog?.Invoke($"[эмоции] {m}");
        MeetingsFile.OnLog += m => OnLog?.Invoke($"[встречи] {m}");
        TranslocationsFile.OnLog += m => OnLog?.Invoke($"[транслокатор] {m}");
        Translocations.OnLog += m => OnLog?.Invoke($"[транслокатор] {m}");
        // ПИШЕМ ФАЙЛ ПО НОВОСТИ, А НЕ ПО ТИКУ — и не чаще, чем раз в
        // SaveTranslocationsEvery. Своего будильника ради этого не заводим
        // (правило «одна механика в одном месте»): новость и есть тот самый
        // редкий миг, когда писать надо. А запись на выходе (Dispose) остаётся
        // — она добирает последние секунды перед остановкой
        Translocations.OnNews += СохранитьПары;
        // Приставка «[вещи]» была уже, а туда идут и правила мира про смерть,
        // и остаток возрождений, и журнал смертей — «вещи» их не называет
        Deaths.OnLog += m => OnLog?.Invoke($"[смерть] {m}");
        Context.Damage = Damage;
        // ОТКАЗ ГОВОРИТСЯ ВСЛУХ И ОДИН РАЗ. Без образцов из языковых файлов
        // причина урона боту недоступна, и он вернётся к прежней догадке
        // «здоровье упало — значит напали». Человек обязан это прочитать
        if (Damage.Why is { } why)
            OnLog?.Invoke($"[урон] {why}");
        Context.Hands = Hands;
        Context.Mining = Mining;

        // ЗАРОСЛИ НА ДОРОГЕ: ход зовёт добычу, упёршись в куст.
        //
        // Шов делается здесь, а не в конструкторе движения, потому что
        // добыча стоит НАД движением (Mining → Hands → Movement), и ссылка
        // вниз замкнула бы кольцо. Клетки движение даёт по уровню ног —
        // на сколько клеток вверх расчищать, знает сама добыча
        // (Mining.GrowthHeight), там же живут пороги и список бережёного
        // Выбраться из ямы: ступени, лестница по стене или столб под собой.
        // Движение само не строит — оно просит того, чьё это дело
        Movement.ClimbOut = async (height, ct) =>
            (await Scaffolding.GetOutAsync(height, ct)).Success;

        // Мост через пропасть и спуск с обрыва — ТЕМ ЖЕ ШВОМ, что и подъём:
        // движение замечает разрыв, а строит тот, чьё это дело. Без этих
        // строк новое молчит честно — бот скажет «мост тут был бы к месту, но
        // строить его некому (Movement.BuildBridge не подключён)», — и живой
        // случай «впереди обрыв, стою» повторился бы слово в слово
        Movement.BuildBridge = async (cells, ct) =>
            (await Scaffolding.BridgeAsync(cells, ct)).Success;
        Movement.GetDown = async (depth, ct) =>
            (await Scaffolding.GetDownAsync(depth, ct)).Success;

        // А ВЫБЕРУСЬ ЛИ Я ОБРАТНО — ТЕМ ЖЕ ШВОМ И РЯДОМ С САМИМ ПОДЪЁМОМ.
        //
        // ЭТОЙ СТРОКИ НЕ БЫЛО ВОВСЕ, и это не мелочь. Крючок
        // Movement.WayBackUp в проекте есть, его док-строка обещает
        // подключение «из VsBot к Scaffolding.WayBack», а все четыре живых
        // звонящих (частичный путь по плоскости, частичный путь к клетке,
        // дальняя дорога, спуск с уступа) уходили в ветку «спросить некому» и
        // печатали «вниз иду не спросясь: подъём обратно посчитать некому».
        // То есть вопрос, ради которого написаны и Walker.WayBackFromBelow, и
        // весь стенд ВыберусьЛиОбратноTests, живьём НЕ ЗАДАВАЛСЯ НИ РАЗУ, и
        // жалоба «застрял в яме» была ровно этим
        Movement.WayBackUp = Scaffolding.WayBack;

        // Сколько блоков в сумках годится под мост. Спрашиваем у ДОБЫЧИ, чтобы
        // ответ был один на всех: осколки камня stone-* в игре ПРЕДМЕТ, а не
        // блок, и второй список рано или поздно решил бы, что ими можно мостить
        Movement.BlocksInBags = () => Mining.SpareBlocks();

        // Запертая дверь — примета чужого места. Движение про заявки не знает
        // и знать не должно, поэтому шов: оно только сообщает, что дверь не
        // поддалась дважды, а куда это записать — дело владений
        Movement.OnDoorLocked += where => Claims.NoteLockedDoor(where);

        Movement.ClearGrowth = (cells, ct) =>
            Mining.ClearGrowthAsync(cells.SelectMany(Mining.GrowthCells), ct);

        // И ТА ЖЕ МЕЛОЧЬ, НО НА ЛУЧЕ. Рука упирается в снег так же, как ноги
        // (прогон 19.08, 21:12: «мешает snowlayer-2», восемь отказов за четыре
        // секунды), а знать про материалы и выпадения ей нечем — правило одно
        // и живёт в добыче. Шов тот же, что у расчистки строкой выше: клетка
        // здесь ОДНА нарочно, на луче мешает только ближняя
        Hands.SweepAside = (cell, ct) => Mining.SweepAsideAsync(cell, ct);

        // И ТОТ ЖЕ ОТВЕТ ПЛАНИРОВЩИКУ: раз тело умеет снести ветку, поиск
        // пути вправе провести маршрут сквозь крону — за надбавку
        // PathOptions.LeafBreakCost (4 блока бега за блок, то есть куст бот
        // снесёт, а ради двух блоков обойдёт, если обход короче восьми).
        // Спрашиваем МАТЕРИАЛ, а не список кодов: коды листвы врут на первом
        // же моде. Пока реестр блоков игры не собран, материал приходит Air —
        // признак честно отвечает «нет», и маршрут остаётся прежним.
        // Выключатель один на планирование и на руки: разреши обход, но
        // запрети ломать — и бот пошёл бы в куст лбом
        Movement.PathOptions.IsLeaves = (x, y, z) =>
            Movement.ClearGrowthOnWay &&
            World.BlockMaterial(x, y, z) == Vintagestory.API.Common.EnumBlockMaterial.Leaves;

        Context.Clock = Clock;
        Context.Storms = Storms;
        Context.Stability = Stability;
        Context.Read = Read;
        Hands.OnLog += m => OnLog?.Invoke($"[руки] {m}");
        // Отказы действий («я мёртв») — та же рука, и метка у них та же:
        // человеку важно не то, каким классом отказ выдан, а чем бот не смог
        Actions.OnLog += m => OnLog?.Invoke($"[руки] {m}");
        Mining.OnLog += m => OnLog?.Invoke($"[добыча] {m}");
        Storms.OnLog += m => OnLog?.Invoke($"[буря] {m}");
        Stability.OnLog += m => OnLog?.Invoke($"[стабильность] {m}");
        Mods.OnLog += m => OnLog?.Invoke($"[моды] {m}");
        Translocating.OnLog += m => OnLog?.Invoke($"[транслокатор] {m}");
        // Приставка СВОЯ: «сервер подтвердил перенос» и «карта сервера не
        // ответила» — разные события, и в одном потоке они читались бы как одно
        Routes.OnLog += m => OnLog?.Invoke($"[сеть транслокаторов] {m}");

        // ═════ ДАЛЬНЯЯ ДОРОГА УЗНАЁТ ПРО ТРАНСЛОКАТОРЫ ═════
        //
        // ЖАЛОБА ЗАКАЗЧИКА (01.09): «мы же записываем в нашу постоянную память
        // пары тл? а то бот побежал вместо тл». Память была, счёт маршрута был,
        // поход по нему был — и звался ровно из одного места: щелчок по карте в
        // окне управления (ControlPanel.MapGoTo). Любая другая дальняя дорога
        // шла прямо в ноги, а ноги про пары не знают ничего.
        //
        // ПОХОД ОДИН НА ВЕСЬ СЕАНС, а не по одному на каждую дорогу: у него
        // свой журнал и своя подписка, и новый на каждый шаг означал бы новую
        // подписку на каждый шаг. Тело он не забирает (см. Trek.TryRideAsync) —
        // его берёт тот, кто позвал дорогу.
        //
        // ПОРОГА ЗДЕСЬ НЕТ НИ ОДНОГО: с какой дальности искать переход, решает
        // РОЛЬ (ИскатьТранслокаторОтБлоков), и до входа в мир движение честно
        // стоит на нуле, то есть сеть не спрашивает вовсе
        var походПоСети = new Trek(Context, Routes);
        походПоСети.OnLog += m => OnLog?.Invoke($"[поход] {m}");
        Movement.ByNet = (куда, секунд, ct) => походПоСети.TryRideAsync(куда, секунд, ct);
        Fire.OnLog += m => OnLog?.Invoke($"[огонь] {m}");
        Cooking.OnLog += m => OnLog?.Invoke($"[кухня] {m}");
        Butchering.OnLog += m => OnLog?.Invoke($"[разделка] {m}");
        Errands.OnLog += m => OnLog?.Invoke($"[поручение] {m}");
        Blueprints.OnLog += m => OnLog?.Invoke($"[стройка] {m}");
        Foraging.OnLog += m => OnLog?.Invoke($"[сбор] {m}");
        Farming.OnLog += m => OnLog?.Invoke($"[поле] {m}");
        Shelter.OnLog += m => OnLog?.Invoke($"[укрытие] {m}");
        Behaviors = new BehaviorRunner();
        // ПЛАНИРОВЩИК БЕЗ ЧУВСТВ СЛЕП, А БЕЗ ГОЛОСА НЕМ. Первое даёт кривым
        // весов живое состояние (сытость, холод, время до заката) и запускает
        // замер скоростей; второе выводит строку плана в общий журнал — без неё
        // «[план] …» не увидел бы никто. Сам револьвер по умолчанию выключен
        // (BehaviorRunner.UseRevolver), так что подключение ничего не меняет,
        // пока человек не включит планирование в настройках роли
        Behaviors.SenseFrom(Context);
        Behaviors.OnLog += m => OnLog?.Invoke($"[способности] {m}");
        Skills = new SkillRunner(Context);
        Commands = new ChatCommands(this);
        Read.OnLog += m => OnLog?.Invoke($"[чтение] {m}");

        // Сводим все источники сообщений в один поток
        Client.OnLog += m => OnLog?.Invoke(m);
        Movement.OnLog += m => OnLog?.Invoke($"[движение] {m}");
        Entities.OnParseError += m => OnLog?.Invoke($"[сущности] {m}");
        World.OnParseError += m => OnLog?.Invoke($"[мир] {m}");
        World.OnGameBlocksReady += () => OnLog?.Invoke("[мир] блоки игры собраны — физика готова");
        Body.OnLog += m => OnLog?.Invoke($"[тело] {m}");
        // Спор за тело шёл МОЛЧА: рефлекс отбирал ход у наряда, наряд у
        // рефлекса, а в журнале не было ни строчки — со стороны это выглядело
        // как «бот вдруг перестал копать». Кого прервали и кому отказали,
        // видно только отсюда
        Context.Turn.OnLog += m => OnLog?.Invoke($"[тело] {m}");
        // ВОЗВРАТ К ПРЕРВАННОМУ говорит своей приставкой, а не приставкой тела:
        // «перебил рефлекс» и «возвращаюсь доделывать» — разные события, и в
        // одном потоке они читались бы как одно
        Context.Resume.OnLog += m => OnLog?.Invoke($"[возврат] {m}");
        Entities.OnOwnPositionOverridden += m => OnLog?.Invoke($"[позиция] {m}");
        Behaviors.OnBehaviorError += (b, ex) => OnLog?.Invoke($"[способность {b.GetType().Name}] {ex.Message}");
        Commands.OnCommandError += (name, ex) => OnLog?.Invoke($"[команда {name}] {ex.Message}");
        Skills.OnLog += m => OnLog?.Invoke($"[навык] {m}");
        Skills.OnSkillCompleted += (name, r) =>
            OnLog?.Invoke(SkillLine(name, r.Success, r.Message, JustHeard));
        Self.OnDeath += () => OnLog?.Invoke("погиб, возрождаюсь");

        // ЖУРНАЛ ПОМНИТ СВОИ ПОСЛЕДНИЕ СТРОКИ — только затем, чтобы не сказать
        // одно и то же дважды (см. SkillLine). Подписка на СВОЙ ЖЕ OnLog, как
        // и у пульса: это единственное место, где видны все слова бота разом
        OnLog += Remember;

        // ПУЛЬС СЛУШАЕТ ОБЩИЙ ПОТОК СЛОВ, А БЬЁТСЯ ОТ ЦИКЛА СПОСОБНОСТЕЙ.
        //
        // Подписка идёт на СВОЙ ЖЕ OnLog, и это не хитрость, а единственное
        // место, где видно ВСЕ слова бота разом: их говорят шесть десятков
        // подсистем, и спрашивать каждую значило бы завести шесть десятков
        // копий одного сторожа. Своя строка пульса приходит сюда же — и это
        // правильно: пульс тоже слово, и отсчёт молчания начинается заново.
        OnLog += _ => Pulse.Heard(DateTime.UtcNow);
        Pulse.OnLog += m => OnLog?.Invoke($"[пульс] {m}");
        Behaviors.OnTick += () => Pulse.Beat(
            DateTime.UtcNow,
            Context.Turn.Busy,
            Skills.CurrentSkill ?? "",
            Behaviors.LastActed?.Who ?? "",
            Self.Position is { } p ? PlayerCoords(p.X, p.Y, p.Z) : "");

        Client.OnChat += line =>
        {
            Commands.Handle(line);
            OnChat?.Invoke(line);
        };
        Client.OnJoined += HandleJoined;
        Client.OnDisconnected += reason => OnLog?.Invoke($"отключён сервером: {reason}");
        Body.IsDead = () => Self.IsDead;

        // Новая сессия — прошлый мир недействителен: чанки, сущности и
        // инвентари придут заново, а старые данные будут врать
        Client.OnSessionReset += () =>
        {
            World.Reset();
            Entities.Reset();
            // Реестр придёт заново, и это не мелочь: биты тегов означают слова
            // только вместе со списком имён ЭТОГО сервера. На другом наборе
            // модов те же биты назвали бы другие признаки
            EntityTypes.Reset();
            Self.Reset();
            Hands.Reset();
            Lighting.Reset();

            // Новое соединение — возможно, ДРУГОЙ МИР. Записи прошлого мира
            // живут ещё 72 игровых часа, и «где я видел медь» назвало бы
            // координаты чужой карты, а наряд честно ушёл бы туда впустую.
            // Собственная защита памяти ловит только часы, ушедшие НАЗАД, —
            // то есть мир с более поздним календарём её обманывает
            Resources.Clear("новое соединение — мир мог смениться");
        };

        // Активный слот хотбара помнит СЕРВЕР (он переживает выход из игры).
        // Без этого бот считает, что держит слот 0, и ставит не тот блок
        Client.OnPacket += p =>
        {
            if (p is { Id: 53, SelectedHotbarSlot: { } s })
                Hands.NoteServerSlot(s.SlotNumber);
        };
        Client.OnReconnecting += n => OnLog?.Invoke($"возвращаюсь в мир (попытка {n})");
    }

    /// <summary>
    /// СЛЕД ЖИВОЙ РОЛИ — по нему её и снимают. Пишется вокруг
    /// <see cref="BotRole.Configure"/> и живёт ровно столько, сколько роль.
    /// </summary>
    private СледРоли? следРоли;

    /// <summary>
    /// ПОВЕРНУЛИ РУЧКУ РОЛИ — записать это в след, чтобы потом снялось.
    ///
    /// Ручку крутят В ОКНЕ, уже в мире, то есть ПОСЛЕ сборки роли: разница
    /// вокруг <c>Configure</c> о таком повороте не знает. Не пиши мы его сюда —
    /// накрученное после сборки осталось бы на новой роли (см.
    /// <see cref="СледРоли.Дописать"/>: читерское зрение копателя на стражнике).
    /// </summary>
    public void ЗаписатьВСлед(Action действие)
    {
        // ОДИН ЗАМОК НА НАДЕВАНИЕ, СНЯТИЕ И ЗАПИСЬ СЛЕДА. Ручку крутят из окна
        // (свой поток на каждый запрос), роль меняют оттуда же, а «!дом тут»
        // приходит из потока чата. Сойдись поворот ручки со сменой роли — и
        // след дописался бы в ту роль, которую в этот миг уже снимают
        lock (замокРоли)
        {
            if (следРоли is { } след)
                след.Дописать(действие);
            else
                действие();
        }
    }

    /// <summary>Надевание, снятие роли и запись её следа не идут одновременно.</summary>
    private readonly Lock замокРоли = new();

    /// <summary>
    /// РОЛЬ СМЕНИЛАСЬ НА ХОДУ. Нужно тем, кто снял у роли что-то СВОЁ и держит
    /// это у себя: очередь работ помнит каталог видов работ роли
    /// (<c>WorkRunner.Catalog</c>), окно управления — набор умений
    /// (<c>ControlPanel.Tools</c>). Оба сняты один раз, при сборке, и о смене
    /// сами не узнают: без этого события заказчик после смены роли получил бы
    /// очередь, предлагающую работы ПРЕЖНЕГО бота.
    /// </summary>
    public event Action<BotRole?, BotRole>? OnRoleChanged;

    /// <summary>
    /// Назначить роль: она подключает способности, навыки и команды.
    ///
    /// ВТОРОЙ РАЗ ЗВАТЬ НЕЛЬЗЯ — это <see cref="СменитьРоль"/>. Второй
    /// <c>UseRole</c> даёт ДВУБОТА: <c>Configure</c> только добавляет, и на
    /// одном теле оказываются два комплекта рефлексов. Отказаться исключением
    /// здесь нельзя (звалок больше сотни, и половина — чужие стенды), поэтому
    /// бот говорит об этом вслух и называет, чем звать вместо.
    /// </summary>
    public VsBot UseRole(BotRole role)
    {
        lock (замокРоли)
        {
            if (Role is { } уже && !меняюРоль)
                OnLog?.Invoke($"[роль] «{уже.Name}» УЖЕ надета, а UseRole зовут второй раз: " +
                              $"на одном теле будет два комплекта рефлексов. " +
                              $"Менять роль надо через СменитьРоль — он сперва снимает прежнюю");

            Role = role;
            role.Хозяин = this;
            // СЛЕД СНИМАЕТСЯ ВСЕГДА, А НЕ «КОГДА СОБЕРУТСЯ МЕНЯТЬ РОЛЬ». Снять
            // его потом нельзя в принципе: разница «до и после Configure»
            // существует только в тот миг, когда Configure зовут. Не сними мы её
            // здесь — смена роли осталась бы рукописным списком полей навсегда
            следРоли = СледРоли.Записать(this, role, () => role.Configure(this));
            foreach (string беда in следРоли.Жалобы)
                OnLog?.Invoke($"[роль] след снят не весь: {беда}");
            return this;
        }
    }

    /// <summary>Идёт смена роли: второй UseRole сейчас законен и не жалуется.</summary>
    private bool меняюРоль;

    /// <summary>
    /// СТАТЬ ДРУГИМ БОТОМ, НЕ ПЕРЕЗАПУСКАЯСЬ.
    ///
    /// СЛОВА ЗАКАЗЧИКА 23.08: «хочется смену настроек и роли без перезапуска».
    /// До этого «применить готового бота» в окне не делало НИЧЕГО: писало
    /// выбор в bot.json и просило перезапустить бота руками — а руки у
    /// заказчика запускают .bat, где стоит своё «--preset digger», и
    /// поднимался прежний бот.
    ///
    /// ПОЧЕМУ ЭТО НЕ ВТОРОЙ <see cref="UseRole"/>. Второй UseRole дал бы не
    /// полубота, а ДВУБОТА: <c>Configure</c> только ДОБАВЛЯЕТ — 24 способности
    /// выживания, команды, навыки, подписки, — и второй набор лёг бы поверх
    /// первого. Два <c>BehaviorIdle</c> ушли бы гулять каждый в свою сторону, а
    /// <c>Behaviors.Get&lt;T&gt;()</c> отдаёт ПЕРВЫЙ найденный — значит новые
    /// ручки настраивали бы старые копии. Поэтому здесь сперва СНИМАЮТ.
    ///
    /// ЧТО ЗДЕСЬ ДЕЛАЕТСЯ, ПО ПОРЯДКУ И ПОЧЕМУ ИМЕННО В ТАКОМ:
    ///   1. останавливается цикл способностей — иначе прежние рефлексы тикают
    ///      ровно в тот миг, когда их вынимают из списка;
    ///   2. честно обрывается и НАЗЫВАЕТСЯ начатое: навык, держатель тела.
    ///      Молча брошенный карьер — это «бот вдруг встал», а такое заказчик
    ///      уже видел;
    ///   3. роль гасит своё живое (<see cref="BotRole.Снять"/>): фоновый цикл
    ///      думающей роли не поле и не подписка, обходом его не снять, а
    ///      оставленный — он продолжает тратить ключ заказчика;
    ///   4. откатывается след — механически, разницей снимков;
    ///   5. надевается новая роль и, если бот уже в мире, ей дают войти.
    ///
    /// Отказ здесь невозможен «наполовину молча»: всё, что не вернулось,
    /// названо поимённо в <see cref="СменаРоли.НеВернулось"/>.
    /// </summary>
    public СменаРоли СменитьРоль(BotRole новая)
    {
        lock (замокРоли)
            return СменитьРольПодЗамком(новая);
    }

    /// <summary>
    /// Сколько ждать, пока начатое дело отпустит тело при смене роли.
    ///
    /// ЧИСЛО ЧИТАЕТСЯ У РАСПОРЯДИТЕЛЯ ТЕЛА, А НЕ ПИШЕТСЯ ЗДЕСЬ ЗАНОВО
    /// (<see cref="BodyArbiter.QuietRefusalSeconds"/>). Дело, не отпустившее
    /// тело за срок, после которого распорядитель уже перестаёт повторять про
    /// него вслух, и есть упрямое. Своей тройкой это число здесь СТОЯЛО, и
    /// приписка обещала вывод из чужой постоянной, которого в коде не было:
    /// поверни человек ручку распорядителя — и два числа разъехались бы молча.
    /// </summary>
    public TimeSpan ЖдатьОстановкиДела =>
        TimeSpan.FromSeconds(Math.Max(0, Context.Turn.QuietRefusalSeconds));

    private СменаРоли СменитьРольПодЗамком(BotRole новая)
    {
        var часы = System.Diagnostics.Stopwatch.StartNew();
        var прежняя = Role;
        string? былоИмя = прежняя?.Name;
        var брошено = new List<string>();

        // 1. Цикл способностей — стоп. Он крутится в своём потоке и трогает
        // ровно те объекты, которые сейчас будут вынуты
        sessionWork?.Cancel();

        // 2. Что бот вёл — назвать и оборвать
        if (Skills.CurrentSkill is { Length: > 0 } навык)
        {
            брошено.Add($"навык «{навык}»");
            Skills.CancelCurrent($"смена роли: {былоИмя} → {новая.Name}");
            // ОТМЕНА — ЭТО ПРОСЬБА, А НЕ ФАКТ. Навык узнаёт о ней на ближайшей
            // проверке своего признака и доматывает свои finally; вынь мы у
            // него из-под ног способности и пороги в ту же миллисекунду —
            // получим падение внутри чужого дела вместо честной остановки.
            // Ждём недолго и НАЗЫВАЕМ, если не дождались
            var ждём = DateTime.UtcNow + ЖдатьОстановкиДела;
            while (Skills.CurrentSkill is { Length: > 0 } && DateTime.UtcNow < ждём)
                Thread.Sleep(50);
            if (Skills.CurrentSkill is { Length: > 0 } упрямый)
                брошено.Add($"навык «{упрямый}» не отпустил тело за " +
                            $"{ЖдатьОстановкиДела.TotalSeconds:0} с — роль снимаю поверх него");
        }
        if (Context.Turn.Current is { } держит)
        {
            брошено.Add($"тело держал «{держит.Owner}»");
            Context.Turn.Interrupt($"смена роли: {былоИмя} → {новая.Name}");
        }

        // 3. Живое роли — гасит сама роль: полем оно не бывает
        if (прежняя is { } была)
        {
            try
            {
                foreach (string что in была.Снять(this))
                    брошено.Add(что);
            }
            catch (Exception e)
            {
                брошено.Add($"роль «{былоИмя}» не погасила своё: {e.GetType().Name}: {e.Message}");
            }
        }

        // 4. След — назад
        IReadOnlyList<string> неВернулось = следРоли?.Откатить() ?? [];
        int тронуто = следРоли?.Тронуто ?? 0;
        if (прежняя is { } ушла)
            ушла.Хозяин = null;   // её ручки больше не пишутся в след: она не на боте

        // 5. Новая роль. Не собралась — говорим это ВСЛУХ и называем, чем
        // бот теперь стал: своя роль заказчика приходит отдельной DLL
        // (Roles.ПапкаРолей), и её Configure вправе упасть на чём угодно.
        // Промолчи мы — бот остался бы без рефлексов, молча и до утра
        меняюРоль = true;
        try
        {
            UseRole(новая);
        }
        catch (Exception e)
        {
            неВернулось = [.. неВернулось,
                $"новая роль «{новая.Name}» не собралась ({e.GetType().Name}: {e.Message}) — " +
                "бот сейчас БЕЗ рабочей роли, перезапустите его"];
        }
        finally
        {
            меняюРоль = false;
        }
        OnRoleChanged?.Invoke(прежняя, новая);

        // Бот уже в мире: новая роль входа не видела, а без входа она не
        // здоровается, не переводит дом в мировые клетки и не начинает дела
        if (Joined)
        {
            _ = Task.Run(async () =>
            {
                try { await новая.OnJoinedAsync(this); }
                catch (Exception e) { OnLog?.Invoke($"[роль] вход новой роли: {e.Message}"); }
            });
            ЗапуститьЦиклСпособностей();
        }

        return new СменаРоли(былоИмя, новая.Name, брошено, неВернулось, тронуто, часы.Elapsed);
    }

    /// <summary>
    /// ЧТО БОТ ГОВОРИТ ЛЮДЯМ В ИГРОВОЙ ЧАТ — сама строка, до отправки.
    ///
    /// Зачем событие есть. Ответы команд уходят в чат и БОЛЬШЕ НИКУДА: журнал
    /// хозяина их не видит, а <see cref="ChatCommands.RunAsync"/> возвращает
    /// позвавшему лишь слово «выполнено». Приёмка нашла на этом целый мёртвый
    /// тест: «команда отвечает и про число, и про разломы» оставался зелёным,
    /// даже когда команда падала на каждом вызове, — содержимого ответа не
    /// видел никто.
    ///
    /// Событие говорит «БОТ ЭТО СКАЗАЛ», а не «сервер это принял»: ушла ли
    /// строка на самом деле, знает только клиент, и об этом он говорит своей
    /// строкой («нет соединения — действие не ушло на сервер»).
    /// </summary>
    public event Action<string>? OnSay;

    /// <summary>
    /// ГОЛОС БОТА: пускать ли строку в игровой чат (радиотишина и её поводы).
    /// Ворота одни на всех и живут у клиента — там единственная дверь в чат;
    /// здесь только удобный путь к ним для роли и панели.
    /// </summary>
    public BotVoice Voice => Client.Voice;

    /// <summary>
    /// Написать в чат (сообщения не теряются: очередь с учётом лимита сервера).
    ///
    /// СОБЫТИЕ ТЕПЕРЬ ПОСЛЕ ВОРОТ, А НЕ ДО. <see cref="OnSay"/> значит «бот это
    /// сказал», и при включённой радиотишине проглоченная строка не сказана
    /// никому: сообщи мы о ней, проверки по чату (см. LivingRole) зеленели бы
    /// на боте, который на самом деле промолчал.
    /// </summary>
    /// <param name="reason">
    /// По какому поводу это сказано; заводское «приказ» — «сказать велели прямо»
    /// (кнопка в окне, консоль, модель), такую строку не глушит ничто.
    /// </param>
    public async Task SayAsync(string message, int groupId = 0,
        SayReason reason = SayReason.Приказ)
    {
        // СОБЫТИЕ НЕСЁТ ТО, ЧТО УШЛО В ЧАТ, а не то, что подали: длинный отчёт
        // ворота укорачивают (BotVoice.ForChat), и «бот сказал» — это короткая
        // строка. Скажи мы здесь длинную, окно управления и проверки по чату
        // видели бы одно, а игроки в чате — другое
        if (await Client.SendChatAsync(message, groupId, reason) is { } сказано)
            OnSay?.Invoke(сказано);
    }

    /// <summary>Сообщить о происходящем в общий поток (консоль, файл — как настроено).</summary>
    public void Log(string message) => OnLog?.Invoke(message);

    // ============ ОДИН ОТЧЁТ — ОДНА СТРОКА В ЖУРНАЛЕ ============

    /// <summary>
    /// Последние сказанные строки. Больше десятка не надо: между отчётом
    /// работы и строкой её навыка живьём стоит одна-две чужих строки
    /// («[возврат] …»), а не сотня.
    /// </summary>
    private readonly Queue<string> heard = new();

    private const int HeardKept = 12;

    private void Remember(string line)
    {
        lock (heard)
        {
            heard.Enqueue(line);
            while (heard.Count > HeardKept)
                heard.Dequeue();
        }
    }

    /// <summary>Что бот сказал только что — снимок, потому что говорят из разных потоков.</summary>
    private string[] JustHeard
    {
        get { lock (heard) return [.. heard]; }
    }

    /// <summary>
    /// СТРОКА О КОНЦЕ НАВЫКА — И НИ ОДНОГО ЛИШНЕГО ЭКЗЕМПЛЯРА ОТЧЁТА В НЕЙ.
    ///
    /// ЖИВОЙ СЛУЧАЙ 22.08, 17:18:35. Один отказ дороги занял в журнале четыре
    /// строки, и три из них были одной и той же простынёй на десять строк
    /// экрана: сама работа («[дорога] дорога 0 рядов из 29 … ПРОХОДКА
    /// ВСТАЛА…»), потом «[навык дорога] не вышло: &lt;та же простыня&gt;», потом
    /// «[коротко в чат] … ЦЕЛИКОМ: &lt;она же&gt;». Заказчик просил КОРОТКУЮ строку
    /// в чат — и получил её, а в журнале от этого стало вдвое хуже.
    ///
    /// ГОВОРИТ РАБОТА, А НАВЫК — ТОЛЬКО РАМКА. Отчёт пишет тот, кто его сочинил
    /// (<c>Roads</c>, <c>Quarry</c>, <c>Tunnel</c> и прочие job'ы: у каждого в
    /// конце <c>OnLog(report.ToString())</c>). Навыку остаётся сказать, ЧТО
    /// именно кончилось и чем, — а причину не пересказывать. Ровно так уже
    /// говорит возврат к прерванному: «до конца НЕ довёл — работа встала сама
    /// (причина в её отчёте)».
    ///
    /// НО МОЛЧАТЬ НЕЛЬЗЯ, И ПОЭТОМУ ПРАВИЛО НЕ «ВСЕГДА МОЛЧИ». Половина
    /// навыков отказывает СВОИМИ словами, которых не писал никто:
    /// «нужно: дорога &lt;x1&gt; &lt;y1&gt; &lt;z1&gt; …», «копать нечем — кирки нет»,
    /// «отменён», «ошибка: …». Проглоти мы их — и человек прочтёт «не вышло»
    /// без единого слова почему. Поэтому спрашиваем журнал: сказано ли это уже.
    ///
    /// Чистая функция без времени и мира — правило 7 проекта: живьём такое
    /// ловится только полным прогоном дороги, то есть почти никогда.
    /// </summary>
    /// <param name="name">Имя навыка.</param>
    /// <param name="ok">Вышло ли.</param>
    /// <param name="message">Что навык вернул наверх (может быть пусто).</param>
    /// <param name="heard">Строки, которые журнал уже принял.</param>
    public static string SkillLine(string name, bool ok, string? message,
        IEnumerable<string> heard)
    {
        string исход = ok ? "готово" : "не вышло";
        if (message is not { Length: > 0 } слова)
            return $"[навык {name}] {исход}";
        // Сравниваем ВХОЖДЕНИЕМ, а не равенством: работа пишет отчёт со своей
        // приставкой («[дорога] дорога 0 рядов…»), и равными эти строки не
        // будут никогда
        return heard.Any(с => с.Contains(слова, StringComparison.Ordinal))
            ? $"[навык {name}] {исход} — отчёт работы сказан выше"
            : $"[навык {name}] {исход}: {слова}";
    }

    /// <summary>
    /// Подключиться и работать, пока не отключат. Цикл способностей запускается
    /// сам после входа в мир.
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            await Client.RunAsync();
        }
        catch (OperationCanceledException)
        {
            // штатное завершение
        }
    }

    private CancellationTokenSource? sessionWork;

    private void HandleJoined()
    {
        OnJoined?.Invoke();
        // Тело на игровой физике: оно же шлёт позицию 15 раз в секунду.
        // Старый поток позиции из Movement больше не нужен
        Body.Start(lifetime.Token);

        ЗапуститьЦиклСпособностей(вход: true);
    }

    /// <summary>
    /// ПУСТИТЬ ЦИКЛ СПОСОБНОСТЕЙ ЗАНОВО, погасив прежний.
    ///
    /// Заводов у цикла ДВА, и место у них одно нарочно: вход в мир (он бывает
    /// не один раз — после обрыва связи бот возвращается сам) и смена роли на
    /// ходу (<see cref="СменитьРоль"/>: старые рефлексы вынимают из списка, и
    /// тикать в этот миг некому). Заведи мы второй пуск отдельной строкой —
    /// два цикла на одно тело подрались бы за него, а со стороны это «бот
    /// дёргается и ничего не доводит до конца».
    /// </summary>
    /// <param name="вход">
    /// Правда — это вход в мир: даём серверу договорить и пускаем роль
    /// здороваться. На смене роли вход в мир уже был, и ждать нечего.
    /// </param>
    private void ЗапуститьЦиклСпособностей(bool вход = false)
    {
        sessionWork?.Cancel();
        sessionWork = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var ct = sessionWork.Token;

        _ = Task.Run(async () =>
        {
            if (вход)
            {
                // Способностям нужны данные о мире и о себе — даём серверу договорить
                await Task.Delay(3000, ct).ContinueWith(_ => { });
                if (ct.IsCancellationRequested)
                    return;
                if (Role != null)
                    await Role.OnJoinedAsync(this);
            }
            // Цикл способностей — сердце бота: пока он крутится, бот ест,
            // лечится и отбивается. Падал он молча (задача брошена без
            // присмотра), и внешне это выглядело как «бот вдруг перестал
            // есть». Теперь падение видно, и цикл поднимается заново
            while (!ct.IsCancellationRequested && Behaviors.Count > 0)
            {
                try
                {
                    await Behaviors.StartAsync(ct);
                    return;   // вышли по отмене — так и надо
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[способности] цикл упал ({ex.GetType().Name}: {ex.Message}) — поднимаю заново");
                    await Task.Delay(1000, ct).ContinueWith(_ => { });
                }
            }
        });
    }

    /// <summary>
    /// Не писать память пар на диск чаще, чем раз в столько времени. Тем же
    /// сроком и по той же причине, что у книги встреч
    /// (<c>BehaviorGreetPlayers.SaveEverySeconds</c>): проходя мимо гнезда
    /// транслокаторов, бот узнаёт десяток пар за секунду, и файл не должен
    /// переписываться десять раз подряд.
    /// </summary>
    public static readonly TimeSpan SaveTranslocationsEvery = TimeSpan.FromSeconds(30);

    private DateTime парыЗаписаныВ = DateTime.MinValue;
    private readonly object парыЗамок = new();

    /// <summary>
    /// Записать память пар, если пора. Отказ диска не должен ронять бота: он
    /// идёт словами в журнал, а память остаётся в руках до выхода.
    /// </summary>
    private void СохранитьПары()
    {
        lock (парыЗамок)
        {
            if (DateTime.UtcNow - парыЗаписаныВ < SaveTranslocationsEvery)
                return;
            парыЗаписаныВ = DateTime.UtcNow;
        }
        try
        {
            TranslocationsFile.Save(Translocations);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"[транслокатор] память пар не легла в файл: {e.Message}");
        }
    }

    public void Dispose()
    {
        // КНИГА ВСТРЕЧ ПИШЕТСЯ НА ВЫХОДЕ. Способность откладывает запись на
        // полминуты, чтобы не тереть диск на каждого прохожего, — и без этой
        // строки последние полминуты встреч терялись бы при каждом выходе.
        // Только если есть что писать: бот, никого не встретивший (а в стенде
        // это каждый второй), не должен переписывать чужую книгу поверх
        try { if (Meetings.Dirty) MeetingsFile.Save(Meetings); }
        catch (Exception e) { OnLog?.Invoke($"[встречи] книга не записалась на выходе: {e.Message}"); }
        // ПАМЯТЬ ТРАНСЛОКАТОРОВ — ТОЖЕ НА ВЫХОДЕ И ПО ТОЙ ЖЕ ПРИЧИНЕ. Пары
        // копятся весь сеанс, а бота перезапускают после каждой правки роли:
        // без этой строки всё, что он прошёл ногами за вечер, пропало бы к утру
        try { if (Translocations.Dirty) TranslocationsFile.Save(Translocations); }
        catch (Exception e)
        {
            OnLog?.Invoke($"[транслокатор] память пар не записалась на выходе: {e.Message}");
        }
        Client.OnOriginLearned -= сказатьОНачалеОтсчёта;
        lifetime.Cancel();
        Routes.Dispose();
        Client.Dispose();
    }
}
