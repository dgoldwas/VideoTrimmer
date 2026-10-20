using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Timer = System.Windows.Forms.Timer;

namespace VideoTrimmer;

public sealed class MainForm : Form
{
    readonly Label dropLabel = new();
    readonly Label fileLabel = new();
    readonly Label metaLabel = new();
    readonly TrackBar startBar = new();
    readonly TrackBar endBar = new();
    readonly TextBox startBox = new();
    readonly TextBox endBox = new();
    readonly ComboBox entryMode = new();
    readonly Label rangeLabel = new();
    readonly Label status = new();
    readonly Button trimButton = new();
    readonly FlowLayoutPanel thumbs = new();
    readonly CheckBox exactMode = new();
    readonly PictureBox startPreview = new();
    readonly PictureBox endPreview = new();
    readonly Label startPreviewLabel = new();
    readonly Label endPreviewLabel = new();
    readonly Timer scrubTimer = new() { Interval = 150 };
    readonly string? ffmpeg;
    readonly string? ffprobe;
    string? input;
    double duration;
    double fps = 30;
    long frameCount;
    bool updating;
    int previewGeneration;

    public MainForm()
    {
        Text = "Video Trimmer — stream copy";
        Width = 1120; Height = 760; MinimumSize = new Size(900, 620);
        BackColor = Color.FromArgb(24, 25, 29); ForeColor = Color.White;
        AllowDrop = true;
        ffmpeg = FindTool("ffmpeg.exe"); ffprobe = FindTool("ffprobe.exe");
        BuildUi();
        scrubTimer.Tick += (_, _) => { scrubTimer.Stop(); UpdateRange(); _ = UpdateSelectedPreviews(); };
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 8, ColumnCount = 1 };
        root.RowStyles.Add(new(SizeType.Absolute, 70)); root.RowStyles.Add(new(SizeType.Absolute, 46)); root.RowStyles.Add(new(SizeType.Absolute, 42));
        root.RowStyles.Add(new(SizeType.Absolute, 90)); root.RowStyles.Add(new(SizeType.Absolute, 90)); root.RowStyles.Add(new(SizeType.Absolute, 56));
        root.RowStyles.Add(new(SizeType.Absolute, 82)); root.RowStyles.Add(new(SizeType.Percent, 100)); Controls.Add(root);

        dropLabel.Text = "Drop a video here, or click to browse"; dropLabel.Dock = DockStyle.Fill; dropLabel.TextAlign = ContentAlignment.MiddleCenter;
        dropLabel.Font = new Font("Segoe UI", 16, FontStyle.Bold); dropLabel.BackColor = Color.FromArgb(39, 42, 50); dropLabel.ForeColor = Color.FromArgb(142, 197, 255);
        dropLabel.Cursor = Cursors.Hand; dropLabel.Click += (_, _) => Browse(); root.Controls.Add(dropLabel, 0, 0);
        fileLabel.Text = "No video loaded"; fileLabel.AutoEllipsis = true; fileLabel.Dock = DockStyle.Fill; fileLabel.TextAlign = ContentAlignment.MiddleLeft; root.Controls.Add(fileLabel, 0, 1);
        metaLabel.Text = "Load a file to inspect its duration and frame rate."; metaLabel.Dock = DockStyle.Fill; metaLabel.ForeColor = Color.LightGray; root.Controls.Add(metaLabel, 0, 2);

