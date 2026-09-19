using System.Security.Cryptography;
using System.Text;

namespace VsBotKit.Panel;

/// <summary>
/// ОТПЕЧАТОК ПАРОЛЯ АДМИНИСТРАТОРА — то, чем панель проверяет пароль, НЕ ХРАНЯ
/// самого пароля.
///
/// ЗАЧЕМ. Заказчик попросил «админ пароль для вебформы на сервере». Пароль,
/// оставленный в памяти строкой, попадает в дамп процесса, в отладчик и в любую
/// случайную строку журнала, стоит кому-нибудь написать «$"пароль {p}"». Здесь
/// его нет вовсе: считается PBKDF2-HMAC-SHA256 с личной солью, и сравниваются
/// только отпечатки. Обратно из отпечатка пароль не достаётся.
///
/// ПОЧЕМУ PBKDF2, А НЕ ПРОСТО SHA-256. Голый хэш от короткого пароля подбирают
/// перебором со скоростью миллиардов в секунду. Двести тысяч кругов делают одну
/// проверку заметной по времени (единицы миллисекунд) — человеку это незаметно,
/// а перебору стоит ровно в двести тысяч раз дороже. Соль у каждого запуска
/// своя: одинаковые пароли дают разные отпечатки, и радужная таблица не
/// помогает.
///
/// СРАВНЕНИЕ ЗА ПОСТОЯННОЕ ВРЕМЯ (<see cref="CryptographicOperations.FixedTimeEquals"/>).
/// Обычное сравнение массивов выходит на первом же несовпавшем байте, и по
/// времени ответа отпечаток подбирается по байту за раз.
/// </summary>
public sealed class PanelPassword
{
    /// <summary>Как считан отпечаток. Пишется в текст отпечатка первым полем.</summary>
    public const string Kind = "pbkdf2-sha256";

    /// <summary>Кругов перемешивания по умолчанию.</summary>
    public const int DefaultRounds = 210_000;

    /// <summary>Кругов меньше этого не бывает: иначе отпечаток не защищает.</summary>
    public const int LeastRounds = 10_000;

    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Пароль короче этого подбирается за вечер — об этом говорим вслух.</summary>
    public const int ShortPassword = 8;

    private readonly byte[] salt;
    private readonly byte[] hash;

    /// <summary>Сколько кругов перемешивания у этого отпечатка.</summary>
    public int Rounds { get; }

    private PanelPassword(int rounds, byte[] salt, byte[] hash)
    {
        Rounds = rounds;
        this.salt = salt;
        this.hash = hash;
    }

    /// <summary>
    /// Снять отпечаток с пароля. Сам пароль сюда приходит один раз и наружу
    /// больше не выходит ни в каком виде.
    /// </summary>
    public static PanelPassword Of(string password, int rounds = DefaultRounds)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException(
                "пустой пароль дверью не работает — такую панель проще оставить на ключе",
                nameof(password));

