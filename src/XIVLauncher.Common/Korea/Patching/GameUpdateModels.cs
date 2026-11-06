namespace XIVLauncher.Common.Korea.Patching;

/// <summary>A single directory-bound update plan. Check again after applying it.</summary>
public sealed class GameUpdateCheck
{
    public bool NeedsUpdate { get; internal init; }
    public bool IsMaintenance { get; internal init; }
    public bool RequiresAuthentication { get; internal init; }
    public string Message { get; internal init; } = "";
    public string? CurrentVersion { get; internal init; }
    public string? LatestVersion { get; internal init; }
    public long DownloadBytes { get; internal init; }
    internal UpdatePlan? Plan { get; init; }
}

public sealed record GameUpdateProgress(long BytesDownloaded, long TotalBytes, int CompletedFiles, int TotalFiles, string Message)
{
    public double Percentage => TotalBytes == 0 ? (CompletedFiles == TotalFiles ? 100 : 0) : Math.Clamp(100.0 * BytesDownloaded / TotalBytes, 0, 100);
}

internal sealed class UpdatePlan
{
    public required string Directory { get; init; }
    public required string? OriginalVersion { get; init; }
    public required Dictionary<string, string?> OriginalVersions { get; init; }
    public required List<DownloadItem> Files { get; init; }
    public required List<PatchItem> Patches { get; init; }
    public required Dictionary<string, string> Versions { get; init; }
    public bool Applied { get; set; }
}

internal sealed record DownloadItem(string RelativePath, Uri Url, long Size, string Md5);
internal sealed record PatchItem(Uri Url, long Size, string Version, string Repository, long BlockSize, string[] Hashes);
internal sealed record FileManifest(List<DownloadItem> Files);

