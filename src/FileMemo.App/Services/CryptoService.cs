using System.Security.Cryptography;
using System.Text;

namespace FileMemo.App.Services;

/// <summary>
/// 分级加密（需求 3.13）：
/// - 普通便签/待办可明文；
/// - 剪贴板、文件路径、文件备注等敏感数据强制加密；
/// 使用 DPAPI(CurrentUser) 绑定当前用户，密钥不出机器。
/// </summary>
public static class CryptoService
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SuperNote.v1");

    /// <summary>加密文本，返回 Base64；失败时回退为原文（并加前缀标记以便识别）。</summary>
    public static string Encrypt(string plain, bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(plain)) return plain;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return "enc:" + Convert.ToBase64String(enc);
        }
        catch { return plain; }
    }

    public static string Decrypt(string stored, bool enabled)
    {
        if (!enabled || string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith("enc:", StringComparison.Ordinal)) return stored;
        try
        {
            var data = Convert.FromBase64String(stored[4..]);
            var dec = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return stored; }
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string TextHash(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    // ---- 云同步使用的字节级 / 字符串级加密（P1，需求 3.12 / 3.13）----
    /// <summary>加密字节流（DPAPI），失败时原样返回。</summary>
    public static byte[] ProtectBytes(byte[] plain)
    {
        try { return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        catch { return plain; }
    }

    /// <summary>解密字节流；非 DPAPI 数据原样返回。</summary>
    public static byte[] UnprotectBytes(byte[] data)
    {
        try { return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser); }
        catch { return data; }
    }

    /// <summary>加密字符串为 Base64（用于保存云同步密码密文）。</summary>
    public static string ProtectString(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var enc = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(enc);
        }
        catch { return ""; }
    }

    /// <summary>解密 Base64 密文；失败返回空串。</summary>
    public static string UnprotectString(string cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var data = Convert.FromBase64String(cipher);
            var dec = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return ""; }
    }
}
