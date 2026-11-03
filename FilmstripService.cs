namespace VideoTrimmer;

sealed class FilmstripService
{
    readonly FfmpegRunner runner;
    readonly StillCapture stills;
    readonly Dictionary<int, Bitmap?[]> filmCache = new();
    readonly HashSet<Bitmap?[]> releasedStrips = new();

    Bitmap?[]? displayedStrip;
    int generation;

    public FilmstripService(FfmpegRunner runner, StillCapture stills)
    {
        this.runner = runner;
        this.stills = stills;
    }

    public int CurrentCount { get; private set; }
    public int BuildingCount { get; private set; }
    public int Generation => generation;

    public void InvalidateJobs()
    {
        generation++;
        BuildingCount = 0;
    }

    public bool TryGetCached(int count, out Bitmap?[]? slots) => filmCache.TryGetValue(count, out slots);

    public void Show(TimelineControl timeline, Bitmap?[]? slots)
    {
        displayedStrip = slots;
        timeline.UseStrip(slots);
    }

    public void ShowCached(TimelineControl timeline, int count, Bitmap?[] slots)
    {
        Show(timeline, slots);
        CurrentCount = count;
    }

    public void Clear(TimelineControl timeline)
    {
        timeline.UseStrip(null);
        var doomed = new HashSet<Bitmap?[]>(filmCache.Values);
        if (displayedStrip is not null) doomed.Add(displayedStrip);
        filmCache.Clear();
        displayedStrip = null;
        CurrentCount = 0;
        BuildingCount = 0;
        foreach (var slots in doomed)
            Release(slots);
        releasedStrips.Clear();
    }

    public void Start(
        Control owner,
        TimelineControl timeline,
        string input,
        double duration,
        int count,
        CancellationToken token,
        Action<int, int> onProgress,
        Action<bool, string> onCompleted,
        Func<bool> isExporting)
    {
        if (count < 1 || runner.Ffmpeg is null)
        {
            timeline.Placeholder = runner.Ffmpeg is null
                ? "FFmpeg is not available, so there is no filmstrip."
                : "";
            timeline.Invalidate();
            return;
        }

        var buildGeneration = ++generation;
        BuildingCount = count;
        _ = BuildAsync(owner, timeline, input, duration, count, buildGeneration, token, onProgress, onCompleted, isExporting);
    }

    async Task BuildAsync(
        Control owner,
        TimelineControl timeline,
        string input,
        double duration,
        int count,
        int buildGeneration,
        CancellationToken token,
        Action<int, int> onProgress,
        Action<bool, string> onCompleted,
        Func<bool> isExporting)
    {
        // Sparse fast seeks stay responsive on long videos. One fps filter would decode the whole file.
        var slots = new Bitmap?[count];
        var height = timeline.ThumbHeight;
        if (buildGeneration != generation || owner.IsDisposed) return;
        Show(timeline, slots);
        timeline.Placeholder = "";
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

        Keep(count, slots);
        CurrentCount = count;
        BuildingCount = 0;
        var any = false;
        foreach (var frame in slots)
        {
            if (frame is null) continue;
            any = true;
            break;
        }

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
                var time = count == 1 ? 0 : duration * index / (count - 1d);
                if (time > duration - 0.05) time = Math.Max(0, duration - 0.05);
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
                    // Paint in batches so a 32-thumb strip does not redraw on every slot.
                    if (filled == count || (filled & 3) == 0)
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

    void Keep(int count, Bitmap?[] slots)
    {
        foreach (var key in filmCache.Keys.ToArray())
        {
            if (key == count) continue;
            var old = filmCache[key];
            filmCache.Remove(key);
            if (old != displayedStrip && old != slots)
                Release(old);
        }

        if (filmCache.TryGetValue(count, out var existing) && existing != slots && existing != displayedStrip)
            Release(existing);
        filmCache[count] = slots;
    }

    void Release(Bitmap?[]? slots)
    {
        if (slots is null || !releasedStrips.Add(slots)) return;
        foreach (var bitmap in slots)
            bitmap?.Dispose();
    }
}
