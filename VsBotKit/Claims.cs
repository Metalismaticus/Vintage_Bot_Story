using System.Collections.Concurrent;

using Vintagestory.API.Common;
using Vintagestory.API.Util;

using Cuboidi = Vintagestory.API.MathTools.Cuboidi;

namespace VsBotKit;

/// <summary>Чем именно место чужое.</summary>
public enum ForeignKind
{
    /// <summary>Заявка игрока (приват). Границы прислал сам сервер.</summary>
    Claim,
    /// <summary>Сервер не дал сломать блок.</summary>
    BreakRefused,
    /// <summary>Сервер не дал поставить блок.</summary>
    PlaceRefused,
    /// <summary>Дверь не открылась — за ней чужое жильё.</summary>
    LockedDoor,
    /// <summary>Чужой сундук или иное хранилище — признак жилья.</summary>
    Container,
    /// <summary>Тут видели живого игрока.</summary>
    Player
}

/// <summary>
/// ЧУЖОЕ МЕСТО: границы, чем оно чужое, чьё и до каких пор мы в это верим.
///
/// Границы — коробка, а не точка, потому что заявка занимает площадь, и
/// мерить до её середины нельзя: у заявки 200×200 середина лежит в сотне
/// блоков от забора, и «до чужого 100 блоков» бот сказал бы, стоя вплотную
/// к чужому огороду.
/// </summary>
/// <param name="Kind">Чем место чужое.</param>
/// <param name="Area">Границы (верхний край коробки НЕ входит — см. <see cref="Claims.DistanceXZ(int, int, Cuboidi)"/>).</param>
/// <param name="Who">Хозяин, как его называет сервер («» — неизвестен).</param>
/// <param name="SeenAt">Когда узнали.</param>
/// <param name="Until">До каких пор верим (у заявки — <see cref="DateTime.MaxValue"/>).</param>
public sealed record ForeignPlace(
    ForeignKind Kind, Cuboidi Area, string Who, DateTime SeenAt, DateTime Until)
{
    /// <summary>Середина места — чтобы было что назвать человеку в журнале.</summary>
    public BlockPos Where => new(
        (Area.MinX + Area.MaxX) / 2,
        (Area.MinY + Area.MaxY) / 2,
        (Area.MinZ + Area.MaxZ) / 2);

    /// <summary>Почему это место чужое — словами, для журнала.</summary>
    public string Why => Kind switch
    {
        ForeignKind.Claim => Who.Length > 0 ? $"заявка игрока {Who}" : "чья-то заявка",
        ForeignKind.BreakRefused => "сервер не дал тут ломать",
        ForeignKind.PlaceRefused => "сервер не дал тут ставить",
        ForeignKind.LockedDoor => "запертая чужая дверь",
        ForeignKind.Container => "чужой сундук",
        ForeignKind.Player => Who.Length > 0 ? $"тут ходит игрок {Who}" : "тут ходит другой игрок",
        _ => "чужое место"
    };

    public override string ToString() => $"{Why} {Where}";
}

/// <summary>
/// Годится ли место для работы и почему.
/// </summary>
/// <param name="Ok">Годится.</param>
/// <param name="Distance">Сколько блоков до ближайшего чужого места по горизонтали
/// (<see cref="double.PositiveInfinity"/> — чужого не знаем вовсе).</param>
/// <param name="Nearest">Само это чужое место (null — чужого не знаем).</param>
/// <param name="KeepAway">На сколько блоков велено держаться (0 — не велено).</param>
public readonly record struct ForeignVerdict(
    bool Ok, double Distance, ForeignPlace? Nearest, int KeepAway)
{
    /// <summary>
    /// Приговор словами — с ЧИСЛОМ. Правило проекта: молчаливый уход в другое
    /// место — дефект; человек, читающий журнал, должен видеть и причину,
    /// и расстояние, чтобы понять, не слишком ли жадно выставлена настройка.
    /// </summary>
    public string Say()
    {
        if (KeepAway <= 0)
            return "держаться от чужого не велено — место годится";
        if (Nearest is not { } near)
            return $"чужого в памяти нет — место годится (велено держаться {KeepAway} бл)";
        return Ok
            ? $"место годится: {near.Why} в {Distance:0} бл, а держаться велено {KeepAway} бл"
            : $"место не годится: {near.Why} {near.Where} всего в {Distance:0} бл, " +
              $"а держаться велено {KeepAway} бл";
    }

    public override string ToString() => Say();
}

