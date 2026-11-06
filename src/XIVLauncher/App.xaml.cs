using System.Windows;
using System.Windows.Input;
using XIVLauncher.Windows;

namespace XIVLauncher;

public partial class App : Application
{
    private async void App_OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("작업을 완료하지 못했습니다. 런처를 다시 열어 주세요.", "FFXIVLauncherKR");
            args.Handled = true;
        };
        var suppressAutoLaunch = e.Args.Contains(LauncherUpdater.SkipAutoLaunchArgument, StringComparer.Ordinal) ||
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        LauncherUpdateWindow? updateWindow = null;
        try
        {
            var updater = new LauncherUpdater();
            if (updater.IsInstalled)
            {
                updateWindow = new LauncherUpdateWindow();
                MainWindow = updateWindow;
                updateWindow.Show();
                var update = await updater.CheckAndDownloadAsync(
                    new Progress<LauncherUpdateProgress>(updateWindow.Report), updateWindow.ClosingToken);
                if (update is not null)
                {
                    await updateWindow.ShowReadyAsync(update);
                    updater.ApplyAndRestart(update);
                    Shutdown();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (updateWindow?.ClosingToken.IsCancellationRequested == true)
        {
            Shutdown();
            return;
        }
        catch (Exception)
        {
            suppressAutoLaunch = true;
            MessageBox.Show("런처 업데이트를 확인하거나 적용하지 못했습니다. 현재 버전으로 시작하며 이번 게임 자동 실행은 건너뜁니다. 다음 실행 때 다시 확인합니다.",
                "FFXIVLauncherKR", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (updateWindow?.IsVisible == true) updateWindow.Close();
            updateWindow?.Dispose();
        }
        MainWindow = new MainWindow(suppressAutoLaunch);
        MainWindow.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
    }
}