        int кругов = Math.Max(LeastRounds, rounds);
        byte[] соль = RandomNumberGenerator.GetBytes(SaltBytes);
        return new PanelPassword(кругов, соль, Derive(password, соль, кругов));
    }

    /// <summary>
    /// Тот ли это пароль. Пустой ответ — всегда «нет»: иначе панель открывалась
    /// бы пустым полем.
    /// </summary>
    public bool Same(string? attempt)
    {
        if (string.IsNullOrEmpty(attempt))
            return false;

        byte[] проба = Derive(attempt, salt, Rounds);
        try
        {
            return CryptographicOperations.FixedTimeEquals(проба, hash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(проба);
        }
    }

    /// <summary>
    /// Отпечаток текстом: «pbkdf2-sha256$кругов$соль$отпечаток».
    ///
    /// Это НЕ пароль и войти по нему нельзя — панель ждёт пароль и сама считает
    /// отпечаток. Годится, чтобы задать пароль службе на сервере, не оставляя
    /// самого пароля ни в файле запуска, ни в истории оболочки
    /// (см. <see cref="PanelPasswordSource"/>).
    ///
    /// В окно браузера он всё равно не уходит: там только «задан / не задан».
    /// </summary>
    public string Fingerprint =>
        $"{Kind}${Rounds}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    /// <summary>Прочитать отпечаток из текста. Мусор — null, а не догадка.</summary>
    public static PanelPassword? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] части = text.Trim().Split('$');
        if (части.Length != 4 || !string.Equals(части[0], Kind, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(части[1], out int кругов) || кругов < LeastRounds)
            return null;

        try
        {
            byte[] соль = Convert.FromBase64String(части[2]);
            byte[] отпечаток = Convert.FromBase64String(части[3]);
            if (соль.Length == 0 || отпечаток.Length == 0)
                return null;
            return new PanelPassword(кругов, соль, отпечаток);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Что не так с этим паролем — словами, или null, если всё в порядке.
    ///
    /// Слабый пароль мы НЕ ОТВЕРГАЕМ: отказ оставил бы панель открытой по
    /// ключу, то есть сделал бы хуже. Но и молчать нельзя — «12345» на сервере
    /// это дверь, которую человек считает запертой.
    /// </summary>
    public static string? Complaint(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "пароль пуст";
        if (password.Length < ShortPassword)
            return $"пароль короче {ShortPassword} знаков — такой подбирают за вечер, " +
                   "даже с задержкой между попытками";
        return null;
    }

    private static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds,
            HashAlgorithmName.SHA256, HashBytes);

    /// <summary>
    /// НАРОЧНО НЕ ОТПЕЧАТОК. Случайная строка вида «пароль: {отпечаток}» в
    /// журнале выдала бы то, что подбирают в тишине; строкой этот тип
    /// представляется безобидно, а отпечаток надо спросить именем.
    /// </summary>
    public override string ToString() => "пароль задан (отпечаток наружу не отдаётся)";
}

/// <summary>
/// ЗАЩИТА ОТ ПОДБОРА: сколько было промахов подряд и сколько ждать до
/// следующей попытки.
///
/// ПОЧЕМУ НЕ ПРОСТО «ТРИ ПОПЫТКИ И ХВАТИТ». Панель — единственный способ
/// управлять ботом на сервере. Запереть её насовсем значит запереть хозяина
/// вместе с чужими: он же не перезапустит бота, у него бот и работает. Поэтому
/// растущая задержка, а не запрет: десять промахов стоят минуту, каждый
/// следующий — пять минут, и перебор становится бессмысленным, а хозяину,
/// набравшему пароль не в той раскладке, ждать секунду.
///
/// ПОЧЕМУ ЖДЁТ БРАУЗЕР, А НЕ ПОТОК БОТА. Задержка объявляется отказом
/// («подождите 4 с»), а не сном внутри обработчика: усыпи мы поток на каждую
/// попытку — десяток одновременных запросов занял бы всю очередь потоков, и
/// подбор превратился бы в способ уронить бота.
///
/// ПОЧЕМУ СЧЁТ ЗАБЫВАЕТСЯ. Вчерашние десять промахов не должны встречать
/// хозяина сегодня утром: после <see cref="Forget"/> тишины счёт обнуляется.
/// Подбору это не помогает — он тишины не делает.
///
/// Правило чистое: часы приходят доводом, внутри времени не спрашивают.
/// </summary>
public sealed class PanelGuard
{
    /// <summary>Столько промахов подряд прощаются без задержки (раскладка, опечатка).</summary>
    public int FreeTries { get; set; } = 3;

    /// <summary>Задержка после первого непрощённого промаха. Дальше удваивается.</summary>
    public TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Потолок растущей задержки.</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>С этого промаха дверь закрывается надолго.</summary>
    public int LockAfter { get; set; } = 10;

