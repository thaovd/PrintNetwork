using System.Drawing.Printing;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using PrintNetwork.Core;
using PrintNetwork.Printing;

namespace PrintNetwork.Server;

/// <summary>Máy chủ in: nhận lệnh từ client qua TCP, xếp hàng và in tuần tự.</summary>
public sealed class PrintHost : IDisposable
{
    sealed class QueuedJob
    {
        public required JobRecord Record { get; init; }
        public required string FilePath { get; init; }
        public required PrintOptions Options { get; init; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly AppSettings _s;
    CancellationTokenSource? _cts;
    TcpListener? _listener;
    Channel<QueuedJob>? _queue;
    DiscoveryResponder? _discovery;
    RawPortServer[] _raw = [];
    volatile Dictionary<string, string> _drivers = new(StringComparer.OrdinalIgnoreCase);
    int _sequence;

    public PrintHost(AppSettings settings) => _s = settings;

    public event Action<JobRecord>? JobUpdated;

    public bool IsRunning => _cts is not null;

    public static string SpoolDir => Path.Combine(AppSettings.Dir, "spool");

    public void Start()
    {
        if (IsRunning) return;
        CleanSpool();

        var listener = TcpListener.Create(_s.ControlPort);
        listener.Start(); // ném lỗi nếu cổng đang bị chiếm
        var cts = new CancellationTokenSource();
        _cts = cts;
        _listener = listener;

        _queue = Channel.CreateUnbounded<QueuedJob>(new UnboundedChannelOptions { SingleReader = true });
        var reader = _queue.Reader;
        new Thread(() => WorkerLoop(reader, cts.Token)) { IsBackground = true, Name = "PrintNetwork worker" }.Start();
        _ = AcceptLoop(listener, cts.Token);

        try
        {
            _discovery = new DiscoveryResponder(_s, () => _s.SharedPrinters.Count(p => p.Enabled));
            _discovery.Start();
        }
        catch (Exception ex)
        {
            _discovery = null;
            Log.Warn($"Không mở được cổng dò tìm UDP {_s.DiscoveryPort} ({ex.Message}) – client vẫn nhập IP thủ công được");
        }

        var raws = new List<RawPortServer>();
        foreach (var p in _s.SharedPrinters.Where(p => p.Enabled && p.RawPort > 0))
        {
            try
            {
                var server = new RawPortServer(p.Name, p.RawPort, _s.LanOnly, NextJobId, Raise);
                server.Start();
                raws.Add(server);
            }
            catch (Exception ex)
            {
                Log.Error($"Không mở được cổng RAW {p.RawPort} cho '{p.Name}'", ex);
            }
        }
        _raw = raws.ToArray();

        _ = Task.Run(RefreshDrivers);
        Log.Info($"Host đang chạy ở cổng {_s.ControlPort}, chia sẻ {_s.SharedPrinters.Count(p => p.Enabled)} máy in");
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;
        cts.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _queue?.Writer.TryComplete();
        _queue = null;
        _discovery?.Dispose();
        _discovery = null;
        foreach (var r in _raw) r.Dispose();
        _raw = [];
        Log.Info("Host đã dừng");
    }

    public void Dispose() => Stop();

    public void UpdateDrivers(IEnumerable<WinPrinter> printers) =>
        _drivers = printers.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DriverName, StringComparer.OrdinalIgnoreCase);

    void RefreshDrivers()
    {
        try { UpdateDrivers(PrinterManager.GetPrinters()); }
        catch (Exception ex) { Log.Warn("Không đọc được tên driver máy in: " + ex.Message); }
    }

