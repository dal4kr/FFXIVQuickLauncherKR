using System.IO;
using Velopack;
using Velopack.Sources;

namespace XIVLauncher;

internal sealed record LauncherUpdateProgress(string Message, int? Percentage = null);

internal sealed class LauncherUpdater
{
    internal const string SkipAutoLaunchArgument = "--no-auto-launch";
    internal const string RepositoryUrl = "https://github.com/dal4kr/FFXIVQuickLauncherKR";
    private readonly UpdateManager manager;
    private UpdateInfo? readyUpdate;

    internal LauncherUpdater(UpdateManager? manager = null)
    {
        this.manager = manager ?? new UpdateManager(new GithubSource(RepositoryUrl, null, false),
            new UpdateOptions { MaximumDeltasBeforeFallback = -1 });
    }

    public bool IsInstalled => manager.IsInstalled;

    public async Task<UpdateInfo?> CheckAndDownloadAsync(IProgress<LauncherUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        readyUpdate = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInstalled) return null;
        if (!string.Equals(manager.AppId, "XIVLauncherKR", StringComparison.Ordinal))
            throw new InvalidOperationException("런처 설치 정보를 확인하지 못했습니다.");

        progress?.Report(new("런처의 새 버전을 확인하는 중입니다…"));
        var update = await manager.CheckForUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (update is null) return null;
        if (!string.Equals(update.TargetFullRelease.PackageId, "XIVLauncherKR", StringComparison.Ordinal))
            throw new InvalidDataException("런처 업데이트 패키지가 올바르지 않습니다.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        progress?.Report(new($"새 버전 {update.TargetFullRelease.Version}을 다운로드하는 중입니다…", 0));
        await manager.DownloadUpdatesAsync(update,
            value => progress?.Report(new($"새 버전 {update.TargetFullRelease.Version}을 다운로드하는 중입니다…", value)),
            timeout.Token).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        readyUpdate = update;
        return update;
    }

    public void ApplyAndRestart(UpdateInfo update)
    {
        if (!ReferenceEquals(update, readyUpdate))
            throw new InvalidOperationException("다운로드와 검증을 마친 업데이트만 적용할 수 있습니다.");
        manager.ApplyUpdatesAndRestart(update.TargetFullRelease, [SkipAutoLaunchArgument]);
    }
}
