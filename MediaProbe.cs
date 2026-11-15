using System.Globalization;
using System.Text.Json;

namespace VideoTrimmer;

/// <summary>
/// <see cref="Aspect"/> is the displayed width over height, after pixel aspect and rotation. <see cref="VideoCodec"/> is the
/// first video stream's codec_name and <see cref="AudioCodecs"/> every audio stream's, in order.
/// </summary>
readonly record struct MediaInfo(double Duration, double Fps, long Frames, bool HasAudio, double Aspect, IReadOnlyList<SubtitleTrack> Subtitles,
    string VideoCodec, IReadOnlyList<string> AudioCodecs, VideoColor Color = default);

readonly record struct VideoColor(string? Transfer, string? Primaries, string? Matrix, int DolbyProfile = 0, int Compatibility = 0)
{
    public bool IsHdr => DolbyProfile > 0 || Transfer is "smpte2084" or "arib-std-b67";
}

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

        var media = Parse(json);
        if (media.Color.Transfer is null or "unknown")
        {
            // Some containers expose colorimetry only after decoding. Bound this to eight opening video packets.
            var frames = await runner.RunAsync(runner.Ffprobe, ["-v", "quiet", "-print_format", "json",
                "-select_streams", "v:0", "-read_intervals", "%+#8", "-show_frames",
                "-show_entries", "frame=color_transfer,color_primaries,color_space", path], cancellationToken).ConfigureAwait(false);
            using var frameDoc = JsonDocument.Parse(frames);
            if (frameDoc.RootElement.TryGetProperty("frames", out var list))
                foreach (var frame in list.EnumerateArray())
                {
                    var color = Color(frame);
                    if (color.Transfer is null or "unknown") continue;
                    media = media with { Color = color with { DolbyProfile = media.Color.DolbyProfile, Compatibility = media.Color.Compatibility } };
                    break;
                }
        }
        return media;
    }

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var video = root.GetProperty("streams").EnumerateArray()
            .FirstOrDefault(stream => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The file does not contain a video stream.");
        var audioCodecs = root.GetProperty("streams").EnumerateArray()
            .Where(stream => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "audio")
            .Select(Codec)
            .ToList();
        var hasAudio = audioCodecs.Count > 0;

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
        return new MediaInfo(probedDuration, probedFps, (long)rounded, hasAudio, DisplayAspect(video), Subtitles(root), Codec(video), audioCodecs, Color(video));
    }

    static VideoColor Color(JsonElement video)
    {
        string? Tag(string key) => video.TryGetProperty(key, out var value) ? value.GetString() : null;
        var profile = 0;
        var compatibility = 0;
        if (video.TryGetProperty("side_data_list", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var item in data.EnumerateArray())
                if (item.TryGetProperty("dv_profile", out var p))
                {
                    profile = p.GetInt32();
                    if (item.TryGetProperty("dv_bl_signal_compatibility_id", out var c)) compatibility = c.GetInt32();
                }
        return new VideoColor(Tag("color_transfer"), Tag("color_primaries"), Tag("color_space"), profile, compatibility);
    }

    static string Codec(JsonElement stream) => stream.TryGetProperty("codec_name", out var name) ? name.GetString() ?? "" : "";

    static IReadOnlyList<SubtitleTrack> Subtitles(JsonElement root)
    {
        var tracks = new List<SubtitleTrack>();
        var index = -1;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object || !stream.TryGetProperty("codec_type", out var kind) || kind.GetString() != "subtitle") continue;
            // Count every subtitle stream, so the index stays in step with FFmpeg's numbering when one is left out.
            index++;
            var codec = Codec(stream);
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
              {"codec_type":"video","codec_name":"wmv3","r_frame_rate":"24/1","duration":"10.0","width":1920,"height":1080},
              {"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}},
              {"codec_type":"audio","codec_name":"truehd"},
              {"codec_type":"audio","codec_name":"ac3"},
              {"codec_type":"subtitle","codec_name":"dvb_teletext"},
              {"codec_type":"subtitle","codec_name":"hdmv_pgs_subtitle","tags":{"language":"fre","title":"Forced"}}
            ],"format":{"duration":"10.0"}}
            """;
        var info = Parse(json);
        var hdr = Parse(json.Replace("\"codec_name\":\"wmv3\"", "\"codec_name\":\"hevc\",\"color_transfer\":\"smpte2084\",\"color_primaries\":\"bt2020\",\"color_space\":\"bt2020nc\""));
        Expect(hdr.Color.IsHdr && hdr.Color.Primaries == "bt2020", "PQ metadata identifies HDR without frame scanning");
        var dolby = Parse(json.Replace("\"codec_name\":\"wmv3\"", "\"codec_name\":\"hevc\",\"side_data_list\":[{\"side_data_type\":\"DOVI configuration record\",\"dv_profile\":5,\"dv_bl_signal_compatibility_id\":0}]"));
        Expect(dolby.Color.IsHdr && dolby.Color.DolbyProfile == 5, "Dolby Vision is detected even without HDR transfer tags");
        var subtitles = info.Subtitles;
        Expect(info.VideoCodec == "wmv3" && info.AudioCodecs is ["truehd", "ac3"] && info.HasAudio, "the video and audio codecs are read");
        Expect(subtitles.Count == 2, "teletext is left out of the subtitle tracks");
        Expect(subtitles.Count == 2 && subtitles[0].Index == 0 && subtitles[1].Index == 2, "subtitle indexes follow FFmpeg's numbering");
        Expect(subtitles.Count == 2 && !subtitles[0].IsBitmap && subtitles[1].IsBitmap, "picture subtitles are told apart");
        Expect(subtitles.Count == 2 && subtitles[0].Label == "Track 1: English" && subtitles[1].Label == "Track 3: Forced", "tracks are labeled by title, then language");
        return errors;
    }
}