    public List<SharedPrinterInfo> GetSharedPrinters()
    {
        var installed = PrinterManager.InstalledPrinterNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var defaultPrinter = PrinterManager.DefaultPrinterName();
        var raw = _raw;
        var drivers = _drivers;
        var list = new List<SharedPrinterInfo>();
        foreach (var cfg in _s.SharedPrinters.Where(p => p.Enabled && installed.Contains(p.Name)))
        {
            var info = new SharedPrinterInfo
            {
                Name = cfg.Name,
                Driver = drivers.GetValueOrDefault(cfg.Name, ""),
                IsDefault = string.Equals(cfg.Name, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                RawPort = raw.FirstOrDefault(r => r.Printer.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase))?.Port ?? 0,
            };
            try
            {
                var ps = new PrinterSettings { PrinterName = cfg.Name };
                info.SupportsColor = ps.SupportsColor;
                info.CanDuplex = ps.CanDuplex;
            }
            catch { }
            list.Add(info);
        }
        return list;
    }

    string NextJobId() => $"{DateTime.Now:MMdd}-{Interlocked.Increment(ref _sequence):D4}";

    void Raise(JobRecord record) => JobUpdated?.Invoke(record.Clone());

    // ---------------- Mạng ----------------

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
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                Log.Warn("Lỗi nhận kết nối: " + ex.Message);
                continue;
            }
            _ = Task.Run(() => HandleClient(client, ct));
        }
    }

    async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var ip = Protocol.Normalize(((IPEndPoint)client.Client.RemoteEndPoint!).Address);
        if (_s.LanOnly && !Protocol.IsLan(ip))
        {
            Log.Warn($"Từ chối kết nối ngoài LAN từ {ip}");
            return;
        }
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            NetMessage request;
            using (var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                request = await Protocol.ReadAsync(stream, headerTimeout.Token);
            }

            if (!Protocol.KeyMatches(_s.HostKey, request.Key))
            {
                Log.Warn($"Sai mã truy cập từ {ip} ({request.Machine})");
                await Protocol.WriteAsync(stream, NetMessage.Fail("Sai mã truy cập của host"), ct);
                return;
            }

            switch (request.Type)
            {
                case "list":
                    await Protocol.WriteAsync(stream, new NetMessage
                    {
                        Type = "list",
                        Ok = true,
                        Version = Protocol.Version,
                        HostName = _s.HostDisplayName,
                        Machine = Environment.MachineName,
                        Printers = GetSharedPrinters(),
                    }, ct);
                    break;
                case "print":
                    await HandlePrint(request, stream, ip, ct);
                    break;
                case "driver":
                    await HandleDriver(request, stream, ip, ct);
                    break;
                default:
                    await Protocol.WriteAsync(stream, NetMessage.Fail($"Lệnh '{request.Type}' không được hỗ trợ"), ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException)
        {
            if (!ct.IsCancellationRequested) Log.Warn($"Kết nối với {ip} bị ngắt: {ex.Message}");
        }
        catch (Exception ex)
        {
            Log.Error($"Lỗi xử lý yêu cầu từ {ip}", ex);
            try { await Protocol.WriteAsync(stream, NetMessage.Fail(ex.Message), ct); } catch { }
        }
    }

    async Task HandlePrint(NetMessage request, NetworkStream stream, IPAddress ip, CancellationToken ct)
    {
        var target = GetSharedPrinters().FirstOrDefault(p => p.Name.Equals(request.Printer, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            await Protocol.WriteAsync(stream, NetMessage.Fail($"Máy in '{request.Printer}' không tồn tại hoặc chưa được chia sẻ trên host"), ct);
            return;
        }
        if (request.Size <= 0 || request.Size > Protocol.MaxFileSize)
        {
            await Protocol.WriteAsync(stream, NetMessage.Fail("Kích thước file không hợp lệ (tối đa 1 GB)"), ct);
            return;
        }
        var queue = _queue;
        if (queue is null) return;

        var id = NextJobId();
        var fileName = SafeFileName(request.FileName);
        Directory.CreateDirectory(SpoolDir);
        var path = Path.Combine(SpoolDir, $"{id}_{fileName}");
        var record = new JobRecord
        {
            Id = id,
            Client = string.IsNullOrEmpty(request.Machine) ? ip.ToString() : $"{request.Machine} ({ip})",
            FileName = fileName,
            Printer = target.Name,
            Size = request.Size,
            Status = "Đang nhận",
        };
        Raise(record);

        await Protocol.WriteAsync(stream, new NetMessage { Type = "ready", Ok = true, JobId = id }, ct);
        try
        {
            using var receiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            receiveTimeout.CancelAfter(TimeSpan.FromMinutes(30));
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            await Protocol.CopyAsync(stream, fs, request.Size, null, receiveTimeout.Token);
        }
        catch
        {
            record.Status = JobRecord.Failed;
            record.Message = "Nhận file thất bại";
            Raise(record);
            TryDelete(path);
            throw;
        }

        var job = new QueuedJob { Record = record, FilePath = path, Options = request.Options ?? new PrintOptions() };
        record.Status = "Chờ in";
        Raise(record);
        await queue.Writer.WriteAsync(job, ct);
        await Protocol.WriteAsync(stream, new NetMessage { Type = "queued", Ok = true, JobId = id, Status = "Đã vào hàng đợi" }, ct);

        var finished = await Task.WhenAny(job.Done.Task, Task.Delay(TimeSpan.FromMinutes(30), ct));
        if (finished != job.Done.Task) return;
        await Protocol.WriteAsync(stream, new NetMessage
        {
            Type = "result",
            Ok = record.Status == JobRecord.Done,
            JobId = id,
            Status = record.Status,
            Error = record.Message,
            Pages = record.Pages,
        }, ct);
    }

    async Task HandleDriver(NetMessage request, NetworkStream stream, IPAddress ip, CancellationToken ct)
    {
        var target = GetSharedPrinters().FirstOrDefault(p => p.Name.Equals(request.Printer, StringComparison.OrdinalIgnoreCase));
        if (target is null || string.IsNullOrEmpty(target.Driver))
        {
            await Protocol.WriteAsync(stream, NetMessage.Fail("Không xác định được driver của máy in này trên host"), ct);
            return;
        }

        string zip;
        try
        {
            zip = await Task.Run(() => PrinterManager.ExportDriverPackage(target.Driver), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await Protocol.WriteAsync(stream, NetMessage.Fail(ex.Message), ct);
            return;
        }

        try
        {
            var length = new FileInfo(zip).Length;
            await Protocol.WriteAsync(stream, new NetMessage { Type = "driver", Ok = true, DriverName = target.Driver, Size = length }, ct);
            await using var fs = File.OpenRead(zip);
            await Protocol.CopyAsync(fs, stream, length, null, ct);
            Log.Info($"Đã gửi gói driver '{target.Driver}' ({length / 1048576.0:0.0} MB) cho {ip}");
        }
        finally
        {
            TryDelete(zip);
        }
    }

    // ---------------- In ----------------

    void WorkerLoop(ChannelReader<QueuedJob> reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueuedJob? job;
            try
            {
                if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) break;
                if (!reader.TryRead(out job)) continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            ProcessJob(job);
        }
    }

    void ProcessJob(QueuedJob job)
    {
        var record = job.Record;
        record.Status = "Đang in";
        Raise(record);
        try
        {
            record.Pages = JobPrinter.Print(job.FilePath, record.FileName, record.Printer, job.Options, _s.PdfDpi);
            record.Status = JobRecord.Done;
            Log.Info($"Đã in '{record.FileName}' → '{record.Printer}'" + (record.Pages > 0 ? $" ({record.Pages} trang)" : "") + $" cho {record.Client}");
        }
        catch (Exception ex)
        {
            record.Status = JobRecord.Failed;
            record.Message = ex.Message;
            Log.Error($"In '{record.FileName}' thất bại", ex);
        }
        finally
        {
            TryDelete(job.FilePath);
            Raise(record);
            job.Done.TrySetResult();
        }
    }

    // ---------------- Tiện ích ----------------

    static string SafeFileName(string? name)
    {
        name = Path.GetFileName(name ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = "document.bin";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 120)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 16) ext = "";
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }

    static void CleanSpool()
    {
        try
        {
            if (!Directory.Exists(SpoolDir)) return;
            foreach (var f in Directory.EnumerateFiles(SpoolDir)) TryDelete(f);
        }
        catch { }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
