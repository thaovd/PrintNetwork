namespace PrintNetwork.Core;

/// <summary>Tùy chọn in client gửi kèm mỗi tài liệu.</summary>
public sealed class PrintOptions
{
    public int Copies { get; set; } = 1;
    /// <summary>none | long | short</summary>
    public string Duplex { get; set; } = "none";
    public bool Color { get; set; } = true;
    /// <summary>Phạm vi trang, ví dụ "1-3,5". Trống = tất cả.</summary>
    public string Pages { get; set; } = "";
    /// <summary>auto | portrait | landscape</summary>
    public string Orientation { get; set; } = "auto";
    /// <summary>fit | actual</summary>
    public string Fit { get; set; } = "fit";
    /// <summary>Tên khổ giấy (A4, A5, Letter…). Trống = mặc định của máy in.</summary>
    public string PaperSize { get; set; } = "";
}

/// <summary>Máy in host công bố cho client.</summary>
public sealed class SharedPrinterInfo
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>Cổng RAW (kiểu JetDirect) để client cài máy in vào Windows. 0 = tắt.</summary>
    public int RawPort { get; set; }
    public bool SupportsColor { get; set; }
    public bool CanDuplex { get; set; }
}

public sealed class HostInfo
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public string HostName { get; set; } = "";
    public string Machine { get; set; } = "";
    public int PrinterCount { get; set; }
    public bool NeedKey { get; set; }

    public string Endpoint => Port == Protocol.DefaultControlPort ? Address : $"{Address}:{Port}";
}

public sealed class JobRecord
{
    public const string Done = "Hoàn tất";
    public const string Failed = "Lỗi";

    public string Id { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public string Client { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Printer { get; set; } = "";
    public long Size { get; set; }
    public int Pages { get; set; }
    public string Status { get; set; } = "";
    public string? Message { get; set; }

    public JobRecord Clone() => (JobRecord)MemberwiseClone();
}

/// <summary>Gói tin điều khiển giữa client và host (JSON, có tiền tố độ dài).</summary>
public sealed class NetMessage
{
    public string Type { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Key { get; set; }
    public string? Version { get; set; }
    public string? Machine { get; set; }
    public string? User { get; set; }
    public string? HostName { get; set; }
    public string? Printer { get; set; }
    public string? FileName { get; set; }
    public string? JobId { get; set; }
    public string? Status { get; set; }
    public string? DriverName { get; set; }
    public long Size { get; set; }
    public int Port { get; set; }
    public int Count { get; set; }
    public int Pages { get; set; }
    public bool NeedKey { get; set; }
    public PrintOptions? Options { get; set; }
    public List<SharedPrinterInfo>? Printers { get; set; }

    public static NetMessage Fail(string error) => new() { Type = "error", Ok = false, Error = error };
}
