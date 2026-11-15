namespace VideoTrimmer;

static class ExportModeCopy
{
    public const string FastTitle = "Fast copy";
    public const string ExactTitle = "Exact frames";

    public const string FastExplanation =
        "Keeps the original streams. The cut can start slightly earlier, on a keyframe.";

    public const string ExactBaseExplanation =
        "Re-encodes so the cut matches the frames you chose. Slower, and the picture can change.";

    public const string CopyAudioTitle = "Copy original";
    public const string SubtitlesOffTitle = "Burn in subtitles";

    public const string FastSubtitlesHint = "To burn in subtitles, choose Exact frames.";

    public const string FastMultiExplanation =
        "Copies each segment, then joins them without re-encoding. Every segment can start slightly earlier, on a keyframe.";

    public const string ExactMultiAudio =
        "Several segments keep the first video and audio track, and audio is re-encoded to AAC.";

    /// <summary>
    /// The one or two sentences under the export options. <paramref name="convertTo"/> names the new container when the
    /// export changes it, or is null when it keeps the source's.
    /// </summary>
    public static string Summary(bool exact, ExactExportOptions options, int segments, string? convertTo)
    {
        if (exact)
        {
            var text = ExactBaseExplanation;
            if (segments > 1) text += " " + ExactMultiAudio;
            return text;
        }

        var fast = segments > 1 ? FastMultiExplanation : FastExplanation;
        return convertTo is null ? fast : fast + " " + FastConvertHint(convertTo);
    }

    public static string FastConvertHint(string format) =>
        $"They are rewrapped as {format}, which some audio and subtitle types can't be.";

    public static string ConvertFailedHint(string format, bool exact, bool copyAudio) => exact
        ? copyAudio ? $"This audio can't be copied into {format}. Turn off Copy original and export again." : ""
        : $"This video's streams can't be copied into {format} as they are. Choose Exact frames to convert it.";

    public static string Explanation(bool exact, ExactExportOptions options = default, int segments = 1)
    {
        if (!exact) return segments > 1 ? FastMultiExplanation : FastExplanation;
        if (options.Equals(default)) options = ExactExportOptions.Default;
        return ExactBaseExplanation
            + " "
            + ExactExportPresets.Hint(options.Quality)
            + " "
            + (segments > 1 ? ExactMultiAudio : ExactExportPresets.AudioHint(options.CopyAudio))
            + (options.BurnSubtitles is null ? "" : " " + ExactExportPresets.SubtitleHint(options.BurnSubtitles));
    }
}
