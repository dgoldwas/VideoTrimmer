using System.Drawing.Drawing2D;

namespace VideoTrimmer;

/// <summary>Which ends of the selected segment its own thumbnails are pinned to.</summary>
enum AnchorEnds { Both, In, Out, Halves }

/// <summary>
/// The selected segment's own thumbnails. <see cref="AnchorEnds.Both"/>: whole tiles from the in point, the last one pinned
/// to the out point. <see cref="AnchorEnds.In"/> or <see cref="AnchorEnds.Out"/>: only that end is in view, so whole tiles
/// run from its handle to the edge of the window, <see cref="Step"/> frames apart. <see cref="AnchorEnds.Halves"/>: the
/// segment is narrower than about a tile, so the left half of the in frame and the right half of the out frame share it.
/// </summary>
readonly record struct AnchorPlan(long In, long Out, AnchorEnds Ends, int Count, double Step)
{
    /// <summary>The frame tile <paramref name="index"/> shows. Tiles count from the pinned end.</summary>
    public long FrameAt(int index) => Ends switch
    {
        AnchorEnds.Both => In + (long)Math.Round((Out - 1 - In) * (index / (double)Math.Max(1, Count - 1))),
        AnchorEnds.In => Math.Min(Out - 1, In + (long)Math.Round(index * Step)),
        AnchorEnds.Out => Math.Max(In, Out - 1 - (long)Math.Round(index * Step)),
        _ => index == 0 ? In : Out - 1,
    };
}

/// <summary>
/// The filmstrip with its kept segments and an audio waveform lane, zoomable around any point.
/// One segment is active: its in and out handles are the ones the user edits. The others are drawn as kept blocks.
/// When zoomed in, an overview bar across the top shows the whole clip and the visible window; drag it to pan.
/// </summary>
sealed class TimelineControl : Control
{
    enum DragKind { None, Playhead, InHandle, OutHandle, Span, Overview }
    enum HitKind { None, InHandle, OutHandle, Selection, Film, Overview }

    // Brushes and pens in the theme's colors, rebuilt when it changes (see Repaint). The rest draw on the video and keep one look.
    static SolidBrush FilmBrush = null!;
    static SolidBrush LaneBrush = null!;
    static SolidBrush AccentBrush = null!;
    static SolidBrush GripBrush = null!;
    static SolidBrush WaveBrush = null!;
    static Pen LaneLinePen = null!;
    static Pen SelectionPen = null!;
    static Pen PickedPen = null!;
    // Cut regions are dimmed and hatched in neutral tones: red is kept for caution, not for "removed".
    static readonly SolidBrush DimBrush = new(Color.FromArgb(196, 8, 9, 12));
    static readonly HatchBrush CutHatch = new(HatchStyle.WideDownwardDiagonal, Color.FromArgb(26, 255, 255, 255), Color.Transparent);
    static readonly SolidBrush TagBackgroundBrush = new(Color.FromArgb(214, 13, 15, 19));
    static readonly SolidBrush PlayheadBrush = new(Color.White);
    static readonly SolidBrush OverviewBrush = new(Color.FromArgb(90, 0, 0, 0));
    static readonly SolidBrush ViewportBrush = new(Color.FromArgb(46, 255, 255, 255));
    static readonly SolidBrush ViewportGripBrush = new(Color.FromArgb(216, 255, 255, 255));
    static readonly Pen ViewportPen = new(Color.FromArgb(204, 255, 255, 255), 1);
    static readonly Pen KeptPen = new(Color.FromArgb(96, 255, 255, 255), 1);
    // Text on the dark tags over the thumbnails, which look the same in either theme.
    static readonly Color OnVideoText = Color.FromArgb(242, 244, 248);
    static readonly Pen PlayheadPen = new(Color.White, 2);

    static TimelineControl()
    {
        Repaint();
        Theme.Changed += Repaint;
    }

    static void Repaint()
    {
        IDisposable?[] old = [FilmBrush, LaneBrush, AccentBrush, GripBrush, WaveBrush, LaneLinePen, SelectionPen, PickedPen];
        FilmBrush = new SolidBrush(Theme.Well);
        LaneBrush = new SolidBrush(Theme.IsDark ? Theme.Darken(Theme.Well, 4) : Theme.Darken(Theme.Well, 8));
        AccentBrush = new SolidBrush(Theme.Accent);
        GripBrush = new SolidBrush(Theme.OnAccent);
        WaveBrush = new SolidBrush(Theme.Muted);
        LaneLinePen = new Pen(Theme.WellBorder);
        SelectionPen = new Pen(Theme.Accent, 2);
        PickedPen = new Pen(Theme.Accent, 2);
        foreach (var item in old) item?.Dispose();
    }

    Bitmap?[]? strip;
    long stripStart;
    long stripEnd;
    // The selected segment's own thumbnails, laid out as anchorPlan says.
    Bitmap?[]? anchor;
    AnchorPlan? anchorPlan;
    double duration;
    double fps;
    long totalFrames;
    long inFrame;
    long outFrame;
    long playhead;
    readonly List<Segment> others = new();
    // Segments picked for export alongside the active one (a multi-selection). Always a subset of others.
    readonly HashSet<Segment> picked = new();
    DragKind drag = DragKind.None;
    Point dragStart;
    long spanAnchorIn;
    long spanAnchorOut;
    long spanAnchorFrame;
    double panAnchorStart;
    bool dragMoved;

    // The visible window, in frames. A fractional start keeps zooming around the pointer smooth.
    double viewStart;
    double viewSpan = 1;

    float[]? envelope;
    float[]? waveColumns;
    (int Width, double Start, double Span) waveKey;

