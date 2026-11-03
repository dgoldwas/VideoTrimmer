namespace VideoTrimmer;

enum ExactQuality
{
    High,
    Balanced,
    Small,
}

readonly record struct ExactExportOptions(ExactQuality Quality, bool CopyAudio)
{
    public static ExactExportOptions Default { get; } = new(ExactQuality.Balanced, CopyAudio: false);
}

static class ExactExportPresets
{
    public static string Title(ExactQuality quality) => quality switch
    {
        ExactQuality.High => "High",
        ExactQuality.Small => "Small",
        _ => "Balanced",
    };

    public static string Hint(ExactQuality quality) => quality switch
    {
        ExactQuality.High => "CRF 18, slower encode, finer detail.",
        ExactQuality.Small => "CRF 28, faster encode, smaller file.",
        _ => "CRF 23, balanced quality and size.",
    };

    public static (int Crf, string Preset) Encoder(ExactQuality quality) => quality switch
    {
        ExactQuality.High => (18, "slow"),
        ExactQuality.Small => (28, "veryfast"),
        _ => (23, "medium"),
    };

    public static string AudioHint(bool copyAudio) => copyAudio
        ? "Keeps the original audio track."
        : "Re-encodes audio to AAC.";
}