        root.Controls.Add(MakeRangeRow("START", startBar, startBox, true), 0, 3);
        root.Controls.Add(MakeRangeRow("END", endBar, endBox, false), 0, 4);
        var mode = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        mode.Controls.Add(new Label { Text = "Type values as:", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
        entryMode.Items.AddRange(["Frames", "Timecode (MM:SS:FF)"]); entryMode.SelectedIndex = 0; entryMode.Width = 170; entryMode.DropDownStyle = ComboBoxStyle.DropDownList; entryMode.SelectedIndexChanged += (_, _) => RefreshEntries(); mode.Controls.Add(entryMode);
        exactMode.Text = "Exact frame mode (re-encodes; slower)"; exactMode.AutoSize = true; exactMode.Margin = new Padding(24, 8, 0, 0); mode.Controls.Add(exactMode); root.Controls.Add(mode, 0, 5);
        var actionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 8, 0, 8) };
        actionBar.ColumnStyles.Add(new(SizeType.Percent, 100)); actionBar.ColumnStyles.Add(new(SizeType.Absolute, 250));
        rangeLabel.Dock = DockStyle.Fill; rangeLabel.TextAlign = ContentAlignment.MiddleLeft; rangeLabel.Font = new Font("Segoe UI", 12, FontStyle.Bold); actionBar.Controls.Add(rangeLabel, 0, 0);
        trimButton.Text = "EXPORT TRIMMED VIDEO"; trimButton.Dock = DockStyle.Fill; trimButton.Font = new Font("Segoe UI", 10, FontStyle.Bold); trimButton.Click += (_, _) => Trim(); actionBar.Controls.Add(trimButton, 1, 0); root.Controls.Add(actionBar, 0, 6);
        var timeline = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 4, 0, 0) };
        timeline.RowStyles.Add(new(SizeType.Absolute, 112)); timeline.RowStyles.Add(new(SizeType.Percent, 100));
        var selectedStrip = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 2, 0, 2) };
        selectedStrip.Controls.Add(PreviewCard("START FRAME", startPreview, startPreviewLabel, Color.LightGreen)); selectedStrip.Controls.Add(PreviewCard("END FRAME", endPreview, endPreviewLabel, Color.Salmon));
        timeline.Controls.Add(selectedStrip, 0, 0);
        thumbs.Dock = DockStyle.Fill; thumbs.AutoScroll = true; thumbs.WrapContents = false; thumbs.FlowDirection = FlowDirection.LeftToRight; thumbs.Padding = new Padding(0, 8, 0, 8); timeline.Controls.Add(thumbs, 0, 1); root.Controls.Add(timeline, 0, 7);
        status.Text = ffmpeg is null || ffprobe is null ? "FFmpeg not found — install it or place ffmpeg.exe and ffprobe.exe beside this app." : "Ready"; status.Dock = DockStyle.Bottom; status.Height = 28; status.ForeColor = Color.Khaki; Controls.Add(status);
        EnableDrop(this);
    }

    void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragEnter;
        control.DragDrop += OnDragDrop;
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    Control MakeRangeRow(string title, TrackBar bar, TextBox box, bool start)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        p.ColumnStyles.Add(new(SizeType.Absolute, 58)); p.ColumnStyles.Add(new(SizeType.Percent, 100)); p.ColumnStyles.Add(new(SizeType.Absolute, 190));
        p.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = start ? Color.LightGreen : Color.Salmon }, 0, 0);
        bar.Dock = DockStyle.Fill; bar.Minimum = 0; bar.Maximum = 1000; bar.TickStyle = TickStyle.None; bar.ValueChanged += (_, _) => { if (!updating) { UpdateRange(); RefreshEntries(); scrubTimer.Stop(); scrubTimer.Start(); } }; p.Controls.Add(bar, 1, 0);
        box.Dock = DockStyle.Fill; box.Font = new Font("Consolas", 11); box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ApplyEntry(start); }; box.Leave += (_, _) => ApplyEntry(start); p.Controls.Add(box, 2, 0); return p;
    }

    static Control PreviewCard(string title, PictureBox picture, Label timeLabel, Color accent)
    {
        var card = new TableLayoutPanel { Width = 250, Height = 106, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
        card.RowStyles.Add(new(SizeType.Absolute, 22)); card.RowStyles.Add(new(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = accent, Font = new Font("Segoe UI", 9, FontStyle.Bold) }, 0, 0);
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(39, 42, 50) };
        picture.Dock = DockStyle.Fill; picture.SizeMode = PictureBoxSizeMode.Zoom; body.Controls.Add(picture);
        timeLabel.Dock = DockStyle.Bottom; timeLabel.Height = 20; timeLabel.TextAlign = ContentAlignment.MiddleCenter; timeLabel.BackColor = Color.FromArgb(180, 0, 0, 0); timeLabel.ForeColor = Color.White; body.Controls.Add(timeLabel);
        card.Controls.Add(body, 0, 1); return card;
    }

    void OnDragEnter(object? s, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    void OnDragDrop(object? s, DragEventArgs e) { if (e.Data?.GetData(DataFormats.FileDrop) is string[] a && a.Length > 0) LoadVideo(a[0]); }
    void Browse() { using var d = new OpenFileDialog { Filter = "Video files|*.mkv;*.mp4;*.mov;*.avi;*.webm;*.ts;*.m2ts;*.wmv|All files|*.*" }; if (d.ShowDialog() == DialogResult.OK) LoadVideo(d.FileName); }

    async void LoadVideo(string path)
    {
        if (!File.Exists(path)) return; input = path; fileLabel.Text = path; status.Text = "Reading metadata…"; trimButton.Enabled = false;
        if (ffprobe is null) { status.Text = "Bundled FFprobe is missing. Rebuild or use the complete publish folder."; MessageBox.Show("The bundled FFprobe executable is missing. Please use the complete publish\\win-x64 folder.", "Video Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        try { var json = await Run(ffprobe, $"-v quiet -print_format json -show_streams -show_format {Q(path)}"); using var doc = JsonDocument.Parse(json); var v = doc.RootElement.GetProperty("streams").EnumerateArray().First(x => x.GetProperty("codec_type").GetString() == "video"); duration = v.TryGetProperty("duration", out var vd) ? double.Parse(vd.GetString()!, CultureInfo.InvariantCulture) : double.Parse(doc.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture); var rate = v.GetProperty("r_frame_rate").GetString()!.Split('/'); fps = double.Parse(rate[0], CultureInfo.InvariantCulture) / double.Parse(rate[1], CultureInfo.InvariantCulture); frameCount = Math.Max(1, (long)Math.Round(duration * fps)); startBar.Maximum = (int)Math.Min(frameCount, int.MaxValue - 1); endBar.Maximum = startBar.Maximum; metaLabel.Text = $"{duration:0.###} seconds  •  {fps:0.###} fps  •  approximately {frameCount:N0} frames  •  stream-copy output"; startBar.Value = 0; endBar.Value = endBar.Maximum; trimButton.Enabled = true; UpdateRange(); RefreshEntries(); await UpdateSelectedPreviews(); await MakeThumbnails(); status.Text = "Ready — choose the range and export."; }
        catch (Exception ex) { status.Text = "Could not read video: " + ex.Message; MessageBox.Show("Video metadata could not be read.\n\n" + ex.Message, "Video Trimmer", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    async Task MakeThumbnails()
    {
        if (ffmpeg is null || input is null || duration <= 0) return; thumbs.Controls.Clear(); status.Text = "Creating lightweight timeline previews…";
        var made = 0;
        for (var i = 0; i < 12; i++) { var t = duration * i / 11.0; var tmp = Path.Combine(Path.GetTempPath(), "vt_" + Guid.NewGuid() + ".jpg"); try { await Run(ffmpeg, $"-nostdin -hide_banner -loglevel error -y -ss {t.ToString(CultureInfo.InvariantCulture)} -i {Q(input)} -frames:v 1 -vf scale=150:-2 -q:v 5 {Q(tmp)}"); if (File.Exists(tmp)) { using var img = Image.FromFile(tmp); var copy = new Bitmap(img); var pb = new PictureBox { Image = copy, SizeMode = PictureBoxSizeMode.Zoom, Width = 150, Height = 86, Tag = t, Cursor = Cursors.Hand, Margin = new Padding(2) }; pb.Click += (_, _) => { updating = true; startBar.Value = (int)Math.Clamp(Math.Round((double)pb.Tag! * fps), 0, startBar.Maximum); updating = false; RefreshEntries(); UpdateRange(); _ = UpdateSelectedPreviews(); }; thumbs.Controls.Add(pb); made++; status.Text = $"Creating timeline previews… {made}/12"; } } catch (Exception ex) { status.Text = $"Preview {i + 1}/12 skipped: {ex.Message}"; } finally { try { File.Delete(tmp); } catch { } } }
        if (made == 0) status.Text = "No thumbnails could be created. The video metadata was read successfully; you can still set a range and export.";
    }

    async Task UpdateSelectedPreviews()
    {
        if (ffmpeg is null || input is null || duration <= 0) return;
        var id = ++previewGeneration;
        var start = StartSeconds(); var end = EndSeconds();
        await UpdatePreview(startPreview, startPreviewLabel, start, "START FRAME", id);
        await UpdatePreview(endPreview, endPreviewLabel, end, "END FRAME", id);
    }

    async Task UpdatePreview(PictureBox picture, Label label, double seconds, string title, int id)
    {
        if (input is null || ffmpeg is null) return;
        var tmp = Path.Combine(Path.GetTempPath(), "vt_selected_" + Guid.NewGuid() + ".jpg");
        try { await Run(ffmpeg, $"-nostdin -hide_banner -loglevel error -y -i {Q(input)} -ss {seconds.ToString(CultureInfo.InvariantCulture)} -frames:v 1 -vf scale=240:-2 -q:v 3 {Q(tmp)}"); if (id != previewGeneration || !File.Exists(tmp)) return; using var img = Image.FromFile(tmp); var copy = new Bitmap(img); var old = picture.Image; picture.Image = copy; old?.Dispose(); label.Text = $"{title}: {Tc(seconds)}  (frame {Frame(seconds):N0})"; } catch { label.Text = $"{title}: {Tc(seconds)}"; } finally { try { File.Delete(tmp); } catch { } }
    }

    void ApplyEntry(bool start)
    {
        if (updating || duration <= 0) return; var text = start ? startBox.Text : endBox.Text; double value;
        if (entryMode.SelectedIndex == 0 && long.TryParse(text, out var f)) value = f / fps; else if (!TryTimecode(text, out value)) return;
        value = Math.Clamp(value, 0, duration); if (start && value > EndSeconds()) value = EndSeconds(); if (!start && value < StartSeconds()) value = StartSeconds();
        updating = true; (start ? startBar : endBar).Value = (int)Math.Clamp(Math.Round(value * fps), 0, startBar.Maximum); updating = false; UpdateRange(); RefreshEntries(); _ = UpdateSelectedPreviews();
    }
    double StartSeconds() => startBar.Value / fps;
    double EndSeconds() => endBar.Value / fps;
    void RefreshEntries() { if (duration <= 0) return; updating = true; var a = StartSeconds(); var b = EndSeconds(); if (entryMode.SelectedIndex == 0) { startBox.Text = Frame(a).ToString(); endBox.Text = Frame(b).ToString(); } else { startBox.Text = Tc(a); endBox.Text = Tc(b); } updating = false; }
    void UpdateRange() { if (duration <= 0) return; var a = StartSeconds(); var b = EndSeconds(); rangeLabel.Text = $"Selected: {Tc(b - a)}  •  {Frame(b) - Frame(a):N0} frames  •  {Tc(a)} → {Tc(b)}"; }
    long Frame(double seconds) => (long)Math.Round(seconds * fps);
    string Tc(double seconds) { var f = Frame(seconds); var whole = f / (long)Math.Round(fps); var fr = f % (long)Math.Round(fps); return $"{whole / 60:00}:{whole % 60:00}:{fr:00}"; }
    bool TryTimecode(string s, out double seconds) { seconds = 0; var p = s.Split(':'); if (p.Length != 3 || !int.TryParse(p[0], out var m) || !int.TryParse(p[1], out var sec) || !int.TryParse(p[2], out var fr)) return false; seconds = (m * 60 + sec) + fr / fps; return true; }

    async void Trim()
    {
        if (ffmpeg is null || input is null) { MessageBox.Show("FFmpeg is required. See README.md."); return; } using var d = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(input) + "_trimmed" + Path.GetExtension(input), Filter = "Same container|*" }; if (d.ShowDialog() != DialogResult.OK) return;
        var a = StartSeconds(); var b = EndSeconds(); var args = exactMode.Checked ? $"-y -ss {a.ToString(CultureInfo.InvariantCulture)} -i {Q(input)} -t {(b-a).ToString(CultureInfo.InvariantCulture)} -c:v libx264 -c:a aac {Q(d.FileName)}" : $"-y -ss {a.ToString(CultureInfo.InvariantCulture)} -i {Q(input)} -t {(b-a).ToString(CultureInfo.InvariantCulture)} -map 0 -c copy -avoid_negative_ts make_zero {Q(d.FileName)}";
        // Accurate seeking belongs after -i. This intentionally trades speed for exact frames.
        if (exactMode.Checked) args = $"-y -i {Q(input)} -ss {a.ToString(CultureInfo.InvariantCulture)} -t {(b-a).ToString(CultureInfo.InvariantCulture)} -map 0 -c:v libx264 -c:a aac {Q(d.FileName)}";
        trimButton.Enabled = false; status.Text = exactMode.Checked ? "Encoding exact-frame output…" : "Remuxing stream copy…"; try { await Run(ffmpeg, args); status.Text = "Saved: " + d.FileName; } catch (Exception ex) { status.Text = "Trim failed: " + ex.Message; } finally { trimButton.Enabled = true; }
    }
    static string? FindTool(string name) { var candidates = new[] { Path.Combine(AppContext.BaseDirectory, name), Path.Combine(AppContext.BaseDirectory, "tools", name) }; var beside = candidates.FirstOrDefault(File.Exists); if (beside is not null) return beside; var p = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(x => Path.Combine(x, name)).FirstOrDefault(File.Exists); return p; }
    static string Q(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    static async Task<string> Run(string exe, string args) { using var p = new Process { StartInfo = new ProcessStartInfo(exe, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; p.Start(); var outputTask = p.StandardOutput.ReadToEndAsync(); var errorTask = p.StandardError.ReadToEndAsync(); await Task.WhenAll(outputTask, errorTask); await p.WaitForExitAsync(); var output = await outputTask; var error = await errorTask; if (p.ExitCode != 0) throw new InvalidOperationException(error.Trim().Split('\n').LastOrDefault()?.Trim() ?? "FFmpeg error"); return output; }
}
