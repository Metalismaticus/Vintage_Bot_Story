namespace VsBotKit;

/// <summary>
/// Почему транслокатором нельзя воспользоваться прямо сейчас. Причины РАЗНЫЕ,
/// и лечатся они разным: сломанный чинят шестерёнками, спящий ждёт сервера,
/// безадресный не вылечить вовсе.
/// </summary>
public enum TranslocatorTrouble
{
    /// <summary>Готов принять: починен, включён и знает адрес.</summary>
    Ready,

    /// <summary>Блок-сущности у нас нет: чанк не приезжал или блок сломан/снят.</summary>
    Unseen,

    /// <summary>Не хватает починок (repairState &lt; 4).</summary>
    Broken,

    /// <summary>Сервер держит его выключенным: canTele = false.</summary>
    NotPowered,

    /// <summary>Включён, но адреса не назвал: tpLocation пуст.</summary>
    NoAddress,
}

/// <summary>
/// ВХОД В ТРАНСЛОКАТОР — ПРАВИЛА САМОЙ ИГРЫ, а не наши догадки.
///
/// Всё ниже выписано из сборок 1.22.6 (ilspycmd):
/// <c>BlockEntityStaticTranslocator</c>, <c>BlockEntityTeleporterBase</c>,
/// <c>BlockStaticTranslocator</c>, <c>CollisionTester</c>,
/// <c>EntityBehaviorControlledPhysics</c>, <c>ServerUdpNetwork</c> и
/// <c>assets/survival/blocktypes/machine/statictranslocator.json</c>.
///
/// КАК ОН РАБОТАЕТ НА САМОМ ДЕЛЕ (и чем это отличается от «нажать кнопку»):
///
/// 1. НАЖИМАТЬ НЕЧЕГО. <c>BlockStaticTranslocator.OnBlockInteractStart</c>
///    отвечает только на ремонт (2 металлические детали, потом временные
///    шестерёнки). Готовый транслокатор на клик не реагирует вовсе — перенос
///    делает столкновение, а не рука.
///
/// 2. СТОЛКНОВЕНИЕ ЗАВОДИТ ОЧЕРЕДЬ. <c>CollisionTester.ApplyTerrainCollision</c>
///    зовёт <c>Block.OnEntityCollide</c> ТОЛЬКО когда коробка тела реально
///    выталкивается из коробки блока (<c>Cuboidd.pushOutY</c> с ненулевым
///    движением). Блок передаёт это блок-сущности, и та кладёт нас в
///    <c>tpingEntities</c>.
///
///    И вот главное, чего не видно снаружи: НА СЕРВЕРЕ ЭТО СЧИТАЕТСЯ ПО НАШИМ
///    ЖЕ ПАКЕТАМ ПОЗИЦИИ. <c>ServerUdpNetwork.HandlePlayerPosition</c> →
///    <c>EntityBehaviorPlayerPhysics.OnReceivedClientPos</c> →
///    <c>HandleRemotePhysics</c>: сервер берёт нашу новую позицию, сам
///    добавляет к ней шаг гравитации (<c>RemoteMotionAndCollision</c>) и
///    сталкивает тело с миром. Поэтому бот, который перестал слать позицию,
///    для транслокатора просто исчезает — сколько бы он на нём ни стоял.
///
/// 3. ГДЕ ИМЕННО СТОЯТЬ. Коробка блока — <c>0…1 × 0…0.125 × 0…1</c> (пластина
///    в одну восьмую блока). <c>pushOutY</c> при падении требует
///    <c>ноги &gt;= верха пластины</c>: провалившись В пластину, столкновения
///    не получишь. Коробка игрока 0.6 в ширину, поэтому от центра клетки можно
///    отойти на 0.2 — дальше коробка вылезает в соседний столбец, и проверка
///    «а я всё ещё на транслокаторе?»
///    (<c>CollisionTester.GetCollidingBlock</c> в
///    <c>BlockEntityTeleporterBase.HandleTeleportingServer</c>) может первым
///    найти соседний блок и выкинуть нас из очереди.
///
/// 4. СКОЛЬКО ЖДАТЬ. <c>TeleportWarmupSec = 4.4</c> с, счётчик растёт на
///    тике блок-сущности (250 мс), то есть перенос случается на 18-м тике —
///    примерно через 4,5 с непрерывного стояния. Прерывание больше 100 мс без
///    столкновения проверяется отдельно и прощается, ЕСЛИ коробка всё ещё
///    касается пластины.
///
/// 5. ЧТО ПРИХОДИТ КЛИЕНТУ. Три разные весточки, все от сервера:
///    • пока стоим — блок-сущность метится грязной с
///      <c>somebodyIsTeleporting = true</c> и приезжает нам пакетом 48;
///    • в миг переноса — <c>EntityPlayer.Onplrteleported</c> шлёт новую позицию
///      и поднимает <c>positionVersionNumber</c>;
///    • и лично нам — пустое сообщение <c>DidTeleport</c> по каналу
///      «tpManager» (см. <see cref="TranslocatorWatch"/>).
///
/// 6. КУДА ВЫБРАСЫВАЕТ. <c>GetTarget</c> отдаёт
///    <c>tpLocation + (−0.3, +1.0, −0.3)</c>, а если <c>tpLocationIsOffset</c>,
///    то к этому прибавляется позиция САМОГО блока. Смещение −0.3 не украшение:
///    оно ставит нас ВПЛОТНУЮ К дальней пластине, но не НА неё — иначе через
///    4,4 с нас отправило бы обратно.
///
/// ЧЕГО МЫ ЗНАТЬ НЕ МОЖЕМ — честно, а не «наверное»:
///
/// • <c>RepairInteractionsRequired</c> сервер НЕ шлёт и даже не сохраняет
///   (его нет в <c>ToTreeAttributes</c>). Обычно это 4, и после любой
///   перезагрузки мира ровно 4; но игрок с надбавкой
///   <c>temporalGearTLRepairCost</c> может в своей сессии поднять требование
///   выше. Значит наш ответ «починен» по <c>repairState &gt;= 4</c> —
///   это ЛУЧШЕЕ, что можно сказать, а не истина в последней инстанции.
///
/// • Состояние ДАЛЬНЕГО транслокатора нам не видно, пока его чанк не приехал.
///   Но это и не нужно: связывая пару, сервер сам чинит и включает дальний
///   конец (<c>exitChunkLoaded</c>), и перенос проверяет состояние только
///   ближнего.
///
/// • «Стоит ли кто-то внутри» — флаг ОБЩИЙ (<c>somebodyIsTeleporting</c>):
///   его поднимает любое существо на пластине, хоть курица. Рядом с одиноким
///   ботом это надёжный признак «сервер меня видит», в толпе — нет.
/// </summary>
public static class TranslocatorRules
{
    /// <summary>
    /// Сколько секунд непрерывного стояния требует игра
    /// (<c>BlockEntityStaticTranslocator.TeleportWarmupSec = 4.4f</c>).
    ///
    /// Число НЕ ПОВТОРЕНО, а взято у
    /// <see cref="TranslocatorNetRules.TeleportWarmupSeconds"/>: оно одно и то
    /// же и для оценки маршрута, и для ожидания на пластине, а два списания
    /// одного числа расходятся ровно тогда, когда игра его поменяет.
    /// </summary>
    public const double WarmupSeconds = TranslocatorNetRules.TeleportWarmupSeconds;

    /// <summary>Шаг тика блок-сущности на сервере, мс (RegisterGameTickListener(…, 250)).</summary>
    public const int ServerTickMs = 250;

    /// <summary>
    /// Сколько ждать НА САМОМ ДЕЛЕ: счётчик растёт целыми тиками, поэтому
    /// 4,4 с превращаются в 4,5 (18 тиков по 250 мс).
    /// </summary>
    public static double RealWarmupSeconds =>
        Math.Ceiling(WarmupSeconds / (ServerTickMs / 1000.0)) * (ServerTickMs / 1000.0);

    /// <summary>Верх коробки транслокатора: пластина в 1/8 блока.</summary>
    public const double PlateTop = 0.125;

    /// <summary>
    /// Половина ширины коробки игрока (hitboxSize.x = 0.6) — берётся из
    /// <see cref="BodySize.HalfWidth"/>, чтобы ширина тела в проекте была
    /// одна: ею меряют и попадание на пластину, и проход по прямой.
    /// </summary>
    public const double PlayerHalfWidth = BodySize.HalfWidth;

