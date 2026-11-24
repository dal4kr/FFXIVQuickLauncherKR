namespace XIVLauncher.Common.Korea.Patching;

/// <summary>A read-only comparison with the official recovery snapshot and an explicit repair plan.</summary>
public sealed class GameIntegrityCheck
{
    public bool NeedsRepair { get; internal init; }
    public string? CurrentVersion { get; internal init; }
    public string SnapshotVersion { get; internal init; } = "";
    public string? LatestVersion { get; internal init; }
    /// <summary>The installed repository versions differ from the snapshot. Differences do not prove corruption of the installed version.</summary>
    public bool UsesRecoverySnapshot { get; internal init; }
    public long DownloadBytes { get; internal init; }
    public IReadOnlyList<GameIntegrityIssue> Issues { get; internal init; } = Array.Empty<GameIntegrityIssue>();
    public string Message { get; internal init; } = "";
    internal GameUpdateCheck? RepairCheck { get; init; }
}

public enum GameIntegrityIssueKind
{
    Missing,
    SizeMismatch,
    ChecksumMismatch,
    /// <summary>No final-version hash is published for this installed repository version.</summary>
    RecoverySnapshotMismatch
}

public sealed record GameIntegrityIssue(string RelativePath, GameIntegrityIssueKind Kind, long ExpectedBytes);

/// <summary>BytesChecked counts snapshot bytes processed, including missing files and size mismatches that need no hash read.</summary>
public sealed record GameIntegrityProgress(long BytesChecked, long TotalBytes, int CompletedFiles, int TotalFiles, string Message)
{
    public double Percentage => TotalBytes == 0 ? (CompletedFiles == TotalFiles ? 100 : 0) : Math.Clamp(100.0 * BytesChecked / TotalBytes, 0, 100);
}
