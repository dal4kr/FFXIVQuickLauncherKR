using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace XIVLauncher.Common.Korea.Patching;

/// <summary>
/// KR manifests provide complete files; the upstream ZiPatch engine applies incremental
/// updates. No launcher, game, installer or elevation helper is executed here.
/// </summary>
public sealed class KrGameUpdater(HttpClient http)
{
    private const string CacheDirectory = ".xivlauncher-update";
    private readonly SemaphoreSlim updateGate = new(1, 1);

    /// <summary>Checks installed repository versions with one patch-list request. Full manifests are loaded only for installation or recovery.</summary>
    /// <param name="sid">Reserved for servers requiring authenticated patch metadata. The verified anonymous protocol does not transmit SID.</param>
    public Task<GameUpdateCheck> CheckAsync(string gameDirectory, string? sid = null, CancellationToken cancellationToken = default) =>
        CheckCoreAsync(gameDirectory, cancellationToken);

    private async Task<GameUpdateCheck> CheckCoreAsync(string gameDirectory, CancellationToken token)
    {
        var root = UpdatePath.Root(gameDirectory);
        var current = ReadVersion(root, "game/ffxivgame.ver");
        try
        {
            if (KrPatchProtocol.IsVersion(current) && File.Exists(UpdatePath.In(root, "game/ffxiv_dx11.exe")) &&
                !File.Exists(UpdatePath.In(root, CacheDirectory + "/pending")))
            {
                try
                {
                    var available = await GetPatchesAsync(current!, token).ConfigureAwait(false);
                    var local = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["game/ffxivgame.ver"] = current };
                    foreach (var repository in available.Select(x => x.Repository).Distinct())
                    {
                        var path = KrPatchProtocol.VersionPath(repository);
                        local[path] = ReadVersion(root, path);
                    }
                    if (TrySelectPatches(available, local, out var patches, out var versions))
                        return CreateCheck(root, current, current!, [], patches, versions, local);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Gone)
                {
                    // The server retired this installed version's incremental chain.
                }
            }
            return await CheckSnapshotAsync(root, current, token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return AuthenticationRequired(current);
        }
    }

