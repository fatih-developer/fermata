using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Fermata.Core.Logging;
using Fermata.Platform.Logging;

namespace Fermata.Platform.Diagnostics;

/// <summary>
/// "Diagnostics export" (PRD MVP-3): one zip with what is needed to debug Fermata and nothing
/// about the user's work (PRD §29). Contents: system.txt, config.toml, state.json, the last days of
/// logs and caller-supplied extras (e.g. doctor output). Every text entry is sanitized again.
/// Never included: Codex credentials (~/.codex), prompts, source code, e-mail addresses; paths under
/// the user profile are shown as "~".
/// </summary>
public static class DiagnosticsBundle
{
    public const int LogDays = 7;

    public static string DefaultFileName(DateTimeOffset now) =>
        $"fermata-diagnostics-{now.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.zip";

    public static string Create(
        AppPaths paths,
        string outputFile,
        IReadOnlyDictionary<string, string> extras,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(extras);
        var now = time.GetLocalNow();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputFile))!);
        var temp = outputFile + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            Add(zip, "system.txt", SystemInfo(paths, now));
            AddFileIfPresent(zip, "config.toml", paths.ConfigFile);
            AddFileIfPresent(zip, "state.json", paths.StateFile);
            AddFileIfPresent(zip, "state.json.bak", paths.StateFile + ".bak");

            if (Directory.Exists(paths.LogDirectory))
            {
                var oldest = DateOnly.FromDateTime(now.DateTime).AddDays(-(LogDays - 1));
                foreach (var log in Directory.EnumerateFiles(paths.LogDirectory, $"{FileLoggerProvider.FilePrefix}*{FileLoggerProvider.FileExtension}").Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(log)[FileLoggerProvider.FilePrefix.Length..];
                    if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day >= oldest)
                    {
                        AddFileIfPresent(zip, "logs/" + Path.GetFileName(log), log);
                    }
                }
            }

            foreach (var (name, content) in extras)
            {
                Add(zip, name, content);
            }
        }

        File.Move(temp, outputFile, overwrite: true);
        FilePermissions.RestrictToCurrentUser(outputFile, isDirectory: false);
        return outputFile;
    }

    private static string SystemInfo(AppPaths paths, DateTimeOffset now)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"Fermata {version}");
        b.AppendLine(CultureInfo.InvariantCulture, $"Created: {now:O}");
        b.AppendLine(CultureInfo.InvariantCulture, $"Time zone: {TimeZoneInfo.Local.Id}");
        b.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        b.AppendLine(CultureInfo.InvariantCulture, $"Runtime: {RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})");
        b.AppendLine(CultureInfo.InvariantCulture, $"Data directory: {paths.Root}");
        return b.ToString();
    }

    private static void AddFileIfPresent(ZipArchive zip, string entryName, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string content;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
        {
            content = reader.ReadToEnd();
        }

        Add(zip, entryName, content);
    }

    private static void Add(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(HideUserProfile(LogSanitizer.Redact(content)));
    }

    /// <summary>Paths under the user profile reveal the OS user name; bundles may be shared publicly.</summary>
    internal static string HideUserProfile(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile) || profile.Length < 4)
        {
            return text;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        text = text.Replace(profile, "~", comparison);

        // JSON log lines escape backslashes.
        return text.Replace(profile.Replace(@"\", @"\\", StringComparison.Ordinal), "~", comparison);
    }
}
