using System.Text.Json.Nodes;
using Fermata.Codex.AppServer;

namespace Fermata.Codex.Tests;

/// <summary><see cref="CodexSessionResumer"/> against a fake daemon with three live threads.</summary>
public sealed class SessionResumerTests : IAsyncDisposable
{
    private readonly FakeAppServer _server = new();

    public SessionResumerTests()
    {
        // goal-thread: goal stopped at the limit. plain-thread: no goal, last turn hit the limit.
        // idle-thread: no goal, last turn fine.
        _server.Handle("thread/loaded/list", _ => new JsonObject
        {
            ["data"] = new JsonArray("goal-thread", "plain-thread", "idle-thread"),
            ["nextCursor"] = null,
        });
        _server.Handle("thread/goal/set", p => new JsonObject { ["goal"] = new JsonObject { ["status"] = p?["status"]?.DeepClone() } });
        _server.Handle("thread/turns/list", p => new JsonObject
        {
            ["data"] = new JsonArray(p?["threadId"]?.GetValue<string>() == "plain-thread"
                ? new JsonObject { ["id"] = "t1", ["status"] = "failed", ["error"] = new JsonObject { ["message"] = "limit", ["codexErrorInfo"] = "usageLimitExceeded" } }
                : new JsonObject { ["id"] = "t2", ["status"] = "completed", ["error"] = null }),
        });
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private async Task<SessionResumeReport> Run(bool dryRun = false)
    {
        _server.Handle("thread/goal/get", p => p?["threadId"]?.GetValue<string>() switch
        {
            "goal-thread" => new JsonObject { ["goal"] = new JsonObject { ["status"] = "usageLimited" } },
            _ => new JsonObject { ["goal"] = null },
        });
        _server.ClientConnection.Start();
        return await new CodexSessionResumer(TimeSpan.FromSeconds(30)).ResumeAsync(_server.ClientConnection, CancellationToken.None, dryRun);
    }

    [Fact]
    public async Task Usage_limited_goals_are_set_active_and_stopped_turns_are_reported()
    {
        var report = await Run();

        Assert.True(report.DaemonReachable);
        Assert.Equal(3, report.LiveSessions);
        Assert.Equal(["goal-thread"], report.GoalsResumed);
        Assert.Equal(["plain-thread"], report.StoppedTurns);
        Assert.Null(report.Error);

        var set = Assert.Single(_server.Snapshot(), r => r.Method == "thread/goal/set");
        Assert.Equal("goal-thread", set.Params?["threadId"]?.GetValue<string>());
        Assert.Equal("active", set.Params?["status"]?.GetValue<string>());
        Assert.DoesNotContain(_server.Snapshot(), r => r.Method is "turn/start" or "thread/queue/add");
        Assert.StartsWith("Resumed 1 Codex goal that stopped at the limit. 1 Codex session stopped at the limit", report.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_changes_nothing()
    {
        var report = await Run(dryRun: true);

        Assert.Equal(["goal-thread"], report.GoalsResumed);
        Assert.DoesNotContain(_server.Snapshot(), r => r.Method == "thread/goal/set");
    }

    [Fact]
    public async Task One_failing_thread_does_not_stop_the_others()
    {
        _server.HandleError("thread/turns/list", -32600, "thread is gone");

        var report = await Run();

        Assert.Equal(["goal-thread"], report.GoalsResumed);
        Assert.Empty(report.StoppedTurns);
        Assert.Null(report.Error);
    }

    [Fact]
    public void Nothing_to_tell_when_no_session_waits() =>
        Assert.Null(new SessionResumeReport(true, [], [], LiveSessions: 3).Describe());

    [Fact]
    public async Task No_daemon_means_nothing_to_resume()
    {
        var home = Directory.CreateTempSubdirectory("fermata-nodaemon-").FullName;
        try
        {
            var report = await new CodexSessionResumer(TimeSpan.FromSeconds(5)).ResumeAsync(home, CancellationToken.None);

            Assert.False(report.DaemonReachable);
            Assert.Empty(report.GoalsResumed);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
