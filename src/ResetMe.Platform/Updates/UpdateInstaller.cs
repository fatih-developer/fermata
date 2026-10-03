using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ResetMe.Platform.Updates;

public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public enum InstallKind
{
    /// <summary>A folder with resetme(.exe) and ResetMeApp(.exe) side by side (Windows zip, Linux tar.gz).</summary>
    Folder,

    /// <summary>ResetMe.app with both executables in Contents/MacOS.</summary>
    AppBundle,
}

/// <summary>Where the running ResetMe is installed, as laid out by scripts/package.sh.</summary>
public sealed record InstallLayout(string Root, InstallKind Kind)
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public string BinDirectory => Kind == InstallKind.AppBundle ? Path.Combine(Root, "Contents", "MacOS") : Root;

    public string DesktopExecutable => Path.Combine(BinDirectory, Exe("ResetMeApp"));

    public string CliExecutable => Path.Combine(BinDirectory, Exe("resetme"));

    /// <summary>
    /// The package the process runs from, or null for development builds (bin/Debug, dotnet run),
    /// which must never be replaced by an update.
    /// </summary>
    public static InstallLayout? Detect(string baseDirectory)
    {
        var dir = Path.GetFullPath(baseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var macSuffix = Path.Combine(".app", "Contents", "MacOS");
        var layout = dir.EndsWith(macSuffix, StringComparison.Ordinal)
            ? new InstallLayout(Path.GetDirectoryName(Path.GetDirectoryName(dir))!, InstallKind.AppBundle)
            : new InstallLayout(dir, InstallKind.Folder);
        return layout.IsComplete() ? layout : null;
    }

    public bool IsComplete() => File.Exists(DesktopExecutable) && File.Exists(CliExecutable);
}

public sealed record StagedUpdate(string Version, string ContentPath, string WorkDirectory);

public enum ApplyResult
{
    /// <summary>Files were swapped; new processes start the new version.</summary>
    Applied,

    /// <summary>A helper swaps the files after the given processes exit (Windows); the caller should exit now.</summary>
    Scheduled,
}

/// <summary>Downloads, verifies (SHA256SUMS.txt) and installs a release over the current package.</summary>
public sealed class UpdateInstaller
{
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateInstaller(HttpClient http)
    {
        _http = http;
    }

    public async Task<StagedUpdate> DownloadAndStageAsync(
        UpdateCheckResult check,
        InstallLayout layout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        ArgumentNullException.ThrowIfNull(layout);
        var package = check.Package
            ?? throw new UpdateException($"Release {check.Latest.Tag} has no package for this platform.");
        var checksums = check.Checksums
            ?? throw new UpdateException($"Release {check.Latest.Tag} has no {UpdateChecker.ChecksumFileName}; refusing to install an unverified package.");

        // Stage next to the install so the final swap is a same-volume rename.
        var parent = Path.GetDirectoryName(layout.Root)!;
        var work = Path.Combine(parent, $".resetme-update-{SafeName(check.Latest.Version)}");
        if (Directory.Exists(work))
        {
            Directory.Delete(work, recursive: true);
        }

        Directory.CreateDirectory(work);
        try
        {
            var archive = Path.Combine(work, package.Name);
            progress?.Report($"Downloading {package.Name}…");
            await DownloadAsync(package.DownloadUrl, archive, cancellationToken).ConfigureAwait(false);

            progress?.Report("Verifying checksum…");
            var sums = await _http.GetStringAsync(checksums.DownloadUrl, cancellationToken).ConfigureAwait(false);
            var expected = ParseChecksum(sums, package.Name)
                ?? throw new UpdateException($"{UpdateChecker.ChecksumFileName} has no entry for {package.Name}.");
            var actual = await HashAsync(archive, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException($"Checksum mismatch for {package.Name}: expected {expected}, got {actual}. Nothing was installed.");
            }

            progress?.Report("Extracting…");
            var extracted = Path.Combine(work, "extracted");
            await ExtractAsync(archive, extracted, cancellationToken).ConfigureAwait(false);
            File.Delete(archive);

            var content = Path.Combine(extracted, layout.Kind == InstallKind.AppBundle ? "ResetMe.app" : "resetme");
            if (!new InstallLayout(content, layout.Kind).IsComplete())
            {
                throw new UpdateException($"{package.Name} does not contain a complete ResetMe package.");
            }

            return new StagedUpdate(check.Latest.Version, content, work);
        }
        catch
        {
            TryDelete(work);
            throw;
        }
    }

