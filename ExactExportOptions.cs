namespace VideoTrimmer;

enum ExactQuality
{
    High,
    Balanced,
    Small,
}

enum ExportSize { Source, Hd1080 }

/// <summary><see cref="BurnSubtitles"/> is the source subtitle track drawn into the picture, or null for none.</summary>
readonly record struct ExactExportOptions(ExactQuality Quality, SubtitleTrack? BurnSubtitles = null, VideoColor Color = default, bool UseLibplacebo = false, ExportSize Size = ExportSize.Source)
{
    public static ExactExportOptions Default { get; } = new(ExactQuality.Balanced);
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
        _ => "CRF 23, fast encoding with balanced quality and size.",
    };

    public static (int Crf, string Preset) Encoder(ExactQuality quality) => quality switch
    {
        ExactQuality.High => (18, "slow"),
        ExactQuality.Small => (28, "veryfast"),
        _ => (23, "veryfast"),
    };

    public static string AudioHint() => "Folds audio down to stereo AAC, 192 kbps.";

    public static string SubtitleHint(SubtitleTrack? track) => track is { } burn
        ? $"Burns {burn.Label} into the picture."
        : "Subtitles are not burned in.";
}