    /// <summary>
    /// На сколько можно отойти от центра клетки и всё ещё держать коробку
    /// целиком в столбце пластины. 0.5 − 0.3 = 0.2.
    /// </summary>
    public const double CenterSlack = 0.5 - PlayerHalfWidth;

    /// <summary>Сколько починок требует ванильный транслокатор (RepairInteractionsRequired).</summary>
    public const int RepairInteractionsRequired = 4;

    /// <summary>
    /// Насколько выше пластины тело ещё «поймается» одним шагом серверной
    /// гравитации: 1/60 × 4 (dtFactor при 15 пакетах в секунду) × 4 = 0,267.
    /// Выше — просто падаем, столкновение будет следующим пакетом.
    ///
    /// ЭТО И ЕСТЬ ВЕРХНИЙ ПОРОГ «стою на пластине» (см. <see cref="OnPlate"/>),
    /// а не отдельное число для справки. Пока их было двое, жило НЕ ТО: у
    /// OnPlate стояло необъяснённое 0,05, то есть впятеро строже выведенного
    /// здесь, — и бот, пойманный шагом гравитации в одной десятой над
    /// пластиной, читался как сошедший с неё. Рабочий транслокатор получал за
    /// это отказ «на пластину встать не вышло».
    /// </summary>
    public const double CatchHeightBlocks = 0.267;

    /// <summary>
    /// Сколько блоков считать «меня унесло с пластины». Бот стоит на месте,
    /// поэтому четыре блока — это уже не походка, а чужая воля (перенос,
    /// возрождение, отброс ударом).
    ///
    /// ВЫШЕ ЭТОГО ЧИСЛА ПОДНИМАТЬ НЕЛЬЗЯ, и вот почему: это ПЕРВЫЙ признак
    /// «меня перенесло» в <see cref="Translocating.EnterAsync"/>. Короткая пара
    /// транслокаторов (выход ближе порога) при завышенном пороге не опознается
    /// вовсе — бот отстоит всё терпение на уже покинутой клетке и доложит
    /// неудачу после удавшегося прыжка. Порог сторожится тестом
    /// «Четыре_блока_в_сторону_это_уже_не_походка_а_чужая_воля».
    /// </summary>
    public const double MovedAwayBlocks = 4.0;

    /// <summary>
    /// Сколько ждать после того, как нас унесло, прежде чем спрашивать «куда».
    ///
    /// ПРИЧИНА В ГОНКЕ, А НЕ В ОСТОРОЖНОСТИ. Своё положение у бота
    /// клиент-авторитетно: тело шлёт его серверу каждый четвёртый тик и тем же
    /// движением пишет в модель мира. Пришедший от сервера перенос модель
    /// принимает сразу, а тело догоняет следующим тиком
    /// (<see cref="Body"/>.SyncFromServer) — и между этими двумя мигами
    /// прочитанная позиция может оказаться ещё старой.
    ///
    /// Число живёт ЗДЕСЬ, а не у шва, потому что из него считается запас
    /// проверки прибытия (<see cref="ArrivalSlackBlocks"/>): два списания
    /// одного срока разошлись бы ровно тогда, когда его поменяют.
    /// </summary>
    public const double LandingSettleSeconds = 0.5;

    /// <summary>
    /// ЗАПАС ПРОВЕРКИ «ТУДА ЛИ ПЕРЕНЕСЛО», блоков — и он ВЫВЕДЕН, а не выбран.
    ///
    /// Сервер ставит нас в точку выхода ТОЧНО (<see cref="LandingPoint"/>), так
    /// что по-хорошему расхождению взяться неоткуда. Берётся оно из одного
    /// места: между «нас унесло» и чтением позиции проходит
    /// <see cref="LandingSettleSeconds"/>, тело на это время отпущено, и
    /// перехвативший его рефлекс успевает пройти
    /// <see cref="Movement.BaseWalkSpeed"/> × 0,5 ≈ 1,7 блока. Вот на это и
    /// запас — ни блоком больше.
    ///
    /// ПОЧЕМУ ЭТО ВАЖНО ДЕРЖАТЬ УЗКИМ. По этой проверке — и только по ней —
    /// шаг маршрута считается удавшимся. Раздайся запас до сотен блоков, и бот
    /// доложил бы «перенёсся к {exit}», высадившись за полкарты от обещанного,
    /// а маршрут пошёл бы дальше от чужой точки.
    /// </summary>
    public static double ArrivalSlackBlocks => Movement.BaseWalkSpeed * LandingSettleSeconds;

    /// <summary>Куда встать ногами: центр клетки, верх пластины.</summary>
    public static (double X, double Y, double Z) StandPoint(BlockPos plate) =>
        (plate.X + 0.5, plate.Y + PlateTop, plate.Z + 0.5);

    /// <summary>
    /// Стоим ли мы так, как этого требует сервер: ноги НЕ НИЖЕ верха пластины
    /// и коробка целиком в её столбце.
    ///
    /// Оба условия взяты из игры, а не придуманы: первое — из
    /// <c>Cuboidd.pushOutY</c> (<c>from.Y1 &gt;= Y2</c>, провалившись в
    /// пластину, столкновения не получишь), второе — из
    /// <c>CollisionTester.GetCollidingBlock</c>, который перебирает столбцы
    /// от меньшего к большему и первым же найденным чужим блоком выкидывает
    /// нас из очереди на перенос.
    ///
    /// ВЕРХНИЙ ПОРОГ — <see cref="CatchHeightBlocks"/>, шаг серверной
    /// гравитации: выше него тело сервер за этот пакет до пластины не опустит,
    /// значит мы падаем, а не стоим. Своего необъяснённого числа здесь больше
    /// нет — их было двое на одну величину, и они расходились впятеро.
    /// </summary>
    public static bool OnPlate(BlockPos plate, double x, double feetY, double z,
        double slack = 0.001)
    {
        double top = plate.Y + PlateTop;
        if (feetY < top - slack || feetY > top + CatchHeightBlocks + slack)
            return false;
        return x - PlayerHalfWidth >= plate.X - slack &&
               x + PlayerHalfWidth <= plate.X + 1 + slack &&
               z - PlayerHalfWidth >= plate.Z - slack &&
               z + PlayerHalfWidth <= plate.Z + 1 + slack;
    }

    /// <summary>
    /// АДРЕС ВЫХОДА так, как его читает сама игра
    /// (<c>BlockEntityStaticTranslocator.FromTreeAttributes</c>):
    /// teleX/Y/Z берутся ТОЛЬКО при canTele, а (0, y, 0) означает «адреса нет».
    /// Второе не мелочь: сервер пишет teleX в дерево при любом непустом
    /// tpLocation, и без этой оговорки бот принял бы за адрес пустое место.
    /// </summary>
    public static BlockPos? ExitBlock(BlockPos plate, bool canTeleport, bool isOffset,
        bool hasAddress, int teleX, int teleY, int teleZ)
    {
        if (!canTeleport || !hasAddress)
            return null;
        if (teleX == 0 && teleZ == 0)
            return null;
        return isOffset
            ? new BlockPos(plate.X + teleX, plate.Y + teleY, plate.Z + teleZ)
            : new BlockPos(teleX, teleY, teleZ);
    }

    /// <summary>
    /// КУДА ИМЕННО ВЫБРОСИТ — точка, которую сервер подставит нам в позицию:
    /// <c>tpLocation + (−0.3, +1.0, −0.3)</c> (BlockEntityStaticTranslocator.GetTarget),
    /// плюс позиция самого блока, если адрес записан смещением
    /// (BlockEntityTeleporterBase.HandleTeleportingServer).
    ///
    /// Смещение −0.3 ставит нас ВПЛОТНУЮ к дальней пластине, но не на неё:
    /// коробка игрока при этом кончается ровно на её грани, а
    /// <c>pushOutY</c> требует пересечения строгого, не касания. Так игра и
    /// спасает от вечного пинг-понга туда-обратно.
    /// </summary>
    public static (double X, double Y, double Z) LandingPoint(BlockPos exit) =>
        (exit.X - 0.3, exit.Y + 1.0, exit.Z - 0.3);

