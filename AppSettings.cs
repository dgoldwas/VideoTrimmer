using System.Text.Json;

namespace VideoTrimmer;

/// <summary>The choices VideoTrimmer remembers between runs, kept in <c>%LOCALAPPDATA%\VideoTrimmer\settings.json</c>.</summary>
sealed class AppSettings
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public bool DarkTheme { get; set; } = true;
    /// <summary>The accent's name, one of <see cref="Theme.Tints"/>.</summary>
    public string Accent { get; set; } = Theme.Tints[0].Name;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoTrimmer", "settings.json");

    /// <summary>The saved settings, or the defaults when there are none or the file can't be read.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultPath), Options) ?? new AppSettings();
            loaded.Accent = Theme.FindTint(loaded.Accent).Name;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings, replacing the file whole. Returns false when it can't be written.</summary>
    public bool Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Options));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> SelfCheck()
    {
        var errors = new List<string>();
        void Expect(bool condition, string message)
        {
            if (!condition) errors.Add(message);
        }

        var folder = Path.Combine(Path.GetTempPath(), "vt-settings-check-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "settings.json");
        try
        {
            var missing = Load(path);
            Expect(missing.DarkTheme && missing.Accent == "Butter", "no file means dark with the first accent");
            Expect(new AppSettings { DarkTheme = false, Accent = "Sky" }.Save(path), "settings are saved");
            var loaded = Load(path);
            Expect(!loaded.DarkTheme && loaded.Accent == "Sky", "saved settings come back");
            File.WriteAllText(path, "{ not json");
            Expect(Load(path).DarkTheme, "a damaged file falls back to the defaults");
            File.WriteAllText(path, """{"DarkTheme":true,"Accent":"Chartreuse"}""");
            Expect(Load(path).Accent == "Butter", "an unknown accent falls back to the first");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }

        return errors;
    }
}
