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

    public async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, IProgress<TimeSpan>? progress = null)
    {
        using var process = Start(executable, arguments, binaryStdout: false);
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

        EnsureSuccess(process, stderr, cancellationToken);
        return stdout.ToString();
    }

    public async Task<byte[]> RunBinaryAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        using var process = Start(executable, arguments, binaryStdout: true);
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        string stderr = "";
        try
        {
            await CopyAsync(process.StandardOutput.BaseStream, buffer, cancellationToken).ConfigureAwait(false);
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

        EnsureSuccess(process, stderr, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// Runs a long analysis pass: <paramref name="readStdout"/> consumes stdout as it arrives, and each stderr line
    /// goes to <paramref name="onStderrLine"/> (FFmpeg filters such as scdet log their results there).
    /// </summary>
    public async Task StreamAsync(
        string executable,
        IReadOnlyList<string> arguments,
        Func<Stream, CancellationToken, Task> readStdout,
        Action<string>? onStderrLine,
        CancellationToken cancellationToken)
    {
        using var process = Start(executable, arguments, binaryStdout: true);
        var tail = new Queue<string>();
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                onStderrLine?.Invoke(line);
                if (tail.Count == 8) tail.Dequeue();
                tail.Enqueue(line);
            }
        });
        try
        {
            await readStdout(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            try { await stderrTask.ConfigureAwait(false); } catch { /* The pipe closes when the process is killed. */ }
        }

        EnsureSuccess(process, string.Join("\n", tail), cancellationToken);
    }

    static Process Start(string executable, IReadOnlyList<string> arguments, bool binaryStdout)
    {
        var process = new Process();
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (!binaryStdout)
            start.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo = start;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start " + Path.GetFileName(executable) + ".");
        }

        return process;
    }

    static void EnsureSuccess(Process process, string stderr, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new OperationCanceledException(cancellationToken);
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(LastLine(stderr) ?? "FFmpeg failed.");
    }

    static async Task CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var block = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(block.AsMemory(0, block.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await destination.WriteAsync(block.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
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
    public static bool TryParseProgress(string line, out TimeSpan time)
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
