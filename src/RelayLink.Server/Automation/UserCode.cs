using System.Security.Cryptography;
using System.Text;

namespace RelayLink.Server.Automation;

/// <summary>
/// 一次性用户码。8 位、无歧义字符集、显示为 XXXX-XXXX。
/// 用户码本身不构成凭据：换取令牌还要求设备私钥签名，且签名内容包含用户码摘要。
/// </summary>
public static class UserCode
{
    // 去掉易混淆的 0/O、1/I/L。
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int Length = 8;

    public static string Generate()
    {
        var chars = new char[Length];
        for (var index = 0; index < Length; index++)
        {
            chars[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    /// <summary>显示形式，例如 ABCD-EFGH。</summary>
    public static string Format(string code) => code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>去掉分隔符与空白，并转为大写，用于比较。</summary>
    public static string Normalize(string? input) =>
        new((input ?? string.Empty).Where(character => character != '-' && !char.IsWhiteSpace(character)).Select(char.ToUpperInvariant).ToArray());

    /// <summary>用户码摘要，参与设备签名，防止仅持用户码者替设备领令牌。</summary>
    public static string Digest(string normalizedCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode))).ToLowerInvariant();

    public static bool FixedTimeEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
