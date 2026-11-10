using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using XIVLauncher.Common.Game;
using XIVLauncher.Common.Korea;
using XIVLauncher.Common.Korea.Dalamud;
using XIVLauncher.Common.Korea.Patching;
using XIVLauncher.Common.Windows;

namespace XIVLauncher.Windows;

public partial class MainWindow : Window
{
    private readonly ActozAuthenticationClient authentication = new();
    private readonly HttpClient dalamudClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly CancellationTokenSource closing = new();
    private readonly AccountStore accounts;
    private StoredAccount? account;
    private readonly HttpClient gameUpdateClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly KrGameUpdater gameUpdater;
    private CancellationTokenSource? gameUpdateCancellation;
    private readonly LauncherSettingsStore settingsStore;
    private readonly DalamudPreparation dalamudPreparation;
    private LauncherSession? session;
    private bool challengeReady;
    private bool busy;
    private bool launching;
    private bool initialized;
    private bool startupHandled;
    private bool backupInProgress;
    private readonly bool suppressAutoLaunch;

    public MainWindow() : this(false) { }

    internal MainWindow(bool suppressAutoLaunch)
    {
        this.suppressAutoLaunch = suppressAutoLaunch;
        InitializeComponent();
        var assembly = typeof(MainWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "?";
        LauncherVersion.Text = $"v{version.Split('+')[0]} · GPL-3.0";
        var data = Program.UserDataDirectory;
        accounts = new AccountStore(Path.Combine(data, "accounts.dat"));
        gameUpdater = new KrGameUpdater(gameUpdateClient);
        settingsStore = new LauncherSettingsStore(Path.Combine(data, "settings.json"));
        dalamudPreparation = new(dalamudClient, data);
        ReloadSettings();
        initialized = true;
        UpdateControls();
    }

    private void ReloadSettings()
    {
        authentication.ClearPendingSession();
        var previous = initialized;
        initialized = false;
        try
        {
            var settings = settingsStore.Load();
            GameDirectory.Text = settings.GameDirectory;
            LoginProviderPicker.SelectedValue = Enum.IsDefined(settings.Provider) ? settings.Provider : LoginProvider.Actoz;
            UserName.Text = settings.UserName;
            SaveSession.IsChecked = settings.SaveSession;
            RunAsAdministrator.IsChecked = settings.RunAsAdministrator;
            AutoLaunch.IsChecked = settings.AutoLaunch;
            var options = settings.Dalamud ?? new();
            UseDalamud.IsChecked = options.Enabled;
            DalamudLoadMethodPicker.SelectedValue = Enum.IsDefined(options.LoadMethod) ? options.LoadMethod : DalamudLoadMethod.DllInject;
            UseLocalDalamud.IsChecked = options.UseLocalBuild;
            LocalDalamudDirectory.Text = options.LocalBuildDirectory;
            LocalDalamudGameVersion.Text = options.LocalBuildGameVersion;
            DalamudNoPlugins.IsChecked = options.NoPlugins;
            DalamudIgnoreGameVersion.IsChecked = options.IgnoreGameVersion;
            DalamudDelay.Text = options.InjectionDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            account = null;
            session = null;
            AccountStorageWarning.Text = "";
            try
            {
                ReloadAccounts(settings.UserName, settings.Provider);
                account = AccountPicker.SelectedItem as StoredAccount;
                if (account is not null) UserName.Text = account.UserName;
            }
            catch (LauncherException error) { AccountStorageWarning.Text = error.Message + " 이번 실행에서는 로그인 정보를 저장하지 않고 사용할 수 있습니다."; }
            session = account?.RestoreSession();
        }
        finally { initialized = previous; }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (startupHandled) return;
        startupHandled = true;
        var skipAutoLaunch = suppressAutoLaunch || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        await RunBusyAsync(async () =>
        {
            _ = GetGameDirectory();
            if (session is null) await PrepareLoginAsync();
            else Status.Text = "저장된 세션을 불러왔습니다. 게임 실행 버튼을 눌러 주세요.";
            if (AutoLaunch.IsChecked != true) return;
            if (skipAutoLaunch)
            {
                Status.Text = "자동 실행을 건너뛰었습니다. 준비되면 로그인 또는 게임 실행 버튼을 눌러 주세요.";
                return;
            }
            if (session is null)
                throw new LauncherException("저장된 로그인 세션이 없어 자동 실행할 수 없습니다. 먼저 로그인한 뒤 게임 실행 버튼을 눌러 주세요.");
            await StartGameAsync();
        });
    }

    private void UpdateControls()
    {
        var patchOnly = session?.IsPatchOnly == true;
        LoginPanel.Visibility = session is not null ? Visibility.Collapsed : Visibility.Visible;
        ActozLoginPanel.Visibility = SelectedProvider == LoginProvider.Actoz ? Visibility.Visible : Visibility.Collapsed;
        NaverLoginHint.Visibility = SelectedProvider == LoginProvider.Naver ? Visibility.Visible : Visibility.Collapsed;
        LoginProviderPicker.IsEnabled = !busy;
        SessionSummary.Text = patchOnly ? $"{session!.UserName} · 사전 패치 로그인" : account is null ? "로그인되어 있지 않습니다." : $"{account.DisplayName} · {(session is null ? "로그인 필요" : "실행 준비됨")}";
        var patchHint = authentication.CanRefreshSession
            ? "현재는 사전 패치만 가능합니다. 점검이 끝나면 접속 상태를 새로고침해 주세요."
            : "현재 로그인 인증 정보가 없습니다. 새로 로그인해 주세요.";
        SessionDate.Text = patchOnly ? patchHint : session is null ? "로그인하면 게임을 실행할 수 있습니다." : $"세션 발급: {session.IssuedAtUtc.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} (KST)";
        LaunchButton.Content = patchOnly ? "게임 설치 / 업데이트" : session is not null ? "게임 실행" : SelectedProvider == LoginProvider.Naver ? "네이버 로그인" : "로그인";
        AccountPicker.IsEnabled = !busy;
        AddAccountButton.IsEnabled = !busy;
        CheckGameUpdateButton.IsEnabled = !busy;
        InstallGameButton.IsEnabled = !busy;
        CancelGameUpdateButton.IsEnabled = gameUpdateCancellation is not null;
        LaunchButton.IsEnabled = !busy && (session is not null || SelectedProvider == LoginProvider.Naver || challengeReady);
        BrowseButton.IsEnabled = !busy;
        NewLoginButton.IsEnabled = !busy;
        RefreshSessionButton.Visibility = patchOnly ? Visibility.Visible : Visibility.Collapsed;
        RefreshSessionButton.IsEnabled = !busy && patchOnly && authentication.CanRefreshSession;
        DeleteSessionButton.IsEnabled = !busy && account is not null;
        RefreshCaptchaButton.IsEnabled = !busy;
        GameDirectory.IsEnabled = !busy;
        LoginPanel.IsEnabled = !busy;
        SaveSession.IsEnabled = !busy && !patchOnly;
        AutoLaunch.IsEnabled = !busy;
        CreateBackupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
        BackupIncludeGameSettings.IsEnabled = !busy;
        RunAsAdministrator.IsEnabled = !busy;
        InjectDalamudButton.IsEnabled = !busy;
        UseDalamud.IsEnabled = !busy;
        DalamudSettingsPanel.IsEnabled = !busy && UseDalamud.IsChecked == true;
        LocalDalamudPanel.Visibility = UseLocalDalamud.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CheckDalamudButton.IsEnabled = UseLocalDalamud.IsChecked != true;
        ReinstallDalamudButton.IsEnabled = UseLocalDalamud.IsChecked != true;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorMessage.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        Status.Text = "";
    }

    private void ClearError()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorMessage.Text = "";
    }

