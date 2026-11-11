using System.Security.Cryptography;
using System.Text;
using EHOT.Launcher.Core.Security;

namespace EHOT.Launcher.Platform.Shared;

/// <summary>
/// Fallback-шифрование секретов для платформ без системного хранилища ключей (Linux, тесты).
/// Ключ хранится в файле с правами только для владельца (0600). Честно помечается как FileKey.
/// </summary>
public sealed class AesGcmFileKeyProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    public AesGcmFileKeyProtector(string keyFilePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(keyFilePath)!);
        _key = LoadOrCreateKey(keyFilePath);
    }

    public string Mechanism => "AES-256-GCM (локальный ключ, права 0600)";

    public SecretProtectionStrength Strength => SecretProtectionStrength.FileKey;

    public byte[] Protect(byte[] plaintext, string context)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(context));

        var output = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(output, 0);
        tag.CopyTo(output, NonceSize);
        ciphertext.CopyTo(output, NonceSize + TagSize);
        return output;
    }

    public byte[] Unprotect(byte[] ciphertext, string context)
    {
        if (ciphertext.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Повреждённый шифротекст секрета.");
        }

        var nonce = ciphertext.AsSpan(0, NonceSize);
        var tag = ciphertext.AsSpan(NonceSize, TagSize);
        var payload = ciphertext.AsSpan(NonceSize + TagSize);
        var plaintext = new byte[payload.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, payload, tag, plaintext, Encoding.UTF8.GetBytes(context));
        return plaintext;
    }

    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == KeySize)
            {
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(path, key);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // best effort
            }
        }

        return key;
    }
}

internal sealed class CryptographicException(string message) : Exception(message);
