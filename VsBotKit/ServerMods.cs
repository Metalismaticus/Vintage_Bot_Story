namespace VsBotKit;

/// <summary>Мод, объявленный сервером при знакомстве.</summary>
/// <param name="Id">modid — короткое имя из modinfo.json («tlpath»).</param>
/// <param name="Name">Человеческое имя («TLPath»).</param>
/// <param name="Version">Версия мода.</param>
/// <param name="NetworkVersion">
/// Версия сетевого протокола мода. Пустая — мод по сети не разговаривает вовсе,
/// то есть он либо чисто клиентский, либо меняет только ассеты. Для нас это
/// важнее имени: разговаривающий мод — единственный, чьи пакеты бот может не
/// понять.
/// </param>
/// <param name="RequiredOnClient">
/// Обязателен ли на клиенте. Бот НЕ настоящий клиент и модов не грузит; сервер
/// об этом не спрашивает (проверка идёт по своему списку и по чёрному/белому
/// спискам), но человеку знать полезно: у такого мода почти наверняка есть
/// свой сетевой канал.
/// </param>
public readonly record struct ServerMod(
    string Id, string Name, string Version, string NetworkVersion, bool RequiredOnClient)
{
    /// <summary>Мод, который может слать боту незнакомые пакеты.</summary>
    public bool Talks => NetworkVersion is { Length: > 0 };

    public override string ToString() =>
        $"{Id} {Version}" + (Name is { Length: > 0 } && Name != Id ? $" ({Name})" : "") +
        (Talks ? $", сеть {NetworkVersion}" : ", без сети");
}

/// <summary>
/// ЧТО СТОИТ НА СЕРВЕРЕ И О ЧЁМ ОН С НАМИ ГОВОРИТ — двумя списками, оба
/// приходят сами, спрашивать ничего не надо.
///
/// ЗАЧЕМ ЭТО ЕСТЬ. Когда на сервере моды, у бота меняется ровно две вещи, и
/// они разной природы:
///
///   1) НОВЫЕ БЛОКИ И ПРЕДМЕТЫ — это НЕ проблема. Реестр сервер шлёт целиком
///      (пакет 19), и модовый блок приезжает в нём наравне с ванильным: код,
///      материал, коробки коллизии, атрибуты. Библиотека нигде не спрашивает
///      «а есть ли такой код в списке ванили» — она спрашивает у реестра. Поэтому
///      модовую руду бот увидит, по модовой лестнице полезет, модовый сундук
///      откроет. Это работает само и проверять тут нечего.
///
///   2) НОВЫЕ ПАКЕТЫ — а вот это мы не знаем и знать не можем. Мод заводит свой
///      сетевой канал, сервер объявляет его в пакете 56 (NetworkChannels), и
///      дальше шлёт по нему пакет 55 (CustomPacket) со своим содержимым. Что
///      внутри — известно только моду: это его собственный protobuf-тип из его
///      собственной сборки. Бот такие пакеты ПОЛУЧАЕТ, но разобрать не может,
///      пока кто-то не научит его конкретному каналу — ровно так, как это
///      сделано для бурь (см. <see cref="TemporalStorms"/>: там мы знаем тип,
///      потому что он лежит в сборках самой игры).
///
/// Поэтому класс не выдумывает поддержку, а честно показывает границу: вот моды,
/// вот каналы, вот те из них, которые бот РАЗБИРАЕТ, — остальные для него шум.
/// Разработчику мода этого хватает, чтобы понять, почему бот не видит его
/// механику: «мой канал в списке есть, но бот его не разбирает» — это ответ, а
/// «бот не работает с модами» — нет.
///
/// СПИСОК НЕПОЛОН, И ЭТО НЕ НАША БЕДА. Сервер объявляет только ОБОЮДНЫЕ моды
/// (<c>side: Universal</c>) — так написано у него в коде:
/// <c>from mod in ModLoader.Mods where mod.Info.Side.IsUniversal()</c>
/// (ServerSystemHandshake.CreatePacketIdentification). Мода с
/// <c>side: Server</c> в списке НЕ БУДЕТ, хотя он работает и его блоки приедут
/// в реестре; мода с <c>side: Client</c> на сервере нет вовсе. Поэтому пустой
/// список — это «сервер не назвал», а не «на сервере ничего не стоит», и
/// говорить надо именно так. Список каналов (56) такой оговорки не требует:
/// туда попадает всякий, кто завёл канал, с любой стороны.
/// </summary>
public sealed class ServerMods
{
    private readonly List<ServerMod> mods = [];
    private readonly Dictionary<string, int> channels = [];
    private readonly HashSet<string> understood = [];

    /// <summary>Диагностика.</summary>
    public event Action<string>? OnLog;

    /// <summary>Сервер представился и назвал свои моды.</summary>
    public event Action<IReadOnlyList<ServerMod>>? OnModsKnown;

