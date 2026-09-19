namespace VsBotKit;

/// <summary>Время года — как его считает игровой календарь.</summary>
public enum Season { Spring, Summer, Fall, Winter }

/// <summary>
/// Игровые часы и календарь: сервер присылает их пакетами Calendar(13) и
/// CalendarUpdate(83). Формулы взяты у GameCalendar игры, а не придуманы:
/// час суток = (всего часов) % HoursPerDay, сутки = TotalHours / HoursPerDay.
///
/// Нужен всему, что зависит от времени: оружие к ночи, сон, посев и полив.
/// </summary>
public class GameClock
{
    private long totalSeconds;
    private DateTime updatedAt = DateTime.UtcNow;

    /// <summary>Часов в сутках (обычно 24).</summary>
    public float HoursPerDay { get; private set; } = 24;

    /// <summary>Дней в месяце (обычно 9).</summary>
    public int DaysPerMonth { get; private set; } = 9;

    /// <summary>
    /// Множитель календаря и суммарная скорость времени: игровых секунд
    /// проходит realSeconds × SpeedOfTime × CalendarSpeedMul — ровно так
    /// считает GameCalendar.Tick в игре.
    /// </summary>
    public float CalendarSpeedMul { get; private set; } = 0.5f;

    /// <summary>Сумма всех модификаторов скорости времени (обычно 60).</summary>
    public float SpeedOfTime { get; private set; } = 60;

    /// <summary>Календарь получен от сервера.</summary>
    public bool Ready { get; private set; }

    public GameClock(BotClient bot)
    {
        bot.OnPacket += p =>
        {
            switch (p.Id)
            {
                case 13 when p.Calendar != null:
                    totalSeconds = p.Calendar.TotalSeconds;
                    // ВНИМАНИЕ: часы в сутках и множитель календаря приходят
                    // НЕ целыми — они упакованы SerializeFloatVeryPrecise.
                    // Без распаковки «сутки» превращаются в 240000 часов,
                    // и бот считает, что вечно идёт первый день (поймано живьём)
                    float hours = Vintagestory.Common.CollectibleNet
                        .DeserializeFloatVeryPrecise(p.Calendar.HoursPerDay);
                    HoursPerDay = hours > 0 ? hours : 24;
                    float mul = Vintagestory.Common.CollectibleNet
                        .DeserializeFloatVeryPrecise(p.Calendar.CalendarSpeedMul);
                    CalendarSpeedMul = mul > 0 ? mul : 0.5f;
                    DaysPerMonth = p.Calendar.DaysPerMonth > 0 ? p.Calendar.DaysPerMonth : 9;

                    // Скорость времени — сумма модификаторов, как в
                    // GameCalendar.CalculateCurrentTimeSpeed
                    float speed = 0;
                    if (p.Calendar.TimeSpeedModifierSpeeds != null)
                        for (int i = 0; i < p.Calendar.TimeSpeedModifierSpeedsCount; i++)
                            speed += Vintagestory.Common.CollectibleNet
                                .DeserializeFloatPrecise(p.Calendar.TimeSpeedModifierSpeeds[i]);
                    SpeedOfTime = speed > 0 ? speed : 60;

                    updatedAt = DateTime.UtcNow;
                    Ready = true;
                    break;

                case 83 when p.Calendarupdate != null:
                    totalSeconds = p.Calendarupdate.TotalSeconds;
                    updatedAt = DateTime.UtcNow;
                    Ready = true;
                    break;
            }
        };
    }

    /// <summary>
    /// Игровых секунд всего. Между пакетами время идёт само — по формуле игры:
    /// реальные секунды × SpeedOfTime × CalendarSpeedMul.
    /// </summary>
    public double TotalSeconds =>
        totalSeconds + (DateTime.UtcNow - updatedAt).TotalSeconds * SpeedOfTime * CalendarSpeedMul;

    public double TotalHours => TotalSeconds / 3600.0;

