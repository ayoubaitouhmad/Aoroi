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

    /// <summary>
    /// Find the byte offset just past the first question (past QNAME + QTYPE + QCLASS).
    /// That's where the answer section would begin — and the length of a header+question-only
    /// reply.
    /// </summary>
    public static bool TryGetFirstQuestionEnd(byte[] packet, out int end)
    {
        end = 0;
        if (packet.Length < HeaderLength + 1)
            return false;

        int questionCount = (packet[4] << 8) | packet[5];
        if (questionCount < 1)
            return false;

        int offset = HeaderLength;
        if (!TryReadName(packet, ref offset, out _))
            return false;

        if (offset + 4 > packet.Length)
            return false;

        end = offset + 4; // skip QTYPE (2) + QCLASS (2)
        return true;
    }

    /// <summary>
    /// Build an NXDOMAIN ("name does not exist", RCODE 3) reply to a query. We reuse the
    /// query's ID and question so the client matches it to its request, flip the header into a
    /// response, set the error code, and carry zero answer records.
    ///
    /// Header bytes we change:
    ///   byte 2: set QR=1 (0x80) to mark it a response, keeping the opcode + RD bit from the query
    ///   byte 3: 0x83 = RA=1 (we do offer recursion) + RCODE=3 (NXDOMAIN)
    ///   bytes 6-11: zero ANCOUNT/NSCOUNT/ARCOUNT (QDCOUNT at 4-5 stays 1)
    /// </summary>
    public static byte[]? BuildNxDomainResponse(byte[] query)
    {
        if (!TryGetFirstQuestionEnd(query, out int questionEnd))
            return null;

        // Header + the original question only.
        var response = new byte[questionEnd];
        Array.Copy(query, response, questionEnd);

        response[2] = (byte)(query[2] | 0x80); // QR=1, preserve opcode + RD
        response[3] = 0x83;                    // RA=1, RCODE=3 (NXDOMAIN)

        response[6] = 0; response[7] = 0;      // ANCOUNT = 0
        response[8] = 0; response[9] = 0;      // NSCOUNT = 0
        response[10] = 0; response[11] = 0;    // ARCOUNT = 0

        return response;
    }

    /// <summary>
    /// Build a reply that answers the query with a single CNAME record pointing the asked-for
    /// name at <paramref name="target"/> (e.g. forcesafesearch.google.com). We echo the question
    /// so the client matches the reply, use a compression pointer (0xC00C) for the answer's owner
    /// name, and carry one CNAME record. The client then resolves <paramref name="target"/>.
    ///
    /// Answer record layout:
    ///   NAME     : 0xC0 0x0C         (pointer to the question name at offset 12)
    ///   TYPE     : 0x0005            (CNAME)
    ///   CLASS    : 0x0001            (IN)
    ///   TTL      : 300 seconds
    ///   RDLENGTH : length of the encoded target name
    ///   RDATA    : target name as length-prefixed labels, ending in 0
    /// </summary>
    public static byte[]? BuildCnameResponse(byte[] query, string target)
    {
        if (!TryGetFirstQuestionEnd(query, out int questionEnd))
            return null;

        byte[] rdata = EncodeName(target);
        int answerLength = 2 /*name ptr*/ + 2 /*type*/ + 2 /*class*/ + 4 /*ttl*/ + 2 /*rdlen*/ + rdata.Length;

        var response = new byte[questionEnd + answerLength];
        Array.Copy(query, response, questionEnd);

        // Header: response, no error, one question, one answer.
        response[2] = (byte)(query[2] | 0x80); // QR=1, preserve opcode + RD
        response[3] = 0x80;                    // RA=1, RCODE=0
        response[4] = 0; response[5] = 1;      // QDCOUNT = 1
        response[6] = 0; response[7] = 1;      // ANCOUNT = 1
        response[8] = 0; response[9] = 0;      // NSCOUNT = 0
        response[10] = 0; response[11] = 0;    // ARCOUNT = 0

        int o = questionEnd;
        response[o++] = 0xC0; response[o++] = 0x0C;            // NAME -> pointer to question
        response[o++] = 0x00; response[o++] = 0x05;            // TYPE = CNAME
        response[o++] = 0x00; response[o++] = 0x01;            // CLASS = IN
        response[o++] = 0x00; response[o++] = 0x00;            // TTL (high)
        response[o++] = 0x01; response[o++] = 0x2C;            // TTL (low) = 300
        response[o++] = (byte)(rdata.Length >> 8);             // RDLENGTH high
        response[o++] = (byte)(rdata.Length & 0xFF);           // RDLENGTH low
        Array.Copy(rdata, 0, response, o, rdata.Length);

        return response;
    }

    /// <summary>Encode a host name as DNS length-prefixed labels terminated by a zero byte.</summary>
    private static byte[] EncodeName(string host)
    {
        var bytes = new List<byte>();
        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)raw.Length);
            bytes.AddRange(raw);
        }
        bytes.Add(0);
        return bytes.ToArray();
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
