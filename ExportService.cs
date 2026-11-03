using System.Globalization;

namespace VideoTrimmer;

static class ExportService
{
    public static string[] BuildArguments(
        string input,
        string output,
        double inSeconds,
        double outSeconds,
        bool exact,
        ExactExportOptions? exactOptions = null)
    {
        var start = inSeconds.ToString(CultureInfo.InvariantCulture);
        var length = (outSeconds - inSeconds).ToString(CultureInfo.InvariantCulture);
        // Exact mode seeks after -i so the cut lands on the chosen frames. Fast copy seeks before -i.
        if (!exact)
        {
            return
            [
                "-y", "-progress", "pipe:1", "-nostats",
                "-ss", start,
                "-i", input,
                "-t", length,
                "-map", "0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                output,
            ];
        }

        var options = exactOptions ?? ExactExportOptions.Default;
        var (crf, preset) = ExactExportPresets.Encoder(options.Quality);
        var audioCodec = options.CopyAudio ? "copy" : "aac";
        return
        [
            "-y", "-progress", "pipe:1", "-nostats",
            "-i", input,
            "-ss", start,
            "-t", length,
            "-map", "0",
            "-c:v", "libx264",
            "-crf", crf.ToString(CultureInfo.InvariantCulture),
            "-preset", preset,
            "-c:a", audioCodec,
            output,
        ];
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A canceled ffmpeg process can keep the file open for a moment.
        }
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var fast = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: false);
        Expect(fast.Contains("-c") && fast.Contains("copy"), "fast copy keeps stream copy");
        Expect(!fast.Contains("-crf"), "fast copy has no crf");

        var balanced = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, ExactExportOptions.Default);
        Expect(balanced.Contains("-crf") && balanced.Contains("23"), "balanced crf 23");
        Expect(balanced.Contains("medium"), "balanced preset");
        Expect(balanced.Contains("aac"), "default audio aac");

        var high = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, new ExactExportOptions(ExactQuality.High, CopyAudio: true));
        Expect(high.Contains("18") && high.Contains("slow"), "high quality encoder");
        var copyIndex = Array.IndexOf(high, "-c:a");
        Expect(copyIndex >= 0 && copyIndex + 1 < high.Length && high[copyIndex + 1] == "copy", "copy audio");

        var small = BuildArguments("in.mp4", "out.mp4", 1, 5, exact: true, new ExactExportOptions(ExactQuality.Small, false));
        Expect(small.Contains("28") && small.Contains("veryfast"), "small quality encoder");
        return errors;
    }
}
