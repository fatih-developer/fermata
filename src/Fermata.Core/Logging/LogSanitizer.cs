using System.Text.RegularExpressions;

namespace Fermata.Core.Logging;

/// <summary>
/// Last line of defence for PRD §28: strips credential material and e-mail addresses from
/// anything written to the log, whatever its source (messages, properties, exceptions, stderr).
/// </summary>
public static partial class LogSanitizer
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }

        text = Jwt().Replace(text, "<jwt>");
        text = Bearer().Replace(text, "Bearer <redacted>");
        text = ApiKey().Replace(text, "<api-key>");
        text = GitHubToken().Replace(text, "<token>");
        text = SecretAssignment().Replace(text, "$1$2<redacted>");
        text = Email().Replace(text, "<email>");
        return text;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]+")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]+")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}")]
    private static partial Regex ApiKey();

    [GeneratedRegex(@"\bgh[opusr]_[A-Za-z0-9]{20,}")]
    private static partial Regex GitHubToken();

    // access_token=..., "refreshToken": "...", Cookie: ..., password=...
    [GeneratedRegex(@"(?i)([""']?\b(?:[a-z_]*token|secret|password|cookie|authorization|api[_-]?key)[""']?)(\s*[:=]\s*[""']?)[^\s""',;}]+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
