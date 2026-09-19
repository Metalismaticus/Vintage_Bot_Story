using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace VsBotKit;

/// <summary>Конфиг не прочитался — с объяснением, что именно не так и где.</summary>
public sealed class BotConfigException : Exception
{
    public BotConfigException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>Настройки журнала в файле конфига (см. <see cref="BotLogOptions"/>).</summary>
public sealed class BotLogConfig
{
    /// <summary>Папка журналов (относительная — рядом с исполняемым файлом).</summary>
    public string Directory { get; set; } = "logs";

    public string FileName { get; set; } = "bot.log";

    /// <summary>Файл «важного»; пусто — не вести.</summary>
    public string ImportantFileName { get; set; } = "important.log";

    /// <summary>Порог ротации в мегабайтах (в файле удобнее в МБ, чем в байтах).</summary>
    public double MaxFileMb { get; set; } = 8;

    public int MaxFiles { get; set; } = 8;

    public double FlushSeconds { get; set; } = 2;

    public bool Utc { get; set; }

    /// <summary>Дублировать журнал в консоль.</summary>
    public bool Console { get; set; } = true;

    /// <summary>
    /// Слова, по которым строка считается важной. Это политика роли, поэтому
    /// список задаётся в конфиге, а не зашит в библиотеку.
    /// </summary>
    public string[] ImportantWords { get; set; } = [];

    /// <summary>Перевести в настройки журнала.</summary>
    public BotLogOptions ToOptions() => new()
    {
        Directory = Directory,
        FileName = FileName,
        ImportantFileName = ImportantFileName,
        MaxFileBytes = (long)Math.Max(0, MaxFileMb * 1024 * 1024),
        MaxFiles = Math.Max(1, MaxFiles),
        FlushInterval = TimeSpan.FromSeconds(Math.Clamp(FlushSeconds, 0.1, 600)),
        UseUtc = Utc,
        MirrorToConsole = Console,
        Important = ImportantWords.Length > 0 ? BotLogOptions.ByWords(ImportantWords) : null
    };
}

/// <summary>
/// Вход под аккаунтом Vintage Story.
///
/// ПАРОЛЯ ЗДЕСЬ НЕТ И НЕ БУДЕТ. Файл настроек человек показывает, копирует и
/// пересылает; пароль в нём рано или поздно утечёт. Поэтому пароль берётся из
/// переменной окружения (<see cref="PasswordEnv"/>) или спрашивается при
/// запуске, а на диске остаётся только ключ сессии — в отдельном файле,
/// закрытом от чужих (см. <see cref="SessionStore"/>).
/// </summary>
public sealed class BotAccountConfig
{
    /// <summary>Входить под аккаунтом. Выключено — старый способ, без проверки.</summary>
    public bool Use { get; set; }

    /// <summary>Почта аккаунта. Пароль сюда не пишется.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// Взять готовую сессию из настроек УСТАНОВЛЕННОЙ игры (clientsettings.json).
    /// Годится, когда человек уже вошёл в игру на этой же машине: тогда пароль
    /// не нужен вовсе.
    /// </summary>
    public bool FromGame { get; set; }

    /// <summary>Файл, где бот хранит свою сессию.</summary>
    public string SessionFile { get; set; } = SessionStore.DefaultFileName;

    /// <summary>Из какой переменной окружения брать пароль, если он понадобится.</summary>
    public string PasswordEnv { get; set; } = "VS_PASSWORD";

    /// <summary>Из какой переменной окружения брать код двухфакторной проверки.</summary>
    public string CodeEnv { get; set; } = "VS_TOTP";
}

/// <summary>
/// Окно управления — страница в браузере, из которой бот виден и управляем.
///
/// ВЫКЛЮЧЕНО ПО УМОЛЧАНИЮ и включается явно: панель может увести бота и
/// поменять его настройки, а открывать такую дверь без спроса нельзя.
///
/// ПАРОЛЯ ЗДЕСЬ НЕТ И НЕ БУДЕТ — по тому же закону, что и в
/// <see cref="BotAccountConfig"/>: этот файл человек показывает, копирует и
/// пересылает вместе с ботом. Задаётся пароль переменной окружения или
/// вопросом при запуске (<see cref="VsBotKit.Panel.PanelPasswordSource"/>), а
/// здесь лежат только ИМЕНА переменных и «спросить ли».
/// </summary>
public sealed class PanelConfig
{
    /// <summary>Открывать окно управления.</summary>
    public bool Use { get; set; }

    /// <summary>Порт на петле. Игра 42420, дверь мода 42461, панель 42462.</summary>
    public int Port { get; set; } = 42462;

    /// <summary>Сразу открыть страницу в браузере этой машины.</summary>
    public bool OpenBrowser { get; set; } = true;

    /// <summary>
    /// Из какой переменной окружения брать пароль администратора для входа в
    /// окно. Пусто в переменной — окно работает по-старому, ключом в ссылке.
    ///
    /// ЖИВОЙ СЛУЧАЙ: «админ пароль для вебформы на сервере». На сервере ссылку
    /// с ключом приходится каждый раз копировать из консоли, и она оседает в
    /// истории браузера; пароль набирают на странице входа.
    /// </summary>
    public string PasswordEnv { get; set; } = "VS_PANEL_PASSWORD";

    /// <summary>
    /// Из какой переменной окружения брать готовый ОТПЕЧАТОК пароля
    /// («pbkdf2-sha256$…», см. <see cref="VsBotKit.Panel.PanelPassword"/>).
    ///
    /// Для службы на сервере: в файле запуска systemd оседает отпечаток, а не
    /// пароль, — войти по отпечатку нельзя, панель ждёт сам пароль. Задан —
    /// сильнее <see cref="PasswordEnv"/>.
    /// </summary>
    public string PasswordHashEnv { get; set; } = "VS_PANEL_PASSWORD_HASH";

    /// <summary>
    /// Спросить пароль окна при запуске — ввод БЕЗ ЭХА, как пароль аккаунта.
    /// Годится, когда бота запускают руками из терминала.
    /// </summary>
    public bool AskPassword { get; set; }

    /// <summary>
    /// Не лезть на сервер при запуске, а ЖДАТЬ команды из окна.
    ///
    /// Так и просил хозяин: «запуск бота — не запуск входа на сервер». Тогда
    /// адрес, пароль сервера и то, под кем входить — ником или аккаунтом, —
    /// выбираются в окне и меняются без перезапуска программы.
    /// </summary>
    public bool WaitForStart { get; set; } = true;

    /// <summary>
    /// КАРТА В ОКНЕ: где бот, где дом, где склад, где запомненная руда.
    ///
    /// Заказ заказчика дословно: «хочу вывод положения игрока на вебкарте, но
    /// КАК ДОП НАСТРОЙКА». Поэтому здесь и поэтому false.
    ///
    /// ПОЧЕМУ ВЫКЛЮЧЕНА ПО УМОЛЧАНИЮ — ЭТО ПРО СЕРВЕР. Бот живёт фоном на
    /// сервере, где на окно неделями никто не смотрит. Карта же на КАЖДЫЙ
    /// опрос страницы перебирает память руды (до
    /// <see cref="ResourceMemory.Capacity"/> клеток), точки склада и книгу
    /// встреч, переводит их в игровые координаты и складывает в JSON. Там, где
    /// её никто не открывает, это чистая трата памяти и работы, — а бот на
    /// сервере делит машину с самим сервером игры.
    ///
    /// Что рисуется и в каких границах — <see cref="VsBotKit.Panel.PanelMapRules"/>.
    /// Своей памяти у карты нет: она показывает то, что бот и так знает.
    /// </summary>
    public bool Map { get; set; }

    /// <summary>
    /// МЕСТА ВСТРЕЧ С ЖИВЫМИ ЛЮДЬМИ на карте. Выключено отдельно от
    /// <see cref="Map"/> и тоже по умолчанию.
    ///
    /// ПОЧЕМУ ОТДЕЛЬНЫМ ВЫКЛЮЧАТЕЛЕМ. Карту включают ради себя — посмотреть,
    /// где бот и где склад. Имена чужих игроков на ней — совсем другое
    /// решение: книга встреч это записи о человеке, который о них не знает и
    /// согласия не давал (см. <see cref="Meeting"/>). Одним выключателем на
    /// оба случая человек включал бы второе, желая первого.
    ///
    /// Включённое, оно показывает ровно два сведения — ИМЯ и МЕСТО последней
    /// встречи. Ни времени, ни счёта встреч, ни того, здоровался ли бот, из
    /// книги наружу не уходит, и никуда, кроме этой страницы на петле, карта
    /// ничего не отправляет.
    /// </summary>
    public bool MapPeople { get; set; }

