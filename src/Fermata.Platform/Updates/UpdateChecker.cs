using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fermata.Platform.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

public sealed record ReleaseInfo(string Tag, string Version, Uri PageUrl, IReadOnlyList<ReleaseAsset> Assets);

public sealed record UpdateCheckResult(
    string CurrentVersion,
    ReleaseInfo Latest,
    bool UpdateAvailable,
    ReleaseAsset? Package,
    ReleaseAsset? Checksums);

/// <summary>
/// Reads the latest GitHub release (PRD MVP-3 self-update). The only network call Fermata makes
/// besides Codex itself; it sends nothing about the user. Disable with [updates] check_automatically.
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultFeed = "https://api.github.com/repos/fatih-developer/fermata/releases/latest";
    public const string ChecksumFileName = "SHA256SUMS.txt";

    /// <summary>Overrides the feed (tests, mirrors). Must be an http(s) URL returning GitHub's release JSON.</summary>
    public const string FeedEnvironmentVariable = "FERMATA_UPDATE_FEED";

    private readonly HttpClient _http;
    private readonly Uri _feed;

    public UpdateChecker(HttpClient http, string? feedUrl = null)
    {
        _http = http;
        _feed = new Uri(feedUrl ?? Environment.GetEnvironmentVariable(FeedEnvironmentVariable) ?? DefaultFeed);
    }

    /// <summary>Running version without build metadata, e.g. "0.3.0" or "0.3.0-ci.7".</summary>
    public static string CurrentVersion()
    {
        var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";
        return informational.Split('+')[0];
    }

    /// <summary>Release asset name for this platform (matches scripts/package.sh), or null if none is published.</summary>
    public static string? PackageNameFor(string runtimeIdentifier) => runtimeIdentifier switch
    {
        "win-x64" or "win-arm64" => $"fermata-{runtimeIdentifier}.zip",
        "linux-x64" or "linux-arm64" => $"fermata-{runtimeIdentifier}.tar.gz",
        "osx-arm64" => "fermata-macos-arm64.tar.gz",
        "osx-x64" => "fermata-macos-x64.tar.gz",
        _ => null,
    };

    public static string CurrentRuntimeIdentifier()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, string? packageName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _feed);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Fermata", SafeVersion(currentVersion)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);

        var latest = ParseRelease(json);
        var package = packageName is null ? null : latest.Assets.FirstOrDefault(a => a.Name == packageName);
        var checksums = latest.Assets.FirstOrDefault(a => a.Name == ChecksumFileName);
        var newer = CompareVersions(latest.Version, currentVersion) > 0;
        return new UpdateCheckResult(currentVersion, latest, newer, package, checksums);
    }

    internal static ReleaseInfo ParseRelease(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Release feed is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Release feed is not valid JSON.", ex);
        }

        var tag = root["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("Release has no tag_name.");
        var page = root["html_url"]?.GetValue<string>() ?? "https://github.com/fatih-developer/fermata/releases";
        var assets = new List<ReleaseAsset>();
        if (root["assets"] is JsonArray array)
        {
            foreach (var item in array.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>();
                var url = item["browser_download_url"]?.GetValue<string>();
                if (name is not null && url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    && uri.Scheme is "https" or "http")
                {
                    assets.Add(new ReleaseAsset(name, uri, item["size"]?.GetValue<long>() ?? 0));
                }
            }
        }

        return new ReleaseInfo(tag, tag.TrimStart('v', 'V'), new Uri(page), assets);
    }

    /// <summary>SemVer precedence on "major.minor.patch[-prerelease]"; build metadata is ignored.</summary>
    public static int CompareVersions(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        for (var i = 0; i < 3; i++)
        {
            var c = coreA[i].CompareTo(coreB[i]);
            if (c != 0)
            {
                return c;
            }
        }

        // A release ranks above its prereleases.
        if (preA.Length == 0 && preB.Length == 0)
        {
            return 0;
        }

        if (preA.Length == 0)
        {
            return 1;
        }

        if (preB.Length == 0)
        {
            return -1;
        }

        var partsA = preA.Split('.');
        var partsB = preB.Split('.');
        for (var i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            var numA = int.TryParse(partsA[i], NumberStyles.None, CultureInfo.InvariantCulture, out var na);
            var numB = int.TryParse(partsB[i], NumberStyles.None, CultureInfo.InvariantCulture, out var nb);
            var c = (numA, numB) switch
            {
                (true, true) => na.CompareTo(nb),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(partsA[i], partsB[i]),
            };
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return partsA.Length.CompareTo(partsB.Length);
    }

    private static (int[] Core, string Pre) Split(string version)
    {
        var v = version.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = v.IndexOf('-', StringComparison.Ordinal);
        var core = dash < 0 ? v : v[..dash];
        var pre = dash < 0 ? "" : v[(dash + 1)..];
        var numbers = core.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();
        while (numbers.Count < 3)
        {
            numbers.Add(0);
        }

        return ([.. numbers.Take(3)], pre);
    }

    private static string SafeVersion(string version) =>
        string.Concat(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-')) is { Length: > 0 } s ? s : "0";
}
