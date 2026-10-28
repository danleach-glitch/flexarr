using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Flexarr.Api.Security;

/// <summary>
/// What Flexarr announces: "Flexarr on mini" as a <c>_flexarr._tcp</c> service, its port, the address apps should use
/// (the TXT record's <c>url=</c>), and the host name that address names.
/// </summary>
public sealed record MdnsService(string InstanceName, string ServiceType, string HostName, int Port, string Url)
{
    public string ServiceName => $"{ServiceType}.local";
    public string InstanceFullName => $"{InstanceName}.{ServiceName}";
    public string HostFullName => $"{HostName}.local";
}

/// <summary>
/// The mDNS messages Flexarr answers with when it announces itself (RFC 6762 and 6763): only what an app browsing for
/// <c>_flexarr._tcp</c> asks about. Names are written whole, without compression, which every reader accepts.
/// </summary>
public static class MdnsMessages
{
    public const int Port = 5353;
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33, TypeAny = 255;
    private const ushort ClassIn = 1;
    /// <summary>On a record: this is all there is, so older copies can go. Not on shared records (PTR).</summary>
    private const ushort CacheFlush = 0x8000;
    private const string ServicesName = "_services._dns-sd._udp.local";

    /// <summary>How long others keep the records: RFC 6762's suggestions, 75 minutes, or 2 for those naming a host.</summary>
    private const uint LongTtl = 4500, HostTtl = 120;

    /// <summary>A question from a query: the name asked about and the record type.</summary>
    public readonly record struct Question(string Name, ushort Type);

    /// <summary>
    /// The questions in <paramref name="message"/>, if it's a query; empty for a response, or anything that doesn't read
    /// as DNS.
    /// </summary>
    public static IReadOnlyList<Question> Questions(ReadOnlySpan<byte> message)
    {
        if (message.Length < 12 || (message[2] & 0x80) != 0)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var questions = new List<Question>(Math.Min((int)count, 16));
        var offset = 12;
        for (var i = 0; i < count; i++)
        {
            if (ReadName(message, ref offset) is not { } name || offset + 4 > message.Length)
            {
                return [];
            }

            questions.Add(new Question(name, BinaryPrimitives.ReadUInt16BigEndian(message[offset..])));
            offset += 4;
        }

        return questions;
    }

    /// <summary>
    /// The answer to <paramref name="query"/> for this service on an interface with <paramref name="addresses"/>, or null
    /// when it asks about nothing of ours. A query from a port other than 5353 (a simple resolver, "legacy unicast") is
    /// answered directly with its id and questions, as RFC 6762 6.7 asks.
    /// </summary>
    public static byte[]? Answer(ReadOnlySpan<byte> query, MdnsService service, IReadOnlyList<IPAddress> addresses, bool legacyUnicast = false)
    {
        var questions = Questions(query);
        var answers = new List<Record>();
        var additional = new List<Record>();
        foreach (var (name, type) in questions)
        {
            var any = type == TypeAny;
            if (Same(name, service.ServiceName) && (any || type == TypePtr))
            {
                answers.Add(Ptr(service));
                additional.AddRange([Srv(service), Txt(service), .. A(service, addresses)]);
            }
            else if (Same(name, service.InstanceFullName))
            {
                if (any || type == TypeSrv) answers.Add(Srv(service));
                if (any || type == TypeTxt) answers.Add(Txt(service));
                if (any || type == TypeSrv) additional.AddRange(A(service, addresses));
            }
            else if (Same(name, service.HostFullName) && (any || type == TypeA))
            {
                answers.AddRange(A(service, addresses));
            }
            else if (Same(name, ServicesName) && (any || type == TypePtr))
            {
                answers.Add(new Record(ServicesName, TypePtr, ClassIn, LongTtl, Name(service.ServiceName)));
            }
        }

        if (answers.Count == 0)
        {
            return null;
        }

        additional.RemoveAll(answers.Contains);
        var id = legacyUnicast ? BinaryPrimitives.ReadUInt16BigEndian(query) : (ushort)0;
        return Write(id, legacyUnicast ? questions : [], answers.Distinct().ToList(), additional.Distinct().ToList(), legacyUnicast);
    }