    /// <summary>Насколько закрывается дверь после <see cref="LockAfter"/>.</summary>
    public TimeSpan LockFor { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Столько тишины — и счёт промахов забыт.</summary>
    public TimeSpan Forget { get; set; } = TimeSpan.FromMinutes(30);

    private DateTime openAt;
    private DateTime lastMiss;

    /// <summary>Промахов подряд.</summary>
    public int Misses { get; private set; }

    /// <summary>Дверь закрыта надолго — не просто «подождите секунду».</summary>
    public bool Locked => Misses >= LockAfter;

    /// <summary>Сколько ждать до следующей попытки. Ноль — можно пробовать.</summary>
    public TimeSpan Wait(DateTime now) => openAt > now ? openAt - now : TimeSpan.Zero;

    /// <summary>Промах: счёт растёт, дверь придерживается.</summary>
    public void Miss(DateTime now)
    {
        // Тишина дольше Forget — считаем с чистого листа: иначе хозяин платит
        // утром за чужие ночные попытки
        if (Misses > 0 && now - lastMiss >= Forget)
            Misses = 0;

        Misses++;
        lastMiss = now;
        openAt = now + WaitAfter(Misses);
    }

    /// <summary>Угадал: счёт обнуляется, дверь открыта.</summary>
    public void Hit()
    {
        Misses = 0;
        openAt = default;
        lastMiss = default;
    }

    /// <summary>
    /// Отказ словами. Молчаливое «не пускаю» человек читает как поломку окна и
    /// начинает жать кнопку чаще — то есть делает себе хуже.
    /// </summary>
    public string Why(DateTime now)
    {
        var ждать = Wait(now);
        if (ждать <= TimeSpan.Zero)
            return "";

        return Locked
            ? $"промахов подряд {Misses} — дверь закрыта на {Spell(ждать)}. " +
              "Столько же придётся ждать после каждой следующей ошибки: " +
              "перебором пароль так не подобрать"
            : $"промахов подряд {Misses} — следующая попытка через {Spell(ждать)}";
    }

    /// <summary>Сколько ждать после n-го промаха подряд.</summary>
    public TimeSpan WaitAfter(int misses)
    {
        if (misses >= LockAfter)
            return LockFor;
        if (misses <= FreeTries)
            return TimeSpan.Zero;

        // Удвоение: 1 с, 2 с, 4 с… — до потолка
        double разов = misses - FreeTries - 1;
        double секунд = FirstWait.TotalSeconds * Math.Pow(2, разов);
        return секунд >= MaxWait.TotalSeconds ? MaxWait : TimeSpan.FromSeconds(секунд);
    }

    /// <summary>
    /// Время словами. Сокращения «с» и «мин» нарочно: «5 секунд», «2 секунды»,
    /// «31 секунда» — три разных окончания, и склонять их ради строки отказа
    /// значило бы завести в панели грамматику.
    /// </summary>
    public static string Spell(TimeSpan t)
    {
        if (t <= TimeSpan.Zero)
            return "0 с";
        if (t < TimeSpan.FromSeconds(1))
            return "меньше секунды";

        int всего = (int)Math.Ceiling(t.TotalSeconds);
        if (всего < 60)
            return $"{всего} с";

        int минут = всего / 60;
        int секунд = всего % 60;
        return секунд == 0 ? $"{минут} мин" : $"{минут} мин {секунд} с";
    }
}

/// <summary>Пускать ли этот запрос: ответ двери с кодом и объяснением.</summary>
/// <param name="Ok">пускаем</param>
/// <param name="Code">код ответа HTTP: 200, 401 (нужен вход), 403, 429 (подождите)</param>
/// <param name="Why">объяснение человеку одной строкой</param>
/// <param name="Token">
/// Пропуск, выданный при удачном входе. Наружу он уходит ОДИН раз и только
/// печенькой браузера: в ответах панели его нет никогда.
/// </param>
public sealed record DoorVerdict(bool Ok, int Code, string Why, string Token = "");

/// <summary>
/// ДВЕРЬ ОКНА УПРАВЛЕНИЯ: кого пускать.
///
/// ЖИВОЙ СЛУЧАЙ ЗАКАЗЧИКА: «админ пароль для вебформы на сервере». До этого
/// доступ был один — ключ в адресе, рождённый при запуске и напечатанный в
/// консоли. На своей машине этого хватает, на сервере — нет: ссылку с ключом
/// приходится копировать из консоли в браузер через ssh каждый раз, она оседает
/// в истории браузера и в списке недавних вкладок, а сменить её можно только
/// перезапуском бота.
///
/// ДВА ПОРЯДКА, И ОНИ НЕ СМЕШИВАЮТСЯ:
///   • пароль НЕ задан — всё как было: пускает ключ в адресе (или заголовок
///     x-panel-key для скриптов);
///   • пароль задан — пускает ТОЛЬКО пароль. Ключ перестаёт что-либо значить, и
///     это нарочно: оставь мы его вторым входом, панель была бы ровно так же
///     открыта, как раньше, — а человек считал бы её запертой. Худший вид
///     защиты — та, в которую верят.
///
/// ЧТО ВЫДАЁТСЯ ПОСЛЕ ВХОДА. Пропуск (случайные 32 байта), он же печенька
/// браузера. Пароль после входа не хранится нигде — ни в окне, ни здесь: здесь
/// лежит только отпечаток (<see cref="PanelPassword"/>).
///
/// НАРУЖУ НЕ УХОДИТ НИЧЕГО: ни пароль, ни его отпечаток, ни ключ, ни пропуск —
/// панель отвечает только «задан / не задан» (см. <see cref="State"/>).
/// </summary>
public sealed class PanelDoor
{
    /// <summary>Имя печеньки с пропуском.</summary>
    public const string CookieName = "vsbot_panel";

