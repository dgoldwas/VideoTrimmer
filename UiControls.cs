using System.Drawing.Drawing2D;

namespace VideoTrimmer;

static class PaintUtil
{
    public static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

sealed class FlatButton : Control, IButtonControl
{
    bool hover;
    bool pressed;
    bool isDefault;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Ui(9.5f, FontStyle.Bold);
        ForeColor = Theme.Text;
        BackColor = Theme.Background;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleDefaultActionDescription = "Press";
        Size = new Size(120, 36);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new FlatButtonAccessibleObject(this);

    public bool Primary { get; set; }
    public Color? TextColor { get; set; }
    public DialogResult DialogResult { get; set; }

    public void NotifyDefault(bool value)
    {
        isDefault = value;
        Invalidate();
    }

    public void PerformClick()
    {
        if (!Enabled) return;
        OnClick(EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = Primary ? Theme.Accent : Theme.Surface;
        var text = Primary ? Color.White : TextColor ?? Theme.Text;
        var border = Primary ? Theme.Accent : Theme.Border;
        if (!Enabled)
        {
            fill = Theme.Surface;
            text = Theme.Muted;
            border = Theme.Border;
        }
        else if (pressed)
        {
            fill = Theme.Darken(fill, 24);
        }
        else if (hover)
        {
            fill = Theme.Lift(fill, 18);
        }

        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = PaintUtil.Rounded(rect, LogicalToDeviceUnits(8));
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        using var pen = new Pen(isDefault && !Primary && Enabled ? Theme.Accent : border);
        graphics.DrawPath(pen, path);
        TextRenderer.DrawText(graphics, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            PerformClick();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    sealed class FlatButtonAccessibleObject : ControlAccessibleObject
    {
        readonly FlatButton owner;

        public FlatButtonAccessibleObject(FlatButton owner) : base(owner) => this.owner = owner;

        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string Name => owner.AccessibleName ?? owner.Text;
        public override string DefaultAction => "Press";

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Focusable;
                if (owner.Focused) state |= AccessibleStates.Focused;
                if (!owner.Enabled) state |= AccessibleStates.Unavailable;
                if (!owner.Visible) state |= AccessibleStates.Invisible;
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (!owner.Enabled || !owner.IsHandleCreated) return;
            try
            {
                owner.BeginInvoke(new Action(owner.PerformClick));
            }
            catch (InvalidOperationException)
            {
                // The window closed between the check and the post.
            }
        }
    }
}

sealed class OptionCard : Control
{
    bool hover;
    bool selected;
    string title = "";

    public OptionCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Font = Theme.Ui(9.5f, FontStyle.Bold);
        ForeColor = Theme.Text;
        BackColor = Theme.Background;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.RadioButton;
        AccessibleDefaultActionDescription = "Select";
        Size = new Size(180, 40);
    }

    public void PerformClick()
    {
        if (!Enabled) return;
        OnClick(EventArgs.Empty);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new OptionCardAccessibleObject(this);

    public string Title
    {
        get => title;
        set { title = value; Text = value; AccessibleName = value; Invalidate(); }
    }

    public bool Selected
    {
        get => selected;
        set { selected = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = !Enabled ? Theme.Surface : Selected ? Theme.Mix(Theme.Surface, Theme.Accent, 0.28f) : hover ? Theme.Lift(Theme.Surface, 12) : Theme.Surface;
        var border = Selected ? Theme.Accent : Theme.Border;
        var text = Enabled ? Theme.Text : Theme.Muted;
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using var path = PaintUtil.Rounded(rect, LogicalToDeviceUnits(8));
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        using var pen = new Pen(border, Selected ? 2 : 1);
        graphics.DrawPath(pen, path);
        var textRect = new Rectangle(LogicalToDeviceUnits(14), 0, Math.Max(1, Width - LogicalToDeviceUnits(22)), Height);
        TextRenderer.DrawText(graphics, Title, Font, textRect, text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnClick(EventArgs e)
    {
        if (!Enabled) return;
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    sealed class OptionCardAccessibleObject : ControlAccessibleObject
    {
        readonly OptionCard owner;

        public OptionCardAccessibleObject(OptionCard owner) : base(owner) => this.owner = owner;

        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override string Name => owner.AccessibleName ?? owner.Text;
        public override string DefaultAction => "Select";

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Focusable;
                if (owner.Focused) state |= AccessibleStates.Focused;
                if (owner.Selected) state |= AccessibleStates.Checked;
                if (!owner.Enabled) state |= AccessibleStates.Unavailable;
                if (!owner.Visible) state |= AccessibleStates.Invisible;
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (!owner.Enabled || !owner.IsHandleCreated) return;
            try
            {
                owner.BeginInvoke(new Action(owner.PerformClick));
            }
            catch (InvalidOperationException)
            {
                // The window closed between the check and the post.
            }
        }
    }
}

sealed class DarkField : Panel
{
    public TextBox Box { get; }

    public DarkField()
    {
        BackColor = Theme.Border;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Mono(11),
        };
        Box.GotFocus += (_, _) => { BackColor = Theme.Accent; Box.SelectAll(); };
        Box.LostFocus += (_, _) => BackColor = Theme.Border;
        Controls.Add(Box);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var y = Math.Max(1, (ClientSize.Height - Box.PreferredHeight) / 2);
        Box.SetBounds(LogicalToDeviceUnits(8), y, Math.Max(10, ClientSize.Width - LogicalToDeviceUnits(16)), Box.PreferredHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Box.Focus();
    }
}

sealed class ThinProgress : Control
{
    double fraction;

    public ThinProgress()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        TabStop = false;
        AccessibleRole = AccessibleRole.ProgressBar;
        Visible = false;
    }

    public double Fraction
    {
        get => fraction;
        set { fraction = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Border);
        var width = (int)Math.Round(ClientSize.Width * fraction);
        if (width <= 0) return;
        using var brush = new SolidBrush(Theme.Accent);
        e.Graphics.FillRectangle(brush, 0, 0, width, ClientSize.Height);
    }
}

sealed class DropPrompt : Control
{
    public FlatButton Open { get; }
    public event EventHandler? OpenRequested;

    public DropPrompt()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
        AccessibleName = "Drop a video to trim";
        Cursor = Cursors.Hand;
        Open = new FlatButton { Text = "Open a video", Primary = true, AccessibleName = "Open a video" };
        Open.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        Controls.Add(Open);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left)
            OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var top = BlockTop();
        Open.SetBounds(
            Math.Max(0, (ClientSize.Width - LogicalToDeviceUnits(180)) / 2),
            top + LogicalToDeviceUnits(84),
            LogicalToDeviceUnits(180),
            LogicalToDeviceUnits(40));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.Clear(Theme.Background);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var pad = LogicalToDeviceUnits(8);
        var rect = Rectangle.Inflate(ClientRectangle, -pad, -pad);
        if (rect.Width < 20 || rect.Height < 20) return;
        using var pen = new Pen(Theme.Border, 2) { DashStyle = DashStyle.Dash };
        using var path = PaintUtil.Rounded(new Rectangle(rect.X, rect.Y, rect.Width - 1, rect.Height - 1), LogicalToDeviceUnits(16));
        graphics.DrawPath(pen, path);

        var top = BlockTop();
        var titleFont = Theme.Ui(22, FontStyle.Bold);
        var titleRect = new Rectangle(0, top, ClientSize.Width, LogicalToDeviceUnits(36));
        TextRenderer.DrawText(graphics, "Drop a video to trim", titleFont, titleRect, Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        var subRect = new Rectangle(pad, top + LogicalToDeviceUnits(40), ClientSize.Width - pad * 2, LogicalToDeviceUnits(24));
        TextRenderer.DrawText(graphics, "mkv, mp4, mov, avi, webm, ts, m2ts, wmv", Theme.Ui(10), subRect, Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    int BlockTop() => Math.Max(LogicalToDeviceUnits(16), (ClientSize.Height - LogicalToDeviceUnits(140)) / 2);
}

sealed class ToolbarPanel : Panel
{
    public Label FileName { get; }
    public Label Meta { get; }
    public FlatButton Open { get; }

    public ToolbarPanel()
    {
        BackColor = Theme.Surface;
        Visible = false;
        FileName = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Text,
            Font = Theme.Ui(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "File name",
        };
        Meta = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Muted,
            Font = Theme.Ui(9),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Video details",
        };
        Open = new FlatButton { Text = "Open", AccessibleName = "Open" };
        Controls.Add(FileName);
        Controls.Add(Meta);
        Controls.Add(Open);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = LogicalToDeviceUnits(12);
        var buttonWidth = LogicalToDeviceUnits(96);
        var buttonHeight = LogicalToDeviceUnits(32);
        var y = Math.Max(pad, (ClientSize.Height - buttonHeight) / 2);
        Open.SetBounds(Math.Max(pad, ClientSize.Width - pad - buttonWidth), y, buttonWidth, buttonHeight);
        var textWidth = Math.Max(10, Open.Left - pad * 2);
        FileName.SetBounds(pad, LogicalToDeviceUnits(6), textWidth, LogicalToDeviceUnits(22));
        Meta.SetBounds(pad, FileName.Bottom, textWidth, LogicalToDeviceUnits(20));
    }
}

sealed class StatusBar : Panel
{
    public Label Label { get; }
    public ThinProgress Progress { get; }
    /// <summary>Shows the running version; becomes "Update to ..." when GitHub has a newer release.</summary>
    public FlatButton UpdateButton { get; }

    public StatusBar()
    {
        BackColor = Theme.Surface;
        Label = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Status",
        };
        Progress = new ThinProgress { AccessibleName = "Export progress" };
        UpdateButton = new FlatButton { Font = Theme.Ui(8.5f), TextColor = Theme.Muted, AccessibleName = "Check for updates", TabStop = false };
        Controls.Add(Label);
        Controls.Add(Progress);
        Controls.Add(UpdateButton);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = LogicalToDeviceUnits(12);
        var right = ClientSize.Width - LogicalToDeviceUnits(4);
        if (UpdateButton.Visible)
        {
            var updateWidth = TextRenderer.MeasureText(UpdateButton.Text, UpdateButton.Font).Width + LogicalToDeviceUnits(20);
            var updateHeight = Math.Max(1, ClientSize.Height - LogicalToDeviceUnits(6));
            UpdateButton.SetBounds(right - updateWidth, (ClientSize.Height - updateHeight) / 2, updateWidth, updateHeight);
            right = UpdateButton.Left - LogicalToDeviceUnits(8);
        }

        if (Progress.Visible)
        {
            var progressWidth = LogicalToDeviceUnits(160);
            var progressHeight = LogicalToDeviceUnits(8);
            Progress.SetBounds(right - progressWidth, Math.Max(0, (ClientSize.Height - progressHeight) / 2), progressWidth, progressHeight);
            right = Progress.Left - pad;
        }

        Label.SetBounds(pad, 0, Math.Max(10, right - pad), ClientSize.Height);
    }
}

/// <summary>A rounded sidebar panel with a small uppercase title, like FrameGrabber's sections.</summary>
sealed class Card : Panel
{
    string title = "";
    string detail = "";

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    public string Title
    {
        get => title;
        set { title = value; AccessibleName = value; Invalidate(); }
    }

    /// <summary>Muted text on the right of the title, such as a count.</summary>
    public string Detail
    {
        get => detail;
        set { if (detail == value) return; detail = value; Invalidate(); }
    }

    public int Pad => LogicalToDeviceUnits(14);
    public int ContentTop => Pad + LogicalToDeviceUnits(22);
    public Rectangle ContentBounds => new(Pad, ContentTop, Math.Max(1, ClientSize.Width - Pad * 2), Math.Max(1, ClientSize.Height - ContentTop - Pad));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = PaintUtil.Rounded(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), LogicalToDeviceUnits(14));
        using var brush = new SolidBrush(Theme.Surface);
        g.FillPath(brush, path);
        var header = new Rectangle(Pad, Pad - LogicalToDeviceUnits(2), ClientSize.Width - Pad * 2, LogicalToDeviceUnits(18));
        TextRenderer.DrawText(g, title.ToUpperInvariant(), Theme.Ui(8, FontStyle.Bold), header, Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        if (detail.Length > 0)
            TextRenderer.DrawText(g, detail, Theme.Ui(8.5f), header, Theme.Muted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// Every kept segment as a row: number, in → out, length, and where it lands in the export.
/// Click a row to edit that segment; its × removes it. Scrolls with the mouse wheel.
/// </summary>
sealed class SegmentList : Control
{
    IReadOnlyList<Segment> segments = [];
    int active;
    double fps = 30;
    int scroll;
    int hover = -1;
    bool hoverClose;

    public event EventHandler<int>? SelectRequested;
    public event EventHandler<int>? RemoveRequested;

    public SegmentList()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Surface;
        AccessibleName = "Segments";
        AccessibleRole = AccessibleRole.List;
    }

    int RowHeight => LogicalToDeviceUnits(46);
    int Gap => LogicalToDeviceUnits(6);
    int Stride => RowHeight + Gap;
    int MaxScroll => Math.Max(0, segments.Count * Stride - Gap - ClientSize.Height);

    public void SetSegments(IReadOnlyList<Segment> all, int activeIndex, double framesPerSecond)
    {
        var activeMoved = activeIndex != active || all.Count != segments.Count;
        segments = all;
        active = activeIndex;
        fps = framesPerSecond;
        if (activeMoved) ScrollIntoView(active);
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        AccessibleDescription = $"{all.Count} segments. Segment {active + 1} is selected.";
        Invalidate();
    }

    void ScrollIntoView(int index)
    {
        var top = index * Stride;
        if (top < scroll) scroll = top;
        else if (top + RowHeight > scroll + ClientSize.Height) scroll = top + RowHeight - ClientSize.Height;
    }

    Rectangle RowRect(int index) => new(0, index * Stride - scroll, ClientSize.Width - LogicalToDeviceUnits(6), RowHeight);

    Rectangle CloseRect(Rectangle row) => new(row.Right - LogicalToDeviceUnits(32), row.Top, LogicalToDeviceUnits(28), row.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var normalized = CutList.Normalize(segments, long.MaxValue);
        for (var i = 0; i < segments.Count; i++)
        {
            var row = RowRect(i);
            if (row.Bottom < 0 || row.Top > ClientSize.Height) continue;
            DrawRow(g, i, row, normalized);
        }

        if (MaxScroll > 0)
        {
            // A thin scroll thumb on the right edge.
            var track = ClientSize.Height;
            var thumb = Math.Max(LogicalToDeviceUnits(24), track * track / (MaxScroll + track));
            var top = (int)((track - thumb) * (scroll / (double)MaxScroll));
            using var brush = new SolidBrush(Theme.Border);
            g.FillRectangle(brush, ClientSize.Width - LogicalToDeviceUnits(3), top, LogicalToDeviceUnits(3), thumb);
        }
    }

    void DrawRow(Graphics g, int index, Rectangle row, IReadOnlyList<Segment> normalized)
    {
        var segment = segments[index];
        var selected = index == active;
        var hovered = index == hover;
        var fill = selected ? Theme.Mix(Theme.Surface, Theme.Accent, 0.28f) : hovered ? Theme.Lift(Theme.Background, 10) : Theme.Background;
        using (var path = PaintUtil.Rounded(new Rectangle(row.X, row.Y, row.Width - 1, row.Height - 1), LogicalToDeviceUnits(10)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(selected ? Theme.Accent : Theme.Border, selected ? 2 : 1))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        var badgeSize = LogicalToDeviceUnits(24);
        var badge = new Rectangle(row.Left + LogicalToDeviceUnits(10), row.Top + (row.Height - badgeSize) / 2, badgeSize, badgeSize);
        using (var badgeBrush = new SolidBrush(selected ? Theme.Accent : Theme.Surface))
            g.FillEllipse(badgeBrush, badge);
        TextRenderer.DrawText(g, (index + 1).ToString(), Theme.Ui(8.5f, FontStyle.Bold), badge, selected ? Color.White : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var textLeft = badge.Right + LogicalToDeviceUnits(10);
        var close = segments.Count > 1 ? CloseRect(row) : Rectangle.Empty;
        var textRight = (close.IsEmpty ? row.Right : close.Left) - LogicalToDeviceUnits(4);
        var half = row.Height / 2;
        var times = new Rectangle(textLeft, row.Top + LogicalToDeviceUnits(4), textRight - textLeft, half - LogicalToDeviceUnits(2));
        TextRenderer.DrawText(g, $"{TimeMath.Format(segment.In, fps)} → {TimeMath.Format(segment.Out, fps)}", Theme.Mono(9.5f), times, Theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        var at = CutList.EditedFrame(normalized, segment.In);
        var info = new Rectangle(textLeft, row.Top + half, textRight - textLeft, half - LogicalToDeviceUnits(4));
        TextRenderer.DrawText(g, $"{TimeMath.Format(segment.Length, fps)} long  ·  at {TimeMath.Format(at, fps)} in export", Theme.Ui(8.5f), info, Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!close.IsEmpty && (hovered || selected))
        {
            var color = hovered && hoverClose ? Theme.OutPoint : Theme.Muted;
            TextRenderer.DrawText(g, "×", Theme.Ui(13, FontStyle.Bold), close, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    (int Index, bool Close) HitAt(Point point)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var row = RowRect(i);
            if (!row.Contains(point)) continue;
            return (i, segments.Count > 1 && CloseRect(row).Contains(point));
        }

        return (-1, false);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var (index, close) = HitAt(e.Location);
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        if (index == hover && close == hoverClose) return;
        hover = index;
        hoverClose = close;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hover = -1;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        var (index, close) = HitAt(e.Location);
        if (index < 0) return;
        if (close) RemoveRequested?.Invoke(this, index);
        else SelectRequested?.Invoke(this, index);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var next = Math.Clamp(scroll - Math.Sign(e.Delta) * Stride, 0, MaxScroll);
        if (next == scroll) return;
        scroll = next;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        scroll = Math.Clamp(scroll, 0, MaxScroll);
    }
}

/// <summary>In and out of the selected segment: set at the playhead, type a timecode or frame, or nudge a frame.</summary>
sealed class SegmentEditor : Panel
{
    public FlatButton SetIn { get; }
    public FlatButton SetOut { get; }
    public DarkField InTime { get; }
    public DarkField InFrame { get; }
    public DarkField OutTime { get; }
    public DarkField OutFrame { get; }
    public FlatButton InEarlier { get; }
    public FlatButton InLater { get; }
    public FlatButton OutEarlier { get; }
    public FlatButton OutLater { get; }
    public Label Selected { get; }

    public SegmentEditor()
    {
        BackColor = Theme.Surface;
        SetIn = Edge("In", "Set in point", Theme.InPoint);
        SetOut = Edge("Out", "Set out point", Theme.OutPoint);
        InTime = Field("In timecode");
        InFrame = Field("In frame");
        OutTime = Field("Out timecode");
        OutFrame = Field("Out frame");
        InEarlier = Nudge("−", "Move in point one frame earlier");
        InLater = Nudge("+", "Move in point one frame later");
        OutEarlier = Nudge("−", "Move out point one frame earlier");
        OutLater = Nudge("+", "Move out point one frame later");
        Selected = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            Font = Theme.Ui(9.5f, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.AddRange([SetIn, InTime, InFrame, InEarlier, InLater, SetOut, OutTime, OutFrame, OutEarlier, OutLater, Selected]);
    }

    public int MeasureHeight() => LogicalToDeviceUnits(34) * 2 + LogicalToDeviceUnits(8) * 2 + LogicalToDeviceUnits(22);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var gap = LogicalToDeviceUnits(8);
        var rowHeight = LogicalToDeviceUnits(34);
        LayoutEdge(0, rowHeight, SetIn, InTime, InFrame, InEarlier, InLater);
        LayoutEdge(rowHeight + gap, rowHeight, SetOut, OutTime, OutFrame, OutEarlier, OutLater);
        Selected.SetBounds(0, (rowHeight + gap) * 2, ClientSize.Width, LogicalToDeviceUnits(22));
    }

    void LayoutEdge(int y, int height, FlatButton edge, DarkField time, DarkField frame, FlatButton earlier, FlatButton later)
    {
        var gap = LogicalToDeviceUnits(6);
        var nudge = LogicalToDeviceUnits(30);
        var edgeWidth = LogicalToDeviceUnits(48);
        var frameWidth = LogicalToDeviceUnits(74);
        later.SetBounds(ClientSize.Width - nudge, y, nudge, height);
        earlier.SetBounds(later.Left - gap - nudge, y, nudge, height);
        edge.SetBounds(0, y, edgeWidth, height);
        frame.SetBounds(earlier.Left - gap - frameWidth, y, frameWidth, height);
        time.SetBounds(edge.Right + gap, y, Math.Max(40, frame.Left - gap - edge.Right - gap), height);
    }

    static FlatButton Edge(string text, string name, Color color) => new()
    {
        Text = text,
        AccessibleName = name,
        TextColor = color,
        BackColor = Theme.Surface,
    };

    static DarkField Field(string name)
    {
        var field = new DarkField();
        field.Box.AccessibleName = name;
        return field;
    }

    static FlatButton Nudge(string text, string name) => new()
    {
        Text = text,
        AccessibleName = name,
        Font = Theme.Ui(12, FontStyle.Bold),
        BackColor = Theme.Surface,
    };
}

/// <summary>Fast copy or exact frames, the exact-mode presets, an explanation, and the Export button, stacked for the sidebar.</summary>
sealed class ExportBar : Panel
{
    public OptionCard Fast { get; }
    public OptionCard Exact { get; }
    public OptionCard QualityHigh { get; }
    public OptionCard QualityBalanced { get; }
    public OptionCard QualitySmall { get; }
    public OptionCard CopyAudio { get; }
    public FlatButton Export { get; }
    public FlatButton Cancel { get; }
    public Label Explanation { get; }

    bool exactMode;
    ExactQuality quality = ExactExportOptions.Default.Quality;
    bool copyAudio;
    int segments = 1;
    bool optionsEnabled = true;

    public ExportBar()
    {
        BackColor = Theme.Surface;
        Fast = Option(ExportModeCopy.FastTitle, ExportModeCopy.FastExplanation, visible: true);
        Exact = Option(ExportModeCopy.ExactTitle, ExportModeCopy.ExactBaseExplanation, visible: true);
        QualityHigh = Option(ExactExportPresets.Title(ExactQuality.High), ExactExportPresets.Hint(ExactQuality.High), visible: false);
        QualityBalanced = Option(ExactExportPresets.Title(ExactQuality.Balanced), ExactExportPresets.Hint(ExactQuality.Balanced), visible: false);
        QualitySmall = Option(ExactExportPresets.Title(ExactQuality.Small), ExactExportPresets.Hint(ExactQuality.Small), visible: false);
        CopyAudio = Option(ExportModeCopy.CopyAudioTitle, ExactExportPresets.AudioHint(true), visible: false);
        Export = new FlatButton { Text = "Export…", Primary = true, AccessibleName = "Export", Enabled = false, BackColor = Theme.Surface };
        Cancel = new FlatButton { Text = "Cancel", AccessibleName = "Cancel export", Enabled = false, Visible = false, TextColor = Theme.OutPoint, BackColor = Theme.Surface };
        Explanation = new Label
        {
            ForeColor = Theme.Muted,
            BackColor = Theme.Surface,
            Font = Theme.Ui(9),
            TextAlign = ContentAlignment.TopLeft,
        };
        Controls.AddRange([Fast, Exact, QualityHigh, QualityBalanced, QualitySmall, CopyAudio, Export, Cancel, Explanation]);

        QualityHigh.Click += (_, _) => SetQuality(ExactQuality.High);
        QualityBalanced.Click += (_, _) => SetQuality(ExactQuality.Balanced);
        QualitySmall.Click += (_, _) => SetQuality(ExactQuality.Small);
        CopyAudio.Click += (_, _) => SetCopyAudio(!copyAudio);

        SetMode(false);
    }

    static OptionCard Option(string title, string description, bool visible) => new()
    {
        Title = title,
        AccessibleDescription = description,
        Visible = visible,
        BackColor = Theme.Surface,
    };

    public ExactExportOptions ExactOptions => new(quality, copyAudio);

    public event EventHandler? ExactOptionsChanged;

    public void SetMode(bool exact)
    {
        exactMode = exact;
        Fast.Selected = !exact;
        Exact.Selected = exact;
        QualityHigh.Visible = exact;
        QualityBalanced.Visible = exact;
        QualitySmall.Visible = exact;
        CopyAudio.Visible = exact;
        SyncExactSelection();
        RefreshExplanation();
        PerformLayout();
    }

    public void SetExactOptionsEnabled(bool enabled)
    {
        optionsEnabled = enabled;
        QualityHigh.Enabled = enabled;
        QualityBalanced.Enabled = enabled;
        QualitySmall.Enabled = enabled;
        // A multi-segment re-encode joins audio in a filter graph, so it cannot be copied.
        CopyAudio.Enabled = enabled && segments == 1;
    }

    public void SetSegmentCount(int count)
    {
        if (segments == count) return;
        segments = count;
        SetExactOptionsEnabled(optionsEnabled);
        RefreshExplanation();
    }

    public int MeasureHeight(int width)
    {
        var gap = LogicalToDeviceUnits(8);
        var height = LogicalToDeviceUnits(36) + gap;
        if (exactMode) height += (LogicalToDeviceUnits(32) + gap) * 2;
        height += ExplanationHeight(width) + gap + LogicalToDeviceUnits(38);
        return height;
    }

    int ExplanationHeight(int width)
    {
        var size = TextRenderer.MeasureText(Explanation.Text, Explanation.Font, new Size(Math.Max(40, width), int.MaxValue), TextFormatFlags.WordBreak);
        return size.Height + LogicalToDeviceUnits(2);
    }

    void SetQuality(ExactQuality value)
    {
        if (!exactMode || quality == value) return;
        quality = value;
        SyncExactSelection();
        RefreshExplanation();
        ExactOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    void SetCopyAudio(bool value)
    {
        if (!exactMode || copyAudio == value || segments > 1) return;
        copyAudio = value;
        SyncExactSelection();
        RefreshExplanation();
        ExactOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    void SyncExactSelection()
    {
        QualityHigh.Selected = quality == ExactQuality.High;
        QualityBalanced.Selected = quality == ExactQuality.Balanced;
        QualitySmall.Selected = quality == ExactQuality.Small;
        CopyAudio.Selected = copyAudio;
        CopyAudio.AccessibleDescription = ExactExportPresets.AudioHint(copyAudio);
    }

    void RefreshExplanation()
    {
        Explanation.Text = ExportModeCopy.Explanation(exactMode, ExactOptions, segments);
        Explanation.AccessibleName = Explanation.Text;
        ExactOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var gap = LogicalToDeviceUnits(8);
        var width = ClientSize.Width;
        var half = (width - gap) / 2;
        var y = 0;
        Fast.SetBounds(0, y, half, LogicalToDeviceUnits(36));
        Exact.SetBounds(half + gap, y, width - half - gap, LogicalToDeviceUnits(36));
        y += LogicalToDeviceUnits(36) + gap;
        if (exactMode)
        {
            var row = LogicalToDeviceUnits(32);
            var cell = (width - gap * 2) / 3;
            QualityHigh.SetBounds(0, y, cell, row);
            QualityBalanced.SetBounds(cell + gap, y, cell, row);
            QualitySmall.SetBounds((cell + gap) * 2, y, width - (cell + gap) * 2, row);
            y += row + gap;
            CopyAudio.SetBounds(0, y, width, row);
            y += row + gap;
        }

        var explanation = ExplanationHeight(width);
        Explanation.SetBounds(0, y, width, explanation);
        y += explanation + gap;
        var buttonHeight = LogicalToDeviceUnits(38);
        if (Cancel.Visible)
        {
            var cancelWidth = LogicalToDeviceUnits(96);
            Cancel.SetBounds(width - cancelWidth, y, cancelWidth, buttonHeight);
            Export.SetBounds(0, y, Math.Max(40, Cancel.Left - gap), buttonHeight);
        }
        else
        {
            Export.SetBounds(0, y, width, buttonHeight);
        }
    }
}

/// <summary>Under the preview: jump to the previous cut, play the edit, jump to the next cut, the time readout, and zoom.</summary>
sealed class TransportBar : Panel
{
    public FlatButton Previous { get; }
    public FlatButton Play { get; }
    public FlatButton Next { get; }
    public Label Readout { get; }
    public FlatButton ZoomOut { get; }
    public FlatButton ZoomIn { get; }
    public FlatButton Fit { get; }
    public Label ZoomLabel { get; }

    public TransportBar()
    {
        BackColor = Theme.Background;
        Previous = Glyph("⏮", "Previous cut");
        Play = new FlatButton { Text = "▶", Primary = true, Font = Theme.Symbol(12), AccessibleName = "Play the edit", TabStop = false };
        Next = Glyph("⏭", "Next cut");
        Readout = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Text,
            Font = Theme.Mono(10),
            TextAlign = ContentAlignment.MiddleLeft,
            AccessibleName = "Position",
        };
        ZoomOut = Glyph("−", "Zoom out");
        ZoomIn = Glyph("+", "Zoom in");
        Fit = new FlatButton { Text = "Fit", AccessibleName = "Show the whole video", TabStop = false };
        ZoomLabel = new Label
        {
            ForeColor = Theme.Muted,
            Font = Theme.Ui(9),
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = "Zoom",
        };
        Controls.AddRange([Previous, Play, Next, Readout, ZoomOut, ZoomLabel, ZoomIn, Fit]);
    }

    static FlatButton Glyph(string text, string name) => new()
    {
        Text = text,
        Font = Theme.Symbol(11),
        AccessibleName = name,
        TabStop = false,
    };

    public void SetPlaying(bool playing)
    {
        Play.Text = playing ? "⏸" : "▶";
        Play.AccessibleName = playing ? "Pause" : "Play the edit";
        Play.Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var gap = LogicalToDeviceUnits(6);
        var height = ClientSize.Height;
        var button = LogicalToDeviceUnits(40);
        Previous.SetBounds(0, 0, button, height);
        Play.SetBounds(Previous.Right + gap, 0, LogicalToDeviceUnits(52), height);
        Next.SetBounds(Play.Right + gap, 0, button, height);
        var right = ClientSize.Width;
        Fit.SetBounds(right - LogicalToDeviceUnits(52), 0, LogicalToDeviceUnits(52), height);
        ZoomIn.SetBounds(Fit.Left - gap - LogicalToDeviceUnits(34), 0, LogicalToDeviceUnits(34), height);
        ZoomLabel.SetBounds(ZoomIn.Left - LogicalToDeviceUnits(56), 0, LogicalToDeviceUnits(56), height);
        ZoomOut.SetBounds(ZoomLabel.Left - LogicalToDeviceUnits(34), 0, LogicalToDeviceUnits(34), height);
        Readout.SetBounds(Next.Right + LogicalToDeviceUnits(14), 0, Math.Max(10, ZoomOut.Left - Next.Right - LogicalToDeviceUnits(24)), height);
    }
}
