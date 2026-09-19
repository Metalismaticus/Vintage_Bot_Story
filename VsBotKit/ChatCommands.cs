namespace VsBotKit;

/// <summary>Разобранная команда из чата — то, что получает обработчик.</summary>
public sealed record ChatCommandArgs
{
    public required VsBot Bot { get; init; }

    /// <summary>Имя игрока, написавшего команду.</summary>
    public required string Sender { get; init; }

    /// <summary>Имя команды без префикса.</summary>
    public required string Command { get; init; }

    /// <summary>Всё, что написано после команды.</summary>
    public required string Text { get; init; }

    /// <summary>Чат-группа, в которую пришла команда (туда же уходит ответ).</summary>
    public required int GroupId { get; init; }

    /// <summary>
    /// КОМАНДУ И ПРАВДА НАПИСАЛ ЖИВОЙ ЧЕЛОВЕК В ИГРОВОЙ ЧАТ. Ложь — её позвали
    /// мимо чата: окно управления, внешний клиент, консоль хозяина
    /// (<see cref="ChatCommands.RunAsync"/>).
    ///
    /// ЗАЧЕМ РАЗЛИЧАТЬ. От этого зависит, ЧЕМ считать ответ команды. Написал
    /// человек «!дорога …» — всё, что бот скажет, есть ответ ему, и глушить
    /// такое нельзя ни при какой настройке. А наряд, отданный из вебформы,
    /// человека в чате не спрашивал вовсе: «Мощу дорогу. „!стоп“ — прервать» и
    /// отчёт о наряде — это бот рассказывает о СВОИХ делах посторонним. Именно
    /// эти строки заказчик и видел в чате 16.08, отдав наряд из окна.
    /// </summary>
    public bool FromChat { get; init; }

    /// <summary>
    /// Отмена для долгих команд. Гаснет, когда тело забрал кто-то важнее —
    /// например бой или побег. Долгая команда ОБЯЗАНА передавать этот токен
    /// внутрь: иначе она продолжит работать телом, которое уже не её,
    /// и будет честно докладывать «не дотянуться» из другого конца поля.
    /// </summary>
    public CancellationToken Token { get; init; } = default;

    /// <summary>Аргументы, разбитые по пробелам.</summary>
    public string[] Words => Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Ответить в тот же чат (с соблюдением лимита сервера).
    ///
    /// ПОВОД У ОТВЕТА РАЗНЫЙ, и в этом вся разница для радиотишины: пришла
    /// команда из чата — это ОТВЕТ живому человеку и он уходит всегда; позвали
    /// её из окна управления — в чат при радиотишине она не идёт, а ложится в
    /// журнал хозяина (см. <see cref="FromChat"/> и <see cref="BotVoice"/>).
    ///
    /// ОТВЕТ ИЗ ОКНА — ЭТО «ОтветМимоЧата», А НЕ «Отчёт», И ЭТО НЕ ПЕРЕИМЕНОВАНИЕ.
    /// Поводом «Отчёт» здесь стояла ложь: длинный ответ уходил в чат обрезанным
    /// с хвостом «целиком — в журнале», а в журнал целое НЕ писалось — повод
    /// «Отчёт» нарочно его не пишет, считая, что работа написала сама. Работы
    /// тут нет, есть команда, и кроме неё в журнал не пишет никто. Улика:
    /// out/разведка-лук4, ответ «!рядом water 48» потерян наполовину.
    /// </summary>
    public Task ReplyAsync(string message) =>
        Bot.SayAsync(message, GroupId,
            FromChat ? SayReason.Ответ : SayReason.ОтветМимоЧата);

    /// <summary>Сущность написавшего игрока, если бот его видит.</summary>
    public EntityInfo? SenderEntity => Bot.Entities.All
        .FirstOrDefault(e => e.IsPlayer && e.PlayerName == Sender);