    private async Task<GameUpdateCheck> CheckSnapshotAsync(string root, string? current, CancellationToken token)
    {
        // Neither full manifest is needed for the normal pre-launch check. When a
        // complete snapshot is required, independent metadata requests run together.
        var gameTask = GetManifestAsync(KrPatchProtocol.GameManifest, "game", token);
        var bootTask = GetManifestAsync(KrPatchProtocol.BootManifest, "boot", token);
        await Task.WhenAll(gameTask, bootTask).ConfigureAwait(false);
        var game = await gameTask.ConfigureAwait(false);
        var boot = await bootTask.ConfigureAwait(false);

        // Manifest.Version is a distribution label, not ffxivgame.ver. Authenticate
        // the tiny repository version files concurrently against their manifest MD5.
        var versionTasks = game.Files.Where(x => x.RelativePath.EndsWith(".ver", StringComparison.OrdinalIgnoreCase)).Select(async file =>
        {
            if (file.Size > 128) throw new InvalidDataException("게임 버전 파일이 너무 큽니다.");
            var bytes = await GetMetadataAsync(file.Url, 128, token).ConfigureAwait(false);
            if (!KrPatchProtocol.MatchesMd5(bytes, file)) throw new InvalidDataException("게임 버전 파일 체크섬이 일치하지 않습니다.");
            var version = Encoding.ASCII.GetString(bytes).Trim();
            if (!KrPatchProtocol.IsVersion(version)) throw new InvalidDataException("공식 게임 버전 파일의 형식이 잘못되었습니다.");
            return KeyValuePair.Create(file.RelativePath, version);
        }).ToArray();
        var snapshot = (await Task.WhenAll(versionTasks).ConfigureAwait(false)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        if (!snapshot.TryGetValue("game/ffxivgame.ver", out var snapshotVersion))
            throw new InvalidDataException("게임 버전을 확인할 수 없습니다.");

        // Always query again from the snapshot baseline. A chain requested from a
        // newer local version omits intermediate patches needed after restoring an
        // older snapshot, even if the first query only failed on an expansion.
        var available = await GetPatchesAsync(snapshotVersion, token).ConfigureAwait(false);
        var baseline = snapshot.ToDictionary(x => x.Key, x => (string?)x.Value, StringComparer.OrdinalIgnoreCase);
        if (!TrySelectPatches(available, baseline, out var patches, out var patchedVersions))
            throw new InvalidDataException("공식 복구 파일에서 이어지는 패치 경로를 확인할 수 없습니다.");
        var versions = new Dictionary<string, string>(snapshot, StringComparer.OrdinalIgnoreCase);
        foreach (var version in patchedVersions) versions[version.Key] = version.Value;
        var latest = versions["game/ffxivgame.ver"];
        if (KrPatchProtocol.IsVersion(current) && string.CompareOrdinal(current, latest) > 0)
            throw new InvalidDataException("설치된 게임보다 공식 복구 파일이 오래되었습니다. 이전 버전으로 덮어쓰지 않습니다.");

        var files = new List<DownloadItem>();
        foreach (var file in boot.Files.Concat(game.Files))
        {
            token.ThrowIfCancellationRequested();
            if (!await KrPatchProtocol.FileMatchesAsync(UpdatePath.In(root, file.RelativePath), file.Size, file.Md5, token).ConfigureAwait(false))
                files.Add(file);
        }
        var original = snapshot.Keys.ToDictionary(path => path, path => ReadVersion(root, path), StringComparer.OrdinalIgnoreCase);
        return CreateCheck(root, current, snapshotVersion, files, patches, versions, original);
    }

    private async Task<List<PatchItem>> GetPatchesAsync(string version, CancellationToken token)
    {
        var uri = new Uri($"http://ngamever-live.ff14.co.kr/http/win32/actoz_release_ko_game/{version}/");
        var bytes = await GetMetadataAsync(uri, 4 * 1024 * 1024, token).ConfigureAwait(false);
        return KrPatchProtocol.ParsePatches(Encoding.UTF8.GetString(bytes));
    }

    private static bool TrySelectPatches(List<PatchItem> available, Dictionary<string, string?> baseline,
                                         out List<PatchItem> patches, out Dictionary<string, string> versions)
    {
        patches = [];
        versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in available)
        {
            var path = KrPatchProtocol.VersionPath(patch.Repository);
            if (!baseline.TryGetValue(path, out var local) || !KrPatchProtocol.IsVersion(local)) return false;
            if (!KrPatchProtocol.IsVersion(patch.Version))
            {
                // ngamever includes old expansion history even for an up-to-date
                // installation. Skip old split patches, but never guess how to apply
                // a needed split chain or a version format we do not understand.
                var historical = KrPatchProtocol.HistoricalVersion(patch.Version);
                if (historical != null && string.CompareOrdinal(historical, local) <= 0) continue;
                return false;
            }
            if (string.CompareOrdinal(patch.Version, local) <= 0) continue;
            patches.Add(patch);
            if (!versions.TryGetValue(path, out var latest) || string.CompareOrdinal(patch.Version, latest) > 0)
                versions[path] = patch.Version;
        }
        return true;
    }

