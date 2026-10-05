using System.Text.Json.Nodes;
using Fermata.Claude;
using Fermata.Core.Jobs;
using Fermata.Platform;
using Fermata.Platform.Jobs;
using Fermata.Platform.Processes;

namespace Fermata.Core.Tests;

public sealed class ClaudeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private const string Exe = @"C:\Apps\Fermata\fermata.exe";

    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-claude-").FullName;
    private readonly AppPaths _paths;
    private readonly ClaudeStateStore _state;
    private readonly JsonJobStore _jobs;
    private readonly ManualTime _time = new(Now);

    public ClaudeTests()
    {
        _paths = new AppPaths(Path.Combine(_dir, "data"));
        _state = new ClaudeStateStore(_paths);
        _jobs = new JsonJobStore(_paths.JobsDirectory);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ClaudeHome => Path.Combine(_dir, ".claude");

    private ClaudeSettingsInstaller Installer => new(ClaudeHome, _state);

    private JsonObject Settings() => (JsonObject)JsonNode.Parse(File.ReadAllText(Installer.SettingsFile))!;

    // ── settings.json ────────────────────────────────────────────────────────

    [Fact]
    public void Install_wraps_the_status_line_and_keeps_everything_else()
    {
        Directory.CreateDirectory(ClaudeHome);
        File.WriteAllText(Installer.SettingsFile, """
            {
              "model": "opus",
              "statusLine": { "type": "command", "command": "bash ~/.claude/statusline.sh", "padding": 1 },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify-done" } ] } ] }
            }
            """);

        Installer.Install(Exe);

        var settings = Settings();
        Assert.Equal("opus", settings["model"]!.GetValue<string>());
        Assert.Equal($"\"{Exe}\" claude statusline", settings["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(1, settings["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Equal("bash ~/.claude/statusline.sh", _state.LoadPreviousStatusLine());

        var stop = settings["hooks"]!["Stop"]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>()).ToList();
        Assert.Contains("notify-done", stop);
        Assert.Contains($"\"{Exe}\" claude hook stop", stop);
        Assert.True(File.Exists(Installer.SettingsFile + ".fermata.bak"));

        var status = Installer.GetStatus();
        Assert.True(status.StatusLineWrapped);
        Assert.Equal(["SessionStart", "SessionEnd", "Notification", "Stop", "StopFailure"], status.HookEvents);
    }

    [Fact]
    public void Reinstall_does_not_lose_the_original_status_line()
    {
        Directory.CreateDirectory(ClaudeHome);
        File.WriteAllText(Installer.SettingsFile, """{ "statusLine": { "type": "command", "command": "my-line" } }""");
        Installer.Install(Exe);
        Installer.Install(@"D:\new\fermata.exe");

        Assert.Equal("my-line", _state.LoadPreviousStatusLine());
        Assert.Single(Settings()["hooks"]!["Stop"]!.AsArray());
    }

    [Fact]
    public void Uninstall_restores_the_status_line_and_removes_only_our_hooks()
    {
        Directory.CreateDirectory(ClaudeHome);
        File.WriteAllText(Installer.SettingsFile, """
            { "statusLine": { "type": "command", "command": "my-line" },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify-done" } ] } ] } }
            """);
        Installer.Install(Exe);

        Assert.True(Installer.Uninstall());

        var settings = Settings();
        Assert.Equal("my-line", settings["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal("notify-done", settings["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Null(settings["hooks"]!["SessionStart"]);
        Assert.False(Installer.GetStatus().Installed);
    }

    [Fact]
    public void An_unparseable_settings_file_is_never_overwritten()
    {
        Directory.CreateDirectory(ClaudeHome);
        File.WriteAllText(Installer.SettingsFile, "[1, 2]");
        Assert.Throws<InvalidDataException>(() => Installer.Install(Exe));
        Assert.Equal("[1, 2]", File.ReadAllText(Installer.SettingsFile));
    }

    // ── status line ─────────────────────────────────────────────────────────

    [Fact]
    public void Status_line_input_is_parsed_in_both_shapes()
    {
        var quota = ClaudeStatusLine.Parse("""
            { "session_id": "s1", "cwd": "/repo",
              "rate_limits": { "five_hour": { "used_percentage": 23.5, "resets_at": 1791200000 },
                               "seven_day": { "utilization": 0.41, "resets_at": "2026-10-09T10:00:00Z" } } }
            """, Now)!;

        Assert.Equal(23.5, quota.FiveHour!.UsedPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791200000), quota.FiveHour.ResetsAt);
        Assert.Equal(41, quota.SevenDay!.UsedPercent, 3);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero), quota.SevenDay.ResetsAt);
        Assert.Equal("5h 24% · 7d 41%", ClaudeStatusLine.DefaultLine(quota));

        Assert.Null(ClaudeStatusLine.Parse("""{ "session_id": "s1", "rate_limits": null }""", Now));
        Assert.Null(ClaudeStatusLine.Parse("not json", Now));
    }

    [Fact]
    public async Task Status_line_records_the_quota_and_prints_a_default_line()
    {
        var line = await new ClaudeStatusLine(_state, _time).RunAsync("""{ "rate_limits": { "five_hour": { "used_percentage": 96, "resets_at": 1791200000 } } }""", CancellationToken.None);
        Assert.Equal("5h 96%", line);
        Assert.Equal(96, _state.Quota(Now)!.Window(QuotaWindowKind.FiveHour)!.UsedPercent);
    }

    // ── hooks ────────────────────────────────────────────────────────────────

    private Job SaveJob(JobStatus status = JobStatus.Running, DateTimeOffset? handoff = null)
    {
        var job = new Job
        {
            Id = "claude-job",
            Provider = JobProviderKind.Claude,
            Cwd = _dir,
            Objective = "Port the parser",
            Status = status,
            Session = new SessionRef("sess-1234-abcd", "sess-123"),
            HandoffRequestedAt = handoff,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        _jobs.Save(job);
        return job;
    }

    private ClaudeHookHandler Hooks => new(_state, _jobs, new QuotaPolicyOptions(), null, false, _time);

    private void Quota(double fivePercent) =>
        _state.SaveQuota(new ClaudeQuotaFile { CapturedAt = Now, FiveHour = new ClaudeWindow(fivePercent, Now.AddHours(2)) });

    [Fact]
    public async Task Stop_asks_for_the_handoff_once_at_the_prepare_level()
    {
        SaveJob();
        Quota(92);

        var first = await Hooks.HandleAsync("stop", """{ "session_id": "sess-1234-abcd", "stop_hook_active": false }""", CancellationToken.None);
        var block = JsonNode.Parse(first!)!;
        Assert.Equal("block", block["decision"]!.GetValue<string>());
        Assert.Contains(".fermata/handoff.md", block["reason"]!.GetValue<string>(), StringComparison.Ordinal);

        // The continuation stops again with stop_hook_active; and a later stop in the same episode is not blocked.
        Assert.Null(await Hooks.HandleAsync("stop", """{ "session_id": "sess-1234-abcd", "stop_hook_active": true }""", CancellationToken.None));
        Assert.Null(await Hooks.HandleAsync("stop", """{ "session_id": "sess-1234-abcd", "stop_hook_active": false }""", CancellationToken.None));
        Assert.Contains(_jobs.ReadEvents("claude-job", 10), e => e.Kind == "handoff-requested");
    }

    [Fact]
    public async Task Stop_lets_the_agent_stop_at_the_stop_level_and_without_a_job()
    {
        SaveJob();
        Quota(97);
        Assert.Null(await Hooks.HandleAsync("stop", """{ "session_id": "sess-1234-abcd" }""", CancellationToken.None));

        Quota(92);
        Assert.Null(await Hooks.HandleAsync("stop", """{ "session_id": "someone-else" }""", CancellationToken.None));
        Assert.Equal(Now, _state.LoadSession("someone-else")!.LastStopAt);
    }

    [Fact]
    public async Task Normal_quota_never_blocks_a_stop()
    {
        SaveJob();
        Quota(40);
        Assert.Null(await Hooks.HandleAsync("stop", """{ "session_id": "sess-1234-abcd" }""", CancellationToken.None));
    }

    [Fact]
    public async Task Limit_and_auto_resume_notifications_are_recorded_and_logged_to_the_job()
    {
        SaveJob();
        await Hooks.HandleAsync("stop-failure", """{ "session_id": "sess-1234-abcd", "error": "rate_limit" }""", CancellationToken.None);
        var record = _state.LoadSession("sess-1234-abcd")!;
        Assert.True(record.LimitOpen);
        Assert.True(_state.Quota(Now)!.LimitReported);

        _time.Now = Now.AddHours(2);
        await Hooks.HandleAsync("notification", """{ "session_id": "sess-1234-abcd", "notification_type": "quota_auto_resume_fired" }""", CancellationToken.None);
        Assert.False(_state.LoadSession("sess-1234-abcd")!.LimitOpen);

        var kinds = _jobs.ReadEvents("claude-job", 10).Select(e => e.Kind).ToList();
        Assert.Contains("limit", kinds);
        Assert.Contains("quota_auto_resume_fired", kinds);
    }

    [Fact]
    public async Task Short_background_ids_find_their_job()
    {
        SaveJob();
        _jobs.Save(_jobs.Get("claude-job")! with { Session = new SessionRef("e92725ea", "e92725ea") });
        await Hooks.HandleAsync("session-start", """{ "session_id": "e92725ea-9825-4e1d-97ba-60aac2b3acdb", "source": "resume" }""", CancellationToken.None);
        Assert.Contains(_jobs.ReadEvents("claude-job", 10), e => e.Kind == "session-start");
    }

    [Fact]
    public async Task Garbage_input_is_ignored()
    {
        Assert.Null(await Hooks.HandleAsync("stop", "{", CancellationToken.None));
        Assert.Null(await Hooks.HandleAsync("stop", "{}", CancellationToken.None));
    }

    // ── provider state mapping ──────────────────────────────────────────────

    private static ClaudeAgent Agent(bool alive = true, string? status = "busy", string? state = null, bool background = true) =>
        new(background ? "abc12345" : null, "abc12345-full", "/repo", background ? "background" : "interactive", alive ? 42 : null, alive ? status : null, state);

    private static SessionState Map(ClaudeAgent? agent, ClaudeSessionRecord? record, DateTimeOffset? resumeAt = null, DateTimeOffset? now = null) =>
        ClaudeJobProvider.Map(agent, record, now ?? Now, resumeAt, TimeSpan.FromMinutes(10)).State;

    private static ClaudeSessionRecord Record(Func<ClaudeSessionRecord, ClaudeSessionRecord>? change = null)
    {
        var record = new ClaudeSessionRecord { SessionId = "abc12345-full", LastSeenAt = Now, LastStartAt = Now.AddHours(-3) };
        return change is null ? record : change(record);
    }

    [Fact]
    public void Busy_idle_done_and_failed_background_sessions()
    {
        Assert.Equal(SessionState.Running, Map(Agent(), Record()));
        Assert.Equal(SessionState.Idle, Map(Agent(status: "idle"), Record()));
        Assert.Equal(SessionState.Completed, Map(Agent(alive: false, state: "done"), Record()));
        Assert.Equal(SessionState.WaitingInput, Map(Agent(status: "idle", state: "failed"), Record()));
    }

    [Fact]
    public void Prompts_waiting_for_the_user_block_until_the_turn_moves_on()
    {
        var asked = Record(r => r with { LastNotification = "permission_prompt", LastNotificationAt = Now.AddMinutes(-1) });
        Assert.Equal(SessionState.WaitingApproval, Map(Agent(), asked));
        Assert.Equal(SessionState.WaitingInput, Map(Agent(), asked with { LastNotification = "agent_needs_input" }));
        Assert.Equal(SessionState.Running, Map(Agent(), asked with { LastStopAt = Now }));
    }

    [Fact]
    public void Limit_waits_for_native_auto_resume_then_takes_over()
    {
        var hit = Record(r => r with { LimitHitAt = Now.AddHours(-1) });
        var reset = Now.AddHours(1);

        Assert.Equal(SessionState.NativeWaiting, Map(Agent(status: "idle"), hit, reset));
        Assert.Equal(SessionState.NativeWaiting, Map(Agent(status: "idle"), hit, reset, now: reset.AddMinutes(5)));
        Assert.Equal(SessionState.LimitStopped, Map(Agent(status: "idle"), hit, reset, now: reset.AddMinutes(11)));
        Assert.Equal(SessionState.LimitStopped, Map(Agent(status: "idle"), hit with { AutoResumeGaveUpAt = Now }, reset));
        Assert.Equal(SessionState.LimitStopped, Map(Agent(alive: false), hit, reset));
        Assert.Equal(SessionState.Running, Map(Agent(), hit with { AutoResumeFiredAt = Now }, reset));
    }

    [Fact]
    public void Gone_processes_are_ended_or_interrupted()
    {
        Assert.Equal(SessionState.Gone, Map(Agent(alive: false), Record(r => r with { EndedAt = Now, EndReason = "other" })));
        Assert.Equal(SessionState.Interrupted, Map(Agent(alive: false), Record()));
        Assert.Equal(SessionState.Interrupted, Map(null, null));
    }

    // ── claude CLI ──────────────────────────────────────────────────────────

    [Fact]
    public void Background_ids_and_refusals_are_read_from_the_output()
    {
        Assert.Equal("e92725ea", ClaudeCli.BackgroundIdOf(new ProcessResult(0, "Starting background service…\nbackgrounded · e92725ea\n  claude agents", "")));
        Assert.Equal("231ab208", ClaudeCli.BackgroundIdOf(new ProcessResult(0, "note: … started a copy as 231ab208.\nbackgrounded \u00b7 231ab208", "")));

        var untrusted = Assert.Throws<JobProviderException>(() => ClaudeCli.BackgroundIdOf(new ProcessResult(0, "Workspace not trusted. Run `claude` in C:\\x once", "")));
        Assert.True(untrusted.Permanent);
        Assert.False(Assert.Throws<JobProviderException>(() => ClaudeCli.BackgroundIdOf(new ProcessResult(1, "", "boom"))).Permanent);
    }

    [Fact]
    public void Agents_json_is_parsed()
    {
        var agents = ClaudeCli.ParseAgents("""
            [ { "pid": 16404, "cwd": "G:\\P", "kind": "interactive", "startedAt": 1, "sessionId": "5cb7", "status": "busy" },
              { "id": "e92725ea", "cwd": "G:\\P", "kind": "background", "startedAt": 2, "sessionId": "e92725ea-98", "state": "failed" } ]
            """);
        Assert.Equal(2, agents.Count);
        Assert.True(agents[0].Alive);
        Assert.False(agents[0].IsBackground);
        Assert.False(agents[1].Alive);
        Assert.Equal("e92725ea", agents[1].Id);
        Assert.Equal("failed", agents[1].State);
    }
}