    public TimelineControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Base;
        TabStop = true;
        AccessibleName = "Timeline";
        AccessibleRole = AccessibleRole.Pane;
        Cursor = Cursors.Hand;
    }

    public bool HasMedia => totalFrames > 0 && fps > 0 && duration > 0;
    public bool IsDragging => drag != DragKind.None;
    public bool Locked { get; set; }
    public string Placeholder { get; set; } = "";
    /// <summary>False hides the waveform lane, giving its height to the filmstrip.</summary>
    public bool ShowLane { get; set; } = true;

    /// <summary>Text shown in the waveform lane while the audio is read, or why there is no waveform.</summary>
    public string LaneNote { get; set; } = "";
    public double Fps => fps;
    public long TotalFrames => totalFrames;
    public long InFrame => inFrame;
    public long OutFrame => outFrame;
    public long PlayheadFrame => playhead;

    /// <summary>How many times the clip is magnified: 1 shows all of it.</summary>
    public double Zoom => totalFrames <= 0 ? 1 : totalFrames / viewSpan;
    public bool IsZoomed => viewSpan < totalFrames - 0.5;
    public long VisibleStart => (long)Math.Floor(viewStart);
    public long VisibleEnd => Math.Min(totalFrames, (long)Math.Ceiling(viewStart + viewSpan));

    Segment Active => new(inFrame, outFrame);

    /// <summary>Every segment in time order, including the active one. Touching segments stay separate here.</summary>
    public IReadOnlyList<Segment> AllSegments => others.Append(Active).OrderBy(s => s.In).ToList();

    public int ActiveIndex => others.Count(s => s.In < inFrame);

    /// <summary>Indices in <see cref="AllSegments"/> picked alongside the active one; empty for a single selection.</summary>
    public IReadOnlySet<int> PickedIndices
    {
        get
        {
            var all = AllSegments;
            return Enumerable.Range(0, all.Count).Where(i => picked.Contains(all[i])).ToHashSet();
        }
    }

    /// <summary>The multi-selection in time order, active segment included, or empty when only one is selected.</summary>
    public IReadOnlyList<Segment> PickedSegments => picked.Count == 0 ? [] : picked.Append(Active).OrderBy(s => s.In).ToList();

    /// <summary>Ctrl+click: adds a segment to the selection or takes it out.</summary>
    public void TogglePick(int index)
    {
        if (!HasMedia || Locked) return;
        var all = AllSegments;
        if (index < 0 || index >= all.Count) return;
        if (index != ActiveIndex)
        {
            if (!picked.Remove(all[index])) picked.Add(all[index]);
        }
        else if (picked.Count > 0)
        {
            // Taking the active segment out hands editing to the earliest one still picked.
            var next = picked.OrderBy(s => s.In).First();
            picked.Remove(next);
            others.Remove(next);
            others.Add(Active);
            (inFrame, outFrame) = (next.In, next.Out);
            playhead = inFrame;
        }

        NotifyValues();
    }

    /// <summary>Shift+click: picks every segment from the active one to <paramref name="index"/>.</summary>
    public void PickRange(int index)
    {
        if (!HasMedia || Locked) return;
        var all = AllSegments;
        if (index < 0 || index >= all.Count) return;
        var active = ActiveIndex;
        picked.Clear();
        for (var i = Math.Min(active, index); i <= Math.Max(active, index); i++)
            if (i != active) picked.Add(all[i]);
        NotifyValues();
    }

    public void PickAll()
    {
        if (!HasMedia || Locked) return;
        picked.Clear();
        picked.UnionWith(others);
        NotifyValues();
    }

    public bool ClearPicks()
    {
        if (picked.Count == 0) return false;
        picked.Clear();
        NotifyValues();
        return true;
    }

    /// <summary>What the export keeps: sorted, with touching segments joined.</summary>
    public IReadOnlyList<Segment> ExportSegments => CutList.Normalize(others.Append(Active), totalFrames);

    public int FilmWidth => Math.Max(1, FilmRect().Width);

    /// <summary>The video's displayed width over height. Thumbnails are drawn at this shape, never cropped top and bottom.</summary>
    public double Aspect { get; set; } = 16 / 9d;

    /// <summary>How wide one uncropped thumbnail is at the filmstrip's height.</summary>
    public double TileWidth => Math.Max(8, FilmRect().Height * (Aspect > 0 ? Aspect : 16 / 9d));

    /// <summary>
    /// The thumbnails the selected segment wants pinned to its handles, or null when it should just show the regular strip:
    /// it is the whole clip, neither end is in view, or it is only a few pixels wide. When one end is off the window the
    /// visible end is anchored; the request then changes with the zoom (through <see cref="AnchorPlan.Step"/>) and with
    /// how many tiles fit, but not with a pan that keeps both.
    /// </summary>
    public AnchorPlan? AnchorRequest
    {
        get
        {
            if (!HasMedia || (inFrame <= 0 && outFrame >= totalFrames)) return null;
            var film = FilmRect();
            var tile = TileWidth;
            var viewEnd = viewStart + viewSpan;
            var inShown = inFrame >= viewStart - 0.5 && inFrame < viewEnd;
            var outShown = outFrame <= viewEnd + 0.5 && outFrame > viewStart;
            if (inShown && outShown)
            {
                var span = XOf(outFrame) - XOf(inFrame);
                if (span >= tile * 1.15) return new AnchorPlan(inFrame, outFrame, AnchorEnds.Both, Math.Clamp((int)Math.Ceiling(span / tile), 2, 64), 0);
                return span >= LogicalToDeviceUnits(16) ? new AnchorPlan(inFrame, outFrame, AnchorEnds.Halves, 2, 0) : null;
            }

            if (inShown == outShown) return null;
            var room = inShown ? film.Right - XOf(inFrame) : XOf(outFrame) - film.Left;
            if (room < LogicalToDeviceUnits(4)) return null;
            var step = tile / film.Width * viewSpan;
            return new AnchorPlan(inFrame, outFrame, inShown ? AnchorEnds.In : AnchorEnds.Out, Math.Clamp((int)Math.Ceiling(room / tile), 1, 64), step);
        }
    }

    /// <summary>Shows <paramref name="images"/>, the thumbnails <paramref name="plan"/> asked for, in its order.</summary>
    public void UseAnchor(Bitmap?[]? images, AnchorPlan? plan)
    {
        anchor = images;
        anchorPlan = plan;
        Invalidate();
    }

    public int ThumbHeight
    {
        get
        {
            var height = Math.Max(48, FilmRect().Height);
            if (height % 2 == 1) height--;
            return height;
        }
    }

    public event EventHandler? ValuesChanged;
    public event EventHandler? InteractionEnded;
    /// <summary>The visible window moved or changed size.</summary>
    public event EventHandler? ViewChanged;

    public double SeekTime(long frame)
    {
        var seconds = Seconds(frame);
        if (duration <= 0) return 0;
        return Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001));
    }

    public void SetClip(double durationSeconds, double framesPerSecond, long frames)
    {
        duration = durationSeconds;
        fps = framesPerSecond;
        totalFrames = Math.Max(1, frames);
        inFrame = 0;
        outFrame = totalFrames;
        playhead = 0;
        others.Clear();
        picked.Clear();
        strip = null;
        anchor = null;
        drag = DragKind.None;
        viewStart = 0;
        viewSpan = totalFrames;
        envelope = null;
        waveColumns = null;
        LaneNote = "";
        NotifyValues();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows thumbnails that cover frames <paramref name="start"/> to <paramref name="end"/>, spread evenly.</summary>
    public void UseStrip(Bitmap?[]? images, long start, long end)
    {
        strip = images;
        stripStart = start;
        stripEnd = end;
        Invalidate();
    }

    public void SetEnvelope(float[]? levels)
    {
        envelope = levels;
        waveColumns = null;
        Invalidate();
    }

    public void SetInFrame(long frame, bool followPlayhead)
    {
        if (!HasMedia || Locked) return;
        (inFrame, outFrame) = TimeMath.WithIn(frame, outFrame, totalFrames);
        if (followPlayhead) playhead = inFrame;
        NotifyValues();
    }

    public void SetOutFrame(long frame, bool followPlayhead)
    {
        if (!HasMedia || Locked) return;
        (inFrame, outFrame) = TimeMath.WithOut(inFrame, frame, totalFrames);
        if (followPlayhead) playhead = Math.Max(inFrame, outFrame - 1);
        NotifyValues();
    }

    public void NudgeIn(int delta)
    {
        if (!HasMedia || Locked) return;
        inFrame = TimeMath.StickIn(inFrame + delta, outFrame, totalFrames);
        playhead = inFrame;
        NotifyValues();
    }

    public void NudgeOut(int delta)
    {
        if (!HasMedia || Locked) return;
        outFrame = TimeMath.StickOut(outFrame + delta, inFrame, totalFrames);
        playhead = Math.Max(inFrame, outFrame - 1);
        NotifyValues();
    }

    public void NudgePlayhead(int delta)
    {
        if (!HasMedia || Locked) return;
        playhead = Math.Clamp(playhead + delta, 0, totalFrames - 1);
        NotifyValues();
    }

    public void NudgeNearestEdge(int delta)
    {
        if (!HasMedia || Locked) return;
        if (Math.Abs(playhead - inFrame) <= Math.Abs(playhead - outFrame))
            NudgeIn(delta);
        else
            NudgeOut(delta);
    }

    public void SetInToPlayhead()
    {
        if (!HasMedia || Locked) return;
        (inFrame, outFrame) = TimeMath.WithIn(playhead, outFrame, totalFrames);
        playhead = inFrame;
        NotifyValues();
    }

    public void SetOutToPlayhead()
    {
        if (!HasMedia || Locked) return;
        var end = Math.Clamp(playhead + 1, 1, totalFrames);
        (inFrame, outFrame) = TimeMath.WithOut(inFrame, end, totalFrames);
        NotifyValues();
    }

    public void JumpPlayheadToIn()
    {
        if (!HasMedia || Locked) return;
        playhead = inFrame;
        NotifyValues();
    }

    public void JumpPlayheadToOut()
    {
        if (!HasMedia || Locked) return;
        playhead = Math.Max(inFrame, outFrame - 1);
        NotifyValues();
    }

    public void SetPlayhead(long frame)
    {
        if (!HasMedia || Locked) return;
        playhead = Math.Clamp(frame, 0, totalFrames - 1);
        NotifyValues();
    }

    /// <summary>Moves the playhead for playback: keeps it in view but does not raise <see cref="ValuesChanged"/>.</summary>
    public void ShowPlaybackFrame(long frame)
    {
        if (!HasMedia) return;
        playhead = Math.Clamp(frame, 0, totalFrames - 1);
        if (IsZoomed && (playhead < viewStart || playhead >= viewStart + viewSpan))
            ScrollTo(playhead - viewSpan * 0.1);
        Invalidate();
    }

    /// <summary>The segment edges after (or before) <paramref name="frame"/>: where the export joins or ends a piece.</summary>
    public long? CutAfter(long frame, int direction, bool startsOnly)
    {
        var edges = new SortedSet<long>();
        foreach (var segment in ExportSegments)
        {
            edges.Add(segment.In);
            if (!startsOnly) edges.Add(Math.Min(segment.Out, totalFrames - 1));
        }

        return direction > 0
            ? edges.Where(e => e > frame).Cast<long?>().FirstOrDefault()
            : edges.Where(e => e < frame).Cast<long?>().LastOrDefault();
    }

    /// <summary>Splits the segment under the playhead in two. The right half becomes active.</summary>
    public bool SplitAtPlayhead()
    {
        if (!HasMedia || Locked) return false;
        picked.Clear();
        if (playhead > inFrame && playhead < outFrame)
        {
            others.Add(new Segment(inFrame, playhead));
            inFrame = playhead;
            NotifyValues();
            return true;
        }

        var index = others.FindIndex(s => playhead > s.In && playhead < s.Out);
        if (index < 0) return false;
        var target = others[index];
        others.RemoveAt(index);
        others.Add(Active);
        others.Add(new Segment(target.In, playhead));
        (inFrame, outFrame) = (playhead, target.Out);
        NotifyValues();
        return true;
    }

    /// <summary>Starts a new active segment at the playhead that runs to the next kept segment or the end.</summary>
    public bool NewSegmentAtPlayhead()
    {
        if (!HasMedia || Locked) return false;
        picked.Clear();
        var gaps = CutList.Gaps(CutList.Normalize(others.Append(Active), totalFrames), totalFrames);
        if (gaps.Count == 0) return false;
        var gap = gaps.FirstOrDefault(g => g.Contains(playhead));
        if (gap.Length == 0) gap = gaps.FirstOrDefault(g => g.In > playhead);
        if (gap.Length == 0) gap = gaps[^1];
        var start = gap.Contains(playhead) ? playhead : gap.In;
        others.Add(Active);
        (inFrame, outFrame) = (start, gap.Out);
        playhead = start;
        NotifyValues();
        return true;
    }

    /// <summary>Makes a segment the one being edited. Unless <paramref name="keepPicks"/>, the multi-selection is cleared.</summary>
    public bool SelectSegment(int index, bool keepPicks = false)
    {
        if (!HasMedia || Locked) return false;
        var all = AllSegments;
        if (index < 0 || index >= all.Count) return false;
        if (!keepPicks) picked.Clear();
        if (index != ActiveIndex)
        {
            var target = all[index];
            // The segment that stops being edited stays in the selection it belonged to.
            if (picked.Count > 0) picked.Add(Active);
            others.Remove(target);
            others.Add(Active);
            (inFrame, outFrame) = (target.In, target.Out);
        }

        playhead = inFrame;
        if (IsZoomed) EnsureVisible(inFrame, outFrame);
        NotifyValues();
        return true;
    }

    void ActivateSegmentAt(long frame)
    {
        var index = others.FindIndex(s => s.Contains(frame));
        if (index < 0) return;
        picked.Clear();
        var target = others[index];
        others.RemoveAt(index);
        others.Add(Active);
        (inFrame, outFrame) = (target.In, target.Out);
    }

    /// <summary>Removes a segment. The last segment cannot be removed.</summary>
    public bool RemoveSegment(int index)
    {
        if (!HasMedia || Locked || others.Count == 0) return false;
        var all = AllSegments;
        if (index < 0 || index >= all.Count) return false;
        if (index != ActiveIndex)
        {
            others.Remove(all[index]);
            NotifyValues();
            return true;
        }

        // The active one goes: the next segment (or the previous one at the end) takes over.
        var replacement = index + 1 < all.Count ? all[index + 1] : all[index - 1];
        others.Remove(replacement);
        (inFrame, outFrame) = (replacement.In, replacement.Out);
        playhead = inFrame;
        NotifyValues();
        return true;
    }

    /// <summary>Keeps only the whole clip as one segment.</summary>
    public void ResetSegments()
    {
        if (!HasMedia || Locked) return;
        others.Clear();
        picked.Clear();
        (inFrame, outFrame) = (0, totalFrames);
        NotifyValues();
    }

    public int FramesInOneSecond() => TimeMath.RoundedFps(fps);

    /// <summary>Magnifies by <paramref name="factor"/>, keeping <paramref name="anchor"/> where it is on screen.</summary>
    public void ZoomBy(double factor, double? anchor = null)
    {
        if (!HasMedia) return;
        var center = anchor ?? playhead;
        var ratio = viewSpan <= 0 ? 0.5 : Math.Clamp((center - viewStart) / viewSpan, 0, 1);
        var minimum = Math.Max(10, fps * 2);
        var span = Math.Clamp(viewSpan / factor, Math.Min(minimum, totalFrames), totalFrames);
        if (Math.Abs(span - viewSpan) < 0.01) return;
        viewSpan = span;
        ScrollTo(center - ratio * span, force: true);
    }

    public void ZoomToFit()
    {
        if (!HasMedia) return;
        viewSpan = totalFrames;
        ScrollTo(0, force: true);
    }

    void ScrollTo(double start, bool force = false)
    {
        var clamped = Math.Clamp(start, 0, Math.Max(0, totalFrames - viewSpan));
        var changed = force || Math.Abs(clamped - viewStart) > 0.001;
        viewStart = clamped;
        Invalidate();
        if (changed) ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    void EnsureVisible(long from, long to)
    {
        if (from >= viewStart && to <= viewStart + viewSpan) return;
        if (to - from > viewSpan) ScrollTo(from - viewSpan * 0.05);
        else ScrollTo((from + to) / 2.0 - viewSpan / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Backdrop.Paint(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintUtil.Panel(g, ClientRectangle, LogicalToDeviceUnits(14));
        // Bilinear is enough for filmstrip thumbs and much cheaper than HighQualityBicubic.
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var film = FilmRect();
        var lane = LaneRect();
        var track = TrackRect();
        g.FillRectangle(FilmBrush, film);

        if (!HasMedia)
        {
            if (!string.IsNullOrWhiteSpace(Placeholder))
                TextRenderer.DrawText(g, Placeholder, Theme.Ui(9.5f), film, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        if (IsZoomed) DrawOverview(g, OverviewRect());
        DrawRuler(g, track);

        var state = g.Save();
        g.SetClip(track);
        DrawStrip(g, film);
        DrawAnchor(g, film);
        DrawLane(g, lane);

        // Everything outside the kept segments is cut: dim it and hatch it.
        foreach (var gap in CutList.Gaps(CutList.Normalize(others.Append(Active), totalFrames), totalFrames))
        {
            var left = XOf(gap.In);
            var right = XOf(gap.Out);
            if (right <= track.Left || left >= track.Right || right <= left) continue;
            g.FillRectangle(DimBrush, left, track.Top, right - left, track.Height);
            g.FillRectangle(CutHatch, left, track.Top, right - left, track.Height);
        }

        var all = AllSegments;
        var numbered = all.Count > 1;
        var active = ActiveIndex;
        for (var i = 0; i < all.Count; i++)
        {
            if (i == active) continue;
            var x1 = XOf(all[i].In);
            var x2 = XOf(all[i].Out);
            g.DrawRectangle(picked.Contains(all[i]) ? PickedPen : KeptPen, x1, track.Top + 1, Math.Max(1, x2 - x1), track.Height - 3);
            if (numbered) DrawBadge(g, (i + 1).ToString(), x1, x2, film, selected: false);
        }

        var inX = XOf(inFrame);
        var outX = XOf(outFrame);
        if (outX > inX)
            g.DrawRectangle(SelectionPen, inX, track.Top + 1, outX - inX, track.Height - 2);
        if (numbered) DrawBadge(g, (active + 1).ToString(), inX, outX, film, selected: true);
        DrawRangeTags(g, film, inX, outX);
        DrawLaneNote(g, lane);

        // The handles sit just outside the selection, so the frame and its two ends read as one object.
        g.SetClip(Rectangle.Inflate(track, HandleWidth, 0));
        DrawHandle(g, inX, track, isIn: true);
        DrawHandle(g, outX, track, isIn: false);
        DrawPlayhead(g, XOf(Math.Clamp(playhead, 0, totalFrames)), track);
        g.Restore(state);
        if (Focused && ShowFocusCues)
            PaintUtil.FocusRing(g, ClientRectangle, LogicalToDeviceUnits(14));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!HasMedia || e.Button != MouseButtons.Left) return;
        Focus();
        var hit = HitTest(e.Location);
        dragMoved = false;
        dragStart = e.Location;
        if (hit == HitKind.Overview)
        {
            // A click beside the thumb centers it there; either way, dragging then pans.
            var (thumb, travel) = OverviewThumb(OverviewRect());
            if (!thumb.Contains(e.Location) && travel > 0)
                ScrollTo((e.X - OverviewRect().Left - thumb.Width / 2) / travel * (totalFrames - viewSpan));
            panAnchorStart = viewStart;
            drag = DragKind.Overview;
            Capture = true;
            return;
        }

        if (Locked) return;
        var frameAt = FrameAt(e.X);
        var shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        var alt = (ModifierKeys & Keys.Alt) == Keys.Alt;
        if (!shift && !alt && hit == HitKind.InHandle)
        {
            drag = DragKind.InHandle;
            Capture = true;
            return;
        }

        if (!shift && !alt && hit == HitKind.OutHandle)
        {
            drag = DragKind.OutHandle;
            Capture = true;
            return;
        }

        if (!shift && !alt && hit == HitKind.Selection)
        {
            drag = DragKind.Span;
            spanAnchorIn = inFrame;
            spanAnchorOut = outFrame;
            spanAnchorFrame = frameAt;
            Capture = true;
            return;
        }

        // Clicking inside another kept segment makes it the one the handles edit; the playhead goes where you clicked.
        if (!shift && !alt) ActivateSegmentAt(frameAt);
        ApplyPointer(frameAt, shift, alt);
        drag = DragKind.Playhead;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasMedia) return;
        if (drag == DragKind.None)
        {
            Cursor = CursorFor(HitTest(e.Location));
            return;
        }

        if (drag == DragKind.Overview)
        {
            // The thumb has a minimum width, so its travel (not the bar's width) maps onto the scrollable range.
            var travel = OverviewThumb(OverviewRect()).Travel;
            if (travel > 0) ScrollTo(panAnchorStart + (e.X - dragStart.X) / travel * (totalFrames - viewSpan));
            return;
        }

        if (drag == DragKind.Span && !dragMoved && Math.Abs(e.X - dragStart.X) < LogicalToDeviceUnits(4))
            return;

        dragMoved = true;
        AutoScroll(e.X);
        var frame = FrameAt(e.X);
        switch (drag)
        {
            case DragKind.InHandle:
                inFrame = TimeMath.StickIn(frame, outFrame, totalFrames);
                playhead = inFrame;
                NotifyValues();
                break;
            case DragKind.OutHandle:
                outFrame = TimeMath.StickOut(frame, inFrame, totalFrames);
                playhead = Math.Max(inFrame, outFrame - 1);
                NotifyValues();
                break;
            case DragKind.Span:
                (inFrame, outFrame) = TimeMath.Slide(spanAnchorIn, spanAnchorOut, frame - spanAnchorFrame, totalFrames);
                playhead = inFrame;
                NotifyValues();
                break;
            case DragKind.Playhead:
                playhead = Math.Clamp(frame, 0, totalFrames - 1);
                NotifyValues();
                break;
        }
    }

    /// <summary>Dragging past either edge of a zoomed view pulls more of the clip into view.</summary>
    void AutoScroll(int x)
    {
        if (!IsZoomed) return;
        var film = FilmRect();
        var edge = LogicalToDeviceUnits(16);
        if (x < film.Left + edge) ScrollTo(viewStart - viewSpan * 0.03);
        else if (x > film.Right - edge) ScrollTo(viewStart + viewSpan * 0.03);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (drag == DragKind.None) return;
        if (drag == DragKind.Span && !dragMoved)
        {
            playhead = Math.Clamp(FrameAt(dragStart.X), 0, totalFrames - 1);
            NotifyValues();
        }

        FinishDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && drag != DragKind.None)
            FinishDrag();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!HasMedia) return;
        if ((ModifierKeys & Keys.Control) == Keys.Control)
        {
            // Ctrl+wheel zooms around the frame under the pointer.
            ZoomBy(e.Delta > 0 ? 1.5 : 1 / 1.5, FrameAtExact(e.X));
            return;
        }

        if (Locked) return;
        var step = (ModifierKeys & Keys.Shift) == Keys.Shift ? FramesInOneSecond() : 1;
        NudgePlayhead(e.Delta > 0 ? -step : step);
        InteractionEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (m.Msg == WM_MOUSEHWHEEL && HasMedia && IsZoomed)
        {
            // A tilt wheel or a touchpad's sideways swipe pans the zoomed view.
            var delta = (short)((long)m.WParam >> 16 & 0xFFFF);
            ScrollTo(viewStart + delta / 120.0 * viewSpan * 0.1);
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (drag == DragKind.None) Cursor = Cursors.Hand;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        waveColumns = null;
    }

    void FinishDrag()
    {
        if (drag == DragKind.None) return;
        var wasPan = drag == DragKind.Overview;
        drag = DragKind.None;
        if (Capture) Capture = false;
        Cursor = Cursors.Hand;
        if (wasPan) return;
        if (AbsorbOverlaps()) NotifyValues();
        InteractionEnded?.Invoke(this, EventArgs.Empty);
    }

    void ApplyPointer(long frame, bool shift, bool alt)
    {
        playhead = Math.Clamp(frame, 0, Math.Max(0, totalFrames - 1));
        if (shift) SetInToPlayhead();
        else if (alt) SetOutToPlayhead();
        else NotifyValues();
    }

    void NotifyValues()
    {
        // Merge on settle rather than mid-drag, so dragging across a neighbor does not swallow it until release.
        if (drag == DragKind.None) AbsorbOverlaps();
        // Removed, merged or now-active segments drop out of the multi-selection.
        picked.IntersectWith(others);
        var description = HasMedia
            ? $"Segment {ActiveIndex + 1} of {others.Count + 1}: in {TimeMath.Format(inFrame, fps)}, out {TimeMath.Format(outFrame, fps)}, playhead {TimeMath.Format(playhead, fps)}"
            : "No video";
        AccessibleDescription = description;
        Invalidate();
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Joins any kept segment the active one now overlaps into the active one.</summary>
    bool AbsorbOverlaps()
    {
        var changed = false;
        while (true)
        {
            var index = others.FindIndex(s => s.Overlaps(Active));
            if (index < 0) return changed;
            var other = others[index];
            others.RemoveAt(index);
            inFrame = Math.Min(inFrame, other.In);
            outFrame = Math.Max(outFrame, other.Out);
            changed = true;
        }
    }

    double Seconds(long frame) => fps <= 0 ? 0 : frame / fps;

    /// <summary>The thumbnails, plus the waveform lane beneath them once a clip is loaded.</summary>
    Rectangle TrackRect()
    {
        // Inside the glass panel, with room at the sides for the handles and below for the time ruler.
        var pad = LogicalToDeviceUnits(10);
        var side = pad + HandleWidth;
        var rect = new Rectangle(side, pad, ClientSize.Width - side * 2, ClientSize.Height - pad * 2);
        if (HasMedia) rect.Height -= RulerHeight();
        if (IsZoomed && HasMedia)
        {
            var overview = OverviewHeight() + LogicalToDeviceUnits(8);
            rect.Y += overview;
            rect.Height -= overview;
        }

        if (rect.Width < 1) rect.Width = 1;
        if (rect.Height < 1) rect.Height = 1;
        return rect;
    }

    int HandleWidth => Math.Max(10, LogicalToDeviceUnits(12));

    int RulerHeight() => LogicalToDeviceUnits(18);

    int OverviewHeight() => LogicalToDeviceUnits(12);

    Rectangle OverviewRect()
    {
        var pad = LogicalToDeviceUnits(10);
        var side = pad + HandleWidth;
        return new Rectangle(side, pad + LogicalToDeviceUnits(2), Math.Max(1, ClientSize.Width - side * 2), OverviewHeight());
    }

    /// <summary>
    /// The draggable thumb on the overview bar. It never gets narrower than 28 pixels, however far the view is zoomed
    /// into a long video, so <c>Travel</c> (the distance it can move) is what maps onto the scrollable range.
    /// </summary>
    (RectangleF Thumb, float Travel) OverviewThumb(Rectangle overview)
    {
        var width = Math.Min(overview.Width, Math.Max(LogicalToDeviceUnits(28), (float)(viewSpan / totalFrames * overview.Width)));
        var travel = overview.Width - width;
        var range = totalFrames - viewSpan;
        var x = overview.Left + (range <= 0 ? 0 : (float)(viewStart / range * travel));
        var grow = LogicalToDeviceUnits(2);
        return (new RectangleF(x, overview.Top - grow, width, overview.Height + grow * 2), travel);
    }

    int LaneHeight()
    {
        if (!HasMedia || !ShowLane) return 0;
        var track = TrackRect();
        return Math.Clamp(track.Height / 4, LogicalToDeviceUnits(18), LogicalToDeviceUnits(36));
    }

    Rectangle FilmRect()
    {
        var track = TrackRect();
        track.Height = Math.Max(1, track.Height - LaneHeight());
        return track;
    }

    Rectangle LaneRect()
    {
        var track = TrackRect();
        var height = LaneHeight();
        return new Rectangle(track.Left, track.Bottom - height, track.Width, height);
    }

    double FrameAtExact(int x)
    {
        var film = FilmRect();
        if (film.Width <= 0 || totalFrames <= 0) return 0;
        return viewStart + (x - film.Left) / (double)film.Width * viewSpan;
    }

    long FrameAt(int x) => (long)Math.Round(Math.Clamp(FrameAtExact(x), 0, totalFrames), MidpointRounding.AwayFromZero);

    /// <summary>The x of a frame in the current view. Frames outside it land beyond the film's edges.</summary>
    int XOf(long frame) => XOf((double)frame);

    int XOf(double frame)
    {
        var film = FilmRect();
        if (totalFrames <= 0 || viewSpan <= 0) return film.Left;
        var x = film.Left + (frame - viewStart) / viewSpan * film.Width;
        return (int)Math.Round(Math.Clamp(x, -100_000, 100_000), MidpointRounding.AwayFromZero);
    }

    HitKind HitTest(Point point)
    {
        // A few extra pixels above and below make the thin bar easy to hit.
        if (IsZoomed && Rectangle.Inflate(OverviewRect(), 0, LogicalToDeviceUnits(5)).Contains(point)) return HitKind.Overview;
        var x = point.X;
        var film = FilmRect();
        if (x < film.Left || x > film.Right) return HitKind.None;
        var slop = Math.Max(8, LogicalToDeviceUnits(10));
        var inX = XOf(inFrame);
        var outX = XOf(outFrame);
        var onIn = Math.Abs(x - inX) <= slop;
        var onOut = Math.Abs(x - outX) <= slop;
        if (onIn && onOut) return x <= (inX + outX) / 2 ? HitKind.InHandle : HitKind.OutHandle;
        if (onIn) return HitKind.InHandle;
        if (onOut) return HitKind.OutHandle;
        var frame = FrameAt(x);
        if (frame >= inFrame && frame < outFrame) return HitKind.Selection;
        return HitKind.Film;
    }

    static Cursor CursorFor(HitKind hit) => hit switch
    {
        HitKind.InHandle or HitKind.OutHandle => Cursors.SizeWE,
        HitKind.Selection => Cursors.SizeAll,
        HitKind.Overview => Cursors.SizeWE,
        _ => Cursors.Hand,
    };

    void DrawStrip(Graphics graphics, Rectangle film)
    {
        if (strip is not { Length: > 0 } || stripEnd <= stripStart)
        {
            if (!string.IsNullOrWhiteSpace(Placeholder))
                TextRenderer.DrawText(graphics, Placeholder, Theme.Ui(9.5f), film, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        // Each slot covers an equal share of the strip's frames; while a zoomed strip loads, the old one stretches.
        var per = (stripEnd - stripStart) / (double)strip.Length;
        for (var i = 0; i < strip.Length; i++)
        {
            var image = strip[i];
            if (image is null) continue;
            var left = XOf(stripStart + per * i);
            var right = XOf(stripStart + per * (i + 1));
            if (right < film.Left || left > film.Right) continue;
            DrawCover(graphics, image, new RectangleF(left, film.Top, right - left + 1, film.Height));
        }
    }

    /// <summary>
    /// The selected segment's own thumbnails, over the regular strip. Skipped mid-drag and whenever they no longer
    /// match the segment or the zoom, so a stale set is never stretched; the regular strip shows through instead.
    /// </summary>
    void DrawAnchor(Graphics graphics, Rectangle film)
    {
        if (anchor is null || anchorPlan is not { } plan || anchor.Length != plan.Count) return;
        if (drag is DragKind.InHandle or DragKind.OutHandle or DragKind.Span || plan != AnchorRequest) return;
        var left = XOf(inFrame);
        var right = XOf(outFrame);
        var middle = (left + right) / 2;
        // Both ends: whole tiles run from the in point, and the last one is pinned to the out point instead, over the end
        // of the one before it. One end: whole tiles run from that handle into the window. Either way the pinned frames
        // are shown uncropped, right against their handles. Halves: each frame keeps its own half of the segment.
        var tile = (float)TileWidth;
        var state = graphics.Save();
        graphics.SetClip(new Rectangle(left, film.Top, right - left, film.Height), CombineMode.Intersect);
        for (var i = 0; i < anchor.Length; i++)
        {
            var image = anchor[i];
            if (image is null) continue;
            var x = plan.Ends switch
            {
                AnchorEnds.Both => i == anchor.Length - 1 ? right - tile : left + tile * i,
                AnchorEnds.In => left + tile * i,
                AnchorEnds.Out => right - tile * (i + 1),
                _ => i == 0 ? left : right - tile,
            };
            var slot = new RectangleF(x, film.Top, tile + 1, film.Height);
            if (slot.Right < film.Left || slot.Left > film.Right) continue;
            if (plan.Ends != AnchorEnds.Halves)
            {
                DrawCover(graphics, image, slot);
                continue;
            }

            var half = graphics.Save();
            graphics.SetClip(i == 0 ? Rectangle.FromLTRB(left, film.Top, middle, film.Bottom) : Rectangle.FromLTRB(middle, film.Top, right, film.Bottom), CombineMode.Intersect);
            DrawCover(graphics, image, slot);
            graphics.Restore(half);
        }

        graphics.Restore(state);
    }

    /// <summary>Timecodes under the filmstrip for the frames at evenly spaced points of the view.</summary>
    void DrawRuler(Graphics graphics, Rectangle track)
    {
        var font = Theme.Mono(8);
        var count = Math.Clamp(track.Width / LogicalToDeviceUnits(170), 2, 8);
        var top = track.Bottom + LogicalToDeviceUnits(4);
        var height = RulerHeight() - LogicalToDeviceUnits(4);
        for (var i = 0; i < count; i++)
        {
            var share = i / (double)(count - 1);
            var text = TimeMath.Format((long)Math.Round(viewStart + viewSpan * share), fps);
            var width = TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding).Width;
            var x = track.Left + (int)((track.Width - width) * share);
            TextRenderer.DrawText(graphics, text, font, new Rectangle(x, top, width + 2, height), Theme.Muted,
                TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    void DrawOverview(Graphics graphics, Rectangle overview)
    {
        var radius = overview.Height / 2f;
        using (var path = PaintUtil.Rounded((RectangleF)overview, radius))
            graphics.FillPath(OverviewBrush, path);
        double Scale(double frame) => overview.Left + frame / totalFrames * overview.Width;
        var bar = LogicalToDeviceUnits(4);
        foreach (var segment in ExportSegments)
        {
            var left = (float)Scale(segment.In);
            var kept = new RectangleF(left, overview.Top + (overview.Height - bar) / 2f, Math.Max(2, (float)Scale(segment.Out) - left), bar);
            using var path = PaintUtil.Rounded(kept, bar / 2f);
            graphics.FillPath(AccentBrush, path);
        }

        var x = (float)Scale(playhead);
        graphics.FillRectangle(PlayheadBrush, x - 1, overview.Top, 2, overview.Height);

        var thumb = OverviewThumb(overview).Thumb;
        using (var path = PaintUtil.Rounded(new RectangleF(thumb.X + 0.5f, thumb.Y + 0.5f, thumb.Width - 1, thumb.Height - 1), LogicalToDeviceUnits(6)))
        {
            graphics.FillPath(ViewportBrush, path);
            graphics.DrawPath(ViewportPen, path);
        }

        // Three grip lines say "drag me"; skipped when the thumb is so wide they would float in the middle of nothing.
        var gripHeight = LogicalToDeviceUnits(6);
        var gap = LogicalToDeviceUnits(4);
        var middle = thumb.X + thumb.Width / 2f;
        for (var i = -1; i <= 1; i++)
            graphics.FillRectangle(ViewportGripBrush, middle + i * gap - 1, thumb.Y + (thumb.Height - gripHeight) / 2f, 2, gripHeight);
    }

    void DrawLane(Graphics graphics, Rectangle lane)
    {
        if (lane.Height <= 0) return;
        graphics.FillRectangle(LaneBrush, lane);
        graphics.DrawLine(LaneLinePen, lane.Left, lane.Top, lane.Right, lane.Top);
        var columns = WaveColumns(lane.Width);
        if (columns is null) return;
        var middle = lane.Top + lane.Height / 2f;
        var half = (lane.Height - LogicalToDeviceUnits(6)) / 2f;
        for (var i = 0; i < columns.Length; i++)
        {
            var height = Math.Max(1, columns[i] * half);
            graphics.FillRectangle(WaveBrush, lane.Left + i, middle - height, 1, height * 2);
        }
    }

    /// <summary>The audio read progress, drawn last so the cut overlay does not dim it.</summary>
    void DrawLaneNote(Graphics graphics, Rectangle lane)
    {
        if (lane.Height <= 0 || string.IsNullOrWhiteSpace(LaneNote)) return;
        var font = Theme.Ui(8.5f);
        var size = TextRenderer.MeasureText(LaneNote, font);
        var width = Math.Min(lane.Width - LogicalToDeviceUnits(16), size.Width + LogicalToDeviceUnits(12));
        var height = Math.Min(lane.Height - 2, size.Height + LogicalToDeviceUnits(2));
        var note = new Rectangle(lane.Right - width - LogicalToDeviceUnits(16), lane.Top + (lane.Height - height) / 2, width, height);
        graphics.FillRectangle(TagBackgroundBrush, note);
        TextRenderer.DrawText(graphics, LaneNote, font, note, OnVideoText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The loudest window under each pixel column of the current view, cached per width and view.</summary>
    float[]? WaveColumns(int width)
    {
        if (envelope is not { Length: > 0 } || width <= 0 || duration <= 0 || fps <= 0) return null;
        var key = (width, viewStart, viewSpan);
        if (waveColumns is not null && waveKey == key) return waveColumns;
        var columns = new float[width];
        var windowsPerFrame = 1 / (fps * AudioWaveform.WindowSeconds);
        for (var x = 0; x < width; x++)
        {
            var start = (int)((viewStart + x / (double)width * viewSpan) * windowsPerFrame);
            var end = Math.Max(start + 1, (int)((viewStart + (x + 1) / (double)width * viewSpan) * windowsPerFrame));
            var peak = 0f;
            for (var i = Math.Max(0, start); i < end && i < envelope.Length; i++)
                peak = Math.Max(peak, envelope[i]);
            columns[x] = AudioWaveform.DisplayLevel(peak);
        }

        waveColumns = columns;
        waveKey = key;
        return columns;
    }

    void DrawBadge(Graphics graphics, string text, int left, int right, Rectangle film, bool selected)
    {
        var font = Theme.Ui(8, FontStyle.Bold);
        var size = TextRenderer.MeasureText(text, font);
        var width = Math.Max(size.Width + LogicalToDeviceUnits(6), LogicalToDeviceUnits(18));
        var height = size.Height + LogicalToDeviceUnits(2);
        left = Math.Max(left, film.Left);
        right = Math.Min(right, film.Right);
        if (right - left < width + LogicalToDeviceUnits(8)) return;
        // Clear of a neighbouring selection's handle, which sits just outside that selection.
        var inset = selected ? LogicalToDeviceUnits(6) : HandleWidth + LogicalToDeviceUnits(4);
        var rect = new Rectangle(left + inset, film.Bottom - height - LogicalToDeviceUnits(6), width, height);
        using var path = PaintUtil.Rounded(rect, height / 2);
        using var brush = new SolidBrush(selected ? Theme.Accent : Color.FromArgb(214, 13, 15, 19));
        graphics.FillPath(brush, path);
        TextRenderer.DrawText(graphics, text, font, rect, selected ? Theme.OnAccent : OnVideoText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>
    /// Both ends of the selection share the accent: in is always the left end and out the right, so position and the
    /// rounded outer edge tell them apart without leaning on red and green.
    /// </summary>
    void DrawHandle(Graphics graphics, int x, Rectangle track, bool isIn)
    {
        var width = HandleWidth;
        var rect = new RectangleF(isIn ? x - width : x, track.Top, width, track.Height);
        using (var path = PaintUtil.RoundedSide(rect, LogicalToDeviceUnits(6), left: isIn))
            graphics.FillPath(AccentBrush, path);
        var grip = Math.Min(LogicalToDeviceUnits(22), track.Height - LogicalToDeviceUnits(12));
        if (grip > 4)
            graphics.FillRectangle(GripBrush, rect.X + width / 2f - 1, track.Top + (track.Height - grip) / 2f, 2, grip);
    }

    static void DrawPlayhead(Graphics graphics, int x, Rectangle track)
    {
        Point[] head = [new Point(x - 6, track.Top), new Point(x + 6, track.Top), new Point(x, track.Top + 9)];
        graphics.FillPolygon(PlayheadBrush, head);
        graphics.DrawLine(PlayheadPen, x, track.Top, x, track.Bottom);
    }

    void DrawRangeTags(Graphics graphics, Rectangle film, int inX, int outX)
    {
        var inText = "IN " + TimeMath.Format(inFrame, fps);
        var outText = "OUT " + TimeMath.Format(outFrame, fps);
        var inSize = TagSize(inText);
        var outSize = TagSize(outText);
        var pad = LogicalToDeviceUnits(8);
        var gap = LogicalToDeviceUnits(4);
        var top = film.Top + pad;

        // Keep each tag just inside its handle. If that collides, park them outside the selection.
        var inLeft = inX + pad;
        var outLeft = outX - pad - outSize.Width;
        if (TagsOverlap(inLeft, inSize.Width, outLeft, outSize.Width, gap))
        {
            inLeft = inX - pad - inSize.Width;
            outLeft = outX + pad;
        }

        inLeft = ClampTag(inLeft, inSize.Width, film);
        outLeft = ClampTag(outLeft, outSize.Width, film);
        if (!TagsOverlap(inLeft, inSize.Width, outLeft, outSize.Width, gap))
        {
            DrawTagAt(graphics, inText, inLeft, top, inSize, OnVideoText);
            DrawTagAt(graphics, outText, outLeft, top, outSize, OnVideoText);
            return;
        }

        // A one-frame selection against the film edge has no side room. Stack the tags.
        inLeft = ClampTag(Math.Min(inX, outX) + pad, inSize.Width, film);
        outLeft = ClampTag(inLeft, outSize.Width, film);
        var outTop = top + inSize.Height + gap;
        if (outTop + outSize.Height > film.Bottom - 4)
            outTop = Math.Max(film.Top + 2, film.Bottom - outSize.Height - 4);
        DrawTagAt(graphics, inText, inLeft, top, inSize, OnVideoText);
        DrawTagAt(graphics, outText, outLeft, outTop, outSize, OnVideoText);
    }

    Size TagSize(string text)
    {
        var size = TextRenderer.MeasureText(text, Theme.Ui(8, FontStyle.Bold));
        return new Size(size.Width + LogicalToDeviceUnits(8), size.Height + LogicalToDeviceUnits(2));
    }

    static bool TagsOverlap(int leftA, int widthA, int leftB, int widthB, int gap) =>
        leftA < leftB + widthB + gap && leftB < leftA + widthA + gap;

    static int ClampTag(int left, int width, Rectangle film)
    {
        var min = film.Left + 4;
        var max = Math.Max(min, film.Right - width - 4);
        return Math.Clamp(left, min, max);
    }

    static void DrawTagAt(Graphics graphics, string text, int left, int top, Size size, Color color)
    {
        var rect = new Rectangle(left, top, size.Width, size.Height);
        graphics.FillRectangle(TagBackgroundBrush, rect);
        TextRenderer.DrawText(graphics, text, Theme.Ui(8, FontStyle.Bold), rect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        using var timeline = new TimelineControl();
        timeline.SetClip(40, 30, 1200);
        foreach (var at in new long[] { 300, 600, 900 })
        {
            timeline.SetPlayhead(at);
            timeline.SplitAtPlayhead();
        }

        // Segments: 0-300, 300-600, 600-900, 900-1200; the last split leaves 900-1200 active.
        Expect(timeline.AllSegments.Count == 4 && timeline.ActiveIndex == 3, "three splits make four segments");
        timeline.TogglePick(1);
        Expect(timeline.PickedSegments.SequenceEqual([new Segment(300, 600), new Segment(900, 1200)]), "ctrl+click adds a segment to the selection");
        timeline.TogglePick(1);
        Expect(timeline.PickedSegments.Count == 0, "ctrl+click again takes it out");
        timeline.PickRange(1);
        Expect(timeline.PickedSegments.Count == 3 && timeline.PickedSegments[0] == new Segment(300, 600), "shift+click picks the range");
        timeline.SelectSegment(2, keepPicks: true);
        Expect(timeline.PickedSegments.Count == 3 && timeline.ActiveIndex == 2, "editing another picked segment keeps the selection");
        timeline.TogglePick(2);
        Expect(timeline.PickedSegments.SequenceEqual([new Segment(300, 600), new Segment(900, 1200)]) && timeline.ActiveIndex == 1, "unpicking the active segment hands editing to another picked one");
        timeline.RemoveSegment(3);
        Expect(timeline.PickedSegments.Count == 0, "removing a picked segment drops it from the selection");
        timeline.PickAll();
        Expect(timeline.PickedSegments.Count == 3, "select all");
        Expect(timeline.ClearPicks() && timeline.PickedSegments.Count == 0, "escape clears the selection");
        timeline.PickAll();
        timeline.SelectSegment(0);
        Expect(timeline.PickedSegments.Count == 0, "a plain click selects just one");

        // Anchored thumbnails, on a 1000-pixel timeline where one tile is wider than 240 frames of the whole clip.
        using var film = new TimelineControl { ShowLane = false, Size = new Size(1000, 180) };
        film.SetClip(40, 30, 1200);
        film.SetInFrame(300, followPlayhead: false);
        film.SetOutFrame(600, followPlayhead: false);
        Expect(film.AnchorRequest is { Ends: AnchorEnds.Halves, Count: 2 } halves && halves.FrameAt(0) == 300 && halves.FrameAt(1) == 599, "a narrow segment shows half of each end");
        film.SetOutFrame(900, followPlayhead: false);
        Expect(film.AnchorRequest is { Ends: AnchorEnds.Both } both && both.FrameAt(0) == 300 && both.FrameAt(both.Count - 1) == 899, "a wide segment pins both ends");
        (film.viewStart, film.viewSpan) = (450, 600);
        var outOnly = film.AnchorRequest;
        Expect(outOnly is { Ends: AnchorEnds.Out } o && o.FrameAt(0) == 899 && o.FrameAt(1) < 899, "with the in point off to the left, the out end is anchored");
        film.viewStart = 451;
        Expect(film.AnchorRequest == outOnly, "a pan that keeps the tiles keeps the request");
        (film.viewStart, film.viewSpan) = (0, 600);
        Expect(film.AnchorRequest is { Ends: AnchorEnds.In } i && i.FrameAt(0) == 300 && i.FrameAt(1) > 300, "with the out point off to the right, the in end is anchored");
        (film.viewStart, film.viewSpan) = (400, 300);
        Expect(film.AnchorRequest is null, "a segment that fills the view uses the regular strip");
        return errors;
    }

    static void DrawCover(Graphics graphics, Image image, RectangleF slot)
    {
        if (image.Width <= 0 || image.Height <= 0 || slot.Width <= 0 || slot.Height <= 0) return;
        var scale = Math.Max(slot.Width / image.Width, slot.Height / image.Height);
        var srcWidth = slot.Width / scale;
        var srcHeight = slot.Height / scale;
        var src = new RectangleF((image.Width - srcWidth) / 2f, (image.Height - srcHeight) / 2f, srcWidth, srcHeight);
        graphics.DrawImage(image, slot, src, GraphicsUnit.Pixel);
    }
}
