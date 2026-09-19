using System.Collections.Concurrent;
using Vintagestory.API.Datastructures;
using Vintagestory.Common;

namespace VsBotKit;

/// <summary>Сущность мира: игрок, моб, брошенный предмет...</summary>
public sealed class EntityInfo
{
    public required long Id { get; init; }

    /// <summary>Код типа без домена: "player", "drifter-normal", "item"...</summary>
    public string Code { get; internal set; } = "";

    /// <summary>Синхронизируемые атрибуты (здоровье, голод, имя...).
    /// При полном обновлении заменяется целиком (атомарно) — читатели
    /// всегда видят согласованное дерево.</summary>
    public SyncedTreeAttribute WatchedAttributes { get; internal set; } = new();

    /// <summary>
    /// БЫЛА ЛИ ЭТА ТВАРЬ УЖЕ МЕРТВА, КОГДА ПРИШЛИ ПОЛНЫЕ ДАННЫЕ. Нужно ровно
    /// одному читателю — сроку распада туши (<see cref="Butchering.ЧасовДоРаспада"/>),
    /// и вот почему, разобрано по DLL игры (VSEssentials 1.22.7, ilspycmd):
    ///
    /// <c>EntityBehaviorDeadDecay.Initialize</c> заводит ВЛОЖЕННОЕ простое
    /// дерево «decay» и пишет туда <c>totalHoursDead = Calendar.TotalHours</c>
    /// ПРИ РОЖДЕНИИ, живой твари. <c>OnEntityDeath</c> переписывает то же поле
    /// тем же <c>decayTree.SetDouble</c> — а метит грязным только
    /// <c>SyncedTreeAttribute</c> в СВОИХ верхних сеттерах; запись во
    /// вложенное дерево не метит НИЧЕГО, и <c>MarkPathDirty</c> во всём
    /// поведении распада нет ни одного. Значит час смерти по сети НЕ ЕДЕТ.
    ///
    /// Отсюда правило, и оно выведено, а не выбрано: дерево «decay» приходит
    /// только с полными данными (<c>ParseFullEntityData</c>). Пришли они по
    /// живой твари — в поле лежит час РОЖДЕНИЯ, и назвать его часом смерти
    /// значит соврать числом. Пришли по уже мёртвой — сервер записал час
    /// смерти ДО отправки, и число настоящее.
    ///
    /// Живой случай, ради которого это заведено: бот сам убил дрифтера,
    /// которого видел живым. До правки он говорил про свежайшую тушу «сгниёт
    /// вот-вот» — потому что вычитал возраст твари, а не время лежания.
    /// </summary>
    public bool WasDeadWhenFullDataCame { get; internal set; }

    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Z { get; internal set; }
    public float Yaw { get; internal set; }

    /// <summary>
    /// Поставить позицию изнутри библиотеки. Для СВОЕЙ сущности это делает
    /// только тело (Body): физика игры — единственный владелец позиции.
    /// </summary>
    internal void SetPosition(double x, double y, double z, float yaw)
    {
        X = x;
        Y = y;
        Z = z;
        Yaw = yaw;
    }

    public bool IsPlayer => Code == "player";
    public bool IsItem => Code == "item";

    /// <summary>Имя игрока (из нашлёпки-нейминга), если это игрок.</summary>
    public string? PlayerName => WatchedAttributes.GetTreeAttribute("nametag")?.GetString("name");

    /// <summary>Версия позиции (сервер отклоняет пакеты с меньшей — echo при отправке).</summary>
    public int PositionVersion => WatchedAttributes.GetInt("positionVersionNumber");

