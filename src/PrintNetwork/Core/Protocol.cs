using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintNetwork.Core;

/// <summary>
/// Giao thức: mỗi gói = 4 byte độ dài (little-endian) + JSON UTF-8.
/// Lệnh in: client gửi header "print" → host trả "ready" → client gửi đúng Size byte dữ liệu file
/// → host trả "queued" → khi in xong host trả "result".
/// </summary>
public static class Protocol
{
    public const int DefaultControlPort = 9200;
    public const int DefaultDiscoveryPort = 9201;
    public const string DiscoverMagic = "PRINTNET_DISCOVER_V1";
    public const string Version = "1.0";
    public const long MaxFileSize = 1L << 30; // 1 GB
    const int MaxHeader = 1 << 20;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task WriteAsync(Stream stream, NetMessage message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var buffer = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, body.Length);
        body.CopyTo(buffer, 4);
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<NetMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (length <= 0 || length > MaxHeader) throw new InvalidDataException("Gói tin không hợp lệ (không phải PrintNetwork?)");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<NetMessage>(body, Json) ?? throw new InvalidDataException("Gói tin rỗng");
    }

    /// <summary>Chép đúng <paramref name="count"/> byte từ src sang dst.</summary>
    public static async Task CopyAsync(Stream src, Stream dst, long count, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        while (done < count)
        {
            var n = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - done)), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Kết nối bị ngắt khi đang truyền dữ liệu");
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            progress?.Invoke(done);
        }
    }

    public static bool KeyMatches(string expected, string? given)
    {
        if (string.IsNullOrEmpty(expected)) return true;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(given ?? ""));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Địa chỉ thuộc mạng nội bộ (private, link-local, loopback).</summary>
    public static bool IsLan(IPAddress ip)
    {
        ip = Normalize(ip);
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    static IEnumerable<UnicastIPAddressInformation> LocalUnicastIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork) yield return ua;
        }
    }

    public static List<IPAddress> LocalIPv4() => LocalUnicastIPv4().Select(u => u.Address).Distinct().ToList();

    public static List<IPAddress> BroadcastAddresses()
    {
        var result = new List<IPAddress>();
        foreach (var ua in LocalUnicastIPv4())
        {
            if (ua.IPv4Mask is null) continue;
            var ip = ua.Address.GetAddressBytes();
            var mask = ua.IPv4Mask.GetAddressBytes();
            if (mask.All(x => x == 0)) continue;
            var bc = new byte[4];
            for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
            result.Add(new IPAddress(bc));
        }
        return result.Distinct().ToList();
    }
}
