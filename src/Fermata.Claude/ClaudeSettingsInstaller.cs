using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fermata.Claude;

public sealed record ClaudeIntegrationStatus(
    string SettingsFile,
    IReadOnlyList<string> HookEvents,
    bool StatusLineWrapped,
    string? PreviousStatusLine,
    string? Command,
    bool ExecutableExists,
    bool SkillInstalled = false)
{
    public bool Installed => HookEvents.Count > 0 || StatusLineWrapped;
}

/// <summary>
/// Puts Fermata into Claude Code's user settings (<c>~/.claude/settings.json</c>, or
/// <c>$CLAUDE_CONFIG_DIR</c>): a status line wrapper that records <c>rate_limits</c> and then runs
/// the user's own status line, and hooks for session start/end, notifications, Stop and
/// StopFailure. Everything else in the file is kept; a file that does not parse is left alone.
/// </summary>
public sealed class ClaudeSettingsInstaller
{
    /// <summary>Claude Code hook event → <c>fermata claude hook</c> argument.</summary>
    public static readonly IReadOnlyDictionary<string, string> Events = new Dictionary<string, string>
    {
        ["SessionStart"] = "session-start",
        ["SessionEnd"] = "session-end",
        ["Notification"] = "notification",
        ["Stop"] = "stop",
        ["StopFailure"] = "stop-failure",
    };

    public const int TimeoutSeconds = 30;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private readonly ClaudeStateStore _state;

    public ClaudeSettingsInstaller(string claudeHome, ClaudeStateStore state)
    {
        ClaudeHome = claudeHome;
        _state = state;
    }

    public string ClaudeHome { get; }

    public string SettingsFile => Path.Combine(ClaudeHome, "settings.json");

    public string SkillFile => Path.Combine(ClaudeHome, "skills", "fermata", "SKILL.md");

    public static string DefaultClaudeHome()
    {
        var overridden = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Path.GetFullPath(overridden);
    }

