using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace VideoTrimmer;

/// <summary>A newer VideoTrimmer release on GitHub and the installer asset that delivers it.</summary>
sealed record AvailableUpdate(string Version, string Title, string ReleaseUrl, string AssetName, string AssetApiUrl, long Size, string? Sha256);

/// <summary>Finds newer releases of VideoTrimmer on GitHub and installs them.</summary>
static class UpdateService
{
    public const string Repository = "dgoldwas/VideoTrimmer";
    static readonly HttpClient http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VideoTrimmer-Updater", "1.0"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>The running build's YYYY.MM.DD.## version.</summary>
    public static string CurrentVersion => typeof(UpdateService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0.0";

    /// <summary>Where the installed copy lives. Updates install there whichever copy is running.</summary>
    public static string InstalledPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoTrimmer", "VideoTrimmer.exe");

    /// <summary>Where the downloaded installer and the helper that runs it are kept.</summary>
    public static string WorkDirectory => Path.Combine(Path.GetTempPath(), "VideoTrimmer-Update");

    /// <summary>The installer helper writes the outcome here so the next launch can report a failed update.</summary>
    public static string ResultPath => Path.Combine(WorkDirectory, "last-result.txt");

    /// <summary>Returns the latest release when it is newer than <paramref name="currentVersion"/> and ships a Setup.exe.</summary>
    public static AvailableUpdate? ParseLatestRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = tag.TrimStart('v', 'V');
        if (!IsNewer(version, currentVersion)) return null;
        if (!root.TryGetProperty("assets", out var assets))
            throw new InvalidDataException($"Release {version} has no installer.");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..].ToLowerInvariant();
            return new AvailableUpdate(version,
                root.TryGetProperty("name", out var title) ? title.GetString() ?? tag : tag,
                root.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : "",
                name, asset.GetProperty("url").GetString() ?? "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0, sha);
        }

