using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using XIVLauncher.Common.Korea;

namespace XIVLauncher.Windows;

internal sealed class NaverLoginWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly TextBlock address = new() { Margin = new Thickness(16, 10, 16, 10), Text = "네이버 로그인 화면을 준비하는 중입니다…", TextWrapping = TextWrapping.Wrap };
    private bool closed;
    private bool capturing;
    public NaverLoginIdentity? Identity { get; private set; }
    public IReadOnlyList<Cookie> Cookies { get; private set; } = [];
    public string? ErrorMessage { get; private set; }

    public NaverLoginWindow()
    {
        Title = "네이버 로그인";
        Width = 520; Height = 740; MinWidth = 460; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Malgun Gothic"); FontSize = 13;
        SetResourceReference(BackgroundProperty, "WindowSurface"); SetResourceReference(ForegroundProperty, "PrimaryText");
        var layout = new Grid();
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.Children.Add(address);
        Grid.SetRow(browser, 1); layout.Children.Add(browser);
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(16, 10, 16, 10), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(cancel, 2); layout.Children.Add(cancel);
        Content = layout;
        Loaded += async (_, _) => await InitializeBrowserAsync();
        Closed += (_, _) => { closed = true; browser.Dispose(); };
    }

    private async Task InitializeBrowserAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XIVLauncherKR", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            if (closed) return;
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            options.ProfileName = "Naver" + Guid.NewGuid().ToString("N");
            await browser.EnsureCoreWebView2Async(environment, options);
            if (closed) return;
            var settings = browser.CoreWebView2.Settings;
            settings.AreHostObjectsAllowed = false;
            settings.IsWebMessageEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) || !NaverLoginProtocol.IsAllowedNavigation(uri)) args.Cancel = true;
                else address.Text = uri.GetLeftPart(UriPartial.Authority);
            };
            browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && NaverLoginProtocol.IsAllowedNavigation(uri))
                    browser.CoreWebView2.Navigate(uri.AbsoluteUri);
            };
            browser.CoreWebView2.WebResourceResponseReceived += ReceiveLoginAsync;
            browser.CoreWebView2.Navigate(NaverLoginProtocol.LoginUri.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        { Fail("네이버 로그인에 Microsoft Edge WebView2 Runtime이 필요합니다. WebView2 Runtime을 설치한 뒤 다시 시도해 주세요."); }
        catch (Exception)
        { if (!closed) Fail("네이버 로그인 창을 준비하지 못했습니다. WebView2 Runtime과 인터넷 연결을 확인해 주세요."); }
    }

    private async void ReceiveLoginAsync(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
    {
        if (closed || capturing || !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) || !NaverLoginProtocol.IsGate(uri)) return;
        capturing = true;
        try
        {
            if (args.Response.StatusCode is < 200 or >= 300) throw new LauncherException("네이버 인증 서버가 로그인 요청을 완료하지 못했습니다.");
            using var content = await args.Response.GetContentAsync();
            if (closed) return;
            if (content is null) throw new LauncherException("네이버 로그인 응답을 읽지 못했습니다.");
            using var reader = new StreamReader(content);
            var html = new StringBuilder();
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer)) != 0)
            {
                if (html.Length > NaverLoginProtocol.MaximumGateResponseLength - read)
                    throw new LauncherException("네이버 로그인 응답이 너무 큽니다.");
                html.Append(buffer, 0, read);
            }
            var identity = NaverLoginProtocol.ParseGate(uri, html.ToString());
            if (identity is null) throw new LauncherException("네이버 로그인 정보를 확인하지 못했습니다. 계정 상태와 이용약관 동의를 확인한 뒤 다시 시도해 주세요.");
            if (closed) return;
            var cookies = await browser.CoreWebView2.CookieManager.GetCookiesAsync("https://newlauncher.ff14.co.kr/");
            if (closed) return;
            Cookies = cookies.Select(cookie => new Cookie(cookie.Name, cookie.Value, cookie.Path, cookie.Domain)
            { Secure = cookie.IsSecure, HttpOnly = cookie.IsHttpOnly }).ToArray();
            Identity = identity;
            DialogResult = true;
        }
        catch (LauncherException error) { if (!closed) Fail(error.Message); }
        catch (Exception) { if (!closed) Fail("네이버 로그인 응답을 처리하지 못했습니다. 다시 로그인해 주세요."); }
        finally { capturing = false; }
    }

    private void Fail(string message)
    {
        if (closed) return;
        ErrorMessage = message;
        DialogResult = false;
    }
}