    public float? Health => WatchedAttributes.GetTreeAttribute("health")?.TryGetFloat("currenthealth");
    public float? MaxHealth => WatchedAttributes.GetTreeAttribute("health")?.TryGetFloat("maxhealth");
    /// <summary>
    /// Значение стата (walkspeed, hungerrate...) — сумма всех модификаторов
    /// в WatchedAttributes["stats"][имя]: base + trait (класс персонажа) +
    /// wearablemod (броня) + модовые. Сервер шлёт их целиком, так что бот
    /// узнаёт свою настоящую скорость, а не выдуманную.
    /// </summary>
    public float StatValue(string name, float fallback = 1f)
    {
        var stat = WatchedAttributes.GetTreeAttribute("stats")?.GetTreeAttribute(name);
        if (stat == null)
            return fallback;
        float sum = 0;
        bool any = false;
        foreach (var (_, value) in stat)
        {
            if (value is not FloatAttribute f)
                continue;
            sum += f.value;
            any = true;
        }
        return any ? sum : fallback;
    }

    /// <summary>Множитель скорости ходьбы персонажа (класс, броня, снаряжение).</summary>
    public float WalkSpeedStat => StatValue("walkspeed");

    /// <summary>Запас воздуха под водой (40000 = 40 секунд).</summary>
    /// <summary>
    /// Вектор отброса от удара — его считает и присылает СЕРВЕР (kbdirX/Y/Z).
    /// Игровой PModuleKnockback складывает его в движение; без передачи в
    /// физическое тело бота от удара вообще не откидывало.
    /// </summary>
    public (double X, double Y, double Z) KnockbackDir => (
        WatchedAttributes.GetDouble("kbdirX"),
        WatchedAttributes.GetDouble("kbdirY"),
        WatchedAttributes.GetDouble("kbdirZ"));

    public float? Oxygen => WatchedAttributes.GetTreeAttribute("oxygen")?.TryGetFloat("currentoxygen");
    public float? MaxOxygen => WatchedAttributes.GetTreeAttribute("oxygen")?.TryGetFloat("maxoxygen");

    /// <summary>Доля оставшегося воздуха (1 — полный запас).</summary>
    public double OxygenFraction =>
        Oxygen is { } o && MaxOxygen is { } m && m > 0 ? o / m : 1.0;

    public float? Saturation => WatchedAttributes.GetTreeAttribute("hunger")?.TryGetFloat("currentsaturation");
    public float? MaxSaturation => WatchedAttributes.GetTreeAttribute("hunger")?.TryGetFloat("maxsaturation");

    public double DistanceTo(double x, double y, double z)
    {
        double dx = X - x, dy = Y - y, dz = Z - z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() =>
        // Клетка ИГРОВАЯ, одним правилом со всем журналом: имя существа рядом с
        // адресом, которого нет у человека на экране, бесполезно
        $"{(IsPlayer ? PlayerName ?? "player" : Code)}#{Id} {WorldOrigin.Say(X, Y, Z)}";
}

/// <summary>
/// Модель сущностей: кто есть в мире вокруг бота. Наполняется пакетами
/// Entities(40)/EntitySpawn(34)/Entity(33), позиции — через обёрнутые в TCP
/// UDP-пакеты (79) и EntitySpawnPosition(80), атрибуты — 37/38/60.
/// Данные сущности — бинарный формат Entity.FromBytes(isSync: true):
///   int64 EntityId, TreeAttribute watched, EntityPos (3×double + 3×float +
///   int32 + 3×double motion), 3×double posBeforeFalling, string code.
/// </summary>
public class EntityModel
{
    private readonly ConcurrentDictionary<long, EntityInfo> entities = new();

    /// <summary>Id собственной сущности бота (из PlayerData, после входа).</summary>
    public long OwnEntityId { get; private set; } = -1;

    /// <summary>Собственная сущность бота (позиция, здоровье, голод).</summary>
    public EntityInfo? Self => entities.TryGetValue(OwnEntityId, out var e) ? e : null;

    public event Action<EntityInfo>? OnEntitySpawned;
    public event Action<EntityInfo>? OnEntityDespawned;