    private void DismissError_Click(object sender, RoutedEventArgs e) => ClearError();

    private async Task RunBusyAsync(Func<Task> operation)
    {
        if (busy) return;
        ClearError();
        busy = true;
        UpdateControls();
        try { await operation(); }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (OperationCanceledException) { ShowError("서버 응답 시간이 초과되었습니다. 다시 시도해 주세요."); }
        catch (LauncherException error) { ShowError(error.Message); }
        catch (BackupFileException error) { ShowError("백업 파일을 처리하지 못했습니다. 파일 형식과 폴더 권한을 확인해 주세요.\n대상: " + error.FilePath); }
        catch (HttpRequestException) { ShowError("서버에 연결하지 못했습니다. 인터넷 연결과 서버 점검 상태를 확인해 주세요."); }
        catch (InvalidDataException error) { ShowError("업데이트 파일을 확인하지 못했습니다. " + error.Message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            ShowError("설정 또는 파일을 읽거나 저장하지 못했습니다. 해당 폴더의 권한을 확인해 주세요.");
        }
        catch (Exception) { ShowError("작업을 완료하지 못했습니다. 설정과 게임 설치 경로를 확인해 주세요."); }
        finally { busy = false; UpdateControls(); }
    }

    private LoginProvider SelectedProvider => LoginProviderPicker.SelectedValue is LoginProvider provider ? provider : LoginProvider.Actoz;

    private Task PrepareLoginAsync()
    {
        authentication.ClearPendingSession();
        if (SelectedProvider == LoginProvider.Actoz) return LoadCaptchaAsync();
        challengeReady = false;
        CaptchaImage.Source = null;
        CaptchaCode.Clear();
        Password.Clear();
        Status.Text = "네이버 로그인 버튼을 눌러 계정을 인증해 주세요.";
        return Task.CompletedTask;
    }

    private async void LoginProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || busy) return;
        account = null;
        session = null;
        initialized = false;
        AccountPicker.SelectedItem = null;
        initialized = true;
        UserName.Clear();
        Password.Clear();
        await RunBusyAsync(async () => { await PrepareLoginAsync(); SaveSettings(); });
    }

    private Task<string?> RequestOtpAsync(string? error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prompt = new OtpWindow(error) { Owner = this };
        using var canceled = cancellationToken.Register(() => Dispatcher.BeginInvoke(() => prompt.Close()));
        return Task.FromResult(prompt.ShowDialog() == true ? prompt.Code : null);
    }

    private async Task LoadCaptchaAsync()
    {
        challengeReady = false;
        CaptchaImage.Source = null;
        CaptchaCode.Clear();
        Status.Text = "로그인 화면을 불러오는 중입니다…";
        var challenge = await authentication.PrepareAsync(closing.Token);
        using var stream = new MemoryStream(challenge.CaptchaImage);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        CaptchaImage.Source = image;
        challengeReady = true;
        Status.Text = "액토즈 계정 정보와 자동 입력 방지 문자를 입력해 주세요.";
    }

    private async void RefreshCaptcha_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(PrepareLoginAsync);

    private void ReloadAccounts(string? selectedUser = null, LoginProvider? provider = null)
    {
        var previous = initialized;
        initialized = false;
        try
        {
            var saved = accounts.LoadAll().OrderBy(x => x.UserName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Provider).ToList();
            AccountPicker.ItemsSource = saved;
            AccountPicker.SelectedItem = saved.FirstOrDefault(x => x.Provider == (provider ?? SelectedProvider) && string.Equals(x.UserName, selectedUser, StringComparison.OrdinalIgnoreCase));
        }
        finally { initialized = previous; }
    }

    private async void AccountSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || busy || AccountPicker.SelectedItem is not StoredAccount selected) return;
        authentication.ClearPendingSession();
        account = selected;
        session = account.RestoreSession();
        UserName.Text = account.UserName;
        Password.Clear();
        initialized = false;
        LoginProviderPicker.SelectedValue = selected.Provider;
        SaveSession.IsChecked = true;
        initialized = true;
        await RunBusyAsync(async () =>
        {
            if (session is null) await PrepareLoginAsync();
            else Status.Text = "저장된 세션을 불러왔습니다. 게임 실행 버튼을 눌러 주세요.";
            SaveSettings();
        });
    }

    private async void AddAccount_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        account = null;
        session = null;
        initialized = false;
        AccountPicker.SelectedItem = null;
        initialized = true;
        UserName.Clear();
        Password.Clear();
        await RunBusyAsync(PrepareLoginAsync);
        UserName.Focus();
    }

    private async void NewLogin_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (account is not null)
        {
            account = new StoredAccount { UserName = account.UserName, Provider = account.Provider };
            PersistAccount();
        }
        session = null;
        Password.Clear();
        await RunBusyAsync(PrepareLoginAsync);
        UserName.Focus();
    }

    private async void RefreshSession_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            if (session?.IsPatchOnly != true) return;
            Status.Text = "게임 접속 가능 여부를 확인하는 중입니다…";
            session = await authentication.RefreshSessionAsync(closing.Token);
            if (session.IsPatchOnly)
            {
                Status.Text = "아직 서버 점검 중입니다. 잠시 후 접속 상태를 다시 새로고침해 주세요.";
                return;
            }
            RememberAccount();
            SaveSettings();
            Status.Text = "게임에 접속할 준비가 되었습니다. 게임 실행 버튼을 눌러 주세요.";
        });
    }

    private async void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            if (account is not null) accounts.Delete(account.UserName, account.Provider);
            account = null;
            session = null;
            ReloadAccounts();
            Password.Clear();
            await PrepareLoginAsync();
            SaveSettings();
            Status.Text = "선택한 계정의 저장된 로그인 정보를 삭제했습니다.";
        });
    }

    private void PersistAccount()
    {
        if (account is null) return;
        try
        {
            if (SaveSession.IsChecked == true) accounts.Save(account);
            else accounts.Delete(account.UserName, account.Provider);
            ReloadAccounts(account.UserName, account.Provider);
            AccountStorageWarning.Text = "";
        }
        catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            AccountStorageWarning.Text = "로그인 정보를 저장하지 못했습니다. 이번 실행에서는 저장 없이 사용합니다. 기존 계정 파일은 유지합니다.";
        }
    }

    private void RememberAccount()
    {
        if (session is null || session.IsPatchOnly) return;
        account = StoredAccount.FromSession(session);
        PersistAccount();
    }

    private string GetGameDirectory()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory.Text))
        {
            MainTabs.SelectedItem = GameUpdateTab;
            throw new LauncherException("게임 설치 경로가 설정되지 않았습니다. 게임 설치 / 업데이트 탭에서 경로를 지정해 주세요.");
        }
        return GameLaunchPlan.GetInstallationDirectory(GameDirectory.Text);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var picker = new OpenFolderDialog { Title = "파이널판타지 XIV 한국 서버 설치 폴더 선택" };
        if (picker.ShowDialog(this) != true) return;
        GameDirectory.Text = picker.FolderName;
        await SaveGameDirectoryAsync();
    }

    private async void GameDirectory_LostFocus(object sender, RoutedEventArgs e)
        => await SaveGameDirectoryAsync();

    private async Task SaveGameDirectoryAsync()
    {
        if (!initialized || busy || closing.IsCancellationRequested) return;
        await RunBusyAsync(async () =>
        {
            SaveSettings();
            _ = GetGameDirectory();
            if (session is null && (SelectedProvider == LoginProvider.Naver || !challengeReady)) await PrepareLoginAsync();
            else Status.Text = "게임 설치 경로를 저장했습니다.";
        });
    }

    private void BrowseDalamud_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "KR Dalamud 빌드 출력 폴더 선택" };
        if (picker.ShowDialog(this) != true) return;
        LocalDalamudDirectory.Text = picker.FolderName;
        LocalDalamudGameVersion.Clear();
        try
        {
            var metadata = Path.Combine(picker.FolderName, "version.json");
            if (File.Exists(metadata))
                LocalDalamudGameVersion.Text = JsonSerializer.Deserialize<DalamudVersionInfo>(File.ReadAllBytes(metadata),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.SupportedGameVer ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            ShowError("빌드의 version.json을 읽지 못했습니다. 대상 게임 버전을 직접 입력해 주세요.");
        }
    }

    private void UseCurrentGameVersion_Click(object sender, RoutedEventArgs e)
    {
        try { LocalDalamudGameVersion.Text = ReadGameVersion(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or LauncherException or ArgumentException)
        { ShowError("설치된 게임 버전을 읽지 못했습니다. 게임 경로를 확인해 주세요."); }
    }

    private string ReadGameVersion()
    {
        return File.ReadAllText(Path.Combine(GetGameDirectory(), "game", "ffxivgame.ver")).Trim();
    }

    private DalamudOptions GetDalamudOptions(bool validate = false, bool manualInjection = false)
    {
        var validDelay = int.TryParse(DalamudDelay.Text, out var delay) && delay is >= 0 and <= 60000;
        if (validate && (UseDalamud.IsChecked == true || manualInjection) && !validDelay)
            throw new LauncherException("Dalamud 초기화 지연은 0~60000ms로 입력해 주세요.");
        var options = new DalamudOptions
        {
            Enabled = UseDalamud.IsChecked == true,
            LoadMethod = DalamudLoadMethodPicker.SelectedValue is DalamudLoadMethod method ? method : DalamudLoadMethod.DllInject,
            UseLocalBuild = UseLocalDalamud.IsChecked == true,
            LocalBuildDirectory = LocalDalamudDirectory.Text.Trim(),
            LocalBuildGameVersion = LocalDalamudGameVersion.Text.Trim(),
            NoPlugins = DalamudNoPlugins.IsChecked == true,
            IgnoreGameVersion = DalamudIgnoreGameVersion.IsChecked == true,
            InjectionDelayMs = validDelay ? delay : 0,
        };
        if (validate && (options.Enabled || manualInjection) && options.UseLocalBuild &&
            (string.IsNullOrWhiteSpace(options.LocalBuildDirectory) ||
             (string.IsNullOrWhiteSpace(options.LocalBuildGameVersion) && !File.Exists(Path.Combine(options.LocalBuildDirectory, "version.json")))))
            throw new LauncherException("Dalamud 로컬 빌드 폴더와 대상 게임 버전을 지정해 주세요.");
        return options;
    }

    private async void CheckDalamud_Click(object sender, RoutedEventArgs e) => await UpdateDalamudAsync(false);

    private async void ReinstallDalamud_Click(object sender, RoutedEventArgs e) => await UpdateDalamudAsync(true);

    private async Task UpdateDalamudAsync(bool forceRedownload)
    {
        await RunBusyAsync(async () =>
        {
            var options = GetDalamudOptions();
            if (options.UseLocalBuild)
                throw new LauncherException("직접 빌드 사용을 끈 뒤 배포 업데이트를 진행해 주세요.");
            var prepared = await dalamudPreparation.PrepareAsync(options, ReadGameVersion(),
                new Progress<string>(message => Status.Text = message), closing.Token, forceRedownload);
            var version = Path.GetFileName(prepared.BinaryDirectory);
            Status.Text = forceRedownload ? $"KR Dalamud {version}을 다시 설치했습니다."
                : $"KR Dalamud {version}: 최신 배포 확인과 파일 준비를 마쳤습니다.";
        });
    }

    private async void InjectDalamud_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var options = GetDalamudOptions(validate: true, manualInjection: true);
            var expectedPath = Path.Combine(GetGameDirectory(), "game", "ffxiv_dx11.exe");
            var running = Process.GetProcessesByName("ffxiv_dx11");
            try
            {
                var candidates = new List<Process>();
                var accessDenied = false;
                foreach (var process in running)
                {
                    try
                    {
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(process);
                    }
                    catch (Win32Exception) { accessDenied = true; }
                    catch (InvalidOperationException) { }
                }
                if (candidates.Count == 0)
                    throw new LauncherException(accessDenied
                        ? "게임 프로세스에 접근하지 못했습니다. 게임이 관리자 권한으로 실행 중이라면 런처도 관리자 권한으로 실행해 주세요."
                        : "지정한 설치 경로에서 실행 중인 게임을 찾지 못했습니다. 게임을 먼저 실행하거나 게임 설치 경로를 확인해 주세요.");
                var target = SelectInjectionTarget(candidates);
                if (target is null) { Status.Text = "Dalamud 수동 연결을 취소했습니다."; return; }
                var gamePath = KrDalamudGameRunner.GetInjectionTargetPath(target);
                var startedAt = target.StartTime;
                var gameVersion = (await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(gamePath)!, "ffxivgame.ver"), closing.Token)).Trim();
                SaveSettings();
                Status.Text = "실행 중인 게임에 연결할 Dalamud를 준비하는 중입니다…";
                var prepared = await dalamudPreparation.PrepareAsync(options, gameVersion,
                    new Progress<string>(message => Status.Text = message), closing.Token);
                target.Refresh();
                if (target.HasExited || target.StartTime != startedAt)
                    throw new LauncherException("선택한 게임이 종료되었거나 다시 실행되었습니다. 수동 연결을 다시 눌러 주세요.");
                Status.Text = "실행 중인 게임에 Dalamud를 연결하는 중입니다…";
                launching = true;
                try { await new KrDalamudGameRunner().InjectAsync(target, prepared, options, closing.Token); }
                finally { launching = false; }
                Status.Text = "선택한 게임에 KR Dalamud를 연결했습니다.";
            }
            finally { foreach (var process in running) process.Dispose(); }
        });
    }

    private Process? SelectInjectionTarget(IReadOnlyList<Process> games)
    {
        if (games.Count == 1) return games[0];
        var dialog = new Window
        {
            Owner = this, Title = "Dalamud를 연결할 게임 선택", Width = 440,
            SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontFamily = FontFamily, FontSize = FontSize
        };
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "연결할 게임을 선택해 주세요.", Margin = new Thickness(0, 0, 0, 12) });
        var picker = new ComboBox
        {
            ItemsSource = games.Select(game => $"PID {game.Id} · {game.MainWindowTitle}").ToArray(),
            SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 16)
        };
        content.Children.Add(picker);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var connect = new Button { Content = "연결", IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        connect.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(connect);
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true });
        content.Children.Add(buttons);
        dialog.Content = content;
        return dialog.ShowDialog() == true && picker.SelectedIndex >= 0 ? games[picker.SelectedIndex] : null;
    }
    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            if (session is null)
            {
                _ = GetGameDirectory();
                try
                {
                    if (SelectedProvider == LoginProvider.Naver)
                    {
                        var login = new NaverLoginWindow { Owner = this };
                        if (login.ShowDialog() != true)
                        {
                            if (login.ErrorMessage is not null) throw new LauncherException(login.ErrorMessage);
                            Status.Text = "네이버 로그인을 취소했습니다.";
                            return;
                        }
                        Status.Text = "네이버 계정의 게임 세션을 발급받는 중입니다…";
                        session = await authentication.LoginNaverAsync(login.Identity!, closing.Token, RequestOtpAsync, login.Cookies);
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(UserName.Text) || Password.Password.Length == 0 || CaptchaCode.Text.Trim().Length != 5)
                            throw new LauncherException("아이디, 비밀번호와 5자리 자동 입력 방지 문자를 입력해 주세요.");
                        Status.Text = "로그인하고 게임 세션을 발급받는 중입니다…";
                        challengeReady = false;
                        var login = authentication.LoginAsync(UserName.Text, Password.Password, CaptchaCode.Text, closing.Token, RequestOtpAsync);
                        Password.Clear();
                        session = await login;
                    }
                }
                catch (LauncherException error) when (error.Code == "CANCELED")
                {
                    Status.Text = SelectedProvider == LoginProvider.Actoz ? "OTP 인증을 취소했습니다. 다시 로그인하려면 자동 입력 방지 이미지를 새로고침해 주세요." : "OTP 인증을 취소했습니다.";
                    return;
                }
                finally { Password.Clear(); }
                RememberAccount();
                SaveSettings();
                if (session.IsPatchOnly)
                {
                    var check = await gameUpdater.CheckAsync(GetGameDirectory(), cancellationToken: closing.Token);
                    ShowGameUpdate(check);
                    if (check.NeedsUpdate) MainTabs.SelectedItem = GameUpdateTab;
                    Status.Text = check.NeedsUpdate
                        ? "로그인했습니다. 사전 패치를 진행할 수 있습니다. 점검 종료 후 접속 상태를 새로고침해 주세요."
                        : "사전 패치 로그인 상태이며 게임 파일이 최신 버전입니다. 점검 종료 후 접속 상태를 새로고침해 주세요.";
                }
                else Status.Text = "로그인했습니다. 게임 실행 버튼을 눌러 주세요.";
                return;
            }
            await StartGameAsync();
        });
    }

    private async Task StartGameAsync()
    {
        if (session is null) throw new LauncherException("먼저 로그인해 주세요.");
        var gameDirectory = GetGameDirectory();
        SaveSettings();
        if (!await EnsureGameReadyAsync(session)) return;
        if (session.IsPatchOnly)
        {
            Status.Text = "게임 파일이 최신 버전입니다. 점검 종료 후 접속 상태를 새로고침해 주세요.";
            return;
        }
        var options = GetDalamudOptions(validate: true);
        var plan = GameLaunchPlan.Create(gameDirectory, session);
        PreparedDalamud? prepared = null;
        string? withoutDalamud = null;
        if (options.Enabled)
        {
            try
            {
                prepared = await dalamudPreparation.PrepareAsync(options, ReadGameVersion(),
                    new Progress<string>(message => Status.Text = message), closing.Token);
            }
            catch (OperationCanceledException) when (!closing.IsCancellationRequested)
            { withoutDalamud = "서버 응답 시간이 초과되었습니다."; }
            catch (Exception error) when (error is LauncherException or IOException or UnauthorizedAccessException)
            { withoutDalamud = error.Message; }
        }
        closing.Token.ThrowIfCancellationRequested();
        Status.Text = prepared is null ? "게임을 실행하는 중입니다…" : "KR Dalamud와 게임을 실행하는 중입니다…";
        launching = true;
        try
        {
            using var process = prepared is null
                ? await Task.Run(() => new WindowsGameRunner().Start(plan))
                : await new KrDalamudGameRunner().StartAsync(plan, prepared, options, closing.Token);
            if (withoutDalamud is not null) ShowError("게임은 실행했지만 KR Dalamud를 연결하지 못했습니다. " + withoutDalamud);
            Status.Text = prepared is not null ? "게임을 실행하고 KR Dalamud를 연결했습니다." : "게임을 실행했습니다.";
        }
        finally { launching = false; }
    }

    private void ShowGameUpdate(GameUpdateCheck check)
    {
        GameUpdateStatus.Text = check.Message;
        GameVersionSummary.Text = $"설치 버전: {check.CurrentVersion ?? "미설치"}  /  최신 버전: {check.LatestVersion ?? "확인 필요"}";
        InstallGameButton.Content = check.IsMaintenance ? "점검 중" : check.RequiresAuthentication ? "로그인 필요" : check.NeedsUpdate ? "게임 설치 / 업데이트" : "최신 버전 확인됨";
    }

    private async Task<bool> EnsureGameReadyAsync(LauncherSession authenticated)
    {
        Status.Text = "게임 버전과 패치 상태를 확인하는 중입니다…";
        var check = await gameUpdater.CheckAsync(GetGameDirectory(), authenticated.Token, closing.Token);
        ShowGameUpdate(check);
        if (check.IsMaintenance || check.RequiresAuthentication) { ShowError(check.Message); return false; }
        if (!check.NeedsUpdate) return true;
        MainTabs.SelectedItem = GameUpdateTab;
        return await ApplyGameUpdateAsync(check);
    }

    private async void CheckGameUpdate_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(async () =>
        {
            var check = await gameUpdater.CheckAsync(GetGameDirectory(), session?.Token, closing.Token);
            ShowGameUpdate(check);
            if (check.IsMaintenance || check.RequiresAuthentication) ShowError(check.Message);
            else Status.Text = check.Message;
        });

    private async void InstallGame_Click(object sender, RoutedEventArgs e)
        => await RunBusyAsync(async () =>
        {
            var check = await gameUpdater.CheckAsync(GetGameDirectory(), session?.Token, closing.Token);
            ShowGameUpdate(check);
            if (check.IsMaintenance) { ShowError(check.Message); return; }
            if (!check.NeedsUpdate) { Status.Text = check.Message; return; }
            if (check.RequiresAuthentication && session is null)
            { MainTabs.SelectedItem = GameTab; ShowError("게임 설치에 로그인 인증이 필요합니다. 먼저 로그인해 주세요."); return; }
            await ApplyGameUpdateAsync(check);
        });

    private async Task<bool> ApplyGameUpdateAsync(GameUpdateCheck check)
    {
        var gameRoot = GetGameDirectory();
        foreach (var name in new[] { "ffxiv_dx11", "FFXIV_Boot", "FFXIV_Launcher" })
        {
            var running = Process.GetProcessesByName(name);
            try { if (running.Length != 0) throw new LauncherException("게임과 공식 런처를 종료한 뒤 설치·업데이트해 주세요."); }
            finally { foreach (var process in running) process.Dispose(); }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        gameUpdateCancellation = cancellation;
        UpdateControls();
        try
        {
            await gameUpdater.UpdateAsync(gameRoot, check, new Progress<GameUpdateProgress>(value =>
            {
                GameUpdateProgressBar.Value = value.Percentage;
                GameUpdateStatus.Text = value.Message;
                Status.Text = value.Message;
            }), cancellation.Token);
            var verified = await gameUpdater.CheckAsync(gameRoot, session?.Token, closing.Token);
            ShowGameUpdate(verified);
            if (verified.NeedsUpdate || verified.IsMaintenance || verified.RequiresAuthentication)
            {
                ShowError("게임 실행을 보류했습니다. " + verified.Message);
                return false;
            }
            Status.Text = "게임 설치·업데이트를 완료했습니다.";
            return true;
        }
        catch (OperationCanceledException) when (!closing.IsCancellationRequested)
        {
            Status.Text = "게임 업데이트를 취소했습니다. 다시 실행하면 내려받기를 이어갑니다.";
            throw new LauncherException("게임 업데이트를 취소했습니다. 다시 실행하면 내려받기를 이어갑니다.");
        }
        finally { gameUpdateCancellation = null; UpdateControls(); }
    }

    private void CancelGameUpdate_Click(object sender, RoutedEventArgs e) => gameUpdateCancellation?.Cancel();

    private static DirectoryInfo GetGameUserDirectory() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "FINAL FANTASY XIV - KOREA"));

    private static void EnsureGameClosedForBackup()
    {
        var games = Process.GetProcessesByName("ffxiv_dx11");
        try
        {
            if (games.Length != 0) throw new LauncherException("게임을 종료한 뒤 백업·복원을 진행해 주세요.");
        }
        finally { foreach (var game in games) game.Dispose(); }
    }

    private async Task RunBackupAsync(Func<Task> operation)
    {
        await RunBusyAsync(async () =>
        {
            EnsureGameClosedForBackup();
            backupInProgress = true;
            try { await operation(); }
            finally { backupInProgress = false; }
        });
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        await RunBackupAsync(async () =>
        {
            var dialog = new SaveFileDialog
            {
                Title = "백업 파일 저장", Filter = $"XIVLauncher 백업 (*{Backup.BACKUP_EXTENSION})|*{Backup.BACKUP_EXTENSION}",
                DefaultExt = Backup.BACKUP_EXTENSION, AddExtension = true,
                FileName = $"xiv_kr_{DateTime.Now:yyyy-MM-dd_HH-mm}{Backup.BACKUP_EXTENSION}",
            };
            if (dialog.ShowDialog(this) != true) return;
            SaveSettings();
            var userPath = BackupIncludeGameSettings.IsChecked == true ? GetGameUserDirectory() : null;
            Status.Text = "백업 파일을 만드는 중입니다…";
            await Task.Run(() =>
            {
                EnsureGameClosedForBackup();
                Backup.CreateBackup(new DirectoryInfo(Program.UserDataDirectory), userPath, new FileInfo(dialog.FileName));
            });
            Status.Text = "백업 파일을 저장했습니다.\n" + dialog.FileName;
        });
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        await RunBackupAsync(async () =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "복원할 백업 선택", Filter = $"XIVLauncher 백업 (*{Backup.BACKUP_EXTENSION})|*{Backup.BACKUP_EXTENSION}",
                CheckFileExists = true, Multiselect = false,
            };
            if (dialog.ShowDialog(this) != true) return;
            var file = new FileInfo(dialog.FileName);
            DirectoryInfo? userPath = null;
            if (await Task.Run(() => Backup.BackupHasUserFiles(file)))
            {
                var choice = MessageBox.Show(this, "이 백업에는 게임·캐릭터 설정이 들어 있습니다. 함께 복원할까요?",
                    "백업 복원", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.Yes) userPath = GetGameUserDirectory();
            }
            Status.Text = "백업을 복원하는 중입니다…";
            await Task.Run(() =>
            {
                EnsureGameClosedForBackup();
                Backup.RestoreBackup(new DirectoryInfo(Program.UserDataDirectory), userPath, file);
            });
            ReloadSettings();
            Password.Clear();
            CaptchaCode.Clear();
            if (session is null && (SelectedProvider == LoginProvider.Naver || !challengeReady))
            {
                try { await PrepareLoginAsync(); }
                catch (Exception error) when (error is LauncherException or HttpRequestException or OperationCanceledException or IOException or NotSupportedException)
                {
                    ShowError("백업 복원은 완료했지만 로그인 화면을 불러오지 못했습니다. 게임 실행 탭에서 자동 입력 방지 이미지를 새로고침해 주세요.");
                    return;
                }
            }
            Status.Text = "백업을 복원하고 설정을 적용했습니다.";
        });
    }

    private void SaveSettings() => settingsStore.Save(new LauncherSettings
    {
        UserName = session?.UserName ?? UserName.Text.Trim(),
        Provider = session?.Provider ?? SelectedProvider,
        GameDirectory = GameDirectory.Text.Trim(),
        SaveSession = SaveSession.IsChecked == true,
        RunAsAdministrator = RunAsAdministrator.IsChecked == true,
        AutoLaunch = AutoLaunch.IsChecked == true,
        Dalamud = GetDalamudOptions(),
    });

    private void AutoLaunch_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy) return;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowError("자동 실행 설정을 저장하지 못했습니다. 저장 폴더의 권한을 확인해 주세요."); }
    }

    private async void Administrator_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy) return;
        var enabled = RunAsAdministrator.IsChecked == true;
        await RunBusyAsync(async () =>
        {
            try { SaveSettings(); }
            catch { RunAsAdministrator.IsChecked = !enabled; throw; }
            if (!enabled)
            {
                Status.Text = "다음 실행부터 관리자 권한을 요청하지 않습니다.";
                return;
            }
            if (Program.IsElevated())
            {
                Status.Text = "관리자 권한으로 실행 중입니다. 설정을 저장했습니다.";
                return;
            }
            try
            {
                await Task.Run(Program.RestartAsAdministrator);
                Close();
            }
            catch (Exception error) when (error is Win32Exception or LauncherException or InvalidOperationException)
            {
                RunAsAdministrator.IsChecked = false;
                SaveSettings();
                if (error is Win32Exception { NativeErrorCode: 1223 })
                    Status.Text = "관리자 권한 요청을 취소했습니다. 설정은 해제 상태로 유지합니다.";
                else ShowError("관리자 권한으로 재시작하지 못했습니다. 설정은 해제 상태로 유지합니다.");
            }
        });
    }

    private void DalamudSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        UpdateControls();
    }

    private void DalamudVersionCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || busy) return;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowError("Dalamud 버전 검사 설정을 저장하지 못했습니다. 저장 폴더의 권한을 확인해 주세요."); }
    }

    private void DalamudLoadMethod_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!initialized || busy) return;
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { ShowError("Dalamud 연결 방식을 저장하지 못했습니다. 저장 폴더의 권한을 확인해 주세요."); }
    }

    private void SaveSession_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        try
        {
            PersistAccount();
            SaveSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        { ShowError("세션 저장 설정을 변경하지 못했습니다. 저장 폴더의 권한을 확인해 주세요."); }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (backupInProgress) { e.Cancel = true; Status.Text = "백업·복원이 끝난 뒤 런처를 닫아 주세요."; return; }
        if (launching) { e.Cancel = true; Status.Text = "게임 실행·연결 작업이 끝난 뒤 런처를 닫아 주세요."; return; }
        closing.Cancel();
        Password.Clear();
        try { SaveSettings(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        authentication.Dispose();
        gameUpdateCancellation?.Cancel();
        gameUpdateClient.Dispose();
        dalamudClient.Dispose();
    }
}