    /// <summary>
    /// АДРЕС ВЕБ-КАРТЫ СЕРВЕРА — той самой, что висит у сервера в браузере.
    ///
    /// Заказ заказчика дословно: «под вебкартой я имел ввиду поддержку карты по
    /// типу https://map.tops.vintagestory.at/?x=0&amp;y=0&amp;zoom=5». То есть
    /// не наша самодельная картинка, а карта САМОГО СЕРВЕРА, и на ней — метка
    /// бота.
    ///
    /// Задан адрес — панель кладёт картинки той карты подложкой под свои точки
    /// (бот, дом, склад), и место бота видно на настоящей карте мира. Пусто —
    /// вкладка работает ровно как раньше, своей сеткой: карта сервера есть не
    /// у каждого, и её отсутствие не беда.
    ///
    /// СЮДА КЛАДУТ АДРЕС СТРАНИЦЫ ЦЕЛИКОМ, прямо из строки браузера — хвост
    /// «?x=0&amp;y=0&amp;zoom=5» бот отбросит сам
    /// (<see cref="VsBotKit.Panel.WebMapRules.Base"/>). Ни ключей, ни паролей
    /// в этот адрес не подставляется: бот только читает оттуда картинки.
    ///
    /// ЧУЖИХ ИГРОКОВ ЭТО НЕ ПОКАЗЫВАЕТ. Такая карта — выгрузка мира, а не живая
    /// связь; положений игроков она не отдаёт вовсе
    /// (см. <see cref="VsBotKit.Panel.WebMapRules"/>).
    /// </summary>
    public string MapUrl { get; set; } = "";
}

/// <summary>
/// Читы в файле настроек — чтобы не выбирать их заново на каждый запуск.
///
/// ЗАЧЕМ ЭТО ВООБЩЕ ЕСТЬ. Хозяин просил: «чтобы каждый раз одно и то же не
/// выбирать в разных настройках». Панель — единственное место, где читы
/// включают, и без записи в файл каждый перезапуск сбрасывал их молча.
///
/// ЧЕМ ЭТО ОПАСНО И ЧТО С ЭТИМ СДЕЛАНО. Чит, поднявшийся из файла, — это
/// «случайно осталось со вчера», ровно то, от чего сторожит главный
/// выключатель. Поэтому: всё здесь по умолчанию ВЫКЛЮЧЕНО (свежий bot.json
/// читов не несёт), а <see cref="ApplyTo"/> возвращает список поднятого —
/// и тот, кто её зовёт, обязан сказать это вслух в журнал. Молча включённый
/// чит хуже, чем чит.
/// </summary>
public sealed class CheatsConfig
{
    /// <summary>Главный выключатель. Пока он выключен, остальные не действуют.</summary>
    public bool Use { get; set; }

    public bool SeeThroughWalls { get; set; }
    public bool NoLineOfSight { get; set; }
    public bool InfiniteReach { get; set; }
    public bool InstantMining { get; set; }
    public bool IgnoreToolTier { get; set; }
    public bool Teleport { get; set; }
    public bool SeeInsideContainers { get; set; }
    public bool NoFallDamage { get; set; }

    /// <summary>Дальность руки, когда включён <see cref="Cheats.InfiniteReach"/>.</summary>
    public double ReachDistance { get; set; } = 64;

    /// <summary>Снять с живых читов то, что стоит сейчас (панель щёлкнула флажком).</summary>
    public CheatsConfig CaptureFrom(Cheats cheats)
    {
        Use = cheats.Enabled;
        SeeThroughWalls = cheats.SeeThroughWalls;
        NoLineOfSight = cheats.NoLineOfSight;
        InfiniteReach = cheats.InfiniteReach;
        InstantMining = cheats.InstantMining;
        IgnoreToolTier = cheats.IgnoreToolTier;
        Teleport = cheats.Teleport;
        SeeInsideContainers = cheats.SeeInsideContainers;
        NoFallDamage = cheats.NoFallDamage;
        ReachDistance = cheats.ReachDistance;
        return this;
    }

    /// <summary>
    /// Поднять читы из файла и вернуть СПИСОК ПОДНЯТОГО словами. Список
    /// возвращается, а не пишется внутрь, потому что журнал заводит
    /// приложение; но пустой список тут значит «ничего не включено», и
    /// молчание в этом случае — правда, а не умолчание.
    /// </summary>
    public IReadOnlyList<string> ApplyTo(Cheats cheats)
    {
        cheats.Enabled = Use;
        cheats.SeeThroughWalls = SeeThroughWalls;
        cheats.NoLineOfSight = NoLineOfSight;
        cheats.InfiniteReach = InfiniteReach;
        cheats.InstantMining = InstantMining;
        cheats.IgnoreToolTier = IgnoreToolTier;
        cheats.Teleport = Teleport;
        cheats.SeeInsideContainers = SeeInsideContainers;
        cheats.NoFallDamage = NoFallDamage;
        cheats.ReachDistance = ReachDistance;

        var on = new List<string>();
        void Say(bool flag, string name)
        {
            if (flag) on.Add(name);
        }
        Say(SeeThroughWalls, nameof(Cheats.SeeThroughWalls));
        Say(NoLineOfSight, nameof(Cheats.NoLineOfSight));
        Say(InfiniteReach, nameof(Cheats.InfiniteReach));
        Say(InstantMining, nameof(Cheats.InstantMining));
        Say(IgnoreToolTier, nameof(Cheats.IgnoreToolTier));
        Say(Teleport, nameof(Cheats.Teleport));
        Say(SeeInsideContainers, nameof(Cheats.SeeInsideContainers));
        Say(NoFallDamage, nameof(Cheats.NoFallDamage));

        if (on.Count == 0)
            return [];

        // Флаги без главного выключателя не действуют, и человек должен
        // узнать это из журнала, а не из странного поведения бота
        return Use
            ? on
            : on.Append("НО главный выключатель читов выключен — ни один из них не действует")
                .ToList();
    }
}

/// <summary>
/// СЕРВЕРНЫЙ РЕЖИМ: «не отжирать лишнего по оперативке».
///
/// ОТКУДА ЧИСЛА. Замерено на живом сервере 11.08 (VsBotKit.Tests, времянка
/// «ЖивойЗамер»), бот стоял в обычной местности с дальностью 128 блоков:
///   • пришло 392 чанка; пока их не читают, все вместе они весят 1,15 МБ;
///   • ПЕРВОЕ ЖЕ ЧТЕНИЕ клетки разжимает свой чанк целиком: int[32768] =
///     131 072 Б на слой блоков и столько же на слой воды;
///   • после обычного осмотра местности (поиск пути, разведка) — 20,5 МБ;
///   • после широкого осмотра, где тронут каждый чанк, — 67,4 МБ.
/// То есть 99 % веса модели мира — это РАЗЖАТЫЙ КЭШ, который никогда не
/// выбрасывался. Он же и есть главная добыча: выбросить его можно БЕЗ ЕДИНОГО
/// ВРАНЬЯ, потому что он пересчитывается из сжатых байтов слово в слово.
///
/// С этим набором на том же месте: 175 чанков, потолок 16,2 МБ (было 67,4),
/// блок-сущностей 648 вместо 1189. Плата — время: три полных обхода всех
/// чанков подряд заняли 56 мс вместо 38.
///
/// ПОПРАВКА К ЧИСЛУ ЧАНКОВ, ЧЕСТНО. Те 175 намерены тогда, когда берег памяти
/// резал и по высоте, — а так делать нельзя: сервер шлёт чанки полными
/// столбами, и забытый слой второй раз не приедет (см.
/// <see cref="WorldModel.BeyondKeep"/>). Теперь столб держится целиком, и
/// чанков при радиусе 3 будет до (2·3+1)² × 8 = 392, как без набора. ПОТОЛОК
/// ВЕСА от этого не двинулся: его держит <see cref="DecodedChunks"/>
/// (64 × 256 КБ = 16 МБ), а лишние сжатые чанки весят по 1,1 КБ — четверть
/// мегабайта на все.
///
/// ОДИН ПЕРЕКЛЮЧАТЕЛЬ, НО КРУТИТСЯ ПО ОДНОМУ. <see cref="Use"/> включает
/// набор целиком; каждое поле, если оно ЗАДАНО, перебивает значение набора.
/// Пустое поле — «как в наборе». Ноль в числовом поле — «без предела», то
/// есть как было до этого набора.
///
/// ПОКА <see cref="Use"/> ВЫКЛЮЧЕН, НЕ ДЕЙСТВУЕТ НИЧЕГО — по тому же правилу,
/// что и у <see cref="CheatsConfig"/>: набор, который применяется наполовину,
/// хуже, чем невключённый, потому что объяснить поведение бота становится
/// нечем.
/// </summary>
public sealed class ServerModeConfig
{
    /// <summary>Главный переключатель набора.</summary>
    public bool Use { get; set; }

