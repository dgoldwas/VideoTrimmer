using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoTrimmer;

/// <summary>Seconds-based silence span found in the audio.</summary>
readonly record struct Silence(double Start, double End);

/// <summary>
/// Finds the places worth cutting at: an audio loudness envelope (drawn as the waveform and used to find
/// silences) and scene changes from FFmpeg's scdet filter. Both are read-only passes over the source file.
/// </summary>
static partial class SmartAnalysis
{
    public const int SampleRate = 8000;
    /// <summary>The envelope holds one RMS value per window.</summary>
    public const double WindowSeconds = 0.02;
    const int WindowSamples = (int)(SampleRate * WindowSeconds);
    const double SilenceDb = -40;
    const double MinSilenceSeconds = 0.4;
    const string SceneThreshold = "12";

    /// <summary>Decodes the first audio track to mono 8 kHz and returns its RMS level (0–1) per 20 ms window.</summary>
    public static async Task<float[]> ReadEnvelopeAsync(FfmpegRunner runner, string input, double duration, IProgress<double>? progress, CancellationToken token)
    {
        if (runner.Ffmpeg is null) throw new InvalidOperationException("FFmpeg is not available.");
        var expected = Math.Max(1, (int)Math.Ceiling(duration / WindowSeconds));
        var levels = new List<float>(expected + 16);
        // first_pts=0 pads a late-starting audio track so its windows line up with the video timeline.
        string[] args =
        [
            "-hide_banner", "-nostats", "-v", "error",
            "-i", input,
            "-map", "0:a:0", "-vn", "-sn", "-dn",
            "-af", $"aresample={SampleRate}:async=1:first_pts=0",
            "-ac", "1", "-f", "s16le", "-acodec", "pcm_s16le",
            "pipe:1",
        ];
        await runner.StreamAsync(runner.Ffmpeg, args, async (stdout, ct) =>
        {
            var buffer = new byte[WindowSamples * 2 * 64];
            var carry = 0;
            double sum = 0;
            var count = 0;
            var lastReport = 0;
            while (true)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct).ConfigureAwait(false);
                if (read == 0) break;
                var available = carry + read;
                var whole = available & ~1;
                for (var i = 0; i < whole; i += 2)
                {
                    var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2)) / 32768.0;
                    sum += sample * sample;
                    if (++count == WindowSamples)
                    {
                        levels.Add((float)Math.Sqrt(sum / count));
                        sum = 0;
                        count = 0;
                    }
                }

                carry = available - whole;
                if (carry > 0) buffer[0] = buffer[whole];
                if (levels.Count - lastReport > 500)
                {
                    lastReport = levels.Count;
                    progress?.Report(Math.Clamp(levels.Count / (double)expected, 0, 1));
                }
            }

            if (count > 0) levels.Add((float)Math.Sqrt(sum / count));
        }, null, token).ConfigureAwait(false);
        return levels.ToArray();
    }

    /// <summary>Spans quieter than -40 dBFS for at least 0.4 s.</summary>
    public static List<Silence> FindSilences(float[] envelope)
    {
        var silences = new List<Silence>();
        var threshold = Math.Pow(10, SilenceDb / 20);
        var minWindows = (int)Math.Ceiling(MinSilenceSeconds / WindowSeconds);
        var start = -1;
        for (var i = 0; i <= envelope.Length; i++)
        {
            var quiet = i < envelope.Length && envelope[i] < threshold;
            if (quiet && start < 0) start = i;
            else if (!quiet && start >= 0)
            {
                if (i - start >= minWindows)
                    silences.Add(new Silence(start * WindowSeconds, i * WindowSeconds));
                start = -1;
            }
        }

        return silences;
    }

    /// <summary>Maps an RMS level to 0–1 for drawing, on a 50 dB scale.</summary>
    public static float DisplayLevel(float rms)
    {
        if (rms <= 0) return 0;
        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db + 50) / 50, 0, 1);
    }

    /// <summary>
    /// Decodes the first video track at thumbnail size through scdet and reports each scene change as it is found.
    /// Times are seconds from the start of the clip, the same timeline the trim uses.
    /// </summary>
    public static async Task FindScenesAsync(FfmpegRunner runner, string input, double duration, Action<double> onScene, IProgress<double>? progress, CancellationToken token)
    {
        if (runner.Ffmpeg is null) throw new InvalidOperationException("FFmpeg is not available.");
        string[] args =
        [
            "-hide_banner", "-nostats", "-progress", "pipe:1",
            "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", $"scale=160:-2,scdet=threshold={SceneThreshold}",
            "-f", "null", "-",
        ];
        await runner.StreamAsync(runner.Ffmpeg, args, async (stdout, ct) =>
        {
            using var reader = new StreamReader(stdout);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (FfmpegRunner.TryParseProgress(line, out var time) && duration > 0)
                    progress?.Report(Math.Clamp(time.TotalSeconds / duration, 0, 1));
            }
        }, line =>
        {
            if (TryParseScene(line, out var seconds)) onScene(seconds);
        }, token).ConfigureAwait(false);
    }

    public static bool TryParseScene(string line, out double seconds)
    {
        seconds = 0;
        var match = SceneTime().Match(line);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    [GeneratedRegex(@"lavfi\.scd\.time:\s*(-?[0-9.]+)")]
    private static partial Regex SceneTime();

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        Expect(TryParseScene("[Parsed_scdet_1 @ 0000] lavfi.scd.score: 28.486, lavfi.scd.time: 10.01", out var t) && Math.Abs(t - 10.01) < 1e-9, "parse scdet time");
        Expect(!TryParseScene("frame=  10 fps=0.0", out _), "ignore other stderr");

        // 1 s loud, 0.6 s silent, 0.2 s loud, 0.2 s silent (too short), 1 s loud.
        var envelope = new List<float>();
        void Add(double seconds, float level) => envelope.AddRange(Enumerable.Repeat(level, (int)Math.Round(seconds / WindowSeconds)));
        Add(1, 0.3f); Add(0.6, 0.001f); Add(0.2, 0.3f); Add(0.2, 0.001f); Add(1, 0.3f);
        var silences = FindSilences(envelope.ToArray());
        Expect(silences.Count == 1, "one silence long enough");
        Expect(silences.Count == 1 && Math.Abs(silences[0].Start - 1) < 1e-6 && Math.Abs(silences[0].End - 1.6) < 1e-6, "silence bounds");
        Expect(DisplayLevel(1) == 1 && DisplayLevel(0) == 0, "display level range");
        return errors;
    }
}
