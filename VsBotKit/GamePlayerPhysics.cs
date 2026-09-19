using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

using WorldModel = VsBotKit.WorldModel;
using BodySize = VsBotKit.BodySize;
using JumpPhysics = VsBotKit.JumpPhysics;

namespace BotGamePhysics;

/// <summary>
/// Физика бота = физика игры. Здесь не считается ничего своего:
/// крутятся НАСТОЯЩИЕ модули из VintagestoryAPI.dll (ветер, земля, жидкость,
/// воздух, гравитация, сопротивление, отбрасывание), её же проверка
/// столкновений, а «драйвер» вокруг них — построчный перенос
/// EntityBehaviorControlledPhysics.ApplyTests из кода игры (сам класс
/// не поднять без клиентского API, но каждая формула в нём — игровая).
///
/// Почему это вообще нужно боту: позиция игрока клиент-авторитетна, сервер
/// её не считает. Бот и есть клиент, поэтому физику обязан крутить сам —
/// но ИГРОВУЮ: любая своя формула рано или поздно оказывается читом.
///
/// Вход — только клавиши и направление взгляда, как у живого игрока.
/// </summary>
public sealed class GamePlayerPhysics
{
    private readonly BotPhysicsWorld world;
    private readonly BotEntity entity;
    private readonly List<PModule> modules;
    private readonly CachingCollisionTester collision = new();

    /// <summary>Физический шаг игры.</summary>
    public const float TickSeconds = 1f / 60f;

    // --- Константы драйвера (EntityBehaviorControlledPhysics, дефолты) ---
    // Высота шага — НЕ своя копия игрового умолчания, а единственное число
    // проекта (BodySize.StepUp). В игре оно приходит из атрибута «stepHeight»
    // сущности, а у игрока такого атрибута нет вовсе — значит действует
    // умолчание 0.6f, и оно же записано в BodySize
    private const float StepHeight = (float)BodySize.StepUp;
    private const float StepUpSpeed = 0.07f;

    // ЛАЗАНЬЕ: ИМЕНА ПОЛЕЙ В ИГРЕ ПЕРЕПУТАНЫ, и это ловушка для читающего.
    // EntityBehaviorControlledPhysics (VSEssentials.dll 1.22.7):
    //   if (controls.Jump)  motion.Y = climbDownSpeed * dt * 60f;   // ВВЕРХ
    //   if (controls.Sneak) motion.Y = Math.Max(-climbUpSpeed,      // ВНИЗ
    //                                            motion.Y - climbUpSpeed);
    // То есть полем с именем «вниз» (0.035 за тик, 2,1 бл/с) лезут ВВЕРХ по
    // клавише прыжка, а полем с именем «вверх» (0.07 за тик, 4,2 бл/с)
    // ограничивают спуск на присяде. Имена оставлены игровыми нарочно: файл —
    // построчный перенос, и переименуй мы их, сверять с игрой стало бы нечем
    private const float ClimbUpSpeed = 0.07f;
    private const float ClimbDownSpeed = 0.035f;
    private const float CollisionYExtra = 1f;

    // --- Состояние между тиками (как поля бихевиора игры) ---
    private Vec3d newPos = new();
    private readonly Vec3d moveDelta = new();
    private readonly Vec3d steppingTestMotion = new();
    private readonly Vec3d steppingTestVec = new();

    /// <summary>
    /// Точка для проб «а если подняться на месте» — своя, чтобы отказ шага не
    /// сорил мусором в куче: <c>OffsetCopy</c> в этом месте выдавал бы дюжину
    /// объектов на тик застревания.
    /// </summary>
    private readonly Vec3d stepProbe = new();

    /// <summary>
    /// Клетка ступеньки, которую только что выбрал <see cref="FindSteppableCollisionBox"/>.
    /// Берётся у самой игры (<c>positions[i]</c> рядом с <c>cuboids[i]</c>) и
    /// живёт до следующего поиска — как и сама ступенька.
    /// </summary>
    private (int X, int Y, int Z)? steppableAt;
    private readonly Cuboidd entityBox = new();
    private readonly Cuboidd steppingCollisionBox = new();
    private readonly BlockPos tmpPos = new(0);
    private readonly Cuboidf sneakTestCollisionbox;

    /// <summary>Рабочая коробка вопроса «есть ли куда распрямиться» (tmpCollBox игры).</summary>
    private readonly Cuboidf standUpBox = new();
    private double prevYMotion;
    private double prevPrevYMotion;
    private bool onGroundBefore;
    private bool feetInLiquidBefore;
    private bool swimmingBefore;