    /// <summary>
    /// Пара координат X и Z в той системе, которую игрок видит в игре
    /// (отсчёт от точки спавна мира). Точка и запятая равнозначны, независимо
    /// от языка системы (см. <see cref="TryParseCoord(string,out double)"/>).
    /// Абсолютные мировые координаты можно написать с префиксом «=», как в
    /// команде /tp.
    /// РОВНО два числа: если написали три, молча выкидывать высоту нельзя —
    /// бот уйдёт совсем не туда. Для трёх есть TryGetCell.
    /// </summary>
    public bool TryGetCoords(out double x, out double z, int firstWord = 0)
    {
        x = z = 0;
        var w = Words;
        if (w.Length != firstWord + 2 ||
            !TryParseCoord(w[firstWord], out x, out bool absX) ||
            !TryParseCoord(w[firstWord + 1], out z, out bool absZ))
            return false;
        // Складывает WorldOrigin, и сдвиг берётся у своего клиента
        // (VsBot.CoordOffset) — КЛЕТКОЙ спавна, а не присланной дробью. Здесь
        // стояло «x += spawn.X», то есть прибавлялось 512000,5: полблока мимо
        // у пары «иди туда-то» и целый блок мимо после округления вниз
        (x, _, z) = WorldOrigin.ToWorld(x, 0, z, OffsetFor(absX, absZ));
        return true;
    }

    /// <summary>
    /// СДВИГ ДЛЯ ЭТИХ ДВУХ ЧИСЕЛ. Префикс «=» означает «число уже мировое», и
    /// такой оси сдвиг не полагается — поэтому он считается по осям, а не
    /// целиком. Своего отсечения дроби здесь нет: клетку спавна знает
    /// <see cref="VsBot.CoordOffset"/>, и второй такой записи в проекте быть
    /// не должно.
    /// </summary>
    private (int X, int Z) OffsetFor(bool absX, bool absZ)
    {
        var о = Bot.CoordOffset;
        return (absX ? 0 : о.X, absZ ? 0 : о.Z);
    }

    /// <summary>
    /// Три координаты как клетка мира (x y z) — так их показывает игра,
    /// то есть относительно точки спавна. С префиксом «=» — абсолютные.
    /// </summary>
    public bool TryGetCell(out BlockPos cell, int firstWord = 0)
    {
        cell = new BlockPos(0, 0, 0);
        var w = Words;
        if (w.Length != firstWord + 3 ||
            !TryParseCoord(w[firstWord], out double x, out bool absX) ||
            !TryParseCoord(w[firstWord + 1], out double y, out bool absY) ||
            !TryParseCoord(w[firstWord + 2], out double z, out bool absZ))
            return false;
        // Высота у игры абсолютная (HudElementCoordinates), сдвигаем только X и Z
        _ = absY;
        // СНАЧАЛА КЛЕТКА, ПОТОМ СДВИГ, а не наоборот. Обратный порядок и был
        // бедой: floor(31,9 + 512000,5) даёт 512032, а floor(31,9) + 512000 —
        // 512031. Целый блок мимо на любом числе с дробью не меньше половины
        var (wx, wy, wz) = WorldOrigin.ToWorld(
            (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z),
            OffsetFor(absX, absZ));
        cell = new BlockPos(wx, wy, wz);
        return true;
    }

    /// <summary>
    /// Позиция в том виде, в каком её видит игрок — этим и отвечаем в чат,
    /// иначе бот и человек говорят на разных языках.
    /// </summary>
    public string Here(double x, double y, double z) => Bot.PlayerCoords(x, y, z);

    /// <summary>Клетка мира в координатах игрока.</summary>
    public string Here(BlockPos p) => Bot.PlayerCoords(p.X, p.Y, p.Z);

    /// <summary>
    /// Три координаты в начале команды, даже если дальше есть ещё слова
    /// (например «!клетка 31 108 29 лестница»).
    /// </summary>
    public bool TryGetCellPrefix(out BlockPos cell)
    {
        cell = new BlockPos(0, 0, 0);
        var w = Words;
        if (w.Length < 3 ||
            !TryParseCoord(w[0], out double x, out bool absX) ||
            !TryParseCoord(w[1], out double y, out _) ||
            !TryParseCoord(w[2], out double z, out bool absZ))
            return false;
        // Тот же порядок, что и в TryGetCell: клетка, потом сдвиг
        var (wx, wy, wz) = WorldOrigin.ToWorld(
            (int)Math.Floor(x), (int)Math.Floor(y), (int)Math.Floor(z),
            OffsetFor(absX, absZ));
        cell = new BlockPos(wx, wy, wz);
        return true;
    }

    /// <summary>Число координаты; «=» впереди означает абсолютную мировую.</summary>
    public static bool TryParseCoord(string s, out double value, out bool absolute)
    {
        absolute = s.StartsWith('=');
        return TryParseCoord(absolute ? s[1..] : s, out value);
    }

