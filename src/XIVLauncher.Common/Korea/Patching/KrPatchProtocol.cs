using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XIVLauncher.Common.Korea.Patching;

internal static partial class KrPatchProtocol
{
    internal const string BaseVersion = "2012.01.01.0000.0000";
    internal static readonly Uri GameManifest = new("https://fcdp.ff14.co.kr/FileListGame.json");
    internal static readonly Uri BootManifest = new("http://boot-launcher-patch-live.ff14.co.kr/FileListLauncher.json");

    [GeneratedRegex(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Md5Pattern();
    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha1Pattern();

    [GeneratedRegex(@"^[HD](\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4})[a-z]?$", RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalVersionPattern();

    internal static string? HistoricalVersion(string version)
    {
        var match = HistoricalVersionPattern().Match(version);
        return match.Success ? match.Groups[1].Value : null;
    }
    internal static bool IsVersion(string? version) => version != null && VersionPattern().IsMatch(version);
    internal static bool IsVersionFile(string path) => path.EndsWith(".ver", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bck", StringComparison.OrdinalIgnoreCase);
    internal static string VersionPath(string repository) => repository == "ffxiv" ? "game/ffxivgame.ver" : $"game/sqpack/{repository}/{repository}.ver";

    internal static Uri OfficialUri(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !uri.Host.EndsWith(".ff14.co.kr", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.IsDefaultPort)
            throw new InvalidDataException("공식 한국 패치 서버 주소가 아닙니다.");
        return uri;
    }

    internal static FileManifest ParseManifest(byte[] bytes, string prefix)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        var baseUrl = root.TryGetProperty("URL", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
        if (string.IsNullOrWhiteSpace(baseUrl) && root.TryGetProperty("URL2", out url)) baseUrl = url.GetString();
        var uri = OfficialUri((baseUrl ?? "").TrimEnd('/') + "/");
        if (uri.Query.Length != 0) throw new InvalidDataException("패치 목록의 기본 주소가 잘못되었습니다.");
        if (!root.TryGetProperty("FileList", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            throw new InvalidDataException("한국 패치 파일 목록이 비어 있습니다.");
        var files = new List<DownloadItem>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in list.EnumerateArray())
        {
            var relative = UpdatePath.Relative(entry.GetProperty("Name").GetString() ?? "");
            var hash = entry.GetProperty("CheckSum").GetString() ?? "";
            var size = entry.GetProperty("Size").GetInt64();
            if (!names.Add(relative) || size < 0 || !Md5Pattern().IsMatch(hash))
                throw new InvalidDataException("패치 파일 목록의 크기·체크섬·중복 경로를 확인할 수 없습니다.");
            total = checked(total + size);
            var escaped = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
            files.Add(new DownloadItem(prefix + "/" + relative, new Uri(uri, escaped), size, hash));
        }
        if (root.TryGetProperty("TotalCount", out var count) && count.GetInt32() != files.Count ||
            root.TryGetProperty("TotalSize", out var totalSize) && totalSize.GetInt64() != total)
            throw new InvalidDataException("패치 목록의 합계가 파일 항목과 일치하지 않습니다.");
        if (prefix == "game" && (!names.Contains("ffxivgame.ver") || !names.Contains("ffxiv_dx11.exe")))
            throw new InvalidDataException("게임 버전 또는 실행 파일이 한국 파일 목록에 없습니다.");
        return new FileManifest(files);
    }

    // Uses the upstream global launcher's documented nine-field patch rows. Unlike its
    // positional parser, this handles each multipart repository section independently.
    internal static List<PatchItem> ParsePatches(string text)
    {
        var patches = new List<PatchItem>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || !char.IsAsciiDigit(line[0])) continue;
            var fields = line.Split('\t');
            if (fields.Length != 9 || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0 ||
                !string.Equals(fields[5], "sha1", StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var blockSize) || blockSize <= 0)
                throw new InvalidDataException("한국 ZiPatch 목록의 형식 또는 해시 방식이 지원되지 않습니다.");
            var hashes = fields[7].Split(',');
            if (hashes.Any(x => !Sha1Pattern().IsMatch(x)) || hashes.LongLength != (size - 1) / blockSize + 1)
                throw new InvalidDataException("패치 SHA-1 블록 목록이 잘못되었습니다.");
            var uri = OfficialUri(fields[8]);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3 || segments[0] != "game")
                throw new InvalidDataException("게임 패치 저장소 주소가 잘못되었습니다.");
            var repository = segments[1] == "de199059" ? "ffxiv" : segments[1];
            if (repository != "ffxiv" && repository is not ("ex1" or "ex2" or "ex3" or "ex4" or "ex5"))
                throw new InvalidDataException("알 수 없는 한국 게임 패치 저장소입니다.");
            patches.Add(new PatchItem(uri, size, fields[4], repository, blockSize, hashes));
        }
        if (!string.IsNullOrWhiteSpace(text) && patches.Count == 0)
            throw new InvalidDataException("패치 서버가 인식할 수 없는 응답을 보냈습니다.");
        return patches;
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("패치 메타데이터 응답이 너무 큽니다.");
        await using var input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[Math.Min(limit + 1, 65536)];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > limit) throw new InvalidDataException("패치 메타데이터 응답이 너무 큽니다.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    internal static bool MatchesMd5(byte[] bytes, DownloadItem item) => bytes.LongLength == item.Size &&
        string.Equals(Convert.ToHexString(MD5.HashData(bytes)), item.Md5, StringComparison.OrdinalIgnoreCase);

    internal static async Task<bool> FileMatchesAsync(string path, long size, string md5, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        var actual = await MD5.HashDataAsync(file, token).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(actual), md5, StringComparison.OrdinalIgnoreCase);
    }
}