    public GamePlayerPhysics(WorldModel worldModel)
    {
        world = new BotPhysicsWorld(worldModel);

        var props = new EntityProperties
        {
            Class = "EntityPlayer",
            Habitat = EnumHabitat.Land,
            // РАЗМЕР ТЕЛА — ИЗ ОДНОГО МЕСТА (<see cref="BodySize"/>), а не своей
            // парой чисел. Здесь поднимается САМА коробка, которой бот упирается
            // в мир; ею же меряет проходимость прямого отрезка спрямление
            // (BodyCorridor), ею же считают свес прыжка, пластину транслокатора
            // и «встал бы в меня самого» у добычи. Разойдись эта пара с BodySize
            // — и планировщик считал бы одно тело, а сервер двигал другое,
            // причём молча: ровно так спрямление и водило бота в косяк
            CollisionBoxSize = new Vec2f((float)BodySize.Width, (float)BodySize.Height),
            SelectionBoxSize = new Vec2f((float)BodySize.Width, (float)BodySize.Height),
            // ВЫСОТА ГЛАЗ НУЖНА САМОЙ ФИЗИКЕ, а не только картинке: присед
            // опускает глаза тем же множителем, что и рост (см. CrouchBox), а
            // из глаз выходит луч руки. Без этого поля игровая формула
            // приседа гнала бы глаза к нулю
            EyeHeight = BodySize.EyeHeight,
            CanClimb = true,
            ClimbTouchDistance = 0.5f,
            Client = new EntityClientProperties([], null),
            Server = new EntityServerProperties([], null),
            // Звуки не резолвятся без загрузчика ассетов — глушим, иначе
            // игровой код споткнётся, пытаясь проиграть прыжок или плеск
            Sounds = null
        };

        entity = new BotEntity(world, props);
        entity.WatchedAttributes.SetString("playerUID", "bot");

        // «Смотрю прямо» в игре — это Pitch = π (диапазон [1.586, 4.697]).
        // С нулём вектор взгляда разворачивается на 180°: cos(0) = +1 вместо
        // cos(π) = −1, и модуль плавания гребёт РОВНО НАЗАД
        entity.Pos.Pitch = MathF.PI;

        // Как в Initialize бихевиора: узкая половинная коробка для приседания
        sneakTestCollisionbox = entity.CollisionBox.Clone().OmniNotDownGrowBy(-0.1f);
        sneakTestCollisionbox.Y2 /= 2f;

        // Набор модулей — ровно как в EntityBehaviorPlayerPhysics.SetModules
        modules =
        [
            new PModuleWind(),
            new PModuleOnGround(),
            new PModulePlayerInLiquid(entity),
            new PModulePlayerInAir(),
            new PModuleGravity(),
            new PModuleMotionDrag(),
            new PModuleKnockback()
        ];
        foreach (var m in modules)
            m.Initialize(null, entity);
    }

    /// <summary>Клавиши и взгляд — единственный вход физики.</summary>
    public EntityControls Controls => entity.Controls;

    public double X => entity.Pos.X;
    public double Y => entity.Pos.Y;
    public double Z => entity.Pos.Z;
    /// <summary>
    /// Куда повёрнут корпус. ПРИСВОЕНИЕ ЗАДАЁТ ЦЕЛЬ, а не мгновенный угол:
    /// корпус доворачивается со скоростью BodyTurnSpeed. Мгновенный разворот
    /// (как было раньше) — то, чего живой игрок не может: мышь поворачивает
    /// быстро, но за конечное время.
    /// </summary>
    public float Yaw { get => entity.Pos.Yaw; set => YawTarget = value; }

    /// <summary>Куда корпус доворачивается.</summary>
    public float YawTarget { get; set; }

    /// <summary>
    /// Скорость поворота корпуса, радиан в секунду. 20 рад/с — это разворот
    /// на 180° за 0.16 с, быстрый рывок мышью. 0 — мгновенно (нечестно).
    /// </summary>
    public double BodyTurnSpeed { get; set; } = 20;

    /// <summary>Поставить угол корпуса мгновенно — только при появлении в мире.</summary>
    public void SnapYaw(float yaw)
    {
        entity.Pos.Yaw = yaw;
        YawTarget = yaw;
    }

    /// <summary>Насколько корпус ещё не довернулся до цели, радиан.</summary>
    public float YawError => Math.Abs(NormalizeAngle(YawTarget - entity.Pos.Yaw));

    private static float NormalizeAngle(float a)
    {
        while (a > MathF.PI) a -= 2 * MathF.PI;
        while (a < -MathF.PI) a += 2 * MathF.PI;
        return a;
    }

    /// <summary>Довернуть корпус за один тик (вызывается из Tick).</summary>
    private void StepBodyTurn()
    {
        float diff = NormalizeAngle(YawTarget - entity.Pos.Yaw);
        if (diff == 0)
            return;
        if (BodyTurnSpeed <= 0)
        {
            entity.Pos.Yaw = YawTarget;
            return;
        }
        float maxStep = (float)(BodyTurnSpeed * TickSeconds);
        entity.Pos.Yaw = NormalizeAngle(entity.Pos.Yaw + Math.Clamp(diff, -maxStep, maxStep));
    }
    public float Pitch { get => entity.Pos.Pitch; set => entity.Pos.Pitch = value; }

    public bool OnGround => entity.OnGround;
    public bool Swimming => entity.Swimming;
    public bool FeetInLiquid => entity.FeetInLiquid;
    public bool CollidedHorizontally => entity.CollidedHorizontally;
    public bool Climbing => entity.Controls.IsClimbing;

    /// <summary>
    /// КАКОГО РОСТА ТЕЛО ПРЯМО СЕЙЧАС, блоков. Стоя это
    /// <see cref="BodySize.Height"/>, в присяде — <see cref="BodySize.CrouchHeight"/>,
    /// а между ними тело ЕДЕТ около 0,7 с (см. <see cref="CrouchBox"/>).
    /// Спрашивают его затем, чтобы не шагать в щель, под которую тело ещё не
    /// присело: коробка тут единственный источник правды, а не намерение.
    /// </summary>
    public double BodyHeightNow => entity.CollisionBox.Y2;

    /// <summary>Держит ли тело присед прямо сейчас (свой шифт или зажатый игрой).</summary>
    public bool Crouching => entity.Controls.Sneak || !entity.PrevFrameCanStandUp;

