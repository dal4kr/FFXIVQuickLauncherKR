using System.Buffers.Binary;
using XIVLauncher.Common.Patching.ZiPatch;
using XIVLauncher.Common.Patching.ZiPatch.Chunk;
using XIVLauncher.Common.Patching.ZiPatch.Chunk.SqpkCommand;
using XIVLauncher.Common.Patching.ZiPatch.Util;

namespace XIVLauncher.Common.Korea.Patching;

internal static class KrZiPatch
{
    internal static void ApplyVerified(string patchPath, string gamePath, Action beforeApply, CancellationToken token)
    {
        try { ApplyCore(patchPath, gamePath, beforeApply, token); }
        catch (Exception ex) when (ex is ZiPatchException or EndOfStreamException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("손상되었거나 지원하지 않는 ZiPatch 파일입니다.", ex);
        }
    }

    private static void ApplyCore(string patchPath, string gamePath, Action beforeApply, CancellationToken token)
    {
        // Two passes keep memory bounded and ensure a later unsafe path or malformed
        // chunk cannot cause an earlier chunk to modify the installation.
        ValidateFrames(patchPath);
        using (var patch = new ZiPatchFile(File.OpenRead(patchPath), needsChecksum: true))
        {
            foreach (var chunk in patch.GetChunks())
            {
                token.ThrowIfCancellationRequested();
                if (!chunk.IsChecksumValid) throw new InvalidDataException("ZiPatch 청크 체크섬이 일치하지 않습니다.");
                ValidateChunk(chunk, gamePath);
            }
        }
        token.ThrowIfCancellationRequested();
        beforeApply();
        Directory.CreateDirectory(gamePath);
        using var store = new SqexFileStreamStore();
        var config = new ZiPatchConfig(gamePath) { Platform = ZiPatchConfig.PlatformId.Win32, Store = store };
        using var applying = new ZiPatchFile(File.OpenRead(patchPath), needsChecksum: true);
        foreach (var chunk in applying.GetChunks())
        {
            // Do not interrupt inside a patch. A failed application retains pending,
            // so CheckAsync restores complete verified files before another launch.
            if (!chunk.IsChecksumValid) throw new InvalidDataException("적용 중 ZiPatch 체크섬이 일치하지 않습니다.");
            ValidateChunk(chunk, gamePath);
            var target = chunk switch
            {
                SqpkAddData add => add.TargetFile,
                SqpkDeleteData delete => delete.TargetFile,
                SqpkExpandData expand => expand.TargetFile,
                SqpkHeader header => header.TargetFile,
                _ => null
            };
            if (target != null) Directory.CreateDirectory(Path.GetDirectoryName(UpdatePath.In(gamePath, target.RelativePath))!);
            chunk.ApplyChunk(config);
        }
    }

    private static void ValidateFrames(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length < 24) throw new InvalidDataException("ZiPatch 파일이 너무 짧습니다.");
        file.Position = 12; // ZiPatchFile checks the magic before decoding.
        Span<byte> header = stackalloc byte[8];
        while (file.Position < file.Length)
        {
            file.ReadExactly(header);
            var size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size > 128 * 1024 * 1024 || size > file.Length - file.Position - 4)
                throw new InvalidDataException("ZiPatch 청크 크기가 파일 범위를 벗어났습니다.");
            file.Position += size + 4; // data followed by CRC
            if (header[4] == 'E' && header[5] == 'O' && header[6] == 'F' && header[7] == '_')
            {
                // EOF_ may carry a payload; it is already included in the frame length and CRC.
                if (file.Position != file.Length) throw new InvalidDataException("ZiPatch 끝 표시 뒤에 예기치 않은 데이터가 있습니다.");
                return;
            }
        }
        throw new InvalidDataException("ZiPatch 끝 표시가 없습니다.");
    }

    private static void ValidateChunk(ZiPatchChunk chunk, string gamePath)
    {
        switch (chunk)
        {
            case AddDirectoryChunk add:
                UpdatePath.In(gamePath, add.DirName);
                break;
            case DeleteDirectoryChunk delete:
                UpdatePath.In(gamePath, delete.DirName);
                break;
            case SqpkFile file:
                if (!Enum.IsDefined(file.Operation)) throw new InvalidDataException("지원하지 않는 ZiPatch 파일 작업입니다.");
                if (file.Operation == SqpkFile.OperationKind.RemoveAll)
                {
                    if (file.ExpansionId > 5) throw new InvalidDataException("알 수 없는 확장팩의 삭제 작업입니다.");
                    var expansion = SqexFile.GetExpansionFolder((byte)file.ExpansionId);
                    UpdatePath.In(gamePath, "sqpack/" + expansion);
                    UpdatePath.In(gamePath, "movie/" + expansion);
                    foreach (var path in SqexFile.GetAllExpansionFiles(gamePath, file.ExpansionId))
                        UpdatePath.In(gamePath, Path.GetRelativePath(gamePath, path).Replace('\\', '/'));
                }
                else
                {
                    var relative = UpdatePath.Relative(file.TargetFile.RelativePath.TrimEnd('\0'));
                    if (KrPatchProtocol.IsVersionFile(relative))
                        throw new InvalidDataException("ZiPatch가 게임 버전 파일을 직접 변경하려고 합니다.");
                    UpdatePath.In(gamePath, relative);
                    file.TargetFile.RelativePath = relative;
                }
                break;
            case SqpkAddData add: ValidateSqpack(add.TargetFile, gamePath); break;
            case SqpkDeleteData delete: ValidateSqpack(delete.TargetFile, gamePath); break;
            case SqpkExpandData expand: ValidateSqpack(expand.TargetFile, gamePath); break;
            case SqpkHeader header: ValidateSqpack(header.TargetFile, gamePath); break;
            case SqpkIndex index: ValidateSqpack(index.TargetFile, gamePath); break;
            case SqpkTargetInfo info when info.Platform != ZiPatchConfig.PlatformId.Win32:
                throw new InvalidDataException("Windows용이 아닌 게임 패치입니다.");
        }
    }

    private static void ValidateSqpack(SqpackFile file, string gamePath)
    {
        file.ResolvePath(ZiPatchConfig.PlatformId.Win32);
        UpdatePath.In(gamePath, file.RelativePath);
    }
}



