using System.Globalization;
using System.Text.Json;

namespace VideoTrimmer;

public sealed class MainForm : Form
{
    const string VideoFilter = "Video files|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.wmv|All files|*.*";
    const string ReadyHint = "Click the filmstrip to move. Shift-click sets in. Alt-click sets out.";

    readonly FfmpegRunner runner = new();
    readonly FrameCache frameCache = new();
    readonly Dictionary<int, Bitmap?[]> filmCache = new();
    readonly HashSet<Bitmap?[]> releasedStrips = new();
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
    int filmGeneration;
    int currentStripCount;
    int buildingCount;
    Bitmap? shown;
    Bitmap?[]? displayedStrip;

    public MainForm(string? startupPath)
    {
        this.startupPath = startupPath;
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
            if (count != currentStripCount && count != buildingCount)
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
        tips.SetToolTip(exportBar.Fast, "Keeps the original streams. The cut can start slightly earlier, on a keyframe.");
        tips.SetToolTip(exportBar.Exact, "Re-encodes so the cut matches the frames you chose. Slower, and the picture can change.");

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
            var json = await runner.RunAsync(runner.Ffprobe, [
                "-v", "quiet",
                "-print_format", "json",
                "-show_streams",
                "-show_format",
                path,
            ], token);
            if (generation != loadGeneration || IsDisposed) return;
            var media = ParseProbe(json);
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

    static (double Duration, double Fps, long Frames) ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var video = root.GetProperty("streams").EnumerateArray()
            .FirstOrDefault(stream => stream.ValueKind == JsonValueKind.Object && stream.TryGetProperty("codec_type", out var kind) && kind.GetString() == "video");
        if (video.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("The file does not contain a video stream.");

        double probedDuration;
        if (video.TryGetProperty("duration", out var videoDuration) && videoDuration.ValueKind == JsonValueKind.String)
            probedDuration = double.Parse(videoDuration.GetString()!, CultureInfo.InvariantCulture);
        else
            probedDuration = double.Parse(root.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);

        var rate = video.GetProperty("r_frame_rate").GetString()?.Split('/');
        if (rate is not { Length: 2 })
            throw new InvalidOperationException("The video frame rate could not be read.");
        var probedFps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture);
        if (probedDuration <= 0 || double.IsNaN(probedDuration) || probedFps <= 0 || double.IsNaN(probedFps) || double.IsInfinity(probedFps))
            throw new InvalidOperationException("The video duration or frame rate could not be read.");

        var rounded = Math.Round(probedDuration * probedFps, MidpointRounding.AwayFromZero);
        if (rounded < 1 || rounded > 1_000_000_000)
            throw new InvalidOperationException("The video duration or frame rate could not be read.");
        return (probedDuration, probedFps, (long)rounded);
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
        if (frameCache.TryGet(frame, width, accurate, out var cached) && cached is not null)
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
            bitmap = await CaptureStillAsync(timeline.SeekTime(frame), accurate, "scale=" + width + ":-2", accurate ? 2 : 3, token);
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
        frameCache.Put(frame, width, bitmap, accurate, shown);
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
        if (count == currentStripCount || count == buildingCount) return;
        if (filmCache.TryGetValue(count, out var cached))
        {
            ShowStrip(cached);
            currentStripCount = count;
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
        if (count < 1 || runner.Ffmpeg is null || input is null) 
        {
            timeline.Placeholder = runner.Ffmpeg is null
                ? "FFmpeg is not available, so there is no filmstrip."
                : "";
            timeline.Invalidate();
            return;
        }

        ReplaceCts(ref filmCts);
        var generation = ++filmGeneration;
        buildingCount = count;
        _ = BuildFilmstripAsync(count, generation, filmCts.Token);
    }

    async Task BuildFilmstripAsync(int count, int generation, CancellationToken token)
    {
        // Sparse fast seeks stay responsive on long videos. One fps filter would decode the whole file.
        var slots = new Bitmap?[count];
        var height = timeline.ThumbHeight;
        if (generation != filmGeneration || IsDisposed) return;
        ShowStrip(slots);
        timeline.Placeholder = "";
        var gate = new SemaphoreSlim(4);
        var filled = 0;
        var tasks = new Task[count];
        for (var i = 0; i < count; i++)
            tasks[i] = FetchSlot(i);

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // The next filmstrip replaced this one.
        }
        finally
        {
            gate.Dispose();
        }

        if (generation != filmGeneration || IsDisposed)
        {
            if (displayedStrip != slots)
                ReleaseStrip(slots);
            return;
        }

        KeepStrip(count, slots);
        currentStripCount = count;
        buildingCount = 0;
        var any = false;
        foreach (var frame in slots)
        {
            if (frame is null) continue;
            any = true;
            break;
        }

        timeline.Placeholder = any ? "" : "No preview frames could be created. You can still set the range and export.";
        timeline.Invalidate();
        if (!exporting)
            SetStatus(any ? ReadyHint : timeline.Placeholder, any ? StatusKind.Info : StatusKind.Error);
        return;

        async Task FetchSlot(int index)
        {
            var acquired = false;
            try
            {
                await gate.WaitAsync(token);
                acquired = true;
                var time = count == 1 ? 0 : duration * index / (count - 1d);
                if (time > duration - 0.05) time = Math.Max(0, duration - 0.05);
                var bitmap = await CaptureStillAsync(time, accurate: false, "scale=-2:" + height, 5, token);
                void Assign()
                {
                    if (generation != filmGeneration || IsDisposed || bitmap is null)
                    {
                        bitmap?.Dispose();
                        return;
                    }

                    slots[index] = bitmap;
                    filled++;
                    // Paint in batches so a 32-thumb strip does not redraw on every slot.
                    if (filled == count || (filled & 3) == 0)
                        timeline.Invalidate();
                    if (!exporting)
                        SetStatus($"Building the filmstrip… {filled}/{count}", StatusKind.Busy);
                }

                if (IsDisposed || !IsHandleCreated)
                    bitmap?.Dispose();
                else if (InvokeRequired)
                    BeginInvoke(Assign);
                else
                    Assign();
            }
            catch (OperationCanceledException)
            {
                // Canceled by the next filmstrip.
            }
            catch (ObjectDisposedException)
            {
                // The form closed while a thumbnail was still decoding.
            }
            finally
            {
                if (acquired)
                {
                    try { gate.Release(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    void ShowStrip(Bitmap?[]? slots)
    {
        displayedStrip = slots;
        timeline.UseStrip(slots);
    }

    void KeepStrip(int count, Bitmap?[] slots)
    {
        foreach (var key in filmCache.Keys.ToArray())
        {
            if (key == count) continue;
            var old = filmCache[key];
            filmCache.Remove(key);
            if (old != displayedStrip && old != slots)
                ReleaseStrip(old);
        }

        if (filmCache.TryGetValue(count, out var existing) && existing != slots && existing != displayedStrip)
            ReleaseStrip(existing);
        filmCache[count] = slots;
    }

    void ReleaseStrip(Bitmap?[]? slots)
    {
        if (slots is null || !releasedStrips.Add(slots)) return;
        foreach (var bitmap in slots)
            bitmap?.Dispose();
    }

    async Task<Bitmap?> CaptureStillAsync(double seconds, bool accurate, string filter, int quality, CancellationToken token)
    {
        if (runner.Ffmpeg is null || input is null) return null;
        var time = Math.Clamp(seconds, 0, Math.Max(0, duration - 0.001)).ToString("0.######", CultureInfo.InvariantCulture);
        // Pipe JPEG to stdout so stills skip temp files. Accurate seeks after -i; fast seeks before.
        string[] args = accurate
            ?
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-i", input,
                "-ss", time,
                "-frames:v", "1",
                "-vf", filter,
                "-q:v", quality.ToString(CultureInfo.InvariantCulture),
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "pipe:1",
            ]
            :
            [
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                "-ss", time,
                "-i", input,
                "-frames:v", "1",
                "-vf", filter,
                "-q:v", quality.ToString(CultureInfo.InvariantCulture),
                "-f", "image2pipe",
                "-vcodec", "mjpeg",
                "pipe:1",
            ];
        try
        {
            var bytes = await runner.RunBinaryAsync(runner.Ffmpeg, args, token);
            if (token.IsCancellationRequested || bytes.Length == 0) return null;
            return BitmapFromJpeg(bytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    void CancelPreviewJobs()
    {
        ReplaceCts(ref previewCts);
        ReplaceCts(ref filmCts);
        previewGeneration++;
        filmGeneration++;
        buildingCount = 0;
        previewTimer.Stop();
        accurateTimer.Stop();
        filmTimer.Stop();
        previewArmed = false;
    }

    void ClearPictures()
    {
        preview.Image = null;
        shown = null;
        frameCache.Clear();
        timeline.UseStrip(null);
        var doomed = new HashSet<Bitmap?[]>(filmCache.Values);
        if (displayedStrip is not null) doomed.Add(displayedStrip);
        filmCache.Clear();
        displayedStrip = null;
        currentStripCount = 0;
        foreach (var slots in doomed)
            ReleaseStrip(slots);
        releasedStrips.Clear();
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
            await runner.RunAsync(runner.Ffmpeg, BuildExportArguments(output), token, progress);
            SetStatus("Saved: " + output, StatusKind.Good);
        }
        catch (OperationCanceledException)
        {
            TryDelete(output);
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

    string[] BuildExportArguments(string output)
    {
        var start = timeline.InSeconds.ToString(CultureInfo.InvariantCulture);
        var length = (timeline.OutSeconds - timeline.InSeconds).ToString(CultureInfo.InvariantCulture);
        // Exact mode seeks after -i so the cut lands on the chosen frames. Fast copy seeks before -i.
        return exact
            ?
            [
                "-y", "-progress", "pipe:1", "-nostats",
                "-i", input!,
                "-ss", start,
                "-t", length,
                "-map", "0",
                "-c:v", "libx264",
                "-c:a", "aac",
                output,
            ]
            :
            [
                "-y", "-progress", "pipe:1", "-nostats",
                "-ss", start,
                "-i", input!,
                "-t", length,
                "-map", "0",
                "-c", "copy",
                "-avoid_negative_ts", "make_zero",
                output,
            ];
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

    static Bitmap? BitmapFromJpeg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        return new Bitmap(image);
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A canceled ffmpeg process can keep the file open for a moment.
        }
    }

    enum StatusKind { Info, Busy, Good, Error }
}

sealed class FrameCache
{
    const int Limit = 24;

    readonly record struct Key(long Frame, int Width);

    sealed class Slot
    {
        public Bitmap? Fast;
        public Bitmap? Accurate;
    }

    readonly Dictionary<Key, Slot> slots = new();
    readonly Queue<Key> order = new();

    public bool TryGet(long frame, int width, bool accurate, out Bitmap? bitmap)
    {
        bitmap = null;
        if (!slots.TryGetValue(new Key(frame, width), out var slot)) return false;
        if (accurate)
        {
            bitmap = slot.Accurate;
            return bitmap is not null;
        }

        bitmap = slot.Accurate ?? slot.Fast;
        return bitmap is not null;
    }

    public void Put(long frame, int width, Bitmap bitmap, bool accurate, Bitmap? shown)
    {
        var key = new Key(frame, width);
        if (!slots.TryGetValue(key, out var slot))
        {
            slot = new Slot();
            slots[key] = slot;
            order.Enqueue(key);
        }

        if (accurate)
            Replace(slot, true, bitmap, shown);
        else if (slot.Accurate is null)
            Replace(slot, false, bitmap, shown);
        else
            bitmap.Dispose();

        Trim(shown);
    }

    public void Clear()
    {
        foreach (var slot in slots.Values)
        {
            slot.Fast?.Dispose();
            slot.Accurate?.Dispose();
        }

        slots.Clear();
        order.Clear();
    }

    static void Replace(Slot slot, bool accurate, Bitmap bitmap, Bitmap? shown)
    {
        var previous = accurate ? slot.Accurate : slot.Fast;
        if (accurate) slot.Accurate = bitmap;
        else slot.Fast = bitmap;
        if (previous is not null && previous != bitmap && previous != shown)
            previous.Dispose();
    }

    void Trim(Bitmap? shown)
    {
        var guard = 0;
        while (order.Count > Limit && guard++ < order.Count + 2)
        {
            var key = order.Dequeue();
            if (!slots.TryGetValue(key, out var slot)) continue;
            var visible = slot.Fast == shown || slot.Accurate == shown;
            if (visible)
            {
                order.Enqueue(key);
                continue;
            }

            slot.Fast?.Dispose();
            slot.Accurate?.Dispose();
            slots.Remove(key);
        }
    }
}
