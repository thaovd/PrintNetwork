using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PrintNetwork.Printing;

/// <summary>Gửi dữ liệu thô (đã qua driver: PCL, PostScript, ESC/P…) thẳng vào hàng đợi máy in.</summary>
public sealed class RawPrintJob : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    sealed class DOC_INFO_1
    {
        public string? pDocName;
        public string? pOutputFile;
        public string? pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern int StartDocPrinter(IntPtr hPrinter, int level, [In] DOC_INFO_1 docInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    static extern bool WritePrinter(IntPtr hPrinter, ref byte pBytes, int dwCount, out int dwWritten);

    IntPtr _handle;

    public RawPrintJob(string printer, string documentName)
    {
        if (!OpenPrinter(printer, out _handle, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Không mở được máy in '{printer}'");

        var info = new DOC_INFO_1 { pDocName = documentName, pDatatype = "RAW" };
        if (StartDocPrinter(_handle, 1, info) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            ClosePrinter(_handle);
            _handle = IntPtr.Zero;
            throw new Win32Exception(error, "Không tạo được lệnh in (StartDocPrinter)");
        }
        StartPagePrinter(_handle);
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            if (!WritePrinter(_handle, ref buffer[offset], count, out var written) || written <= 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Ghi dữ liệu tới máy in thất bại");
            offset += written;
            count -= written;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        EndPagePrinter(_handle);
        EndDocPrinter(_handle);
        ClosePrinter(_handle);
        _handle = IntPtr.Zero;
    }

    public static void PrintFile(string printer, string path, string documentName, int copies)
    {
        using var job = new RawPrintJob(printer, documentName);
        var buffer = new byte[65536];
        for (var c = 0; c < Math.Max(1, copies); c++)
        {
            using var fs = File.OpenRead(path);
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) job.Write(buffer, 0, n);
        }
    }
}
