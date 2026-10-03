using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using ResetMe.Core.Logging;

namespace ResetMe.Platform.Logging;

/// <summary>
/// Local structured log (PRD §30): one JSON object per line in <c>logs/resetme-YYYY-MM-DD.log</c>,
/// written off the calling thread, sanitized, with day-based retention. Safe for several ResetMe
/// processes writing the same file (short exclusive appends with retry).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FilePrefix = "resetme-";
    public const string FileExtension = ".log";

    private const int MaxBatch = 256;
    private const int MaxFileBytes = 20 * 1024 * 1024;

    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly int _retentionDays;
    private readonly TimeProvider _time;
    private readonly Channel<LogEntry> _queue = Channel.CreateBounded<LogEntry>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Logs are never embedded in HTML; keep <email>, + and non-ASCII readable.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Task _writer;
    private DateOnly _cleanedFor;

    public FileLoggerProvider(string directory, LogLevel minimum, int retentionDays, TimeProvider time)
    {
        _directory = directory;
        _minimum = minimum;
        _retentionDays = Math.Max(1, retentionDays);
        _time = time;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Directory => _directory;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public static string FileNameFor(DateOnly day) =>
        $"{FilePrefix}{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{FileExtension}";

    /// <summary>Flushes pending entries (bounded wait) and stops the writer.</summary>
    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(3));
    }

    internal bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= _minimum;

    internal void Enqueue(LogEntry entry) => _queue.Writer.TryWrite(entry);

    private async Task WriteLoopAsync()
    {
        var batch = new List<LogEntry>(MaxBatch);
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < MaxBatch && reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            try
            {
                Write(batch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the tool down.
            }
        }
    }

    private void Write(List<LogEntry> batch)
    {
        foreach (var group in batch.GroupBy(e => DateOnly.FromDateTime(e.Timestamp.DateTime)))
        {
            EnsureDirectory();
            CleanUpOnce(group.Key);

            var path = Path.Combine(_directory, FileNameFor(group.Key));
            var buffer = new StringBuilder();
            foreach (var entry in group)
            {
                buffer.Append(Format(entry)).Append('\n');
            }

            AppendWithRetry(path, Encoding.UTF8.GetBytes(buffer.ToString()));
        }
    }

    private static void AppendWithRetry(string path, byte[] bytes)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var created = !File.Exists(path);
                // Exclusive while appending: with shared access, two processes could both write at the
                // same end offset and overwrite each other's lines (seen on Linux, where sharing maps to flock).
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None))
                {
                    if (stream.Length > MaxFileBytes)
                    {
                        return; // Daily cap: drop rather than fill the disk.
                    }

                    stream.Write(bytes);
                }

                if (created && !OperatingSystem.IsWindows())
                {
                    // On Windows the restricted directory ACL is inherited.
                    FilePermissions.RestrictToCurrentUser(path, isDirectory: false);
                }

                return;
            }
            catch (IOException) when (attempt < 100)
            {
                // Another ResetMe process is appending right now.
                Thread.Sleep(15);
            }
        }
    }

    private void EnsureDirectory()
    {
        if (!System.IO.Directory.Exists(_directory))
        {
            System.IO.Directory.CreateDirectory(_directory);
            FilePermissions.RestrictToCurrentUser(_directory, isDirectory: true);
        }
    }

    private void CleanUpOnce(DateOnly today)
    {
        if (_cleanedFor == today)
        {
            return;
        }

        _cleanedFor = today;
        var oldest = today.AddDays(-(_retentionDays - 1));
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, $"{FilePrefix}*{FileExtension}"))
        {
            var name = Path.GetFileNameWithoutExtension(file)[FilePrefix.Length..];
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    internal static string Format(LogEntry entry)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("ts", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("level", entry.Level.ToString());
            json.WriteString("category", entry.Category);
            if (entry.EventId.Id != 0)
            {
                json.WriteNumber("event", entry.EventId.Id);
            }

            json.WriteString("msg", LogSanitizer.Redact(entry.Message));

            if (entry.Properties.Count > 0)
            {
                json.WriteStartObject("props");
                foreach (var (key, value) in entry.Properties)
                {
                    WriteValue(json, key, value);
                }

                json.WriteEndObject();
            }

            if (entry.Exception is not null)
            {
                json.WriteString("exception", LogSanitizer.Redact(entry.Exception.ToString()));
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case bool b:
                json.WriteBoolean(key, b);
                break;
            case int or long or short or byte:
                json.WriteNumber(key, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                json.WriteNumber(key, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dto:
                json.WriteString(key, dto.ToString("O", CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteString(key, LogSanitizer.Redact(Convert.ToString(value, CultureInfo.InvariantCulture)));
                break;
        }
    }

    internal sealed record LogEntry(
        DateTimeOffset Timestamp,
        LogLevel Level,
        string Category,
        EventId EventId,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Properties,
        Exception? Exception);

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            IReadOnlyList<KeyValuePair<string, object?>> properties = state is IReadOnlyList<KeyValuePair<string, object?>> list
                ? [.. list.Where(p => p.Key != "{OriginalFormat}")]
                : [];

            provider.Enqueue(new LogEntry(
                provider._time.GetLocalNow(),
                logLevel,
                category,
                eventId,
                formatter(state, exception),
                properties,
                exception));
        }
    }
}
