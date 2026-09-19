using System.Reflection;

namespace VsBotKit.Panel;

/// <summary>
/// КОГДА ПОВЁРНУТОЕ ПОДЕЙСТВУЕТ. Три ответа, и третий — не то же самое, что
/// второй.
/// </summary>
public enum WhenApplies
{
    /// <summary>Уже подействовало: механизм получил новое значение.</summary>
    Now,

    /// <summary>
    /// Подействует со СЛЕДУЮЩЕГО ВХОДА НА СЕРВЕР. Перезапускать программу не
    /// надо — надо выйти и войти. Так живут адрес, имя, аккаунт и радиус мира:
    /// первый пакет опознания уже ушёл, и на открытом соединении его не
    /// переспросить.
    /// </summary>
    NextJoin,

    /// <summary>
    /// Подействует только при СЛЕДУЮЩЕМ ЗАПУСКЕ ПРОГРАММЫ: это то, что читается
    /// один раз при сборке бота, и снять это уже некому.
    /// </summary>
    NextStart
}

/// <summary>ГДЕ ЭТО ВООБЩЕ ПРАВИТСЯ — в окне или только руками в bot.json.</summary>
public enum WhereChanged
{
    /// <summary>Есть кнопка или поле в окне управления.</summary>
    Panel,

    /// <summary>
    /// В окне такой двери нет вовсе — только правка файла настроек. Сказать это
    /// вслух пришлось потому, что молчание здесь читается как «настройки не
    /// бывает»: порт окна и журнал в файле окно не показывало ни строкой.
    /// </summary>
    ConfigFile
}

/// <summary>Одна настройка самого бота: как её зовут, когда действует, где правится.</summary>
/// <param name="Name">
/// Имя настройки ТЕМИ ЖЕ СЛОВАМИ, какими её зовёт отказ «строка запуска сильнее
/// файла» (<see cref="BotConfig.ЧтоЗадаётСтрокаЗапуска"/>). Одно имя на все
/// ответы: два разных названия одной настройки человек читает как две настройки.
/// </param>
public sealed record SettingWhen(string Name, WhenApplies When, WhereChanged Where);

/// <summary>Приговор одной ручке: когда подействует и почему именно так.</summary>
/// <param name="Why">
/// Причина СЛОВАМИ и с ИМЕНЕМ той ручки, о которой речь. Безымянное «часть
/// настроек подействует после перезапуска» — это и был обман, ради снятия
/// которого всё написано: приписка шла на КАЖДЫЙ поворот ЛЮБОЙ ручки, ни разу
/// не называя, какая именно не доехала.
/// </param>
public sealed record AppliesVerdict(WhenApplies When, string Why);

/// <summary>Пресет по отношению к живой роли.</summary>
public enum PresetSwap
{
    /// <summary>Та же роль — меняются только ручки, а они умеют на лету.</summary>
    SameRole,

    /// <summary>Другая роль — ручки чужие, накатывать их на живого бота нельзя.</summary>
    OtherRole,

    /// <summary>Сравнить не с чем: роли нет в каталоге или каталог не подключён.</summary>
    Unknown
}

/// <summary>
/// «ЭТО ПОДЕЙСТВУЕТ СЕЙЧАС ИЛИ ТОЛЬКО ПРИ ЗАПУСКЕ» — чистое правило и одно
/// на всё окно.
///
/// ЗАЧЕМ ЗАВЕДЕНО. Слова заказчика 23.08: «хочется смену настроек и роли без
/// перезапуска». Разбор показал, что перезапуск ему нужен далеко не всегда, а
/// окно требовало его ВСЕГДА: на каждый поворот любой ручки уходила приписка
/// «часть настроек роль раздаёт способностям при сборке — такие подействуют
/// после перезапуска бота», и та же фраза висела постоянной подсказкой на
/// вкладке. КАКАЯ ИМЕННО настройка не доехала, она не называла НИ РАЗУ, — а по
/// факту все наши роли доносят ручки живьём (LivingRole.OnKnobsChanged).
/// Сказать «перезапустите» там, где не надо, — тот же обман, что промолчать:
/// человек перестаёт верить окну и перезапускает бота после каждой правки.
///
/// ПОЧЕМУ ПРАВИЛО ЗДЕСЬ, А НЕ В ОТВЕТАХ ПАНЕЛИ. Об одном и том же говорят
/// четыре места: ответ на поворот ручки, ответ на выбор готового бота, список
/// настроек бота и подсказки на вкладках. Разойдись они — и человек прочтёт
/// про одну настройку два разных приговора; ровно так и вышло с «перезапустите
/// бота», которое стояло и там, где перезапуск был не нужен.
/// </summary>
public static class PanelWhenApplies
{
    // ---------------- как это зовётся в ответе окну ----------------
    //
    // Окно получает КОД, а не готовую фразу: фразу оно составляет само и
    // переводит вместе со всей обстановкой. Пришли мы ему готовый русский текст
    // — он остался бы русским в английском окне (см. СЛОВАРЬ в panel.html:
    // слова бота не переводятся никогда, и это правильно).

