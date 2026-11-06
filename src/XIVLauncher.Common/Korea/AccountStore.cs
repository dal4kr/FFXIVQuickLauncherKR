using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XIVLauncher.Common.Korea;

public sealed class AccountStore(string path)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FFXIVLauncherKR/accounts/v1");

    public IReadOnlyList<StoredAccount> LoadAll()
    {
        if (!File.Exists(path)) return [];
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var accounts = JsonSerializer.Deserialize<List<StoredAccount>>(plaintext);
            if (accounts is null || accounts.Any(x => string.IsNullOrWhiteSpace(x.UserName) || x.Token is null || !Enum.IsDefined(x.Provider)))
                throw new JsonException();
            using var document = JsonDocument.Parse(plaintext);
            if (document.RootElement.EnumerateArray().Any(x => x.TryGetProperty("Cookies", out _) || x.TryGetProperty("MemberKey", out _)))
                Write(accounts);
            return accounts;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherException("저장된 계정 정보를 읽지 못했습니다. 다시 로그인해 주세요.");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(StoredAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.UserName) || !Enum.IsDefined(account.Provider))
            throw new LauncherException("저장할 계정 이름이 없습니다. 다시 로그인해 주세요.");
        var accounts = LoadAll().ToList();
        accounts.RemoveAll(x => x.Provider == account.Provider && string.Equals(x.UserName, account.UserName, StringComparison.OrdinalIgnoreCase));
        accounts.Add(account);
        Write(accounts);
    }

    public void Delete(string userName, LoginProvider provider = LoginProvider.Actoz)
    {
        var accounts = LoadAll().Where(x => x.Provider != provider || !string.Equals(x.UserName, userName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (accounts.Count == 0) { if (File.Exists(path)) File.Delete(path); }
        else Write(accounts);
    }

    private void Write(List<StoredAccount> accounts)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(accounts);
        try { AtomicFile.Write(path, ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}
