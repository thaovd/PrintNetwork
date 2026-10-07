using System.Drawing.Printing;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using static PrintNetwork.Printing.PowerShellRunner;

namespace PrintNetwork.Printing;

public sealed class WinPrinter
{
    public string Name { get; set; } = "";
    public string DriverName { get; set; } = "";
    public string PortName { get; set; } = "";
    public bool Shared { get; set; }
}

public sealed record DriverPackage(string Dir, string InfPath, string DriverName);

/// <summary>Khai báo máy in, cổng và driver với Windows (qua module PrintManagement + pnputil).</summary>
public static class PrinterManager
{
    const string ManifestName = "printnetwork-driver.json";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "PrintNetwork";
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // ---------- Truy vấn (không cần quyền Administrator) ----------

    public static List<string> InstalledPrinterNames() => PrinterSettings.InstalledPrinters.Cast<string>().ToList();

    public static string? DefaultPrinterName()
    {
        try
        {
            var ps = new PrinterSettings();
            return ps.IsDefaultPrinter ? ps.PrinterName : null;
        }
        catch
        {
            return null;
        }
    }

    public static List<WinPrinter> GetPrinters() =>
        QueryJson<WinPrinter>("ConvertTo-Json -Compress -InputObject @(Get-Printer | Select-Object Name,DriverName,PortName,Shared)");

    public static List<string> GetDrivers() =>
        QueryJson<string>("ConvertTo-Json -Compress -InputObject @(Get-PrinterDriver | Select-Object -ExpandProperty Name | Sort-Object -Unique)");

    public static List<string> GetPorts() =>
        QueryJson<string>("ConvertTo-Json -Compress -InputObject @(Get-PrinterPort | Select-Object -ExpandProperty Name | Sort-Object)");

    static List<T> QueryJson<T>(string script)
    {
        var r = Run(script);
        if (!r.Ok) throw new InvalidOperationException(r.Output);
        return string.IsNullOrWhiteSpace(r.Output) ? [] : JsonSerializer.Deserialize<List<T>>(r.Output, JsonOptions) ?? [];
    }

    // ---------- Script cần quyền Administrator (gộp lại để chỉ hỏi UAC một lần) ----------

    /// <summary>
    /// Bảo đảm driver <paramref name="driver"/> có trên máy: nếu có file INF thì nạp vào Driver Store bằng pnputil,
    /// sau đó Add-PrinterDriver (driver có sẵn của Windows cũng cài được theo tên mà không cần INF).
    /// </summary>
    public static string ScriptInstallDriver(string driver, string? infPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"$drv = {Q(driver)}");
        sb.AppendLine("if (Get-PrinterDriver -Name $drv -ErrorAction SilentlyContinue) { Write-Output \"Driver da co san: $drv\" }");
        sb.AppendLine("else {");
        if (infPath is not null)
        {
            sb.AppendLine($"  $inf = {Q(infPath)}");
            sb.AppendLine("  Write-Output \"Nap driver vao Driver Store: $inf\"");
            sb.AppendLine("  Invoke-Native pnputil.exe @('/add-driver', $inf, '/install')");
            sb.AppendLine("  if ($script:NativeExit -notin 0, 259, 3010) { throw \"pnputil bao loi (ma $($script:NativeExit)) khi nap driver\" }");
        }
        sb.AppendLine("  try { Add-PrinterDriver -Name $drv }");
        sb.AppendLine("  catch {");
        if (infPath is not null)
        {
            sb.AppendLine("    Write-Output \"Add-PrinterDriver loi: $($_.Exception.Message) - thu bang printui\"");
            sb.AppendLine("    Invoke-Native rundll32.exe @('printui.dll,PrintUIEntry', '/ia', '/m', $drv, '/f', $inf)");
            sb.AppendLine("    Start-Sleep -Seconds 3");
            sb.AppendLine("    if (-not (Get-PrinterDriver -Name $drv -ErrorAction SilentlyContinue)) { throw \"Không cài được driver '$drv' từ file INF. Kiểm tra đúng tên model và đúng bản 64-bit.\" }");
        }
        else
        {
            sb.AppendLine("    throw \"Máy này chưa có driver '$drv' và Windows cũng không có sẵn. Hãy chọn file INF của driver (tải từ trang hãng).\"");
        }
        sb.AppendLine("  }");
        sb.AppendLine("  Write-Output \"Da cai driver: $drv\"");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string TcpPortName(string host, int port) => port == 9100 ? $"IP_{host}" : $"IP_{host}_{port}";

    /// <summary>Tạo cổng Standard TCP/IP (RAW) rồi thêm máy in dùng cổng đó.</summary>
    public static string ScriptAddTcpPrinter(string name, string host, int port, string driver)
    {
        var portName = TcpPortName(host, port);
        return $$"""
            $portName = {{Q(portName)}}
            if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
                Add-PrinterPort -Name $portName -PrinterHostAddress {{Q(host)}} -PortNumber {{port}}
                Write-Output "Da tao cong $portName"
            }
            {{ScriptAddPrinter(name, portName, driver)}}
            """;
    }

