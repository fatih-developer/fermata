using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ResetMe.Core.Domain;
using ResetMe.Core.Logging;
using ResetMe.Core.Reset;
using ResetMe.Platform;
using ResetMe.Platform.Logging;

namespace ResetMe.Core.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("resetme-logs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("user someone.name+x@example.co.uk logged in", "user <email> logged in")]
    [InlineData("Authorization: Bearer abc.def-123", "Authorization: <redacted> <redacted>")]
    [InlineData("header bearer abcdef123", "header Bearer <redacted>")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.c2lnbmF0dXJl end", "token <jwt> end")]
    [InlineData("key sk-proj-abcdefghijklmnop1234 used", "key <api-key> used")]
    [InlineData("gh token gho_abcdefghijklmnopqrstuvwx", "gh token <token>")]
    [InlineData("{\"access_token\":\"abc123\",\"x\":1}", "{\"access_token\":\"<redacted>\",\"x\":1}")]
    [InlineData("refreshToken=xyz; path=/", "refreshToken=<redacted>; path=/")]
    [InlineData("password: hunter2", "password: <redacted>")]
    public void Sanitizer_removes_credentials_and_emails(string input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Redact(input));
    }

    [Theory]
    [InlineData("Consume outcome Reset for key 31abd3d3-9652-4297-9e37-614bdf0695fd")]
    [InlineData("account/rateLimits/read completed in 640 ms")]
    [InlineData("Limit episode 9f2c: exhausted=[FiveHour] handling=AskUser reason=-")]
    public void Sanitizer_leaves_ordinary_messages_alone(string input)
    {
        Assert.Equal(input, LogSanitizer.Redact(input));
    }

    [Fact]
    public void Entries_are_written_as_json_lines_with_properties()
    {
        using (var provider = new FileLoggerProvider(_dir, LogLevel.Information, 14, TimeProvider.System))
        {
            var logger = provider.CreateLogger("ResetMe.Test");
            logger.LogInformation(new EventId(42), "Outcome {Outcome} after {Tries} tries, verified={Verified}", "Reset", 2, true);
        }

        var line = Assert.Single(ReadAllLines());
        var entry = JsonNode.Parse(line)!.AsObject();
        Assert.Equal("Information", entry["level"]!.GetValue<string>());
        Assert.Equal("ResetMe.Test", entry["category"]!.GetValue<string>());
        Assert.Equal(42, entry["event"]!.GetValue<int>());
        Assert.Equal("Outcome Reset after 2 tries, verified=True", entry["msg"]!.GetValue<string>());
        Assert.Equal("Reset", entry["props"]!["Outcome"]!.GetValue<string>());
        Assert.Equal(2, entry["props"]!["Tries"]!.GetValue<int>());
        Assert.True(entry["props"]!["Verified"]!.GetValue<bool>());
    }

    [Fact]
    public void Below_minimum_level_is_not_written()
    {
        using (var provider = new FileLoggerProvider(_dir, LogLevel.Warning, 14, TimeProvider.System))
        {
            var logger = provider.CreateLogger("ResetMe.Test");
            logger.LogInformation("ignored");
            logger.LogDebug("ignored");
            logger.LogWarning("kept");
        }

        Assert.Contains("kept", Assert.Single(ReadAllLines()), StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_never_reach_the_file_from_messages_properties_or_exceptions()
    {
        using (var provider = new FileLoggerProvider(_dir, LogLevel.Trace, 14, TimeProvider.System))
        {
            var logger = provider.CreateLogger("ResetMe.Test");
            logger.LogWarning(
                new InvalidOperationException("auth failed for owner@example.com with Bearer abc.def"),
                "Login for {Email} using {Header}",
                "owner@example.com",
                "Bearer secret-value");
        }

        var text = string.Join("\n", ReadAllLines());
        Assert.DoesNotContain("owner@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", text, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", text, StringComparison.Ordinal);
        Assert.Contains("<email>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_files_are_removed_by_retention()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var old = Path.Combine(_dir, FileLoggerProvider.FileNameFor(today.AddDays(-20)));
        var recent = Path.Combine(_dir, FileLoggerProvider.FileNameFor(today.AddDays(-3)));
        var unrelated = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(old, "{}\n");
        File.WriteAllText(recent, "{}\n");
        File.WriteAllText(unrelated, "keep");

        using (var provider = new FileLoggerProvider(_dir, LogLevel.Information, 14, TimeProvider.System))
        {
            provider.CreateLogger("ResetMe.Test").LogInformation("trigger");
        }

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public void Two_writers_on_the_same_file_lose_no_lines()
    {
        // Simulates `watch` and `reset` running at the same time.
        using (var a = new FileLoggerProvider(_dir, LogLevel.Information, 14, TimeProvider.System))
        using (var b = new FileLoggerProvider(_dir, LogLevel.Information, 14, TimeProvider.System))
        {
            var la = a.CreateLogger("A");
            var lb = b.CreateLogger("B");
            Parallel.For(0, 200, i =>
            {
                (i % 2 == 0 ? la : lb).LogInformation("line {Index}", i);
            });
        }

        var lines = ReadAllLines();
        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => JsonNode.Parse(l)); // every line is intact JSON
    }

    [Fact]
    public void Logging_section_is_read_from_config()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "[logging]\nlevel = \"debug\"\nretention_days = 3\n");

        var options = new TomlConfigStore(path).Load().Options;

        Assert.Equal(LogLevel.Debug, options.Logging.Level);
        Assert.Equal(3, options.Logging.RetentionDays);
    }

    [Fact]
    public void Unknown_log_level_falls_back_with_a_warning()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, "[logging]\nlevel = \"chatty\"\n");

        var result = new TomlConfigStore(path).Load();

        Assert.Equal(LogLevel.Information, result.Options.Logging.Level);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task Reset_flow_records_start_outcome_and_verification()
    {
        var client = new FakeCodexClient();
        client.ThenUsage(Usage.Blocked()).ThenConsume(ResetOutcome.Reset).ThenUsage(Usage.Healthy());
        var logger = new CapturingLogger<ResetManager>();
        var manager = new ResetManager(client, new InMemoryStateStore(), new FakeLock(), Usage.FastOptions(), new SteppingTimeProvider(Usage.Now), logger);

        await manager.ExecuteAsync(new ResetRequest(), CancellationToken.None);

        Assert.Equal([100, 102, 106, 107], logger.EventIds);
    }

    private List<string> ReadAllLines() =>
        [.. Directory.EnumerateFiles(_dir, "resetme-*.log").SelectMany(File.ReadAllLines).Where(l => l.Length > 0)];

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            EventIds.Add(eventId.Id);
    }
}