    /// <summary>
    /// СКОЛЬКО РЕАЛЬНЫХ СЕКУНД ИДЁТ ОДИН ИГРОВОЙ ЧАС. null — время сервера
    /// стоит (скорость нулевая) либо пакета календаря ещё не было: соврать тут
    /// нечем, и выдумывать «наверное, два часа» нельзя.
    ///
    /// ЗАЧЕМ ЭТО ЖИВЁТ ЗДЕСЬ. Игра меряет сроки в игровых часах: туша
    /// распадается через <c>hoursToDecay</c>, до заката столько-то часов. А
    /// человек и все паузы бота — в реальных секундах, и переводить одно в
    /// другое приходится всем. Формула ОДНА и уже стоит строкой выше
    /// (<see cref="TotalSeconds"/>): игровых секунд = реальные × SpeedOfTime ×
    /// CalendarSpeedMul. На сервере с ускоренным временем «три часа до распада»
    /// — это шесть минут, и считать такое каждому по-своему нельзя.
    /// </summary>
    public double? RealSecondsPerGameHour =>
        SpeedOfTime * CalendarSpeedMul is > 0 and var speed ? 3600.0 / speed : null;

    /// <summary>Всего суток от начала мира.</summary>
    public double TotalDays => TotalHours / HoursPerDay;

    /// <summary>Час суток: 0 — полночь, 12 — полдень.</summary>
    public double HourOfDay => TotalHours % HoursPerDay;

    /// <summary>Доля суток: 0 — полночь, 0.5 — полдень.</summary>
    public double DayFraction => HourOfDay / HoursPerDay;

    /// <summary>Дней в году (12 месяцев).</summary>
    public int DaysPerYear => Math.Max(1, DaysPerMonth * 12);

    /// <summary>Номер месяца, 1..12.</summary>
    public int Month => (int)Math.Ceiling(TotalDays % DaysPerYear / DaysPerYear * 12);

    /// <summary>
    /// Время года по месяцу — грубее, чем у сервера (он учитывает широту),
    /// но для «сеять или не сеять» этого достаточно.
    /// </summary>
    public Season Season => (Month % 12) switch
    {
        0 or 1 or 2 => Season.Winter,
        3 or 4 or 5 => Season.Spring,
        6 or 7 or 8 => Season.Summer,
        _ => Season.Fall
    };

    /// <summary>Час, когда наступает ночь (по умолчанию 20:00).</summary>
    public double NightStartsHour { get; set; } = 20;

    /// <summary>Час, когда светает (по умолчанию 6:00).</summary>
    public double DayStartsHour { get; set; } = 6;

    /// <summary>Сейчас ночь.</summary>
    public bool IsNight => NightWithin(0);

    /// <summary>
    /// НОЧЬ ЛИ, ЕСЛИ РАЗДВИНУТЬ СУМЕРКИ НА <paramref name="marginHours"/> ЧАСОВ
    /// В ОБЕ СТОРОНЫ — второй, ОТПУСКАЮЩИЙ порог для тех, кому нельзя дёргаться
    /// на самой границе (см. <see cref="Hysteresis"/>).
    ///
    /// ЗАЧЕМ ЭТО НУЖНО, ХОТЯ ВРЕМЯ И ИДЁТ ТОЛЬКО ВПЕРЁД. Час суток между
    /// пакетами сервера бот досчитывает сам (см. <see cref="TotalSeconds"/>), а
    /// пришедший пакет может поправить его НАЗАД — тогда ровно на 6:00 ответ
    /// «ночь ли» успевает перевернуться несколько раз подряд. Тому, кто на этом
    /// ответе что-то делает (фонарь в руке — это два переноса вещи на сервер),
    /// такой дребезг обходится дорого.
    ///
    /// ЗАПАС НЕ СМЕЕТ СЪЕСТЬ БОЛЬШЕ ПОЛОВИНЫ СВЕТОВОГО ДНЯ — по четверти с
    /// каждого края. Иначе выкрученное число превращало бы «ночь с запасом» в
    /// вечную ночь, и заметить это было бы нечем: бот молча носил бы фонарь
    /// круглые сутки, платя за левую руку 20% скорости голода.
    /// </summary>
    public bool NightWithin(double marginHours)
    {
        double m = Math.Max(0, marginHours);
        double светлыхЧасов = NightStartsHour - DayStartsHour;
        if (светлыхЧасов > 0)
            m = Math.Min(m, светлыхЧасов / 4);
        double h = HourOfDay;
        return h >= NightStartsHour - m || h < DayStartsHour + m;
    }

    /// <summary>Сколько игровых часов осталось до темноты (0 — уже ночь).</summary>
    public double HoursUntilNight =>
        IsNight ? 0 : NightStartsHour - HourOfDay;

    public override string ToString() =>
        Ready ? $"день {TotalDays:0}, {(int)HourOfDay:00}:{(int)(HourOfDay % 1 * 60):00}, {Season}"
              : "календарь ещё не пришёл";
}
