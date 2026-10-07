using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PrintNetwork.Core;

/// <summary>
/// Tự cập nhật chạy nền từ GitHub Releases (bản phát hành mới nhất, bỏ qua bản nháp/pre-release).
/// Tải PrintNetwork.exe của release về cạnh exe đang chạy rồi tráo tên – Windows cho phép đổi tên exe đang chạy,
/// nên lần khởi động sau là bản mới. Không hiện thông báo, chỉ ghi nhật ký.
/// </summary>
public static class AutoUpdater
{
    const string LatestReleaseApi = "https://api.github.com/repos/thaovd/PrintNetwork/releases/latest";
    const string AssetName = "PrintNetwork.exe";
    public const string AfterUpdateArg = "--after-update";

    static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public static Version CurrentVersion { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0));

    public static string DisplayVersion => CurrentVersion.ToString(3);

    /// <summary>Đã thay exe bằng bản mới, chỉ chờ khởi động lại.</summary>
    public static bool UpdateReady { get; private set; }

    static string? ExePath => Environment.ProcessPath;

    /// <summary>Chỉ tự cập nhật bản đóng gói một file (publish.ps1); bản build thường có PrintNetwork.dll đi kèm thì bỏ qua.</summary>
    static bool Enabled => ExePath is { } exe && !File.Exists(Path.ChangeExtension(exe, ".dll"));

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Kiểm tra định kỳ đến khi đã cài được bản mới; khi đó gọi <paramref name="onReady"/> (trên luồng nền).</summary>
    public static async Task RunAsync(Action onReady, CancellationToken ct)
    {
        if (!Enabled) return;
        try
        {
            await Task.Delay(FirstCheckDelay, ct);
            CleanUp();
            while (true)
            {
                try
                {
                    if (await TryUpdateAsync(ct))
                    {
                        UpdateReady = true;
                        onReady();
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Log.Warn("Tự cập nhật: " + ex.Message);
                }
                await Task.Delay(CheckInterval, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Xóa exe cũ và file tải dở còn lại từ lần cập nhật trước.</summary>
    static void CleanUp()
    {
        var exe = ExePath!;
        foreach (var file in new[] { exe + ".old", exe + ".download" })
            try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    static async Task<bool> TryUpdateAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"PrintNetwork/{DisplayVersion}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.GetAsync(LatestReleaseApi, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false; // chưa có release nào
        response.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var tag = release.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest) || Normalize(latest) <= CurrentVersion) return false;

        var asset = release.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind != JsonValueKind.Object)
        {
            Log.Warn($"Tự cập nhật: release {tag} không có file {AssetName}");
            return false;
        }
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;

        var exe = ExePath!;
        var download = exe + ".download";
        Log.Info($"Tự cập nhật: đang tải bản {tag}");
        await using (var source = await http.GetStreamAsync(url, ct))
        await using (var target = File.Create(download))
            await source.CopyToAsync(target, ct);

        try
        {
            if (digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
            {
                await using var fs = File.OpenRead(download);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("file tải về sai mã SHA-256");
            }
            if (FileVersionInfo.GetVersionInfo(download).ProductName != "PrintNetwork")
                throw new InvalidDataException("file tải về không phải PrintNetwork");
        }
        catch
        {
            File.Delete(download);
            throw;
        }

        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(download, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        Log.Info($"Tự cập nhật: đã cài bản {tag}, có hiệu lực khi khởi động lại");
        return true;
    }

    /// <summary>Chạy bản mới (ẩn dưới khay); bản mới chờ phiên này thoát hẳn rồi mới chạy.</summary>
    public static bool Restart()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ExePath!, $"--minimized {AfterUpdateArg}") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Tự cập nhật: không khởi động lại được – " + ex.Message);
            return false;
        }
    }
}
