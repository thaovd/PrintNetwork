using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using PrintNetwork.Core;

namespace PrintNetwork.Server;

/// <summary>Trả lời gói broadcast UDP để client tự tìm thấy host trong LAN.</summary>
public sealed class DiscoveryResponder : IDisposable
{
    const int SIO_UDP_CONNRESET = -1744830452;

    readonly AppSettings _settings;
    readonly Func<int> _printerCount;
    readonly CancellationTokenSource _cts = new();
    UdpClient? _udp;

    public DiscoveryResponder(AppSettings settings, Func<int> printerCount)
    {
        _settings = settings;
        _printerCount = printerCount;
    }

    public void Start()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _settings.DiscoveryPort)) { EnableBroadcast = true };
        try { _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }
        _ = Loop(_udp, _cts.Token);
    }

    async Task Loop(UdpClient udp, CancellationToken ct)
    {
        var magic = Encoding.ASCII.GetBytes(Protocol.DiscoverMagic);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (!r.Buffer.AsSpan().SequenceEqual(magic)) continue;
            if (_settings.LanOnly && !Protocol.IsLan(r.RemoteEndPoint.Address)) continue;

            var reply = JsonSerializer.SerializeToUtf8Bytes(new NetMessage
            {
                Type = "host",
                Ok = true,
                Version = Protocol.Version,
                HostName = _settings.HostDisplayName,
                Machine = Environment.MachineName,
                Port = _settings.ControlPort,
                Count = _printerCount(),
                NeedKey = !string.IsNullOrEmpty(_settings.HostKey),
            }, Protocol.Json);
            try { await udp.SendAsync(reply, r.RemoteEndPoint, ct); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
    }
}
