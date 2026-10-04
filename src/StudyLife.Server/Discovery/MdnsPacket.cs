using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StudyLife.Server.Discovery;

/// <summary>A question read from an incoming mDNS query (name as dotted labels, no trailing dot).</summary>
public readonly record struct MdnsQuestion(string Name, ushort Type);

/// <summary>
/// The tiny slice of RFC 1035 / RFC 6762 / RFC 6763 wire format the announcer needs: encoding the
/// response for ONE service (PTR, SRV, TXT, A, AAAA) and reading the questions of incoming queries.
/// Pure functions over byte arrays, so it is unit-tested without sockets. Names are handled as
/// label arrays, so an instance name containing dots stays a single label.
/// </summary>
public static class MdnsPacket
{
    public const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeAaaa = 28, TypeSrv = 33, TypeAny = 255;
    private const ushort ClassIn = 1;
    private const ushort CacheFlush = 0x8000;
    /// <summary>RFC 6762 recommended TTLs: 120 s for records tied to a host address, 75 min otherwise.</summary>
    public const uint HostTtl = 120, OtherTtl = 4500;

    private static readonly string[] ServiceEnumeration = ["_services", "_dns-sd", "_udp", "local"];
    private static readonly string[] ServiceTypeLabels = ["_studylife", "_tcp", "local"];

    /// <summary>The DNS host name label used as SRV target (e.g. "studylife" for "StudyLife").</summary>
    public static string HostLabel(string instanceName)
    {
        var sb = new StringBuilder();
        foreach (var c in instanceName.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var label = sb.ToString().Trim('-');
        return label.Length == 0 ? "studylife" : label;
    }

    private static string[] HostName(MdnsServiceDescription s) => [HostLabel(s.InstanceName), "local"];

    private static string[] InstanceName(MdnsServiceDescription s) => [s.InstanceName, .. ServiceTypeLabels];

    /// <summary>
    /// Builds an unsolicited announcement / query response. <paramref name="ttlZero"/> turns it into
    /// the goodbye packet (every record with TTL 0, RFC 6762 section 10.1).
    /// </summary>
    public static byte[] BuildResponse(MdnsServiceDescription service, IEnumerable<IPAddress> addresses, bool ttlZero)
    {
        var host = HostName(service);
        var instance = InstanceName(service);
        var records = new List<byte[]>
        {
            Record(ServiceEnumeration, TypePtr, false, ttlZero ? 0 : OtherTtl, EncodeName(ServiceTypeLabels)),
            Record(ServiceTypeLabels, TypePtr, false, ttlZero ? 0 : OtherTtl, EncodeName(instance)),
        };

        // SRV rdata: priority 0, weight 0, port, target.
        var srv = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(srv.AsSpan(4), (ushort)service.Port);
        records.Add(Record(instance, TypeSrv, true, ttlZero ? 0 : HostTtl, [.. srv, .. EncodeName(host)]));

        var txt = new List<byte>();
        foreach (var entry in service.TxtRecords)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            if (bytes.Length > 255) throw new ArgumentException("TXT entry longer than 255 bytes: " + entry);
            txt.Add((byte)bytes.Length);
            txt.AddRange(bytes);
        }
        if (txt.Count == 0) txt.Add(0);
        records.Add(Record(instance, TypeTxt, true, ttlZero ? 0 : OtherTtl, txt.ToArray()));

        foreach (var address in addresses.Distinct())
        {
            if (address.AddressFamily == AddressFamily.InterNetwork)
                records.Add(Record(host, TypeA, true, ttlZero ? 0 : HostTtl, address.GetAddressBytes()));
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                records.Add(Record(host, TypeAaaa, true, ttlZero ? 0 : HostTtl, address.GetAddressBytes()));
        }

        var packet = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400); // response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)records.Count);
        packet.AddRange(header);
        foreach (var record in records) packet.AddRange(record);
        return packet.ToArray();
    }

    /// <summary>True for a well-formed query packet (QR bit clear); fills <paramref name="questions"/>.</summary>
    public static bool TryReadQuestions(ReadOnlySpan<byte> packet, out List<MdnsQuestion> questions)
    {
        questions = [];
        if (packet.Length < 12) return false;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        if ((flags & 0x8000) != 0) return false; // a response, not a query
        var count = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        var offset = 12;
        for (var i = 0; i < count; i++)
        {
            if (!TryReadName(packet, ref offset, out var name) || offset + 4 > packet.Length)
            {
                questions = [];
                return false;
            }
            var qtype = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
            offset += 4; // type + class (the unicast-response bit is ignored: we always multicast)
            questions.Add(new MdnsQuestion(name, qtype));
        }
        return true;
    }

    /// <summary>Does any question ask for something this service answers?</summary>
    public static bool Matches(MdnsServiceDescription service, IEnumerable<MdnsQuestion> questions)
    {
        var host = string.Join('.', HostName(service));
        var instance = string.Join('.', InstanceName(service));
        var type = string.Join('.', ServiceTypeLabels);
        var enumeration = string.Join('.', ServiceEnumeration);
        foreach (var q in questions)
        {
            bool Is(string n) => string.Equals(q.Name, n, StringComparison.OrdinalIgnoreCase);
            var any = q.Type == TypeAny;
            if ((Is(enumeration) || Is(type)) && (any || q.Type == TypePtr)) return true;
            if (Is(instance) && (any || q.Type is TypeSrv or TypeTxt)) return true;
            if (Is(host) && (any || q.Type is TypeA or TypeAaaa)) return true;
        }
        return false;
    }

    private static byte[] Record(string[] name, ushort type, bool cacheFlush, uint ttl, byte[] data)
    {
        var buffer = new List<byte>();
        buffer.AddRange(EncodeName(name));
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), (ushort)(ClassIn | (cacheFlush ? CacheFlush : 0)));
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);
        buffer.AddRange(fixedPart);
        buffer.AddRange(data);
        return buffer.ToArray();
    }

    /// <summary>Wire-encodes a name given as labels (no compression).</summary>
    public static byte[] EncodeName(IEnumerable<string> labels)
    {
        var bytes = new List<byte>();
        foreach (var label in labels)
        {
            var encoded = Encoding.UTF8.GetBytes(label);
            if (encoded.Length == 0 || encoded.Length > 63)
                throw new ArgumentException("DNS label must be 1-63 bytes: " + label);
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    private static bool TryReadName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        var sb = new StringBuilder();
        var position = offset;
        var jumped = false;
        var hops = 0;
        name = string.Empty;
        while (true)
        {
            if (position >= packet.Length) return false;
            var length = packet[position];
            if (length == 0)
            {
                if (!jumped) offset = position + 1;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length || ++hops > 16) return false;
                var pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped) offset = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }
            if ((length & 0xC0) != 0 || position + 1 + length > packet.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }
        name = sb.ToString();
        return true;
    }
}
