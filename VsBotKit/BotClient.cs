using System.Buffers.Binary;
using System.Net.Sockets;
using Vintagestory.API.Config;
using Vintagestory.Common;

namespace VsBotKit;

/// <summary>
/// Headless-клиент Vintage Story: TCP-подключение, рукопожатие, чат.
/// Ядро библиотеки — поверх него работают модель мира и способности.
///
/// Протокол (см. декомпилированный TcpNetClient):
///   [4 байта big-endian: длина][payload]; старший бит длины = payload сжат (zstd).
///   Payload — protobuf Packet_Client / Packet_Server, (де)сериализуется
///   классами самой игры из VintagestoryLib.dll.
///
/// Последовательность входа:
///   → LoginTokenQuery(33)  ← TokenAnswer(77)   → Identification(1)
///   ← ServerReady(73)      → RequestJoin(11)
///   ← LevelFinalize(6)     → ClientLoaded(26) + ClientPlaying(29)
///   ← Ping(2)              → PingReply(2)
/// </summary>
public class BotClient : IDisposable
{
    // Куда подключаться — НЕ окончательно: окно управления задаёт сервер
    // на ходу, пока бот не в игре (см. UseServer)
    private string host;
    private int port;
    // Имя и uid НЕ окончательны: вход под аккаунтом заменяет их на настоящие
    // (см. UseAccount). Сервер сверяет имя с аккаунтом строго, буква в букву,
    // поэтому придуманное в конфиге имя на настоящем сервере не годится
    private string playerName;
    private string playerUid;
    private readonly string? mpToken;
    private string? serverPassword;

    private TcpClient? tcp;
    private NetworkStream? stream;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly CancellationTokenSource cts = new();

    /// <summary>Печатать hex всех пакетов (диагностика).</summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Дальность прогрузки в блоках. Влияет и на видимость сущностей:
    /// сервер отслеживает для клиента только сущности в присланных ему чанках
    /// (сам трекинг ограничен ~128 блоками — больше ставить смысла нет,
    /// меньше — бот не увидит игроков за пределами).
    /// </summary>
    public int ViewDistance { get; set; } = 128;

    /// <summary>Диагностические сообщения ядра (подключение, вход, дисконнект).</summary>
    public event Action<string>? OnLog;

    /// <summary>Каждый входящий пакет сервера — точка подключения подсистем (мир, сущности).</summary>
    public event Action<Packet_Server>? OnPacket;

    /// <summary>
    /// Пакет ушёл на сервер. Нужен записи прогонов: без исходящих запись
    /// показывает, что бот увидел, но не что он делал.
    /// </summary>
    public event Action<Packet_Client>? OnPacketSent;

    /// <summary>
    /// Подать пакет так, будто он пришёл с сервера, — шов для стенда.
    /// Так же работает и воспроизведение записи (PacketLog.Replay): модели
    /// получают ровно то же, что пришло бы по сети, и не отличают одно от
    /// другого.
    ///
    /// ЧАТ РАЗБИРАЕТСЯ ЗДЕСЬ ЖЕ, и это не мелочь: журнал урона сервера
    /// приходит именно чатом (группа −5, см. <see cref="DamageWatch"/>), и без
    /// этого шва проверить «строка сервера → решение бота» было бы нечем, не
    /// поднимая сети. Разбор один и тот же, что и в приёмнике: <see cref="Chat"/>.
    /// </summary>
    public void Feed(Packet_Server packet)
    {
        OnPacket?.Invoke(packet);
        if (packet.Id == 8 && packet.Chatline is { } line)
            Chat(line);
    }

    /// <summary>
    /// ЧТО ИЗ ЭТОЙ СТРОКИ ЧАТА ПОПАДЁТ В ЖУРНАЛ ХОЗЯИНА (null — промолчать).
    ///
    /// ЖИВОЙ СЛУЧАЙ. Журнал урона сервера (группа −5) каплет: за полминуты
    /// двадцать строк «Получено 0,7 хп от heal», а весь день подряд —
    /// «Потеряно 0,13 хп от hunger» каждые шесть секунд. Человеку это читать
    /// незачем, а ПРИЧИНУ урона сервер сообщает только здесь — выбросить
    /// группу целиком тоже нельзя.
    ///
    /// Ставит правило ТОТ, КТО ЭТУ ГРУППУ ПОНИМАЕТ, — <see cref="DamageWatch"/>:
    /// он один разбирает строку в факт и потому один может отличить каплю от
    /// новости. Второго желающего нет и быть не должно: разбор группы −5 в
    /// проекте один (правило 1 проекта).
    ///
    /// Не поставлено — печатаем как раньше, слово в слово от сервера.
    /// </summary>
    public Func<ChatMessage, string?>? ChatLogRule { get; set; }

