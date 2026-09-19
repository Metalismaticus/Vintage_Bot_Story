using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace VsBotKit;

/// <summary>
/// Точка, названная ПО-ИГРОВОМУ — так, как её показывает игра игроку
/// (258, 111, −260), а не так, как её знает сервер (512258, 111, 511740).
///
/// Зачем ей отдельный тип. <see cref="BlockPos"/> задокументирован как
/// «позиция блока в МИРОВЫХ координатах», и любой метод мира ждёт именно её.
/// Пока игровая точка была тем же самым BlockPos, перепутать их можно было
/// только по невнимательности — и путали: ручка «Склад» ложилась в сундуки
/// склада БЕЗ перевода, а панель показывала её игровыми. Человек
/// вводит то, что видит в игре, — бот уходит за полмиллиона блоков.
///
/// Теперь перепутать нельзя по типу: GamePos не годится ни для одного метода
/// мира, пока её не перевели через <see cref="GameCoords.WorldCell"/>.
/// </summary>
public readonly record struct GamePos(int X, int Y, int Z)
{
    public override string ToString() => $"({X}, {Y}, {Z})";

    /// <summary>«258 111 -260» или «258, 111, -260» — как человек пишет в чате.</summary>
    public static bool TryParse(string? text, out GamePos pos)
    {
        pos = default;
        if (text is not { Length: > 0 })
            return false;
        var w = text.Split([' ', ',', ';', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 3 ||
            !int.TryParse(w[0], out int x) ||
            !int.TryParse(w[1], out int y) ||
            !int.TryParse(w[2], out int z))
            return false;
        pos = new GamePos(x, y, z);
        return true;
    }
}

/// <summary>
/// Перевод между игровыми и мировыми координатами — ровно там же, где он
/// уже живёт: <see cref="VsBot.WorldCell(double,double,double)"/> прибавляет
/// сдвиг, <see cref="VsBot.PlayerCoords"/> вычитает. Здесь только типизованные
/// обёртки, чтобы GamePos нельзя было отдать методу мира случайно.
///
/// Сдвиг приходит пакетом «позиция спавна» ТОЛЬКО ПОСЛЕ ВХОДА В МИР. До входа
/// он равен нулю, и перевод даст точку, отличающуюся от нужной на полмиллиона
/// блоков. Поэтому <see cref="CoordsKnown"/> спрашивают ПЕРЕД переводом, а не
/// после того, как бот ушёл не туда.
/// </summary>
public static class GameCoords
{
    /// <summary>Игровая точка во внутренних координатах мира.</summary>
    public static BlockPos WorldCell(this VsBot bot, GamePos p) => bot.WorldCell(p.X, p.Y, p.Z);

    /// <summary>
    /// Мировая клетка так, как её называет игра игроку. Вычитает
    /// <see cref="WorldOrigin.ToGame(int,int,int,ValueTuple{int,int})"/> —
    /// здесь только типизованная обёртка, чтобы GamePos нельзя было отдать
    /// методу мира случайно.
    /// </summary>
    public static GamePos GameOf(this VsBot bot, BlockPos cell)
    {
        var (x, y, z) = WorldOrigin.ToGame(cell.X, cell.Y, cell.Z, bot.CoordOffset);
        return new GamePos(x, y, z);
    }

    /// <summary>
    /// Известен ли сдвиг. Ноль означает «спавн ещё не пришёл»: настоящий спавн
    /// стоит около середины карты (512000), нулём он не бывает.
    /// </summary>
    public static bool CoordsKnown(this VsBot bot) => bot.CoordOffset != (0, 0);

    /// <summary>
    /// КЛЕТКА, В КОТОРОЙ БОТ СТОИТ, НАЗВАННАЯ ПО-ИГРОВОМУ — то самое, что
    /// человек имеет в виду, говоря «!дом тут», «!склад тут», «!пост тут».
    ///
    /// ЗАЧЕМ ОТДЕЛЬНАЯ ДВЕРЬ. Строчный близнец у этого уже есть
    /// (<see cref="VsBot.PlayerCoordsHere"/>), но он отдаёт ТЕКСТ, а в ручку
    /// роли надо положить ЧИСЛА, — и всякий, кому нужны были числа, писал эти
    /// три floor-а и перевод сам. Копий сейчас две («!дом тут» в
    /// <c>LivingRole.ЗапомнитьДомЗдесь</c> и «!пост тут» у стражника), и
    /// разъехаться им было куда: у одной могла отстать высота, у другой —
    /// начало отсчёта.
    ///
    /// null — позиция от сервера ещё не пришла. Молчать об этом нельзя: точка,
    /// записанная «из ниоткуда», встала бы нулями, то есть за полмиллиона
    /// блоков от человека.
    /// </summary>
    public static GamePos? GamePosHere(this VsBot bot) =>
        bot.Self.Position is { } p
            ? bot.GameOf(new BlockPos(
                (int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z)))
            : null;
}

/// <summary>
/// Имена полей задачи. Строки, а не свойства: поля у каждого вида работы свои,
/// и механизм их не разбирает — разбирает вид работы (как <see cref="RolePreset.Settings"/>).
/// Константы нужны, чтобы панель, каталог видов и исполнитель писали слово
/// «сдавать» одинаково.
/// </summary>
public static class WorkFields
{
    /// <summary>Откуда работать: центр карьера, начало хода, якорь сбора.</summary>
    public const string Start = "старт";

    /// <summary>До какой точки: цель штольни и дороги, второй угол карьера.</summary>
    public const string End = "конец";

    /// <summary>Сторона карьера, ширина полосы дороги.</summary>
    public const string Width = "ширина";

    /// <summary>Слоёв вниз от старта.</summary>
    public const string Depth = "глубина";

    /// <summary>В каком радиусе искать.</summary>
    public const string Radius = "радиус";

    /// <summary>Что добывать или собирать: «медь», «олово», «torch».</summary>
    public const string What = "что";

    /// <summary>Сколько единиц набрать.</summary>
    public const string HowMany = "сколько";

    /// <summary>ВРЕМЕННОЕ хранилище под эту задачу: сундук, куда сдаётся добытое.</summary>
    public const string Deliver = "сдавать";

    /// <summary>Сундук-ИСТОЧНИК материалов под эту задачу: кирка, факелы, материал дороги.</summary>
    public const string Take = "брать";

    /// <summary>Какие маски кодов брать из сундука-источника. Пусто — НИЧЕГО, а не «всё».</summary>
    public const string TakeWhat = "братьЧто";

    /// <summary>По окончании отнести на ПОСТОЯННЫЙ склад роли, а не во временное хранилище.</summary>
    public const string ToDepot = "наСклад";

    /// <summary>
    /// НЕ ХВАТИЛО МАТЕРИАЛА НА КРАФТ — СБЕГАТЬ ДОМОЙ ЗА НИМ И ВЕРНУТЬСЯ ДОДЕЛЫВАТЬ.
    ///
    /// Заказ слово в слово: «если не хватает чего для крафта и в задании не
    /// указано, что склад для крафта и добытых ресурсов в задании определенный,
    /// то можно бежать домой за ресурсами, чтобы вернуться и достроить — доп
    /// настройка».
    ///
    /// Поле необязательное: не задано — как решил бегун
    /// (<see cref="WorkRunner.RunHomeForMaterial"/>). Задано «нет» — не бегать
    /// даже когда бегун разрешает: человек вправе сказать «работай тем, что дал».
    /// </summary>
    public const string HomeForMaterial = "домойЗаМатериалом";

    /// <summary>Потолок времени на задачу, минут.</summary>
    public const string Minutes = "минут";

    /// <summary>Что человек хотел сказать себе будущему. В аргументы навыка не идёт.</summary>
    public const string Note = "примечание";

    /// <summary>
    /// Строка аргументов навыка целиком — ровно та, что пишут в чате.
    ///
    /// Поле СВОБОДНОЙ задачи (<see cref="WorkFree"/>). Готовый вид работы
    /// собирает аргументы из своих полей сам, а у свободной задачи навык любой,
    /// и полей его никто заранее не знает — собирать не из чего.
    /// </summary>
    public const string Args = "аргументы";

    /// <summary>
    /// Поля, которые понимает САМ исполнитель, а не вид работы: куда прийти,
    /// временное хранилище, источник материалов, потолок времени, примечание.
    /// Они разрешены у любой задачи — иначе каждый вид работы переписывал бы
    /// один и тот же список, и они бы разъехались.
    ///
    /// «Старт» попал сюда позже остальных, и вот почему. Бегун идёт на точку
    /// «старт» у ЛЮБОЙ задачи (шаг 5), а <see cref="WorkKind.Check"/> у видов
    /// работ, где это поле не объявлено (разгрузка, снаряжение), отвечал «поле
    /// не применится». Два места говорили разное про одно и то же поле, и
    /// человек верил панели: писал «старт» у разгрузки, видел жалобу и убирал
    /// его — хотя бот туда бы дошёл.
    /// </summary>
    public static readonly string[] Common =
        [Start, Deliver, Take, TakeWhat, ToDepot, HomeForMaterial, Minutes, Note];
}

/// <summary>
/// Вид поля — теми же словами, что <see cref="RoleKnobs.KindOf"/>: панель уже
/// умеет рисовать «клетку» тремя числами и «флаг» галочкой, и заводить ей
/// второй словарь названий значило бы разойтись на первой же новой роли.
/// </summary>
public static class WorkFieldKinds
{
    public const string Cell = "клетка";
    public const string Int = "целое";
    public const string Text = "строка";
    public const string Flag = "флаг";
    public const string Texts = "список строк";

    /// <summary>
    /// ВЕЩЬ ИГРЫ: та же строка, но окно подсказывает при наборе — списком с
    /// картинкой и настоящим игровым кодом.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫЙ ВИД, А НЕ ПРОВЕРКА ИМЕНИ ПОЛЯ В РАЗМЕТКЕ. Заказчик 16.08:
    /// «при выдаче задания на поиск чего-либо и добычу неплохо выпадающий список
    /// при вводе с иконкой, чтобы я точно знал, что бот понял меня». Знай окно,
    /// что подсказывать надо полю с именем «что», — оно решало бы за РОЛЬ, какие
    /// у неё поля и что в них лежит (правило 1). Заведи роль поле «материал» или
    /// «чем копать» — подсказки бы не было, и никто бы не понял почему. Теперь
    /// говорит роль: назвала поле вещью — окно подсказывает.
    ///
    /// Для механизма это ТА ЖЕ СТРОКА: читается <see cref="WorkItem.Text"/>,
    /// пишется в файл строкой, в аргументы навыка идёт строкой. Отличается
    /// только тем, что окно про неё знает.
    /// </summary>
    public const string Thing = "вещь";

    /// <summary>
    /// НЕСКОЛЬКО ВЕЩЕЙ СРАЗУ: строчками «медь 20», «олово 8», у каждой своя
    /// подсказка при наборе и своё число.
    ///
    /// ЗАКАЗЧИК 17.08, ДОСЛОВНО: «добавить бы допустим в добыче и еще где
    /// логично возможность добавить несколько вещей, а не один ресурс, а точнее
    /// вид». Наряд брал ровно одно слово, и «сходи за медью и оловом» значило
    /// две задачи в очереди — с двумя дорогами туда и обратно, двумя разборами
    /// запасов и двумя рейсами на склад.
    ///
    /// Для механизма это <see cref="Texts"/>: список строк, читается
    /// <see cref="WorkItem.Texts"/>, пишется в файл списком. Отличается тем, что
    /// окно рисует строки парами «вещь + сколько» и подсказывает вещи, а разбор
    /// строки в пары живёт одним чистым правилом (<see cref="OrderLines.Parse"/>).
    /// </summary>
    public const string Things = "список вещей";
}

/// <summary>Одно поле вида работы: как зовут, чем заполняется, обязательно ли.</summary>
/// <param name="Name">Имя поля («старт», «ширина») — оно же ключ в файле.</param>
/// <param name="Kind">Вид поля из <see cref="WorkFieldKinds"/>.</param>
/// <param name="About">Что это значит человеку.</param>
/// <param name="Required">Без него задача не запустится и честно откажется.</param>
/// <param name="OnlyWith">
/// ПОКАЗЫВАТЬ ТОЛЬКО ТОГДА, КОГДА ЗАПОЛНЕНО ВОТ ЭТО ПОЛЕ. Пусто — показывать
/// всегда.
///
/// ЗАКАЗЧИК 17.08, ДОСЛОВНО: «в конструкторе задач стало все лучше намного, но
/// еще неудобно, например лишний пункт везде „что брать с собой“». Он прав:
/// «братьЧто» — это МАСКИ КОДОВ, которые бот вынимает ИЗ СУНДУКА-ИСТОЧНИКА
/// («брать»). Без сундука брать неоткуда, и поле не значит ничего — а стояло
/// оно у всех семи видов работ, включая разгрузку и снаряжение.
///
/// Правило одно и живёт ЗДЕСЬ, а не в разметке страницы: реши это страница по
/// имени поля — заведи роль такую же пару, и прятать её пришлось бы правкой
/// окна. Сам механизм на зависимость не смотрит: заполненное поле работает
/// и без родителя, а <see cref="WorkPrep"/> честно скажет, что оно ни к чему
/// («„братьЧто“ задано, а „брать“ нет»).
/// </param>
public sealed record WorkField(string Name, string Kind, string About = "", bool Required = false,
    string OnlyWith = "")
{
    public override string ToString() => $"{Name} ({Kind}){(Required ? ", обязательно" : "")}";
}

/// <summary>
/// Строка аргументов для навыка — или причина, почему её не собрать.
///
/// Отказ строкой, а не исключением: очередь на вечер — это десяток задач, и
/// одна недозаполненная не должна ронять остальные девять. Но и притвориться
/// сделанной она не имеет права.
/// </summary>
public sealed record WorkArgs(bool Ok, string Args, string Problem)
{
    /// <summary>Аргументы собрались.</summary>
    public static WorkArgs From(string args) => new(true, args.Trim(), "");

    /// <summary>Навыку аргументы не нужны вовсе («разгрузка», «снаряжение»).</summary>
    public static WorkArgs None() => new(true, "", "");

    /// <summary>Не хватает поля — назвать его по имени, а не «неверные данные».</summary>
    public static WorkArgs Need(string field) => new(false, "", $"не задано поле «{field}»");

    /// <summary>Отказ своими словами.</summary>
    public static WorkArgs No(string why) => new(false, "", why);
}

/// <summary>
/// ВИД РАБОТЫ: какие поля у карьера и дороги и как из полей собрать строку
/// аргументов уже существующего навыка.
///
/// Механизм — здесь; сам список видов работ — политика роли: библиотека не
/// знает ни одного навыка по имени и знать не должна (ровно как
/// <see cref="RoleCatalog"/> не знает ни одной роли).
/// </summary>
public sealed class WorkKind
{
    private readonly List<WorkField> fields = [];
    private Func<WorkItem, WorkArgs>? build;

    /// <summary>Ключ вида работы: он же <see cref="WorkItem.Skill"/> в задаче и в файле.</summary>
    public string Id { get; }

    /// <summary>Как показывать человеку («Карьер», «Наряд (руда или ресурс)»).</summary>
    public string Title { get; }

    /// <summary>
    /// Имя навыка, которым этот вид работы исполняется. Пусто — навыка нет
    /// вовсе, и задача обязана отказаться словами, а не притвориться сделанной.
    /// </summary>
    public string SkillName { get; }

    /// <summary>Одна-две фразы: что бот сделает.</summary>
    public string About { get; init; } = "";

    /// <summary>Какие поля показывать человеку у этого вида работы.</summary>
    public IReadOnlyList<WorkField> Fields => fields;

    public WorkKind(string id, string title, string skillName)
    {
        Id = id;
        Title = title;
        SkillName = skillName;
    }

    // ---------------- сборка вида (по одной строке на поле) ----------------

    public WorkKind Cell(string name, string about = "", bool required = false) =>
        Add(name, WorkFieldKinds.Cell, about, required);

    public WorkKind Int(string name, string about = "", bool required = false) =>
        Add(name, WorkFieldKinds.Int, about, required);

    public WorkKind Text(string name, string about = "", bool required = false) =>
        Add(name, WorkFieldKinds.Text, about, required);

    /// <summary>
    /// Поле под ВЕЩЬ ИГРЫ: для механизма — та же строка, но окно подсказывает
    /// при наборе (см. <see cref="WorkFieldKinds.Thing"/>).
    /// </summary>
    public WorkKind Thing(string name, string about = "", bool required = false) =>
        Add(name, WorkFieldKinds.Thing, about, required);

    /// <summary>
    /// Поле под НЕСКОЛЬКО ВЕЩЕЙ СРАЗУ («медь 20», «олово 8») — см.
    /// <see cref="WorkFieldKinds.Things"/>. Для механизма это список строк.
    /// </summary>
    public WorkKind Things(string name, string about = "", bool required = false) =>
        Add(name, WorkFieldKinds.Things, about, required);

    public WorkKind Flag(string name, string about = "") =>
        Add(name, WorkFieldKinds.Flag, about, false);

    public WorkKind List(string name, string about = "", string onlyWith = "") =>
        Add(name, WorkFieldKinds.Texts, about, false, onlyWith);

    /// <summary>Показать у этого вида работы поля временного хранилища.</summary>
    public WorkKind Storage() => this
        .Cell(WorkFields.Deliver, "сундук под эту задачу: куда сдавать добытое")
        .Cell(WorkFields.Take, "сундук под эту задачу: откуда брать материалы")
        // ТОЛЬКО ПРИ ЗАДАННОМ СУНДУКЕ-ИСТОЧНИКЕ: брать без сундука неоткуда.
        // Заказчик: «лишний пункт везде „что брать с собой“» — вот он и есть,
        // и лишний он ровно там, где «брать» пусто (см. WorkField.OnlyWith)
        .List(WorkFields.TakeWhat, "какие маски кодов брать из сундука-источника; пусто — ничего",
            onlyWith: WorkFields.Take)
        .Flag(WorkFields.ToDepot, "по окончании отнести на постоянный склад роли")
        .Flag(WorkFields.HomeForMaterial,
            "не хватило материала на крафт — сбегать домой за ним и вернуться доделывать; " +
            "работает только когда свой склад у задания не задан");

    /// <summary>Показать поле «минут».</summary>
    public WorkKind Limit() => Int(WorkFields.Minutes, "потолок времени на задачу, минут");

    private WorkKind Add(string name, string kind, string about, bool required,
        string onlyWith = "")
    {
        fields.RemoveAll(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        fields.Add(new WorkField(name, kind, about, required, onlyWith));
        return this;
    }

    /// <summary>Как из полей собрать строку аргументов навыка.</summary>
    public WorkKind Args(Func<WorkItem, WorkArgs> builder)
    {
        build = builder;
        return this;
    }

    // ---------------- вопросы ----------------

    /// <summary>Собрать аргументы навыка. Без сборщика — навык идёт без аргументов.</summary>
    public WorkArgs BuildArgs(WorkItem job) => build?.Invoke(job) ?? WorkArgs.None();

    /// <summary>
    /// ЧТО ЭТА ЗАДАЧА ЗАКАЗЫВАЕТ — строками «вещь + сколько».
    ///
    /// ЗАЧЕМ. Заказчик просил показывать в конструкторе, во что превратится
    /// заказ («лучше отображать в вебформе тоже»), а разбирать это должен тот,
    /// кто знает поля: у наряда вещи лежат в поле «список вещей», у разведки —
    /// в одиночном поле «вещь», и у следующей роли будут лежать в поле с другим
    /// именем. Пусть окно решает это по ИМЕНИ поля — и оно решало бы за роль,
    /// какие у неё поля (правило 1); заведи роль поле «материал» — разбор молча
    /// перестал бы показываться, и никто бы не понял почему.
    ///
    /// СМОТРИМ НА ВИД ПОЛЯ, а не на имя: вид объявила сама роль
    /// (<see cref="WorkFieldKinds.Things"/>, <see cref="WorkFieldKinds.Thing"/>).
    ///
    /// СТРОГОСТИ ЗДЕСЬ НЕТ НАРОЧНО (<see cref="OrderLines.Reading"/>): человек
    /// ещё набирает, и «медь» без числа — это не отказ, а полстроки. Отказ
    /// живёт там, где задачу ПУСКАЮТ (<see cref="BuildArgs"/>), и он не
    /// изменился.
    /// </summary>
    public IReadOnlyList<OrderLine> Ordered(WorkItem job)
    {
        // Общее число задачи — умолчание для строк без своего («медь 20,
        // олово»): его же берёт и сборщик аргументов
        int общее = job.Int(WorkFields.HowMany) ?? 0;
        var lines = new List<OrderLine>();
        foreach (var f in fields)
        {
            if (f.Kind == WorkFieldKinds.Things)
                lines.AddRange(OrderLines.Reading(OrderLines.Written(job, f.Name), общее));
            else if (f.Kind == WorkFieldKinds.Thing &&
                     job.Text(f.Name) is { Length: > 0 } одна && одна.Trim().Length > 0)
                lines.Add(new OrderLine(одна.Trim(), Math.Max(0, общее)));
        }
        return lines;
    }

    /// <summary>Объявлено ли такое поле у этого вида (общие поля есть у всех).</summary>
    public bool Knows(string name) =>
        WorkFields.Common.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        fields.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Обязательные поля, которых у задачи нет.
    ///
    /// Отдельно от <see cref="Check"/>, потому что цена разная: без
    /// обязательного поля задачу пускать НЕЛЬЗЯ (карьер без центра выкопается
    /// там, где бот случайно стоит), а лишнее поле — повод сказать вслух, но
    /// не повод отменять вечер работы. Бегун спрашивает именно этот список.
    /// </summary>
    public IReadOnlyList<string> Missing(WorkItem job) =>
        fields.Where(f => f.Required && !Filled(f, job)).Select(f => f.Name).ToList();

    private static bool Filled(WorkField f, WorkItem job) => f.Kind switch
    {
        WorkFieldKinds.Cell => job.Cell(f.Name) != null,
        WorkFieldKinds.Int => job.Int(f.Name) != null,
        // Вещь для механизма — та же строка: подсказка при наборе живёт в окне,
        // а сюда доезжает уже выбранный код
        WorkFieldKinds.Text or WorkFieldKinds.Thing => job.Text(f.Name) != null,
        WorkFieldKinds.Flag => job.Flag(f.Name) != null,
        // Список вещей для механизма — тот же список строк: строки «медь 20»
        // разбирает чистое правило OrderLines.Parse, а не поле
        WorkFieldKinds.Texts or WorkFieldKinds.Things => job.Texts(f.Name).Count > 0,
        _ => job.Has(f.Name)
    };

    /// <summary>
    /// Что не так с задачей ДО того, как бот тронулся с места.
    ///
    /// Молча проглотить чужое поле нельзя: человек написал «радиус» у карьера,
    /// весь вечер ждал, что карьер станет шире, и не понял, почему не стал.
    /// </summary>
    public IReadOnlyList<string> Check(WorkItem job)
    {
        var problems = new List<string>();

        foreach (string name in Missing(job))
            problems.Add($"не задано поле «{name}»");

        foreach (var (name, value) in job.Fields)
        {
            if (value.ValueKind == JsonValueKind.Null)
                continue;
            if (!Knows(name))
            {
                problems.Add($"вид работы «{Title}» поля «{name}» не знает — оно не применится");
                continue;
            }
            var declared = fields.FirstOrDefault(
                f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (declared == null)
                continue;
            bool readable = declared.Kind switch
            {
                WorkFieldKinds.Cell => job.Cell(name) != null,
                WorkFieldKinds.Int => job.Int(name) != null,
                WorkFieldKinds.Text or WorkFieldKinds.Thing => job.Text(name) != null,
                WorkFieldKinds.Flag => job.Flag(name) != null,
                WorkFieldKinds.Texts or WorkFieldKinds.Things => true,
                _ => true
            };
            if (!readable)
                problems.Add($"поле «{name}» задано, но прочитать его как «{declared.Kind}» не вышло");
        }

        return problems;
    }

    /// <summary>
    /// Собрать задачу из того, что прислала панель. Задача возвращается всегда
    /// (человек не должен терять набранное из-за одной опечатки), но жалобы
    /// обязаны быть показаны.
    /// </summary>
    public (WorkItem Job, IReadOnlyList<string> Problems) Compose(
        string id, string? title, JsonElement fields)
    {
        var job = new WorkItem { Id = id, Skill = Id, Title = title ?? Title };
        if (fields.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in fields.EnumerateObject())
                job.Fields[p.Name] = p.Value.Clone();
        }
        return (job, Check(job));
    }

    public override string ToString() => $"{Id} — {Title} (навык «{SkillName}»)";
}

/// <summary>
/// КАТАЛОГ ВИДОВ РАБОТ: какие работы вообще можно поставить в очередь.
///
/// Каталог — механизм; чем его наполнить, решает роль. Панель спрашивает у
/// роли каталог и рисует поля по нему — так она не знает ни одной конкретной
/// роли, ровно как с ручками (<see cref="RoleKnobs.Of"/>).
/// </summary>
public sealed class WorkCatalog
{
    private readonly Dictionary<string, WorkKind> kinds = new(StringComparer.OrdinalIgnoreCase);

    public WorkCatalog Add(WorkKind kind)
    {
        kinds[kind.Id] = kind;
        return this;
    }

    /// <summary>Все виды работ по порядку добавления.</summary>
    public IReadOnlyList<WorkKind> All => kinds.Values.ToList();

    /// <summary>Найти вид работы. null — такого нет.</summary>
    public WorkKind? Find(string? id) =>
        id is { Length: > 0 } && kinds.TryGetValue(id, out var k) ? k : null;

    /// <summary>Для сообщения об ошибке: «есть такие виды работ: …».</summary>
    public string Names() => string.Join(", ", kinds.Keys);

    /// <summary>Каким видом работы исполняется этот навык. null — таким готовым видом никаким.</summary>
    public WorkKind? BySkill(string? skill) =>
        skill is { Length: > 0 }
            ? kinds.Values.FirstOrDefault(k =>
                string.Equals(k.SkillName, skill, StringComparison.OrdinalIgnoreCase))
            : null;
}

/// <summary>
/// СВОБОДНАЯ ЗАДАЧА: любой навык бота плюс строка аргументов, как её пишут
/// в чате.
///
/// Ради чего. Каталог видов работ — это удобство: у карьера отдельные поля
/// «ширина» и «глубина», и заполнять их приятнее, чем помнить порядок чисел.
/// Но пока в очередь можно было поставить ТОЛЬКО описанный вид работы, всё
/// остальное, что бот умеет, в очередь не попадало вовсе: навыки у бота есть,
/// а поставить их в очередь нельзя — «в долгих делах задам, и уже просто без
/// очерёдности». Свободная задача снимает этот потолок: любой навык из
/// <c>bot.Skills</c> ставится в очередь и занимает в ней своё место.
///
/// Ключ у неё нарочно с приставкой — «навык:карьер», а не «карьер». Так видно
/// глазом (в том числе в файле очереди, который правят руками), что это не
/// готовый вид работы с полями, а навык со строкой аргументов; и так роль
/// вольна назвать свой вид работы тем же словом, что и навык, не сталкиваясь
/// с ним ключами.
/// </summary>
public static class WorkFree
{
    /// <summary>Приставка ключа свободной задачи.</summary>
    public const string Prefix = "навык:";

    /// <summary>Ключ вида работы для навыка: «карьер» → «навык:карьер».</summary>
    public static string Id(string skill) => Prefix + skill.Trim();

    /// <summary>Свободный ли это ключ.</summary>
    public static bool Is(string? kindId) =>
        kindId is { Length: > 0 } && kindId.TrimStart()
            .StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Имя навыка из ключа. Ключ не свободный — вернётся как есть.</summary>
    public static string Name(string? kindId)
    {
        string id = (kindId ?? "").Trim();
        return Is(id) ? id[Prefix.Length..].Trim() : id;
    }

    /// <summary>
    /// Вид работы для навыка: строка аргументов и общие поля исполнителя.
    ///
    /// Точка «старт» тут не роскошь, а половина смысла: «сходи туда и сделай»
    /// — это и есть большинство задач, которые ставят на вечер.
    /// </summary>
    public static WorkKind Kind(string skill) =>
        new WorkKind(Id(skill), $"Навык «{skill}»", skill)
        {
            About = $"навык «{skill}» со строкой аргументов — той же, что в чате"
        }
        .Text(WorkFields.Args, "строка аргументов навыка; пусто — навык пойдёт без аргументов")
        .Cell(WorkFields.Start, "куда прийти перед пуском; пусто — где стою")
        .Storage()
        .Limit()
        // Строка человека уходит навыку КАК ЕСТЬ. Разбирать её здесь нечем:
        // сколько в ней чисел и что они значат, знает только сам навык
        .Args(j => WorkArgs.From(j.Text(WorkFields.Args) ?? ""));
}

/// <summary>
/// Какой вид работы стоит за ключом задачи: готовый из каталога роли или
/// свободный «навык:X».
///
/// Одно место на всех — панель, бегун и разбор задачи обязаны отвечать на этот
/// вопрос одинаково. Разъедься они, и панель показывала бы задачу готовой,
/// а бегун отказывался бы её брать.
/// </summary>
public static class WorkChoice
{
    /// <summary>Вид работы за ключом. null — причина в <paramref name="problem"/>.</summary>
    /// <param name="skills">
    /// Имена навыков, которые бот знает (<c>bot.Skills.Registered</c>). null —
    /// спросить не у кого, тогда свободная задача берётся на веру, а отказ (если
    /// такого навыка нет) придёт от <see cref="SkillRunner"/> уже на пуске.
    /// </param>
    public static WorkKind? Kind(WorkCatalog? catalog, string? id,
        IReadOnlyCollection<string>? skills, out string problem)
    {
        problem = "";
        string key = (id ?? "").Trim();
        if (key.Length == 0)
        {
            problem = "у задачи не сказано, что делать: ни вида работы, ни навыка";
            return null;
        }

        // Готовый вид работы роли — первым: роль вправе назвать свой вид как
        // угодно, в том числе с приставкой
        if (catalog?.Find(key) is { } ready)
            return ready;

        if (WorkFree.Is(key))
        {
            string skill = WorkFree.Name(key);
            if (skill.Length == 0)
            {
                problem = $"после «{WorkFree.Prefix}» не сказано, какой навык запускать";
                return null;
            }
            if (skills != null && !skills.Contains(skill, StringComparer.OrdinalIgnoreCase))
            {
                problem = $"навыка «{skill}» бот не знает; есть такие: {Join(skills)}";
                return null;
            }
            return WorkFree.Kind(skill);
        }

        problem = Why(catalog, key, skills);
        return null;
    }

    /// <summary>
    /// Почему ключ не принят — так, чтобы человек знал, что делать дальше.
    ///
    /// Живой случай: навык у бота есть, а вида работы под него роль не завела —
    /// и на «вид работы неизвестен» человек делает вывод, что бот этого не
    /// умеет. Умеет; надо только поставить задачу свободной.
    /// </summary>
    private static string Why(WorkCatalog? catalog, string id, IReadOnlyCollection<string>? skills)
    {
        bool known = skills != null && skills.Contains(id, StringComparer.OrdinalIgnoreCase);
        string hint = known
            ? $". Навык «{id}» у бота есть — поставьте задачу как «{WorkFree.Id(id)}» " +
              "и задайте строку аргументов"
            : skills is { Count: > 0 }
                ? $". Навыки бота: {Join(skills)} — любой из них ставится как " +
                  $"«{WorkFree.Prefix}имя»"
                : "";

        return catalog == null
            ? $"у роли нет конструктора работ — готового вида работы «{id}» взять неоткуда{hint}"
            : $"вид работы «{id}» роли неизвестен; есть такие: {catalog.Names()}{hint}";
    }

    private static string Join(IReadOnlyCollection<string> names) => string.Join(", ", names);
}

/// <summary>Навык бота глазами очереди: как зовут и что делает.</summary>
public sealed record WorkSkill(string Name, string About = "")
{
    public override string ToString() => About.Length > 0 ? $"{Name} — {About}" : Name;
}

/// <summary>
/// Одна строка меню «что поставить в очередь».
/// </summary>
/// <param name="Kind">Ключ для <see cref="WorkItem.Skill"/>: «карьер» или «навык:карьер».</param>
/// <param name="Title">Как показать человеку.</param>
/// <param name="About">Что бот сделает.</param>
/// <param name="Ready">
/// Готовый вид работы роли (поля вместо строки аргументов) — или свободный навык.
/// </param>
/// <param name="SkillName">Каким навыком исполняется.</param>
/// <param name="Fields">Какие поля показывать.</param>
public sealed record WorkOffer(string Kind, string Title, string About, bool Ready,
    string SkillName, IReadOnlyList<WorkField> Fields)
{
    public override string ToString() => $"{Kind} — {Title}{(Ready ? "" : " (навык напрямую)")}";
}

/// <summary>
/// МЕНЮ ОЧЕРЕДИ: всё, что вообще можно поставить в очередь.
///
/// Готовые виды работ роли идут первыми (у них поля, и заполнять их удобнее),
/// а следом — ВСЕ навыки бота без изъятия. Пропустить навык нельзя: ровно на
/// этом человек и споткнулся — бот умеет, а поставить в очередь нечем.
/// </summary>
public static class WorkMenu
{
    public static IReadOnlyList<WorkOffer> Of(WorkCatalog? catalog, IEnumerable<WorkSkill>? skills)
    {
        var menu = new List<WorkOffer>();

        foreach (var kind in catalog?.All ?? [])
            menu.Add(new WorkOffer(kind.Id, kind.Title, kind.About, true, kind.SkillName, kind.Fields));

        foreach (var skill in (skills ?? []).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var free = WorkFree.Kind(skill.Name);
            // Навык, у которого готовый вид уже есть, из меню НЕ выкидываем:
            // готовый вид может не уметь того, что умеет сам навык (лишний
            // аргумент, новый режим), и запирать человека в полях нельзя.
            // Но сказать, что удобнее, обязаны
            var ready = catalog?.BySkill(skill.Name);
            string about = skill.About.Length > 0 ? skill.About : free.About;
            if (ready != null)
                about += $"; тот же навык есть готовым видом работы «{ready.Title}» — там поля " +
                         "вместо строки аргументов";
            menu.Add(new WorkOffer(free.Id, free.Title, about, false, skill.Name, free.Fields));
        }

        return menu;
    }
}

/// <summary>Состояние задачи в очереди.</summary>
public enum WorkState
{
    /// <summary>Ждёт своей очереди.</summary>
    Waiting,
    /// <summary>Выполняется прямо сейчас.</summary>
    Running,
    /// <summary>Сделана.</summary>
    Done,
    /// <summary>Не вышла — причина лежит в <see cref="WorkItem.Result"/>.</summary>
    Failed
}

/// <summary>Состояние словами: их видит человек в панели и в файле очереди.</summary>
public static class WorkStates
{
    public static string Word(WorkState state) => state switch
    {
        WorkState.Waiting => "ждёт",
        WorkState.Running => "выполняется",
        WorkState.Done => "сделана",
        _ => "не вышла"
    };

    /// <summary>Понимаем и русское слово, и английское имя: файл правят руками.</summary>
    public static WorkState Parse(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        "выполняется" or "running" => WorkState.Running,
        "сделана" or "done" => WorkState.Done,
        "не вышла" or "невышла" or "failed" => WorkState.Failed,
        _ => WorkState.Waiting
    };
}

/// <summary>
/// ПЕРЕХОДЫ СОСТОЯНИЙ: что с задачей можно сделать, а что было бы враньём.
///
/// Правило вынесено отдельной чистой функцией не для красоты. Состояние задачи
/// — это то единственное, по чему человек утром судит, что было ночью: «сделана
/// — значит руда в сундуке». Каждый запрет здесь стоит за случаем, когда
/// состояние сказало не то, что произошло на самом деле.
/// </summary>
public static class WorkFlow
{
    /// <summary>
    /// Почему так нельзя. null — можно.
    /// </summary>
    /// <param name="reason">
    /// Итог словами. Для «сделана» и «не вышла» он обязателен: успех меряется
    /// фактом от сервера, и «сделана» без единого слова о том, чем именно она
    /// сделана, — это и есть то самое молчание, из-за которого склад докладывал
    /// «легло 112 шт.», а сундук был пуст.
    /// </param>
    public static string? Why(WorkState from, WorkState to, string reason = "")
    {
        if (to is WorkState.Done or WorkState.Failed && reason.Trim().Length == 0)
            return $"итог «{WorkStates.Word(to)}» без причины не записываю: " +
                   "человеку утром нечем будет отличить сделанное от несделанного";

        if (from == WorkState.Running && to == WorkState.Running)
            return "задачу уже ведут — второй бегун копал бы тот же карьер вдвоём, " +
                   "а итог записал бы один из двух";

        if (from == WorkState.Waiting && to == WorkState.Done)
            return "задача не запускалась — «сделана» из «ждёт» помечает несделанное " +
                   "сделанным (мимо «выполняется» пути нет)";

        if (from is WorkState.Done or WorkState.Failed && to is WorkState.Done or WorkState.Failed)
            return "итог задачи меняется только пробегом: сперва «повторить» " +
                   "(вернуть в «ждёт»), потом работа";

        return null;
    }

    /// <summary>Можно ли так.</summary>
    public static bool Can(WorkState from, WorkState to, string reason = "") =>
        Why(from, to, reason) == null;
}

/// <summary>Состояние в файле — русским словом, а не числом: файл правят руками.</summary>
public sealed class WorkStateJson : JsonConverter<WorkState>
{
    public override WorkState Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions o) =>
        reader.TokenType == JsonTokenType.String
            ? WorkStates.Parse(reader.GetString())
            : WorkState.Waiting;

    public override void Write(Utf8JsonWriter writer, WorkState value, JsonSerializerOptions o) =>
        writer.WriteStringValue(WorkStates.Word(value));
}

/// <summary>
/// Как читать и писать файл очереди. Те же правила, что у пресетов
/// (<see cref="PresetLibrary"/>): без <see cref="JavaScriptEncoder"/> на все
/// кириллица уезжает в \uXXXX и файл нельзя ни прочитать глазами, ни поправить
/// руками — а править его руками будут.
/// </summary>
public static class WorkJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        Converters = { new WorkStateJson() }
    };
}

