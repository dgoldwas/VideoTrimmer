namespace VideoTrimmer;

/// <summary>A project file that has been read, waiting for its video to open.</summary>
sealed record PendingProject(string Path, SavedCuts Cuts);

// The menu bar, projects (a video's segment list saved with its path), New, and undo and redo of the segments.
public sealed partial class MainForm
{
    const string ProjectFilter = "VideoTrimmer project|*.json";

    readonly EditHistory history = new();
    string? projectPath;
    /// <summary>While the segments are being put back by undo, redo or opening a project, which must not make an undo step.</summary>
    bool restoring;
    /// <summary>Set by <c>--render</c>, which closes the window over changes it never means to save.</summary>
    bool skipSavePrompt;

    void WireMenus()
    {
        MainMenuStrip = topBar.Menu;
        Wire(topBar.FileMenu, FileItems);
        Wire(topBar.EditMenu, EditItems);
        Wire(topBar.HelpMenu, HelpItems);

        static void Wire(ToolStripMenuItem menu, Func<IEnumerable<ToolStripItem>> items)
        {
            Menus.Fill(menu, items());
            menu.DropDownOpening += (_, _) => Menus.Fill(menu, items());
        }
    }

    IEnumerable<ToolStripItem> FileItems()
    {
        var idle = !exporting;
        var loaded = timeline.HasMedia;
        yield return Menus.Item("New", false, NewWorkspace, "Ctrl+N", enabled: idle && loaded);
        yield return Menus.Separator();
        yield return Menus.Item("Open video…", false, Browse, "Ctrl+O", enabled: idle);
        yield return Menus.Item("Open project…", false, OpenProject, "Ctrl+Shift+O", enabled: idle);
        yield return Menus.Separator();
        yield return Menus.Item("Save project", false, () => SaveProject(saveAs: false), "Ctrl+S", enabled: idle && loaded);
        yield return Menus.Item("Save project as…", false, () => SaveProject(saveAs: true), "Ctrl+Shift+S", enabled: idle && loaded);
        yield return Menus.Separator();
        yield return Menus.Item("Export…", false, Export, "Ctrl+E", enabled: idle && loaded);
        yield return Menus.Separator();
        yield return Menus.Item("Exit", false, Close, "Alt+F4");
    }

    IEnumerable<ToolStripItem> EditItems()
    {
        var editing = !exporting && timeline.HasMedia;
        yield return Menus.Item("Undo", false, () => StepHistory(redo: false), "Ctrl+Z", enabled: editing && history.CanUndo);
        yield return Menus.Item("Redo", false, () => StepHistory(redo: true), "Ctrl+Y", enabled: editing && history.CanRedo);
        yield return Menus.Separator();
        yield return Menus.Item("Split at the playhead", false, SplitSegment, "S", enabled: editing);
        yield return Menus.Item("New segment", false, NewSegment, "N", enabled: editing);
        yield return Menus.Item("Delete segment", false, () => RemoveSegment(timeline.ActiveIndex), "Del", enabled: editing && timeline.AllSegments.Count > 1);
        yield return Menus.Separator();
        yield return Menus.Item("Select all segments", false, timeline.PickAll, "Ctrl+A", enabled: editing && timeline.AllSegments.Count > 1);
        yield return Menus.Item("Keep the whole video", false, ResetSegments, enabled: editing);
        yield return Menus.Separator();
        yield return Menus.Item("Settings…", false, ShowSettings, "Ctrl+,");
    }

    IEnumerable<ToolStripItem> HelpItems()
    {
        yield return Menus.Item("Keyboard shortcuts", false, ShowShortcuts, "F1");
        yield return Menus.Separator();
        yield return Menus.Item(updateLabel, false, OnUpdateClick, enabled: !updateBusy);
        yield return Menus.Item("About VideoTrimmer", false, ShowAbout, UpdateService.CurrentVersion);
    }

