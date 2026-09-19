namespace VsBotKit;

/// <summary>
/// Поиск пути A* по клеткам мира — реализация по умолчанию.
///
/// Умеет: 8 направлений (диагонали без «срезания углов» сквозь стены),
/// ступеньки вверх, безопасный спуск и осознанные спрыгивания с обрывов
/// (урон от падения оценивается и входит в стоимость пути), спрямление
/// готового маршрута.
///
/// Модель урона от падения в игре: до 3.5 блоков — без урона, дальше
/// примерно (высота − 3.5) хп (см. EntityBehaviorHealth.OnFallToGround).
/// </summary>
public class AStarPathFinder : IPathFinder
{
    /// <summary>Высота падения, с которой начинается урон.</summary>
    public const double FallDamageFreeHeight = 3.5;

    /// <summary>Оценка урона в хп за падение с указанной высоты.</summary>
    public static double EstimateFallDamage(double height) =>
        Math.Max(0, height - FallDamageFreeHeight);

    // dx, dz, стоимость шага
    private static readonly (int dx, int dz, double cost)[] AllSteps =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, 1.414), (1, -1, 1.414), (-1, 1, 1.414), (-1, -1, 1.414)
    ];

    /// <summary>
    /// Сколько клеток осмотрел ПОСЛЕДНИЙ поиск.
    ///
    /// Не украшение: жалобу «подвисает на пару секунд» иначе нечем ни
    /// измерить, ни доказать, что вылечена. По этому числу видно, упёрся ли
    /// поиск в свой потолок (см. NodeCeiling) или честно обошёл всё, до чего
    /// дотянулся, — а это два совсем разных отказа.
    /// </summary>
    public int LastExplored { get; private set; }

    public List<PathStep>? FindPath(IPathWorld world, BlockPos from, BlockPos to, PathOptions o)
    {
        // Цель может быть в воздухе, в стене или на воде — ищем опору в её колонне
        if (!world.IsSupport(to.X, to.Y, to.Z))
        {
            int? fixedY = world.FindSupportY(to.X, to.Y, to.Z, up: 6, down: 6);
            if (fixedY != null)
                to = to with { Y = fixedY.Value };
            else if (!o.AllowPartialPath)
            {
                o.OnTrace?.Invoke($"в колонне цели {to} нет опоры — вставать некуда");
                return null;
            }
            // В режиме частичного пути цель за краем прогруженного мира —
            // это норма: она нужна лишь как направление для эвристики
        }

        // Сколько клеток позволено просмотреть ИМЕННО ЭТОМУ поиску (см. NodeCeiling).
        // Считается после уточнения высоты цели: потолок зависит от расстояния,
        // а цель могла переехать на опору в своей колонне
        int ceiling = NodeCeiling(from, to, o);

        var steps = o.AllowDiagonals ? AllSteps : Straight4;
        var open = new PriorityQueue<BlockPos, double>();
        var cameFrom = new Dictionary<BlockPos, BlockPos>();
        // Чем именно попали в клетку. Планировщик это ЗНАЕТ — и обязан
        // передать телу, а не заставлять его гадать по геометрии
        var kindOf = new Dictionary<BlockPos, StepKind>();
        var costSoFar = new Dictionary<BlockPos, double> { [from] = 0 };
        open.Enqueue(from, 0);

        BlockPos? goal = null;
        int explored = 0;

        // Ближайшая к цели просмотренная клетка — из неё выйдет частичный путь
        var closest = from;
        // Мерка «как далеко от цели» с места старта: она не меняется за поиск,
        // поэтому считается ОДИН раз. Раньше её (и мерку текущей клетки) звали
        // по три раза на каждую снятую с очереди клетку — а клеток в тупике
        // набегает под тридцать тысяч
        double startScore = GoalScore(from, to, o);
        double closestDistance = startScore;
        // Самая дальняя от старта клетка, до которой добрались: если к цели не
        // приблизиться ни на шаг, лучше уйти разведывать, чем стоять столбом
        var farthest = from;
        double farthestCost = 0;

        while (open.TryDequeue(out var current, out _))
        {
            double distance = GoalScore(current, to, o);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closest = current;
            }
            if (o.AllowExploration && costSoFar.TryGetValue(current, out double got) &&
                got > farthestCost && distance <= startScore + o.ExploreSlack)
            {
                farthestCost = got;
                farthest = current;
            }

            // Допуск в клетку — чтобы не упираться в точную клетку цели. Но вися
            // на лестнице, «почти дошёл» не считается: иначе бот бросит спуск
            // на середине шахты и останется висеть
            if (current == to || (Math.Abs(current.X - to.X) + Math.Abs(current.Z - to.Z) <= 1 &&
                                  Math.Abs(current.Y - to.Y) <= 1 &&
                                  !world.CanHangAt(current.X, current.Y, current.Z)))
            {
                goal = current;
                break;
            }
            if (++explored > ceiling)
                break;

            // ДОКУДА ВООБЩЕ МОГУТ ПОДНЯТЬСЯ НОГИ ИЗ ЭТОЙ КЛЕТКИ, блоков.
            //
            // ЖИВАЯ БЕДА 27.08 (стенд, Стенд2: одиннадцать опытов, ДОШЁЛ 0).
            // Бот сидел в затопленном ходе — пол 113, вода в 113 и 114,
            // кирпичное перекрытие в 115 — и раз за разом получал маршрут на
            // кромку (32, 114, 5) с верхом ровно 114,0:
            //     упёрся на точке 2/13 (32, 114, 5), стою (32,8, 113,1, 4,7)
            // Число 113,1 не «примерно»: 115,0 − 1,85 = 113,15, тело ВИСЕЛО
            // МАКУШКОЙ В ПОТОЛКЕ. Выше него ноги не поднимет НИЧТО — ни
            // плавучесть, ни разгон, ни прыжок (у плывущего его нет вовсе:
            // PModuleOnGround.Applicable = «OnGround && !Swimming»), — и от
            // 113,15 остаётся только всход в 0,6.
            //
            // ПОЧЕМУ ЗАСТАВА ОБЩАЯ, А НЕ В ОДНОЙ ВЕТКЕ. Из воды наверх ведут
            // ТРИ разные ветки: ступенька вверх, всплытие на клетку и «выбраться
            // на берег». Первый заход чинил только третью — и поиск тут же нашёл
            // обход через первые две, выдав тот же невозможный подъём другим
            // путём. Одна беда — одна застава.
            //
            // А 27.08 ЭТА ЖЕ ЗАСТАВА СТАЛА ЗНАТЬ И ПРО ОТКРЫТУЮ ВОДУ, где до
            // сих пор молчала. Под открытым небом потолка нет, мерка приходила
            // бесконечной, и выход из воды решала ОДНА ВЫДУМАННАЯ ЕДИНИЦА —
            // ручка MaxWaterExitHeight, у которой в собственной подписи стояло
            // «осторожное умолчание без источника». Теперь предел считает
            // плавучесть (Buoyancy.ReachOutOfWater), и он ЗАМЕРЕН на живых
            // модулях игры: из пруда 7/8 тело выходит на кромку 111,5 и не
            // выходит на 112,0, а из такого же пруда 6/8 не выходит уже на
            // 111,0 — уровень заполнения решает, а не глубина.
            //
            // Спрашиваем только у воды и только раз на клетку: ответ мира стоит
            // чтения чанка, а на суше эта мерка не значит ничего
            double докудаНоги = double.PositiveInfinity;
            // ВСПЛЫТИЕ МЕРЯЕТСЯ ДРУГИМ ЧИСЛОМ, И ЭТО ПОЧИНКА 27.08. Всходу и
            // вылазу по стенке нужно, ВО ЧТО УПЕРЕТЬСЯ (коробка столкновений и
            // CollidedHorizontally), а всплывают в пустую воду — см.
            // Buoyancy.AfloatReachInWater. Ценой подаренных полблока была
            // живая улика «упёрся на точке 4/29 (32, 115, 9)»: ноги держатся
            // на 114,63, в клетку 115 не попадают никогда, а поиск строил по
            // ней плывущий отрезок
            double докудаВплавь = double.PositiveInfinity;
            if (o.AllowSwimming && InWater(world, current))
            {
                докудаНоги = world.ReachOutOfWater(current.X, current.Y, current.Z);
                докудаВплавь = world.AfloatReachInWater(current.X, current.Y, current.Z);
            }

            foreach (var (dx, dz, stepCost) in steps)
            {
                int nx = current.X + dx, nz = current.Z + dz;

                // Диагональ: оба смежных прохода должны быть свободны,
                // иначе бот «протискивается» сквозь угол стены
                if (dx != 0 && dz != 0)
                {
                    if (!IsClearColumn(world, current.X + dx, current.Y, current.Z) ||
                        !IsClearColumn(world, current.X, current.Y, current.Z + dz))
                        continue;
                }

                BlockPos? next = null;
                double extraCost = 0;
                var stepKind = StepKind.Walk;

                if (IsClearColumn(world, nx, current.Y, nz))
                {
                    // Ровно или вниз: ищем первую опору — пол, воду или лестницу.
                    //
                    // СМОТРИМ ГЛУБОКО, а решаем по цене. Раньше глубина взгляда
                    // равнялась «сколько согласны спрыгнуть», и при осторожном
                    // профиле (MaxDeliberateFall = 0) бот видел вниз ровно на
                    // три блока. С башни выше трёх у поиска пути не было НИ
                    // ОДНОГО ребра вниз — и бот, забравшись, оставался наверху
                    // навсегда. Живьём это кончилось падением насмерть
                    for (int drop = 0; drop <= o.MaxDropScan; drop++)
                    {
                        int y = current.Y - drop;

                        // СКВОЗЬ ОГОНЬ НЕ ПАДАЮТ. Лава, кипяток и огонь — это
                        // не «дорого», это конец: под ними искать опору
                        // бессмысленно, тело до неё не долетит живым
                        if (world.HurtsToEnter(nx, y, nz))
                            break;

                        // Лестница: перехватываемся на неё (так заходят в шахту из тоннеля)
                        if (o.AllowClimbing && world.CanHangAt(nx, y, nz))
                        {
                            extraCost = drop > 0 ? 0.3 : 0;
                            stepKind = StepKind.Climb;
                            next = new BlockPos(nx, y, nz);
                            break;
                        }

                        // Вода: плывём по поверхности. Падение в воду безопасно —
                        // урона нет на любой высоте, только время на спуск
                        if (o.AllowSwimming && world.IsWaterSurface(nx, y, nz))
                        {
                            extraCost = stepCost * (o.SwimCostMultiplier - 1) + (drop > 0 ? 0.3 : 0);
                            stepKind = StepKind.Swim;
                            next = new BlockPos(nx, y, nz);
                            break;
                        }

                        // Под водой: проплыть вбок на своей глубине. Без этого
                        // вода для бота — плоскость: он не проходит затопленный
                        // проход и не может выплыть из-под нависшего берега
                        if (o.AllowSwimming && o.AllowDiving && drop == 0 &&
                            world.IsWater(nx, y, nz) && InWater(world, current))
                        {
                            extraCost = stepCost * (o.DiveCostMultiplier - 1);
                            stepKind = StepKind.Dive;
                            next = new BlockPos(nx, y, nz);
                            break;
                        }

                        if (world.IsStandable(nx, y, nz))
                        {
                            if (drop > o.SafeDropDistance)
                            {
                                // Дальше этого не спрыгиваем ни за какие деньги —
                                // отдельный потолок, а не глубина взгляда
                                if (drop > o.SafeDropDistance + o.MaxDeliberateFall)
                                    break;
                                double damage = EstimateFallDamage(drop);
                                if (damage > o.FallDamageBudget)
                                    break; // такой прыжок нам не по здоровью
                                // Падение быстрое, но платим здоровьем
                                extraCost = 0.5 + damage * o.BlocksPerHitPoint;
                                stepKind = StepKind.Drop;   // это СПРЫГИВАНИЕ, а не шаг
                            }
                            next = new BlockPos(nx, y, nz);
                            break;
                        }
                        // Лететь можно только сквозь свободные и безопасные клетки
                        if (!world.IsEnterable(nx, y, nz) || world.HurtsToEnter(nx, y, nz))
                            break;
                    }
                }
                else if (o.MaxStepUp > 0 &&
                         world.IsPassable(current.X, current.Y + 2, current.Z) &&
                         IsClearColumn(world, nx, current.Y + 1, nz) &&
                         world.IsStandable(nx, current.Y + 1, nz))
                {
                    // ВВЕРХ НА КЛЕТКУ — ЭТО НЕ ВСЕГДА ШАГ, И ЭТО ЖИВОЙ СЛУЧАЙ
                    // 26.08. Здесь стояло просто «клетка выше на одну — значит
                    // ступенька», и шаг уходил как StepKind.Walk. А тело для
                    // Walk прыжок не жмёт никогда: в Movement условие
                    // «step.IsJump || NeedsJumpTo», и второе отвечает только
                    // про яму впереди, да и для соседней клетки выходит сразу
                    // (dist < 1.5).
                    //
                    // Одна клетка по номеру — это что угодно по высоте. За
                    // четыре минуты прогона бот двенадцать раз упёрся и записал
                    // тринадцать клеток в ТУПИКИ, прыгнув всего шесть раз:
                    //     упёрся на точке 2/4 (46, 111, 234), стою (46,5, 109,9, 235,3)
                    // Ноги на 109,9 — он стоял на дорожной плите (верх 0,9375),
                    // а целил на целый блок: подъём 1,06 при шаге в 0,6.
                    // Хуже отказа было последствие: клетка запоминалась
                    // непроходимой, и бот сам себе портил карту.
                    //
                    // Меру берём в БЛОКАХ у самого мира (FeetY — одно правило
                    // на весь проект «где стоят ноги в этой клетке»), а решение
                    // — у физики игры (JumpPhysics.KindOfRise)
                    double rise = world.FeetY(nx, current.Y + 1, nz)
                                - world.FeetY(current.X, current.Y, current.Z);

                    // ИЗ ВОДЫ СТУПЕНЬКА ВВЕРХ ИДЁТ ЧЕРЕЗ ТУ ЖЕ ЗАСТАВУ, ЧТО И
                    // ОСТАЛЬНЫЕ ТРИ ВЕТКИ. Прежде здесь стояла застава ПОТОЛКА,
                    // и её убрали, доказав щупом, что она не срабатывает ни
                    // разу; тут же было записано: «ЕСЛИ КТО-ТО ОСЛАБИТ ТО
                    // УСЛОВИЕ — верни заставу сюда». Ослабили не условие —
                    // расширилась сама мерка: плавучесть кусается там, где
                    // потолок не кусался никогда, и первый же стенд поймал это
                    // на живой геометрии (пруд 6/8, кромка на клетку выше
                    // поверхности: три ветки отказали, а эта провела).
                    //
                    // Почему именно здесь и не хватает KindOfRise: он меряет
                    // подъём ПРЫЖКОМ, а у плывущего прыжка нет вовсе
                    // (PModuleOnGround.Applicable — «OnGround && !Swimming»)
                    if (world.FeetY(nx, current.Y + 1, nz) > докудаНоги)
                        continue;

                    switch (JumpPhysics.KindOfRise(rise))
                    {
                        case RiseKind.Step:
                            next = new BlockPos(nx, current.Y + 1, nz);
                            extraCost = 0.3;
                            break;
                        case RiseKind.Jump when o.MaxJumpUp > 0:
                            // Прыжок дороже шага — иначе бот предпочёл бы
                            // скакать там, где можно обойти по ровному
                            next = new BlockPos(nx, current.Y + 1, nz);
                            extraCost = 0.3 + o.JumpCost;
                            stepKind = StepKind.JumpUp;
                            break;
                        // Прыгать не велено или высоко даже для прыжка — сюда
                        // своими силами не подняться, и соседом эта клетка не
                        // становится. Подъём стройкой решает не поиск пути
                    }
                }
                else if (LeafCost(world, o, nx, current.Y, nz) is { } leafCost)
                {
                    // СКВОЗЬ ЛИСТВУ — ШАГОМ, А НЕ КРУГОМ. Живой игрок не
                    // обходит куст: он сносит его рукой на ходу. Клетка
                    // остаётся обычным шагом, ветки на ней ломает тело
                    next = new BlockPos(nx, current.Y, nz);
                    extraCost = leafCost;
                }

                if (next is not { } n)
                    continue;

                // ЗДЕСЬ СТОЯЛА ТРЕТЬЯ ЗАСТАВА ПОТОЛКА, И ОНА НЕ МОГЛА СРАБОТАТЬ
                // НИ РАЗУ. Убрана 27.08 по находке приёмки — не потому, что её
                // никто не сторожил, а потому, что сторожить было нечего.
                //
                // ЧЕМ ЭТО ИЗМЕРЕНО, А НЕ ВЫВЕДЕНО. Щупом в теле правила: в том
                // самом мире живой беды (пол 112, вода 113 и 114, потолок 115)
                // за один поиск раскрыто 120 водных клеток, и застава всплытия
                // сработала 60 раз, застава выхода на берег 34, А ЭТА — 0. Потом
                // она была заменена на throw и прогнан ВЕСЬ набор: 4888 тестов
                // зелёные, то есть она не срабатывает нигде.
                //
                // ПОЧЕМУ ТАК, ПО УСТРОЙСТВУ. Ступенька вверх заводится только
                // при «world.IsPassable(current.X, current.Y + 2, current.Z)»
                // (условие двумя десятками строк выше). Застава кусалась бы при
                // потолке ниже current.Y + 2,25, а потолок — это граница блока,
                // то есть current.Y + 2 или current.Y + 1. В первом случае
                // клетка current.Y + 2 и ЕСТЬ потолок, и ступенька не заводится
                // вовсе; во втором у бота потолок в собственной голове, и стоять
                // там он не может. Одна и та же плита закрывает ход раньше.
                //
                // ЕСЛИ КТО-ТО ОСЛАБИТ ТО УСЛОВИЕ — верни заставу сюда: тогда
                // ступенька из воды снова сможет целить выше макушки. Две живые
                // заставы («всплыть» и «выбраться на берег») стоят ниже, обе
                // краснеют при подмене.

                double newCost = costSoFar[current] + stepCost * GroundFactor(world, n, o) + extraCost;
                if (!costSoFar.TryGetValue(n, out double old) || newCost < old)
                {
                    costSoFar[n] = newCost;
                    cameFrom[n] = current;
                    kindOf[n] = stepKind;
                    open.Enqueue(n, newCost + Heuristic(n, to));
                }
            }

            // ВОДА — ЭТО ОБЪЁМ, А НЕ ПЛОСКОСТЬ.
            // Пока бот умел плыть только по поверхности, любая вода с нависшим
            // краем становилась ловушкой: соседние клетки «не опора», выхода
            // нет, A* возвращал null, и бот стоял в воде («не мог выйти»).
            if (o.AllowSwimming && InWater(world, current))
            {
                // Всплыть / нырнуть
                foreach (int dy in UpDown)
                {
                    int ny = current.Y + dy;
                    bool isWater = world.IsWater(current.X, ny, current.Z);
                    // Вверх можно и в воздух над водой — это выныривание
                    if (dy > 0 && !isWater && !world.IsWaterSurface(current.X, current.Y, current.Z))
                        continue;
                    if (dy < 0 && (!isWater || !o.AllowDiving))
                        continue;
                    if (dy > 0 && !isWater)
                        continue; // над поверхностью плыть некуда

                    // Глубже, чем позволяет дыхание, не уходим
                    if (dy < 0 && DepthBelowSurface(world, current.X, ny, current.Z) > o.MaxDiveDepth)
                        continue;

                    // ВЫШЕ, ЧЕМ ДЕРЖИТ ВОДА, НЕ ВСПЛЫТЬ (см. «докудаВплавь»).
                    // Без этой строки поиск обходил заставу выхода на берег:
                    // всплывал на клетку вверх ВНУТРИ затопленного хода и шагал
                    // на кромку уже «вровень» — тот же невозможный подъём
                    // другим путём
                    if (dy > 0 && world.FeetY(current.X, ny, current.Z) > докудаВплавь)
                        continue;

                    var swimTo = new BlockPos(current.X, ny, current.Z);
                    double swimCost = o.SwimCostMultiplier * (dy < 0 ? o.DiveCostMultiplier / o.SwimCostMultiplier : 1);
                    double total = costSoFar[current] + swimCost;
                    if (!costSoFar.TryGetValue(swimTo, out double prevSwim) || total < prevSwim)
                    {
                        costSoFar[swimTo] = total;
                        cameFrom[swimTo] = current;
                        kindOf[swimTo] = StepKind.Dive;
                        open.Enqueue(swimTo, total + Heuristic(swimTo, to));
                    }
                }

                // ВЫБРАТЬСЯ НА БЕРЕГ. Докуда — считает «докудаНоги», ЗАМЕРЕННАЯ
                // мерка плавучести, а не число клеток. Здесь стояло
                // «up <= o.MaxWaterExitHeight» с единицей, у которой не было
                // источника, и мерилось это КЛЕТКАМИ: одна клетка по номеру —
                // что угодно по высоте (с плиты на плиту 0,5, с дорожной плиты
                // на целый блок 1,06), да и сама единица бралась из головы.
                //
                // Обычная «ступенька вверх» сюда не попадает — она работает
                // только если соседняя колонна ЗАНЯТА, а у воды она свободна
                for (int i = 0; i < steps.Length; i++)
                {
                    var (dx, dz, stepCost) = steps[i];
                    // Выше предела номера клеток смотреть незачем: высота ног в
                    // клетке никогда не ниже её пола. Бесконечным предел здесь
                    // не бывает — эта ветка живёт под «InWater», а там он и
                    // считается (плавучесть конечна всегда)
                    for (int up = 1; current.Y + up <= докудаНоги; up++)
                    {
                        int ex = current.X + dx, ey = current.Y + up, ez = current.Z + dz;
                        if (!world.IsStandable(ex, ey, ez) || !IsClearColumn(world, ex, ey, ez))
                            continue;

                        // ВЫШЕ, ЧЕМ ПОДНИМУТ НОГИ, НЕ ВЫЛЕЗТЬ — та же застава,
                        // что и у прочих подъёмов из воды. Считается она по
                        // ВЫСОТЕ ног на кромке, а не по номеру клетки
                        if (world.FeetY(ex, ey, ez) > докудаНоги)
                            continue;

                        var exit = new BlockPos(ex, ey, ez);
                        // Выход из воды стоит дороже шага: приходится подтягиваться
                        double total = costSoFar[current] + stepCost * o.SwimCostMultiplier + up;
                        if (!costSoFar.TryGetValue(exit, out double prevExit) || total < prevExit)
                        {
                            costSoFar[exit] = total;
                            cameFrom[exit] = current;
                            kindOf[exit] = StepKind.Swim;
                            open.Enqueue(exit, total + Heuristic(exit, to));
                        }
                        break;
                    }
                }
            }

            // Лестницы: подъём и спуск по вертикали (тоннели, шахты, выходы наверх)
            if (o.AllowClimbing)
                foreach (int dy in UpDown)
                {
                    int ny = current.Y + dy;
                    // Лезем, если текущая клетка или соседняя по вертикали — лестница
                    bool onLadder = world.CanHangAt(current.X, current.Y, current.Z) ||
                                    world.CanHangAt(current.X, ny, current.Z);
                    if (!onLadder)
                        continue;
                    // И вверх, и вниз конечная клетка обязана держать: либо там
                    // лестница (висим), либо это твёрдый пол. Лезть в воздух
                    // нельзя — бот оттуда просто упадёт
                    bool holds = world.CanHangAt(current.X, ny, current.Z) ||
                                 world.IsStandable(current.X, ny, current.Z);
                    bool ok = dy > 0
                        ? world.IsEnterable(current.X, ny + 1, current.Z) && holds
                        : holds;
                    if (!ok)
                        continue;

                    var climbTo = new BlockPos(current.X, ny, current.Z);
                    double climbCost = o.ClimbCost;
                    double total = costSoFar[current] + climbCost;
                    if (!costSoFar.TryGetValue(climbTo, out double prev) || total < prev)
                    {
                        costSoFar[climbTo] = total;
                        cameFrom[climbTo] = current;
                        kindOf[climbTo] = StepKind.Climb;
                        open.Enqueue(climbTo, total + Heuristic(climbTo, to));
                    }
                }

            // Запрыгивание на висящую лестницу: нижний конец обрезан и до пола
            // не достаёт, но игрок подпрыгивает и сразу цепляется
            if (o.AllowClimbing && o.MaxLadderMountHeight > 0 &&
                world.IsStandable(current.X, current.Y, current.Z))
                foreach (var mount in LadderMounts(world, current, o))
                {
                    double newCost = costSoFar[current] + mount.cost;
                    if (!costSoFar.TryGetValue(mount.pos, out double old) || newCost < old)
                    {
                        costSoFar[mount.pos] = newCost;
                        cameFrom[mount.pos] = current;
                        kindOf[mount.pos] = StepKind.JumpToLadder;
                        open.Enqueue(mount.pos, newCost + Heuristic(mount.pos, to));
                    }
                }

            // Прыжки через ямы: перелететь пропасть часто быстрее, чем обходить.
            //
            // ПЛЫВУЩИЙ НЕ ПРЫГАЕТ ВОВСЕ — И ЭТО БЫЛА ЧЕТВЁРТАЯ ДЫРА ИЗ ВОДЫ,
            // О КОТОРОЙ ПРОЕКТ НЕ ЗНАЛ. Волна воды 27.08 писала: «Из воды
            // наверх ведут ТРИ разные ветки… Одна беда — одна застава», и
            // заставу плавучести поставила во все три. А выходов оказалось
            // ЧЕТЫРЕ: прыжок через щель зовётся здесь БЕЗУСЛОВНО, ни про воду,
            // ни про плавучесть не спрашивает, и его ветка «прыжок на уступ
            // выше» спокойно выводила плывущего на кромку выше линии воды.
            // Поймано новым стендом «Дробная_кромка_выше_плавучести…»: все три
            // заставы отказали, а эта провела — ровно так же, как в первом
            // заходе починки провела ступенька вверх.
            //
            // Ответ игры однозначен: PModuleOnGround.Applicable — это
            // «OnGround && !Swimming», то есть у плывущего прыжка нет вовсе.
            // Вброд (ноги на дне, макушка над водой) прыжок ЕСТЬ, и его тут
            // никто не отнимает
            if (o.MaxJumpDistance >= 2 && !Afloat(world, current))
                foreach (var jump in JumpNeighbours(world, current, o))
                {
                    double newCost = costSoFar[current] + jump.cost;
                    if (!costSoFar.TryGetValue(jump.pos, out double old) || newCost < old)
                    {
                        costSoFar[jump.pos] = newCost;
                        cameFrom[jump.pos] = current;
                        kindOf[jump.pos] = jump.kind;
                        open.Enqueue(jump.pos, newCost + Heuristic(jump.pos, to));
                    }
                }
        }

        LastExplored = explored;

        // Цель не достигнута: либо сдаёмся, либо отдаём путь «в её сторону»
        if (goal == null && o.AllowPartialPath && closest != from)
            goal = closest;
        // Ни одна клетка не оказалась ближе к цели — а это ровно тот случай,
        // когда бот стоял и не двигался вовсе. Человек в такой ситуации идёт
        // осматриваться, а не замирает: отдаём самый дальний разведанный путь
        if (goal == null && o.AllowPartialPath && o.AllowExploration && farthest != from)
        {
            o.OnTrace?.Invoke($"к цели не приблизиться, иду разведать до {farthest}");
            goal = farthest;
        }
        // ОТКАЗ ОБЯЗАН РАЗЛИЧАТЬ «пути нет» и «я не досмотрел». Потолок поиска
        // свой у каждого вызова, поэтому в жалобе называем и его, и сколько
        // осмотрено: иначе по журналу не понять, тесно ли в мире или тесно
        // в настройках
        if (goal == null)
            o.OnTrace?.Invoke(explored > ceiling
                ? $"осмотрел {explored} клеток и упёрся в потолок поиска ({ceiling}) — " +
                  "дальше не смотрел (NodesPerBlock/MinNodes/MaxNodes)"
                : "пути нет: все соседи хуже места, где стою");

        if (goal is not { } end)
            return null;
        if (end != from && !cameFrom.ContainsKey(end))
        {
            o.OnTrace?.Invoke($"до {end} путь не сложился — обратной дороги нет в записях");
            return null;
        }

        var path = new List<PathStep> { new(end, kindOf.GetValueOrDefault(end, StepKind.Walk)) };
        var cur = end;
        while (cameFrom.TryGetValue(cur, out var prev))
        {
            path.Add(new PathStep(prev, kindOf.GetValueOrDefault(prev, StepKind.Walk)));
            cur = prev;
        }
        path.Reverse();

        return o.SmoothPath ? Smooth(world, path, o) : path;
    }

    /// <summary>
    /// Потолок просмотренных клеток для ЭТОГО поиска.
    ///
    /// Почему потолок не может быть один на всех. Замер на стенде (ровное поле
    /// 160×160, цель наглухо заперта в ПЯТИ блоках от бота): поиск обошёл
    /// 27 620 клеток, задал миру 16,4 млн вопросов и думал 1,8 секунды — а
    /// движение зовёт его ещё раз, с частичным путём. Это и есть жалоба
    /// хозяина: «для перестроения чуть-чуть маршрута подвисает на пару секунд».
    /// Дороги на пять шагов и дороги на двести шагов нельзя мерить одной меркой.
    ///
    /// Расстояние берётся восьминаправленное (диагональ короче двух прямых) плюс
    /// вертикаль как есть — та же геометрия, что и в эвристике.
    /// </summary>
    private static int NodeCeiling(BlockPos from, BlockPos to, PathOptions o)
    {
        double dx = Math.Abs(from.X - to.X), dz = Math.Abs(from.Z - to.Z);
        double reach = Math.Max(dx, dz) + 0.414 * Math.Min(dx, dz) + Math.Abs(from.Y - to.Y);
        double ceiling = Math.Max(o.MinNodes, o.NodesPerBlock * reach);
        return (int)Math.Min(o.MaxNodes, ceiling);
    }

    /// <summary>
    /// Цена шага СКВОЗЬ ЛИСТВУ в клетку; null — так не пройти.
    ///
    /// Листва в игре непроходима, и для поиска пути она была стеной: бот
    /// обходил кусты кругами там, где живой игрок сносит ветку рукой на ходу.
    /// Платим за каждый блок, который придётся снести: под ноги и под голову —
    /// это два разных блока, и обход в два блока листвы должен стоить вдвое.
    ///
    /// Признак листвы спрашиваем у <see cref="PathOptions.IsLeaves"/>, а не по
    /// списку кодов: коды знает игра (материал блока), а всякий список врёт на
    /// первом же моде со своими деревьями. Ответчик не задан — механика спит.
    /// </summary>
    private static double? LeafCost(IPathWorld world, PathOptions o, int x, int y, int z)
    {
        if (o.IsLeaves == null || o.LeafBreakCost <= 0)
            return null;
        // ПОД НОГАМИ НУЖЕН НАСТОЯЩИЙ ПОЛ. Сквозь листву ходят, но НА листве не
        // стоят: срубив ветку под ногами, бот полетит вниз сквозь крону
        if (!world.IsSolid(x, y - 1, z))
            return null;

        int count = 0;
        for (int h = 0; h <= 1; h++)   // ноги и голова
        {
            if (world.IsEnterable(x, y + h, z))
                continue;
            if (!o.IsLeaves(x, y + h, z))
                return null;    // там не листва, а стена — рукой не снести
            count++;
        }
        return count > 0 ? count * o.LeafBreakCost : null;
    }

    /// <summary>
    /// Куда можно запрыгнуть, чтобы уцепиться за висящую лестницу: свой столб
    /// (лестница обрезана над головой) и четыре соседних. Между ботом и целью
    /// должно быть свободно — иначе он ударится головой о потолок.
    /// </summary>
    private static IEnumerable<(BlockPos pos, double cost)> LadderMounts(
        IPathWorld world, BlockPos from, PathOptions o)
    {
        foreach (var (dx, dz) in MountWays)
        {
            int nx = from.X + dx, nz = from.Z + dz;
            for (int dy = 1; dy <= o.MaxLadderMountHeight; dy++)
            {
                // Лестница прямо над головой на +1 — это обычное лазание
                if (dx == 0 && dz == 0 && dy == 1)
                    continue;

                int ny = from.Y + dy;
                if (!world.CanHangAt(nx, ny, nz))
                    continue;

                // Свободен ли подъём: своя колонна над головой и колонна цели
                bool clear = true;
                for (int y = from.Y + 1; y <= ny + 1 && clear; y++)
                {
                    if (!world.IsEnterable(nx, y, nz))
                        clear = false;
                    if ((dx != 0 || dz != 0) && y <= from.Y + 2 && !world.IsEnterable(from.X, y, from.Z))
                        clear = false; // сначала надо подпрыгнуть у себя
                }
                if (!clear)
                    continue;

                // Прыжок с зацепом дороже обычного лазания: разгон и риск промаха
                yield return (new BlockPos(nx, ny, nz), o.JumpCost + o.ClimbCost * dy);
                break; // нижний доступный рунг — самый дешёвый
            }
        }
    }

    /// <summary>Бот в этой клетке плывёт (вода на уровне ног).</summary>
    private static bool InWater(IPathWorld world, BlockPos c) => world.IsWater(c.X, c.Y, c.Z);

    /// <summary>
    /// ПЛЫВЁТ ЛИ ТЕЛО В ЭТОЙ КЛЕТКЕ (а не бредёт по дну). У плывущего нет ни
    /// прыжка, ни всхода: <c>PModuleOnGround.Applicable</c> — это
    /// «OnGround &amp;&amp; !Swimming», а <c>entity.Swimming</c> игра ставит по
    /// блоку на высоте «ноги + SwimmingOffsetY» (<c>ApplyTests</c>).
    ///
    /// Перевод на язык клеток: воды нет — не плывёт; вставать не на что —
    /// плывёт; стоит на дне, но над головой вода — тоже плывёт.
    /// </summary>
    private static bool Afloat(IPathWorld world, BlockPos c) =>
        world.IsWater(c.X, c.Y, c.Z) &&
        (!world.IsStandable(c.X, c.Y, c.Z) || world.IsWater(c.X, c.Y + 1, c.Z));

    /// <summary>
    /// На сколько блоков клетка ниже поверхности воды. Нужно, чтобы бот не
    /// уходил на глубину, с которой не успеет вынырнуть: воздуха под водой
    /// в игре на 40 секунд.
    /// </summary>
    private static int DepthBelowSurface(IPathWorld world, int x, int y, int z)
    {
        for (int up = 0; up <= 32; up++)
            if (!world.IsWater(x, y + up, z))
                return up == 0 ? 0 : up - 1;
        return 32;
    }

    /// <summary>
    /// Четыре прямые стороны. ОТДЕЛЬНЫМ ПОЛЕМ, а не срезом на каждом вызове:
    /// «AllSteps[..4]» создаёт новый массив КАЖДЫЙ раз, а зовут его на каждого
    /// принятого соседа. В моде, где поиск идёт в потоке отрисовки кадра, этот
    /// мусор виден человеку как рывок.
    /// </summary>
    private static readonly (int dx, int dz, double cost)[] Straight4 = AllSteps[..4];

    /// <summary>Длина шага наискось в блоках — корень из двух.</summary>
    private const double Diagonal = 1.4142135623730951;

    /// <summary>
    /// Куда можно прыгать: четыре стороны и четыре диагонали.
    /// Третье число — сколько БЛОКОВ пролёта приходится на одну клетку в эту
    /// сторону: наискось клетка «длиннее», и дальность прыжка это чувствует.
    /// </summary>
    private static readonly (int dx, int dz, double length)[] JumpWays =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, Diagonal), (1, -1, Diagonal), (-1, 1, Diagonal), (-1, -1, Diagonal)
    ];

    /// <summary>
    /// Те же четыре прямые стороны для прыжков. ОТДЕЛЬНЫМ ПОЛЕМ, по той же
    /// причине, что и Straight4: «JumpWays.AsSpan(0, 4).ToArray()» создавал
    /// новый массив на КАЖДЫЙ узел поиска, а узлов в тупике под тридцать тысяч.
    /// </summary>
    private static readonly (int dx, int dz, double length)[] JumpWays4 = JumpWays[..4];

    /// <summary>
    /// Куда смотреть в поисках висящей лестницы: свой столб и четыре соседних.
    /// Тоже отдельным полем — этот список перебирается на каждом узле.
    /// </summary>
    private static readonly (int dx, int dz)[] MountWays =
        [(0, 0), (1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Вверх и вниз — тоже без создания массива на каждом узле.</summary>
    private static readonly int[] UpDown = [1, -1];

    private static bool IsClearColumn(IPathWorld world, int x, int y, int z) =>
        world.IsEnterable(x, y, z) && world.IsEnterable(x, y + 1, z);

    /// <summary>
    /// Прыжки через провалы: по 4 сторонам ищем площадку в 2–MaxJumpDistance
    /// клетках, если между нами и ею нет опоры (иначе дешевле дойти шагом).
    /// Приземление — на своём уровне или чуть ниже.
    /// </summary>
    private static IEnumerable<(BlockPos pos, double cost, StepKind kind)> JumpNeighbours(
        IPathWorld world, BlockPos from, PathOptions o)
    {
        // Прыгать можно, только если над головой есть место на замах.
        // Спрашиваем «пройти можно?», а не «пусто?»: у лестницы сплошная
        // коллизия, и стоя у её подножия бот терял ВСЕ прыжки разом
        if (!world.IsEnterable(from.X, from.Y + 2, from.Z))
            yield break;

        // С висения на лестнице прыгают без импульса — очень коротко.
        // Стоя НА ТОРЦЕ лестницы (клетка над её верхом — твёрдая опора),
        // прыжок обычный наземный: без разбега сзади он и так ограничится
        // тремя клетками ниже, а с разбегом здесь не бывает
        // ВИСЕТЬ и СТОЯТЬ В КЛЕТКЕ С ЛЕСТНИЦЕЙ — разное. У подножия и у торца
        // лестницы ноги на твёрдом полу, и прыжок там обычный, наземный.
        //
        // Спрашивать про это через IsStandable НЕЛЬЗЯ, и на этом мы уже
        // обожглись: у настоящей лестницы коллизия в целый блок, поэтому
        // IsPassable в её клетке ложь, а с ней ложь и IsStandable — ДАЖЕ
        // когда ноги стоят на твёрдом полу. Выходило, что у подножия любой
        // лестницы бот считался висящим и терял ВСЕ прыжки разом.
        // Спрашиваем прямо: есть ли под ногами твердь
        bool hanging = world.CanHangAt(from.X, from.Y, from.Z) &&
                       !world.IsSolid(from.X, from.Y - 1, from.Z);
        int maxDistance = hanging ? o.MaxLadderJumpDistance : o.MaxJumpDistance;
        if (maxDistance < 2)
            yield break;

        var ways = o.AllowDiagonals ? JumpWays : JumpWays4;

        // ПРЫГАТЬ НЕ ЧЕРЕЗ ЧТО — и это надо понять ДО перебора.
        //
        // Ниже каждый прыжок отвергается, как только на пути нашлась опора
        // («там есть пол: дойдём пешком»). Значит, если опора есть у ВСЕХ
        // соседей разом — то есть бот стоит посреди ровного места, — вся
        // работа перебора заведомо впустую. А работа немалая: на замере
        // ровного поля из 550 вопросов к миру на один узел поиска 480
        // приходилось ровно на этот перебор. Восемь вопросов вместо
        // четырёхсот восьмидесяти — вот откуда взялись «пара секунд»
        bool anyGap = false;
        foreach (var (dx, dz, _) in ways)
            if (!world.IsSupport(from.X + dx, from.Y, from.Z + dz))
            {
                anyGap = true;
                break;
            }
        if (!anyGap)
            yield break;

        // ПРЯМО И ПО ДИАГОНАЛИ. Раньше прыжки считались только по четырём
        // сторонам, и угол щели бот обойти не мог: пропасть, которую человек
        // перемахивает наискось одним движением, для бота была стеной.
        foreach (var (dx, dz, length) in ways)
        {
            bool diagonal = dx != 0 && dz != 0;

            // УГЛЫ НА ВЗЛЁТЕ. По диагонали тело идёт между двух блоков и
            // задевает оба: если хоть один из соседей по осям непроходим,
            // прыжок в игре не выйдет, как бы красиво он ни считался на бумаге
            if (diagonal &&
                (!IsClearColumn(world, from.X + dx, from.Y, from.Z) ||
                 !IsClearColumn(world, from.X, from.Y, from.Z + dz)))
                continue;
            // РАЗБЕГ СЧИТАЕТСЯ ДЛЯ КАЖДОГО НАПРАВЛЕНИЯ ОТДЕЛЬНО, а не берётся
            // из общей настройки: от длины разбега зависит дальность прыжка.
            // С места это 3 клетки, с полутора блоков разгона — уже 5.
            // Висящий на лестнице не отталкивается вовсе
            double runUp = hanging ? 0 : RunUpBlocks(world, from, dx, dz, o.MaxRunUpCells);
            double reach = hanging
                ? o.MaxLadderJumpDistance
                : JumpPhysics.ReachWithRunUp(runUp);
            // ДИАГОНАЛЬ ДЛИННЕЕ. Клетка наискось — это 1.41 блока пролёта,
            // а не один: делить дальность на длину шага обязательно, иначе
            // бот «долетит» по расчёту и не долетит на деле
            int reachable = Math.Min(maxDistance, (int)Math.Floor(reach / length));

            // ЧТО УЖЕ ЗНАЕМ ПРО ТРАЕКТОРИЮ — то заново не считаем. Прицелы
            // 2, 3, 4, 5 клеток летят над ОДНИМИ И ТЕМИ ЖЕ клетками, и раньше
            // каждый следующий прицел перепроверял их все с начала: на пять
            // клеток это десять осмотров вместо четырёх. Теперь на каждом
            // шаге осматривается ровно одна новая клетка — прошлый прицел
            bool anySupport = false, fireUnder = false;
            int pit = 0, pitDone = 0;   // яму меряем лениво: она нужна не всегда

            for (int dist = 2; dist <= reachable; dist++)
            {
                int lx = from.X + dx * dist, lz = from.Z + dz * dist;

                // Все клетки между стартом и приземлением должны быть пролетаемы
                // и БЕЗ опоры — иначе это обычная дорога, а не пропасть.
                // Новая среди них ровно одна: прошлая точка прицела
                int step = dist - 1;
                int mx = from.X + dx * step, mz = from.Z + dz * step;
                // Под траекторией лава? Промах здесь стоит не здоровья,
                // а жизни — и «пропасть поглубже» тут не мерка
                if (DangerBelow(world, mx, from.Y, mz, o.MaxDropScan))
                    fireUnder = true;
                if (!IsClearColumn(world, mx, from.Y, mz) ||
                    !world.IsEnterable(mx, from.Y + 2, mz))
                    break;      // стена на пути — дальше в эту сторону не прыгнуть
                if (world.IsSupport(mx, from.Y, mz))
                    anySupport = true;
                if (anySupport)
                    continue;   // там есть пол: дойдём пешком, прыжок не нужен

                // ЗАПАС ПРЫЖКА — вот что решает. Прыжок с запасом в полтора
                // блока не становится опасным оттого, что под ним пропасть:
                // тело летит по физике игры, а не по удаче. Опасен прыжок
                // НА ПРЕДЕЛЕ — вот его и меряем глубиной ямы.
                //
                // Раньше здесь стояло вето по одной лишь глубине, и бот
                // отказывался перепрыгнуть двухблочную щель над оврагом
                // в пять блоков. Это и есть «стал бояться перепрыгивать»
                double margin = JumpPhysics.Margin(reach, dist);
                double missChance = JumpPhysics.MissChance(margin, o.SafeJumpMargin);
                // Шум округления — не риск: см. NegligibleMissChance
                if (missChance < o.NegligibleMissChance)
                    missChance = 0;

                // ЛОВЯТ ЛИ НАС ВНИЗУ. Прыжок на лестницу — особый случай:
                // промахнувшись, тело не летит на дно, а цепляет ту же плиту
                // ниже, потому что лестница — это стена во всю высоту, а не
                // пятачок. Именно так игрок и перескакивает между столбами
                // лестниц в шахте: риск там не «двадцать блоков вниз», а
                // «съеду на пару ступеней». Без этого бот честно, но неверно
                // считал такой прыжок смертельным и отказывался наотрез
                bool caughtByLadder = o.AllowClimbing && LadderPlaneBelow(world, lx, from.Y, lz);

                // НАД ОГНЁМ ПРЫГАЕМ ТОЛЬКО НАВЕРНЯКА. Заказчик прав: лаву и
                // кипяток надо приравнять к пропасти, и даже строже —
                // из пропасти можно выбраться, из лавы нет. Поэтому здесь
                // не «цена промаха», а простое правило: есть хоть какой-то
                // шанс не долететь — не прыгаем вовсе
                if (fireUnder && missChance > 0)
                    continue;

                double risk = 0;
                if (missChance > 0 && !caughtByLadder)
                {
                    // Яму под траекторией домеряем ровно на новые клетки:
                    // прицелы растут, а промеренное остаётся промеренным
                    while (pitDone < dist - 1)
                    {
                        pitDone++;
                        pit = Math.Max(pit, PitDepthAt(world,
                            from.X + dx * pitDone, from.Y, from.Z + dz * pitDone));
                    }
                    double damage = JumpPhysics.FallDamageAfterJump(pit);
                    if (damage > o.FallDamageBudget)
                        continue;   // на пределе, а промах не по здоровью — обходим
                    risk = missChance * damage * o.BlocksPerHitPoint;
                }
                else if (missChance > 0)
                {
                    // Цена всё же есть: съехать вниз и лезть заново
                    risk = missChance * 2;
                }

                // ПРЫЖОК НА УСТУП ВЫШЕ — отдельной проверкой, ДО спуска.
                //
                // Внутрь общего цикла его класть нельзя, и это стоило нам
                // сломанных прыжков через пропасть: в цикле стоит «нет
                // свободной колонны — break», и клетка над площадкой (навес,
                // ветка, лестница, стена) обрывала перебор ещё до того, как
                // проверялась посадка на своём уровне.
                if (o.MaxJumpUp > 0 && dist <= reach * JumpPhysics.UpwardReachFraction)
                {
                    for (int rise = 1; rise <= o.MaxJumpUp; rise++)
                    {
                        int uy = from.Y + rise;
                        if (!IsClearColumn(world, lx, uy, lz))
                            break;
                        bool catchUp = o.AllowClimbing && world.CanHangAt(lx, uy, lz);
                        if (world.IsStandable(lx, uy, lz) || catchUp)
                        {
                            // Вверх и через щель — дороже обычного прыжка:
                            // промахнуться легче, а падать потом дальше
                            yield return (new BlockPos(lx, uy, lz),
                                dist + o.JumpCost + risk + 0.5 + (catchUp ? 0.7 : 0),
                                catchUp ? StepKind.JumpToLadder : StepKind.JumpUp);
                            break;
                        }
                    }
                }

                // Приземление: на своём уровне или на 1–2 ниже. Уцепиться за
                // лестницу в полёте — это тоже приземление: игрок так
                // перепрыгивает с уступа на лестницу и с грани на грань
                for (int drop = 0; drop <= 2; drop++)
                {
                    int ly = from.Y - drop;
                    if (!IsClearColumn(world, lx, ly, lz))
                        break;
                    bool ladderCatch = o.AllowClimbing && world.CanHangAt(lx, ly, lz);
                    if (world.IsStandable(lx, ly, lz) || ladderCatch ||
                        (o.AllowSwimming && world.IsWaterSurface(lx, ly, lz)))
                    {
                        // УГЛЫ НА ПРИЗЕМЛЕНИИ — та же беда с другого конца:
                        // влетая наискось, тело проходит мимо двух блоков
                        if (diagonal &&
                            (!IsClearColumn(world, lx - dx, ly, lz) ||
                             !IsClearColumn(world, lx, ly, lz - dz)))
                            break;
                        // За лестницу цепляться сложнее, чем встать на землю
                        yield return (new BlockPos(lx, ly, lz),
                            dist * length + o.JumpCost + risk + (ladderCatch ? 0.7 : 0),
                            ladderCatch ? StepKind.JumpToLadder : StepKind.Jump);
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Сколько блоков ровной земли позади точки отрыва — столько бот и
    /// разгонится. Считаем по прямой назад, пока идёт опора на своём уровне.
    /// </summary>
    private static double RunUpBlocks(IPathWorld world, BlockPos from, int dx, int dz, int maxCells)
    {
        int cells = 0;
        while (cells < maxCells &&
               world.IsStandable(from.X - dx * (cells + 1), from.Y, from.Z - dz * (cells + 1)))
            cells++;
        return cells;
    }

    /// <summary>
    /// Есть ли под точкой приземления ЛЕСТНИЧНАЯ СТЕНА — то, за что тело
    /// зацепится, даже если немного не долетит. Смотрим на несколько клеток
    /// вниз: этого хватает, чтобы отличить «шахта с лестницей» от «обрыв».
    /// </summary>
    private static bool LadderPlaneBelow(IPathWorld world, int x, int y, int z)
    {
        for (int d = 0; d <= 3; d++)
            if (world.CanHangAt(x, y - d, z))
                return true;
        return false;
    }

    /// <summary>
    /// Вредная клетка вплотную (по сторонам, сверху и снизу): лава, кипяток,
    /// огонь, горящий костёр. Вопрос про соседей, а не про саму клетку: рядом
    /// с огнём тесно, и толчок мобом стоит жизни.
    /// </summary>
    private static bool FireNextTo(IPathWorld world, BlockPos c)
    {
        if (world.HurtsToEnter(c.X, c.Y, c.Z) || world.HurtsToEnter(c.X, c.Y + 1, c.Z))
            return true;
        foreach (var (dx, dz, _) in Straight4)
            if (world.HurtsToEnter(c.X + dx, c.Y, c.Z + dz) ||
                world.HurtsToEnter(c.X + dx, c.Y - 1, c.Z + dz))
                return true;
        return world.HurtsToEnter(c.X, c.Y - 1, c.Z);
    }

    /// <summary>
    /// Есть ли под клеткой вредная клетка в пределах падения. Смотрим вниз
    /// до первой опоры: если раньше неё встретились лава или огонь — падать
    /// туда нельзя ни за какие деньги.
    /// </summary>
    private static bool DangerBelow(IPathWorld world, int x, int y, int z, int scan)
    {
        for (int d = 0; d <= scan; d++)
        {
            if (world.HurtsToEnter(x, y - d, z))
                return true;
            if (world.IsStandable(x, y - d, z) || world.IsSupport(x, y - d, z))
                return false;   // приземлимся раньше
        }
        return false;
    }

    /// <summary>
    /// Глубина ямы под ОДНОЙ клеткой траектории (32 — «бездна»). По клетке,
    /// а не по всей траектории: прицелы прыжка растут от двух клеток к пяти
    /// над одними и теми же местами, и промерять их заново на каждый прицел —
    /// та же лишняя работа, из-за которой перестроение маршрута подвисало.
    /// </summary>
    private static int PitDepthAt(IPathWorld world, int x, int y, int z)
    {
        int deep = 0;
        while (deep < 32 && !world.IsSupport(x, y - deep, z))
            deep++;
        return deep;
    }

    /// <summary>
    /// Во сколько раз шаг в эту клетку дороже обычного. По тропинке игрок
    /// идёт быстрее — значит шаг дешевле; по рыхлому и вязкому дороже.
    /// Отдельная скидка — рукотворным дорогам, где игра скорость не меняет.
    /// Так маршрут сам держится дорог, вместо того чтобы ломиться напрямик.
    /// </summary>
    private static double GroundFactor(IPathWorld world, BlockPos cell, PathOptions o)
    {
        double factor = 1;
        if (o.PreferFastGround)
        {
            // СПРАШИВАЕМ КЛЕТКУ НОГ, А НЕ ТУ, ЧТО ПОД НЕЙ. На ровной земле это
            // одно и то же; на снегу — нет: ноги стоят В СНЕГУ, и тормозит
            // именно он, а «клетка ниже» показывает на закопанное полотно
            float speed = world.StandingWalkSpeed(cell.X, cell.Y, cell.Z);
            if (speed > 0.05f)
                factor /= speed;
        }
        if (o.RoadCostFactor < 1 && world.IsRoadLike(cell.X, cell.Y - 1, cell.Z))
            factor *= o.RoadCostFactor;
        // Закрытая дверь: пройти можно, но на открывание уйдёт время
        if (world.IsDoor(cell.X, cell.Y, cell.Z) && !world.IsDoorOpen(cell.X, cell.Y, cell.Z))
            factor *= 1 + o.DoorCost;
        // ОГОНЬ ПОД БОКОМ. Идти впритирку к лаве живой игрок избегает: шаг в
        // сторону, толчок мобом, обвал — и всё кончено. Клетка проходима,
        // но дорога через неё должна быть дорогой, чтобы обход выигрывал
        if (o.LavaProximityCost > 1 && FireNextTo(world, cell))
            factor *= o.LavaProximityCost;

        // Память тупиков и прочие внешние штрафы
        if (o.ExtraCostAt != null)
            factor *= o.ExtraCostAt(cell.X, cell.Y, cell.Z);
        return factor;
    }

    /// <summary>
    /// Оценка «сколько ещё осталось». Обязана быть НЕ БОЛЬШЕ настоящей цены —
    /// иначе A* начнёт срезать верные пути.
    ///
    /// Подъём и спуск оценены по-разному, и это не придирка. Раньше вертикаль
    /// весила 0.3 в обе стороны, а подъём по лестнице стоит 2.0 — оценка врала
    /// в шесть раз. Из-за этого «стоять прямо под целью на двенадцать блоков
    /// ниже» выглядело почти достижением, а шаг ВБОК, к подножию лестницы, —
    /// ухудшением. Живьём: «!иди 38 144 412» → «ближе 13 бл не подобрался».
    /// </summary>
    private static double Heuristic(BlockPos a, BlockPos b)
    {
        // Восьминаправленная эвристика: диагональ дешевле двух прямых шагов
        double dx = Math.Abs(a.X - b.X), dz = Math.Abs(a.Z - b.Z);
        double up = Math.Max(0, b.Y - a.Y);     // цель выше — надо лезть
        double down = Math.Max(0, a.Y - b.Y);   // цель ниже — можно просто упасть
        return Math.Max(dx, dz) + 0.414 * Math.Min(dx, dz) + up * 1.0 + down * 0.3;
    }

    /// <summary>
    /// Насколько клетка ХОРОША как «почти цель». Здесь вертикаль считается по
    /// настоящей цене подъёма: оказаться под целью и оказаться у цели — совсем
    /// не одно и то же, и частичный путь не должен этого путать.
    /// </summary>
    private static double GoalScore(BlockPos a, BlockPos b, PathOptions o)
    {
        double dx = Math.Abs(a.X - b.X), dz = Math.Abs(a.Z - b.Z);
        double up = Math.Max(0, b.Y - a.Y);
        double down = Math.Max(0, a.Y - b.Y);
        // Спуск дёшев, только пока бот СОГЛАСЕН падать. При осторожном профиле
        // (MaxDeliberateFall = 0) слезть можно лишь по лестницам и уступам, и
        // «оказаться на двадцать блоков ВЫШЕ цели» — это не «почти дошёл», а
        // крыша без выхода. Раньше такая клетка выигрывала конкурс «ближайшая
        // к цели», и частичный путь вёл бота ровно туда, откуда он не слезет
        double downCost = o.MaxDeliberateFall > 0 ? 0.5 : o.ClimbCost;
        return Math.Max(dx, dz) + 0.414 * Math.Min(dx, dz) + up * o.ClimbCost + down * downCost;
    }

    /// <summary>
    /// Спрямление: выкидываем промежуточные клетки, если между опорными
    /// есть прямой проход. Убирает «лесенки» — бот бежит длинными прямыми.
    ///
    /// ЧЕМ МЕРЯЕТСЯ ПРОХОДИМОСТЬ ОТРЕЗКА — <see cref="BodyCorridor.Straight"/>,
    /// и это КОРОБКА ТЕЛА, а не нить по центрам клеток. Нитью мерили раньше, и
    /// стоило это живого зависания: «упёрся на точке 4/4 … до точки было 31 бл
    /// по прямой — её поставило спрямление, а тело там не проходит» (19.08).
    /// Прямая шла сквозь постройку: серединой проёма нить проходила, а плечом
    /// тело упиралось в косяк.
    ///
    /// ЭТО ВСЁ ЕЩЁ ПРЕДПОЛОЖЕНИЕ, А НЕ ГАРАНТИЯ, и знать это обязан тот, кто
    /// маршрут исполняет: правило спрашивает мир КЛЕТКАМИ (пятнадцать вопросов
    /// <see cref="IPathWorld"/>), а у тела есть коробки частичных блоков,
    /// которых в этих вопросах нет; да и мир между планом и ходьбой меняется.
    /// Поэтому движение обязано уметь ОТКАЗАТЬСЯ от спрямления: не дошёл до
    /// спрямлённой точки — строй заново с SmoothPath = false (см.
    /// <c>Walker.IsStraightenedLeg</c> и Movement).
    ///
    /// СПРЯМЛЕНИЕ НЕ ВЕДЁТ ТЕЛО ВПЛОТНУЮ К ОГНЮ — и вот почему это отдельный
    /// абзац. Поиск пути обходит клетки рядом с огнём, назначая им цену
    /// (<see cref="GroundFactor"/>, <c>LavaProximityCost</c>), а спрямление
    /// срезало углы и вело тело ВПЛОТНУЮ к костру: тело-то проходит. Живая
    /// цена этой дыры известна: бот сгорел в собственном костре и потерял 48
    /// стопок вещей.
    ///
    /// ПРАВИЛО ЗДЕСЬ ОДНО И ПРОСТОЕ: <b>вплотную к огню тело идёт только по
    /// клеткам, которые выбрал САМ ПОИСК ПУТИ. Своих клеток у огня спрямление
    /// не изобретает.</b> Это не запрет и не трусость: раз поиск уже согласился
    /// платить восьмикратную цену за последние шаги к очагу — по ним тело и
    /// пойдёт, и греться (<c>BehaviorKeepWarm</c>) и готовить бот не разучится.
    ///
    /// ПОЧЕМУ НЕ «НЕ ДОРОЖЕ ВЫБРОШЕННОГО», КАК БЫЛО. Прежнее правило считало
    /// потолком худшую ЦЕНУ на участке — и приёмка 27.08 показала щупом, что
    /// такой потолок снимается сам собой сразу двумя способами:
    ///   • опорной точкой участка служит клетка, где бот СТОИТ, а <c>KeepWarm</c>
    ///     ставит его к очагу на 0,8…1,6 блока: вышел от своего костра — и
    ///     потолок 8 для КАЖДОЙ пробы, застава молчит на всём отрезке. Это
    ///     дословно та последовательность, что стоила 48 стопок;
    ///   • память тупиков (<c>PathOptions.ExtraCostAt</c>,
    ///     <c>Movement.StubbornCellFactor</c> = 100) входит в ту же цену
    ///     сомножителем: одна упрямая клетка на маршруте — и потолок 100.
    ///     Ровно тогда, когда бот суетится и лезет не туда, сторож и выключался.
    /// Цена и вред — разные вещи, и мешать их в одно число нельзя: скорость
    /// земли, скидка дороге, время на дверь и память тупиков к ожогу отношения
    /// не имеют. Поэтому спрашивается ровно то, чего боимся, —
    /// <see cref="FireNextTo"/>, тот же самый вопрос, которым мерит и
    /// <see cref="GroundFactor"/>.
    /// </summary>
    private static List<PathStep> Smooth(IPathWorld world, List<PathStep> path, PathOptions o)
    {
        if (path.Count <= 2)
            return path;

        // КЛЕТКИ, КОТОРЫЕ ВЫБРАЛ ПОИСК ПУТИ. Только по ним телу дозволено идти
        // впритирку к огню — за них платил A*, зная про надбавку
        var выбранные = new HashSet<BlockPos>(path.Count);
        foreach (var s in path)
            выбранные.Add(s.Pos);

        // ПАМЯТЬ ОТВЕТА ПРО ОГОНЬ НА ВЕСЬ МАРШРУТ, и она тут не украшение:
        // один FireNextTo — это пять вопросов к миру, а коридоры соседних проб
        // накладываются друг на друга десятками раз. Прошлая волна не полезла
        // в эту проверку именно из страха за скорость (числа и замер — в
        // PathSpeedTests)
        var known = new Dictionary<BlockPos, bool>();
        bool УОгня(BlockPos cell)
        {
            if (!known.TryGetValue(cell, out bool f))
                known[cell] = f = FireNextTo(world, cell);
            return f;
        }

        // Надбавки нет вовсе — значит роль велела идти напрямик, и заставе
        // тут делать нечего: механика в библиотеке, безрассудство в роли.
        //
        // ЭТО ЭКОНОМИЯ РАБОТЫ, А НЕ ПЕРЕКЛЮЧАТЕЛЬ ПОВЕДЕНИЯ, И ЭТО ИЗМЕРЕНО,
        // А НЕ ВЫВЕДЕНО (27.08, по находке приёмки). Условие заменили на
        // «застава стоит ВСЕГДА» и прогнали ВЕСЬ набор: 4889 тестов зелёные,
        // ни один маршрут не изменился. Причина по устройству: застава
        // отвергает только клетки, КОТОРЫХ САМ A* НЕ ВЫБИРАЛ, а при цене 1 он
        // к огню слеп — значит все огневые клетки на прямой он и так выбрал.
        //
        // ЧЕГО ОТСЮДА НЕЛЬЗЯ ЗАКЛЮЧАТЬ: что разницы не бывает вовсе. A* может
        // обойти огневую клетку и по ДРУГОЙ причине (снег, дверь, память
        // тупиков) — тогда при цене 1 застава её всё же отвергнет. Такой
        // местности ни один стенд не строит, и я её не строил: говорю вслух,
        // что эта ветка сторожится замером «разницы нет», а не стендом.
        Func<int, int, int, bool>? нельзяТуда = o.LavaProximityCost <= 1 ? null
            : (x, y, z) =>
            {
                var cell = new BlockPos(x, y, z);
                return УОгня(cell) && !выбранные.Contains(cell);
            };

        var result = new List<PathStep> { path[0] };
        int anchor = 0;
        while (anchor < path.Count - 1)
        {
            int furthest = anchor + 1;

            // ХОДЫ НЕ СПРЯМЛЯЮТСЯ. Прыжок, зацеп за лестницу и спрыгивание —
            // это отдельные действия тела, и склеивать их с соседними шагами
            // в «длинную прямую» нельзя: план останется, а действие пропадёт
            if (path[anchor + 1].Kind != StepKind.Walk)
            {
                result.Add(path[anchor + 1]);
                anchor++;
                continue;
            }
            // Лестничные клетки — ОБЯЗАТЕЛЬНЫЕ точки маршрута. Иначе спрямление
            // выбрасывает клетку торца («долезть до верха»), бот получает план
            // «с середины лестницы сразу на дальний пол» и прыгает прямо
            // во время лазания вместо того, чтобы долезть и шагнуть
            int limit = anchor + 1;
            while (limit < path.Count - 1 && !IsLadderCell(world, path[limit]))
                limit++;

            // Дальше опорной точки со «своим» ходом не заглядываем
            while (limit > anchor + 1 && path[limit].Kind != StepKind.Walk)
                limit--;

            for (int probe = limit; probe > anchor + 1; probe--)
            {
                if (BodyCorridor.Straight(world, path[anchor], path[probe], o.IsLeaves, нельзяТуда))
                {
                    furthest = probe;
                    break;
                }
            }
            result.Add(path[furthest]);
            anchor = furthest;
        }
        return result;
    }

    /// <summary>Клетка связана с лестницей: вишу в ней или стою на её торце.</summary>
    private static bool IsLadderCell(IPathWorld world, BlockPos c) =>
        world.CanHangAt(c.X, c.Y, c.Z) || world.IsClimbable(c.X, c.Y - 1, c.Z);

}
