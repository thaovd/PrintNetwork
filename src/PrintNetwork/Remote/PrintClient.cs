using System.Net.Sockets;
using PrintNetwork.Core;

namespace PrintNetwork.Remote;

/// <summary>Gửi yêu cầu tới host: lấy danh sách máy in, gửi file in, tải gói driver.</summary>
public sealed class PrintClient
{
    readonly string _key;

    public PrintClient(string hostSpec, string key, int defaultPort = Protocol.DefaultControlPort)
    {
        _key = key;
        hostSpec = hostSpec.Trim();
        var colon = hostSpec.LastIndexOf(':');
        if (colon > 0 && hostSpec.IndexOf(':') == colon && int.TryParse(hostSpec[(colon + 1)..], out var port))
        {
            Host = hostSpec[..colon];
            Port = port;
        }
        else
        {
            Host = hostSpec;
            Port = defaultPort;
        }
    }

    public string Host { get; }
    public int Port { get; }

    async Task<TcpClient> ConnectAsync(CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await client.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"Không kết nối được tới {Host}:{Port} (quá thời gian). Kiểm tra host đã bật và tường lửa đã mở.");
        }
        catch (SocketException ex)
        {
            client.Dispose();
            throw new IOException($"Không kết nối được tới {Host}:{Port}: {ex.Message}", ex);
        }
    }

    NetMessage NewRequest(string type) => new()
    {
        Type = type,
        Key = _key,
        Version = Protocol.Version,
        Machine = Environment.MachineName,
        User = Environment.UserName,
    };

    static NetMessage EnsureOk(NetMessage m) =>
        m.Ok ? m : throw new InvalidOperationException(m.Error ?? "Host báo lỗi không rõ nguyên nhân");

    static async Task<NetMessage> ReadAsync(Stream s, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await Protocol.ReadAsync(s, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Host không phản hồi");
        }
    }

    public async Task<NetMessage> ListAsync(CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var s = client.GetStream();
        await Protocol.WriteAsync(s, NewRequest("list"), ct).ConfigureAwait(false);
        return EnsureOk(await ReadAsync(s, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false));
    }

    /// <summary>Gửi file và chờ host in xong. Trả về gói "result" của host (Ok = in thành công).</summary>
    public async Task<NetMessage> PrintFileAsync(string path, string printer, PrintOptions options,
        Action<double>? progress = null, Action<string>? status = null, CancellationToken ct = default)
    {
        var file = new FileInfo(path);
        if (file.Length == 0) throw new InvalidOperationException("File rỗng");
        if (file.Length > Protocol.MaxFileSize) throw new InvalidOperationException("File quá lớn (tối đa 1 GB)");

        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var s = client.GetStream();
        var request = NewRequest("print");
        request.Printer = printer;
        request.FileName = file.Name;
        request.Size = file.Length;
        request.Options = options;
        await Protocol.WriteAsync(s, request, ct).ConfigureAwait(false);
        EnsureOk(await ReadAsync(s, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false));

        status?.Invoke("Đang gửi…");
        var lastPercent = -1;
        void Report(long done)
        {
            // Chỉ báo khi phần trăm thay đổi để không dội sự kiện lên giao diện.
            var percent = (int)(done * 100 / file.Length);
            if (percent == lastPercent) return;
            lastPercent = percent;
            progress?.Invoke(percent / 100.0);
        }
        await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, true))
            await Protocol.CopyAsync(fs, s, file.Length, Report, ct).ConfigureAwait(false);

        EnsureOk(await ReadAsync(s, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false));
        status?.Invoke("Host đang in…");
        return await ReadAsync(s, TimeSpan.FromMinutes(35), ct).ConfigureAwait(false);
    }

    /// <summary>Tải gói driver của máy in từ host về file zip tạm.</summary>
    public async Task<(string DriverName, string ZipPath)> DownloadDriverAsync(string printer, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct).ConfigureAwait(false);
        var s = client.GetStream();
        var request = NewRequest("driver");
        request.Printer = printer;
        await Protocol.WriteAsync(s, request, ct).ConfigureAwait(false);
        var header = EnsureOk(await ReadAsync(s, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false));

        var dir = Path.Combine(Path.GetTempPath(), "PrintNetwork");
        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, $"driver_{Guid.NewGuid():N}.zip");
        await using (var fs = File.Create(zip))
            await Protocol.CopyAsync(s, fs, header.Size, null, ct).ConfigureAwait(false);
        return (header.DriverName ?? "", zip);
    }
}
