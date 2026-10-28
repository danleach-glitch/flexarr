using System.Buffers.Binary;
using System.Net;
using System.Text;
using Flexarr.Api.Security;

namespace Flexarr.Api.Tests;

/// <summary>Flexarr's own mDNS answers (Docker, Linux without Avahi, Windows): what an Apple TV browsing for it asks.</summary>
public sealed class MdnsTests
{
    private static readonly MdnsService Service = new("Flexarr on mini", "_flexarr._tcp", "mini", 8383, "http://mini.local:8383");
    private static readonly IPAddress[] Lan = [IPAddress.Parse("192.168.1.20")];

    private const ushort A = 1, Ptr = 12, Txt = 16, Srv = 33, Any = 255;

    [Fact]
    public void ABrowse_GetsTheService_WithItsAddressAndUrl_AlongsideIt()
    {
        var answer = MdnsMessages.Answer(Query(("_flexarr._tcp.local", Ptr)), Service, Lan);

        var response = Parse(answer!);
        Assert.True(response.IsResponse);
        Assert.Equal(0, response.Id);
        var ptr = Assert.Single(response.Answers);
        Assert.Equal(("_flexarr._tcp.local", Ptr), (ptr.Name, ptr.Type));
        Assert.Equal("Flexarr on mini._flexarr._tcp.local", ptr.Target);

        // Everything else needed to connect comes with it, so there's no second round trip.
        var srv = Assert.Single(response.Additional, r => r.Type == Srv);
        Assert.Equal(("Flexarr on mini._flexarr._tcp.local", 8383, "mini.local"), (srv.Name, srv.Port, srv.Target));
        Assert.Equal("url=http://mini.local:8383", Assert.Single(response.Additional, r => r.Type == Txt).Text);
        var a = Assert.Single(response.Additional, r => r.Type == A);
        Assert.Equal(("mini.local", "192.168.1.20"), (a.Name, a.Address));
    }

    [Fact]
    public void TheHostName_ResolvesToThisInterfacesAddresses()
    {
        var answer = MdnsMessages.Answer(Query(("MINI.local", A)), Service, [IPAddress.Parse("10.0.0.5"), IPAddress.Parse("fe80::1")]);

        // Names are matched without regard to case; only IPv4 addresses go in A records.
        var a = Assert.Single(Parse(answer!).Answers);
        Assert.Equal("10.0.0.5", a.Address);
    }

    [Fact]
    public void TheInstance_AnswersSrvAndTxt_ForAny()
    {
        var answer = MdnsMessages.Answer(Query(("Flexarr on mini._flexarr._tcp.local", Any)), Service, Lan);

        var response = Parse(answer!);
        Assert.Equal([Txt, Srv], response.Answers.Select(r => r.Type).Order());
        Assert.Equal(A, Assert.Single(response.Additional).Type);
    }

    [Fact]
    public void ServiceTypeEnumeration_ListsFlexarr()
    {
        var answer = MdnsMessages.Answer(Query(("_services._dns-sd._udp.local", Ptr)), Service, Lan);

        Assert.Equal("_flexarr._tcp.local", Assert.Single(Parse(answer!).Answers).Target);
    }

    [Fact]
    public void OtherQuestions_AndResponses_GetNothing()
    {
        Assert.Null(MdnsMessages.Answer(Query(("_airplay._tcp.local", Ptr)), Service, Lan));
        Assert.Null(MdnsMessages.Answer(Query(("mini.local", Txt)), Service, Lan));

        // Another responder's answer, even about our name, isn't a question.
        var response = Query(("_flexarr._tcp.local", Ptr));
        response[2] = 0x84;
        Assert.Null(MdnsMessages.Answer(response, Service, Lan));
    }

    [Fact]
    public void ALegacyResolver_GetsItsIdAndQuestionBack_AndShortLivedAnswers()
    {
        var query = Query(("mini.local", A));
        BinaryPrimitives.WriteUInt16BigEndian(query, 0x1234);

        var response = Parse(MdnsMessages.Answer(query, Service, Lan, legacyUnicast: true)!);

        Assert.Equal(0x1234, response.Id);
        Assert.Equal(1, response.QuestionCount);
        var a = Assert.Single(response.Answers);
        Assert.True(a.Ttl <= 10);
        Assert.Equal(1, a.Class); // no cache-flush bit
    }