    /// <summary>
    /// СЕРВЕР СКАЗАЛ, ГДЕ СТОИТ ЧУЖАЯ СУЩНОСТЬ (не наша: своё положение
    /// клиент-авторитетно).
    ///
    /// ЗАЧЕМ НАРУЖУ. Скорость твари не приходит ни одним пакетом — её МЕРЯЮТ по
    /// присланным положениям (<see cref="CombatPace"/>). Пока замер жил в ходе
    /// боя, он зависел от того, дошла ли до боя очередь: живой случай 19.08 —
    /// между двумя ходами боя проходило три и двадцать восемь секунд, а замер
    /// берётся только с промежутка от 0,2 до 2 секунд. Ни одного замера не
    /// набралось НИ РАЗУ, и бот отказывался и драться, и убегать: «скорость его
    /// ещё не мерил». Мерить надо там, где приходят факты, — здесь.
    /// </summary>
    public event Action<EntityInfo>? OnEntityMoved;

    /// <summary>Ошибка разбора данных сущности (диагностика).</summary>
    public event Action<string>? OnParseError;

    /// <summary>Сервер подвинул нас самих (телепорт/коррекция) — для диагностики.</summary>
    public event Action<string>? OnOwnPositionOverridden;

    public int Count => entities.Count;

    private readonly BotClient bot;

    public EntityModel(BotClient bot)
    {
        this.bot = bot;
        bot.OnPacket += HandlePacket;
    }

    public EntityInfo? Get(long id) => entities.TryGetValue(id, out var e) ? e : null;

    /// <summary>
    /// Забыть всех: после переподключения прошлые сущности не существуют
    /// (id выдаются заново), и держаться за них — верный способ бить пустоту.
    /// </summary>
    public void Reset()
    {
        entities.Clear();
        OwnEntityId = -1;
    }

    public IEnumerable<EntityInfo> All => entities.Values;

    /// <summary>Сущности в радиусе от точки, ближайшие первыми.</summary>
    public IEnumerable<EntityInfo> Nearby(double x, double y, double z, double radius) =>
        entities.Values
            .Select(e => (e, dist: e.DistanceTo(x, y, z)))
            .Where(p => p.dist <= radius)
            .OrderBy(p => p.dist)
            .Select(p => p.e);

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 40: // Entities (начальная загрузка)
                if (p.Entities?.Entities != null)
                    for (int i = 0; i < p.Entities.EntitiesCount; i++)
                        UpsertEntity(p.Entities.Entities[i], spawned: false);
                break;

            case 34: // EntitySpawn
                if (p.EntitySpawn?.Entity != null)
                    for (int i = 0; i < p.EntitySpawn.EntityCount; i++)
                        UpsertEntity(p.EntitySpawn.Entity[i], spawned: true);
                break;

            case 33: // Entity (догрузка одиночной)
                if (p.Entity != null)
                    UpsertEntity(p.Entity, spawned: false);
                break;

            case 36: // EntityDespawn
                if (p.EntityDespawn?.EntityId != null)
                    for (int i = 0; i < p.EntityDespawn.EntityIdCount; i++)
                        if (entities.TryRemove(p.EntityDespawn.EntityId[i], out var gone))
                            OnEntityDespawned?.Invoke(gone);
                break;

            case 37: // EntityAttributes (полное обновление — тот же формат, что при спавне)
                if (p.EntityAttributes is { } fa && Get(fa.EntityId) is { } fe && fa.Data != null)
                    ParseFullEntityData(fe, fa.Data, ownEntity: fa.EntityId == OwnEntityId);
                break;

            case 38: // EntityAttributeUpdate (частичное)
                ApplyPartialUpdate(p.EntityAttributeUpdate);
                break;