    /// <summary>
    /// Радиус мира в ЧАНКАХ ПО ГОРИЗОНТАЛИ: и сколько бот ПРОСИТ у сервера, и
    /// сколько помнит. 3 чанка = 96 блоков. 0 — без предела (просить и помнить
    /// как раньше). Меньше <see cref="WorldModel.MinKeepChunkRadius"/> не
    /// бывает. По высоте столб держится целиком — вертикального запроса в
    /// протоколе нет, и забытый слой сервер не пришлёт заново.
    /// </summary>
    public int? ChunkRadius { get; set; }

    /// <summary>
    /// Сколько чанков держать РАЗЖАТЫМИ. Главная экономия и единственная,
    /// которая ничего не стоит в правде: 64 чанка ≈ 16 МБ потолка.
    /// 0 — без предела.
    /// </summary>
    public int? DecodedChunks { get; set; }

    /// <summary>Сколько строк журнала держать в окне (замерено 210 Б на строку).</summary>
    public int? PanelLogLines { get; set; }

    /// <summary>
    /// Украшения окна: подписи к настройкам, читаемые из файла документации
    /// (замерено 2,6 МБ на 4332 подписи). false — окно работает без них и
    /// честно говорит, что подписи выключены, а не молчит.
    /// </summary>
    public bool? Decorations { get; set; }

    /// <summary>Значения набора, когда поле не задано.</summary>
    public const int DefaultChunkRadius = 3;
    public const int DefaultDecodedChunks = 64;
    public const int DefaultPanelLogLines = 100;
    public const bool DefaultDecorations = false;

    /// <summary>Радиус мира, который действует сейчас (0 — без предела).</summary>
    public int EffectiveChunkRadius => Use ? ChunkRadius ?? DefaultChunkRadius : 0;

    /// <summary>Бюджет разжатых чанков, который действует сейчас (0 — без предела).</summary>
    public int EffectiveDecodedChunks => Use ? DecodedChunks ?? DefaultDecodedChunks : 0;

    /// <summary>Строк журнала в окне; 0 — «сколько было» (решает панель).</summary>
    public int EffectivePanelLogLines => Use ? PanelLogLines ?? DefaultPanelLogLines : 0;

    /// <summary>Показывать ли украшения окна.</summary>
    public bool EffectiveDecorations => !Use || (Decorations ?? DefaultDecorations);

    /// <summary>
    /// Наложить набор на модель мира и вернуть СПИСОК СДЕЛАННОГО словами —
    /// тем же приёмом, что и <see cref="CheatsConfig.ApplyTo"/>: молча
    /// урезанная память неотличима от потерянной.
    ///
    /// ЗВАТЬ ДО ВХОДА НА СЕРВЕР: радиус уходит в самом первом пакете опознания
    /// (<see cref="BotClient.ViewDistance"/>), после входа его менять поздно.
    /// </summary>
    public IReadOnlyList<string> ApplyTo(WorldModel world)
    {
        var сделано = new List<string>();
        if (!Use)
            return сделано;

        int радиус = EffectiveChunkRadius;
        if (радиус > 0)
        {
            world.KeepChunkRadius = радиус;
            сделано.Add(
                $"мир помню на {world.KeepChunkRadius} чанк. вокруг себя по горизонтали " +
                $"({world.KeepChunkRadius * WorldModel.ChunkSize} бл) — столько же и прошу у сервера; " +
                "по высоте столб держу целиком (иначе забытый слой сервер не пришлёт заново); " +
                "что дальше — забываю честно, «не знаю», а не «пусто»" +
                (радиус != world.KeepChunkRadius
                    ? $" (просили {радиус}, но меньше {WorldModel.MinKeepChunkRadius} нельзя)"
                    : ""));
        }

        int бюджет = EffectiveDecodedChunks;
        if (бюджет > 0)
        {
            world.DecodedChunkBudget = бюджет;
            сделано.Add(
                $"разжатыми держу не больше {бюджет} чанков (≈{бюджет * 256 / 1024.0:0.#} МБ потолка) — " +
                "знание при этом целое: выброшенное разжимается заново из сжатых байтов");
        }

        return сделано;
    }

    /// <summary>Снять с живых настроек то, что стоит сейчас (окно щёлкнуло).</summary>
    public ServerModeConfig CaptureFrom(WorldModel world, int panelLogLines, bool decorations)
    {
        ChunkRadius = world.KeepChunkRadius;
        DecodedChunks = world.DecodedChunkBudget;
        PanelLogLines = panelLogLines;
        Decorations = decorations;
        return this;
    }
}

/// <summary>
/// Настройки запуска бота в JSON-файле плюс аргументы командной строки.
///
/// Зачем: после ночного прогона надо знать, с какими настройками бот работал,
/// и уметь изменить их, не пересобирая проект. Аргумент командной строки
/// всегда перекрывает файл — так удобно гонять один и тот же конфиг с разными
/// именами/портами.
///
/// Раздел <see cref="Role"/> библиотека НЕ разбирает: что там за пороги,
/// радиусы и дом — знает только роль. Библиотека даёт механизм чтения
/// (<see cref="RoleAs{T}"/>, <see cref="RoleNumber"/>, <see cref="RolePos"/>),
/// а смысл ключей — политика роли.
/// </summary>
public sealed class BotConfig
{
    /// <summary>Имя файла по умолчанию (ищется рядом с исполняемым файлом).</summary>
    public const string DefaultFileName = "bot.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,   // конфиг с комментариями читаемее
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Иначе кириллица уезжает в бот и файл нельзя править руками
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    // --- подключение ---

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 42420;

    /// <summary>Имя игрока (оно же видно в чате).</summary>
    public string Name { get; set; } = "Bot";

    /// <summary>Uid игрока: сервер по нему отличает бота от других игроков.</summary>
    public string Uid { get; set; } = "bot-local-uid";

    /// <summary>
    /// Токен мультиплеера строкой.
    ///
    /// Годится ТОЛЬКО для сервера, где проверка аккаунта выключена
    /// (serverconfig.json, VerifyPlayerAuth = false) — например, для своего
    /// испытательного. На настоящем сервере токен одноразовый и выпускается
    /// под каждое соединение: для него нужен раздел <see cref="Account"/>.
    /// </summary>
    public string? MpToken { get; set; }

    /// <summary>Вход под аккаунтом Vintage Story (для серверов с проверкой).</summary>
    public BotAccountConfig Account { get; set; } = new();

    /// <summary>Окно управления в браузере.</summary>
    public PanelConfig Panel { get; set; } = new();

    /// <summary>Пароль сервера, если он задан.</summary>
    public string? ServerPassword { get; set; }

    /// <summary>
    /// Читы, поднятые из файла. По умолчанию выключены все — см. <see cref="CheatsConfig"/>.
    /// </summary>
    public CheatsConfig Cheats { get; set; } = new();

    /// <summary>
    /// Серверный режим: не отжирать лишнего по оперативке (см. <see cref="ServerModeConfig"/>).
    /// По умолчанию выключен — на своей машине памяти не жалко, а платить за
    /// неё скоростью разжатия без спроса нельзя.
    /// </summary>
    public ServerModeConfig ServerMode { get; set; } = new();

    /// <summary>
    /// Файл очереди задач. Пусто — <c>botjobs.json</c> рядом с ботом (там же,
    /// где botsession.json). Заведено по образцу
    /// <see cref="BotAccountConfig.SessionFile"/>: путь достраивает сам
    /// <see cref="WorkPlanStore"/>, второго правила достройки не заводим.
    /// </summary>
    public string? JobsFile { get; set; }

    // --- флаги ---

    /// <summary>
    /// Готовый бот из папки presets: «кем быть». Пусто — роль выбирается
    /// аргументами запуска, как раньше.
    ///
    /// Порядок силы: пресет → раздел "role" этого файла → аргументы запуска.
    /// То есть пресет задаёт основу, а поправить её можно не трогая пресет —
    /// иначе правка «на один вечер» портила бы файл, которым делятся.
    /// </summary>
    public string? Preset { get; set; }

    /// <summary>Ботом управляет нейросеть.</summary>
    public bool Ai { get; set; }

    /// <summary>Подключить отладочные команды.</summary>
    public bool Diag { get; set; }

    /// <summary>Фразы, которые сказать в чат сразу после входа (прогон проверок).</summary>
    public string[] Script { get; set; } = [];

    // --- журнал ---

    public BotLogConfig Log { get; set; } = new();

    // --- настройки роли ---

    /// <summary>
    /// Сырой раздел "role" как есть. Разбирает его роль — своим типом
    /// (<see cref="RoleAs{T}"/>) или по ключам.
    /// </summary>
    public JsonElement? Role { get; set; }

