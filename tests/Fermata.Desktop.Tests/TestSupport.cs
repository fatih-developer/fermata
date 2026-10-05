using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Fermata.Core.Domain;
using Fermata.Core.Ports;
using Fermata.Desktop;
using Fermata.Desktop.Services;
using Fermata.Desktop.Tests;
using Fermata.Desktop.ViewModels;
using Fermata.Platform.Autostart;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Fermata.Desktop.Tests;

public static class TestAppBuilder
{
    // The real App with Skia rendering, so frames can be captured as images.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "avares://Avalonia.Fonts.Inter/Assets#Inter" });
}

internal static class Fixtures
{
    public static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public static CodexUsage Blocked(long credits = 2) => new()
    {
        AccountId = "acct",
        UsageAllowed = false,
        ReachedType = "rate_limit_reached",
        FiveHour = new UsageWindow(100, 300, Now.AddHours(2).AddMinutes(40)),
        Weekly = new UsageWindow(64, 10080, Now.AddDays(3)),
        AvailableResetCount = credits,
        ResetCreditsReported = true,
        Credits = credits > 0
            ? [new ResetCredit("credit-1", ResetCreditStatus.Available, Now.AddDays(-1), Now.AddDays(9), "Full reset (Weekly + 5 hr)")]
            : [],
        ReadAt = Now,
    };

    public static CodexUsage Healthy(double fiveHour = 42, double weekly = 30, long credits = 2) => Blocked(credits) with
    {
        UsageAllowed = true,
        ReachedType = null,
        FiveHour = new UsageWindow(fiveHour, 300, Now.AddHours(3)),
        Weekly = new UsageWindow(weekly, 10080, Now.AddDays(4)),
    };

    public static string ScreenshotDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("FERMATA_SCREENSHOTS")
            ?? Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>Scripted Codex: usage reads return the current value; consume flips it to healthy.</summary>
internal sealed class FakeCodex : ICodexConnector, ICodexConnection
{
    public CodexUsage Usage { get; set; } = Fixtures.Healthy();

    public List<string> ConsumeKeys { get; } = [];

    public event Action? UsageChanged;

    public event Action? AccountChanged;

    public Task<ICodexConnection> ConnectAsync(CancellationToken cancellationToken) => Task.FromResult<ICodexConnection>(this);

    public Task<CodexUsage> GetUsageAsync(bool includeCreditDetails, CancellationToken cancellationToken) => Task.FromResult(Usage);

    public Task<AccountStatus> GetAccountAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AccountStatus(true, "chatgpt", "plus"));

    public Task<ResetOutcome> ConsumeResetAsync(string idempotencyKey, string? creditId, CancellationToken cancellationToken)
    {
        ConsumeKeys.Add(idempotencyKey);
        Usage = Fixtures.Healthy(0, 0, Usage.AvailableResetCount - 1);
        return Task.FromResult(ResetOutcome.Reset);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Touch()
    {
        UsageChanged?.Invoke();
        AccountChanged?.Invoke();
    }
}

internal sealed class FakeUi(bool answer, bool answerQuestions = false) : IDesktopUi
{
    public List<string> AskedTitles { get; } = [];

    public Task<bool> AskAsync(string title, string message, string accept, string cancel)
    {
        AskedTitles.Add(title);
        return Task.FromResult(answerQuestions);
    }

    public int Questions { get; private set; }

    public ConfirmViewModel? LastQuestion { get; private set; }

    /// <summary>Serializes "UI thread" work: the monitor posts from a pool thread while tests read the view model.</summary>
    public static readonly Lock UiLock = new();

    public void Post(Action action)
    {
        lock (UiLock)
        {
            action();
        }
    }

    public Task<bool> ConfirmAsync(ConfirmViewModel content)
    {
        Questions++;
        LastQuestion = content;
        return Task.FromResult(answer);
    }
}

internal sealed class FakeUpdates(string latest, bool canInstall = true) : IUpdateService
{
    public int Checks { get; private set; }

    public int Installs { get; private set; }

    public Exception? FailCheckWith { get; set; }

    public bool CanInstall => canInstall;

    public Task<Platform.Updates.UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        if (FailCheckWith is { } error)
        {
            throw error;
        }

        var pkg = new Platform.Updates.ReleaseAsset("fermata-test.zip", new Uri("https://dl.test/pkg"), 1);
        var sums = new Platform.Updates.ReleaseAsset("SHA256SUMS.txt", new Uri("https://dl.test/sums"), 1);
        var info = new Platform.Updates.ReleaseInfo("v" + latest, latest, new Uri("https://github.test/release"), [pkg, sums]);
        var newer = Platform.Updates.UpdateChecker.CompareVersions(latest, "0.3.0") > 0;
        return Task.FromResult(new Platform.Updates.UpdateCheckResult("0.3.0", info, newer, pkg, sums));
    }

    public Task<Platform.Updates.ApplyResult> InstallAsync(Platform.Updates.UpdateCheckResult check, IProgress<string> progress, CancellationToken cancellationToken)
    {
        Installs++;
        return Task.FromResult(Platform.Updates.ApplyResult.Scheduled);
    }
}

internal sealed class FakeAutostart : IAutostartManager
{
    public AutostartEntry? Enabled { get; private set; }

    public AutostartStatus GetStatus(AutostartTarget target) => new(Enabled is not null, "fake", "fake", null);

    public Task EnableAsync(AutostartEntry entry, CancellationToken cancellationToken)
    {
        Enabled = entry;
        return Task.CompletedTask;
    }

    public Task DisableAsync(AutostartTarget target, CancellationToken cancellationToken)
    {
        Enabled = null;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNotifier : INotifier
{
    public List<Notification> Shown { get; } = [];

    public string Mechanism => "recording";

    public bool IsAvailable => true;

    public Task<bool> ShowAsync(Notification notification, CancellationToken cancellationToken)
    {
        lock (Shown)
        {
            Shown.Add(notification);
        }

        return Task.FromResult(true);
    }
}
