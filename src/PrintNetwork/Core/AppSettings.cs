using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrintNetwork.Core;

public sealed class SharedPrinterConfig
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public int RawPort { get; set; }
}

/// <summary>Cấu hình lưu tại %AppData%\PrintNetwork\settings.json.</summary>
public sealed class AppSettings
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrintNetwork");

    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Chỉ để chuyển cấu hình bản cũ (chọn chế độ Host/Client); bản mới không dùng.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    public int ControlPort { get; set; } = Protocol.DefaultControlPort;
    public int DiscoveryPort { get; set; } = Protocol.DefaultDiscoveryPort;

    // --- Host ---
    public string HostDisplayName { get; set; } = Environment.MachineName;
    public string HostKey { get; set; } = "";
    public bool LanOnly { get; set; } = true;
    public int PdfDpi { get; set; } = 300;
    /// <summary>Luôn thay cả danh sách (không sửa tại chỗ) vì luồng host đọc song song.</summary>
    public List<SharedPrinterConfig> SharedPrinters { get; set; } = [];
    /// <summary>Các cổng đã mở tường lửa thành công (để chỉ hỏi quyền Administrator khi có cổng mới).</summary>
    public List<int> FirewallPorts { get; set; } = [];

    // --- Client ---
    public string ClientHost { get; set; } = "";
    public string ClientKey { get; set; } = "";
    public string ClientPrinter { get; set; } = "";
    public PrintOptions ClientOptions { get; set; } = new();
    public List<string> RecentHosts { get; set; } = [];
    /// <summary>Mã truy cập theo từng host (khóa = địa chỉ host). Không có thì dùng <see cref="ClientKey"/>.</summary>
    public Dictionary<string, string> HostKeys { get; set; } = [];

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
                // Bản cũ ở chế độ Client không chạy máy chủ in: giữ nguyên bằng cách tắt các máy in đang tick chia sẻ.
                if (s.Mode == "client")
                    s.SharedPrinters = s.SharedPrinters.Select(p => new SharedPrinterConfig { Name = p.Name, RawPort = p.RawPort }).ToList();
                s.Mode = null;
                return s;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Không đọc được file cấu hình, dùng cấu hình mặc định", ex);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Không lưu được cấu hình", ex);
        }
    }
}
