using System.Security.Cryptography;
using System.Text;

namespace TextCascadeSharp.Core;

public static class CryptoManager
{
    private const int AesKeyBytes = 32;
    private const int NonceBytes = 16;
    private static readonly RandomNumberGenerator Random = RandomNumberGenerator.Create();

    public static string Sha3_512LowercaseHex(string input)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA3_512.IsSupported ? SHA3_512.HashData(inputBytes) : Sha3Fallback.Sha3_512(inputBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static byte[] DerivePasswordKey(string username, string rawPassword, string saltSuffix, int rounds)
    {
        var salt = Encoding.UTF8.GetBytes(username + rawPassword + saltSuffix);
        return Rfc2898DeriveBytes.Pbkdf2(
            rawPassword,
            salt,
            rounds,
            HashAlgorithmName.SHA256,
            AesKeyBytes);
    }

    public static EncryptedPayload Encrypt(string plainText, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        var nonce = new byte[NonceBytes];
        Random.GetBytes(nonce);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var (ciphertext, tag) = GcmCipher.Encrypt(key, nonce, plainBytes);
        return new EncryptedPayload(
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag));
    }

    public static string Decrypt(EncryptedPayload payload, string keyBase64)
    {
        var key = Convert.FromBase64String(keyBase64);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        var tag = Convert.FromBase64String(payload.Tag);
        var plainBytes = GcmCipher.Decrypt(key, nonce, ciphertext, tag);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