    private void Chat(Packet_ChatLine line)
    {
        var message = new ChatMessage(line.Message ?? "", line.Groupid, line.Data);
        // Печать ПЕРЕД разбором: порядок строк в журнале остаётся прежним —
        // сперва слово сервера, потом решение бота по нему
        if ((ChatLogRule is { } rule ? rule(message) : message.Text) is { Length: > 0 } show)
            Log($"[чат:{line.Groupid}] {show}");
        OnChat?.Invoke(message);
    }

    public event Action<ChatMessage>? OnChat;
    public event Action? OnJoined;
    public event Action<string>? OnDisconnected;

    /// <summary>
    /// СЕРВЕР НАЗВАЛ НАЧАЛО ОТСЧЁТА МИРА (пакет 51), и оно НОВОЕ. Тот же пакет
    /// при каждом входе новостью не считается — про это <see cref="WorldOrigin.Learn"/>.
    ///
    /// Событие ИМЕННО ЗДЕСЬ, у клиента, а не статическое у <see cref="WorldOrigin"/>:
    /// начало отсчёта одно на процесс, а вот УСЛЫШАЛ его конкретный бот, и
    /// говорить о нём в свой журнал должен он один. Разбор живого случая — в
    /// <see cref="WorldOrigin.Learn"/>.
    /// </summary>
    public event Action<WorldOrigin.Spawn>? OnOriginLearned;

    public bool Joined { get; private set; }
    public string PlayerName => playerName;
    public string PlayerUid => playerUid;

    /// <summary>Точка спавна бота (появляется после входа в мир).</summary>
    public (double X, double Y, double Z)? SpawnPosition { get; private set; }

    /// <summary>
    /// НА КАКОМ ЯЗЫКЕ СЕРВЕР ГОВОРИТ С БОТОМ. Мы сами просим его при входе
    /// (Packet_ClientRequestJoin.Language), сервер запоминает это за игроком
    /// и шлёт ему свои строки именно так (IServerPlayer.LanguageCode).
    ///
    /// Не украшение: журнал урона — единственное место, где сервер сообщает
    /// ПРИЧИНУ урона, и приходит он текстом на этом самом языке (см.
    /// <see cref="DamageWatch"/>). Раньше «ru» стояло прямо в пакете, и
    /// разбору было неоткуда узнать, чего ждать.
    /// </summary>
    public string Language { get; set; } = "ru";

    public BotClient(string host, int port, string playerName, string playerUid,
        string? mpToken = null, string? serverPassword = null)
    {
        this.host = host;
        this.port = port;
        this.playerName = playerName;
        this.playerUid = playerUid;
        this.mpToken = mpToken;
        this.serverPassword = serverPassword;
        // ПРОГЛОЧЕННОЕ НЕ ПРОПАДАЕТ. Радиотишина прячет отчёт от чужих глаз в
        // чате, а не от хозяина: строка уходит в его журнал. Иначе включённая
        // тишина стала бы способом ПОТЕРЯТЬ отчёт о наряде
        Voice.OnHushed += Log;
        // УКОРОЧЕННОЕ — ТОЖЕ НЕ ПРОПАДАЕТ. В чате коротко, в журнале целиком:
        // иначе краткость стала бы вторым способом потерять отчёт
        Voice.OnShortened += Log;
    }

    /// <summary>
    /// Кто добывает ОДНОРАЗОВЫЙ токен подключения по токену сервера.
    ///
    /// Зачем так. Сервер игры на каждое соединение придумывает свой
    /// serverlogintoken и присылает его пакетом 77. Токен мультиплеера
    /// выпускается сервером аккаунтов ПОД ЭТОТ токен, то есть под конкретное
    /// соединение: запасённая в конфиге строка годится только там, где
    /// проверка аккаунта выключена вовсе.
    ///
    /// null — вход без аккаунта, как было раньше.
    /// Возврат null от самого поставщика означает «не вышло»: тогда бот шлёт
    /// то, что есть, и сервер откажет с внятной причиной, а не молча.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? MpTokenProvider { get; set; }

