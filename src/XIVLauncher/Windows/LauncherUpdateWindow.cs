using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Velopack;

namespace XIVLauncher.Windows;

internal sealed class LauncherUpdateWindow : Window, IDisposable
{
    private readonly CancellationTokenSource closing = new();
    private readonly TaskCompletionSource<bool> restartRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBlock message = new() { Text = "런처의 새 버전을 확인하는 중입니다…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) };
    private readonly ProgressBar progress = new() { Height = 6, Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly TextBox notes = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 260, Margin = new Thickness(0, 16, 0, 14), Visibility = Visibility.Collapsed };
    private readonly Button restart = new() { Content = "업데이트 후 재시작", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private bool ready;

    public LauncherUpdateWindow()
    {
        Title = "FFXIVLauncherKR 업데이트";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Malgun Gothic");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowSurface");
        SetResourceReference(ForegroundProperty, "PrimaryText");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = "FFXIVLauncherKR", FontSize = 23, FontWeight = FontWeights.SemiBold });
        content.Children.Add(message);
        content.Children.Add(progress);
        content.Children.Add(notes);
        content.Children.Add(restart);
        Content = content;
        restart.Click += (_, _) => restartRequested.TrySetResult(true);
    }

    public CancellationToken ClosingToken => closing.Token;

    public void Report(LauncherUpdateProgress status)
    {
        if (ready) return;
        message.Text = status.Message;
        progress.IsIndeterminate = !status.Percentage.HasValue;
        if (status.Percentage is { } value) progress.Value = Math.Clamp(value, 0, 100);
    }

    public Task ShowReadyAsync(UpdateInfo update)
    {
        ready = true;
        message.Text = $"새 버전 {update.TargetFullRelease.Version}의 준비가 끝났습니다. 창을 닫거나 재시작 버튼을 누르면 업데이트를 적용합니다.";
        progress.IsIndeterminate = false;
        progress.Value = 100;
        notes.Text = string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)
            ? "제공된 변경사항이 없습니다." : update.TargetFullRelease.NotesMarkdown;
        notes.Visibility = Visibility.Visible;
        restart.Visibility = Visibility.Visible;
        restart.Focus();
        return restartRequested.Task;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        if (ready) restartRequested.TrySetResult(true);
        else closing.Cancel();
    }

    public void Dispose() => closing.Dispose();
}