    public static string ScriptAddPrinter(string name, string portName, string driver) => $$"""
        $pn = {{Q(name)}}
        if (Get-Printer -Name $pn -ErrorAction SilentlyContinue) {
            Set-Printer -Name $pn -DriverName {{Q(driver)}} -PortName {{Q(portName)}}
            Write-Output "Da cap nhat may in: $pn"
        } else {
            Add-Printer -Name $pn -DriverName {{Q(driver)}} -PortName {{Q(portName)}}
            Write-Output "Da them may in: $pn"
        }
        """;

    public static string ScriptRemovePrinter(string name) => $$"""
        Remove-Printer -Name {{Q(name)}}
        Write-Output "Da xoa may in"
        """;

    public static PsResult OpenFirewall(int controlPort, int discoveryPort, IEnumerable<int> rawPorts)
    {
        var tcp = string.Join(",", new[] { controlPort }.Concat(rawPorts).Distinct().OrderBy(p => p));
        var script = $$"""
            Invoke-Native netsh.exe @('advfirewall','firewall','delete','rule','name=PrintNetwork-TCP') | Out-Null
            Invoke-Native netsh.exe @('advfirewall','firewall','delete','rule','name=PrintNetwork-UDP') | Out-Null
            Invoke-Native netsh.exe @('advfirewall','firewall','add','rule','name=PrintNetwork-TCP','dir=in','action=allow','protocol=TCP','localport={{tcp}}','profile=any')
            if ($script:NativeExit -ne 0) { throw 'netsh khong tao duoc rule TCP' }
            Invoke-Native netsh.exe @('advfirewall','firewall','add','rule','name=PrintNetwork-UDP','dir=in','action=allow','protocol=UDP','localport={{discoveryPort}}','profile=any')
            if ($script:NativeExit -ne 0) { throw 'netsh khong tao duoc rule UDP' }
            Write-Output "Da mo tuong lua: TCP {{tcp}}, UDP {{discoveryPort}}"
            """;
        return Run(script, elevated: true);
    }

    // ---------- Đóng gói driver để client tải về ----------

    /// <summary>Nén thư mục gói driver (trong DriverStore) của <paramref name="driverName"/> thành file zip tạm.</summary>
    public static string ExportDriverPackage(string driverName)
    {
        var r = Run($"(Get-PrinterDriver -Name {Q(driverName)} | Select-Object -First 1).InfPath");
        var inf = r.Output.Trim();
        if (!r.Ok) throw new InvalidOperationException($"Không đọc được thông tin driver '{driverName}' trên host: {r.Output}");
        if (inf.Length == 0 || !File.Exists(inf))
            throw new InvalidOperationException(
                $"Driver '{driverName}' trên host được cài bằng bộ cài riêng của hãng (không có gói INF) nên không chuyển sang client được. Hãy cài driver này trên client bằng bộ cài của hãng.");

        var infName = Path.GetFileName(inf);
        if (infName.StartsWith("ntprint", StringComparison.OrdinalIgnoreCase) || infName.StartsWith("prnms", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{driverName}' là driver có sẵn của Windows, client tự cài được không cần tải");

        var dir = Path.GetDirectoryName(inf)!;
        var tempDir = Path.Combine(Path.GetTempPath(), "PrintNetwork");
        Directory.CreateDirectory(tempDir);
        var zip = Path.Combine(tempDir, $"driver_{Guid.NewGuid():N}.zip");
        using (var fs = File.Create(zip))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { archive.CreateEntryFromFile(file, Path.GetRelativePath(dir, file), CompressionLevel.Fastest); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var entry = archive.CreateEntry(ManifestName);
            using var w = new StreamWriter(entry.Open());
            w.Write(JsonSerializer.Serialize(new { driverName, inf = infName }));
        }
        return zip;
    }

    public static DriverPackage ExtractDriverPackage(string zipPath)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PrintNetwork", "drv_" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(zipPath, dir);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ManifestName)));
        var inf = manifest.RootElement.GetProperty("inf").GetString()!;
        var name = manifest.RootElement.GetProperty("driverName").GetString()!;
        return new DriverPackage(dir, Path.Combine(dir, Path.GetFileName(inf)), name);
    }

    // ---------- Tiện ích hệ thống ----------

    public static bool GetAutoStart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }

    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(RunValue, false);
    }

    /// <summary>Tạo lối tắt trong menu chuột phải → Send to.</summary>
    public static PsResult CreateSendToShortcut()
    {
        var exe = Environment.ProcessPath!;
        var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "In qua LAN (PrintNetwork).lnk");
        var script = $$"""
            $s = (New-Object -ComObject WScript.Shell).CreateShortcut({{Q(lnk)}})
            $s.TargetPath = {{Q(exe)}}
            $s.Arguments = '--send'
            $s.IconLocation = {{Q(exe + ",0")}}
            $s.Save()
            Write-Output 'OK'
            """;
        return Run(script);
    }
}