/// <summary>
/// ЗАДАЧА: что делать и где. Поля у каждого вида работы свои, поэтому они
/// лежат словарём, а не свойствами — механизм их не разбирает, разбирает вид
/// работы. Пустое поле — норма: у штольни нет ширины, у разгрузки нет глубины.
/// </summary>
public sealed class WorkItem
{
    /// <summary>Короткое имя, уникальное в очереди. Им задачу двигают и повторяют.</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// Ключ вида работы (<see cref="WorkKind.Id"/>): готового из каталога роли
    /// («карьер») или свободного — любого навыка бота («навык:карьер»,
    /// см. <see cref="WorkFree"/>).
    /// </summary>
    public string Skill { get; set; } = "";

    /// <summary>Как человек назвал задачу: это уйдёт в журнал и в панель.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Выключенная задача остаётся в очереди, но пропускается. Ради одного
    /// вечера удалять описанный карьер и набирать заново глупо.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Поля задачи как они пришли из файла или из панели.</summary>
    public Dictionary<string, JsonElement> Fields { get; set; } = new();

    /// <summary>Ждёт, выполняется, сделана, не вышла.</summary>
    public WorkState State { get; set; } = WorkState.Waiting;

    /// <summary>Чем кончилось — словами. Пусто, пока не бралась.</summary>
    public string Result { get; set; } = "";

    /// <summary>Сколько раз бралась. Нужно, чтобы «повторить» не выглядело как «не начиналась».</summary>
    public int Attempts { get; set; }

    /// <summary>
    /// СКОЛЬКО СЕКУНД ЗАНЯЛ ПОСЛЕДНИЙ ЗАХОД. 0 — не бралась ни разу.
    ///
    /// Не украшение отчёта, а единственный честный ответ на вопрос «сколько
    /// это займёт», без которого нельзя ничего спланировать наперёд. Выдумать
    /// срок карьера нельзя: он зависит от породы, от инструмента и от того,
    /// сколько раз бот отвлекался. А вот сколько он занял ВЧЕРА — это факт,
    /// и завтра он ближе к правде, чем любая догадка.
    ///
    /// Хранится в файле очереди: перезапуск бота не должен стирать замеры,
    /// иначе первая задача каждого вечера снова непредсказуема.
    /// </summary>
    public double Took { get; set; }

    /// <summary>
    /// ДОШЁЛ ЛИ ТОТ ЗАХОД ДО КОНЦА. false — задача либо ещё не бралась, либо
    /// оборвалась (отказ, отмена рукой, спасение).
    ///
    /// ЗАЧЕМ ЭТОТ ФЛАЖОК ЗАВЕДЁН — находка приёмки, и находка злая. Замер
    /// пишется при ЛЮБОМ исходе (см. <see cref="WorkRunner"/>), и «нечем взять
    /// породу» на второй секунде давало <c>Took = 2</c>. Оценка срока брала
    /// замер как старший источник и печатала в журнал, что двухсекундный замер
    /// ТОЧНЕЕ честной прикидки в полторы тысячи секунд, — а по этому числу
    /// револьвер решает, успеет ли бот поесть перед выходом. Сам замер при этом
    /// не врал: он честно говорил, сколько длился ЗАХОД. Врало то, что его
    /// принимали за срок ЗАДАЧИ.
    ///
    /// Хранится в файле очереди рядом с самим замером: без него замер снова
    /// стал бы неотличим от оборванного после первого же перезапуска.
    /// </summary>
    public bool TookDone { get; set; }

    /// <summary>Как звать в журнале: имя человека, а не машинный ключ.</summary>
    [JsonIgnore]
    public string Name =>
        Title.Length > 0 ? Title : Id.Length > 0 ? Id : Skill.Length > 0 ? Skill : "задача";

    // ---------------- читать поля ----------------

    public bool Has(string name) =>
        Fields.TryGetValue(name, out var v) && v.ValueKind != JsonValueKind.Null;

    /// <summary>Игровая точка. Не задана или задана не так — null.</summary>
    public GamePos? Cell(string name) =>
        Fields.TryGetValue(name, out var v) && TryCell(v, out var p) ? p : null;

    /// <summary>Клетка из JSON: {"x": …, "y": …, "z": …}.</summary>
    public static bool TryCell(JsonElement v, out GamePos pos)
    {
        pos = default;
        if (v.ValueKind == JsonValueKind.String)
            return GamePos.TryParse(v.GetString(), out pos);
        if (v.ValueKind != JsonValueKind.Object)
            return false;
        if (!Number(v, "x", out int x) || !Number(v, "y", out int y) || !Number(v, "z", out int z))
            return false;
        pos = new GamePos(x, y, z);
        return true;
    }

