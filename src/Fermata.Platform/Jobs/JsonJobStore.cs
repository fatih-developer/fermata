using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fermata.Core.Jobs;

namespace Fermata.Platform.Jobs;

/// <summary>
/// <c>jobs/&lt;id&gt;/job.json</c> (atomic replace with a last-known-good <c>.bak</c>, like state.json),
/// <c>events.ndjson</c> (append only) and <c>requests/</c> (one file per user command).
/// </summary>
public sealed class JsonJobStore : IJobStore
{
    private const string JobFile = "job.json";
    private const string EventsFile = "events.ndjson";
    private const string RequestsFolder = "requests";

    private readonly string _root;

    public JsonJobStore(string root)
    {
        _root = root;
    }

    public string Root => _root;

    public string DirectoryOf(string id) => Path.Combine(_root, Validate(id));

    public bool Exists(string id) => Directory.Exists(DirectoryOf(id));

    public IReadOnlyList<Job> List()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(_root)
            .Select(dir => Get(Path.GetFileName(dir)))
            .OfType<Job>()
            .OrderBy(job => job.CreatedAt)
            .ToList();
    }

    public Job? Get(string id)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var path = Path.Combine(DirectoryOf(id), JobFile);
        return TryRead(path) ?? TryRead(path + ".bak");
    }

    public void Save(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var directory = DirectoryOf(job.Id);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            FilePermissions.RestrictToCurrentUser(directory, isDirectory: true);
        }

        var path = Path.Combine(directory, JobFile);
        var temp = Path.Combine(directory, $".job.{Environment.ProcessId}.tmp");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, job, JobJsonContext.Default.Job);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path) && TryRead(path) is not null)
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void AppendEvent(string id, JobEvent jobEvent)
    {
        ArgumentNullException.ThrowIfNull(jobEvent);
        var directory = DirectoryOf(id);
        Directory.CreateDirectory(directory);
        var line = JsonSerializer.Serialize(jobEvent, JobEventJsonContext.Default.JobEvent) + "\n";

        // Hooks of several Claude sessions may append at once; retry briefly on a sharing violation.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path.Combine(directory, EventsFile), FileMode.Append, FileAccess.Write, FileShare.Read);
                var bytes = Encoding.UTF8.GetBytes(line);
                stream.Write(bytes);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(15);
            }
        }
    }

    public IReadOnlyList<JobEvent> ReadEvents(string id, int tail)
    {
        var path = Path.Combine(DirectoryOf(id), EventsFile);
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<JobEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (JsonSerializer.Deserialize(line, JobEventJsonContext.Default.JobEvent) is { } parsed)
                {
                    events.Add(parsed);
                }
            }
            catch (JsonException)
            {
                // A torn last line after a crash; the rest stays readable.
            }
        }

        return tail <= 0 || events.Count <= tail ? events : events[^tail..];
    }

    public void Enqueue(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var directory = Path.Combine(DirectoryOf(request.JobId), RequestsFolder);
        Directory.CreateDirectory(directory);
        var name = $"{request.RequestedAt.UtcTicks:D20}-{request.Id}.json";
        var temp = Path.Combine(directory, "." + name + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(request, JobJsonContext.Default.JobRequest));
        File.Move(temp, Path.Combine(directory, name));
    }

    public bool HasRequests() =>
        Directory.Exists(_root)
        && Directory.EnumerateDirectories(_root)
            .Any(dir => Directory.Exists(Path.Combine(dir, RequestsFolder))
                && Directory.EnumerateFiles(Path.Combine(dir, RequestsFolder), "*.json").Any());

    public IReadOnlyList<JobRequest> TakeRequests()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var requests = new List<JobRequest>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var folder = Path.Combine(dir, RequestsFolder);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    if (JsonSerializer.Deserialize(File.ReadAllText(file), JobJsonContext.Default.JobRequest) is { } request)
                    {
                        requests.Add(request);
                    }
                }
                catch (JsonException)
                {
                }

                File.Delete(file);
            }
        }

        return requests.OrderBy(r => r.RequestedAt).ToList();
    }

    /// <summary>True while the request file still waits for the scheduler.</summary>
    public bool IsPending(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return File.Exists(Path.Combine(DirectoryOf(request.JobId), RequestsFolder, $"{request.RequestedAt.UtcTicks:D20}-{request.Id}.json"));
    }

    public static bool IsValidId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 64 && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static string Validate(string id) =>
        IsValidId(id) ? id : throw new ArgumentException($"'{id}' is not a valid job id.", nameof(id));

    private static Job? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), JobJsonContext.Default.Job) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(JobRequest))]
[JsonSerializable(typeof(CheckpointRecord))]
[JsonSerializable(typeof(QuotaSnapshot))]
internal sealed partial class JobJsonContext : JsonSerializerContext
{
}

/// <summary>Events are single lines, so they get their own compact context.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JobEvent))]
internal sealed partial class JobEventJsonContext : JsonSerializerContext
{
}
