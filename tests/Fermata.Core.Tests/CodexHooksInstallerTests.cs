using System.Text.Json.Nodes;
using Fermata.Platform.Codex;
using Xunit;

namespace Fermata.Core.Tests;

public sealed class CodexHooksInstallerTests : IDisposable
{
    private const string Exe = @"C:\Program Files\Fermata\fermata.exe";
    private readonly string _home = Directory.CreateTempSubdirectory("fermata-codexhome-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private CodexHooksInstaller Installer => new(_home);

    private JsonObject Read() => (JsonObject)JsonNode.Parse(File.ReadAllText(Installer.HooksFile))!;

    [Theory]
    [InlineData(true, @"& 'C:\Program Files\Fermata\fermata.exe' hook session-start")]
    [InlineData(false, @"'C:\Program Files\Fermata\fermata.exe' hook session-start")]
    public void Commands_are_quoted_for_powershell_and_sh(bool windows, string expected) =>
        Assert.Equal(expected, CodexHooksInstaller.BuildCommand(Exe, "session-start", windows));

    [Fact]
    public void Single_quotes_in_paths_are_escaped()
    {
        Assert.Equal("& 'C:\\O''Brien\\fermata.exe' hook x", CodexHooksInstaller.BuildCommand(@"C:\O'Brien\fermata.exe", "x", windows: true));
        Assert.Equal("'/home/o'\\''b/fermata' hook x", CodexHooksInstaller.BuildCommand("/home/o'b/fermata", "x", windows: false));
    }

    [Fact]
    public void Install_creates_the_file_in_the_codex_format()
    {
        Installer.Install(Exe, windows: true);

        var handler = Read()["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!;
        Assert.Equal("command", handler["type"]!.GetValue<string>());
        Assert.Equal(@"& 'C:\Program Files\Fermata\fermata.exe' hook user-prompt-submit", handler["command"]!.GetValue<string>());
        Assert.Equal(5, handler["timeout"]!.GetValue<int>());
        Assert.Equal(["SessionStart", "UserPromptSubmit"], Installer.GetStatus().InstalledEvents);
    }

    [Fact]
    public void Install_keeps_other_hooks_and_is_idempotent()
    {
        File.WriteAllText(Installer.HooksFile, """
            { "hooks": {
                "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "other-tool check" } ] } ],
                "Stop": [ { "hooks": [ { "type": "command", "command": "notify-me" } ] } ] },
              "custom": 1 }
            """);

        Installer.Install(Exe, windows: true);
        Installer.Install(@"C:\new\fermata.exe", windows: true); // re-install replaces, never duplicates

        var hooks = Read()["hooks"]!;
        var prompt = hooks["UserPromptSubmit"]!.AsArray();
        Assert.Equal(2, prompt.Count);
        Assert.Equal("other-tool check", prompt[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Contains(@"C:\new\fermata.exe", prompt[1]!["hooks"]![0]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("notify-me", hooks["Stop"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(1, Read()["custom"]!.GetValue<int>());
        Assert.True(File.Exists(Installer.HooksFile + ".fermata.bak"));
    }

    [Fact]
    public void Uninstall_removes_only_fermata()
    {
        File.WriteAllText(Installer.HooksFile, """{ "hooks": { "UserPromptSubmit": [ { "hooks": [ { "type": "command", "command": "other-tool check" } ] } ] } }""");
        Installer.Install(Exe, windows: true);

        Assert.True(Installer.Uninstall());
        Assert.False(Installer.Uninstall());

        var hooks = Read()["hooks"]!.AsObject();
        Assert.False(hooks.ContainsKey("SessionStart"));
        Assert.Single(hooks["UserPromptSubmit"]!.AsArray());
        Assert.False(Installer.GetStatus().Installed);
    }

    [Fact]
    public void An_unparsable_hooks_file_is_never_overwritten()
    {
        File.WriteAllText(Installer.HooksFile, "[1, 2]");

        Assert.Throws<InvalidDataException>(() => Installer.Install(Exe, windows: true));
        Assert.Equal("[1, 2]", File.ReadAllText(Installer.HooksFile));
    }

    [Fact]
    public void Status_reports_a_missing_executable()
    {
        Installer.Install(@"C:\nowhere\fermata.exe", windows: true);

        var status = Installer.GetStatus();
        Assert.True(status.Installed);
        Assert.False(status.ExecutableExists);
    }
}

public sealed class CodexIntegrationTests : IDisposable
{
    private const string Exe = @"C:\Fermata\fermata.exe";
    private readonly string _home = Directory.CreateTempSubdirectory("fermata-codexint-").FullName;
    private readonly RecordingRunner _runner = new();

    public void Dispose() => Directory.Delete(_home, recursive: true);

    [Fact]
    public async Task Install_adds_hooks_skill_and_registers_the_mcp_server_through_codex()
    {
        var integration = new CodexIntegration(_home, "codex.exe", _runner);

        var warnings = await integration.InstallAsync(Exe, CancellationToken.None);

        Assert.Empty(warnings);
        Assert.True(integration.Hooks.GetStatus().Installed);
        Assert.StartsWith("---\nname: fermata\n", File.ReadAllText(integration.SkillFile).ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Equal(["mcp remove fermata", $"mcp add fermata -- {Exe} mcp"], _runner.Calls);
        Assert.All(_runner.Environments, env => Assert.Equal(_home, env["CODEX_HOME"]));
    }

    [Fact]
    public async Task Without_codex_the_mcp_part_is_skipped_with_a_warning()
    {
        var integration = new CodexIntegration(_home, null, _runner);

        var warnings = await integration.InstallAsync(Exe, CancellationToken.None);

        Assert.Single(warnings);
        Assert.Empty(_runner.Calls);
        Assert.True(integration.GetStatus().Installed);
    }

    [Fact]
    public async Task Uninstall_removes_all_parts()
    {
        var integration = new CodexIntegration(_home, "codex.exe", _runner);
        await integration.InstallAsync(Exe, CancellationToken.None);
        File.WriteAllText(Path.Combine(_home, "config.toml"), "model = \"x\"\n\n[mcp_servers.fermata]\ncommand = \"x\"\n");
        Assert.True(integration.GetStatus().McpRegistered);

        Assert.True(await integration.UninstallAsync(CancellationToken.None));

        Assert.False(integration.Hooks.GetStatus().Installed);
        Assert.False(File.Exists(integration.SkillFile));
        Assert.Equal("mcp remove fermata", _runner.Calls[^1]);
    }

    private sealed class RecordingRunner : Fermata.Platform.Processes.IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public List<IReadOnlyDictionary<string, string>> Environments { get; } = [];

        public Task<Fermata.Platform.Processes.ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
        {
            Calls.Add(string.Join(" ", arguments));
            Environments.Add(environment ?? new Dictionary<string, string>());
            return Task.FromResult(new Fermata.Platform.Processes.ProcessResult(0, "", ""));
        }

        public string? FindOnPath(string name) => null;
    }
}