    private static bool Number(JsonElement o, string name, out int value)
    {
        value = 0;
        if (!o.TryGetProperty(name, out var v))
            return false;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt32(out value);
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out value);
    }

    public int? Int(string name)
    {
        if (!Fields.TryGetValue(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i))
            return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int s))
            return s;
        return null;
    }

    /// <summary>Строка. Пустая строка — это «не задано», а не «пустое значение».</summary>
    public string? Text(string name)
    {
        if (!Fields.TryGetValue(name, out var v))
            return null;
        string? s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            _ => null
        };
        s = s?.Trim();
        return s is { Length: > 0 } ? s : null;
    }

    public bool? Flag(string name)
    {
        if (!Fields.TryGetValue(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out double d) && d != 0,
            JsonValueKind.String => (v.GetString() ?? "").Trim().ToLowerInvariant() switch
            {
                "да" or "true" or "1" => true,
                "нет" or "false" or "0" => false,
                _ => null
            },
            _ => null
        };
    }

    /// <summary>Список строк. Не задан — пустой список, и это значит «ничего».</summary>
    public IReadOnlyList<string> Texts(string name)
    {
        if (!Fields.TryGetValue(name, out var v))
            return [];
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString() is { Length: > 0 } one ? [one.Trim()] : [];
        if (v.ValueKind != JsonValueKind.Array)
            return [];
        return v.EnumerateArray()
            .Select(e => (e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()) ?? "")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    // ---------------- писать поля ----------------

    public WorkItem SetCell(string name, GamePos? value) => Put(name, value is { } p
        ? JsonSerializer.SerializeToElement(new { x = p.X, y = p.Y, z = p.Z }, WorkJson.Options)
        : null);

    public WorkItem SetInt(string name, int? value) => Put(name,
        value is { } i ? JsonSerializer.SerializeToElement(i, WorkJson.Options) : null);

    public WorkItem SetText(string name, string? value) => Put(name,
        value is { Length: > 0 } s ? JsonSerializer.SerializeToElement(s, WorkJson.Options) : null);

    public WorkItem SetFlag(string name, bool? value) => Put(name,
        value is { } b ? JsonSerializer.SerializeToElement(b, WorkJson.Options) : null);

    public WorkItem SetTexts(string name, IEnumerable<string>? value) => Put(name,
        value is { } list
            ? JsonSerializer.SerializeToElement(list.ToArray(), WorkJson.Options)
            : null);

    /// <summary>Убрать поле совсем (не «поставить null», а «его тут нет»).</summary>
    public WorkItem Drop(string name)
    {
        Fields.Remove(name);
        return this;
    }

    /// <summary>
    /// Пустое значение пишется как null, а не удаляется: человеку в файле
    /// видно, что такое поле у задачи бывает, и он допишет его руками.
    /// </summary>
    private WorkItem Put(string name, JsonElement? value)
    {
        Fields[name] = value ?? JsonSerializer.SerializeToElement<object?>(null, WorkJson.Options);
        return this;
    }

    public override string ToString() =>
        $"{Name} [{Skill}] — {WorkStates.Word(State)}" +
        (Enabled ? "" : ", выключена") +
        (Result.Length > 0 ? $": {Result}" : "");
}

/// <summary>
/// Одна строка очереди так, как её видит человек: с номером и с отметкой
/// «идёт сейчас» / «следующая».
///
/// Зачем отдельный тип, а не «панель сама посчитает». Номер и «что следующее» —
/// это ПРАВИЛО, а не оформление: номер считается по всем задачам подряд
/// (выключенные и сделанные тоже занимают место — иначе человек, глядя на
/// список, двигает не ту), а следующей идёт первая ждущая и включённая. Считай
/// это панель у себя — правило жило бы в двух местах и разошлось бы на первой
/// же выключенной задаче.
/// </summary>
/// <param name="Number">Номер по порядку, с единицы.</param>
/// <param name="Job">Сама задача.</param>
/// <param name="Current">Идёт прямо сейчас.</param>
/// <param name="Next">Пойдёт следующей, когда бегун освободится.</param>
/// <param name="CanUp">Есть куда двигать выше.</param>
/// <param name="CanDown">Есть куда двигать ниже.</param>
public sealed record WorkLine(int Number, WorkItem Job, bool Current, bool Next,
    bool CanUp, bool CanDown)
{
    public string Id => Job.Id;
    public string Name => Job.Name;

    /// <summary>Состояние словом — тем же, что в файле и в панели.</summary>
    public string State => WorkStates.Word(Job.State);

    /// <summary>Отметка очерёдности одним словом. Пусто — обычная строка.</summary>
    public string Mark => Current ? "идёт" : Next ? "следующая" : "";

    public override string ToString() =>
        $"{Number}. {Name} [{Job.Skill}] — {State}" +
        (Mark.Length > 0 ? $" ({Mark})" : "") +
        (Job.Enabled ? "" : ", выключена") +
        (Job.Result.Length > 0 ? $": {Job.Result}" : "");
}

/// <summary>
/// ОЧЕРЕДЬ: задачи по порядку. Двигать, добавлять, убирать, повторять.
///
/// Порядок — не украшение: снаряжение стоит перед карьером, разгрузка — после,
/// и перепутать их значит уйти копать без кирки.
/// </summary>
public sealed class WorkPlan
{
    /// <summary>Версия формата файла. Чужую версию читать не беремся.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Самоописание файла: в каких координатах записаны точки. Всегда «game».
    /// Другое значение — честный отказ читать, а не догадка: цена ошибки —
    /// точка, уехавшая на полмиллиона блоков.
    /// </summary>
    public string Coords { get; set; } = "game";

    public List<WorkItem> Jobs { get; set; } = [];

    public WorkItem? Find(string? id) => id is { Length: > 0 }
        ? Jobs.FirstOrDefault(j => string.Equals(j.Id, id, StringComparison.OrdinalIgnoreCase))
        : null;

    /// <summary>Добавить задачу. at меньше нуля или больше длины — в конец.</summary>
    public WorkItem Add(WorkItem job, int at = -1)
    {
        if (job.Id.Length == 0)
            job.Id = FreeId(Wish(job));
        if (at < 0 || at >= Jobs.Count)
            Jobs.Add(job);
        else
            Jobs.Insert(at, job);
        return job;
    }

    public bool Remove(string id)
    {
        if (Find(id) is not { } job)
            return false;
        Jobs.Remove(job);
        return true;
    }

    /// <summary>
    /// Подвинуть задачу на другое место. Индекс за краями прижимается к краю:
    /// «вверх» у первой задачи должно ничего не менять, а не ронять очередь.
    /// </summary>
    public bool Move(string id, int to)
    {
        if (Find(id) is not { } job)
            return false;
        int from = Jobs.IndexOf(job);
        int target = Math.Clamp(to, 0, Jobs.Count - 1);
        if (from == target)
            return true;
        Jobs.RemoveAt(from);
        Jobs.Insert(target, job);
        return true;
    }

    /// <summary>Подвинуть на одну позицию (−1 вверх, +1 вниз).</summary>
    public bool Shift(string id, int by) =>
        Find(id) is { } job && Move(id, Jobs.IndexOf(job) + by);

    /// <summary>
    /// Подвинуть на НОМЕР, а не на индекс: человек видит в панели «3.» и говорит
    /// «встань третьей». Разница в единицу тут стоит одной задачи не на своём
    /// месте — снаряжения после карьера.
    /// </summary>
    public bool MoveTo(string id, int number) => Move(id, number - 1);

    /// <summary>Номер задачи по порядку, с единицы. 0 — такой задачи нет.</summary>
    public int NumberOf(string? id) =>
        Find(id) is { } job ? Jobs.IndexOf(job) + 1 : 0;

    /// <summary>
    /// Вставить сразу ЗА названной задачей. Такой задачи в очереди нет — в
    /// конец: терять набранное из-за опечатки в чужом имени нельзя, а куда
    /// задача легла на самом деле, видно по её номеру.
    /// </summary>
    public WorkItem InsertAfter(WorkItem job, string? afterId) =>
        Add(job, Find(afterId) is { } after ? Jobs.IndexOf(after) + 1 : -1);

    /// <summary>
    /// Скопировать задачу и положить копию сразу за ней.
    ///
    /// Ради живого вечера: второй карьер рядом с первым отличается одним числом,
    /// а набирать заново приходится всё — точку, ширину, глубину, сундуки.
    /// Копия начинает с чистого листа: ждёт, без итога и без попыток — она
    /// НЕ бралась, и делать вид, что бралась, нельзя.
    /// </summary>
    public WorkItem? Copy(string id)
    {
        if (Find(id) is not { } job)
            return null;

        var copy = new WorkItem
        {
            Id = FreeId(job.Id),
            Skill = job.Skill,
            Title = job.Title,
            Enabled = job.Enabled,
            // Clone обязателен: значение поля может смотреть внутрь чужого
            // JsonDocument, а тот закрывают сразу после чтения файла
            Fields = job.Fields.ToDictionary(p => p.Key, p => p.Value.Clone())
        };
        return Add(copy, Jobs.IndexOf(job) + 1);
    }

    /// <summary>Включить или выключить задачу. false — такой задачи нет.</summary>
    public bool Enable(string id, bool on)
    {
        if (Find(id) is not { } job)
            return false;
        job.Enabled = on;
        return true;
    }

    /// <summary>
    /// Повторить: сделанная или не вышедшая снова становится ждущей. Счётчик
    /// попыток НЕ сбрасывается — иначе «третий раз не вышла» выглядит как
    /// «первый раз».
    /// </summary>
    public bool Repeat(string id)
    {
        if (Find(id) is not { } job)
            return false;
        job.State = WorkState.Waiting;
        job.Result = "";
        return true;
    }

    /// <summary>Повторить всю очередь: обычный «прогнать ещё раз».</summary>
    public int RepeatAll()
    {
        int n = 0;
        foreach (var job in Jobs.Where(j => j.State is WorkState.Done or WorkState.Failed))
        {
            job.State = WorkState.Waiting;
            job.Result = "";
            n++;
        }
        return n;
    }

    /// <summary>Что ещё предстоит: ждущие и включённые, по порядку.</summary>
    public IEnumerable<WorkItem> Pending() =>
        Jobs.Where(j => j.Enabled && j.State == WorkState.Waiting);

    /// <summary>
    /// Какую задачу возьмут следующей. null — брать нечего.
    ///
    /// Спрашивается КАЖДЫЙ РАЗ заново, а не запоминается: человек вправе
    /// передвинуть задачу вверх, пока идёт предыдущая, и очередь обязана
    /// послушаться сразу, а не с завтрашнего вечера.
    /// </summary>
    public WorkItem? Next(string? currentId = null) =>
        Pending().FirstOrDefault(j => !string.Equals(j.Id, currentId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Очередь строками для показа: номер, отметка «идёт»/«следующая» и куда
    /// её можно двигать.
    /// </summary>
    /// <param name="currentId">Имя задачи, которую бегун ведёт прямо сейчас.</param>
    public IReadOnlyList<WorkLine> Lines(string? currentId = null)
    {
        var next = Next(currentId);
        return Jobs.Select((job, i) => new WorkLine(
            Number: i + 1,
            Job: job,
            Current: currentId is { Length: > 0 } &&
                     string.Equals(job.Id, currentId, StringComparison.OrdinalIgnoreCase),
            Next: ReferenceEquals(job, next),
            CanUp: i > 0,
            CanDown: i < Jobs.Count - 1)).ToList();
    }

    /// <summary>
    /// От какого слова плясать, придумывая имя. У свободной задачи приставка
    /// «навык:» в имя не идёт: <see cref="BotFolders.SafeName"/> заменил бы
    /// двоеточие подчёркиванием, и в очереди стояло бы «навык_карьер» — имя,
    /// которое человек не наберёт с первого раза, когда захочет её подвинуть.
    /// </summary>
    private static string Wish(WorkItem job) =>
        WorkFree.Name(job.Skill) is { Length: > 0 } name ? name : "задача";

    /// <summary>Свободное имя: «карьер», «карьер-2», «карьер-3».</summary>
    public string FreeId(string wish)
    {
        string basis = BotFolders.SafeName(wish.Trim());
        if (Find(basis) == null)
            return basis;
        for (int n = 2; n < 10000; n++)
        {
            string tried = $"{basis}-{n}";
            if (Find(tried) == null)
                return tried;
        }
        return $"{basis}-{Guid.NewGuid():N}";
    }

    /// <summary>Коротко для панели и журнала.</summary>
    public override string ToString()
    {
        if (Jobs.Count == 0)
            return "очередь пуста";
        int done = Jobs.Count(j => j.State == WorkState.Done);
        int failed = Jobs.Count(j => j.State == WorkState.Failed);
        int off = Jobs.Count(j => !j.Enabled);
        return $"задач {Jobs.Count}: сделано {done}, не вышло {failed}, " +
               $"ждёт {Jobs.Count(j => j.Enabled && j.State == WorkState.Waiting)}" +
               (off > 0 ? $", выключено {off}" : "");
    }

    /// <summary>
    /// Привести очередь в порядок после чтения файла и назвать вслух всё, что
    /// пришлось поправить.
    ///
    /// Три беды, каждая из живого случая:
    /// • задача без имени — её нечем двигать и нечем повторять из панели;
    /// • два одинаковых имени (файл правили руками, скопировали блок) — тогда
    ///   «убрать вторую» убирает первую, и человек теряет не ту задачу;
    /// • состояние «выполняется», записанное перед тем как бота выключили. При
    ///   следующем запуске никто её не выполняет, а очередь показывает работу
    ///   и не берёт задачу снова: вечер простоит впустую.
    /// </summary>
    public IReadOnlyList<string> Tidy()
    {
        var fixes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // «jobs»: null и «fields»: null в правленом руками файле — обычное дело,
        // и падать на них нельзя: очередь потеряется целиком из-за одного слова
        Jobs ??= [];

        foreach (var job in Jobs)
        {
            job.Fields ??= new();

            if (job.Id.Length == 0)
            {
                job.Id = FreeId(Wish(job));
                fixes.Add($"задача без имени получила имя «{job.Id}»");
            }
            else if (!seen.Add(job.Id))
            {
                string was = job.Id;
                job.Id = FreeId(was);
                fixes.Add($"имя «{was}» в очереди встретилось дважды — вторая задача теперь «{job.Id}»");
            }
            seen.Add(job.Id);

            if (job.State == WorkState.Running)
            {
                job.State = WorkState.Waiting;
                job.Result = "бота перезапустили посреди работы — беру задачу заново";
                fixes.Add($"задача «{job.Name}» осталась в состоянии «выполняется» — вернул в «ждёт»");
            }
        }

        return fixes;
    }
}

// ======================================================================
//  ФАЙЛ ОЧЕРЕДИ
// ======================================================================

/// <summary>
/// Очередь задач на диске: botjobs.json там же, где botsession.json.
///
/// Зачем вообще файл. Очередь на вечер — это десяток описанных задач с
/// координатами; потерять её от перезапуска бота (а он перезапускается после
/// каждой правки роли) значит набирать всё заново. Поэтому панель обязана
/// звать <see cref="Save"/> после КАЖДОГО изменения, а не «когда-нибудь».
///
/// Секретов здесь нет структурно: в <see cref="WorkPlan"/> и
/// <see cref="WorkItem"/> их некуда положить. Но чужой файл всё равно
/// осматриваем тем же списком, что и пресеты (<see cref="RolePreset.Forbidden"/>),
/// — второго списка запретов не заводим.
/// </summary>
public sealed class WorkPlanStore
{
    /// <summary>Имя файла по умолчанию — по образцу <see cref="SessionStore.DefaultFileName"/>.</summary>
    public const string DefaultFileName = "botjobs.json";

    /// <summary>Куда положен файл очереди.</summary>
    public string Path { get; }

    /// <summary>Что случилось с файлом — словами. Молчания тут быть не должно.</summary>
    public event Action<string>? OnLog;

    public WorkPlanStore(string? path = null)
    {
        string p = path is { Length: > 0 } ? path : DefaultFileName;
        Path = System.IO.Path.IsPathRooted(p)
            ? System.IO.Path.GetFullPath(p)
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(BotFolders.State(), p));
    }

    /// <summary>
    /// Прочитать очередь. Файла нет — пустая очередь МОЛЧА: это первый запуск,
    /// а не беда. Всё остальное (битый файл, чужие координаты, чужая версия) —
    /// пустая очередь И жалоба с причиной: тихий ноль здесь означал бы
    /// «вечерней работы не было», хотя она была и просто не прочиталась.
    /// </summary>
    public WorkPlan Load()
    {
        if (!File.Exists(Path))
            return new WorkPlan();

        string text;
        try
        {
            text = File.ReadAllText(Path, Encoding.UTF8);
        }
        catch (Exception e)
        {
            OnLog?.Invoke($"файл очереди {Path} не прочитался ({e.Message}) — работаю с пустой очередью");
            return new WorkPlan();
        }

        var (plan, problems) = Read(text);
        foreach (string p in problems)
            OnLog?.Invoke($"очередь задач ({System.IO.Path.GetFileName(Path)}): {p}");
        return plan;
    }

    /// <summary>
    /// Разбор текста файла — отдельно от диска, чтобы его можно было закрыть
    /// тестами: цена ошибки тут не «медленно», а «бот пошёл не туда».
    /// </summary>
    public static (WorkPlan Plan, IReadOnlyList<string> Problems) Read(string? text)
    {
        var problems = new List<string>();

        if (text is not { Length: > 0 } || text.AsSpan().Trim().IsEmpty)
        {
            problems.Add("файл пуст — начинаю с пустой очереди");
            return (new WorkPlan(), problems);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (Exception e)
        {
            problems.Add($"файл испорчен ({e.Message}) — начинаю с пустой очереди");
            return (new WorkPlan(), problems);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                problems.Add("в файле не объект, а что-то другое — начинаю с пустой очереди");
                return (new WorkPlan(), problems);
            }

            var bad = doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => RolePreset.Forbidden.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (bad.Count > 0)
                problems.Add($"в файле лежит лишнее: {string.Join(", ", bad)}. Очередь — это «что " +
                             "делать», а не «куда подключаться»; такие поля не применяются");
        }

        WorkPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<WorkPlan>(text, WorkJson.Options);
        }
        catch (Exception e)
        {
            problems.Add($"файл разобрался не до конца ({e.Message}) — начинаю с пустой очереди");
            return (new WorkPlan(), problems);
        }

        if (plan == null)
        {
            problems.Add("файл разобрался в пустоту — начинаю с пустой очереди");
            return (new WorkPlan(), problems);
        }

        // Координаты — единственное, из-за чего читать отказываемся совсем.
        // Догадка тут стоит полмиллиона блоков: те же три числа означают либо
        // точку у дома, либо точку у края мира
        if (!string.Equals(plan.Coords.Trim(), "game", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"точки записаны в координатах «{plan.Coords}», а я понимаю только " +
                         "игровые («game») — читать не берусь, чтобы не увести бота за " +
                         "полмиллиона блоков");
            return (new WorkPlan(), problems);
        }

        if (plan.Version != 1)
        {
            problems.Add($"версия файла {plan.Version}, а я умею только 1 — читать не берусь");
            return (new WorkPlan(), problems);
        }

        problems.AddRange(plan.Tidy());
        return (plan, problems);
    }

    /// <summary>
    /// Записать очередь. Сначала во временный файл, потом подменой: если
    /// питание пропадёт посреди записи, старая очередь останется целой, а не
    /// превратится в половину файла.
    /// </summary>
    public void Save(WorkPlan plan)
    {
        string? dir = System.IO.Path.GetDirectoryName(Path);
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);

        string text = JsonSerializer.Serialize(plan, WorkJson.Options);
        string temp = Path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
        OnLog?.Invoke($"очередь записана ({plan}): {Path}");
    }
}

// ======================================================================
//  ПОДКРУТКИ НА ВРЕМЯ ОДНОЙ ЗАДАЧИ
// ======================================================================

/// <summary>
/// Пороги, подкрученные ПОД ОДНУ задачу, и обещание вернуть прежние.
///
/// Живой случай, ради которого это отдельный механизм: у задачи стоит «минут:
/// 5», бегун ставит <c>Quarry.MaxSeconds = 300</c> — и забывает вернуть 900.
/// Дальше все карьеры за вечер обрываются на пятой минуте, и понять почему
/// нельзя: в задачах-то ничего не написано. Поэтому «поставил» и «верну»
/// пишутся ОДНОЙ строкой и возвращаются скопом — в том числе на отмене и на
/// исключении.
/// </summary>
public sealed class WorkTweaks
{
    private readonly List<(string What, Action Undo)> undo = [];

    /// <summary>Что именно подкручено — для строчки в журнале.</summary>
    public IReadOnlyList<string> Changed => undo.Select(u => u.What).ToList();

    /// <summary>
    /// Поставить новое значение, запомнив прежнее. Значение совпало с текущим —
    /// не трогаем вовсе: незачем возвращать то, чего не меняли.
    /// </summary>
    public void Set<T>(string what, Func<T> read, Action<T> write, T value)
    {
        T was = read();
        if (EqualityComparer<T>.Default.Equals(was, value))
            return;
        write(value);
        undo.Add(($"{what}: было {was}, стало {value}", () => write(was)));
    }

    /// <summary>
    /// Вернуть всё как было. Возвращает, сколько вернул. Одна упавшая
    /// подкрутка не отменяет остальные — иначе первая же ошибка оставила бы
    /// половину порогов чужими навсегда.
    /// </summary>
    public int RestoreAll()
    {
        int n = 0;
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            try
            {
                undo[i].Undo();
                n++;
            }
            catch
            {
                // Возврат порога — дело последней строчки; уронить на нём всё
                // остальное значит не вернуть и то, что вернуть можно
            }
        }
        undo.Clear();
        return n;
    }
}

/// <summary>
/// Как приложить общие поля задачи («минут», «радиус», «глубина») к тому
/// механизму, которым живёт навык. Механизмы у библиотеки свои, а вот ИМЯ
/// навыка — уговор роли, поэтому связка и вынесена в таблицу.
/// </summary>
public delegate void WorkTuner(BotContext ctx, WorkItem job, double? seconds, WorkTweaks tweaks);

/// <summary>Что бегун знает про навык сверх его имени.</summary>
/// <param name="StandAbove">
/// На сколько клеток ВЫШЕ точки «старт» вставать ногами. У карьера — на одну:
/// навык «карьер» берёт центр из клетки ПОД ногами, и встань бот прямо на
/// «старт» — карьер уехал бы на слой вниз.
/// </param>
/// <param name="Tune">Куда приложить «минут» и прочие пороги; null — некуда.</param>
/// <param name="Size">
/// ИЗ ЧЕГО СКЛАДЫВАЕТСЯ СРОК ЭТОЙ РАБОТЫ, когда прошлого замера ещё нет
/// (<see cref="WorkSizer"/>). null — прикидки для навыка нет, и бегун скажет
/// это вслух, а не выдумает число.
/// </param>
public sealed record WorkBinding(int StandAbove = 0, WorkTuner? Tune = null,
    WorkSizer? Size = null);

