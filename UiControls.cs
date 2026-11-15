using System.Drawing.Drawing2D;

namespace VideoTrimmer;

/// <summary>
/// A control that paints the window's backdrop under itself, so it looks see-through without any real transparency.
/// One that sits on a glass panel adds the panel's tint, which makes it blend into the panel around it.
/// </summary>
abstract class GlassControl : Control
{
    protected const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
        | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

    static SolidBrush PanelBrush = new(Theme.PanelFill);

    static GlassControl() => Theme.Changed += () =>
    {
        var old = PanelBrush;
        PanelBrush = new SolidBrush(Theme.PanelFill);
        old.Dispose();
    };

    protected GlassControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ForeColor = Theme.Text;
        BackColor = Theme.Base;
    }

    /// <summary>True for a child of a glass panel.</summary>
    public bool OnPanel { get; set; }

    /// <summary>
    /// Lets the pointer fall through to the window, which treats the spot as part of its title bar: empty parts of the top
    /// bar, and the maximize button, which Windows must see as one for snap layouts to open over it.
    /// </summary>
    public bool PartOfCaption { get; set; }

    protected override void WndProc(ref Message m)
    {
        if (PartOfCaption && m.Msg == WindowChrome.WM_NCHITTEST)
        {
            m.Result = WindowChrome.HTTRANSPARENT;
            return;
        }

        base.WndProc(ref m);
    }

    protected void PaintBase(Graphics g)
    {
        Backdrop.Paint(g, this);
        if (OnPanel) g.FillRectangle(PanelBrush, ClientRectangle);
    }

    protected int S(int logical) => LogicalToDeviceUnits(logical);

    protected static int TextWidth(string text, Font font) =>
        text.Length == 0 ? 0 : TextRenderer.MeasureText(text, font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
}

/// <summary>Painted text. A stock Label cannot show the backdrop through itself.</summary>
sealed class GlassLabel : GlassControl
{
    public GlassLabel()
    {
        Font = Theme.Ui(9.5f);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
    }

    public ContentAlignment Align { get; set; } = ContentAlignment.MiddleLeft;
    /// <summary>Wraps onto several lines from the top, for the export explanation.</summary>
    public bool Wrap { get; set; }

    public int MeasureWidth() => TextWidth(Text, Font);

    public int MeasureWidth(string text) => TextWidth(text, Font);

    public int MeasureHeight(int width) => TextRenderer.MeasureText(Text, Font, new Size(Math.Max(40, width), int.MaxValue),
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak).Height;

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnForeColorChanged(EventArgs e) { Invalidate(); base.OnForeColorChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintBase(e.Graphics);
        if (Text.Length == 0) return;
        var flags = Wrap
            ? TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak
            : Line | Align switch
            {
                ContentAlignment.MiddleRight => TextFormatFlags.Right,
                ContentAlignment.MiddleCenter => TextFormatFlags.HorizontalCenter,
                _ => TextFormatFlags.Left,
            };
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, flags);
    }
}

enum ButtonKind { Primary, Glass, Quiet, Caution }

/// <summary>
/// The one button, in four weights: Primary (one per window), Glass (a faint filled control), Quiet (bare until hovered,
/// for the dock's tools) and Caution (cancel and remove).
/// </summary>
sealed class GlassButton : GlassControl, IButtonControl
{
    bool hover;
    bool pressed;
    double progress = -1;

