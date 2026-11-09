namespace VideoTrimmer;

// The status bar offers a manual check and changes to "Update to …" when a release is available.
public sealed partial class MainForm
{
    readonly System.Windows.Forms.Timer updateTimer = new() { Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds };
    readonly CancellationTokenSource updateLifetime = new();
    AvailableUpdate? availableUpdate;
    string? pendingUpdateSetup;
    string? pendingUpdateVersion;
    bool updateBusy;

    void StartUpdateChecks()
    {
        ShowVersion();
        statusBar.UpdateButton.Click += (_, _) => OnUpdateClick();
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
        var update = statusBar.UpdateButton;
        if (availableUpdate is { } found)
        {
            update.Text = $"Update to {found.Version}";
            update.Primary = true;
            tips.SetToolTip(update, $"VideoTrimmer {found.Version} is available (you have {UpdateService.CurrentVersion}). Click to download and install it.");
        }
        else
        {
            update.Text = "Check for updates";
            update.Primary = false;
            tips.SetToolTip(update, $"VideoTrimmer {UpdateService.CurrentVersion}: check for updates");
        }

        update.AccessibleName = update.Text;
        update.Invalidate();
        statusBar.PerformLayout();
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
            ShowVersion();
            if (manual && availableUpdate is null)
                SetStatus($"VideoTrimmer is up to date (version {UpdateService.CurrentVersion}).", StatusKind.Good);
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
        statusBar.UpdateButton.Enabled = false;
        try
        {
            var progress = new Progress<double>(p =>
            {
                statusBar.UpdateButton.Text = $"Downloading… {p:P0}";
                statusBar.PerformLayout();
            });
            pendingUpdateSetup = await UpdateService.DownloadAsync(update, progress, updateLifetime.Token);
            pendingUpdateVersion = update.Version;
            if (exporting)
            {
                // An export started during the download; install when the user clicks again.
                statusBar.UpdateButton.Text = "Install update";
                statusBar.UpdateButton.Enabled = true;
                statusBar.PerformLayout();
                return;
            }

            statusBar.UpdateButton.Text = "Installing…";
            // OnFormClosed hands the installer to the helper once the window is gone.
            Close();
        }
        catch (OperationCanceledException) when (updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            pendingUpdateSetup = null;
            statusBar.UpdateButton.Enabled = true;
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
