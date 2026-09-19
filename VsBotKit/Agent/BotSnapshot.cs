using System.Text;

namespace VsBotKit.Agent;

/// <summary>
/// Снимок обстановки для нейросети — то же самое, что видит игрок на экране:
/// своё состояние, время, погода, кто рядом, что рядом. Никаких «сверх-знаний»
/// (содержимое закрытых сундуков, адреса транслокаторов) сюда не попадает —
/// правило «бот не может того, чего не может игрок» действует и для ИИ-роли.
///
/// Отдаётся ТЕКСТОМ, а не JSON: модели читают его лучше, а лишние скобки —
/// это лишние токены на каждом такте.
/// </summary>
public static class BotSnapshot
{
    public static string Describe(VsBot bot)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Я: {bot.Name}, здоровье {bot.Self.Health:0.#}/{bot.Self.MaxHealth:0.#}, " +
                      $"сытость {bot.Self.Saturation:0}/{bot.Self.MaxSaturation:0}, " +
                      $"стою на ({bot.PlayerCoordsHere()})" +
                      (bot.Movement.IsSwimming ? ", плыву" : "") +
                      (bot.Movement.IsClimbing ? ", вишу на лестнице" : ""));

        if (bot.Clock.Ready)
            sb.AppendLine($"Время: {bot.Clock}{(bot.Clock.IsNight ? " (ночь)" : "")}");
        if (bot.Storms.Known)
            sb.AppendLine(bot.Storms.Active
                ? $"ВРЕМЕННАЯ БУРЯ ИДЁТ ({bot.Storms.Strength}) — снаружи опасно"
                : $"Бурь нет; следующая ({bot.Storms.Strength}) через {bot.Storms.DaysUntilStorm:0.#} сут");

        // ТЕМПОРАЛЬНАЯ СТАБИЛЬНОСТЬ — только когда сервер её ПРИСЫЛАЕТ. Строка
        // «стабильность неизвестна» в каждом снимке научила бы нейросеть, что
        // это обычное дело, и она перестала бы отличать «мир без стабильности»
        // от «бот ещё не в мире»
        if (bot.Stability.Known)
            sb.AppendLine($"Темпоральная стабильность: {bot.Stability.Say()}" +
                          (bot.Self.Position is { } где &&
                           bot.Stability.NearestHarmful(где.X, где.Y, где.Z) is { } рядом
                              ? $"; РАЗЛОМ в {рядом.Distance:0.#} бл — он её и тянет"
                              : ""));

        sb.AppendLine(DescribeInventory(bot));

        if (bot.Self.Position is { } me)
        {
            var players = bot.Entities.Nearby(me.X, me.Y, me.Z, 32)
                .Where(e => e.IsPlayer && e.Id != bot.Entities.OwnEntityId)
                .Select(e => $"{e.PlayerName ?? "игрок"} в {e.DistanceTo(me.X, me.Y, me.Z):0.#} бл " +
                             $"({bot.PlayerCoords(e.X, e.Y, e.Z)})")
                .ToList();
            sb.AppendLine("Игроки рядом: " + (players.Count > 0 ? string.Join("; ", players) : "нет"));

            // СУЩЕСТВА — ЭТО ЖИВОЕ, И СПРАШИВАЕТСЯ ЭТО У РЕЕСТРА СЕРВЕРА.
            // Иначе в снимке для нейросети значилось «Существа рядом:
            // bowtornprojectile x3» — то есть воткнутые в стену стрелы,
            // выданные модели за тварей (тот же корень, что и у боя со
            // стрелами 17.08). Тег «inanimate» ставит сама игра
            var mobs = bot.Entities.Nearby(me.X, me.Y, me.Z, 20)
                .Where(e => !e.IsPlayer && !e.IsItem && !bot.EntityTypes.Inanimate(e.Code))
                .GroupBy(e => e.Code)
                .Select(g => $"{g.Key} x{g.Count()}")
                .Take(8)
                .ToList();
            sb.AppendLine("Существа рядом: " + (mobs.Count > 0 ? string.Join(", ", mobs) : "никого"));

            var drops = bot.Entities.Nearby(me.X, me.Y, me.Z, 12).Count(e => e.IsItem);
            if (drops > 0)
                sb.AppendLine($"На земле лежит вещей: {drops} (подбираются ходьбой по ним)");

            var signs = bot.Read.SignsNear(16).Take(6).ToList();
            if (signs.Count > 0)
                sb.AppendLine("Таблички: " + string.Join(" | ", signs.Select(s =>
                    $"({bot.PlayerCoords(s.Pos.X, s.Pos.Y, s.Pos.Z)}) {string.Join(" / ", s.Lines)}")));

            var chests = bot.Read.ContainersNear(10).Take(6).ToList();
            if (chests.Count > 0)
                sb.AppendLine("Сундуки: " + string.Join(", ", chests.Select(c =>
                    $"({bot.PlayerCoords(c.Pos.X, c.Pos.Y, c.Pos.Z)})")));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Что в руке и что в сумках — кратко.</summary>
    public static string DescribeInventory(VsBot bot)
    {
        var held = bot.Hands.Held;
        var items = new List<string>();
        foreach (var (invId, slots) in bot.Self.OwnInventories)
        {
            if (invId.StartsWith("character") || invId.StartsWith("mouse"))
                continue;
            foreach (var slot in slots)
                if (!slot.IsEmpty && slot.Code != null)
                    items.Add($"{slot.Code} x{slot.Count}");
        }
        // Одинаковые предметы из разных слотов сливаем — модели не нужен раскладной хотбар
        string bag = items.Count == 0
            ? "пусто"
            : string.Join(", ", items
                .GroupBy(s => s[..s.LastIndexOf(" x", StringComparison.Ordinal)])
                .Select(g => $"{g.Key} x{g.Sum(s => int.Parse(s[(s.LastIndexOf(" x", StringComparison.Ordinal) + 2)..]))}")
                .Take(24));

        return $"В руке: {(held is { IsEmpty: false } ? held.Code : "ничего")}. С собой: {bag}";
    }
}
