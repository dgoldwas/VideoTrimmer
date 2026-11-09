using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VideoTrimmer;

/// <summary>
/// The filmstrip with its kept segments, audio waveform lane, and scene/silence markers.
/// One segment is active: its in and out handles are the ones the user edits. The others are drawn as kept blocks.
/// </summary>
sealed class TimelineControl : Control
{
    enum DragKind { None, Playhead, InHandle, OutHandle, Span }
    enum HitKind { None, InHandle, OutHandle, Selection, Film }

    static readonly SolidBrush SurfaceBrush = new(Theme.Surface);
    static readonly SolidBrush LaneBrush = new(Theme.Darken(Theme.Surface, 8));
    static readonly SolidBrush DimBrush = new(Color.FromArgb(170, 16, 18, 22));
    static readonly HatchBrush CutHatch = new(HatchStyle.WideDownwardDiagonal, Color.FromArgb(46, Theme.OutPoint), Color.Transparent);
    static readonly SolidBrush TagBackgroundBrush = new(Color.FromArgb(210, 16, 18, 22));
    static readonly SolidBrush PlayheadBrush = new(Theme.Text);
    static readonly SolidBrush GripBrush = new(Color.FromArgb(180, 16, 18, 22));
    static readonly SolidBrush WaveBrush = new(Theme.Wave);
    static readonly SolidBrush SilenceBrush = new(Color.FromArgb(70, Theme.Silence));
    static readonly SolidBrush SceneBrush = new(Theme.Scene);
    static readonly Pen SceneLinePen = new(Color.FromArgb(210, Theme.Scene), 2);
    static readonly Pen SilenceEdgePen = new(Color.FromArgb(150, Theme.Silence), 1);
    static readonly Pen BorderPen = new(Theme.Border);
    static readonly Pen LaneLinePen = new(Theme.Border);
    static readonly Pen SelectionPen = new(Theme.Accent, 2);
    static readonly Pen KeptPen = new(Color.FromArgb(170, Theme.Accent), 2) { DashStyle = DashStyle.Dash };
    static readonly Pen PlayheadPen = new(Theme.Text, 2);

    Bitmap?[]? strip;
    double duration;
    double fps;
    long totalFrames;
    long inFrame;
    long outFrame;
    long playhead;
    readonly List<Segment> others = new();
    DragKind drag = DragKind.None;
    Point dragStart;
    long spanAnchorIn;
    long spanAnchorOut;
    long spanAnchorFrame;
    bool dragMoved;

    float[]? envelope;
    float[]? waveColumns;
    int waveColumnsWidth;
    readonly List<Silence> silences = new();
    readonly List<long> scenes = new();
    long[] snapPoints = [];

