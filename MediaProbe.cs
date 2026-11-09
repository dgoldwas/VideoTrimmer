using System.Globalization;
using System.Text.Json;

namespace VideoTrimmer;

/// <summary><see cref="Aspect"/> is the displayed width over height, after pixel aspect and rotation.</summary>
readonly record struct MediaInfo(double Duration, double Fps, long Frames, bool HasAudio, double Aspect);

static class MediaProbe
{
    public static async Task<MediaInfo> ProbeAsync(FfmpegRunner runner, string path, CancellationToken cancellationToken)
    {
        if (runner.Ffprobe is null)
            throw new InvalidOperationException("FFprobe is not available.");

        var json = await runner.RunAsync(runner.Ffprobe, [
            "-v", "quiet",
            "-print_format", "json",
            "-show_streams",
            "-show_format",
            path,
        ], cancellationToken).ConfigureAwait(false);

        return Parse(json);
    }

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var video = root.GetProperty("streams").EnumerateArray()
            .FirstOrDefault(stream => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The file does not contain a video stream.");
        var hasAudio = root.GetProperty("streams").EnumerateArray()
            .Any(stream => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "audio");

        double probedDuration;
        if (video.TryGetProperty("duration", out var videoDuration) && videoDuration.ValueKind == JsonValueKind.String)
            probedDuration = double.Parse(videoDuration.GetString()!, CultureInfo.InvariantCulture);
        else
            probedDuration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);

        var rate = video.GetProperty("r_frame_rate").GetString()?.Split('/');
        if (rate is not { Length: 2 })
            throw new InvalidOperationException("The video frame rate could not be read.");
        var probedFps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
        if (probedDuration <= 0 || double.IsNaN(probedDuration) || probedFps <= 0 || double.IsNaN(probedFps) || double.IsInfinity(probedFps))
            throw new InvalidOperationException("The video duration or frame rate could not be read.");

        var rounded = Math.Round(probedDuration * probedFps, MidpointRounding.AwayFromZero);
        if (rounded < 1 || rounded > 1_000_000_000)
            throw new InvalidOperationException("The video duration or frame rate could not be read.");
        return new MediaInfo(probedDuration, probedFps, (long)rounded, hasAudio, DisplayAspect(video));
    }

    static double DisplayAspect(JsonElement video)
    {
        if (!video.TryGetProperty("width", out var w) || !video.TryGetProperty("height", out var h)) return 16 / 9d;
        var width = w.GetDouble();
        var height = h.GetDouble();
        if (width <= 0 || height <= 0) return 16 / 9d;
        var aspect = width / height;
        if (video.TryGetProperty("sample_aspect_ratio", out var sar) && sar.GetString()?.Split(':') is [var num, var den]
            && double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.TryParse(den, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && n > 0 && d > 0)
            aspect *= n / d;
        // FFmpeg rotates phone video upright when decoding, so a quarter turn swaps the shape.
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in sideData.EnumerateArray())
            {
                if (item.TryGetProperty("rotation", out var rotation) && rotation.TryGetDouble(out var degrees) && Math.Abs(Math.Abs(degrees) % 180 - 90) < 1)
                    aspect = 1 / aspect;
            }
        }

        return aspect;
    }
}
