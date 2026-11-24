using System.Windows;
using System.Windows.Controls;

namespace XIVLauncher.Windows;

internal sealed class AutoLoginWindow : Window
{
    public string EnvironmentName { get; private set; } = "";
    public string Code { get; private set; } = "";

    public AutoLoginWindow()
    {
        Title = "U-OTP 자동 로그인 등록"; Width = 420; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowSurface"); SetResourceReference(ForegroundProperty, "PrimaryText");
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "이 PC를 등록하면 다음부터 비밀번호와 U-OTP 입력 없이 로그인합니다.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "환경 이름", Margin = new Thickness(0, 16, 0, 6) });
        var name = new TextBox { Text = Environment.MachineName, MaxLength = 50 };
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "새 U-OTP 인증번호 7자리", Margin = new Thickness(0, 16, 0, 6) });
        var otp = new TextBox { MaxLength = 7, FontSize = 22, TextAlignment = TextAlignment.Center };
        panel.Children.Add(otp);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var confirm = new Button { Content = "등록", IsDefault = true, IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
        void Validate(object? sender, TextChangedEventArgs args) => confirm.IsEnabled = name.Text.Trim().Length > 0 && otp.Text.Length == 7 && otp.Text.All(char.IsAsciiDigit);
        name.TextChanged += Validate; otp.TextChanged += Validate;
        confirm.Click += (_, _) => { EnvironmentName = name.Text.Trim(); Code = otp.Text; otp.Clear(); DialogResult = true; };
        buttons.Children.Add(confirm); buttons.Children.Add(new Button { Content = "취소", IsCancel = true });
        panel.Children.Add(buttons); Content = panel;
        Loaded += (_, _) => otp.Focus(); Closed += (_, _) => otp.Clear();
    }
}
