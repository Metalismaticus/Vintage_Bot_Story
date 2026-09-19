namespace VsBotKit;

/// <summary>
/// КТО НАСКОЛЬКО БЫСТР — ЗАМЕР, А НЕ ДОГАДКА.
///
/// Зачем понадобилось. Заказчик просит решать бой числами: «догоню ли
/// (скорость, расстояние)». Скорость моба не приходит ни одним пакетом:
/// движение мобов считает СЕРВЕР, а их настройки (aitasks → movespeed) живут в
/// серверной половине описания сущности и клиенту не рассылаются вовсе. Зато
/// сервер шлёт их ПОЛОЖЕНИЯ, и по ним скорость меряется так же, как её видит
/// живой игрок: смотрит, догоняет его тварь или отстаёт.
///
/// Меряем ЛУЧШЕЕ, что видели за последние секунды, а не «сколько прошёл с
/// прошлого замера». Причина простая: моб то стоит, то бросается. Мгновенный
/// ноль у стоящего дрифтера означал бы «я его точно догоню», а через полсекунды
/// он вцепляется в бота. Для вопроса «догоню ли» верен именно ПОТОЛОК.
///
/// МЕРЯЕМ ИЗ ПОТОКА ПАКЕТОВ, А НЕ ИЗ ХОДА СПОСОБНОСТИ, и поэтому здесь замок.
/// Живой случай 19.08: замер стоял в тике боя, а тик боя между двумя стычками
/// приходил через три и через двадцать восемь секунд — оба промежутка длиннее
/// <see cref="MaxGapSeconds"/>, и НИ ОДНОГО замера не набралось. Бот отказывал
/// себе и в драке, и в бегстве одними и теми же словами: «скорость его ещё не
/// мерил». Положения приходят в потоке чтения пакетов (EntityModel.OnEntityMoved),
/// спрашивают их из хода способности — то есть из двух потоков.
/// </summary>
public sealed class CombatPace
{
    private readonly object gate = new();
    private readonly Dictionary<long, Track> tracks = [];

    private sealed class Track
    {
        public double X, Z;
        public DateTime At;
        public bool Started;
        public readonly List<(DateTime At, double Speed)> Seen = [];

        /// <summary>
        /// САМИ ПОЛОЖЕНИЯ, а не только скорости — для <see cref="Drift"/>.
        /// Скорость это ДЛИНА, и стрельбе её мало: чтобы взять упреждение,
        /// надо знать ещё и КУДА цель идёт. Держим тот же поток замеров, что
        /// и скорости, — второго наблюдения за одной тварью в проекте быть не
        /// должно.
        /// </summary>
        public readonly List<(DateTime At, double X, double Z)> Path = [];
    }

    /// <summary>Сколько секунд помнить замеры (дальше они уже не про сейчас).</summary>
    public double MemorySeconds { get; set; } = 6;

    /// <summary>
    /// Короче этого промежутка замер не берём: между двумя посылками позиций
    /// проходит доля секунды, и деление на неё превращает дрожание в «двадцать
    /// блоков в секунду».
    /// </summary>
    public double MinGapSeconds { get; set; } = 0.2;

    /// <summary>
    /// Длиннее этого — тоже не берём: за пропущенную секунду цель могла
    /// свернуть, и прямая между точками короче настоящего пути.
    /// </summary>
    public double MaxGapSeconds { get; set; } = 2.0;

    /// <summary>
    /// Быстрее этого никто не бегает — значит, это телепорт, подгрузка чанка
    /// или скачок позиции. Спринт игрока около 6,8 бл/с, так что запас велик.
    /// </summary>
    public double ImpossibleSpeed { get; set; } = 20;

    /// <summary>Сколько замеров держать на одну цель.</summary>
    public int Depth { get; set; } = 24;