    /// <summary>Что мешает воспользоваться этим транслокатором.</summary>
    public static TranslocatorTrouble Trouble(TranslocatorInfo? tl)
    {
        if (tl == null)
            return TranslocatorTrouble.Unseen;
        if (tl.RepairState < RepairInteractionsRequired)
            return TranslocatorTrouble.Broken;
        if (!tl.CanTeleport)
            return TranslocatorTrouble.NotPowered;
        if (tl.Destination == null)
            return TranslocatorTrouble.NoAddress;
        return TranslocatorTrouble.Ready;
    }

    /// <summary>
    /// ОТКАЗ СЛОВАМИ — свой на каждую причину. Одинаковое «не работает» на все
    /// четыре беды человеку бесполезно: чинить, ждать и искать другой — разные
    /// дела.
    /// </summary>
    public static string Refusal(TranslocatorTrouble trouble, BlockPos where, TranslocatorInfo? tl) =>
        trouble switch
        {
            TranslocatorTrouble.Unseen =>
                $"транслокатора в {where} не вижу: блок-сущность мне не приезжала " +
                "(чанк не грузился либо там его нет)",
            TranslocatorTrouble.Broken =>
                $"транслокатор {where} сломан: починок {tl?.RepairState ?? 0} из " +
                $"{RepairInteractionsRequired}. Чинят двумя металлическими деталями и " +
                "временными шестерёнками — бот этого не делает",
            TranslocatorTrouble.NotPowered =>
                $"транслокатор {where} не запитан: сервер держит его выключенным " +
                "(canTele = false) — пара ещё не найдена или её отобрали",
            TranslocatorTrouble.NoAddress =>
                $"транслокатор {where} включён, но адреса не назвал: сервер всё ещё " +
                "ищет ему пару (в игре это «Warping spacetime…»)",
            _ => "",
        };

    /// <summary>Плоское расстояние между двумя точками.</summary>
    public static double Horizontal((double X, double Y, double Z) a, (double X, double Y, double Z) b)
    {
        double dx = a.X - b.X, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Унесло ли нас с пластины (мы стоим — значит это не мы).</summary>
    public static bool MovedAway(BlockPos plate, (double X, double Y, double Z) now,
        double least = MovedAwayBlocks) =>
        Horizontal(now, (plate.X + 0.5, 0, plate.Z + 0.5)) > least ||
        Math.Abs(now.Y - plate.Y) > least;

    /// <summary>
    /// ТУДА ЛИ ПЕРЕНЕСЛО — единственная проверка, по которой шаг маршрута
    /// считается удавшимся.
    ///
    /// Меряем плоско: по высоте сервер ставит нас на блок выше дальней
    /// пластины, и оттуда мы честно падаем на пол — сколько там до пола, знает
    /// только дальняя сторона. А вот X и Z сервер задаёт ровно, и всякое их
    /// расхождение больше запаса — это уже другое место.
    ///
    /// Запас по умолчанию НЕ ВЫБРАН, а выведен — см.
    /// <see cref="ArrivalSlackBlocks"/>.
    /// </summary>
    public static (bool Ok, string Why) CheckArrival(
        (double X, double Y, double Z) landed,
        (double X, double Y, double Z) promised,
        double? slackBlocks = null)
    {
        double slack = slackBlocks ?? ArrivalSlackBlocks;
        double gap = Horizontal(landed, promised);
        if (gap <= slack)
            return (true, $"на месте: {WorldOrigin.Say(landed.X, landed.Y, landed.Z)}, " +
                          $"обещали {WorldOrigin.Say(promised.X, promised.Y, promised.Z)}, " +
                          $"расхождение {gap:0.#} кл.");
        return (false, $"перенесло НЕ ТУДА: обещали {WorldOrigin.Say(promised.X, promised.Y, promised.Z)}, " +
                       $"а я в {WorldOrigin.Say(landed.X, landed.Y, landed.Z)} — мимо на {gap:0.#} кл.");
    }

    /// <summary>
    /// СХОДИТСЯ ЛИ КАРТА С САМИМ БЛОКОМ. Карта (GeoJSON мода) — чужое мнение,
    /// блок-сущность — слово сервера. Расходятся они по делу: карту рисовали
    /// когда-то, а пары сервер связывает на ходу.
    /// </summary>
    public static (bool Agrees, string Why) MapAgrees(BlockPos byBlock, BlockPos byMap, int slack = 1)
    {
        int dx = Math.Abs(byBlock.X - byMap.X);
        int dy = Math.Abs(byBlock.Y - byMap.Y);
        int dz = Math.Abs(byBlock.Z - byMap.Z);
        if (dx <= slack && dy <= slack && dz <= slack)
            return (true, $"карта и блок сходятся на {byBlock}");
        return (false, $"карта обещает выход {byMap}, а сам транслокатор говорит {byBlock}");
    }

    /// <summary>
    /// ДО КАКОГО МИГА ЖДЁМ — одно правило на оба срока входа.
    ///
    /// СРОКОВ ДВА, И ОНИ НЕ СКЛАДЫВАЮТСЯ В ОДИН. Пока на пластину не встали,
    /// идёт срок ПОДХОДА (<paramref name="settleSeconds"/> от того мига, как
    /// тело взялось вставать). Встали — с этой самой минуты начинает течь срок
    /// ОЖИДАНИЯ (<paramref name="patienceSeconds"/>), и всё, что съел подход, в
    /// него уже не входит.
    ///
    /// ПОЧЕМУ ЭТО ЖИВЁТ ЗДЕСЬ, А НЕ СТРОЧКОЙ В ЦИКЛЕ. Пока перевод срока был
    /// отдельным присваиванием у шва, его можно было стереть, и ни один тест не
    /// замечал пропажи: сроки молча сливались обратно в один, и вместе с ними
    /// возвращалась прежняя беда — дойдя до пластины за десять секунд из
    /// пятнадцати, бот сдавался за миг до переноса и говорил «не дождался» про
    /// то, чего и не ждал. Правило в одном месте — и оно в тестах.
    /// </summary>
    /// <param name="startedAt">когда тело взялось вставать на пластину</param>
    /// <param name="stoodAt">когда ВПЕРВЫЕ встали как надо; null — ещё не встали</param>
    public static DateTime Deadline(DateTime startedAt, DateTime? stoodAt,
        double settleSeconds, double patienceSeconds) =>
        stoodAt is { } встал
            ? встал.AddSeconds(patienceSeconds)
            : startedAt.AddSeconds(settleSeconds);
}

/// <summary>
/// ПОДТВЕРЖДЕНИЕ ПЕРЕНОСА ОТ САМОГО СЕРВЕРА.
///
/// Перенеся игрока, <c>BlockEntityStaticTranslocator.didTeleport</c> зовёт
/// <c>TeleporterManager.DidTranslocateServer</c>, и тот шлёт ЛИЧНО ЭТОМУ
/// игроку пустое сообщение <c>DidTeleport</c> по своему каналу «tpManager»
/// (пакет 55, номер сообщения — порядок регистрации типов: TpLocations 0,
/// TeleporterLocation 1, DidTeleport 2). Живому клиенту оно нужно ради звука
/// и тряски камеры, боту — ради правды: «меня перенесли» сказал сервер, а не
/// наши догадки по координатам.
///
/// ЧЕСТНАЯ ОГОВОРКА: то же сообщение шлют и три других телепорта игры
/// (обычный телепортер, возврат к телу, телепорт Тобиаса). Само по себе оно
/// значит «сервер меня куда-то перенёс», а «через ЭТОТ транслокатор» —
/// только вместе с тем, что мы на нём стояли.
/// </summary>
public sealed class TranslocatorWatch
{
    /// <summary>Имя канала, который заводит TeleporterManager.</summary>
    public const string Channel = "tpManager";

    /// <summary>Номер сообщения DidTeleport — третий зарегистрированный тип.</summary>
    public const int DidTeleportMessageId = 2;

    private int channelId = -1;

    /// <summary>Диагностика.</summary>
    public event Action<string>? OnLog;

    /// <summary>Сервер сказал: я перенёсся.</summary>
    public event Action? OnDidTeleport;

    /// <summary>Когда сервер в последний раз подтвердил перенос (null — ни разу).</summary>
    public DateTime? LastDidTeleport { get; private set; }

    /// <summary>Канал найден: подтверждения будут приходить.</summary>
    public bool ChannelKnown => channelId >= 0;

    public TranslocatorWatch(BotClient bot) => bot.OnPacket += HandlePacket;