/// <summary>
/// ЧУЖИЕ ВЛАДЕНИЯ: где нельзя работать и на сколько от них держаться.
///
/// ОТКУДА БОТ ЭТО ЗНАЕТ. Не гадает — ему присылает сервер. Разбор игровых
/// сборок 1.22.6 (VintagestoryLib.dll) показывает цепочку:
///   ServerMain.HandlePlayerReady → WorldMap.All → ServerWorldMap.SendClaims
///   → пакет 75 (Packet_ServerIdEnum.LandClaims);
///   на клиенте GeneralPacketHandler.HandleLandClaims кладёт их в
///   ClientWorldMap.LandClaims.
/// То есть КАЖДЫЙ клиент сразу после входа получает ВСЕ заявки мира целиком,
/// а изменения (создали, убрали, поправили) прилетают потом через
/// ServerWorldMap.BroadcastClaims. Обычный игрок видит ровно это же — по ним
/// рисуется разметка на карте. Значит правило 4 (не делать того, чего не
/// может живой игрок) здесь не нарушается и флаг Cheats не нужен.
///
/// ПОЧЕМУ НЕ ЧЕРЕЗ IWorldAccessor.Claims. Наш <c>BotPhysicsWorld.Claims</c>
/// намеренно бросает NotSupportedException — это заглушка мира ДЛЯ ФИЗИКИ,
/// и физика туда не ходит. Сетевого канала она не заменяет.
///
/// ЗАЯВОК МАЛО, ЖИЛЬЯ МНОГО. Заявка есть далеко не у каждого дома: её надо
/// купить и поставить. Поэтому кроме присланных границ бот помнит и то, что
/// видел сам: где сервер не дал сломать или поставить, где не открылась
/// дверь, где стоит чужой сундук, где ходит игрок. Такие приметы слабее
/// заявки (сервер молчит о причине: отказ на слом бывает и от укрепления
/// блока), поэтому они ЗАБЫВАЮТСЯ по времени, как метка запертой двери
/// в Movement — обстановка меняется, а вечная память врёт.
///
/// Класс — механизм. Держаться или нет и на сколько, решает роль
/// через <see cref="KeepAway"/>.
/// </summary>
public sealed class Claims
{
    private readonly BotClient bot;

    /// <summary>Заявки, как их прислал сервер (под замком: пакеты идут из сети).</summary>
    private readonly List<LandClaim> claims = [];

    /// <summary>Заготовленные коробки ЧУЖИХ заявок: считаются один раз, на приходе пакета.</summary>
    private List<ForeignPlace> claimPlaces = [];

    /// <summary>Что бот подметил сам. Ключ — клетка, чтобы одно место не копилось дважды.</summary>
    private readonly ConcurrentDictionary<BlockPos, ForeignPlace> seen = new();

    private readonly object gate = new();

    public Claims(BotClient bot)
    {
        this.bot = bot;
        bot.OnPacket += HandlePacket;
        // Переподключились — мир может быть уже другим (сервер перезапустили,
        // хозяин сменил адрес). Старые границы тогда врут, а сервер пришлёт
        // свои заново сразу после входа
        bot.OnSessionReset += Reset;
    }

    /// <summary>Что происходит с чужими владениями — в общий журнал бота.</summary>
    public event Action<string>? OnLog;

    // ---------------- настройки ----------------

    /// <summary>
    /// На сколько блоков держаться от ЗАЯВКИ (границы прислал сервер).
    /// 0 — не держаться вовсе.
    ///
    /// Заказчик просил 500: «в автопоиске чего-либо избегать приваты игроков
    /// и стараться от них в расстоянии 500 блоков добывать что-либо».
    /// </summary>
    public int KeepAway { get; set; } = 500;

    /// <summary>
    /// СЧИТАТЬ ЧУЖИМИ ТОЛЬКО ПРИВАТЫ ЖИВЫХ ИГРОКОВ. Владения НПС (торговцы и
    /// прочие) стоят по всей карте, и держаться от них наравне с чужим домом
    /// значит не работать нигде. Выключить — если однажды понадобится обходить
    /// и НПС тоже.
    /// </summary>
    public bool SkipNpcClaims { get; set; } = true;

    /// <summary>
    /// На сколько блоков держаться от ПОДМЕЧЕННОГО САМИМ: отказа сервера,
    /// запертой двери, чужого сундука, встреченного игрока.
    ///
    /// ЧИСЛО ЗДЕСЬ ДРУГОЕ, И ЭТО ГЛАВНОЕ. Пятьсот блоков заказчик просил для
    /// ЗАЯВОК — у них есть границы, присланные сервером, и они не врут.
    /// Примета же — догадка: сервер не говорит, почему не принял удар, и
    /// «сломал, а подтверждения за две секунды не пришло» бывает и от лага
    /// сети, и от укреплённого блока. С общим числом выходило так: одна
    /// неподтверждённая установка факела в собственной штольне выключала
    /// работу в круге радиусом 500 блоков на полчаса — вместе со складом и
    /// домом, которые остались внутри этого круга.
    ///
    /// Тридцать два — это радиус, в котором бот работает: обойти чужой сарай
    /// и копать рядом можно, а лезть в него — нет.
    /// </summary>
    public int KeepAwaySeen { get; set; } = 32;

