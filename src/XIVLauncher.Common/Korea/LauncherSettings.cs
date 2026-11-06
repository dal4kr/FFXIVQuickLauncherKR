using System.Text.Json;

namespace XIVLauncher.Common.Korea;

public sealed class LauncherSettings
{
    public string GameDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FINAL FANTASY XIV - KOREA");
    public string UserName { get; set; } = "";
    public LoginProvider Provider { get; set; }
    public bool SaveSession { get; set; } = true;
    public bool RunAsAdministrator { get; set; }
    public bool AutoLaunch { get; set; }
    public Dalamud.DalamudOptions Dalamud { get; set; } = new();
}

public sealed class LauncherSettingsStore(string path)
{
    public LauncherSettings Load()
    {
        if (!File.Exists(path)) return new LauncherSettings();
        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllBytes(path)) ?? new LauncherSettings();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LauncherSettings();
        }
    }

    public void Save(LauncherSettings settings) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(settings));
}
