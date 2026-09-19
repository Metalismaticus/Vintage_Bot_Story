namespace VsBotKit;

/// <summary>Что стало с попыткой запомнить клетку.</summary>
public enum Remembered
{
    /// <summary>Записали впервые.</summary>
    Written,
    /// <summary>Уже знали — обновили время и счёт встреч.</summary>
    Refreshed,
    /// <summary>Не записали: бот этого НЕ ВИДЕЛ — блок замурован в породе.</summary>
    NotSeen,
    /// <summary>Не записали: слишком далеко, чтобы это честно называлось «видел».</summary>
    TooFar
}

/// <summary>
/// Одна клетка, которую бот встретил своими глазами.
/// </summary>
/// <param name="Pos">Клетка — она же ключ памяти.</param>
/// <param name="Code">Полный код блока, как его дал реестр сервера.</param>
/// <param name="Kind">Чем это полезно человеку: металл руды («nativecopper»), порода камня («granite»).</param>
/// <param name="SeenAtHours">Игровые часы от начала мира на момент последнего взгляда.</param>
/// <param name="Times">Сколько раз бот подтвердил эту клетку глазами.</param>
/// <param name="Exposed">
/// Клетка была ОТКРЫТА, то есть видна честно. Ложь здесь означает, что запись
/// сделана при включённом чите зрения: такую по умолчанию не выдают.
/// </param>
public readonly record struct ResourceSighting(BlockPos Pos, string Code, string Kind,
    double SeenAtHours, int Times, bool Exposed)
{
    /// <summary>Сколько игровых часов прошло с последнего взгляда.</summary>
    public double AgeHours(double nowHours) => nowHours - SeenAtHours;

    public override string ToString() =>
        $"{(Kind.Length > 0 ? Kind : Code)} в {Pos}" + (Exposed ? "" : " (подсмотрено читом)");
}

/// <summary>
/// ПАМЯТЬ ВСТРЕЧЕННОГО: что видел, где, когда и сколько раз.
///
/// Зачем она. Бот всё время проходит мимо того, что сейчас не берёт: не тот
/// металл, сумка полна, до клетки не дотянуться. Раньше это забывалось в ту же
/// секунду, и на вопрос «где ты видел медь» ответить было нечем — приходилось
/// заново уходить в разведку по тем же местам.
///
/// ПАМЯТЬ ОБЯЗАНА БЫТЬ ЧЕСТНОЙ. Сервер шлёт чанк целиком, и в модели мира
/// лежит каждый блок внутри камня. Записывать это в «видел» — тот же чит
/// SeeThroughWalls, только с чёрного хода: бот «вспоминал» бы руду, на которую
/// никогда не смотрел. Поэтому в записи хранится признак <see cref="ResourceSighting.Exposed"/>
/// (у блока была открытая грань — <see cref="WorldModel.IsExposed"/>), и
/// запросы по умолчанию отдают только такие. Кто пишет — обязан этот признак
/// посчитать, а не сочинить.
///
/// ЗАБЫВАНИЕ. Мир живёт без нас: чужой игрок выкапывает жилу, обвал засыпает
/// выход, сервер откатывает чанк. Значит у памяти есть срок (<see cref="KeepHours"/>)
/// и есть опровержение (<see cref="Refute"/>) — пришли на место, а там уже не
/// то, что помнили. Срок считается по ИГРОВЫМ часам: мир меняется в игровом
/// времени, а не в том, сколько бот простоял выключенным.
///
/// Класс — механизм и ничего не знает ни про роль, ни про сеть: время ему
/// подают снаружи (<see cref="GameClock.TotalHours"/>), клетки — тот, кто
/// смотрит на мир (<see cref="Ores.Find"/>).
/// </summary>
public sealed class ResourceMemory
{
    private readonly object gate = new();
    private readonly Dictionary<BlockPos, ResourceSighting> seen = [];
    private readonly Func<double> gameHours;

    /// <param name="gameHours">Игровые часы от начала мира (см. <see cref="GameClock.TotalHours"/>).</param>
    public ResourceMemory(Func<double> gameHours) => this.gameHours = gameHours;