    /// <summary>Claude Code runs hook and status line commands through a shell; a double-quoted path works in bash and cmd.</summary>
    public static string BuildCommand(string executable, string arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        return $"\"{executable.Replace("\"", "\\\"", StringComparison.Ordinal)}\" {arguments}";
    }

    public void Install(string executable)
    {
        var root = LoadOrEmpty();

        var statusLine = root["statusLine"];
        if (!IsOurs(statusLine?["command"]?.GetValue<string>()))
        {
            _state.SavePreviousStatusLine(statusLine);
        }

        var wrapper = new JsonObject
        {
            ["type"] = "command",
            ["command"] = BuildCommand(executable, "claude statusline"),
        };
        if (statusLine?["padding"] is { } padding)
        {
            wrapper["padding"] = padding.DeepClone();
        }

        root["statusLine"] = wrapper;

        var hooks = root["hooks"] as JsonObject ?? [];
        root["hooks"] = hooks;
        foreach (var (claudeEvent, argument) in Events)
        {
            var groups = RemoveOurs(hooks[claudeEvent] as JsonArray);
            groups.Add((JsonNode)new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = BuildCommand(executable, $"claude hook {argument}"),
                    ["timeout"] = TimeoutSeconds,
                }),
            });
            hooks[claudeEvent] = groups;
        }

        Save(root);
        Directory.CreateDirectory(Path.GetDirectoryName(SkillFile)!);
        File.WriteAllText(SkillFile, SkillText);
    }

    /// <summary>Removes the hooks and puts the user's status line back. True when something changed.</summary>
    public bool Uninstall()
    {
        if (!File.Exists(SettingsFile))
        {
            return false;
        }

        var root = LoadOrEmpty();
        var changed = false;
        if (File.Exists(SkillFile))
        {
            Directory.Delete(Path.GetDirectoryName(SkillFile)!, recursive: true);
            changed = true;
        }

        if (IsOurs(root["statusLine"]?["command"]?.GetValue<string>()))
        {
            var original = _state.LoadOriginalStatusLine();
            if (original is null)
            {
                root.Remove("statusLine");
            }
            else
            {
                root["statusLine"] = original;
            }

            _state.ForgetPreviousStatusLine();
            changed = true;
        }

        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var claudeEvent in Events.Keys)
            {
                if (hooks[claudeEvent] is not JsonArray groups)
                {
                    continue;
                }

                var kept = RemoveOurs(groups);
                if (kept.Count != groups.Count || CountHandlers(kept) != CountHandlers(groups))
                {
                    changed = true;
                }

                if (kept.Count == 0)
                {
                    hooks.Remove(claudeEvent);
                }
                else
                {
                    hooks[claudeEvent] = kept;
                }
            }
        }

        if (changed)
        {
            Save(root);
        }

        return changed;
    }

    public ClaudeIntegrationStatus GetStatus()
    {
        var events = new List<string>();
        string? command = null;
        var wrapped = false;
        if (File.Exists(SettingsFile))
        {
            var root = LoadOrEmpty();
            var statusCommand = root["statusLine"]?["command"]?.GetValue<string>();
            if (IsOurs(statusCommand))
            {
                wrapped = true;
                command = statusCommand;
            }

            if (root["hooks"] is JsonObject hooks)
            {
                foreach (var claudeEvent in Events.Keys)
                {
                    var ours = (hooks[claudeEvent] as JsonArray ?? []).SelectMany(Handlers).Select(h => h["command"]?.GetValue<string>()).FirstOrDefault(IsOurs);
                    if (ours is not null)
                    {
                        events.Add(claudeEvent);
                        command ??= ours;
                    }
                }
            }
        }

        return new ClaudeIntegrationStatus(SettingsFile, events, wrapped, _state.LoadPreviousStatusLine(), command, command is not null && File.Exists(ExecutableOf(command)), File.Exists(SkillFile));
    }

    internal const string SkillText = """
        ---
        name: fermata
        description: Long Claude Code work across usage limits with Fermata. Use when the user asks about Claude usage limits, Fermata jobs, resuming after a limit, or before starting long unattended work.
        ---

        # Fermata

        Fermata supervises long jobs across usage limits: before the quota runs out it asks for a handoff note and records a git checkpoint, waits for the reset and resumes the same session.

        - Call the `fermata_jobs` tool (Fermata MCP server) to see supervised jobs: status, why one waits or is blocked, and when it resumes.
        - If Fermata asks you (through a Stop hook) to write `.fermata/handoff.md`, write it as instructed: Active Goal, Completed, Current Work, Remaining, Verification, Important Decisions, Next Best Action, Do Not. Keep it concrete and short, then stop without starting new work.
        - When you resume after a pause, read `.fermata/handoff.md` first, check `git status` and `git log -3`, then continue with the Next Best Action.
        - For long unattended work, suggest `fermata run --provider claude --objective "…"` (optionally `--at 07:30` or `--in 2h`). The user manages jobs with `fermata jobs`, `fermata pause <id>`, `fermata resume <id> [--now|--force]` and `fermata cancel <id>`; do not run these unless asked.
        - Claude reset credits can only be used on claude.ai (Limit resets). Never try to redeem one yourself; you may tell the user that they can.
        """;

    internal static bool IsOurs(string? command) =>
        command is not null
        && command.Contains("fermata", StringComparison.OrdinalIgnoreCase)
        && (command.Contains(" claude hook ", StringComparison.Ordinal) || command.EndsWith(" claude statusline", StringComparison.Ordinal));

    private static string ExecutableOf(string command)
    {
        var start = command.IndexOf('"', StringComparison.Ordinal);
        var end = command.IndexOf('"', start + 1);
        return start >= 0 && end > start ? command[(start + 1)..end] : "";
    }

    private static IEnumerable<JsonObject> Handlers(JsonNode? group) => (group?["hooks"] as JsonArray ?? []).OfType<JsonObject>();

    private static int CountHandlers(JsonArray groups) => groups.Sum(g => Handlers(g).Count());

    private static JsonArray RemoveOurs(JsonArray? groups)
    {
        var result = new JsonArray();
        foreach (var group in groups ?? [])
        {
            if (group is not JsonObject obj)
            {
                result.Add(group?.DeepClone());
                continue;
            }

            var copy = (JsonObject)obj.DeepClone();
            if (copy["hooks"] is JsonArray handlers)
            {
                foreach (var handler in handlers.OfType<JsonObject>().Where(h => IsOurs(h["command"]?.GetValue<string>())).ToList())
                {
                    handlers.Remove(handler);
                }

                if (handlers.Count == 0)
                {
                    continue;
                }
            }

            result.Add((JsonNode)copy);
        }

        return result;
    }

    private JsonObject LoadOrEmpty()
    {
        if (!File.Exists(SettingsFile))
        {
            return [];
        }

        var text = File.ReadAllText(SettingsFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // A file we cannot parse belongs to the user: refuse rather than overwrite it.
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? throw new InvalidDataException($"{SettingsFile} is not a JSON object; fix or remove it first.");
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(ClaudeHome);
        if (File.Exists(SettingsFile))
        {
            File.Copy(SettingsFile, SettingsFile + ".fermata.bak", overwrite: true);
        }

        var temp = SettingsFile + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + Environment.NewLine);
        File.Move(temp, SettingsFile, overwrite: true);
    }
}
