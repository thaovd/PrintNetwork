using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PrintNetwork.Core;

namespace PrintNetwork.Remote;

/// <summary>Phát broadcast UDP và gom phản hồi của các host trong LAN.</summary>
public static class HostDiscovery
{
    const int SIO_UDP_CONNRESET = -1744830452;

    public static async Task<List<HostInfo>> DiscoverAsync(int discoveryPort, int timeoutMs = 1500, CancellationToken ct = default)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }

        var payload = Encoding.ASCII.GetBytes(Protocol.DiscoverMagic);
        var targets = Protocol.BroadcastAddresses();
        targets.Add(IPAddress.Broadcast);
        targets.Add(IPAddress.Loopback);
        foreach (var target in targets.Distinct())
        {
            try { await udp.SendAsync(payload, new IPEndPoint(target, discoveryPort), ct).ConfigureAwait(false); }
            catch (SocketException) { }
        }

        var found = new Dictionary<string, HostInfo>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);
        while (true)
        {
            try
            {
                var r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var m = JsonSerializer.Deserialize<NetMessage>(r.Buffer, Protocol.Json);
                if (m?.Type != "host") continue;
                var address = Protocol.Normalize(r.RemoteEndPoint.Address);
                // Host trên chính máy này có thể trả lời qua nhiều địa chỉ; gộp theo tên máy + cổng.
                var key = $"{m.Machine}|{m.Port}";
                if (found.ContainsKey(key) && IPAddress.IsLoopback(address)) continue;
                found[key] = new HostInfo
                {
                    Address = address.ToString(),
                    Port = m.Port,
                    HostName = m.HostName ?? "",
                    Machine = m.Machine ?? "",
                    PrinterCount = m.Count,
                    NeedKey = m.NeedKey,
                };
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or JsonException)
            {
            }
        }
        return found.Values.OrderBy(h => h.HostName).ToList();
    }
}