    public const string КодСразу = "сразу";
    public const string КодВход = "вход";
    public const string КодЗапуск = "запуск";

    /// <summary>
    /// СМЕНИТ РОЛЬ НА ХОДУ. Отдельный код, а не «запуск»: готовый бот ЧУЖОЙ
    /// роли меняет её сейчас же (<c>bot.СменитьРоль</c>), и красная бирка
    /// «только при запуске бота» на нём была бы прямой неправдой.
    /// </summary>
    public const string КодСмена = "смена";
    public const string КодОкно = "окно";
    public const string КодФайл = "файл";

    /// <summary>Код для окна.</summary>
    public static string Code(WhenApplies when) => when switch
    {
        WhenApplies.Now => КодСразу,
        WhenApplies.NextJoin => КодВход,
        _ => КодЗапуск
    };

    /// <summary>Код для окна.</summary>
    public static string Code(WhereChanged where) =>
        where == WhereChanged.Panel ? КодОкно : КодФайл;

    // ---------------- имена настроек, которых нет у строки запуска ----------------
    //
    // Ключа запуска у них нет, поэтому и в BotConfig их имён нет: та таблица
    // отвечает на другой вопрос («что перебьёт файл»). Здесь они нужны все:
    // человек ищет в окне порт окна и журнал в файле, а окно про них молчало
    // вовсе — и молчание читается как «такой настройки не бывает».

    public const string ИмяПарольСервера = "пароль сервера";
    public const string ИмяТокен = "токен";
    public const string ИмяПортОкна = "порт окна";
    public const string ИмяПарольОкна = "пароль окна";
    public const string ИмяСпроситьПарольОкна = "спрашивать пароль окна при запуске";
    public const string ИмяЖдатьКомандыИзОкна = "ждать команды из окна, а не входить самому";
    public const string ИмяФайлЖурнала = "имя файла журнала";
    public const string ИмяРазмерЖурнала = "размер файла журнала";
    public const string ИмяЧислоФайловЖурнала = "сколько файлов журнала хранить";
    public const string ИмяСбросЖурнала = "как часто сбрасывать журнал на диск";
    public const string ИмяВремяЖурналаUTC = "время в журнале по UTC";
    public const string ИмяВажныеСлова = "слова, по которым строка журнала важная";
    public const string ИмяФайлЗадач = "файл очереди задач";
    public const string ИмяЧиты = "читы";
    public const string ИмяСерверныйРежим = "серверный режим";
    public const string ИмяРадиусМира = "радиус мира в чанках";
    public const string ИмяРазжатыхЧанков = "сколько чанков держать разжатыми";
    public const string ИмяСтрокЖурналаВОкне = "сколько строк журнала держать в окне";
    public const string ИмяПодписи = "подписи к настройкам";
    public const string ИмяКлючНейросети = "ключ нейросети";

