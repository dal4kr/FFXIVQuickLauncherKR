namespace XIVLauncher.Common.Korea.Patching;

/// <summary>Rejects Windows path aliases and links before any updater file access.</summary>
internal static class UpdatePath
{
    internal static string Relative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith("//", StringComparison.Ordinal))
            throw new InvalidDataException("패치 목록의 파일 경로가 잘못되었습니다.");
        // The official manifests and ZiPatch SqPack paths use one leading slash.
        path = path.TrimStart('/').TrimEnd('/');
        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("패치 경로가 설치 폴더 밖을 가리키거나 Windows에서 안전하지 않습니다.");
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
                throw new InvalidDataException("패치 경로에 Windows 예약 이름이 포함되어 있습니다.");
        }
        return string.Join('/', segments);
    }

    internal static string Root(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("드라이브 루트는 게임 설치 폴더로 사용할 수 없습니다.");
        NoLinks(full);
        return full;
    }

    internal static string In(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("패치 경로가 게임 설치 폴더 밖을 가리킵니다.");
        NoLinks(full);
        return full;
    }

    private static void NoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("링크 또는 정션을 통과하는 게임 경로에는 업데이트할 수 없습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
