using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace XIVLauncher.Common.Korea.Dalamud;

internal sealed class DalamudPackageStore(HttpClient client, string stagingRoot, Func<bool> isGameRunning)
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    internal static readonly string[] RequiredFiles =
    [
        "Dalamud.Injector.exe", "Dalamud.Injector.dll", "Dalamud.Injector.runtimeconfig.json",
        "Dalamud.Boot.dll", "Dalamud.dll", "nethost.dll", "Dalamud.runtimeconfig.json",
    ];

    internal async Task<T> ReadJsonAsync<T>(Uri uri, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(client.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            request.Headers.UserAgent.ParseAdd("FFXIVLauncherKR/0.2");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, timeout.Token).ConfigureAwait(false)
                ?? throw new LauncherException("Dalamud 배포 정보를 읽지 못했습니다.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LauncherException("Dalamud 배포 정보 응답 시간이 초과되었습니다.");
        }
    }

    internal async Task DownloadAsync(Uri uri, string destination, CancellationToken ct)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(client.Timeout);
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await input.CopyToAsync(output, timeout.Token).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                failure = new TimeoutException("파일 전송 시간이 초과되었습니다.");
            }
            catch (Exception error) when (error is HttpRequestException or IOException)
            {
                failure = error;
            }
            if (attempt < 2) await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), ct).ConfigureAwait(false);
        }
        throw new LauncherException("Dalamud 파일 다운로드에 실패했습니다: " + failure!.Message);
    }

    internal string CreateStagingDirectory()
    {
        var path = Under(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal void RemoveStagingDirectory(string path)
    {
        if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(stagingRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Invalid staging directory.");
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void ReplaceDirectory(string stagedDirectory, string destination)
    {
        var hadPrevious = Directory.Exists(destination);
        if (hadPrevious && isGameRunning())
            throw new LauncherException("실행 중인 게임을 종료한 뒤 Dalamud 파일을 업데이트해 주세요.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
        if (hadPrevious) Directory.Move(destination, previous);
        try { Directory.Move(stagedDirectory, destination); }
        catch
        {
            if (hadPrevious) Directory.Move(previous, destination);
            throw;
        }
        // Keep a locked previous directory rather than disturbing a running process.
        if (hadPrevious)
        {
            try { Directory.Delete(previous, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ExtractArchive(string archivePath, string destination, CancellationToken ct, bool overwrite = false)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = entry.FullName.Replace('\\', '/');
            var path = Under(destination, relative.TrimEnd('/'));
            if (relative.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite);
        }
    }

    internal static string Under(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new LauncherException("Dalamud 배포 파일 경로가 비어 있습니다.");
        relative = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) ||
            relative.Split(Path.DirectorySeparatorChar).Any(part => part is "." or ".." ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new LauncherException("Dalamud 배포 파일 경로가 올바르지 않습니다.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!result.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("Dalamud 배포 파일 경로가 저장 폴더를 벗어납니다.");
        return result;
    }

    internal static Uri DownloadUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new LauncherException("Dalamud 다운로드 주소가 올바르지 않습니다.");
        return uri;
    }

    internal static async Task<bool> HashMatchesAsync(string path, string expected, bool sha1, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var bytes = sha1 ? await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false) : await MD5.HashDataAsync(stream, ct).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(bytes), expected, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<bool> ValidateBinaryAsync(string directory, string? manifestHash, CancellationToken ct)
    {
        try
        {
            if (RequiredFiles.Any(name => !File.Exists(Under(directory, name)))) return false;
            var hashPath = Under(directory, "hashes.json");
            if (manifestHash is null && !File.Exists(hashPath)) return true;
            if (!string.IsNullOrWhiteSpace(manifestHash) && !await HashMatchesAsync(hashPath, manifestHash, false, ct).ConfigureAwait(false))
                return false;
            var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllBytesAsync(hashPath, ct).ConfigureAwait(false));
            if (hashes is null || hashes.Count == 0 || RequiredFiles.Any(name => !hashes.ContainsKey(name))) return false;
            foreach (var (relative, expected) in hashes)
                if (!await HashMatchesAsync(Under(directory, relative), expected, false, ct).ConfigureAwait(false)) return false;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal static bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName("ffxiv_dx11");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
