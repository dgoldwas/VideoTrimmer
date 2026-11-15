namespace VideoTrimmer;

/// <summary>The container the export is written to. <see cref="Source"/> keeps the input's own.</summary>
enum OutputFormat
{
    Source,
    Mp4,
    Mkv,
    Mov,
}

static class OutputFormats
{
    public static readonly OutputFormat[] All = [OutputFormat.Source, OutputFormat.Mp4, OutputFormat.Mkv, OutputFormat.Mov];

    public static string Name(OutputFormat format) => format switch
    {
        OutputFormat.Mp4 => "MP4",
        OutputFormat.Mkv => "MKV",
        OutputFormat.Mov => "MOV",
        _ => "Same as source",
    };

    /// <summary>The extension to save with, dot included.</summary>
    public static string Extension(OutputFormat format, string sourceExtension) => format switch
    {
        OutputFormat.Mp4 => ".mp4",
        OutputFormat.Mkv => ".mkv",
        OutputFormat.Mov => ".mov",
        _ => sourceExtension,
    };

    /// <summary>"MKV" for ".mkv"; "the source" when there is no extension to name.</summary>
    public static string Label(string extension)
    {
        var name = extension.TrimStart('.').ToUpperInvariant();
        return name.Length == 0 ? "the source" : name;
    }

    /// <summary>MP4 and MOV share a muxer family, and with it the same limits on what can be copied in.</summary>
    public static bool IsMp4Family(string extension) =>
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);

    /// <summary>The extensions the open dialog lists as video, dot included.</summary>
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".wmv"];

    static readonly HashSet<string> Mp4Video = ["h264", "hevc", "av1", "vp9", "mpeg4"];
    static readonly HashSet<string> Mp4Audio = ["aac", "mp3", "ac3", "eac3", "opus", "alac"];

    /// <summary>
    /// Whether a stream copy can put <paramref name="codec"/> (an FFprobe codec_name) into <paramref name="extension"/>.
    /// MP4 and MOV take a short list, MOV adds PCM audio, and MKV takes nearly anything. An unknown codec is given the benefit of the doubt.
    /// </summary>
    public static bool CanCopy(string extension, string codec, bool audio)
    {
        if (codec.Length == 0 || !IsMp4Family(extension)) return true;
        if (!audio) return Mp4Video.Contains(codec);
        return Mp4Audio.Contains(codec) || (extension.Equals(".mov", StringComparison.OrdinalIgnoreCase) && codec.StartsWith("pcm_", StringComparison.Ordinal));
    }

    /// <summary>The streams a copy into <paramref name="extension"/> can't carry, named for the user: "WMV3 video", "TRUEHD audio".</summary>
    public static IReadOnlyList<string> Misfits(string extension, string videoCodec, IEnumerable<string> audioCodecs, bool includeVideo = true)
    {
        var misfits = new List<string>();
        if (includeVideo && !CanCopy(extension, videoCodec, audio: false)) misfits.Add(videoCodec.ToUpperInvariant() + " video");
        foreach (var codec in audioCodecs.Distinct())
            if (!CanCopy(extension, codec, audio: true)) misfits.Add(codec.ToUpperInvariant() + " audio");
        return misfits;
    }

    /// <summary>
    /// The file to write when the user typed <paramref name="typed"/> with a format chosen. Another video extension is
    /// replaced (clip.mkv becomes clip.mp4, and so does a clip.mkv.mp4 the dialog may have made of it); anything else gets
    /// the extension added.
    /// </summary>
    public static string SaveName(string typed, string extension)
    {
        var typedExtension = Path.GetExtension(typed);
        if (typedExtension.Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            var stem = typed[..^typedExtension.Length];
            return IsVideoExtension(Path.GetExtension(stem)) ? Path.ChangeExtension(stem, extension) : typed;
        }

        return IsVideoExtension(typedExtension) ? Path.ChangeExtension(typed, extension) : typed + extension;
    }

    static bool IsVideoExtension(string extension) =>
        extension.Length > 0 && VideoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        Expect(SaveName(@"C:\v\clip.mkv", ".mp4") == @"C:\v\clip.mp4", "another video extension is replaced");
        Expect(SaveName(@"C:\v\clip.mkv.mp4", ".mp4") == @"C:\v\clip.mp4", "a doubled extension is undone");
        Expect(SaveName(@"C:\v\clip.MP4", ".mp4") == @"C:\v\clip.MP4", "the chosen extension is kept as typed");
        Expect(SaveName(@"C:\v\take.2", ".mp4") == @"C:\v\take.2.mp4", "a name that only has a dot gets the extension");
        Expect(SaveName(@"C:\v\clip", ".mov") == @"C:\v\clip.mov", "a bare name gets the extension");
        Expect(Misfits(".mp4", "wmv3", ["truehd", "aac"]).SequenceEqual(["WMV3 video", "TRUEHD audio"]), "MP4 can't take WMV3 or TrueHD");
        Expect(Misfits(".mov", "h264", ["pcm_s16le"]).Count == 0 && Misfits(".mp4", "h264", ["pcm_s16le"]).Count == 1, "MOV takes PCM and MP4 does not");
        Expect(Misfits(".mkv", "wmv3", ["truehd"]).Count == 0, "MKV takes anything");
        Expect(Misfits(".mp4", "wmv3", ["aac"], includeVideo: false).Count == 0, "re-encoded video is not checked");
        return errors;
    }
}
