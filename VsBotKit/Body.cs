using Vintagestory.Common;

namespace VsBotKit;

/// <summary>
/// Тело бота: единственное место, которое двигает бота и шлёт его позицию.
///
/// Внутри крутится игровая физика с шагом 1/60 с, а наружу торчат только
/// «клавиши»: идти туда, прыгнуть, присесть. Позицию НИКТО больше не пишет —
/// поэтому бот физически не может ни зависнуть, ни прыгнуть выше игрока, ни
/// пройти сквозь стену: за это отвечает код игры, а не наш.
///
/// Пакет позиции уходит каждый 4-й тик — ровно как у живого клиента (~15 раз
/// в секунду). Это же единственный способ показать наблюдателям анимации.
/// Нажатия клавиш дублируются пакетами MoveKeyChange — по ним сервер
/// включает окружающим анимации ходьбы, бега и прыжка.
/// </summary>
public sealed class Body
{
    private readonly BotClient bot;
    private readonly EntityModel entities;
    private readonly WorldModel world;
    private BotGamePhysics.GamePlayerPhysics? physics;
    private int tick;

    /// <summary>Физика игры (null, пока не пришёл реестр блоков).</summary>
    public BotGamePhysics.GamePlayerPhysics? Physics => physics;

    /// <summary>Тело готово: реестр разобран, физика крутится.</summary>
    public bool Ready => physics != null;

    /// <summary>
    /// Сколько физических тиков тело реально прокрутило. Это наш аналог кадров
    /// клиента: игра считает шаги использования предмета по кадрам, и брать их
    /// надо ОТСЮДА, а не из «секунды × 60» — иначе при подтормаживании бот
    /// сообщит серверу больше шагов, чем сделал.
    /// </summary>
    public int Ticks => tick;

    /// <summary>
    /// СКОЛЬКО ВРЕМЕНИ ПРОЖИЛО САМО ТЕЛО — те же тики, только в секундах.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНЫЕ ЧАСЫ, КОГДА ЕСТЬ СТЕННЫЕ. Тело двигает вот этот цикл, и
    /// шагает оно НЕ по стенным часам, а по своим тикам: цикл старается
    /// держать шаг реального времени, но, отстав, долг не копит («отстали — не
    /// копим долг» ниже по коду). Значит на занятой машине за полторы стенные
    /// секунды тело проживает, скажем, полсекунды — и проходит втрое меньше.
    ///
    /// ЧЕМ ЭТО СТОИЛО. Сторожа застревания в движении спрашивали стенные часы:
    /// «прошло StuckSeconds, а тело не сдвинулось — упёрся». Под нагрузкой они
    /// срабатывали на ровном месте: бот объявлял «упёрся на точке 3/4», винил
    /// спрямление, писал клетку в тупики и перестраивал маршрут — хотя мешала
    /// ему только занятая машина. Хуже того, перехват тела соперником в такой
    /// миг переставал считаться перехватом (ходьба кончалась «застрял», а не
    /// «отобрали»), счётчик уступки не рос — и две дороги дрались за тело без
    /// конца. Это ровно живая жалоба «бегает вперёд-назад».
    ///
    /// Та же мерка, что и у <see cref="Ticks"/>, и по той же причине: спрашивать
    /// надо тело, а не стену. Стенные часы остаются там, где идёт СВЕТ ИЗВНЕ:
    /// срок, отпущенный вызывающим, и скорость чужих сущностей по их пакетам.
    /// </summary>
    public double LivedSeconds => Ticks * BotGamePhysics.GamePlayerPhysics.TickSeconds;

    /// <summary>Диагностика (телепорты сервера, сбои).</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Куда смотреть головой (смещение от корпуса). Само значение доезжает
    /// плавно, со скоростью шеи — иначе взгляд дёргается, как тик.
    /// </summary>
    public float HeadYawTarget { get; set; }

    /// <summary>Скорость поворота головы, радиан в секунду (у человека ~4-5).</summary>
    public double HeadTurnSpeed { get; set; } = 4.5;

    /// <summary>
    /// Стоя в воде, держать пробел (иначе игрок тонет — и бот утонет так же).
    /// </summary>
    public bool KeepAfloat { get; set; } = true;

    private float headYaw;
    private bool drowningWarned;