    private readonly Dictionary<string, DateTime> passes = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private PanelPassword? password;

    /// <summary>Ключ доступа этого запуска — тот самый, что в ссылке.</summary>
    public string Key { get; } =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>Счёт попыток и задержка.</summary>
    public PanelGuard Guard { get; } = new();

    /// <summary>
    /// Сколько живёт пропуск БЕЗ ОБРАЩЕНИЙ. Открытая вкладка опрашивает панель
    /// каждую секунду и потому не протухает; закрытая — протухает к утру.
    /// </summary>
    public TimeSpan PassLife { get; set; } = TimeSpan.FromHours(12);

    /// <summary>Часы. Довод, а не обращение к системе, — чтобы правила были проверяемы.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Панель закрыта паролем.</summary>
    public bool HasPassword => password != null;

    /// <summary>Сколько сейчас живых пропусков (для окна — числом, не списком).</summary>
    public int Passes
    {
        get
        {
            lock (gate)
            {
                Tidy(Clock());
                return passes.Count;
            }
        }
    }

    /// <summary>
    /// Закрыть панель паролем (или снять пароль, передав null).
    ///
    /// Смена пароля ВЫБРАСЫВАЕТ ВСЕ ПРОПУСКА: иначе тот, кто вошёл по старому
    /// паролю, остался бы внутри — а пароль меняют как раз потому, что старый
    /// узнали.
    /// </summary>
    public void UsePassword(PanelPassword? fingerprint)
    {
        lock (gate)
        {
            password = fingerprint;
            passes.Clear();
            Guard.Hit();
        }
    }

    /// <summary>
    /// Пускать ли запрос. Ключи два, потому что прийти он может двумя путями:
    /// «?k=…» в адресе (так открывают ссылку) и заголовок x-panel-key
    /// (так ходит сама страница и скрипты).
    /// </summary>
    public DoorVerdict Enter(string? keyFromUrl, string? keyFromHeader, string? pass)
    {
        var now = Clock();
        lock (gate)
        {
            if (password != null)
            {
                Tidy(now);
                if (pass is { Length: > 0 } && passes.ContainsKey(pass))
                {
                    // Пропуск продлевается на каждом обращении: пока вкладка
                    // открыта, человека не выбрасывает посреди работы
                    passes[pass] = now + PassLife;
                    return new DoorVerdict(true, 200, "");
                }

                return new DoorVerdict(false, 401,
                    "окно управления закрыто паролем администратора. Введите его на странице " +
                    "входа; ключ в адресе с этого запуска ничего не открывает");
            }

            if (SameSecret(keyFromUrl, Key) || SameSecret(keyFromHeader, Key))
                return new DoorVerdict(true, 200, "");

            return new DoorVerdict(false, 403,
                "не тот ключ доступа. Полная ссылка печатается в КОНСОЛИ при запуске бота " +
                "(в журнал она не пишется — это и есть доступ к боту)");
        }
    }

    /// <summary>
    /// Попытка входа по паролю. Пароль приходит сюда и дальше не идёт: наружу
    /// возвращается только пропуск, в журнал — только итог словами.
    /// </summary>
    public DoorVerdict Login(string? attempt)
    {
        var now = Clock();
        lock (gate)
        {
            if (password == null)
                return new DoorVerdict(false, 400,
                    "пароль администратора в этом запуске не задан: панель пускает по ключу " +
                    "в адресе, а он печатается в консоли при запуске бота");

            // Пустое поле промахом не считается: угадать им нечего (пустой
            // пароль отпечатком не бывает), а хозяин, задевший Enter, не должен
            // за это ждать
            if (string.IsNullOrEmpty(attempt))
                return new DoorVerdict(false, 400, "пароль не введён");

            var ждать = Guard.Wait(now);
            if (ждать > TimeSpan.Zero)
                return new DoorVerdict(false, 429, Guard.Why(now));

            if (!password.Same(attempt))
            {
                Guard.Miss(now);
                string ещё = Guard.Why(now);
                return new DoorVerdict(false, 403,
                    "не тот пароль" + (ещё.Length > 0 ? $". {ещё}" : ""));
            }

            Guard.Hit();
            string пропуск = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
                .ToLowerInvariant();
            Tidy(now);
            passes[пропуск] = now + PassLife;
            return new DoorVerdict(true, 200, "вход по паролю администратора", пропуск);
        }
    }