    public ServerMods(BotClient bot) => bot.OnPacket += HandlePacket;

    /// <summary>Моды сервера. Пусто до знакомства (пакет 1).</summary>
    public IReadOnlyList<ServerMod> Mods => mods;

    /// <summary>Сервер уже представился — список модов настоящий, а не «ещё не пришёл».</summary>
    public bool Known { get; private set; }

    /// <summary>Каналы связи модов: имя → номер. Приходят пакетом 56.</summary>
    public IReadOnlyDictionary<string, int> Channels => channels;

    /// <summary>Стоит ли мод на сервере (по modid, регистр не важен).</summary>
    public bool Has(string modid) =>
        mods.Any(m => string.Equals(m.Id, modid, StringComparison.OrdinalIgnoreCase));

    /// <summary>Мод по modid или null.</summary>
    public ServerMod? Find(string modid) =>
        mods.FirstOrDefault(m => string.Equals(m.Id, modid, StringComparison.OrdinalIgnoreCase))
            is { Id.Length: > 0 } m ? m : null;

    /// <summary>Номер канала мода или −1: по нему узнаются пакеты 55.</summary>
    public int ChannelId(string name) => channels.TryGetValue(name, out int id) ? id : -1;

    /// <summary>
    /// Объявить канал разобранным: «этот я понимаю». Зовёт тот, кто научился
    /// читать его пакеты, — иначе список «понимаем» пришлось бы держать здесь,
    /// и он врал бы при каждом новом разборе на стороне.
    /// </summary>
    public void MarkUnderstood(string channel) => understood.Add(channel);

    /// <summary>Разбирает ли бот пакеты этого канала.</summary>
    public bool Understands(string channel) => understood.Contains(channel);

    /// <summary>
    /// Каналы, которые бот получает, но НЕ разбирает. Это и есть честная
    /// граница поддержки модов — то, чего бот про сервер не знает.
    /// </summary>
    public IEnumerable<string> UnknownChannels =>
        channels.Keys.Where(c => !understood.Contains(c)).OrderBy(c => c);

