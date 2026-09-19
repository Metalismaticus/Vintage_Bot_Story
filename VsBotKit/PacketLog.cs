using System.Buffers.Binary;

namespace VsBotKit;

/// <summary>Одна запись: когда, куда и что.</summary>
/// <param name="Millis">миллисекунды от начала записи</param>
/// <param name="Incoming">true — от сервера, false — от бота</param>
/// <param name="PacketId">id пакета</param>
/// <param name="Body">тело пакета в том виде, в каком оно шло по сети</param>
public readonly record struct RecordedPacket(int Millis, bool Incoming, int PacketId, byte[] Body);

/// <summary>
/// Запись и воспроизведение прогонов.
///
/// ЗАЧЕМ. Все тяжёлые дефекты этого проекта были в поведении сервера, а не
/// в нашей логике: слот умения, разделители в id инвентарей, бросок кубика
/// у огнива, приседание для ножа. Юнит-тесты их не ловят по определению —
/// они проверяют наш код против наших же представлений. Ловит только живой
/// сервер, но каждая такая проверка стоит минут и не повторяется.
///
/// Запись прогона разрывает этот круг: один раз словили на сервере —
/// дальше воспроизводим сколько угодно, без сервера и мгновенно.
///
/// Формат простой и потоковый: заголовок «VSBOTREC1», дальше записи
/// [время 4б][направление 1б][длина 4б][тело]. Тела — НАСТОЯЩИЕ пакеты игры,
/// поэтому воспроизведение кормит модели ровно тем же, что шло по сети.
/// </summary>
public sealed class PacketLog : IDisposable
{
    private const string Magic = "VSBOTREC1";

    private readonly Stream? output;
    private readonly BinaryWriter? writer;
    private readonly DateTime started = DateTime.UtcNow;
    private readonly object gate = new();
    private BotClient? attached;

    /// <summary>Сколько записей сделано.</summary>
    public int Count { get; private set; }

    /// <summary>Писать входящие пакеты (по умолчанию да).</summary>
    public bool RecordIncoming { get; set; } = true;

    /// <summary>Писать исходящие пакеты (по умолчанию да).</summary>
    public bool RecordOutgoing { get; set; } = true;

    /// <summary>
    /// Не писать эти входящие id — иначе запись тонет в потоке позиций.
    /// Замерено живьём (командой --recsummary): полминуты прогона дают 52 МБ,
    /// из них 15 МБ — один только UdpPacket с позициями существ и ещё 5 МБ —
    /// их же атрибуты пачкой. Разбирать по ним нечего, а интересное
    /// (инвентари, ответы сервера на действия, чат) в этом тонет.
    ///
    /// Нужен весь поток целиком — очистите список: <c>log.SkipIncoming.Clear()</c>.
    /// </summary>
    public HashSet<int> SkipIncoming { get; } =
    [
        Packet_ServerIdEnum.Chunks,          // рельеф
        Packet_ServerIdEnum.MapChunk,
        Packet_ServerIdEnum.MapRegion,
        Packet_ServerIdEnum.UnloadServerChunk,
        Packet_ServerIdEnum.LevelDataChunk,
        Packet_ServerIdEnum.Entities,        // поток позиций существ
        Packet_ServerIdEnum.UdpPacket,       // он же по UDP — самый объёмный
        Packet_ServerIdEnum.EntityBulkAttributes,
        Packet_ServerIdEnum.Ping,
        Packet_ServerIdEnum.PlayerPing,
        Packet_ServerIdEnum.Sound
    ];

    /// <summary>Не писать эти исходящие id (35 — своя позиция, её десятки в секунду).</summary>
    public HashSet<int> SkipOutgoing { get; } = [35];

