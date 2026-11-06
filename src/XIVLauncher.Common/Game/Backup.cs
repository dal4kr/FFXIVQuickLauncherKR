using System.IO.Compression;
using System.IO.Enumeration;

namespace XIVLauncher.Common.Game;

public sealed class BackupFileException(string filePath, Exception inner)
    : IOException($"백업 파일을 처리하지 못했습니다: {filePath}", inner)
{
    public string FilePath { get; } = filePath;
}

public static class Backup
{
    private static readonly string[] IncludedRoamingFolders = ["pluginConfigs", "installedPlugins"];
    private static readonly string[] IncludedRoamingFiles =
        ["dalamudVfs.db", "dalamudConfig.json", "dalamudUI.ini", "settings.json", "accounts.dat"];
    private static readonly string[] IncludedUserPatterns = ["FFXIV_CHR*", "*.cfg", "*.dat"];

    public const string BACKUP_EXTENSION = ".xivlauncher_backup";

    public static void CreateBackup(DirectoryInfo roamingPath, DirectoryInfo? userPath, FileInfo targetFile)
    {
        var temporary = targetFile.FullName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (IncludedRoamingFiles.Any(name => string.Equals(targetFile.FullName, Path.Combine(roamingPath.FullName, name), StringComparison.OrdinalIgnoreCase)) ||
                IncludedRoamingFolders.Any(name => IsWithin(Path.Combine(roamingPath.FullName, name), targetFile.FullName)) ||
                userPath is not null && IsWithin(userPath.FullName, targetFile.FullName))
                throw new IOException("백업 파일은 백업 대상 폴더 밖에 저장해 주세요.");
            RejectLinks(targetFile.FullName);
            Directory.CreateDirectory(targetFile.DirectoryName!);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in IncludedRoamingFiles)
                {
                    var file = Path.Combine(roamingPath.FullName, name);
                    if (File.Exists(file)) AddFile(file, name);
                }
                foreach (var name in IncludedRoamingFolders)
                {
                    var directory = Path.Combine(roamingPath.FullName, name);
                    if (Directory.Exists(directory))
                        foreach (var file in EnumerateFiles(directory))
                            AddFile(file, Path.GetRelativePath(roamingPath.FullName, file));
                }
                if (userPath is not null && Directory.Exists(userPath.FullName))
                {
                    foreach (var file in EnumerateFiles(userPath.FullName))
                    {
                        var relative = Path.GetRelativePath(userPath.FullName, file).Replace('\\', '/');
                        if (relative.StartsWith("cfgcpy/", StringComparison.OrdinalIgnoreCase) ||
                            IncludedUserPatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(file), true)))
                            AddFile(file, "user/" + relative);
                    }
                }

                void AddFile(string path, string name)
                {
                    if (string.Equals(path, targetFile.FullName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(path, temporary, StringComparison.OrdinalIgnoreCase)) return;
                    RejectLinks(path);
                    name = name.Replace('\\', '/');
                    if (!names.Add(name)) return;
                    try
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                        entry.ExternalAttributes = 0;
                        entry.LastWriteTime = DateTimeOffset.UtcNow;
                        using var output = entry.Open();
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        input.CopyTo(output);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    { throw new BackupFileException(path, error); }
                }
            }
            RejectLinks(targetFile.FullName);
            File.Move(temporary, targetFile.FullName, true);
        }
        catch (Exception error) when (error is not BackupFileException && error is IOException or UnauthorizedAccessException)
        { throw new BackupFileException(targetFile.FullName, error); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static bool BackupHasUserFiles(FileInfo targetFile)
    {
        try
        {
            using var stream = targetFile.OpenRead();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Any(entry => entry.FullName.Replace('\\', '/').StartsWith("user/", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new BackupFileException(targetFile.FullName, error); }
    }

    public static void RestoreBackup(DirectoryInfo roamingPath, DirectoryInfo? userPath, FileInfo targetFile)
    {
        string? staging = null;
        try
        {
            using var stream = targetFile.OpenRead();
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<(ZipArchiveEntry Entry, string Name, string Destination)>();
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var isDirectory = name.EndsWith('/');
                var parts = name.TrimEnd('/').Split('/');
                if (parts.Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
                    part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException("백업에 올바르지 않은 파일 경로가 있습니다.");
                if (!names.Add(name.TrimEnd('/')))
                    throw new InvalidDataException("백업에 중복된 파일 경로가 있습니다.");

                var isUser = string.Equals(parts[0], "user", StringComparison.OrdinalIgnoreCase);
                if (!isUser && !(IncludedRoamingFolders.Contains(parts[0], StringComparer.OrdinalIgnoreCase) && (isDirectory || parts.Length > 1)) &&
                    !(parts.Length == 1 && !isDirectory && IncludedRoamingFiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase)))
                    throw new InvalidDataException("이 KR 런처에서 지원하지 않는 백업 항목이 있습니다.");
                if (isUser && !isDirectory && parts.Length < 2) throw new InvalidDataException("게임 설정 경로가 올바르지 않습니다.");
                if (isDirectory || isUser && userPath is null) continue;
                var root = isUser ? userPath!.FullName : roamingPath.FullName;
                var relative = isUser ? Path.Combine(parts[1..]) : Path.Combine(parts);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!IsWithin(root, destination))
                    throw new InvalidDataException("백업 경로가 복원 폴더를 벗어났습니다.");
                RejectLinks(destination);
                files.Add((entry, name, destination));
            }

            staging = Path.Combine(roamingPath.FullName, "temp", "restore-" + Guid.NewGuid().ToString("N"));
            RejectLinks(staging);
            Directory.CreateDirectory(staging);
            foreach (var file in files)
            {
                var path = Path.Combine(staging, file.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                file.Entry.ExtractToFile(path);
            }
            foreach (var file in files)
            {
                RejectLinks(file.Destination);
                Directory.CreateDirectory(Path.GetDirectoryName(file.Destination)!);
                File.Move(Path.Combine(staging, file.Name.Replace('/', Path.DirectorySeparatorChar)), file.Destination, true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw new BackupFileException(targetFile.FullName, error); }
        finally { if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    private static bool IsWithin(string root, string path) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        RejectLinks(directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLinks(path);
            if (Directory.Exists(path))
            {
                foreach (var file in EnumerateFiles(path)) yield return file;
            }
            else yield return path;
        }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null)
                throw new IOException("연결된 폴더나 파일은 백업·복원할 수 없습니다.");
        }
    }
}
