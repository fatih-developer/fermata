using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fermata.Core.Jobs;
using Fermata.Core.Monitoring;
using Fermata.Core.Policies;
using Fermata.Desktop.Services;
using Fermata.Desktop.ViewModels;
using Fermata.Desktop.Views;
using Fermata.Platform;
using Fermata.Platform.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fermata.Desktop.Tests;

/// <summary>Jobs in the view model, the windows and the host.</summary>
public sealed class JobsUiTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("fermata-desktop-jobs-").FullName;
    private DesktopHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    private static Job Job(string id, JobProviderKind provider, JobStatus status, DateTimeOffset? resumeAt = null, string? reason = null) => new()
    {
        Id = id,
        Provider = provider,
        Cwd = "/repo",
        Objective = "Migrate the payment module to the new API and keep the tests green",
        Status = status,
        ResumeAt = resumeAt,
        BlockReason = reason,
        Session = new SessionRef(provider == JobProviderKind.Codex ? "019a-thread" : "e92725ea-9825", provider == JobProviderKind.Claude ? "e92725ea" : null),
        CreatedAt = Fixtures.Now.AddHours(-2),
        UpdatedAt = Fixtures.Now.AddMinutes(-5),
    };

    private static IReadOnlyList<Job> SampleJobs() =>
    [
        Job("payment-migration", JobProviderKind.Claude, JobStatus.WaitingQuota, Fixtures.Now.AddHours(2).AddMinutes(10), "Waiting for the 5-hour limit to reset."),
        Job("docs-rewrite", JobProviderKind.Codex, JobStatus.Running),
        Job("flaky-tests", JobProviderKind.Codex, JobStatus.BlockedApproval, reason: "Waiting for an approval."),
    ];

    private static MainViewModel ViewModel()
    {
        var vm = new MainViewModel(() => Task.CompletedTask, () => Task.CompletedTask, () => { }) { TimeZone = TimeZoneInfo.Utc };
        var usage = Fixtures.Healthy(91, 47);
        vm.ApplyUsage(usage, LimitEvaluator.Assess(usage, new GuardOptions(), Fixtures.Now), Fixtures.Now);
        vm.ApplyJobs(SampleJobs(), _ => [new JobEvent(Fixtures.Now.AddMinutes(-3), "waiting", "Waiting for the 5-hour limit to reset.")], Fixtures.Now);
        return vm;
    }

    [Fact]
    public void Rows_show_status_next_step_and_actions()
    {
        var vm = ViewModel();

        Assert.True(vm.HasJobs);
        Assert.True(vm.ShowClaudeLimitResets);
        Assert.Equal("3 job(s), 1 need you", vm.JobsSummary);

        var waiting = vm.Jobs.Single(j => j.Id == "payment-migration");
        Assert.Equal("Claude · waiting for quota · resumes in 2h 10m", waiting.Summary);
        Assert.True(waiting.CanResume);
        Assert.Equal("claude attach e92725ea", waiting.Attach);

        var blocked = vm.Jobs.Single(j => j.Id == "flaky-tests");
        Assert.True(blocked.NeedsAttention);
        Assert.Equal("codex resume 019a-thread", blocked.Attach);
    }

    [Fact]
    public void Finished_jobs_drop_out_after_a_day()
    {
        var vm = ViewModel();
        var old = Job("old", JobProviderKind.Codex, JobStatus.Completed) with { FinishedAt = Fixtures.Now.AddDays(-2) };
        var recent = Job("recent", JobProviderKind.Codex, JobStatus.Completed) with { FinishedAt = Fixtures.Now.AddHours(-1) };

        vm.ApplyJobs([old, recent], _ => [], Fixtures.Now);

        Assert.Equal(["recent"], vm.Jobs.Select(j => j.Id));
        Assert.Equal("", vm.JobsSummary);
        Assert.False(vm.ShowClaudeLimitResets);
    }

    [Fact]
    public async Task Pause_and_resume_go_through_the_job_folder()
    {
        var paths = new AppPaths(_root);
        var store = new JsonJobStore(paths.JobsDirectory);
        store.Save(Job("payment-migration", JobProviderKind.Claude, JobStatus.WaitingQuota, Fixtures.Now.AddHours(2)));
        _host = new DesktopHost(paths, new FakeCodex(), new FakeAutostart(), _ => new RecordingNotifier(), NullLoggerFactory.Instance, new FixedTime(Fixtures.Now))
        {
            ClaudeHome = Path.Combine(_root, ".claude"),
        };
        string? opened = null;
        _host.OpenUrl = url => opened = url;
        _host.RefreshJobs();

        var row = Assert.Single(_host.ViewModel.Jobs);
        Assert.True(_host.ViewModel.ShowClaudeLimitResets);

        _host.ViewModel.OpenClaudeLimitResetsCommand.Execute(null);
        Assert.Equal(new GuardOptions().Jobs.Claude.LimitResetsUrl, opened);

        var resume = row.ResumeCommand.ExecuteAsync(null);
        var request = Assert.Single(store.TakeRequests());
        Assert.Equal(JobRequestKind.Resume, request.Kind);
        Assert.True(request.Now);
        await resume;
    }

    [AvaloniaFact]
    public void Windows_render_jobs()
    {
        var vm = ViewModel();

        var window = new MainWindow { DataContext = vm, Width = 440, Height = 1000 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Save(window.CaptureRenderedFrame(), "main-window-jobs.png");
        window.AllowClose = true;
        window.Close();

        var flyout = new FlyoutWindow { DataContext = vm };
        flyout.Show();
        Dispatcher.UIThread.RunJobs();
        var frame = Save(flyout.CaptureRenderedFrame(), "flyout-jobs.png");
        Assert.True(frame.PixelSize.Height > 300);
        flyout.Close();
    }

    private static Bitmap Save(Bitmap? bitmap, string name)
    {
        Assert.NotNull(bitmap);
        bitmap.Save(Path.Combine(Fixtures.ScreenshotDirectory(), name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        return bitmap;
    }
}