    /// <summary>
    /// Replaces <paramref name="layout"/> with the staged content. On Windows running executables
    /// cannot be replaced, so a helper waits for <paramref name="waitForProcessIds"/> to exit first.
    /// </summary>
    public static ApplyResult Apply(StagedUpdate staged, InstallLayout layout, IReadOnlyList<int> waitForProcessIds, bool relaunchDesktop)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(waitForProcessIds);

        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(staged.WorkDirectory, "apply-update.ps1");
            File.WriteAllText(script, WindowsHelperScript(staged, layout, waitForProcessIds, relaunchDesktop), new UTF8Encoding(true));
            var info = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            {
                info.ArgumentList.Add(arg);
            }

            Process.Start(info)?.Dispose();
            return ApplyResult.Scheduled;
        }

        // Unix: running processes keep their (unlinked) files, so the swap can happen right away.
        var old = layout.Root + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        Directory.Move(layout.Root, old);
        try
        {
            Directory.Move(staged.ContentPath, layout.Root);
        }
        catch
        {
            Directory.Move(old, layout.Root);
            throw;
        }

        TryDelete(old);
        TryDelete(staged.WorkDirectory);

        if (relaunchDesktop)
        {
            // Start the new app only after this process (which holds the single-instance lock) exits.
            var wait = string.Join(' ', waitForProcessIds.Select(id => id.ToString(CultureInfo.InvariantCulture)));
            var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"for p in {wait}; do while kill -0 $p 2>/dev/null; do sleep 0.2; done; done; exec \"$0\"");
            info.ArgumentList.Add(layout.DesktopExecutable);
            Process.Start(info)?.Dispose();
        }

        return ApplyResult.Applied;
    }

    internal static string WindowsHelperScript(StagedUpdate staged, InstallLayout layout, IReadOnlyList<int> waitForProcessIds, bool relaunchDesktop)
    {
        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var old = layout.Root + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var ids = waitForProcessIds.Count == 0 ? "@()" : "@(" + string.Join(", ", waitForProcessIds) + ")";

        return $$"""
            # ResetMe update helper: swaps the install folder once ResetMe has exited.
            $ErrorActionPreference = 'Stop'
            $log = {{Q(Path.Combine(staged.WorkDirectory, "update.log"))}}
            function Log([string]$message) { Add-Content -LiteralPath $log -Value ("{0:o} {1}" -f (Get-Date), $message) }
            $root = {{Q(layout.Root)}}
            $staged = {{Q(staged.ContentPath)}}
            $old = {{Q(old)}}
            $relaunch = {{Q(relaunchDesktop ? layout.DesktopExecutable : "")}}

            Log "waiting for ResetMe to exit"
            foreach ($id in {{ids}}) { Wait-Process -Id $id -Timeout 60 -ErrorAction SilentlyContinue }
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) }) -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 500
            }

            try {
                Move-Item -LiteralPath $root -Destination $old
                try {
                    Move-Item -LiteralPath $staged -Destination $root
                } catch {
                    Move-Item -LiteralPath $old -Destination $root
                    throw
                }
                Log "installed {{staged.Version}}"
                Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue
            } catch {
                Log ("update failed, previous version kept: " + $_)
            }

            if ($relaunch) { Start-Process -FilePath $relaunch }
            """;
    }

    /// <summary>Lines of `sha256sum` output: "&lt;hex&gt;  name" or "&lt;hex&gt; *name".</summary>
    internal static string? ParseChecksum(string sums, string fileName)
    {
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            var name = line[(space + 1)..].TrimStart(' ', '*');
            if (name == fileName)
            {
                var hash = line[..space];
                return hash.Length == 64 && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null;
            }
        }

        return null;
    }

    private async Task DownloadAsync(Uri url, string target, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxPackageBytes)
        {
            throw new UpdateException("Update package is unexpectedly large; refusing to download.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(target);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxPackageBytes)
            {
                throw new UpdateException("Update package is unexpectedly large; download aborted.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> HashAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task ExtractAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archive, destination, overwriteFiles: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var file = File.OpenRead(archive);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destination, overwriteFiles: false, cancellationToken).ConfigureAwait(false);
    }

    private static string SafeName(string version) =>
        string.Concat(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-'));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
