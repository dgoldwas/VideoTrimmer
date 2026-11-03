namespace VideoTrimmer;

static class ExportModeCopy
{
    public const string FastTitle = "Fast copy";
    public const string ExactTitle = "Exact frames";

    public const string FastExplanation =
        "Keeps the original streams. The cut can start slightly earlier, on a keyframe.";

    public const string ExactExplanation =
        "Re-encodes so the cut matches the frames you chose. Slower, and the picture can change.";

    public static string Explanation(bool exact) => exact ? ExactExplanation : FastExplanation;
}
