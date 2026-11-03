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
        Controls.Add(Label);
        Controls.Add(Progress);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = LogicalToDeviceUnits(12);
        if (Progress.Visible)
        {
            var progressWidth = LogicalToDeviceUnits(160);
            var progressHeight = LogicalToDeviceUnits(8);
            Progress.SetBounds(ClientSize.Width - pad - progressWidth, Math.Max(0, (ClientSize.Height - progressHeight) / 2), progressWidth, progressHeight);
            Label.SetBounds(pad, 0, Math.Max(10, Progress.Left - pad * 2), ClientSize.Height);
        }
        else
        {
            Label.SetBounds(pad, 0, Math.Max(10, ClientSize.Width - pad * 2), ClientSize.Height);
        }
    }
}

sealed class PointsPanel : Panel
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

    public PointsPanel()
    {
        BackColor = Theme.Background;
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
            Font = Theme.Ui(10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.AddRange([SetIn, InTime, InFrame, InEarlier, InLater, SetOut, OutTime, OutFrame, OutEarlier, OutLater, Selected]);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var gap = LogicalToDeviceUnits(8);
        var rowHeight = Math.Max(LogicalToDeviceUnits(34), (ClientSize.Height - gap) / 2);
        LayoutEdge(0, rowHeight, SetIn, InTime, InFrame, InEarlier, InLater, null);
        LayoutEdge(rowHeight + gap, rowHeight, SetOut, OutTime, OutFrame, OutEarlier, OutLater, Selected);
    }

    void LayoutEdge(int y, int height, FlatButton edge, DarkField time, DarkField frame, FlatButton earlier, FlatButton later, Label? selected)
    {
        var gap = LogicalToDeviceUnits(8);
        var x = 0;
        edge.SetBounds(x, y, LogicalToDeviceUnits(56), height);
        x = edge.Right + gap;
        time.SetBounds(x, y, LogicalToDeviceUnits(118), height);
        x = time.Right + gap;
        frame.SetBounds(x, y, LogicalToDeviceUnits(88), height);
        x = frame.Right + gap;
        earlier.SetBounds(x, y, LogicalToDeviceUnits(34), height);
        x = earlier.Right + gap;
        later.SetBounds(x, y, LogicalToDeviceUnits(34), height);
        if (selected is null) return;
        x = later.Right + gap;
        selected.SetBounds(x, y, Math.Max(0, ClientSize.Width - x), height);
    }

    static FlatButton Edge(string text, string name, Color color) => new()
    {
        Text = text,
        AccessibleName = name,
        TextColor = color,
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
    };
}

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

    bool narrow;
    bool exactMode;
    ExactQuality quality = ExactExportOptions.Default.Quality;
    bool copyAudio;

    public ExportBar()
    {
        BackColor = Theme.Background;
        Fast = new OptionCard { Title = ExportModeCopy.FastTitle, AccessibleDescription = ExportModeCopy.FastExplanation };
        Exact = new OptionCard { Title = ExportModeCopy.ExactTitle, AccessibleDescription = ExportModeCopy.ExactBaseExplanation };
        QualityHigh = new OptionCard { Title = ExactExportPresets.Title(ExactQuality.High), AccessibleDescription = ExactExportPresets.Hint(ExactQuality.High), Visible = false };
        QualityBalanced = new OptionCard { Title = ExactExportPresets.Title(ExactQuality.Balanced), AccessibleDescription = ExactExportPresets.Hint(ExactQuality.Balanced), Visible = false };
        QualitySmall = new OptionCard { Title = ExactExportPresets.Title(ExactQuality.Small), AccessibleDescription = ExactExportPresets.Hint(ExactQuality.Small), Visible = false };
        CopyAudio = new OptionCard { Title = ExportModeCopy.CopyAudioTitle, AccessibleDescription = ExactExportPresets.AudioHint(true), Visible = false };
        Export = new FlatButton { Text = "Export…", Primary = true, AccessibleName = "Export", Enabled = false };
        Cancel = new FlatButton { Text = "Cancel", AccessibleName = "Cancel export", Enabled = false, Visible = false, TextColor = Theme.OutPoint };
        Explanation = new Label
        {
            AutoEllipsis = true,
            ForeColor = Theme.Muted,
            Font = Theme.Ui(9),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(Fast);
        Controls.Add(Exact);
        Controls.Add(QualityHigh);
        Controls.Add(QualityBalanced);
        Controls.Add(QualitySmall);
        Controls.Add(CopyAudio);
        Controls.Add(Export);
        Controls.Add(Cancel);
        Controls.Add(Explanation);

        QualityHigh.Click += (_, _) => SetQuality(ExactQuality.High);
        QualityBalanced.Click += (_, _) => SetQuality(ExactQuality.Balanced);
        QualitySmall.Click += (_, _) => SetQuality(ExactQuality.Small);
        CopyAudio.Click += (_, _) => SetCopyAudio(!copyAudio);

        SetMode(false);
    }

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
        QualityHigh.Enabled = enabled;
        QualityBalanced.Enabled = enabled;
        QualitySmall.Enabled = enabled;
        CopyAudio.Enabled = enabled;
    }

    public int MeasureHeight(bool narrowLayout)
    {
        var gap = LogicalToDeviceUnits(8);
        var explanation = LogicalToDeviceUnits(32);
        if (!narrowLayout)
        {
            var height = LogicalToDeviceUnits(40) + LogicalToDeviceUnits(4) + explanation;
            if (exactMode) height += gap + LogicalToDeviceUnits(32);
            return height;
        }

        var heightNarrow = LogicalToDeviceUnits(36) + gap + LogicalToDeviceUnits(36) + gap + LogicalToDeviceUnits(36) + LogicalToDeviceUnits(4) + explanation;
        if (exactMode) heightNarrow += gap + LogicalToDeviceUnits(32);
        return heightNarrow;
    }

    public void Arrange(bool narrowLayout)
    {
        narrow = narrowLayout;
        PerformLayout();
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
        if (!exactMode || copyAudio == value) return;
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
        Explanation.Text = ExportModeCopy.Explanation(exactMode, ExactOptions);
        Explanation.AccessibleName = Explanation.Text;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var gap = LogicalToDeviceUnits(8);
        var buttonWidth = LogicalToDeviceUnits(132);
        var cancelWidth = LogicalToDeviceUnits(96);
        var buttonHeight = LogicalToDeviceUnits(36);
        var optionsHeight = LogicalToDeviceUnits(32);
        if (!narrow)
        {
            var rowHeight = LogicalToDeviceUnits(40);
            var right = ClientSize.Width;
            Export.SetBounds(Math.Max(0, right - buttonWidth), 0, buttonWidth, buttonHeight);
            right = Export.Left - gap;
            if (Cancel.Visible)
            {
                Cancel.SetBounds(Math.Max(0, right - cancelWidth), 0, cancelWidth, buttonHeight);
                right = Cancel.Left - gap;
            }

            var cardWidth = Math.Max(40, (right - gap) / 2);
            Fast.SetBounds(0, 0, cardWidth, rowHeight);
            Exact.SetBounds(cardWidth + gap, 0, Math.Max(40, right - cardWidth - gap), rowHeight);
            var y = rowHeight + LogicalToDeviceUnits(4);
            if (exactMode)
            {
                LayoutExactOptions(0, y, ClientSize.Width, optionsHeight, gap);
                y += optionsHeight + gap;
            }

            Explanation.SetBounds(0, y, ClientSize.Width, Math.Max(0, ClientSize.Height - y));
            return;
        }

        var cardHeight = LogicalToDeviceUnits(36);
        Fast.SetBounds(0, 0, ClientSize.Width, cardHeight);
        Exact.SetBounds(0, cardHeight + gap, ClientSize.Width, cardHeight);
        var row = Exact.Bottom + gap;
        Export.SetBounds(Math.Max(0, ClientSize.Width - buttonWidth), row, buttonWidth, buttonHeight);
        Cancel.SetBounds(Math.Max(0, Export.Left - gap - cancelWidth), row, cancelWidth, buttonHeight);
        row = Export.Bottom + LogicalToDeviceUnits(4);
        if (exactMode)
        {
            LayoutExactOptions(0, row, ClientSize.Width, optionsHeight, gap);
            row += optionsHeight + gap;
        }

        Explanation.SetBounds(0, row, ClientSize.Width, Math.Max(0, ClientSize.Height - row));
    }

    void LayoutExactOptions(int x, int y, int width, int height, int gap)
    {
        var audioWidth = LogicalToDeviceUnits(120);
        var qualityWidth = Math.Max(40, width - audioWidth - gap);
        var cell = Math.Max(40, (qualityWidth - gap * 2) / 3);
        QualityHigh.SetBounds(x, y, cell, height);
        QualityBalanced.SetBounds(QualityHigh.Right + gap, y, cell, height);
        QualitySmall.SetBounds(QualityBalanced.Right + gap, y, Math.Max(40, qualityWidth - cell * 2 - gap * 2), height);
        CopyAudio.SetBounds(x + qualityWidth + gap, y, Math.Max(40, width - qualityWidth - gap), height);
    }
}
