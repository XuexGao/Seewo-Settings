using System.Runtime.InteropServices;
using System.Text;

namespace SeewoAssistant.Services;

/// <summary>
/// Writes a standalone crash report when the process is about to die.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary log is written by a background thread and is the right place for
/// normal operation, but it is a poor channel for a fatal fault: the writer thread
/// may not get to flush before the process is torn down, and the user has no idea
/// where the file lives.
/// </para>
/// <para>
/// This writes synchronously, to a fixed and easily described location, and also
/// puts the path on the clipboard so a user can paste it into a message. A crash
/// that produces no artefact is indistinguishable from a crash caused by something
/// else entirely, which is what makes a report like "it just closed" impossible to
/// act on.
/// </para>
/// </remarks>
internal static class CrashReporter
{
    /// <summary>Directory the reports are written to, under the user's local app data.</summary>
    private static string ReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SeewoAssistant",
        "crashes");

    /// <summary>
    /// Writes a report for <paramref name="exception"/> and returns its path, or null
    /// when even that failed.
    /// </summary>
    internal static string? WriteCrashReport(Exception? exception, string context)
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);

            var timestamp = DateTime.Now;
            var path = Path.Combine(
                ReportDirectory,
                $"crash-{timestamp:yyyyMMdd-HHmmss}.txt");

            var builder = new StringBuilder();

            builder.AppendLine("希沃助手崩溃报告");
            builder.AppendLine("=================");
            builder.AppendLine($"时间      : {timestamp:yyyy-MM-dd HH:mm:ss.fff}");
            builder.AppendLine($"上下文    : {context}");
            builder.AppendLine($"进程架构  : {(Environment.Is64BitProcess ? "64 位" : "32 位")}");
            builder.AppendLine($"操作系统  : {RuntimeInformation.OSDescription}");
            builder.AppendLine($"运行时    : {RuntimeInformation.FrameworkDescription}");
            builder.AppendLine($"版本      : {typeof(CrashReporter).Assembly.GetName().Version}");
            builder.AppendLine();

            if (exception is null)
            {
                builder.AppendLine("没有可用的异常对象。");
            }
            else
            {
                // Walk the whole chain: the outermost exception is usually a wrapper
                // and the useful frame is an inner one.
                var depth = 0;

                for (var current = exception; current is not null; current = current.InnerException)
                {
                    var indent = new string(' ', depth * 2);

                    builder.AppendLine($"{indent}异常类型 : {current.GetType().FullName}");
                    builder.AppendLine($"{indent}消息     : {current.Message}");

                    if (current is COMException com)
                    {
                        // An HRESULT alone is not readable; give the Win32 meaning too.
                        builder.AppendLine($"{indent}HRESULT  : 0x{com.HResult:X8}");
                    }

                    if (!string.IsNullOrWhiteSpace(current.StackTrace))
                    {
                        builder.AppendLine($"{indent}堆栈     :");
                        builder.AppendLine(current.StackTrace);
                    }

                    builder.AppendLine();
                    depth++;
                }
            }

            builder.AppendLine("最近的日志：");
            builder.AppendLine("-------------");
            builder.AppendLine(AppendRecentLogTail());

            // WriteAllText, not the queued logger: this must complete before the
            // process goes away.
            File.WriteAllText(path, builder.ToString(), Encoding.UTF8);

            TryCopyPathToClipboard(path);

            return path;
        }
        catch
        {
            // Never let crash reporting itself throw.
            return null;
        }
    }

    /// <summary>
    /// Reads the tail of the ordinary log so the crash report is self-contained.
    /// </summary>
    private static string AppendRecentLogTail()
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SeewoAssistant",
                "logs",
                "seewo-assistant.log");

            if (!File.Exists(logPath))
            {
                return "（没有日志文件）";
            }

            var lines = File.ReadAllLines(logPath);
            var tail = lines.Length <= 100 ? lines : lines[^100..];

            return string.Join(Environment.NewLine, tail);
        }
        catch (Exception ex)
        {
            return $"（无法读取日志：{ex.Message}）";
        }
    }

    /// <summary>
    /// Puts the report path on the clipboard so the user can paste it without
    /// hunting for the folder.
    /// </summary>
    private static void TryCopyPathToClipboard(string path)
    {
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch
        {
            // The clipboard may be unavailable during shutdown; not important.
        }
    }
}
