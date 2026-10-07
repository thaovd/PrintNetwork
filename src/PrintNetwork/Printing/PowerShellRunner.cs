using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace PrintNetwork.Printing;

public sealed record PsResult(bool Ok, string Output);

/// <summary>
/// Chạy script PowerShell (Windows PowerShell 5.1 có sẵn module PrintManagement).
/// Kết quả được ghi ra file tạm nên dùng chung cho cả chế độ thường và chế độ Administrator (UAC).
/// </summary>
public static class PowerShellRunner
{
    public static bool IsAdmin
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Chuỗi PowerShell trong nháy đơn. PowerShell coi cả ‘ ’ ‚ ‛ là nháy đơn nên phải nhân đôi hết.</summary>
    public static string Q(string value)
    {
        var sb = new StringBuilder("'");
        foreach (var c in value)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    public static PsResult Run(string script, bool elevated = false, int timeoutMs = 600_000)
    {
        var dir = Path.Combine(Path.GetTempPath(), "PrintNetwork");
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString("N");
        var ps1 = Path.Combine(dir, id + ".ps1");
        var outFile = Path.Combine(dir, id + ".out");

        // Invoke-Native: gọi exe (pnputil, netsh…) mà không bị PowerShell 5.1 biến stderr thành exception.
        var wrapped = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $__out = {{Q(outFile)}}
            function Invoke-Native {
                param([string]$Exe, [string[]]$ArgList)
                $old = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                try { $o = & $Exe @ArgList 2>&1 | ForEach-Object { "$_" } }
                finally { $ErrorActionPreference = $old }
                $script:NativeExit = $LASTEXITCODE
                $o
            }
            try {
                & {
            {{script}}
                } *>&1 | ForEach-Object { "$_" } | Set-Content -LiteralPath $__out -Encoding UTF8
                exit 0
            }
            catch {
                Add-Content -LiteralPath $__out -Encoding UTF8 -Value ("LỖI: " + $_.Exception.Message)
                exit 1
            }
            """;
        File.WriteAllText(ps1, wrapped, new UTF8Encoding(true));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"");
        if (elevated && !IsAdmin)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
        }

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được PowerShell");
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
                return new PsResult(false, "Hết thời gian chờ PowerShell");
            }
            var output = File.Exists(outFile) ? File.ReadAllText(outFile).Trim() : "";
            return new PsResult(p.ExitCode == 0, output);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new PsResult(false, "Bạn đã từ chối cấp quyền Administrator (UAC).");
        }
        finally
        {
            TryDelete(ps1);
            TryDelete(outFile);
        }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