    /// <summary>Выйти: пропуск больше не годится. Чужой пропуск — просто «нет».</summary>
    public bool Leave(string? pass)
    {
        if (string.IsNullOrEmpty(pass))
            return false;
        lock (gate)
        {
            return passes.Remove(pass);
        }
    }

    /// <summary>
    /// Что панель говорит про дверь НАРУЖУ. Здесь нет и не может быть ни
    /// пароля, ни отпечатка, ни ключа, ни пропуска — только «задан / не задан»
    /// и то, сколько ждать после промахов.
    /// </summary>
    public object State(bool inside)
    {
        var now = Clock();
        var ждать = Guard.Wait(now);
        return new
        {
            парольЗадан = HasPassword,
            вошли = inside,
            // Без пароля дверь открывает ключ из ссылки — и человек должен
            // понимать, что окно сейчас держится именно на нём
            пускаетКлюч = !HasPassword,
            промахов = Guard.Misses,
            ждатьСекунд = (int)Math.Ceiling(ждать.TotalSeconds),
            почему = Guard.Why(now),
            пропусков = inside ? Passes : 0
        };
    }

    /// <summary>
    /// Значение печеньки из заголовка «Cookie: имя=значение; имя2=значение2».
    /// Разбор свой и крошечный: тащить ради одной строки чужую библиотеку в
    /// панель, которая обязана работать без сети, незачем.
    /// </summary>
    public static string? CookieValue(string? header, string name)
    {
        if (string.IsNullOrEmpty(header))
            return null;

        foreach (string кусок in header.Split(';'))
        {
            var пара = кусок.AsSpan().Trim();
            int равно = пара.IndexOf('=');
            if (равно <= 0)
                continue;
            if (пара[..равно].Trim().SequenceEqual(name))
                return пара[(равно + 1)..].Trim().ToString();
        }
        return null;
    }

    /// <summary>
    /// Сравнение секретов за постоянное время. Обычное «==» выходит на первом
    /// несовпавшем знаке, и по времени ответа ключ подбирается по знаку за раз;
    /// на петле это теория, но правило дешевле исключения из правила.
    /// </summary>
    private static bool SameSecret(string? got, string mine)
    {
        if (string.IsNullOrEmpty(got))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(mine));
    }

    /// <summary>Выбросить протухшие пропуска. Зовётся под замком.</summary>
    private void Tidy(DateTime now)
    {
        if (passes.Count == 0)
            return;
        foreach (var (пропуск, до) in passes.ToList())
            if (до <= now)
                passes.Remove(пропуск);
    }
}

/// <summary>Откуда взялся пароль панели и что об этом сказать человеку.</summary>
/// <param name="Fingerprint">отпечаток или null, если пароля нет</param>
/// <param name="Trouble">
/// Пароль ХОТЕЛИ, но взять не вышло. Это не «пароля нет»: открыть панель на
/// одном ключе, когда человек просил пароль, значит оставить её распахнутой,
/// думая, что заперли.
/// </param>
/// <param name="Say">строка для журнала — пароля в ней нет и быть не может</param>
public sealed record PanelPasswordChoice(PanelPassword? Fingerprint, bool Trouble, string Say);