    private static GameUpdateCheck CreateCheck(string root, string? current, string baselineVersion, List<DownloadItem> files,
                                               List<PatchItem> patches, Dictionary<string, string> versions, Dictionary<string, string?> original)
    {
        var totalBytes = files.Aggregate(0L, (sum, file) => checked(sum + file.Size));
        totalBytes = patches.Aggregate(totalBytes, (sum, patch) => checked(sum + patch.Size));
        var needsUpdate = files.Count != 0 || patches.Count != 0 || File.Exists(UpdatePath.In(root, CacheDirectory + "/pending"));
        return new GameUpdateCheck
        {
            NeedsUpdate = needsUpdate,
            CurrentVersion = current,
            LatestVersion = versions.GetValueOrDefault("game/ffxivgame.ver") ?? baselineVersion,
            DownloadBytes = totalBytes,
            Message = needsUpdate ? $"한국 게임 파일 업데이트가 필요합니다. ({totalBytes:N0} 바이트)" : "한국 게임이 최신 버전입니다.",
            Plan = new UpdatePlan { Directory = root, OriginalVersion = current, OriginalVersions = original, Files = files, Patches = patches, Versions = versions }
        };
    }
    public async Task UpdateAsync(string gameDirectory, GameUpdateCheck check, IProgress<GameUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var root = UpdatePath.Root(gameDirectory);
        var plan = check.Plan;
        if (check.IsMaintenance || check.RequiresAuthentication || plan == null || plan.Applied ||
            !string.Equals(plan.Directory, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("업데이트 계획이 유효하지 않습니다. 설치 폴더의 업데이트를 다시 확인하세요.");
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (plan.Applied || !VersionsUnchanged(root, plan))
                throw new InvalidOperationException("확인한 뒤 게임 버전이 바뀌었습니다. 업데이트를 다시 확인하세요.");
            if (!check.NeedsUpdate) { plan.Applied = true; return; }
            GuardRunningProcesses();
            Directory.CreateDirectory(root);
            var cache = UpdatePath.In(root, CacheDirectory);
            Directory.CreateDirectory(cache);
            // FileShare.None also prevents another launcher process applying a plan.
            await using var updateLock = new FileStream(UpdatePath.In(root, CacheDirectory + "/lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!VersionsUnchanged(root, plan))
                throw new InvalidOperationException("확인한 뒤 게임 버전이 바뀌었습니다. 업데이트를 다시 확인하세요.");
            var pending = UpdatePath.In(root, CacheDirectory + "/pending");
            void MarkPending() => File.WriteAllText(UpdatePath.In(root, CacheDirectory + "/pending"), "Incomplete update: recheck and verify before launch.");
            var state = new TransferState(check.DownloadBytes, plan.Files.Count + plan.Patches.Count, progress);
            var versionDownloads = new List<(DownloadItem File, string Cache)>();
            var patchDownloads = new List<string>();
            foreach (var file in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var download = await DownloadAsync(root, file.Url, file.Size, file.Md5, null, state, file.RelativePath, cancellationToken).ConfigureAwait(false);
                if (KrPatchProtocol.IsVersionFile(file.RelativePath)) versionDownloads.Add((file, download));
                else
                {
                    MarkPending();
                    ReplaceFile(root, file.RelativePath, download);
                }
                state.Complete(file.RelativePath);
            }
            foreach (var patch in plan.Patches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var download = await DownloadAsync(root, patch.Url, patch.Size, null, patch, state, patch.Version, cancellationToken).ConfigureAwait(false);
                // Validate every CRC and path before the first chunk writes. Cancellation
                // is honored before a patch; an active patch finishes to a safe boundary.
                GuardRunningProcesses();
                KrZiPatch.ApplyVerified(download, UpdatePath.In(root, "game"), MarkPending, cancellationToken);
                patchDownloads.Add(download);
                state.Complete("패치 적용: " + patch.Version);
            }
            cancellationToken.ThrowIfCancellationRequested();
            MarkPending();
            foreach (var item in versionDownloads)
            {
                if (!plan.Versions.ContainsKey(item.File.RelativePath)) ReplaceFile(root, item.File.RelativePath, item.Cache);
            }
            // Commit base .ver last. If any write fails, the pending marker forces a
            // full verified repair on the next check, even if some markers were saved.
            foreach (var version in plan.Versions.OrderBy(x => x.Key.Equals("game/ffxivgame.ver", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                var staged = UpdatePath.In(root, CacheDirectory + "/version.tmp");
                await File.WriteAllTextAsync(staged, version.Value, Encoding.ASCII, CancellationToken.None).ConfigureAwait(false);
                ReplaceFile(root, version.Key, staged);
            }
            File.Delete(pending);
            plan.Applied = true;
            foreach (var download in patchDownloads.Concat(versionDownloads.Select(x => x.Cache)))
            {
                try { File.Delete(download); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            state.Finish();
        }
        finally { updateGate.Release(); }
    }

    private async Task<FileManifest> GetManifestAsync(Uri uri, string prefix, CancellationToken token)
    {
        try { return KrPatchProtocol.ParseManifest(await GetMetadataAsync(uri, 4 * 1024 * 1024, token).ConfigureAwait(false), prefix); }
        catch (System.Text.Json.JsonException ex) { throw new InvalidDataException("한국 패치 파일 목록 JSON을 읽을 수 없습니다.", ex); }
        catch (KeyNotFoundException ex) { throw new InvalidDataException("한국 패치 파일 목록에 필수 항목이 없습니다.", ex); }
    }

    private async Task<byte[]> GetMetadataAsync(Uri uri, int limit, CancellationToken token)
    {
        using var request = Request(uri);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        VerifyResponse(response);
        return await KrPatchProtocol.ReadBoundedAsync(response.Content, limit, token).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(Uri uri)
    {
        KrPatchProtocol.OfficialUri(uri.AbsoluteUri);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("FFXIV_Patch");
        request.Headers.TryAddWithoutValidation("X-Hash-Check", "enabled");
        return request;
    }

    private static void VerifyResponse(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final) KrPatchProtocol.OfficialUri(final.AbsoluteUri);
    }

    private static GameUpdateCheck AuthenticationRequired(string? current) => new()
    {
        NeedsUpdate = true,
        RequiresAuthentication = true,
        CurrentVersion = current,
        Message = "공식 패치 서버가 인증을 요청했습니다. 공식 런처에서 업데이트 상태를 확인하세요."
    };

    private static string? ReadVersion(string root, string relative)
    {
        var path = UpdatePath.In(root, relative);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("로컬 게임 버전 파일이 너무 큽니다.");
        return File.ReadAllText(path).Trim();
    }

    private static bool VersionsUnchanged(string root, UpdatePlan plan) =>
        ReadVersion(root, "game/ffxivgame.ver") == plan.OriginalVersion && plan.OriginalVersions.All(x => ReadVersion(root, x.Key) == x.Value);

    private static void ReplaceFile(string root, string relative, string source)
    {
        GuardRunningProcesses();
        var target = UpdatePath.In(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        // Recheck after creating directories. Never delete the old target first.
        target = UpdatePath.In(root, relative);
        File.Move(source, target, true);
    }

    private static void GuardRunningProcesses()
    {
        foreach (var name in new[] { "ffxiv_dx11", "ffxiv", "FFXIV_Boot", "FFXIV_Launcher", "ffxivboot", "ffxivboot64", "ffxivlauncher", "ffxivlauncher64" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) throw new InvalidOperationException("게임과 공식 런처를 종료한 후 업데이트하세요."); }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private sealed class TransferState(long totalBytes, int totalFiles, IProgress<GameUpdateProgress>? progress)
    {
        private long bytes;
        private int files;
        internal void Add(long count, string message) { bytes = checked(bytes + count); Report(message); }
        internal void Complete(string message) { files++; Report(message); }
        internal void Finish() { bytes = totalBytes; files = totalFiles; Report("한국 게임 업데이트를 완료했습니다."); }
        private void Report(string message) => progress?.Report(new GameUpdateProgress(bytes, totalBytes, files, totalFiles, message));
    }

    private async Task<string> DownloadAsync(string root, Uri url, long size, string? md5, PatchItem? patch, TransferState state, string name, CancellationToken token)
    {
        var identity = url.AbsoluteUri + "|" + size + "|" + (md5 ?? string.Join(',', patch!.Hashes));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var path = UpdatePath.In(root, CacheDirectory + "/" + key + ".partial");
        if (File.Exists(path) && new FileInfo(path).Length == size)
        {
            if (await VerifyDownloadAsync(path, size, md5, patch, token).ConfigureAwait(false)) { state.Add(size, name); return path; }
            File.Delete(path);
        }
        var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset > size) { File.Delete(path); offset = 0; }
        using var request = Request(url);
        if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        VerifyResponse(response);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != offset || range.To != size - 1 || range.Length != size)
                throw new InvalidDataException("재개 다운로드의 범위가 요청과 일치하지 않습니다.");
        }
        else
        {
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("지원하지 않는 게임 파일 다운로드 응답입니다.");
            offset = 0; // A server may legally ignore Range and return the full file.
        }
        if (response.Content.Headers.ContentLength is { } length && length != size - offset)
            throw new InvalidDataException("게임 다운로드의 크기가 공식 파일 목록과 일치하지 않습니다.");
        var received = offset;
        state.Add(offset, name);
        await using (var output = new FileStream(UpdatePath.In(root, CacheDirectory + "/" + key + ".partial"), offset == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None, 65536, true))
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        {
            var buffer = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > size - received) throw new InvalidDataException("게임 다운로드가 예상 파일 크기를 초과했습니다.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                received += count;
                state.Add(count, name);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        if (received != size || !await VerifyDownloadAsync(path, size, md5, patch, token).ConfigureAwait(false))
        {
            File.Delete(path);
            throw new InvalidDataException("게임 파일 크기 또는 체크섬이 일치하지 않습니다: " + name);
        }
        return path;
    }

    private static async Task<bool> VerifyDownloadAsync(string path, long size, string? md5, PatchItem? patch, CancellationToken token)
    {
        if (md5 != null) return await KrPatchProtocol.FileMatchesAsync(path, size, md5, token).ConfigureAwait(false);
        if (new FileInfo(path).Length != size) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var buffer = new byte[65536];
        foreach (var expected in patch!.Hashes)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            var left = Math.Min(patch.BlockSize, file.Length - file.Position);
            while (left > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(left, buffer.Length)), token).ConfigureAwait(false);
                if (read == 0) return false;
                hash.AppendData(buffer, 0, read);
                left -= read;
            }
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return file.Position == file.Length;
    }
}