    /// <summary>Подтверждал ли сервер перенос после этого мига.</summary>
    public bool Confirmed(DateTime since) =>
        LastDidTeleport is { } t && t >= since;

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 56 when p.NetworkChannels is { ChannelNames: { } names, ChannelIds: { } ids }:
                for (int i = 0; i < p.NetworkChannels.ChannelNamesCount &&
                                i < p.NetworkChannels.ChannelIdsCount; i++)
                    if (names[i] == Channel)
                    {
                        channelId = ids[i];
                        OnLog?.Invoke($"канал транслокаторов найден (id {channelId})");
                    }
                break;

            case 55 when p.CustomPacket is { } cp && channelId >= 0 && cp.ChannelId == channelId:
                // Тело сообщения пустое (класс DidTeleport без полей) —
                // разбирать нечего, важен сам факт его прихода
                if (cp.MessageId == DidTeleportMessageId)
                {
                    LastDidTeleport = DateTime.UtcNow;
                    OnLog?.Invoke("сервер подтвердил перенос");
                    OnDidTeleport?.Invoke();
                }
                break;
        }
    }
}

/// <summary>
/// ЖИВОЙ ВХОД: дойти, встать, дождаться и УБЕДИТЬСЯ ПО ФАКТУ, что оказался
/// там, где обещано. Правила отдельно (<see cref="TranslocatorRules"/>) —
/// здесь только руки.
///
/// ЧЕМ ЭТО ПОДПЁРТО, СЛОВО В СЛОВО (приёмка ведётся по этой строке, и
/// приукрашивать её нельзя):
///
/// • ПОРОГИ ПРАВИЛ сторожатся тестами ПО ЗНАЧЕНИЮ, а не по названию:
///   верх и низ <see cref="TranslocatorRules.OnPlate"/>, порог
///   <see cref="TranslocatorRules.MovedAwayBlocks"/>, запас
///   <see cref="TranslocatorRules.ArrivalSlackBlocks"/> и разведение двух
///   сроков в <see cref="TranslocatorRules.Deadline"/>. Раньше их не сторожил
///   ни один: подмена запаса прибытия на 250 блоков оставляла прогон зелёным.
///
/// • ИЗ ШВА проверены только отказы ДО ХОДЬБЫ: беда самого транслокатора и
///   расхождение карты с блок-сущностью (тесты «шов: отказы до первого шага»
///   в TranslocatorEntryTests). Всё, что делается телом, — подход, стояние,
///   ожидание, разбор посадки — НЕ ПРОВЕРЕНО НИЧЕМ: нужен сервер с настоящим
///   транслокатором, и ни одного переноса бот пока не делал.
/// </summary>
public static class Translocating
{
    /// <summary>
    /// Сколько ждать переноса по умолчанию. Игре нужно 4,5 с (см.
    /// <see cref="TranslocatorRules.RealWarmupSeconds"/>), остальное — запас
    /// на сетевую задержку и на то, что первый тик блок-сущности может прийти
    /// через 250 мс после того, как мы встали.
    /// </summary>
    public const double DefaultPatienceSeconds = 15;

    /// <summary>
    /// Сколько давать телу на то, чтобы ВСТАТЬ на пластину после того, как оно
    /// дошло до её клетки.
    ///
    /// ЭТО НЕ ЧАСТЬ ТЕРПЕНИЯ, И ЭТО ВАЖНО. Пока обе доли считались одним
    /// сроком, подход съедал ожидание: дойдя до пластины за десять секунд из
    /// пятнадцати, бот сдавался бы за миг до переноса и говорил «не дождался»
    /// про то, чего и не ждал.
    /// </summary>
    public const double SettleSeconds = 20;

    /// <summary>
    /// Сколько ждать после того, как нас унесло, прежде чем спрашивать «куда».
    /// Само число и причина живут у правил
    /// (<see cref="TranslocatorRules.LandingSettleSeconds"/>): из него же
    /// считается запас проверки прибытия, и двух списаний у срока быть не должно.
    /// </summary>
    public const double LandingSettleSeconds = TranslocatorRules.LandingSettleSeconds;