    /// <summary>
    /// Палец на клавише прыжка. Живёт при теле, а не при ходьбе, потому что
    /// отпускать клавишу надо в тот же ТИК, когда ноги коснулись земли
    /// (см. <see cref="JumpKey"/>), а тикает тело.
    /// </summary>
    private readonly JumpKey jumpKey = new();

    /// <summary>
    /// ПАЛЕЦ НА ШИФТЕ — сколько приёмов прямо сейчас просят держать присед.
    ///
    /// Живёт при теле по той же причине, что и палец на прыжке строкой выше:
    /// клавиши жмёт тело, и второго места, где их жмут, быть не должно. Счётчик,
    /// а не флаг, потому что приёмы вкладываются друг в друга: мост держит
    /// присед на всю переправу, а внутри него подход к клетке шагает сам по
    /// себе и на выходе не должен отпускать чужой шифт.
    ///
    /// ПРИСЕД НУЖЕН НЕ ДЛЯ СКРЫТНОСТИ. Заказчик описал приём живого игрока
    /// дословно: «нужно чуть выдвинуться вперёд с зажатым шифтом и ставить блок,
    /// глядя на боковую стенку». Присед в игре — это две разные вещи разом:
    /// тело не сходит с кромки (<c>HandleSneaking</c>: шаг за край отменяется
    /// по каждой оси отдельно) и тело УКОРАЧИВАЕТСЯ до 1,48
    /// (<see cref="BodySize.CrouchHeight"/>), то есть влезает под низкий потолок.
    /// </summary>
    private int crouchHolds;

    /// <summary>Держит ли тело присед по чьей-то просьбе (не считая лестниц).</summary>
    public bool Crouching => Volatile.Read(ref crouchHolds) > 0;

    /// <summary>
    /// Держать присед, пока не отпустят. Возвращённое надо освободить
    /// (<c>using</c>) — иначе бот уползёт по миру втрое медленнее и не поймёт,
    /// почему: скорость на присяде в игре 0,35 от обычной.
    ///
    /// Тело нажимает шифт САМО, каждый тик: клавишу сбрасывает любой
    /// <c>Physics.Stop()</c> — а его зовут и подход к блоку, и расчистка, и
    /// открывание двери, то есть ровно то, что делается посреди приёма.
    /// </summary>
    public IDisposable HoldCrouch()
    {
        Interlocked.Increment(ref crouchHolds);
        return new CrouchRelease(this);
    }

