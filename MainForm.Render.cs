namespace VideoTrimmer;

/// <summary>
/// <c>--render &lt;folder&gt;</c>: makes a short test clip, opens it, and saves pictures of the window in its main states,
/// so the UI can be looked at the same way every time without sending keys or clicks to the desktop. The pictures are
/// taken at the display scale of the monitor the window opens on.
/// </summary>
public sealed partial class MainForm
{
    static readonly TimeSpan RenderWait = TimeSpan.FromSeconds(30);

    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        var code = 1;
        using var form = new MainForm(null, checkForUpdates: false) { ShowInTaskbar = false };
        form.Shown += async (_, _) =>
        {
            try
            {
                await form.RenderStatesAsync(folder);
                code = 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(folder, "render-error.txt"), ex.ToString());
            }
            finally
            {
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
        Save(Popup(MoreMenu()), folder, "1-more-menu", close: true);

        LoadVideo(clip);
        await Until(() => timeline.HasMedia && preview.Image is not null && filmstrip.Current is not null && filmstrip.Building is null);
        // The backdrop takes its tint once the still settles.
        await Task.Delay(800);
        Save(this, folder, "2-loaded");

        // A trimmed segment shows its own thumbnails pinned to the handles.
        timeline.SetInFrame(120, followPlayhead: true);
        timeline.SetOutFrame(330, followPlayhead: false);
        await Until(() => anchorStrip is not null);
        await Task.Delay(400);
        Save(this, folder, "3-trimmed");

        ChooseExact(true);
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
        exportBar.Export.Progress = 0.42;
        exportBar.Export.Text = "Exporting… 42%";
        SetStatus("Encoding exact frames… 42%", StatusKind.Busy);
        LayoutChrome();
        await Task.Delay(200);
        Save(this, folder, "6-exporting");
        exportBar.SetBusy(false);
        timeline.Locked = false;
        UpdateReadouts();
        LayoutChrome();

        using var shortcuts = new ShortcutsDialog { StartPosition = FormStartPosition.Manual, Location = Location };
        shortcuts.Show(this);
        await Task.Delay(300);
        Save(shortcuts, folder, "7-shortcuts");
    }

    ContextMenuStrip Popup(ContextMenuStrip menu)
    {
        menu.Show(this, new Point(ClientSize.Width / 2, ClientSize.Height / 3));
        return menu;
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
        if (close && control is ToolStripDropDown menu)
        {
            menu.Close();
            menu.Dispose();
        }
    }
}
