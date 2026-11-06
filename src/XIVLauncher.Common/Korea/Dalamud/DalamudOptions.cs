namespace XIVLauncher.Common.Korea.Dalamud;

public enum DalamudLoadMethod
{
    DllInject,
    EntryPoint,
}

public sealed class DalamudOptions
{
    public bool Enabled { get; set; } = true;
    public DalamudLoadMethod LoadMethod { get; set; } = DalamudLoadMethod.DllInject;
    public bool UseLocalBuild { get; set; }
    public string LocalBuildDirectory { get; set; } = "";
    public string LocalBuildGameVersion { get; set; } = "";
    public bool NoPlugins { get; set; }
    public int InjectionDelayMs { get; set; }
}

public sealed record PreparedDalamud(string BinaryDirectory, string RuntimeDirectory, string AssetDirectory, string UserDirectory)
{
    public string InjectorPath => Path.Combine(BinaryDirectory, "Dalamud.Injector.exe");
}

public sealed class DalamudVersionInfo
{
    public string AssemblyVersion { get; set; } = "";
    public string SupportedGameVer { get; set; } = "";
    public string RuntimeVersion { get; set; } = "";
    public bool RuntimeRequired { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class DalamudAssetsInfo
{
    public int Version { get; set; }
    public List<DalamudAsset> Assets { get; set; } = [];
}

public sealed class DalamudAsset
{
    public string FileName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Hash { get; set; } = "";
}
