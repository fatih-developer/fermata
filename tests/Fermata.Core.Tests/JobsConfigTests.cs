using Fermata.Core.Policies;
using Fermata.Platform;

namespace Fermata.Core.Tests;

public sealed class JobsConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-jobsconfig-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Jobs_settings_survive_a_save_and_load()
    {
        var options = new GuardOptions();
        options.Jobs.PrepareRemaining = 15;
        options.Jobs.StopRemaining = 3;
        options.Jobs.GraceSeconds = 120;
        options.Jobs.SavePatch = true;
        options.Jobs.Codex.Approvals = "auto_review";
        options.Jobs.Codex.Model = "gpt-test";
        options.Jobs.Claude.Executable = @"C:\tools\claude.exe";
        options.Jobs.Claude.PermissionMode = "acceptEdits";
        options.Jobs.Claude.SuggestResetAfterMinutes = 30;
        options.Jobs.Claude.LimitResetsUrl = "https://example.test/limits";

        var store = new TomlConfigStore(Path.Combine(_dir, "config.toml"));
        store.Save(options);
        var loaded = store.Load();

        Assert.Empty(loaded.Warnings);
        var jobs = loaded.Options.Jobs;
        Assert.Equal(15, jobs.PrepareRemaining);
        Assert.Equal(3, jobs.StopRemaining);
        Assert.Equal(120, jobs.GraceSeconds);
        Assert.True(jobs.SavePatch);
        Assert.Equal("auto_review", jobs.Codex.Approvals);
        Assert.Equal("gpt-test", jobs.Codex.Model);
        Assert.Equal(@"C:\tools\claude.exe", jobs.Claude.Executable);
        Assert.Equal("acceptEdits", jobs.Claude.PermissionMode);
        Assert.Equal(30, jobs.Claude.SuggestResetAfterMinutes);
        Assert.Equal("https://example.test/limits", jobs.Claude.LimitResetsUrl);
    }

    [Fact]
    public void Invalid_jobs_values_fall_back_with_warnings()
    {
        var path = Path.Combine(_dir, "config.toml");
        File.WriteAllText(path, """
            [jobs]
            prepare_remaining = 4
            stop_remaining = 120

            [jobs.codex]
            approvals = "nobody"
            """);

        var loaded = new TomlConfigStore(path).Load();

        Assert.Equal(5, loaded.Options.Jobs.StopRemaining);
        Assert.Equal(5, loaded.Options.Jobs.PrepareRemaining); // raised to the stop level
        Assert.Equal("user", loaded.Options.Jobs.Codex.Approvals);
        Assert.Equal(3, loaded.Warnings.Count);
    }
}
