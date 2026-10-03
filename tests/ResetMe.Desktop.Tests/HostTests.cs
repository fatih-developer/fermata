using Microsoft.Extensions.Logging.Abstractions;
using ResetMe.Core.Policies;
using ResetMe.Desktop.Services;
using ResetMe.Desktop.ViewModels;
using ResetMe.Platform;
using ResetMe.Platform.Autostart;
using Xunit;

namespace ResetMe.Desktop.Tests;

public sealed class HostTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("resetme-desktop-").FullName;
    private readonly FakeCodex _codex = new();
    private readonly FakeAutostart _autostart = new();
    private readonly RecordingNotifier _notifier = new();
    private DesktopHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <param name="mode">Manual keeps the monitor from resetting on its own, isolating "Reset now".</param>
    private DesktopHost CreateHost(GuardMode mode = GuardMode.Confirm)
    {
        // Fast verification so reset flows finish quickly.
        var options = new GuardOptions { Mode = mode };
        options.Reset.VerifyAfterSeconds = 0;
        options.Reset.CooldownSeconds = 0;
        new TomlConfigStore(Path.Combine(_root, "config.toml")).Save(options);

        _host = new DesktopHost(new AppPaths(_root), _codex, _autostart, _ => _notifier, NullLoggerFactory.Instance, new FixedTime(Fixtures.Now));
        return _host;
    }

    [Fact]
    public async Task Reset_now_does_nothing_when_codex_is_not_limited()
    {
        var host = CreateHost();
        var ui = new FakeUi(answer: true);
        host.Start(ui);

        await host.ResetNowAsync();

        Assert.Equal(0, ui.Questions);
        Assert.Empty(_codex.ConsumeKeys);
        Assert.Contains(host.ViewModel.Events, e => e.Contains("nothing to reset", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reset_now_asks_and_redeems_exactly_one_credit()
    {
        _codex.Usage = Fixtures.Blocked();
        var host = CreateHost(GuardMode.Manual);
        var ui = new FakeUi(answer: true);
        host.Start(ui);

        await host.ResetNowAsync();

        Assert.Equal(1, ui.Questions);
        Assert.Single(_codex.ConsumeKeys);
        Assert.Contains(host.ViewModel.Events, e => e.Contains("Codex reset applied", StringComparison.Ordinal));
        Assert.Equal(HealthKind.Ok, host.ViewModel.Health);
        lock (_notifier.Shown)
        {
            Assert.Contains(_notifier.Shown, n => n.Title == "Codex reset applied");
        }
    }

    [Fact]
    public async Task Declining_uses_no_credit()
    {
        _codex.Usage = Fixtures.Blocked();
        var host = CreateHost(GuardMode.Manual);
        var ui = new FakeUi(answer: false);
        host.Start(ui);

        await host.ResetNowAsync();

        Assert.Equal(1, ui.Questions);
        Assert.Empty(_codex.ConsumeKeys);
        Assert.Contains(host.ViewModel.Events, e => e.Contains("Reset declined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Monitor_asks_through_the_dialog_and_notifies_once()
    {
        _codex.Usage = Fixtures.Blocked();
        var host = CreateHost();
        var ui = new FakeUi(answer: true);

        host.Start(ui);
        await WaitUntil(() => host.ViewModel.Events.Any(e => e.Contains("Codex reset applied", StringComparison.Ordinal)));

        Assert.Equal(1, ui.Questions);
        Assert.Single(_codex.ConsumeKeys);
        Assert.Equal("5-hour Codex limit reached.", ui.LastQuestion?.Headline);
        lock (_notifier.Shown)
        {
            Assert.Single(_notifier.Shown, n => n.Title == "Codex reset applied");
            Assert.Single(_notifier.Shown, n => n.Title == "5-hour Codex limit reached");
        }
    }

    [Fact]
    public async Task Pending_attempt_is_shown_and_finished_with_its_own_key()
    {
        _codex.Usage = Fixtures.Blocked();
        var host = CreateHost(GuardMode.Manual);
        new JsonResetStateStore(Path.Combine(_root, "state.json")).Save(new Core.Reset.ResetState
        {
            Pending = new Core.Reset.PendingResetAttempt("crash-key", "credit-1", "episode", Fixtures.Now),
        });

        host.Start(new FakeUi(answer: false));
        await WaitUntil(() => host.ViewModel.HasPendingAttempt);
        Assert.True(host.ViewModel.FinishPendingCommand.CanExecute(null));

        await host.FinishPendingAsync();

        Assert.Equal(["crash-key"], _codex.ConsumeKeys);
        Assert.False(host.ViewModel.HasPendingAttempt);
        Assert.Null(new JsonResetStateStore(Path.Combine(_root, "state.json")).Load().Pending);
    }

    [Fact]
    public async Task Saving_settings_writes_config_and_registers_autostart()
    {
        var host = CreateHost();
        host.Start(new FakeUi(answer: false));

        host.ViewModel.ModeIndex = (int)GuardMode.Manual;
        host.ViewModel.IntervalSeconds = 60;
        host.ViewModel.StartAtLogin = true;
        await host.SaveSettingsAsync();

        var saved = new TomlConfigStore(Path.Combine(_root, "config.toml")).Load().Options;
        Assert.Equal(GuardMode.Manual, saved.Mode);
        Assert.Equal(60, saved.Monitor.IntervalSeconds);
        Assert.Equal(AutostartTarget.Desktop, _autostart.Enabled?.Target);
        Assert.Equal(["--minimized"], _autostart.Enabled!.Arguments);
        Assert.Equal("Saved.", host.ViewModel.SettingsMessage);

        host.ViewModel.StartAtLogin = false;
        await host.SaveSettingsAsync();
        Assert.Null(_autostart.Enabled);
    }

    [Fact]
    public async Task Tray_mode_change_is_persisted()
    {
        var host = CreateHost();
        host.Start(new FakeUi(answer: false));

        await host.ChangeModeAsync(GuardMode.Manual);

        Assert.Equal(GuardMode.Manual, new TomlConfigStore(Path.Combine(_root, "config.toml")).Load().Options.Mode);
        Assert.Equal(0, host.ViewModel.ModeIndex);
    }

    [Fact]
    public async Task Automatic_mode_requires_explicit_confirmation()
    {
        var host = CreateHost();
        var ui = new FakeUi(answer: false, answerQuestions: false);
        host.Start(ui);

        await host.ChangeModeAsync(GuardMode.Automatic);

        Assert.Equal(["Enable automatic mode?"], ui.AskedTitles);
        Assert.Equal(GuardMode.Confirm, new TomlConfigStore(Path.Combine(_root, "config.toml")).Load().Options.Mode);
        Assert.Equal((int)GuardMode.Confirm, host.ViewModel.ModeIndex);
        Assert.Equal("Automatic mode was not enabled.", host.ViewModel.SettingsMessage);
    }

    [Fact]
    public async Task Automatic_mode_is_enabled_after_confirmation_and_not_asked_again()
    {
        var host = CreateHost();
        var ui = new FakeUi(answer: false, answerQuestions: true);
        host.Start(ui);

        await host.ChangeModeAsync(GuardMode.Automatic);
        await host.SaveSettingsAsync(); // already automatic: no second question

        Assert.Single(ui.AskedTitles);
        Assert.Equal(GuardMode.Automatic, new TomlConfigStore(Path.Combine(_root, "config.toml")).Load().Options.Mode);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(50);
        }
    }
}