    /// <summary>
    /// ПРОЙТИ ТРАНСЛОКАТОРОМ. Возвращает удачу ТОЛЬКО если сервер переставил
    /// нас туда, куда обещал сам транслокатор (и карта, если её мнение дали).
    /// </summary>
    /// <param name="plate">Клетка транслокатора — того самого блока-пластины.</param>
    /// <param name="promisedExit">
    /// Что обещает карта маршрута (мод TLPath). null — мнения карты нет,
    /// сверяемся только с блок-сущностью.
    /// </param>
    /// <param name="patienceSeconds">Сколько стоять, прежде чем признать неудачу.</param>
    /// <param name="say">Куда рассказывать по ходу дела.</param>
    public static async Task<SkillResult> EnterAsync(BotContext ctx, BlockPos plate,
        BlockPos? promisedExit = null, double patienceSeconds = DefaultPatienceSeconds,
        Action<string>? say = null, CancellationToken ct = default)
    {
        // 1. ЧТО ЗА БЛОК И ГОДЕН ЛИ ОН — до всякой ходьбы: идти сорок секунд,
        //    чтобы у сломанного развести руками, обидно
        var tl = ctx.Translocators.At(plate);
        var trouble = TranslocatorRules.Trouble(tl);
        if (trouble != TranslocatorTrouble.Ready)
            return SkillResult.Fail(TranslocatorRules.Refusal(trouble, plate, tl));

        var exit = tl!.Destination!.Value;

        // 2. СЛОВО КАРТЫ ПРОТИВ СЛОВА СЕРВЕРА. Расхождение — не мелочь: шаг
        //    маршрута рассчитан на другой выход, и «удача» тут была бы враньём
        if (promisedExit is { } mapExit)
        {
            var (agrees, why) = TranslocatorRules.MapAgrees(exit, mapExit);
            if (!agrees)
                return SkillResult.Fail($"не пойду: {why}. Сеть транслокаторов пересобралась — " +
                                        "маршрут надо строить заново");
        }

        var promised = TranslocatorRules.LandingPoint(exit);
        var (sx, sy, sz) = TranslocatorRules.StandPoint(plate);

        // 3. ДОЙТИ ДО САМОЙ ПЛАСТИНЫ. Целимся в её КЛЕТКУ, а не в клетку выше:
        //    коробка блока 0.125 — это ступенька, и ноги, стоящие на ней,
        //    лежат именно в этой клетке
        say?.Invoke($"иду к транслокатору {plate} (выход {exit})");
        // НОГАМИ, НЕ СПРАШИВАЯ СЕТЬ (Movement.TravelOnFootAsync). Сюда приходят
        // ИЗ похода по сети — это его шаг «дойти до входа», — и спроси мы сеть
        // снова, каждый подход к пластине заводил бы новый поход к ней же, тот
        // — свой подход, и так без дна. А для того, кто зовёт вход напрямую
        // («!телепорт»), ничего не меняется: он и раньше шёл сюда ногами
        if (!await ctx.Movement.TravelOnFootAsync(plate, 300, ct))
            return SkillResult.Fail($"не дошёл до транслокатора {plate}");

        // 4. ВСТАТЬ И СТОЯТЬ. Стояние держит тело (иначе рефлексы уведут),
        //    а мы в это время смотрим за фактами
        var watch = ctx.Translocating;
        var since = DateTime.UtcNow;
        using var hold = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var standing = ctx.Movement.StandAtAsync(sx, sy, sz,
            SettleSeconds + patienceSeconds + 1, hold.Token);

        bool serverSawMe = false;      // блок-сущность подняла somebodyIsTeleporting
        DateTime? stoodAt = null;      // когда ВПЕРВЫЕ встали так, как надо
        string offPlateWhy = "своей позиции так и не узнал";
        string? troubleWhileWaiting = null;
        (double X, double Y, double Z)? landed = null;

        // ГДЕ Я ПОСЛЕ ПЕРЕНОСА — и ПЕРВЫМ ДЕЛОМ СНЯТЬ РУКИ С ТЕЛА.
        //
        // Порядок здесь не вкусовщина, а починка живой беды. Стояние держит
        // тело через StandAtAsync, а та первым делом ДОГОНЯЕТ точку
        // (PursueAsync с точностью 0,2 и сроком 15 с) — и прогрев игры в 4,5 с
        // укладывается внутрь погони целиком. Значит перенос вполне может
        // случиться, пока погоня ещё правит ногами. Подожди мы полсекунды
        // ПРЕЖДЕ, чем отпустить тело, — и всё это время оно полным шагом
        // возвращалось бы к покинутой пластине: при BaseWalkSpeed = 3,393 бл/с
        // это 1,7 блока, то есть ВЕСЬ запас проверки «туда ли перенесло»
        // (ArrivalSlackBlocks считается ровно из этих же двух чисел).
        // Удавшийся перенос отдавался бы как «перенесло НЕ ТУДА», а вина
        // приписывалась бы сети транслокаторов.
        async Task<(double X, double Y, double Z)> ГдеЯПослеПереносаAsync(
            (double X, double Y, double Z) сейчас)
        {
            hold.Cancel();
            await standing;              // ноги встали, клавиши отпущены
            // И только теперь — дать телу догнать серверный перенос: своё
            // положение клиент-авторитетно, и первый прочитанный отсчёт может
            // оказаться ещё старым (см. LandingSettleSeconds)
            await Task.Delay(TimeSpan.FromSeconds(LandingSettleSeconds), ct);
            return ctx.Self.Position ?? сейчас;
        }

        try
        {
            while (DateTime.UtcNow < TranslocatorRules.Deadline(
                       since, stoodAt, SettleSeconds, patienceSeconds) &&
                   !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct);

                if (ctx.Self.Position is not { } now)
                    continue;

                // УНЕСЛО — И СПРАШИВАТЬ ОБ ЭТОМ НАДО ПЕРВЫМ ДЕЛОМ. Перенос
                // уводит нас за сотни блоков, сервер выгружает покинутый чанк,
                // а вместе с ним пропадает и блок-сущность транслокатора.
                // Спроси мы сперва «цел ли транслокатор» — удавшийся перенос
                // прочёлся бы как «транслокатора не вижу»
                if (TranslocatorRules.MovedAway(plate, now))
                {
                    landed = await ГдеЯПослеПереносаAsync(now);
                    break;
                }

                // СЕРВЕР ПЕРЕДУМАЛ, ПОКА МЫ ШЛИ И СТОЯЛИ. Блок-сущность
                // приезжает заново при каждом изменении, и «пару отобрали»
                // надо услышать сразу, а не отстояв всё терпение впустую.
                // Но не с первого же взгляда: между «сервер нас перенёс» и
                // «наше тело об этом узнало» есть щель, и в ней транслокатор
                // выглядит пропавшим, хотя пропали из его чанка мы сами
                if (TranslocatorRules.Trouble(ctx.Translocators.At(plate)) is var сейчас &&
                    сейчас != TranslocatorTrouble.Ready)
                {
                    var после = await ГдеЯПослеПереносаAsync(now);
                    if (TranslocatorRules.MovedAway(plate, после))
                    {
                        landed = после;
                        break;
                    }
                    troubleWhileWaiting = TranslocatorRules.Refusal(
                        сейчас, plate, ctx.Translocators.At(plate));
                    break;
                }

                if (TranslocatorRules.OnPlate(plate, now.X, now.Y, now.Z))
                {
                    if (stoodAt is null)
                    {
                        // Терпение считается ОТСЮДА, а не от начала подхода:
                        // сам перевод срока живёт в TranslocatorRules.Deadline,
                        // и весь цикл спрашивает его каждым витком
                        stoodAt = DateTime.UtcNow;
                        say?.Invoke($"встал на пластину {plate}, жду переноса " +
                                    $"(игре нужно {TranslocatorRules.RealWarmupSeconds:0.##} с)");
                    }
                }
                else
                    offPlateWhy = $"ноги в {WorldOrigin.Say(now.X, now.Y, now.Z)}, " +
                                  $"а надо {WorldOrigin.Say(sx, sy, sz)}";

                // Слово сервера: он завёл нас в очередь на перенос
                if (!serverSawMe &&
                    ctx.World.GetBlockEntity(plate)?.Attributes?.GetBool("somebodyIsTeleporting") == true)
                {
                    serverSawMe = true;
                    say?.Invoke("сервер видит меня на пластине — жду переноса");
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            hold.Cancel();
            await standing;
        }

        // 5. РАЗБОР ПОЛЁТА
        if (landed is not { } at)
        {
            if (troubleWhileWaiting is { } испортился)
                return SkillResult.Fail($"пока я шёл и стоял, {испортился}");
            if (ct.IsCancellationRequested)
                return SkillResult.Fail($"перенос через {plate} прерван");
            if (stoodAt is null)
                return SkillResult.Fail(
                    $"до клетки {plate} дошёл, а на пластину встать не вышло за " +
                    $"{SettleSeconds:0} с: {offPlateWhy}. " +
                    "Сервер считает перенос по столкновению коробок: провалившись в пластину " +
                    "или сойдя с неё больше чем на " +
                    $"{TranslocatorRules.CenterSlack:0.##} кл. от центра, его не получишь");
            if (!serverSawMe)
                return SkillResult.Fail(
                    $"стою на пластине {plate} уже {patienceSeconds:0} с, а сервер меня на ней " +
                    "не видит: блок-сущность так и не подняла somebodyIsTeleporting. " +
                    "Значит наши пакеты позиции до него не доходят или он их отвергает " +
                    "(например, устарел positionVersionNumber)");
            return SkillResult.Fail(
                $"стою внутри {plate}, сервер это видит, а переноса нет за {patienceSeconds:0} с " +
                $"(игре нужно {TranslocatorRules.RealWarmupSeconds:0.##} с непрерывно). " +
                "Похоже, нас выкидывает из очереди между тиками");
        }

        // 6. ТУДА ЛИ. Только это и делает шаг маршрута удавшимся
        var (ok, why2) = TranslocatorRules.CheckArrival(at, promised);
        bool confirmed = watch?.Confirmed(since) ?? false;

        if (ok)
        {
            string word = confirmed
                ? "сервер подтвердил перенос"
                : watch is { ChannelKnown: true }
                    ? "подтверждения по каналу «tpManager» не было, но координаты сошлись"
                    : "канал подтверждений не найден — верю координатам";
            // ВОТ ЭТО И ЕСТЬ ФАКТ «БЫЛ ТУТ → СТАЛ ТАМ». Сильнее слова
            // блок-сущности: та верна на миг чтения, а этот переход УЖЕ
            // случился и проверен координатами (CheckArrival). Запись переживёт
            // и выгрузку чанка, и перезапуск бота — см. TlMemory
            ctx.Translocators.Memory?.Learn(plate, exit, byFeet: true);
            say?.Invoke($"перенёсся через {plate}: {why2}");
            await StepOffAsync(ctx, exit, at, say, ct);
            return SkillResult.Ok($"перенёсся через {plate} к {exit}: {why2} ({word})");
        }

        return SkillResult.Fail(confirmed
            ? $"{why2}. Сервер перенос подтвердил, значит транслокатор {plate} ведёт не туда, " +
              "куда обещала его же блок-сущность, — сеть пересобралась"
            : $"{why2}. Подтверждения переноса от сервера не было — возможно, меня просто " +
              "сбило с пластины");
    }

    /// <summary>
    /// СОЙТИ С ДАЛЬНЕЙ ПЛАСТИНЫ. Ваниль выбрасывает нас ВПЛОТНУЮ к ней, но не
    /// на неё (смещение −0.3), — и всё же проверяем делом: встав на дальнюю
    /// пластину, через 4,4 с уедешь обратно, и маршрут пойдёт по кругу.
    /// </summary>
    private static async Task StepOffAsync(BotContext ctx, BlockPos exit,
        (double X, double Y, double Z) at, Action<string>? say, CancellationToken ct)
    {
        if (!TranslocatorRules.OnPlate(exit, at.X, at.Y, at.Z))
            return;
        say?.Invoke($"стою на дальней пластине {exit} — схожу, иначе через " +
                    $"{TranslocatorRules.RealWarmupSeconds:0.##} с уедет обратно");
        await ctx.Movement.StepAsideFromAsync(exit, 3, null, ct);
    }
}

/// <summary>
/// ПОХОД К ТОЧКЕ: «вот место на карте — доберись сам».
///
/// ЗАКАЗ ДОСЛОВНО: «хочу по веб админке на карте давать точку куда ему бежать,
/// и он уже думал, через телепорты или пешком и шел».
///
/// ЧТО ЗДЕСЬ ЕСТЬ И ЧЕГО ЗДЕСЬ НЕТ. Здесь ТОЛЬКО РУКИ: спросить план, сказать
/// его вслух, пройти шаг за шагом и честно доложить. Ни одного своего числа и
/// ни одного своего решения:
///   • чем меряется дорога и когда сеть того стоит — <see cref="TrekRules"/>
///     и ручки роли (<see cref="TlRouteSettings"/>);
///   • куда идти по сети — <see cref="TlNet.Route"/> (Дейкстра по карте сервера);
///   • как войти в транслокатор и КАК УЗНАТЬ, ЧТО ПЕРЕНОС СЛУЧИЛСЯ, —
///     <see cref="Translocating.EnterAsync"/>. Второй проверки прибытия здесь
///     нет НАРОЧНО: она уже есть там, и двух её списаний быть не должно.
///
/// ТРИ ВЕЩИ, РАДИ КОТОРЫХ ЭТОТ КЛАСС И НАПИСАН.
///
/// 1. ОБЕ ОЦЕНКИ ВСЛУХ, ДО ПЕРВОГО ШАГА. «87 минут пешком против 47 через
///    сеть» — это разговор с человеком; «иду» — приказ, в котором нечего
///    проверить.
///
/// 2. НАТКНУЛСЯ — СКАЖИ И ПЕРЕСЧИТАЙ. Карта сервера — выгрузка суточной
///    давности: транслокатор могли сломать, застроить или отобрать у него пару.
///    Наткнувшись, бот кладёт эту пластину в обход и считает дорогу заново
///    (<see cref="TlNet.Route"/>, довод <c>avoid</c>) — а не идёт вслепую
///    дальше по плану, которого больше нет.
///
/// 3. ПРЕРВАЛИ — ВЕРНИСЬ. Поход длится десятки минут, и рефлекс жизни
///    (поесть, отбиться, спастись) обязан его перебивать. Поэтому он идёт под
///    <see cref="Resume"/>: рефлекс отпустил тело — бот возвращается и считает
///    дорогу заново ОТТУДА, ГДЕ ОЧНУЛСЯ, а не с покинутого места. А «стоп»
///    человека гасит поход насмерть, и возврата после него нет.
/// </summary>
public sealed class Trek
{
    private readonly BotContext ctx;
    private readonly TranslocatorRouting routes;

