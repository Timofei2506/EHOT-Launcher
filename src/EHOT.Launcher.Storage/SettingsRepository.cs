using System.Text.Json;
using EHOT.Launcher.Core.Models;
using Microsoft.Data.Sqlite;

namespace EHOT.Launcher.Storage;

public interface ISettingsRepository
{
    Task<LauncherSettings> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(LauncherSettings settings, CancellationToken ct = default);
}

public sealed class SettingsRepository(SqliteDatabase database) : ISettingsRepository
{
    public async Task<LauncherSettings> LoadAsync(CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'launcher';";
        var json = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;

        if (string.IsNullOrWhiteSpace(json))
        {
            return LauncherSettings.Default;
        }

        return JsonSerializer.Deserialize<LauncherSettings>(json) ?? LauncherSettings.Default;
    }

    public async Task SaveAsync(LauncherSettings settings, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value, updated_at) VALUES ('launcher', $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(settings));
        cmd.Parameters.AddWithValue("$now", Db.Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
