using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fermata.Core.Jobs;
using Fermata.Platform.Jobs;

namespace Fermata.Claude;

/// <summary>
/// <c>fermata claude hook &lt;event&gt;</c>: records what Claude Code reports about a session and, for
/// sessions that belong to a job, writes job events. The Stop hook may ask the agent for the
/// handoff note once per quota episode. Never throws into Claude Code: a broken hook must not get
/// in the way of the user's work.
/// </summary>
public sealed class ClaudeHookHandler
{
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly ClaudeStateStore _state;
    private readonly IJobStore _jobs;
    private readonly QuotaPolicyOptions _policy;
    private readonly CheckpointWriter? _checkpoints;
    private readonly bool _savePatch;
    private readonly TimeProvider _time;

    public ClaudeHookHandler(ClaudeStateStore state, IJobStore jobs, QuotaPolicyOptions policy, CheckpointWriter? checkpoints, bool savePatch, TimeProvider time)
    {
        _state = state;
        _jobs = jobs;
        _policy = policy;
        _checkpoints = checkpoints;
        _savePatch = savePatch;
        _time = time;
    }

    /// <summary>The hook's stdout, or null for nothing.</summary>
    public async Task<string?> HandleAsync(string hookEvent, string input, CancellationToken cancellationToken)
    {
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(input) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return null;
        }

