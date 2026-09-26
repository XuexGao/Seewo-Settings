using System.Collections.Concurrent;
using System.Text;
using SeewoAssistant.Core.Abstractions;

namespace SeewoAssistant.Services;

/// <summary>
/// Logger that writes to a rolling file and keeps a bounded in-memory tail for the
/// diagnostics panel.
/// </summary>
/// <remarks>
/// Writes are queued and flushed by a single background worker so logging from a
/// registry watch thread or a scheduler tick never blocks on disk I/O. The file is
/// rolled at 2 MB and five files are kept, which bounds disk use at 10 MB.
/// </remarks>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxRolledFiles = 5;
    private const int MaxMemoryEntries = 1000;

    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), 4096);
    private readonly ConcurrentQueue<string> _recent = new();
    private readonly Thread _worker;
    private readonly string _logDirectory;
    private readonly string _logFilePath;
    private readonly object _fileGate = new();

    private volatile bool _disposed;

    public FileLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        _logFilePath = Path.Combine(logDirectory, "seewo-assistant.log");

        _worker = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "SeewoAssistant.LogWriter",
        };
        _worker.Start();
    }

    /// <summary>Full path of the active log file.</summary>
    public string LogFilePath => _logFilePath;

    /// <summary>The most recent log lines, newest last.</summary>
    public IReadOnlyList<string> RecentEntries => _recent.ToArray();

    /// <summary>Raised for every line, so the UI can append live.</summary>
    public event EventHandler<string>? EntryWritten;

    public void Debug(string message) => Write("DEBUG", message, null);

    public void Info(string message) => Write("INFO ", message, null);

    public void Warn(string message) => Write("WARN ", message, null);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (_disposed)
        {
            return;
        }

        var builder = new StringBuilder()
            .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(level).Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine().Append("        ").Append(exception.GetType().Name)
                .Append(": ").Append(exception.Message);

            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            {
                builder.AppendLine().Append(exception.StackTrace);
            }
        }

        var line = builder.ToString();

        // The queue is bounded; dropping log lines is far better than blocking the
        // caller or growing without limit.
        _queue.TryAdd(line);

        _recent.Enqueue(line);
        while (_recent.Count > MaxMemoryEntries)
        {
            _recent.TryDequeue(out _);
        }

        EntryWritten?.Invoke(this, line);
    }

    private void ProcessQueue()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                AppendToFile(line);
            }
            catch (IOException)
            {
                // Disk full or locked. Nothing useful to do from a log writer.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private void AppendToFile(string line)
    {
        lock (_fileGate)
        {
            Directory.CreateDirectory(_logDirectory);
            RollIfNeeded();

            File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    private void RollIfNeeded()
    {
        var info = new FileInfo(_logFilePath);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        // Shift .4 -> .5, .3 -> .4, ... then the active file -> .1. The oldest is
        // discarded by overwriting it during the shift.
        var oldest = _logFilePath + $".{MaxRolledFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = MaxRolledFiles - 1; i >= 1; i--)
        {
            var source = _logFilePath + $".{i}";
            if (File.Exists(source))
            {
                File.Move(source, _logFilePath + $".{i + 1}", overwrite: true);
            }
        }

        File.Move(_logFilePath, _logFilePath + ".1", overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();

        // Give the writer a moment to drain, but do not hang shutdown on it.
        _worker.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }
}