/// <summary>
/// Связки «имя навыка → механизм» по умолчанию.
///
/// ЭТО ЗНАЧЕНИЯ ПО УМОЛЧАНИЮ ПОД УГОВОР РОЛИ-КОПАТЕЛЯ, а не знание библиотеки
/// о роли: <see cref="WorkRunner.Bindings"/> — обычный словарь, и роль вправе
/// заменить любую строку или дописать свою одной строкой. Имени в словаре нет
/// — бегун скажет вслух, что «минут» приложить некуда, и будет работать с
/// обычным потолком механизма; молчать он не станет.
///
/// ВТОРОЕ, ЧТО ЗДЕСЬ ЛЕЖИТ, — ПРИКИДКА СРОКА (<see cref="WorkBinding.Size"/>).
/// По той же причине: «сколько клеток у карьера» знает сам карьер, а вот КАК
/// ЗОВУТ навык карьера — уговор роли. Роль, назвавшая свой навык иначе, ставит
/// свою строку сама: <c>runner.Bindings["мойкарьер"] = new WorkBinding(Size:
/// WorkSizes.Quarry)</c>.
/// </summary>
public static class WorkBindings
{
    public static Dictionary<string, WorkBinding> Digger() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["наряд"] = new WorkBinding(Tune: (ctx, job, sec, t) =>
            {
                Seconds(t, "наряд", () => ctx.Order.MaxSeconds, v => ctx.Order.MaxSeconds = v, sec);
                Whole(t, "наряд: радиус поиска", () => ctx.Order.SearchRadius,
                    v => ctx.Order.SearchRadius = v, job.Int(WorkFields.Radius));
            }),

            // Единственный вид работы, где бот встаёт НЕ на точку «старт»:
            // центр карьера навык берёт из клетки под ногами
            ["карьер"] = new WorkBinding(StandAbove: 1, Size: WorkSizes.Quarry,
                Tune: (ctx, _, sec, t) =>
                Seconds(t, "карьер", () => ctx.Quarry.MaxSeconds, v => ctx.Quarry.MaxSeconds = v, sec)),

            ["штольня"] = new WorkBinding(Size: WorkSizes.Tunnel, Tune: (ctx, _, sec, t) =>
                Seconds(t, "штольня", () => ctx.Tunnel.MaxSeconds, v => ctx.Tunnel.MaxSeconds = v, sec)),

            ["дорога"] = new WorkBinding(Size: WorkSizes.Road, Tune: (ctx, _, sec, t) =>
                Seconds(t, "дорога", () => ctx.Roads.MaxSeconds, v => ctx.Roads.MaxSeconds = v, sec)),

            ["разведка"] = new WorkBinding(Size: WorkSizes.Prospecting, Tune: (ctx, job, sec, t) =>
            {
                Seconds(t, "разведка", () => ctx.Prospecting.MaxSeconds,
                    v => ctx.Prospecting.MaxSeconds = v, sec);
                Whole(t, "разведка: глубина ствола", () => ctx.Prospecting.DepthBelowStart,
                    v => ctx.Prospecting.DepthBelowStart = v, job.Int(WorkFields.Depth));
                Whole(t, "разведка: радиус обхода", () => ctx.Prospecting.SweepRadius,
                    v => ctx.Prospecting.SweepRadius = v, job.Int(WorkFields.Radius));
            }),

            ["разгрузка"] = new WorkBinding(Tune: (ctx, _, sec, t) =>
                Seconds(t, "разгрузка", () => ctx.Depot.MaxSeconds, v => ctx.Depot.MaxSeconds = v, sec)),

            ["снаряжение"] = new WorkBinding(Tune: (ctx, _, sec, t) =>
                Seconds(t, "снаряжение", () => ctx.Depot.MaxSeconds, v => ctx.Depot.MaxSeconds = v, sec)),

            // Сбор дикой еды: радиус и число мест уходят в аргументы навыка,
            // а «минут» приложить есть куда — у сбора такой же порог, как у
            // карьера и штольни
            ["сбор"] = new WorkBinding(Tune: (ctx, _, sec, t) =>
                Seconds(t, "сбор", () => ctx.Foraging.MaxSeconds, v => ctx.Foraging.MaxSeconds = v, sec))
        };

    private static void Seconds(WorkTweaks t, string what, Func<double> read, Action<double> write,
        double? seconds)
    {
        if (seconds is { } s)
            t.Set($"{what}: потолок времени, с", read, write, s);
    }

    private static void Whole(WorkTweaks t, string what, Func<int> read, Action<int> write, int? value)
    {
        if (value is { } v)
            t.Set(what, read, write, v);
    }
}

// ======================================================================
//  ЧТО БРАТЬ ИЗ СУНДУКА-ИСТОЧНИКА
// ======================================================================

/// <summary>
/// Маски кодов для сундука-источника задачи.
///
/// ЛОВУШКА, ради которой это отдельное место: <c>Container.TakeAllAsync(null)</c>
/// выгребает сундук ПОДЧИСТУЮ. Значит пустой список масок нельзя переводить в
/// «условия нет» — «братьЧто» пусто означает «ничего не брать», и шаг просто
/// пропускается. Разница между «взял кирку» и «унёс весь склад» — одна строка.
/// </summary>
public static class WorkMasks
{
    /// <summary>Подходит ли код предмета хоть под одну маску. Пустой список — нет.</summary>
    public static bool Matches(string? code, IReadOnlyList<string> masks) =>
        code is { Length: > 0 } && masks.Count > 0 &&
        masks.Any(m => m.Length > 0 && code.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Условие для <c>TakeAllAsync</c>. Никогда не null — см. ловушку выше.</summary>
    public static Func<string, bool> Wanted(IReadOnlyList<string> masks) =>
        code => Matches(code, masks);
}

// ======================================================================
//  ЧТО БЕГУН СДЕЛАЕТ С ЗАДАЧЕЙ (решается ДО выхода из дома)
// ======================================================================

/// <summary>
/// Разбор задачи в то, что бегун будет делать: вид работы, строка аргументов
/// навыка, точки и пороги. Ни одного обращения к миру — потому это и можно
/// закрыть тестами, а отказ человек получает ДО того, как бот ушёл копать.
/// </summary>
/// <param name="Ok">Задачу можно пускать.</param>
/// <param name="Problem">Почему нельзя — словами и по имени поля.</param>
/// <param name="Kind">Найденный вид работы.</param>
/// <param name="Args">Строка аргументов для <c>Skills.RunAsync</c>.</param>
/// <param name="Start">Куда прийти перед пуском (игровая точка).</param>
/// <param name="Deliver">Временное хранилище задачи (игровая точка).</param>
/// <param name="Take">Сундук-источник материалов (игровая точка).</param>
/// <param name="TakeWhat">Маски кодов, которые из него брать. Пусто — не брать ничего.</param>
/// <param name="ToDepot">По окончании отнести на постоянный склад роли.</param>
/// <param name="Seconds">Потолок времени задачи в секундах; null — обычный потолок механизма.</param>
/// <param name="Notes">Жалобы, которые НЕ отменяют задачу, но обязаны быть сказаны.</param>
/// <param name="HomeForMaterial">
/// Бежать ли домой за недостающим материалом. null — поле не задано, решает
/// бегун (<see cref="WorkRunner.RunHomeForMaterial"/>).
/// </param>
public sealed record WorkPrep(
    bool Ok,
    string Problem,
    WorkKind? Kind,
    string Args,
    GamePos? Start,
    GamePos? Deliver,
    GamePos? Take,
    IReadOnlyList<string> TakeWhat,
    bool ToDepot,
    double? Seconds,
    IReadOnlyList<string> Notes,
    bool? HomeForMaterial = null)
{
    /// <summary>
    /// У ЗАДАНИЯ СВОЙ СКЛАД. Спрашивается одним правилом на всех
    /// (<see cref="WorkStorage.Own"/>): второй такой ответ разошёлся бы с
    /// первым на первом же задании, где сундук назван только у «брать».
    /// </summary>
    public bool OwnStorage => WorkStorage.Own(Deliver, Take);

    /// <summary>Отказ с причиной. Задачу не пускаем.</summary>
    public static WorkPrep No(string problem, WorkKind? kind = null,
        IReadOnlyList<string>? notes = null) =>
        new(false, problem, kind, "", null, null, null, [], false, null, notes ?? []);

    /// <summary>
    /// Разобрать задачу.
    /// </summary>
    /// <param name="coordsKnown">
    /// Пришла ли позиция спавна (<see cref="GameCoords.CoordsKnown"/>). До входа
    /// в мир сдвиг равен нулю, и перевод игровой точки в мировую увёл бы бота
    /// на полмиллиона блоков — поэтому спрашиваем ДО, а не после.
    /// </param>
    /// <param name="skills">
    /// Имена навыков бота (<c>bot.Skills.Registered</c>) — ими проверяется
    /// свободная задача «навык:X». null — проверить нечем, и отказ (если навыка
    /// нет) придёт позже, от <see cref="SkillRunner"/>.
    /// </param>
    public static WorkPrep Of(WorkCatalog? catalog, WorkItem job, bool coordsKnown,
        IReadOnlyCollection<string>? skills = null)
    {
        var kind = WorkChoice.Kind(catalog, job.Skill, skills, out string problem);
        if (kind == null)
            return No(problem);

        if (kind.SkillName.Length == 0)
            return No($"у вида работы «{kind.Title}» навыка нет — делать это я не умею", kind);

        // Про недостающее обязательное скажет отказ ниже — второй раз то же
        // самое человеку не нужно
        var missing = kind.Missing(job);
        var notes = kind.Check(job)
            .Where(p => !missing.Any(m => p == $"не задано поле «{m}»"))
            .ToList();

        if (missing.Count > 0)
            return No("не заполнено обязательное: " +
                      string.Join(", ", missing.Select(m => $"«{m}»")), kind, notes);

        // Клетка, которая ЗАПИСАНА, но не читается, — это не «поля нет».
        // Человек её задал и ждёт, что бот туда пойдёт; молча пойти в другое
        // место хуже, чем честно отказаться
        var cellNames = kind.Fields.Where(f => f.Kind == WorkFieldKinds.Cell).Select(f => f.Name)
            .Concat([WorkFields.Start, WorkFields.End, WorkFields.Deliver, WorkFields.Take])
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string name in cellNames)
        {
            if (job.Has(name) && job.Cell(name) == null)
                return No($"поле «{name}» записано так, что точки из него не прочитать " +
                          "(жду три числа: x y z)", kind, notes);
        }

        var args = kind.BuildArgs(job);
        if (!args.Ok)
            return No(args.Problem, kind, notes);

        var start = job.Cell(WorkFields.Start);
        var deliver = job.Cell(WorkFields.Deliver);
        var take = job.Cell(WorkFields.Take);

        // Перевод игровой точки в мировую делает ТОЛЬКО бегун и только здесь,
        // после этой проверки. Поле «конец» в список не входит: оно уходит в
        // аргументы игровыми числами, навык переводит его сам
        if ((start ?? deliver ?? take) != null && !coordsKnown)
            return No("позиция спавна ещё не пришла — перевести игровую точку в мировую нельзя " +
                      "(до входа в мир сдвиг нулевой, и точка уехала бы за полмиллиона блоков)",
                      kind, notes);

        // ЯКОРЬ НЕ ЗАДАН — СКАЖИ ОБ ЭТОМ. Поле нарочно необязательное («работай
        // там, где стоишь»), но человек, поставивший в вечернюю очередь «сбор»
        // без точки, ждёт сбора вокруг дома, а радиус будет меряться оттуда,
        // где бота застала предыдущая задача. Молчать про это нельзя
        if (start == null && kind.Fields.Any(f => f.Kind == WorkFieldKinds.Cell &&
                string.Equals(f.Name, WorkFields.Start, StringComparison.OrdinalIgnoreCase)))
        {
            // Про радиус говорим, только если он у этого вида работы есть.
            // У «разгрузки» радиуса нет вовсе, и лишняя половина фразы делает
            // из честного предупреждения бессмыслицу, которую перестают читать
            bool hasRadius = kind.Fields.Any(f =>
                string.Equals(f.Name, WorkFields.Radius, StringComparison.OrdinalIgnoreCase));
            notes.Add($"«{WorkFields.Start}» не задан — работать буду там, где меня застанет " +
                      "эта задача" + (hasRadius ? ", и радиус померю оттуда же" : ""));
        }

        var takeWhat = job.Texts(WorkFields.TakeWhat);
        if (take != null && takeWhat.Count == 0)
            notes.Add($"сундук-источник {take} задан, а «{WorkFields.TakeWhat}» пусто — " +
                      "выгребать сундук подчистую не стану, шаг пропущу");
        if (take == null && takeWhat.Count > 0)
            notes.Add($"«{WorkFields.TakeWhat}» задано, а «{WorkFields.Take}» нет — " +
                      "брать неоткуда, шаг пропущу");

        // ДВЕ НАСТРОЙКИ, КОТОРЫЕ ДРУГ ДРУГА ОТМЕНЯЮТ. Человек поставил галочку
        // «домой за материалом» И назвал сундук задания — галочка не сработает
        // ни разу, и молчать об этом нельзя: он весь вечер будет ждать рейса,
        // которого по его же условию не будет
        if (job.Flag(WorkFields.HomeForMaterial) == true && WorkStorage.Own(deliver, take))
            notes.Add($"«{WorkFields.HomeForMaterial}» стоит, но у задания свой склад " +
                      $"({(deliver != null ? WorkFields.Deliver : WorkFields.Take)}) — " +
                      "домой за материалом не побегу, буду работать тем, что дали");

        double? seconds = null;
        if (job.Int(WorkFields.Minutes) is { } minutes)
        {
            if (minutes > 0)
                seconds = minutes * 60.0;
            else
                notes.Add($"«{WorkFields.Minutes}» = {minutes} не бывает — беру обычный потолок механизма");
        }

        return new WorkPrep(true, "", kind, args.Args, start, deliver, take, takeWhat,
            job.Flag(WorkFields.ToDepot) == true, seconds, notes,
            job.Flag(WorkFields.HomeForMaterial));
    }
}

// ======================================================================
//  КАКОЕ ХРАНИЛИЩЕ ДЕЙСТВУЕТ СЕЙЧАС
// ======================================================================

/// <summary>
/// Ответ на вопрос «куда бот сдаёт добытое ПРЯМО СЕЙЧАС». Вопрос не праздный:
/// на время задачи постоянный склад роли подменяется временным сундуком, и
/// если человек в этот момент не может узнать, какой из них действует, он
/// пойдёт искать руду в пустом сундуке.
/// </summary>
public static class WorkStorage
{
    /// <param name="temp">Временное хранилище задачи; null — подмены нет.</param>
    /// <param name="jobName">Чья задача подменила склад.</param>
    /// <param name="permanent">Постоянные склады роли, названные по-игровому.</param>
    public static string Say(GamePos? temp, string? jobName, IReadOnlyList<string> permanent)
    {
        string where = permanent.Count > 0
            ? $"постоянный склад роли: {string.Join("; ", permanent)}"
            : "постоянного склада у роли нет";

        if (temp is { } t)
            return $"сдаю во временное хранилище задачи «{jobName ?? "без имени"}» — {t}; " +
                   $"после задачи вернётся {where}";

        return permanent.Count > 0
            ? $"сдаю на {where}"
            : "сдавать некуда: ни постоянного склада, ни временного хранилища";
    }

    /// <summary>
    /// У ЗАДАНИЯ СВОЙ СКЛАД: человек назвал сундук — временное хранилище
    /// («сдавать») или источник материалов («брать»).
    ///
    /// ЧИСТОЕ ПРАВИЛО, и на нём стоит вся отлучка домой (см.
    /// <see cref="WorkSupply"/>). Заказчик сказал прямо: домой за ресурсами
    /// бежать можно, «если в задании не указано, что склад для крафта и
    /// добытых ресурсов в задании определенный». Назвал сундук — значит
    /// работать этим сундуком, а не тащить полсклада с другого конца карты.
    /// </summary>
    public static bool Own(GamePos? deliver, GamePos? take) => deliver != null || take != null;
}

// ======================================================================
//  НЕ ХВАТИЛО МАТЕРИАЛА — СБЕГАТЬ ДОМОЙ И ВЕРНУТЬСЯ ДОДЕЛЫВАТЬ
// ======================================================================

/// <summary>
/// ЧЕГО НЕ ХВАТИЛО НА КРАФТ И СТОИТ ЛИ БЕЖАТЬ ЗА ЭТИМ ДОМОЙ.
///
/// ЗАКАЗ (слова заказчика, 09.08): «если не хватает чего для крафта и в задании
/// не указано, что склад для крафта и добытых ресурсов в задании определенный,
/// то можно бежать домой за ресурсами, чтобы вернуться и достроить — доп
/// настройка». Живьём это дорога: полотно кончилось на середине, дорожный
/// камень крафтится из камня и земли, камень лежит дома в сундуках — а задача
/// просто вставала с «мостить нечем».
///
/// ЗДЕСЬ ТОЛЬКО ЧИСТЫЕ ПРАВИЛА: чего не хватило, стоит ли идти, сколько раз и
/// что нести. Сам рейс делает <see cref="WorkRunner"/> складом
/// (<c>Depot.TakeAsync</c>), а «вернуться доделывать» — тот же навык, который
/// внутри себя уже ходит через общий возврат (<see cref="Resume"/>). Второго
/// механизма возврата тут нет и заводить его нельзя.
/// </summary>
public static class WorkSupply
{
    /// <summary>
    /// ЧЕГО НЕ ХВАТИЛО — из отказа самой работы.
    ///
    /// Спрашивать напрямую не у кого: бегун зовёт навык по имени и не знает ни
    /// про дорогу, ни про её рецепты. Зато нехватку называют вслух все, кто
    /// считает её одинаково, — через <c>Crafting.Missing</c>, и написаний у неё
    /// ровно два:
    ///   «на stonepath-free не хватает: stone (надо 4, есть 0)»
    ///   «не хватает материалов (stone: надо 4, есть 0, dirt: надо 1, есть 0)»
    /// Оба и разбираем. Ничего не разобралось — пусто, и бегун честно скажет,
    /// что бежать не за чем: догадываться, чего боту недостаёт, нельзя.
    /// </summary>
    public static IReadOnlyList<(string Code, int Need, int Have)> Lacking(string? reason)
    {
        var found = new List<(string Code, int Need, int Have)>();
        if (reason is not { Length: > 0 })
            return found;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in Gap.Matches(reason))
        {
            string code = m.Groups["code"].Value.Trim();
            if (code.Length == 0 ||
                !int.TryParse(m.Groups["need"].Value, out int need) ||
                !int.TryParse(m.Groups["have"].Value, out int have))
                continue;
            if (seen.Add(code))
                found.Add((code, need, have));
        }
        return found;
    }

    /// <summary>
    /// «имя (надо N, есть M)» и «имя: надо N, есть M» — оба написания сразу.
    /// В имени допускаем то, чем зовут предметы игра и её моды: буквы, цифры,
    /// дефис, подчёркивание, звёздочку, двоеточие домена и «собаку» тега.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Gap =
        new(@"(?<code>[\p{L}\p{Nd}_\-*:@/]+)\s*(?:\(\s*|:\s*)надо\s+(?<need>\d+)\s*,\s*есть\s+(?<have>\d+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// БЕЖАТЬ ЛИ ДОМОЙ. ЧИСТОЕ ПРАВИЛО — всё «стоит ли» и «сколько раз» стоит
    /// на нём, и стенд проверяет именно его.
    /// </summary>
    /// <param name="allowed">Разрешено настройкой (задачи или бегуна).</param>
    /// <param name="ownStorage">У задания свой склад — см. <see cref="WorkStorage.Own"/>.</param>
    /// <param name="lacking">Работа назвала, чего именно ей не хватило.</param>
    /// <param name="runsAlready">Сколько раз за эту задачу уже бегали.</param>
    /// <param name="maxRuns">Потолок рейсов на одну задачу.</param>
    public static bool Should(bool allowed, bool ownStorage, bool lacking,
        int runsAlready, int maxRuns) =>
        allowed && lacking && !ownStorage && runsAlready < maxRuns;

    /// <summary>
    /// ПОЧЕМУ НЕ ПОБЕЖАЛИ — словами. Молчание тут стоит вечера: человек утром
    /// видит «не вышла: мостить нечем» и не понимает, отчего бот не сходил за
    /// камнем, которого дома два сундука. Пусто — бежать можно.
    /// </summary>
    public static string WhyNot(bool allowed, bool ownStorage, bool lacking,
        int runsAlready, int maxRuns)
    {
        if (!lacking)
            return "чего именно не хватило, работа не назвала — домой бежать не за чем";
        if (!allowed)
            return $"«{WorkFields.HomeForMaterial}» выключено — домой за материалом не бегу";
        if (ownStorage)
            return "у задания свой склад — беру только из него, домой за материалом не бегу";
        if (runsAlready >= maxRuns)
            return $"домой за материалом бегал {Resume.Times(runsAlready)}, и всё равно не хватило — бросаю";
        return "";
    }

    /// <summary>
    /// ЧТО И СКОЛЬКО НЕСТИ СО СКЛАДА — ДВЕРЬ К ЕДИНСТВЕННОМУ ПРАВИЛУ
    /// (<see cref="Depot.Shopping"/>). Своей копии здесь нет и быть не должно:
    /// на склад ходит склад, и «сколько брать» обязано считаться одним счётом
    /// и для очереди задач, и для крафта, зовущего склад напрямую.
    /// </summary>
    public static IReadOnlyList<(string Code, int Count)> Shopping(
        IReadOnlyList<(string Code, int Need, int Have)> lacking, int batch) =>
        Depot.Shopping(lacking, batch);

    /// <summary>Чего не хватило — одной строкой; та же дверь к складу.</summary>
    public static string Names(IReadOnlyList<(string Code, int Need, int Have)> lacking) =>
        Depot.Names(lacking);
}

// ======================================================================
//  СКОЛЬКО ЗАЙМЁТ ЗАДАЧА (без этого числа планировать наперёд нечем)
// ======================================================================

