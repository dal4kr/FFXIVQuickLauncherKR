using System.Security.Cryptography;

namespace XIVLauncher.Common.Korea.Patching;

public sealed partial class KrGameUpdater
{
    /// <summary>
    /// Manually hashes game files against the official complete-file manifest without
    /// writing to the installation. A different installed version is reported as a
    /// recovery-snapshot comparison, never as confirmed corruption of newer files.
    /// </summary>
    public async Task<GameIntegrityCheck> CheckIntegrityAsync(string gameDirectory, IProgress<GameIntegrityProgress>? progress = null,
                                                             CancellationToken cancellationToken = default)
    {
        var root = UpdatePath.Root(gameDirectory);
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            GuardRunningProcesses();
            var scan = new IntegrityScan(progress);
            var check = await CheckSnapshotAsync(root, ReadVersion(root, "game/ffxivgame.ver"), cancellationToken, scan).ConfigureAwait(false);
            GuardRunningProcesses();
            if (!VersionsUnchanged(root, check.Plan!))
                throw new InvalidOperationException("검사 중 게임 버전이 바뀌었습니다. 무결성 검사를 다시 실행하세요.");
            return new GameIntegrityCheck
            {
                NeedsRepair = check.NeedsUpdate,
                CurrentVersion = check.CurrentVersion,
                SnapshotVersion = scan.SnapshotVersion,
                LatestVersion = check.LatestVersion,
                UsesRecoverySnapshot = scan.UsesRecoverySnapshot,
                DownloadBytes = check.DownloadBytes,
                Issues = scan.Issues.AsReadOnly(),
                Message = scan.UsesRecoverySnapshot
                    ? "설치된 버전과 공식 복구 기준 버전이 다릅니다. 불일치는 손상을 의미하지 않습니다. 복구하면 공식 기준 파일을 복원한 뒤 최신 패치를 적용합니다."
                    : check.NeedsUpdate ? "공식 게임 파일과 다른 파일이 있거나 업데이트가 필요합니다." : "공식 게임 파일의 무결성 검사를 완료했습니다.",
                RepairCheck = check
            };
        }
        finally { updateGate.Release(); }
    }

    /// <summary>Explicitly restores verified complete files, applies all subsequent patches, and commits repository versions last.</summary>
    public Task RepairIntegrityAsync(string gameDirectory, GameIntegrityCheck check, IProgress<GameUpdateProgress>? progress = null,
                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (check.RepairCheck == null) throw new InvalidOperationException("무결성 검사 결과가 유효하지 않습니다. 다시 검사하세요.");
        return UpdateAsync(gameDirectory, check.RepairCheck, progress, cancellationToken);
    }

    private sealed class IntegrityScan(IProgress<GameIntegrityProgress>? progress)
    {
        internal string SnapshotVersion { get; private set; } = "";
        internal bool UsesRecoverySnapshot { get; private set; }
        internal List<GameIntegrityIssue> Issues { get; } = [];
        private byte[]? readBuffer;
        private long bytes;
        private long totalBytes;
        private int files;
        private int totalFiles;

        internal void Start(string snapshotVersion, IEnumerable<DownloadItem> items, Dictionary<string, string> snapshot,
                            Dictionary<string, string?> original)
        {
            SnapshotVersion = snapshotVersion;
            UsesRecoverySnapshot = original.Any(x => x.Value != snapshot[x.Key]);
            totalBytes = items.Aggregate(0L, (sum, file) => checked(sum + file.Size));
            totalFiles = items.Count();
            Report("공식 게임 파일의 무결성을 검사합니다.");
        }

        internal async Task<bool> MatchesAsync(string root, DownloadItem item, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            GuardRunningProcesses();
            var path = UpdatePath.In(root, item.RelativePath);
            GameIntegrityIssueKind? issue = null;
            if (!File.Exists(path)) issue = GameIntegrityIssueKind.Missing;
            else
            {
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
                                                      FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != item.Size) issue = GameIntegrityIssueKind.SizeMismatch;
                else
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    var buffer = readBuffer ??= new byte[1024 * 1024];
                    long readBytes = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (count == 0) break;
                        hash.AppendData(buffer, 0, count);
                        readBytes = checked(readBytes + count);
                        bytes = checked(bytes + count);
                        Report(item.RelativePath);
                    }
                    token.ThrowIfCancellationRequested();
                    if (readBytes != item.Size || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), item.Md5, StringComparison.OrdinalIgnoreCase))
                        issue = GameIntegrityIssueKind.ChecksumMismatch;
                }
            }
            if (issue is GameIntegrityIssueKind.Missing or GameIntegrityIssueKind.SizeMismatch) bytes = checked(bytes + item.Size);
            if (issue != null)
            {
                var kind = UsesRecoverySnapshot && issue != GameIntegrityIssueKind.Missing ? GameIntegrityIssueKind.RecoverySnapshotMismatch : issue.Value;
                Issues.Add(new GameIntegrityIssue(item.RelativePath, kind, item.Size));
            }
            files++;
            Report(item.RelativePath);
            token.ThrowIfCancellationRequested();
            return issue == null;
        }

        private void Report(string message) => progress?.Report(new GameIntegrityProgress(bytes, totalBytes, files, totalFiles, message));
    }
}
