using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAiPerf.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface IAppLogger
{
    void Log(LogLevel level, string source, string message, Exception? exception = null);
}

public static class AppLoggerExtensions
{
    public static void Info(this IAppLogger log, string source, string message) => log.Log(LogLevel.Info, source, message);

    public static void Warn(this IAppLogger log, string source, string message, Exception? ex = null) => log.Log(LogLevel.Warning, source, message, ex);

    public static void Error(this IAppLogger log, string source, string message, Exception? ex = null) => log.Log(LogLevel.Error, source, message, ex);

    public static void Debug(this IAppLogger log, string source, string message) => log.Log(LogLevel.Debug, source, message);
}

public sealed class NullLogger : IAppLogger
{
    public static NullLogger Instance { get; } = new();

    public void Log(LogLevel level, string source, string message, Exception? exception = null)
    {
    }
}

/// <summary>Removes anything that looks like a credential before it reaches a log or export.</summary>
public static partial class SecretRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        var result = BearerRegex().Replace(text, "Bearer [REDACTED]");
        result = KeyValueRegex().Replace(result, m => m.Groups[1].Value + "=[REDACTED]");
        return result;
    }

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-\._~\+/=]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(api[_-]?key|x-api-key|token|password|secret)\s*[=:]\s*[^\s,;&]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();
}

/// <summary>Thread-safe daily log file with a size cap and a bounded number of files.</summary>
public sealed class FileAppLogger : IAppLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly long _maxBytesPerFile;
    private readonly int _maxFiles;
    private readonly LogLevel _minimum;
    private readonly TimeProvider _time;
    private readonly string _filePrefix;
    private StreamWriter? _writer;
    private string? _currentPath;

    public FileAppLogger(string directory, LogLevel minimum = LogLevel.Info, long maxBytesPerFile = 10 * 1024 * 1024, int maxFiles = 14, TimeProvider? time = null, string filePrefix = "lapc")
    {
        _directory = directory;
        _filePrefix = filePrefix;
        _minimum = minimum;
        _maxBytesPerFile = maxBytesPerFile;
        _maxFiles = maxFiles;
        _time = time ?? TimeProvider.System;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
    }

    public string? CurrentPath => _currentPath;

    public void Log(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (level < _minimum)
        {
            return;
        }

        var now = _time.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ")
            .Append(source).Append(": ").Append(SecretRedactor.Redact(message));
        if (exception is not null)
        {
            line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(SecretRedactor.Redact(exception.Message));
        }

        lock (_gate)
        {
            try
            {
                var writer = EnsureWriter(now);
                writer.WriteLine(line.ToString());
                writer.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // Logging must never take the app down.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter EnsureWriter(DateTimeOffset now)
    {
        var path = Path.Combine(_directory, $"{_filePrefix}-{now:yyyyMMdd}.log");
        if (_writer is not null && _currentPath == path && _writer.BaseStream.Length < _maxBytesPerFile)
        {
            return _writer;
        }

        _writer?.Dispose();
        if (File.Exists(path) && new FileInfo(path).Length >= _maxBytesPerFile)
        {
            var rotated = Path.Combine(_directory, $"{_filePrefix}-{now:yyyyMMdd}-{now:HHmmss}.log");
            File.Move(path, rotated, overwrite: true);
            PruneOldFiles();
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _currentPath = path;
        return _writer;
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = new DirectoryInfo(_directory).GetFiles(_filePrefix + "-*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in files.Skip(_maxFiles))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}

/// <summary>Keeps the most recent log lines in memory for the diagnostics view and bundle.</summary>
public sealed class MemoryLogSink : IAppLogger
{
    private readonly object _gate = new();
    private readonly LinkedList<string> _lines = new();
    private readonly int _capacity;
    private readonly IAppLogger? _next;
    private readonly TimeProvider _time;

    public MemoryLogSink(int capacity = 500, IAppLogger? next = null, TimeProvider? time = null)
    {
        _capacity = capacity;
        _next = next;
        _time = time ?? TimeProvider.System;
    }

    public void Log(LogLevel level, string source, string message, Exception? exception = null)
    {
        _next?.Log(level, source, message, exception);
        var text = $"{_time.GetLocalNow():HH:mm:ss} [{level}] {source}: {SecretRedactor.Redact(message)}"
            + (exception is null ? "" : $" ({exception.GetType().Name}: {SecretRedactor.Redact(exception.Message)})");
        lock (_gate)
        {
            _lines.AddLast(text);
            while (_lines.Count > _capacity)
            {
                _lines.RemoveFirst();
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.ToList();
        }
    }
}