/// <summary>
/// ОДНА СОСТАВЛЯЮЩАЯ РАБОТЫ: столько-то единиц по столько-то секунд каждая.
///
/// ЗАЧЕМ СЧИТАТЬ ПО ЧАСТЯМ, А НЕ ОДНИМ ЧИСЛОМ. Оценка обязана говорить, из чего
/// она сложена: «720 кл. × 2,1 с» человек проверяет глазом и спорит с ней по
/// делу, а «полчаса» проверить нечем — и такое число он справедливо считает
/// выдумкой. Ровно этим вопросом заказчик и поймал прошлую оценку.
///
/// Половина части может быть неизвестна, и это НОРМА, а не ошибка: клеток мы
/// насчитали, а какая там порода — ещё не видели. Такая часть в срок не идёт
/// вовсе (<see cref="Known"/>), и <see cref="WorkGuess"/> называет вслух,
/// какой именно половины ему не хватило.
/// </summary>
/// <param name="What">Что делаем: «копать», «пройти полотно».</param>
/// <param name="Units">Сколько единиц. 0 и меньше — «сколько, не знаю».</param>
/// <param name="Unit">Как зовут единицу: «кл.», «бл».</param>
/// <param name="Each">Секунд за одну единицу. 0 и меньше — «почём, не знаю».</param>
public readonly record struct WorkPart(string What, double Units, string Unit, double Each)
{
    /// <summary>Известны ОБЕ половины — только тогда часть можно считать.</summary>
    public bool Known => Units > 0 && Each > 0 &&
                         double.IsFinite(Units) && double.IsFinite(Each);

    /// <summary>Сколько эта часть займёт, с. Неизвестная часть — ноль, а не догадка.</summary>
    public double Seconds => Known ? Units * Each : 0;

    public override string ToString() => $"{What} {Units:0} {Unit} × {Each:0.##} с = {Seconds:0} с";
}

/// <summary>
/// ВОЗНЯ ВОКРУГ УДАРОВ, ПОМЕРЕННАЯ НА ПРОШЛОМ ДЕЛЕ: во сколько раз всё дело
/// длиннее чистого удержания кнопки.
///
/// Ради чего. Заказчик сказал про бота: «копает в разы дольше живого игрока,
/// потому что не соседние блоки и долго между ними переключается». С тех пор
/// это не мнение, а два числа рядом — их считает <see cref="MiningTally"/> и
/// печатает сам («блоков 2: удары 2,1 с, накладные 0,5 с (18 %), смен руки 2»).
/// Прикидка «клетки × секунды на клетку» считает ТОЛЬКО удары, и без этого
/// множителя она занижена ровно на возню: подход, смену руки, ответ сервера.
///
/// ЧИСЛО БЕРЁТСЯ ИЗ ЗАМЕРА И НИОТКУДА БОЛЬШЕ. Не мерили — множителя нет, и
/// оценка честно говорит, что возня в неё не заложена. Выдуманный «коэффициент
/// накладных» был бы ровно тем гаданием, ради запрета которого писан этот файл.
/// </summary>
/// <param name="Ratio">Во сколько раз дело длиннее ударов (1 — возни не было).</param>
/// <param name="Why">Откуда взялся множитель — словами, для журнала.</param>
public readonly record struct WorkFuss(double Ratio, string Why)
{
    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: из счёта прошлого дела — в множитель. null — мерить было
    /// нечего (ни блока, ни секунды ударов) или возни не оказалось вовсе.
    /// </summary>
    /// <param name="blocks">Сколько блоков ломали (<see cref="MiningTally.Blocks"/>).</param>
    /// <param name="diggingSeconds">Сколько из них ушло на удержание кнопки.</param>
    /// <param name="totalSeconds">Сколько заняло всё вместе с вознёй.</param>
    public static WorkFuss? Of(int blocks, double diggingSeconds, double totalSeconds)
    {
        if (blocks <= 0 || diggingSeconds <= 0 ||
            !double.IsFinite(diggingSeconds) || !double.IsFinite(totalSeconds) ||
            totalSeconds <= diggingSeconds)
            return null;

        double ratio = totalSeconds / diggingSeconds;
        return new WorkFuss(ratio,
            $"и ещё {(ratio - 1) * 100:0} % сверху на возню вокруг ударов (подход, смена руки, " +
            $"ответ сервера): на прошлом деле {blocks} бл стоили {diggingSeconds:0.#} с ударов " +
            $"и {totalSeconds:0.#} с всего");
    }
}

/// <summary>
/// ПРИКИДКА СРОКА ИЗ САМОЙ РАБОТЫ — то, чем живёт первый вечер, когда прошлого
/// замера ещё нет.
///
/// ЗАКАЗЧИК ПОЙМАЛ ЭТО СЛОВАМИ: «а почему револьвер со второго захода? ты
/// уверен, что это правильно?» — и был прав. Срок задачи брался ТОЛЬКО из
/// <see cref="WorkItem.Took"/>, то есть из ПРОШЛОГО захода, и в первый вечер
/// планирования не было вовсе: карьер на 720 клеток и поход за угол выглядели
/// одинаково — «не знаю». Это не свойство задачи, а лень: объём работы известен
/// ДО того, как её начали.
///
/// ЗДЕСЬ ПО-ПРЕЖНЕМУ НЕТ НИ ОДНОГО ВЫДУМАННОГО ЧИСЛА. Каждое приходит от того
/// механизма, который за него отвечает: сколько клеток — у карьера
/// (<c>Quarry.Plan</c>) и у дороги (<c>RoadPlan.Rows</c>), сколько секунд стоит
/// клетка — у добычи (<c>Mining.FastestSeconds</c>, а та берёт сопротивление
/// блока и скорость инструмента из реестра игры), скорость шага — у движения
/// (<c>Movement.WalkSpeed</c>), возня — из замера прошлого дела
/// (<see cref="WorkFuss"/>). Нет числа — нет прикидки и есть названная причина.
/// </summary>
/// <param name="Seconds">Прикидка, с. null — прикинуть нечем.</param>
/// <param name="Why">Из чего сложено (или почему не сложилось) — словами.</param>
/// <param name="Parts">Части, которые вошли в счёт.</param>
public sealed record WorkGuess(double? Seconds, string Why, IReadOnlyList<WorkPart> Parts)
{
    /// <summary>Прикинуть нечем, и вот почему. Молчания здесь быть не может.</summary>
    public static WorkGuess Nothing(string why) => new(null, why, []);

    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО: части плюс померенная возня. Ни мира, ни бота — потому и
    /// проверяется стендом на живых числах из журнала.
    /// </summary>
    /// <param name="what">Что за работа — одной строкой для человека.</param>
    /// <param name="parts">Составляющие; неизвестные в срок не идут.</param>
    /// <param name="fuss">Возня вокруг ударов; null — не мерена, и об этом скажут.</param>
    /// <param name="notCounted">Что в срок НЕ заложено (это обязаны знать).</param>
    public static WorkGuess Of(string what, IReadOnlyList<WorkPart> parts,
        WorkFuss? fuss = null, string notCounted = "")
    {
        var known = parts.Where(p => p.Known).ToList();
        if (known.Count == 0)
            return Nothing($"{what}: считать нечем — " + (parts.Count == 0
                ? "объёма работы я не знаю"
                : string.Join("; ", parts.Select(Missing))));

        // Возня — МНОЖИТЕЛЬ, а не слагаемое: она приходится на каждый удар, и
        // на карьере в 720 клеток её набегает больше, чем всё дело на десяти
        double ratio = fuss is { Ratio: > 1 } measured ? measured.Ratio : 1;

        var words = new List<string> { $"{what}: " + string.Join(" + ", known) };
        words.Add(fuss is { Ratio: > 1 } f
            ? f.Why
            : "возню вокруг ударов в срок не кладу — я её ещё не мерил");
        if (notCounted.Length > 0)
            words.Add(notCounted);

        return new WorkGuess(known.Sum(p => p.Seconds) * ratio, string.Join("; ", words), known);
    }

    /// <summary>Какой половины части не хватило — по имени, а не «нет данных».</summary>
    private static string Missing(WorkPart p) => p.Units > 0
        ? $"{p.What} {p.Units:0} {p.Unit} я насчитал, а во сколько обойдётся одна — нет: " +
          "какая там порода и чем я её возьму, отсюда не видно"
        : $"сколько будет {p.What} ({p.Unit}) — не знаю";
}

/// <summary>
/// ЧТО ИЗВЕСТНО ПРО ЗАДАЧУ К МИНУТЕ ПРИКИДКИ.
///
/// Числа собирает бегун (<see cref="WorkRunner.GuessOf"/>): он один умеет
/// переводить игровые точки в мировые и спрашивать механизмы. Складывает их
/// правило вида работы (<see cref="WorkSizer"/>) — поэтому правило и можно
/// проверить стендом, не поднимая ни сервера, ни мира.
/// </summary>
/// <param name="Ctx">Механизмы бота: у них и спрашивают объёмы работы.</param>
/// <param name="Job">Сама задача: её поля старше умолчаний механизма.</param>
/// <param name="From">
/// Откуда начнётся работа, МИРОВАЯ клетка: точка «старт», а нет её — та, где
/// бот стоит сейчас. null — неизвестно (спавн ещё не пришёл, позиции нет).
/// </param>
/// <param name="To">Куда работа ведёт, МИРОВАЯ клетка: поле «конец». null — не задано.</param>
/// <param name="WalkSpeed">Скорость шага, бл/с — её считает игра.</param>
/// <param name="SecondsPerCell">
/// Сколько держать кнопку на клетке породы В ТОЧКЕ НАЧАЛА РАБОТЫ, с (лучшим
/// инструментом из сумок). 0 — породы не видно: чанк ещё не пришёл или там
/// воздух. Заглянуть на двадцать слоёв вниз, не отправив туда бота, нечем —
/// поэтому число одно и берётся оно оттуда, куда бот и придёт.
/// </param>
/// <param name="Fuss">Возня вокруг ударов с прошлого дела; null — не мерена.</param>
public sealed record WorkSizing(BotContext Ctx, WorkItem Job, BlockPos? From, BlockPos? To,
    double WalkSpeed, double SecondsPerCell, WorkFuss? Fuss = null);

/// <summary>
/// ИЗ ЧЕГО СКЛАДЫВАЕТСЯ СРОК ЭТОЙ РАБОТЫ. Механизм — здесь, а какой навык как
/// зовут — уговор роли, поэтому правило и лежит в таблице связок
/// (<see cref="WorkBinding.Size"/>), а не в списке имён внутри библиотеки.
/// </summary>
public delegate WorkGuess WorkSizer(WorkSizing at);

/// <summary>
/// ПРИКИДКИ ПО УМОЛЧАНИЮ — ровно те четыре работы, чей объём известен ДО начала:
/// карьер, штольня, дорога и разведка. Каждая спрашивает объём У СВОЕГО
/// МЕХАНИЗМА и не считает его заново: второй счёт клеток разошёлся бы с первым
/// на первой же правке, и человек читал бы срок от одной ямы, а копал бот
/// другую.
///
/// Чего здесь нет намеренно: наряда («принеси меди 20» — сколько её искать по
/// округе, из задачи не видно), сбора и разгрузки. Для них бегун честно скажет
/// «прикидки нет», а срок останется дорогой до места, как и был.
/// </summary>
public static class WorkSizes
{
    /// <summary>
    /// КАРЬЕР: число клеток спрашивается у самого карьера
    /// (<c>Quarry.Plan(...).Total</c>) — того же плана, по которому он и будет
    /// копать, с уже вычтенным пандусом. Живьём это «карьер 7 15» = 720 клеток
    /// из журнала, тот самый, что бросили на 67-й.
    ///
    /// Сторону и глубину берём ТОЛЬКО из полей задачи. Пустое поле означает «при
    /// пуске подставит роль своими умолчаниями», а умолчаний роли библиотека не
    /// знает и знать не должна — гадать за неё нельзя, можно только сказать это
    /// вслух и назвать поля по именам.
    ///
    /// ПЛАН СТРОИТСЯ ПО-НАСТОЯЩЕМУ, и это стоит прохода по объёму. Звать прикидку
    /// на каждый опрос панели поэтому не надо — её место перед выходом на работу,
    /// раз на задачу. Зато число выходит то же самое и до прихода чанка: клетка
    /// либо в «крошку», либо в «целые», а <c>Total</c> — их сумма, и от того, что
    /// именно стоит в мире, она не зависит.
    /// </summary>
    public static WorkGuess Quarry(WorkSizing at)
    {
        if (at.Ctx.Quarry is not { } quarry)
            return WorkGuess.Nothing("карьера у этого бота нет — прикинуть его срок нечем");
        if (at.From is not { } centre)
            return WorkGuess.Nothing("где копать, я пока не знаю — без центра карьера объём не посчитать");

        if (at.Job.Int(WorkFields.Width) is not { } side || side < 1 ||
            at.Job.Int(WorkFields.Depth) is not { } depth || depth < 1)
            return WorkGuess.Nothing(
                $"«{WorkFields.Width}» и «{WorkFields.Depth}» у задачи не заданы — сколько клеток " +
                "копать, отсюда не видно (при пуске их подставит роль своими умолчаниями, а гадать " +
                "за неё я не стану: поставьте оба поля, и срок посчитаю)");

        int cells = quarry.Plan(centre, side, depth).Total;
        return WorkGuess.Of($"карьер {side}×{side} на {depth} слоёв",
            [new WorkPart("копать", cells, "кл.", at.SecondsPerCell)], at.Fuss,
            "пустые клетки (пещера, вода) сочтены породой — это единственное место, где срок может " +
            "выйти БОЛЬШЕ настоящего: что внутри ямы, до раскопа не видно");
    }

    /// <summary>
    /// ШТОЛЬНЯ: шагов НЕ МЕНЬШЕ, чем клеток по горизонтали, и не меньше, чем
    /// перепад высоты. Это прямо следует из того, как проходка ходит: «высота
    /// меняется не больше чем на клетку за шаг и всегда вместе с шагом в
    /// сторону» (<see cref="Tunnel"/>, ход лесенкой, а не колодцем).
    ///
    /// Настоящую ломаную здесь не считают НАРОЧНО: она зависит от того, что
    /// встретится по дороге, и повторять её вторым кодом значит завести вторую
    /// проходку. Оценка снизу на то и снизу.
    ///
    /// СРОК И РАБОТА ОТВЕЧАЮТ ОДНО И ТО ЖЕ — ИНАЧЕ ЭТО ДВА ГОЛОСА ОДНОЙ РУЧКИ.
    /// Находка приёмки: высота хода читалась здесь МИМО заставы
    /// <see cref="VsBotKit.Tunnel.TooLow"/>, да ещё и с молчаливым зажимом
    /// «не меньше единицы». Живьём при <c>ВысотаШтольни = 1</c> это читалось
    /// так: прикидка бодро отвечала «штольня N шагов, ход высотой 1» и отдавала
    /// число револьверу (который по нему решает, есть ли время поесть), а
    /// <see cref="VsBotKit.Tunnel.DigToAsync"/> ту же самую работу начать
    /// отказывался — «такую нору я выкопаю, но пройти по ней не смогу». Обещание
    /// и отказ на одну ручку. Спрашиваем ту же заставу и теми же словами: они
    /// называют ручку по-человечески и говорят, чем это чинится.
    /// </summary>
    public static WorkGuess Tunnel(WorkSizing at)
    {
        if (at.Ctx.Tunnel is not { } tunnel)
            return WorkGuess.Nothing("проходки у этого бота нет — прикинуть её срок нечем");
        if (VsBotKit.Tunnel.TooLow(tunnel.Height, tunnel.Knobs.Height) is { Stop: true } low)
            return WorkGuess.Nothing($"срок штольни не считаю — этой работы не будет вовсе: {low.Why}");
        if (at.From is not { } mouth)
            return WorkGuess.Nothing("откуда вести ход, я пока не знаю — длину прикинуть не из чего");
        if (at.To is not { } target)
            return WorkGuess.Nothing($"«{WorkFields.End}» не задан — куда вести ход, а значит и " +
                                     "сколько его вести, отсюда не видно");

        int steps = Math.Max(Math.Abs(target.X - mouth.X) + Math.Abs(target.Z - mouth.Z),
                             Math.Abs(target.Y - mouth.Y));
        if (steps <= 0)
            return WorkGuess.Nothing("устье и цель — одна клетка: вести ход некуда");

        // Зажима «не меньше единицы» тут больше нет: застава выше уже отказала
        // за всё, что ниже роста тела, а выше него зажимать нечего
        int height = tunnel.Height;
        return WorkGuess.Of($"штольня {steps} шагов, ход высотой {height}",
            [new WorkPart("вскрыть", (double)steps * height, "кл.", at.SecondsPerCell)], at.Fuss,
            "крюки в обход помех и заделку пола не считаю; пустые клетки по дороге (пещера, вода) " +
            "сочтены породой — на них срок может выйти больше настоящего");
    }

    /// <summary>
    /// ДОРОГА: длину знает сама раскладка полотна (<see cref="RoadPlan.Rows"/>),
    /// скорость шага — движение. Ход проходится ДВАЖДЫ, пока стоит ручка
    /// «сперва выкопать весь ход, потом мостить» (<c>Roads.DigFirst</c>), и это
    /// не догадка: цена названа в самой ручке — «ход бот проходит ДВАЖДЫ».
    ///
    /// Укладка и порода на пути в срок НЕ идут: сколько стоит поставить блок,
    /// никто не мерил, а что стоит на трассе — до выхода не видно. Значит это
    /// оценка снизу, и сказано об этом вслух.
    /// </summary>
    public static WorkGuess Road(WorkSizing at)
    {
        if (at.Ctx.Roads is not { } roads)
            return WorkGuess.Nothing("дорог у этого бота нет — прикинуть их срок нечем");
        if (at.From is not { } from || at.To is not { } to)
            return WorkGuess.Nothing($"«{WorkFields.Start}» и «{WorkFields.End}» — оба нужны: " +
                                     "без них длина дороги неизвестна");

        int width = Math.Clamp(at.Job.Int(WorkFields.Width) ?? roads.Width, 1, RoadPlan.MaxWidth);
        int rows = RoadPlan.Rows(from, to, width).Count;
        int passes = roads.DigFirst ? 2 : 1;
        int cells = RoadPlan.Cells(from, to, width).Count;

        return WorkGuess.Of(
            $"дорога {rows} рядов шириной {width}" + (passes > 1 ? ", ход проходится дважды" : ""),
            [new WorkPart("пройти полотно", (double)rows * passes, "кл.",
                at.WalkSpeed > 0 ? 1 / at.WalkSpeed : 0)],
            null,
            $"саму укладку {cells} клеток полотна и породу на пути в срок не кладу: сколько стоит " +
            "поставить блок, я не мерил");
    }

    /// <summary>
    /// РАЗВЕДКА: объём свой — ствол вниз и штреки в стороны, и все три числа
    /// стоят настройками у самой разведки (<c>DepthBelowStart</c>,
    /// <c>Drifts</c>, <c>DriftLength</c>), а высота хода — у проходки, которой
    /// разведка и копает.
    ///
    /// ОБХОД ОКРУГИ НОГАМИ В СРОК НЕ ИДЁТ, и это главное, о чём тут надо сказать
    /// вслух: спираль обхода прокладывается по местности, её длина зависит от
    /// оврагов и заборов, и повторять её счёт вторым кодом значило бы завести
    /// вторую разведку.
    ///
    /// РАЗВЕДКА КОПАЕТ ПРОХОДКОЙ — ЗНАЧИТ И ОТКАЗЫВАЕТ ЕЮ ЖЕ. Здесь стояло
    /// <c>at.Ctx.Tunnel?.Height ?? 1</c>: без проходки срок считался по
    /// выдуманной единице, хотя сама разведка без неё не начинается вовсе
    /// («проходка недоступна», <see cref="VsBotKit.Prospecting.SearchAsync"/>), а
    /// при низкой ручке хода прикидка называла число, которого работа не
    /// подтвердит (разбор — у <see cref="Tunnel(WorkSizing)"/>).
    /// </summary>
    public static WorkGuess Prospecting(WorkSizing at)
    {
        if (at.Ctx.Prospecting is not { } prospecting)
            return WorkGuess.Nothing("разведки у этого бота нет — прикинуть её срок нечем");
        if (at.Ctx.Tunnel is not { } tunnel)
            return WorkGuess.Nothing("проходки у этого бота нет, а разведка копает ею — " +
                                     "прикинуть её срок нечем");
        if (VsBotKit.Tunnel.TooLow(tunnel.Height, tunnel.Knobs.Height) is { Stop: true } low)
            return WorkGuess.Nothing($"срок разведки не считаю — она копает тем же ходом, а хода " +
                                     $"не будет: {low.Why}");

        int depth = Math.Max(0, at.Job.Int(WorkFields.Depth) ?? prospecting.DepthBelowStart);
        int drifts = Math.Max(0, prospecting.Drifts);
        int driftLength = Math.Max(0, prospecting.DriftLength);
        int height = tunnel.Height;
        double cells = ((double)depth + (double)drifts * driftLength) * height;

        int radius = at.Job.Int(WorkFields.Radius) ?? prospecting.SweepRadius;
        return WorkGuess.Of(
            $"разведка: ствол {depth} бл вниз и {drifts} штрека по {driftLength} кл., ход высотой {height}",
            [new WorkPart("вскрыть", cells, "кл.", at.SecondsPerCell)], at.Fuss,
            prospecting.WalkAround
                ? $"обход округи ({prospecting.SweepStops} точек в радиусе {radius} бл) в срок не " +
                  "кладу: его длину задаёт местность, а не задача"
                : "");
    }
}

