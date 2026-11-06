using XIVLauncher.Common.Encryption;

namespace XIVLauncher.Common.Korea;

public sealed class GameLaunchPlan
{
    public required string ExecutablePath { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string Arguments { get; init; }

    public static string GetInstallationDirectory(string selectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(selectedDirectory))
            throw new LauncherException("게임 설치 경로를 선택해 주세요.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory.Trim()));
        if (string.Equals(Path.GetFileName(full), "game", StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(Path.Combine(full, "ffxiv_dx11.exe")) || File.Exists(Path.Combine(full, "ffxivgame.ver"))))
            return Path.GetDirectoryName(full)!;
        return full;
    }

    public static GameLaunchPlan Create(string gameDirectory, LauncherSession session)
    {
        if (!LauncherSession.HasValidTokenFormat(session.Token))
            throw new LauncherException("게임 세션 형식이 올바르지 않습니다. 다시 로그인해 주세요.");
        if (string.IsNullOrWhiteSpace(gameDirectory))
            throw new LauncherException("게임 설치 경로를 선택해 주세요.");
        var directory = Path.Combine(GetInstallationDirectory(gameDirectory), "game");
        var executable = Path.Combine(directory, "ffxiv_dx11.exe");
        if (!File.Exists(executable))
            throw new LauncherException("선택한 경로에서 ffxiv_dx11.exe를 찾지 못했습니다. 게임 설치 폴더 또는 game 폴더를 선택해 주세요.");
        return new GameLaunchPlan
        {
            ExecutablePath = executable,
            WorkingDirectory = directory,
            Arguments = new ArgumentBuilder()
                .Append("DEV.LobbyHost01", "nlobbyf-live.ff14.co.kr")
                .Append("DEV.LobbyPort01", "54994")
                .Append("DEV.GMServerHost", "ngm-live.ff14.co.kr")
                .Append("DEV.TestSID", session.Token)
                .Append("SYS.resetConfig", "0")
                .Append("DEV.SaveDataBankHost", "nconfig-dl-live.ff14.co.kr")
                .Build()
        };
    }
}
