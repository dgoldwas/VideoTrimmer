namespace VideoTrimmer;

// The ⋯ menu offers a manual check; its item changes to "Update to …", and the button gets a dot, when a release is available.
public sealed partial class MainForm
{
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
    readonly CancellationTokenSource updateLifetime = new();
    AvailableUpdate? availableUpdate;
    string? pendingUpdateSetup;
    string? pendingUpdateVersion;
    bool updateBusy;
    string updateLabel = "Check for updates";

    void StartUpdateChecks()
    {
        ShowVersion();
        if (UpdateService.TakeLastResult() is { } result)
        {
            if (result.StartsWith("failed", StringComparison.OrdinalIgnoreCase))
                SetStatus($"The last update didn't install ({result["failed: ".Length..]}). Check for updates to try again.", StatusKind.Error);
            else if (result.StartsWith("installed", StringComparison.OrdinalIgnoreCase))
                SetStatus($"Updated to VideoTrimmer {UpdateService.CurrentVersion}.", StatusKind.Good);
        }

        updateTimer.Tick += async (_, _) => await CheckForUpdateAsync(manual: false);
        updateTimer.Start();
        // Let the window open first; the check is a single small request.
        _ = Task.Delay(TimeSpan.FromSeconds(5), updateLifetime.Token).ContinueWith(_ =>
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(async () => await CheckForUpdateAsync(manual: false));
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    void StopUpdateChecks()
    {
        updateTimer.Stop();
        try { updateLifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    void ShowVersion()
    {
        updateLabel = availableUpdate is { } found ? $"Update to {found.Version}" : "Check for updates";
        topBar.More.Dot = availableUpdate is not null;
        topBar.More.Invalidate();
        tips.SetToolTip(topBar.More, availableUpdate is { } update
            ? $"VideoTrimmer {update.Version} is available (you have {UpdateService.CurrentVersion})"
            : "Shortcuts, updates and About");
    }

    void ShowMoreMenu()
    {
        var menu = Menus.Create();
        menu.Items.Add(Menus.Item("Open video…", false, Browse, "Ctrl+O", enabled: !exporting));
        menu.Items.Add(Menus.Item("Keyboard shortcuts", false, ShowShortcuts, "F1"));
        menu.Items.Add(Menus.Separator());
        menu.Items.Add(Menus.Item(updateLabel, false, OnUpdateClick, enabled: !updateBusy));
        menu.Items.Add(Menus.Item("About VideoTrimmer", false, ShowAbout, UpdateService.CurrentVersion));
        Menus.ShowBelow(menu, topBar.More, alignRight: true);
    }

    void ShowAbout()
    {
        MessageBox.Show(this,
            $"VideoTrimmer {UpdateService.CurrentVersion}\n\nCuts a video down to the parts you want to keep.\nUses FFmpeg for reading and writing video, and NAudio for playback sound.",
            "About VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task CheckForUpdateAsync(bool manual)
    {
        if (updateBusy || pendingUpdateSetup != null || IsDisposed) return;
        updateBusy = true;
        if (manual) SetStatus("Checking for updates…", StatusKind.Busy);
        try
        {
            availableUpdate = await UpdateService.CheckAsync(updateLifetime.Token);
            if (IsDisposed) return;
            var known = updateLabel;
            ShowVersion();
            if (manual && availableUpdate is null)
                SetStatus($"VideoTrimmer is up to date (version {UpdateService.CurrentVersion}).", StatusKind.Good);
            // A release found in the background is only announced once, and never over an export or playback.
            else if (!manual && availableUpdate is { } found && known != updateLabel && !exporting && !player.IsPlaying)
                SetStatus($"VideoTrimmer {found.Version} is available. Open the ⋯ menu to update.", StatusKind.Info);
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Offline or GitHub unavailable: automatic checks try again later.
            if (manual && !IsDisposed) SetStatus("Couldn't check for updates: " + ex.Message, StatusKind.Error);
            return;
        }
        finally
        {
            updateBusy = false;
        }

        if (manual && availableUpdate is not null) await InstallUpdateAsync();
    }

    async void OnUpdateClick()
    {
        if (updateBusy) return;
        if (availableUpdate is null) await CheckForUpdateAsync(manual: true);
        else await InstallUpdateAsync();
    }

    async Task InstallUpdateAsync()
    {
        if (availableUpdate is not { } update || updateBusy) return;
        if (exporting)
        {
            SetStatus("Finish or cancel the export before updating.", StatusKind.Error);
            return;
        }

        if (pendingUpdateSetup != null)
        {
            // Already downloaded while an export ran.
            Close();
            return;
        }

        var size = update.Size > 0 ? $" ({update.Size / 1048576.0:N0} MB)" : "";
        var reopen = input is null ? "" : " with this video";
        if (MessageBox.Show(this,
                $"VideoTrimmer {update.Version} is available. You have {UpdateService.CurrentVersion}.\n\nDownload it{size} and install it now? VideoTrimmer will close, Windows will ask for permission to install, and VideoTrimmer will open again{reopen} when the update is done. Your segments are not kept.",
                "Update VideoTrimmer", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;

        updateBusy = true;
        try
        {
            var progress = new Progress<double>(p =>
            {
                updateLabel = $"Downloading… {p:P0}";
                if (!exporting && !IsDisposed) SetStatus($"Downloading the update… {p:P0}", StatusKind.Busy);
            });
            pendingUpdateSetup = await UpdateService.DownloadAsync(update, progress, updateLifetime.Token);
            pendingUpdateVersion = update.Version;
            if (exporting)
            {
                // An export started during the download; install when the user clicks again.
                updateLabel = "Install update";
                return;
            }

            updateLabel = "Installing…";
            // OnFormClosed hands the installer to the helper once the window is gone.
            Close();
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            pendingUpdateSetup = null;
            ShowVersion();
            SetStatus("The update couldn't be downloaded: " + ex.Message, StatusKind.Error);
        }
        finally
        {
            updateBusy = false;
        }
    }

    /// <summary>Called as the window closes: hands a downloaded update to the installer helper.</summary>
    void LaunchPendingUpdate()
    {
        if (pendingUpdateSetup == null) return;
        try
        {
            UpdateService.LaunchInstallerAfterExit(pendingUpdateSetup, pendingUpdateVersion ?? "", input);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"The update couldn't be started: {ex.Message}\n\nRun {pendingUpdateSetup} to install it.", "VideoTrimmer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
