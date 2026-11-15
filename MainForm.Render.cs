namespace VideoTrimmer;

/// <summary>
/// <c>--render &lt;folder&gt;</c>: makes a short test clip, opens it, and saves pictures of the window and its menus in its main states,
/// so the UI can be looked at the same way every time without sending keys or clicks to the desktop. The pictures are
/// taken at the display scale of the monitor the window opens on, in the dark theme and then the light one.
/// </summary>
public sealed partial class MainForm
{
    static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(30);

    public static int Render(string folder, string? project = null)
    {
        Directory.CreateDirectory(folder);
        var code = 1;
        using var form = new MainForm(null, checkForUpdates: false) { ShowInTaskbar = false };
        form.Shown += async (_, _) =>
        {
            try
            {
                if (project is null) await form.RenderStatesAsync(folder);
                else await form.RenderProjectAsync(project, folder);
                code = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "render-error.txt"), ex.ToString());
            }
            finally
            {
                form.skipSavePrompt = true;
                form.Close();
            }
        };
        Application.Run(form);
        return code;
    }

    async Task RenderStatesAsync(string folder)
    {
        if (runner.Ffmpeg is null) throw new InvalidOperationException("FFmpeg is needed to make the test clip.");
        var clip = Path.Combine(folder, "render-clip.mp4");
        await runner.RunAsync(runner.Ffmpeg,
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=20",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=20",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "30",
            "-c:a", "aac",
            clip,
        ]);

        await Task.Delay(300);
        Save(this, folder, "1-empty");
        Save(Popup(topBar.FileMenu), folder, "1-file-menu", close: true);
        var highlighted = Popup(topBar.FileMenu);
        highlighted.Items.OfType<ToolStripMenuItem>().First(item => item.Enabled).Select();
        Save(highlighted, folder, "1-file-menu-highlighted", close: true);

        LoadVideo(clip);
        await Until(() => timeline.HasMedia && preview.Image is not null && filmstrip.Current is not null && filmstrip.Building is null);
        // The backdrop takes its tint once the still settles.
        await Task.Delay(800);
        Save(this, folder, "2-loaded");
        // The window picture shows the classic frame over the top bar, so the bar is saved on its own too.
        Save(topBar, folder, "2-title-bar");

        // A trimmed segment shows its own thumbnails pinned to the handles.
        timeline.SetInFrame(120, followPlayhead: true);
        timeline.SetOutFrame(330, followPlayhead: false);
        await Until(() => anchorStrip is not null);
        await Task.Delay(400);
        Save(this, folder, "3-trimmed");
        Save(Popup(topBar.EditMenu), folder, "3-edit-menu", close: true);

        ChooseExact(true);
        exportBar.SetOutputSize(ExportSize.Hd1080);
        await Task.Delay(200);
        Save(this, folder, "4-exact");

        timeline.ZoomBy(4);
        await Until(() => filmstrip.Current == DesiredStrip() && filmstrip.Building is null);
        await Until(() => anchorStrip is not null && anchorKey == timeline.AnchorRequest);
        await Task.Delay(400);
        Save(this, folder, "5-zoomed");

        // What an export looks like partway, without writing a file.
        timeline.Locked = true;
        exportBar.SetBusy(true);

        exportBar.Export.Text = "Exporting…";
        SetStatus("Tone-mapping HDR to SDR · Stereo AAC…", StatusKind.Busy);
        workStatus.Progress = 0.42;
        LayoutChrome();
        await Task.Delay(200);
        Save(this, folder, "6-exporting");
        var originalSize = Size;
        Size = MinimumSize;
        await Task.Delay(200);
        Save(this, folder, "6-exporting-small");
        Size = originalSize;
        exportBar.SetBusy(false);
        SetStatus("Ready.", StatusKind.Info);
        timeline.Locked = false;
        UpdateReadouts();
        LayoutChrome();

        using var shortcuts = new ShortcutsDialog { StartPosition = FormStartPosition.Manual, Location = Location };
        shortcuts.Show(this);
        await Task.Delay(300);
        Save(shortcuts, folder, "7-shortcuts");
        shortcuts.Close();

        // Settings, then the light theme with another accent. Nothing here is saved.
        using (var dark = new SettingsDialog(new AppSettings()) { StartPosition = FormStartPosition.Manual, Location = Location })
        {
            dark.Show(this);
            await Task.Delay(300);
            Save(dark, folder, "8-settings");
            dark.Close();
        }

        Theme.Apply(dark: false, "Sky");
        await Task.Delay(300);
        Save(this, folder, "9-light");
        using var light = new SettingsDialog(new AppSettings { DarkTheme = false, Accent = "Sky" }) { StartPosition = FormStartPosition.Manual, Location = Location };
        light.Show(this);
        await Task.Delay(300);
        Save(light, folder, "9-light-settings");
        light.Close();
        Save(Popup(topBar.FileMenu), folder, "9-light-menu", close: true);
    }

    async Task RenderProjectAsync(string project, string folder)
    {
        OpenProjectFile(project);
        if (drop.Enabled) throw new InvalidOperationException("The empty prompt must not accept clicks during loading.");
        await Until(() => projectPath == project && timeline.HasMedia);
        var saved = CutList.Load(File.ReadAllText(project));
        var expected = CutList.Rescale(saved.Segments, saved.Fps, timeline.Fps);
        if (!timeline.AllSegments.SequenceEqual(expected)) throw new InvalidOperationException("Project cuts changed when reopened.");
        timeline.SetPlayhead(timeline.AllSegments[0].In);
        await Until(() => preview.Image is not null);
        previewTimer.Stop();
        accurateTimer.Stop();
        await GrabPreviewAsync(timeline.PlayheadFrame, accurate: true);
        if (preview.Image is null) throw new InvalidOperationException("The project's preview did not load.");
        Save(this, folder, "project-opened");
        var originalSize = Size;
        Size = MinimumSize;
        LayoutChrome();
        Save(this, folder, "project-opened-small");
        if (!side.ContentFits) throw new InvalidOperationException("Project controls overlap at the minimum window size.");
        Size = originalSize;
        LayoutChrome();
        Save(preview, folder, "project-tonemapped-preview");
        using var mapped = new Bitmap(preview.Image);
        using var raw = await stills.CaptureAsync(input!, duration, timeline.SeekTime(timeline.PlayheadFrame), true, "scale=" + PreviewWidth() + ":-2", 2, CancellationToken.None);
        if (raw is null) throw new InvalidOperationException("Could not make the unprocessed comparison frame.");
        raw.Save(Path.Combine(folder, "project-unprocessed-preview.png"));
        double difference = 0;
        var samples = 0;
        for (var y = 0; y < Math.Min(raw.Height, mapped.Height); y += 8)
            for (var x = 0; x < Math.Min(raw.Width, mapped.Width); x += 8)
            {
                var a = raw.GetPixel(x, y);
                var b = mapped.GetPixel(x, y);
                difference += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3d;
                samples++;
            }
        difference /= Math.Max(1, samples);
        if (sourceColor.IsHdr && difference < 1) throw new InvalidOperationException("The HDR preview still looks like the unprocessed frame.");

        // Exercise the real playback producer silently; it shares neither the still command nor its cache.
        var shown = new TaskCompletionSource<long>();
        void Frame(long frame) => shown.TrySetResult(frame);
        player.FrameShown += Frame;
        var first = timeline.AllSegments[0];
        var size = new Size(640, Math.Max(2, (int)Math.Round(640 / aspect / 2) * 2));
        if (!player.Play(input!, [new Segment(first.In, Math.Min(first.Out, first.In + 48))], fps, first.In, size, false, sourceColor, previewUseLibplacebo))
            throw new InvalidOperationException("Project playback did not start.");
        await shown.Task.WaitAsync(RenderWait);
        Save(preview, folder, "project-playback-preview");
        player.FrameShown -= Frame;
        StopPlayback();
        File.WriteAllText(Path.Combine(folder, "project-check.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            input, sourceColor, previewUseLibplacebo, segments = timeline.AllSegments,
            meanPreviewDifference = difference, playbackFrame = shown.Task.Result,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    static ToolStripDropDown Popup(ToolStripMenuItem menu)
    {
        menu.ShowDropDown();
        return menu.DropDown;
    }

    static async Task Until(Func<bool> done)
    {
        var started = DateTime.UtcNow;
        while (!done())
        {
            if (DateTime.UtcNow - started > RenderWait) throw new TimeoutException("The window did not settle in time.");
            await Task.Delay(100);
        }
    }

    static void Save(Control control, string folder, string name, bool close = false)
    {
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(Path.Combine(folder, name + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        // The menu bar keeps its drop-downs, so they are only closed.
        if (close && control is ToolStripDropDown menu)
            menu.Close();
    }
}
