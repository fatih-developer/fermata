using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fermata.Core.Jobs;
using Fermata.Platform;

namespace Fermata.Claude;

public sealed record ClaudeWindow(double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>
/// The latest <c>rate_limits</c> from any Claude Code session's status line input. Claude Code has
/// no API for its quota outside a running session, so this is all Fermata knows.
/// </summary>
public sealed record ClaudeQuotaFile
{
    public required DateTimeOffset CapturedAt { get; init; }

    public string? SessionId { get; init; }

    public string? Cwd { get; init; }

    public ClaudeWindow? FiveHour { get; init; }

    public ClaudeWindow? SevenDay { get; init; }
}

/// <summary>What the hooks saw of one Claude Code session.</summary>
public sealed record ClaudeSessionRecord
{
    public required string SessionId { get; init; }

    public string? Cwd { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Latest SessionStart (startup, resume, clear…): a resumed session closes the limit episode.</summary>
    public DateTimeOffset? LastStartAt { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public string? EndReason { get; init; }

    /// <summary>Last <c>notification_type</c> and when it came.</summary>
    public string? LastNotification { get; init; }

    public DateTimeOffset? LastNotificationAt { get; init; }

    /// <summary>A turn ended normally (Stop hook).</summary>
    public DateTimeOffset? LastStopAt { get; init; }

    /// <summary>A turn failed on the usage limit (StopFailure with error rate_limit).</summary>
    public DateTimeOffset? LimitHitAt { get; init; }

    /// <summary>Claude's own auto-resume ran (quota_auto_resume_fired).</summary>
    public DateTimeOffset? AutoResumeFiredAt { get; init; }

    /// <summary>Claude gave up resuming on its own (quota_auto_resume_stale/disabled).</summary>
    public DateTimeOffset? AutoResumeGaveUpAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The limit episode is still open: hit, and neither resumed natively nor restarted since.</summary>
    public bool LimitOpen =>
        LimitHitAt is { } hit
        && !(AutoResumeFiredAt > hit)
        && !(LastStopAt > hit)
        && !(LastStartAt > hit);
}

/// <summary>Where the Claude integration keeps its files (inside the Fermata data root).</summary>
public sealed class ClaudeStateStore
{
    private readonly AppPaths _paths;

    public ClaudeStateStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string QuotaFile => _paths.ClaudeQuotaFile;

    /// <summary>The status line command that ran before Fermata's wrapper.</summary>
    public string PreviousStatusLineFile => Path.Combine(_paths.Root, "claude-statusline.json");

    public ClaudeQuotaFile? LoadQuota() => Read(QuotaFile, ClaudeJsonContext.Default.ClaudeQuotaFile);

    public void SaveQuota(ClaudeQuotaFile quota) => Write(QuotaFile, quota, ClaudeJsonContext.Default.ClaudeQuotaFile);

    public ClaudeSessionRecord? LoadSession(string sessionId) =>
        IsSafe(sessionId) ? Read(SessionPath(sessionId), ClaudeJsonContext.Default.ClaudeSessionRecord) : null;

    public IReadOnlyList<ClaudeSessionRecord> Sessions()
    {
        var dir = _paths.ClaudeSessionsDirectory;
        return Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.json").Select(f => Read(f, ClaudeJsonContext.Default.ClaudeSessionRecord)).OfType<ClaudeSessionRecord>().ToList()
            : [];
    }

    /// <summary>Read-modify-write of one session's record (hooks of one session run one at a time).</summary>
    public ClaudeSessionRecord UpdateSession(string sessionId, DateTimeOffset now, Func<ClaudeSessionRecord, ClaudeSessionRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!IsSafe(sessionId))
        {
            throw new ArgumentException("Unexpected session id.", nameof(sessionId));
        }

        var current = LoadSession(sessionId) ?? new ClaudeSessionRecord { SessionId = sessionId, LastSeenAt = now };
        var updated = change(current) with { LastSeenAt = now };
        Write(SessionPath(sessionId), updated, ClaudeJsonContext.Default.ClaudeSessionRecord);
        return updated;
    }

    public string? LoadPreviousStatusLine()
    {
        try
        {
            return File.Exists(PreviousStatusLineFile)
                ? JsonNode.Parse(File.ReadAllText(PreviousStatusLineFile))?["command"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    public void SavePreviousStatusLine(JsonNode? statusLine)
    {
        _paths.EnsureRoot();
        var file = new JsonObject
        {
            ["command"] = statusLine?["command"]?.GetValue<string>(),
            ["original"] = statusLine?.DeepClone(),
        };
        File.WriteAllText(PreviousStatusLineFile, file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The whole statusLine object as it was before install (for uninstall).</summary>
    public JsonNode? LoadOriginalStatusLine()
    {
        try
        {
            return File.Exists(PreviousStatusLineFile) ? JsonNode.Parse(File.ReadAllText(PreviousStatusLineFile))?["original"]?.DeepClone() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public void ForgetPreviousStatusLine() => File.Delete(PreviousStatusLineFile);

    /// <summary>The provider-neutral quota, with a reported limit when an open limit episode exists.</summary>
    public QuotaSnapshot? Quota(DateTimeOffset now)
    {
        var quota = LoadQuota();
        var limit = Sessions().Any(s => s.LimitOpen && s.LimitHitAt > now.AddDays(-7));
        if (quota is null && !limit)
        {
            return null;
        }

        var windows = new List<QuotaWindow>();
        if (quota?.FiveHour is { } five)
        {
            windows.Add(new QuotaWindow(QuotaWindowKind.FiveHour, five.UsedPercent, five.ResetsAt));
        }

        if (quota?.SevenDay is { } week)
        {
            windows.Add(new QuotaWindow(QuotaWindowKind.Weekly, week.UsedPercent, week.ResetsAt));
        }

        return new QuotaSnapshot
        {
            Windows = windows,
            UsageAllowed = null,
            LimitReported = limit,
            Source = "claude-statusline",
            CapturedAt = quota?.CapturedAt ?? now,
        };
    }

    /// <summary>
    /// <c>rate_limits</c> from the status line input. Accepts the documented
    /// <c>used_percentage</c> and the API's <c>utilization</c>; <c>resets_at</c> in Unix seconds or ISO 8601.
    /// </summary>
    public static ClaudeWindow? ParseWindow(JsonNode? node)
    {
        if (node is not JsonObject window)
        {
            return null;
        }

        var used = Number(window["used_percentage"]) ?? (Number(window["utilization"]) is { } u ? (u <= 1 ? u * 100 : u) : null);
        if (used is null)
        {
            return null;
        }

        DateTimeOffset? resets = null;
        if (Number(window["resets_at"]) is { } seconds)
        {
            resets = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
        else if (window["resets_at"] is JsonValue text && text.TryGetValue<string>(out var iso)
            && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            resets = parsed;
        }

        return new ClaudeWindow(used.Value, resets);
    }

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private string SessionPath(string sessionId) => Path.Combine(_paths.ClaudeSessionsDirectory, sessionId + ".json");

    private static bool IsSafe(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Environment.CurrentManagedThreadId}.tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(value, type));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 10)
            {
                Thread.Sleep(20); // a reader has it open (Windows)
            }
        }
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ClaudeQuotaFile))]
[JsonSerializable(typeof(ClaudeSessionRecord))]
internal sealed partial class ClaudeJsonContext : JsonSerializerContext
{
}
