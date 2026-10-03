using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ResetMe.Platform.Updates;

namespace ResetMe.Core.Tests;

public sealed class UpdateTests : IDisposable
{
    private const string Feed = "https://updates.test/releases/latest";
    private readonly string _dir = Directory.CreateTempSubdirectory("resetme-update-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    [Theory]
    [InlineData("0.3.0", "0.2.9", 1)]
    [InlineData("0.3.0", "0.3.0", 0)]
    [InlineData("v1.0.0", "0.9.9", 1)]
    [InlineData("0.3.0", "0.3.0-ci.7", 1)]
    [InlineData("0.3.0-beta.2", "0.3.0-beta.10", -1)]
    [InlineData("0.3.0-beta", "0.3.0-alpha", 1)]
    [InlineData("0.3.0+abc", "0.3.0+def", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    public void Versions_compare_by_semver_precedence(string a, string b, int expected)
    {
        Assert.Equal(expected, UpdateChecker.CompareVersions(a, b));
        Assert.Equal(-expected, UpdateChecker.CompareVersions(b, a));
    }

    [Theory]
    [InlineData("win-x64", "resetme-win-x64.zip")]
    [InlineData("linux-arm64", "resetme-linux-arm64.tar.gz")]
    [InlineData("osx-arm64", "resetme-macos-arm64.tar.gz")]
    [InlineData("freebsd-x64", null)]
    public void Package_names_match_the_release_assets(string rid, string? expected)
    {
        Assert.Equal(expected, UpdateChecker.PackageNameFor(rid));
    }

    [Fact]
    public void Release_json_is_parsed_and_unsafe_urls_are_dropped()
    {
        var release = UpdateChecker.ParseRelease(ReleaseJson("v0.4.0", ("resetme-linux-x64.tar.gz", "https://dl.test/a"), ("evil", "file:///etc/passwd")));

        Assert.Equal("0.4.0", release.Version);
        Assert.Equal("v0.4.0", release.Tag);
        Assert.Equal(["resetme-linux-x64.tar.gz"], release.Assets.Select(a => a.Name));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    public void Malformed_feed_is_an_error(string json)
    {
        Assert.Throws<InvalidDataException>(() => UpdateChecker.ParseRelease(json));
    }

    [Fact]
    public void Checksums_are_read_in_both_sha256sum_formats()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var sums = $"{a}  resetme-linux-x64.tar.gz\n{b} *resetme-win-x64.zip\nbad  short.zip\n";

        Assert.Equal(a, UpdateInstaller.ParseChecksum(sums, "resetme-linux-x64.tar.gz"));
        Assert.Equal(b.ToLowerInvariant(), UpdateInstaller.ParseChecksum(sums, "resetme-win-x64.zip"));
        Assert.Null(UpdateInstaller.ParseChecksum(sums, "short.zip"));
        Assert.Null(UpdateInstaller.ParseChecksum(sums, "missing.zip"));
    }

    [Fact]
    public async Task Check_reports_a_newer_release_with_its_assets()
    {
        var handler = new FakeHttp { [Feed] = Text(ReleaseJson("v0.4.0", ("resetme-linux-x64.tar.gz", "https://dl.test/pkg"), ("SHA256SUMS.txt", "https://dl.test/sums"))) };
        using var http = new HttpClient(handler);

        var result = await new UpdateChecker(http, Feed).CheckAsync("0.3.0", "resetme-linux-x64.tar.gz", CancellationToken.None);

        Assert.True(result.UpdateAvailable);
        Assert.Equal("resetme-linux-x64.tar.gz", result.Package?.Name);
        Assert.Equal("SHA256SUMS.txt", result.Checksums?.Name);
        Assert.StartsWith("ResetMe/0.3.0", handler.UserAgents.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_version_is_up_to_date()
    {
        using var http = new HttpClient(new FakeHttp { [Feed] = Text(ReleaseJson("v0.3.0")) });

        var result = await new UpdateChecker(http, Feed).CheckAsync("0.3.0", "resetme-linux-x64.tar.gz", CancellationToken.None);

        Assert.False(result.UpdateAvailable);
    }

    [Theory]
    [InlineData(".tar.gz")]
    [InlineData(".zip")]
    public async Task Verified_package_is_staged_next_to_the_install(string extension)
    {
        var layout = InstallFolder("current");
        var (check, handler) = Release("0.4.0", Package("0.4.0", extension));
        using var http = new HttpClient(handler);

        var staged = await new UpdateInstaller(http).DownloadAndStageAsync(check, layout, null, CancellationToken.None);

        Assert.Equal(Path.GetDirectoryName(layout.Root), Path.GetDirectoryName(staged.WorkDirectory));
        Assert.True(new InstallLayout(staged.ContentPath, InstallKind.Folder).IsComplete());
        Assert.Equal("0.4.0", File.ReadAllText(Path.Combine(staged.ContentPath, "VERSION")));
    }

    [Fact]
    public async Task Checksum_mismatch_installs_nothing()
    {
        var layout = InstallFolder("current");
        var (check, handler) = Release("0.4.0", Package("0.4.0", ".tar.gz"), corruptChecksum: true);
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => new UpdateInstaller(http).DownloadAndStageAsync(check, layout, null, CancellationToken.None));

        Assert.Contains("Checksum mismatch", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_dir, ".resetme-update-0.4.0")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(layout.Root, "VERSION")));
    }

    [Fact]
    public async Task Release_without_checksums_is_refused()
    {
        var layout = InstallFolder("current");
        var (check, handler) = Release("0.4.0", Package("0.4.0", ".tar.gz"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<UpdateException>(() =>
            new UpdateInstaller(http).DownloadAndStageAsync(check with { Checksums = null }, layout, null, CancellationToken.None));
    }

    [Fact]
    public void Development_builds_are_not_an_install()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "bin-debug")).FullName;
        File.WriteAllText(Path.Combine(dir, Exe("resetme")), "");

        Assert.Null(InstallLayout.Detect(dir));
        Assert.NotNull(InstallLayout.Detect(InstallFolder("x").Root));
    }

    [Fact]
    public void App_bundle_layout_is_detected_from_contents_macos()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "ResetMe.app", "Contents", "MacOS")).FullName;
        File.WriteAllText(Path.Combine(bin, Exe("resetme")), "");
        File.WriteAllText(Path.Combine(bin, Exe("ResetMeApp")), "");

        var layout = InstallLayout.Detect(bin);

        Assert.Equal(InstallKind.AppBundle, layout?.Kind);
        Assert.Equal(Path.Combine(_dir, "ResetMe.app"), layout?.Root);
    }

    [Fact]
    public async Task Unix_apply_swaps_the_install_folder()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows uses the helper script (see the next test).
        }

        var layout = InstallFolder("current");
        var (check, handler) = Release("0.4.0", Package("0.4.0", ".tar.gz"));
        using var http = new HttpClient(handler);
        var staged = await new UpdateInstaller(http).DownloadAndStageAsync(check, layout, null, CancellationToken.None);

        var result = UpdateInstaller.Apply(staged, layout, [], relaunchDesktop: false);

        Assert.Equal(ApplyResult.Applied, result);
        Assert.Equal("0.4.0", File.ReadAllText(Path.Combine(layout.Root, "VERSION")));
        Assert.False(Directory.Exists(staged.WorkDirectory));
        Assert.Empty(Directory.GetDirectories(_dir, "current.old-*"));
    }

    [Fact]
    public void Windows_helper_waits_swaps_with_rollback_and_relaunches()
    {
        var layout = new InstallLayout(@"C:\Users\o'neil\resetme", InstallKind.Folder);
        var staged = new StagedUpdate("0.4.0", @"C:\Users\o'neil\.resetme-update-0.4.0\extracted\resetme", @"C:\Users\o'neil\.resetme-update-0.4.0");

        var script = UpdateInstaller.WindowsHelperScript(staged, layout, [111, 222], relaunchDesktop: true);

        Assert.Contains("$root = 'C:\\Users\\o''neil\\resetme'", script, StringComparison.Ordinal);
        Assert.Contains("foreach ($id in @(111, 222))", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $old -Destination $root", script, StringComparison.Ordinal); // rollback
        Assert.Contains("ResetMeApp", script, StringComparison.Ordinal);
    }

    private InstallLayout InstallFolder(string version)
    {
        var root = Directory.CreateDirectory(Path.Combine(_dir, "current")).FullName;
        File.WriteAllText(Path.Combine(root, Exe("resetme")), "cli");
        File.WriteAllText(Path.Combine(root, Exe("ResetMeApp")), "app");
        File.WriteAllText(Path.Combine(root, "VERSION"), version);
        return new InstallLayout(root, InstallKind.Folder);
    }

    /// <summary>Builds a real archive shaped like scripts/package.sh output.</summary>
    private (string Name, byte[] Bytes) Package(string version, string extension)
    {
        var source = Path.Combine(_dir, "pkg-src-" + extension.Trim('.').Replace('.', '-'));
        var folder = Directory.CreateDirectory(Path.Combine(source, "resetme")).FullName;
        File.WriteAllText(Path.Combine(folder, Exe("resetme")), "new cli");
        File.WriteAllText(Path.Combine(folder, Exe("ResetMeApp")), "new app");
        File.WriteAllText(Path.Combine(folder, "VERSION"), version);

        var name = "resetme-test" + extension;
        var archive = Path.Combine(_dir, name);
        if (extension == ".zip")
        {
            ZipFile.CreateFromDirectory(source, archive);
        }
        else
        {
            using var file = File.Create(archive);
            using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            TarFile.CreateFromDirectory(source, gzip, includeBaseDirectory: false);
        }

        var bytes = File.ReadAllBytes(archive);
        File.Delete(archive);
        Directory.Delete(source, recursive: true);
        return (name, bytes);
    }

    private static (UpdateCheckResult Check, FakeHttp Handler) Release(string version, (string Name, byte[] Bytes) package, bool corruptChecksum = false)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(package.Bytes));
        if (corruptChecksum)
        {
            hash = new string('0', 64);
        }

        var handler = new FakeHttp
        {
            ["https://dl.test/pkg"] = () => new ByteArrayContent(package.Bytes),
            ["https://dl.test/sums"] = Text($"{hash}  {package.Name}\n"),
        };
        var pkg = new ReleaseAsset(package.Name, new Uri("https://dl.test/pkg"), package.Bytes.Length);
        var sums = new ReleaseAsset("SHA256SUMS.txt", new Uri("https://dl.test/sums"), 100);
        var info = new ReleaseInfo("v" + version, version, new Uri("https://github.test/release"), [pkg, sums]);
        return (new UpdateCheckResult("0.3.0", info, true, pkg, sums), handler);
    }

    private static string ReleaseJson(string tag, params (string Name, string Url)[] assets) =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.test/r\",\"assets\":["
        + string.Join(",", assets.Select(a => "{\"name\":\"" + a.Name + "\",\"browser_download_url\":\"" + a.Url + "\",\"size\":1}"))
        + "]}";

    private static Func<HttpContent> Text(string text) => () => new StringContent(text, Encoding.UTF8);

    private sealed class FakeHttp : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpContent>> _routes = [];

        public List<string> UserAgents { get; } = [];

        public Func<HttpContent> this[string url]
        {
            set => _routes[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(_routes.TryGetValue(request.RequestUri!.ToString(), out var content)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content() }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
