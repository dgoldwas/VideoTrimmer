using System.Globalization;
using System.Text.Json;

namespace VideoTrimmer;

/// <summary><see cref="Aspect"/> is the displayed width over height, after pixel aspect and rotation.</summary>
readonly record struct MediaInfo(double Duration, double Fps, long Frames, bool HasAudio, double Aspect, IReadOnlyList<SubtitleTrack> Subtitles);

/// <summary>A subtitle stream in the source. <see cref="Index"/> counts subtitle streams only, as in FFmpeg's <c>0:s:N</c>.</summary>
readonly record struct SubtitleTrack(int Index, string Codec, string? Language, string? Title)
{
    /// <summary>Picture subtitles (Blu-ray, DVD, DVB) are overlaid; text subtitles are drawn by libass.</summary>
    public bool IsBitmap => Codec is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";

    public string Label
    {
        get
        {
            var name = Title ?? (Language is null or "und" ? null : CultureName(Language));
            return name is null ? $"Track {Index + 1}" : $"Track {Index + 1}: {name}";
        }
    }

    static string CultureName(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
                .FirstOrDefault(c => c.ThreeLetterISOLanguageName == language || c.TwoLetterISOLanguageName == language);
            return culture?.EnglishName ?? language;
        }
        catch (CultureNotFoundException)
        {
            return language;
        }
    }
}

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
        return new MediaInfo(probedDuration, probedFps, (long)rounded, hasAudio, DisplayAspect(video), Subtitles(root));
    }

    static IReadOnlyList<SubtitleTrack> Subtitles(JsonElement root)
    {
        var tracks = new List<SubtitleTrack>();
        var index = -1;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object || !stream.TryGetProperty("codec_type", out var kind) || kind.GetString() != "subtitle") continue;
            // Count every subtitle stream, so the index stays in step with FFmpeg's numbering when one is left out.
            index++;
            var codec = stream.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "";
            string? Tag(string key) => stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                && tags.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
            // Teletext and caption data cannot be burned in by the subtitles or overlay filters.
            if (codec is "" or "dvb_teletext" or "eia_608" or "bin_data") continue;
            tracks.Add(new SubtitleTrack(index, codec, Tag("language"), Tag("title")));
        }

        return tracks;
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

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        const string json = """
            {"streams":[
              {"codec_type":"video","r_frame_rate":"24/1","duration":"10.0","width":1920,"height":1080},
              {"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}},
              {"codec_type":"audio"},
              {"codec_type":"subtitle","codec_name":"dvb_teletext"},
              {"codec_type":"subtitle","codec_name":"hdmv_pgs_subtitle","tags":{"language":"fre","title":"Forced"}}
            ],"format":{"duration":"10.0"}}
            """;
        var subtitles = Parse(json).Subtitles;
        Expect(subtitles.Count == 2, "teletext is left out of the subtitle tracks");
        Expect(subtitles.Count == 2 && subtitles[0].Index == 0 && subtitles[1].Index == 2, "subtitle indexes follow FFmpeg's numbering");
        Expect(subtitles.Count == 2 && !subtitles[0].IsBitmap && subtitles[1].IsBitmap, "picture subtitles are told apart");
        Expect(subtitles.Count == 2 && subtitles[0].Label == "Track 1: English" && subtitles[1].Label == "Track 3: Forced", "tracks are labeled by title, then language");
        return errors;
    }
}
