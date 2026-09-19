namespace VsBotKit;

/// <summary>
/// Читы — то, чего живой игрок не может.
///
/// Правило 2 проекта («бот неотличим от живого игрока») остаётся в силе: всё
/// выключено по умолчанию, и честный путь — это тот код, который выполняется
/// при выключенных читах. Но иногда нечестный бот нужен: отладить механику,
/// не бегая полкарты; проверить мод; собрать данные о мире; сделать
/// служебного бота для своего сервера. Чтобы это не превращалось в
/// расковыривание библиотеки, все нарушения собраны ЗДЕСЬ.
///
/// Три требования к каждому читу:
/// 1. **Отдельный именованный переключатель.** Никаких «режимов вообще».
/// 2. **Выключен по умолчанию** и не включается ничем, кроме явного присвоения.
/// 3. **Слышен в журнале.** Включённый чит пишет о себе, потому что иначе
///    результат проверки нельзя отличить от честного — а это хуже, чем чит.
///
/// Читер в две строки:
/// <code>
/// bot.Cheats.Enabled = true;
/// bot.Cheats.InstantMining = bot.Cheats.InfiniteReach = true;
/// </code>
/// </summary>
public sealed class Cheats
{
    /// <summary>
    /// Общий рубильник. Пока он выключен, ни один чит ниже не действует —
    /// даже если его включили. Это защита от «случайно осталось со вчера».
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Дальность руки, когда включён <see cref="InfiniteReach"/>.</summary>
    public double ReachDistance { get; set; } = 64;

    /// <summary>Тянуться дальше PickingRange игры (4.5 блока).</summary>
    public bool InfiniteReach { get; set; }

    /// <summary>Кликать сквозь стены: не требовать прямой видимости.</summary>
    public bool NoLineOfSight { get; set; }

    /// <summary>Ломать блоки мгновенно, не держа кнопку.</summary>
    public bool InstantMining { get; set; }

    /// <summary>Ломать любой блок независимо от тира инструмента.</summary>
    public bool IgnoreToolTier { get; set; }

    /// <summary>Разрешить телепортацию (сама по себе она не происходит).</summary>
    public bool Teleport { get; set; }

    /// <summary>
    /// ЧИТАТЬ СОДЕРЖИМОЕ ЗАКРЫТЫХ КОНТЕЙНЕРОВ, не открывая их.
    ///
    /// ЗДЕСЬ ДОЛГО СТОЯЛА НЕПРАВДА: «флаг ничего не делает, сервер присылает
    /// содержимое только после открытия, подглядывать физически не во что». По
    /// собственному коду выходило ровно наоборот — сервер шлёт инвентарь
    /// сундука ВМЕСТЕ С ЧАНКОМ (WorldModel.GetContainerContents), и бот
    /// пересчитывал чужой закрытый сундук за глухой стеной, ни разу к нему не
    /// подойдя: «туда не иду», «мест там 15». Прогон, объявленный честным,
    /// честным не был.
    ///
    /// Теперь флаг ДЕЙСТВУЕТ: без него содержимое видно только у контейнера,
    /// который бот открыл сам и недавно (VsBot.RememberInsideSeconds), — а с
    /// ним доступен любой, и об этом говорится вслух, как о всяком чите.
    ///
    /// Помнить, что лежало в открытом РАНЕЕ сундуке, — не чит: живой игрок
    /// помнит так же. Именно поэтому есть ForgetInventory: перед новым
    /// открытием память сбрасывается, чтобы старое не выдавалось за свежее.
    /// </summary>
    public bool SeeInsideContainers { get; set; }

    /// <summary>
    /// ВИДЕТЬ СКВОЗЬ КАМЕНЬ. Сервер присылает чанк целиком, поэтому бот знает
    /// каждый блок внутри породы — в том числе руду, которую игрок не увидит
    /// никак, пока не вскроет стену.
    ///
    /// Пока это выключено, поиск блоков видит только то, у чего есть открытая
    /// грань, — как человек: по выходам в пещере, на обрыве, в стене шахты.
    /// Это самый крупный чит, который у нас был включён молча.
    /// </summary>
    public bool SeeThroughWalls { get; set; }

    /// <summary>Не терять здоровье от падений в планировщике пути.</summary>
    public bool NoFallDamage { get; set; }

    /// <summary>О включённом чите сообщают сюда (подключает VsBot к общему журналу).</summary>
    public event Action<string>? OnCheatUsed;

    private readonly HashSet<string> announced = new(StringComparer.Ordinal);

    /// <summary>
    /// Действует ли чит прямо сейчас. Первое срабатывание каждого чита громко
    /// объявляется: молчаливый чит превращает любой прогон в недостоверный.
    ///
    /// Имя чита передаётся явно (<c>nameof</c>), а не берётся от места вызова:
    /// иначе в журнал попадает имя метода, который спросил, — и по нему не
    /// понять, ЧТО именно нечестно. Тест это и поймал.
    /// </summary>
    public bool On(bool flag, string name)
    {
        if (!Enabled || !flag)
            return false;
        if (announced.Add(name))
            OnCheatUsed?.Invoke($"ЧИТ ВКЛЮЧЁН: {name} — этот прогон нечестный");
        return true;
    }

    /// <summary>Читы, включённые прямо сейчас (для отчётов и диагностики).</summary>
    public IEnumerable<string> Active()
    {
        if (!Enabled)
            yield break;
        if (InfiniteReach) yield return nameof(InfiniteReach);
        if (NoLineOfSight) yield return nameof(NoLineOfSight);
        if (InstantMining) yield return nameof(InstantMining);
        if (IgnoreToolTier) yield return nameof(IgnoreToolTier);
        if (Teleport) yield return nameof(Teleport);
        if (SeeInsideContainers) yield return nameof(SeeInsideContainers);
        if (SeeThroughWalls) yield return nameof(SeeThroughWalls);
        if (NoFallDamage) yield return nameof(NoFallDamage);
    }

    /// <summary>Всё разом — для служебного бота, которому честность не нужна.</summary>
    public Cheats All()
    {
        Enabled = true;
        InfiniteReach = NoLineOfSight = InstantMining = IgnoreToolTier =
            Teleport = SeeInsideContainers = SeeThroughWalls = NoFallDamage = true;
        return this;
    }

    public override string ToString() =>
        !Enabled ? "читы выключены" : Active().Any()
            ? "читы: " + string.Join(", ", Active())
            : "читы разрешены, но ни один не включён";
}