    /// <summary>Откуда прочитан конфиг (или куда он был бы записан).</summary>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    /// <summary>Файла не было — создан пример; значения сейчас по умолчанию.</summary>
    [JsonIgnore]
    public bool CreatedSample { get; private set; }

    /// <summary>
    /// Токен или пароль сервера пришли АРГУМЕНТОМ ЗАПУСКА, а не из файла.
    ///
    /// Ради чего этот флаг заведён. <see cref="Save"/> сериализует объект
    /// целиком — значит автосохранение из панели перенесло бы секрет из
    /// командной строки в файл на диске, чего до появления автосохранения
    /// не происходило никогда. Человек запускал бота с «--token …» именно
    /// затем, чтобы токен нигде не осел. Поэтому при взведённом флаге панель
    /// автосохранение НЕ делает и ГОВОРИТ ПОЧЕМУ: молчаливое «не сохранилось»
    /// — дефект не меньший, чем утечка.
    ///
    /// В файл сам флаг не пишется: он про этот запуск, а не про настройки.
    /// </summary>
    [JsonIgnore]
    public bool SecretsFromCommandLine { get; private set; }

    // --- что задано строкой запуска ---
    //
    // ЖИВАЯ ПОТЕРЯ 22.08, РАДИ КОТОРОЙ ЭТОТ РАЗДЕЛ ЕСТЬ. Заказчик выбрал в окне
    // управления другого бота, окно честно ответило «пресет „trader“ выбран.
    // настройки записаны в …\bot.json. Роль меняется только при запуске —
    // перезапустите бота» — и в файле вправду встало "preset": "trader". А
    // запускает он бота через start-bot-panel.bat, где стоит «--preset digger»:
    // при следующем запуске строка запуска перебила файл, поднялся ПРЕЖНИЙ бот,
    // и в журнале об этом не было НИ СЛОВА («настройки: …, пресет digger»).
    // Со стороны человека это ровно «настройки не запоминаются»: он выбрал,
    // ему сказали «записано», и ничего не изменилось.
    //
    // Сильнее строка запуска остаётся — это правильно, её человек набирает
    // сейчас, а файл писан когда-то. Неправильным было МОЛЧАНИЕ: перебили и
    // не сказали. Отсюда два списка ниже и одна готовая причина для окна.

    /// <summary>Человеческие имена настроек — ими же зовёт их окно и журнал.</summary>
    public const string ИмяХост = "хост";
    public const string ИмяПорт = "порт";
    public const string ИмяИгрока = "имя";
    public const string ИмяUid = "uid";
    public const string ИмяАккаунт = "аккаунт";
    public const string ИмяПресет = "пресет";
    public const string ИмяОкно = "окно управления";
    public const string ИмяБраузер = "открывать браузер";
    public const string ИмяКарта = "карта";
    public const string ИмяЛюдиНаКарте = "имена людей на карте";
    public const string ИмяАдресКарты = "адрес карты сервера";
    public const string ИмяИи = "ии";
    public const string ИмяДиагностика = "диагностика";
    public const string ИмяПапкаЖурнала = "папка журнала";
    public const string ИмяСценарий = "фразы после входа";
    public const string ИмяЖурналВКонсоль = "журнал в консоль";

    /// <summary>
    /// Какой ключ запуска какую настройку задаёт и берёт ли он следующее слово
    /// значением. Таблица ОДНА на всех, кто спрашивает: и предупреждение
    /// человеку, и разбор ниже — иначе второй список однажды отстанет от
    /// первого и соврёт про то, что запомнится.
    ///
    /// «Берёт значение» здесь не украшение: без него «--preset digger» читалось
    /// бы как ключ плюс позиционный аргумент, и бот честно сообщал бы, что
    /// строка запуска задаёт ХОСТ «digger».
    /// </summary>
    private static readonly (string Ключ, string Имя, bool СоЗначением)[] КлючиИИмена =
    [
        ("--host", ИмяХост, true), ("--port", ИмяПорт, true),
        ("--name", ИмяИгрока, true), ("--uid", ИмяUid, true),
        ("--account", ИмяАккаунт, true), ("--no-account", ИмяАккаунт, false),
        ("--account-from-game", ИмяАккаунт, false),
        ("--preset", ИмяПресет, true),
        ("--panel", ИмяОкно, true), ("--no-panel", ИмяОкно, false),
        ("--no-browser", ИмяБраузер, false),
        ("--map", ИмяКарта, false), ("--no-map", ИмяКарта, false),
        ("--map-people", ИмяЛюдиНаКарте, false), ("--no-map-people", ИмяЛюдиНаКарте, false),
        ("--map-url", ИмяАдресКарты, true), ("--no-map-url", ИмяАдресКарты, false),
        ("--ai", ИмяИи, false), ("--no-ai", ИмяИи, false),
        ("--diag", ИмяДиагностика, false), ("--no-diag", ИмяДиагностика, false),
        ("--log-dir", ИмяПапкаЖурнала, true), ("--script", ИмяСценарий, true),
        ("--quiet", ИмяЖурналВКонсоль, false),
    ];

    /// <summary>
    /// Ключи запуска, которые берут следующее слово значением. Нужно стенду:
    /// иначе таблица выше и разбор ниже разъезжаются молча.
    /// </summary>
    public static IReadOnlyList<string> КлючиСоЗначением =>
        КлючиИИмена.Where(к => к.СоЗначением).Select(к => к.Ключ).ToList();

    /// <summary>
    /// ГДЕ КАЖДАЯ НАСТРОЙКА ЛЕЖИТ В ФАЙЛЕ — чтобы вернуть в запись ровно её,
    /// а не отказаться писать файл целиком.
    ///
    /// ЗАЧЕМ ЭТА ТАБЛИЦА ЕСТЬ. <see cref="Save"/> пишет объект ЦЕЛИКОМ, и на
    /// этом заказчик потерял две настройки: чужой прогон запускался с
    /// «--no-browser» и «--log-dir …», в окне повернулась одна ручка — и в его
    /// bot.json навсегда встали «"openBrowser": false» и папка журнала во
    /// временном каталоге. Разовый ключ прогона стал постоянной настройкой.
    ///
    /// ПОЧЕМУ НЕ «ПРОСТО НЕ СОХРАНЯТЬ». Так и было сделано в этой волне — и
    /// ломало окно: штатный start-bot.bat подаёт «--diag», у которого заводское
    /// значение «нет», значит на свежей установке ЛЮБОЙ запуск батником отнимал
    /// у человека запись из окна вообще всего — выбора готового бота, хоста,
    /// порта, имени. Это дословно возвращённая жалоба 22.08 «настройки не
    /// запоминаются», только другой дверью. Отказ обязан быть по КЛЮЧАМ.
    ///
    /// Имена полей — camelCase: ровно так их пишет <see cref="Json"/>.
    /// Настройка может лежать в двух полях сразу («окно управления» — это и
    /// «включено ли», и «на каком порту»), поэтому путей у имени список.
    /// </summary>
    private static readonly Dictionary<string, string[][]> ГдеВФайле = new()
    {
        [ИмяХост] = [["host"]],
        [ИмяПорт] = [["port"]],
        [ИмяИгрока] = [["name"]],
        [ИмяUid] = [["uid"]],
        [ИмяАккаунт] = [["account"]],
        [ИмяПресет] = [["preset"]],
        [ИмяОкно] = [["panel", "use"], ["panel", "port"]],
        [ИмяБраузер] = [["panel", "openBrowser"]],
        [ИмяКарта] = [["panel", "map"]],
        [ИмяЛюдиНаКарте] = [["panel", "mapPeople"]],
        [ИмяАдресКарты] = [["panel", "mapUrl"]],
        [ИмяИи] = [["ai"]],
        [ИмяДиагностика] = [["diag"]],
        [ИмяПапкаЖурнала] = [["log", "directory"]],
        [ИмяСценарий] = [["script"]],
        [ИмяЖурналВКонсоль] = [["log", "console"]],
    };

    /// <summary>
    /// Настройки, у которых у самих имя есть, а места в файле — нет. Пусто, и
    /// пусть остаётся пусто: имя без места означало бы, что запись такой
    /// настройки защитить нечем и об этом никто не узнает.
    /// </summary>
    public static IReadOnlyList<string> ИменаБезМестаВФайле =>
        new[] { ИмяХост, ИмяПорт, ИмяИгрока, ИмяUid, ИмяАккаунт, ИмяПресет, ИмяОкно, ИмяБраузер,
                ИмяКарта, ИмяЛюдиНаКарте, ИмяАдресКарты, ИмяИи, ИмяДиагностика, ИмяПапкаЖурнала,
                ИмяСценарий, ИмяЖурналВКонсоль }
        .Where(и => !ГдеВФайле.ContainsKey(и)).ToList();

