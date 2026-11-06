using System.Diagnostics.CodeAnalysis;

namespace XIVLauncher.Common.Korea;

public enum LoginProvider { Actoz = 0, Naver = 5 }

public sealed class LauncherSession
{
    public string UserName { get; init; } = "";
    public LoginProvider Provider { get; init; }
    public string Token { get; init; } = "";
    public DateTimeOffset IssuedAtUtc { get; init; }

    internal static bool HasValidTokenFormat([NotNullWhen(true)] string? token)
        => !string.IsNullOrWhiteSpace(token) && !token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '"');

    public override string ToString() => $"{UserName} ({IssuedAtUtc:O})";
}

public sealed record LoginChallenge(byte[] CaptchaImage);

public sealed class LauncherException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}
