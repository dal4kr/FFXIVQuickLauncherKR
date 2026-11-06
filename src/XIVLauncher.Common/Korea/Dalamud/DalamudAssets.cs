namespace XIVLauncher.Common.Korea.Dalamud;

internal sealed class DalamudAssets(DalamudPackageStore store, string root, Uri metadataUri)
{
    internal async Task<string> EnsureAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Dalamud 리소스를 확인하는 중입니다…");
        var info = await store.ReadJsonAsync<DalamudAssetsInfo>(metadataUri, ct).ConfigureAwait(false);
        if (info.Version <= 0 || info.Assets is not { Count: > 0 } ||
            info.Assets.Any(asset => asset is null || string.IsNullOrWhiteSpace(asset.FileName) || string.IsNullOrWhiteSpace(asset.Url))) throw new LauncherException("Dalamud 리소스 배포 정보가 비어 있습니다.");
        var destination = DalamudPackageStore.Under(root, info.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (await ValidateAsync(destination, info, ct).ConfigureAwait(false)) return destination;
        var stage = store.CreateStagingDirectory();
        try
        {
            foreach (var asset in info.Assets)
            {
                ct.ThrowIfCancellationRequested();
                var target = DalamudPackageStore.Under(stage, asset.FileName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var previous = DalamudPackageStore.Under(destination, asset.FileName);
                if (await AssetMatchesAsync(previous, asset, ct).ConfigureAwait(false))
                {
                    File.Copy(previous, target);
                    continue;
                }
                var dev = DalamudPackageStore.Under(Path.Combine(root, "dev"), asset.FileName);
                if (await AssetMatchesAsync(dev, asset, ct).ConfigureAwait(false))
                {
                    File.Copy(dev, target);
                    continue;
                }
                progress?.Report("Dalamud 리소스 다운로드: " + asset.FileName);
                var valid = false;
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await store.DownloadAsync(DalamudPackageStore.DownloadUri(asset.Url), target, ct).ConfigureAwait(false);
                    if (await AssetMatchesAsync(target, asset, ct).ConfigureAwait(false)) { valid = true; break; }
                }
                if (!valid) throw new LauncherException("다운로드한 Dalamud 리소스의 무결성을 확인하지 못했습니다: " + asset.FileName);
            }
            if (!await ValidateAsync(stage, info, ct).ConfigureAwait(false))
                throw new LauncherException("Dalamud 리소스 준비를 완료하지 못했습니다.");
            store.ReplaceDirectory(stage, destination);
            AtomicFile.Write(Path.Combine(root, "asset.ver"), System.Text.Encoding.UTF8.GetBytes(info.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return destination;
        }
        finally { store.RemoveStagingDirectory(stage); }
    }

    private static async Task<bool> ValidateAsync(string directory, DalamudAssetsInfo info, CancellationToken ct)
    {
        foreach (var asset in info.Assets)
            if (!await AssetMatchesAsync(DalamudPackageStore.Under(directory, asset.FileName), asset, ct).ConfigureAwait(false)) return false;
        return true;
    }

    private static async Task<bool> AssetMatchesAsync(string path, DalamudAsset asset, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return string.IsNullOrEmpty(asset.Hash) ? new FileInfo(path).Length > 0 :
                await DalamudPackageStore.HashMatchesAsync(path, asset.Hash, true, ct).ConfigureAwait(false);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
