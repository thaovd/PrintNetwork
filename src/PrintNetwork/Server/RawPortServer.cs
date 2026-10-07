using System.Net.Sockets;
using PrintNetwork.Core;
using PrintNetwork.Printing;

namespace PrintNetwork.Server;

/// <summary>
/// Cổng RAW kiểu JetDirect (9100…): client khai báo một máy in "Standard TCP/IP" trỏ tới host:cổng này,
/// in từ bất kỳ phần mềm nào; dữ liệu đã qua driver của client được chuyển nguyên vẹn vào máy in của host.
/// </summary>
public sealed class RawPortServer : IDisposable
{
    readonly string _printer;
    readonly int _port;
    readonly bool _lanOnly;
    readonly Func<string> _nextId;
    readonly Action<JobRecord> _report;
    readonly CancellationTokenSource _cts = new();
    TcpListener? _listener;

    public RawPortServer(string printer, int port, bool lanOnly, Func<string> nextId, Action<JobRecord> report)
    {
        _printer = printer;
        _port = port;
        _lanOnly = lanOnly;
        _nextId = nextId;
        _report = report;
    }

    public string Printer => _printer;
    public int Port => _port;

    public void Start()
    {
        _listener = TcpListener.Create(_port);
        _listener.Start();
        _ = AcceptLoop(_listener, _cts.Token);
        Log.Info($"Cổng RAW {_port} → '{_printer}'");
    }

    async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Warn($"Cổng RAW {_port}: {ex.Message}");
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = Task.Run(() => Handle(client, ct));
        }
    }

    async Task Handle(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var ip = Protocol.Normalize(((System.Net.IPEndPoint)client.Client.RemoteEndPoint!).Address);
        if (_lanOnly && !Protocol.IsLan(ip))
        {
            Log.Warn($"Từ chối kết nối RAW ngoài LAN từ {ip}");
            return;
        }

        var stream = client.GetStream();
        var buffer = new byte[65536];
        RawPrintJob? job = null;
        JobRecord? record = null;
        long total = 0;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (job is null)
                {
                    // Chỉ tạo lệnh in khi có dữ liệu thật (Windows đôi khi mở kết nối chỉ để kiểm tra cổng).
                    record = new JobRecord
                    {
                        Id = _nextId(),
                        Client = ip.ToString(),
                        FileName = "Lệnh in từ Windows (RAW)",
                        Printer = _printer,
                        Status = "Đang in",
                    };
                    _report(record);
                    job = new RawPrintJob(_printer, $"PrintNetwork RAW từ {ip}");
                }
                job.Write(buffer, 0, n);
                total += n;
            }
            if (record is not null)
            {
                record.Status = JobRecord.Done;
                Log.Info($"Đã chuyển lệnh RAW {total:N0} byte từ {ip} → '{_printer}'");
            }
        }
        catch (Exception ex)
        {
            if (record is not null)
            {
                record.Status = JobRecord.Failed;
                record.Message = ex.Message;
            }
            Log.Error($"Lệnh RAW từ {ip} tới '{_printer}' thất bại", ex);
        }
        finally
        {
            job?.Dispose();
            if (record is not null)
            {
                record.Size = total;
                _report(record);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
