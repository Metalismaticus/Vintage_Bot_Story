using Newtonsoft.Json.Linq;

namespace VsBotKit;

/// <summary>
/// ОБХОД ОПИСАНИЙ СУЩНОСТЕЙ УСТАНОВЛЕННОЙ ИГРЫ — ОДИН НА ВСЕХ, КТО ИХ ЧИТАЕТ.
///
/// ПОЧЕМУ ЗАВЕДЁН. Обход был выписан ДВАЖДЫ, слово в слово: в
/// <see cref="CreatureSpeeds"/> (объявленные скорости тварей) и в
/// <see cref="CarcassDecay"/> (сроки распада туш) — те же
/// <c>EnumerateDirectories</c>, тот же <c>entities</c>, тот же
/// <c>AllDirectories</c>, тот же <c>try/catch</c> с тем же объяснением. Это
/// закон 2 в чистом виде: у правила «где лежат описания и как их читать» стало
/// два хозяина, и разойтись они могли только молча.
///
/// ЧТО ЭТО СТОИЛО ЖИВЬЁМ, помимо будущего расхождения: описаний под тысячу, и
/// каждый читатель обходил и разбирал их ЗАНОВО — второй полный проход по
/// диску ради того же самого набора файлов.
///
/// ПОЧЕМУ ЧТЕНИЕ ВООБЩЕ С ДИСКА, А НЕ ИЗ ПАКЕТА: серверную половину описания
/// (<c>server.behaviors</c>) сервер клиенту не шлёт вовсе —
/// <c>EntityTypeNet.EntityPropertiesToPacket</c> кладёт только клиентскую. И
/// скорости задач, и распад тела лежат именно в серверной.
/// </summary>
public static class EntityDescriptions
{
    /// <summary>
    /// Обойти все описания сущностей игры и отдать каждое разобранным.
    ///
    /// Возвращает ПРИЧИНУ, по которой не прочитано ничего, — или null, если
    /// обход состоялся. «Ни одного описания не подошло» отсюда не видно: что
    /// именно искали в описании, знает только звавший, он же и скажет об этом
    /// человеку своими словами (закон 4 — незнание называется, а не прячется).
    /// </summary>
    /// <param name="gameFolder">Папка игры; null — искать самим.</param>
    /// <param name="чегоНет">
    /// Чего не будет без описаний, родительным падежом: «объявленных скоростей
    /// тварей», «сроков распада туш». Идёт прямо в строку человеку.
    /// </param>
    /// <param name="каждое">Что делать с одним разобранным описанием.</param>
    public static string? ReadAll(string? gameFolder, string чегоНет, Action<JObject> каждое)
    {
        string? game = gameFolder ?? GameFolders.FindGame();
        if (game == null)
            return $"папку игры не нашёл — {чегоНет} нет";

        string assets = Path.Combine(game, "assets");
        if (!Directory.Exists(assets))
            return $"в {game} нет папки assets — {чегоНет} нет";

        foreach (string domainDir in Directory.EnumerateDirectories(assets))
        {
            string entities = Path.Combine(domainDir, "entities");
            if (!Directory.Exists(entities))
                continue;
            foreach (string file in Directory.EnumerateFiles(entities, "*.json",
                         SearchOption.AllDirectories))
            {
                try
                {
                    // Описания сущностей игра пишет вольным json (имена без
                    // кавычек, висячие запятые, комментарии) — и читает их сама
                    // тем же Newtonsoft, каким читаем мы
                    if (JToken.Parse(File.ReadAllText(file)) is JObject json)
                        каждое(json);
                }
                catch
                {
                    // Одно кривое описание не повод остаться вовсе ни с чем:
                    // остальные читаются, а о пустом итоге скажет звавший
                }
            }
        }
        return null;
    }

    /// <summary>Где игра держит описания сущностей — человеку в строку отказа.</summary>
    public static string Where(string? gameFolder) =>
        Path.Combine(gameFolder ?? GameFolders.FindGame() ?? "", "assets");
}
