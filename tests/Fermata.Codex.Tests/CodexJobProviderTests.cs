using System.Text.Json.Nodes;
using Fermata.Codex.AppServer;
using Fermata.Codex.Jobs;
using Fermata.Core.Jobs;

namespace Fermata.Codex.Tests;

/// <summary><see cref="CodexJobProvider"/> against a fake daemon that keeps threads and goals between connections.</summary>
public sealed class CodexJobProviderTests : IAsyncDisposable
{
    private readonly List<FakeAppServer> _servers = [];
    private readonly Dictionary<string, JsonObject> _threads = [];
    private readonly Dictionary<string, string> _goals = [];
    private readonly List<string> _calls = [];
    private bool _daemonUp = true;
    private int _daemonStarts;

    private CodexJobProvider CreateProvider()
    {
        var provider = new CodexJobProvider(
            new CodexJobProviderOptions { CodexHome = "/tmp/codex", Executable = () => "codex", Approvals = "auto_review" },
            () => null,
            (_, _) =>
            {
                _daemonStarts++;
                _daemonUp = true;
                return Task.FromResult(true);
            });
        provider.ConnectOverride = ConnectAsync;
        return provider;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    private async Task<CodexDaemonClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_daemonUp)
        {
            throw new CodexUnavailableException("no daemon");
        }

