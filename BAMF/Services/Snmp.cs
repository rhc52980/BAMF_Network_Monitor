using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// Just enough SNMP to read a managed switch's port counters: version 2c,
/// walking a table with GetBulk. No library, because this is all BAMF needs
/// and it only ever reads.
///
/// SNMP messages are BER-encoded: every value is a type byte, a length, then
/// the contents, and a message is values nested inside sequences.
/// </summary>
public static class Snmp
{
    public sealed record Value(string Oid, byte Type, object? Data);

    private const byte Integer = 0x02, OctetString = 0x04, Null = 0x05, ObjectId = 0x06, Sequence = 0x30;
    private const byte GetBulk = 0xA5, Response = 0xA2;
    private const byte Counter32 = 0x41, Gauge32 = 0x42, TimeTicks = 0x43, Counter64 = 0x46;
    private const byte NoSuchObject = 0x80, NoSuchInstance = 0x81, EndOfMib = 0x82;

    /// <summary>
    /// Every value under an OID, keyed by the index after it: walking ifName
    /// gives { "1": "gi1", "2": "gi2", … }. Stops at the end of the subtree, and
    /// after a few thousand rows in case an agent never does.
    /// </summary>
    public static async Task<Dictionary<string, Value>> Walk(IPAddress host, string community, string baseOid, CancellationToken ct, int port = 161)
    {
        var rows = new Dictionary<string, Value>();
        var prefix = baseOid + ".";
        var next = baseOid;
        using var udp = new UdpClient(host.AddressFamily);
        udp.Connect(host, port);
        for (var guard = 0; guard < 200 && rows.Count < 5000; guard++)
        {
            var reply = await Request(udp, community, next, ct);
            var more = false;
            foreach (var v in reply)
            {
                if (v.Type is EndOfMib or NoSuchObject or NoSuchInstance || !v.Oid.StartsWith(prefix, StringComparison.Ordinal)) { more = false; break; }
                rows[v.Oid[prefix.Length..]] = v;
                next = v.Oid;
                more = true;
            }
            if (!more) break;
        }
        return rows;
    }

    private static int _requestId = Environment.TickCount & 0x7FFFFFFF;