    /// <summary>
    /// КАНАЛЫ САМОЙ ИГРЫ — то же, что <see cref="Vanilla"/> для модов, только
    /// для связи.
    ///
    /// ЗАЧЕМ. Заказчик прочёл в журнале «каналов 40, НЕ разбираем: …, oremap,
    /// rifts, temporalstability, worldmap, …» и спросил ровно то, что и должен
    /// был спросить: «я моды никакие на сервер не ставил, может что-то
    /// официальное?» Спросил ПРАВИЛЬНО — все четыре ванильные. Список каналов
    /// без этой пометки читается как «на сервере сорок чужих модов», и это
    /// пугает на ровном месте.
    ///
    /// Список СНЯТ СО СБОРОК УСТАНОВЛЕННОЙ ИГРЫ (1.22.6), а не придуман:
    /// вызовы <c>api.Network.RegisterChannel("…")</c> в VSSurvivalMod.dll,
    /// VSEssentials.dll, VSCreativeMod.dll и VintagestoryLib.dll. Ванильная
    /// игра собрана из модов, поэтому её собственные каналы приезжают ровно
    /// тем же пакетом 56, что и чужие, и отличить их иначе нечем.
    ///
    /// Полнота списка — не догма: игра растёт, а мод может завести канал с
    /// таким же именем. Поэтому список НИЧЕГО НЕ ЗАПРЕЩАЕТ и ни на что не
    /// влияет, кроме слов в журнале.
    /// </summary>
    public static readonly IReadOnlySet<string> VanillaChannels =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "EntityAnims", "HeldItemMouseInputHandler", "StoryGenFailed", "activityEditor",
            "auctionHouse", "beamplacer", "blockreinforcement", "catchcreature",
            "charselection", "clothphysics", "commandhandbook", "detailmodesync",
            "devastation", "editablebook", "entityownership", "errorreporter",
            "gameeffects", "hairstyling", "journal", "latitudedata", "lootrandomizer",
            "oremap", "remoteplayertracker", "riftWeather", "rifts", "screenshake",
            "sleeping", "storylockeddoors", "survivalCoreConfig", "temporalstability",
            "timeswitch", "tobiasteleporter", "tpManager", "tutorial", "upgradeTasks",
            "variable", "vsmechnetwork", "weather",
            // «worldedit» — ТОЖЕ ИГРА, а не чужой мод. Пропуск виден в живом
            // журнале заказчика (16.08, 14:40): «чужих 1 из 40 … worldedit» —
            // то самое пугающее «на сервере чей-то мод», ради которого список и
            // заводился. Канал ванильный: его держит редактор мира из
            // VSCreativeMod.dll (WorldEditClientHandler:
            // capi.Network.GetChannel("worldedit"), сверено по 1.22.7)
            "worldedit", "worldmap"
        };

    /// <summary>Канал самой игры, а не чужого мода.</summary>
    public static bool IsVanillaChannel(string channel) => VanillaChannels.Contains(channel);

    /// <summary>
    /// Каналы, которых у ванильной игры нет, — то есть точно чьи-то чужие.
    /// Именно их стоит показывать человеку: остальное — сама игра.
    /// </summary>
    public IEnumerable<string> ForeignChannels =>
        channels.Keys.Where(c => !IsVanillaChannel(c)).OrderBy(c => c);

    /// <summary>
    /// Что рассказать человеку. Без бодрости: если сервер ещё не представился,
    /// так и сказано — «не знаю», а не «модов нет».
    /// </summary>
    public string Describe()
    {
        if (!Known)
            return "сервер ещё не представился — какие на нём моды, не знаю";

        var свои = mods.Where(m => !Vanilla.Contains(m.Id)).ToList();
        // «Сервер не назвал» вместо «модов нет»: серверные (side: Server) моды
        // в это объявление не попадают вовсе, и уверенное «модов нет» было бы
        // враньём ровно там, где разработчик его проверить не может
        string шапка = свои.Count == 0
            ? $"сверх ванильных сервер модов не назвал (объявляет он только обоюдные, " +
              $"side: Universal — серверный мод сюда не попадёт; всего объявлено {mods.Count})"
            : $"модов сверх ванильных {свои.Count}: " +
              string.Join("; ", свои.Select(m => m.ToString()));

        // ЧУЖИЕ КАНАЛЫ — ОТДЕЛЬНО ОТ ВАНИЛЬНЫХ. Раньше все сорок валились в одну
        // строку «НЕ разбираем», и заказчик честно недоумевал: «я моды никакие
        // на сервер не ставил». Он и не ставил — сорок каналов завела сама игра
        var чужие = ForeignChannels.Where(c => !understood.Contains(c)).ToList();
        int ванильных = channels.Keys.Count(IsVanillaChannel);
        // СЧИТАЕМ РАЗОБРАННЫМИ ТОЛЬКО ТО, ЧТО СЕРВЕР И ВПРЯМЬ ОБЪЯВИЛ.
        // «Понимаю» отмечает бот при сборке (VsBot: temporalstability и
        // tpManager), ещё до знакомства с сервером, — а сервер этих каналов
        // может и не завести. Пока считался весь список отметок, строка
        // обещала человеку разбор того, чего на сервере нет вовсе
        int разбираем = channels.Keys.Count(understood.Contains);

        string каналы = channels.Count == 0
            ? "каналов модов сервер не объявлял"
            : $"каналов {channels.Count} (ванильных {ванильных}), разбираем {разбираем}" +
              (чужие.Count > 0
                  // ЧИСЛО ЧУЖИХ — ПЕРВЫМ СЛОВОМ. Заказчик спрашивает не «какие
                  // имена», а «не поставил ли я лишнего», и ответ ему нужен
                  // счётом: «чужой 1 из 40» читается сразу, а список имён без
                  // числа выглядит как «моды повсюду»
                  ? $"; чужих {чужие.Count} из {channels.Count}, их НЕ разбираем: " +
                    $"{string.Join(", ", чужие)} — " +
                    "их пакеты бот получает, но что внутри, знает только сам мод"
                  : "; чужих каналов нет — все от самой игры");

        return $"{шапка}. {каналы}";
    }

    /// <summary>
    /// Моды САМОЙ ИГРЫ. Ванильная игра собрана из модов, и сервер объявляет их
    /// наравне с чужими — без этого списка «на сервере 5 модов» было бы правдой
    /// на голом сервере и сбивало бы с толку каждый раз.
    /// Список из папки Mods установленной игры (game, survival, creative…).
    /// </summary>
    private static readonly HashSet<string> Vanilla =
        new(StringComparer.OrdinalIgnoreCase) { "game", "survival", "creative" };

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 1 when p.Identification is { } ident:
                mods.Clear();
                for (int i = 0; i < ident.ModsCount; i++)
                {
                    var m = ident.Mods?[i];
                    if (m?.Modid is not { Length: > 0 } id)
                        continue;
                    mods.Add(new ServerMod(id, m.Name ?? id, m.Version ?? "?",
                        m.Networkversion ?? "", m.RequiredOnClient));
                }
                Known = true;
                OnLog?.Invoke(Describe());
                OnModsKnown?.Invoke(mods);
                break;

            case 56 when p.NetworkChannels is { ChannelNames: { } names, ChannelIds: { } ids }:
                for (int i = 0; i < p.NetworkChannels.ChannelNamesCount && i < p.NetworkChannels.ChannelIdsCount; i++)
                    if (names[i] is { Length: > 0 } name)
                        channels[name] = ids[i];
                OnLog?.Invoke($"каналы модов объявлены: {channels.Count} " +
                              $"({string.Join(", ", channels.Keys.OrderBy(c => c))})");
                break;
        }
    }
}