    /// <summary>
    /// НА СКОЛЬКО НАДО БЫЛО ВЗОЙТИ, КОГДА ИГРОВАЯ ПРОБА ШАГА ОТКАЗАЛА ПО
    /// ПОТОЛКУ (блоков). Ноль — такого отказа в прошлом тике не было.
    ///
    /// ЭТО НЕ СВОЙ РАСЧЁТ, А ОТВЕТ САМОЙ ИГРЫ. <c>TryStep</c> (построчный
    /// перенос из <c>EntityBehaviorControlledPhysics</c>) поднимает тело на
    /// «верх ступеньки − нога + 0,03» и требует, чтобы на этой высоте тело
    /// НИ ВО ЧТО не упиралось. Здесь сохраняется ровно та высота, что игра
    /// сочла нужной, БЕЗ служебных трёх сотых: спрашивающему важно, на сколько
    /// подняться, а не сколько игра берёт про запас.
    ///
    /// ЖИВОЙ СЛУЧАЙ, РАДИ КОТОРОГО ЗАВЕДЕНО (стенд 26.08, дверной проём
    /// (62, 112, 252) и точно такой же в (512032, 110, 512010)). Щуп в теле:
    ///   ступенька верх 110,125 (нога 110); подъём на 0,155 НЕ ВЫШЕЛ:
    ///   голова тела 112,005 упирается в game:mudbrick-light
    /// Порог занесло слоем снега 0,125, а притолока проёма ровно в 2,0 над
    /// полом. Рост тела 1,85, и 0,125 + 0,03 + 1,85 = 2,005 — проба шага не
    /// проходит на ПЯТЬ ТЫСЯЧНЫХ блока. Шага нет — и снег гасит ось насмерть
    /// (<c>pushOutZ</c>), движение по ней РОВНО ноль, тик за тиком.
    ///
    /// Тело живого игрока стоит там же и так же, и выходит он оттуда прыжком:
    /// потолок подрезает прыжок на 0,15, а этого снегу хватает с запасом.
    /// Чтобы бот мог решить то же самое, ему нужен ИМЕННО ЭТОТ ответ игры —
    /// свой пересчёт «что там впереди» уже трижды расходился с телом.
    /// Решает по нему <c>Movement.CanHopObstacle</c> через чистое правило
    /// <c>JumpPhysics.HopWhenStepDenied</c>.
    ///
    /// СТАВИТСЯ ТОЛЬКО ПРИ ОТКАЗЕ ПО ПОТОЛКУ, и это поправка приёмки, а не
    /// придирка. Игровая проба двигает тело И ВВЕРХ, И ВПЕРЁД одним разом,
    /// поэтому её отказ сам по себе значит «что-то помешало», а не «не пустил
    /// потолок»: ровно так же он звучит, упёршись в СТЕНУ по курсу, у подножия
    /// которой лежит что угодно ниже шага (снег, плита, тропа). Прежняя запись
    /// стояла на любом отказе — и бот в комнате с просветом 2,0 получал
    /// «прыгать» перед глухой стеной и долбился в неё подскоками, а человек
    /// читал в журнале про потолок, которого там нет. Теперь потолок
    /// спрашивается отдельной пробой ВВЕРХ БЕЗ ХОДА (<see cref="NoteStepDenied"/>).
    /// </summary>
    public double StepDeniedOverhead => StepDenied?.Rise ?? 0;

    /// <summary>
    /// СКОЛЬКО НА САМОМ ДЕЛЕ НАД ГОЛОВОЙ, когда проба шага отказала по потолку
    /// (блоков). Ноль — отказа не было или мерить было нечего.
    ///
    /// МЕРИТ ИГРА, А НЕ МИРОВАЯ МОДЕЛЬ, и это тоже поправка приёмки. Прежде
    /// просвет считала ходьба: <c>WorldModel.ClearanceAt(клетка, где стою)</c>
    /// минус рост тела. Три беды разом: мерилась КЛЕТКА БОТА, а притолока могла
    /// стоять над клеткой препятствия; под открытым небом <c>ClearanceAt</c>
    /// отдаёт заглушку <c>BodySize.Height</c>, то есть просвет выходил РОВНО
    /// НОЛЬ и в журнал шла выдумка «над головой всего 0 бл»; а у пригнувшегося
    /// тела из той же заглушки бралось 0,575 просвета из ниоткуда.
    ///
    /// Здесь же тело поднимается НА МЕСТЕ пробами игрового
    /// <c>CollisionTester</c> и половинным делением находит ту высоту, на
    /// которой оно ещё ни во что не упирается. Это ответ самой игры про то
    /// самое тело в той самой точке — придумать тут нечего.
    /// </summary>
    public double StepDeniedHeadroom => StepDenied?.Headroom ?? 0;

    /// <summary>
    /// КЛЕТКА ТОЙ САМОЙ СТУПЕНЬКИ, на которую тело не пустил потолок; null —
    /// отказа не было.
    ///
    /// Позиция берётся у игрового списка коллизий (<c>positions[i]</c> рядом с
    /// <c>cuboids[i]</c>) — то есть это ТОТ ЖЕ блок, который игра сочла
    /// ступенькой. Ходьба свою клетку прежде УГАДЫВАЛА («полшага по курсу,
    /// округлить»), и угадывала мимо: тело шириной 0,6 задевает соседнюю
    /// клетку плечом, и в журнале стояло «шаг не вышел в (512017,104,512017)»
    /// при снеге в (512017,104,512016). По этой же угаданной клетке потом
    /// целился приём «смахнуть мелочь» — и первым ударом молотка становился пол.
    /// </summary>
    public (int X, int Y, int Z)? StepDeniedAt =>
        StepDenied is { } d ? (d.X, d.Y, d.Z) : null;

    /// <summary>
    /// ОТКАЗ ПРОБЫ ШАГА ЦЕЛИКОМ, ОДНИМ ОТВЕТОМ. null — отказа в прошлом тике
    /// не было.
    ///
    /// ПОЧЕМУ ОДНИМ, А НЕ ТРЕМЯ ПОЛЯМИ, — это найдено живым стендом, а не из
    /// осторожности. Физику крутит СВОЙ поток (<c>Body</c>), а спрашивает про
    /// отказ поток ходьбы; ответ живёт ровно один тик и в начале следующего
    /// стирается. Пока полей было три, ходьба успевала прочитать «подъём
    /// 0,125» от прошлого тика и «клетку» уже от нового, то есть null, — и
    /// честная клетка от игры подменялась догадкой «полшага по курсу». В
    /// снежном стенде это выглядело так: первый отказ (512017,104,512016) —
    /// снег, второй (512017,104,512017) — ВОЗДУХ, и приём «смахнуть мелочь»
    /// целился в него.
    ///
    /// Ссылка на запись меняется одним присваиванием, и читатель видит либо
    /// весь прошлый ответ, либо весь новый — но не половину одного и половину
    /// другого.
    /// </summary>
    public StepDenial? StepDenied => System.Threading.Volatile.Read(ref stepDenied);