        var server = new FakeAppServer();
        _servers.Add(server);
        server.Handle("thread/start", p =>
        {
            var id = $"thread-{_threads.Count + 1}";
            _calls.Add($"start {p?["cwd"]} {p?["sandbox"]} {p?["approvalsReviewer"]}");
            _threads[id] = Thread(id, "idle", p?["cwd"]?.GetValue<string>());
            return new JsonObject { ["thread"] = Thread(id, "idle", null) };
        });
        server.Handle("thread/unsubscribe", p =>
        {
            _calls.Add($"unsubscribe {p?["threadId"]}");
            return new JsonObject { ["status"] = "unsubscribed" };
        });
        server.Handle("thread/goal/set", p =>
        {
            var id = p!["threadId"]!.GetValue<string>();
            _goals[id] = p["status"]?.GetValue<string>() ?? _goals.GetValueOrDefault(id, "active");
            _calls.Add($"goal {id} {_goals[id]}{(p["objective"] is { } o ? " " + o : "")}");
            return new JsonObject { ["goal"] = Goal(id) };
        });
        server.Handle("thread/goal/get", p => new JsonObject { ["goal"] = Goal(p!["threadId"]!.GetValue<string>()) });
        server.Handle("thread/read", p => new JsonObject { ["thread"] = _threads[p!["threadId"]!.GetValue<string>()].DeepClone() });
        server.Handle("thread/loaded/list", _ => new JsonObject { ["data"] = new JsonArray([.. _threads.Keys.Select(k => (JsonNode)k)]) });
        server.Handle("thread/resume", p =>
        {
            var id = p!["threadId"]!.GetValue<string>();
            _calls.Add($"load {id}");
            _threads[id]["status"] = new JsonObject { ["type"] = "idle" };
            return new JsonObject { ["thread"] = _threads[id].DeepClone() };
        });
        return await CodexDaemonClient.OverAsync(server.ClientConnection, TimeSpan.FromSeconds(5), cancellationToken);
    }

    private JsonNode? Goal(string id) =>
        _goals.TryGetValue(id, out var status) ? new JsonObject { ["objective"] = "obj", ["status"] = status } : null;

    private static JsonObject Thread(string id, string status, string? cwd, params string[] flags)
    {
        var state = new JsonObject { ["type"] = status };
        if (status == "active")
        {
            state["activeFlags"] = new JsonArray([.. flags.Select(f => (JsonNode)f)]);
        }

        return new JsonObject { ["id"] = id, ["status"] = state, ["cwd"] = cwd, ["preview"] = "", ["updatedAt"] = 100 };
    }

    private static Job NewJob(SessionRef? session = null) => new()
    {
        Id = "job",
        Provider = JobProviderKind.Codex,
        Cwd = "/repo",
        Objective = "Ship it",
        Session = session,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Start_creates_a_thread_unsubscribes_and_sets_the_goal_without_sending_turns()
    {
        var session = await CreateProvider().StartAsync(NewJob(), CancellationToken.None);

        Assert.Equal("thread-1", session.Id);
        Assert.Equal(["start /repo workspace-write auto_review", "unsubscribe thread-1", "goal thread-1 active Ship it"], _calls);
        Assert.DoesNotContain(_servers.SelectMany(s => s.Snapshot()), r => r.Method is "turn/start");
    }

    [Fact]
    public async Task Start_brings_up_the_daemon_when_it_is_not_running()
    {
        _daemonUp = false;
        await CreateProvider().StartAsync(NewJob(), CancellationToken.None);
        Assert.Equal(1, _daemonStarts);
    }

    [Fact]
    public async Task Observation_without_a_daemon_is_an_interruption_not_an_error()
    {
        _daemonUp = false;
        var state = await CreateProvider().GetSessionStateAsync(NewJob(new SessionRef("thread-1")), CancellationToken.None);
        Assert.Equal(SessionState.Interrupted, state.State);
        Assert.Equal(0, _daemonStarts);
    }

    [Theory]
    [InlineData("active", "active", new string[0], SessionState.Running)]
    [InlineData("idle", "active", new string[0], SessionState.Running)]
    [InlineData("active", "active", new[] { "waitingOnApproval" }, SessionState.WaitingApproval)]
    [InlineData("active", "active", new[] { "waitingOnUserInput" }, SessionState.WaitingInput)]
    [InlineData("idle", "usageLimited", new string[0], SessionState.LimitStopped)]
    [InlineData("idle", "complete", new string[0], SessionState.Completed)]
    [InlineData("active", "paused", new string[0], SessionState.Running)]
    [InlineData("idle", "paused", new string[0], SessionState.Paused)]
    [InlineData("idle", "blocked", new string[0], SessionState.WaitingInput)]
    [InlineData("idle", "budgetLimited", new string[0], SessionState.WaitingInput)]
    [InlineData("notLoaded", "active", new string[0], SessionState.Interrupted)]
    [InlineData("systemError", "active", new string[0], SessionState.Interrupted)]
    [InlineData("idle", null, new string[0], SessionState.Paused)]
    public void Thread_and_goal_map_to_session_states(string thread, string? goal, string[] flags, SessionState expected)
    {
        var info = CodexDaemonClient.ParseThread(Thread("t", thread, "/repo", flags));
        var state = CodexJobProvider.Map(info, goal is null ? null : new CodexGoal("t", "obj", goal));
        Assert.Equal(expected, state.State);
    }

    [Fact]
    public async Task Approvals_point_the_user_to_codex_resume()
    {
        _threads["thread-1"] = Thread("thread-1", "active", "/repo", "waitingOnApproval");
        _goals["thread-1"] = "active";
        var state = await CreateProvider().GetSessionStateAsync(NewJob(new SessionRef("thread-1")), CancellationToken.None);
        Assert.Equal(SessionState.WaitingApproval, state.State);
        Assert.Contains("codex resume thread-1", state.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pause_and_resume_flip_the_goal_and_reload_an_unloaded_thread()
    {
        _threads["thread-1"] = Thread("thread-1", "active", "/repo");
        _goals["thread-1"] = "active";
        var provider = CreateProvider();
        var job = NewJob(new SessionRef("thread-1"));

        Assert.False(await provider.PauseAsync(job, CancellationToken.None)); // a turn is still running
        Assert.Equal("paused", _goals["thread-1"]);

        _threads["thread-1"] = Thread("thread-1", "notLoaded", "/repo");
        await provider.ResumeAsync(job, "ignored", CancellationToken.None);

        Assert.Contains("load thread-1", _calls);
        Assert.Equal("active", _goals["thread-1"]);
        Assert.Contains("unsubscribe thread-1", _calls);
    }

    [Fact]
    public async Task Adopt_requires_a_goal_or_an_objective()
    {
        _threads["thread-1"] = Thread("thread-1", "idle", "/work");
        var provider = CreateProvider();

        var error = await Assert.ThrowsAsync<JobProviderException>(() => provider.AdoptAsync("thread-1", null, "/cwd", CancellationToken.None));
        Assert.True(error.Permanent);

        var adopted = await provider.AdoptAsync(null, "Finish the port", "/cwd", CancellationToken.None);
        Assert.Equal("thread-1", adopted.Session.Id);
        Assert.Equal("/work", adopted.Cwd);
        Assert.Equal("active", _goals["thread-1"]);
    }

    [Fact]
    public async Task Adopt_rejects_threads_outside_the_daemon()
    {
        var error = await Assert.ThrowsAsync<JobProviderException>(() => CreateProvider().AdoptAsync("elsewhere", "x", "/cwd", CancellationToken.None));
        Assert.Contains("not loaded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resumer_leaves_job_threads_to_the_scheduler()
    {
        var server = new FakeAppServer();
        _servers.Add(server);
        server.Handle("thread/loaded/list", _ => new JsonObject { ["data"] = new JsonArray("job-thread", "other") });
        server.Handle("thread/goal/get", _ => new JsonObject { ["goal"] = new JsonObject { ["status"] = "usageLimited" } });
        server.Handle("thread/goal/set", p => new JsonObject { ["goal"] = new JsonObject { ["status"] = p?["status"]?.DeepClone() } });

        var report = await new CodexSessionResumer(TimeSpan.FromSeconds(5)).ResumeAsync(server.ClientConnection, CancellationToken.None, skipThreads: new HashSet<string> { "job-thread" });

        Assert.Equal(["other"], report.GoalsResumed);
    }
}
