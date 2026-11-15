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
}
