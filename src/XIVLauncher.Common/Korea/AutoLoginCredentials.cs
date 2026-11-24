namespace XIVLauncher.Common.Korea;

public sealed class AutoLoginCredentials
{
    public string LauncherId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string AccessToken { get; init; } = "";
    public string EnvironmentName { get; init; } = "";

    public bool IsValid => Guid.TryParseExact(LauncherId, "D", out _) && DeviceId is { Length: 32 } &&
        DeviceId.All(Uri.IsHexDigit) && AccessToken is { Length: > 0 and <= 8192 } && LauncherSession.HasValidTokenFormat(AccessToken);
}
