using System.Globalization;

namespace VideoTrimmer;

sealed class StillCapture
{
    readonly FfmpegRunner runner;
    readonly FrameCache cache = new();

    public StillCapture(FfmpegRunner runner) => this.runner = runner;

    public FrameCache Cache => cache;

    public void Clear() => cache.Clear();

    public async Task<Bitmap?> CaptureAsync(string input, double duration, double seconds, bool accurate, string filter, int quality, CancellationToken token)
    {
        if (runner.Ffmpeg is null) return null;
        var time = Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001)).ToString("0.######", CultureInfo.InvariantCulture);
        // Pipe JPEG to stdout so stills skip temp files. Accurate seeks after -i; fast seeks before.
        string[] args = accurate
            ?
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", input,
                "-ss", time,
                "-frames:v", "1",
                "-vf", filter,
                "-q:v", quality.ToString(CultureInfo.InvariantCulture),
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "pipe:1",
            ]
            :
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-ss", time,
                "-i", input,
                "-frames:v", "1",
                "-vf", filter,
                "-q:v", quality.ToString(CultureInfo.InvariantCulture),
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "pipe:1",
            ];
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

    static Bitmap? BitmapFromJpeg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        return new Bitmap(image);
    }
}

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

    public void Put(long frame, int width, Bitmap bitmap, bool accurate, Bitmap? shown)
    {
        var key = new Key(frame, width);
        if (!slots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            slots[key] = slot;
            order.Enqueue(key);
        }

        if (accurate)
            Replace(slot, true, bitmap, shown);
        else if (slot.Accurate is null)
            Replace(slot, false, bitmap, shown);
        else
            bitmap.Dispose();

        Trim(shown);
    }

    public void Clear()
    {
        foreach (var slot in slots.Values)
        {
            slot.Fast?.Dispose();
            slot.Accurate?.Dispose();
        }

        slots.Clear();
        order.Clear();
    }

    static void Replace(Slot slot, bool accurate, Bitmap bitmap, Bitmap? shown)
    {
        var previous = accurate ? slot.Accurate : slot.Fast;
        if (accurate) slot.Accurate = bitmap;
        else slot.Fast = bitmap;
        if (previous is not null && previous != bitmap && previous != shown)
            previous.Dispose();
    }

    void Trim(Bitmap? shown)
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
}