        var sessionId = Text(payload, "session_id");
        if (sessionId is null)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var cwd = Text(payload, "cwd");
        var job = FindJob(sessionId);
        switch (hookEvent)
        {
            case "session-start":
                _state.UpdateSession(sessionId, now, s => s with
                {
                    Cwd = cwd ?? s.Cwd,
                    StartedAt = s.StartedAt ?? now,
                    LastStartAt = now,
                    EndedAt = null,
                    EndReason = null,
                    CompletedAt = null,
                });
                Event(job, now, "session-start", $"Claude session started ({Text(payload, "source") ?? "startup"}).");
                return null;

            case "session-end":
                var reason = Text(payload, "reason");
                _state.UpdateSession(sessionId, now, s => s with { EndedAt = now, EndReason = reason });
                Event(job, now, "session-end", $"Claude session ended ({reason ?? "unknown"}).");
                return null;

            case "notification":
                return Notification(payload, sessionId, job, now);

            case "stop-failure":
                var error = Text(payload, "error");
                if (error == "rate_limit")
                {
                    _state.UpdateSession(sessionId, now, s => s with { LimitHitAt = now, Cwd = cwd ?? s.Cwd });
                    Event(job, now, "limit", "Claude stopped at the usage limit.");
                }
                else
                {
                    _state.UpdateSession(sessionId, now, s => s with { Cwd = cwd ?? s.Cwd });
                    Event(job, now, "turn-failed", $"The turn failed: {error ?? "unknown error"}.");
                }

                return null;

            case "stop":
                return await StopAsync(payload, sessionId, job, now, cancellationToken).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private string? Notification(JsonObject payload, string sessionId, Job? job, DateTimeOffset now)
    {
        var type = Text(payload, "notification_type");
        _state.UpdateSession(sessionId, now, s => s with
        {
            LastNotification = type,
            LastNotificationAt = now,
            AutoResumeFiredAt = type == "quota_auto_resume_fired" ? now : s.AutoResumeFiredAt,
            AutoResumeGaveUpAt = type is "quota_auto_resume_stale" or "quota_auto_resume_disabled" ? now : s.AutoResumeGaveUpAt,
            CompletedAt = type == "agent_completed" ? now : s.CompletedAt,
        });

        var text = type switch
        {
            "quota_auto_resume_fired" => "The usage limit reset: Claude continued on its own.",
            "quota_auto_resume_stale" => "The limit reset while Claude was not watching; it will not continue on its own. Fermata takes over.",
            "quota_auto_resume_disabled" => "The limit resets more than 24 hours out; Claude will not continue on its own. Fermata takes over.",
            "permission_prompt" or "worker_permission_prompt" => "Claude is waiting for a permission.",
            "agent_needs_input" or "elicitation_dialog" or "elicitation_url_dialog" => "Claude is waiting for your answer.",
            "agent_completed" => "Claude reports the task as completed.",
            _ => null,
        };
        if (text is not null)
        {
            Event(job, now, type!, text);
        }

        return null;
    }

    /// <summary>
    /// Prepare level: block the stop once and ask for <c>.fermata/handoff.md</c>. Stop level: let it
    /// stop and record a mechanical checkpoint. <c>stop_hook_active</c> guards against loops.
    /// </summary>
    private async Task<string?> StopAsync(JsonObject payload, string sessionId, Job? job, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = payload["stop_hook_active"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
        if (job is null || active || job.IsFinished)
        {
            _state.UpdateSession(sessionId, now, s => s with { LastStopAt = now });
            return null;
        }

        var quota = QuotaPolicy.Assess(_state.Quota(now), _policy, now);
        var marker = Path.Combine(_jobs.DirectoryOf(job.Id), JobScheduler.HandoffMarker);
        var wantsHandoff = job.Status is JobStatus.Running
            && !File.Exists(marker)
            && !quota.AtLeast(QuotaLevel.StopNewWork)
            && (quota.AtLeast(QuotaLevel.Prepare) || job.HandoffRequestedAt is not null);

        if (wantsHandoff)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            await File.WriteAllTextAsync(marker, now.ToString("O"), cancellationToken).ConfigureAwait(false);
            Event(job, now, "handoff-requested", "Asked Claude to write .fermata/handoff.md before stopping.");
            return new JsonObject { ["decision"] = "block", ["reason"] = JobPrompts.Handoff(job) }.ToJsonString(Relaxed);
        }

        _state.UpdateSession(sessionId, now, s => s with { LastStopAt = now });
        if (quota.AtLeast(QuotaLevel.StopNewWork) && _checkpoints is not null)
        {
            var info = await _checkpoints.WriteAsync(job, _jobs.DirectoryOf(job.Id), "stop hook (quota low)", _savePatch, now, cancellationToken).ConfigureAwait(false);
            Event(job, now, "checkpoint", $"Quota almost used up: Claude stopped; checkpoint {Path.GetFileName(info.Path)}.");
        }

        return null;
    }

    private Job? FindJob(string sessionId) =>
        _jobs.List().LastOrDefault(j => j.Provider == JobProviderKind.Claude && !j.IsFinished
            && (j.Session?.Id == sessionId || (j.Session?.ShortId is { } shortId && sessionId.StartsWith(shortId, StringComparison.Ordinal))));

    private void Event(Job? job, DateTimeOffset now, string kind, string message)
    {
        if (job is not null)
        {
            _jobs.AppendEvent(job.Id, new JobEvent(now, kind, message));
        }
    }

    private static string? Text(JsonObject payload, string name) =>
        payload[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

/// <summary>
/// <c>fermata claude statusline</c>: records <c>rate_limits</c> from Claude Code's status line input,
/// then runs the user's previous status line command with the same input and prints its output.
/// </summary>
public sealed class ClaudeStatusLine
{
    private readonly ClaudeStateStore _state;
    private readonly TimeProvider _time;

    public ClaudeStatusLine(ClaudeStateStore state, TimeProvider time)
    {
        _state = state;
        _time = time;
    }

    public static ClaudeQuotaFile? Parse(string input, DateTimeOffset now)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(input) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?["rate_limits"] is not JsonObject limits)
        {
            return null;
        }

        var five = ClaudeStateStore.ParseWindow(limits["five_hour"]);
        var week = ClaudeStateStore.ParseWindow(limits["seven_day"]);
        return five is null && week is null
            ? null
            : new ClaudeQuotaFile
            {
                CapturedAt = now,
                SessionId = payload["session_id"]?.GetValue<string>(),
                Cwd = payload["cwd"]?.GetValue<string>() ?? payload["workspace"]?["current_dir"]?.GetValue<string>(),
                FiveHour = five,
                SevenDay = week,
            };
    }

    /// <summary>Records the quota and returns what to print.</summary>
    public async Task<string> RunAsync(string input, CancellationToken cancellationToken)
    {
        var quota = Parse(input, _time.GetUtcNow());
        if (quota is not null)
        {
            try
            {
                _state.SaveQuota(quota);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The status line must render even if the snapshot cannot be written.
            }
        }

        var previous = _state.LoadPreviousStatusLine();
        if (string.IsNullOrWhiteSpace(previous))
        {
            return DefaultLine(quota);
        }

        return await RunPreviousAsync(previous, input, cancellationToken).ConfigureAwait(false) ?? DefaultLine(quota);
    }

    internal static string DefaultLine(ClaudeQuotaFile? quota)
    {
        if (quota is null)
        {
            return "Fermata";
        }

        var parts = new List<string>();
        if (quota.FiveHour is { } five)
        {
            parts.Add($"5h {five.UsedPercent:0}%");
        }

        if (quota.SevenDay is { } week)
        {
            parts.Add($"7d {week.UsedPercent:0}%");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Through the same kind of shell Claude Code uses: Git Bash (or cmd) on Windows, sh elsewhere.</summary>
    private static async Task<string?> RunPreviousAsync(string command, string input, CancellationToken cancellationToken)
    {
        var (shell, args) = Shell(command);
        var info = new ProcessStartInfo(shell)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return (await output.ConfigureAwait(false)).TrimEnd('\r', '\n');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static (string Shell, string[] Args) Shell(string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ("/bin/sh", ["-c", command]);
        }

        var bash = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH");
        if (string.IsNullOrWhiteSpace(bash) || !File.Exists(bash))
        {
            bash = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
            }.FirstOrDefault(File.Exists);
        }

        return bash is not null
            ? (bash, ["-c", command])
            : (Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/s", "/c", command]);
    }
}