    private StepDenial? stepDenied;

    /// <summary>Что именно сказала игровая проба шага, когда отказала.</summary>
    /// <param name="Rise">Высота ступеньки без служебных трёх сотых игры.</param>
    /// <param name="Headroom">Сколько тело на самом деле берёт подъёмом на месте.</param>
    public sealed record StepDenial(double Rise, double Headroom, int X, int Y, int Z);

    /// <summary>Скорость, блоков за тик (как в игре).</summary>
    public Vec3d Motion => entity.Pos.Motion;

    /// <summary>Сущность бота — для кода, говорящего на языке игры.</summary>
    public EntityPlayer Entity => entity;

    /// <summary>Стат скорости персонажа с сервера (класс, броня).</summary>
    public void SetWalkSpeedStat(float value) =>
        entity.Stats.Set("walkspeed", "fromserver", value - 1, persistent: false);

    /// <summary>Поставить тело в точку (вход в мир, телепорт сервера).</summary>
    /// <summary>
    /// Отброс от удара: кладём присланный сервером вектор туда, откуда его
    /// берёт игровой PModuleKnockback, и поднимаем его флаг. Считает отброс
    /// игра, мы только передаём данные — своей арифметики здесь нет.
    /// </summary>
    public void ApplyKnockback(double x, double y, double z)
    {
        entity.WatchedAttributes.SetDouble("kbdirX", x);
        entity.WatchedAttributes.SetDouble("kbdirY", y);
        entity.WatchedAttributes.SetDouble("kbdirZ", z);
        entity.Attributes.SetInt("dmgkb", 1);
    }

    public void Teleport(double x, double y, double z)
    {
        entity.Pos.SetPos(x, y, z);
        entity.Pos.Motion.Set(0, 0, 0);
        entity.PositionBeforeFalling.Set(x, y, z);
        if (entity.Pos.Pitch == 0)
            entity.Pos.Pitch = MathF.PI; // прямой взгляд, а не «в зенит»
    }

    /// <summary>
    /// Идти в сторону точки: только направление и клавиши, как у игрока.
    /// Позицию посчитает физика.
    /// </summary>
    public void WalkToward(double targetX, double targetZ, bool sprint)
    {
        double dx = targetX - entity.Pos.X, dz = targetZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        // Почти на месте: не дёргаем корпус. Иначе у цели крошечный вектор
        // до неё прыгает из-за дрожания позиции, и бот вертится влево-вправо
        if (dist < 0.15)
        {
            Controls.Forward = false;
            Controls.Sprint = false;
            return;
        }
        YawTarget = (float)Math.Atan2(dx / dist, dz / dist);
        Controls.Forward = true;
        Controls.Sprint = sprint;
    }

