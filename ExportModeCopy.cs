namespace VideoTrimmer;

static class ExportModeCopy
{
    public const string FastTitle = "Fast video copy";
    public const string ExactTitle = "Exact frames";

    public const string FastExplanation =
        "Copies video and converts audio to stereo AAC. The cut can start slightly earlier, on a keyframe.";

    public const string ExactBaseExplanation =
        "Re-encodes so the cut matches the frames you chose. Slower, and the picture can change.";

    public const string SubtitlesOffTitle = "Burn in subtitles";

    public const string FastSubtitlesHint = "To burn in subtitles, choose Exact frames.";

    public const string FastMultiExplanation =
        "Copies each video segment with stereo AAC audio, then joins them. Every segment can start slightly earlier, on a keyframe.";

    public const string ExactMultiAudio =
        "Several segments keep the first video and audio track, and audio is folded down to stereo AAC.";

    /// <summary>
    /// The one or two sentences under the export options. <paramref name="convertTo"/> names the new container when the
    /// export changes it, or is null when it keeps the source's. <paramref name="misfits"/> names the streams the export
    /// would have to copy into it but can't (see <see cref="OutputFormats.Misfits"/>); <paramref name="dropsPictureSubtitles"/>
    /// is set when the new container leaves the source's picture subtitles out.
    /// </summary>
    public static string Summary(bool exact, ExactExportOptions options, int segments, string? convertTo,
        IReadOnlyList<string>? misfits = null, bool dropsPictureSubtitles = false)
    {
        if (exact)
        {
            var text = ExactBaseExplanation;
            if (segments > 1) text += " " + ExactMultiAudio;
            // Several segments carry no subtitle streams at all, which the sentence above already says.
            if (convertTo is not null && dropsPictureSubtitles && segments == 1) text += " " + PictureSubtitlesHint(convertTo, exact: true);
            return text;
        }

        var fast = segments > 1 ? FastMultiExplanation : FastExplanation;
        if (convertTo is null) return fast;
        if (misfits is { Count: > 0 }) return fast + " " + FastMisfitHint(convertTo, misfits);
        fast += " " + FastConvertHint(convertTo);
        return dropsPictureSubtitles ? fast + " " + PictureSubtitlesHint(convertTo, exact: false) : fast;
    }

    public static string FastConvertHint(string format) =>
        $"They are rewrapped as {format}, which some audio and subtitle types can't be.";

    public static string FastMisfitHint(string format, IReadOnlyList<string> misfits) =>
        $"{Join(misfits)} can't be copied into {format}. Choose Exact frames to convert it.";

    /// <summary>In Fast copy the existing hint already says burning in needs Exact frames.</summary>
    public static string PictureSubtitlesHint(string format, bool exact) => exact
        ? $"Picture subtitles are left out of {format}; burn one in to keep it."
        : $"Picture subtitles are left out of {format}.";

    static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => items[0] + " and " + items[1],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    public static string ConvertFailedHint(string format, bool exact) => exact
        ? ""
        : $"This video's streams can't be copied into {format} as they are. Choose Exact frames to convert it.";

    public static string Explanation(bool exact, ExactExportOptions options = default, int segments = 1)
    {
        if (!exact) return segments > 1 ? FastMultiExplanation : FastExplanation;
        if (options.Equals(default)) options = ExactExportOptions.Default;
        return ExactBaseExplanation
            + " "
            + ExactExportPresets.Hint(options.Quality)
            + " "
            + (segments > 1 ? ExactMultiAudio : ExactExportPresets.AudioHint())
            + (options.BurnSubtitles is null ? "" : " " + ExactExportPresets.SubtitleHint(options.BurnSubtitles));
    }
}