    /// <summary>Что стоит сказать вслух: забыл, опроверг, переполнился.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Сколько ИГРОВЫХ часов держать запись. Трое суток по умолчанию: за это
    /// время соседний игрок вполне успевает выработать жилу, а бегать за
    /// вчерашней медью всё ещё имеет смысл. Ноль и меньше — не забывать вовсе.
    /// </summary>
    public double KeepHours { get; set; } = 72;

    /// <summary>
    /// Дальше этого «видел» не бывает. Открытая грань есть и у блока за
    /// двести метров, но там его не разглядеть — это уже не память, а чтение
    /// чанков. Значение того же порядка, что и радиусы поиска руды.
    /// </summary>
    public double MaxDistance { get; set; } = 64;

    /// <summary>
    /// Сколько клеток держать. Память живёт весь сеанс, а рудокоп за час
    /// проходит тысячи клеток: без потолка это утечка. Когда тесно, уходит
    /// самое старое — свежее полезнее.
    /// </summary>
    public int Capacity { get; set; } = 4096;

    /// <summary>
    /// ПОДХОДИТ ЛИ ЗАПОМНЕННОЕ ПОД СЛОВО ЧЕЛОВЕКА. Пусто — чистое правило по
    /// одному коду (<see cref="Ores.SuitsCode"/>), тем и жила память, пока была
    /// сама по себе.
    ///
    /// Подключаясь к руде (<see cref="Ores.Memory"/>), память получает ЖИВУЮ
    /// мерку — ту же, какой руда смотрит вокруг, со спросом у реестра
    /// выпадений. Иначе «вижу» и «помню» расходились бы молча: осмотр находит
    /// метеорит по «stone-meteorite-iron» (падает с блока meteorite-iron), а
    /// сравнение букв кода на той же записи отвечало бы «не то».
    /// </summary>
    public Func<string, string, bool>? Suits { get; set; }

    /// <summary>Игровые часы сейчас.</summary>
    public double NowHours => gameHours();

    /// <summary>Сколько клеток помним (включая те, что уже пора забыть).</summary>
    public int Count
    {
        get { lock (gate) return seen.Count; }
    }

    /// <summary>Всё, что помним, — как есть (для отчётов и панели).</summary>
    public List<ResourceSighting> All()
    {
        lock (gate)
            return [.. seen.Values];
    }

    // ---------------- чистые правила ----------------

    /// <summary>
    /// МОЖНО ЛИ ЭТО ЗАПОМНИТЬ — правило без мира и без сети, поэтому и
    /// проверяется тестом.
    ///
    /// Видел — значит у блока была открытая грань и он был не дальше, чем
    /// видно. Замурованный блок не запоминается никогда, кроме случая, когда
    /// чит зрения включён явно: тогда запись делается, но помечается нечестной,
    /// чтобы честный запрос её не выдал.
    /// </summary>
    public static Remembered Judge(bool exposed, double distance, double maxDistance,
        bool seeThroughWalls, bool known)
    {
        if (!exposed && !seeThroughWalls)
            return Remembered.NotSeen;
        if (maxDistance > 0 && distance > maxDistance)
            return Remembered.TooFar;
        return known ? Remembered.Refreshed : Remembered.Written;
    }

    /// <summary>
    /// ПОРА ЛИ ЗАБЫТЬ. Считается по игровым часам.
    ///
    /// Отдельный случай — часы, ушедшие назад: это другой мир (или другой
    /// сервер), и старые координаты в нём означают что угодно. Такое забывается
    /// сразу, иначе бот пошёл бы за медью по чужой карте.
    /// </summary>
    public static bool Forgotten(double seenAtHours, double nowHours, double keepHours)
    {
        double age = nowHours - seenAtHours;
        if (age < -1)
            return true;
        return keepHours > 0 && age >= keepHours;
    }

