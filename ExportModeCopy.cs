namespace VideoTrimmer;

static class ExportModeCopy
{
    public const string FastTitle = "Fast copy";
    public const string ExactTitle = "Exact frames";

    public const string FastExplanation =
        "Keeps the original streams. The cut can start slightly earlier, on a keyframe.";

    public const string ExactBaseExplanation =
        "Re-encodes so the cut matches the frames you chose. Slower, and the picture can change.";

    public const string CopyAudioTitle = "Copy audio";

    public const string FastMultiExplanation =
        "Copies each segment, then joins them without re-encoding. Every segment can start slightly earlier, on a keyframe.";

    public const string ExactMultiAudio =
        "Several segments keep the first video and audio track, and audio is re-encoded to AAC.";

    public static string Explanation(bool exact, ExactExportOptions options = default, int segments = 1)
    {
        if (!exact) return segments > 1 ? FastMultiExplanation : FastExplanation;
        if (options.Equals(default)) options = ExactExportOptions.Default;
        return ExactBaseExplanation
            + " "
            + ExactExportPresets.Hint(options.Quality)
            + " "
            + (segments > 1 ? ExactMultiAudio : ExactExportPresets.AudioHint(options.CopyAudio));
    }
}
