using System.Globalization;

namespace VideoTrimmer;

public sealed partial class MainForm : Form
{
    static readonly string VideoFilter = "Video files|" + string.Join(";", OutputFormats.VideoExtensions.Select(e => "*" + e)) + "|All files|*.*";
    const string ReadyHint = "Ready";

    readonly FfmpegRunner runner = new();
    readonly StillCapture stills;
    readonly FilmstripService filmstrip;
    readonly EditPlayer player;
    readonly System.Windows.Forms.Timer previewTimer = new() { Interval = 60 };
    readonly System.Windows.Forms.Timer accurateTimer = new() { Interval = 220 };
    readonly System.Windows.Forms.Timer filmTimer = new() { Interval = 200 };
    readonly ToolTip tips = new() { ShowAlways = true };
    readonly string? startupPath;
    readonly AppSettings settings;

    readonly TopBar topBar = new();
    readonly DropPrompt drop = new();
    readonly Panel previewHost = new();
    readonly PictureBox preview = new();
    readonly TransportBar transport = new();
    readonly TimelineControl timeline = new();
    readonly SidePanel side = new();
    // The backdrop takes its color from the still, but only once the still has settled.
    readonly System.Windows.Forms.Timer ambientTimer = new() { Interval = 350 };
    readonly System.Windows.Forms.Timer anchorTimer = new() { Interval = 250 };
    // The backdrop is rebuilt once a resize settles, not on every step of it.
    readonly System.Windows.Forms.Timer backdropTimer = new() { Interval = 150 };

    SegmentList segmentList => side.List;
    SegmentEditor editor => side.Editor;
    ExportBar exportBar => side.Export;

    CancellationTokenSource loadCts = new();
    CancellationTokenSource previewCts = new();
    CancellationTokenSource filmCts = new();
    CancellationTokenSource exportCts = new();
    CancellationTokenSource waveCts = new();
    CancellationTokenSource anchorCts = new();

    // The selected segment's own thumbnails, from its in frame to its out frame.
    Bitmap?[]? anchorStrip;
    AnchorPlan? anchorKey;
    IReadOnlyList<SubtitleTrack> subtitles = [];

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
    /// <summary>Between the start and the end of dragging the window's edge (or moving it).</summary>
    bool sizing;
    Size backdropSize;
    string? readoutTip;
    int loadGeneration;
    int previewGeneration;
    int waveGeneration;
    /// <summary>
    /// Off for now: reading the waveform demuxes the whole file, which on a large MKV means reading every byte of it.
    /// </summary>
    static readonly bool WaveformEnabled = false;
    /// <summary>The waveform read so far, while it is still being read (or paused for an export); otherwise null.</summary>
    List<float>? waveLevels;
    Task waveRead = Task.CompletedTask;

    /// <param name="checkForUpdates">Off for <c>--render</c>, which must not take the last update's result or go online.</param>
    /// <param name="settings">The saved settings, already applied to <see cref="Theme"/>.</param>
    internal MainForm(string? startupPath, bool checkForUpdates = true, AppSettings? settings = null)
    {
        this.startupPath = startupPath;
        this.settings = settings ?? new AppSettings();
        Theme.Changed += OnThemeChanged;
        stills = new StillCapture(runner);
        filmstrip = new FilmstripService(runner, stills);
        player = new EditPlayer(runner, preview);
        Font = Theme.Ui(10);
        BackColor = Theme.Base;
        ForeColor = Theme.Text;
        Text = "VideoTrimmer";
        DoubleBuffered = true;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        AllowDrop = true;
        BuildUi();
        previewTimer.Tick += (_, _) => OnPreviewTick();
        accurateTimer.Tick += (_, _) => OnAccurateTick();
        ambientTimer.Tick += (_, _) => OnAmbientTick();
        anchorTimer.Tick += (_, _) => OnAnchorTick();
        backdropTimer.Tick += (_, _) =>
        {
            backdropTimer.Stop();
            if (!sizing) RebuildBackdrop();
        };
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
        if (checkForUpdates) StartUpdateChecks();
        else ShowVersion();
    }

