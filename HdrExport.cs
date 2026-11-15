namespace VideoTrimmer;

static class HdrExport
{
    public const string Placebo = "libplacebo=colorspace=bt709:color_primaries=bt709:color_trc=bt709:range=tv:format=yuv420p:tonemapping=bt.2390:apply_dolbyvision=true";
    const string StripHdr = ",sidedata=mode=delete:type=MASTERING_DISPLAY_METADATA,sidedata=mode=delete:type=CONTENT_LIGHT_LEVEL,sidedata=mode=delete:type=DYNAMIC_HDR_PLUS,sidedata=mode=delete:type=DOVI_RPU_BUFFER,sidedata=mode=delete:type=DOVI_METADATA";

    public static string Filter(VideoColor color, bool libplacebo)
    {
        if (!color.IsHdr) return "";
        if (libplacebo) return Placebo + StripHdr;
        if (!CanUseCpu(color)) throw new InvalidOperationException("This Dolby Vision video needs libplacebo and a working Vulkan driver. Install the full VideoTrimmer build and update your graphics driver.");
        var transfer = color.Compatibility switch { 1 => "smpte2084", 4 => "arib-std-b67", _ => color.Transfer };
        // Dolby Vision's HDR-compatible base layer is usable without its RPU; Profile 5 is not.
        var primaries = color.Primaries is null or "unknown" ? "bt2020" : color.Primaries;
        var matrix = color.Matrix is null or "unknown" ? "bt2020nc" : color.Matrix;
        return $"zscale=transferin={transfer}:primariesin={primaries}:matrixin={matrix}:transfer=linear:npl=100,format=gbrpf32le,tonemap=tonemap=mobius:desat=0,zscale=primaries=bt709:transfer=bt709:matrix=bt709:range=limited,format=yuv420p" + StripHdr;
    }

    public static bool CanUseCpu(VideoColor color) => color.DolbyProfile == 0 || color.Compatibility is 1 or 4;

    public static string[] OutputTags(VideoColor color) => color.IsHdr
        ? ["-pix_fmt", "yuv420p", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709", "-color_range", "tv"] : [];

    // Exercise the driver as well as the filter. This touches one synthetic frame, never the source movie.
    public static async Task<bool> SupportsPlaceboAsync(FfmpegRunner runner, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await runner.RunAsync(runner.Ffmpeg!, ["-v", "error", "-f", "lavfi", "-i", "color=size=32x32:duration=0.04", "-vf", Placebo, "-frames:v", "1", "-f", "null", "-"], timeout.Token);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }
}
