namespace VsBotKit;

/// <summary>
/// Временные бури: бот узнаёт о них ровно оттуда же, откуда живой игрок —
/// из канала мода «temporalstability». Сервер шлёт по нему TemporalStormRunTimeData
/// (пакет CustomPacket(55), номер канала — из NetworkChannels(56)), а клиент
/// разбирает его protobuf'ом. Никакого «знания сверх игрока» тут нет: тот же
/// пакет получает и обычный клиент, он же рисует предупреждение на экране.
///
/// Если мод выключен (канал не зарегистрирован), остаётся запасной путь —
/// объявления в чате, их игрок тоже читает.
/// </summary>
public class TemporalStorms
{
    private int channelId = -1;

    /// <summary>Буря идёт прямо сейчас.</summary>
    public bool Active { get; private set; }

    /// <summary>Абсолютный день начала следующей бури (как его знает сервер).</summary>
    public double NextStormTotalDays { get; private set; } = double.MaxValue;

    /// <summary>Абсолютный день окончания текущей бури.</summary>
    public double StormEndsTotalDays { get; private set; }

    /// <summary>
    /// Игровых суток до начала следующей бури. СЧИТАЕТСЯ КАЖДЫЙ РАЗ от текущего
    /// времени: сервер шлёт пакет только при смене состояния, и «замороженное»
    /// значение не убывало — бот не мог подготовиться заранее.
    /// </summary>
    public double DaysUntilStorm =>
        NextStormTotalDays >= double.MaxValue ? double.MaxValue : NextStormTotalDays - clock.TotalDays;

    /// <summary>Игровых суток до конца текущей бури.</summary>
    public double DaysUntilStormEnds => StormEndsTotalDays - clock.TotalDays;

    /// <summary>Сила ближайшей бури («Light», «Medium», «Heavy»).</summary>
    public string Strength { get; private set; } = "?";

    /// <summary>Данные вообще приходили (иначе бури в этом мире нет или мод выключен).</summary>
    public bool Known { get; private set; }

    /// <summary>Состояние изменилось: (буря идёт, сила).</summary>
    public event Action<bool, string>? OnChanged;

    /// <summary>Диагностика.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// За сколько игровых часов до бури пора прятаться. Ноль — «только когда
    /// началась»; по умолчанию полчаса игрового времени на подготовку.
    /// </summary>
    public double PrepareHours { get; set; } = 0.5;

    private readonly GameClock clock;

    public TemporalStorms(BotClient bot, GameClock clock)
    {
        this.clock = clock;
        bot.OnPacket += HandlePacket;
    }

    /// <summary>Пора прятаться: буря идёт или начнётся вот-вот.</summary>
    public bool ShelterNow =>
        Known && (Active || DaysUntilStorm * clock.HoursPerDay <= PrepareHours);

    private void HandlePacket(Packet_Server p)
    {
        switch (p.Id)
        {
            case 56 when p.NetworkChannels?.ChannelNames != null:
                for (int i = 0; i < p.NetworkChannels.ChannelNamesCount; i++)
                    if (p.NetworkChannels.ChannelNames[i] == "temporalstability" &&
                        p.NetworkChannels.ChannelIds != null && i < p.NetworkChannels.ChannelIdsCount)
                    {
                        channelId = p.NetworkChannels.ChannelIds[i];
                        OnLog?.Invoke($"канал бурь найден (id {channelId})");
                    }
                break;

            case 55 when p.CustomPacket is { } cp && cp.ChannelId == channelId && cp.Data != null:
                ReadStormData(cp.Data);
                break;
        }
    }

    private void ReadStormData(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            // Тип из мода выживания игры — тот же, что разбирает клиент
            var d = ProtoBuf.Serializer.Deserialize<Vintagestory.GameContent.TemporalStormRunTimeData>(ms);
            bool was = Active;
            string wasStrength = Strength;

            Active = d.nowStormActive;
            Strength = d.nextStormStrength.ToString();
            NextStormTotalDays = d.nextStormTotalDays;
            StormEndsTotalDays = d.stormActiveTotalDays;
            Known = true;

            if (was != Active || wasStrength != Strength)
            {
                OnLog?.Invoke(Active
                    ? $"временная буря НАЧАЛАСЬ ({Strength}), продлится ~{DaysUntilStormEnds * clock.HoursPerDay:0.#} ч"
                    : $"бурь нет; следующая ({Strength}) через {DaysUntilStorm:0.#} сут");
                OnChanged?.Invoke(Active, Strength);
            }
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"не разобрал данные бури: {ex.GetType().Name} {ex.Message}");
        }
    }
}
