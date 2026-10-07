namespace PrintNetwork.Core;

/// <summary>Ghi nhật ký ra %AppData%\PrintNetwork\logs\yyyy-MM-dd.log và phát sự kiện cho giao diện.</summary>
public static class Log
{
    static readonly object Gate = new();

    public static string Dir => Path.Combine(AppSettings.Dir, "logs");

    public static event Action<string>? Written;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("CẢNH BÁO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("LỖI", ex is null ? message : $"{message}: {ex.Message}", ex);

    static void Write(string level, string message, Exception? ex = null)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var text = ex is null ? line : line + Environment.NewLine + ex;
                File.AppendAllText(Path.Combine(Dir, $"{DateTime.Now:yyyy-MM-dd}.log"), text + Environment.NewLine);
            }
        }
        catch
        {
            // Không để lỗi ghi log làm hỏng luồng chính.
        }
        Written?.Invoke(line);
    }
}
