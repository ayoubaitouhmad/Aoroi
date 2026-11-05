using System.Text;

namespace ParentalGuard.Service.Dns;

/// <summary>
/// STEP 2 — a minimal reader for the one part of a DNS query we care about: the domain name
/// being asked about (and its record type).
///
/// === The DNS wire format, just enough to read a question ===
///
/// A DNS message is a compact binary blob laid out as:
///
///   [ 12-byte HEADER ][ QUESTION(s) ][ ANSWER(s) ][ AUTHORITY ][ ADDITIONAL ]
///
/// The HEADER is always 12 bytes. The fields are 16-bit, big-endian (most significant byte
/// first):
///   bytes 0-1  : ID         (matches a reply to its request)
///   bytes 2-3  : flags      (query/response, opcode, response code, ...)
///   bytes 4-5  : QDCOUNT     (how many questions follow — normally 1)
///   bytes 6-7  : ANCOUNT     (answer count)
///   bytes 8-9  : NSCOUNT
///   bytes 10-11: ARCOUNT
///
/// Right after the header comes the QUESTION. A question is:
///   QNAME  : the domain name, encoded as length-prefixed labels (see below)
///   QTYPE  : 2 bytes — what kind of record (A=1, AAAA=28, ...)
///   QCLASS : 2 bytes — almost always IN (1), the Internet class
///
/// QNAME encoding: the name "google.com" is NOT stored as text with dots. It's stored as a
/// series of labels, each prefixed by its length, ending with a zero byte:
///
///   6 'g''o''o''g''l''e'   3 'c''o''m'   0
///   └length┘              └length┘      └ end of name
///
/// So we read a length byte, then that many characters, repeat, until we hit a 0. Join the
/// labels with '.' and we have "google.com".
///
/// (In replies, names can use "compression pointers" — a byte with its top two bits set that
/// points elsewhere in the packet. The QNAME in a *query's question* is never compressed, so
/// we don't need to follow pointers here; if we ever see one, we bail out safely.)
/// </summary>
public static class DnsMessage
{
    private const int HeaderLength = 12;

    /// <summary>One parsed question: the domain name, the record type, and the class.</summary>
    public sealed record Question(string Name, ushort Type, ushort Class);

    /// <summary>
    /// Try to read the first question from a raw DNS query packet. Returns false if the packet
    /// is too short, has no questions, or the name can't be read.
    /// </summary>
    public static bool TryReadFirstQuestion(byte[] packet, out Question? question)
    {
        question = null;

        if (packet.Length < HeaderLength + 1)
            return false;

        // QDCOUNT lives at bytes 4-5, big-endian. Need at least one question.
        int questionCount = (packet[4] << 8) | packet[5];
        if (questionCount < 1)
            return false;

        int offset = HeaderLength;

        if (!TryReadName(packet, ref offset, out string name))
            return false;

        // After the name: QTYPE (2 bytes) then QCLASS (2 bytes).
        if (offset + 4 > packet.Length)
            return false;

        ushort qtype = (ushort)((packet[offset] << 8) | packet[offset + 1]);
        ushort qclass = (ushort)((packet[offset + 2] << 8) | packet[offset + 3]);

        question = new Question(name, qtype, qclass);
        return true;
    }

    /// <summary>
    /// Read a QNAME starting at <paramref name="offset"/>, advancing it past the terminating
    /// zero byte. Labels are joined with '.'.
    /// </summary>
    private static bool TryReadName(byte[] packet, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();

        while (true)
        {
            if (offset >= packet.Length)
                return false;

            int length = packet[offset];

            // A zero length byte marks the end of the name.
            if (length == 0)
            {
                offset++;
                break;
            }

            // Compression pointer (top two bits set): shouldn't appear in a query's question.
            if ((length & 0xC0) == 0xC0)
                return false;

            offset++;
            if (offset + length > packet.Length)
                return false;

            labels.Add(Encoding.ASCII.GetString(packet, offset, length));
            offset += length;
        }

        name = string.Join('.', labels);
        return name.Length > 0;
    }

    /// <summary>Human-readable name for the common record types, for logging.</summary>
    public static string TypeName(ushort type) => type switch
    {
        1 => "A",       // IPv4 address
        28 => "AAAA",   // IPv6 address
        5 => "CNAME",
        2 => "NS",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        33 => "SRV",
        65 => "HTTPS",
        _ => $"TYPE{type}"
    };
}