    /// <summary>
    /// Назвать настоящие имя и uid аккаунта. Сервер сверяет имя с тем, что
    /// вернул сервер аккаунтов, СТРОГО и с учётом регистра — поэтому своё
    /// придуманное имя приходится заменить.
    /// </summary>
    public void UseAccount(string name, string uid)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(uid))
            return;
        if (name != playerName)
            Log($"вхожу под именем: {name} (было {playerName})");
        playerName = name;
        playerUid = uid;
    }

    /// <summary>Куда сейчас нацелен бот — окну управления надо это показать.</summary>
    public string Host => host;

    /// <summary>Порт сервера.</summary>
    public int Port => port;

    /// <summary>Задан ли пароль сервера (сам пароль наружу не отдаём).</summary>
    public bool HasServerPassword => !string.IsNullOrEmpty(serverPassword);

    /// <summary>
    /// Назначить сервер, к которому подключаться.
    ///
    /// Зачем менять на ходу. Раньше «запустить бота» и означало «войти на
    /// сервер»: адрес брался из файла настроек в момент создания клиента, и
    /// чтобы сменить сервер, программу надо было закрыть и открыть заново.
    /// Хозяин попросил разумного: запустил — открыл окно — выбрал, кем и куда
    /// входить — нажал «Вход».
    ///
    /// Пока бот В ИГРЕ, менять адрес нельзя: сперва выйти с сервера.
    /// </summary>
    public bool UseServer(string newHost, int newPort, string? password = null)
    {
        if (Joined)
        {
            Log("сменить сервер на ходу нельзя — сперва выйти с текущего");
            return false;
        }
        if (string.IsNullOrWhiteSpace(newHost) || newPort is < 1 or > 65535)
        {
            Log($"негодный адрес сервера: {newHost}:{newPort}");
            return false;
        }
        host = newHost.Trim();
        port = newPort;
        // Пустая строка — это «пароля нет», а не «оставить прежний»:
        // иначе снятый пароль незаметно продолжал бы уходить на сервер
        serverPassword = string.IsNullOrEmpty(password) ? null : password;
        Log($"сервер: {host}:{port}" + (serverPassword != null ? " (с паролем)" : ""));
        return true;
    }

    /// <summary>Возвращаться в мир после разрыва связи (сервер упал, перезапуск).</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Пауза перед первой попыткой; дальше она растёт до ReconnectMaxDelay.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Потолок паузы между попытками.</summary>
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Сколько раз пробовать (0 — бесконечно).</summary>
    public int MaxReconnectAttempts { get; set; }

    /// <summary>Связь потеряна, идёт попытка вернуться (номер попытки).</summary>
    public event Action<int>? OnReconnecting;

    /// <summary>
    /// Всё, что копилось за прошлое соединение, больше не действительно:
    /// подписчики (мир, сущности, инвентари) должны забыть своё состояние.
    /// </summary>
    public event Action? OnSessionReset;

    /// <summary>Отменять эту сессию (соединение), не убивая весь бот.</summary>
    private CancellationTokenSource session = new();

    /// <summary>
    /// Работать: подключиться и держать соединение. При обрыве — вернуться
    /// в мир самостоятельно (сервер падает и перезапускается — бот не должен
    /// требовать человека).
    /// </summary>
    public async Task RunAsync()
    {
        int attempt = 0;
        var delay = ReconnectDelay;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"связь потеряна: {ex.GetType().Name} {ex.Message}");
            }

            Joined = false;
            stream = null;
            try { tcp?.Dispose(); } catch { /* уже закрыт */ }
            tcp = null;

            if (!AutoReconnect || cts.IsCancellationRequested)
                return;
            if (MaxReconnectAttempts > 0 && attempt >= MaxReconnectAttempts)
            {
                Log($"не смог вернуться за {attempt} попыток — сдаюсь");
                return;
            }

            attempt++;
            OnReconnecting?.Invoke(attempt);
            Log($"попытка вернуться №{attempt} через {delay.TotalSeconds:0} с");
            try { await Task.Delay(delay, cts.Token); }
            catch (OperationCanceledException) { return; }
            // Каждая неудача удваивает паузу — не долбим упавший сервер
            delay = TimeSpan.FromMilliseconds(Math.Min(ReconnectMaxDelay.TotalMilliseconds,
                delay.TotalMilliseconds * 2));
        }
    }

    private async Task ConnectAndRunAsync()
    {
        // Новая сессия: старое состояние мира и инвентарей больше не про нас
        session = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        OnSessionReset?.Invoke();

        tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, session.Token);
        tcp.NoDelay = true;
        stream = tcp.GetStream();
        Log($"TCP-соединение с {host}:{port} установлено");

        // Первым пакетом свежего соединения сервер ждёт LoginTokenQuery,
        // иначе трактует соединение как запрос статистики и отключает
        await SendPacketAsync(new Packet_Client { Id = 33 });
        Log("Запросил логин-токен, жду ответ сервера...");

        await ReceiveLoopAsync();
    }

    private async Task ReceiveLoopAsync()
    {
        var header = new byte[4];
        while (!session.IsCancellationRequested)
        {
            await ReadExactAsync(header, 4);
            int raw = BinaryPrimitives.ReadInt32BigEndian(header);
            bool compressed = (raw & unchecked((int)0x80000000)) != 0;
            int length = raw & 0x7FFFFFFF;
            if (length is < 0 or > 256 * 1024 * 1024)
                throw new InvalidDataException($"Подозрительная длина пакета: {length}");

            var payload = new byte[length];
            await ReadExactAsync(payload, length);
            if (compressed)
                payload = Compression.Decompress(payload);

            var packet = new Packet_Server();
            Packet_ServerSerializer.DeserializeBuffer(payload, payload.Length, packet);
            if (Debug)
                Log($"<< Id={packet.Id} len={length} {Convert.ToHexString(payload.AsSpan(0, Math.Min(64, payload.Length)))}");
            await HandlePacketAsync(packet);
            OnPacket?.Invoke(packet);
        }
    }

    private async Task HandlePacketAsync(Packet_Server p)
    {
        switch (p.Id)
        {
            case 77: // TokenAnswer — теперь можно представляться
                Log("Логин-токен получен, отправляю идентификацию");

                // ТОКЕН ПОДКЛЮЧЕНИЯ. Токен сервера действителен только для
                // этого соединения, и токен аккаунта выпускается под него.
                // Поэтому ходить за ним надо ЗДЕСЬ, между пакетом 77 и
                // пакетом 1, а не заранее
                string? token = mpToken;
                if (MpTokenProvider is { } getToken && p.Token?.Token is { Length: > 0 } serverToken)
                {
                    try
                    {
                        token = await getToken(serverToken, session.Token) ?? mpToken;
                    }
                    catch (Exception e)
                    {
                        // Молчать нельзя: сервер откажет, и без этой строки
                        // причина выглядела бы как «сервер не пустил»
                        Log($"токен аккаунта получить не вышло: {e.Message}");
                    }
                }

                await SendPacketAsync(new Packet_Client
                {
                    Id = 1,
                    Identification = new Packet_ClientIdentification
                    {
                        Playername = playerName,
                        PlayerUID = playerUid,
                        MpToken = token,
                        ServerPassword = string.IsNullOrEmpty(serverPassword) ? null : serverPassword,
                        // Версии берём из констант установленной игры — совпадают всегда
                        MdProtocolVersion = GameVersion.ShortGameVersion,
                        NetworkVersion = GameVersion.NetworkVersion,
                        ShortGameVersion = GameVersion.ShortGameVersion,
                        ViewDistance = ViewDistance,
                        RenderMetaBlocks = 0
                    }
                });
                break;

            case 1: // ServerIdentification
                Log($"Сервер представился: \"{p.Identification?.ServerName}\", игра {p.Identification?.GameVersion}");
                // Позиции сущностей просим по TCP — обходимся без UDP-канала
                await SendPacketAsync(new Packet_Client { Id = 34 });
                break;

            case 2: // Ping
                await SendPacketAsync(new Packet_Client { Id = 2, PingReply = new Packet_ClientPingReply() });
                break;

            case 5: // LevelDataChunk (прогресс загрузки)
                Log($"Загрузка мира: {p.LevelDataChunk?.PercentComplete}%");
                break;

            case 73: // ServerReady
                Log("Сервер готов, запрашиваю вход в мир...");
                await SendPacketAsync(new Packet_Client
                {
                    Id = 11,
                    RequestJoin = new Packet_ClientRequestJoin { Language = Language }
                });
                break;

            case 6: // LevelFinalize
                Log("Мир финализирован, подтверждаю загрузку");
                await SendPacketAsync(new Packet_Client { Id = 26 }); // ClientLoaded
                await SendPacketAsync(new Packet_Client { Id = 29 }); // ClientPlaying
                Joined = true;
                OnJoined?.Invoke();
                break;

            case 8: // ChatLine
                if (p.Chatline != null)
                    Chat(p.Chatline);
                break;

            case 9: // DisconnectPlayer
                string reason = p.DisconnectPlayer?.DisconnectReason ?? "без причины";
                Log($"Сервер отключил бота: {reason}");
                Joined = false;
                OnDisconnected?.Invoke(reason);
                // Рвём только эту сессию: вернуться в мир — дело RunAsync
                session.Cancel();
                break;

            case 51: // SpawnPosition
                if (p.EntityPosition != null)
                {
                    SpawnPosition = (
                        CollectibleNet.DeserializeDoublePrecise(p.EntityPosition.X),
                        CollectibleNet.DeserializeDoublePrecise(p.EntityPosition.Y),
                        CollectibleNet.DeserializeDoublePrecise(p.EntityPosition.Z));
                    // ЭТОТ ПАКЕТ — ЕДИНСТВЕННЫЙ ИСТОЧНИК НАЧАЛА ОТСЧЁТА, и у
                    // игры тоже: GeneralPacketHandler.HandleSpawnPosition кладёт
                    // его в ClientMain.SpawnPosition, а окно координат (клавиша
                    // «C», HudElementCoordinates.Every250ms) вычитает именно эту
                    // клетку. Отсюда и все игровые координаты в журнале и в окне
                    // О НОВОМ НАЧАЛЕ ОТСЧЁТА ГОВОРИТ ТОТ, КТО ЕГО УСЛЫШАЛ.
                    // Раньше об этом кричало статическое событие на весь
                    // процесс, и на стенде чужие боты повторяли новость за
                    // соседом (см. WorldOrigin.Learn — там живой случай)
                    if (WorldOrigin.Learn(SpawnPosition.Value.X, SpawnPosition.Value.Z)
                        is { } начало)
                        OnOriginLearned?.Invoke(начало);
                }
                break;

            case 68: // IngameError — сервер отклонил действие
                Log($"ОТКАЗ СЕРВЕРА: {p.IngameError?.Code} {p.IngameError?.Message}");
                break;

            case 82: // Queue
                Log($"В очереди на вход, позиция: {p.QueuePacket?.Position}");
                break;
        }
    }

    /// <summary>Минимальный интервал между сообщениями (сервер режет частый чат).</summary>
    public TimeSpan ChatInterval { get; set; } = TimeSpan.FromMilliseconds(1100);

    private readonly SemaphoreSlim chatLock = new(1, 1);
    private DateTime lastChatAt = DateTime.MinValue;

    /// <summary>
    /// ГОЛОС БОТА: пускать ли строку в игровой чат и почему.
    ///
    /// Живёт здесь, потому что здесь ЕДИНСТВЕННАЯ дверь в чат: через
    /// <see cref="SendChatAsync"/> идут и ответы команд, и приветствия, и
    /// «/emote», и строка из окна «сказать». Ворота у каждого говорящего
    /// означали бы семь одинаковых проверок и восьмую забытую — а забытая
    /// проверка здесь выдаёт бота одной строкой (см. <see cref="BotVoice"/>).
    /// </summary>
    public BotVoice Voice { get; } = new();

    /// <summary>
    /// Написать в чат. Сообщения выстраиваются в очередь и отправляются не чаще
    /// ChatInterval — иначе сервер молча выбрасывает лишние.
    ///
    /// Возвращает ТО, ЧТО НА САМОМ ДЕЛЕ УШЛО В ЧАТ: null — строку проглотила
    /// радиотишина (и уже сказала об этом в журнал); иначе — текст, который
    /// получил сервер. Не «правда/ложь» нарочно: строку могли УКОРОТИТЬ
    /// (см. <see cref="BotVoice.ForChat"/>), и тогда «бот сказал» — это
    /// короткая строка, а не та, что подали. Верни мы «правда», окно
    /// управления и проверки по чату видели бы одно, а игроки в чате — другое.
    /// Дошла ли строка до сервера, знает только он сам; об обрыве связи
    /// говорит SendPacketAsync.
    /// </summary>
    /// <param name="reason">
    /// По какому поводу бот это говорит. Заводское «приказ» — «эту строку
    /// велели сказать прямо»: такую не глушит ничто. Свои отчёты о делах
    /// говорящий обязан называть своим именем (<see cref="SayReason.Отчёт"/>),
    /// иначе радиотишина их не заметит.
    /// </param>
    public async Task<string?> SendChatAsync(string message, int groupId = 0,
        SayReason reason = SayReason.Приказ)
    {
        if (!Voice.Allows(message, reason))
            return null;

        // КОРОТКАЯ СТРОКА ДЛЯ ЧАТА — здесь, у единственной двери в чат, а не у
        // говорящего. Говорящих семь, и восьмой бы забыл: ровно так весь отчёт
        // о дороге (700 букв) и уехал в общий чат 20.08 простынёй на пять
        // строк. Повод передаём вместе со строкой: от него зависит, нужен ли
        // журналу целый текст вторым экземпляром (см. BotVoice.ShortenNote)
        message = Voice.ForChat(message, reason);

        await chatLock.WaitAsync(cts.Token);
        try
        {
            var wait = ChatInterval - (DateTime.UtcNow - lastChatAt);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, cts.Token);
            lastChatAt = DateTime.UtcNow;
            await SendPacketAsync(new Packet_Client
            {
                Id = 4,
                Chatline = new Packet_ChatLine { Message = message, Groupid = groupId }
            });
        }
        finally
        {
            chatLock.Release();
        }
        return message;
    }

    private DateTime lastOfflineLog = DateTime.MinValue;

    /// <summary>Отправка произвольного пакета — для подсистем библиотеки.</summary>
    public async Task SendPacketAsync(Packet_Client packet)
    {
        // Во время переподключения слать некуда. Молча ронять пакеты нельзя
        // (правило «не врать о результате»), но и заваливать лог одинаковыми
        // строками незачем — сообщаем не чаще раза в секунду
        if (stream == null)
        {
            if (DateTime.UtcNow - lastOfflineLog > TimeSpan.FromSeconds(1))
            {
                lastOfflineLog = DateTime.UtcNow;
                Log("нет соединения — действие не ушло на сервер");
            }
            return;
        }

        OnPacketSent?.Invoke(packet);

        byte[] body = Packet_ClientSerializer.SerializeToBytes(packet);
        var framed = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
        body.CopyTo(framed, 4);

        if (Debug)
            Log($">> {Convert.ToHexString(framed.AsSpan(0, Math.Min(68, framed.Length)))}");
        await sendLock.WaitAsync(cts.Token);
        try
        {
            await stream!.WriteAsync(framed, cts.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async Task ReadExactAsync(byte[] buffer, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream!.ReadAsync(buffer.AsMemory(offset, count - offset), cts.Token);
            if (read == 0)
                throw new IOException("Сервер закрыл соединение");
            offset += read;
        }
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    /// <summary>
    /// Разорвать текущее соединение, не убивая бота.
    ///
    /// Нужно окну управления: «выйти с сервера» и «переподключиться» — обычные
    /// действия, и ради них незачем завершать программу. Возвращаться или нет,
    /// решает <paramref name="comeBack"/>: с ним разрыв выглядит как обычная
    /// потеря связи и приёмный цикл поднимет соединение заново.
    /// </summary>
    public void Disconnect(string why = "по просьбе хозяина", bool comeBack = false)
    {
        AutoReconnect = comeBack;
        Log($"отключаюсь: {why}" + (comeBack ? " (вернусь)" : ""));
        // Рвём именно СЕССИЮ, а не жизнь бота: общий cts гасит его целиком
        session.Cancel();
        try
        {
            stream?.Dispose();
            tcp?.Dispose();
        }
        catch
        {
            // Сокет мог уже умереть — это ровно то, чего мы и добивались
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        stream?.Dispose();
        tcp?.Dispose();
    }
}