    public TimelineControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Background;
        TabStop = true;
        AccessibleName = "Timeline";
        AccessibleRole = AccessibleRole.Pane;
        Cursor = Cursors.Hand;
    }

    public bool HasMedia => totalFrames > 0 && fps > 0 && duration > 0;
    public bool HasFrames => strip is { Length: > 0 };
    public bool IsDragging => drag != DragKind.None;
    public bool Locked { get; set; }
    public string Placeholder { get; set; } = "";
    /// <summary>Text shown in the waveform lane while analysis runs, or why there is no waveform.</summary>
    public string LaneNote { get; set; } = "";
    public bool SnapEnabled { get; set; } = true;
    public double Fps => fps;
    public long TotalFrames => totalFrames;
    public long InFrame => inFrame;
    public long OutFrame => outFrame;
    public long PlayheadFrame => playhead;
    public double InSeconds => Seconds(inFrame);
    public double OutSeconds => Seconds(outFrame);
    public int MarkerCount => snapPoints.Length;
    public int SceneCount => scenes.Count;
    public int SilenceCount => silences.Count;

    Segment Active => new(inFrame, outFrame);

    /// <summary>Every segment in time order, including the active one. Touching segments stay separate here.</summary>
    public IReadOnlyList<Segment> AllSegments => others.Append(Active).OrderBy(s => s.In).ToList();

    public int ActiveIndex => others.Count(s => s.In < inFrame);

    /// <summary>What the export keeps: sorted, with touching segments joined.</summary>
    public IReadOnlyList<Segment> ExportSegments => CutList.Normalize(others.Append(Active), totalFrames);

    public double PlayheadSeconds
    {
        get
        {
            var seconds = Seconds(playhead);
            if (duration <= 0) return 0;
            return Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001));
        }
    }

    public int FilmWidth => Math.Max(1, FilmRect().Width);

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
        strip = null;
        drag = DragKind.None;
        ClearAnalysis();
        NotifyValues();
    }

    public void UseStrip(Bitmap?[]? images)
    {
        strip = images;
        Invalidate();
    }

    public void ClearAnalysis()
    {
        envelope = null;
        waveColumns = null;
        silences.Clear();
        scenes.Clear();
        snapPoints = [];
        LaneNote = "";
        Invalidate();
    }

    public void SetAudio(float[]? levels, IEnumerable<Silence> found)
    {
        envelope = levels;
        waveColumns = null;
        silences.Clear();
        silences.AddRange(found);
        RebuildSnapPoints();
        Invalidate();
    }

    public void AddScene(double seconds)
    {
        var frame = (long)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);
        if (frame <= 0 || frame >= totalFrames) return;
        var index = scenes.BinarySearch(frame);
        if (index >= 0) return;
        scenes.Insert(~index, frame);
        RebuildSnapPoints();
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

    /// <summary>Moves the playhead to the next (or previous) scene change or silence edge.</summary>
    public bool JumpToMarker(int direction)
    {
        if (!HasMedia || Locked || snapPoints.Length == 0) return false;
        long? target = direction > 0
            ? snapPoints.Where(p => p > playhead).Cast<long?>().FirstOrDefault()
            : snapPoints.Where(p => p < playhead).Cast<long?>().LastOrDefault();
        if (target is null) return false;
        playhead = Math.Clamp(target.Value, 0, totalFrames - 1);
        NotifyValues();
        return true;
    }

    /// <summary>Splits the segment under the playhead in two. The right half becomes active.</summary>
    public bool SplitAtPlayhead()
    {
        if (!HasMedia || Locked) return false;
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

    public bool SelectSegment(int index)
    {
        if (!HasMedia || Locked) return false;
        var all = AllSegments;
        if (index < 0 || index >= all.Count || index == ActiveIndex) return false;
        var target = all[index];
        others.Remove(target);
        others.Add(Active);
        (inFrame, outFrame) = (target.In, target.Out);
        playhead = inFrame;
        NotifyValues();
        return true;
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

    public int FramesInOneSecond() => TimeMath.RoundedFps(fps);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Bilinear is enough for filmstrip thumbs and much cheaper than HighQualityBicubic.
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var film = FilmRect();
        var lane = LaneRect();
        var track = TrackRect();
        g.FillRectangle(SurfaceBrush, film);

        if (strip is { Length: > 0 })
        {
            var slotWidth = film.Width / (float)strip.Length;
            for (var i = 0; i < strip.Length; i++)
            {
                var image = strip[i];
                if (image is null) continue;
                var slot = new RectangleF(film.Left + i * slotWidth, film.Top, slotWidth + 1, film.Height);
                DrawCover(g, image, slot);
            }
        }
        else if (!string.IsNullOrWhiteSpace(Placeholder))
        {
            TextRenderer.DrawText(g, Placeholder, Theme.Ui(9.5f), film, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        if (!HasMedia)
        {
            DrawBorder(g, film);
            return;
        }

        DrawLane(g, lane);
        DrawScenes(g, film);

        // Everything outside the kept segments is cut: dim it and hatch it.
        foreach (var gap in CutList.Gaps(CutList.Normalize(others.Append(Active), totalFrames), totalFrames))
        {
            var left = XOf(gap.In);
            var right = XOf(gap.Out);
            if (right <= left) continue;
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
            g.DrawRectangle(KeptPen, x1, track.Top + 1, Math.Max(1, x2 - x1), track.Height - 3);
            if (numbered) DrawBadge(g, (i + 1).ToString(), x1, x2, film, selected: false);
        }

        var inX = XOf(inFrame);
        var outX = XOf(outFrame);
        if (outX > inX)
            g.DrawRectangle(SelectionPen, inX, track.Top + 1, outX - inX, track.Height - 3);
        if (numbered) DrawBadge(g, (active + 1).ToString(), inX, outX, film, selected: true);

        DrawHandle(g, inX, track, Theme.InPoint);
        DrawHandle(g, outX, track, Theme.OutPoint);
        DrawPlayhead(g, XOf(Math.Clamp(playhead, 0, totalFrames)), track);
        DrawRangeTags(g, film, inX, outX);
        DrawLaneNote(g, lane);
        DrawBorder(g, track);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(track, 3, 3));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!HasMedia || Locked || e.Button != MouseButtons.Left) return;
        Focus();
        var frame = FrameAt(e.X);
        var shift = (ModifierKeys & Keys.Shift) == Keys.Shift;
        var alt = (ModifierKeys & Keys.Alt) == Keys.Alt;
        var hit = HitTest(e.X);
        dragMoved = false;
        dragStart = e.Location;
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
            spanAnchorFrame = frame;
            Capture = true;
            return;
        }

        ApplyPointer(Snap(frame, e.X), shift, alt);
        drag = DragKind.Playhead;
        Capture = true;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (!HasMedia || Locked || e.Button != MouseButtons.Left) return;
        // Double-click a kept segment to make it the one the handles edit.
        var frame = FrameAt(e.X);
        var all = AllSegments;
        var index = all.ToList().FindIndex(s => s.Contains(frame));
        if (index >= 0 && index != ActiveIndex && SelectSegment(index))
            InteractionEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasMedia) return;
        if (drag == DragKind.None)
        {
            Cursor = CursorFor(HitTest(e.X));
            return;
        }

        if (drag == DragKind.Span && !dragMoved && Math.Abs(e.X - dragStart.X) < LogicalToDeviceUnits(4))
            return;

        dragMoved = true;
        var frame = FrameAt(e.X);
        switch (drag)
        {
            case DragKind.InHandle:
                inFrame = TimeMath.StickIn(Snap(frame, e.X), outFrame, totalFrames);
                playhead = inFrame;
                NotifyValues();
                break;
            case DragKind.OutHandle:
                outFrame = TimeMath.StickOut(Snap(frame, e.X), inFrame, totalFrames);
                playhead = Math.Max(inFrame, outFrame - 1);
                NotifyValues();
                break;
            case DragKind.Span:
                (inFrame, outFrame) = TimeMath.Slide(spanAnchorIn, spanAnchorOut, frame - spanAnchorFrame, totalFrames);
                playhead = inFrame;
                NotifyValues();
                break;
            case DragKind.Playhead:
                playhead = Math.Clamp(Snap(frame, e.X), 0, totalFrames - 1);
                NotifyValues();
                break;
        }
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
        if (!HasMedia || Locked) return;
        var step = (ModifierKeys & Keys.Shift) == Keys.Shift ? FramesInOneSecond() : 1;
        NudgePlayhead(e.Delta > 0 ? -step : step);
        InteractionEnded?.Invoke(this, EventArgs.Empty);
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
        drag = DragKind.None;
        if (Capture) Capture = false;
        Cursor = Cursors.Hand;
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

    /// <summary>Pulls a dragged frame onto a nearby scene change or silence edge. Hold Ctrl to place it freely.</summary>
    long Snap(long frame, int x)
    {
        if (!SnapEnabled || snapPoints.Length == 0 || (ModifierKeys & Keys.Control) == Keys.Control) return frame;
        var index = Array.BinarySearch(snapPoints, frame);
        if (index >= 0) return frame;
        index = ~index;
        var reach = Math.Max(6, LogicalToDeviceUnits(7));
        long? best = null;
        var bestDistance = int.MaxValue;
        foreach (var candidate in new[] { index - 1, index })
        {
            if (candidate < 0 || candidate >= snapPoints.Length) continue;
            var distance = Math.Abs(XOf(snapPoints[candidate]) - x);
            if (distance <= reach && distance < bestDistance)
            {
                best = snapPoints[candidate];
                bestDistance = distance;
            }
        }

        return best ?? frame;
    }

    void RebuildSnapPoints()
    {
        var points = new SortedSet<long>(scenes);
        foreach (var silence in silences)
        {
            points.Add(FrameOfSeconds(silence.Start));
            points.Add(FrameOfSeconds(silence.End));
        }

        points.RemoveWhere(p => p <= 0 || p >= totalFrames);
        snapPoints = points.ToArray();
    }

    long FrameOfSeconds(double seconds) => (long)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);

    double Seconds(long frame) => fps <= 0 ? 0 : frame / fps;

    /// <summary>The thumbnails, plus the waveform lane beneath them once a clip is loaded.</summary>
    Rectangle TrackRect()
    {
        var pad = LogicalToDeviceUnits(2);
        var rect = ClientRectangle;
        rect.Inflate(-pad, -pad);
        if (rect.Width < 1) rect.Width = 1;
        if (rect.Height < 1) rect.Height = 1;
        return rect;
    }

    int LaneHeight()
    {
        if (!HasMedia) return 0;
        var track = TrackRect();
        return Math.Clamp(track.Height / 4, LogicalToDeviceUnits(22), LogicalToDeviceUnits(40));
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

    long FrameAt(int x)
    {
        var film = FilmRect();
        if (film.Width <= 0 || totalFrames <= 0) return 0;
        var ratio = Math.Clamp((x - film.Left) / (double)film.Width, 0, 1);
        return (long)Math.Round(ratio * totalFrames, MidpointRounding.AwayFromZero);
    }

    int XOf(long frame)
    {
        var film = FilmRect();
        if (totalFrames <= 0) return film.Left;
        var ratio = Math.Clamp(frame / (double)totalFrames, 0, 1);
        return film.Left + (int)Math.Round(ratio * film.Width, MidpointRounding.AwayFromZero);
    }

    int XOfSeconds(double seconds) => XOf(FrameOfSeconds(seconds));

    HitKind HitTest(int x)
    {
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
        _ => Cursors.Hand,
    };

    void DrawLane(Graphics graphics, Rectangle lane)
    {
        if (lane.Height <= 0) return;
        graphics.FillRectangle(LaneBrush, lane);
        graphics.DrawLine(LaneLinePen, lane.Left, lane.Top, lane.Right, lane.Top);

        foreach (var silence in silences)
        {
            var left = XOfSeconds(silence.Start);
            var right = Math.Max(left + 1, XOfSeconds(silence.End));
            graphics.FillRectangle(SilenceBrush, left, lane.Top, right - left, lane.Height);
            graphics.DrawLine(SilenceEdgePen, left, lane.Top, left, lane.Bottom);
            graphics.DrawLine(SilenceEdgePen, right, lane.Top, right, lane.Bottom);
        }

        var columns = WaveColumns(lane.Width);
        if (columns is not null)
        {
            var middle = lane.Top + lane.Height / 2f;
            var half = (lane.Height - LogicalToDeviceUnits(6)) / 2f;
            for (var i = 0; i < columns.Length; i++)
            {
                var height = Math.Max(1, columns[i] * half);
                graphics.FillRectangle(WaveBrush, lane.Left + i, middle - height, 1, height * 2);
            }
        }
    }

    /// <summary>Analysis progress or results, drawn last so the cut overlay does not dim it.</summary>
    void DrawLaneNote(Graphics graphics, Rectangle lane)
    {
        if (lane.Height <= 0 || string.IsNullOrWhiteSpace(LaneNote)) return;
        var font = Theme.Ui(8.5f);
        var size = TextRenderer.MeasureText(LaneNote, font);
        var width = Math.Min(lane.Width - LogicalToDeviceUnits(16), size.Width + LogicalToDeviceUnits(12));
        var height = Math.Min(lane.Height - 2, size.Height + LogicalToDeviceUnits(2));
        var note = new Rectangle(lane.Right - width - LogicalToDeviceUnits(16), lane.Top + (lane.Height - height) / 2, width, height);
        graphics.FillRectangle(TagBackgroundBrush, note);
        TextRenderer.DrawText(graphics, LaneNote, font, note, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    /// <summary>The loudest window under each pixel column, cached per width.</summary>
    float[]? WaveColumns(int width)
    {
        if (envelope is not { Length: > 0 } || width <= 0 || duration <= 0) return null;
        if (waveColumns is not null && waveColumnsWidth == width) return waveColumns;
        var columns = new float[width];
        var windowsPerSecond = 1 / SmartAnalysis.WindowSeconds;
        for (var x = 0; x < width; x++)
        {
            var start = (int)(x / (double)width * duration * windowsPerSecond);
            var end = Math.Max(start + 1, (int)((x + 1) / (double)width * duration * windowsPerSecond));
            var peak = 0f;
            for (var i = start; i < end && i < envelope.Length; i++)
                peak = Math.Max(peak, envelope[i]);
            columns[x] = SmartAnalysis.DisplayLevel(peak);
        }

        waveColumns = columns;
        waveColumnsWidth = width;
        return columns;
    }

    void DrawScenes(Graphics graphics, Rectangle film)
    {
        if (scenes.Count == 0) return;
        var size = Math.Max(4, LogicalToDeviceUnits(5));
        int? lastX = null;
        foreach (var scene in scenes)
        {
            var x = XOf(scene);
            if (x - lastX < 2) continue;
            lastX = x;
            graphics.DrawLine(SceneLinePen, x, film.Top, x, film.Bottom);
            Point[] diamond = [new(x, film.Bottom - size * 2), new(x + size, film.Bottom - size), new(x, film.Bottom), new(x - size, film.Bottom - size)];
            graphics.FillPolygon(SceneBrush, diamond);
        }
    }

    void DrawBadge(Graphics graphics, string text, int left, int right, Rectangle film, bool selected)
    {
        var font = Theme.Ui(8, FontStyle.Bold);
        var size = TextRenderer.MeasureText(text, font);
        var width = Math.Max(size.Width + LogicalToDeviceUnits(6), LogicalToDeviceUnits(18));
        var height = size.Height + LogicalToDeviceUnits(2);
        if (right - left < width + LogicalToDeviceUnits(8)) return;
        var rect = new Rectangle(left + LogicalToDeviceUnits(6), film.Bottom - height - LogicalToDeviceUnits(6), width, height);
        using var path = PaintUtil.Rounded(rect, height / 2);
        using var brush = new SolidBrush(selected ? Theme.Accent : Color.FromArgb(220, Theme.Surface));
        graphics.FillPath(brush, path);
        TextRenderer.DrawText(graphics, text, font, rect, selected ? Color.White : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawHandle(Graphics graphics, int x, Rectangle track, Color color)
    {
        var width = Math.Max(8, LogicalToDeviceUnits(10));
        var rect = new Rectangle(x - width / 2, track.Top, width, track.Height);
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, rect);
        var middle = track.Top + track.Height / 2;
        var gap = LogicalToDeviceUnits(5);
        for (var i = -1; i <= 1; i++)
            graphics.FillEllipse(GripBrush, x - 2, middle + i * gap - 2, 4, 4);
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
            DrawTagAt(graphics, inText, inLeft, top, inSize, Theme.InPoint);
            DrawTagAt(graphics, outText, outLeft, top, outSize, Theme.OutPoint);
            return;
        }

        // A one-frame selection against the film edge has no side room. Stack the tags.
        inLeft = ClampTag(Math.Min(inX, outX) + pad, inSize.Width, film);
        outLeft = ClampTag(inLeft, outSize.Width, film);
        var outTop = top + inSize.Height + gap;
        if (outTop + outSize.Height > film.Bottom - 4)
            outTop = Math.Max(film.Top + 2, film.Bottom - outSize.Height - 4);
        DrawTagAt(graphics, inText, inLeft, top, inSize, Theme.InPoint);
        DrawTagAt(graphics, outText, outLeft, outTop, outSize, Theme.OutPoint);
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

    static void DrawBorder(Graphics graphics, Rectangle rect)
    {
        graphics.DrawRectangle(BorderPen, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
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
