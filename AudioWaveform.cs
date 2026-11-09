using System.Buffers.Binary;

namespace VideoTrimmer;

/// <summary>The audio loudness envelope drawn as the waveform under the filmstrip. A read-only pass over the source.</summary>
static class AudioWaveform
{
    public const int SampleRate = 8000;
    /// <summary>The envelope holds one RMS value per window.</summary>
    public const double WindowSeconds = 0.02;
    const int WindowSamples = (int)(SampleRate * WindowSeconds);

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

        /// <summary>Maps an RMS level to 0–1 for drawing, on a 50 dB scale.</summary>
    public static float DisplayLevel(float rms)
    {
        if (rms <= 0) return 0;
        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db + 50) / 50, 0, 1);
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        if (!(DisplayLevel(1) == 1 && DisplayLevel(0) == 0)) errors.Add("display level range");
        if (!(DisplayLevel(0.01f) > 0 && DisplayLevel(0.01f) < 1)) errors.Add("display level midrange");
        return errors;
    }
}