    public static bool TryParseCoord(string s, out double value) =>
        double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// Команды чата: вместо ручного разбора строк — регистрация обработчиков.
/// <code>bot.Commands.Add("прайс", "показать цены", a => a.ReplyAsync(...));</code>
/// Сам разбирает «Игрок: !команда аргументы», отсеивает эхо бота и служебные
/// сообщения сервера.
/// </summary>
public sealed class ChatCommands
{
    private readonly VsBot bot;
    private readonly Dictionary<string,
        (string Description, Func<ChatCommandArgs, Task> Handler, bool NeedsBody)> commands =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Символ-префикс команд.</summary>
    public char Prefix { get; set; } = '!';

    /// <summary>Отвечать ли на неизвестные команды подсказкой.</summary>
    public bool ReplyToUnknown { get; set; } = true;

    /// <summary>Имя встроенной команды со списком (пусто — не регистрировать).</summary>
    public string HelpCommand { get; set; } = "помощь";

    /// <summary>
    /// СЛУШАТЬ ЛИ ПРИКАЗЫ ИЗ ИГРОВОГО ЧАТА. Выключено — командовать ботом
    /// может только хозяин из окна управления.
    ///
    /// Зачем понадобилось. На общем сервере командовать чужим ботом мог кто
    /// угодно: «!иди 0 0» уводит торговца из лавки, «!стоп» бросает наряд на
    /// полудороге. Ограничивать по одной команде бессмысленно — вредна сама
    /// возможность, поэтому выключатель один и закрывает все.
    ///
    /// КОГО ЭТО НЕ КАСАЕТСЯ — трое, и у каждого своя причина.
    ///
    /// 1. «!помощь». Это не управление, а вопрос: бот ничего не делает, только
    ///    рассказывает о себе. Человеку, подошедшему к незнакомому боту, надо
    ///    дать возможность узнать, что тут вообще происходит, — и список честно
    ///    добавляет, что командовать из чата сейчас нельзя.
    /// 2. Окно управления и внешний клиент. Они зовут <see cref="RunAsync"/>
    ///    напрямую, минуя разбор чата, и проверки здесь нет НАРОЧНО: выключатель
    ///    закрывает чужих, а не хозяина. Запри его тем же флагом — и человек,
    ///    закрывший чат, потерял бы управление собственным ботом.
    /// 3. Разговор вообще. Сюда попадают только строки, начинающиеся с
    ///    <see cref="Prefix"/>. Ответ на своё имя, приветствие, болтовня живут
    ///    в роли через VsBot.OnChat и этим выключателем не затрагиваются:
    ///    закрыто УПРАВЛЕНИЕ, а не рот.
    ///
    /// РАДИОТИШИНЫ ЭТА РУЧКА НЕ ДАЁТ — прямой вопрос заказчика 16.08, и ответ
    /// на него «нет». Она стоит на разборе ВХОДЯЩЕЙ строки и про рот бота не
    /// знает ничего: с выключенным чатом бот всё так же писал «Мощу дорогу…» и
    /// отчёт о наряде, отданном из окна управления. За молчание отвечает другая
    /// ручка — <c>LivingRole.ПисатьВЧатОСвоихДелах</c>, и работает она через
    /// единственную дверь в чат (<see cref="BotVoice"/>). Две ручки, потому что
    /// и дела разные: одна закрывает ЧУЖИЕ ПРИКАЗЫ, другая — СВОИ СЛОВА.
    /// </summary>
    public bool AcceptFromChat { get; set; } = true;

    /// <summary>
    /// Что ответить тому, кто всё-таки написал команду. Молчать нельзя: для
    /// человека молчащий бот — сломанный бот, и он будет писать ещё и ещё,
    /// а потом пойдёт жаловаться хозяину.
    /// </summary>
    public string ClosedReply { get; set; } =
        "Управление из чата закрыто хозяином — команды я отсюда не принимаю";

    /// <summary>
    /// Сколько секунд не повторять отказ ОДНОМУ И ТОМУ ЖЕ человеку. Ответить
    /// надо, но на десять «!иди» подряд десять одинаковых строк — это спам от
    /// имени бота, за который на сервере молча выдают мьют.
    /// </summary>
    public double ClosedReplyQuietSeconds { get; set; } = 20;

    /// <summary>
    /// Кто-то пробовал командовать ботом из чата при закрытом управлении
    /// (имя игрока, команда). Хозяин должен видеть это в журнале, даже когда
    /// самому игроку бот уже ответил и замолчал.
    /// </summary>
    public event Action<string, string>? OnChatControlRefused;