    /// <summary>
    /// Сколько НЕподтверждённых сломов и установок в одном месте считать
    /// приметой чужого. 1 — верить первому же (прежнее поведение).
    ///
    /// ПОЧЕМУ НЕ ПЕРВОМУ. Сервер не принимает действие по десятку причин, и
    /// приват — лишь одна из них: факел на негодную грань, клетка занята
    /// существом, плита в воду, лаг сети на две секунды. Заявка отказывает
    /// В ОДНОМ И ТОМ ЖЕ МЕСТЕ КАЖДЫЙ РАЗ, а лаг — один раз и где попало.
    /// Отсюда правило: верим повторению, а не первому случаю.
    /// </summary>
    public int RefusalsToBelieve { get; set; } = 3;

    /// <summary>За какое время отказы должны уложиться, чтобы считаться одним упорством.</summary>
    public TimeSpan RefusalWindow { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>В каком радиусе клеток отказы считаются «тем же местом».</summary>
    public int RefusalRadius { get; set; } = 8;

    /// <summary>
    /// Сколько помнить ПОДМЕЧЕННОЕ САМИМ (отказ сервера, дверь, сундук).
    /// Заявки это не трогает: их держит сервер и сам же присылает отмену.
    /// </summary>
    public TimeSpan Remember { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Сколько помнить место, где видели игрока. Коротко нарочно: игрок —
    /// не забор, он уходит за минуты, и вечная метка «тут кто-то был» через
    /// час работы выгоняет бота с половины карты.
    /// </summary>
    public TimeSpan RememberPlayers { get; set; } = TimeSpan.FromMinutes(5);

    // ---------------- что бот знает ----------------

    /// <summary>Присылал ли сервер заявки хоть раз (пакет 75).</summary>
    public bool ClaimsKnown { get; private set; }

    /// <summary>Сколько заявок знает бот (и своих, и чужих).</summary>
    public int Count { get { lock (gate) return claims.Count; } }

    /// <summary>Сколько чужих площадок бот обходит по заявкам.</summary>
    public int ForeignAreaCount { get { lock (gate) return claimPlaces.Count; } }

    /// <summary>Сколько чужих мест бот подметил сам (не считая просроченных).</summary>
    public int SeenCount => seen.Values.Count(p => p.Until > DateTime.UtcNow);

    /// <summary>Заявки в том виде, в каком их прислал сервер.</summary>
    public IReadOnlyList<LandClaim> All { get { lock (gate) return claims.ToArray(); } }

    /// <summary>
    /// Своя ли это заявка: хозяин — бот, либо хозяин впустил бота поимённо
    /// с правом строить и ломать.
    ///
    /// ГРУППЫ НЕ ПРОВЕРЯЕМ, и это осознанно: список групп, в которых состоит
    /// игрок, приходит отдельным пакетом, и выдумывать его нельзя (правило 5).
    /// Ошибка получается в безопасную сторону — бот будет держаться в стороне
    /// от заявки, куда его на самом деле впустили через группу.
    /// </summary>
    public bool Mine(LandClaim claim)
    {
        string uid = bot.PlayerUid;
        if (uid.Length == 0)
            return false;
        if (claim.OwnedByPlayerUid == uid)
            return true;
        return claim.PermittedPlayerUids != null &&
               claim.PermittedPlayerUids.TryGetValue(uid, out var flags) &&
               flags.HasFlag(EnumBlockAccessFlags.BuildOrBreak);
    }

    /// <summary>Все чужие места, которые бот считает живыми на этот миг.</summary>
    public IReadOnlyList<ForeignPlace> Known(DateTime now)
    {
        List<ForeignPlace> all;
        lock (gate)
            all = [.. claimPlaces];
        all.AddRange(seen.Values.Where(p => p.Until > now));
        return all;
    }

    /// <inheritdoc cref="Known(DateTime)"/>
    public IReadOnlyList<ForeignPlace> Known() => Known(DateTime.UtcNow);

    // ---------------- чистое правило ----------------

    /// <summary>
    /// Горизонтальное расстояние в блоках от клетки до КРАЯ коробки.
    /// 0 — клетка внутри коробки (по горизонтали).
    ///
    /// ТОЛЬКО ПО ГОРИЗОНТАЛИ, потому что заявка — это участок земли: хозяин
    /// берёт её от неба до камня, и «уйти на 500 блоков вниз» вместо «в
    /// сторону» — не то, о чём просил заказчик.
    ///
    /// ВЕРХНИЙ КРАЙ КОРОБКИ НЕ ВХОДИТ. Проверено на самой игре: у заявки
    /// с областью [0,0,0 .. 10,20,10] метод LandClaim.PositionInside(10,10,10)
    /// отвечает false, а (9,19,9) — true. То есть последняя занятая колонка —
    /// это MaxX-1. Ошибка на единицу здесь стоит дорого: бот считал бы, что
    /// стоит в блоке от забора, стоя ровно на нём.
    /// </summary>
    public static double DistanceXZ(int x, int z, Cuboidi area)
    {
        int maxX = Math.Max(area.MinX, area.MaxX - 1);
        int maxZ = Math.Max(area.MinZ, area.MaxZ - 1);
        int dx = Math.Max(Math.Max(area.MinX - x, x - maxX), 0);
        int dz = Math.Max(Math.Max(area.MinZ - z, z - maxZ), 0);
        return Math.Sqrt((double)dx * dx + (double)dz * dz);
    }

    /// <inheritdoc cref="DistanceXZ(int, int, Cuboidi)"/>
    public static double DistanceXZ(BlockPos where, ForeignPlace place) =>
        DistanceXZ(where.X, where.Z, place.Area);

    /// <summary>
    /// ГОДИТСЯ ЛИ МЕСТО ДЛЯ РАБОТЫ — одно число на все виды чужого.
    /// Оставлено для тех, кому разница между заявкой и приметой не важна.
    /// </summary>
    public static ForeignVerdict Judge(BlockPos where, IEnumerable<ForeignPlace> places,
        int keepAway, DateTime now) => Judge(where, places, keepAway, keepAway, now);

    /// <summary>
    /// ГОДИТСЯ ЛИ МЕСТО ДЛЯ РАБОТЫ. Чистое правило: ни мира, ни сервера, ни
    /// часов — только клетка, список чужих мест, две настройки и текущее время.
    ///
    /// Правил ровно четыре, и каждое закрыто тестом:
    ///   1) оба предела ноль или меньше — держаться не велено, годится любое
    ///      место;
    ///   2) просроченное чужое место в счёт не идёт (забывание по времени);
    ///   3) у ЗАЯВКИ свой предел (<paramref name="keepAwayClaims"/>), у
    ///      ПОДМЕЧЕННОГО САМИМ — свой (<paramref name="keepAwaySeen"/>);
    ///   4) место не годится, когда хоть одно чужое ближе своего предела; в
    ///      приговор попадает то, до которого НЕ ХВАТАЕТ БОЛЬШЕ ВСЕГО, —
    ///      именно от него и придётся отходить дальше прочих.
    /// </summary>
    public static ForeignVerdict Judge(BlockPos where, IEnumerable<ForeignPlace> places,
        int keepAwayClaims, int keepAwaySeen, DateTime now)
    {
        if (keepAwayClaims <= 0 && keepAwaySeen <= 0)
            return new ForeignVerdict(true, double.PositiveInfinity, null, 0);

        ForeignPlace? nearest = null;
        double nearestDistance = double.PositiveInfinity;
        int nearestNeed = 0;

        ForeignPlace? worst = null;
        double worstDistance = double.PositiveInfinity;
        int worstNeed = 0;
        double worstShortfall = 0;

        foreach (var place in places)
        {
            if (place.Until <= now)
                continue;
            int need = NeedFor(place.Kind, keepAwayClaims, keepAwaySeen);
            double d = DistanceXZ(where, place);

            if (d < nearestDistance)
            {
                nearestDistance = d;
                nearest = place;
                nearestNeed = need;
            }
            if (need <= 0)
                continue;
            double shortfall = need - d;
            if (shortfall <= 0 || shortfall <= worstShortfall)
                continue;
            worstShortfall = shortfall;
            worst = place;
            worstDistance = d;
            worstNeed = need;
        }

        return worst is { } bad
            ? new ForeignVerdict(false, worstDistance, bad, worstNeed)
            : new ForeignVerdict(true, nearestDistance, nearest,
                nearest == null ? Math.Max(keepAwayClaims, keepAwaySeen) : nearestNeed);
    }

    /// <summary>
    /// Какой предел применяется к этому виду чужого. Заявке — свой: её границы
    /// прислал сервер, и они не догадка. Всему остальному — предел примет.
    /// </summary>
    public static int NeedFor(ForeignKind kind, int keepAwayClaims, int keepAwaySeen) =>
        kind == ForeignKind.Claim ? keepAwayClaims : keepAwaySeen;

    /// <summary>
    /// КЛЕТКА ВНУТРИ ЧУЖОГО — чистое правило. Не «далеко ли до чужого», а
    /// «стоим ли мы в нём»: расстояние по горизонтали ровно ноль.
    ///
    /// ЗАЧЕМ ОТДЕЛЬНО ОТ <see cref="Judge(BlockPos, IEnumerable{ForeignPlace}, int, int, DateTime)"/>.
    /// У приговора вопрос другой: «годится ли МЕСТО ДЛЯ РАБОТЫ», и ответ на него
    /// заказчик просил давать с большим запасом — «избегать приваты игроков и
    /// стараться от них в расстоянии 500 блоков добывать что-либо». Перенести
    /// это число на подпорку под собственной ногой нельзя: тогда бот, застрявший
    /// в яме в трёхстах блоках от чужого забора, не имел бы права бросить себе
    /// под ноги ни одного блока — а живой игрок в этом месте строит свободно,
    /// потому что за забором он не стоит. Здесь спрашивают именно про запрет
    /// сервера: внутри чужой заявки блок не поставит и человек.
    ///
    /// Возвращаем само чужое место, а не «да/нет»: отказ обязан назвать, ЧЕМ
    /// место чужое, — иначе бот молча не станет строить, и понять почему будет
    /// нельзя.
    /// </summary>
    public static ForeignPlace? Inside(BlockPos where, IEnumerable<ForeignPlace> places,
        DateTime now)
    {
        foreach (var place in places)
        {
            if (place.Until <= now)
                continue;
            // Верх коробки не входит — тем же счётом, что и у расстояния
            if (place.Area.MinY > where.Y || where.Y >= Math.Max(place.Area.MinY + 1, place.Area.MaxY))
                continue;
            if (DistanceXZ(where, place) <= 0)
                return place;
        }
        return null;
    }

    /// <inheritdoc cref="Inside(BlockPos, IEnumerable{ForeignPlace}, DateTime)"/>
    public ForeignPlace? Inside(BlockPos where) => Inside(where, Known(), DateTime.UtcNow);

    /// <summary>
    /// ТОЛЬКО ЗАЯВКА ИГРОКА (приват), а не всякая примета чужого.
    ///
    /// <see cref="Inside(BlockPos)"/> отвечает про ВСЁ, что бот когда-либо
    /// счёл чужим: «тут ходил игрок» (помнится пять минут), чужой сундук,
    /// отказ сервера сломать или поставить блок, не открывшуюся дверь. Для
    /// «не ломай тут» это верно, а для вопросов вроде «чья это овца» — нет:
    /// прошёл мимо живой человек, и вся дичь в его коробке на пять минут
    /// перестаёт быть дичью, причём с ЛОЖНОЙ причиной («он стоит на чужой
    /// заявке», хотя заявки нет).
    ///
    /// Заявка отличается от прочего тем, что её границы прислал САМ СЕРВЕР
    /// (<see cref="ForeignKind.Claim"/>), и живёт она до отмены, а не по
    /// таймеру догадки.
    /// </summary>
    public ForeignPlace? ClaimAt(BlockPos where) =>
        Inside(where, Known().Where(p => p.Kind == ForeignKind.Claim), DateTime.UtcNow);

    /// <summary>
    /// СКОЛЬКО ОТКАЗОВ УЖЕ НАБРАЛОСЬ В ЭТОМ МЕСТЕ — чистое правило счёта,
    /// вместе с текущим отказом (то есть не меньше единицы).
    ///
    /// Считаются только те, что уложились и в окно времени, и в радиус вокруг
    /// клетки: лаг сети роняет удары по всей выработке и один раз, а заявка
    /// отказывает в одном углу и каждый раз.
    /// </summary>
    public static int RefusalsNear(IEnumerable<(BlockPos Pos, DateTime At)> recent,
        BlockPos where, DateTime now, TimeSpan window, int radius)
    {
        int count = 1;
        foreach (var (pos, at) in recent)
        {
            if (now - at > window)
                continue;
            if (Math.Abs(pos.X - where.X) > radius || Math.Abs(pos.Y - where.Y) > radius ||
                Math.Abs(pos.Z - where.Z) > radius)
                continue;
            count++;
        }
        return count;
    }

    // ---------------- живой ответ ----------------

    private bool saidNoClaims;

    /// <summary>Годится ли место — по тому, что бот знает прямо сейчас.</summary>
    public ForeignVerdict Judge(BlockPos where)
    {
        // ЧЕСТНОСТЬ ПРЕЖДЕ УДОБСТВА. Если сервер заявок не присылал, «до
        // чужого далеко» означает лишь «мы ничего не знаем». Сказать об этом
        // надо, иначе выключенный на сервере приват выглядит как чистое поле
        if (KeepAway > 0 && !ClaimsKnown && !saidNoClaims)
        {
            saidNoClaims = true;
            OnLog?.Invoke("сервер заявок не присылал — держусь только от того, " +
                          "что подметил сам (отказы, двери, сундуки, игроки)");
        }
        return Judge(where, Known(), KeepAway, KeepAwaySeen, DateTime.UtcNow);
    }

    private string lastRefusal = "";

    /// <summary>
    /// Годится ли место, с оглашением отказа в журнал.
    ///
    /// Подряд идущие ОДИНАКОВЫЕ отказы не повторяются: обход округи щупает
    /// точки десятками, и без этого одна заявка залила бы журнал сотней
    /// строк, за которыми не видно работы. Как только место сменилось или
    /// стало годным — следующий отказ прозвучит снова.
    /// </summary>
    public bool Suits(BlockPos where)
    {
        var verdict = Judge(where);
        if (verdict.Ok)
        {
            lastRefusal = "";
            return true;
        }
        string text = $"{where}: {verdict.Say()}";
        if (text != lastRefusal)
        {
            lastRefusal = text;
            OnLog?.Invoke(text);
        }
        return false;
    }

    /// <summary>
    /// Куда идти вместо занятого места: та же сторона, но за пределом.
    ///
    /// Отходим ПРЯМО ОТ чужого места — по лучу от его середины через желаемую
    /// точку. Так бот не крутится вокруг заявки, а уходит от неё, и каждый
    /// шаг увеличивает расстояние.
    /// </summary>
    /// <param name="wanted">Куда хотелось.</param>
    /// <param name="step">Шаг отхода в блоках.</param>
    /// <param name="limit">Дальше этого не отходим — иначе бот уйдёт за горизонт.</param>
    /// <returns>Годная точка; null — в пределах limit годного места нет.</returns>
    public BlockPos? Aside(BlockPos wanted, int step = 32, int limit = 1200)
    {
        var verdict = Judge(wanted);
        if (verdict.Ok)
            return wanted;
        if (verdict.Nearest is not { } bad)
        {
            // Негодно, но ближайшего нет — так не бывает; молчать всё равно нельзя
            OnLog?.Invoke($"{wanted}: место негодно, а чужого рядом не назвать — это сбой правила");
            return null;
        }

        double dx = wanted.X - bad.Where.X, dz = wanted.Z - bad.Where.Z;
        double len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 1e-6)
        {
            // Стоим ровно в середине чужого участка — уходить всё равно надо,
            // и любая сторона одинаково хороша
            dx = 1;
            dz = 0;
            len = 1;
        }
        dx /= len;
        dz /= len;

        for (int gone = Math.Max(1, step); gone <= limit; gone += Math.Max(1, step))
        {
            var probe = new BlockPos(
                wanted.X + (int)Math.Round(dx * gone),
                wanted.Y,
                wanted.Z + (int)Math.Round(dz * gone));
            var next = Judge(probe);
            if (!next.Ok)
                continue;
            OnLog?.Invoke($"{wanted}: {verdict.Say()} — отхожу на {gone} бл, работать буду в {probe} " +
                          $"({next.Say()})");
            return probe;
        }

        OnLog?.Invoke($"{wanted}: {verdict.Say()} — и на {limit} бл вокруг годного места не нашлось");
        return null;
    }

    // ---------------- что бот подметил сам ----------------

    /// <summary>
    /// Запомнить чужое место, увиденное своими глазами.
    ///
    /// В СВОЕЙ заявке ничего не запоминается: отказ сервера там означает не
    /// приват, а что-то другое (укреплённый блок, занятая рука), и записав
    /// его как чужое, бот выгнал бы себя с собственной базы.
    /// </summary>
    /// <param name="where">Клетка.</param>
    /// <param name="kind">Чем место чужое.</param>
    /// <param name="who">Хозяин, если известен.</param>
    /// <returns>Запомнили (false — место в своей же заявке).</returns>
    public bool Note(BlockPos where, ForeignKind kind, string who = "")
    {
        if (InOwnClaim(where))
        {
            OnLog?.Invoke($"{where}: {kind} в моей же заявке — это не чужое, не запоминаю");
            return false;
        }

        var now = DateTime.UtcNow;
        var life = kind == ForeignKind.Player ? RememberPlayers : Remember;
        // Одна клетка — одна запись: обход мимо одного и того же сундука
        // не должен раздувать память, но обязан продлевать срок
        var place = new ForeignPlace(kind, Box(where), who, now, now + life);
        bool isNew = !seen.ContainsKey(where);
        seen[where] = place;
        if (isNew)
            OnLog?.Invoke($"запомнил чужое место: {place.Why} {where} " +
                          $"(помню {life.TotalMinutes:0} мин)");
        return true;
    }

    /// <summary>Сервер не дал сломать блок — это чужое (без сомнений).</summary>
    public bool NoteBreakRefused(BlockPos where) => Note(where, ForeignKind.BreakRefused);

    /// <summary>Сервер не дал поставить блок — это чужое (без сомнений).</summary>
    public bool NotePlaceRefused(BlockPos where) => Note(where, ForeignKind.PlaceRefused);

    /// <summary>Недавние отказы сервера: клетка и когда. Держатся ради счёта повторов.</summary>
    private readonly List<(BlockPos Pos, DateTime At)> refusals = [];

    /// <summary>Про какое место уже сказано «отказ не первый» — чтобы не повторяться.</summary>
    private BlockPos? saidRepeating;

    /// <summary>
    /// СЕРВЕР НЕ ПОДТВЕРДИЛ ДЕЙСТВИЕ — А ЭТО ЕЩЁ НЕ ПРИВАТ.
    ///
    /// Живой случай, ради которого правило и появилось: бот ставит факел в
    /// своей же штольне, подтверждения за две секунды не приходит (лаг сети,
    /// негодная грань, клетка занята дрифтером) — и место записывалось как
    /// «сервер не дал тут ставить» на полчаса. Дальше «!наряд медь 64» звал
    /// проверку чужого, до «чужого» выходило 0 блоков, и работа не начиналась
    /// ни разу: склад и дом оставались внутри запретного круга.
    ///
    /// Поэтому одиночный отказ только СЧИТАЕТСЯ. Приметой он становится, когда
    /// повторится <see cref="RefusalsToBelieve"/> раз рядом и подряд: заявка
    /// отказывает каждый раз, а сеть — один.
    /// </summary>
    /// <returns>Отказ стал приметой чужого места.</returns>
    public bool NoteRefusal(BlockPos where, ForeignKind kind)
    {
        var now = DateTime.UtcNow;
        int radius = Math.Max(0, RefusalRadius);
        var window = RefusalWindow > TimeSpan.Zero ? RefusalWindow : TimeSpan.Zero;

        // Просроченные не копим: за смену их набежит несколько тысяч
        refusals.RemoveAll(r => now - r.At > window);
        int count = RefusalsNear(refusals, where, now, window, radius);
        refusals.Add((where, now));

        int needed = Math.Max(1, RefusalsToBelieve);
        if (count < needed)
        {
            // Молчать нельзя, но и кричать на каждый отказ — шум: сам отказ
            // уже назвал тот, кто ломал или ставил. Говорим ровно тогда, когда
            // отказ перестал быть одиночным, — это первый признак привата
            if (count > 1 && saidRepeating != where)
            {
                saidRepeating = where;
                OnLog?.Invoke($"{where}: сервер не принял уже {count}-е действие подряд рядом " +
                              $"(нужно {needed}, чтобы счесть место чужим)");
            }
            return false;
        }

        saidRepeating = null;
        // Поверили — прошлые отказы своё дело сделали, счёт начинаем заново,
        // иначе одна метка тут же породила бы соседнюю
        refusals.RemoveAll(r => Math.Abs(r.Pos.X - where.X) <= radius &&
                                Math.Abs(r.Pos.Y - where.Y) <= radius &&
                                Math.Abs(r.Pos.Z - where.Z) <= radius);
        return Note(where, kind);
    }

    /// <summary>Дверь не открылась — за ней чужое жильё.</summary>
    public bool NoteLockedDoor(BlockPos where) => Note(where, ForeignKind.LockedDoor);

    /// <summary>Чужой сундук — признак жилья.</summary>
    public bool NoteContainer(BlockPos where) => Note(where, ForeignKind.Container);

    /// <summary>Тут ходит живой игрок.</summary>
    public bool NotePlayer(BlockPos where, string name = "") =>
        Note(where, ForeignKind.Player, name);

    /// <summary>
    /// Забыть подмеченное самим. Заявки не трогаются: их правда — у сервера,
    /// и переспрашивать её нечем, кроме переподключения.
    /// </summary>
    public int Forget()
    {
        int had = seen.Count;
        seen.Clear();
        refusals.Clear();
        saidRepeating = null;
        lastRefusal = "";
        OnLog?.Invoke(had > 0
            ? $"забыл подмеченных чужих мест: {had} (заявки сервера остались: {ForeignAreaCount} площадок)"
            : "подмеченных чужих мест и так не было — забывать нечего");
        return had;
    }

    // ---------------- приём от сервера ----------------

    private void HandlePacket(Packet_Server p)
    {
        if (p.Id != Packet_ServerIdEnum.LandClaims || p.LandClaims is not { } packet)
            return;

        // Порядок ровно как у клиента игры (GeneralPacketHandler.HandleLandClaims):
        // Allclaims ЗАМЕНЯЕТ список целиком, Addclaims ДОБАВЛЯЕТ к нему.
        // Пустой массив уходит по сети как null — поэтому проверяем на null,
        // а не на длину: иначе «заявок не осталось» и «заявок не прислали»
        // слиплись бы в одно
        int replaced = -1, added = 0, broken = 0;
        lock (gate)
        {
            if (packet.Allclaims != null)
            {
                claims.Clear();
                replaced = Read(packet.Allclaims, claims, ref broken);
            }
            if (packet.Addclaims != null)
                added = Read(packet.Addclaims, claims, ref broken);
            if (replaced < 0 && added == 0 && broken == 0)
                return;
            claimPlaces = BuildPlaces();
        }

        ClaimsKnown = true;
        string what = replaced >= 0 ? $"весь список: {replaced}" : $"добавлено: {added}";
        OnLog?.Invoke($"сервер прислал заявки ({what}); всего знаю {Count}, " +
                      $"чужих площадок {ForeignAreaCount}" +
                      (broken > 0 ? $"; не разобрал {broken} — сервер прислал их в незнакомом виде" : ""));
    }

    /// <summary>
    /// Разобрать присланные заявки. Тело каждой — protobuf-снимок LandClaim,
    /// упакованный тем же SerializerUtil, каким его читает клиент игры.
    /// </summary>
    private static int Read(Packet_LandClaim[] packed, List<LandClaim> into, ref int broken)
    {
        int taken = 0;
        foreach (var one in packed)
        {
            if (one?.Data is not { Length: > 0 } data)
                continue;
            try
            {
                var claim = SerializerUtil.Deserialize<LandClaim>(data);
                if (claim == null)
                {
                    broken++;
                    continue;
                }
                into.Add(claim);
                taken++;
            }
            catch (Exception)
            {
                // Молчать нельзя (правило 3), но и падать из-за одной кривой
                // заявки — тоже: остальные пригодны. Считаем и скажем числом
                broken++;
            }
        }
        return taken;
    }

    /// <summary>Коробки ЧУЖИХ заявок — по одной на площадку, а не на заявку.</summary>
    private List<ForeignPlace> BuildPlaces()
    {
        var now = DateTime.UtcNow;
        var places = new List<ForeignPlace>();
        foreach (var claim in claims)
        {
            if (claim.Areas == null || Mine(claim))
                continue;

            // ТОЛЬКО ПРИВАТЫ ЖИВЫХ ИГРОКОВ. Живой случай заказчика: «!добудь»
            // отказывал на КАЖДУЮ команду — «заявка игрока Trader в 237 бл, а
            // держаться велено 500». Trader — это НПС-торговец: их владения
            // раскиданы по всей карте, и держаться от них как от чужого дома
            // значит не работать нигде («на 1200 бл вокруг годного места не
            // нашлось»). У заявки игрока есть его uid, у НПС его нет — по нему
            // и отличаем. Слова заказчика: «нпс игнорируем, я имел ввиду только
            // приваты реальных игроков»
            if (SkipNpcClaims && string.IsNullOrEmpty(claim.OwnedByPlayerUid))
                continue;

            string who = claim.LastKnownOwnerName ?? "";
            foreach (var area in claim.Areas)
                if (area != null)
                    // Срок — вечность: заявку отменяет сервер, а не наши часы
                    places.Add(new ForeignPlace(ForeignKind.Claim, area, who, now, DateTime.MaxValue));
        }
        return places;
    }

    /// <summary>Стоим ли мы в СВОЕЙ заявке (по горизонтали — так же, как меряем всё остальное).</summary>
    private bool InOwnClaim(BlockPos where)
    {
        lock (gate)
        {
            foreach (var claim in claims)
            {
                if (claim.Areas == null || !Mine(claim))
                    continue;
                foreach (var area in claim.Areas)
                    if (area != null && DistanceXZ(where.X, where.Z, area) == 0)
                        return true;
            }
        }
        return false;
    }

    private static Cuboidi Box(BlockPos p) =>
        // Верхний край коробки не входит — поэтому одна клетка это [p .. p+1)
        new(p.X, p.Y, p.Z, p.X + 1, p.Y + 1, p.Z + 1);

    /// <summary>Забыть всё: новое соединение — возможно, другой мир.</summary>
    private void Reset()
    {
        lock (gate)
        {
            claims.Clear();
            claimPlaces = [];
        }
        seen.Clear();
        refusals.Clear();
        saidRepeating = null;
        ClaimsKnown = false;
        saidNoClaims = false;
        lastRefusal = "";
    }
}
