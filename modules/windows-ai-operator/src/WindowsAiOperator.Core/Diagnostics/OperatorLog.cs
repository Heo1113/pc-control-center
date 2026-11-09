using System.Globalization;
using System.Text;
using WindowsAiOperator.Core.Security;

namespace WindowsAiOperator.Core.Diagnostics;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public interface IOperatorLog
{
    void Write(LogLevel level, string category, string message, Exception? exception = null);
}

public static class OperatorLogExtensions
{
    public static void Info(this IOperatorLog log, string category, string message) => log.Write(LogLevel.Info, category, message);

    public static void Warn(this IOperatorLog log, string category, string message, Exception? ex = null) =>
        log.Write(LogLevel.Warning, category, message, ex);

    public static void Error(this IOperatorLog log, string category, string message, Exception? ex = null) =>
        log.Write(LogLevel.Error, category, message, ex);

    public static void Debug(this IOperatorLog log, string category, string message) => log.Write(LogLevel.Debug, category, message);
}

public sealed class NullOperatorLog : IOperatorLog
{
    public static readonly NullOperatorLog Instance = new();

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
    }
}

/// <summary>Keeps the last entries in memory (tests and the in-app log view).</summary>
public sealed class MemoryOperatorLog : IOperatorLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        var line = $"{level} [{category}] {Redactor.Redact(message)}" + (exception is null ? string.Empty : " | " + Redactor.Redact(exception.Message));
        lock (_gate)
        {
            _entries.Add(line);
            if (_entries.Count > 2000)
            {
                _entries.RemoveRange(0, 500);
            }
        }
    }
}

/// <summary>
/// Daily log files in the data folder. Every message is redacted first; files are capped in size and old files are
/// removed after <see cref="RetentionDays"/> days. Logging never throws into the caller.
/// </summary>
public sealed class FileOperatorLog : IOperatorLog, IDisposable
{
    public const int RetentionDays = 14;
    private const long MaxFileBytes = 20L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minimum;
    private StreamWriter? _writer;
    private string? _currentPath;
    private bool _capped;

    public FileOperatorLog(string directory, LogLevel minimum = LogLevel.Info)
    {
        _directory = directory;
        _minimum = minimum;
        Directory.CreateDirectory(directory);
        TryCleanup();
    }

    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimum)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.Now;
            var builder = new StringBuilder();
            builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                   .Append(' ').Append(level.ToString().ToUpperInvariant())
                   .Append(" [").Append(category).Append("] ")
                   .Append(Redactor.Sanitize(message, 4000).Replace('\n', ' '));
            if (exception is not null)
            {
                builder.Append(" | ").Append(exception.GetType().Name).Append(": ")
                       .Append(Redactor.Sanitize(exception.Message, 1000).Replace('\n', ' '));
            }

            lock (_gate)
            {
                var path = Path.Combine(_directory, $"wao-{now:yyyyMMdd}.log");
                if (!string.Equals(path, _currentPath, StringComparison.Ordinal))
                {
                    _writer?.Dispose();
                    _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true,
                    };
                    _currentPath = path;
                    _capped = false;
                }

                if (_capped || _writer is null)
                {
                    return;
                }

                if (_writer.BaseStream.Length > MaxFileBytes)
                {
                    _writer.WriteLine("(로그 파일 크기 상한에 도달해 오늘 기록을 멈춥니다)");
                    _capped = true;
                    return;
                }

                _writer.WriteLine(builder.ToString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging must never break automation or the UI.
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

    private void TryCleanup()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "wao-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
