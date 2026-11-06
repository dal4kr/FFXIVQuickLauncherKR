using System.Windows;
using System.Windows.Controls;

namespace XIVLauncher.Windows;

internal sealed class OtpWindow : Window
{
    private readonly TextBox input = new() { MaxLength = 8, FontSize = 22, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 14, 0, 18) };
    public string? Code { get; private set; }

    public OtpWindow(string? error)
    {
        Title = "OTP 인증"; Width = 390; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowSurface"); SetResourceReference(ForegroundProperty, "PrimaryText");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "OTP 앱에 표시된 인증번호를 입력해 주세요.", TextWrapping = TextWrapping.Wrap });
        if (error is not null)
        {
            var message = new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
            message.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText"); panel.Children.Add(message);
        }
        panel.Children.Add(input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var confirm = new Button { Content = "인증", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        input.TextChanged += (_, _) => confirm.IsEnabled = input.Text.Length is >= 6 and <= 8 && input.Text.All(c => c is >= '0' and <= '9');
        confirm.Click += (_, _) => { Code = input.Text; input.Clear(); DialogResult = true; };
        buttons.Children.Add(confirm); buttons.Children.Add(new Button { Content = "취소", IsCancel = true });
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => input.Focus();
        Closed += (_, _) => input.Clear();
    }
}
