using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.InteropServices;
using AngleSharp.Html.Parser;

namespace XIVLauncher.Common.Korea;

public sealed class ActozAuthenticationClient : IDisposable
{
    private static readonly Uri LauncherUri = new("https://newlauncher.ff14.co.kr/");
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly CookieContainer? cookieJar;
    private Dictionary<string, string>? form;
    private string? authenticatedUserName;
    private LoginProvider authenticatedProvider;
    private string? registrationMemberKey;
    private string accessToken = "";
    private LauncherSession? issuedSession;
    public bool CanRefreshSession => authenticatedUserName is not null && issuedSession is null;
    public bool CanRegisterAutomaticLogin => authenticatedProvider == LoginProvider.Actoz && registrationMemberKey is not null;

    public ActozAuthenticationClient() : this(new CookieContainer()) { }

    private ActozAuthenticationClient(CookieContainer cookies) : this(new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All, CookieContainer = cookies,
    }) { Timeout = TimeSpan.FromSeconds(30) })
    {
        cookieJar = cookies;
        ownsClient = true;
    }

    public ActozAuthenticationClient(HttpClient client)
    {
        this.client = client;
        this.client.BaseAddress ??= LauncherUri;
        this.client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ko-KR,ko;q=0.9");
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("FFXIVLauncherKR/0.2");
    }

    public async Task<LoginChallenge> PrepareAsync(CancellationToken cancellationToken = default, bool requireCaptcha = false)
    {
        ResetAuthentication();
        var page = await ReadLoginPageAsync(cancellationToken);
        form = page.Fields;
        if (!requireCaptcha) return new LoginChallenge(null);
        if (!page.Fields.ContainsKey("BDC_VCID_LauncherLoginCaptcha") || string.IsNullOrWhiteSpace(page.Image))
            throw new LauncherException("자동 입력 방지 화면을 읽지 못했습니다. 다시 불러와 주세요.");
        var imageUri = new Uri(client.BaseAddress!, page.Image);
        if (imageUri.Scheme != client.BaseAddress!.Scheme || imageUri.Authority != client.BaseAddress.Authority)
            throw new LauncherException("자동 입력 방지 이미지 주소를 확인하지 못했습니다.");
        var image = await client.GetByteArrayAsync(imageUri, cancellationToken);
        form = page.Fields;
        return new LoginChallenge(image);
    }

    private async Task<(Dictionary<string, string> Fields, string? Image)> ReadLoginPageAsync(CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(cancellationToken), cancellationToken);
        var loginForm = document.QuerySelector("form#loginForm")
            ?? throw new LauncherException("로그인 화면을 읽지 못했습니다. 잠시 후 다시 시도해 주세요.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in loginForm.QuerySelectorAll("input[name]"))
        {
            var type = input.GetAttribute("type")?.ToLowerInvariant() ?? "text";
            if (input.HasAttribute("disabled") || type is "submit" or "button" or "reset" or "file" or "image") continue;
            if (type is "checkbox" or "radio" && !input.HasAttribute("checked")) continue;
            fields[input.GetAttribute("name")!] = input.GetAttribute("value") ?? "";
        }
        return (fields, loginForm.QuerySelector("#LauncherLoginCaptcha_CaptchaImage")?.GetAttribute("src"));
    }

    public async Task<LauncherSession> LoginNaverAsync(NaverLoginIdentity identity, CancellationToken cancellationToken = default,
        Func<string?, CancellationToken, Task<string?>>? requestOtp = null, IEnumerable<Cookie>? browserCookies = null)
    {
        ResetAuthentication();
        if (string.IsNullOrWhiteSpace(identity.MemberId) || identity.MemberId.Length > 1024 || identity.MemberId.Any(char.IsControl))
            throw new LauncherException("네이버 로그인 정보를 확인하지 못했습니다. 다시 로그인해 주세요.");
        if (browserCookies is not null && cookieJar is not null)
            foreach (var cookie in browserCookies)
                if (cookie.Domain.TrimStart('.').Equals("ff14.co.kr", StringComparison.OrdinalIgnoreCase) ||
                    cookie.Domain.TrimStart('.').Equals("newlauncher.ff14.co.kr", StringComparison.OrdinalIgnoreCase))
                    cookieJar.Add(cookie);
        var page = await ReadLoginPageAsync(cancellationToken);
        var fields = page.Fields;
        fields["gameServiceID"] = "34";
        fields["csiteNo"] = "5";
        fields["memberID"] = identity.MemberId;
        fields["chMemberID"] = identity.ChannelMemberId;
        fields["passWord"] = "";
        fields["CaptchaCode"] = "";
        fields["BDC_VCID_LauncherLoginCaptcha"] = "";
        fields["checkMemberID"] = "true";
        fields["decideDX"] = "1";
        return await AuthenticateAsync(identity.MemberId, LoginProvider.Naver, fields, requestOtp, cancellationToken);
    }

    public async Task<LauncherSession> LoginAsync(string userName, string password, string captcha, CancellationToken cancellationToken = default,
        Func<string?, CancellationToken, Task<string?>>? requestOtp = null)
    {
        ClearPendingSession();
        var fields = form ?? throw new LauncherException("로그인 화면을 먼저 불러와 주세요.");
        form = null;
        userName = userName.Trim();
        if (userName.Length == 0 || password.Length == 0)
            throw new LauncherException("아이디와 비밀번호를 입력해 주세요.");
        if (captcha.Trim().Length is not (0 or 5))
            throw new LauncherException("자동 입력 방지 문자는 표시된 5자리를 입력해 주세요.", "CAPTCHA");
        fields["gameServiceID"] = "34";
        fields["csiteNo"] = "0";
        fields["memberID"] = userName;
        fields["passWord"] = password;
        fields["CaptchaCode"] = captcha.Trim();
        fields["checkMemberID"] = "false";
        fields["decideDX"] = "1";

        return await AuthenticateAsync(userName, LoginProvider.Actoz, fields, requestOtp, cancellationToken);
    }

    private async Task<LauncherSession> AuthenticateAsync(string userName, LoginProvider provider, Dictionary<string, string> fields,
        Func<string?, CancellationToken, Task<string?>>? requestOtp, CancellationToken cancellationToken)
    {
        try
        {
            using var login = await PostAsync("LauncherFF/LauncherProcess", fields, cancellationToken);
            var data = login.RootElement;
            var result = Read(data, "result");
            if (result != "0")
                throw LoginError(result);
            var loginResult = Read(data, "loginResult");
            if (loginResult != "O")
                throw LoginError(loginResult);
            if (Read(data, "ipUseChek") == "X")
                throw new LauncherException("현재 IP에서 로그인할 수 없습니다. 공식 홈페이지의 계정 설정을 확인해 주세요.");
            var memberKey = Read(data, "memberKey");
            var channel = Read(data, "csiteNo");
            if (!string.IsNullOrEmpty(channel) && channel != ((int)provider).ToString())
                throw new LauncherException("선택한 로그인 방식과 서버의 로그인 정보가 다릅니다. 다시 로그인해 주세요.");
            var returnedUser = Read(data, "memberID");
            if (!string.IsNullOrWhiteSpace(returnedUser) && !string.Equals(returnedUser, userName, StringComparison.OrdinalIgnoreCase))
                throw new LauncherException("선택한 계정과 서버의 로그인 계정이 다릅니다. 다시 로그인해 주세요.");
            var patchOnly = Read(data, "pathcheck") == "1";
            if (Read(data, "systemCheck") == "O" && !patchOnly)
                throw new LauncherException("서버 점검 중입니다. 점검 종료 후 다시 시도해 주세요.", "MAINTENANCE");
            fields["memberKey"] = memberKey ?? "";
            fields["passWord"] = "";
            var usesOtp = Read(data, "motpUse") == "O";
            if (usesOtp)
                await CompleteOtpAsync(fields, requestOtp, cancellationToken);
            authenticatedUserName = userName;
            authenticatedProvider = provider;
            registrationMemberKey = provider == LoginProvider.Actoz && usesOtp ? memberKey : null;
            if (patchOnly) return PatchSession();
            return await IssueTokenAsync(userName, provider, cancellationToken);
        }
        finally
        {
            fields.Clear();
        }
    }

    public void ClearPendingSession()
    {
        authenticatedUserName = null;
        issuedSession = null;
        registrationMemberKey = null;
        accessToken = "";
    }

    public void ResetAuthentication()
    {
        ClearPendingSession();
        form?.Clear();
        form = null;
        if (cookieJar is not null)
            foreach (Cookie cookie in cookieJar.GetAllCookies()) cookie.Expired = true;
    }

    private LauncherSession PatchSession() => new()
    {
        UserName = authenticatedUserName!, Provider = authenticatedProvider,
        IsPatchOnly = true, IssuedAtUtc = DateTimeOffset.UtcNow,
    };

    public async Task<LauncherSession> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (issuedSession is not null) return issuedSession;
        if (authenticatedUserName is null)
            throw new LauncherException("현재 로그인 인증 정보가 없습니다. 새로 로그인해 주세요.", "SESSION_EXPIRED");
        try { return await IssueTokenAsync(authenticatedUserName, authenticatedProvider, cancellationToken); }
        catch (LauncherException error) when (error.Code == "-909") { return PatchSession(); }
        catch (LauncherException)
        {
            ClearPendingSession();
            throw;
        }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearPendingSession();
            throw TokenError("-401");
        }
    }

    public async Task<LauncherSession> LoginAutomaticallyAsync(string userName, AutoLoginCredentials credentials,
        CancellationToken cancellationToken = default, bool checkPage = true)
    {
        if (string.IsNullOrWhiteSpace(userName) || !credentials.IsValid)
            throw new LauncherException("저장된 자동 로그인 정보를 확인하지 못했습니다. 다시 로그인해 주세요.", "-99");
        ResetAuthentication();
        _ = await ReadLoginPageAsync(cancellationToken);
        using var reply = await PostAsync("LauncherFF/AutoLoginProcess", new()
        {
            ["accessToken"] = credentials.AccessToken, ["launcherID"] = credentials.LauncherId,
            ["deviceID"] = credentials.DeviceId, ["checkPage"] = checkPage ? "true" : "false",
        }, cancellationToken);
        var code = Read(reply.RootElement, "ResultCode");
        if (code != "0") throw AutomaticLoginError(code);
        var patchOnly = Read(reply.RootElement, "PathCheck") == "1";
        if (Read(reply.RootElement, "SystemCheck") == "O" && !patchOnly)
            throw new LauncherException("서버 점검 중입니다. 점검 종료 후 다시 시도해 주세요.", "MAINTENANCE");
        authenticatedUserName = userName;
        authenticatedProvider = LoginProvider.Actoz;
        accessToken = credentials.AccessToken;
        if (patchOnly) return PatchSession();
        return await IssueTokenAsync(userName, LoginProvider.Actoz, cancellationToken);
    }

    public async Task<AutoLoginCredentials> RegisterAutomaticLoginAsync(string launcherId, string deviceId,
        string environmentName, string otp, CancellationToken cancellationToken = default)
    {
        if (!CanRegisterAutomaticLogin || authenticatedUserName is null)
            throw new LauncherException("U-OTP를 사용하는 액토즈 계정으로 먼저 로그인해 주세요.", "OTP");
        ValidateFreshOtp(otp);
        environmentName = environmentName.Trim();
        if (environmentName.Length is < 1 or > 50 || environmentName.Any(char.IsControl) ||
            !Guid.TryParseExact(launcherId, "D", out _) || deviceId.Length != 32 || !deviceId.All(Uri.IsHexDigit))
            throw new LauncherException("등록할 환경 이름과 기기 정보를 확인해 주세요.");
        using var reply = await PostAsync("LauncherFF/SaveDevice", new()
        {
            ["launcherID"] = launcherId, ["deviceID"] = deviceId, ["deviceDesc"] = environmentName,
            ["otpNum"] = otp, ["memberKey"] = registrationMemberKey!, ["os"] = RuntimeInformation.OSDescription,
        }, cancellationToken);
        var code = Read(reply.RootElement, "ResultCode");
        if (code != "0") throw new LauncherException("자동 로그인 환경을 등록하지 못했습니다. 새 U-OTP 번호와 계정 상태를 확인해 주세요.", code);
        var credentials = new AutoLoginCredentials
        {
            LauncherId = launcherId, DeviceId = deviceId, EnvironmentName = environmentName,
            AccessToken = Read(reply.RootElement, "Result") ?? "",
        };
        if (!credentials.IsValid) throw new LauncherException("자동 로그인 등록 응답을 확인하지 못했습니다.");
        accessToken = credentials.AccessToken;
        return credentials;
    }

    private static void ValidateFreshOtp(string otp)
    {
        if (otp.Length != 7 || !otp.All(char.IsAsciiDigit))
            throw new LauncherException("새 U-OTP 인증번호 7자리를 입력해 주세요.", "OTP");
    }

    private async Task<string> GetCurrentRegistrationIdAsync(AutoLoginCredentials credentials, CancellationToken cancellationToken)
    {
        if (!credentials.IsValid) throw new LauncherException("자동 로그인 정보를 확인하지 못했습니다.");
        using var devices = await PostAsync("LauncherFF/GetDevice", new() { ["accessToken"] = credentials.AccessToken }, cancellationToken);
        var code = Read(devices.RootElement, "ResultCode");
        if (code != "0") throw AutomaticLoginError(code);
        if (!devices.RootElement.TryGetProperty("Result", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new LauncherException("등록된 자동 로그인 환경 목록을 확인하지 못했습니다.");
        var current = items.EnumerateArray().Where(x => string.Equals(Read(x, "IsCurEnv"), "true", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (current.Length != 1 || string.IsNullOrWhiteSpace(Read(current[0], "AutoLoginID")))
            throw new LauncherException("현재 기기의 자동 로그인 등록을 확인하지 못했습니다. 공식 홈페이지에서 환경을 확인해 주세요.");
        return Read(current[0], "AutoLoginID")!;
    }

    public async Task RenewAutomaticLoginAsync(AutoLoginCredentials credentials, string otp, CancellationToken cancellationToken = default)
    {
        ValidateFreshOtp(otp);
        var registrationId = await GetCurrentRegistrationIdAsync(credentials, cancellationToken);
        using var reply = await PostAsync("LauncherFF/UdateDeviceExpireDate", new()
        {
            ["otpNum"] = otp, ["accessToken"] = credentials.AccessToken, ["autoLoginID"] = registrationId,
        }, cancellationToken);
        var code = Read(reply.RootElement, "ResultCode");
        if (code != "0")
            throw new LauncherException("자동 로그인 유효기간을 갱신하지 못했습니다. 새 U-OTP 번호와 등록 상태를 확인해 주세요.", code);
    }

    public async Task UnregisterAutomaticLoginAsync(AutoLoginCredentials credentials, CancellationToken cancellationToken = default)
    {
        var registrationId = await GetCurrentRegistrationIdAsync(credentials, cancellationToken);
        using var reply = await PostAsync("LauncherFF/DelDevice", new()
        {
            ["accessToken"] = credentials.AccessToken, ["autoLoginID"] = registrationId,
        }, cancellationToken);
        var code = Read(reply.RootElement, "ResultCode");
        if (code != "0") throw new LauncherException("자동 로그인 등록을 해제하지 못했습니다. 잠시 후 다시 시도해 주세요.", code);
        ResetAuthentication();
    }

    public static bool IsInvalidAutomaticLogin(string? code) => code is "-97" or "-96" or "-95" or "-94" or "-93" or "-92" or "-91" or "-99";

    private static LauncherException AutomaticLoginError(string? code) => code switch
    {
        "-999" => new("서버 점검 중입니다. 점검 종료 후 다시 시도해 주세요.", "MAINTENANCE"),
        "-98" => new("자동 로그인이 로그아웃 상태입니다. 자동 로그인 버튼을 눌러 다시 접속해 주세요.", code),
        "-90" => new("현재 IP에서는 자동 로그인할 수 없습니다. 공식 홈페이지의 계정 설정을 확인해 주세요.", code),
        _ when IsInvalidAutomaticLogin(code) => new("자동 로그인 등록이 만료되거나 해제되었습니다. 다시 로그인한 뒤 등록해 주세요.", code),
        _ => new("자동 로그인하지 못했습니다. 잠시 후 다시 시도하거나 새로 로그인해 주세요.", code),
    };

    private async Task<LauncherSession> IssueTokenAsync(string userName, LoginProvider provider, CancellationToken cancellationToken)
    {
        using var tokenReply = await PostAsync("LauncherFF/MakeToken", new Dictionary<string, string>
        {
            ["internetCafeType"] = "0", ["freeTrialSelect"] = "0", ["accessToken"] = accessToken,
        }, cancellationToken);
        var tokenResult = Read(tokenReply.RootElement, "tokenResult");
        if (tokenResult != "0")
            throw TokenError(tokenResult);
        var returnedUser = Read(tokenReply.RootElement, "memberID");
        if (!string.IsNullOrWhiteSpace(returnedUser) && !string.Equals(returnedUser, userName, StringComparison.OrdinalIgnoreCase))
            throw new LauncherException("선택한 계정과 서버의 로그인 계정이 다릅니다. 다시 로그인해 주세요.");
        var token = Read(tokenReply.RootElement, "toKen");
        if (!LauncherSession.HasValidTokenFormat(token))
            throw new LauncherException("게임 세션을 확인하지 못했습니다. 다시 로그인해 주세요.");
        return issuedSession = new LauncherSession { UserName = userName, Provider = provider, Token = token, IssuedAtUtc = DateTimeOffset.UtcNow };
    }

    private async Task CompleteOtpAsync(Dictionary<string, string> login, Func<string?, CancellationToken, Task<string?>>? requestOtp,
        CancellationToken cancellationToken)
    {
        if (requestOtp is null) throw new LauncherException("OTP 인증이 필요한 계정입니다.", "OTP");
        if (string.IsNullOrWhiteSpace(login["memberKey"])) throw new LauncherException("OTP 인증 정보를 확인하지 못했습니다. 다시 로그인해 주세요.", "OTP");
        string? message = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = await requestOtp(message, cancellationToken);
            if (code is null) throw new LauncherException("OTP 인증을 취소했습니다.", "CANCELED");
            if (code.Length != 7 || !code.All(char.IsAsciiDigit))
                throw new LauncherException("U-OTP 인증번호 7자리를 입력해 주세요.", "OTP");
            var fields = new Dictionary<string, string>
            {
                ["memberKey"] = login["memberKey"], ["memberID"] = login["memberID"],
                ["csiteNo"] = login["csiteNo"], ["otpNum"] = code,
            };
            try
            {
                using var response = await PostAsync("LauncherFF/OTPCheck", fields, cancellationToken);
                var result = Read(response.RootElement, "Result");
                if (result is null) throw new LauncherException("OTP 서버 응답을 확인하지 못했습니다. 다시 로그인해 주세요.");
                if (result.Length == 0) return;
                message = "OTP 인증번호가 올바르지 않거나 만료되었습니다. 새 번호를 입력해 주세요.";
            }
            finally { fields.Clear(); }
        }
    }

    private async Task<JsonDocument> PostAsync(string path, Dictionary<string,string> fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(fields)
        };
        request.Headers.Referrer = client.BaseAddress;
        request.Headers.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        try
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind == JsonValueKind.Object)
                return json;
            json.Dispose();
        }
        catch (JsonException) { }
        throw new LauncherException("로그인 서버 응답을 읽지 못했습니다. 잠시 후 다시 시도해 주세요.");
    }

    private static string? Read(JsonElement data, string name)
    {
        foreach (var property in data.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return property.Value.ToString();
        return null;
    }

    private static LauncherException LoginError(string? code) => code switch
    {
        "-106" => new("자동 입력 방지 문자가 일치하지 않습니다. 새 이미지의 문자를 입력해 주세요.", code),
        "P" or "T" or "E" or "C" or "-102" or "-107" => new("아이디 또는 비밀번호를 확인해 주세요.", code),
        "L" or "Z" => new("로그인이 제한된 계정입니다. 공식 홈페이지에서 본인 인증을 진행해 주세요.", code),
        "D" => new("중복 접속으로 로그인이 제한되었습니다. 잠시 후 다시 시도해 주세요.", code),
        "N" or "Q" or "V" or "W" => new("이 계정은 현재 로그인할 수 없습니다. 공식 홈페이지에서 계정 상태를 확인해 주세요.", code),
        _ => new("로그인하지 못했습니다. 입력 정보와 공식 서버 상태를 확인해 주세요.")
    };

    private static LauncherException TokenError(string? code) => code switch
    {
        "-909" => new("서버 점검 중입니다. 점검 종료 후 다시 로그인해 주세요.", code),
        "-401" or "-402" or "-403" or "-404" or "-405" => new("로그인 인증이 만료되었습니다. 다시 로그인해 주세요.", code),
        _ when IsInvalidAutomaticLogin(code) => AutomaticLoginError(code),
        "601" => new("게임 인증에 실패했습니다. 다시 로그인해 주세요.", code),
        _ => new("게임 세션을 발급받지 못했습니다. 계정 이용 상태와 서버 상태를 확인해 주세요.")
    };

    public void Dispose()
    {
        ResetAuthentication();
        if (ownsClient) client.Dispose();
    }
}