    void BuildUi()
    {
        drop.OpenRequested += (_, _) => Browse();
        previewHost.Visible = false;
        previewHost.BackColor = Theme.WellBorder;
        previewHost.Padding = new Padding(1);
        preview.Dock = DockStyle.Fill;
        preview.BackColor = Theme.Well;
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        preview.TabStop = false;
        previewHost.Controls.Add(preview);
        transport.Visible = false;
        timeline.Visible = false;
        timeline.ShowLane = WaveformEnabled;
        side.Visible = false;

        Controls.AddRange([topBar, drop, previewHost, transport, timeline, side]);

        topBar.Open.Click += (_, _) => Browse();
        WireMenus();
        WireCaptionButtons();
        timeline.ValuesChanged += (_, _) => OnTimelineChanged();
        timeline.InteractionEnded += (_, _) => OnTimelineInteractionEnded();
        timeline.ViewChanged += (_, _) => OnViewChanged();
        timeline.MouseDown += (_, _) => StopPlayback();
        preview.MouseWheel += OnWheel;
        preview.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left && timeline.HasMedia) TogglePlayback(); };

        transport.Play.Click += (_, _) => TogglePlayback();
        transport.Previous.Click += (_, _) => JumpCut(-1);
        transport.Next.Click += (_, _) => JumpCut(1);
        transport.Split.Click += (_, _) => SplitSegment();
        transport.NewSegment.Click += (_, _) => NewSegment();
        transport.ZoomIn.Click += (_, _) => timeline.ZoomBy(2);
        transport.ZoomOut.Click += (_, _) => timeline.ZoomBy(0.5);
        transport.Fit.Click += (_, _) => timeline.ZoomToSelection();
        player.FrameShown += frame =>
        {
            timeline.ShowPlaybackFrame(frame);
            UpdateReadout(frame);
        };
        player.Ended += OnPlaybackEnded;

        side.Reset.Click += (_, _) => ResetSegments();
        segmentList.SelectRequested += (_, click) => OnSegmentClicked(click);
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

        exportBar.ModePicked += ChooseExact;
        exportBar.Export.Click += (_, _) => Export();
        exportBar.Cancel.Click += (_, _) => CancelExport();
        exportBar.OptionsChanged += (_, _) => LayoutChrome();
        AcceptButton = exportBar.Export;
        CancelButton = exportBar.Cancel;
        ChooseExact(false);

        tips.SetToolTip(topBar.Open, "Open a video (Ctrl+O)");
        tips.SetToolTip(transport.Play, "Play the kept segments back to back, as the export will join them (Space)");
        tips.SetToolTip(transport.Previous, "Previous cut (,)");
        tips.SetToolTip(transport.Next, "Next cut (.)");
        tips.SetToolTip(transport.ZoomIn, "Zoom in (+ or Ctrl+wheel)");
        tips.SetToolTip(transport.ZoomOut, "Zoom out (− or Ctrl+wheel)");
        tips.SetToolTip(transport.Fit, "Fit the selection to the timeline; again to show the whole video (0)");
        tips.SetToolTip(transport.Split, "Split the segment under the playhead in two (S)");
        tips.SetToolTip(transport.NewSegment, "Start a new segment at the playhead (N)");
        tips.SetToolTip(side.Reset, "Throw away the segments and keep the whole video");
        tips.SetToolTip(editor.SetIn, "Set the in point to the current frame (I)");
        tips.SetToolTip(editor.SetOut, "Include the current frame and end here (O)");
        tips.SetToolTip(editor.InEarlier, "One frame earlier");
        tips.SetToolTip(editor.InLater, "One frame later");
        tips.SetToolTip(editor.OutEarlier, "One frame earlier");
        tips.SetToolTip(editor.OutLater, "One frame later");
        tips.SetToolTip(timeline, "Drag the handles to trim. Drag the highlighted span to slide it. Click another segment to edit it. Ctrl+wheel zooms; drag the bar on top to pan.");
        tips.SetToolTip(exportBar.Mode, ExportModeCopy.FastTitle + ": " + ExportModeCopy.FastExplanation + "\n" + ExportModeCopy.ExactTitle + ": " + ExportModeCopy.ExactBaseExplanation);
        tips.SetToolTip(exportBar.Quality, string.Join("\n", new[] { ExactQuality.High, ExactQuality.Balanced, ExactQuality.Small }
            .Select(quality => ExactExportPresets.Title(quality) + ": " + ExactExportPresets.Hint(quality))));
        tips.SetToolTip(exportBar.CopyAudio, "Keep the original audio instead of re-encoding to AAC.");
        tips.SetToolTip(exportBar.Subtitles, "Draw a subtitle track into the picture, so it shows in every player.");
        tips.SetToolTip(exportBar.Format, "The container to save as. Fast copy rewraps the streams; Exact frames re-encodes them.");

        WireDrop(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (sized) return;
        sized = true;
        WindowChrome.Merge(this);
        MinimumSize = new Size(S(980), S(700));
        Size = new Size(S(1320), S(860));
        CenterToScreen();
        LayoutChrome();
    }

    /// <summary>The window itself shows the backdrop in the gaps between its controls.</summary>
    protected override void OnPaintBackground(PaintEventArgs e) => Backdrop.Paint(e.Graphics, this);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LayoutChrome();
        if (!string.IsNullOrWhiteSpace(startupPath))
            OpenPath(startupPath);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The backdrop is stretched to the window, so every control's patch of it changes with the size.
        backdropTimer.Stop();
        backdropTimer.Start();
        topBar.SetMaximized(WindowState == FormWindowState.Maximized);
        LayoutChrome();
        Invalidate(true);
        QueueFilmstrip();
        QueueAnchor();
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        sizing = true;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        sizing = false;
        base.OnResizeEnd(e);
        // A move ends here too, and leaves the size as it was.
        if (ClientSize != backdropSize) RebuildBackdrop();
    }

    void RebuildBackdrop()
    {
        backdropTimer.Stop();
        backdropSize = ClientSize;
        Backdrop.Resize(ClientSize);
        Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutChrome();
        QueueFilmstrip();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason != CloseReason.WindowsShutDown && !ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        StopPlayback();
        TryCancel(loadCts);
        TryCancel(previewCts);
        TryCancel(filmCts);
        TryCancel(exportCts);
        TryCancel(waveCts);
        TryCancel(anchorCts);
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
            Theme.Changed -= OnThemeChanged;
            CancelAndDispose(loadCts);
            CancelAndDispose(previewCts);
            CancelAndDispose(filmCts);
            CancelAndDispose(exportCts);
            CancelAndDispose(waveCts);
            CancelAndDispose(anchorCts);
            previewTimer.Stop();
            accurateTimer.Stop();
            filmTimer.Stop();
            ambientTimer.Stop();
            anchorTimer.Stop();
            backdropTimer.Stop();
            ambientTimer.Dispose();
            anchorTimer.Dispose();
            backdropTimer.Dispose();
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

        if (MenuShortcut(keyData)) return true;

        if (keyData == Keys.Escape && exporting)
        {
            CancelExport();
            return true;
        }

        if (keyData == Keys.F1)
        {
            ShowShortcuts();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Oemcomma))
        {
            ShowSettings();
            return true;
        }

        if (keyData == (Keys.Control | Keys.A) && ActiveControl is not TextBox && timeline.HasMedia && !exporting)
        {
            timeline.PickAll();
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
            case Keys.Escape when timeline.ClearPicks():
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
                timeline.ZoomToSelection();
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
            var top = S(44);
            topBar.SetBounds(0, 0, ClientSize.Width, top);
            var bottom = ClientSize.Height;
            var pad = S(12);
            var gap = S(10);
            if (!loaded)
            {
                drop.SetBounds(pad, top + pad, Math.Max(10, ClientSize.Width - pad * 2), Math.Max(10, bottom - top - pad * 2));
                return;
            }

            // One glass sidebar on the right; the preview, the dock and the timeline stacked on the left.
            var sideWidth = S(352);
            var sideLeft = ClientSize.Width - pad - sideWidth;
            var mainWidth = Math.Max(S(320), sideLeft - pad - pad);
            var dockHeight = S(52);
            var timelineHeight = Math.Clamp((bottom - top) / 5, S(138), S(190));
            var timelineTop = bottom - pad - timelineHeight;
            var dockTop = timelineTop - gap - dockHeight;
            previewHost.SetBounds(pad, top + pad, mainWidth, Math.Max(S(80), dockTop - gap - top - pad));
            transport.SetBounds(pad, dockTop, mainWidth, dockHeight);
            timeline.SetBounds(pad, timelineTop, mainWidth, timelineHeight);
            side.SetBounds(sideLeft, top + pad, sideWidth, Math.Max(S(200), bottom - pad - top - pad));
            side.PerformLayout();
            // The readout's room changed with the dock.
            UpdateReadout(timeline.PlayheadFrame);
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
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0 && ConfirmDiscard())
            OpenPath(paths[0]);
    }

    void OnWheel(object? sender, MouseEventArgs e)
    {
        if (!timeline.HasMedia || exporting) return;
        var step = (ModifierKeys & Keys.Shift) == Keys.Shift ? timeline.FramesInOneSecond() : 1;
        Edit(() => timeline.NudgePlayhead(e.Delta > 0 ? -step : step));
    }

    void Browse()
    {
        if (exporting || !ConfirmDiscard()) return;
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

    async void LoadVideo(string path, PendingProject? project = null)
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
            if (project is not null) ApplyProject(project);
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
        subtitles = media.Subtitles;
        timeline.Aspect = aspect;
        CancelPreviewJobs();
        ClearPictures();
        timeline.Locked = false;
        timeline.SetClip(media.Duration, media.Fps, media.Frames);
        projectPath = null;
        history.Reset(timeline.State);
        ShowTitle();
        topBar.Meta.Text = string.Format(CultureInfo.CurrentCulture, "{0}  ·  {1:0.###} fps  ·  {2:N0} frames{3}{4}",
            TimeMath.Format(media.Frames, media.Fps), media.Fps, media.Frames, media.HasAudio ? "" : "  ·  no audio",
            media.Subtitles.Count switch { 0 => "", 1 => "  ·  1 subtitle track", var n => $"  ·  {n} subtitle tracks" });
        tips.SetToolTip(topBar.Meta, topBar.Meta.Text);
        topBar.PerformLayout();
        drop.Visible = false;
        foreach (Control control in new Control[] { previewHost, transport, timeline, side })
            control.Visible = true;
        exportBar.SetSource(Path.GetExtension(path), media);
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
        if (!timeline.IsDragging) TrackEdit();
        UpdateReadouts();
        SchedulePreview(false);
        QueueAnchor();
    }

    void OnTimelineInteractionEnded()
    {
        if (!syncing) WriteBoxes();
        TrackEdit();
        SchedulePreview(accurate: true);
    }

    void OnViewChanged()
    {
        var zoom = timeline.Zoom;
        transport.ZoomLabel.Text = zoom < 1.05 ? "1×" : zoom < 10 ? $"{zoom:0.#}×" : $"{zoom:0}×";
        QueueFilmstrip();
        QueueAnchor();
    }

    void UpdateReadouts()
    {
        if (!timeline.HasMedia)
        {
            side.EditorTitle = "";
            side.EditorDetail = "";
            transport.Readout.Text = "";
            return;
        }

        var all = timeline.AllSegments;
        var kept = timeline.ExportSegments;
        var picks = timeline.PickedSegments;
        segmentList.SetSegments(all, timeline.ActiveIndex, timeline.Fps, timeline.PickedIndices);
        exportBar.SetSegmentCount(picks.Count > 0 ? picks.Count : kept.Count);
        // While an export runs the button is its progress bar, and says so itself.
        if (!exporting)
        {
            var exportText = picks.Count > 0 ? $"Export {picks.Count} selected…" : all.Count > 1 ? $"Export {all.Count} segments…" : "Export…";
            if (exportBar.Export.Text != exportText) exportBar.Export.Text = exportText;
        }

        var keptFrames = CutList.KeptFrames(kept);
        side.SegmentsDetail = picks.Count > 0
            ? $"{picks.Count} of {all.Count} selected  ·  {TimeMath.Format(picks.Sum(p => p.Length), timeline.Fps)}"
            : $"{all.Count} kept  ·  {TimeMath.Format(keptFrames, timeline.Fps)}";
        var frames = timeline.OutFrame - timeline.InFrame;
        var unit = frames == 1 ? "frame" : "frames";
        side.EditorTitle = $"Segment {timeline.ActiveIndex + 1}";
        side.EditorDetail = $"{TimeMath.Format(frames, timeline.Fps)}  ·  {frames:N0} {unit}";
        UpdateReadout(timeline.PlayheadFrame);
    }

    /// <summary>
    /// The playhead in the source, and where it lands in the export. When the dock is too narrow it drops the word
    /// "frame", then the edit position, rather than end in an ellipsis; the tooltip keeps the full text.
    /// </summary>
    void UpdateReadout(long frame)
    {
        var kept = timeline.ExportSegments;
        var edited = CutList.EditedFrame(kept, frame);
        var cut = kept.Any(segment => segment.Contains(frame)) ? "" : "   (cut)";
        var time = TimeMath.Format(frame, timeline.Fps);
        var edit = $"     Edit {TimeMath.Format(edited, timeline.Fps)} / {TimeMath.Format(CutList.KeptFrames(kept), timeline.Fps)}";
        var full = $"{time}  frame {frame:N0}{edit}{cut}";
        var room = transport.Readout.Width;
        var text = full;
        foreach (var shorter in new[] { $"{time}  {frame:N0}{edit}{cut}", $"{time}  {frame:N0}{cut}" })
        {
            if (room <= 0 || transport.Readout.MeasureWidth(text) <= room) break;
            text = shorter;
        }

        transport.Readout.Text = text;
        if (readoutTip != full)
        {
            readoutTip = full;
            tips.SetToolTip(transport.Readout, full);
        }
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
        ambientTimer.Stop();
        ambientTimer.Start();
    }

    /// <summary>
    /// Tints the window from the still on screen. It waits for the still to settle and skips playback and drags,
    /// because a new backdrop repaints every control.
    /// </summary>
    void OnAmbientTick()
    {
        ambientTimer.Stop();
        if (IsDisposed || preview.Image is null) return;
        if (player.IsPlaying || timeline.IsDragging)
        {
            ambientTimer.Start();
            return;
        }

        Backdrop.SetFrame(preview.Image, ClientSize);
        backdropSize = ClientSize;
        Invalidate(true);
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
        // One thumbnail per uncropped tile: a slot no wider than the picture never crops it top and bottom.
        // The strip is a whole number of tiles, so it usually runs a little past the right edge, where the last tile is clipped.
        var tiles = timeline.FilmWidth / timeline.TileWidth;
        var count = Math.Clamp((int)Math.Ceiling(tiles), 1, 48);
        var start = timeline.VisibleStart;
        var visible = Math.Max(1, timeline.VisibleEnd - start);
        return new StripKey(count, start, start + Math.Max(1, (long)Math.Round(visible * (count / tiles))));
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

    /// <summary>Asks for the selected segment's own thumbnails once the edit or the view has settled.</summary>
    void QueueAnchor()
    {
        if (!timeline.HasMedia || IsDisposed) return;
        anchorTimer.Stop();
        anchorTimer.Start();
    }

    void OnAnchorTick()
    {
        anchorTimer.Stop();
        if (!timeline.HasMedia || IsDisposed || input is null || runner.Ffmpeg is null || exporting) return;
        if (timeline.IsDragging)
        {
            anchorTimer.Start();
            return;
        }

        var want = timeline.AnchorRequest;
        if (want == anchorKey) return;
        anchorKey = want;
        ReplaceCts(ref anchorCts);
        if (want is { } request) _ = BuildAnchorAsync(input, request, timeline.ThumbHeight, anchorCts.Token);
        else ShowAnchor(null, null);
    }

    /// <summary>
    /// The tiles pinned to the handles show the segment's in and out frames, so the strip shows where the cut lands.
    /// They use the same quick seek as the regular strip: seeking after the input would decode the file from its start.
    /// </summary>
    async Task BuildAnchorAsync(string path, AnchorPlan request, int height, CancellationToken token)
    {
        var slots = new Bitmap?[request.Count];
        using var gate = new SemaphoreSlim(3);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, request.Count).Select(async i =>
            {
                await gate.WaitAsync(token);
                try
                {
                    var time = timeline.SeekTime(request.FrameAt(i));
                    // The probed length can run a little past the last real frame; step back rather than leave a hole.
                    slots[i] = await stills.CaptureAsync(path, duration, time, accurate: false, "scale=-2:" + height, 5, token)
                        ?? await stills.CaptureAsync(path, duration, Math.Max(0, time - 0.5), accurate: false, "scale=-2:" + height, 5, token);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // The segment or the view changed again.
        }

        if (token.IsCancellationRequested || IsDisposed || anchorKey != request)
        {
            foreach (var bitmap in slots) bitmap?.Dispose();
            return;
        }

        ShowAnchor(slots, request);
    }

    void ShowAnchor(Bitmap?[]? slots, AnchorPlan? plan)
    {
        var old = anchorStrip;
        anchorStrip = slots;
        timeline.UseAnchor(slots, plan);
        if (old is null) return;
        foreach (var bitmap in old) bitmap?.Dispose();
    }

    void CancelPreviewJobs()
    {
        StopPlayback();
        ReplaceCts(ref previewCts);
        ReplaceCts(ref filmCts);
        ReplaceCts(ref anchorCts);
        anchorTimer.Stop();
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
        anchorKey = null;
        ShowAnchor(null, null);
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
        // A multi-selection in the list limits the export to those segments.
        // Separate files follow the segments as drawn, even ones that touch (splitting a video into parts).
        // A stitched export joins touching segments, since that changes nothing in the result.
        var picks = timeline.PickedSegments;
        var pieces = picks.Count > 0 ? picks : timeline.AllSegments;
        var separate = false;
        if (pieces.Count > 1)
        {
            var choice = AskSeparateFiles(pieces.Count);
            if (choice is null) return;
            separate = choice.Value;
        }

        var segments = separate ? pieces : CutList.Normalize(pieces, timeline.TotalFrames);

        // The format is chosen in the Export panel; the dialog only names the file.
        var format = exportBar.OutputFormat;
        var extension = OutputFormats.Extension(format, Path.GetExtension(input));
        using var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileNameWithoutExtension(input) + (separate ? "_part" : "_trimmed") + extension,
            Filter = format == OutputFormat.Source ? "Same container|*" : $"{OutputFormats.Name(format)} video|*{extension}",
            InitialDirectory = Path.GetDirectoryName(input) ?? "",
            Title = separate ? $"Name the files — the {segments.Count} segments are saved as name_01, name_02, …" : "Save the trimmed video",
            // Separate files get numbered names, so the chosen name itself is never written.
            OverwritePrompt = !separate,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var output = dialog.FileName;
        // A different extension typed in the dialog would quietly change the format, so the chosen one is kept.
        if (format != OutputFormat.Source) output = OutputFormats.SaveName(output, extension);
        // The dialog asked about the name as typed, not the one with the extension fixed.
        if (!separate && output != dialog.FileName && File.Exists(output) && MessageBox.Show(this,
                $"{Path.GetFileName(output)} already exists.\n\nReplace it?",
                "VideoTrimmer", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        var remux = Remux.For(input, output, subtitles);
        var outputs = separate ? ExportService.SeparateOutputs(output, segments.Count) : [output];
        if (outputs.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus("Choose a different file from the video you are trimming.", StatusKind.Error);
            return;
        }

        var existing = separate ? outputs.Where(File.Exists).ToList() : [];
        if (existing.Count > 0 && MessageBox.Show(this,
                $"{existing.Count} of these files already exist and will be replaced, starting with {Path.GetFileName(existing[0])}.\n\nReplace them?",
                "VideoTrimmer", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

        ReplaceCts(ref exportCts);
        var token = exportCts.Token;
        var mode = exact ? "Encoding exact frames" : "Saving fast copy";
        if (exact && exportBar.ExactOptions.BurnSubtitles is not null) mode += " with subtitles";
        if (segments.Count > 1) mode += $" of {segments.Count} segments";
        exporting = true;
        PauseWaveform();
        timeline.Locked = true;
        exportBar.SetBusy(true);
        exportBar.Export.Text = "Exporting…";
        exportBar.Export.Progress = 0;
        LayoutChrome();
        SetStatus(mode + "…", StatusKind.Busy);
        var exactOptions = exportBar.ExactOptions;
        ExportPlan? plan = null;
        var completed = 0;
        try
        {
            plan = separate
                ? ExportService.PlanSeparate(input, output, segments, timeline.Fps, exact, exactOptions, remux)
                : ExportService.Plan(input, output, segments, timeline.Fps, hasAudio, exact, exactOptions, remux);
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
                var phase = separate ? $"Saving segment {i + 1} of {plan.Steps.Count}"
                    : plan.Steps.Count == 1 ? mode
                    : i < plan.Steps.Count - 1 ? $"Copying segment {i + 1} of {plan.Steps.Count - 1}" : "Joining segments";
                var progress = new Progress<TimeSpan>(elapsed =>
                {
                    if (!exporting || IsDisposed) return;
                    var within = Math.Clamp(elapsed.TotalSeconds / Math.Max(0.001, step.Seconds), 0, 1);
                    var fraction = Math.Clamp((before + within * step.Weight) / totalWeight, 0, 1);
                    var percent = (int)Math.Round(fraction * 100);
                    exportBar.Export.Progress = fraction;
                    exportBar.Export.Text = $"Exporting… {percent}%";
                    SetStatus($"{phase}… {percent}%", StatusKind.Busy);
                });
                SetStatus(phase + "…", StatusKind.Busy);
                await runner.RunAsync(runner.Ffmpeg, step.Arguments, token, progress);
                done += step.Weight;
                completed++;
            }

            SetStatus(separate ? $"Saved {outputs.Count} files: {Path.GetFileName(outputs[0])} … {Path.GetFileName(outputs[^1])} in {Path.GetDirectoryName(outputs[0])}" : "Saved: " + output, StatusKind.Good);
        }
        catch (OperationCanceledException)
        {
            // Files finished before the cancel are kept; the one being written is removed.
            foreach (var path in separate ? outputs.Skip(completed) : outputs)
                ExportService.TryDelete(path);
            if (!IsDisposed) SetStatus("Export canceled.", StatusKind.Info);
        }
        catch (Exception ex)
        {
            // As on cancel: what finished is kept, and the half-written file is removed.
            foreach (var path in separate ? outputs.Skip(completed) : outputs)
                ExportService.TryDelete(path);
            if (!IsDisposed)
            {
                SetStatus("Export failed: " + ex.Message, StatusKind.Error);
                // A change of container fails when a stream cannot go into the new one; say what to do about it.
                var hint = remux is null ? "" : ExportModeCopy.ConvertFailedHint(OutputFormats.Name(format), exact, exactOptions.CopyAudio);
                if (hint.Length > 0)
                    MessageBox.Show(this, hint, "VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            ExportService.TryDeleteDirectory(plan?.WorkDirectory);
            exporting = false;
            timeline.Locked = false;
            if (!IsDisposed)
            {
                exportBar.SetBusy(false);
                UpdateReadouts();
                LayoutChrome();
                ResumeWaveform();
            }
        }
    }

    /// <summary>
    /// With several segments, asks whether to join them into one file or save each as its own file.
    /// Returns null when the user cancels.
    /// </summary>
    bool? AskSeparateFiles(int count)
    {
        var stitch = new TaskDialogCommandLinkButton("Stitch into one video", "Join the segments in order into a single file, as Play shows them.");
        var split = new TaskDialogCommandLinkButton($"Save {count} separate files", "Each segment becomes its own file, numbered _01, _02, …");
        var page = new TaskDialogPage
        {
            Caption = "VideoTrimmer",
            Heading = $"Export {count} segments",
            Text = "How should the segments be saved?",
            Icon = TaskDialogIcon.None,
            Buttons = { stitch, split, TaskDialogButton.Cancel },
            DefaultButton = stitch,
            AllowCancel = true,
        };
        var result = TaskDialog.ShowDialog(this, page);
        if (result == stitch) return false;
        if (result == split) return true;
        return null;
    }

    void CancelExport()
    {
        if (!exporting) return;
        exportCts.Cancel();
    }

    /// <summary>
    /// A click in the segment list: plain selects one segment to edit, Ctrl adds or removes one from the selection,
    /// Shift selects a range. A selection of two or more is what Export saves.
    /// </summary>
    void OnSegmentClicked(SegmentClick click)
    {
        if (!timeline.HasMedia || exporting) return;
        StopPlayback();
        if (click.Modifiers.HasFlag(Keys.Shift)) timeline.PickRange(click.Index);
        else if (click.Modifiers.HasFlag(Keys.Control)) timeline.TogglePick(click.Index);
        else if (timeline.SelectSegment(click.Index)) SchedulePreview(true);
        var picks = timeline.PickedSegments.Count;
        if (picks > 0) SetStatus($"{picks} segments selected. Export saves just these; click a segment or press Esc to clear.", StatusKind.Info);
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
    void StartWaveform(string path, double length, bool audio)
    {
        ReplaceCts(ref waveCts);
        ++waveGeneration;
        waveLevels = WaveformEnabled && runner.Ffmpeg is not null && audio ? [] : null;
        waveRead = ReadWaveform(path, length);
    }

    /// <summary>
    /// Reading the waveform demuxes the whole file, so it stops while an export runs rather than compete for the disk.
    /// What was read is kept; <see cref="ResumeWaveform"/> continues from there.
    /// </summary>
    void PauseWaveform()
    {
        if (waveLevels is null) return;
        ++waveGeneration;
        TryCancel(waveCts);
        timeline.LaneNote = "Audio waveform paused while exporting";
        timeline.Invalidate();
    }

    async void ResumeWaveform()
    {
        if (waveLevels is null || input is null) return;
        var generation = waveGeneration;
        // The paused read may still be finishing its last window; let it settle before reading on from its end.
        try { await waveRead; } catch { }
        if (generation != waveGeneration || IsDisposed || exporting) return;
        ReplaceCts(ref waveCts);
        waveRead = ReadWaveform(input, duration);
    }

    async Task ReadWaveform(string path, double length)
    {
        var levels = waveLevels;
        if (levels is null) return;
        var generation = ++waveGeneration;
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
            await Task.Run(() => AudioWaveform.ReadEnvelopeAsync(runner, path, length, levels, progress, token), token);
            if (!Current()) return;
            waveLevels = null;
            // Progress reports are still queued on the UI thread; clear the note behind them.
            BeginInvoke(() => Note(""));
            timeline.SetEnvelope(levels.ToArray());
        }
        catch (OperationCanceledException)
        {
            // A newer file replaced this one, an export paused it, or the app is closing.
        }
        catch (Exception)
        {
            if (Current()) waveLevels = null;
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
        topBar.SetStatus(text, kind);
        // The bar shortens a long message; the tooltip has all of it.
        tips.SetToolTip(topBar.Status, text);
    }

    void ShowShortcuts()
    {
        using var dialog = new ShortcutsDialog();
        dialog.ShowDialog(this);
    }

    void ShowSettings()
    {
        using var dialog = new SettingsDialog(settings);
        if (dialog.ShowDialog(this) == DialogResult.OK && !settings.Save())
            SetStatus("The settings couldn't be saved, so they last only until VideoTrimmer closes.", StatusKind.Error);
    }

    /// <summary>
    /// Follows a switch of theme or accent: the controls' own colors, the frame Windows draws, and the backdrop, which is
    /// built on the theme's base. Cached brushes rebuild themselves.
    /// </summary>
    void OnThemeChanged()
    {
        if (IsDisposed) return;
        Theme.Recolor(this);
        if (IsHandleCreated) WindowChrome.Colors(this);
        backdropSize = ClientSize;
        Backdrop.Resize(ClientSize);
        Invalidate(true);
    }

    int S(int logical) => LogicalToDeviceUnits(logical);
}