/// <summary>
/// СКОЛЬКО ПРИМЕРНО ЗАЙМЁТ ЗАДАЧА — и откуда это известно.
///
/// Ради чего. Револьверный цикл (<see cref="Revolver"/>) решает «поесть сейчас
/// или после» по одному числу: сколько продлится дело. Живой случай, который
/// он лечит, — наряд за 90 блоков, прерванный обедом на середине и отменённый
/// целиком. Без честного срока это решение принять нельзя.
///
/// ЗДЕСЬ НЕТ НИ ОДНОГО ВЫДУМАННОГО ЧИСЛА, и это главное правило файла.
/// Известны ровно три источника:
/// • сколько задача заняла В ПРОШЛЫЙ РАЗ (<see cref="WorkItem.Took"/>) — факт;
/// • сколько идти до точки работы: расстояние ПО ПРЯМОЙ, делённое на скорость
///   шага, которую считает сама игра (<c>Movement.WalkSpeed</c>);
/// • сколько работы В САМОЙ ЗАДАЧЕ (<see cref="WorkGuess"/>): клетки карьера,
///   ряды дороги, шаги штольни — помноженные на то, что стоит одна клетка.
///
/// ЗАМЕР СТАРШЕ ПРИКИДКИ, И ЭТО НЕ ВКУСОВЩИНА. Прошлый заход — это факт со
/// всей его правдой: и порода была настоящая, и бот отвлекался ровно столько,
/// сколько отвлекался. Прикидка же считает ровно то, что видно наперёд. Пока
/// замера нет — считаем работу; появился замер — он и побеждает, а прикидка
/// остаётся в объяснении, чтобы человек видел, насколько она врала.
///
/// НО ТОЛЬКО ЗАМЕР ЗАХОДА, ДОШЕДШЕГО ДО КОНЦА (<see cref="WorkItem.TookDone"/>).
/// Это находка приёмки и одна из самых дорогих: замер пишется при ЛЮБОМ исходе,
/// и «нечем взять породу» на второй секунде давало <c>Took = 2</c>. Дальше
/// оценка объявляла двухсекундный замер точнее полуторатысячной прикидки — и
/// револьвер решал, что перед выходом ничего не созреет, а бот уходил на весь
/// вечер голодным. Оборванный заход теперь не срок задачи, а НИЖНЯЯ ГРАНИЦА:
/// столько работа уже длилась, и меньше не выйдет — но сколько выйдет, честнее
/// спросить у прикидки. Оба числа названы человеку, и названо, почему выбрано
/// большее.
///
/// ТРЕТИЙ ИСТОЧНИК ПОЯВИЛСЯ ПО ЖАЛОБЕ ЗАКАЗЧИКА: «а почему револьвер со
/// второго захода?» — потому что срок брался только из прошлого замера, и в
/// первый вечер планирования не было вовсе. Объём работы известен ДО того, как
/// её начали, и не пользоваться этим было ленью, а не осторожностью.
///
/// Все три — оценка СНИЗУ, и это сказано вслух (<see cref="AtLeast"/>): дорога
/// всегда длиннее прямой, прошлый заход мог пройти удачнее нынешнего, а
/// прикидка не знает ни перерывов на обед, ни того, что стоит на трассе.
/// Единственное, где прикидка может хватить ЛИШНЕГО, — пустоты внутри объёма
/// (пещера в карьере): их видно только раскопом, и потому они сочтены породой.
/// Каждая прикидка называет это сама, в своей же строке.
/// Занижать срок безопаснее, чем завышать: при заниженном сроке планировщик
/// реже лезет вперёд с «сделай сейчас», то есть чаще ведёт себя как прежний
/// бот.
///
/// ПОЧЕМУ ПОТОЛОК «МИНУТ» НЕ ГОДИТСЯ В ОЦЕНКУ. Это предел, за которым работу
/// обрывают, а не срок, за который её сделают: у карьера на 15 минут работа
/// может кончиться за две. Считать предел оценкой значило бы гадать — и
/// гадать в опасную сторону (планировщик стал бы вечно всё делать «заранее»).
/// </summary>
/// <param name="Seconds">Оценка, с. null — оценивать нечем.</param>
/// <param name="AtLeast">Это оценка СНИЗУ («не меньше»), а не точный срок.</param>
/// <param name="Why">Откуда взялось число — словами, для журнала.</param>
public sealed record WorkEta(double? Seconds, bool AtLeast, string Why)
{
    /// <summary>
    /// ЧИСТОЕ ПРАВИЛО. Ни бота, ни мира — только числа, поэтому оно проверяется
    /// стендом, а не живым прогоном.
    /// </summary>
    /// <param name="measuredSeconds">Сколько заняла прошлая попытка; null — не бралась.</param>
    /// <param name="blocksAway">Расстояние до точки работы по прямой; null — идти некуда или неизвестно.</param>
    /// <param name="walkSpeed">Скорость шага, бл/с (её считает игра).</param>
    /// <param name="capSeconds">Потолок «минут» — В ОЦЕНКУ НЕ ИДЁТ, только в объяснение.</param>
    /// <param name="guess">
    /// Прикидка по самой работе (<see cref="WorkGuess"/>); null — прикидывать
    /// было нечем и об этом даже не спрашивали. Отказавшаяся прикидка тоже
    /// приходит сюда — со своей причиной, и причина уходит человеку: молчаливое
    /// «не знаю» он читает как лень, и в этот раз читал справедливо.
    /// </param>
    /// <param name="measuredToTheEnd">
    /// Тот заход ДОШЁЛ ДО КОНЦА (<see cref="WorkItem.TookDone"/>). false —
    /// замер есть, но он про оборванный заход, и сроком задачи он не является:
    /// от него берётся только «меньше этого не выйдет».
    /// </param>
    public static WorkEta Of(double? measuredSeconds, double? blocksAway, double? walkSpeed,
        double? capSeconds, WorkGuess? guess = null, bool measuredToTheEnd = true)
    {
        double? travel = blocksAway is { } blocks && blocks > 0 &&
                         walkSpeed is { } speed && speed > 0
            ? blocks / speed
            : null;
        double? measured = measuredSeconds is { } m && m > 0 ? m : null;
        double? work = guess is { Seconds: { } w } && w > 0 ? w : null;

        if (measured == null && travel == null && work == null)
            return new WorkEta(null, false,
                "сколько это займёт, я не знаю: задача ещё не бралась, а дороги до неё нет" +
                (guess is { } none ? $"; по самой работе тоже не прикинуть — {none.Why}" : "") +
                (capSeconds is { } cap
                    ? $" (потолок {cap / 60:0.#} мин — это предел, а не оценка: гадать по нему не стану)"
                    : ""));

        var parts = new List<string>();
        double seconds;

        if (measured is { } was && measuredToTheEnd)
        {
            // ЗАМЕР ПОБЕЖДАЕТ ПРИКИДКУ: он — факт, и в нём уже есть всё, чего
            // прикидка не видит (порода, перерывы, возня). Складывать их нельзя
            // тем более: это два ответа на ОДИН вопрос, а не две части дела
            seconds = Math.Max(was, travel ?? 0);
            parts.Add($"прошлый заход занял {was:0} с");
            if (travel is { } way)
                parts.Add($"до места по прямой {blocksAway:0} бл — это {way:0} с ходу");
            if (work is { } guessed)
                parts.Add($"по работе вышло бы {guessed:0} с, но замер прошлого захода точнее — " +
                          "он про эту самую задачу и со всей её правдой");
        }
        else if (measured is { } cut)
        {
            // ОБОРВАННЫЙ ЗАХОД — НЕ СРОК, А НИЖНЯЯ ГРАНИЦА. Он не врёт: работа
            // и вправду длилась столько. Но он не про то, сколько она ЗАЙМЁТ,
            // и объявлять его «точнее прикидки» — это ложь ценой вечера:
            // карьер на 720 клеток, отказавший на второй секунде («нечем взять
            // породу»), обещал бы 2 с, и револьвер решил бы, что перед выходом
            // ничего не созреет
            double honest = (work ?? 0) + (travel ?? 0);
            seconds = Math.Max(cut, honest);
            parts.Add($"прошлый заход оборвался на {cut:0} с — это не срок задачи, " +
                      "а только «меньше не выйдет»");
            if (travel is { } way)
                parts.Add($"до места по прямой {blocksAway:0} бл — это {way:0} с ходу");
            if (guess is { } g)
                parts.Add(work != null
                    ? g.Why + " — по работе и считаю"
                    : $"по работе не прикинуть — {g.Why}");
        }
        else
        {
            // РАБОТА И ДОРОГА К НЕЙ СКЛАДЫВАЮТСЯ, и это не противоречит правилу
            // выше: прикидка считает работу НА МЕСТЕ и дороги до места в себе не
            // содержит — в отличие от замера, который её уже включал
            seconds = (work ?? 0) + (travel ?? 0);
            if (travel is { } way)
                parts.Add($"до места по прямой {blocksAway:0} бл — это {way:0} с ходу");
            if (guess is { } g)
                parts.Add(work != null ? g.Why : $"по работе не прикинуть — {g.Why}");
        }

        return new WorkEta(seconds, true,
            $"не меньше {seconds:0} с: " + string.Join("; ", parts) +
            // Оговорка про сложение — только про ЦЕЛЫЙ замер: в нём дорога уже
            // сидела. У оборванного захода дорога с работой как раз складывается,
            // и та же строка была бы там прямой неправдой
            (measured != null && measuredToTheEnd && travel != null
                ? " (складывать нельзя — прошлый заход уже включал свою дорогу)"
                : ""));
    }

    public override string ToString() =>
        Seconds is { } s ? $"{(AtLeast ? "не меньше " : "")}{s:0} с — {Why}" : Why;
}

// ======================================================================
//  БЕГУН ОЧЕРЕДИ
// ======================================================================

/// <summary>Чем кончилась одна задача.</summary>
public sealed record WorkResult(WorkState State, string Reason)
{
    public bool Ok => State == WorkState.Done;

    public override string ToString() => $"{WorkStates.Word(State)}: {Reason}";
}

/// <summary>
/// РОЛЬ, КОТОРАЯ ХОЧЕТ КРУТИТЬ ОЧЕРЕДЬ СВОИМИ РУЧКАМИ.
///
/// Зачем понадобилось. Пороги очереди — сколько дать на дорогу, бегать ли
/// домой за материалом, сколько раз и с каким запасом — это ПОЛИТИКА: тому,
/// кто копает в трёхстах блоках от дома, дорога стоит пяти минут, а лавочнику
/// у прилавка — полминуты. Повернуть их до сих пор было нечем, и причина
/// техническая: бегун очереди собирается не ролью и не ботом, а программой
/// (Program.cs), и роль о нём не знала ВООБЩЕ — донести ручку было НЕКУДА.
///
/// Второй склад настроек заводить нельзя (разъедется с первым), поэтому
/// знакомство идёт в одну сторону: бегун, собравшись, сам представляется роли
/// бота. Роль запоминает его и с этой минуты доносит свои ручки туда же, куда
/// и все остальные, — на каждый поворот в окне управления.
///
/// Роль этого не умеет — ничего не происходит: очередь работает на своих
/// умолчаниях, как и работала.
/// </summary>
public interface IWorkRunnerRole
{
    /// <summary>Бегун очереди собран — можно доносить до него ручки роли.</summary>
    void UseRunner(WorkRunner runner);
}

/// <summary>
/// ИСПОЛНИТЕЛЬ ОЧЕРЕДИ: превращает задачу в вызов УЖЕ СУЩЕСТВУЮЩЕГО навыка и
/// записывает честный итог.
///
/// Чего он НЕ делает: новых умений. Навыка под вид работы нет — задача
/// отказывается словами <see cref="SkillRunner"/> («навык "сбор" не
/// зарегистрирован»), а не притворяется сделанной. Успех задачи — это
/// <see cref="SkillResult.Success"/> от навыка, а не то, что бегун дошёл до
/// конца своего списка шагов.
///
/// Задача бывает двух видов, и бегуну они одинаковы: готовая из каталога роли
/// (поля собираются в строку аргументов) и свободная — ЛЮБОЙ навык бота со
/// строкой аргументов как есть (<see cref="WorkFree"/>). Оба вида приходят
/// сюда одним и тем же <see cref="WorkPrep"/>, поэтому и пороги, и «встать на
/// клетку выше», и склад работают у них одинаково: связки ищутся по ИМЕНИ
/// НАВЫКА, а не по ключу вида работы.
///
/// Порядок шагов на одну задачу (каждый кончается фактом или названной причиной):
/// разобрать -> подкрутить пороги -> запомнить постоянный склад -> «брать» ->
/// «сдавать» -> «старт» -> навык -> ДОМОЙ ЗА МАТЕРИАЛОМ И ОБРАТНО К НАВЫКУ ->
/// «наСклад» -> ВЕРНУТЬ склад и пороги (и на отмене, и на исключении).
/// </summary>
public sealed class WorkRunner
{
    private readonly VsBot bot;
    private CancellationTokenSource? cts;
    private int busy;

    public WorkRunner(VsBot bot, WorkPlan plan, WorkCatalog? catalog = null)
    {
        this.bot = bot;
        Plan = plan;
        Catalog = catalog;

        // БЕГУН ПРЕДСТАВЛЯЕТСЯ РОЛИ — иначе её ручки до очереди не доедут.
        // Собирает бегуна не роль и не бот, а программа, и роль о нём не знает
        // ниоткуда: без этой строки «сколько дать на дорогу» и «бегать ли домой
        // за материалом» остаются числами внутри библиотеки, повернуть которые
        // человеку нечем (см. IWorkRunnerRole)
        (bot.Role as IWorkRunnerRole)?.UseRunner(this);
    }

    /// <summary>Очередь, которую исполняем.</summary>
    public WorkPlan Plan { get; set; }

    /// <summary>
    /// ГОТОВЫЕ виды работ роли — удобство, а не потолок: чего в каталоге нет,
    /// ставится свободной задачей «навык:X» (<see cref="WorkFree"/>). null —
    /// роль каталога не даёт, и очередь работает одними свободными задачами.
    /// </summary>
    public WorkCatalog? Catalog { get; set; }

    /// <summary>
    /// Все навыки, какие бот знает: имя и описание. Список берётся у самого
    /// бота, а не заводится вторым — иначе новый навык роли появлялся бы в
    /// чате, но не в очереди, и человек считал бы, что бот его не умеет.
    /// </summary>
    public IReadOnlyList<WorkSkill> KnownSkills =>
        bot.Skills.Registered
            .Select(s => new WorkSkill(s.Name, s.Description))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Только имена навыков — ими проверяются свободные задачи.</summary>
    public IReadOnlyList<string> SkillNames =>
        bot.Skills.Registered.Select(s => s.Name).ToList();

    /// <summary>
    /// Что вообще можно поставить в очередь: готовые виды работ роли и все
    /// навыки бота. Это же меню рисует панель.
    /// </summary>
    public IReadOnlyList<WorkOffer> Menu() => WorkMenu.Of(Catalog, KnownSkills);

    /// <summary>
    /// Разобрать задачу так же, как это сделает пуск: тем же каталогом, тем же
    /// списком навыков и с той же оглядкой на позицию спавна.
    ///
    /// Панель обязана спрашивать ИМЕННО ЭТО, а не собирать разбор сама: иначе
    /// она показывает задачу готовой, а бегун её не берёт — и человек весь
    /// вечер ждёт работы, которая не начиналась.
    /// </summary>
    public WorkPrep Prep(WorkItem job) =>
        WorkPrep.Of(Catalog, job, bot.CoordsKnown(), SkillNames);

    /// <summary>
    /// Связки «навык → механизм» для общих полей. Обычный словарь: роль вправе
    /// дописать свой навык одной строкой (<c>runner.Bindings["мой"] = new WorkBinding(...)</c>).
    /// </summary>
    public Dictionary<string, WorkBinding> Bindings { get; } = WorkBindings.Digger();

    /// <summary>
    /// Сколько секунд отводится на дорогу к точке задачи. Это решение РОЛИ и
    /// доносится ручкой («СекундНаДорогуКЗадаче»): копателю, уходящему за
    /// триста блоков, пяти минут в обрез, а лавочнику в своём дворе они значат
    /// пять минут топтания перед запертой калиткой вместо честного отказа.
    /// </summary>
    public double TravelSeconds { get; set; } = 300;

    /// <summary>
    /// БЕЖАТЬ ЛИ ДОМОЙ ЗА НЕДОСТАЮЩИМ МАТЕРИАЛОМ — ДВЕРЬ К ЧИСЛУ СКЛАДА
    /// (<see cref="Depot.SupplyForCraft"/>), а не вторая его копия. Отдельная
    /// задача вправе сказать своё поле
    /// «<see cref="WorkFields.HomeForMaterial"/>», и оно старше этого.
    ///
    /// ПОЧЕМУ ДВЕРЬ, А НЕ СВОЁ ПОЛЕ. Тот же рейс за материалом делает теперь и
    /// крафт, зовущий склад напрямую (<c>Crafting.RestockAsync</c>), — без
    /// очереди задач вовсе. Останься число здесь, человек выключил бы рейс
    /// галочкой роли и всё равно видел бы, как бот уходит со стройки на склад:
    /// ровно тот образец («одно число — два хозяина, и команда об этом
    /// молчала»), который эта работа уже однажды разбирала на пороге оружия.
    ///
    /// Включено по умолчанию, и вот почему это не самоуправство: рейс бывает
    /// ТОЛЬКО когда своего склада у задания нет (человек не сказал, откуда
    /// брать) и когда работа сама назвала, чего именно ей не хватило. В
    /// остальных случаях бот и так вставал с «мостить нечем», а камень лежал
    /// дома в сундуках.
    /// </summary>
    public bool RunHomeForMaterial
    {
        get => bot.Depot.SupplyForCraft;
        set => bot.Depot.SupplyForCraft = value;
    }

    /// <summary>
    /// Сколько раз за ОДНУ задачу отлучаться домой за материалом — дверь к
    /// <see cref="Depot.SupplyRuns"/>. Два — потому что первый рейс лечит
    /// обычную нехватку, второй бывает нужен, когда рецепту нужны два разных
    /// материала и первого хватило, а второй кончился следом. Третий подряд
    /// означает, что дома его тоже нет, и честнее сказать это вслух, чем
    /// бегать всю ночь.
    /// </summary>
    public int MaxHomeRuns
    {
        get => bot.Depot.SupplyRuns;
        set => bot.Depot.SupplyRuns = value;
    }

    /// <summary>
    /// На сколько сборов рецепта брать материал за один рейс — дверь к
    /// <see cref="Depot.SupplyBatch"/>. Рецепт называет нехватку на ОДИН сбор
    /// («камня надо 4»), а дороге нужны сотни: без запаса бот бегал бы домой на
    /// каждый метр полотна.
    /// </summary>
    public int HomeBatch
    {
        get => bot.Depot.SupplyBatch;
        set => bot.Depot.SupplyBatch = value;
    }

    /// <summary>
    /// СКОЛЬКО ПРИМЕРНО ЗАЙМЁТ ЭТА ЗАДАЧА — по замеру прошлого захода, по дороге
    /// до места и по объёму самой работы. Правило чистое и лежит в
    /// <see cref="WorkEta"/>; здесь только сбор чисел у бота.
    /// </summary>
    public WorkEta EtaOf(WorkItem job, WorkPrep prep)
    {
        double? blocks = null;
        if (prep.Start is { } start && bot.CoordsKnown() && bot.Self.Position is { } me)
        {
            var cell = bot.WorldCell(start);
            double dx = cell.X + 0.5 - me.X;
            double dz = cell.Z + 0.5 - me.Z;
            blocks = Math.Sqrt(dx * dx + dz * dz);
        }
        double speed = bot.Movement.WalkSpeed;
        // Замер идёт вместе с ответом «дошёл ли тот заход до конца»: без него
        // двухсекундный отказ выдавал себя за срок карьера (см. WorkItem.TookDone)
        return WorkEta.Of(job.Took, blocks, speed > 0 ? speed : null, prep.Seconds,
            GuessOf(job, prep), job.TookDone);
    }

    /// <summary>
    /// ПРИКИДКА ПО САМОЙ РАБОТЕ — сколько это займёт, если замера прошлого
    /// захода ещё нет.
    ///
    /// ЗДЕСЬ ТОЛЬКО СБОР ЧИСЕЛ, и это важно: перевести игровую точку в мировую и
    /// спросить механизмы умеет бегун, а складывает числа чистое правило вида
    /// работы (<see cref="WorkSizer"/>), которое поэтому и закрыто стендом.
    ///
    /// Каждый отказ называется вслух и по делу: «прикидки для навыка нет»,
    /// «породы в точке не вижу», «ширина не задана». Молчаливое «не знаю» —
    /// это ровно то, за что заказчик и спросил: «а почему со второго захода?»
    /// </summary>
    public WorkGuess GuessOf(WorkItem job, WorkPrep prep)
    {
        if (prep.Kind is not { } kind)
            return WorkGuess.Nothing("задача не разобрана — прикидывать нечего");

        if (!Bindings.TryGetValue(kind.SkillName, out var binding) || binding?.Size is not { } size)
            return WorkGuess.Nothing(
                $"из чего складывается срок навыка «{kind.SkillName}», я не знаю: прикидки для него " +
                $"у меня нет (ставится одной строкой: Bindings[\"{kind.SkillName}\"] = " +
                "new WorkBinding(Size: ...))");

        // ОТКУДА НАЧНЁТСЯ РАБОТА. Точка «старт», а её нет — там, где бот стоит:
        // именно так и поступит сам навык («пусто — где стою»), и прикидка
        // обязана мерить то же место, иначе она честна только на словах
        BlockPos? from = null;
        if (prep.Start is { } start)
        {
            if (bot.CoordsKnown())
                from = bot.WorldCell(start);
        }
        else if (bot.Self.Position is { } me)
        {
            from = new BlockPos((int)Math.Floor(me.X), (int)Math.Floor(me.Y), (int)Math.Floor(me.Z));
        }

        BlockPos? to = bot.CoordsKnown() && job.Cell(WorkFields.End) is { } end
            ? bot.WorldCell(end)
            : null;

        // ПОРОДА — ТА, ЧТО В ТОЧКЕ НАЧАЛА РАБОТЫ. Ноль означает «не вижу»: чанк
        // ещё не пришёл или там воздух, — и тогда прикидка честно откажется, а
        // не посчитает 720 клеток по нулю секунд. Бесконечность («нечем взять»)
        // тоже не число для оценки: с таким инструментом работа не пойдёт вовсе
        double each = 0;
        if (from is { } cell && bot.Context.Mining is { } mining)
        {
            double hold = mining.FastestSeconds(cell);
            each = double.IsFinite(hold) ? hold : 0;
        }

        // ВОЗНЯ БЕРЁТСЯ С ПРОШЛОГО ДЕЛА: счёт сбрасывается на каждый навык
        // (SkillRunner), поэтому здесь, ДО пуска, в нём лежит именно прошлое
        var tally = bot.Context.Mining?.Tally;
        var fuss = tally is { } t ? WorkFuss.Of(t.Blocks, t.Digging, t.Total) : null;

        try
        {
            return size(new WorkSizing(bot.Context, job, from, to,
                bot.Movement.WalkSpeed, each, fuss));
        }
        catch (Exception e)
        {
            // Упавшая прикидка не смеет ронять задачу: она всего лишь оценка,
            // и её беда обязана стать словами, а не отменённым вечером работы
            return WorkGuess.Nothing($"прикидку по работе посчитать не вышло: {e.Message}");
        }
    }

