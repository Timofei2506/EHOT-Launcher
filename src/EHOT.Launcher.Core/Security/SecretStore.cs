namespace EHOT.Launcher.Core.Security;

/// <summary>
/// Шифрование секретов «в покое» платформенными средствами:
/// Windows — DPAPI (CurrentUser), macOS — Keychain через Security.framework,
/// Linux — файл-ключ 0600 + AES-GCM (честный fallback без системного хранилища).
/// </summary>
public interface ISecretProtector
{
    /// <summary>Название механизма для отображения в настройках/диагностике.</summary>
    string Mechanism { get; }

    /// <summary>Насколько защита сильна на этой платформе (для честного предупреждения).</summary>
    SecretProtectionStrength Strength { get; }

    byte[] Protect(byte[] plaintext, string context);

    byte[] Unprotect(byte[] ciphertext, string context);
}

public enum SecretProtectionStrength
{
    /// <summary>Шифрование ключом в файле рядом с данными (только права ФС).</summary>
    FileKey = 0,

    /// <summary>Шифрование ключом пользователя ОС (DPAPI/Keychain).</summary>
    OsUser = 1,

    /// <summary>Аппаратный/системный анклав (Secure Enclave, TPM-подобное).</summary>
    Hardware = 2
}

/// <summary>Хранилище токенов: только через ISecretProtector, на диск — шифротекст.</summary>
public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, CancellationToken ct = default);

    Task RemoveAsync(string key, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default);
}

/// <summary>Файловое хранилище секретов (используется платформенными реализациями).</summary>
public sealed class FileSecretStore(string filePath, ISecretProtector protector) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            if (!map.TryGetValue(key, out var protectedBase64))
            {
                return null;
            }

            var cipher = Convert.FromBase64String(protectedBase64);
            var plain = protector.Unprotect(cipher, key);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null; // повреждённая запись = отсутствие секрета
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            var cipher = protector.Protect(System.Text.Encoding.UTF8.GetBytes(value), key);
            map[key] = Convert.ToBase64String(cipher);
            await SaveAsync(map, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            if (map.Remove(key))
            {
                await SaveAsync(map, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var map = await LoadAsync(ct).ConfigureAwait(false);
            return map.Keys.ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, string>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var json = await File.ReadAllTextAsync(filePath, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(StringComparer.Ordinal);
    }

    private async Task SaveAsync(Dictionary<string, string> map, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var tmp = filePath + ".tmp";
        await File.WriteAllTextAsync(tmp, System.Text.Json.JsonSerializer.Serialize(map), ct).ConfigureAwait(false);
        TryRestrictPermissions(tmp);
        File.Move(tmp, filePath, overwrite: true);
    }

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // на Windows права задаются ACL; файл и так в профиле пользователя
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception)
        {
            // best effort: не ломаем сохранение из-за прав
        }
    }
}

internal sealed class CryptographicException(string message) : Exception(message);
