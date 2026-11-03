using System.Globalization;
using System.Text.Json;

namespace VideoTrimmer;

readonly record struct MediaInfo(double Duration, double Fps, long Frames);

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
        return new MediaInfo(probedDuration, probedFps, (long)rounded);
    }
}
