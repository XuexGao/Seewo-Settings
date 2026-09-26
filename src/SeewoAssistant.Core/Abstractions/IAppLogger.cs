namespace SeewoAssistant.Core.Abstractions;

/// <summary>
/// Minimal logging seam. The UI supplies a sink that writes to both a rolling file
/// and the in-app log view; tests supply a collecting sink.
/// </summary>
public interface IAppLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

/// <summary>Logger that discards everything. Useful as a default and in tests.</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();

    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
