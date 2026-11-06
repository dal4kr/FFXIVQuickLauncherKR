using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace XIVLauncher.Common.Korea;

public sealed record NaverLoginIdentity(string MemberId, string ChannelMemberId);

public static class NaverLoginProtocol
{
    public const int MaximumGateResponseLength = 2 * 1024 * 1024;
    private static readonly string ReturnUrl = "https://ff14.game.naver.com/ChannelNv/LauncherGate";
    public static Uri LoginUri { get; } = new("https://nid.naver.com/nidlogin.login?url=" + Uri.EscapeDataString(
        "https://game.naver.com/login.naver?nxtUrl=" + Uri.EscapeDataString(ReturnUrl)));
    private static readonly Regex FieldAssignment = new(
        """(?:\$|jQuery)\(\s*(['"])#(?<field>memberID|chMemberID)\1\s*(?:,\s*(?:parent\.)?document)?\s*\)\s*\.val\(\s*(?<value>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')\s*\)""",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    public static bool IsAllowedNavigation(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && (uri.Host.Equals("naver.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith(".naver.com", StringComparison.OrdinalIgnoreCase) ||
        uri.Host.Equals("newlauncher.ff14.co.kr", StringComparison.OrdinalIgnoreCase));

    public static bool IsGate(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Equals("newlauncher.ff14.co.kr", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').Equals("/LauncherFF/GateProcess", StringComparison.OrdinalIgnoreCase);

    public static NaverLoginIdentity? ParseGate(Uri uri, string html)
    {
        if (!IsGate(uri)) return null;
        if (html.Length > MaximumGateResponseLength) throw new LauncherException("네이버 로그인 응답이 너무 큽니다.");
        using var document = new HtmlParser().ParseDocument(html);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in document.QuerySelectorAll("input"))
        {
            var key = input.GetAttribute("name") ?? input.Id;
            if (key is not null && (key.Equals("memberID", StringComparison.OrdinalIgnoreCase) || key.Equals("chMemberID", StringComparison.OrdinalIgnoreCase)))
                values[key] = input.GetAttribute("value") ?? "";
        }
        try
        {
            foreach (var script in document.QuerySelectorAll("script"))
                foreach (Match match in FieldAssignment.Matches(script.TextContent))
                {
                    var literal = match.Groups["value"].Value;
                    var value = Regex.Unescape(literal[1..^1]);
                    if (!string.IsNullOrEmpty(value)) values[match.Groups["field"].Value] = value;
                }
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        { throw new LauncherException("네이버 로그인 응답을 해석하지 못했습니다."); }
        var member = values.GetValueOrDefault("memberID");
        if (string.IsNullOrWhiteSpace(member) || member.Length > 1024 || member.Any(char.IsControl)) return null;
        return new(member, values.GetValueOrDefault("chMemberID") ?? "");
    }
}
