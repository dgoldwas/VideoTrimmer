using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace VideoTrimmer;

sealed class TimelineControl : Control
{
    enum DragKind { None, Playhead, InHandle, OutHandle, Span }
    enum HitKind { None, InHandle, OutHandle, Selection, Film }

    static readonly SolidBrush SurfaceBrush = new(Theme.Surface);
    static readonly SolidBrush DimBrush = new(Color.FromArgb(150, 16, 18, 22));
    static readonly SolidBrush TagBackgroundBrush = new(Color.FromArgb(210, 16, 18, 22));
    static readonly SolidBrush PlayheadBrush = new(Theme.Text);
    static readonly SolidBrush GripBrush = new(Color.FromArgb(180, 16, 18, 22));
    static readonly Pen BorderPen = new(Theme.Border);
    static readonly Pen SelectionPen = new(Theme.Accent, 2);
    static readonly Pen PlayheadPen = new(Theme.Text, 2);

    Bitmap?[]? strip;
    double duration;
    double fps;
    long totalFrames;
    long inFrame;
    long outFrame;
    long playhead;
    DragKind drag = DragKind.None;
    Point dragStart;
    long spanAnchorIn;
    long spanAnchorOut;
    long spanAnchorFrame;
    bool dragMoved;

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
    public double Fps => fps;
    public long InFrame => inFrame;
    public long OutFrame => outFrame;
    public long PlayheadFrame => playhead;
    public double InSeconds => Seconds(inFrame);
    public double OutSeconds => Seconds(outFrame);

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
        strip = null;
        drag = DragKind.None;
        Invalidate();
        NotifyValues();
    }

    public void UseStrip(Bitmap?[]? images)
    {
        strip = images;
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

        var inX = XOf(inFrame);
        var outX = XOf(outFrame);
        if (inX > film.Left)
            g.FillRectangle(DimBrush, film.Left, film.Top, inX - film.Left, film.Height);
        if (outX < film.Right)
            g.FillRectangle(DimBrush, outX, film.Top, film.Right - outX, film.Height);

        if (outX > inX)
            g.DrawRectangle(SelectionPen, inX, film.Top + 1, outX - inX, film.Height - 3);

        DrawHandle(g, inX, film, Theme.InPoint);
        DrawHandle(g, outX, film, Theme.OutPoint);
        DrawPlayhead(g, XOf(Math.Clamp(playhead, 0, totalFrames)), film);
        DrawRangeTags(g, film, inX, outX);
        DrawBorder(g, film);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(film, 3, 3));
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

        ApplyPointer(frame, shift, alt);
        drag = DragKind.Playhead;
        Capture = true;
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

    void FinishDrag()
    {
        if (drag == DragKind.None) return;
        drag = DragKind.None;
        if (Capture) Capture = false;
        Cursor = Cursors.Hand;
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
        var description = HasMedia
            ? $"In {TimeMath.Format(inFrame, fps)}, out {TimeMath.Format(outFrame, fps)}, playhead {TimeMath.Format(playhead, fps)}"
            : "No video";
        AccessibleDescription = description;
        Invalidate();
        ValuesChanged?.Invoke(this, EventArgs.Empty);
    }

    double Seconds(long frame) => fps <= 0 ? 0 : frame / fps;

    Rectangle FilmRect()
    {
        var pad = LogicalToDeviceUnits(2);
        var rect = ClientRectangle;
        rect.Inflate(-pad, -pad);
        if (rect.Width < 1) rect.Width = 1;
        if (rect.Height < 1) rect.Height = 1;
        return rect;
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

    void DrawHandle(Graphics graphics, int x, Rectangle film, Color color)
    {
        var width = Math.Max(8, LogicalToDeviceUnits(10));
        var rect = new Rectangle(x - width / 2, film.Top, width, film.Height);
        using var brush = new SolidBrush(color);
        graphics.FillRectangle(brush, rect);
        var middle = film.Top + film.Height / 2;
        var gap = LogicalToDeviceUnits(5);
        for (var i = -1; i <= 1; i++)
            graphics.FillEllipse(GripBrush, x - 2, middle + i * gap - 2, 4, 4);
    }

    static void DrawPlayhead(Graphics graphics, int x, Rectangle film)
    {
        Point[] head = [new Point(x - 6, film.Top), new Point(x + 6, film.Top), new Point(x, film.Top + 9)];
        graphics.FillPolygon(PlayheadBrush, head);
        graphics.DrawLine(PlayheadPen, x, film.Top, x, film.Bottom);
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

    static void DrawBorder(Graphics graphics, Rectangle film)
    {
        graphics.DrawRectangle(BorderPen, film.X, film.Y, film.Width - 1, film.Height - 1);
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
