using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoTrimmer;

sealed class FfmpegRunner
{
    public string? Ffmpeg { get; }
    public string? Ffprobe { get; }

    public FfmpegRunner()
    {
        Ffmpeg = Find("ffmpeg.exe");
        Ffprobe = Find("ffprobe.exe");
    }

    public static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    public async Task<string> RunAsync(string executable, string arguments, CancellationToken cancellationToken = default, IProgress<TimeSpan>? progress = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not start " + Path.GetFileName(executable) + ".");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = new StringBuilder();
        string stderr = "";
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                stdout.AppendLine(line);
                if (TryParseProgress(line, out var time))
                    progress?.Report(time);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            try { stderr = await stderrTask.ConfigureAwait(false); } catch { /* The pipe closes when the process is killed. */ }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new OperationCanceledException(cancellationToken);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(LastLine(stderr) ?? "FFmpeg failed.");

        return stdout.ToString();
    }

    static string? Find(string name)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, name),
            Path.Combine(AppContext.BaseDirectory, "tools", name),
        };
        var beside = candidates.FirstOrDefault(File.Exists);
        if (beside is not null) return beside;
        return Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, name))
            .FirstOrDefault(File.Exists);
    }

    // ffmpeg writes out_time as HH:MM:SS.microseconds on the progress pipe.
    static bool TryParseProgress(string line, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        const string prefix = "out_time=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var text = line[prefix.Length..].Trim();
        if (text.Length == 0 || text.Equals("N/A", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out time)) return false;
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        return true;
    }

    static string? LastLine(string text)
    {
        return text.Split('\n').Select(line => line.Trim()).LastOrDefault(line => line.Length > 0);
    }

    static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process exited between the check and the kill.
        }
    }
}