    /// <summary>
    /// Отметить, где цель находится сейчас. Высоту НЕ считаем: догоняют по
    /// земле, а падение и прыжок к скорости погони отношения не имеют.
    /// </summary>
    public void Sample(long id, double x, double z, DateTime now)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(id, out var t))
            {
                // Целей за день набегает много; чистим разом, когда набралось
                // явно больше, чем может быть врагов вокруг
                if (tracks.Count > 256)
                    tracks.Clear();
                tracks[id] = t = new Track();
            }

            if (t.Started)
            {
                double dt = (now - t.At).TotalSeconds;
                double moved = Math.Sqrt((x - t.X) * (x - t.X) + (z - t.Z) * (z - t.Z));
                if (dt >= MinGapSeconds && dt <= MaxGapSeconds)
                {
                    double speed = moved / dt;
                    if (speed <= ImpossibleSpeed)
                    {
                        t.Seen.Add((now, speed));
                        while (t.Seen.Count > Math.Max(2, Depth))
                            t.Seen.RemoveAt(0);
                    }
                }
                else if (dt < MinGapSeconds)
                {
                    return;   // слишком часто: копим до следующего раза
                }
            }

            t.X = x;
            t.Z = z;
            t.At = now;
            t.Started = true;

            // Путь копим тем же потоком и той же глубиной: он и есть источник
            // упреждения для лука (см. Drift)
            t.Path.Add((now, x, z));
            while (t.Path.Count > Math.Max(2, Depth))
                t.Path.RemoveAt(0);
        }
    }

    /// <summary>То же самое для сущности, как её видит бот.</summary>
    public void Sample(EntityInfo e, DateTime now) => Sample(e.Id, e.X, e.Z, now);

    /// <summary>
    /// Лучшая скорость цели за память, бл/с. null — замеров нет, и врать про
    /// «догоню» нечем: так и надо сказать.
    /// </summary>
    public double? TopSpeed(long id, DateTime now)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(id, out var t))
                return null;
            double since = MemorySeconds;
            double top = -1;
            foreach (var (at, speed) in t.Seen)
                if ((now - at).TotalSeconds <= since && speed > top)
                    top = speed;
            return top >= 0 ? top : null;
        }
    }

    /// <summary>
    /// КУДА И КАК БЫСТРО ИДЁТ ЦЕЛЬ, блоков в секунду по каждой оси. Нули —
    /// замеров мало или цель стоит.
    ///
    /// ЗАЧЕМ ВЕКТОР, КОГДА ЕСТЬ <see cref="TopSpeed"/>. Драка спрашивает «догоню
    /// ли» — ей хватает длины. Лук спрашивает другое: стрела летит до цели
    /// 0,2–0,7 с, и за это время дрифтер уходит на метр вбок. Без НАПРАВЛЕНИЯ
    /// упреждение взять нечем, а без упреждения бот мажет по всему, что не
    /// стоит на месте.
    ///
    /// СРЕДНЕЕ ПО ОКНУ, А НЕ ПОСЛЕДНИЙ ШАГ, и это принципиально. Положения
    /// приходят рывками, и «последний шаг» у стоящей твари даёт то ноль, то
    /// пять блоков в секунду — упреждение по такому числу уводит стрелу дальше,
    /// чем спасает. Берём смещение от начала окна до конца, делённое на его
    /// длину: рывки гасятся, а настоящий бег виден.
    ///
    /// ОКНО КОРОТКОЕ НАРОЧНО (<see cref="DriftSeconds"/>): тварь меняет
    /// направление за полсекунды, и память в шесть секунд, годная для «кто
    /// быстрее», для «куда он бежит прямо сейчас» врала бы усреднением
    /// метаний в ноль.
    /// </summary>
    public (double X, double Z) Drift(long id, DateTime now)
    {
        lock (gate)
        {
            if (!tracks.TryGetValue(id, out var t) || t.Path.Count < 2)
                return (0, 0);

            (DateTime At, double X, double Z)? first = null;
            (DateTime At, double X, double Z)? last = null;
            foreach (var p in t.Path)
            {
                if ((now - p.At).TotalSeconds > DriftSeconds)
                    continue;
                first ??= p;
                last = p;
            }
            if (first is not { } a || last is not { } b)
                return (0, 0);

            double dt = (b.At - a.At).TotalSeconds;
            if (dt < MinGapSeconds)
                return (0, 0);   // окно короче одного честного замера — врать нечем

            double vx = (b.X - a.X) / dt, vz = (b.Z - a.Z) / dt;
            // Тот же караул от скачков, что и у скорости: телепорт, подгрузка
            // чанка и прыжок позиции не должны становиться упреждением
            return Math.Sqrt(vx * vx + vz * vz) > ImpossibleSpeed ? (0, 0) : (vx, vz);
        }
    }

    /// <summary>
    /// Какое окно замеров считать «куда он идёт прямо сейчас», секунд.
    /// Отдельно от <see cref="MemorySeconds"/> нарочно — см. <see cref="Drift"/>.
    /// </summary>
    public double DriftSeconds { get; set; } = 1.5;

    /// <summary>Сколько замеров набрано по этой цели за память.</summary>
    public int Samples(long id, DateTime now)
    {
        lock (gate)
            return tracks.TryGetValue(id, out var t)
                ? t.Seen.Count(s => (now - s.At).TotalSeconds <= MemorySeconds)
                : 0;
    }

    /// <summary>Забыть цель (умерла, ушла из виду).</summary>
    public void Forget(long id)
    {
        lock (gate)
            tracks.Remove(id);
    }

    /// <summary>Забыть всё: новое соединение — новый мир.</summary>
    public void Clear()
    {
        lock (gate)
            tracks.Clear();
    }
}
