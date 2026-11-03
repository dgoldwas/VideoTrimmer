using System.Globalization;

namespace VideoTrimmer;

public sealed class MainForm : Form
{
    const string VideoFilter = "Video files|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.wmv|All files|*.*";
    const string ReadyHint = "Click the filmstrip to move. Shift-click sets in. Alt-click sets out.";

    readonly FfmpegRunner runner = new();
    readonly StillCapture stills;
    readonly FilmstripService filmstrip;
    readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer accurateTimer = new() { Interval = 220 };
    readonly System.Windows.Forms.Timer filmTimer = new() { Interval = 200 };
    readonly ToolTip tips = new() { ShowAlways = true };
    readonly string? startupPath;

    readonly ToolbarPanel toolbar = new();
    readonly StatusBar statusBar = new();
    readonly Panel bottom = new();
    readonly Panel body = new();
    readonly DropPrompt drop = new();
    readonly Panel previewHost = new();
    readonly PictureBox preview = new();
    readonly Label playheadLabel = new();
    readonly TimelineControl timeline = new();
    readonly PointsPanel points = new();
    readonly ExportBar exportBar = new();

    CancellationTokenSource loadCts = new();
    CancellationTokenSource previewCts = new();
    CancellationTokenSource filmCts = new();
    CancellationTokenSource exportCts = new();

    string? input;
    double duration;
    double fps;
    bool exact;
    bool exporting;
    bool syncing;
    bool sized;
    bool layingOut;
    bool narrowLayout;
    bool previewArmed;
    bool disposed;
    int loadGeneration;
    int previewGeneration;
    Bitmap? shown;

    public MainForm(string? startupPath)
    {
        this.startupPath = startupPath;
        stills = new StillCapture(runner);
        filmstrip = new FilmstripService(runner, stills);
        Font = Theme.Ui(10);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Text = "Video Trimmer";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        AllowDrop = true;
        BuildUi();
        previewTimer.Tick += (_, _) => OnPreviewTick();
        accurateTimer.Tick += (_, _) => OnAccurateTick();
        filmTimer.Tick += (_, _) =>
        {
            filmTimer.Stop();
            var count = DesiredThumbCount();
            if (count != filmstrip.CurrentCount && count != filmstrip.BuildingCount)
                StartFilmstrip(count);
        };
        if (runner.Ffmpeg is null || runner.Ffprobe is null)
            SetStatus("FFmpeg not found — install it or place ffmpeg.exe and ffprobe.exe beside this app.", StatusKind.Error);
        else
            SetStatus("Ready.", StatusKind.Info);
    }

