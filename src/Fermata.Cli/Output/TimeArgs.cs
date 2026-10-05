using System.Globalization;
using System.Text.RegularExpressions;

namespace Fermata.Cli.Output;

/// <summary>Parses <c>--at</c> (local clock time or date-time) and <c>--in</c> (durations like 90m, 2h30m, 1d).</summary>
internal static partial class TimeArgs
{
    private static readonly string[] AtFormats =
    [
        "HH:mm", "H:mm", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd",
    ];

    /// <summary>A clock time alone means its next occurrence; ISO 8601 with an offset is taken as is.</summary>
    public static DateTimeOffset ParseAt(string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Contains('Z', StringComparison.Ordinal) || OffsetSuffix().IsMatch(text))
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            {
                return exact;
            }
        }

        if (!DateTime.TryParseExact(text, AtFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local))
        {
            throw new FormatException($"'{text}' is not a time. Use HH:mm, \"yyyy-MM-dd HH:mm\" or ISO 8601.");
        }

        var at = new DateTimeOffset(local);
        if (text.Length <= 5 && at <= now)
        {
            at = at.AddDays(1); // "07:30" after 07:30 means tomorrow
        }

        return at;
    }

    public static TimeSpan ParseIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = DurationPattern().Match(text.Trim().ToLowerInvariant());
        if (!match.Success || match.Length == 0)
        {
            throw new FormatException($"'{text}' is not a duration. Use e.g. 45m, 2h, 1h30m or 1d.");
        }

        static int Part(Group g) => g.Success ? int.Parse(g.Value, CultureInfo.InvariantCulture) : 0;
        var span = new TimeSpan(Part(match.Groups["d"]), Part(match.Groups["h"]), Part(match.Groups["m"]), 0);
        return span > TimeSpan.Zero ? span : throw new FormatException($"'{text}' is not a positive duration.");
    }

    /// <summary>Exactly one of --at and --in, or neither.</summary>
    public static DateTimeOffset? Resolve(string? at, string? inText, DateTimeOffset now)
    {
        if (at is not null && inText is not null)
        {
            throw new FormatException("Use either --at or --in, not both.");
        }

        return at is not null ? ParseAt(at, now) : inText is not null ? now + ParseIn(inText) : null;
    }

    [GeneratedRegex(@"^(?:(?<d>\d+)d)?\s*(?:(?<h>\d+)h)?\s*(?:(?<m>\d+)m(?:in)?)?$")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"[+-]\d{2}:?\d{2}$")]
    private static partial Regex OffsetSuffix();
}