            case 60: // BulkEntityAttributes
                if (p.BulkEntityAttributes is { } bulk)
                {
                    if (bulk.FullUpdates != null)
                        for (int i = 0; i < bulk.FullUpdatesCount; i++)
                            if (Get(bulk.FullUpdates[i].EntityId) is { } be && bulk.FullUpdates[i].Data != null)
                                ParseFullEntityData(be, bulk.FullUpdates[i].Data,
                                    ownEntity: bulk.FullUpdates[i].EntityId == OwnEntityId);
                    if (bulk.PartialUpdates != null)
                        for (int i = 0; i < bulk.PartialUpdatesCount; i++)
                            ApplyPartialUpdate(bulk.PartialUpdates[i]);
                }
                break;

            case 41: // PlayerData — узнаём свой entity id (пакет приходит и про других игроков!)
                if (p.PlayerData != null && p.PlayerData.EntityId != 0 &&
                    p.PlayerData.PlayerUID == bot.PlayerUid)
                    OwnEntityId = p.PlayerData.EntityId;
                break;

            case 79: // UdpPacket поверх TCP (мы просили позиции по TCP)
                if (p.UdpPacket is { } udp)
                {
                    if (udp.Id == 4 && udp.BulkPositions?.EntityPositions != null)
                    {
                        foreach (var ep in udp.BulkPositions.EntityPositions)
                            if (ep != null)
                                UpdatePosition(ep);
                    }
                    else if (udp.Id == 5 && udp.EntityPosition != null)
                    {
                        UpdatePosition(udp.EntityPosition);
                    }
                }
                break;