    /// <summary>Everything, unasked: sent when Flexarr starts, so apps already looking see it straight away.</summary>
    public static byte[] Announcement(MdnsService service, IReadOnlyList<IPAddress> addresses) =>
        Write(0, [], [Ptr(service), Srv(service), Txt(service), .. A(service, addresses)], [], false);

    /// <summary>"Gone": the service with a time to live of zero, sent when Flexarr stops.</summary>
    public static byte[] Goodbye(MdnsService service) => Write(0, [], [Ptr(service) with { Ttl = 0 }], [], false);

    private sealed record Record(string Name, ushort Type, ushort Class, uint Ttl, byte[] Data)
    {
        public bool Equals(Record? other) =>
            other is not null && Same(Name, other.Name) && Type == other.Type && Data.AsSpan().SequenceEqual(other.Data);

        public override int GetHashCode() => HashCode.Combine(Name.ToUpperInvariant(), Type, Data.Length);
    }

    private static Record Ptr(MdnsService s) => new(s.ServiceName, TypePtr, ClassIn, LongTtl, Name(s.InstanceFullName));

    private static Record Srv(MdnsService s)
    {
        var target = Name(s.HostFullName);
        var data = new byte[6 + target.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), (ushort)s.Port);
        target.CopyTo(data, 6);
        return new Record(s.InstanceFullName, TypeSrv, ClassIn | CacheFlush, HostTtl, data);
    }

    private static Record Txt(MdnsService s)
    {
        var entry = Encoding.UTF8.GetBytes($"url={s.Url}");
        return new Record(s.InstanceFullName, TypeTxt, ClassIn | CacheFlush, LongTtl, [(byte)Math.Min(entry.Length, 255), .. entry.AsSpan(0, Math.Min(entry.Length, 255))]);
    }

    private static IEnumerable<Record> A(MdnsService s, IReadOnlyList<IPAddress> addresses) =>
        addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => new Record(s.HostFullName, TypeA, ClassIn | CacheFlush, HostTtl, a.GetAddressBytes()));

    private static byte[] Write(ushort id, IReadOnlyList<Question> questions, IReadOnlyList<Record> answers, IReadOnlyList<Record> additional, bool legacyUnicast)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x84; // a response, and the authority for these names
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(header[10..], (ushort)additional.Count);
        stream.Write(header);
        Span<byte> tail = stackalloc byte[4];
        Span<byte> fixedPart = stackalloc byte[10];
        foreach (var question in questions)
        {
            stream.Write(Name(question.Name));
            BinaryPrimitives.WriteUInt16BigEndian(tail, question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], ClassIn);
            stream.Write(tail);
        }

        foreach (var record in answers.Concat(additional))
        {
            stream.Write(Name(record.Name));
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, record.Type);
            // A simple resolver doesn't know the cache-flush bit, and keeps answers no longer than 10 seconds (6.7).
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[2..], legacyUnicast ? ClassIn : record.Class);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart[4..], legacyUnicast ? Math.Min(record.Ttl, 10) : record.Ttl);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart[8..], (ushort)record.Data.Length);
            stream.Write(fixedPart);
            stream.Write(record.Data);
        }

        return stream.ToArray();
    }

    /// <summary>A name in DNS form: each label after its length, then a zero. Labels longer than 63 bytes are cut.</summary>
    private static byte[] Name(string name)
    {
        var bytes = new List<byte>(name.Length + 2);
        foreach (var label in SplitLabels(name))
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            var length = Math.Min(encoded.Length, 63);
            bytes.Add((byte)length);
            bytes.AddRange(encoded.AsSpan(0, length));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    /// <summary>
    /// The labels of a name. The instance name is one label even if it has dots in it ("Flexarr on mini.home"), so only
    /// the known service and domain labels after it are split off.
    /// </summary>
    private static string[] SplitLabels(string name)
    {
        var at = name.IndexOf("._", StringComparison.Ordinal);
        if (at > 0)
        {
            return [name[..at], .. name[(at + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries)];
        }

        return name.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A name read from a message, following compression pointers; null if it runs off the end or loops.</summary>
    private static string? ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            if (position >= message.Length)
            {
                return null;
            }

            var length = message[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= message.Length)
                {
                    return null;
                }

                if (!jumped) offset = position + 2;
                jumped = true;
                position = ((length & 0x3F) << 8) | message[position + 1];
                continue;
            }

            if (position + 1 + length > message.Length)
            {
                return null;
            }

            labels.Add(Encoding.UTF8.GetString(message.Slice(position + 1, length)));
            position += 1 + length;
        }

        return null;
    }

    private static bool Same(string a, string b) => string.Equals(a.TrimEnd('.'), b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Announces Flexarr over mDNS itself, for when the system has no tool for it: in Docker (with host networking, as
/// multicast doesn't cross Docker's own network), on Linux without Avahi, and on Windows. Shares port 5353 with any
/// other responder on the machine. Each query is answered on the interface it came in on, with that interface's
/// addresses, so a tailnet or Docker bridge address never reaches the TV.
/// </summary>
public sealed partial class MdnsResponder(MdnsService service, ILogger logger) : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly HashSet<int> _joined = [];
    private readonly Lock _sendLock = new();
    private Dictionary<int, IPAddress[]> _interfaces = [];

    public async Task RunAsync(CancellationToken ct)
    {
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        _socket.Bind(new IPEndPoint(IPAddress.Any, MdnsMessages.Port));

        Refresh();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        try
        {
            // Twice, a second apart, as RFC 6762 8.3 asks: a single packet can be lost.
            Announce();
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Announce();

            var buffer = new byte[9000];
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            while (!ct.IsCancellationRequested)
            {
                var received = await _socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, from, ct);
                if (received.RemoteEndPoint is not IPEndPoint sender || !_interfaces.TryGetValue(received.PacketInformation.Interface, out var addresses))
                {
                    continue;
                }

                var legacy = sender.Port != MdnsMessages.Port;
                if (MdnsMessages.Answer(buffer.AsSpan(0, received.ReceivedBytes), service, addresses, legacy) is { } answer)
                {
                    Send(answer, received.PacketInformation.Interface, legacy ? sender : null);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            foreach (var index in _interfaces.Keys)
            {
                Send(MdnsMessages.Goodbye(service), index, null);
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        }
    }

    public void Dispose() => _socket.Dispose();

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        Refresh();
        Announce();
    }

    private void Announce()
    {
        foreach (var (index, addresses) in _interfaces)
        {
            Send(MdnsMessages.Announcement(service, addresses), index, null);
        }
    }

    /// <summary>The interfaces to answer on: up, able to multicast, with a private IPv4 address (not a tailnet's).</summary>
    private void Refresh()
    {
        var found = new Dictionary<int, IPAddress[]>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            var addresses = properties.UnicastAddresses.Select(u => u.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && NetworkZones.Classify(a) == NetworkZone.Local && !IsLinkLocal(a)).ToArray();
            if (addresses.Length == 0 || properties.GetIPv4Properties() is not { } v4)
            {
                continue;
            }

            found[v4.Index] = addresses;
            if (_joined.Add(v4.Index))
            {
                try
                {
                    _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsMessages.Group, v4.Index));
                }
                catch (SocketException ex)
                {
                    LogJoinFailed(nic.Name, ex.Message);
                }
            }
        }

        _interfaces = found;
    }

    private void Send(byte[] message, int interfaceIndex, IPEndPoint? unicastTo)
    {
        lock (_sendLock)
        {
            try
            {
                if (unicastTo is not null)
                {
                    _socket.SendTo(message, unicastTo);
                    return;
                }

                _socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
                _socket.SendTo(message, new IPEndPoint(MdnsMessages.Group, MdnsMessages.Port));
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                LogSendFailed(ex.Message);
            }
        }
    }

    private static bool IsLinkLocal(IPAddress address) => address.GetAddressBytes() is [169, 254, ..];

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't listen for mDNS on {Interface}: {Reason}")]
    private partial void LogJoinFailed(string @interface, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't send an mDNS message: {Reason}")]
    private partial void LogSendFailed(string reason);
}
