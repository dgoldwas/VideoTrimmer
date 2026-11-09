namespace VideoTrimmer;

/// <summary>The thumbnails for one visible window of the timeline.</summary>
readonly record struct StripKey(int Count, long Start, long End);

sealed class FilmstripService
{
    const int CacheLimit = 4;

    readonly FfmpegRunner runner;
    readonly StillCapture stills;
    // Most recently used last. Zooming back out finds the whole-clip strip here instead of decoding it again.
    readonly List<(StripKey Key, Bitmap?[] Slots)> filmCache = new();
    readonly HashSet<Bitmap?[]> releasedStrips = new();

    Bitmap?[]? displayedStrip;
    int generation;

    public FilmstripService(FfmpegRunner runner, StillCapture stills)
    {
        this.runner = runner;
        this.stills = stills;
    }

    public StripKey? Current { get; private set; }
    public StripKey? Building { get; private set; }

    public void InvalidateJobs()
    {
        generation++;
        Building = null;
    }

    /// <summary>Shows a cached strip for <paramref name="key"/> if there is one.</summary>
    public bool TryShowCached(TimelineControl timeline, StripKey key)
    {
        var index = filmCache.FindIndex(entry => entry.Key == key);
        if (index < 0) return false;
        var entry = filmCache[index];
        filmCache.RemoveAt(index);
        filmCache.Add(entry);
        Show(timeline, entry.Slots, key);
        Current = key;
        return true;
    }

    void Show(TimelineControl timeline, Bitmap?[]? slots, StripKey key)
    {
        // A strip that was evicted while on screen is freed once something replaces it.
        if (displayedStrip is not null && displayedStrip != slots && !filmCache.Any(entry => entry.Slots == displayedStrip))
            Release(displayedStrip);
        displayedStrip = slots;
        timeline.UseStrip(slots, key.Start, key.End);
    }

    public void Clear(TimelineControl timeline)
    {
        timeline.UseStrip(null, 0, 0);
        var doomed = new HashSet<Bitmap?[]>(filmCache.Select(entry => entry.Slots));
        if (displayedStrip is not null) doomed.Add(displayedStrip);
        filmCache.Clear();
        displayedStrip = null;
        Current = null;
        Building = null;
        foreach (var slots in doomed)
            Release(slots);
        releasedStrips.Clear();
    }

    public void Start(
        Control owner,
        TimelineControl timeline,
        string input,
        double duration,
        StripKey key,
        CancellationToken token,
        Action<int, int> onProgress,
        Action<bool, string> onCompleted,
        Func<bool> isExporting)
    {
        if (key.Count < 1 || runner.Ffmpeg is null)
        {
            timeline.Placeholder = runner.Ffmpeg is null
                ? "FFmpeg is not available, so there is no filmstrip."
                : "";
            timeline.Invalidate();
            return;
        }

        var buildGeneration = ++generation;
        Building = key;
        _ = BuildAsync(owner, timeline, input, duration, key, buildGeneration, token, onProgress, onCompleted, isExporting);
    }

    async Task BuildAsync(
        Control owner,
        TimelineControl timeline,
        string input,
        double duration,
        StripKey key,
        int buildGeneration,
        CancellationToken token,
        Action<int, int> onProgress,
        Action<bool, string> onCompleted,
        Func<bool> isExporting)
    {
        // Sparse fast seeks stay responsive on long videos. One fps filter would decode the whole file.
        var count = key.Count;
        var slots = new Bitmap?[count];
        var height = timeline.ThumbHeight;
        var fps = timeline.Fps;
        if (buildGeneration != generation || owner.IsDisposed) return;
        // Keep the previous strip on screen (stretched to the new view) until the new thumbnails arrive.
        var swapped = false;
        var gate = new SemaphoreSlim(4);
        var filled = 0;
        var tasks = new Task[count];
        for (var i = 0; i < count; i++)
            tasks[i] = FetchSlot(i);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // The next filmstrip replaced this one.
        }
        finally
        {
            gate.Dispose();
        }

        if (buildGeneration != generation || owner.IsDisposed)
        {
            if (displayedStrip != slots)
                Release(slots);
            return;
        }

        if (!swapped) Show(timeline, slots, key);
        Keep(key, slots);
        Current = key;
        Building = null;
        var any = slots.Any(frame => frame is not null);
        var failure = "No preview frames could be created. You can still set the range and export.";
        timeline.Placeholder = any ? "" : failure;
        timeline.Invalidate();
        onCompleted(any, failure);
        return;

        async Task FetchSlot(int index)
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                acquired = true;
                // The middle of the slot's share of the window.
                var frame = key.Start + (key.End - key.Start) * (index + 0.5) / count;
                var time = Math.Min(frame / fps, Math.Max(0, duration - 0.05));
                var bitmap = await stills.CaptureAsync(input, duration, time, accurate: false, "scale=-2:" + height, 5, token).ConfigureAwait(false);
                void Assign()
                {
                    if (buildGeneration != generation || owner.IsDisposed || bitmap is null)
                    {
                        bitmap?.Dispose();
                        return;
                    }

                    slots[index] = bitmap;
                    filled++;
                    // Swap to the new strip once a quarter of it is ready, so zooming does not flash empty slots.
                    if (!swapped && filled * 4 >= count)
                    {
                        swapped = true;
                        Show(timeline, slots, key);
                    }

                    // Paint in batches so a 32-thumb strip does not redraw on every slot.
                    if (swapped && (filled == count || (filled & 3) == 0))
                        timeline.Invalidate();
                    if (!isExporting())
                        onProgress(filled, count);
                }

                if (owner.IsDisposed || !owner.IsHandleCreated)
                    bitmap?.Dispose();
                else if (owner.InvokeRequired)
                    owner.BeginInvoke(Assign);
                else
                    Assign();
            }
            catch (OperationCanceledException)
            {
                // Canceled by the next filmstrip.
            }
            catch (ObjectDisposedException)
            {
                // The form closed while a thumbnail was still decoding.
            }
            finally
            {
                if (acquired)
                {
                    try { gate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    void Keep(StripKey key, Bitmap?[] slots)
    {
        var existing = filmCache.FindIndex(entry => entry.Key == key);
        if (existing >= 0)
        {
            var old = filmCache[existing].Slots;
            filmCache.RemoveAt(existing);
            if (old != slots && old != displayedStrip) Release(old);
        }

        filmCache.Add((key, slots));
        while (filmCache.Count > CacheLimit)
        {
            // Evict the oldest zoomed strip; the widest (whole-clip) strip is the one zooming out returns to.
            var widest = filmCache.Max(entry => entry.Key.End - entry.Key.Start);
            var victim = filmCache.FindIndex(entry => entry.Key != key && entry.Key.End - entry.Key.Start < widest);
            if (victim < 0) victim = filmCache.FindIndex(entry => entry.Key != key);
            var old = filmCache[victim].Slots;
            filmCache.RemoveAt(victim);
            if (old != displayedStrip) Release(old);
        }
    }

    void Release(Bitmap?[]? slots)
    {
        if (slots is null || !releasedStrips.Add(slots)) return;
        foreach (var bitmap in slots)
            bitmap?.Dispose();
    }
}