    public GlassButton()
    {
        SetStyle(ControlStyles.Selectable, true);
        Font = Theme.Ui(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleDefaultActionDescription = "Press";
        Size = new Size(120, 36);
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ButtonAccessibleObject(this);

    public ButtonKind Kind { get; set; } = ButtonKind.Glass;
    public Icon Icon { get; set; }
    /// <summary>A small key cap after the label, such as S for Split.</summary>
    public string? KeyHint { get; set; }
    /// <summary>Muted text after the label of a left-aligned button.</summary>
    public string Detail { get; set; } = "";
    public bool AlignLeft { get; set; }
    /// <summary>A chevron at the right edge: the button opens a menu.</summary>
    public bool Chevron { get; set; }
    /// <summary>A dot in the corner: something is waiting behind this button.</summary>
    public bool Dot { get; set; }
    /// <summary>Icon only, when there is no room for the label.</summary>
    public bool Compact { get; set; }
    public DialogResult DialogResult { get; set; }

    /// <summary>The hover fill of a Quiet button, when it is not the usual one (red for the close button).</summary>
    public Color? HoverFill { get; set; }

    /// <summary>Hover and press for a button whose pointer events go to the window instead (the maximize button).</summary>
    public void ShowPointer(bool hovered, bool down)
    {
        if (hover == hovered && pressed == down) return;
        hover = hovered;
        pressed = down;
        Invalidate();
    }

    /// <summary>0 to 1 fills the button as a progress bar; negative shows it as a normal button.</summary>
    public double Progress
    {
        get => progress;
        set { progress = value; Invalidate(); }
    }

    public void NotifyDefault(bool value) { }

    public void PerformClick()
    {
        if (!Enabled) return;
        OnClick(EventArgs.Empty);
    }

    bool ShowsText => Text.Length > 0 && !Compact;
    bool ShowsHint => !string.IsNullOrEmpty(KeyHint) && !Compact;
    Font HintFont => Theme.Ui(8);
    int IconSize => S(16);

    /// <summary>The width that fits the icon, label and key hint.</summary>
    public int PreferredWidth()
    {
        var content = ContentWidth();
        return ShowsText ? content + S(12) * 2 : Math.Max(S(36), content + S(10) * 2);
    }

    int ContentWidth()
    {
        var width = 0;
        if (Icon != Icon.None) width += IconSize;
        if (ShowsText) width += (width > 0 ? S(8) : 0) + TextWidth(Text, Font);
        if (ShowsHint) width += S(8) + TextWidth(KeyHint!, HintFont) + S(10);
        return width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        var radius = S(Kind is ButtonKind.Primary or ButtonKind.Quiet ? 10 : 8);
        var text = ForeColor;
        if (progress >= 0)
        {
            PaintUtil.Fill(g, rect, radius, Theme.ControlFill);
            var state = g.Save();
            using (var clip = PaintUtil.Rounded(rect, radius)) g.SetClip(clip, CombineMode.Intersect);
            using (var fill = new SolidBrush(Theme.Alpha(Theme.Accent, 150)))
                g.FillRectangle(fill, 0, 0, (float)(Width * Math.Clamp(progress, 0, 1)), Height);
            g.Restore(state);
            PaintUtil.Stroke(g, rect, radius, Theme.ControlBorder);
        }
        else if (!Enabled)
        {
            if (Kind != ButtonKind.Quiet) PaintUtil.Stroke(g, rect, radius, Theme.Hairline);
            text = Theme.Faint;
        }
        else if (Kind == ButtonKind.Primary)
        {
            var fill = pressed ? Theme.Darken(Theme.Accent, 26) : hover ? Theme.Lift(Theme.Accent, 16) : Theme.Accent;
            PaintUtil.Fill(g, rect, radius, fill);
            if (!pressed)
            {
                using var shine = new Pen(Color.FromArgb(60, 255, 255, 255));
                g.DrawLine(shine, rect.Left + radius, rect.Top + 1, rect.Right - radius, rect.Top + 1);
            }

            text = Theme.OnAccent;
        }
        else if (Kind == ButtonKind.Quiet)
        {
            if (pressed) PaintUtil.Fill(g, rect, radius, HoverFill is { } down ? Theme.Darken(down, 20) : Theme.ControlPressed);
            else if (hover) PaintUtil.Fill(g, rect, radius, HoverFill ?? Theme.ControlHover);
            if ((pressed || hover) && HoverFill is not null) text = Color.White;
        }
        else
        {
            var caution = Kind == ButtonKind.Caution;
            var fill = pressed ? Theme.ControlPressed : hover ? (caution ? Theme.Alpha(Theme.Caution, 36) : Theme.ControlHover) : Theme.ControlFill;
            PaintUtil.Fill(g, rect, radius, fill);
            PaintUtil.Stroke(g, rect, radius, caution && (hover || pressed) ? Theme.Alpha(Theme.Caution, 115) : Theme.ControlBorder);
            if (caution) text = Theme.Caution;
        }

        var right = Width - (Chevron ? S(8) + S(14) + S(4) : AlignLeft ? S(10) : S(4));
        var x = AlignLeft ? S(10) : Math.Max(S(4), (Width - ContentWidth()) / 2);
        if (Icon != Icon.None)
        {
            Icons.Draw(g, Icon, new RectangleF(x, (Height - IconSize) / 2f, IconSize, IconSize), text);
            x += IconSize + S(8);
        }

        if (ShowsText)
        {
            var width = Math.Min(TextWidth(Text, Font), Math.Max(0, right - x));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, width, Height), text, Line | TextFormatFlags.Left);
            x += width + S(8);
        }

        if (ShowsHint && x < right)
        {
            var chipWidth = TextWidth(KeyHint!, HintFont) + S(10);
            var chipHeight = S(18);
            var chip = new Rectangle(x, (Height - chipHeight) / 2, chipWidth, chipHeight);
            PaintUtil.Stroke(g, new RectangleF(chip.X + 0.5f, chip.Y + 0.5f, chip.Width - 1, chip.Height - 1), S(4), Theme.Outline);
            TextRenderer.DrawText(g, KeyHint, HintFont, chip, Enabled ? Theme.Muted : Theme.Faint, Line | TextFormatFlags.HorizontalCenter);
        }
        else if (AlignLeft && Detail.Length > 0 && x < right)
        {
            TextRenderer.DrawText(g, Detail, Theme.Ui(9), new Rectangle(x, 0, right - x, Height), Enabled ? Theme.Muted : Theme.Faint, Line | TextFormatFlags.Left);
        }

        if (Chevron)
            Icons.Draw(g, Icon.Chevron, new RectangleF(Width - S(8) - S(14), (Height - S(14)) / 2f, S(14), S(14)), Enabled ? Theme.Muted : Theme.Faint);
        if (Dot)
        {
            using var dot = new SolidBrush(Theme.Accent);
            g.FillEllipse(dot, Width - S(11), S(4), S(7), S(7));
        }

        if (Focused && ShowFocusCues) PaintUtil.FocusRing(g, ClientRectangle, radius);
    }

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        base.OnEnabledChanged(e);
    }

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

    sealed class ButtonAccessibleObject : ControlAccessibleObject
    {
        readonly GlassButton owner;

        public ButtonAccessibleObject(GlassButton owner) : base(owner) => this.owner = owner;

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

/// <summary>
/// Pick one of a few cells. Left and Right move the choice, and a screen reader sees one radio button per item.
/// Subclasses lay out the cells and paint them.
/// </summary>
abstract class Picker : GlassControl
{
    protected string[] items = [];
    protected int selected;
    protected int hover = -1;

    protected Picker()
    {
        SetStyle(ControlStyles.Selectable, true);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Grouping;
    }

    /// <summary>Raised when the user picks a cell; <see cref="SelectedIndex"/> is not changed for them.</summary>
    public event Action<int>? Picked;

    public string[] Items
    {
        get => items;
        set { items = value; Invalidate(); }
    }

    public int SelectedIndex
    {
        get => selected;
        set
        {
            var changed = selected != value;
            selected = value;
            AccessibleDescription = value >= 0 && value < items.Length ? items[value] + " selected" : "";
            Invalidate();
            if (changed && IsHandleCreated && value >= 0 && value < items.Length)
            {
                AccessibilityNotifyClients(AccessibleEvents.StateChange, value);
                AccessibilityNotifyClients(AccessibleEvents.Selection, value);
            }
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new PickerAccessibleObject(this);

    protected abstract RectangleF Cell(int index);

    int HitAt(Point point)
    {
        for (var i = 0; i < items.Length; i++)
            if (Cell(i).Contains(point)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitAt(e.Location);
        if (index == hover) return;
        hover = index;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left || !Enabled) return;
        var index = HitAt(e.Location);
        if (index >= 0 && index != selected) Picked?.Invoke(index);
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var next = e.KeyCode switch { Keys.Left => selected - 1, Keys.Right => selected + 1, _ => -1 };
        if (next >= 0 && next < items.Length)
        {
            Picked?.Invoke(next);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>The control as a group with one radio button per item, so a screen reader can list and pick them.</summary>
    sealed class PickerAccessibleObject : ControlAccessibleObject
    {
        readonly Picker owner;
        ItemAccessibleObject[] children = [];

        public PickerAccessibleObject(Picker owner) : base(owner) => this.owner = owner;

        public override AccessibleRole Role => AccessibleRole.Grouping;
        public override string? Name => owner.AccessibleName;

        ItemAccessibleObject[] Children()
        {
            if (children.Length != owner.items.Length)
                children = owner.items.Select((_, i) => new ItemAccessibleObject(this, owner, i)).ToArray();
            return children;
        }

        public override int GetChildCount() => owner.items.Length;

        public override AccessibleObject? GetChild(int index) => index >= 0 && index < owner.items.Length ? Children()[index] : null;

        public override AccessibleObject? GetFocused() => owner.Focused ? GetChild(owner.selected) : null;

        public override AccessibleObject? GetSelected() => GetChild(owner.selected);

        public override AccessibleObject? HitTest(int x, int y)
        {
            var index = owner.HitAt(owner.PointToClient(new Point(x, y)));
            return index >= 0 ? GetChild(index) : base.HitTest(x, y);
        }
    }

    sealed class ItemAccessibleObject : AccessibleObject
    {
        readonly PickerAccessibleObject parent;
        readonly Picker owner;
        readonly int index;

        public ItemAccessibleObject(PickerAccessibleObject parent, Picker owner, int index)
        {
            this.parent = parent;
            this.owner = owner;
            this.index = index;
        }

        public override AccessibleObject Parent => parent;
        public override AccessibleRole Role => AccessibleRole.RadioButton;
        public override string Name => index < owner.items.Length ? owner.items[index] : "";
        public override string DefaultAction => "Select";
        public override Rectangle Bounds => owner.RectangleToScreen(Rectangle.Round(owner.Cell(index)));

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Focusable | AccessibleStates.Selectable;
                if (index == owner.selected) state |= AccessibleStates.Checked | AccessibleStates.Selected;
                if (index == owner.selected && owner.Focused) state |= AccessibleStates.Focused;
                if (!owner.Enabled) state |= AccessibleStates.Unavailable;
                if (!owner.Visible) state |= AccessibleStates.Invisible;
                return state;
            }
        }

        public override AccessibleObject? Navigate(AccessibleNavigation direction) => direction switch
        {
            AccessibleNavigation.Next or AccessibleNavigation.Right => parent.GetChild(index + 1),
            AccessibleNavigation.Previous or AccessibleNavigation.Left => parent.GetChild(index - 1),
            _ => base.Navigate(direction),
        };

        public override void DoDefaultAction()
        {
            if (!owner.Enabled || !owner.IsHandleCreated || index == owner.selected) return;
            try
            {
                owner.BeginInvoke(new Action(() => owner.Picked?.Invoke(index)));
            }
            catch (InvalidOperationException)
            {
                // The window closed between the check and the post.
            }
        }

        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection)) != 0) DoDefaultAction();
        }
    }
}

/// <summary>Pick one of a few words: a dark track with the chosen cell raised.</summary>
sealed class Segmented : Picker
{
    public Segmented() => Font = Theme.Ui(9.5f);