    /// <summary>
    /// ЧТО СДЕЛАТЬ ПЕРЕД ВЫХОДОМ — то самое «заранее планировать, что и когда
    /// сделает бот», о чём просил заказчик.
    ///
    /// ЖИВОЙ СЛУЧАЙ (журнал 23:31): «поесть fruit-pinkapple» важнее — прерываю
    /// «команда добудь»; наряд: принёс 0 из 10, наряд ОТМЕНЁН. И через минуту
    /// то же самое. Бот выходил в дорогу на 90 блоков сытым на 45% и на
    /// середине пути становился голодным — со стороны это выглядело как
    /// кружение на месте.
    ///
    /// Теперь перед выходом задаётся один вопрос: что созреет за то время,
    /// пока я буду занят? Созреет голод — поесть СЕЙЧАС, у порога дома, пока
    /// это стоит десять секунд, а не отмены всей работы. Порог способности
    /// поднимается ровно на один заход и возвращается всегда
    /// (<see cref="NeedHurry"/>).
    ///
    /// ВЫКЛЮЧАЕТСЯ ТОЙ ЖЕ ОДНОЙ ГАЛОЧКОЙ, что и весь револьвер
    /// (<c>bot.Behaviors.UseRevolver</c>): двух выключателей на одно дело быть
    /// не должно — человек выключит один и будет думать, что выключил оба.
    /// </summary>
    private async Task PlanBeforeLeavingAsync(WorkItem job, WorkPrep prep, CancellationToken ct)
    {
        var behaviors = bot.Behaviors;
        if (!behaviors.UseRevolver)
            return;

        // ДАТЧИК СОСТОЯНИЯ ПОДКЛЮЧАЕТСЯ САМ, если этого не сделали при сборке
        // бота. Правильное место для этой строки — там же, где бота собирают
        // (одна строка <c>bot.Behaviors.SenseFrom(bot.Context)</c>); но если её
        // забыли, кривые молча отвечали бы «всё в полную силу», и весь
        // револьвер выглядел бы работающим, ничего не планируя. Молчаливая
        // пустышка хуже отсутствия механизма
        if (behaviors.Sense == null)
        {
            behaviors.SenseFrom(bot.Context);
            Log("состояние для планирования никто не подключил — беру его сам " +
                "(правильнее один раз при сборке бота: bot.Behaviors.SenseFrom(bot.Context))");
        }

        var eta = EtaOf(job, prep);
        if (eta.Seconds is not { } seconds)
        {
            Log($"«{job.Name}»: {eta.Why} — перед выходом ничего не планирую");
            return;
        }

        var ripening = behaviors.Ripening(seconds);
        if (ripening.Count == 0)
        {
            Log($"«{job.Name}»: дело на {eta} — за это время ничего не созреет, выхожу как есть");
            return;
        }

        foreach (var (need, at) in ripening)
        {
            if (ct.IsCancellationRequested)
                return;

            if (!NeedHurry.Prepare(need, behaviors.Now, out string what, out var undo))
            {
                // Молчать нельзя даже здесь: человек должен знать, что бот
                // ЗНАЛ про эту помеху и не смог её убрать заранее
                Log($"«{job.Name}»: «{need.Title}» созреет через {at:0} с, а дело на " +
                    $"{eta.Seconds:0} с — но {what}");
                continue;
            }

            Log($"«{job.Name}»: делаю «{need.Title}» сейчас, потому что в пути " +
                $"(дело на {eta.Seconds:0} с) она созреет через {at:0} с; {what}");

            bool acted;
            try
            {
                acted = await need.TickAsync(ct);
            }
            catch (OperationCanceledException)
            {
                undo();
                throw;
            }
            catch (Exception e)
            {
                acted = false;
                Log($"«{job.Name}»: «{need.Title}» перед выходом сорвалась: {e.Message}");
            }
            finally
            {
                // Порог возвращается ВСЕГДА — и на отказе, и на исключении.
                // Забытый порог «есть при 95%» сделал бы из бота обжору
                // навсегда, и найти это потом было бы нечем
                undo();
            }

            Log(acted
                ? $"«{job.Name}»: «{need.Title}» — сделано до выхода"
                : $"«{job.Name}»: «{need.Title}» до выхода сделать не вышло — иду так, " +
                  "она вмешается сама, когда приспичит");
        }
    }

    /// <summary>Что происходит — словами, в общий журнал бота.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Задача изменилась (состояние, итог, число попыток). Панель по этому
    /// событию сохраняет файл: очередь на вечер не должна пропасть оттого, что
    /// бота выключили между задачами.
    /// </summary>
    public event Action<WorkItem>? OnChanged;

    /// <summary>Задача, которая выполняется прямо сейчас. null — бегун свободен.</summary>
    public WorkItem? Current { get; private set; }

    /// <summary>Бегун занят задачей.</summary>
    public bool Busy => Current != null;

    /// <summary>
    /// Временное хранилище, которым СЕЙЧАС подменён склад роли. null — подмены
    /// нет, действует постоянный склад.
    /// </summary>
    public GamePos? TempStorage { get; private set; }

    /// <summary>Куда бот сдаёт добытое прямо сейчас — одной фразой для человека.</summary>
    public string StorageNow
    {
        get
        {
            // Пока сдвиг спавна не пришёл, назвать склад по-игровому нельзя:
            // получились бы те самые «полмиллиона блоков»
            var names = bot.CoordsKnown()
                ? bot.Depot.Chests.Select(c => bot.GameOf(c).ToString()).ToList()
                : bot.Depot.Chests.Select(c => $"{c} (мировые: спавн ещё не пришёл)").ToList();
            return WorkStorage.Say(TempStorage, Current?.Name, names);
        }
    }

    /// <summary>
    /// Прервать то, что идёт: и навык, и шаги бегуна вокруг него, и возврат
    /// к прерванному (последнее — внутри CancelCurrent: без него бот после
    /// «Остановить» доделывал работу, как только рефлекс отпускал тело).
    /// </summary>
    public void Stop()
    {
        bot.Skills.CancelCurrent("остановили очередь задач");
        cts?.Cancel();
    }

    // ---------------- НОГИ ОЧЕРЕДИ: чьим телом бегун делает свои шаги ----------------

    /// <summary>
    /// ЧЬИМ ИМЕНЕМ ОЧЕРЕДЬ РАСПИСЫВАЕТСЯ У РАСПОРЯДИТЕЛЯ ТЕЛА.
    ///
    /// Имя называет ЗАДАЧУ, а не механизм, потому что читает его человек: оно
    /// уходит и в окно управления («чем занят»), и в каждый отказ распорядителя
    /// («„снарядиться“ не берёт тело: занято „очередь: карьер у реки“»).
    /// «WorkRunner» в такой строке не сказал бы человеку ничего.
    ///
    /// Живой случай, ради которого очередь вообще стала брать тело, назван
    /// у <see cref="Legs"/>.
    /// </summary>
    public static string BodyOwner(WorkItem job) => $"очередь: {job.Name}";

    /// <summary>
    /// НАСКОЛЬКО ВАЖНЫ СВОИ ШАГИ ОЧЕРЕДИ — и это ровно то, чем очередь и
    /// является: <see cref="BodyArbiter.Importance.Routine"/>, фоновое дело роли.
    ///
    /// НЕ <see cref="BodyArbiter.Importance.Command"/>, хотя соблазн велик:
    /// задачи в очередь ставит человек, и на первый взгляд это тот же приказ.
    /// Разница в том, ЖДЁТ ЛИ ЧЕЛОВЕК ОТВЕТА. Приказ из чата человек сказал
    /// СЕЙЧАС и стоит рядом; очередь он записал вечером и ушёл спать. Возьми
    /// очередь тело приказом — равный равного не перебивает, и живой человек на
    /// «!иди ко мне» получил бы «Сейчас занят: очередь. Повтори позже» от
    /// собственного вчерашнего списка дел. Слушаться живого человека немедленно
    /// дороже.
    ///
    /// ФОНОВОМУ ДЕЛУ ФОРЫ НЕТ (<see cref="BodyArbiter.SettleLeft"/>: холдер не
    /// выше Routine — выдержка ноль), и это здесь не мелочь, а условие работы:
    /// навык, взявший тело приказом изнутри задачи (карьер, дорога, наряд идут
    /// через <see cref="Resume"/> со ступенью Command), забирает его у нас В ТУ
    /// ЖЕ СЕКУНДУ, без ожидания. То есть держать тело ради навыка мы ему не
    /// мешаем ничем.
    /// </summary>
    public const BodyArbiter.Importance LegsImportance = BodyArbiter.Importance.Routine;

    /// <summary>
    /// ВЗЯТЬ ТЕЛО ПОД ОДИН ПОХОД БЕГУНА — и отдать, как только поход кончился.
    /// Владение возвращается вызывающему, и закрывает его он же (<c>using</c>).
    ///
    /// ПОЧЕМУ ЭТО ПОНАДОБИЛОСЬ. Бегун очереди был единственным в проекте, кто
    /// телом НЕ ВЛАДЕЛ ВОВСЕ: свои шаги (дорога к точке работы, поход к
    /// сундуку-источнику, рейс за материалом, разгрузка на склад) он делал под
    /// свободным телом. Пока у ходьбы был свой распорядитель, это сходило с
    /// рук; с тех пор как ходьба сведена к одному хозяину
    /// (<see cref="BodyArbiter.Era"/>), поход действителен ровно до первой
    /// смены хозяина тела — а у свободного тела ПЕРВЫЙ ЖЕ ЖЕЛАЮЩИЙ и есть эта
    /// смена.
    ///
    /// ЖИВОЙ СЛУЧАЙ, ДОСЛОВНО. Бот идёт к точке задачи, в сумке не хватает
    /// факела, «снарядиться» (<c>BehaviorEquipSelf</c>, ступень Routine) берёт
    /// свободное тело — поход гаснет, и через ДВЕ СЕКУНДЫ после старта задача
    /// объявлена проваленной словами «до точки работы (x,y,z) не дошёл за
    /// 300 с». То же от «доделать начатое» и «прогулки без дела».
    ///
    /// ЛЕЧИТСЯ ТЕМ ЖЕ, ЧЕМ У ВСЕХ ОСТАЛЬНЫХ: очередь берёт тело. Тогда равный
    /// («снарядиться», «доделать начатое», «прогулка») получает честный отказ и
    /// ноги не трогает, а тот, кто и вправду важнее — приказ человека, рефлекс,
    /// дело жизни, разделка, — перебивает нас мгновенно и по-прежнему.
    ///
    /// НА ПОХОД, А НЕ НА ЗАДАЧУ — И ЭТО ПЕРЕРЕШЕНО ПРИЁМКОЙ, а не переставлено
    /// по вкусу. Здесь стояло «берём один раз и держим до конца задачи», и
    /// цена этому вышла втрое:
    ///
    ///   1. ЗАДАЧА ХОРОНИЛАСЬ НАВСЕГДА. Владение бралось ОДНИМ ЗАХВАТОМ ДО
    ///      ПЕРВОГО ШАГА, и «тела не дали» означало «не вышла». Ночью тело
    ///      держит «сон: домой за кроватью», днём — «прогулка без дела»,
    ///      «снарядиться», «приветствие»; все они равные, все отказывают
    ///      честно — а <see cref="WorkPlan.Pending"/> берёт только ждущие, и
    ///      похороненную задачу поднимал бы человек руками;
    ///   2. БОТ НЕ ЛОЖИЛСЯ СПАТЬ ВСЮ ЗАДАЧУ. Сон — тоже фоновое дело роли
    ///      (<c>Sleeping.BodyLevel</c>), равный равного не перебивает, и всю
    ///      ночь в журнале шло «телом сейчас распоряжается „очередь: …“ —
    ///      подожду 30 с». Ночь кончается сама, а задача нет;
    ///   3. «ДОБЫЧА В ДЕСЯТИ ШАГАХ» ОТМЕНЯЛАСЬ. Предел похода за тушей
    ///      (<see cref="KillLootRules.HowFarToGo"/>) спрашивает, занято ли
    ///      тело: занято — предел равен ручке, свободно — вдвое больше. Пока
    ///      владение держалось всю задачу, «занято» было ВСЕГДА, и заказчик
    ///      снова читал «до тела 10,2 бл, а дальше 8 я за добычей не хожу»,
    ///      стоя рядом со своей же тушей.
    ///
    /// ЧТО ДЕЛАТЬ С НАВЫКАМИ, КОТОРЫЕ ТЕЛА НЕ БЕРУТ, — вопрос, из-за которого
    /// прошлая волна и оставила владение на всю задачу. Он настоящий: «сбор»,
    /// «разгрузка», «штольня», «разведка» и «снаряжение» тела НЕ БЕРУТ вовсе
    /// (SurvivorRole) и ходят токеном того, кто их позвал; чат прикрывает их
    /// своим владением на всю команду (<c>ChatCommands</c>, ступень приказа),
    /// а очередь прикрывала своим — и тем самым платила за них тремя бедами
    /// выше.
    ///
    /// РЕШЕНО: НЕ ПРИКРЫВАТЬ. Владение обязано быть у того, КТО ХОДИТ, — на
    /// этом стоит вся ходьба (<see cref="BodyArbiter.Era"/>) и весь караул
    /// «никто не ходит без тела». Одеяло очереди не чинило навыки, а прятало
    /// их долг: пока оно лежит, никто не заметит, что «сбор» ходит без тела, —
    /// и заметит только тогда, когда его позовут откуда-нибудь ещё. Долг
    /// назван вслух и записан в ОСТАЛОСЬ.md: навык, который ходит, обязан
    /// брать тело сам — так это делают карьер, дорога и наряд (через
    /// <see cref="Resume"/>, ступенью приказа), и им наше владение не нужно
    /// вовсе.
    ///
    /// ЧЕМ ПЛАТИМ ЗА ЭТО, ТОЖЕ СКАЗАНО ВСЛУХ. Между походами тело ничьё, и
    /// равный вправе его занять (щель разобрана у
    /// <see cref="BodyArbiter.ReserveLeft"/>; бронь стоит только за
    /// ПЕРЕБИТЫМ, а мы отпускаем сами). Тогда следующий шаг получит честный
    /// отказ — и задача останется ЖДУЩЕЙ, а не будет объявлена проваленной
    /// (<see cref="Postpone"/>). Это и есть разница: «не брали» и «не вышла» —
    /// разные вещи, и человек утром читает их по-разному.
    ///
    /// Вызывать только из хода одной задачи: вторую бегун не берёт
    /// (<see cref="RunAsync"/>, счётчик busy).
    /// </summary>
    /// <returns>Живое владение или null — тела не дали, и распорядитель сказал, кем занято.</returns>
    private BodyArbiter.Hold? Legs(WorkItem job, CancellationToken ct) =>
        bot.Context.Turn.TryTake(BodyOwner(job), LegsImportance, ct);

    /// <summary>
    /// КТО ЗАБРАЛ ТЕЛО — ОДНИМИ СЛОВАМИ НА ВСЕ ШАГИ БЕГУНА (пусто — никто не
    /// забирал, шаг не вышел сам по себе).
    ///
    /// Ровно этого не хватало живому случаю: тело забирали на второй секунде, а
    /// задача объявлялась проваленной словами «до точки работы (x,y,z) не дошёл
    /// за 300 с». Пять минут не проходило, дорога была ни при чём — а человек
    /// утром читал приговор дороге и шёл искать стену, которой нет. Имя
    /// перебившего распорядитель кладёт в само владение, спросить его есть у
    /// кого.
    ///
    /// Место одно на все шаги нарочно: шагов у бегуна четыре, и разойдись слова
    /// по ним — человек читал бы про одно и то же четырьмя разными фразами.
    /// </summary>
    private static string TakenBy(BodyArbiter.Hold legs) =>
        legs.Interrupted ? $"тело забрало «{legs.EndedBy}»" : "";

    /// <summary>
    /// Прогнать всю очередь по порядку: ждущие и включённые.
    /// Возвращает, сколько задач сделано.
    /// </summary>
    public async Task<int> RunAllAsync(CancellationToken ct = default)
    {
        int done = 0;
        Log($"беру очередь: {Plan}");

        while (!ct.IsCancellationRequested)
        {
            // Следующую спрашиваем КАЖДЫЙ РАЗ у очереди, а не берём список
            // один раз в начале: человек вправе передвинуть задачу вверх или
            // выключить её, пока идёт предыдущая, и послушаться надо сразу
            if (Plan.Next() is not { } job)
                break;

            Log($"{Plan.NumberOf(job.Id)}-я по очереди из {Plan.Jobs.Count}: «{job.Name}»" +
                (Plan.Next(job.Id) is { } after ? $"; следом «{after.Name}»" : "; она последняя"));

            var result = await RunAsync(job, ct);
            if (result.Ok)
                done++;

            // Задача осталась ждущей — значит её не взяли (бегун занят другой).
            // Крутиться на ней вечно нельзя, и молча уйти тоже нельзя
            if (job.State == WorkState.Waiting)
            {
                Log($"задача «{job.Name}» осталась ждущей ({result.Reason}) — очередь останавливаю");
                break;
            }
        }

        Log($"очередь пройдена: сделано {done}; {Plan}");
        return done;
    }

    /// <summary>
    /// Выполнить одну задачу. Состояние и итог пишутся в саму задачу, о каждой
    /// перемене сообщается через <see cref="OnChanged"/>.
    ///
    /// Если бегун занят другой задачей, эта НЕ помечается «не вышла»: её не
    /// брали, и врать про неё нечего — возвращается отказ с причиной.
    /// </summary>
    public async Task<WorkResult> RunAsync(WorkItem job, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            return new WorkResult(WorkState.Failed,
                $"занят задачей «{Current?.Name}» — эту не брал");

        try
        {
            // Задача, помеченная «выполняется», пока этот бегун свободен, — это
            // либо чужой бегун, либо след от выключения бота. Ни то ни другое
            // не повод копать один карьер вдвоём: след чинит Tidy при чтении
            // файла, а здесь честный отказ — и саму задачу мы НЕ трогаем, её
            // итог принадлежит тому, кто её ведёт
            if (WorkFlow.Why(job.State, WorkState.Running, "взял в работу") is { } no)
                return new WorkResult(WorkState.Failed, $"«{job.Name}»: {no}");

            return await RunOneAsync(job, ct);
        }
        finally
        {
            Current = null;
            Interlocked.Exchange(ref busy, 0);
        }
    }