    /// <param name="ctx">тело, ноги и мир</param>
    /// <param name="routes">
    /// Сеть переходов живого бота (<see cref="VsBot.Routes"/>). Отдельным
    /// доводом, а не через <paramref name="ctx"/>: маршруты знают адрес карты
    /// сервера, то есть политику роли, и подсовывать их всем навыкам разом
    /// незачем.
    /// </param>
    public Trek(BotContext ctx, TranslocatorRouting routes)
    {
        this.ctx = ctx;
        this.routes = routes;
    }

    /// <summary>Куда рассказывать по ходу дела. Молчать нельзя.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// СКОЛЬКО РАЗ ПЕРЕСЧИТЫВАТЬ ДОРОГУ ЗА ОДИН ПОХОД. Три — по той же причине,
    /// по какой три возврата у <see cref="Resume.MaxReturns"/>: раз-другой
    /// споткнуться можно и по случайности, а три подряд означают, что дороги
    /// туда сейчас нет. Само правило живёт в <see cref="TrekRules.AfterTrouble"/>.
    /// </summary>
    public int MaxReplans { get; set; } = 3;

    /// <summary>Клетка, в которой бот стоит; null — своей позиции он не знает.</summary>
    public BlockPos? Here => ctx.Self.Position is { } p
        ? new BlockPos((int)Math.Floor(p.X), (int)Math.Floor(p.Y), (int)Math.Floor(p.Z))
        : null;

    /// <summary>
    /// ЧТО ЗА ПОХОД ВЫЙДЕТ — НЕ ДЕЛАЯ НИ ШАГУ. Это и показывает окно управления
    /// человеку, щёлкнувшему по карте: выбранную точку, обе оценки и цену
    /// решения. Случайный щелчок не должен уводить бота за десять тысяч блоков,
    /// поэтому между «выбрал» и «пошёл» стоит подтверждение — а подтверждать
    /// вслепую человеку нечего.
    ///
    /// null — бот не знает, где стоит (не вошёл в мир): считать дорогу не от чего.
    /// </summary>
    public TrekPlan? PlanTo(BlockPos goalWorld, bool ask = true) =>
        Here is { } здесь ? routes.PlanTo(здесь, goalWorld, ask) : null;

    /// <summary>
    /// ИДТИ. Успех — ТОЛЬКО когда ноги и сервер это подтвердили: пеший шаг
    /// считается пройденным по факту позиции, пересадка — по факту смены
    /// координат (<see cref="Translocating.EnterAsync"/>).
    /// </summary>
    public async Task<SkillResult> GoAsync(BlockPos goalWorld, CancellationToken ct = default)
    {
        // МЁРТВЫЕ ПЛАСТИНЫ ЖИВУТ ОДИН ПОХОД, а не вечно: сломанный транслокатор
        // чинят, у выключенного находится пара. Помнить это между походами
        // значило бы вычеркнуть половину сети из-за одного вечера
        var мёртвые = new HashSet<TlPoint>();
        var итог = SkillResult.Fail("поход даже не начинался");

        var отчёт = await ctx.Resume.RunAsync($"поход к {Точка(goalWorld)}",
            BodyArbiter.Importance.Command,
            async (заход, token) =>
            {
                if (заход > 1)
                    Say($"возвращаюсь к походу (заход {заход}) — дорогу считаю заново оттуда, " +
                        "где очнулся: пока меня не было, я мог оказаться где угодно");
                // СРОКА У ЭТОГО ПОХОДА НЕТ: его завёл человек щелчком по карте
                // и ждёт, сколько понадобится. Срок появляется у того похода,
                // что вырос из обычной дороги, — см. TryRideAsync
                Setback помеха;
                (итог, помеха) = await ЗаходAsync(goalWorld, мёртвые, DateTime.MaxValue, token);
                // И ПОМЕХУ НАЗЫВАЕМ ВСЛУХ — до этой правки поход отдавал
                // возврату голый bool, то есть Setback.None: «причину не
                // назвали», а оно числится среди постоянных. Значит любой
                // поход сдавался с первого раза — и тот, где чанк не доехал, и
                // тот, где на пластине стоял игрок
                return (итог.Success, помеха);
            }, ct);

        // ТЕЛА МОГЛИ И НЕ ДАТЬ — тогда похода не было вовсе, и отвечать надо
        // словами распорядителя, а не выдуманным «не дошёл»
        if (отчёт.Tries == 0)
            return SkillResult.Fail(отчёт.Say);

        // Итог — ОТВЕТ САМОГО ПОХОДА. «Тело не отбирали» и «дошёл» — разное:
        // ровно на этой подмене прежде докладывали о доделанном карьере,
        // вставшем на третьей клетке
        return итог.Success
            ? итог
            : SkillResult.Fail($"{итог.Message} ({отчёт.Say})");
    }