    protected override RectangleF Cell(int index)
    {
        var inset = S(3);
        var width = (Width - inset * 2) / (float)Math.Max(1, items.Length);
        return new RectangleF(inset + width * index, inset, width, Height - inset * 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        PaintUtil.Fill(g, track, S(10), Theme.Well);
        PaintUtil.Stroke(g, track, S(10), Theme.WellBorder);
        for (var i = 0; i < items.Length; i++)
        {
            var cell = Cell(i);
            var on = i == selected;
            if (on) PaintUtil.Fill(g, cell, S(7), Enabled ? Theme.Selected : Theme.Hairline);
            var color = !Enabled ? Theme.Faint : on || i == hover ? Theme.Text : Theme.Muted;
            TextRenderer.DrawText(g, items[i], on ? Theme.Ui(Font.SizeInPoints, FontStyle.Bold) : Font, Rectangle.Round(cell), color, Line | TextFormatFlags.HorizontalCenter);
        }

        if (Focused && ShowFocusCues) PaintUtil.FocusRing(g, ClientRectangle, S(10));
    }
}

/// <summary>
/// The accent colors as a row of round swatches, each drawn as the current theme shows it, with the chosen one ringed
/// and named after the row.
/// </summary>
sealed class Swatches : Picker
{
    public Swatches()
    {
        Font = Theme.Ui(9.5f);
        items = Theme.Tints.Select(tint => tint.Name).ToArray();
    }

    int Swatch => S(30);

    protected override RectangleF Cell(int index) => new(index * (Swatch + S(8)), (Height - Swatch) / 2f, Swatch, Swatch);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (var i = 0; i < items.Length; i++)
        {
            var cell = Cell(i);
            var tint = Theme.Tints[i];
            var color = Theme.IsDark ? tint.Dark : tint.LightFill;
            using (var brush = new SolidBrush(color))
                g.FillEllipse(brush, RectangleF.Inflate(cell, -S(5), -S(5)));
            if (i == selected || i == hover)
            {
                using var ring = new Pen(i == selected ? Theme.Text : Theme.OutlineStrong, S(2));
                g.DrawEllipse(ring, RectangleF.Inflate(cell, -S(1), -S(1)));
            }

            if (i == selected && Focused && ShowFocusCues)
            {
                using var focus = new Pen(Theme.Text, 1) { DashStyle = DashStyle.Dot };
                g.DrawEllipse(focus, RectangleF.Inflate(cell, S(2), S(2)));
            }
        }

        var left = (int)Cell(items.Length - 1).Right + S(12);
        if (selected >= 0 && selected < items.Length)
            TextRenderer.DrawText(g, items[selected], Font, new Rectangle(left, 0, Math.Max(1, Width - left), Height), Theme.Muted, Line | TextFormatFlags.Left);
    }
}

/// <summary>A labelled on/off switch; the whole row is the target.</summary>
sealed class ToggleSwitch : GlassControl
{
    bool on;
    bool hover;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.Selectable, true);
        Font = Theme.Ui(9.5f);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    /// <summary>Raised when the user flips the switch; <see cref="Checked"/> is not changed for them.</summary>
    public event Action<bool>? Toggled;

    public bool Checked
    {
        get => on;
        set { on = value; Invalidate(); }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new SwitchAccessibleObject(this);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var width = S(34);
        var height = S(20);
        var track = new RectangleF(Width - width - 0.5f, (Height - height) / 2f, width, height);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Math.Max(1, (int)track.Left - S(8)), Height), Enabled ? Theme.Text : Theme.Faint, Line | TextFormatFlags.Left);
        var active = on && Enabled;
        PaintUtil.Fill(g, track, height / 2f, active ? Theme.Accent : Theme.Well);
        if (!active) PaintUtil.Stroke(g, track, height / 2f, hover && Enabled ? Theme.OutlineStrong : Theme.Outline);
        var knob = S(12);
        var inset = (height - knob) / 2f;
        using var brush = new SolidBrush(active ? Theme.OnAccent : Enabled ? Theme.Muted : Theme.Faint);
        g.FillEllipse(brush, on ? track.Right - inset - knob : track.Left + inset, track.Top + inset, knob, knob);
        if (Focused && ShowFocusCues) PaintUtil.FocusRing(g, Rectangle.Round(RectangleF.Inflate(track, S(3), S(3))), S(12));
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && Enabled) Toggled?.Invoke(!on);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            Toggled?.Invoke(!on);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    sealed class SwitchAccessibleObject : ControlAccessibleObject
    {
        readonly ToggleSwitch owner;

        public SwitchAccessibleObject(ToggleSwitch owner) : base(owner) => this.owner = owner;

        public override AccessibleRole Role => AccessibleRole.CheckButton;
        public override string Name => owner.AccessibleName ?? owner.Text;
        public override string DefaultAction => owner.Checked ? "Turn off" : "Turn on";

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Focusable;
                if (owner.Focused) state |= AccessibleStates.Focused;
                if (owner.Checked) state |= AccessibleStates.Checked;
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
                owner.BeginInvoke(new Action(() => owner.Toggled?.Invoke(!owner.Checked)));
            }
            catch (InvalidOperationException)
            {
                // The window closed between the check and the post.
            }
        }
    }
}

/// <summary>A text box in a rounded well; the border takes the accent while it has focus.</summary>
sealed class DarkField : GlassControl
{
    public TextBox Box { get; }

    public DarkField()
    {
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Well,
            ForeColor = Theme.Text,
            Font = Theme.Mono(10),
        };
        Box.GotFocus += (_, _) => { Invalidate(); Box.SelectAll(); };
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var y = Math.Max(1, (ClientSize.Height - Box.PreferredHeight) / 2);
        Box.SetBounds(S(8), y, Math.Max(10, ClientSize.Width - S(16)), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        PaintUtil.Fill(g, rect, S(8), Theme.Well);
        PaintUtil.Stroke(g, rect, S(8), Box.Focused ? Theme.AccentInk : Theme.WellBorder);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Box.Focus();
    }
}

/// <summary>The empty window: a glass card that takes a dropped video or opens the file picker.</summary>
sealed class DropPrompt : GlassControl
{
    public GlassButton Open { get; }
    public event EventHandler? OpenRequested;

