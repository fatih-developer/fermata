using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ResetMe.Platform;
using ResetMe.Platform.Logging;

namespace ResetMe.Cli.Commands;

/// <summary>`resetme logs`: the last N entries across the daily log files, newest last.</summary>
internal static class LogsCommand
{
    public static int Run(int tail, bool json, bool pathOnly)
    {
        var directory = AppPaths.Default().LogDirectory;
        if (pathOnly)
        {
            Console.WriteLine(directory);
            return ExitCodes.Ok;
        }

        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"No logs yet ({directory}).");
            return ExitCodes.Ok;
        }

        var lines = ReadTail(directory, Math.Max(1, tail));
        if (lines.Count == 0)
        {
            Console.WriteLine($"No log entries in {directory}.");
            return ExitCodes.Ok;
        }

        foreach (var line in lines)
        {
            Console.WriteLine(json ? line : Pretty(line));
        }

        return ExitCodes.Ok;
    }

    private static List<string> ReadTail(string directory, int tail)
    {
        var files = Directory
            .EnumerateFiles(directory, $"{FileLoggerProvider.FilePrefix}*{FileLoggerProvider.FileExtension}")
            .OrderDescending(StringComparer.Ordinal); // yyyy-MM-dd sorts chronologically

        var collected = new List<string>();
        foreach (var file in files)
        {
            var fileLines = ReadWithRetry(file).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            collected.InsertRange(0, fileLines.TakeLast(tail - collected.Count));
            if (collected.Count >= tail)
            {
                break;
            }
        }

        return collected;
    }

    /// <summary>Writers hold the file exclusively for a few milliseconds per batch.</summary>
    private static string ReadWithRetry(string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static string Pretty(string line)
    {
        try
        {
            if (JsonNode.Parse(line) is not JsonObject entry)
            {
                return line;
            }

            var ts = DateTimeOffset.TryParse(entry["ts"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : "?";
            var level = (entry["level"]?.GetValue<string>() ?? "?") switch
            {
                "Information" => "INF",
                "Warning" => "WRN",
                "Error" => "ERR",
                "Critical" => "CRT",
                "Debug" => "DBG",
                "Trace" => "TRC",
                var other => other,
            };
            var category = entry["category"]?.GetValue<string>() ?? "";
            category = category[(category.LastIndexOf('.') + 1)..];
            var text = $"{ts} {level} {category,-16} {entry["msg"]?.GetValue<string>()}";

            if (entry["exception"]?.GetValue<string>() is { } exception)
            {
                text += Environment.NewLine + "    " + exception.Split('\n')[0].Trim();
            }

            return text;
        }
        catch (JsonException)
        {
            return line;
        }
    }
}