    /// <summary>
    /// Пятиться спиной, не отворачиваясь от точки (бой): корпус смотрит на
    /// faceX/faceZ, движение — клавишей «назад».
    /// </summary>
    public void BackpedalFrom(double faceX, double faceZ)
    {
        double dx = faceX - entity.Pos.X, dz = faceZ - entity.Pos.Z;
        double dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist >= 0.01)
            YawTarget = (float)Math.Atan2(dx / dist, dz / dist);
        Controls.Forward = false;
        Controls.Backward = true;
        Controls.Sprint = false;
    }

    /// <summary>Остановиться (отпустить клавиши движения).</summary>
    public void Stop()
    {
        Controls.Forward = false;
        Controls.Backward = false;
        Controls.Left = false;
        Controls.Right = false;
        Controls.Sprint = false;
        Controls.Jump = false;
        Controls.Sneak = false;
    }

    /// <summary>Нажать/отпустить прыжок (вверх по лестнице — тоже он).</summary>
    public void Jump(bool down = true) => Controls.Jump = down;

    /// <summary>Нажать/отпустить приседание (вниз по лестнице — тоже оно).</summary>
    public void Sneak(bool down = true) => Controls.Sneak = down;

    /// <summary>
    /// Один физический тик — перенос OnPhysicsTick + ApplyTests из игры:
    /// состояние → векторы движения из клавиш → модули → лазание →
    /// столкновения со ступенькой → флаги.
    /// </summary>
    public void Tick()
    {
        var pos = entity.Pos;
        var controls = entity.Controls;
        float dtFac = TickSeconds * 60f;

        // Корпус доворачивается к цели — мгновенных разворотов у игрока нет
        StepBodyTurn();

        // ПРИСЕД МЕНЯЕТ КОРОБКУ ТЕЛА — и делает это ДО столкновений, ровно как
        // в игре (EntityPlayer.OnGameTick зовёт updateEyeHeight перед
        // base.OnGameTick, а тот уже крутит физику)
        bool forcedSneak = CrouchBox(controls, TickSeconds);

        // SetState
        prevPrevYMotion = prevYMotion;
        prevYMotion = pos.Motion.Y;
        onGroundBefore = entity.OnGround;
        feetInLiquidBefore = entity.FeetInLiquid;
        swimmingBefore = entity.Swimming;

        // Жидкостные флаги нужны модулям ДО их применения
        UpdateLiquidState(pos);

        // MotionAndCollision: клавиши → векторы → модули
        controls.CalcMovementVectors(pos, TickSeconds);
        foreach (var m in modules)
            if (m.Applicable(entity, pos, controls))
                m.DoApply(TickSeconds, entity, pos, controls);

        // --- ApplyTests (ветка !remote), построчно ---
        var motion = pos.Motion;

        // Лазание: касается ли тело плиты лестницы (своя колонна + 4 соседних)
        controls.IsClimbing = false;
        entity.ClimbingOnFace = null;
        entity.ClimbingIntoFace = null;
        DetectClimbing(pos, controls);

        // Вертикаль на лестнице: прыжок — вверх, присед — вниз (имена
        // скоростей в игре перепутаны местами, скорости — нет)
        if (controls.IsClimbing && controls.WalkVector.Y == 0.0)
        {
            if (controls.Sneak)
                motion.Y = Math.Max(0f - ClimbUpSpeed, motion.Y - ClimbUpSpeed);
            if (controls.Jump)
                motion.Y = ClimbDownSpeed * dtFac;
        }

        double wantX = motion.X * dtFac + pos.X;
        double wantY = motion.Y * dtFac + pos.Y;
        double wantZ = motion.Z * dtFac + pos.Z;
        moveDelta.Set(motion.X * dtFac, prevYMotion * dtFac, motion.Z * dtFac);

        collision.NewTick(pos);
        collision.ApplyTerrainCollision(entity, pos, dtFac, ref newPos, 0f, CollisionYExtra);
        controls.IsStepping = HandleSteppingOnBlocks(pos, moveDelta, dtFac, controls);
        HandleSneaking(pos, controls, TickSeconds);

        int px = (int)pos.X, py = (int)pos.Y, pz = (int)pos.Z;

        // Упёрся в стену в глубокой воде — выталкиваемся наверх (как в игре)
        if (entity.CollidedHorizontally && !controls.IsClimbing && !controls.IsStepping)
        {
            var ba = world.BlockAccessor;
            if (ba.GetBlockRaw(px, (int)(pos.InternalY + 0.5), pz, 2).LiquidLevel >= 7 ||
                ba.GetBlockRaw(px, (int)pos.InternalY, pz, 2).LiquidLevel >= 7 ||
                ba.GetBlockRaw(px, (int)(pos.InternalY - 0.05), pz, 2).LiquidLevel >= 7)
            {
                motion.Y += 0.2 * TickSeconds;
                controls.IsStepping = true;
            }
            else
            {
                // Скольжение вдоль стены, чтобы не залипать в углах
                double ax = Math.Abs(motion.X), az = Math.Abs(motion.Z);
                if (ax > az)
                {
                    if (az < 0.001)
                        motion.Z += motion.Z < 0.0 ? -0.0025 : 0.0025;
                }
                else if (ax < 0.001)
                {
                    motion.X += motion.X < 0.0 ? -0.0025 : 0.0025;
                }
            }
        }

        // Непроходимые зоны мира (край карты)
        float halfW = entity.CollisionBox.Width / 2f;
        var acc = world.BlockAccessor;
        if (acc.IsNotTraversable((int)(wantX + halfW * Math.Sign(motion.X)), py, pz, pos.Dimension))
            newPos.X = pos.X;
        if (acc.IsNotTraversable(px, (int)wantY, pz, pos.Dimension))
            newPos.Y = pos.Y;
        if (acc.IsNotTraversable(px, py, (int)(wantZ + halfW * Math.Sign(motion.Z)), pos.Dimension))
            newPos.Z = pos.Z;

        pos.SetPos(newPos);

        // Упёрлись — гасим скорость по этой оси
        if ((wantX < newPos.X && motion.X < 0.0) || (wantX > newPos.X && motion.X > 0.0))
            motion.X = 0.0;
        if ((wantY < newPos.Y && motion.Y < 0.0) || (wantY > newPos.Y && motion.Y > 0.0))
            motion.Y = 0.0;
        if ((wantZ < newPos.Z && motion.Z < 0.0) || (wantZ > newPos.Z && motion.Z > 0.0))
            motion.Z = 0.0;

        // Флаги — как в конце ApplyTests
        bool falling = prevYMotion <= 0.0;
        UpdateLiquidState(pos);
        entity.OnGround = (entity.CollidedVertically && falling && !controls.IsClimbing) ||
                          controls.IsStepping;

        if (!falling || entity.OnGround || controls.IsClimbing)
            entity.PositionBeforeFalling.Set(pos.X, pos.Y, pos.Z);

        // ЗАЖАТЫЙ ИГРОЙ ШИФТ ОТПУСКАЕТСЯ В ТОМ ЖЕ ТИКЕ — построчно как в игре:
        //   servercontrols.Sneak = true; base.OnGameTick(dt); servercontrols.Sneak = false;
        // (EntityPlayer.OnGameTick). Иначе присед, включённый низким потолком,
        // остался бы висеть на теле после выхода из щели — и бот пополз бы
        // дальше втрое медленнее, не зная почему
        if (forcedSneak)
            controls.Sneak = false;
    }

    /// <summary>
    /// ПРИСЕД: КОРОБКА ТЕЛА И ГЛАЗА — ПОСТРОЧНЫЙ ПЕРЕНОС
    /// <c>EntityPlayer.updateEyeHeight</c> (VintagestoryAPI.dll 1.22.7).
    ///
    /// ЗАЧЕМ ЭТО БОТУ. Заказчик просил дословно: «научи видеть высоту
    /// препятствия, некоторые можно пройти в присяд». Само зрение — дело
    /// модели мира и правила (<c>WorldModel.ClearanceAt</c>,
    /// <c>Walker.PoseForClearance</c>), а вот ПРОЛЕЗТЬ телом можно было только
    /// здесь: коробка бота была намертво 0,6 × 1,85 в любой позе, и в щель
    /// 1,5 бл, куда живой игрок проходит пригнувшись, бот упирался лбом. Никакая
    /// политика этого не лечит — лечится только физика, и только игровой
    /// формулой.
    ///
    /// ЧТО ИМЕННО ДЕЛАЕТ ИГРА:
    /// <list type="number">
    /// <item>PrevFrameCanStandUp = !Sneak &amp;&amp; canStandUp();</item>
    /// <item>на присяде (или когда встать нельзя) рост и глаза × 0,8;</item>
    /// <item>коробка ДОЕЗЖАЕТ до этого роста, а не прыгает: шаг
    /// (want − Y2)·5·dt, то есть около 0,7 с на полный присед;</item>
    /// <item>если встать нельзя, игра САМА зажимает шифт на этот тик.</item>
    /// </list>
    /// Числа не свои — <see cref="BodySize.CrouchScale"/> и
    /// <see cref="BodySize.CrouchFitPerSecond"/>.
    /// </summary>
    /// <returns>Зажала ли игра шифт за бота (отпустить его надо в этом же тике).</returns>
    private bool CrouchBox(EntityControls controls, float dt)
    {
        entity.PrevFrameCanStandUp = !controls.Sneak && CanStandUp();

        double wantEye = entity.Properties.EyeHeight;
        double wantY2 = entity.Properties.CollisionBoxSize.Y;
        // FloorSitting (0,5 и 0,55) сюда не переносится нарочно: сидеть на полу
        // бот не умеет вовсе, а мёртвая ветка врала бы, что умеет
        if ((controls.Sneak || !entity.PrevFrameCanStandUp) &&
            !controls.IsClimbing && !controls.IsFlying)
        {
            wantEye *= BodySize.CrouchScale;
            wantY2 *= BodySize.CrouchScale;
        }

        entity.LocalEyePos.Y = Approach(entity.LocalEyePos.Y, wantEye, dt);
        float y2 = (float)Approach(entity.CollisionBox.Y2, wantY2, dt);
        entity.OriginSelectionBox.Y2 = entity.SelectionBox.Y2 = y2;
        entity.OriginCollisionBox.Y2 = entity.CollisionBox.Y2 = y2;

        // Игра зажимает шифт за игрока, когда распрямиться некуда, — и делает
        // это ТОЛЬКО если игрок его ещё не держит
        if (entity.PrevFrameCanStandUp || controls.Sneak)
            return false;
        controls.Sneak = true;
        return true;
    }

    /// <summary>
    /// Шаг «доехать до величины», как в игре: <c>d = (want − now)·5·dt</c>,
    /// и ни в коем случае не перелёт (Min/Max по знаку).
    /// </summary>
    private static double Approach(double now, double want, float dt)
    {
        double d = (want - now) * BodySize.CrouchFitPerSecond * dt;
        return d > 0 ? Math.Min(now + d, want) : Math.Max(now + d, want);
    }

    /// <summary>
    /// Есть ли куда распрямиться — построчный перенос <c>EntityPlayer.canStandUp</c>.
    ///
    /// Игра смотрит НЕ всю колонну, а ПОЯС от «ноги + 1» до полного роста: то,
    /// что мешает ниже пояса, и присевшему помешает так же, а «уже упёрся»
    /// (flag) отдельно разрешает распрямляться — иначе застрявшее в блоке тело
    /// навсегда осталось бы в присяде.
    /// </summary>
    private bool CanStandUp()
    {
        standUpBox.Set(entity.SelectionBox);
        var at = entity.Pos.XYZ;
        bool stuck = collision.IsColliding(world.BlockAccessor, standUpBox, at,
            alsoCheckTouch: false);
        standUpBox.Y2 = entity.Properties.CollisionBoxSize.Y;
        standUpBox.Y1 += 1f;
        return !collision.IsColliding(world.BlockAccessor, standUpBox, at,
            alsoCheckTouch: false) || stuck;
    }

    /// <summary>Сколько блоков падения накоплено (сервер по нему считает урон).</summary>
    public double FallDistance => Math.Max(0, entity.PositionBeforeFalling.Y - entity.Pos.Y);

    /// <summary>
    /// Детекция лазания — перенос из ApplyTests: тело касается плиты лестницы
    /// в своей колонне (клетка ног и выше) или в четырёх соседних.
    /// </summary>
    private void DetectClimbing(EntityPos pos, EntityControls controls)
    {
        var ba = world.BlockAccessor;
        int bodyCells = (int)Math.Ceiling(entity.CollisionBox.Y2);
        entityBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);

        tmpPos.Set((int)pos.X, 0, (int)pos.Z);
        for (int i = 0; i < bodyCells && !controls.IsClimbing; i++)
        {
            tmpPos.Y = (int)pos.Y + i;
            var block = ba.GetBlock(tmpPos, 1);
            if (!block.IsClimbable(tmpPos))
                continue;
            var boxes = block.GetCollisionBoxes(ba, tmpPos);
            if (boxes == null)
                continue;
            foreach (var box in boxes)
            {
                if (entityBox.ShortestDistanceFrom(box, tmpPos) <
                    entity.Properties.ClimbTouchDistance)
                {
                    controls.IsClimbing = true;
                    entity.ClimbingOnFace = null;
                    break;
                }
            }
        }

        if (controls.IsClimbing || controls.IsStepping)
            return;

        // Соседние клетки: цепляемся за лестницу сбоку. ВАЖНО: смещения
        // IterateHorizontalOffsets НАКАПЛИВАЮТСЯ (N, E, S, W по кругу) —
        // позицию между итерациями сбрасывать нельзя, иначе обход уходит
        // по диагоналям
        float touch = entity.Properties.ClimbTouchDistance;
        int feetY = (int)pos.Y;
        tmpPos.Set((int)pos.X, feetY, (int)pos.Z);
        for (int face = 0; face < 4; face++)
        {
            tmpPos.IterateHorizontalOffsets(face);
            for (int i = 0; i < bodyCells; i++)
            {
                tmpPos.Y = feetY + i;
                var block = ba.GetBlock(tmpPos, 1);
                if (!block.IsClimbable(tmpPos))
                    continue;
                var boxes = block.GetCollisionBoxes(ba, tmpPos);
                if (boxes == null)
                    continue;
                for (int b = 0; b < boxes.Length; b++)
                {
                    double dist = entityBox.ShortestDistanceFrom(boxes[b], tmpPos);
                    bool reachable = boxes[b].Y2 > 0.375f + pos.Y - (int)pos.Y || !entity.OnGround;
                    if (reachable && dist < touch)
                    {
                        controls.IsClimbing = true;
                        entity.ClimbingOnFace = BlockFacing.HORIZONTALS[face];
                        entity.ClimbingOnCollBox = boxes[b];
                        return;
                    }
                }
            }
        }
    }

    /// <summary>Шаг на ступеньку — перенос HandleSteppingOnBlocks из игры.</summary>
    private bool HandleSteppingOnBlocks(EntityPos pos, Vec3d delta, float dtFac, EntityControls controls)
    {
        // Ответ пробы шага живёт ровно один тик: он про ЭТОТ шаг, а не про
        // прошлый (см. <see cref="StepDeniedOverhead"/>)
        System.Threading.Volatile.Write(ref stepDenied, null);
        if (controls.WalkVector.X == 0.0 && controls.WalkVector.Z == 0.0)
            return false;
        if (!entity.OnGround && !entity.Swimming)
            return false;

        steppingCollisionBox.SetAndTranslate(entity.CollisionBox, pos.X, pos.Y, pos.Z);
        steppingCollisionBox.Y2 = Math.Max(steppingCollisionBox.Y1 + StepHeight, steppingCollisionBox.Y2);

        var walkVector = controls.WalkVector;
        var steppable = FindSteppableCollisionBox(steppingCollisionBox, delta.Y, walkVector);
        if (steppable == null)
            return false;

        var testMotion = steppingTestMotion;
        testMotion.Set(delta.X, delta.Y, delta.Z);
        if (TryStep(pos, testMotion, dtFac, steppable, steppingCollisionBox))
            return true;

        var testVec = steppingTestVec;
        testMotion.Z = 0.0;
        if (TryStep(pos, testMotion, dtFac,
                FindSteppableCollisionBox(steppingCollisionBox, delta.Y, testVec.Set(walkVector.X, walkVector.Y, 0.0)),
                steppingCollisionBox))
            return true;

        testMotion.Set(0.0, delta.Y, delta.Z);
        return TryStep(pos, testMotion, dtFac,
            FindSteppableCollisionBox(steppingCollisionBox, delta.Y, testVec.Set(0.0, walkVector.Y, walkVector.Z)),
            steppingCollisionBox);
    }

    // Подходящей ступеньки может не быть — это норма, а не ошибка: игра
    // проверяет три направления и берёт первое, где есть на что взойти
    private bool TryStep(EntityPos pos, Vec3d delta, float dtFac, Cuboidd? steppableBox, Cuboidd box)
    {
        if (steppableBox == null)
            return false;
        double lift = steppableBox.Y2 - box.Y1 + 0.03;
        var test = newPos.OffsetCopy(delta.X, lift, delta.Z);
        if (collision.IsColliding(world.BlockAccessor, entity.CollisionBox, test, alsoCheckTouch: false))
        {
            // ОТКАЗ ПРОБЫ ШАГА — НЕ МОЛЧАНИЕ, но и не готовый ответ: кто именно
            // помешал, проба не говорит. Разбирается это отдельно и пробами той
            // же игры (см. <see cref="NoteStepDenied"/>)
            NoteStepDenied(steppableBox.Y2 - box.Y1, lift);
            return false;
        }
        pos.Y += StepUpSpeed * dtFac;
        collision.ApplyTerrainCollision(entity, pos, dtFac, ref newPos, 1f, 1f);
        return true;
    }

    /// <summary>
    /// РАЗОБРАТЬ ОТКАЗ ПРОБЫ ШАГА: потолок это или стена по курсу, и сколько
    /// над головой на самом деле.
    ///
    /// Обе пробы — игровые (<c>CollisionTester.IsColliding</c> с той же
    /// коробкой того же тела), своего расчёта тут нет ни одного:
    /// <list type="number">
    /// <item>ПОДНЯТЬСЯ НА МЕСТЕ, БЕЗ ХОДА. Упёрлись — мешает то, что НАД
    /// головой, и разговор про прыжок имеет смысл. Не упёрлись — мешает то,
    /// что ВПЕРЕДИ, и прыгать на стену незачем: молчим, и клеточное правило
    /// ходьбы разбирается само;</item>
    /// <item>ПОЛОВИННЫМ ДЕЛЕНИЕМ — сколько подъёма тело всё-таки берёт.
    /// Двенадцать делений отрезка «0…подъём игры» дают сотые доли миллиметра,
    /// и стоят они дюжины проб в тот единственный тик, когда шаг отказал.</item>
    /// </list>
    /// </summary>
    /// <param name="rise">Высота ступеньки без служебных трёх сотых игры.</param>
    /// <param name="lift">Подъём, которого просила проба шага (ступенька + 0,03).</param>
    private void NoteStepDenied(double rise, double lift)
    {
        // ВЫШЕ БЕРЁМ ТУ СТУПЕНЬКУ, ЧТО ВЫШЕ. Проба шага за тик зовётся до трёх
        // раз (по обеим осям и по каждой отдельно), и решать надо по самой
        // трудной: перепрыгнуть её — значит перепрыгнуть и остальные
        if (rise <= StepDeniedOverhead)
            return;

        stepProbe.Set(newPos.X, newPos.Y + lift, newPos.Z);
        if (!collision.IsColliding(world.BlockAccessor, entity.CollisionBox, stepProbe,
                alsoCheckTouch: false))
            return;

        double free = 0, blocked = lift;
        for (int i = 0; i < 12; i++)
        {
            double mid = (free + blocked) / 2.0;
            stepProbe.Set(newPos.X, newPos.Y + mid, newPos.Z);
            if (collision.IsColliding(world.BlockAccessor, entity.CollisionBox, stepProbe,
                    alsoCheckTouch: false))
                blocked = mid;
            else
                free = mid;
        }

        // ЦЕЛИКОМ И ОДНИМ ПРИСВАИВАНИЕМ — см. StepDenied. Клетку берём у игры;
        // не назвала (такого быть не должно, но молча врать нельзя) — записи
        // не делаем вовсе, и ходьба честно разберётся клеточным правилом
        if (steppableAt is not { } где)
            return;
        System.Threading.Volatile.Write(ref stepDenied,
            new StepDenial(rise, free, где.X, где.Y, где.Z));
    }

    private Cuboidd? FindSteppableCollisionBox(Cuboidd box, double motionY, Vec3d walkVector)
    {
        Cuboidd? best = null;
        steppableAt = null;
        var list = collision.CollisionBoxList;
        int count = list.Count;
        var bp = new BlockPos(entity.Pos.Dimension);
        for (int i = 0; i < count; i++)
        {
            var block = list.blocks[i];
            // На слишком высокие «неступабельные» блоки не наступаем.
            //
            // ПРОВЕРЯЕМ ДЛИНУ, А НЕ ТОЛЬКО null. В самой игре здесь стоит
            // «!= null» и этого хватает: у блока из реестра массив боксов либо
            // отсутствует, либо непуст. У нас появился третий случай — тёсаный
            // блок, стёсанный ДОЧИСТА: его боксы приходят из блок-сущности, и
            // законный ответ «коробок нет» — пустой массив. Обращение к [0]
            // уронило бы физику целиком, то есть бота
            if (block.CollisionBoxes is { Length: > 0 } own && !block.CanStep &&
                entity.CollisionBox.Height < 5f * own[0].Height)
                continue;

            bp.Set(list.positions[i]);
            if (!block.SideIsSolid(bp, 4) && !block.SideIsSolid(bp, 5))
            {
                bp.Down();
                var below = world.BlockAccessor.GetMostSolidBlock(bp);
                bp.Up();
                if (below.CollisionBoxes is { Length: > 0 } under && !below.CanStep &&
                    entity.CollisionBox.Height < 5f * under[0].Height)
                    continue;
            }

            var cuboid = list.cuboids[i];
            var intersect = CollisionTester.AabbIntersect(cuboid, box, walkVector);
            if (intersect == EnumIntersect.NoIntersect)
                continue;
            if ((intersect == EnumIntersect.Stuck && !block.AllowStepWhenStuck) ||
                (intersect == EnumIntersect.IntersectY && motionY > 0.0))
                return null;

            double heightDiff = cuboid.Y2 - box.Y1;
            if (heightDiff <= 0.0 || heightDiff > StepHeight)
                continue;
            if (best == null || best.Y2 < cuboid.Y2)
            {
                best = cuboid;
                // ЧЬЯ ЭТО СТУПЕНЬКА — берём у игры, рядом с самой коробкой.
                // Своего пересчёта «что там впереди» тут быть не должно: он
                // уже трижды расходился с телом (см. StepDeniedAt)
                var at = list.positions[i];
                steppableAt = (at.X, at.Y, at.Z);
            }
        }
        return best;
    }

    /// <summary>Присев у края, игрок не падает — перенос HandleSneaking.</summary>
    private void HandleSneaking(EntityPos pos, EntityControls controls, float dt)
    {
        if (!controls.Sneak || !entity.OnGround || pos.Motion.Y > 0.0)
            return;

        var probe = new Vec3d(pos.X, pos.InternalY - Vintagestory.API.Config.GlobalConstants.GravityPerSecond * dt, pos.Z);
        if (!collision.IsColliding(world.BlockAccessor, sneakTestCollisionbox, probe, alsoCheckTouch: true))
            return;

        tmpPos.Set((int)pos.X, (int)pos.Y - 1, (int)pos.Z);
        var below = world.BlockAccessor.GetBlock(tmpPos);

        // Над лестницей игра НЕ держит намертво, а лишь гасит шаг до 1/10:
        // так игрок сходит с торца в колонну, не срываясь. Без этой ветки
        // присевший бот замирал на верху лестницы и не мог спуститься
        probe.Set(newPos.X, newPos.Y - Vintagestory.API.Config.GlobalConstants.GravityPerSecond * dt, pos.Z);
        if (!collision.IsColliding(world.BlockAccessor, sneakTestCollisionbox, probe, alsoCheckTouch: true))
        {
            if (below.IsClimbable(tmpPos))
                newPos.X += (pos.X - newPos.X) / 10.0;
            else
                newPos.X = pos.X;
        }

        probe.Set(pos.X, newPos.Y - Vintagestory.API.Config.GlobalConstants.GravityPerSecond * dt, newPos.Z);
        if (!collision.IsColliding(world.BlockAccessor, sneakTestCollisionbox, probe, alsoCheckTouch: true))
        {
            if (below.IsClimbable(tmpPos))
                newPos.Z += (pos.Z - newPos.Z) / 10.0;
            else
                newPos.Z = pos.Z;
        }
    }

    /// <summary>
    /// Флаги жидкости — как их считает игра: «плыву» по блоку воды на высоте
    /// двух третей роста, «ноги в воде» по уровню заполнения нижнего блока.
    /// </summary>
    private void UpdateLiquidState(EntityPos pos)
    {
        var ba = world.BlockAccessor;
        int x = (int)pos.X, y = (int)pos.Y, z = (int)pos.Z;
        int swimY = (int)(pos.Y + entity.SwimmingOffsetY);

        var feet = ba.GetBlockRaw(x, y, z, 2);
        var atSwim = swimY == y ? feet : ba.GetBlockRaw(x, swimY, z, 2);
        entity.Swimming = atSwim.IsLiquid();

        if (feet.IsLiquid())
        {
            var above = ba.GetBlockRaw(x, y + 1, z, 2);
            // Правило ОДНО на весь проект (закон 2): по нему же решает промывка
            // лотком, у которой была своя копия — и копия другая
            entity.FeetInLiquid = JumpPhysics.FeetInLiquid(
                pos.Y, feet.LiquidLevel / 8.0, above.LiquidLevel > 0);
            entity.InLava = feet.LiquidCode == "lava";
        }
        else
        {
            entity.FeetInLiquid = false;
            entity.InLava = false;
        }
    }
}