    /// <summary>
    /// ПОПРОБОВАТЬ ДОЙТИ ЧЕРЕЗ СЕТЬ, НЕ ЗАБИРАЯ ТЕЛО — вход для ОБЫЧНОЙ дороги.
    ///
    /// ЖИВАЯ ЖАЛОБА ЗАКАЗЧИКА (01.09): «мы же записываем в нашу постоянную
    /// память пары тл? а то бот побежал вместо тл». Всё, что нужно для похода
    /// через переходы, было — и звалось ровно из одного места: щелчок по карте
    /// в окне управления. Любая другая дальняя дорога (задача, склад, дом,
    /// работа) шла прямо в ноги, а ноги ходят по клеткам и про пары не знают.
    ///
    /// ЧЕМ ЭТОТ ВХОД ОТЛИЧАЕТСЯ ОТ <see cref="GoAsync"/>. Тем, ЧЬЁ ТЕЛО.
    /// GoAsync заводит поход сам и потому берёт тело через
    /// <see cref="Resume"/>; сюда же приходят ИЗ УЖЕ ИДУЩЕЙ дороги — тело уже
    /// у того, кто позвал, и второй захват тела означал бы, что задача сама у
    /// себя отбирает ноги. И срок здесь чужой: сколько дали дороге, столько и
    /// есть (см. <see cref="TrekRules.LegSecondsWithin"/>).
    ///
    /// null — «СЕТЬ ТУТ НИ ПРИ ЧЁМ, ИДИ КАК ШЁЛ». Это не отказ и не неудача:
    /// ногами дойти можно всегда, и ноги умеют то, чего не умеет маршрут по
    /// карте, — обходить стену, лезть, плыть, строить мост. Возвращать вместо
    /// этого «не дошёл» значило бы отменять обычную дорогу из-за того, что
    /// карта не помогла.
    /// </summary>
    /// <param name="goalWorld">куда идти — клеткой МИРА</param>
    /// <param name="maxSeconds">сколько всего дали на дорогу</param>
    public async Task<bool?> TryRideAsync(BlockPos goalWorld, double maxSeconds,
        CancellationToken ct = default)
    {
        if (Here is not { } здесь)
            return null;   // не знаю, где стою: считать дорогу не от чего — пусть идут ноги

        var план = routes.PlanTo(здесь, goalWorld, ask: true);

        // ПЛАН ГОВОРИТ «НОГАМИ» — И ГОВОРИТ ЭТО ВСЛУХ. Ровно этой строки не
        // хватало заказчику: он видел бегущего бота и не мог узнать, посмотрел
        // ли тот на сеть вообще. Здесь сказано и то и другое сразу: и что
        // посмотрел, и почему не помогло (сети нет / не по пути / выигрыш мал)
        if (план.NothingToDo || !план.ByNet)
        {
            Say(план.Tell());
            return null;
        }

        // Дальше поход сам скажет и план, и каждый шаг: он их и считает
        var (итог, _) = await ЗаходAsync(goalWorld, [], DateTime.UtcNow.AddSeconds(maxSeconds), ct);
        // Слова похода — наружу целиком. Отказ без причины тут особенно дорог:
        // дорогу звала задача, и «просто не дошёл» она перескажет человеку как
        // свою беду, хотя беда была в пересадке
        Say(итог.Message is { Length: > 0 } слова
            ? слова
            : итог.Success ? $"дошёл до {Точка(goalWorld)}" : $"до {Точка(goalWorld)} не дошёл");
        return итог.Success;
    }

    /// <summary>
    /// ОДИН ЗАХОД: спросить план, сказать его вслух и пройти. Пересчёт дороги
    /// живёт внутри — он не новый заход, а продолжение того же похода.
    /// </summary>
    /// <param name="deadline">
    /// Когда у похода кончается время. <see cref="DateTime.MaxValue"/> — срока
    /// нет вовсе (поход, заведённый человеком с карты). А поход, выросший из
    /// обычной дороги, обязан уложиться в чужой срок: иначе задача, которой
    /// отвели минуту, молча висела бы шесть — один только минимум на шаг
    /// (<see cref="TrekRules.MinLegSeconds"/>) больше всего, что ей дали.
    /// </param>
    /// <returns>
    /// Чем кончился заход И ЧЕМ ПОХОД ОБЪЯСНЯЕТ СВОЮ ОСТАНОВКУ — общим словом
    /// (<see cref="Setback"/>). Слово нужно возврату: до этой правки поход
    /// отдавал ему голый <c>bool</c>, тот превращался в
    /// <see cref="Setback.None"/> («причину не назвали»), а это слово лежит
    /// среди ПОСТОЯННЫХ — то есть всякий поход сдавался с первого раза.
    ///
    /// ЧЕГО ЭТО СЛОВО ЗДЕСЬ НЕ ЗНАЕТ И ЗНАТЬ ПОКА НЕ МОЖЕТ. Пеший шаг ходит
    /// через <c>Movement.TravelOnFootAsync</c>, а тот отвечает голым
    /// <c>false</c>: «упёрся в стену», «игрок встал на клетке» и «не хватило
    /// секунд» приходят сюда неотличимыми. Назвать любое из трёх наугад
    /// значило бы соврать в двух случаях из трёх — поэтому пеший провал
    /// остаётся «причину не назвали», и починка его не здесь, а в ногах.
    /// </returns>
    private async Task<(SkillResult Итог, Setback Помеха)> ЗаходAsync(BlockPos goal,
        HashSet<TlPoint> мёртвые, DateTime deadline, CancellationToken ct)
    {
        int пересчётов = 0;
        double Осталось() => (deadline - DateTime.UtcNow).TotalSeconds;

        while (!ct.IsCancellationRequested)
        {
            if (Осталось() <= 0)
                // СРОК ЗДЕСЬ И ВПРАВДУ СРОК: у похода, выросшего из обычной
                // дороги, время чужое и второго захода ему не дадут
                return (SkillResult.Fail(
                    $"время, отведённое на дорогу до {Точка(goal)}, вышло; стою на {Где()}"),
                    Setback.OutOfTime);

            if (Here is not { } здесь)
                // «СВОЕЙ ПОЗИЦИИ Я НЕ ЗНАЮ» — ЭТО НЕДОЕХАВШИЙ МИР: своя
                // сущность приходит уже после входа. Помеха временная, и это
                // ровно тот случай, ради которого возврат и заводили
                return (SkillResult.Fail("своей позиции я не знаю — идти не от чего"),
                    Setback.NotLoaded);

            var план = routes.PlanTo(здесь, goal, ask: true, мёртвые);

            // ОБЕ ОЦЕНКИ ВСЛУХ, каждый раз заново: после пересадки и после
            // пересчёта числа другие, и старые были бы враньём
            Say(план.TellSteps());

            if (план.NothingToDo)
                return (SkillResult.Ok($"идти никуда не надо: я уже на {Точка(goal)}"),
                    Setback.None);

            // ── ПЕШКОМ НАПРЯМУЮ ────────────────────────────────────────────
            if (!план.ByNet)
            {
                // Срок шага — свой, но не длиннее того, что осталось у всего
                // похода: см. TrekRules.LegSecondsWithin
                double срок = TrekRules.LegSecondsWithin(
                    TrekRules.LegBudgetSeconds(план.Choice.WalkSeconds), Осталось());
                // Ногами и без вопросов к сети: сеть только что спросили — вот
                // этот самый план (см. Movement.TravelOnFootAsync)
                if (await ctx.Movement.TravelOnFootAsync(goal, срок, ct))
                    return (SkillResult.Ok($"дошёл ногами до {Точка(goal)}, стою на {Где()}"),
                        Setback.None);

                // ПРЕРВАЛИ — ЭТО НЕ «НЕ ДОШЁЛ». Ноги бросают дорогу и по своей
                // беде, и по чужой воле: рефлекс жизни забрал тело, человек
                // сказал «стоп». Свалив их в одно, бот жаловался бы на стену
                // там, где его просто остановили, — и «почему он не дошёл»
                // человек искал бы в карте вместо собственной кнопки
                if (ct.IsCancellationRequested)
                    return (SkillResult.Fail($"поход прерван на полдороге, стою на {Где()}"),
                        Setback.Stopped);

                // Пеший провал НАПРЯМУЮ новых сведений не даёт: пересчёт вывел
                // бы тот же самый путь через то же самое место
                var (_, слово) = TrekRules.AfterTrouble(false, пересчётов, MaxReplans);
                // ЧЕСТНОГО ИМЕНИ ТУТ НЕТ. Ноги вернули голый false, и под ним
                // лежат три разные беды: стена без обхода (NoRoute), игрок на
                // клетке (Crowd) и «не хватило секунд» (OutOfTime). Выбрать
                // одну наугад значило бы соврать в двух случаях из трёх —
                // отдаём «причину не назвали», а сама причина, какую бот знает,
                // стоит словами в отказе
                return (SkillResult.Fail(
                    $"до {Точка(goal)} не дошёл ногами за {срок:0} с, стою на {Где()}. {слово}"),
                    Setback.None);
            }

            // ── ПО СЕТИ, ШАГ ЗА ШАГОМ ─────────────────────────────────────
            var шаги = план.Legs;
            (string Why, bool NewFacts)? беда = null;

            for (int i = 0; i < шаги.Count && беда is null; i++)
            {
                if (ct.IsCancellationRequested)
                    return (SkillResult.Fail("поход прерван"), Setback.Stopped);

                var шаг = шаги[i];
                Say(TrekRules.SayLeg(i + 1, шаги.Count, шаг));
                беда = шаг.ByTeleport
                    ? await ПересадкаAsync(шаг, мёртвые, ct)
                    : await ПешкомAsync(шаг, i + 1 < шаги.Count && шаги[i + 1].ByTeleport,
                        мёртвые, Осталось(), ct);
            }

            // Та же оговорка, что и у пешего пути: шаг, оборванный чужой волей,
            // не повод жаловаться на дорогу и уж точно не повод пересчитывать
            if (ct.IsCancellationRequested)
                return (SkillResult.Fail($"поход прерван на полдороге, стою на {Где()}"),
                    Setback.Stopped);

            if (беда is not { } что)
                return (SkillResult.Ok(
                    $"дошёл до {Точка(goal)} через сеть ({план.Choice.Transfers} " +
                    $"{TlRoute.Пересадок(план.Choice.Transfers)}), стою на {Где()}"),
                    Setback.None);

            var (пересчитать, как) = TrekRules.AfterTrouble(что.NewFacts, пересчётов, MaxReplans);
            Say($"{что.Why}. {как}");
            if (!пересчитать)
                // ПЕРЕСЧИТЫВАТЬ БОЛЬШЕ НЕЧЕГО — и это не помеха мира, а конец
                // сведений: карта соврала про все пластины, какие знала.
                // Честного слова на «дорог больше не осталось» тут нет: под
                // отказом лежит всё тот же голый false ног
                return (SkillResult.Fail($"{что.Why}. {как}. Стою на {Где()}"), Setback.None);

            пересчётов++;
        }

        // Сюда приходят только через отмену: цикл ходит, пока токен жив
        return (SkillResult.Fail($"поход прерван, стою на {Где()}"), Setback.Stopped);
    }