    [Fact]
    public void CompressedQuestionNames_AreRead()
    {
        // Two questions, the second naming "_flexarr._tcp.local" by pointing back into the first ("x._flexarr...").
        var first = Name("x._flexarr._tcp.local");
        var message = new List<byte> { 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0 };
        message.AddRange(first);
        message.AddRange([0, (byte)A, 0, 1]);
        message.AddRange([0xC0, 12 + 2]); // past the one-letter "x" label
        message.AddRange([0, (byte)Ptr, 0, 1]);

        var questions = MdnsMessages.Questions([.. message]);

        Assert.Equal(["x._flexarr._tcp.local", "_flexarr._tcp.local"], questions.Select(q => q.Name));
        Assert.NotNull(MdnsMessages.Answer([.. message], Service, Lan));
    }

    [Fact]
    public void BrokenMessages_AreIgnored()
    {
        Assert.Empty(MdnsMessages.Questions([0, 0, 0]));
        // A name running off the end, and a pointer pointing at itself.
        Assert.Empty(MdnsMessages.Questions([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 20, (byte)'a']));
        Assert.Empty(MdnsMessages.Questions([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1]));
    }

    [Fact]
    public void TheAnnouncement_HasEverything_AndTheGoodbye_EndsIt()
    {
        var announcement = Parse(MdnsMessages.Announcement(Service, Lan));
        Assert.Equal([A, Ptr, Txt, Srv], announcement.Answers.Select(r => r.Type).Order());
        Assert.All(announcement.Answers, r => Assert.True(r.Ttl > 0));

        var goodbye = Assert.Single(Parse(MdnsMessages.Goodbye(Service)).Answers);
        Assert.Equal((Ptr, 0u), (goodbye.Type, goodbye.Ttl));
    }

    [Fact]
    public void AnInstanceNameWithDots_StaysOneLabel()
    {
        var service = Service with { InstanceName = "Flexarr on mini.home" };

        var ptr = Assert.Single(Parse(MdnsMessages.Answer(Query(("_flexarr._tcp.local", Ptr)), service, Lan)!).Answers);

        Assert.Equal("Flexarr on mini.home", ptr.TargetLabels[0]);
    }

    private static byte[] Query(params (string Name, ushort Type)[] questions)
    {
        var message = new List<byte> { 0, 0, 0, 0, 0, (byte)questions.Length, 0, 0, 0, 0, 0, 0 };
        foreach (var (name, type) in questions)
        {
            message.AddRange(Name(name));
            message.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        }

        return [.. message];
    }

    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.UTF8.GetBytes(label));
        }

        bytes.Add(0);
        return [.. bytes];
    }

    private sealed record ParsedRecord(string Name, ushort Type, ushort Class, uint Ttl, string? Target, string[] TargetLabels, int Port, string? Text, string? Address);

    private sealed record Response(ushort Id, bool IsResponse, int QuestionCount, List<ParsedRecord> Answers, List<ParsedRecord> Additional);

    /// <summary>Reads a response the way a browsing device would.</summary>
    private static Response Parse(byte[] message)
    {
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var additional = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10));
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            ReadLabels(message, ref offset);
            offset += 4;
        }

        var records = new List<ParsedRecord>();
        for (var i = 0; i < answers + additional; i++)
        {
            var name = string.Join('.', ReadLabels(message, ref offset));
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            var @class = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 2)) & 0x7FFF);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message.AsSpan(offset + 4));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset + 8));
            var data = offset + 10;
            offset = data + length;
            records.Add(type switch
            {
                Ptr => Target(name, type, @class, ttl, message, data, 0),
                Srv => Target(name, type, @class, ttl, message, data + 6) with { Port = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(data + 4)) },
                Txt => new ParsedRecord(name, type, @class, ttl, null, [], 0, Encoding.UTF8.GetString(message, data + 1, message[data]), null),
                A => new ParsedRecord(name, type, @class, ttl, null, [], 0, null, new IPAddress(message.AsSpan(data, 4)).ToString()),
                _ => new ParsedRecord(name, type, @class, ttl, null, [], 0, null, null),
            });
        }

        return new Response(BinaryPrimitives.ReadUInt16BigEndian(message), (message[2] & 0x80) != 0, questions, records[..answers], records[answers..]);
    }

    private static ParsedRecord Target(string name, ushort type, ushort @class, uint ttl, byte[] message, int at, int port = 0)
    {
        var labels = ReadLabels(message, ref at);
        return new ParsedRecord(name, type, @class, ttl, string.Join('.', labels), labels, port, null, null);
    }

    private static string[] ReadLabels(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        while (message[offset] != 0)
        {
            labels.Add(Encoding.UTF8.GetString(message, offset + 1, message[offset]));
            offset += 1 + message[offset];
        }

        offset++;
        return [.. labels];
    }
}