    /// <summary>
    /// ВСЕ НАСТРОЙКИ САМОГО БОТА И ПРИГОВОР КАЖДОЙ.
    ///
    /// Приговор берётся не на слух, а по тому, КТО ЧИТАЕТ ЭТО ПОЛЕ:
    ///  • читает живой механизм на каждый щелчок — <see cref="WhenApplies.Now"/>
    ///    (читы: <c>ControlPanel.SetCheat</c> правит живой <see cref="Cheats"/>;
    ///    карта: <c>SetMap</c> правит рисование окна; серверный режим, бюджет
    ///    разжатых, длина журнала в окне и подписи: <c>SetMemory</c> правит живую
    ///    <see cref="WorldModel"/> и саму панель);
    ///  • уходит к серверу первым пакетом соединения — <see cref="WhenApplies.NextJoin"/>
    ///    (адрес, порт, ник и uid: <c>BotClient.UseServer</c> отказывает, пока бот
    ///    в игре; аккаунт: одноразовый токен берётся заново на каждый вход;
    ///    радиус мира: дальность просится пакетом опознания);
    ///  • читается ОДИН РАЗ при сборке бота — <see cref="WhenApplies.NextStart"/>
    ///    (журнал в файле: <c>BotLogFile.Attach</c>; окно: <c>MiniHttp</c> и
    ///    <see cref="PanelDoor"/> рождаются один раз, и ключ окна вместе с ними;
    ///    очередь: <c>new WorkPlanStore</c>; отладочные команды:
    ///    <c>DiagnosticCommands.AddTo</c>; фразы после входа: <c>RunScriptOnJoin</c>;
    ///    токен: он уходит в конструктор <see cref="VsBot"/>).
    ///
    /// ПРЕСЕТ здесь про РОЛЬ, а не про ручки: поле «пресет» в bot.json решает,
    /// кем бот поднимется в следующий раз. Ручки того же пресета применяются
    /// сейчас же — про это отвечает <c>ControlPanel.ApplyPreset</c>, и второго
    /// правила на этот счёт здесь не заводится.
    /// </summary>
    public static IReadOnlyList<SettingWhen> Settings { get; } =
    [
        // --- со следующего входа на сервер ---
        new(BotConfig.ИмяХост, WhenApplies.NextJoin, WhereChanged.Panel),
        new(BotConfig.ИмяПорт, WhenApplies.NextJoin, WhereChanged.Panel),
        new(BotConfig.ИмяИгрока, WhenApplies.NextJoin, WhereChanged.Panel),
        new(BotConfig.ИмяUid, WhenApplies.NextJoin, WhereChanged.Panel),
        new(BotConfig.ИмяАккаунт, WhenApplies.NextJoin, WhereChanged.Panel),
        new(ИмяПарольСервера, WhenApplies.NextJoin, WhereChanged.Panel),
        new(ИмяРадиусМира, WhenApplies.NextJoin, WhereChanged.Panel),

        // --- действует сразу ---
        new(ИмяЧиты, WhenApplies.Now, WhereChanged.Panel),
        new(BotConfig.ИмяКарта, WhenApplies.Now, WhereChanged.Panel),
        new(BotConfig.ИмяЛюдиНаКарте, WhenApplies.Now, WhereChanged.Panel),
        new(BotConfig.ИмяАдресКарты, WhenApplies.Now, WhereChanged.Panel),
        new(ИмяСерверныйРежим, WhenApplies.Now, WhereChanged.Panel),
        new(ИмяРазжатыхЧанков, WhenApplies.Now, WhereChanged.Panel),
        new(ИмяСтрокЖурналаВОкне, WhenApplies.Now, WhereChanged.Panel),
        new(ИмяПодписи, WhenApplies.Now, WhereChanged.Panel),

        // ПРЕСЕТ ДЕЙСТВУЕТ СЕЙЧАС. Правится он ровно одной дверью — кнопкой
        // «Выбрать» на вкладке «Готовые боты» (ControlPanel.ApplyPreset), — и
        // за этой дверью либо ручки накатываются на живую роль, либо роль
        // меняется на ходу. В файл он при этом тоже ложится, но это НЕ отсрочка,
        // а память на следующий запуск.
        //
        // СТОЯЛ он здесь «только при запуске бота», и выходило, что про один и
        // тот же «ночной» окно писало на вкладке подключения красное «только
        // при запуске», а на вкладке готовых ботов — зелёное «действует сразу».
        // Ровно тот разъезд, от которого этот класс и заведён: «человек прочтёт
        // про одну настройку два разных приговора»
        new(BotConfig.ИмяПресет, WhenApplies.Now, WhereChanged.Panel),

        // --- только при следующем запуске программы ---
        new(ИмяКлючНейросети, WhenApplies.NextStart, WhereChanged.Panel),
        new(BotConfig.ИмяИи, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(BotConfig.ИмяДиагностика, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(BotConfig.ИмяСценарий, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяТокен, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяФайлЗадач, WhenApplies.NextStart, WhereChanged.ConfigFile),

        new(BotConfig.ИмяОкно, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяПортОкна, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(BotConfig.ИмяБраузер, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяПарольОкна, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяСпроситьПарольОкна, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяЖдатьКомандыИзОкна, WhenApplies.NextStart, WhereChanged.ConfigFile),

        new(BotConfig.ИмяПапкаЖурнала, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяФайлЖурнала, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяРазмерЖурнала, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяЧислоФайловЖурнала, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяСбросЖурнала, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяВремяЖурналаUTC, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(BotConfig.ИмяЖурналВКонсоль, WhenApplies.NextStart, WhereChanged.ConfigFile),
        new(ИмяВажныеСлова, WhenApplies.NextStart, WhereChanged.ConfigFile)
    ];

    /// <summary>
    /// Приговор одной настройке бота по имени. null — такой настройки не знаем,
    /// и МОЛЧА подставлять «сразу» нельзя: это обещание, за которое некому
    /// отвечать.
    /// </summary>
    public static SettingWhen? Setting(string? name) =>
        name == null ? null
        : Settings.FirstOrDefault(н =>
            string.Equals(н.Name, name, StringComparison.OrdinalIgnoreCase));

    // ---------------- ручки роли ----------------

    /// <summary>
    /// ДОНОСИТ ЛИ РОЛЬ ПОВЁРНУТУЮ РУЧКУ ДО МЕХАНИЗМА, не дожидаясь перезапуска.
    ///
    /// Путь от поля роли к способности РОВНО ОДИН: <see cref="BotRole.OnKnobsChanged"/>,
    /// который зовёт <c>RoleKnobs.Apply</c> сразу после записи значения. У самой
    /// <see cref="BotRole"/> тело этого метода ПУСТОЕ — значит роль, которая его
    /// не перекрыла, поворот никуда не понесёт: значение ляжет в поле роли и
    /// пролежит там до следующей сборки бота.
    ///
    /// ЭТО НЕ ПРИДИРКА К НАШИМ РОЛЯМ — все они доносят (за всех перекрывает
    /// <c>LivingRole</c>). Правило написано ради ЧУЖИХ: заказчик вправе положить
    /// свою роль отдельной DLL (<c>Roles.ПапкаРолей</c>), и её ручки крутились бы
    /// в окне молча и без всякого действия. Сказать ему об этом должно окно, а
    /// не вечер, потраченный на «настройка не работает».
    /// </summary>
    public static bool CarriesKnobsLive(Type? roleType)
    {
        if (roleType == null)
            return false;
        try
        {
            var м = roleType.GetMethod(nameof(BotRole.OnKnobsChanged),
                BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            return м != null && м.DeclaringType != typeof(BotRole);
        }
        catch (AmbiguousMatchException)
        {
            // Сказать честно нельзя — и мы не говорим: «не доносит» здесь
            // безопаснее, потому что зовёт человека проверить, а не обещает
            return false;
        }
    }

    /// <summary>
    /// Приговор ОДНОЙ ручке роли.
    /// </summary>
    /// <param name="knob">имя ручки — оно обязано прозвучать в ответе</param>
    /// <param name="roleName">название роли для человека</param>
    /// <param name="carriesLive">
    /// доносит ли роль ручки живьём (<see cref="CarriesKnobsLive"/>)
    /// </param>
    /// <param name="atStartOnly">
    /// СЛОВА САМОЙ РУЧКИ о том, почему она подействует только при запуске
    /// (<see cref="KnobPlaceAttribute.AtStartOnly"/>). Пусто — таких слов нет,
    /// и выдумывать их за настройку панель не станет.
    /// </param>
    public static AppliesVerdict Knob(string knob, string roleName,
        bool carriesLive, string? atStartOnly)
    {
        if (!string.IsNullOrWhiteSpace(atStartOnly))
            return new(WhenApplies.NextStart,
                $"«{knob}» подействует только после перезапуска бота: {atStartOnly.Trim()}");

        if (!carriesLive)
            return new(WhenApplies.NextStart,
                $"«{knob}» записана в роль, но роль «{roleName}» не доносит повёрнутые ручки " +
                "до способностей — у неё нет своего OnKnobsChanged. Подействует при " +
                "следующем запуске бота");

        return new(WhenApplies.Now, $"«{knob}» подействовала сейчас");
    }

    // ---------------- готовый бот ----------------

    /// <summary>
    /// ЧТО ЗА ПРЕСЕТ ВЫБРАЛИ: с той же ролью, с другой или сравнить не с чем.
    ///
    /// Правило отдельное потому, что цена ошибки разная в обе стороны. Счесть
    /// чужой пресет своим — вкрутить в копателя настройки продавца: десяток
    /// жалоб «у роли нет настройки» и один молча совпавший ключ, то есть
    /// наполовину чужой бот. Счесть свой чужим — оставить человека с «выбрано,
    /// перезапустите» там, где всё могло примениться сейчас же.
    /// </summary>
    public static PresetSwap PresetKind(string? liveRoleId, string? presetRoleId)
    {
        if (string.IsNullOrWhiteSpace(liveRoleId) || string.IsNullOrWhiteSpace(presetRoleId))
            return PresetSwap.Unknown;
        return string.Equals(liveRoleId.Trim(), presetRoleId.Trim(),
            StringComparison.OrdinalIgnoreCase)
            ? PresetSwap.SameRole
            : PresetSwap.OtherRole;
    }
}
