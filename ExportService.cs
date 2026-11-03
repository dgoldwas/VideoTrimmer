using System.Globalization;

namespace VideoTrimmer;

static class ExportService
{
    public static string[] BuildArguments(string input, string output, double inSeconds, double outSeconds, bool exact)
    {
        var start = inSeconds.ToString(CultureInfo.InvariantCulture);
        var length = (outSeconds - inSeconds).ToString(CultureInfo.InvariantCulture);
        // Exact mode seeks after -i so the cut lands on the chosen frames. Fast copy seeks before -i.
        return exact
            ?
            [
                "-y", "-progress", "pipe:1", "-nostats",
                "-i", input,
                "-ss", start,
                "-t", length,
                "-map", "0",
                "-c:v", "libx264",
                "-c:a", "aac",
                output,
            ]
            :
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
}
