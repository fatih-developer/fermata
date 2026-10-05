using System.IO.Compression;
using Fermata.Core.Reset;
using Fermata.Platform;
using Fermata.Platform.Diagnostics;
using Fermata.Platform.Logging;

namespace Fermata.Core.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-diag-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppPaths Paths()
    {
        var paths = new AppPaths(Path.Combine(_dir, "home"));
        paths.EnsureRoot();
        return paths;
    }

    private static Dictionary<string, string> Read(string zipFile)
    {
        using var zip = ZipFile.OpenRead(zipFile);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }

    [Fact]
    public void Bundle_contains_system_config_state_logs_and_extras()
    {
        var paths = Paths();
        new TomlConfigStore(paths.ConfigFile).EnsureDefault();
        new JsonResetStateStore(paths.StateFile).Save(new ResetState { LastResolvedLimitEventId = "episode-1" });
        Directory.CreateDirectory(paths.LogDirectory);
        var today = DateOnly.FromDateTime(DateTime.Now);
        File.WriteAllText(Path.Combine(paths.LogDirectory, FileLoggerProvider.FileNameFor(today)), "{\"msg\":\"today\"}\n");

        var file = DiagnosticsBundle.Create(paths, Path.Combine(_dir, "out", "d.zip"), new Dictionary<string, string> { ["doctor.txt"] = "[ ok ] Codex" }, TimeProvider.System);

        var entries = Read(file);
        Assert.Contains("Fermata ", entries["system.txt"], StringComparison.Ordinal);
        Assert.Contains("mode = \"confirm\"", entries["config.toml"], StringComparison.Ordinal);
        Assert.Contains("episode-1", entries["state.json"], StringComparison.Ordinal);
        Assert.Contains("today", entries["logs/" + FileLoggerProvider.FileNameFor(today)], StringComparison.Ordinal);
        Assert.Equal("[ ok ] Codex", entries["doctor.txt"]);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public void Old_logs_are_left_out()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.LogDirectory);
        var old = FileLoggerProvider.FileNameFor(DateOnly.FromDateTime(DateTime.Now).AddDays(-DiagnosticsBundle.LogDays - 3));
        File.WriteAllText(Path.Combine(paths.LogDirectory, old), "{}\n");

        var entries = Read(DiagnosticsBundle.Create(paths, Path.Combine(_dir, "d.zip"), new Dictionary<string, string>(), TimeProvider.System));

        Assert.DoesNotContain("logs/" + old, entries.Keys);
    }

    [Fact]
    public void Everything_is_sanitized_again()
    {
        var paths = Paths();
        File.WriteAllText(paths.ConfigFile, "# owner: someone@example.com\n[codex]\nexecutable = \"\"\n");

        var entries = Read(DiagnosticsBundle.Create(
            paths,
            Path.Combine(_dir, "d.zip"),
            new Dictionary<string, string> { ["doctor.txt"] = "Authorization: Bearer abc.def" },
            TimeProvider.System));

        Assert.DoesNotContain("someone@example.com", entries["config.toml"], StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def", entries["doctor.txt"], StringComparison.Ordinal);
    }

    [Fact]
    public void User_profile_path_is_hidden()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var plain = Path.Combine(profile, "x");
        var jsonEscaped = Path.Combine(profile, "y").Replace(@"\", @"\\", StringComparison.Ordinal);

        var text = DiagnosticsBundle.HideUserProfile($"data: {plain} json: {jsonEscaped}");

        Assert.DoesNotContain(profile, text, StringComparison.Ordinal);
        Assert.StartsWith("data: ~", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_name_is_timestamped()
    {
        Assert.Matches(@"^fermata-diagnostics-\d{8}-\d{6}\.zip$", DiagnosticsBundle.DefaultFileName(DateTimeOffset.Now));
    }
}