    /// <summary>One GetBulk, with one retry. Throws TimeoutException when nothing answers.</summary>
    private static async Task<List<Value>> Request(UdpClient udp, string community, string oid, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId) & 0x7FFFFFFF;
        var varbind = Tlv(Sequence, Concat(Tlv(ObjectId, EncodeOid(oid)), Tlv(Null, Array.Empty<byte>())));
        var pdu = Tlv(GetBulk, Concat(Int(id), Int(0), Int(25), Tlv(Sequence, varbind)));
        var message = Tlv(Sequence, Concat(Int(1), Tlv(OctetString, Encoding.ASCII.GetBytes(community)), pdu));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await udp.SendAsync(message, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                while (true)
                {
                    var got = await udp.ReceiveAsync(timeout.Token);
                    var parsed = Parse(got.Buffer, id);
                    if (parsed is not null) return parsed;   // anything else is a stray answer to an earlier try
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                throw new InvalidOperationException("Nothing is listening for SNMP there (the port is closed).");
            }
        }
        throw new TimeoutException("No SNMP answer. Check that SNMP v2c is switched on in the switch and the community is right.");
    }

    // ------------------------------------------------------------ decoding

    /// <summary>The varbinds of a response to this request id, or null if it's for some other request.</summary>
    private static List<Value>? Parse(byte[] b, int id)
    {
        var at = 0;
        var msg = Read(b, ref at, Sequence);
        var m = msg.Start;
        ReadAny(b, ref m);                              // version
        ReadAny(b, ref m);                              // community
        var (pduType, pduStart, pduLen) = ReadAny(b, ref m);
        if (pduType != Response) return null;
        var p = pduStart;
        if (ToLong(b, Read(b, ref p, Integer)) != id) return null;
        var err = ToLong(b, Read(b, ref p, Integer));
        ReadAny(b, ref p);                              // error index
        if (err == 2) return new List<Value>();         // noSuchName: nothing there
        if (err != 0) throw new InvalidOperationException($"The switch refused the request (SNMP error {err}).");
        var list = Read(b, ref p, Sequence);
        var values = new List<Value>();
        var v = list.Start;
        while (v < list.Start + list.Length)
        {
            var one = Read(b, ref v, Sequence);
            var q = one.Start;
            var oid = DecodeOid(b, Read(b, ref q, ObjectId));
            var (type, start, len) = ReadAny(b, ref q);
            object? data = type switch
            {
                Integer => ToLong(b, (start, len)),
                Counter32 or Gauge32 or TimeTicks or Counter64 => ToULong(b, start, len),
                OctetString => Encoding.UTF8.GetString(b, start, len).TrimEnd('\0'),
                ObjectId => DecodeOid(b, (start, len)),
                _ => null,
            };
            values.Add(new Value(oid, type, data));
        }
        _ = pduLen;
        return values;
    }

    private static (byte Type, int Start, int Length) ReadAny(byte[] b, ref int at)
    {
        if (at + 2 > b.Length) throw new InvalidDataException("Short SNMP packet.");
        var type = b[at++];
        int len = b[at++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            if (n is 0 or > 4 || at + n > b.Length) throw new InvalidDataException("Bad SNMP length.");
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | b[at++];
        }
        if (len < 0 || at + len > b.Length) throw new InvalidDataException("SNMP value runs past the packet.");
        var start = at;
        at += len;
        return (type, start, len);
    }

    private static (int Start, int Length) Read(byte[] b, ref int at, byte want)
    {
        var (type, start, len) = ReadAny(b, ref at);
        if (type != want) throw new InvalidDataException($"Expected SNMP type {want:X2}, got {type:X2}.");
        return (start, len);
    }

    private static long ToLong(byte[] b, (int Start, int Length) s)
    {
        long v = s.Length > 0 && (b[s.Start] & 0x80) != 0 ? -1 : 0;
        for (var i = 0; i < s.Length; i++) v = (v << 8) | b[s.Start + i];
        return v;
    }

    private static ulong ToULong(byte[] b, int start, int len)
    {
        ulong v = 0;
        for (var i = 0; i < len; i++) v = (v << 8) | b[start + i];
        return v;
    }

    private static string DecodeOid(byte[] b, (int Start, int Length) s)
    {
        if (s.Length == 0) return "";
        var parts = new List<ulong> { (ulong)(b[s.Start] / 40), (ulong)(b[s.Start] % 40) };
        ulong cur = 0;
        for (var i = 1; i < s.Length; i++)
        {
            var x = b[s.Start + i];
            cur = (cur << 7) | (uint)(x & 0x7F);
            if ((x & 0x80) == 0) { parts.Add(cur); cur = 0; }
        }
        return string.Join(".", parts);
    }

    // ------------------------------------------------------------ encoding

    private static byte[] EncodeOid(string oid)
    {
        var n = oid.Split('.').Select(ulong.Parse).ToArray();
        var bytes = new List<byte> { (byte)(n[0] * 40 + n[1]) };
        foreach (var x in n.Skip(2))
        {
            var chunk = new Stack<byte>();
            var v = x;
            chunk.Push((byte)(v & 0x7F));
            while ((v >>= 7) > 0) chunk.Push((byte)(0x80 | (v & 0x7F)));
            bytes.AddRange(chunk);
        }
        return bytes.ToArray();
    }

    private static byte[] Int(long v)
    {
        var bytes = new List<byte>();
        do { bytes.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v != 0 && v != -1);
        if (v == 0 && (bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return Tlv(Integer, bytes.ToArray());
    }

    private static byte[] Tlv(byte type, byte[] content)
    {
        var len = content.Length;
        byte[] head = len < 0x80 ? new[] { type, (byte)len }
            : len < 0x100 ? new[] { type, (byte)0x81, (byte)len }
            : new[] { type, (byte)0x82, (byte)(len >> 8), (byte)len };
        return Concat(head, content);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, at, p.Length); at += p.Length; }
        return all;
    }
}
