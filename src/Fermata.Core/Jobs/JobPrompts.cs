namespace Fermata.Core.Jobs;

/// <summary>The texts Fermata gives the agent. Plain instructions; no secrets, no tool-specific syntax.</summary>
public static class JobPrompts
{
    /// <summary>Relative to the job's working directory; kept out of git through .git/info/exclude.</summary>
    public const string HandoffFile = ".fermata/handoff.md";

    public static string Start(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return $"""
            {job.Objective}

            (Fermata supervises this task across usage limits. If you are asked to write a handoff
            note, write {HandoffFile} as instructed and stop. When you resume later, read that file first.)
            """;
    }

    public static string Resume(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var checkpoint = job.LastCheckpoint is { } c
            ? $"At the checkpoint the branch was {c.Branch ?? "unknown"} at {Short(c.Head)}{(c.Dirty ? " with uncommitted changes" : "")}."
            : "There is no recorded checkpoint.";
        return $"""
            Continue the task after a usage-limit pause.

            1. Read {HandoffFile} if it exists.
            2. Check the repository (git status, git log -3). {checkpoint}
            3. Continue with the "Next Best Action" from the handoff note, or with the next step of the objective.

            Objective: {job.Objective}
            """;
    }

    /// <summary>Stop hook reason: write the handoff note now, start nothing new.</summary>
    public static string Handoff(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return $"""
            Fermata: usage is almost exhausted. Before stopping, write {HandoffFile} (create the folder) with:

            # Resume Context
            ## Active Goal
            ## Completed
            ## Current Work
            ## Remaining
            ## Verification
            ## Important Decisions
            ## Next Best Action
            ## Do Not

            Keep it short and concrete (files, commands, test results). Do not start new work; stop after writing it.
            Objective: {job.Objective}
            """;
    }

    private static string Short(string? head) => head is { Length: > 10 } ? head[..10] : head ?? "unknown";
}