    void BuildUi()
    {
        statusBar.Dock = DockStyle.Bottom;
        bottom.Dock = DockStyle.Bottom;
        bottom.Visible = false;
        bottom.BackColor = Theme.Background;
        toolbar.Dock = DockStyle.Top;
        body.Dock = DockStyle.Fill;
        body.BackColor = Theme.Background;

        drop.Dock = DockStyle.Fill;
        drop.OpenRequested += (_, _) => Browse();
        previewHost.Dock = DockStyle.Fill;
        previewHost.Visible = false;
        previewHost.BackColor = Theme.Border;
        previewHost.Padding = new Padding(1);
        preview.Dock = DockStyle.Fill;
        preview.BackColor = Theme.Background;
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        preview.TabStop = false;
        playheadLabel.Dock = DockStyle.Bottom;
        playheadLabel.TextAlign = ContentAlignment.MiddleCenter;
        playheadLabel.ForeColor = Theme.Text;
        playheadLabel.BackColor = Theme.Surface;
        playheadLabel.Font = Theme.Mono(10);
        previewHost.Controls.Add(preview);
        previewHost.Controls.Add(playheadLabel);
        body.Controls.Add(previewHost);
        body.Controls.Add(drop);

        bottom.Controls.Add(timeline);
        bottom.Controls.Add(points);
        bottom.Controls.Add(exportBar);

        Controls.Add(body);
        Controls.Add(toolbar);
        Controls.Add(bottom);
        Controls.Add(statusBar);

        toolbar.Open.Click += (_, _) => Browse();
        timeline.ValuesChanged += (_, _) => OnTimelineChanged();
        timeline.InteractionEnded += (_, _) => OnTimelineInteractionEnded();
        preview.MouseWheel += OnWheel;

        points.SetIn.Click += (_, _) => timeline.SetInToPlayhead();
        points.SetOut.Click += (_, _) => timeline.SetOutToPlayhead();
        points.InEarlier.Click += (_, _) => timeline.NudgeIn(-1);
        points.InLater.Click += (_, _) => timeline.NudgeIn(1);
        points.OutEarlier.Click += (_, _) => timeline.NudgeOut(-1);
        points.OutLater.Click += (_, _) => timeline.NudgeOut(1);
        points.InTime.Box.Leave += (_, _) => CommitIn(timecode: true);
        points.InFrame.Box.Leave += (_, _) => CommitIn(timecode: false);
        points.OutTime.Box.Leave += (_, _) => CommitOut(timecode: true);
        points.OutFrame.Box.Leave += (_, _) => CommitOut(timecode: false);

        exportBar.Fast.Click += (_, _) => ChooseExact(false);
        exportBar.Exact.Click += (_, _) => ChooseExact(true);
        exportBar.Export.Click += (_, _) => Export();
        exportBar.Cancel.Click += (_, _) => CancelExport();
        AcceptButton = exportBar.Export;
        CancelButton = exportBar.Cancel;
        ChooseExact(false);

        tips.SetToolTip(points.SetIn, "Set the in point to the current frame (I)");
        tips.SetToolTip(points.SetOut, "Include the current frame and end here (O)");
        tips.SetToolTip(points.InEarlier, "One frame earlier");
        tips.SetToolTip(points.InLater, "One frame later");
        tips.SetToolTip(points.OutEarlier, "One frame earlier");
        tips.SetToolTip(points.OutLater, "One frame later");
        tips.SetToolTip(timeline, "Drag the handles to trim. Drag the highlighted span to slide it. Click to move the playhead.");
        tips.SetToolTip(exportBar.Fast, ExportModeCopy.FastExplanation);
        tips.SetToolTip(exportBar.Exact, ExportModeCopy.ExactExplanation);

        WireDrop(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (sized) return;
        sized = true;
        MinimumSize = new Size(S(860), S(700));
        Size = new Size(S(1180), S(800));
        CenterToScreen();
        LayoutChrome();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutChrome();
        if (!string.IsNullOrWhiteSpace(startupPath))
            LoadVideo(startupPath);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChrome();
        QueueFilmstrip();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutChrome();
        QueueFilmstrip();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        TryCancel(loadCts);
        TryCancel(previewCts);
        TryCancel(filmCts);
        TryCancel(exportCts);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            CancelAndDispose(loadCts);
            CancelAndDispose(previewCts);
            CancelAndDispose(filmCts);
            CancelAndDispose(exportCts);
            previewTimer.Stop();
            accurateTimer.Stop();
            filmTimer.Stop();
            preview.Image = null;
            timeline.UseStrip(null);
            ClearPictures();
            previewTimer.Dispose();
            accurateTimer.Dispose();
            filmTimer.Dispose();
            tips.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.O))
        {
            if (!exporting) Browse();
            return true;
        }

        if (keyData == Keys.Escape && exporting)
        {
            CancelExport();
            return true;
        }