    private sealed class CrouchRelease(Body body) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
                return;
            if (Interlocked.Decrement(ref body.crouchHolds) <= 0 &&
                body.physics is { } phys)
                phys.Controls.Sneak = false;
        }
    }

    /// <summary>
    /// Тик простаивает, ожидая своё состояние от сервера. Флаг нужен, чтобы
    /// сказать об этом ОДИН раз, а не шестьдесят раз в секунду.
    /// </summary>
    private bool waitingForWorld;

    // Последние отправленные клавиши (шлём только изменения, как живой клиент)
    private readonly bool[] sentKeys = new bool[15];

    public Body(BotClient bot, EntityModel entities, WorldModel world)
    {
        this.bot = bot;
        this.entities = entities;
        this.world = world;
    }

    /// <summary>
    /// Мёртвый игрок не шлёт серверу свою позицию — экран смерти, тело лежит.
    /// Роль/фасад подставляет сюда своё «я мёртв».
    /// </summary>
    public Func<bool>? IsDead { get; set; }

    private CancellationTokenSource? runCts;

    /// <summary>
    /// Запустить тело: физика + поток позиции. Повторный вызов (возвращение
    /// в мир после обрыва связи) останавливает прошлый цикл — двух владельцев
    /// позиции быть не должно.
    /// </summary>
    public void Start(CancellationToken ct)
    {
        runCts?.Cancel();
        runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        physics = null;
        tick = 0;
        StartLoop(runCts.Token);
    }

    private void StartLoop(CancellationToken ct) => _ = Task.Run(async () =>
    {
        // Физику можно создать только когда есть настоящие блоки игры.
        // И НЕ раньше, чем доехал чанк под ногами: стартовав над «воздухом»
        // непрогруженного чанка, бот проседает, а когда чанк приходит —
        // он уже врос в землю на блок
        while (!ct.IsCancellationRequested)
        {
            if (world.GameBlocksReady && entities.Self is { } s && ChunkBelowLoaded(s))
                break;
            await Task.Delay(200, ct).ContinueWith(_ => { });
        }
        if (ct.IsCancellationRequested)
            return;

        physics = new BotGamePhysics.GamePlayerPhysics(world);
        if (entities.Self is { } self)
            physics.Teleport(self.X, self.Y, self.Z);
        OnLog?.Invoke("тело на игровой физике");

        var next = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            next = next.AddSeconds(BotGamePhysics.GamePlayerPhysics.TickSeconds);
            double wait = (next - DateTime.UtcNow).TotalMilliseconds;
            if (wait > 0)
                await Task.Delay((int)Math.Max(1, wait), ct).ContinueWith(_ => { });
            else
                next = DateTime.UtcNow; // отстали — не копим долг

            try
            {
                // ТИКАТЬ НЕЧЕМ — И ЭТО НОРМАЛЬНО.
                //
                // После смерти, возрождения и особенно переподключения сессия
                // сбрасывается: мир, сущности и своё состояние забываются и
                // приходят заново пакетами. Между сбросом и их приходом тик
                // продолжал крутиться на пустом месте и падал —
                // «сбой тика: NullReferenceException» вылезал в каждом
                // переподключении, а раз в двести миллисекунд бот вместо
                // работы разбирал исключение.
                //
                // Ждать пустоту молча тоже нельзя: если состояние не придёт
                // вовсе, человек должен это увидеть, а не гадать, почему бот
                // стоит столбом
                if (physics == null || entities.Self == null || !world.GameBlocksReady)
                {
                    if (!waitingForWorld)
                    {
                        waitingForWorld = true;
                        OnLog?.Invoke("жду своё состояние от сервера — тело пока не двигаю");
                    }
                    await Task.Delay(100, ct).ContinueWith(_ => { });
                    continue;
                }
                if (waitingForWorld)
                {
                    waitingForWorld = false;
                    OnLog?.Invoke("состояние пришло — тело снова работает");
                }

                SyncFromServer();

                // Ударили — передаём телу серверный вектор отброса, дальше
                // игровой модуль сам добавит его в движение
                ApplyServerKnockback();

                // Стоя в воде, игрок держит пробел, чтобы не утонуть
                if (KeepAfloat && physics.Swimming && !physics.Controls.TriesToMove)
                    physics.Controls.Jump = true;

                // ВОЗДУХ — ВЫШЕ ЛЮБЫХ ПЛАНОВ. Бот тонул, потому что всплытие
                // жило только в ходьбе: в бою тело занято дракой, и никто не
                // всплывал. Здесь это последний рубеж — работает всегда,
                // чем бы бот ни был занят
                if (physics.Swimming && entities.Self is { } me && me.OxygenFraction < 0.35)
                {
                    if (!drowningWarned)
                    {
                        drowningWarned = true;
                        OnLog?.Invoke($"воздух на исходе ({me.OxygenFraction:P0}) — всплываю, что бы ни делал");
                    }
                    physics.Controls.Jump = true;      // вверх
                    physics.Controls.Sneak = false;    // вниз — ни в коем случае
                    // И взгляд вверх: под водой вертикаль тянет наклон, и с
                    // «взглядом в дно» бот всплывал бы против собственной тяги
                    if (physics.Pitch > MathF.PI)
                        physics.Pitch = MathF.PI;
                }
                else if (drowningWarned && (entities.Self?.OxygenFraction ?? 1) > 0.8)
                {
                    drowningWarned = false;
                }

                // ПРИЗЕМЛИЛСЯ — ПАЛЕЦ С КЛАВИШИ ПРЫЖКА, В ТОТ ЖЕ ТИК.
                //
                // Игра прыгает каждым тиком, пока клавиша нажата и с прошлого
                // прыжка прошло 500 мс (PModuleOnGround), а полёт длится
                // JumpPhysics.AirTime (683 мс, замер на игровой физике; здесь
                // стояло переписанное от руки «720 мс» — бумажное число, от
                // которого сам полёт давно пересчитали):
                // к посадке кулдаун давно истёк, и зажатая клавиша даёт второй
                // прыжок сама собой. Отсюда «лишние прыжки при беге» и «будто
                // двойной прыжок» — и падение за кромку, если второй скачок
                // случился у самого края.
                //
                // Решение обязано жить ЗДЕСЬ: ходьба смотрит на тело раз в
                // 50 мс, а это три игровых тика — игре хватает первого. Само
                // правило — в JumpKey, одно на всех, кто вообще жмёт прыжок
                physics.Controls.Jump = jumpKey.Hold(physics.Controls.Jump,
                    physics.OnGround, physics.Climbing, physics.Swimming);

                // ПРИСЕД ДОЖИМАЕТСЯ КАЖДЫЙ ТИК, ПОКА ЕГО ПРОСЯТ, — и только
                // дожимается, никогда не отпускается. Отпускает его тот, кто
                // просил (CrouchRelease), и лестница вправе держать свой
                // собственный присед на спуске: там клавиша значит «вниз», а
                // не «пригнуться», и затирать её отсюда нельзя
                if (Volatile.Read(ref crouchHolds) > 0)
                    physics.Controls.Sneak = true;

                physics.Tick();
                if (++tick % 4 == 0)
                {
                    await SendPositionAsync();
                    await SendKeyDiffsAsync();
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                OnLog?.Invoke($"сбой тика: {ex.GetType().Name} {ex.Message}");
                await Task.Delay(200, ct).ContinueWith(_ => { });
            }
        }
    }, ct);

    /// <summary>
    /// Сервер может нас переставить (телепорт, возрождение, транслокатор).
    /// Такое принимаем: физика продолжит с нового места.
    /// </summary>
    private void SyncFromServer()
    {
        if (physics is not { } p || entities.Self is not { } self)
            return;
        double dx = self.X - p.X, dy = self.Y - p.Y, dz = self.Z - p.Z;
        if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4 || Math.Abs(dz) > 4)
        {
            OnLog?.Invoke($"сервер перенёс: {p.Y:0.#} -> {self.Y:0.#}");
            p.Teleport(self.X, self.Y, self.Z);
        }

        Unstick(p);
    }

    /// <summary>Прогружен ли столб под ботом (не «пустота» неприехавшего чанка).</summary>
    private bool ChunkBelowLoaded(EntityInfo self)
    {
        int x = (int)Math.Floor(self.X), z = (int)Math.Floor(self.Z);
        int top = (int)Math.Floor(self.Y);
        for (int y = top; y > top - 12 && y > 0; y--)
            if (world.GetBlockId(x, y, z) != 0)
                return true;
        return false;
    }

    /// <summary>
    /// Врос в блок (чанк доехал позже старта) — выбираемся ЧЕСТНО, клавишами:
    /// прыжок плюс шаг в сторону ближайшего свободного места. Никаких
    /// телепортаций: живой игрок в такой ситуации именно выпрыгивает, а
    /// подстановка позиции — это чит, пусть и «спасательный».
    /// Лестницы и двери не считаются: в них тело находится законно.
    /// </summary>
    private void Unstick(BotGamePhysics.GamePlayerPhysics p)
    {
        int fx = (int)Math.Floor(p.X), fz = (int)Math.Floor(p.Z);
        int fy = (int)Math.Floor(p.Y + 0.001);
        float top = world.GetCollisionTop(fx, fy, fz);
        // «ВРОС» — ЭТО РОВНО «НЕ СТОЮ НА НЁМ», и спрашивается это одним
        // правилом на весь проект (Walker.StandingOnOwnCell). Своя копия
        // условия жила здесь и отвечала верно, но такая же нужна была столбу —
        // а там её не было, и бот, стоя на каменной тропе, двумя разными
        // вечерами не мог с неё подняться (см. заглавие правила)
        bool embedded = top > 0.61f && !Walker.StandingOnOwnCell(p.Y, fy, top) &&
                        !world.IsClimbable(fx, fy, fz) && !world.IsDoor(fx, fy, fz);
        if (!embedded)
        {
            stuckSince = DateTime.MinValue;
            return;
        }

        if (stuckSince == DateTime.MinValue)
        {
            stuckSince = DateTime.UtcNow;
            OnLog?.Invoke($"врос в блок на Y={p.Y:0.##} — выбираюсь");
        }

        // Жмём вверх и в сторону, где над головой свободно: столкновения
        // игры сами вытолкнут тело наверх, как при обычном шаге на ступеньку
        p.Controls.Jump = true;
        foreach (var (dx, dz) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1)])
            if (world.IsPassable(fx + dx, fy + 1, fz + dz) &&
                world.IsPassable(fx + dx, fy + 2, fz + dz))
            {
                p.WalkToward(fx + dx + 0.5, fz + dz + 0.5, sprint: false);
                return;
            }
    }

    private DateTime stuckSince = DateTime.MinValue;
    private float lastHealth = float.MaxValue;

    /// <summary>
    /// Отброс от удара. Сервер присылает вектор в атрибутах игрока, но узнать
    /// «вот сейчас ударили» можно только по падению здоровья — по нему и
    /// поднимаем флаг для игрового PModuleKnockback. Без этого модуль работал
    /// впустую: данные до физического тела не доходили, и бота не откидывало.
    /// </summary>
    private void ApplyServerKnockback()
    {
        if (physics == null || entities.Self is not { } me)
            return;
        float hp = me.Health ?? float.MaxValue;
        if (hp < lastHealth - 0.01f)
        {
            var (kx, ky, kz) = me.KnockbackDir;
            if (kx != 0 || ky != 0 || kz != 0)
            {
                physics.ApplyKnockback(kx, ky, kz);
                OnLog?.Invoke($"удар отбросил ({kx:0.##}, {ky:0.##}, {kz:0.##})");
            }
        }
        lastHealth = hp;
    }

    private async Task SendPositionAsync()
    {
        if (physics is not { } p || entities.Self is not { } self)
            return;
        // Пока бот мёртв, позиция не уходит: живой клиент в это время
        // показывает экран смерти и ничего не шлёт
        if (IsDead?.Invoke() == true)
            return;

        // Голова доезжает до цели со скоростью шеи (4 тика между пакетами)
        float maxStep = (float)(HeadTurnSpeed * 4 * BotGamePhysics.GamePlayerPhysics.TickSeconds);
        headYaw += Math.Clamp(HeadYawTarget - headYaw, -maxStep, maxStep);

        // Модель мира должна знать, где мы: на неё смотрят способности и поиск пути
        self.SetPosition(p.X, p.Y, p.Z, p.Yaw);

        await bot.SendPacketAsync(new Packet_Client
        {
            Id = 35,
            UdpPacket = new Packet_UdpPacket
            {
                Id = 2,
                EntityPosition = new Packet_EntityPosition
                {
                    EntityId = self.Id,
                    X = CollectibleNet.SerializeDoublePrecise(p.X),
                    Y = CollectibleNet.SerializeDoublePrecise(p.Y),
                    Z = CollectibleNet.SerializeDoublePrecise(p.Z),
                    Yaw = CollectibleNet.SerializeFloatPrecise(p.Yaw),
                    // Наклон взгляда живого клиента всегда в [1.586, 4.697]
                    Pitch = CollectibleNet.SerializeFloatPrecise(
                        Math.Clamp(p.Pitch == 0 ? MathF.PI : p.Pitch, 1.5857964f, 4.697389f)),
                    Roll = CollectibleNet.SerializeFloatPrecise(0),
                    HeadYaw = CollectibleNet.SerializeFloatPrecise(headYaw),
                    HeadPitch = CollectibleNet.SerializeFloatPrecise(0),
                    BodyYaw = CollectibleNet.SerializeFloatPrecise(p.Yaw),
                    MotionX = CollectibleNet.SerializeDoublePrecise(p.Motion.X),
                    MotionY = CollectibleNet.SerializeDoublePrecise(p.Motion.Y),
                    MotionZ = CollectibleNet.SerializeDoublePrecise(p.Motion.Z),
                    PositionVersion = self.PositionVersion,
                    Tick = 0
                }
            }
        });
    }

    /// <summary>
    /// Сервер включает окружающим анимации ходьбы/бега/прыжка по клавишам,
    /// а не по позиции — шлём изменения, как настоящий клиент
    /// (Key = индекс EnumEntityAction: 0 вперёд, 4 прыжок, 5 присед, 6 бег).
    /// </summary>
    private async Task SendKeyDiffsAsync()
    {
        if (physics is not { } p)
            return;
        var c = p.Controls;
        (int Key, bool Down)[] states =
        [
            (0, c.Forward), (1, c.Backward), (2, c.Left), (3, c.Right),
            (4, c.Jump), (5, c.Sneak), (6, c.Sprint)
        ];
        foreach (var (key, down) in states)
        {
            if (sentKeys[key] == down)
                continue;
            sentKeys[key] = down;
            await bot.SendPacketAsync(new Packet_Client
            {
                Id = 21,
                MoveKeyChange = new Packet_MoveKeyChange { Key = key, Down = down ? 1 : 0 }
            });
        }
    }
}