    /// <summary>Ошибка внутри обработчика команды.</summary>
    public event Action<string, Exception>? OnCommandError;

    /// <summary>Сколько ждать освобождения тела, прежде чем отказать.</summary>
    public double WaitForBodySeconds { get; set; } = 8;

    internal ChatCommands(VsBot bot) => this.bot = bot;

    /// <param name="needsBody">
    /// Команде нужно ТЕЛО — она будет ходить, копать, класть. Такая ждёт своей
    /// очереди у распорядителя и получает отказ «сейчас занят».
    ///
    /// false — команда телом не двигает, и просить его нельзя. Живой случай:
    /// «!стоп» во время карьера НЕ ДОХОДИЛ до обработчика вовсе. Он брал тело
    /// важностью «команда», а тело уже держала «команда карьер» — равный
    /// равного не перебивает, и человек получал «Сейчас занят: команда карьер.
    /// Повтори позже» вместо остановки. То есть остановить бота из чата было
    /// нельзя ровно тогда, когда это и нужно.
    /// </param>
    public void Add(string name, string description, Func<ChatCommandArgs, Task> handler,
        bool needsBody = true) =>
        commands[name] = (description, handler, needsBody);

    public void Add(string name, string description, Action<ChatCommandArgs> handler,
        bool needsBody = true) =>
        commands[name] = (description, a => { handler(a); return Task.CompletedTask; }, needsBody);

    public bool Remove(string name) => commands.Remove(name);

    public bool Has(string name) => commands.ContainsKey(name);

    public IEnumerable<(string Name, string Description)> All =>
        commands.Select(kv => (kv.Key, kv.Value.Description));

    /// <summary>Разбор входящего сообщения чата и вызов обработчика.</summary>
    internal void Handle(ChatMessage line)
    {
        string text = line.PlainText;
        int sep = text.IndexOf(": ", StringComparison.Ordinal);
        string sender = sep >= 0 ? text[..sep] : "";
        string body = (sep >= 0 ? text[(sep + 2)..] : text).Trim();

        if (sender.Length == 0 || sender == bot.Name)
            return; // служебное сообщение сервера или собственное эхо
        if (body.Length < 2 || body[0] != Prefix)
            return;

        int space = body.IndexOf(' ');
        string name = space < 0 ? body[1..] : body[1..space];
        string args = space < 0 ? "" : body[(space + 1)..].Trim();

        var callArgs = new ChatCommandArgs
        {
            Bot = bot,
            Sender = sender,
            Command = name,
            Text = args,
            GroupId = line.GroupId,
            // Единственное место, где команду ВПРАВДУ написал человек в чате:
            // отсюда и только отсюда ответ считается ответом ему (см. FromChat)
            FromChat = true
        };

        if (HelpCommand.Length > 0 && name.Equals(HelpCommand, StringComparison.OrdinalIgnoreCase))
        {
            _ = callArgs.ReplyAsync(BuildHelp());
            return;
        }

        // ВЫКЛЮЧАТЕЛЬ УПРАВЛЕНИЯ ИЗ ЧАТА — здесь, а не в RunAsync: см. разбор
        // у AcceptFromChat. Окну управления команда пройдёт и при закрытом чате
        if (!Allowed(AcceptFromChat, name, HelpCommand))
        {
            OnChatControlRefused?.Invoke(sender, name);
            if (SayRefusal(sender, DateTime.UtcNow, ClosedReplyQuietSeconds))
                _ = callArgs.ReplyAsync(ClosedReply);
            return;
        }

        // Обработчик может быть долгим (сходить, принести) — не блокируем приём
        // пакетов. «Спросил человек в чате» передаём дальше НАРОЧНО: RunAsync
        // собирает свой ChatCommandArgs, и без этого ответ на живой вопрос
        // считался бы рассказом бота о своих делах — то есть при радиотишине
        // человек в чате получал бы молчание
        _ = RunAsync(sender, name, args, line.GroupId, fromChat: true);
    }

    /// <summary>
    /// Пропустить ли команду из чата. Правило отдельной функцией и под тестом:
    /// на нём держится вся защита от чужих приказов, а проверять его живьём —
    /// значит просить постороннего человека покомандовать чужим ботом.
    /// </summary>
    public static bool Allowed(bool acceptFromChat, string command, string helpCommand) =>
        acceptFromChat ||
        (helpCommand.Length > 0 && command.Equals(helpCommand, StringComparison.OrdinalIgnoreCase));