        if (ActiveControl is TextBox box)
        {
            if (keyData == Keys.Enter)
            {
                CommitBox(box);
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (!timeline.HasMedia || exporting)
            return base.ProcessCmdKey(ref msg, keyData);

        var code = keyData & Keys.KeyCode;
        var shift = (keyData & Keys.Shift) == Keys.Shift;
        var other = (keyData & ~(Keys.KeyCode | Keys.Shift)) != Keys.None;
        if (other) return base.ProcessCmdKey(ref msg, keyData);
        var second = timeline.FramesInOneSecond();
        switch (code)
        {
            case Keys.I:
                timeline.SetInToPlayhead();
                SchedulePreview(true);
                return true;
            case Keys.O:
                timeline.SetOutToPlayhead();
                SchedulePreview(true);
                return true;
            case Keys.Left:
                timeline.NudgePlayhead(shift ? -second : -1);
                SchedulePreview(true);
                return true;
            case Keys.Right:
                timeline.NudgePlayhead(shift ? second : 1);
                SchedulePreview(true);
                return true;
            case Keys.Home:
                timeline.JumpPlayheadToIn();
                SchedulePreview(true);
                return true;
            case Keys.End:
                timeline.JumpPlayheadToOut();
                SchedulePreview(true);
                return true;
            case Keys.OemOpenBrackets:
                timeline.NudgeNearestEdge(-1);
                SchedulePreview(true);
                return true;
            case Keys.OemCloseBrackets:
                timeline.NudgeNearestEdge(1);
                SchedulePreview(true);
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    void LayoutChrome()
    {
        if (layingOut || !IsHandleCreated) return;
        layingOut = true;
        try
        {
            narrowLayout = ClientSize.Width < S(1000);
            toolbar.Height = S(56);
            statusBar.Height = S(32);
            body.Padding = new Padding(S(16));
            playheadLabel.Height = S(28);

            var timelineHeight = S(148);
            var pointsHeight = S(34) + S(8) + S(34);
            var exportHeight = narrowLayout
                ? S(36) + S(8) + S(36) + S(8) + S(36) + S(4) + S(32)
                : S(40) + S(4) + S(36);
            var bottomHeight = S(12) + timelineHeight + S(8) + pointsHeight + S(8) + exportHeight + S(12);
            var chrome = statusBar.Height + (toolbar.Visible ? toolbar.Height : 0);
            var previewRoom = ClientSize.Height - chrome - (bottom.Visible ? bottomHeight : 0);
            if (bottom.Visible && previewRoom < S(120))
            {
                timelineHeight = Math.Max(S(96), timelineHeight - (S(120) - previewRoom));
                bottomHeight = S(12) + timelineHeight + S(8) + pointsHeight + S(8) + exportHeight + S(12);
            }

            bottom.Height = bottomHeight;
            var pad = S(12);
            var gap = S(8);
            var width = Math.Max(10, bottom.ClientSize.Width - pad * 2);
            timeline.SetBounds(pad, pad, width, timelineHeight);
            points.SetBounds(pad, timeline.Bottom + gap, width, pointsHeight);
            exportBar.SetBounds(pad, points.Bottom + gap, width, exportHeight);
            exportBar.Arrange(narrowLayout);
            statusBar.PerformLayout();
        }
        finally
        {
            layingOut = false;
        }
    }

    void WireDrop(Control control)
    {
        // WinForms delivers the drop to the child under the pointer, so every child shares one handler.
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls)
            WireDrop(child);
    }

    void OnDragEnter(object? sender, DragEventArgs e)
    {
        var files = e.Data?.GetDataPresent(DataFormats.FileDrop) == true;
        e.Effect = files && !exporting ? DragDropEffects.Copy : DragDropEffects.None;
    }

    void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (exporting) return;
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            LoadVideo(paths[0]);
    }

    void OnWheel(object? sender, MouseEventArgs e)
    {
        if (!timeline.HasMedia || exporting) return;
        var step = (ModifierKeys & Keys.Shift) == Keys.Shift ? timeline.FramesInOneSecond() : 1;
        timeline.NudgePlayhead(e.Delta > 0 ? -step : step);
        SchedulePreview(true);
    }

    void Browse()
    {
        if (exporting) return;
        using var dialog = new OpenFileDialog { Filter = VideoFilter };
        if (input is not null)
        {
            dialog.FileName = Path.GetFileName(input);
            var directory = Path.GetDirectoryName(input);
            if (!string.IsNullOrEmpty(directory)) dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
            LoadVideo(dialog.FileName);
    }

    async void LoadVideo(string path)
    {
        if (exporting)
        {
            SetStatus("Finish or cancel the export before opening another video.", StatusKind.Error);
            return;
        }

        if (!File.Exists(path))
        {
            SetStatus("File not found.", StatusKind.Error);
            return;
        }

        if (runner.Ffprobe is null)
        {
            SetStatus("Bundled FFprobe is missing. Rebuild or use the complete publish folder.", StatusKind.Error);
            MessageBox.Show(this, "The bundled FFprobe executable is missing. Please use the complete publish\\win-x64 folder.", "Video Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ReplaceCts(ref loadCts);
        var token = loadCts.Token;
        var generation = ++loadGeneration;
        SetStatus("Reading metadata…", StatusKind.Busy);
        try
        {
            var media = await MediaProbe.ProbeAsync(runner, path, token);
            if (generation != loadGeneration || IsDisposed) return;
            ApplyMedia(path, media.Duration, media.Fps, media.Frames);
        }
        catch (OperationCanceledException)
        {
            // A newer file replaced this request.
        }
        catch (Exception ex)
        {
            if (generation != loadGeneration || IsDisposed) return;
            SetStatus("Could not read video: " + ex.Message, StatusKind.Error);
            MessageBox.Show(this, "Video metadata could not be read.\n\n" + ex.Message, "Video Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ApplyMedia(string path, double probedDuration, double probedFps, long frames)
    {
        input = path;
        duration = probedDuration;
        fps = probedFps;
        CancelPreviewJobs();
        ClearPictures();
        timeline.Locked = false;
        timeline.SetClip(probedDuration, probedFps, frames);
        toolbar.FileName.Text = Path.GetFileName(path);
        tips.SetToolTip(toolbar.FileName, path);
        toolbar.Meta.Text = string.Format(CultureInfo.CurrentCulture, "{0:0.###} s   ·   {1:0.###} fps   ·   {2:N0} frames", probedDuration, probedFps, frames);
        toolbar.Visible = true;
        bottom.Visible = true;
        drop.Visible = false;
        previewHost.Visible = true;
        exportBar.Export.Enabled = true;
        LayoutChrome();
        WriteBoxes();
        UpdateReadouts();
        SchedulePreview(true);
        StartFilmstrip(DesiredThumbCount());
        SetStatus("Building the filmstrip…", StatusKind.Busy);
    }

    void OnTimelineChanged()
    {
        // Skip text-box churn while dragging; InteractionEnded writes the final values.
        if (!syncing && !timeline.IsDragging) WriteBoxes();
        UpdateReadouts();
        SchedulePreview(false);
    }

    void OnTimelineInteractionEnded()
    {
        if (!syncing) WriteBoxes();
        SchedulePreview(accurate: true);
    }

    void UpdateReadouts()
    {
        if (!timeline.HasMedia)
        {
            points.Selected.Text = "";
            playheadLabel.Text = "";
            return;
        }

        var frames = timeline.OutFrame - timeline.InFrame;
        var unit = frames == 1 ? "frame" : "frames";
        points.Selected.Text = $"Selected {TimeMath.Format(frames, timeline.Fps)}   ·   {frames:N0} {unit}";
        playheadLabel.Text = $"{TimeMath.Format(timeline.PlayheadFrame, timeline.Fps)}    frame {timeline.PlayheadFrame:N0}";
    }

    void WriteBoxes()
    {
        if (!timeline.HasMedia) return;
        SetBox(points.InTime.Box, TimeMath.Format(timeline.InFrame, timeline.Fps));
        SetBox(points.InFrame.Box, timeline.InFrame.ToString(CultureInfo.CurrentCulture));
        SetBox(points.OutTime.Box, TimeMath.Format(timeline.OutFrame, timeline.Fps));
        SetBox(points.OutFrame.Box, timeline.OutFrame.ToString(CultureInfo.CurrentCulture));
    }

    static void SetBox(TextBox box, string value)
    {
        if (box.Text != value) box.Text = value;
    }

    void CommitBox(TextBox box)
    {
        if (box == points.InTime.Box) CommitIn(true);
        else if (box == points.InFrame.Box) CommitIn(false);
        else if (box == points.OutTime.Box) CommitOut(true);
        else if (box == points.OutFrame.Box) CommitOut(false);
    }

    void CommitIn(bool timecode)
    {
        if (syncing || exporting || !timeline.HasMedia) return;
        if (!TryRead(timecode ? points.InTime.Box : points.InFrame.Box, timecode, out var frame))
        {
            WriteBoxes();
            return;
        }

        syncing = true;
        timeline.SetInFrame(frame, followPlayhead: true);
        WriteBoxes();
        syncing = false;
        SchedulePreview(true);
    }

    void CommitOut(bool timecode)
    {
        if (syncing || exporting || !timeline.HasMedia) return;
        if (!TryRead(timecode ? points.OutTime.Box : points.OutFrame.Box, timecode, out var frame))
        {
            WriteBoxes();
            return;
        }

        syncing = true;
        timeline.SetOutFrame(frame, followPlayhead: true);
        WriteBoxes();
        syncing = false;
        SchedulePreview(true);
    }

    bool TryRead(TextBox box, bool timecode, out long frame)
    {
        if (timecode) return TimeMath.TryParse(box.Text, timeline.Fps, out frame);
        return long.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out frame);
    }

    void ChooseExact(bool value)
    {
        if (exporting) return;
        exact = value;
        exportBar.SetExplanation(value);
    }

    void SchedulePreview(bool accurate)
    {
        if (!timeline.HasMedia || IsDisposed) return;
        if (accurate)
        {
            accurateTimer.Stop();
            accurateTimer.Start();
        }

        if (previewArmed) return;
        previewArmed = true;
        previewTimer.Stop();
        previewTimer.Start();
    }

    void OnPreviewTick()
    {
        previewTimer.Stop();
        previewArmed = false;
        if (!timeline.HasMedia || IsDisposed) return;
        _ = GrabPreviewAsync(timeline.PlayheadFrame, accurate: false);
    }

    void OnAccurateTick()
    {
        accurateTimer.Stop();
        if (!timeline.HasMedia || IsDisposed) return;
        _ = GrabPreviewAsync(timeline.PlayheadFrame, accurate: true);
    }

    async Task GrabPreviewAsync(long frame, bool accurate)
    {
        if (!timeline.HasMedia || runner.Ffmpeg is null || input is null || IsDisposed) return;
        var width = PreviewWidth();
        if (stills.Cache.TryGet(frame, width, accurate, out var cached) && cached is not null)
        {
            ShowPreview(cached);
            return;
        }

        ReplaceCts(ref previewCts);
        var token = previewCts.Token;
        var generation = ++previewGeneration;
        Bitmap? bitmap = null;
        try
        {
            bitmap = await stills.CaptureAsync(input, duration, timeline.SeekTime(frame), accurate, "scale=" + width + ":-2", accurate ? 2 : 3, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (bitmap is null || generation != previewGeneration || IsDisposed)
        {
            bitmap?.Dispose();
            return;
        }

        ShowPreview(bitmap);
        stills.Cache.Put(frame, width, bitmap, accurate, shown);
    }

    void ShowPreview(Bitmap bitmap)
    {
        if (IsDisposed) return;
        shown = bitmap;
        preview.Image = bitmap;
    }

    int PreviewWidth()
    {
        var width = Math.Clamp(preview.ClientSize.Width, 320, 1600);
        if (width % 2 == 1) width--;
        return width;
    }

    void QueueFilmstrip()
    {
        if (!timeline.HasMedia || exporting || IsDisposed) return;
        var count = DesiredThumbCount();
        if (count == filmstrip.CurrentCount || count == filmstrip.BuildingCount) return;
        if (filmstrip.TryGetCached(count, out var cached) && cached is not null)
        {
            filmstrip.ShowCached(timeline, count, cached);
            return;
        }

        filmTimer.Stop();
        filmTimer.Start();
    }

    int DesiredThumbCount()
    {
        var slot = Math.Max(S(104), 48);
        return Math.Clamp(timeline.FilmWidth / slot, 6, 32);
    }

    void StartFilmstrip(int count)
    {
        if (input is null) return;
        ReplaceCts(ref filmCts);
        filmstrip.Start(
            this,
            timeline,
            input,
            duration,
            count,
            filmCts.Token,
            onProgress: (filled, total) => SetStatus($"Building the filmstrip… {filled}/{total}", StatusKind.Busy),
            onCompleted: (any, failure) =>
            {
                if (exporting) return;
                SetStatus(any ? ReadyHint : failure, any ? StatusKind.Info : StatusKind.Error);
            },
            isExporting: () => exporting);
    }

    void CancelPreviewJobs()
    {
        ReplaceCts(ref previewCts);
        ReplaceCts(ref filmCts);
        previewGeneration++;
        filmstrip.InvalidateJobs();
        previewTimer.Stop();
        accurateTimer.Stop();
        filmTimer.Stop();
        previewArmed = false;
    }

    void ClearPictures()
    {
        preview.Image = null;
        shown = null;
        stills.Clear();
        filmstrip.Clear(timeline);
    }

    async void Export()
    {
        if (exporting || !timeline.HasMedia || input is null) return;
        if (runner.Ffmpeg is null)
        {
            MessageBox.Show(this, "FFmpeg is required. See README.md.", "Video Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(input) + "_trimmed" + Path.GetExtension(input),
            Filter = "Same container|*",
            InitialDirectory = Path.GetDirectoryName(input) ?? "",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var output = dialog.FileName;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("Choose a different file from the video you are trimming.", StatusKind.Error);
            return;
        }

        ReplaceCts(ref exportCts);
        var token = exportCts.Token;
        var mode = exact ? "Encoding exact frames" : "Saving fast copy";
        exporting = true;
        timeline.Locked = true;
        exportBar.Export.Enabled = false;
        exportBar.Cancel.Enabled = true;
        exportBar.Cancel.Visible = true;
        exportBar.Fast.Enabled = false;
        exportBar.Exact.Enabled = false;
        statusBar.Progress.Fraction = 0;
        statusBar.Progress.Visible = true;
        statusBar.PerformLayout();
        exportBar.Arrange(narrowLayout);
        SetStatus(mode + "…", StatusKind.Busy);
        try
        {
            var progress = new Progress<TimeSpan>(elapsed =>
            {
                if (!exporting || IsDisposed) return;
                var length = Math.Max(0.001, timeline.OutSeconds - timeline.InSeconds);
                var fraction = Math.Clamp(elapsed.TotalSeconds / length, 0, 1);
                statusBar.Progress.Fraction = fraction;
                SetStatus($"{mode}… {(int)Math.Round(fraction * 100)}%", StatusKind.Busy);
            });
            var args = ExportService.BuildArguments(input, output, timeline.InSeconds, timeline.OutSeconds, exact);
            await runner.RunAsync(runner.Ffmpeg, args, token, progress);
            SetStatus("Saved: " + output, StatusKind.Good);
        }
        catch (OperationCanceledException)
        {
            ExportService.TryDelete(output);
            if (!IsDisposed) SetStatus("Export canceled.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) SetStatus("Export failed: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            exporting = false;
            timeline.Locked = false;
            if (!IsDisposed)
            {
                exportBar.Export.Enabled = timeline.HasMedia;
                exportBar.Cancel.Enabled = false;
                exportBar.Cancel.Visible = false;
                exportBar.Fast.Enabled = true;
                exportBar.Exact.Enabled = true;
                statusBar.Progress.Visible = false;
                statusBar.PerformLayout();
                exportBar.Arrange(narrowLayout);
            }
        }
    }

    void CancelExport()
    {
        if (!exporting) return;
        exportCts.Cancel();
    }

    static void ReplaceCts(ref CancellationTokenSource cts)
    {
        var previous = cts;
        cts = new CancellationTokenSource();
        CancelAndDispose(previous);
    }

    static void TryCancel(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    static void CancelAndDispose(CancellationTokenSource cts)
    {
        TryCancel(cts);
        try { cts.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    void SetStatus(string text, StatusKind kind)
    {
        statusBar.Label.Text = text;
        statusBar.Label.AccessibleName = text;
        statusBar.Label.ForeColor = kind switch
        {
            StatusKind.Error => Theme.OutPoint,
            StatusKind.Good => Theme.InPoint,
            StatusKind.Busy => Theme.Text,
            _ => Theme.Muted,
        };
    }

    int S(int logical) => LogicalToDeviceUnits(logical);

    enum StatusKind { Info, Busy, Good, Error }
}
