namespace XIVLauncher.Common.Korea;

public static class LauncherDataDirectory
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncherKR");

    public static void MigrateLegacyFiles(string legacyDirectory, string dataDirectory)
    {
        var settings = Path.Combine(dataDirectory, "settings.json");
        var legacySettings = Path.Combine(legacyDirectory, "settings.json");
        if (File.Exists(settings) || !File.Exists(legacySettings)) return;

        Directory.CreateDirectory(dataDirectory);
        var session = Path.Combine(dataDirectory, "accounts.dat");
        var legacySession = Path.Combine(legacyDirectory, "accounts.dat");
        if (!File.Exists(session) && File.Exists(legacySession)) File.Copy(legacySession, session);
        // Settings mark the completed import, so a deleted session stays deleted.
        File.Copy(legacySettings, settings);
    }
}
