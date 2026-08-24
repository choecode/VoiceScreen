using System.IO;
using System.Text;

namespace VoiceScreen.App.Diagnostics;

/// <summary>
/// 进程内单一日志文件，写到 %LOCALAPPDATA%\VoiceScreen\voicescreen.log。
/// 故意做得简单：纯 append、UTF-8、单写者 lock，避免引入 Serilog/NLog 这类重依赖。
/// 用于在 UI 之外记录启动、停止、WebSocket 关闭等关键生命周期事件。
/// </summary>
public static class VoiceScreenLog
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private static readonly object _lock = new();
    private static readonly string _path = InitPath();

    public static string FilePath => _path;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static string InitPath()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceScreen");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "voicescreen.log");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                File.Move(path, Path.Combine(root, "voicescreen.previous.log"), overwrite: true);
        }
        catch
        {
            // 日志轮换失败不应阻止应用启动。
        }
        return path;
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try { File.AppendAllText(_path, line, Encoding.UTF8); }
            catch { /* 日志写失败绝不能影响主流程 */ }
        }
    }
}
