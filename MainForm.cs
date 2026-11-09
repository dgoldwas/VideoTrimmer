using System.Globalization;

namespace VideoTrimmer;

public sealed partial class MainForm : Form
{
    const string VideoFilter = "Video files|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.wmv|All files|*.*";
    const string ReadyHint = "Space plays the edit. S splits, N adds a segment, Delete removes one. Ctrl+wheel zooms the timeline.";

    readonly FfmpegRunner runner = new();
    readonly StillCapture stills;
    readonly FilmstripService filmstrip;
    readonly EditPlayer player;
    readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer accurateTimer = new() { Interval = 220 };
    readonly System.Windows.Forms.Timer filmTimer = new() { Interval = 200 };
    readonly ToolTip tips = new() { ShowAlways = true };
    readonly string? startupPath;

    readonly ToolbarPanel toolbar = new();
    readonly StatusBar statusBar = new();
    readonly DropPrompt drop = new();
    readonly Panel previewHost = new();
    readonly PictureBox preview = new();
    readonly TransportBar transport = new();
    readonly TimelineControl timeline = new();
    readonly Card segmentsCard = new() { Title = "Segments" };
    readonly SegmentList segmentList = new();
    readonly FlatButton splitButton = new() { Text = "Split", AccessibleName = "Split the segment at the playhead", BackColor = Theme.Surface };
    readonly FlatButton newButton = new() { Text = "+ Segment", AccessibleName = "Start a new segment at the playhead", BackColor = Theme.Surface };
    readonly FlatButton resetButton = new() { Text = "Reset", AccessibleName = "Keep the whole video as one segment", BackColor = Theme.Surface };
    readonly Card selectedCard = new() { Title = "Selected segment" };
    readonly SegmentEditor editor = new();
    readonly Card exportCard = new() { Title = "Export" };
    readonly ExportBar exportBar = new();

    CancellationTokenSource loadCts = new();
    CancellationTokenSource previewCts = new();
    CancellationTokenSource filmCts = new();
    CancellationTokenSource exportCts = new();
    CancellationTokenSource waveCts = new();

    string? input;
    double duration;
    double fps;
    double aspect = 16 / 9d;
    bool exact;
    bool hasAudio;
    bool exporting;
    bool syncing;
    bool sized;
    bool layingOut;
    bool previewArmed;
    bool disposed;
    int loadGeneration;
    int previewGeneration;
    int waveGeneration;

