namespace VideoTrimmer;

/// <summary>A dedicated status strip, outside the draggable window caption.</summary>
sealed class WorkStatus : GlassControl
{
    public GlassLabel Message { get; } = new() { Font = Theme.Ui(9.5f), AccessibleName = "Status" };
    readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1000, AccessibleName = "Operation progress", TabStop = false };
    readonly GlassLabel percent = new() { Font = Theme.Ui(9), Align = ContentAlignment.MiddleRight };
    bool busy;
    double progress = -1;

    public WorkStatus() => Controls.AddRange([Message, bar, percent]);

    public double Progress
    {
        get => progress;
        set
        {
            progress = value;
            bar.Style = value < 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (value >= 0) bar.Value = (int)(Math.Clamp(value, 0, 1) * 1000);
            percent.Text = value < 0 ? "" : $"{Math.Clamp(value, 0, 1):P0}";
            percent.AccessibleName = "Progress " + percent.Text;
        }
    }

    public void SetStatus(string text, StatusKind kind)
    {
        Message.Text = text;
        Message.AccessibleName = text;
        Message.ForeColor = kind == StatusKind.Error ? Theme.Caution : Theme.Text;
        busy = kind == StatusKind.Busy;
        bar.Visible = percent.Visible = busy;
        if (!busy) Progress = -1;
        PerformLayout();
        Invalidate();
        if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = S(14);
        percent.SetBounds(Width - pad - S(48), 0, S(48), Height);
        bar.SetBounds(percent.Left - S(10) - S(200), (Height - S(10)) / 2, S(200), S(10));
        Message.SetBounds(pad, 0, Math.Max(0, (busy ? bar.Left - S(18) : Width - pad) - pad), Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintBase(e.Graphics);
        using var line = new Pen(Theme.Hairline);
        e.Graphics.DrawLine(line, 0, 0, Width, 0);
    }
}