    /// <summary>
    /// КАКИЕ НАСТРОЙКИ ЗАДАЁТ ЭТА СТРОКА ЗАПУСКА — чистое правило: ни файла,
    /// ни бота, потому и проверяется стендом.
    ///
    /// Считаем ЗАДАННЫМИ, а не «перебитыми»: пока ключ стоит в строке запуска,
    /// он перебьёт файл и в тот раз, когда значения совпадали, — то есть
    /// выбранное в окне не переживёт перезапуск, даже если сегодня разницы
    /// не видно. Обещать человеку обратное нельзя.
    ///
    /// Позиционные аргументы («VintageBotStory 127.0.0.1 42420 Бот uid») —
    /// такие же ключи, только без имени, и молчать о них было бы тем же
    /// молчанием.
    /// </summary>
    public static IReadOnlyList<string> ЧтоЗадаётСтрокаЗапуска(IEnumerable<string> args)
    {
        var имена = new List<string>();
        var позиционные = new List<string>();
        // Значение ключа («--preset digger») само ключом не считается, иначе
        // «--name --map» разобралось бы как две настройки вместо одной
        bool ждёмЗначение = false;

        foreach (string a in args)
        {
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                if (ждёмЗначение)
                    ждёмЗначение = false;
                else
                    позиционные.Add(a);
                continue;
            }
            ждёмЗначение = false;
            int eq = a.IndexOf('=');
            string ключ = eq > 0 ? a[..eq] : a;
            if (ключ is "--config" or "--token" or "--password")
            {
                // Путь к ФАЙЛУ настроек и секреты — не настройки внутри файла:
                // первый выбирает сам файл, вторые в файл не пишутся вовсе
                // (см. SecretsFromCommandLine). Но значение у них есть, и не
                // проглотить его нельзя: «--token abc» иначе прочлось бы как
                // позиционный хост «abc»
                ждёмЗначение = eq < 0;
                continue;
            }
            foreach (var (к, имя, соЗначением) in КлючиИИмена)
                if (к == ключ)
                {
                    if (!имена.Contains(имя))
                        имена.Add(имя);
                    ждёмЗначение = eq < 0 && соЗначением;
                    break;
                }
        }

        string[] поПорядку = [ИмяХост, ИмяПорт, ИмяИгрока, ИмяUid];
        foreach (string имя in поПорядку.Take(позиционные.Count))
            if (!имена.Contains(имя))
                имена.Add(имя);

        return имена;
    }

    /// <summary>
    /// ЧТО СКАЗАТЬ ОКНУ, когда человек правит настройку, которую строка запуска
    /// всё равно перебьёт. null — запомнится, врать не о чем.
    ///
    /// Чистое правило и ГОТОВАЯ ФРАЗА, а не флаг: окно, панель и журнал должны
    /// говорить об этом ОДНИМИ словами, иначе человек прочтёт две разные
    /// причины одного и того же и не поверит ни одной.
    /// </summary>
    public static string? ПочемуНеЗапомнится(string имя, IEnumerable<string> заданоСтрокой) =>
        заданоСтрокой.Contains(имя, StringComparer.OrdinalIgnoreCase)
            ? $"в файл записал, но следующий запуск это НЕ поднимет: «{имя}» задаётся ключом " +
              "строки запуска (в start-bot-panel.bat и подобных), а строка запуска сильнее " +
              "файла. Уберите ключ из строки запуска — тогда выбранное здесь и будет главным"
            : null;

    /// <summary>
    /// ЧТО СТРОКА ЗАПУСКА ПЕРЕБИЛА ПРЯМО СЕЙЧАС: имя, что стояло в файле и что
    /// стало. Пусто — файл и строка сказали одно и то же.
    ///
    /// Отдельно от <see cref="ЧтоЗадаётСтрокаЗапуска"/> нарочно: там «это не
    /// запомнится», а здесь «вот это уже не подействовало» — разные вести, и
    /// человеку нужны обе. Именно вторая объясняет, куда делся выбранный вчера
    /// в окне бот.
    /// </summary>
    public static IReadOnlyList<string> ЧтоПеребито(IEnumerable<string> заданоСтрокой,
        IReadOnlyDictionary<string, string> изФайла,
        IReadOnlyDictionary<string, string> послеСтроки)
    {
        var вести = new List<string>();
        foreach (string имя in ИменаПеребитых(заданоСтрокой, изФайла, послеСтроки))
            вести.Add($"{имя}: в файле «{изФайла[имя]}», в строке запуска «{послеСтроки[имя]}»");
        return вести;
    }

    /// <summary>
    /// ТО ЖЕ САМОЕ, НО ИМЕНАМИ — их спрашивает запись файла, чтобы вернуть на
    /// место ровно перебитое. Одно правило на обоих спрашивающих: разойдись
    /// они, и фраза человеку говорила бы про один список, а запись берегла бы
    /// другой.
    /// </summary>
    public static IReadOnlyList<string> ИменаПеребитых(IEnumerable<string> заданоСтрокой,
        IReadOnlyDictionary<string, string> изФайла,
        IReadOnlyDictionary<string, string> послеСтроки)
    {
        var имена = new List<string>();
        foreach (string имя in заданоСтрокой)
        {
            if (!изФайла.TryGetValue(имя, out string? было) ||
                !послеСтроки.TryGetValue(имя, out string? стало) || было == стало)
                continue;
            имена.Add(имя);
        }
        return имена;
    }

    /// <summary>
    /// Снимок настроек ТЕМИ ЖЕ ИМЕНАМИ, какими их зовёт человек, — чтобы
    /// сравнить «до строки запуска» и «после». Значения строками: сравнивать
    /// надо ровно то, что человек прочтёт в ответе окна.
    /// </summary>
    public IReadOnlyDictionary<string, string> Снимок() => new Dictionary<string, string>
    {
        [ИмяХост] = Host,
        [ИмяПорт] = Port.ToString(CultureInfo.InvariantCulture),
        [ИмяИгрока] = Name,
        [ИмяUid] = Uid,
        [ИмяАккаунт] = Account.Use ? (Account.Email.Length > 0 ? Account.Email : "по сессии")
                                   : "выключен",
        [ИмяПресет] = Preset ?? "",
        [ИмяОкно] = Panel.Use ? Panel.Port.ToString(CultureInfo.InvariantCulture) : "выключено",
        [ИмяБраузер] = Panel.OpenBrowser ? "да" : "нет",
        [ИмяКарта] = Panel.Map ? "да" : "нет",
        [ИмяЛюдиНаКарте] = Panel.MapPeople ? "да" : "нет",
        [ИмяАдресКарты] = Panel.MapUrl,
        [ИмяИи] = Ai ? "да" : "нет",
        [ИмяДиагностика] = Diag ? "да" : "нет",
        [ИмяПапкаЖурнала] = Log.Directory,
        [ИмяСценарий] = string.Join("; ", Script),
        [ИмяЖурналВКонсоль] = Log.Console ? "да" : "нет",
    };

    /// <summary>
    /// Настройки, которые в ЭТОМ запуске задала строка запуска. Правка любой из
    /// них в окне управления перезапуска не переживёт — окно обязано сказать
    /// об этом словами (<see cref="ПочемуНеЗапомнится"/>), а не обещать.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> ИзСтрокиЗапуска { get; private set; } = [];

    /// <summary>Что строка запуска перебила у файла в этом запуске (для журнала).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ПеребитоСтрокойЗапуска { get; private set; } = [];

    /// <summary>Те же перебитые, но именами — их бережёт запись файла.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> ИменаПеребитыхСтрокой { get; private set; } = [];

    /// <summary>Файл, каким он пришёл С ДИСКА, — до наложения строки запуска.</summary>
    [JsonIgnore]
    private string? ФайлДоСтрокиЗапуска { get; set; }

    /// <summary>Снимок сразу ПОСЛЕ строки запуска: с ним сверяем «а не крутил ли человек».</summary>
    [JsonIgnore]
    private IReadOnlyDictionary<string, string> СнимокПослеСтроки { get; set; } =
        new Dictionary<string, string>();

    /// <summary>
    /// ЧТО ПРИ ЗАПИСИ ВЕРНЁТСЯ ИЗ ФАЙЛА, а не уедет из этого запуска. Пусто —
    /// пишется всё как есть.
    ///
    /// Условий два, и оба обязательны: строка запуска эту настройку ВПРАВДУ
    /// перебила (иначе беречь нечего) И человек её с тех пор не трогал (иначе
    /// мы затрём его собственную правку — это была бы вторая потеря вместо
    /// починки первой; ровно её человек и назвал бы «настройки не запоминаются»).
    /// </summary>
    public IReadOnlyList<string> ЧтоЗаписьВернётИзФайла()
    {
        if (ИменаПеребитыхСтрокой.Count == 0 || ФайлДоСтрокиЗапуска == null)
            return [];
        var сейчас = Снимок();
        var вернём = new List<string>();
        foreach (string имя in ИменаПеребитыхСтрокой)
        {
            if (!ГдеВФайле.ContainsKey(имя))
                continue;
            if (СнимокПослеСтроки.TryGetValue(имя, out string? после) &&
                сейчас.TryGetValue(имя, out string? теперь) && после == теперь)
                вернём.Add(имя);
        }
        return вернём;
    }