    private async Task<WorkResult> RunOneAsync(WorkItem job, CancellationToken ct)
    {
        var prep = Prep(job);
        foreach (string note in prep.Notes)
            Log($"«{job.Name}»: {note}");

        job.Attempts++;
        if (!prep.Ok)
            return Finish(job, WorkState.Failed, prep.Problem);

        var kind = prep.Kind!;
        Current = job;
        job.State = WorkState.Running;
        job.Result = "";
        // С ЭТОЙ МИНУТЫ И СЧИТАЕМ, СКОЛЬКО ЗАДАЧА ЗАНЯЛА. Замер нужен не для
        // отчёта, а для следующего раза: без него планировать наперёд нечем
        ranFrom = DateTime.UtcNow;
        // …и с этой же минуты заход ничего ещё не сделал (разбор — у Postpone)
        doneAlready = "";
        Changed(job);
        Log($"«{job.Name}»: {kind.Title}, навык «{kind.SkillName}»" +
            (prep.Args.Length > 0 ? $" с аргументами «{prep.Args}»" : " без аргументов"));

        var tweaks = new WorkTweaks();
        using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts = link;
        var c = link.Token;

        try
        {
            // 1. Пороги под эту задачу
            var binding = Bindings.TryGetValue(kind.SkillName, out var b) ? b : null;
            if (binding?.Tune is { } tune)
                tune(bot.Context, job, prep.Seconds, tweaks);
            else if (prep.Seconds is { } s)
                Log($"«{job.Name}»: потолок {s / 60:0} мин приложить к навыку «{kind.SkillName}» " +
                    "некуда — такой связки у меня нет, работаю с обычным потолком механизма");
            if (tweaks.Changed.Count > 0)
                Log($"«{job.Name}»: на время задачи — {string.Join("; ", tweaks.Changed)}");

            // 2. ЧТО СДЕЛАТЬ ПЕРЕД ВЫХОДОМ. Ровно здесь бот доедает, пока
            // стоит у дома, вместо того чтобы бросить наряд на середине
            // дороги. Выключено, пока не включён револьвер
            await PlanBeforeLeavingAsync(job, prep, c);

            // 2. Постоянный склад роли запоминает сам склад — при подмене
            // (шаг 4). Держать здесь свой список было нельзя: роль доносит
            // настройки в механизмы на каждый поворот ручки в панели и
            // затирала подмену, о которой знал только бегун

            // 2в. НОГИ ЗДЕСЬ НЕ БЕРУТСЯ, И ЭТО ПЕРЕРЕШЕНО ПРИЁМКОЙ. Тут стоял
            // ОДИН захват тела на всю задачу, и он же был могилой: «тела не
            // дали» превращалось в «задача не вышла» — навсегда, потому что
            // повтор берёт только ждущие. Теперь тело берётся НА ПОХОД, каждым
            // шагом отдельно (полный разбор и цена — у Legs), а отказ
            // распорядителя оставляет задачу ждущей (Postpone)

            // 3. «брать» — материалы под эту задачу
            if (await TakeAsync(job, prep, c) is { } takeFailed)
                return takeFailed.NotStarted
                    ? Postpone(job, takeFailed.Reason)
                    : Finish(job, WorkState.Failed, takeFailed.Reason);
            if (c.IsCancellationRequested)
                return Finish(job, WorkState.Failed, "отменено до пуска навыка");

            // 4. «сдавать» — временное хранилище вместо постоянного склада
            if (prep.Deliver is { } deliver)
            {
                var seen = bot.Depot.UseTemporary(bot.WorldCell(deliver));
                TempStorage = deliver;
                if (!seen.WorthGoing)
                    return Finish(job, WorkState.Failed,
                        $"временное хранилище {deliver} не годится: {seen.Reason}");
                Log($"«{job.Name}»: сдавать буду во временное хранилище {deliver}");
            }

            // 5. «старт» — прийти туда, где работать
            if (prep.Start is { } start)
            {
                int above = binding?.StandAbove ?? 0;
                var cell = bot.WorldCell(start);
                var stand = above == 0 ? cell : new BlockPos(cell.X, cell.Y + above, cell.Z);
                Log($"«{job.Name}»: иду на {start}" +
                    (above > 0 ? $" (встаю на {above} клетку выше: навык берёт точку из-под ног)" : ""));
                // ИДЁМ НА СВОИХ НОГАХ И ПО ИХ ТОКЕНУ. Отнимут тело — поход
                // кончится сам, а не будет топать под чужим хозяином.
                // Владение живёт РОВНО ЭТОТ ПОХОД: дошли — отдали (using)
                using var ноги = Legs(job, c);
                if (ноги == null)
                    // ТЕЛА НЕ ДАЛИ — ЗАДАЧУ НЕ БРАЛИ, И ХОРОНИТЬ ЕЁ НЕЧЕГО
                    return Postpone(job,
                        $"до точки работы {start} не иду: тело занято «{bot.Context.Turn.Busy}»");
                if (!await bot.Movement.TravelToAsync(stand, TravelSeconds, ноги.Token))
                    return Finish(job, WorkState.Failed,
                        TakenBy(ноги) is { Length: > 0 } кто
                            ? $"до точки работы {start} не дошёл: {кто}"
                            : $"до точки работы {start} не дошёл за {TravelSeconds:0} с");
            }

            // 6. Собственно работа. Успех — только отсюда.
            //
            // ТОКЕН НАВЫКУ ДАЁМ СВОЙ, А НЕ ОТ ВЛАДЕНИЯ, И ЭТО НАРОЧНО: чьим
            // телом работать, навык решает сам и по своим правилам — карьер,
            // дорога и наряд идут через Resume и берут тело ступенью Command,
            // то есть законно отнимают его у нас (фоновому делу форы нет). Дай
            // мы навыку токен нашего владения — он умер бы ровно в тот миг,
            // когда сам же и взял тело.
            //
            // И ТЕЛА НА ВРЕМЯ НАВЫКА МЫ БОЛЬШЕ НЕ ДЕРЖИМ. Здесь было сказано,
            // что навык, тела не берущий (сбор, разгрузка, штольня, разведка,
            // снаряжение), идёт «под нашим владением» — то есть очередь
            // прикрывала своим именем чужую ходьбу. Это стоило трёх живых бед
            // разом (разбор и счёт — у Legs), а долг навыков не чинило: пока
            // одеяло лежит, никто и не увидит, что «сбор» ходит без тела.
            // Тело свободно — значит и сон ночью его получит, и предел похода
            // за тушей будет мерить работу, а не наше имя
            var result = await bot.Skills.RunAsync(kind.SkillName, prep.Args, c);
            string reason = Reason(result);

            // 6б. НЕ ХВАТИЛО МАТЕРИАЛА — ДОМОЙ ЗА НИМ И ОБРАТНО ДОДЕЛЫВАТЬ
            (result, reason) = await HomeForMaterialAsync(job, prep, kind, result, reason, c);

            // 7. «наСклад» — сперва вернуть постоянный склад, потом нести.
            // Рейс на склад — это минуты ходьбы, и ноги на него нужны свои же:
            // навык мог отнять у нас тело и уже отпустить
            if (prep.ToDepot)
            {
                RestoreDepot();
                using var ноги = Legs(job, c);
                if (ноги == null)
                    reason += $"; на постоянный склад не понёс: тело занято " +
                              $"«{bot.Context.Turn.Busy}»";
                else
                {
                    var haul = await bot.Depot.HaulAsync(ноги.Token);
                    reason += $"; на постоянный склад: {haul.Reason}";
                    // Отняли тело посреди рейса — говорим об этом отдельной
                    // половиной фразы, а не вместо слов склада: склад успел
                    // сделать часть работы, и врать, что он не сделал ничего,
                    // так же нечестно, как молчать о перехвате
                    if (TakenBy(ноги) is { Length: > 0 } кто)
                        reason += $" ({кто} — рейс не доведён)";
                }
            }

            return Finish(job, result.Success ? WorkState.Done : WorkState.Failed, reason);
        }
        catch (OperationCanceledException)
        {
            return Finish(job, WorkState.Failed, "отменено");
        }
        catch (Exception e)
        {
            return Finish(job, WorkState.Failed, $"ошибка: {e.Message}");
        }
        finally
        {
            // И на отмене, и на исключении: чужие пороги и подменённый склад,
            // оставшиеся после задачи, портят все следующие — молча.
            //
            // ТЕЛА ЗДЕСЬ БОЛЬШЕ НЕ ОТДАЮТ, и это не потеря: владение живёт
            // ровно один поход и закрывается его же using'ом — в том числе на
            // отмене и на исключении. Общего «отпустить в конце задачи» не
            // осталось потому, что не осталось и общего захвата
            RestoreDepot();
            int back = tweaks.RestoreAll();
            if (back > 0)
                Log($"«{job.Name}»: пороги вернул как было ({back})");
            cts = null;
        }
    }

    /// <summary>Итог навыка словами. Без причины навык не отпускаем даже на успехе.</summary>
    private static string Reason(SkillResult result) =>
        result.Message is { Length: > 0 } m ? m : (result.Success ? "готово" : "без причины");

    /// <summary>
    /// ШАГ 6б: НЕ ХВАТИЛО МАТЕРИАЛА — СБЕГАТЬ ДОМОЙ И ВЕРНУТЬСЯ ДОДЕЛЫВАТЬ.
    ///
    /// ЗАКАЗ (09.08, слова заказчика): «если не хватает чего для крафта и в
    /// задании не указано, что склад для крафта и добытых ресурсов в задании
    /// определенный, то можно бежать домой за ресурсами, чтобы вернуться и
    /// достроить — доп настройка». Живьём это дорога, вставшая на середине с
    /// «мостить нечем», когда камня дома два сундука.
    ///
    /// ВТОРОГО МЕХАНИЗМА ВОЗВРАТА ЗДЕСЬ НЕТ, и заводить его нельзя. «Вернуться
    /// доделывать» — это повторный запуск ТОГО ЖЕ навыка, а он внутри себя уже
    /// идёт через общий возврат (<see cref="Resume"/>) и сам продолжает с того
    /// места, где встал: дорога домащивает остаток рядов, наряд доносит
    /// недостачу. Здесь только устранение помехи между заходами.
    ///
    /// САМОГО РЕЙСА ЗДЕСЬ ТОЖЕ НЕТ. Он один на весь бот и живёт у склада
    /// (<see cref="Depot.SupplyAsync"/>): туда же ходит крафт, когда работа
    /// идёт вовсе без очереди задач. Здесь остаётся только то, чего склад про
    /// задачу не знает: разрешила ли рейс сама задача, свой ли у неё склад и
    /// сколько заходов уже было.
    ///
    /// Правила «стоит ли» и «сколько раз» — чистые и лежат в
    /// <see cref="WorkSupply"/>. Каждый отказ называется вслух: молчаливое «не
    /// вышла: мостить нечем» человек утром прочтёт как лень бота.
    /// </summary>
    private async Task<(SkillResult Result, string Reason)> HomeForMaterialAsync(
        WorkItem job, WorkPrep prep, WorkKind kind, SkillResult result, string reason,
        CancellationToken ct)
    {
        int runs = 0;
        bool allowed = prep.HomeForMaterial ?? RunHomeForMaterial;

        while (!result.Success && !ct.IsCancellationRequested)
        {
            var lacking = WorkSupply.Lacking(reason);
            if (!WorkSupply.Should(allowed, prep.OwnStorage, lacking.Count > 0, runs, MaxHomeRuns))
            {
                // Про «работа не назвала, чего не хватило» молчим НАРОЧНО: так
                // кончается любая неудача, не связанная с материалом вовсе (не
                // дошёл, лава, приват), и лишняя строка про склад там только
                // сбивает с толку
                if (lacking.Count > 0)
                    Log($"«{job.Name}»: " +
                        WorkSupply.WhyNot(allowed, prep.OwnStorage, true, runs, MaxHomeRuns));
                break;
            }

            runs++;
            Log($"«{job.Name}»: рейс за материалом {runs} из {MaxHomeRuns}");

            // Снабжение САМО перебирает звенья (сумки → склад этой работы →
            // дом → крафт → добыть рядом), САМО спрашивает опись, САМО решает
            // «не слишком ли далеко», САМО возвращает бота туда, откуда он ушёл
            // (Depot.ReturnToFace), и САМО считает принесённое по сумке, а не по
            // отправленным пакетам. Прежде здесь звался ОДИН склад — и «смотреть
            // дома недостающие» не делал никто
            // РЕЙС — ЭТО ПОХОД БЕГУНА, а не навыка, и ноги ему нужны свои: навык,
            // только что работавший, мог взять тело сам и уже отпустить
            // (разбор — у Legs). Владение живёт РОВНО РЕЙС и не переживает
            // возврат к навыку строкой ниже: навык ходит своим телом
            int принёс;
            using (var ноги = Legs(job, ct))
            {
                if (ноги == null)
                {
                    reason += $"; за материалом не пошёл: тело занято «{bot.Context.Turn.Busy}»";
                    break;
                }
                var рейс = await bot.Supply.GetAsync(lacking, allowed, ноги.Token);
                принёс = рейс.Brought;
                Log($"«{job.Name}»: {рейс.Reason}");

                if (рейс.Brought <= 0)
                {
                    // ЧЕСТНАЯ ПРИЧИНА ВМЕСТО ПРИГОВОРА ДОРОГЕ — теми же словами, что
                    // у остальных шагов бегуна (TakenBy). Без неё перехват посреди
                    // рейса читался бы как «до дома за stone не дойти: …», и человек
                    // шёл бы утром искать стену на дороге к собственному дому —
                    // ровно та ложь, ради которой TakenBy и заведён. Шаг у бегуна
                    // четвёртый, а этих слов у него одного и не было
                    reason += $"; {рейс.Reason}" +
                              (TakenBy(ноги) is { Length: > 0 } кто ? $" ({кто} — рейс не доведён)" : "");
                    break;
                }
            }
            if (ct.IsCancellationRequested)
            {
                reason += $"; принёс со склада {принёс} шт, но задачу отменили — не доделал";
                break;
            }

            Log($"«{job.Name}»: возвращаюсь доделывать " +
                $"(навык «{kind.SkillName}» продолжит с того места, где встал)");
            result = await bot.Skills.RunAsync(kind.SkillName, prep.Args, ct);
            reason = Reason(result);
        }

        return (result, runs > 0
            ? $"{reason}; за материалом домой бегал {Resume.Times(runs)}"
            : reason);
    }

    /// <summary>Шаг «брать». null — всё в порядке (в том числе «шаг пропущен»).</summary>
    private async Task<StepFail?> TakeAsync(WorkItem job, WorkPrep prep, CancellationToken ct)
    {
        if (prep.Take is not { } take || prep.TakeWhat.Count == 0)
            return null;   // про пропуск уже сказано в WorkPrep.Notes

        var cell = bot.WorldCell(take);
        Log($"«{job.Name}»: иду к сундуку-источнику {take} за: {string.Join(", ", prep.TakeWhat)}");

        // И СЮДА — НА СВОИХ НОГАХ: поход к сундуку-источнику ничем не отличается
        // от дороги к точке работы, и отнимал его у бегуна ровно тот же первый
        // желающий (разбор — у Legs). Владение живёт весь шаг: и дорогу, и
        // осмотр, и выемку — сундук открыт нашим же телом
        using var ноги = Legs(job, ct);
        if (ноги == null)
            // ТЕЛА НЕ ДАЛИ — ЗАДАЧУ НЕ БРАЛИ, и хоронить её нечего (см. Postpone)
            return new StepFail(
                $"к сундуку-источнику {take} не иду: тело занято «{bot.Context.Turn.Busy}»",
                NotStarted: true);
        var token = ноги.Token;

        // Дальняя дорога может не довести ровно В клетку сундука (он твёрдый) —
        // это не беда: доводить вплотную умеет сам осмотр контейнера
        if (!await bot.Movement.TravelToAsync(cell, TravelSeconds, token))
            Log($"«{job.Name}»: маршрутом до {take} не дошёл" +
                (TakenBy(ноги) is { Length: > 0 } кто ? $" — {кто}" : "") +
                " — пробую дотянуться оттуда, где встал");

        // Осмотр открывает сундук: без открытия сервер не присылает
        // содержимое, и «взять» честно нечего было бы искать
        if (await bot.Read.InspectAsync(cell, token) is not { } inside)
            // А ВОТ ЭТО УЖЕ НАСТОЯЩАЯ НЕУДАЧА, а не «не брали»: тело нам дали,
            // поход состоялся, и не вышло именно дело
            return new StepFail(
                $"сундук-источник {take} не открылся: не подойти или приват" +
                (TakenBy(ноги) is { Length: > 0 } чей ? $" ({чей})" : ""),
                NotStarted: false);

        var chest = Container.Chest(bot.Context, bot.Context.Hands, cell);
        chest.OnLog += Log;
        int stacks = await chest.TakeAllAsync(WorkMasks.Wanted(prep.TakeWhat), token);
        chest.OnLog -= Log;

        if (stacks == 0)
            Log($"«{job.Name}»: в сундуке {take} по маскам ({string.Join(", ", prep.TakeWhat)}) " +
                $"ничего не нашлось; там сейчас: {inside.Describe()}");
        else
            Log($"«{job.Name}»: взял стопок {stacks}");
        // ЭТО УЖЕ СЛУЧИЛОСЬ С МИРОМ, И ЗАБЫТЬ ОБ ЭТОМ НЕЛЬЗЯ: сундук опустошён,
        // стопки в сумке, и следующий заход найдёт там пусто. Зачем это
        // помнить — у Postpone
        doneAlready = TookFromChest(take, stacks);
        return null;
    }

    /// <summary>
    /// ЧТО ЗАХОД СДЕЛАЛ С МИРОМ НА ШАГЕ «БРАТЬ» (пусто — ничего не сделал, и
    /// врать про сделанное нечего).
    ///
    /// Вынутая стопка — это ПЕРЕМЕНА В МИРЕ, а не пометка у бегуна: сундук
    /// опустел, груз в сумке, и повтор задачи найдёт там пусто. Слова про это
    /// нужны человеку целиком — и клетка сундука (по ней он его и найдёт), и
    /// число стопок, и главное: что второй раз там брать нечего.
    /// </summary>
    /// <param name="take">Клетка сундука-источника (игровые числа — их читает человек).</param>
    /// <param name="stacks">Сколько стопок реально переехало в сумку.</param>
    public static string TookFromChest(GamePos take, int stacks) =>
        stacks <= 0
            ? ""
            : $"из сундука-источника {take} взято стопок {stacks} — они уже в сумке, " +
              "и второй раз там будет пусто";

    /// <summary>
    /// Вернуть постоянный склад роли на место. Повторный вызов ничего не делает.
    ///
    /// Что именно вернуть, помнит сам склад (<see cref="Depot.DropTemporary"/>):
    /// подменой и возвратом обязано ведать одно место, иначе постоянный склад
    /// живёт в двух списках и они расходятся на первом же повороте ручки.
    /// </summary>
    private void RestoreDepot()
    {
        if (TempStorage == null)
            return;
        TempStorage = null;
        bot.Depot.DropTemporary();
    }

    /// <summary>
    /// ЧЕМ КОНЧИЛСЯ ШАГ БЕГУНА — и, главное, НАЧАЛСЯ ЛИ ОН ВООБЩЕ.
    ///
    /// Одной строкой отказа здесь не обойтись, и разница не в словах, а в
    /// судьбе задачи: «сундук не открылся» — это неудача (тело нам дали, поход
    /// состоялся, не вышло дело), а «тела не дали» — это НЕ НАЧИНАЛОСЬ, и
    /// хоронить задачу за это нельзя (разбор — у <see cref="Postpone"/>).
    /// </summary>
    /// <param name="Reason">Причина словами — её читает человек.</param>
    /// <param name="NotStarted">Шаг не начинался: тела не дали.</param>
    private readonly record struct StepFail(string Reason, bool NotStarted);

    /// <summary>Когда начался нынешний заход. null — задача ещё не пускалась.</summary>
    private DateTime? ranFrom;

    /// <summary>
    /// ТЕЛА НЕ ДАЛИ — ЗАДАЧА ОСТАЁТСЯ ЖДУЩЕЙ, А НЕ ХОРОНИТСЯ.
    ///
    /// ЖИВАЯ БЕДА, РАДИ КОТОРОЙ ЭТО ЗАВЕДЕНО. Отказ распорядителя тела
    /// записывался как «не вышла»: <c>Legs(...) == null → Finish(Failed)</c>.
    /// А отказывает распорядитель РОВНО ТОГДА, когда тело держит равный, — то
    /// есть ночью «сон: домой за кроватью», днём «прогулка без дела»,
    /// «снарядиться», «приветствие». Все они отпустят тело через минуту, а
    /// задача была уже похоронена: повтор берёт только ждущие
    /// (<see cref="WorkPlan.Pending"/>), <see cref="WorkItem.Attempts"/> не
    /// читает никто, и поднимал бы её человек руками.
    ///
    /// ЭТО НЕ НОВОЕ ПРАВИЛО, А ТО ЖЕ САМОЕ, ЧТО ДВУМЯ ЭКРАНАМИ ВЫШЕ: бегун,
    /// занятый другой задачей, отвечает отказом и САМУ ЗАДАЧУ НЕ ТРОГАЕТ — «её
    /// не брали, и врать про неё нечего» (<see cref="RunAsync"/>). Разница
    /// была лишь в том, что там про задачу не врали, а здесь врали.
    ///
    /// КРУТИТЬСЯ НА НЕЙ ВЕЧНО ОЧЕРЕДЬ НЕ СТАНЕТ: ждущая задача останавливает
    /// круг (<see cref="RunAllAsync"/>), и следующий круг начнётся, когда бот
    /// снова окажется не у дел — то есть когда тело освободится.
    ///
    /// «НЕ БРАЛИ» ПРАВДА НЕ ВСЕГДА, И ВОТ ГДЕ ОНА ВРАЛА. Тело берётся НА ПОХОД,
    /// и между походами оно ничьё: шаг «брать» может УЖЕ ПРОЙТИ (сундук-источник
    /// опустошён в сумку), а следующий шаг — дорогу к точке работы — тут же не
    /// получить тела. Задача тогда оставалась ждущей со словами «не иду: тело
    /// занято», и человек читал их как «заход ничего не сделал». А заход сделал:
    /// на следующем круге бот шёл к тому же сундуку и печатал «ничего не
    /// нашлось» — строку, по которой не догадаться, что материал давно при нём.
    ///
    /// Поэтому отложенная задача НАЗЫВАЕТ УЖЕ СДЕЛАННОЕ (<see cref="doneAlready"/>).
    /// Состояние при этом остаётся ждущим — работа не сделана, и браться за неё
    /// надо снова; врать было в словах, а не в судьбе задачи.
    /// </summary>
    private WorkResult Postpone(WorkItem job, string reason) =>
        Finish(job, WorkState.Waiting, PostponeWords(reason, doneAlready));

    /// <summary>
    /// СЛОВА ОТЛОЖЕННОЙ ЗАДАЧИ: «не брали» — ПРАВДА ТОЛЬКО ДЛЯ ПУСТОГО ЗАХОДА.
    /// Заход, который уже что-то сделал с миром, обязан это назвать — иначе
    /// человек читает «ждёт» как «ничего не случилось» (разбор — у
    /// <see cref="Postpone"/>).
    /// </summary>
    /// <param name="reason">Почему отложили — теми же словами, что и всегда.</param>
    /// <param name="doneAlready">Что заход уже успел (пусто — ничего).</param>
    public static string PostponeWords(string reason, string doneAlready) =>
        doneAlready is { Length: > 0 } было ? $"{reason}; но заход не пустой: {было}" : reason;

    /// <summary>
    /// ЧТО ЭТОТ ЗАХОД УЖЕ СДЕЛАЛ С МИРОМ (пусто — ничего). Пишется теми шагами,
    /// которые мир МЕНЯЮТ (<see cref="TookFromChest"/>), и читается отложенной
    /// задачей (<see cref="Postpone"/>).
    /// </summary>
    private string doneAlready = "";

    private WorkResult Finish(WorkItem job, WorkState state, string reason)
    {
        // СКОЛЬКО ЗАНЯЛО — ФАКТ, а не оценка, и записывается он ВСЕГДА: и на
        // удаче, и на отказе. Заход, оборванный через десять секунд, — тоже
        // правда о задаче, и завтрашнему планированию она нужна не меньше.
        // Не пускавшаяся задача (отказ разбора) замера не получает: там
        // мерить нечего
        if (ranFrom is { } began)
        {
            job.Took = Math.Max(0, (DateTime.UtcNow - began).TotalSeconds);
            // ДОШЁЛ ЛИ ЗАХОД ДО КОНЦА — записываем ВМЕСТЕ с замером и тем же
            // движением. Без этой отметки замер оборванного захода неотличим от
            // замера сделанной работы, и двухсекундный отказ выдавал себя за
            // срок карьера на семьсот двадцать клеток (см. WorkItem.TookDone)
            job.TookDone = state == WorkState.Done;
            ranFrom = null;
        }

        // Итог записываем ВСЕГДА — он пришёл от дела и терять его нельзя. Но
        // если переход не по правилам, это наша нескладица, и молчать о ней
        // тоже нельзя: человек читает состояние как правду о ночи
        if (WorkFlow.Why(job.State, state, reason) is { } no)
            Log($"«{job.Name}»: итог «{WorkStates.Word(state)}» после «{WorkStates.Word(job.State)}» " +
                $"— так быть не должно ({no}); итог всё равно записываю: {reason}");

        job.State = state;
        job.Result = reason;
        Changed(job);
        Log($"«{job.Name}»: {WorkStates.Word(state)} — {reason}");
        return new WorkResult(state, reason);
    }

    private void Changed(WorkItem job) => OnChanged?.Invoke(job);

    private void Log(string message) => OnLog?.Invoke(message);
}