    public DropPrompt()
    {
        AccessibleName = "Drop a video to trim";
        Cursor = Cursors.Hand;
        Open = new GlassButton { Text = "Open a video", Kind = ButtonKind.Primary, AccessibleName = "Open a video", OnPanel = true };
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
        Open.SetBounds(Math.Max(0, (ClientSize.Width - S(180)) / 2), BlockTop() + S(84), S(180), S(40));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (ClientSize.Width < 20 || ClientSize.Height < 20) return;
        PaintUtil.Panel(g, ClientRectangle, S(16));
        var top = BlockTop();
        TextRenderer.DrawText(g, "Drop a video to trim", Theme.Ui(20, FontStyle.Bold), new Rectangle(0, top, ClientSize.Width, S(36)), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "mkv, mp4, mov, avi, webm, ts, m2ts, wmv", Theme.Ui(10), new Rectangle(S(8), top + S(40), ClientSize.Width - S(16), S(24)), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    int BlockTop() => Math.Max(S(16), (ClientSize.Height - S(140)) / 2);
}

enum StatusKind { Info, Busy, Good, Error }

/// <summary>
/// The window's title bar: the file and its details on the left, the status in the middle, Open and the ⋯ menu, then
/// minimize, maximize and close on the right. It replaces the native caption, the old toolbar and the status bar.
/// Its empty parts and its labels drag the window.
/// </summary>
sealed class TopBar : GlassControl
{
    public GlassLabel FileName { get; }
    public GlassLabel Meta { get; }
    public GlassLabel Status { get; }
    public GlassButton Open { get; }
    public GlassButton More { get; }
    public GlassButton Minimize { get; }
    public GlassButton Maximize { get; }
    public GlassButton Close { get; }

    StatusKind statusKind;
    Rectangle dotRect;

    public TopBar()
    {
        FileName = new GlassLabel { Text = "VideoTrimmer", Font = Theme.Ui(10, FontStyle.Bold), AccessibleName = "File name" };
        Meta = new GlassLabel { ForeColor = Theme.Muted, Font = Theme.Ui(9), AccessibleName = "Video details" };
        Status = new GlassLabel { ForeColor = Theme.Muted, Font = Theme.Ui(9), Align = ContentAlignment.MiddleRight, AccessibleName = "Status" };
        Open = new GlassButton { Text = "Open", AccessibleName = "Open a video" };
        More = new GlassButton { Kind = ButtonKind.Quiet, Icon = Icon.More, AccessibleName = "More: shortcuts, updates, about" };
        Minimize = Caption(Icon.Minimize, "Minimize");
        Maximize = Caption(Icon.Maximize, "Maximize");
        Maximize.PartOfCaption = true;
        Close = Caption(Icon.Close, "Close");
        Close.HoverFill = Color.FromArgb(196, 43, 28);
        Controls.AddRange([FileName, Meta, Status, Open, More, Minimize, Maximize, Close]);
        PartOfCaption = true;
        foreach (var label in new[] { FileName, Meta, Status })
            WindowChrome.ActAsCaption(label);
    }

    static GlassButton Caption(Icon icon, string name) => new() { Kind = ButtonKind.Quiet, Icon = icon, AccessibleName = name, TabStop = false };

    public void SetMaximized(bool maximized)
    {
        Maximize.Icon = maximized ? Icon.Restore : Icon.Maximize;
        Maximize.AccessibleName = maximized ? "Restore" : "Maximize";
        Maximize.Invalidate();
    }

    public void SetStatus(string text, StatusKind kind)
    {
        Status.Text = text;
        Status.AccessibleName = text;
        Status.ForeColor = kind switch
        {
            StatusKind.Error => Theme.Caution,
            StatusKind.Busy or StatusKind.Good => Theme.Text,
            _ => Theme.Muted,
        };
        statusKind = kind;
        PerformLayout();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = S(14);
        var height = S(30);
        var y = (ClientSize.Height - height) / 2;
        var caption = S(40);
        Close.SetBounds(ClientSize.Width - S(6) - caption, y, caption, height);
        Maximize.SetBounds(Close.Left - S(2) - caption, y, caption, height);
        Minimize.SetBounds(Maximize.Left - S(2) - caption, y, caption, height);
        More.SetBounds(Minimize.Left - S(14) - S(34), y, S(34), height);
        var openWidth = Open.PreferredWidth();
        Open.SetBounds(More.Left - S(6) - openWidth, y, openWidth, height);

        var left = pad + S(18) + S(10);
        var room = Math.Max(S(40), Open.Left - S(14) - left);
        var statusWidth = Math.Min(Status.MeasureWidth() + S(2), Math.Max(S(60), room * 45 / 100));
        Status.SetBounds(Open.Left - S(14) - statusWidth, 0, statusWidth, ClientSize.Height - 1);
        dotRect = new Rectangle(Status.Left - S(13), (ClientSize.Height - S(6)) / 2, S(6), S(6));

        var textRoom = Math.Max(S(20), dotRect.Left - S(14) - left);
        var nameWidth = Math.Min(FileName.MeasureWidth() + S(2), Meta.Text.Length > 0 ? textRoom * 60 / 100 : textRoom);
        FileName.SetBounds(left, 0, nameWidth, ClientSize.Height - 1);
        Meta.SetBounds(FileName.Right + S(12), 0, Math.Max(0, left + textRoom - FileName.Right - S(12)), ClientSize.Height - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Icons.Draw(g, Icon.Split, new RectangleF(S(14), (ClientSize.Height - S(18)) / 2f, S(18), S(18)), Theme.AccentInk);
        if (Status.Text.Length > 0)
        {
            // Picked at paint time, so it follows a switch of theme or accent.
            using var brush = new SolidBrush(statusKind switch
            {
                StatusKind.Error => Theme.Caution,
                StatusKind.Busy => Theme.AccentInk,
                _ => Theme.Good,
            });
            g.FillEllipse(brush, dotRect);
        }

        using var line = new Pen(Theme.Hairline);
        g.DrawLine(line, 0, ClientSize.Height - 1, ClientSize.Width, ClientSize.Height - 1);
    }
}

/// <summary>A click on a segment row, with the Ctrl and Shift state that decides how it selects.</summary>
readonly record struct SegmentClick(int Index, Keys Modifiers);

/// <summary>
/// Every kept segment as a row: number, in → out, length, and where it lands in the export.
/// Click a row to edit that segment; Ctrl+click and Shift+click pick several for export. Its × removes it.
/// Scrolls with the mouse wheel.
/// </summary>
sealed class SegmentList : GlassControl
{
    IReadOnlyList<Segment> segments = [];
    IReadOnlySet<int> picked = new HashSet<int>();
    int active;
    double fps = 30;
    int scroll;
    int hover = -1;
    bool hoverClose;

    public event EventHandler<SegmentClick>? SelectRequested;
    public event EventHandler<int>? RemoveRequested;

    public SegmentList()
    {
        AccessibleName = "Segments";
        AccessibleRole = AccessibleRole.List;
    }

    int RowHeight => S(46);
    int Gap => S(4);
    int Stride => RowHeight + Gap;
    int MaxScroll => Math.Max(0, segments.Count * Stride - Gap - ClientSize.Height);

    /// <param name="pickedIndices">Rows picked for export besides the active one; empty when only one is selected.</param>
    public void SetSegments(IReadOnlyList<Segment> all, int activeIndex, double framesPerSecond, IReadOnlySet<int> pickedIndices)
    {
        var activeMoved = activeIndex != active || all.Count != segments.Count;
        segments = all;
        active = activeIndex;
        fps = framesPerSecond;
        picked = pickedIndices;
        if (activeMoved) ScrollIntoView(active);
        scroll = Math.Clamp(scroll, 0, MaxScroll);
        AccessibleDescription = picked.Count > 0
            ? $"{all.Count} segments. {picked.Count + 1} are selected for export; segment {active + 1} is being edited."
            : $"{all.Count} segments. Segment {active + 1} is selected.";
        Invalidate();
    }

    void ScrollIntoView(int index)
    {
        var top = index * Stride;
        if (top < scroll) scroll = top;
        else if (top + RowHeight > scroll + ClientSize.Height) scroll = top + RowHeight - ClientSize.Height;
    }

    Rectangle RowRect(int index) => new(0, index * Stride - scroll, ClientSize.Width - (MaxScroll > 0 ? S(8) : 0), RowHeight);

    Rectangle CloseRect(Rectangle row) => new(row.Right - S(34), row.Top, S(30), row.Height);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBase(g);
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
            var thumb = Math.Max(S(24), track * track / (MaxScroll + track));
            var top = (int)((track - thumb) * (scroll / (double)MaxScroll));
            PaintUtil.Fill(g, new RectangleF(ClientSize.Width - S(3), top, S(3), thumb), S(2), Theme.Outline);
        }
    }

    void DrawRow(Graphics g, int index, Rectangle row, IReadOnlyList<Segment> normalized)
    {
        var segment = segments[index];
        var selected = index == active;
        var multi = picked.Count > 0;
        var inPick = multi && (selected || picked.Contains(index));
        var hovered = index == hover;
        var rect = new RectangleF(row.X + 0.5f, row.Y + 0.5f, row.Width - 1, row.Height - 1);
        // The selected row is the only filled one, so it reads at a glance; the others are bare until hovered.
        if (selected)
        {
            PaintUtil.Fill(g, rect, S(10), Theme.Alpha(Theme.Accent, 46));
            PaintUtil.Stroke(g, rect, S(10), Theme.AccentInk);
        }
        else if (inPick)
        {
            PaintUtil.Fill(g, rect, S(10), Theme.Alpha(Theme.Accent, 24));
            PaintUtil.Stroke(g, rect, S(10), Theme.Alpha(Theme.AccentInk, 140));
        }
        else if (hovered)
        {
            PaintUtil.Fill(g, rect, S(10), Theme.ControlFill);
        }

        // Picked rows show a check instead of their number, so the export choice reads at a glance.
        var badgeSize = S(22);
        var badge = new Rectangle(row.Left + S(10), row.Top + (row.Height - badgeSize) / 2, badgeSize, badgeSize);
        var marked = selected || inPick;
        using (var badgeBrush = new SolidBrush(marked ? Theme.Accent : Theme.Selected))
            g.FillEllipse(badgeBrush, badge);
        if (inPick)
            Icons.Draw(g, Icon.Check, RectangleF.Inflate(badge, -S(5), -S(5)), Theme.OnAccent, 3.2f);
        else
            TextRenderer.DrawText(g, (index + 1).ToString(), Theme.Ui(8.5f, FontStyle.Bold), badge, marked ? Theme.OnAccent : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var textLeft = badge.Right + S(10);
        var close = segments.Count > 1 ? CloseRect(row) : Rectangle.Empty;
        var textRight = (close.IsEmpty ? row.Right : close.Left) - S(4);
        var half = row.Height / 2;
        var times = new Rectangle(textLeft, row.Top + S(5), textRight - textLeft, half - S(3));
        TextRenderer.DrawText(g, $"{TimeMath.Format(segment.In, fps)} → {TimeMath.Format(segment.Out, fps)}", Theme.Mono(9), times, Theme.Text, Line | TextFormatFlags.Left);
        var at = CutList.EditedFrame(normalized, segment.In);
        var info = new Rectangle(textLeft, row.Top + half, textRight - textLeft, half - S(5));
        TextRenderer.DrawText(g, $"{TimeMath.Format(segment.Length, fps)} long  ·  at {TimeMath.Format(at, fps)} in export", Theme.Ui(8.5f), info,
            selected ? Theme.Text : Theme.Muted, Line | TextFormatFlags.Left);

        if (!close.IsEmpty && (hovered || selected))
        {
            var size = S(12);
            Icons.Draw(g, Icon.Close, new RectangleF(close.Left + (close.Width - size) / 2f, close.Top + (close.Height - size) / 2f, size, size),
                hovered && hoverClose ? Theme.Caution : Theme.Muted, 2.4f);
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
        else SelectRequested?.Invoke(this, new SegmentClick(index, ModifierKeys & (Keys.Control | Keys.Shift)));
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
sealed class SegmentEditor : GlassControl
{
    public GlassButton SetIn { get; }
    public GlassButton SetOut { get; }
    public DarkField InTime { get; }
    public DarkField InFrame { get; }
    public DarkField OutTime { get; }
    public DarkField OutFrame { get; }
    public GlassButton InEarlier { get; }
    public GlassButton InLater { get; }
    public GlassButton OutEarlier { get; }
    public GlassButton OutLater { get; }

    public SegmentEditor()
    {
        OnPanel = true;
        SetIn = new GlassButton { Text = "In", AccessibleName = "Set in point", OnPanel = true };
        SetOut = new GlassButton { Text = "Out", AccessibleName = "Set out point", OnPanel = true };
        InTime = Field("In timecode");
        InFrame = Field("In frame");
        OutTime = Field("Out timecode");
        OutFrame = Field("Out frame");
        InEarlier = Nudge(Icon.Minus, "Move in point one frame earlier");
        InLater = Nudge(Icon.Plus, "Move in point one frame later");
        OutEarlier = Nudge(Icon.Minus, "Move out point one frame earlier");
        OutLater = Nudge(Icon.Plus, "Move out point one frame later");
        Controls.AddRange([SetIn, InTime, InFrame, InEarlier, InLater, SetOut, OutTime, OutFrame, OutEarlier, OutLater]);
    }

    public int MeasureHeight() => S(32) * 2 + S(6);

    protected override void OnPaint(PaintEventArgs e) => PaintBase(e.Graphics);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var rowHeight = S(32);
        LayoutEdge(0, rowHeight, SetIn, InTime, InFrame, InEarlier, InLater);
        LayoutEdge(rowHeight + S(6), rowHeight, SetOut, OutTime, OutFrame, OutEarlier, OutLater);
    }

    void LayoutEdge(int y, int height, GlassButton edge, DarkField time, DarkField frame, GlassButton earlier, GlassButton later)
    {
        var gap = S(6);
        var nudge = S(30);
        later.SetBounds(ClientSize.Width - nudge, y, nudge, height);
        earlier.SetBounds(later.Left - S(4) - nudge, y, nudge, height);
        edge.SetBounds(0, y, S(52), height);
        frame.SetBounds(earlier.Left - gap - S(74), y, S(74), height);
        time.SetBounds(edge.Right + gap, y, Math.Max(40, frame.Left - gap - edge.Right - gap), height);
    }

    static DarkField Field(string name)
    {
        var field = new DarkField { OnPanel = true };
        field.Box.AccessibleName = name;
        return field;
    }

    static GlassButton Nudge(Icon icon, string name) => new() { Icon = icon, AccessibleName = name, OnPanel = true };
}

/// <summary>
/// Fast copy or exact frames, the exact-mode options, the output format, a one-line explanation and the Export button.
/// Each option is a row: a muted label on the left, its control on the right.
/// </summary>
sealed class ExportBar : GlassControl
{
    public Segmented Mode { get; }
    public Segmented Quality { get; }
    public ToggleSwitch CopyAudio { get; }
    public GlassButton Subtitles { get; }
    public GlassButton Format { get; }
    public GlassButton Export { get; }
    public GlassButton Cancel { get; }
    public GlassLabel Explanation { get; }

    static readonly ExactQuality[] Qualities = [ExactQuality.High, ExactQuality.Balanced, ExactQuality.Small];

    readonly List<(string Label, Rectangle Row)> rows = new();
    bool exactMode;
    ExactQuality quality = ExactExportOptions.Default.Quality;
    bool copyAudio;
    IReadOnlyList<SubtitleTrack> subtitleTracks = [];
    SubtitleTrack? burn;
    OutputFormat format;
    string sourceExtension = "";
    string videoCodec = "";
    IReadOnlyList<string> audioCodecs = [];
    int segments = 1;
    bool busy;
    bool ready;

    public ExportBar()
    {
        OnPanel = true;
        Mode = new Segmented { Items = [ExportModeCopy.FastTitle, ExportModeCopy.ExactTitle], AccessibleName = "Export mode", OnPanel = true };
        Quality = new Segmented { Items = Qualities.Select(ExactExportPresets.Title).ToArray(), Font = Theme.Ui(9), AccessibleName = "Quality", Visible = false, OnPanel = true };
        CopyAudio = new ToggleSwitch { Text = ExportModeCopy.CopyAudioTitle, AccessibleName = ExportModeCopy.CopyAudioTitle, Visible = false, OnPanel = true };
        Subtitles = new GlassButton { AlignLeft = true, Chevron = true, Font = Theme.Ui(9.5f), AccessibleName = "Burn in subtitles", Visible = false, OnPanel = true };
        Format = new GlassButton { AlignLeft = true, Chevron = true, Font = Theme.Ui(9.5f), AccessibleName = "Output format", OnPanel = true };
        Export = new GlassButton { Text = "Export…", Kind = ButtonKind.Primary, AccessibleName = "Export", Enabled = false, OnPanel = true };
        Cancel = new GlassButton { Text = "Cancel", Kind = ButtonKind.Caution, AccessibleName = "Cancel export", Enabled = false, Visible = false, OnPanel = true };
        Explanation = new GlassLabel { ForeColor = Theme.Muted, Font = Theme.Ui(9), Wrap = true, OnPanel = true };
        Controls.AddRange([Mode, Quality, CopyAudio, Subtitles, Format, Explanation, Export, Cancel]);

        Mode.Picked += index => ModePicked?.Invoke(index == 1);
        Quality.Picked += index => SetQuality(Qualities[index]);
        CopyAudio.Toggled += SetCopyAudio;
        Subtitles.Click += (_, _) => ChooseSubtitles();
        Format.Click += (_, _) => ChooseFormat();

        SetMode(false);
    }

    /// <summary>The user picked Fast copy (false) or Exact frames (true).</summary>
    public event Action<bool>? ModePicked;

    /// <summary>An option changed in a way that can change this bar's height.</summary>
    public event EventHandler? OptionsChanged;

    public ExactExportOptions ExactOptions => new(quality, copyAudio, burn);

    public OutputFormat OutputFormat => format;

    bool HasSubtitles => subtitleTracks.Count > 0;

    public void SetMode(bool exact)
    {
        exactMode = exact;
        Mode.SelectedIndex = exact ? 1 : 0;
        Quality.Visible = exact;
        CopyAudio.Visible = exact;
        Subtitles.Visible = exact && HasSubtitles;
        Sync();
        PerformLayout();
        Invalidate();
    }

    /// <summary>While an export runs, the options lock and Cancel shows beside the progress.</summary>
    public void SetBusy(bool value)
    {
        busy = value;
        Mode.Enabled = !value;
        Format.Enabled = !value;
        Cancel.Enabled = value;
        Cancel.Visible = value;
        if (!value) Export.Progress = -1;
        Sync();
        PerformLayout();
    }

    /// <summary>A new video starts with nothing burned in and its own container as the format's reference.</summary>
    public void SetSource(string extension, MediaInfo media)
    {
        sourceExtension = extension;
        subtitleTracks = media.Subtitles;
        videoCodec = media.VideoCodec;
        audioCodecs = media.AudioCodecs;
        burn = null;
        ready = true;
        SetMode(exactMode);
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSegmentCount(int count)
    {
        if (segments == count) return;
        segments = count;
        Sync();
    }

    const int RowHeight = 32;

    public int MeasureHeight(int width)
    {
        var height = S(36) + S(8);
        var optionRows = 1 + (exactMode ? 2 + (HasSubtitles ? 1 : 0) : 0);
        height += optionRows * S(RowHeight) + (optionRows - 1) * S(4);
        height += S(10) + Explanation.MeasureHeight(width) + S(12) + S(40);
        return height;
    }

    void SetQuality(ExactQuality value)
    {
        if (!exactMode || quality == value) return;
        quality = value;
        Sync();
    }

    void SetCopyAudio(bool value)
    {
        if (!exactMode || copyAudio == value || segments > 1) return;
        copyAudio = value;
        Sync();
    }

    /// <summary>One track toggles on and off; several open a menu to pick which one.</summary>
    void ChooseSubtitles()
    {
        if (!exactMode || !HasSubtitles) return;
        if (subtitleTracks.Count == 1)
        {
            SetBurn(burn is null ? subtitleTracks[0] : null);
            return;
        }

        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Don't burn in", burn is null, () => SetBurn(null)));
        foreach (var track in subtitleTracks)
            menu.Items.Add(Menus.Item(track.Label, burn == track, () => SetBurn(track)));
        Menus.ShowBelow(menu, Subtitles);
    }

    void ChooseFormat()
    {
        var menu = Menus.Create();
        foreach (var option in OutputFormats.All)
        {
            var text = option == OutputFormat.Source ? $"Same as source ({OutputFormats.Label(sourceExtension)})" : OutputFormats.Name(option);
            menu.Items.Add(Menus.Item(text, format == option, () => SetFormat(option)));
        }

        Menus.ShowBelow(menu, Format);
    }

    void SetFormat(OutputFormat value)
    {
        if (format == value) return;
        format = value;
        Sync();
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    void SetBurn(SubtitleTrack? value)
    {
        if (burn == value) return;
        burn = value;
        Sync();
        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>True when the export goes into a different container from the source's.</summary>
    public bool Converts => format != OutputFormat.Source
        && !OutputFormats.Extension(format, sourceExtension).Equals(sourceExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The streams this export would copy into a new container that can't hold them. Fast copy copies everything; Exact
    /// frames copies only the audio, and only with Copy original on a single segment. Export stays off while any are listed,
    /// since FFmpeg would fail on them partway in.
    /// </summary>
    IReadOnlyList<string> Misfits()
    {
        if (!Converts) return [];
        var target = OutputFormats.Extension(format, sourceExtension);
        if (!exactMode) return OutputFormats.Misfits(target, videoCodec, audioCodecs);
        return copyAudio && segments == 1 ? OutputFormats.Misfits(target, videoCodec, audioCodecs, includeVideo: false) : [];
    }

    void Sync()
    {
        Quality.SelectedIndex = Array.IndexOf(Qualities, quality);
        Quality.Enabled = !busy;
        CopyAudio.Checked = copyAudio && segments == 1;
        // A multi-segment re-encode joins audio in a filter graph, so it cannot be copied.
        CopyAudio.Enabled = !busy && segments == 1;
        CopyAudio.AccessibleDescription = ExactExportPresets.AudioHint(copyAudio);
        Subtitles.Enabled = !busy;
        Subtitles.Text = burn is { } track ? track.Label : "Off";
        Subtitles.AccessibleDescription = ExactExportPresets.SubtitleHint(burn);
        Subtitles.Invalidate();
        Format.Text = format == OutputFormat.Source ? "Same as source" : OutputFormats.Name(format);
        Format.Detail = sourceExtension.Length == 0 ? ""
            : format == OutputFormat.Source ? OutputFormats.Label(sourceExtension)
            : Converts ? "source is " + OutputFormats.Label(sourceExtension) : "same as source";
        Format.Invalidate();
        var misfits = Misfits();
        Export.Enabled = ready && !busy && misfits.Count == 0;
        // Picture subtitles go only when nothing is burned in: a burned-in export carries no subtitle streams anyway.
        var dropsPictures = Converts && OutputFormats.IsMp4Family(OutputFormats.Extension(format, sourceExtension))
            && subtitleTracks.Any(track => track.IsBitmap) && !(exactMode && burn is not null);
        var text = ExportModeCopy.Summary(exactMode, ExactOptions, segments, Converts ? OutputFormats.Name(format) : null, misfits, dropsPictures)
            + (!exactMode && HasSubtitles && misfits.Count == 0 ? " " + ExportModeCopy.FastSubtitlesHint : "");
        if (Explanation.Text != text)
        {
            Explanation.Text = text;
            Explanation.AccessibleName = text;
            OptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        PaintBase(e.Graphics);
        foreach (var (label, row) in rows)
            TextRenderer.DrawText(e.Graphics, label, Theme.Ui(9.5f), new Rectangle(0, row.Top, S(70), row.Height), Theme.Muted, Line | TextFormatFlags.Left);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var width = ClientSize.Width;
        var labelWidth = S(74);
        var y = 0;
        Mode.SetBounds(0, y, width, S(36));
        y += S(36) + S(8);
        rows.Clear();

        void Row(string label, Control control, int inset = 0)
        {
            var row = new Rectangle(0, y, width, S(RowHeight));
            rows.Add((label, row));
            control.SetBounds(labelWidth, y + inset, width - labelWidth, S(RowHeight) - inset * 2);
            y += S(RowHeight) + S(4);
        }

        if (exactMode)
        {
            Row("Quality", Quality, S(1));
            Row("Audio", CopyAudio);
            if (HasSubtitles) Row("Subtitles", Subtitles, S(1));
        }

        Row("Format", Format, S(1));
        y += S(10) - S(4);
        var explanation = Explanation.MeasureHeight(width);
        Explanation.SetBounds(0, y, width, explanation);
        y += explanation + S(12);
        var buttonHeight = S(40);
        if (Cancel.Visible)
        {
            var cancelWidth = S(92);
            Cancel.SetBounds(width - cancelWidth, y, cancelWidth, buttonHeight);
            Export.SetBounds(0, y, Math.Max(40, Cancel.Left - S(8)), buttonHeight);
        }
        else
        {
            Export.SetBounds(0, y, width, buttonHeight);
        }

        Invalidate();
    }
}

/// <summary>
/// The sidebar: one glass panel holding the segment list, the selected segment's in and out, and the export options,
/// separated by hairlines instead of being three cards.
/// </summary>
sealed class SidePanel : GlassControl
{
    public SegmentList List { get; } = new() { OnPanel = true };
    public SegmentEditor Editor { get; } = new();
    public ExportBar Export { get; } = new();
    public GlassButton Reset { get; }

    string segmentsDetail = "";
    string editorTitle = "";
    string editorDetail = "";
    Rectangle segmentsHeader;
    Rectangle editorHeader;
    Rectangle exportHeader;
    int firstDivider;
    int secondDivider;

    public SidePanel()
    {
        Reset = new GlassButton
        {
            Text = "Reset",
            Icon = Icon.Reset,
            Kind = ButtonKind.Quiet,
            Font = Theme.Ui(9),
            ForeColor = Theme.Muted,
            AccessibleName = "Keep the whole video as one segment",
            OnPanel = true,
        };
        Controls.AddRange([Reset, List, Editor, Export]);
    }

    public int Pad => S(14);

    /// <summary>Muted text beside the Segments title, such as the count and kept length.</summary>
    public string SegmentsDetail
    {
        set { if (segmentsDetail == value) return; segmentsDetail = value; Invalidate(segmentsHeader); }
    }

    public string EditorTitle
    {
        set { if (editorTitle == value) return; editorTitle = value; Invalidate(editorHeader); }
    }

    public string EditorDetail
    {
        set { if (editorDetail == value) return; editorDetail = value; Invalidate(editorHeader); }
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = Pad;
        var inner = Math.Max(S(120), ClientSize.Width - pad * 2);
        var title = S(22);
        var resetWidth = Reset.PreferredWidth() - S(8);
        Reset.SetBounds(ClientSize.Width - pad - resetWidth + S(6), pad - S(2), resetWidth, S(26));
        segmentsHeader = new Rectangle(pad, pad, Math.Max(10, Reset.Left - S(6) - pad), title);

        // The export block and the editor keep their natural heights; the list takes what is left.
        var exportHeight = Export.MeasureHeight(inner);
        var exportTop = ClientSize.Height - pad - exportHeight;
        exportHeader = new Rectangle(pad, exportTop - S(8) - title, inner, title);
        secondDivider = exportHeader.Top - S(12);
        var editorHeight = Editor.MeasureHeight();
        var editorTop = secondDivider - S(12) - editorHeight;
        editorHeader = new Rectangle(pad, editorTop - S(8) - title, inner, title);
        firstDivider = editorHeader.Top - S(12);
        var listTop = segmentsHeader.Bottom + S(10);
        List.SetBounds(pad, listTop, inner, Math.Max(S(40), firstDivider - S(12) - listTop));
        Editor.SetBounds(pad, editorTop, inner, editorHeight);
        Export.SetBounds(pad, exportTop, inner, exportHeight);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Backdrop.Paint(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintUtil.Panel(g, ClientRectangle, S(14));
        Header(g, segmentsHeader, "Segments", segmentsDetail);
        Header(g, editorHeader, editorTitle, editorDetail);
        Header(g, exportHeader, "Export", "");
        using var line = new Pen(Theme.Hairline);
        g.DrawLine(line, Pad, firstDivider, ClientSize.Width - Pad, firstDivider);
        g.DrawLine(line, Pad, secondDivider, ClientSize.Width - Pad, secondDivider);
    }

    void Header(Graphics g, Rectangle rect, string title, string detail)
    {
        var font = Theme.Ui(10, FontStyle.Bold);
        TextRenderer.DrawText(g, title, font, rect, Theme.Text, Line | TextFormatFlags.Left);
        if (detail.Length == 0) return;
        var offset = TextWidth(title, font) + S(10);
        TextRenderer.DrawText(g, detail, Theme.Ui(9), new Rectangle(rect.Left + offset, rect.Top, Math.Max(1, rect.Width - offset), rect.Height), Theme.Muted, Line | TextFormatFlags.Left);
    }
}

/// <summary>
/// The dock under the preview: jump to the previous cut, play the edit, jump to the next cut, the time readout,
/// the two tools that act at the playhead (Split and New segment), and zoom.
/// </summary>
sealed class TransportBar : GlassControl
{
    public GlassButton Previous { get; }
    public GlassButton Play { get; }
    public GlassButton Next { get; }
    public GlassLabel Readout { get; }
    public GlassButton Split { get; }
    public GlassButton NewSegment { get; }
    public GlassButton ZoomOut { get; }
    public GlassButton ZoomIn { get; }
    public GlassButton Fit { get; }
    public GlassLabel ZoomLabel { get; }

    int dividerX;

    public TransportBar()
    {
        Previous = Tool(Icon.Previous, "Previous cut");
        Play = new GlassButton { Icon = Icon.Play, Kind = ButtonKind.Primary, AccessibleName = "Play the edit", TabStop = false, OnPanel = true };
        Next = Tool(Icon.Next, "Next cut");
        Readout = new GlassLabel { Font = Theme.Mono(9.5f), AccessibleName = "Position", OnPanel = true };
        Split = Tool(Icon.Split, "Split the segment at the playhead");
        Split.Text = "Split";
        Split.KeyHint = "S";
        NewSegment = Tool(Icon.Plus, "Start a new segment at the playhead");
        NewSegment.Text = "New segment";
        NewSegment.KeyHint = "N";
        ZoomOut = Tool(Icon.Minus, "Zoom out");
        ZoomIn = Tool(Icon.Plus, "Zoom in");
        Fit = Tool(Icon.None, "Show the whole video");
        Fit.Text = "Fit";
        ZoomLabel = new GlassLabel { ForeColor = Theme.Muted, Font = Theme.Ui(9), Align = ContentAlignment.MiddleCenter, AccessibleName = "Zoom", OnPanel = true };
        Controls.AddRange([Previous, Play, Next, Readout, Split, NewSegment, ZoomOut, ZoomLabel, ZoomIn, Fit]);
    }

    static GlassButton Tool(Icon icon, string name) => new()
    {
        Icon = icon,
        Kind = ButtonKind.Quiet,
        AccessibleName = name,
        TabStop = false,
        OnPanel = true,
    };

    public void SetPlaying(bool playing)
    {
        Play.Icon = playing ? Icon.Pause : Icon.Play;
        Play.AccessibleName = playing ? "Pause" : "Play the edit";
        Play.Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var pad = S(8);
        var gap = S(4);
        var height = S(36);
        var y = (ClientSize.Height - height) / 2;
        Previous.SetBounds(pad, y, S(36), height);
        Play.SetBounds(Previous.Right + gap, y, S(42), height);
        Next.SetBounds(Play.Right + gap, y, S(36), height);

        var right = ClientSize.Width - pad;
        var fitWidth = Fit.PreferredWidth() - S(4);
        Fit.SetBounds(right - fitWidth, y, fitWidth, height);
        ZoomIn.SetBounds(Fit.Left - S(32), y, S(32), height);
        ZoomLabel.SetBounds(ZoomIn.Left - S(40), y, S(40), height);
        ZoomOut.SetBounds(ZoomLabel.Left - S(32), y, S(32), height);
        dividerX = ZoomOut.Left - S(7);

        // In a narrow window the two tools drop their labels rather than squeeze the readout to nothing.
        var compact = ClientSize.Width < S(700);
        Split.Compact = compact;
        NewSegment.Compact = compact;
        // The key caps go first; the tooltips still name the keys.
        var hints = ClientSize.Width >= S(1000);
        Split.KeyHint = hints ? "S" : null;
        NewSegment.KeyHint = hints ? "N" : null;
        var newWidth = NewSegment.PreferredWidth();
        NewSegment.SetBounds(dividerX - S(7) - newWidth, y, newWidth, height);
        var splitWidth = Split.PreferredWidth();
        Split.SetBounds(NewSegment.Left - gap - splitWidth, y, splitWidth, height);
        Readout.SetBounds(Next.Right + S(12), y, Math.Max(0, Split.Left - S(12) - Next.Right - S(12)), height);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Backdrop.Paint(g, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintUtil.Panel(g, ClientRectangle, S(14));
        using var line = new Pen(Theme.ControlBorder);
        g.DrawLine(line, dividerX, (ClientSize.Height - S(22)) / 2, dividerX, (ClientSize.Height + S(22)) / 2);
    }
}

/// <summary>Opaque dark drop-down menus.</summary>
static class Menus
{
    public static ContextMenuStrip Create() => new()
    {
        Renderer = new ToolStripProfessionalRenderer(new MenuColors()) { RoundedEdges = false },
        ShowImageMargin = false,
        ShowCheckMargin = true,
        Font = Theme.Ui(9.5f),
        BackColor = Theme.Menu,
        ForeColor = Theme.Text,
    };

    public static ToolStripMenuItem Item(string text, bool isChecked, Action onClick, string? shortcut = null, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text)
        {
            Checked = isChecked,
            ForeColor = enabled ? Theme.Text : Theme.Faint,
            ShortcutKeyDisplayString = shortcut,
            Enabled = enabled,
            Padding = new Padding(0, 3, 0, 3),
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static ToolStripSeparator Separator() => new() { ForeColor = Theme.MenuBorder };

    /// <summary>Opens the menu under <paramref name="anchor"/> and frees it once it closes.</summary>
    public static void ShowBelow(ContextMenuStrip menu, Control anchor, bool alignRight = false)
    {
        menu.Closed += (_, _) => { if (anchor.IsHandleCreated) anchor.BeginInvoke(menu.Dispose); };
        if (alignRight)
            menu.Show(anchor, new Point(anchor.Width, anchor.Height + 2), ToolStripDropDownDirection.BelowLeft);
        else
            menu.Show(anchor, new Point(0, anchor.Height + 2));
    }

    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Menu;
        public override Color MenuBorder => Theme.MenuBorder;
        // The hovered item steps away from the menu's color: lighter on the dark theme, darker on the light one.
        static Color Highlight => Theme.IsDark ? Theme.Lift(Theme.Menu, 22) : Theme.Darken(Theme.Menu, 16);

        public override Color MenuItemBorder => Highlight;
        public override Color MenuItemSelected => Highlight;
        public override Color CheckBackground => Theme.Menu;
        public override Color CheckSelectedBackground => Highlight;
        public override Color CheckPressedBackground => Highlight;
        public override Color ImageMarginGradientBegin => Theme.Menu;
        public override Color ImageMarginGradientMiddle => Theme.Menu;
        public override Color ImageMarginGradientEnd => Theme.Menu;
        public override Color SeparatorDark => Theme.MenuBorder;
        public override Color SeparatorLight => Theme.Menu;
    }
}

/// <summary>Every keyboard and mouse shortcut on one sheet, opened from the ⋯ menu or with F1.</summary>
sealed class ShortcutsDialog : Form
{
    static readonly (string Keys, string Does)[] Rows =
    [
        ("Space", "Play or pause the edit"),
        (",  and  .", "Previous and next cut"),
        ("←  and  →", "Step one frame (Shift: one second)"),
        ("Home  and  End", "Jump to the segment's in and out"),
        ("I  and  O", "Set in and out at the playhead"),
        ("[  and  ]", "Nudge the nearest edge one frame"),
        ("S", "Split the segment at the playhead"),
        ("N", "Start a new segment at the playhead"),
        ("Delete", "Remove the selected segment"),
        ("Ctrl+click, Shift+click", "Pick several segments to export"),
        ("Ctrl+A,  Esc", "Pick all segments, clear the pick"),
        ("+,  −,  0", "Zoom in, zoom out, fit"),
        ("Ctrl+wheel", "Zoom the timeline at the pointer"),
        ("Shift+click, Alt+click", "Set in or out on the timeline"),
        ("Ctrl+O", "Open a video"),
        ("Ctrl+,", "Settings: theme and accent color"),
        ("F1", "Show this sheet"),
    ];

    public ShortcutsDialog()
    {
        Text = "Keyboard shortcuts";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Base;
        ForeColor = Theme.Text;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    int S(int logical) => LogicalToDeviceUnits(logical);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.Colors(this);
        ClientSize = new Size(S(520), S(24) * 2 + Rows.Length * S(30));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Escape or Keys.Enter or Keys.F1) Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Base);
        using var line = new Pen(Theme.Hairline);
        var y = S(24);
        foreach (var (keys, does) in Rows)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            TextRenderer.DrawText(g, keys, Theme.Ui(9.5f, FontStyle.Bold), new Rectangle(S(28), y, S(190), S(30)), Theme.Text, flags);
            TextRenderer.DrawText(g, does, Theme.Ui(9.5f), new Rectangle(S(226), y, ClientSize.Width - S(254), S(30)), Theme.Muted, flags);
            y += S(30);
            if (y < ClientSize.Height - S(24)) g.DrawLine(line, S(28), y, ClientSize.Width - S(28), y);
        }
    }
}

/// <summary>
/// Settings: the light or dark theme and the accent color, as in WB Tunes. Each choice shows across the app at once;
/// OK keeps it and Cancel puts the saved look back.
/// </summary>
sealed class SettingsDialog : Form
{
    readonly AppSettings settings;
    readonly Segmented theme = new() { Items = ["Light", "Dark"], AccessibleName = "Theme" };
    readonly Swatches accent = new() { AccessibleName = "Accent color" };
    readonly GlassButton ok = new() { Text = "OK", Kind = ButtonKind.Primary, AccessibleName = "OK" };
    readonly GlassButton cancel = new() { Text = "Cancel", AccessibleName = "Cancel" };
    readonly bool savedDark;
    readonly string savedAccent;
    bool dark;
    string accentName;

    public SettingsDialog(AppSettings settings)
    {
        this.settings = settings;
        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Base;
        ForeColor = Theme.Text;
        Font = Theme.Ui(9.5f);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        savedDark = dark = settings.DarkTheme;
        savedAccent = accentName = Theme.FindTint(settings.Accent).Name;
        theme.Picked += index =>
        {
            dark = index == 1;
            Preview();
        };
        accent.Picked += index =>
        {
            accentName = Theme.Tints[index].Name;
            Preview();
        };
        ok.Click += (_, _) =>
        {
            settings.DarkTheme = dark;
            settings.Accent = accentName;
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange([theme, accent, cancel, ok]);
        Theme.Changed += OnThemeChanged;
        ShowChoices();
    }

    int S(int logical) => LogicalToDeviceUnits(logical);
    int LabelWidth => S(96);
    Rectangle ThemeRow => new(S(24), S(24), ClientSize.Width - S(48), S(36));
    Rectangle AccentRow => new(S(24), ThemeRow.Bottom + S(16), ClientSize.Width - S(48), S(36));
    Rectangle Hint => new(S(24) + LabelWidth, AccentRow.Bottom + S(10), ClientSize.Width - S(48) - LabelWidth, S(20));

    void Preview()
    {
        Theme.Apply(dark, accentName);
        ShowChoices();
    }

    void ShowChoices()
    {
        theme.SelectedIndex = dark ? 1 : 0;
        accent.SelectedIndex = Array.FindIndex(Theme.Tints, tint => tint.Name == accentName);
    }

    void OnThemeChanged()
    {
        if (IsDisposed) return;
        Theme.Recolor(this);
        if (IsHandleCreated) WindowChrome.Colors(this);
        Invalidate(true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.Colors(this);
        ClientSize = new Size(S(500), S(24) + S(36) + S(16) + S(36) + S(10) + S(20) + S(24) + S(36) + S(20));
        theme.SetBounds(ThemeRow.Left + LabelWidth, ThemeRow.Top, S(180), ThemeRow.Height);
        accent.SetBounds(AccentRow.Left + LabelWidth, AccentRow.Top, AccentRow.Width - LabelWidth, AccentRow.Height);
        var buttonTop = ClientSize.Height - S(20) - S(36);
        ok.SetBounds(ClientSize.Width - S(24) - S(96), buttonTop, S(96), S(36));
        cancel.SetBounds(ok.Left - S(8) - S(96), buttonTop, S(96), S(36));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        if (DialogResult != DialogResult.OK) Theme.Apply(savedDark, savedAccent);
        base.OnFormClosed(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // The same tinted backdrop as the main window, so the controls' patches of it match the dialog around them.
        Backdrop.Paint(g, this);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
        TextRenderer.DrawText(g, "Theme", Theme.Ui(9.5f), ThemeRow with { Width = LabelWidth }, Theme.Muted, flags);
        TextRenderer.DrawText(g, "Accent", Theme.Ui(9.5f), AccentRow with { Width = LabelWidth }, Theme.Muted, flags);
        TextRenderer.DrawText(g, "Changes show right away; Cancel puts back the saved look.", Theme.Ui(9), Hint, Theme.Faint, flags | TextFormatFlags.EndEllipsis);
    }
}