    /// <summary>
    /// Одна готовая фраза о том, что запись сберегла файловые значения. Пусто —
    /// говорить не о чем. Фраза одна на окно, чат и журнал: три разные фразы про
    /// одно и то же человек читает как три разные беды.
    /// </summary>
    public string ЧтоСбереженоПриЗаписи()
    {
        var имена = ЧтоЗаписьВернётИзФайла();
        return имена.Count == 0
            ? ""
            : $"из файла оставлено как было: {string.Join(", ", имена)} — эти настройки в " +
              "этом запуске задала строка запуска, и запись не имеет права превратить разовый " +
              "ключ прогона в постоянную настройку. Хотите изменить их насовсем — правьте " +
              "здесь же (тогда запишется ваше) или уберите ключ из строки запуска";
    }

    // --- чтение ---

    /// <summary>
    /// Полный путь запуска: найти файл (или создать пример), прочитать,
    /// наложить аргументы командной строки, проверить.
    /// Бросает <see cref="BotConfigException"/> с понятным текстом.
    /// </summary>
    public static BotConfig FromCommandLine(string[] args, string? defaultPath = null)
    {
        string path = ValueOf(args, "--config") ?? defaultPath ?? Resolve(DefaultFileName);
        var cfg = LoadOrCreate(path);
        cfg.ApplyCommandLine(args);

        // ЗАПУСК БЕЗ ЕДИНОГО АРГУМЕНТА — ЭТО ДВОЙНОЙ ЩЕЛЧОК ПО ЗНАЧКУ.
        //
        // Так бота запускает человек, который не открывал терминала и не читал
        // ключей запуска: скачал один файл, щёлкнул. Открывать ему пустую
        // консоль и молча ничего не делать — худшее, что можно сделать; окно
        // управления как раз и умеет всё остальное (выбрать роль, ввести имя
        // и адрес сервера, нажать «Вход»).
        //
        // Ключ --panel при этом никуда не делся: он нужен тем, кто запускает
        // бота из скрипта С аргументами и всё равно хочет окно.
        if (args.Length == 0 && !cfg.Panel.Use)
            cfg.Panel.Use = true;

        cfg.Validate();
        return cfg;
    }

    /// <summary>Прочитать файл. Нет файла — рядом создаётся пример со значениями по умолчанию.</summary>
    public static BotConfig LoadOrCreate(string path)
    {
        path = Resolve(path);
        if (!File.Exists(path))
        {
            var fresh = new BotConfig { SourcePath = path, CreatedSample = true };
            try
            {
                fresh.WriteSample(path);
            }
            catch (Exception e)
            {
                // Молчать нельзя: иначе человек правит файл, которого нет,
                // и не понимает, почему настройки не действуют
                throw new BotConfigException(
                    $"не удалось создать файл настроек «{path}»: {e.Message}", e);
            }
            return fresh;
        }
        return Load(path);
    }

    /// <summary>Прочитать файл, который обязан существовать.</summary>
    public static BotConfig Load(string path)
    {
        path = Resolve(path);
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (FileNotFoundException e)
        {
            throw new BotConfigException($"файл настроек не найден: «{path}»", e);
        }
        catch (Exception e)
        {
            throw new BotConfigException($"не удалось прочитать «{path}»: {e.Message}", e);
        }

        BotConfig? cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<BotConfig>(text, Json);
        }
        catch (JsonException e)
        {
            // Строка и позиция — самое полезное, что можно сказать про кривой JSON
            string where = e.LineNumber is { } ln
                ? $" (строка {ln + 1}, позиция {(e.BytePositionInLine ?? 0) + 1})"
                : "";
            throw new BotConfigException(
                $"ошибка в файле настроек «{path}»{where}: {e.Message}", e);
        }
        if (cfg == null)
            throw new BotConfigException($"файл настроек «{path}» пуст");