    /// <summary>
    /// ПЕРЕСАДКА. Сам вход, стояние и проверка «туда ли перенесло» — целиком
    /// в <see cref="Translocating.EnterAsync"/>; здесь только разбор отказа.
    ///
    /// НЕУДАВШАЯСЯ ПЕРЕСАДКА — ЭТО НОВЫЕ СВЕДЕНИЯ, и в этом весь смысл: карта
    /// про эту пластину соврала (сломана, не запитана, ведёт не туда), и
    /// следующий маршрут обязан её обойти, а не привести нас сюда опять.
    ///
    /// НО ТОЛЬКО НЕ ТОГДА, КОГДА ШАГ ОБОРВАЛИ ЧУЖОЙ ВОЛЕЙ: отменённый вход
    /// отвечает таким же отказом, а вычеркнутая пластина уходит из сети на весь
    /// поход (см. <see cref="TrekRules.LegFailureIsAboutTheRoad"/>).
    /// </summary>
    private async Task<(string Why, bool NewFacts)?> ПересадкаAsync(TlLeg шаг,
        HashSet<TlPoint> мёртвые, CancellationToken ct)
    {
        if (routes.WorldOf(шаг.From.At) is not { } пластина)
            return ("сдвиг координат пропал прямо посреди похода — перевести адрес " +
                    "пластины в клетку мира нечем", false);

        // МНЕНИЕ КАРТЫ О ВЫХОДЕ подаём в сам вход: расхождение с блок-сущностью
        // останавливает ДО того, как бот встанет на пластину (см. MapAgrees)
        var обещано = routes.WorldOf(шаг.To.At);
        var ответ = await Translocating.EnterAsync(ctx, пластина, обещано, say: Say, ct: ct);
        if (ответ.Success)
        {
            Say(ответ.Message ?? $"перенёсся через {пластина}");
            return null;
        }

        // ПРЕРВАЛИ — ЭТО НЕ «МЁРТВАЯ ПЛАСТИНА». Вход отвечает отказом и тогда,
        // когда тело у него отобрали на полдороге («перенос через … прерван»), а
        // вычеркнутая пластина уходит в набор обхода, который живёт весь поход.
        // Правило одно на оба вида шага — см. TrekRules.LegFailureIsAboutTheRoad
        if (!TrekRules.LegFailureIsAboutTheRoad(ct.IsCancellationRequested, aboutPlate: true))
            return ($"пересадку {шаг.From.Name} → {шаг.To.Name} прервали на полдороге " +
                    $"({ответ.Message}) — на пластину это не наговариваю", false);

        мёртвые.Add(шаг.From.At);
        return ($"пересадка {шаг.From.Name} → {шаг.To.Name} не вышла: {ответ.Message}", true);
    }

    /// <summary>
    /// ПЕШИЙ ШАГ МАРШРУТА.
    ///
    /// ЧЕМ ПРОВАЛ ПОДХОДА К ПЛАСТИНЕ ОТЛИЧАЕТСЯ ОТ ЛЮБОГО ДРУГОГО. «К этой
    /// пластине дороги нет» — такие же новые сведения, как и сломанный
    /// транслокатор: карта считала, что туда можно дойти, а на деле там обрыв
    /// или вода. Значит пластину в обход — и другой дорогой. Провал шага,
    /// который ведёт просто в промежуточную точку, ничего нового не говорит.
    ///
    /// А ПРЕРВАННЫЙ ШАГ НЕ ГОВОРИТ НИЧЕГО ВООБЩЕ: ноги, у которых отобрали тело,
    /// возвращают тот же false, что и ноги, упёршиеся в обрыв (см.
    /// <see cref="TrekRules.LegFailureIsAboutTheRoad"/>).
    /// </summary>
    /// <param name="secondsLeft">
    /// Сколько осталось у всего похода. Шаг не вправе взять больше: у похода,
    /// выросшего из обычной дороги, срок чужой — см.
    /// <see cref="TrekRules.LegSecondsWithin"/>.
    /// </param>
    private async Task<(string Why, bool NewFacts)?> ПешкомAsync(TlLeg шаг, bool кПластине,
        HashSet<TlPoint> мёртвые, double secondsLeft, CancellationToken ct)
    {
        if (routes.WorldOf(шаг.To.At) is not { } куда)
            return ("сдвиг координат пропал прямо посреди похода — перевести точку " +
                    "маршрута в клетку мира нечем", false);

        double срок = TrekRules.LegSecondsWithin(TrekRules.LegBudgetSeconds(шаг.Seconds), secondsLeft);
        // Шаг похода идёт НОГАМИ и сеть не спрашивает: он сам и есть кусок
        // маршрута по сети (см. Movement.TravelOnFootAsync)
        if (await ctx.Movement.TravelOnFootAsync(куда, срок, ct))
            return null;

        // ТО ЖЕ ПРАВИЛО, ЧТО И У ПЕРЕСАДКИ: отменённые ноги возвращают тот же
        // false, что и ноги, упёршиеся в обрыв, — и без этой развилки чужая воля
        // («стоп», голод) вычёркивала исправный транслокатор из всей сети
        bool прервали = ct.IsCancellationRequested;
        if (!TrekRules.LegFailureIsAboutTheRoad(прервали, кПластине))
            return (прервали
                ? $"шаг к {шаг.To.Name} прервали на полдороге, стою на {Где()} — " +
                  "на дорогу это не наговариваю"
                : $"не дошёл до {шаг.To.Name} за {срок:0} с, стою на {Где()}", false);

        мёртвые.Add(шаг.To.At);
        return ($"к транслокатору {шаг.To.Name} дороги нет: не дошёл за {срок:0} с, " +
                $"стою на {Где()}", true);
    }

    /// <summary>
    /// Клетка человеческими числами — ИГРОВЫМИ, теми же, что бот называет в
    /// журнале.
    ///
    /// ЗДЕСЬ БЫЛА ВТОРАЯ КОПИЯ ПЕРЕВОДА: своя развилка «есть сдвиг — вычитаю,
    /// нет — говорю „мировые“». Копия жила отдельно от <see cref="WorldOrigin"/>
    /// и отвечала на «сдвига нет» ДРУГИМИ словами, чем весь остальной журнал, —
    /// а два разных слова об одном и том же человек читает как два разных
    /// случая. Правило одно, и оно там же, где разбор по сборкам игры.
    /// </summary>
    private static string Точка(BlockPos клетка) =>
        WorldOrigin.Say(клетка.X, клетка.Y, клетка.Z);

    /// <summary>Где бот прямо сейчас — для доклада.</summary>
    private string Где() => Here is { } клетка ? Точка(клетка) : "неизвестно где";

    private void Say(string слова) => OnLog?.Invoke(слова);
}
