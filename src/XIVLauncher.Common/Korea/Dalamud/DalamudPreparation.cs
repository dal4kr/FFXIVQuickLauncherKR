using System.Text.Json;

namespace XIVLauncher.Common.Korea.Dalamud;

// The preparation sequence follows the GPL-3.0 QuickLauncher implementation.
public sealed class DalamudPreparation
{
    public static readonly Uri VersionUri = new("https://raw.githubusercontent.com/dal4kr/Dalamud.Resources/refs/heads/main/Dalamud/VersionInfo.json");
    public static readonly Uri AssetsUri = new("https://raw.githubusercontent.com/goatcorp/DalamudAssets/refs/heads/master/asset.json");
    private readonly string userDirectory;
    private readonly Uri versionUri;
    private readonly Uri assetsUri;
    private readonly IEnumerable<string>? runtimeRoots;
    private readonly DalamudPackageStore store;

    public DalamudPreparation(HttpClient client, string userDirectory, Uri? versionUri = null, Uri? assetsUri = null,
        IEnumerable<string>? runtimeRoots = null, Func<bool>? isGameRunning = null)
    {
        this.userDirectory = Path.GetFullPath(userDirectory);
        this.versionUri = versionUri ?? VersionUri;
        this.assetsUri = assetsUri ?? AssetsUri;
        this.runtimeRoots = runtimeRoots;
        store = new(client, Path.Combine(this.userDirectory, ".launcher-staging"), isGameRunning ?? DalamudPackageStore.IsGameRunning);
    }

    public async Task<PreparedDalamud> PrepareAsync(DalamudOptions options, string gameVersion, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, bool forceRedownload = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameVersion)) throw new LauncherException("게임 버전을 읽지 못했습니다.");
            Directory.CreateDirectory(userDirectory);
            string binaries;
            string? runtimeVersion;
            if (options.UseLocalBuild)
            {
                if (string.IsNullOrWhiteSpace(options.LocalBuildDirectory))
                    throw new LauncherException("Dalamud 로컬 빌드 폴더를 선택해 주세요.");
                binaries = Path.GetFullPath(options.LocalBuildDirectory.Trim());
                var supported = options.LocalBuildGameVersion.Trim();
                var infoPath = Path.Combine(binaries, "version.json");
                if (File.Exists(infoPath))
                {
                    var local = JsonSerializer.Deserialize<DalamudVersionInfo>(await File.ReadAllBytesAsync(infoPath, cancellationToken).ConfigureAwait(false), DalamudPackageStore.JsonOptions);
                    if (!string.IsNullOrWhiteSpace(local?.SupportedGameVer)) supported = local.SupportedGameVer;
                }
                EnsureGameVersion(gameVersion, supported, options.IgnoreGameVersion);
                progress?.Report("KR Dalamud 로컬 빌드를 확인하는 중입니다…");
                if (!await DalamudPackageStore.ValidateBinaryAsync(binaries, null, cancellationToken).ConfigureAwait(false))
                    throw new LauncherException("Dalamud 로컬 빌드 파일이 없거나 무결성 검사를 통과하지 못했습니다.");
                runtimeVersion = null;
            }
            else
            {
                progress?.Report("KR Dalamud 배포 버전을 확인하는 중입니다…");
                var info = await store.ReadJsonAsync<DalamudVersionInfo>(FreshUri(versionUri), cancellationToken).ConfigureAwait(false);
                EnsureGameVersion(gameVersion, info.SupportedGameVer, options.IgnoreGameVersion);
                if (string.IsNullOrWhiteSpace(info.AssemblyVersion) ||
                    info.AssemblyVersion.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')) ||
                    info.AssemblyVersion.Trim('.').Length == 0 || string.IsNullOrWhiteSpace(info.Hash))
                    throw new LauncherException("KR Dalamud 배포 버전 정보가 올바르지 않습니다.");
                binaries = DalamudPackageStore.Under(Path.Combine(userDirectory, "addon", "Hooks"), info.AssemblyVersion);
                if (forceRedownload || !await DalamudPackageStore.ValidateBinaryAsync(binaries, info.Hash, cancellationToken).ConfigureAwait(false))
                {
                    progress?.Report("KR Dalamud를 다운로드하는 중입니다…");
                    var stage = store.CreateStagingDirectory();
                    try
                    {
                        var archive = Path.Combine(stage, "release.zip");
                        await store.DownloadAsync(FreshUri(DalamudPackageStore.DownloadUri(info.DownloadUrl)), archive, cancellationToken).ConfigureAwait(false);
                        var extracted = Path.Combine(stage, "files");
                        DalamudPackageStore.ExtractArchive(archive, extracted, cancellationToken);
                        if (!await DalamudPackageStore.ValidateBinaryAsync(extracted, info.Hash, cancellationToken).ConfigureAwait(false))
                            throw new LauncherException("다운로드한 KR Dalamud의 무결성을 확인하지 못했습니다.");
                        AtomicFile.Write(Path.Combine(extracted, "version.json"), JsonSerializer.SerializeToUtf8Bytes(info));
                        store.ReplaceDirectory(extracted, binaries);
                    }
                    finally { store.RemoveStagingDirectory(stage); }
                }
                runtimeVersion = info.RuntimeVersion;
            }

            var runtime = await new DalamudRuntime(store, Path.Combine(userDirectory, "runtime"), runtimeRoots)
                .EnsureAsync(binaries, runtimeVersion, progress, cancellationToken).ConfigureAwait(false);
            var assets = await new DalamudAssets(store, Path.Combine(userDirectory, "dalamudAssets"), assetsUri)
                .EnsureAsync(progress, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Combine(userDirectory, "installedPlugins"));
            Directory.CreateDirectory(Path.Combine(userDirectory, "temp"));
            return new(binaries, runtime, assets, userDirectory);
        }
        catch (OperationCanceledException) { throw; }
        catch (LauncherException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidDataException or ArgumentException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new LauncherException("Dalamud를 준비하지 못했습니다: " + error.Message);
        }
    }

    private static Uri FreshUri(Uri uri)
    {
        var builder = new UriBuilder(uri);
        builder.Query = (string.IsNullOrEmpty(builder.Query) ? "" : builder.Query.TrimStart('?') + "&") + "cache_bust=" + Guid.NewGuid().ToString("N");
        return builder.Uri;
    }

    private static void EnsureGameVersion(string gameVersion, string supported, bool ignoreVersion)
    {
        if (string.IsNullOrWhiteSpace(supported))
            throw new LauncherException("로컬 빌드의 대상 게임 버전을 입력하거나 version.json이 있는 빌드 폴더를 선택해 주세요.");
        if (!ignoreVersion && !string.Equals(gameVersion.Trim(), supported.Trim(), StringComparison.Ordinal))
            throw new LauncherException($"Dalamud 지원 게임 버전({supported.Trim()})과 설치된 게임 버전({gameVersion.Trim()})이 다릅니다.");
    }
}
