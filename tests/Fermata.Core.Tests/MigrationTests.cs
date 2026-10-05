using System.Text.Json.Nodes;
using Fermata.Platform;
using Fermata.Platform.Codex;

namespace Fermata.Core.Tests;

public sealed class MigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fermata-migration-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Legacy => Path.Combine(_dir, "ResetMe");

    private AppPaths Paths => new(Path.Combine(_dir, "Fermata"));

    private LegacyMigration Create(bool running = false, bool autostart = false) =>
        new(Paths, Legacy, _ => running, _ => Task.FromResult(autostart));

    private void WriteLegacy(string name, string content)
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, name), content);
    }

    [Fact]
    public async Task Copies_config_state_and_backup_keeping_the_pending_key()
    {
        WriteLegacy("config.toml", "mode = \"automatic\"\n");
        WriteLegacy("state.json", """{ "pending": { "idempotencyKey": "key-1" } }""");
        WriteLegacy("state.json.bak", "{}");

        var result = await Create(autostart: true).RunAsync(CancellationToken.None);

        Assert.Equal(MigrationOutcome.Migrated, result.Outcome);
        Assert.Equal(["config.toml", "state.json", "state.json.bak"], result.CopiedFiles);
        Assert.True(result.LegacyAutostartWasEnabled);
        Assert.Contains("key-1", File.ReadAllText(Paths.StateFile), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Legacy, "state.json")), "the old folder is never deleted");
        Assert.True(File.Exists(Paths.MigrationMarker));
    }

    [Fact]
    public async Task Runs_only_once()
    {
        WriteLegacy("config.toml", "mode = \"manual\"\n");
        Assert.Equal(MigrationOutcome.Migrated, (await Create().RunAsync(CancellationToken.None)).Outcome);

        File.Delete(Paths.ConfigFile); // even if the user later removes the copied config
        Assert.Equal(MigrationOutcome.NotNeeded, (await Create().RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Stops_while_resetme_still_runs_and_copies_nothing()
    {
        WriteLegacy("state.json", "{}");

        var result = await Create(running: true).RunAsync(CancellationToken.None);

        Assert.Equal(MigrationOutcome.BlockedByRunningResetMe, result.Outcome);
        Assert.False(File.Exists(Paths.StateFile));
        Assert.False(File.Exists(Paths.MigrationMarker));

        Assert.Equal(MigrationOutcome.Migrated, (await Create().RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Never_overwrites_existing_fermata_data()
    {
        WriteLegacy("state.json", """{ "old": true }""");
        Paths.EnsureRoot();
        File.WriteAllText(Paths.StateFile, "{}");

        Assert.Equal(MigrationOutcome.NotNeeded, (await Create().RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal("{}", File.ReadAllText(Paths.StateFile));
    }

    [Fact]
    public async Task Nothing_to_do_on_a_fresh_install_or_with_an_explicit_root()
    {
        Assert.Equal(MigrationOutcome.NotNeeded, (await Create().RunAsync(CancellationToken.None)).Outcome);
        Assert.Equal(MigrationOutcome.NotNeeded, (await new LegacyMigration(Paths, null, _ => false, _ => Task.FromResult(false)).RunAsync(CancellationToken.None)).Outcome);
    }

    [Fact]
    public void A_held_legacy_desktop_lock_means_resetme_runs()
    {
        WriteLegacy("desktop.lock", "");
        using var held = new FileStream(Path.Combine(Legacy, "desktop.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(LegacyMigration.IsResetMeRunning(Legacy));
    }

    [Fact]
    public void Codex_hooks_installed_by_resetme_are_recognized_and_replaced()
    {
        var installer = new CodexHooksInstaller(_dir);
        File.WriteAllText(installer.HooksFile, """
            { "hooks": { "SessionStart": [ { "hooks": [
                { "type": "command", "command": "& 'C:\\Apps\\ResetMe\\resetme.exe' hook session-start" },
                { "type": "command", "command": "other-tool" } ] } ] } }
            """);

        Assert.True(installer.GetStatus().IsLegacy);

        installer.Install(@"C:\Apps\Fermata\fermata.exe", windows: true);

        var handlers = ((JsonObject)JsonNode.Parse(File.ReadAllText(installer.HooksFile))!)["hooks"]!["SessionStart"]!.AsArray()
            .SelectMany(g => g!["hooks"]!.AsArray()).Select(h => h!["command"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain(handlers, h => h.Contains("resetme", StringComparison.Ordinal));
        Assert.Contains("other-tool", handlers);
        Assert.Contains(handlers, h => h.Contains("fermata.exe' hook session-start", StringComparison.Ordinal));
        Assert.False(installer.GetStatus().IsLegacy);
    }
}
