using PrintNetwork.Core;
using PrintNetwork.UI;

namespace PrintNetwork;

static class Program
{
    const string MutexName = @"Local\PrintNetwork.Main";
    const string ShowEventName = @"Local\PrintNetwork.Show";

    /// <summary>
    /// PrintNetwork.exe                 → mở giao diện (một phiên duy nhất)
    /// PrintNetwork.exe --minimized     → chạy ẩn dưới khay (dùng khi khởi động cùng Windows)
    /// PrintNetwork.exe --send a.pdf …  → in nhanh các file tới host/máy in đã lưu (menu Send to)
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("Lỗi không mong muốn", e.Exception);
            MessageBox.Show(e.Exception.Message, "PrintNetwork", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log.Error("Lỗi nghiêm trọng", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Lỗi tác vụ nền", e.Exception);
            e.SetObserved();
        };

        var settings = AppSettings.Load();

        var sendIndex = Array.FindIndex(args, a => a.Equals("--send", StringComparison.OrdinalIgnoreCase));
        if (sendIndex >= 0)
        {
            var files = args.Skip(sendIndex + 1).Where(File.Exists).ToList();
            if (files.Count > 0) Application.Run(new QuickSendForm(settings, files));
            return;
        }

        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance && args.Contains(AutoUpdater.AfterUpdateArg, StringComparer.OrdinalIgnoreCase))
            isFirstInstance = WaitForPreviousInstance(mutex);
        if (!isFirstInstance)
        {
            // Đã có một phiên đang chạy (có thể đang ẩn dưới khay) → bảo nó hiện lên.
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch
            {
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var form = new MainForm(settings, args.Contains("--minimized", StringComparer.OrdinalIgnoreCase));
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => form.RequestShow(), null, Timeout.Infinite, false);
        Application.Run(form);
    }

    /// <summary>Sau khi tự cập nhật: chờ phiên cũ thoát (nhả mutex) rồi chạy tiếp.</summary>
    static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            // Phiên cũ thoát mà không nhả mutex – coi như đã có quyền.
            return true;
        }
    }
}
