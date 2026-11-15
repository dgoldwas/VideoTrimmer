namespace VideoTrimmer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--check"])
        {
            var errors = TimeMath.SelfCheck()
                .Concat(FrameCache.SelfCheck())
                .Concat(StillCapture.SelfCheck())
                .Concat(OutputFormats.SelfCheck())
                .Concat(ExportService.SelfCheck())
                .Concat(MediaProbe.SelfCheck())
                .Concat(CutList.SelfCheck())
                .Concat(AudioWaveform.SelfCheck())
                .Concat(EditPlayer.SelfCheck())
                .Concat(TimelineControl.SelfCheck())
                .Concat(UpdateService.SelfCheck())
                .Concat(Theme.SelfCheck())
                .Concat(AppSettings.SelfCheck())
                .Concat(Menus.SelfCheck())
                .Concat(DropPrompt.SelfCheck())
                .Concat(SidePanel.SelfCheck())
                .ToList();
            var path = Path.Combine(Path.GetTempPath(), "vt-check.txt");
            File.WriteAllLines(path, errors.Count == 0 ? ["ok"] : errors);
            Environment.Exit(errors.Count == 0 ? 0 : 1);
        }

        ApplicationConfiguration.Initialize();
        if (args is ["--render", var folder])
            Environment.Exit(MainForm.Render(Path.GetFullPath(folder)));
        if (args is ["--render-project", var project, var projectFolder])
            Environment.Exit(MainForm.Render(Path.GetFullPath(projectFolder), Path.GetFullPath(project)));

        var startup = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        var settings = AppSettings.Load();
        Theme.Apply(settings.DarkTheme, settings.Accent);
        Application.Run(new MainForm(startup, settings: settings));
    }
}
