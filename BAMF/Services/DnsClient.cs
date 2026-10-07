using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// One DNS question, asked of one server, timed. The operating system's resolver would answer from its cache and say
/// nothing about how long the server took or which server answered, so this builds the packet itself: a plain UDP
/// query for A records, recursion wanted, and reads the addresses out of the reply.
/// </summary>
public static class DnsClient
{
    /// <summary>What came back: the addresses, how long it took, the reply's response code (0 fine, 3 no such name), or why it failed.</summary>
    public sealed record Answer(List<IPAddress> Addresses, int Ms, int RCode, string? Error)
    {
        public bool Ok => Error is null;
        public bool NoSuchName => Error is null && RCode == 3;
    }

    public static async Task<Answer> QueryA(IPAddress server, string name, int timeoutMs, CancellationToken ct)
    {
        var id = (ushort)Random.Shared.Next(1, 65535);
        var query = Build(id, name);
        var sw = Stopwatch.StartNew();
        try
        {
            using var udp = new UdpClient(server.AddressFamily);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeoutMs);
            await udp.SendAsync(query, new IPEndPoint(server, 53), limit.Token);
            while (true)
            {
                var r = await udp.ReceiveAsync(limit.Token);
                if (r.Buffer.Length < 12 || (r.Buffer[0] << 8 | r.Buffer[1]) != id) continue;    // not ours
                sw.Stop();
                var (addresses, rcode) = Parse(r.Buffer);
                return new Answer(addresses, (int)sw.ElapsedMilliseconds, rcode, null);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new Answer(new(), timeoutMs, -1, "No answer in time."); }
        catch (Exception ex) { return new Answer(new(), (int)sw.ElapsedMilliseconds, -1, ex.GetBaseException().Message); }
    }

    /// <summary>The IPv4 DNS servers this machine's working network cards are set to use, in order, without repeats.</summary>
    public static List<IPAddress> SystemServers()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var dns in nic.GetIPProperties().DnsAddresses)
                    if (dns.AddressFamily == AddressFamily.InterNetwork && !list.Contains(dns)) list.Add(dns);
            }
        }
        catch (NetworkInformationException) { }
        return list;
    }

    internal static byte[] Build(ushort id, string name)
    {
        var buf = new List<byte>(64)
        {
            (byte)(id >> 8), (byte)id,
            0x01, 0x00,             // standard query, recursion desired
            0x00, 0x01,             // one question
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Not a name DNS can ask about.", nameof(name));
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.AddRange(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01 });     // end of name, type A, class IN
        return buf.ToArray();
    }

    internal static (List<IPAddress> Addresses, int RCode) Parse(byte[] d)
    {
        var rcode = d[3] & 0x0F;
        int qd = d[4] << 8 | d[5], an = d[6] << 8 | d[7];
        var pos = 12;
        for (var i = 0; i < qd; i++) { pos = SkipName(d, pos) + 4; }
        var addresses = new List<IPAddress>();
        for (var i = 0; i < an && pos + 10 <= d.Length; i++)
        {
            pos = SkipName(d, pos);
            int type = d[pos] << 8 | d[pos + 1], len = d[pos + 8] << 8 | d[pos + 9];
            pos += 10;
            if (pos + len > d.Length) break;
            if (type == 1 && len == 4) addresses.Add(new IPAddress(new ReadOnlySpan<byte>(d, pos, 4)));
            pos += len;
        }
        return (addresses, rcode);
    }

    private static int SkipName(byte[] d, int pos)
    {
        while (pos < d.Length)
        {
            var len = d[pos];
            if ((len & 0xC0) == 0xC0) return pos + 2;      // a pointer ends the name
            if (len == 0) return pos + 1;
            pos += 1 + len;
        }
        return pos;
    }
}