        cfg.SourcePath = path;
        return cfg;
    }

    /// <summary>Записать текущие настройки в файл (без комментариев).</summary>
    public void Save(string? path = null)
    {
        string p = Resolve(path ?? SourcePath ?? DefaultFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, ТекстДляЗаписи(), new UTF8Encoding(true));
        SourcePath = p;
    }

    /// <summary>
    /// ЧТО ИМЕННО ЛОЖИТСЯ В ФАЙЛ: всё как есть, но перебитое строкой запуска —
    /// таким, каким оно в файле и было (см. <see cref="ГдеВФайле"/>).
    ///
    /// Вынесено отдельно и сделано публичным нарочно: иначе проверить это
    /// правило можно только через диск, а мерка «а что бы записалось» нужна
    /// и окну — оно об этом человеку говорит.
    /// </summary>
    public string ТекстДляЗаписи()
    {
        string мой = JsonSerializer.Serialize(this, Json);
        var вернуть = ЧтоЗаписьВернётИзФайла();
        if (вернуть.Count == 0 || ФайлДоСтрокиЗапуска == null)
            return мой;

        JsonObject куда, откуда;
        try
        {
            куда = JsonNode.Parse(мой)!.AsObject();
            откуда = JsonNode.Parse(ФайлДоСтрокиЗапуска)!.AsObject();
        }
        catch (JsonException)
        {
            // Разбор собственной же записи не удался — писать как есть честнее,
            // чем не писать вовсе: настройка человека дороже нашей аккуратности
            return мой;
        }

        foreach (string имя in вернуть)
            foreach (string[] путь in ГдеВФайле[имя])
                ВернутьПоле(куда, откуда, путь);
        return куда.ToJsonString(Json);
    }

    /// <summary>
    /// Вернуть одно поле по пути: было в файле — ставим как было, не было
    /// вовсе — УБИРАЕМ. Убирать обязательно: заводской пример bot.json ключа
    /// «preset» не несёт вовсе, и запись «preset: resident» из «--preset
    /// resident» завела бы человеку готового бота, которого он не выбирал.
    /// </summary>
    private static void ВернутьПоле(JsonObject куда, JsonObject откуда, string[] путь)
    {
        JsonObject? ц = куда, и = откуда;
        for (int k = 0; k < путь.Length - 1; k++)
        {
            ц = ц?[путь[k]] as JsonObject;
            и = и?[путь[k]] as JsonObject;
            if (ц == null)
                return;
        }
        if (ц == null)
            return;
        string поле = путь[^1];
        JsonNode? было = и?[поле];
        if (было == null)
            ц.Remove(поле);
        else
            ц[поле] = было.DeepClone();
    }

    // --- командная строка ---

    /// <summary>
    /// Наложить аргументы поверх файла: аргумент всегда сильнее.
    /// Поддерживается и старый позиционный вид «VintageBotStory [хост] [порт] [имя] [uid]»,
    /// и именованный: --host --port --name --uid --token --password --config
    /// --log-dir --script --ai/--no-ai --diag/--no-diag.
    /// </summary>
    public void ApplyCommandLine(string[] args)
    {
        // СНИМОК ДО — чтобы сказать человеку, что именно перебито и чем. Без
        // него остаётся только «перебито», а «перебито ЧТО НА ЧТО» и есть
        // ответ на вопрос «куда делся выбранный вчера в окне бот»
        var доСтроки = Снимок();
        // ФАЙЛ ЦЕЛИКОМ, ПОКА ЕГО НЕ ТРОНУЛА СТРОКА ЗАПУСКА. Из снимка выше
        // настройку не восстановить: он строковый и для человека. А вернуть в
        // запись надо ровно то значение и ровно того вида, что лежало в файле
        ФайлДоСтрокиЗапуска = JsonSerializer.Serialize(this, Json);
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(a);
                continue;
            }

            // Допускаем и «--имя значение», и «--имя=значение»
            string key = a;
            string? inline = null;
            int eq = a.IndexOf('=');
            if (eq > 0)
            {
                key = a[..eq];
                inline = a[(eq + 1)..];
            }

            switch (key)
            {
                case "--config":
                    Take(ref i, inline);            // путь уже использован при загрузке
                    break;
                case "--host":
                    Host = Take(ref i, inline) ?? Host;
                    break;
                case "--port":
                    if (Take(ref i, inline) is { } portText)
                    {
                        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                out int portValue))
                            throw new BotConfigException($"порт должен быть числом, а не «{portText}»");
                        Port = portValue;
                    }
                    break;
                case "--name":
                    Name = Take(ref i, inline) ?? Name;
                    break;
                case "--uid":
                    Uid = Take(ref i, inline) ?? Uid;
                    break;
                // Секрет, пришедший аргументом запуска, взводит запрет на
                // автосохранение: человек передал его так, чтобы он никуда не
                // осел, и записать его в файл значило бы обмануть это ожидание
                case "--token":
                    MpToken = Take(ref i, inline);
                    SecretsFromCommandLine |= MpToken is { Length: > 0 };
                    break;
                case "--password":
                    ServerPassword = Take(ref i, inline);
                    SecretsFromCommandLine |= ServerPassword is { Length: > 0 };
                    break;
                case "--account":
                    // Пароль сюда НЕ принимаем даже как значение: аргументы
                    // запуска видны в списке процессов всей машине
                    Account.Use = true;
                    if (Take(ref i, inline) is { Length: > 0 } email)
                        Account.Email = email;
                    break;
                case "--no-account":
                    Account.Use = false;
                    break;
                case "--preset":
                    Preset = Take(ref i, inline) ?? Preset;
                    break;
                case "--panel":
                    Panel.Use = true;
                    if (Take(ref i, inline) is { Length: > 0 } panelPort &&
                        int.TryParse(panelPort, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out int panelValue))
                        Panel.Port = panelValue;
                    break;
                case "--no-panel":
                    Panel.Use = false;
                    break;
                case "--no-browser":
                    Panel.OpenBrowser = false;
                    break;
                // Карта — доп настройка, и включать её надо явно: см. Panel.Map
                case "--map":
                    Panel.Map = true;
                    break;
                case "--no-map":
                    Panel.Map = false;
                    break;
                // Имена живых людей на карте — отдельное решение, отдельный флаг
                case "--map-people":
                    Panel.MapPeople = true;
                    break;
                case "--no-map-people":
                    Panel.MapPeople = false;
                    break;
                // Адрес веб-карты СЕРВЕРА (см. Panel.MapUrl). Со значением, а
                // не выключателем: на сервере бота запускают строкой из
                // systemd, и лезть в файл настроек ради одного адреса незачем
                case "--map-url":
                    Panel.MapUrl = Take(ref i, inline) ?? Panel.MapUrl;
                    // Адрес карты сам по себе бесполезен при выключенной карте,
                    // и человек, который его вписал, хотел её ВИДЕТЬ
                    if (Panel.MapUrl.Length > 0)
                        Panel.Map = true;
                    break;
                case "--no-map-url":
                    Panel.MapUrl = "";
                    break;
                case "--account-from-game":
                    Account.Use = true;
                    Account.FromGame = true;
                    break;
                case "--log-dir":
                    Log.Directory = Take(ref i, inline) ?? Log.Directory;
                    break;
                case "--script":
                    // Фразы через «;» — как это уже принято в запуске бота
                    if (Take(ref i, inline) is { } phrases)
                        Script = phrases.Split(';', StringSplitOptions.RemoveEmptyEntries |
                                              StringSplitOptions.TrimEntries);
                    break;
                case "--ai":
                    Ai = true;
                    break;
                case "--no-ai":
                    Ai = false;
                    break;
                case "--diag":
                    Diag = true;
                    break;
                case "--no-diag":
                    Diag = false;
                    break;
                case "--quiet":
                    Log.Console = false;
                    break;
                default:
                    // Чужие флаги (их разбирает роль) не мешают и не роняют запуск
                    break;
            }
        }

        // Позиционные — только те, что реально переданы
        if (positional.Count > 0)
            Host = positional[0];
        if (positional.Count > 1)
        {
            if (!int.TryParse(positional[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out int portValue))
                throw new BotConfigException($"порт должен быть числом, а не «{positional[1]}»");
            Port = portValue;
        }
        if (positional.Count > 2)
            Name = positional[2];
        if (positional.Count > 3)
            Uid = positional[3];

        ИзСтрокиЗапуска = ЧтоЗадаётСтрокаЗапуска(args);
        var послеСтроки = Снимок();
        ПеребитоСтрокойЗапуска = ЧтоПеребито(ИзСтрокиЗапуска, доСтроки, послеСтроки);
        ИменаПеребитыхСтрокой = ИменаПеребитых(ИзСтрокиЗапуска, доСтроки, послеСтроки);
        СнимокПослеСтроки = послеСтроки;

        string? Take(ref int at, string? inlineValue)
        {
            if (inlineValue != null)
                return inlineValue;
            if (at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal))
                return args[++at];
            return null;
        }
    }

    /// <summary>Проверить осмысленность значений. Лучше отказаться сразу, чем полночи стучаться в никуда.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new BotConfigException("не задан хост сервера");
        if (Port is < 1 or > 65535)
            throw new BotConfigException($"порт вне допустимого диапазона 1..65535: {Port}");
        if (string.IsNullOrWhiteSpace(Name))
            throw new BotConfigException("не задано имя игрока");
        if (string.IsNullOrWhiteSpace(Uid))
            throw new BotConfigException("не задан uid игрока: сервер отличает игроков по нему");
        if (string.IsNullOrWhiteSpace(Log.FileName))
            throw new BotConfigException("не задано имя файла журнала (log.fileName)");
        if (Log.MaxFiles < 1)
            throw new BotConfigException($"log.maxFiles должно быть не меньше 1, а не {Log.MaxFiles}");
        if (Log.MaxFileMb < 0)
            throw new BotConfigException($"log.maxFileMb не может быть отрицательным: {Log.MaxFileMb}");
    }

    /// <summary>Одна строка для журнала: с чем стартовали (пароль и токен не печатаем).</summary>
    public string Describe() =>
        $"настройки: {Host}:{Port}, имя {Name}, uid {Uid}" +
        (Account.Use
            ? $", аккаунт {(Account.Email.Length > 0 ? Account.Email : "по сохранённой сессии")}"
            : "") +
        $"{(Ai ? ", ии" : "")}{(Diag ? ", диагностика" : "")}" +
        // Чит, поднятый из файла, должен быть виден в первой же строке журнала:
        // иначе результат прогона не отличить от честного
        $"{(Cheats.Use ? ", ЧИТЫ ВКЛЮЧЕНЫ ФАЙЛОМ" : "")}" +
        $"{(Panel.Use ? $", окно управления на {Panel.Port}" : "")}" +
        // Карта молчаливо не включается: она доп настройка, и «когда она успела
        // включиться» человек должен читать в первой же строке журнала. Имена
        // живых людей на ней — тем более
        $"{(Panel.Use && Panel.Map ? ", карта" : "")}" +
        $"{(Panel.Use && Panel.Map && Panel.MapPeople ? " С ИМЕНАМИ ВСТРЕЧЕННЫХ ЛЮДЕЙ" : "")}" +
        // Адрес карты сервера называем: с этой минуты бот ходит за картинками
        // на чужую машину, и человек должен прочесть это в журнале, а не
        // обнаружить в сетевом журнале сервера
        $"{(Panel.Use && Panel.Map && Panel.MapUrl.Length > 0 ? $", карта сервера {Panel.MapUrl}" : "")}" +
        $"{(Preset is { Length: > 0 } p ? $", пресет {p}" : "")}" +
        $", журнал {Log.Directory}/{Log.FileName}" +
        $"{(SourcePath is { } s ? $", файл {s}" : "")}" +
        $"{(CreatedSample ? " (создан пример, значения по умолчанию)" : "")}" +
        // СТРОКА ЗАПУСКА СИЛЬНЕЕ ФАЙЛА — И ОБ ЭТОМ ГОВОРИМ ВСЛУХ. Живой случай
        // 22.08: человек выбрал в окне пресет «trader», окно записало его в
        // bot.json и обещало «перезапустите бота», а start-bot-panel.bat при
        // каждом запуске подаёт «--preset digger» и поднимал прежнего. Молча.
        // Строку эту читает первым делом тот, кто ищет пропавшую настройку
        (ИзСтрокиЗапуска.Count == 0 ? "" :
            $"; строкой запуска задано: {string.Join(", ", ИзСтрокиЗапуска)} — " +
            "выбранное в окне управления для них перезапуска не переживёт, пока эти ключи " +
            "стоят в строке запуска") +
        (ПеребитоСтрокойЗапуска.Count == 0 ? "" :
            $"; и уже перебито: {string.Join("; ", ПеребитоСтрокойЗапуска)}");

    // --- раздел роли ---

    /// <summary>Есть ли вообще раздел "role" в файле.</summary>
    public bool HasRole => Role is { ValueKind: JsonValueKind.Object };

    /// <summary>
    /// Разобрать раздел "role" в собственный тип роли. Нет раздела — null.
    /// Кривой раздел — <see cref="BotConfigException"/>: молча подставлять
    /// значения по умолчанию нельзя, иначе бот всю ночь работает не так,
    /// как написано в конфиге.
    /// </summary>
    public T? RoleAs<T>() where T : class
    {
        if (!HasRole)
            return null;
        try
        {
            return Role!.Value.Deserialize<T>(Json);
        }
        catch (JsonException e)
        {
            throw new BotConfigException(
                $"раздел \"role\" не подходит под настройки роли ({typeof(T).Name}): {e.Message}", e);
        }
    }

    /// <summary>Число из раздела роли: RoleNumber("shop.radius", 20).</summary>
    public double RoleNumber(string path, double fallback) =>
        Find(path) is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out double v) ? v : fallback;

    /// <summary>Целое из раздела роли.</summary>
    public int RoleInt(string path, int fallback) =>
        Find(path) is { ValueKind: JsonValueKind.Number } e && e.TryGetInt32(out int v) ? v : fallback;

    /// <summary>Строка из раздела роли.</summary>
    public string? RoleText(string path, string? fallback = null) =>
        Find(path) is { ValueKind: JsonValueKind.String } e ? e.GetString() : fallback;

    /// <summary>Флаг из раздела роли.</summary>
    public bool RoleFlag(string path, bool fallback = false) => Find(path) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => fallback
    };

    /// <summary>Список строк из раздела роли (например, что продавать).</summary>
    public string[] RoleList(string path)
    {
        if (Find(path) is not { ValueKind: JsonValueKind.Array } arr)
            return [];
        return arr.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .ToArray();
    }

    /// <summary>
    /// Клетка мира из раздела роли: {"home": {"x": 512, "y": 110, "z": -340}}.
    /// Нет или неполно — null (лучше «не знаю, где дом», чем случайные числа).
    /// </summary>
    public BlockPos? RolePos(string path)
    {
        if (Find(path) is not { ValueKind: JsonValueKind.Object } o)
            return null;
        if (o.TryGetProperty("x", out var x) && o.TryGetProperty("y", out var y) &&
            o.TryGetProperty("z", out var z) &&
            x.TryGetInt32(out int xi) && y.TryGetInt32(out int yi) && z.TryGetInt32(out int zi))
            return new BlockPos(xi, yi, zi);
        return null;
    }

    /// <summary>Поиск по пути «а.б.в» внутри раздела роли.</summary>
    private JsonElement? Find(string path)
    {
        if (!HasRole)
            return null;
        JsonElement cur = Role!.Value;
        foreach (string part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next))
                return null;
            cur = next;
        }
        return cur;
    }

    // --- служебное ---

    /// <summary>
    /// Достроить относительный путь. Считаем от папки, куда боту РАЗРЕШЕНО
    /// писать: обычно это папка рядом с ним, но на Linux бота часто кладут
    /// в /opt или /usr/local, где запись запрещена, — и создание файла
    /// настроек падало бы с невнятной жалобой на права (см. BotFolders).
    /// </summary>
    private static string Resolve(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(BotFolders.State(), path));

    private static string? ValueOf(string[] args, string key)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == key && i + 1 < args.Length)
                return args[i + 1];
            if (args[i].StartsWith(key + "=", StringComparison.Ordinal))
                return args[i][(key.Length + 1)..];
        }
        return null;
    }

    /// <summary>
    /// Пример конфига с комментариями: файл читается человеком, поэтому пишем
    /// не голый сериализованный объект, а текст с пояснениями. Комментарии
    /// при чтении пропускаются (JsonCommentHandling.Skip).
    /// </summary>
    private void WriteSample(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Числа — только инвариантной культурой: с русской «8,5» получился бы не JSON
        static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
        string sample =
            $$"""
            // Настройки бота. Любое значение можно перекрыть аргументом запуска:
            //   Bot --host 127.0.0.1 --port 42420 --name Бот --uid bot-uid --diag
            // Комментарии в этом файле разрешены.
            {
              // Куда подключаться
              "host": "{{Host}}",
              "port": {{Port}},
              "name": "{{Name}}",
              "uid": "{{Uid}}",
              // "mpToken": "",         // ТОЛЬКО для сервера без проверки аккаунта
              // "serverPassword": "",

              // Вход под аккаунтом Vintage Story — нужен на любом настоящем
              // сервере. ПАРОЛЬ СЮДА НЕ ПИШЕТСЯ: он берётся из переменной
              // окружения VS_PASSWORD или спрашивается при запуске, а на диске
              // остаётся только ключ сессии (botsession.json, закрыт от чужих).
              "account": {
                "use": false,
                "email": "",
                // true — взять готовую сессию из настроек установленной игры:
                // если вы уже входили в игру на этой машине, пароль не нужен
                "fromGame": false,
                "sessionFile": "botsession.json",
                "passwordEnv": "VS_PASSWORD",
                "codeEnv": "VS_TOTP"
              },

              // Чем управлять ботом
              "ai": {{(Ai ? "true" : "false")}},     // ключ нейросети берётся из ANTHROPIC_API_KEY
              "diag": {{(Diag ? "true" : "false")}},   // отладочные команды в чате
              "script": [],           // фразы в чат сразу после входа

              // Окно управления: страница в браузере с настройками, командами,
              // готовыми ботами и журналом. Слушает только 127.0.0.1, при
              // запуске рождается ключ доступа — ссылка печатается в КОНСОЛИ.
              // На сервере смотреть через: ssh -L 42462:127.0.0.1:42462 …
              //
              // ПАРОЛЬ АДМИНИСТРАТОРА. Задан — окно спрашивает его страницей
              // входа, и ключ в ссылке больше ничего не открывает. САМ ПАРОЛЬ
              // СЮДА НЕ ПИШЕТСЯ: этот файл показывают и пересылают. Он берётся
              // из переменной окружения VS_PANEL_PASSWORD, или из готового
              // отпечатка в VS_PANEL_PASSWORD_HASH (для службы на сервере),
              // или спрашивается при запуске без эха — "askPassword": true.
              "panel": {
                "use": false,
                "port": 42462,
                "openBrowser": true,
                "passwordEnv": "VS_PANEL_PASSWORD",
                "passwordHashEnv": "VS_PANEL_PASSWORD_HASH",
                "askPassword": false
              },

              // Очередь задач переживает перезапуск и лежит отдельным файлом.
              // Пусто — botjobs.json рядом с этим файлом.
              // "jobsFile": "botjobs.json",

              // Читы: то, чего живой игрок не может. Всё выключено, и свежий
              // файл их не несёт. Панель записывает сюда то, что вы щёлкнули,
              // чтобы не выбирать заново на каждый запуск, — а бот при старте
              // называет вслух каждый поднятый отсюда чит.
              "cheats": {
                "use": false
              },

              // СЕРВЕРНЫЙ РЕЖИМ: не отжирать лишнего по оперативке. Один
              // переключатель "use"; каждое поле ниже, если его ЗАДАТЬ,
              // перебивает значение набора, а ноль означает «без предела».
              // Замерено на живом сервере: без набора модель мира доходила до
              // 67 МБ (392 чанка, каждый разжатый — 128 КБ на слой), с
              // набором — 16 МБ и 175 чанков. Забытое место отвечает
              // «не знаю», а не «там пусто».
              "serverMode": {
                "use": false
                // "chunkRadius": 3,      // чанков вокруг: столько просим и столько помним
                // "decodedChunks": 64,   // разжатыми держим не больше стольких (≈16 МБ)
                // "panelLogLines": 100,  // строк журнала в окне (210 Б на строку)
                // "decorations": false   // подписи к настройкам в окне (2,6 МБ)
              },

              // Журнал
              "log": {
                "directory": "{{Log.Directory}}",
                "fileName": "{{Log.FileName}}",
                "importantFileName": "{{Log.ImportantFileName}}",
                "maxFileMb": {{Num(Log.MaxFileMb)}},
                "maxFiles": {{Log.MaxFiles}},
                "flushSeconds": {{Num(Log.FlushSeconds)}},
                "console": {{(Log.Console ? "true" : "false")}},
                "utc": false,
                // Строки с этими словами дополнительно попадут в important.log
                "importantWords": ["погиб", "ошибка", "не смог", "отключён"]
              },

              // Настройки роли: их разбирает сама роль, библиотека сюда не лезет.
              // Ключи ниже — пример; берите те, что читает ваша роль.
              "role": {
                "healthThreshold": 0.5,   // ниже этой доли здоровья — лечиться
                "hungerThreshold": 0.4,   // ниже этой доли сытости — есть
                "defendRadius": 4,        // дальше не гоняемся за врагом
                "workRadius": 24,         // дальше от дома не уходим
                "home": { "x": 0, "y": 0, "z": 0 }
              }
            }

            """;
        File.WriteAllText(path, sample, new UTF8Encoding(true));
    }
}