    public MainForm(string? startupPath)
    {
        this.startupPath = startupPath;
        stills = new StillCapture(runner);
        filmstrip = new FilmstripService(runner, stills);
        player = new EditPlayer(runner, preview);
        Font = Theme.Ui(10);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Text = "VideoTrimmer";
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
            var key = DesiredStrip();
            if (key != filmstrip.Current && key != filmstrip.Building)
                StartFilmstrip(key);
        };
        if (runner.Ffmpeg is null || runner.Ffprobe is null)
            SetStatus("FFmpeg not found — install it or place ffmpeg.exe and ffprobe.exe beside this app.", StatusKind.Error);
        else
            SetStatus("Ready.", StatusKind.Info);
        StartUpdateChecks();
    }

    void BuildUi()
    {
        drop.OpenRequested += (_, _) => Browse();
        previewHost.Visible = false;
        previewHost.BackColor = Theme.Border;
        previewHost.Padding = new Padding(1);
        preview.Dock = DockStyle.Fill;
        preview.BackColor = Theme.Background;
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        preview.TabStop = false;
        previewHost.Controls.Add(preview);
        transport.Visible = false;
        timeline.Visible = false;

        segmentsCard.Controls.AddRange([segmentList, splitButton, newButton, resetButton]);
        selectedCard.Controls.Add(editor);
        exportCard.Controls.Add(exportBar);
        foreach (var card in new[] { segmentsCard, selectedCard, exportCard })
            card.Visible = false;

        Controls.AddRange([toolbar, drop, previewHost, transport, timeline, segmentsCard, selectedCard, exportCard, statusBar]);

        toolbar.Open.Click += (_, _) => Browse();
        timeline.ValuesChanged += (_, _) => OnTimelineChanged();
        timeline.InteractionEnded += (_, _) => OnTimelineInteractionEnded();
        timeline.ViewChanged += (_, _) => OnViewChanged();
        timeline.MouseDown += (_, _) => StopPlayback();
        preview.MouseWheel += OnWheel;
        preview.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && timeline.HasMedia) TogglePlayback(); };

        transport.Play.Click += (_, _) => TogglePlayback();
        transport.Previous.Click += (_, _) => JumpCut(-1);
        transport.Next.Click += (_, _) => JumpCut(1);
        transport.ZoomIn.Click += (_, _) => timeline.ZoomBy(2);
        transport.ZoomOut.Click += (_, _) => timeline.ZoomBy(0.5);
        transport.Fit.Click += (_, _) => timeline.ZoomToFit();
        player.FrameShown += frame =>
        {
            timeline.ShowPlaybackFrame(frame);
            UpdateReadout(frame);
        };
        player.Ended += OnPlaybackEnded;

        splitButton.Click += (_, _) => SplitSegment();
        newButton.Click += (_, _) => NewSegment();
        resetButton.Click += (_, _) => ResetSegments();
        segmentList.SelectRequested += (_, index) =>
        {
            StopPlayback();
            if (timeline.SelectSegment(index)) SchedulePreview(true);
        };
        segmentList.RemoveRequested += (_, index) => RemoveSegment(index);

        editor.SetIn.Click += (_, _) => Edit(() => timeline.SetInToPlayhead());
        editor.SetOut.Click += (_, _) => Edit(() => timeline.SetOutToPlayhead());
        editor.InEarlier.Click += (_, _) => Edit(() => timeline.NudgeIn(-1));
        editor.InLater.Click += (_, _) => Edit(() => timeline.NudgeIn(1));
        editor.OutEarlier.Click += (_, _) => Edit(() => timeline.NudgeOut(-1));
        editor.OutLater.Click += (_, _) => Edit(() => timeline.NudgeOut(1));
        editor.InTime.Box.Leave += (_, _) => CommitIn(timecode: true);
        editor.InFrame.Box.Leave += (_, _) => CommitIn(timecode: false);
        editor.OutTime.Box.Leave += (_, _) => CommitOut(timecode: true);
        editor.OutFrame.Box.Leave += (_, _) => CommitOut(timecode: false);

        exportBar.Fast.Click += (_, _) => ChooseExact(false);
        exportBar.Exact.Click += (_, _) => ChooseExact(true);
        exportBar.Export.Click += (_, _) => Export();
        exportBar.Cancel.Click += (_, _) => CancelExport();
        exportBar.ExactOptionsChanged += (_, _) => LayoutChrome();
        AcceptButton = exportBar.Export;
        CancelButton = exportBar.Cancel;
        ChooseExact(false);

        tips.SetToolTip(transport.Play, "Play the kept segments back to back, as the export will join them (Space)");
        tips.SetToolTip(transport.Previous, "Previous cut (,)");
        tips.SetToolTip(transport.Next, "Next cut (.)");
        tips.SetToolTip(transport.ZoomIn, "Zoom in (+ or Ctrl+wheel)");
        tips.SetToolTip(transport.ZoomOut, "Zoom out (− or Ctrl+wheel)");
        tips.SetToolTip(transport.Fit, "Show the whole video (0)");
        tips.SetToolTip(splitButton, "Split the segment under the playhead in two (S)");
        tips.SetToolTip(newButton, "Start a new segment at the playhead (N)");
        tips.SetToolTip(resetButton, "Throw away the segments and keep the whole video");
        tips.SetToolTip(editor.SetIn, "Set the in point to the current frame (I)");
        tips.SetToolTip(editor.SetOut, "Include the current frame and end here (O)");
        tips.SetToolTip(editor.InEarlier, "One frame earlier");
        tips.SetToolTip(editor.InLater, "One frame later");
        tips.SetToolTip(editor.OutEarlier, "One frame earlier");
        tips.SetToolTip(editor.OutLater, "One frame later");
        tips.SetToolTip(timeline, "Drag the handles to trim. Drag the highlighted span to slide it. Double-click a dashed segment to edit it. Ctrl+wheel zooms; drag the bar on top to pan.");
        tips.SetToolTip(exportBar.Fast, ExportModeCopy.FastExplanation);
        tips.SetToolTip(exportBar.Exact, ExportModeCopy.ExactBaseExplanation);
        tips.SetToolTip(exportBar.QualityHigh, ExactExportPresets.Hint(ExactQuality.High));
        tips.SetToolTip(exportBar.QualityBalanced, ExactExportPresets.Hint(ExactQuality.Balanced));
        tips.SetToolTip(exportBar.QualitySmall, ExactExportPresets.Hint(ExactQuality.Small));
        tips.SetToolTip(exportBar.CopyAudio, "Keep the original audio instead of re-encoding to AAC.");

        WireDrop(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (sized) return;
        sized = true;
        MinimumSize = new Size(S(980), S(700));
        Size = new Size(S(1320), S(860));
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
        StopPlayback();
        TryCancel(loadCts);
        TryCancel(previewCts);
        TryCancel(filmCts);
        TryCancel(exportCts);
        TryCancel(waveCts);
        StopUpdateChecks();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        LaunchPendingUpdate();
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
            CancelAndDispose(waveCts);
            previewTimer.Stop();
            accurateTimer.Stop();
            filmTimer.Stop();
            preview.Image = null;
            player.Dispose();
            timeline.UseStrip(null, 0, 0);
            ClearPictures();
            previewTimer.Dispose();
            accurateTimer.Dispose();
            filmTimer.Dispose();
            tips.Dispose();
            updateTimer.Dispose();
            updateLifetime.Dispose();
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
            case Keys.Space:
                TogglePlayback();
                return true;
            case Keys.Escape when player.IsPlaying:
                StopPlayback();
                return true;
            case Keys.Oemcomma:
                JumpCut(-1);
                return true;
            case Keys.OemPeriod:
                JumpCut(1);
                return true;
            case Keys.Oemplus:
            case Keys.Add:
                timeline.ZoomBy(2);
                return true;
            case Keys.OemMinus:
            case Keys.Subtract:
                timeline.ZoomBy(0.5);
                return true;
            case Keys.D0:
            case Keys.NumPad0:
                timeline.ZoomToFit();
                return true;
            case Keys.I:
                Edit(() => timeline.SetInToPlayhead());
                return true;
            case Keys.O:
                Edit(() => timeline.SetOutToPlayhead());
                return true;
            case Keys.Left:
                Edit(() => timeline.NudgePlayhead(shift ? -second : -1));
                return true;
            case Keys.Right:
                Edit(() => timeline.NudgePlayhead(shift ? second : 1));
                return true;
            case Keys.Home:
                Edit(() => timeline.JumpPlayheadToIn());
                return true;
            case Keys.End:
                Edit(() => timeline.JumpPlayheadToOut());
                return true;
            case Keys.OemOpenBrackets:
                Edit(() => timeline.NudgeNearestEdge(-1));
                return true;
            case Keys.OemCloseBrackets:
                Edit(() => timeline.NudgeNearestEdge(1));
                return true;
            case Keys.S:
                SplitSegment();
                return true;
            case Keys.N:
                NewSegment();
                return true;
            case Keys.Delete:
            case Keys.Back:
                RemoveSegment(timeline.ActiveIndex);
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>Stops playback, applies an edit or move, and refreshes the still.</summary>
    void Edit(Action change)
    {
        if (exporting) return;
        StopPlayback();
        change();
        SchedulePreview(true);
    }

    void LayoutChrome()
    {
        if (layingOut || !IsHandleCreated) return;
        layingOut = true;
        try
        {
            var loaded = timeline.HasMedia;
            var toolbarHeight = toolbar.Visible ? S(56) : 0;
            var statusHeight = S(32);
            toolbar.SetBounds(0, 0, ClientSize.Width, toolbarHeight);
            statusBar.SetBounds(0, ClientSize.Height - statusHeight, ClientSize.Width, statusHeight);
            statusBar.PerformLayout();
            var top = toolbarHeight;
            var bottom = ClientSize.Height - statusHeight;
            var pad = S(16);
            var gap = S(12);
            if (!loaded)
            {
                drop.SetBounds(pad, top + pad, Math.Max(10, ClientSize.Width - pad * 2), Math.Max(10, bottom - top - pad * 2));
                return;
            }

            // A sidebar of cards on the right, the preview, transport and timeline on the left.
            var sideWidth = S(372);
            var sideLeft = ClientSize.Width - pad - sideWidth;
            var mainWidth = Math.Max(S(320), sideLeft - gap - pad);
            var transportHeight = S(40);
            var timelineHeight = Math.Clamp((bottom - top) / 4, S(132), S(196));
            var timelineTop = bottom - pad - timelineHeight;
            var transportTop = timelineTop - S(8) - transportHeight;
            previewHost.SetBounds(pad, top + pad, mainWidth, Math.Max(S(80), transportTop - S(10) - top - pad));
            transport.SetBounds(pad, transportTop, mainWidth, transportHeight);
            timeline.SetBounds(pad, timelineTop, mainWidth, timelineHeight);

            var innerWidth = sideWidth - segmentsCard.Pad * 2;
            exportBar.Width = innerWidth;
            var exportHeight = exportCard.ContentTop + exportBar.MeasureHeight(innerWidth) + exportCard.Pad;
            var selectedHeight = selectedCard.ContentTop + editor.MeasureHeight() + selectedCard.Pad;
            var y = top + pad;
            var segmentsHeight = Math.Max(S(170), bottom - pad - y - selectedHeight - exportHeight - gap * 2);
            segmentsCard.SetBounds(sideLeft, y, sideWidth, segmentsHeight);
            selectedCard.SetBounds(sideLeft, segmentsCard.Bottom + gap, sideWidth, selectedHeight);
            exportCard.SetBounds(sideLeft, selectedCard.Bottom + gap, sideWidth, exportHeight);

            var content = segmentsCard.ContentBounds;
            var buttonHeight = S(34);
            var buttonWidth = (content.Width - S(8) * 2) / 3;
            var buttonTop = content.Bottom - buttonHeight;
            splitButton.SetBounds(content.Left, buttonTop, buttonWidth, buttonHeight);
            newButton.SetBounds(splitButton.Right + S(8), buttonTop, buttonWidth, buttonHeight);
            resetButton.SetBounds(newButton.Right + S(8), buttonTop, content.Right - newButton.Right - S(8), buttonHeight);
            segmentList.SetBounds(content.Left, content.Top, content.Width, Math.Max(S(40), buttonTop - S(10) - content.Top));
            editor.Bounds = selectedCard.ContentBounds;
            exportBar.Bounds = exportCard.ContentBounds;
            exportBar.PerformLayout();
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
        Edit(() => timeline.NudgePlayhead(e.Delta > 0 ? -step : step));
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
            SetStatus("Bundled FFprobe is missing. Reinstall VideoTrimmer.", StatusKind.Error);
            MessageBox.Show(this, "The bundled FFprobe executable is missing. Please reinstall VideoTrimmer.", "VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        StopPlayback();
        ReplaceCts(ref loadCts);
        var token = loadCts.Token;
        var generation = ++loadGeneration;
        SetStatus("Reading metadata…", StatusKind.Busy);
        try
        {
            var media = await MediaProbe.ProbeAsync(runner, path, token);
            if (generation != loadGeneration || IsDisposed) return;
            ApplyMedia(path, media);
        }
        catch (OperationCanceledException)
        {
            // A newer file replaced this request.
        }
        catch (Exception ex)
        {
            if (generation != loadGeneration || IsDisposed) return;
            SetStatus("Could not read video: " + ex.Message, StatusKind.Error);
            MessageBox.Show(this, "Video metadata could not be read.\n\n" + ex.Message, "VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ApplyMedia(string path, MediaInfo media)
    {
        input = path;
        duration = media.Duration;
        fps = media.Fps;
        hasAudio = media.HasAudio;
        aspect = media.Aspect > 0 ? media.Aspect : 16 / 9d;
        CancelPreviewJobs();
        ClearPictures();
        timeline.Locked = false;
        timeline.SetClip(media.Duration, media.Fps, media.Frames);
        toolbar.FileName.Text = Path.GetFileName(path);
        tips.SetToolTip(toolbar.FileName, path);
        toolbar.Meta.Text = string.Format(CultureInfo.CurrentCulture, "{0}   ·   {1:0.###} fps   ·   {2:N0} frames{3}",
            TimeMath.Format(media.Frames, media.Fps), media.Fps, media.Frames, media.HasAudio ? "" : "   ·   no audio");
        toolbar.Visible = true;
        drop.Visible = false;
        foreach (Control control in new Control[] { previewHost, transport, timeline, segmentsCard, selectedCard, exportCard })
            control.Visible = true;
        exportBar.Export.Enabled = true;
        LayoutChrome();
        WriteBoxes();
        UpdateReadouts();
        SchedulePreview(true);
        StartFilmstrip(DesiredStrip());
        StartWaveform(path, media.Duration, media.HasAudio);
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

    void OnViewChanged()
    {
        var zoom = timeline.Zoom;
        transport.ZoomLabel.Text = zoom < 1.05 ? "1×" : zoom < 10 ? $"{zoom:0.#}×" : $"{zoom:0}×";
        QueueFilmstrip();
    }

    void UpdateReadouts()
    {
        if (!timeline.HasMedia)
        {
            editor.Selected.Text = "";
            transport.Readout.Text = "";
            return;
        }

        var all = timeline.AllSegments;
        var kept = timeline.ExportSegments;
        segmentList.SetSegments(all, timeline.ActiveIndex, timeline.Fps);
        exportBar.SetSegmentCount(kept.Count);
        var keptFrames = CutList.KeptFrames(kept);
        segmentsCard.Detail = $"{all.Count}  ·  {TimeMath.Format(keptFrames, timeline.Fps)} kept";
        var frames = timeline.OutFrame - timeline.InFrame;
        var unit = frames == 1 ? "frame" : "frames";
        editor.Selected.Text = $"Segment {timeline.ActiveIndex + 1}  ·  {TimeMath.Format(frames, timeline.Fps)}  ·  {frames:N0} {unit}";
        UpdateReadout(timeline.PlayheadFrame);
    }

    /// <summary>The playhead in the source, and where it lands in the export.</summary>
    void UpdateReadout(long frame)
    {
        var kept = timeline.ExportSegments;
        var edited = CutList.EditedFrame(kept, frame);
        var cut = kept.Any(segment => segment.Contains(frame)) ? "" : "   (cut)";
        transport.Readout.Text = $"{TimeMath.Format(frame, timeline.Fps)}  frame {frame:N0}     Edit {TimeMath.Format(edited, timeline.Fps)} / {TimeMath.Format(CutList.KeptFrames(kept), timeline.Fps)}{cut}";
    }

    void WriteBoxes()
    {
        if (!timeline.HasMedia) return;
        SetBox(editor.InTime.Box, TimeMath.Format(timeline.InFrame, timeline.Fps));
        SetBox(editor.InFrame.Box, timeline.InFrame.ToString(CultureInfo.CurrentCulture));
        SetBox(editor.OutTime.Box, TimeMath.Format(timeline.OutFrame, timeline.Fps));
        SetBox(editor.OutFrame.Box, timeline.OutFrame.ToString(CultureInfo.CurrentCulture));
    }

    static void SetBox(TextBox box, string value)
    {
        if (box.Text != value) box.Text = value;
    }

    void CommitBox(TextBox box)
    {
        if (box == editor.InTime.Box) CommitIn(true);
        else if (box == editor.InFrame.Box) CommitIn(false);
        else if (box == editor.OutTime.Box) CommitOut(true);
        else if (box == editor.OutFrame.Box) CommitOut(false);
    }

    void CommitIn(bool timecode)
    {
        if (syncing || exporting || !timeline.HasMedia) return;
        if (!TryRead(timecode ? editor.InTime.Box : editor.InFrame.Box, timecode, out var frame))
        {
            WriteBoxes();
            return;
        }

        StopPlayback();
        syncing = true;
        timeline.SetInFrame(frame, followPlayhead: true);
        WriteBoxes();
        syncing = false;
        SchedulePreview(true);
    }

    void CommitOut(bool timecode)
    {
        if (syncing || exporting || !timeline.HasMedia) return;
        if (!TryRead(timecode ? editor.OutTime.Box : editor.OutFrame.Box, timecode, out var frame))
        {
            WriteBoxes();
            return;
        }

        StopPlayback();
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
        exportBar.SetMode(value);
        LayoutChrome();
    }

    void TogglePlayback()
    {
        if (!timeline.HasMedia || exporting || input is null) return;
        if (player.IsPlaying)
        {
            StopPlayback();
            return;
        }

        StartPlayback(timeline.PlayheadFrame);
    }

    void StartPlayback(long frame)
    {
        if (input is null) return;
        previewTimer.Stop();
        accurateTimer.Stop();
        previewArmed = false;
        ReplaceCts(ref previewCts);
        previewGeneration++;
        var width = Math.Clamp(preview.ClientSize.Width, 320, 960) & ~3;
        var height = Math.Max(2, (int)Math.Round(width / aspect / 2) * 2);
        if (player.Play(input, timeline.ExportSegments, timeline.Fps, frame, new Size(width, height), hasAudio))
        {
            transport.SetPlaying(true);
            SetStatus("Playing the edit. Space pauses; , and . jump between cuts.", StatusKind.Info);
        }
    }

    void StopPlayback()
    {
        if (player.IsPlaying) player.Stop();
    }

    void OnPlaybackEnded(PlaybackEnd end, string? error)
    {
        transport.SetPlaying(false);
        if (IsDisposed) return;
        if (end == PlaybackEnd.Failed) SetStatus("Playback stopped: " + error, StatusKind.Error);
        else if (end == PlaybackEnd.Finished) SetStatus("End of the edit.", StatusKind.Info);
        else SetStatus(ReadyHint, StatusKind.Info);
        // Playback moved the playhead quietly; bring the still and the readouts up to date.
        UpdateReadouts();
        SchedulePreview(true);
    }

    /// <summary>
    /// Previous or next cut. While playing, it skips between the joins of the edit; while paused, it also stops at
    /// each segment's out point so both sides of every cut can be checked.
    /// </summary>
    void JumpCut(int direction)
    {
        if (!timeline.HasMedia || exporting) return;
        var playing = player.IsPlaying;
        var from = timeline.PlayheadFrame;
        // Going back from just after a join goes to the join before it, not the one just passed.
        if (direction < 0 && playing) from -= (long)Math.Round(timeline.Fps * 0.75);
        var target = timeline.CutAfter(from, direction, startsOnly: playing);
        if (target is null)
        {
            SetStatus(direction > 0 ? "No more cuts after this point." : "No more cuts before this point.", StatusKind.Info);
            return;
        }

        if (playing)
        {
            StartPlayback(target.Value);
            return;
        }

        timeline.SetPlayhead(target.Value);
        SchedulePreview(true);
    }

    void SchedulePreview(bool accurate)
    {
        if (!timeline.HasMedia || IsDisposed || player.IsPlaying) return;
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
        if (!timeline.HasMedia || IsDisposed || player.IsPlaying) return;
        _ = GrabPreviewAsync(timeline.PlayheadFrame, accurate: false);
    }

    void OnAccurateTick()
    {
        accurateTimer.Stop();
        if (!timeline.HasMedia || IsDisposed || player.IsPlaying) return;
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
        stills.Cache.Put(frame, width, bitmap, accurate);
    }

    void ShowPreview(Bitmap bitmap)
    {
        // A still that lands after playback started must not cover the playing frames.
        if (IsDisposed || player.IsPlaying) return;
        preview.Image = bitmap;
        stills.Cache.SetShown(bitmap);
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
        var key = DesiredStrip();
        if (key == filmstrip.Current || key == filmstrip.Building) return;
        if (filmstrip.TryShowCached(timeline, key)) return;
        filmTimer.Stop();
        filmTimer.Start();
    }

    /// <summary>Enough thumbnails to fill the timeline's width, for the frames it shows now.</summary>
    StripKey DesiredStrip()
    {
        var slot = Math.Max(S(104), 48);
        var count = Math.Clamp(timeline.FilmWidth / slot, 6, 32);
        return new StripKey(count, timeline.VisibleStart, Math.Max(timeline.VisibleStart + 1, timeline.VisibleEnd));
    }

    void StartFilmstrip(StripKey key)
    {
        if (input is null) return;
        ReplaceCts(ref filmCts);
        filmstrip.Start(
            this,
            timeline,
            input,
            duration,
            key,
            filmCts.Token,
            onProgress: (filled, total) => { if (!player.IsPlaying) SetStatus($"Building the filmstrip… {filled}/{total}", StatusKind.Busy); },
            onCompleted: (any, failure) =>
            {
                if (exporting || player.IsPlaying) return;
                SetStatus(any ? ReadyHint : failure, any ? StatusKind.Info : StatusKind.Error);
            },
            isExporting: () => exporting);
    }

    void CancelPreviewJobs()
    {
        StopPlayback();
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
        stills.Cache.SetShown(null);
        stills.Clear();
        filmstrip.Clear(timeline);
    }

    async void Export()
    {
        if (exporting || !timeline.HasMedia || input is null) return;
        if (runner.Ffmpeg is null)
        {
            MessageBox.Show(this, "FFmpeg is required. See README.md.", "VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        StopPlayback();
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
        var segments = timeline.ExportSegments;
        var mode = exact ? "Encoding exact frames" : "Saving fast copy";
        if (segments.Count > 1) mode += $" of {segments.Count} segments";
        exporting = true;
        timeline.Locked = true;
        exportBar.Export.Enabled = false;
        exportBar.Cancel.Enabled = true;
        exportBar.Cancel.Visible = true;
        exportBar.Fast.Enabled = false;
        exportBar.Exact.Enabled = false;
        exportBar.SetExactOptionsEnabled(false);
        statusBar.Progress.Fraction = 0;
        statusBar.Progress.Visible = true;
        statusBar.PerformLayout();
        LayoutChrome();
        SetStatus(mode + "…", StatusKind.Busy);
        var exactOptions = exportBar.ExactOptions;
        ExportPlan? plan = null;
        try
        {
            plan = ExportService.Plan(input, output, segments, timeline.Fps, hasAudio, exact, exactOptions);
            if (plan.WorkDirectory is not null)
            {
                var work = Directory.CreateDirectory(plan.WorkDirectory);
                work.Attributes |= FileAttributes.Hidden;
                File.WriteAllText(plan.ListFile!, plan.ListText);
            }

            var totalWeight = Math.Max(0.001, plan.Steps.Sum(step => step.Weight));
            var done = 0.0;
            for (var i = 0; i < plan.Steps.Count; i++)
            {
                var step = plan.Steps[i];
                var before = done;
                var phase = plan.Steps.Count == 1 ? mode : i < plan.Steps.Count - 1 ? $"Copying segment {i + 1} of {plan.Steps.Count - 1}" : "Joining segments";
                var progress = new Progress<TimeSpan>(elapsed =>
                {
                    if (!exporting || IsDisposed) return;
                    var within = Math.Clamp(elapsed.TotalSeconds / Math.Max(0.001, step.Seconds), 0, 1);
                    var fraction = Math.Clamp((before + within * step.Weight) / totalWeight, 0, 1);
                    statusBar.Progress.Fraction = fraction;
                    SetStatus($"{phase}… {(int)Math.Round(fraction * 100)}%", StatusKind.Busy);
                });
                SetStatus(phase + "…", StatusKind.Busy);
                await runner.RunAsync(runner.Ffmpeg, step.Arguments, token, progress);
                done += step.Weight;
            }

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
            ExportService.TryDeleteDirectory(plan?.WorkDirectory);
            exporting = false;
            timeline.Locked = false;
            if (!IsDisposed)
            {
                exportBar.Export.Enabled = timeline.HasMedia;
                exportBar.Cancel.Enabled = false;
                exportBar.Cancel.Visible = false;
                exportBar.Fast.Enabled = true;
                exportBar.Exact.Enabled = true;
                exportBar.SetExactOptionsEnabled(true);
                statusBar.Progress.Visible = false;
                statusBar.PerformLayout();
                LayoutChrome();
            }
        }
    }

    void CancelExport()
    {
        if (!exporting) return;
        exportCts.Cancel();
    }

    void SplitSegment()
    {
        if (!timeline.HasMedia || exporting) return;
        StopPlayback();
        if (timeline.SplitAtPlayhead())
            SetStatus("Split. Drag a handle to open a gap, or select a piece and press Delete to cut it.", StatusKind.Info);
        else
            SetStatus("Move the playhead inside a segment (not on its edge) to split it.", StatusKind.Info);
    }

    void NewSegment()
    {
        if (!timeline.HasMedia || exporting) return;
        StopPlayback();
        if (timeline.NewSegmentAtPlayhead())
        {
            SetStatus("New segment. Set its out point with O or drag its handle.", StatusKind.Info);
            SchedulePreview(true);
        }
        else
        {
            SetStatus("The whole video is already kept. Split a segment or trim one to make room.", StatusKind.Info);
        }
    }

    void RemoveSegment(int index)
    {
        if (!timeline.HasMedia || exporting) return;
        StopPlayback();
        if (timeline.RemoveSegment(index))
        {
            SetStatus("Segment removed.", StatusKind.Info);
            SchedulePreview(true);
        }
        else
        {
            SetStatus("The last segment can't be removed — trim it instead.", StatusKind.Info);
        }
    }

    void ResetSegments()
    {
        if (!timeline.HasMedia || exporting) return;
        if (timeline.AllSegments.Count > 1 && MessageBox.Show(this, "Remove every segment and keep the whole video?", "VideoTrimmer",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        StopPlayback();
        timeline.ResetSegments();
        WriteBoxes();
        SetStatus("Keeping the whole video.", StatusKind.Info);
    }

    /// <summary>Reads the audio once and draws it as the waveform under the filmstrip.</summary>
    async void StartWaveform(string path, double length, bool audio)
    {
        ReplaceCts(ref waveCts);
        var generation = ++waveGeneration;
        if (runner.Ffmpeg is null || !audio) return;
        var token = waveCts.Token;
        bool Current() => generation == waveGeneration && !IsDisposed;
        void Note(string text)
        {
            if (!Current()) return;
            timeline.LaneNote = text;
            timeline.Invalidate();
        }

        try
        {
            Note("Reading audio…");
            var progress = new Progress<double>(p => Note($"Reading audio… {p:P0}"));
            var envelope = await Task.Run(() => AudioWaveform.ReadEnvelopeAsync(runner, path, length, progress, token), token);
            if (!Current()) return;
            // Progress reports are still queued on the UI thread; clear the note behind them.
            BeginInvoke(() => Note(""));
            timeline.SetEnvelope(envelope);
        }
        catch (OperationCanceledException)
        {
            // A newer file replaced this one, or the app is closing.
        }
        catch (Exception)
        {
            Note("No waveform for this audio");
        }
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