        throw new InvalidDataException($"Release {version} has no installer.");
    }

    /// <summary>Compares YYYY.MM.DD.## versions numerically, so 2026.11.09.10 is newer than 2026.11.09.09.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && (!Version.TryParse(current, out var b) || a > b);

    /// <summary>Asks GitHub for the latest release. Returns null when there is nothing newer.</summary>
    public static async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        await AuthorizeAsync(request, cancellation);
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return ParseLatestRelease(await response.Content.ReadAsStringAsync(cancellation), CurrentVersion);
    }

    /// <summary>Downloads the installer, checks it against GitHub's SHA-256 and returns its path.</summary>
    public static async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(WorkDirectory);
        var target = Path.Combine(WorkDirectory, Path.GetFileName(update.AssetName));
        var partial = target + ".partial";
        using var request = new HttpRequestMessage(HttpMethod.Get, update.AssetApiUrl);
        // The asset API URL redirects to the file itself; HttpClient drops the token on that redirect.
        request.Headers.Accept.ParseAdd("application/octet-stream");
        await AuthorizeAsync(request, cancellation);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? update.Size;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation);
                    hash.AppendData(buffer, 0, count);
                    read += count;
                    if (length > 0) progress?.Report(Math.Min(1, (double)read / length));
                }
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (update.Sha256 != null && actual != update.Sha256)
            {
                File.Delete(partial);
                throw new InvalidDataException($"The downloaded installer is damaged (SHA-256 {actual}, expected {update.Sha256}).");
            }
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// Starts a hidden helper that waits for this process to exit, runs the installer silently through UAC,
    /// then opens VideoTrimmer again (the new copy, or this one if the install didn't finish). The caller must close the app.
    /// </summary>
    public static void LaunchInstallerAfterExit(string setupPath, string version, string? reopenWith)
    {
        Directory.CreateDirectory(WorkDirectory);
        var script = Path.Combine(WorkDirectory, "install-update.ps1");
        File.WriteAllText(script, HelperScript);
        try { File.Delete(ResultPath); } catch (IOException) { }
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
            "-ProcessId", Environment.ProcessId.ToString(), "-Setup", setupPath, "-Version", version,
            "-Installed", InstalledPath, "-Current", Environment.ProcessPath ?? InstalledPath, "-Result", ResultPath, "-Video", reopenWith ?? "" })
            start.ArgumentList.Add(arg);
        Process.Start(start);
    }

    /// <summary>Reads and clears the outcome the helper left from the last update, if any.</summary>
    public static string? TakeLastResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            var text = File.ReadAllText(ResultPath).Trim();
            File.Delete(ResultPath);
            return text;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    const string HelperScript = """
        param([int]$ProcessId, [string]$Setup, [string]$Version, [string]$Installed, [string]$Current, [string]$Result, [string]$Video)
        try { Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction Stop } catch { }
        $outcome = "failed: setup did not run"
        try {
            $process = Start-Process -FilePath $Setup -ArgumentList '/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS' -Verb RunAs -Wait -PassThru -ErrorAction Stop
            if ($process.ExitCode -eq 0) { $outcome = "installed $Version" } else { $outcome = "failed: setup exited with code $($process.ExitCode)" }
        } catch { $outcome = "failed: $($_.Exception.Message)" }
        Set-Content -LiteralPath $Result -Value $outcome -Encoding utf8
        $app = if ($outcome -like 'installed*' -and (Test-Path -LiteralPath $Installed)) { $Installed } else { $Current }
        # Started from this unelevated helper, VideoTrimmer runs as the user rather than as administrator.
        if (Test-Path -LiteralPath $app) {
            if ($Video -and (Test-Path -LiteralPath $Video)) { Start-Process -FilePath $app -ArgumentList ('"' + $Video + '"') } else { Start-Process -FilePath $app }
        }
        if ($outcome -like 'installed*') { Remove-Item -LiteralPath $Setup -ErrorAction SilentlyContinue }
        """;

    /// <summary>
    /// The repository is public, so anonymous requests work. A token (VIDEOTRIMMER_GITHUB_TOKEN, GH_TOKEN, GITHUB_TOKEN,
    /// or the GitHub CLI's sign-in) is still sent when available, which raises GitHub's rate limit.
    /// </summary>
    static async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        var token = await GetTokenAsync(cancellation);
        if (!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    static string? cachedToken;
    static bool tokenLookedUp;

    static async Task<string?> GetTokenAsync(CancellationToken cancellation)
    {
        foreach (var name in new[] { "VIDEOTRIMMER_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value.Trim();
        if (tokenLookedUp) return cachedToken;
        try
        {
            var start = new ProcessStartInfo("gh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "auth", "token", "--hostname", "github.com" })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process != null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                if (process.ExitCode == 0) cachedToken = (await output).Trim();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { }
        tokenLookedUp = true;
        return cachedToken;
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        Expect(IsNewer("2026.11.09.10", "2026.11.09.09"), "numeric version compare");
        Expect(!IsNewer("2026.11.09.01", "2026.11.09.01"), "same version is not newer");
        Expect(IsNewer("2026.11.09.01", "0.0.0.0"), "any release beats an unversioned build");
        const string release = """
            {"tag_name":"v2026.12.01.01","name":"VideoTrimmer 2026.12.01.01","html_url":"https://example/r","draft":false,"prerelease":false,
             "assets":[{"name":"notes.txt","url":"a","size":1},{"name":"VideoTrimmer-2026.12.01.01-Setup.exe","url":"https://api/asset/2","size":42,"digest":"sha256:ABCD"}]}
            """;
        var found = ParseLatestRelease(release, "2026.11.09.01");
        Expect(found is { Version: "2026.12.01.01", AssetApiUrl: "https://api/asset/2", Size: 42, Sha256: "abcd" }, "parse setup asset");
        Expect(ParseLatestRelease(release, "2026.12.01.01") is null, "no update when current");
        try
        {
            ParseLatestRelease("{\"tag_name\":\"v2026.12.01.01\",\"assets\":[]}", "2026.11.09.01");
            errors.Add("new release without installer must report an error");
        }
        catch (InvalidDataException) { }
        return errors;
    }
}