    /// <summary>Когда этому человеку в последний раз сказали, что чат закрыт.</summary>
    private readonly Dictionary<string, DateTime> refusedAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Отвечать ли ЭТОМУ человеку про закрытый чат прямо сейчас. Счёт для
    /// каждого свой: молчание в ответ подошедшему второму игроку — это ровно
    /// та тишина, из-за которой бота считают сломанным.
    /// </summary>
    private bool SayRefusal(string sender, DateTime now, double quietSeconds)
    {
        lock (refusedAt)
        {
            if (refusedAt.TryGetValue(sender, out var last) &&
                (now - last).TotalSeconds < quietSeconds)
                return false;
            // Чистим по дороге: на людном сервере имён набегает сколько угодно,
            // а помнить надо только тех, кто писал только что
            if (refusedAt.Count > 64)
                foreach (string stale in refusedAt
                             .Where(kv => (now - kv.Value).TotalSeconds >= quietSeconds)
                             .Select(kv => kv.Key).ToList())
                    refusedAt.Remove(stale);
            refusedAt[sender] = now;
            return true;
        }
    }

    /// <summary>
    /// Выполнить команду, минуя разбор строки чата: так её зовут окно
    /// управления и внешний клиент.
    ///
    /// ПОЧЕМУ ЧЕРЕЗ ОБЩИЙ ВХОД, А НЕ НАПРЯМУЮ. Именно здесь команда занимает
    /// тело у распорядителя: пока она идёт, роль своими делами бота не дёргает,
    /// а бой и побег её перебивают. Панель, зовущая обработчик напрямую,
    /// подралась бы с рефлексами выживания за одного и того же бота — и оба
    /// тянули бы его в разные стороны.
    ///
    /// Возвращает короткий итог для того, кто позвал. Ответы самой команды
    /// как шли в игровой чат, так и идут: команда одна и та же.
    /// </summary>
    /// <param name="fromChat">
    /// Команду написал ЖИВОЙ ЧЕЛОВЕК В ЧАТЕ. Заводское «нет» — так её зовут
    /// окно управления, внешний клиент и консоль хозяина, и тогда всё, что
    /// команда скажет, есть рассказ бота о своих делах, а не ответ кому-то
    /// (см. <see cref="ChatCommandArgs.FromChat"/> и <see cref="BotVoice"/>).
    /// Разбор чата передаёт сюда «да» — иначе при радиотишине человек, честно
    /// написавший «!помощь», получил бы молчание.
    /// </param>
    public async Task<string> RunAsync(string sender, string name, string args, int groupId = 0,
        bool fromChat = false)
    {
        var callArgs = new ChatCommandArgs
        {
            Bot = bot,
            Sender = sender,
            Command = name,
            Text = args,
            GroupId = groupId,
            FromChat = fromChat
        };

        if (HelpCommand.Length > 0 && name.Equals(HelpCommand, StringComparison.OrdinalIgnoreCase))
            return BuildHelp();

        if (!commands.TryGetValue(name, out var command))
        {
            // ПОДСКАЗЫВАЕМ, НО НЕ УГАДЫВАЕМ. Живой случай: заказчик написал
            // «!разгружайся», получил голое «Не знаю команду» и пошёл искать
            // нужное слово руками — при том что бот его знал. Выполнить похожую
            // команду молча нельзя (сегодня это «разгрузка», а завтра — «сломай
            // дом» вместо «сложи дом»), а СПРОСИТЬ обязаны
            string guess = Nearest(name, commands.Keys) is { } near
                ? $" Может быть, {Prefix}{near}? Скажите её — выполню."
                : "";
            string complaint = $"Не знаю команду \"{name}\".{guess} " +
                               $"{Prefix}{HelpCommand} — список команд";
            if (ReplyToUnknown)
                _ = callArgs.ReplyAsync(complaint);
            return complaint;
        }

        // КОМАНДА, КОТОРАЯ ТЕЛОМ НЕ ДВИГАЕТ, ТЕЛА И НЕ ПРОСИТ. Иначе выходит
        // самое обидное: «!стоп» просит тело у того самого карьера, который он
        // и пришёл остановить, — и человек читает «Сейчас занят: команда
        // карьер. Повтори позже» вместо остановки (см. Add(needsBody))
        if (!command.NeedsBody)
        {
            try
            {
                await command.Handler(callArgs);
                return "выполнено";
            }
            catch (Exception ex)
            {
                OnCommandError?.Invoke(name, ex);
                return $"беда в команде «{name}»: {ex.Message}";
            }
        }

        using var hold = await bot.Context.Turn.TakeAsync(
            $"команда {name}", BodyArbiter.Importance.Command, WaitForBodySeconds);
        if (hold == null)
        {
            string busy = $"Сейчас занят: {bot.Context.Turn.Busy}. Повтори позже";
            _ = callArgs.ReplyAsync(busy);
            return busy;
        }
        try
        {
            await command.Handler(callArgs with { Token = hold.Token });
            return "выполнено";
        }
        catch (OperationCanceledException) when (hold.Interrupted)
        {
            _ = callArgs.ReplyAsync("Бросил дело: понадобилось спасаться");
            return "бросил дело: понадобилось спасаться";
        }
        catch (Exception ex)
        {
            OnCommandError?.Invoke(name, ex);
            return $"беда в команде «{name}»: {ex.Message}";
        }
    }

