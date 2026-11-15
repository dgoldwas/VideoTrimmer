using System.Globalization;

namespace VideoTrimmer;

sealed class StillCapture
{
    readonly FfmpegRunner runner;
    readonly FrameCache cache = new();

    public StillCapture(FfmpegRunner runner) => this.runner = runner;

    public FrameCache Cache => cache;

    public void Clear() => cache.Clear();

    /// <summary>How far before the frame an accurate still jumps with the quick seek, then decodes forward from.</summary>
    const double AccurateLeadIn = 2;

    public async Task<Bitmap?> CaptureAsync(string input, double duration, double seconds, bool accurate, string filter, int quality, CancellationToken token)
    {
        if (runner.Ffmpeg is null) return null;
        var args = Arguments(input, Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001)), accurate, filter, quality);
        try
        {
            var bytes = await runner.RunBinaryAsync(runner.Ffmpeg, args, token).ConfigureAwait(false);
            if (token.IsCancellationRequested || bytes.Length == 0) return null;
            return BitmapFromJpeg(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Pipes one JPEG to stdout so stills skip temp files. A fast still seeks before -i, to the keyframe at or before the time.
    /// An accurate one seeks in two steps: before -i to a little short of the time, then after -i the rest of the way, which
    /// stays frame-exact but decodes only the lead-in instead of everything from the start of the file.
    /// </summary>
    static string[] Arguments(string input, double seconds, bool accurate, string filter, int quality)
    {
        var jump = accurate ? Math.Max(0, seconds - AccurateLeadIn) : seconds;
        return
        [
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", Text(jump),
            "-i", input,
            .. accurate ? ["-ss", Text(seconds - jump)] : Array.Empty<string>(),
            "-frames:v", "1",
            "-vf", filter,
            "-q:v", quality.ToString(CultureInfo.InvariantCulture),
            "-f", "image2pipe",
            "-vcodec", "mjpeg",
            "pipe:1",
        ];
    }

    static string Text(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var late = string.Join(" ", Arguments("in.mkv", 600.5, accurate: true, "scale=640:-2", 2));
        Expect(late.Contains("-ss 598.5 -i in.mkv -ss 2 -frames:v 1"), "an accurate still decodes two seconds, not the whole lead-up");
        var early = string.Join(" ", Arguments("in.mkv", 0.75, accurate: true, "scale=640:-2", 2));
        Expect(early.Contains("-ss 0 -i in.mkv -ss 0.75 -frames:v 1"), "an accurate still near the start seeks from zero");
        var fast = string.Join(" ", Arguments("in.mkv", 600.5, accurate: false, "scale=640:-2", 3));
        Expect(fast.Contains("-ss 600.5 -i in.mkv -frames:v 1"), "a fast still seeks once, before the input");
        return errors;
    }

    static Bitmap? BitmapFromJpeg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        return new Bitmap(image);
    }
}

/// <summary>
/// LRU cache of preview stills. Ownership rules:
/// - <see cref="Put"/> takes ownership of the bitmap (or disposes it if unused).
/// - Call <see cref="SetShown"/> with the bitmap currently in the preview PictureBox
///   so replace/trim will not dispose that live image.
/// - Clear the PictureBox, call <see cref="SetShown"/>(null), then <see cref="Clear"/> when unloading.
/// </summary>
sealed class FrameCache
{
    const int Limit = 24;

    readonly record struct Key(long Frame, int Width);

    sealed class Slot
    {
        public Bitmap? Fast;
        public Bitmap? Accurate;
    }

    readonly Dictionary<Key, Slot> slots = new();
    readonly Queue<Key> order = new();
    Bitmap? shown;

    public void SetShown(Bitmap? bitmap) => shown = bitmap;

    public bool TryGet(long frame, int width, bool accurate, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!slots.TryGetValue(new Key(frame, width), out var slot)) return false;
        if (accurate)
        {
            bitmap = slot.Accurate;
            return bitmap is not null;
        }

        bitmap = slot.Accurate ?? slot.Fast;
        return bitmap is not null;
    }

    public void Put(long frame, int width, Bitmap bitmap, bool accurate)
    {
        var key = new Key(frame, width);
        if (!slots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            slots[key] = slot;
            order.Enqueue(key);
        }

        if (accurate)
            Replace(slot, true, bitmap);
        else if (slot.Accurate is null)
            Replace(slot, false, bitmap);
        else
            bitmap.Dispose();

        Trim();
    }

    public void Clear()
    {
        foreach (var slot in slots.Values)
        {
            DisposeUnlessShown(slot.Fast);
            DisposeUnlessShown(slot.Accurate);
        }

        slots.Clear();
        order.Clear();
    }

    void Replace(Slot slot, bool accurate, Bitmap bitmap)
    {
        var previous = accurate ? slot.Accurate : slot.Fast;
        if (accurate) slot.Accurate = bitmap;
        else slot.Fast = bitmap;
        DisposeUnlessShown(previous, bitmap);
    }

    void Trim()
    {
        var guard = 0;
        while (order.Count > Limit && guard++ < order.Count + 2)
        {
            var key = order.Dequeue();
            if (!slots.TryGetValue(key, out var slot)) continue;
            var visible = slot.Fast == shown || slot.Accurate == shown;
            if (visible)
            {
                order.Enqueue(key);
                continue;
            }

            slot.Fast?.Dispose();
            slot.Accurate?.Dispose();
            slots.Remove(key);
        }
    }

    void DisposeUnlessShown(Bitmap? bitmap, Bitmap? incoming = null)
    {
        if (bitmap is null || bitmap == shown || bitmap == incoming) return;
        bitmap.Dispose();
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var cache = new FrameCache();
        var live = new Bitmap(2, 2);
        cache.SetShown(live);
        cache.Put(0, 320, live, accurate: false);

        for (var i = 1; i <= Limit + 4; i++)
            cache.Put(i, 320, new Bitmap(1, 1), accurate: false);

        Expect(Alive(live), "trim kept the shown bitmap");

        var replacement = new Bitmap(2, 2);
        cache.Put(0, 320, replacement, accurate: true);
        Expect(Alive(live), "replace kept the shown bitmap");
        Expect(Alive(replacement), "replacement stayed alive");

        cache.SetShown(null);
        cache.Clear();
        Expect(!Alive(live), "clear disposed the unpinned former shown bitmap");
        Expect(!Alive(replacement), "clear disposed cached bitmaps");
        return errors;

        static bool Alive(Bitmap bitmap)
        {
            try
            {
                _ = bitmap.GetPixel(0, 0);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
