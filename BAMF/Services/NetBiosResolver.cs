using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanWatch.Services;

/// <summary>
/// Resolves a host's name via a NetBIOS node status request (UDP 137).
/// Many Windows machines, NAS devices, and printers answer this even when
/// they have no reverse-DNS record. Best-effort: returns "" on no reply.
/// </summary>
public static class NetBiosResolver
{
    public static async Task<string> QueryAsync(IPAddress ip, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = timeoutMs;

            var request = BuildNodeStatusRequest();
            await udp.SendAsync(request, request.Length, new IPEndPoint(ip, 137));

            var receiveTask = udp.ReceiveAsync();
            var done = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, ct));
            if (done != receiveTask) return "";

            return ParseNodeStatusResponse(receiveTask.Result.Buffer);
        }
        catch
        {
            return "";
        }
    }

    // NBSTAT query for the wildcard name "*" — asks the host to list its names.
    private static byte[] BuildNodeStatusRequest()
    {
        var packet = new byte[50];
        // Transaction ID
        packet[0] = 0x13; packet[1] = 0x37;
        // Flags (0x0000), Questions (1)
        packet[4] = 0x00; packet[5] = 0x01;
        // Question name: encoded "*" padded with 0x00 -> "CKAAAA...AA"
        packet[12] = 0x20; // name length 0x20 (32 bytes encoded)

        // Encode the wildcard "*" name: first char '*' (0x2A) then 15 nulls,
        // each nibble mapped to 'A' + nibble.
        var name = new byte[16];
        name[0] = (byte)'*';
        var idx = 13;
        foreach (var b in name)
        {
            packet[idx++] = (byte)('A' + ((b >> 4) & 0x0F));
            packet[idx++] = (byte)('A' + (b & 0x0F));
        }
        packet[idx++] = 0x00;          // null terminator for name
        packet[idx++] = 0x00; packet[idx++] = 0x21; // Type: NBSTAT (0x21)
        packet[idx++] = 0x00; packet[idx++] = 0x01; // Class: IN
        return packet;
    }

    /// <summary>
    /// Detects synthetic names devices generate from their MAC when they have
    /// no real name (common on printers: e.g. "C22E4F700000"). Heuristic: the
    /// name is mostly hex digits with almost no wordlike letters.
    /// </summary>
    private static bool LooksMacDerived(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length < 8) return false; // short names are probably real

        int hexChars = 0, nonHex = 0;
        foreach (var c in trimmed)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (isHex) hexChars++;
            else if (c != '-' && c != '_') nonHex++;
        }

        var total = hexChars + nonHex;
        if (total == 0) return false;
        return nonHex <= 1 && (double)hexChars / total >= 0.8;
    }

    private static string ParseNodeStatusResponse(byte[] data)
    {
        // Header is 12 bytes; then the answer name, type/class/ttl, rdlength,
        // then: 1 byte number-of-names, followed by 18-byte entries
        // (16-byte name + 2 flag bytes).
        try
        {
            var offset = 12;
            // skip the answer's encoded name (0x20 length + 32 bytes + null)
            offset += 1 + 32 + 1;
            offset += 2 + 2 + 4; // type, class, ttl
            offset += 2;         // rdlength
            if (offset >= data.Length) return "";

            int nameCount = data[offset];
            offset += 1;

            for (var i = 0; i < nameCount; i++)
            {
                var start = offset + i * 18;
                if (start + 18 > data.Length) break;

                var rawName = Encoding.ASCII.GetString(data, start, 15).TrimEnd();
                var suffix = data[start + 15];       // NetBIOS suffix byte
                var flags = (data[start + 16] << 8) | data[start + 17];
                var isGroup = (flags & 0x8000) != 0;

                // Suffix 0x00 + unique = the workstation/computer name we want.
                if (suffix == 0x00 && !isGroup && rawName.Length > 0)
                {
                    if (LooksMacDerived(rawName)) return ""; // junk name (e.g. printers)
                    return rawName;
                }
            }
        }
        catch { }
        return "";
    }
}
