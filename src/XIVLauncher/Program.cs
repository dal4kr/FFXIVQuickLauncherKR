using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using XIVLauncher.Common.Korea;

namespace XIVLauncher;

public static class Program
{
    internal static string UserDataDirectory => LauncherDataDirectory.Root;

    [STAThread]
    public static void Main(string[] args)
    {
        // Apply through the update screen so restart keeps the one-time auto-launch block.
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var instance = new Mutex(true, "Local\\FFXIVLauncherKR", out var ownsInstance);
        if (!ownsInstance && args.Contains("--restart"))
        {
            try { ownsInstance = instance.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { ownsInstance = true; }
        }
        if (!ownsInstance)
        {
            MessageBox.Show("FFXIVLauncherKR가 이미 실행 중입니다.", "FFXIVLauncherKR");
            return;
        }
        try
        {
            try
            {
                var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFXIVLauncherKR");
                LauncherDataDirectory.MigrateLegacyFiles(legacy, UserDataDirectory);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show("기존 런처 설정을 가져오지 못했습니다. 데이터 폴더의 접근 권한과 파일 사용 여부를 확인해 주세요.", "FFXIVLauncherKR");
                return;
            }
            var settings = new LauncherSettingsStore(Path.Combine(UserDataDirectory, "settings.json")).Load();
            if (settings.RunAsAdministrator && !IsElevated())
            {
                try { RestartAsAdministrator(); }
                catch (Win32Exception error) when (error.NativeErrorCode == 1223) { }
                catch (Exception) { MessageBox.Show("관리자 권한으로 런처를 시작하지 못했습니다.", "FFXIVLauncherKR"); }
                return;
            }
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
        finally { instance.ReleaseMutex(); }
    }

    internal static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static void RestartAsAdministrator()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new LauncherException("런처 경로를 찾지 못했습니다."))
        { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--restart");
        if (Environment.GetCommandLineArgs().Contains(LauncherUpdater.SkipAutoLaunchArgument, StringComparer.Ordinal))
            start.ArgumentList.Add(LauncherUpdater.SkipAutoLaunchArgument);
        using var process = Process.Start(start) ?? throw new LauncherException("관리자 권한으로 런처를 시작하지 못했습니다.");
    }
}
