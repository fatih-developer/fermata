using ResetMe.Cli.Output;
using ResetMe.Core.Policies;
using ResetMe.Core.Reset;

namespace ResetMe.Cli.Commands;

internal static class ResetCommand
{
    public static async Task<int> RunAsync(bool yes, bool force, bool verbose, CancellationToken cancellationToken)
    {
        var runtime = GuardRuntime.Load();
        await using var client = await runtime.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var account = await client.GetAccountAsync(cancellationToken).ConfigureAwait(false);
        if (!account.IsAuthenticated)
        {
            Console.Error.WriteLine("AUTH_REQUIRED: Codex is not logged in. Run `codex login` first. No reset was sent.");
            return ExitCodes.AuthRequired;
        }

        var state = runtime.StateStore.Load();
        var usage = await client.GetUsageAsync(includeCreditDetails: true, cancellationToken).ConfigureAwait(false);
        var now = TimeProvider.System.GetUtcNow();

        if (state.Pending is { } pending)
        {
            Console.WriteLine($"An earlier reset attempt (started {pending.StartedAt.ToLocalTime():g}) has no confirmed result.");
            Console.WriteLine("It will be retried with the same idempotency key, which cannot consume a second credit.");
            if (!ResetPresenter.Confirm(yes, "Continue? [y/N] "))
            {
                return ExitCodes.Declined;
            }
        }
        else
        {
            var assessment = LimitEvaluator.Assess(usage, runtime.Options, now, ignoreNaturalResetThreshold: force);
            if (!assessment.Blocked)
            {
                Console.WriteLine("Codex is not rate-limited right now; nothing to reset. No credit was used.");
                return ExitCodes.Ok;
            }

            if (assessment.NoOfferReason is { } reason)
            {
                Console.WriteLine(ResetPresenter.Explain(reason, usage, assessment, runtime.Options, now));
                return ExitCodes.Error;
            }

            ResetPresenter.PrintOffer(usage, assessment, now);
            if (!ResetPresenter.Confirm(yes, "Use one reset credit? [y/N] "))
            {
                Console.WriteLine("Waiting. No credit was used.");
                return ExitCodes.Declined;
            }
        }

        var manager = new ResetManager(client, runtime.StateStore, runtime.Lock, runtime.Options, TimeProvider.System);
        if (verbose)
        {
            manager.StateChanged += s => Console.WriteLine($"  → {s}");
        }

        var report = await manager.ExecuteAsync(new ResetRequest(Force: force), cancellationToken).ConfigureAwait(false);

        if (verbose)
        {
            Console.WriteLine($"  outcome={report.Outcome?.ToString() ?? "unknown"} key={report.IdempotencyKey ?? "-"} resumed={report.ResumedPendingAttempt}");
        }

        return ResetPresenter.PrintReport(report, runtime.Options, now);
    }
}
