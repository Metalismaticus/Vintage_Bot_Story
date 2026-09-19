using Vintagestory.API.Common;
using Vintagestory.Common;

namespace BotGamePhysics;

/// <summary>Пустое поведение блока: нужно только чтобы разбор реестра не падал.</summary>
public class StubBlockBehavior : BlockBehavior
{
    public StubBlockBehavior(Block block) : base(block) { }
}

/// <summary>Пустое поведение предмета — по той же причине.</summary>
public class StubCollectibleBehavior : CollectibleBehavior
{
    public StubCollectibleBehavior(CollectibleObject obj) : base(obj) { }
}

/// <summary>
/// Сервер присылает блоки с поведениями из своих модов («Reinforcable»
/// и прочие), а голый реестр игры знает только ванильные классы и падает.
/// Физике поведения не нужны — она смотрит на коллизии, жидкость и скорость,
/// поэтому все незнакомые имена подменяются пустышкой.
/// </summary>
public static class StubBehaviors
{
    public static void RegisterAll(ClassRegistry registry, Packet_BlockType[] packets, int count)
    {
        // Сначала сами классы блоков: сервер шлёт и модовые (BlockSpawner
        // и прочие). Физике важна геометрия, а не класс — подменяем базовым
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            if (packets[i]?.Blockclass is not { } cls || !seenClasses.Add(cls))
                continue;
            try
            {
                registry.CreateBlock(cls);
            }
            catch
            {
                registry.RegisterBlockClass(cls, typeof(Block));
            }
        }

        // Игра ищет имя поведения сначала среди блочных, потом среди
        // предметных — незнакомое нигде и роняет разбор
        var known = new HashSet<string>(StringComparer.Ordinal);
        var probe = new Block();

        for (int i = 0; i < count; i++)
        {
            var bt = packets[i];
            if (bt?.Behaviors == null)
                continue;
            for (int b = 0; b < bt.BehaviorsCount; b++)
            {
                if (bt.Behaviors[b]?.Code is not { } code || !known.Add(code))
                    continue;
                if (!CanCreate(registry, probe, code))
                    registry.RegisterCollectibleBehaviorClass(code, typeof(StubCollectibleBehavior));
            }
        }
    }

    /// <summary>Умеет ли реестр создавать такое поведение сам.</summary>
    private static bool CanCreate(ClassRegistry registry, Block probe, string code)
    {
        try
        {
            registry.CreateBlockBehavior(probe, code);
            return true;
        }
        catch { /* не блочное — пробуем предметное */ }

        try
        {
            registry.CreateCollectibleBehavior(probe, code);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
