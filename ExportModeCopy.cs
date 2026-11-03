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

    public static string Explanation(bool exact, ExactExportOptions options = default)
    {
        if (!exact) return FastExplanation;
        if (options.Equals(default)) options = ExactExportOptions.Default;
        return ExactBaseExplanation
            + " "
            + ExactExportPresets.Hint(options.Quality)
            + " "
            + ExactExportPresets.AudioHint(options.CopyAudio);
    }
}