    /// <summary>Расстояние между клетками — обычное, по прямой.</summary>
    public static double DistanceBetween(BlockPos a, BlockPos b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// ЧТО ОТДАТЬ НА ВОПРОС «где ближайшая медь, которую я видел» — целиком
    /// чистое правило: отбор и порядок, без мира и без часов внутри.
    ///
    /// Порядок ровно тот, что нужен ногам: сперва ближнее. Одинаково близкие
    /// разводит свежесть — чем позже видел, тем вероятнее, что оно ещё там.
    /// </summary>
    /// <param name="wanted">«медь», «камень», «cassiterite»; пусто — что угодно.</param>
    /// <param name="onlyHonest">Отдавать только увиденное глазами (по умолчанию да).</param>
    /// <param name="suits">
    /// Чем мерить «подходит ли»; пусто — чистым правилом по коду. Живой бот
    /// подаёт сюда мерку руды, спрашивающую реестр (см. <see cref="Suits"/>).
    /// </param>
    public static List<ResourceSighting> Choose(IEnumerable<ResourceSighting> all, string wanted,
        BlockPos from, double nowHours, double keepHours, bool onlyHonest = true,
        int limit = int.MaxValue, Func<string, string, bool>? suits = null)
    {
        var fit = new List<ResourceSighting>();
        foreach (var s in all)
        {
            if (onlyHonest && !s.Exposed)
                continue;
            if (Forgotten(s.SeenAtHours, nowHours, keepHours))
                continue;
            if (!(suits ?? Ores.SuitsCode)(s.Code, wanted))
                continue;
            fit.Add(s);
        }
        fit.Sort((a, b) =>
        {
            int byDistance = DistanceBetween(a.Pos, from).CompareTo(DistanceBetween(b.Pos, from));
            return byDistance != 0 ? byDistance : b.SeenAtHours.CompareTo(a.SeenAtHours);
        });
        return fit.Count <= limit ? fit : fit.GetRange(0, limit);
    }

    /// <summary>Возраст словами: «40 мин», «6 ч», «2 сут» — для человека, а не для машины.</summary>
    public static string AgeWords(double hours)
    {
        if (hours < 0)
            return "только что";
        if (hours < 1)
            return $"{hours * 60:0} мин назад";
        if (hours < 24)
            return $"{hours:0} ч назад";
        return $"{hours / 24:0.#} сут назад";
    }

    // ---------------- работа ----------------

    /// <summary>
    /// Запомнить клетку. Возвращает, что с ней стало, — молча память не
    /// отказывает: тот, кто смотрит, обязан знать, почему увиденное не легло.
    /// </summary>
    /// <param name="exposed">У блока БЫЛА открытая грань (<see cref="WorldModel.IsExposed"/>).</param>
    /// <param name="distance">Сколько до него блоков от бота.</param>
    /// <param name="seeThroughWalls">Включён ли чит зрения — тогда пишем, но помечаем нечестным.</param>
    public Remembered Remember(BlockPos pos, string code, bool exposed, double distance,
        bool seeThroughWalls = false)
    {
        if (code is not { Length: > 0 })
            return Remembered.NotSeen;

        lock (gate)
        {
            var verdict = Judge(exposed, distance, MaxDistance, seeThroughWalls,
                seen.ContainsKey(pos));
            if (verdict is Remembered.NotSeen or Remembered.TooFar)
                return verdict;

            int times = verdict == Remembered.Refreshed ? seen[pos].Times + 1 : 1;
            seen[pos] = new ResourceSighting(pos, code, OreCode.Describe(code).Metal,
                NowHours, times, exposed);
            Trim();
            return verdict;
        }
    }

    /// <summary>
    /// Ближайшее из того, что помним. Null — не помним ничего подходящего
    /// (спрашивающий обязан сказать об этом вслух, а не молчать).
    /// </summary>
    public ResourceSighting? Nearest(string wanted, BlockPos from, bool onlyHonest = true)
    {
        var list = Recall(wanted, from, limit: 1, onlyHonest);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>Что помним про это, начиная с ближайшего.</summary>
    public List<ResourceSighting> Recall(string wanted, BlockPos from, int limit = 16,
        bool onlyHonest = true)
    {
        lock (gate)
            return Choose(seen.Values, wanted, from, NowHours, KeepHours, onlyHonest, limit, Suits);
    }

    /// <summary>
    /// ПРИШЛИ — А ТАМ УЖЕ НЕ ТО. Забыть клетку, потому что мир изменился:
    /// жилу выкопал сосед, обвал засыпал выход, сервер откатил чанк.
    /// </summary>
    /// <param name="nowCode">Что стоит в клетке сейчас (null — пусто).</param>
    /// <returns>true, если запись была и её убрали.</returns>
    public bool Refute(BlockPos pos, string? nowCode)
    {
        lock (gate)
        {
            if (!seen.TryGetValue(pos, out var was))
                return false;
            if (nowCode is { Length: > 0 } && string.Equals(was.Code, nowCode,
                    StringComparison.OrdinalIgnoreCase))
                return false;   // всё на месте — забывать нечего
            seen.Remove(pos);
            OnLog?.Invoke($"в {pos} помнил {was.Code}, а там {nowCode ?? "пусто"} — забыл");
            return true;
        }
    }

    /// <summary>Выбросить просроченное. Возвращает, сколько забыли.</summary>
    public int Sweep()
    {
        lock (gate)
        {
            double now = NowHours;
            var stale = seen.Where(p => Forgotten(p.Value.SeenAtHours, now, KeepHours))
                            .Select(p => p.Key).ToList();
            foreach (var pos in stale)
                seen.Remove(pos);
            if (stale.Count > 0)
                OnLog?.Invoke($"забыл {stale.Count} кл. — им больше {KeepHours:0} игровых часов");
            return stale.Count;
        }
    }

    /// <summary>
    /// ЗАБЫТЬ ВСЁ: другой мир, другой сервер, начали заново.
    ///
    /// Зовётся на КАЖДОМ новом соединении (см. BotClient.OnSessionReset) — так
    /// же, как это делают чужие владения. Единственной защитой раньше был
    /// <see cref="Forgotten"/>, а он ловит лишь мир, чей календарь УШЁЛ НАЗАД
    /// больше чем на час: смена сервера на мир с более поздним календарём
    /// оставляла записи живыми ещё 72 игровых часа, и «!помню медь» называл
    /// координаты чужой карты, а наряд честно уходил туда впустую.
    ///
    /// Молчать нельзя: со стороны «бот вдруг перестал помнить медь»
    /// неотличимо от поломки памяти.
    /// </summary>
    /// <param name="why">Почему забываем — уйдёт в журнал.</param>
    public int Clear(string why = "начали заново")
    {
        int had;
        lock (gate)
        {
            had = seen.Count;
            seen.Clear();
        }
        OnLog?.Invoke(had > 0
            ? $"забыл всё, что помнил про руду и породу ({had} кл.): {why}"
            : $"забывать нечего — память была пуста ({why})");
        return had;
    }

    /// <summary>
    /// Ответ человеку словами. Всегда говорит хоть что-то: «не помню» — это
    /// тоже ответ, а молчание — дефект.
    /// </summary>
    public string Tell(string wanted, BlockPos from, int limit = 3)
    {
        var list = Recall(wanted, from, limit);
        string what = wanted.Length > 0 ? wanted : "ресурсов";
        if (list.Count == 0)
            return $"{what} я нигде не видел" +
                   (Count > 0 ? $" (в памяти {Count} кл. другого)" : " (память пуста)");

        double now = NowHours;
        int total = Recall(wanted, from, int.MaxValue).Count;
        var words = list.Select(s =>
            $"{s.Pos} — {DistanceBetween(s.Pos, from):0} бл, {AgeWords(s.AgeHours(now))}" +
            (s.Times > 1 ? $", видел {s.Times} раза" : ""));
        return $"{what}: помню {total} кл.; ближайшие: {string.Join("; ", words)}";
    }

    /// <summary>
    /// Держать память в берегах: когда клеток больше потолка, уходит самое
    /// старое. Вызывается под уже взятым замком.
    /// </summary>
    private void Trim()
    {
        if (Capacity <= 0 || seen.Count <= Capacity)
            return;
        int extra = seen.Count - Capacity;
        var oldest = seen.Values.OrderBy(s => s.SeenAtHours).Take(extra).Select(s => s.Pos).ToList();
        foreach (var pos in oldest)
            seen.Remove(pos);
        OnLog?.Invoke($"память ресурсов полна ({Capacity} кл.) — забыл {oldest.Count} самых старых");
    }
}