            case 80: // EntitySpawnPosition
                if (p.EntityPosition != null)
                    UpdatePosition(p.EntityPosition);
                break;
        }
    }

    private void UpsertEntity(Packet_Entity pe, bool spawned)
    {
        if (pe.Data == null)
            return;
        bool isNew = !entities.TryGetValue(pe.EntityId, out var e);
        if (isNew)
            e = new EntityInfo { Id = pe.EntityId };

        try
        {
            // Своё положение сервер нам не диктует — кроме самого первого раза,
            // когда мы ещё не знаем, где находимся
            ParseFullEntityData(e!, pe.Data,
                ownEntity: !isNew && pe.EntityId == OwnEntityId);
        }
        catch (Exception ex)
        {
            OnParseError?.Invoke($"Сущность {pe.EntityType}#{pe.EntityId}: {ex.GetType().Name} {ex.Message}");
            return;
        }

        if (isNew)
        {
            entities[pe.EntityId] = e!;
            if (spawned)
                OnEntitySpawned?.Invoke(e!);
        }
        // Полные данные приходят и по ходу жизни сущности (пакет 33), и в них
        // тоже лежит положение — для замера скорости это такой же факт, как и
        // короткий пакет позиции
        if (pe.EntityId != OwnEntityId)
            OnEntityMoved?.Invoke(e!);
    }

    /// <summary>
    /// Сервер сдвинул нас далеко (телепорт, возрождение, транслокатор) — такое
    /// принимаем. Порог с запасом больше обычных расхождений на бегу.
    /// </summary>
    private static bool IsBigJump(EntityInfo e, double x, double y, double z) =>
        Math.Abs(e.X - x) > 4 || Math.Abs(e.Y - y) > 4 || Math.Abs(e.Z - z) > 4;

    /// <param name="ownEntity">
    /// true для собственной сущности: своё положение мы знаем лучше сервера
    /// (позиция клиент-авторитетна) и берём его только при крупном скачке,
    /// а атрибуты — здоровье, сытость — принимаем всегда.
    /// </param>
    private void ParseFullEntityData(EntityInfo e, byte[] data, bool ownEntity = false)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        reader.ReadInt64(); // EntityId — уже знаем
        var freshAttributes = new SyncedTreeAttribute();
        freshAttributes.FromBytes(reader);
        e.WatchedAttributes = freshAttributes; // swap целиком — без «пустого» окна для читателей
        // Мёртвой ли она была В ЭТОТ МИГ — спрашивается ЗДЕСЬ и только здесь:
        // это единственное место, где дерево «decay» приезжает от сервера, и
        // ответ годен ровно для того дерева, что сейчас положено (см.
        // EntityInfo.WasDeadWhenFullDataCame). Здоровье берём из СВЕЖЕГО
        // дерева, а не из e.Health: то читало бы уже подменённое поле в
        // следующей строке — здесь разницы нет, но полагаться на порядок строк
        // в таком вопросе нельзя
        e.WasDeadWhenFullDataCame =
            freshAttributes.GetTreeAttribute("health")?.TryGetFloat("currenthealth") is { } hp
            && hp <= 0;

        // EntityPos — читаем всегда (поток последовательный), применяем по флагу
        double px = reader.ReadDouble();
        double y = reader.ReadDouble();
        int dimension = (int)y / 32768;
        double py = y - dimension * 32768;
        double pz = reader.ReadDouble();
        reader.ReadSingle(); // roll
        float yaw = reader.ReadSingle();
        reader.ReadSingle(); // pitch
        reader.ReadInt32();  // stance
        reader.ReadDouble(); reader.ReadDouble(); reader.ReadDouble(); // motion
        if (!ownEntity || IsBigJump(e, px, py, pz))
        {
            if (ownEntity)
                // Числа ИГРОВЫЕ (WorldOrigin): строку читает человек, а не
                // сервер, и сверяет он её с окном координат игры
                OnOwnPositionOverridden?.Invoke(
                    $"полные данные: {WorldOrigin.Say(e.X, e.Y, e.Z)} -> " +
                    $"{WorldOrigin.Say(px, py, pz)}");
            e.X = px;
            e.Y = py;
            e.Z = pz;
            e.Yaw = yaw;
        }

        reader.ReadDouble(); reader.ReadDouble(); reader.ReadDouble(); // posBeforeFalling

        string code = reader.ReadString();
        e.Code = code.StartsWith("game:") ? code[5..] : code;
        // дальше идут анимации/теги — боту не нужны
    }

    private void ApplyPartialUpdate(Packet_EntityAttributeUpdate? upd)
    {
        if (upd?.Attributes == null || Get(upd.EntityId) is not { } e)
            return;
        for (int i = 0; i < upd.AttributesCount; i++)
        {
            var pa = upd.Attributes[i];
            if (pa?.Path != null && pa.Data != null)
                e.WatchedAttributes.PartialUpdate(pa.Path, pa.Data);
        }
    }

    private void UpdatePosition(Packet_EntityPosition ep)
    {
        if (Get(ep.EntityId) is not { } e)
            return;

        double nx = CollectibleNet.DeserializeDoublePrecise(ep.X);
        double y = CollectibleNet.DeserializeDoublePrecise(ep.Y);
        int dimension = (int)y / 32768;
        double ny = y - dimension * 32768;
        double nz = CollectibleNet.DeserializeDoublePrecise(ep.Z);

        // Своё положение авторитетно на клиенте. Мелкие расхождения с сервером
        // игнорируем (иначе его запаздывающее мнение перетирает наше движение —
        // бот «сползает» назад по лестнице), но крупные скачки принимаем:
        // это телепорт, возрождение или перенос транслокатором.
        if (ep.EntityId == OwnEntityId && !ep.Teleport && !IsBigJump(e, nx, ny, nz))
            return;

        if (ep.EntityId == OwnEntityId)
            OnOwnPositionOverridden?.Invoke(
                $"пакет позиции (телепорт={ep.Teleport}): {WorldOrigin.Say(e.X, e.Y, e.Z)} -> " +
                $"{WorldOrigin.Say(nx, ny, nz)}");

        e.X = nx;
        e.Y = ny;
        e.Z = nz;
        e.Yaw = CollectibleNet.DeserializeFloatPrecise(ep.Yaw);

        if (ep.EntityId != OwnEntityId)
            OnEntityMoved?.Invoke(e);
    }
}