    /// <summary>
    /// Сколько первых букв обязаны совпасть, чтобы счесть слова одним корнем.
    ///
    /// Три — не с потолка: короче общее начало бывает у совсем разных команд
    /// («склад» и «скажи» сходятся на двух буквах), а на трёх расходятся уже
    /// почти все. Второе условие (общее начало не короче половины КОРОТКОГО
    /// слова) отсекает случай «длинное слово случайно начинается так же».
    /// </summary>
    public const int SameRootLetters = 3;

    /// <summary>
    /// БЛИЖАЙШАЯ ИЗВЕСТНАЯ КОМАНДА — чистое правило, без бота и без чата.
    ///
    /// Мерка НЕ «сколько букв поменять» (расстояние Левенштейна), а «общий
    /// корень». Так пишут люди по-русски: одно и то же дело они называют то
    /// существительным, то глаголом — «разгрузка» и «разгружайся», «копай» и
    /// «копать». По числу правок эти пары далеки (у «разгружайся» и «разгрузка»
    /// их пять — больше, чем у половины несвязанных слов), а по корню — рядом:
    /// «разгру».
    ///
    /// Ничьей не бывает: если на одинаково длинный корень претендуют двое,
    /// ответ null. Подсказать наугад из двух — то же самое, что угадать.
    /// </summary>
    /// <returns>Имя команды или null, если похожей нет либо их несколько.</returns>
    public static string? Nearest(string typed, IEnumerable<string> known)
    {
        string? best = null;
        int bestShared = 0;
        bool tie = false;

        foreach (string name in known)
        {
            // Точное имя — уже не «похожее»: спорить с ним нечему. Сюда такое
            // слово не доходит (его нашли бы по словарю), но правило обязано
            // быть верным и само по себе — его зовут и снаружи
            if (string.Equals(typed, name, StringComparison.OrdinalIgnoreCase))
                return name;
            int shared = SharedStart(typed, name);
            if (shared < SameRootLetters || shared * 2 < Math.Min(typed.Length, name.Length))
                continue;
            if (shared > bestShared)
            {
                bestShared = shared;
                best = name;
                tie = false;
            }
            else if (shared == bestShared && !string.Equals(best, name, StringComparison.OrdinalIgnoreCase))
                tie = true;
        }
        return tie ? null : best;
    }

    /// <summary>Сколько первых букв у слов общие (регистр не в счёт).</summary>
    private static int SharedStart(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
            i++;
        return i;
    }

    private string BuildHelp()
    {
        // Список команд при закрытом чате — это обещание, которого бот не
        // сдержит. Поэтому оговорка идёт в тот же ответ: человек видит, что
        // бот умеет, и сразу — что отсюда командовать нельзя
        string closed = AcceptFromChat
            ? ""
            : ". Командовать из игрового чата сейчас нельзя — только хозяину из окна управления";
        if (commands.Count == 0)
            return "Команд пока нет" + closed;
        var list = commands
            .OrderBy(kv => kv.Key)
            .Select(kv => kv.Value.Description.Length > 0
                ? $"{Prefix}{kv.Key} — {kv.Value.Description}"
                : Prefix + kv.Key);
        return "Команды: " + string.Join("; ", list) + closed;
    }
}