/// <summary>
/// ОТКУДА БЕРЁТСЯ ПАРОЛЬ ПАНЕЛИ ПРИ ЗАПУСКЕ.
///
/// ЗАКОН ПРОЕКТА, ради которого этот класс отдельный: пароль НЕ ПИШЕТСЯ в файл
/// настроек и НЕ ПОПАДАЕТ В ЖУРНАЛ. Поля под пароль в <see cref="PanelConfig"/>
/// нет вовсе — не «мы его туда не пишем», а негде: <c>bot.json</c> человек
/// показывает, копирует и пересылает вместе с ботом.
///
/// Три источника, по убыванию силы:
///   1) готовый ОТПЕЧАТОК в переменной окружения (по умолчанию
///      VS_PANEL_PASSWORD_HASH) — так задают пароль службе на сервере: в
///      systemd-файле оседает отпечаток, а не пароль, и войти по нему нельзя;
///   2) сам пароль в переменной окружения (по умолчанию VS_PANEL_PASSWORD);
///   3) вопрос при запуске с вводом БЕЗ ЭХА (panel.askPassword: true) — так
///      запускают руками.
///
/// Ни один из них не остаётся на диске: наружу отсюда выходит отпечаток.
/// </summary>
public static class PanelPasswordSource
{
    /// <summary>
    /// Взять пароль так, как просят настройки.
    /// </summary>
    /// <param name="cfg">раздел «panel» настроек запуска</param>
    /// <param name="ask">
    /// Спросить пароль без эха (см. ReadSecret в запуске бота). Не передан —
    /// спрашивать некому: так бывает в режиме MCP, где stdin занят протоколом,
    /// и в службе без консоли.
    /// </param>
    /// <param name="env">
    /// Чем читать переменные окружения. Довод, а не прямое обращение, — чтобы
    /// правило проверялось тестом без правки окружения всего процесса.
    /// </param>
    public static PanelPasswordChoice Take(PanelConfig? cfg,
        Func<string, string?>? ask = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        string имяОтпечатка = (cfg?.PasswordHashEnv ?? "").Trim();
        string имяПароля = (cfg?.PasswordEnv ?? "").Trim();

        // 1. Готовый отпечаток
        if (имяОтпечатка.Length > 0 && env(имяОтпечатка) is { Length: > 0 } отпечаток)
        {
            if (PanelPassword.Parse(отпечаток) is { } готовый)
                return new PanelPasswordChoice(готовый, false,
                    $"вход по паролю: отпечаток взят из {имяОтпечатка}");

            // Молча свалиться на «пароля нет» нельзя: человек ЗАДАЛ переменную
            // и уверен, что панель заперта
            return new PanelPasswordChoice(null, true,
                $"в переменной {имяОтпечатка} не отпечаток пароля: жду вид " +
                $"«{PanelPassword.Kind}$кругов$соль$отпечаток». Панель паролем НЕ закрыта");
        }

        // 2. Сам пароль переменной окружения
        if (имяПароля.Length > 0 && env(имяПароля) is { Length: > 0 } пароль)
        {
            // Один пробел в переменной — это опечатка запуска, а не пароль:
            // принять его значило бы «закрыть» панель тем, что набирается само
            if (пароль.Trim().Length == 0)
                return new PanelPasswordChoice(null, true,
                    $"переменная {имяПароля} задана, но в ней одни пробелы. " +
                    "Панель паролем НЕ закрыта");

            return new PanelPasswordChoice(PanelPassword.Of(пароль), false,
                $"вход по паролю: пароль взят из {имяПароля}" + Warn(пароль));
        }

        // 3. Спросить у человека без эха
        if (cfg?.AskPassword == true)
        {
            if (ask == null)
                return new PanelPasswordChoice(null, true,
                    "просили спросить пароль при запуске, а спросить некого " +
                    "(нет консоли или запуск служебный). Задайте его переменной " +
                    $"{(имяПароля.Length > 0 ? имяПароля : "окружения")}. Панель паролем НЕ закрыта");

            string? набрано = ask("Пароль администратора для окна управления (не отображается): ");
            if (string.IsNullOrEmpty(набрано))
                return new PanelPasswordChoice(null, true,
                    "пароль при запуске не набран. Панель паролем НЕ закрыта");

            return new PanelPasswordChoice(PanelPassword.Of(набрано), false,
                "вход по паролю: пароль набран при запуске" + Warn(набрано));
        }

        // 4. Пароля не просили — прежний порядок с ключом в ссылке
        return new PanelPasswordChoice(null, false,
            "пароль администратора не задан — вход по ключу в ссылке. " +
            $"Чтобы закрыть окно паролем: переменная {(имяПароля.Length > 0 ? имяПароля : "VS_PANEL_PASSWORD")} " +
            "или «panel.askPassword: true» в настройках");

        static string Warn(string password) =>
            PanelPassword.Complaint(password) is { } жалоба ? $". ВНИМАНИЕ: {жалоба}" : "";
    }
}
