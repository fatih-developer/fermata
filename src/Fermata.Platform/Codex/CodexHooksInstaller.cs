using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fermata.Platform.Codex;

public sealed record CodexHookStatus(string HooksFile, IReadOnlyList<string> InstalledEvents, string? Command, bool ExecutableExists)
{
    public bool Installed => InstalledEvents.Count > 0;

    /// <summary>Installed by ResetMe (before the rename); re-install to point it at fermata.</summary>
    public bool IsLegacy => Command is not null && CodexHooksInstaller.IsLegacy(Command);
}

/// <summary>
/// Adds Fermata's command hooks to Codex's user-level <c>hooks.json</c> (<c>$CODEX_HOME</c>, default
/// <c>~/.codex</c>) and removes them again. Other hooks in the file are left untouched. Codex marks
/// new hooks as untrusted, so the user approves them once in Codex (startup review or <c>/hooks</c>).
/// </summary>
public sealed class CodexHooksInstaller
{
    /// <summary>Codex event name → <c>fermata hook</c> argument.</summary>
    public static readonly IReadOnlyDictionary<string, string> Events = new Dictionary<string, string>
    {
        ["SessionStart"] = "session-start",
        ["UserPromptSubmit"] = "user-prompt-submit",
    };

    public const int TimeoutSeconds = 5;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public CodexHooksInstaller(string codexHome)
    {
        CodexHome = codexHome;
    }

    public string CodexHome { get; }

    public string HooksFile => Path.Combine(CodexHome, "hooks.json");

    public static string DefaultCodexHome()
    {
        var overridden = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : Path.GetFullPath(overridden);
    }

    /// <summary>
    /// The hook command line. Codex runs hooks through PowerShell on Windows (a bare quoted path
    /// is a string there, so <c>&amp;</c> is required) and through sh elsewhere.
    /// </summary>
    public static string BuildCommand(string executable, string hookArgument, bool windows)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        return windows
            ? $"& '{executable.Replace("'", "''", StringComparison.Ordinal)}' hook {hookArgument}"
            : $"'{executable.Replace("'", "'\\''", StringComparison.Ordinal)}' hook {hookArgument}";
    }

    public void Install(string executable) => Install(executable, OperatingSystem.IsWindows());

    internal void Install(string executable, bool windows)
    {
        var root = LoadOrEmpty();
        var hooks = root["hooks"] as JsonObject ?? [];
        root["hooks"] = hooks;

        foreach (var (codexEvent, argument) in Events)
        {
            var groups = RemoveOurs(hooks[codexEvent] as JsonArray);
            groups.Add((JsonNode)new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = BuildCommand(executable, argument, windows),
                    ["timeout"] = TimeoutSeconds,
                }),
            });
            hooks[codexEvent] = groups;
        }

        Save(root);
    }

    /// <summary>Returns true when something was removed.</summary>
    public bool Uninstall()
    {
        if (!File.Exists(HooksFile))
        {
            return false;
        }

        var root = LoadOrEmpty();
        if (root["hooks"] is not JsonObject hooks)
        {
            return false;
        }

        var removed = false;
        foreach (var codexEvent in Events.Keys)
        {
            if (hooks[codexEvent] is not JsonArray groups)
            {
                continue;
            }

            var before = CountHandlers(groups);
            var kept = RemoveOurs(groups);
            removed |= CountHandlers(kept) != before;
            if (kept.Count == 0)
            {
                hooks.Remove(codexEvent);
            }
            else
            {
                hooks[codexEvent] = kept;
            }
        }

        if (removed)
        {
            Save(root);
        }

        return removed;
    }

    public CodexHookStatus GetStatus()
    {
        var installed = new List<string>();
        string? command = null;
        if (File.Exists(HooksFile) && LoadOrEmpty()["hooks"] is JsonObject hooks)
        {
            foreach (var codexEvent in Events.Keys)
            {
                var ours = (hooks[codexEvent] as JsonArray ?? [])
                    .SelectMany(Handlers)
                    .Select(h => h["command"]?.GetValue<string>())
                    .FirstOrDefault(IsOurs);
                if (ours is not null)
                {
                    installed.Add(codexEvent);
                    command ??= ours;
                }
            }
        }

        return new CodexHookStatus(HooksFile, installed, command, command is not null && File.Exists(ExecutableOf(command)));
    }

    /// <summary>
    /// Ours: a <c>fermata</c> (or, before the rename, <c>resetme</c>) executable followed by
    /// <c>hook &lt;event&gt;</c>. Re-installing replaces a ResetMe hook with the Fermata one.
    /// </summary>
    internal static bool IsOurs(string? command) =>
        command is not null
        && (command.Contains("fermata", StringComparison.OrdinalIgnoreCase) || IsLegacy(command))
        && Events.Values.Any(arg => command.EndsWith($" hook {arg}", StringComparison.Ordinal));

    internal static bool IsLegacy(string command) =>
        command.Contains("resetme", StringComparison.OrdinalIgnoreCase)
        && !command.Contains("fermata", StringComparison.OrdinalIgnoreCase);

    private static string ExecutableOf(string command)
    {
        var start = command.IndexOf('\'', StringComparison.Ordinal);
        var end = command.LastIndexOf('\'');
        return start >= 0 && end > start
            ? command[(start + 1)..end].Replace("''", "'", StringComparison.Ordinal).Replace("'\\''", "'", StringComparison.Ordinal)
            : "";
    }

    private static IEnumerable<JsonObject> Handlers(JsonNode? group) =>
        (group?["hooks"] as JsonArray ?? []).OfType<JsonObject>();

    private static int CountHandlers(JsonArray groups) => groups.Sum(g => Handlers(g).Count());

    /// <summary>A copy of <paramref name="groups"/> without our handlers; groups left empty are dropped.</summary>
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
        if (!File.Exists(HooksFile))
        {
            return [];
        }

        var text = File.ReadAllText(HooksFile);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        // A file we cannot parse belongs to the user: refuse rather than overwrite it.
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException($"{HooksFile} is not a JSON object; fix or remove it first.");
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(CodexHome);
        if (File.Exists(HooksFile))
        {
            File.Copy(HooksFile, HooksFile + ".fermata.bak", overwrite: true);
        }

        var temp = HooksFile + $".{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, root.ToJsonString(Indented) + Environment.NewLine);
        File.Move(temp, HooksFile, overwrite: true);
    }
}