    /// <summary>
    /// Писать реестр игры (пакет 19). Он ОДИН, но весит 32 МБ — это всё
    /// содержимое игры разом, и без него воспроизведение не сможет перевести
    /// id в коды. Все остальные события прогона укладываются в полмегабайта.
    ///
    /// Выключайте, когда нужен лёгкий след событий и реестр уже есть в другой
    /// записи: <c>log.RecordAssets = false</c>.
    /// </summary>
    public bool RecordAssets { get; set; } = true;

    private PacketLog(Stream? output)
    {
        this.output = output;
        if (output == null)
            return;
        writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes(Magic));
    }

    /// <summary>Начать запись в файл.</summary>
    public static PacketLog RecordTo(string path)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new PacketLog(File.Create(path));
    }

    /// <summary>Подключиться к боту и писать всё, что идёт по сети.</summary>
    public PacketLog Attach(BotClient client)
    {
        attached = client;
        client.OnPacket += WriteIncoming;
        client.OnPacketSent += WriteOutgoing;
        return this;
    }

    private void WriteIncoming(Packet_Server packet)
    {
        if (!RecordIncoming || SkipIncoming.Contains(packet.Id))
            return;
        if (!RecordAssets && packet.Id == Packet_ServerIdEnum.ServerAssets)
            return;
        Write(true, packet.Id, Packet_ServerSerializer.SerializeToBytes(packet));
    }

    private void WriteOutgoing(Packet_Client packet)
    {
        if (!RecordOutgoing || SkipOutgoing.Contains(packet.Id))
            return;
        Write(false, packet.Id, Packet_ClientSerializer.SerializeToBytes(packet));
    }

    private void Write(bool incoming, int id, byte[] body)
    {
        if (writer == null)
            return;
        lock (gate)
        {
            writer.Write((int)(DateTime.UtcNow - started).TotalMilliseconds);
            writer.Write(incoming ? (byte)1 : (byte)0);
            writer.Write(id);
            writer.Write(body.Length);
            writer.Write(body);
            Count++;
        }
    }

    /// <summary>Прочитать запись с диска.</summary>
    public static IReadOnlyList<RecordedPacket> Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        var magic = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length));
        if (magic != Magic)
            throw new FormatException($"это не запись прогона: заголовок «{magic}»");

        var result = new List<RecordedPacket>();
        while (stream.Position < stream.Length)
        {
            int millis = reader.ReadInt32();
            bool incoming = reader.ReadByte() == 1;
            int id = reader.ReadInt32();
            int length = reader.ReadInt32();
            result.Add(new RecordedPacket(millis, incoming, id, reader.ReadBytes(length)));
        }
        return result;
    }

    /// <summary>
    /// Воспроизвести запись НА БОТА: входящие пакеты уходят его подсистемам
    /// ровно тем же путём, каким их отдаёт сеть. Исходящие в мир не идут —
    /// они здесь только как след того, что бот делал.
    ///
    /// Возвращает, сколько входящих пакетов скормлено.
    /// </summary>
    public static int Replay(BotClient client, IEnumerable<RecordedPacket> packets)
    {
        int fed = 0;
        foreach (var record in packets)
        {
            if (!record.Incoming)
                continue;
            var packet = new Packet_Server();
            Packet_ServerSerializer.DeserializeBuffer(record.Body, record.Body.Length, packet);
            client.Feed(packet);
            fed++;
        }
        return fed;
    }

    /// <summary>Что в записи: сколько чего по id (для быстрого взгляда).</summary>
    public static IEnumerable<(int PacketId, bool Incoming, int Count)> Summary(
        IEnumerable<RecordedPacket> packets) =>
        packets.GroupBy(p => (p.PacketId, p.Incoming))
            .Select(g => (g.Key.PacketId, g.Key.Incoming, g.Count()))
            .OrderByDescending(t => t.Item3);

    public void Dispose()
    {
        if (attached != null)
        {
            attached.OnPacket -= WriteIncoming;
            attached.OnPacketSent -= WriteOutgoing;
            attached = null;
        }
        lock (gate)
        {
            writer?.Flush();
            writer?.Dispose();
            output?.Dispose();
        }
    }
}
