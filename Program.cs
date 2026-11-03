namespace VideoTrimmer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--check"])
        {
            var errors = TimeMath.SelfCheck();
            var path = Path.Combine(Path.GetTempPath(), "vt-check.txt");
            File.WriteAllLines(path, errors.Count == 0 ? ["ok"] : errors);
            Environment.Exit(errors.Count == 0 ? 0 : 1);
        }

        ApplicationConfiguration.Initialize();
        var startup = args.Length > 0 && File.Exists(args[0]) ? args[0] : null;
        Application.Run(new MainForm(startup));
    }
}
