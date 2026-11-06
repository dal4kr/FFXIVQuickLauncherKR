using System.Text.Json.Serialization;

namespace XIVLauncher.Common.Korea;

public sealed class StoredAccount
{
    public string UserName { get; init; } = "";
    public LoginProvider Provider { get; init; }
    [JsonIgnore] public string DisplayName => $"{UserName} · {(Provider == LoginProvider.Naver ? "네이버" : "액토즈")}";
    public string Token { get; init; } = "";
    public DateTimeOffset IssuedAtUtc { get; init; }

    public static StoredAccount FromSession(LauncherSession session) => new()
    {
        UserName = session.UserName, Provider = session.Provider, Token = session.Token, IssuedAtUtc = session.IssuedAtUtc
    };

    public LauncherSession? RestoreSession() => LauncherSession.HasValidTokenFormat(Token)
        ? new LauncherSession { UserName = UserName, Provider = Provider, Token = Token, IssuedAtUtc = IssuedAtUtc }
        : null;

    public override string ToString() => UserName;
}