    /// <summary>The menu shortcuts that hold Ctrl. Single keys (S, N, Delete) are handled with the other editing keys.</summary>
    bool MenuShortcut(Keys keyData)
    {
        // In a text box, Ctrl+Z and Ctrl+Y undo the typing instead.
        var inText = ActiveControl is TextBox;
        switch (keyData)
        {
            case Keys.Control | Keys.N:
                NewWorkspace();
                return true;
            case Keys.Control | Keys.Shift | Keys.O:
                OpenProject();
                return true;
            case Keys.Control | Keys.S:
                SaveProject(saveAs: false);
                return true;
            case Keys.Control | Keys.Shift | Keys.S:
                SaveProject(saveAs: true);
                return true;
            case Keys.Control | Keys.E:
                Export();
                return true;
            case Keys.Control | Keys.Z when !inText:
                StepHistory(redo: false);
                return true;
            case Keys.Control | Keys.Y when !inText:
            case Keys.Control | Keys.Shift | Keys.Z when !inText:
                StepHistory(redo: true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Notes the segments after a change, which makes an undo step when they differ from before.</summary>
    void TrackEdit()
    {
        if (restoring || !timeline.HasMedia) return;
        var wasDirty = history.IsDirty;
        history.Record(timeline.State);
        if (history.IsDirty != wasDirty) ShowTitle();
    }

    void StepHistory(bool redo)
    {
        if (exporting || !timeline.HasMedia || timeline.IsDragging) return;
        TrackEdit();
        if ((redo ? history.Redo() : history.Undo()) is not { } state)
        {
            SetStatus(redo ? "Nothing to redo." : "Nothing to undo.", StatusKind.Info);
            return;
        }

        StopPlayback();
        restoring = true;
        try
        {
            timeline.SetSegments(state.Segments, state.Active, movePlayhead: false);
        }
        finally
        {
            restoring = false;
        }

        WriteBoxes();
        ShowTitle();
        SchedulePreview(true);
        SetStatus(redo ? "Redone." : "Undone.", StatusKind.Info);
    }

    /// <summary>New: closes the video and its segments, back to the empty window.</summary>
    void NewWorkspace()
    {
        if (exporting || !timeline.HasMedia || !ConfirmDiscard()) return;
        StopPlayback();
        ReplaceCts(ref loadCts);
        loadGeneration++;
        CancelPreviewJobs();
        ReplaceCts(ref waveCts);
        waveGeneration++;
        waveLevels = null;
        ClearPictures();
        timeline.SetClip(0, 0, 0);
        input = null;
        subtitles = [];
        projectPath = null;
        history.Clear();
        foreach (Control control in new Control[] { previewHost, transport, timeline, side })
            control.Visible = false;
        drop.Visible = true;
        topBar.Meta.Text = "";
        tips.SetToolTip(topBar.Meta, null);
        Backdrop.SetFrame(null, ClientSize);
        ShowTitle();
        LayoutChrome();
        Invalidate(true);
        SetStatus("Ready.", StatusKind.Info);
    }

    /// <summary>
    /// Asks to save unsaved segment changes before they are closed. False means stay: Cancel, or the save didn't happen.
    /// </summary>
    bool ConfirmDiscard()
    {
        if (skipSavePrompt || exporting || !timeline.HasMedia || input is null) return true;
        TrackEdit();
        if (!history.IsDirty) return true;
        var question = projectPath is null
            ? $"Save the segments of {Path.GetFileName(input)} as a project?"
            : $"Save the changes to {Path.GetFileNameWithoutExtension(projectPath)}?";
        return MessageBox.Show(this, question, "VideoTrimmer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question) switch
        {
            DialogResult.Yes => SaveProject(saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    /// <summary>Opens a video, or a project when the file is a <c>.json</c>.</summary>
    void OpenPath(string path)
    {
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            OpenProjectFile(path);
        else
            LoadVideo(path);
    }

    void OpenProject()
    {
        if (exporting || !ConfirmDiscard()) return;
        using var dialog = new OpenFileDialog { Filter = ProjectFilter, Title = "Open a project" };
        var near = projectPath ?? input;
        var directory = near is null ? null : Path.GetDirectoryName(near);
        if (!string.IsNullOrEmpty(directory)) dialog.InitialDirectory = directory;
        if (dialog.ShowDialog(this) == DialogResult.OK)
            OpenProjectFile(dialog.FileName);
    }

    /// <summary>Opens a project: its video, unless that is the one already open, then its segments.</summary>
    void OpenProjectFile(string path)
    {
        if (exporting) return;
        SavedCuts cuts;
        try
        {
            cuts = CutList.Load(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            SetStatus("The project couldn't be opened: " + (ex is System.Text.Json.JsonException ? "it is not a VideoTrimmer project." : ex.Message), StatusKind.Error);
            return;
        }

        var project = new PendingProject(path, cuts);
        if (!IsOpen(cuts.Video) && cuts.Video is not null && File.Exists(cuts.Video))
        {
            LoadVideo(cuts.Video, project);
            return;
        }

        if (!timeline.HasMedia)
        {
            SetStatus(cuts.Video is null ? "Open the video this project belongs to, then open the project again." : "The project's video was not found: " + cuts.Video, StatusKind.Error);
            return;
        }

        ApplyProject(project);
    }

    bool IsOpen(string? video) =>
        input is not null && video is not null && string.Equals(Path.GetFullPath(video), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase);

    void ApplyProject(PendingProject project)
    {
        StopPlayback();
        var cuts = project.Cuts;
        bool applied;
        restoring = true;
        try
        {
            applied = timeline.SetSegments(CutList.Rescale(cuts.Segments, cuts.Fps, timeline.Fps));
        }
        finally
        {
            restoring = false;
        }

        if (!applied)
        {
            SetStatus("That project has no segments inside this video.", StatusKind.Error);
            return;
        }

        projectPath = project.Path;
        history.Reset(timeline.State);
        // Its own video is missing (moved or renamed), so the segments went onto the open one. Saving records that.
        var moved = !IsOpen(cuts.Video);
        if (moved) history.MarkUnsaved();
        ShowTitle();
        WriteBoxes();
        SchedulePreview(true);
        var count = timeline.AllSegments.Count;
        SetStatus($"Opened {Path.GetFileNameWithoutExtension(project.Path)}: {(count == 1 ? "1 segment" : $"{count} segments")}"
            + (moved ? " on this video, as the project's own video wasn't found." : "."), StatusKind.Good);
    }

    /// <summary>Saves the project where it was opened from, or asks where when it is new or <paramref name="saveAs"/>.</summary>
    bool SaveProject(bool saveAs)
    {
        if (exporting || !timeline.HasMedia || input is null) return false;
        var path = projectPath;
        if (saveAs || path is null)
        {
            using var dialog = new SaveFileDialog
            {
                Filter = ProjectFilter,
                FileName = projectPath is null ? Path.GetFileNameWithoutExtension(input) + "-project.json" : Path.GetFileName(projectPath),
                InitialDirectory = Path.GetDirectoryName(projectPath ?? input) ?? "",
                Title = "Save the project",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            path = dialog.FileName;
        }

        TrackEdit();
        try
        {
            File.WriteAllText(path, CutList.Save(input, timeline.Fps, timeline.AllSegments));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("The project couldn't be saved: " + ex.Message, StatusKind.Error);
            return false;
        }

        projectPath = path;
        history.MarkSaved();
        ShowTitle();
        SetStatus("Saved " + path, StatusKind.Good);
        return true;
    }

    /// <summary>The title bar and taskbar name: the project and its video, with a dot while there are unsaved changes.</summary>
    void ShowTitle()
    {
        if (input is null)
        {
            topBar.FileName.Text = Text = "VideoTrimmer";
            tips.SetToolTip(topBar.FileName, null);
        }
        else
        {
            var video = Path.GetFileName(input);
            var name = projectPath is null ? video : $"{Path.GetFileNameWithoutExtension(projectPath)} — {video}";
            var dirty = history.IsDirty;
            topBar.FileName.Text = dirty ? name + "  •" : name;
            Text = name + " — VideoTrimmer";
            tips.SetToolTip(topBar.FileName, (projectPath is null ? input : projectPath + "\n" + input) + (dirty ? "\nUnsaved changes" : ""));
        }

        topBar.PerformLayout();
        topBar.Invalidate();
    }
}
