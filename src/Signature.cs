using System.Security.Cryptography;
using System.Text;

namespace InfisicalPushBridge;

/// <summary>
/// Infisical の Webhook 署名の検証。
///
/// Infisical はペイロード(JSON文字列そのもの)を secret key で HMAC-SHA256 し、
/// ヘッダ x-infisical-signature に "t=&lt;unix ms&gt;;&lt;hex&gt;" の形で付ける
/// (backend/src/services/webhook/webhook-fns.ts の triggerWebhookRequest)。
/// 検証は受信した生のボディバイト列に対して行う。パースしてから再シリアライズすると
/// キー順や空白の差で必ず壊れる。
/// </summary>
public static class Signature
{
    /// <summary>リプレイ許容幅。署名対象に時刻が含まれるので、古い要求の再送をここで落とす。</summary>
    public const long ToleranceMs = 15 * 60 * 1000;

    public static bool Verify(string? header, ReadOnlySpan<byte> body, string secretKey, long nowMs)
    {
        if (string.IsNullOrEmpty(header)) return false;

        // "t=1234;abcd..." の2部品。形式が崩れていたら黙って拒否
        var parts = header.Split(';', 2);
        if (parts.Length != 2 || !parts[0].StartsWith("t=", StringComparison.Ordinal)) return false;
        if (!long.TryParse(parts[0].AsSpan(2), out var ts)) return false;
        if (Math.Abs(nowMs - ts) > ToleranceMs) return false;

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), body));
        var given = parts[1].Trim().ToLowerInvariant();

        // 比較は固定時間で。文字列比較だと一致した接頭辞の長さが応答時間に漏れる
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(given));
    }
}
