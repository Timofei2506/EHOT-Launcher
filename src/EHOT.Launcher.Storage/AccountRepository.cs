using EHOT.Launcher.Core.Models;
using Microsoft.Data.Sqlite;

namespace EHOT.Launcher.Storage;

public interface IAccountRepository
{
    Task<IReadOnlyList<AccountInfo>> GetAllAsync(CancellationToken ct = default);

    Task<AccountInfo?> GetAsync(string id, CancellationToken ct = default);

    Task SaveAsync(AccountInfo account, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task TouchAsync(string id, CancellationToken ct = default);
}

public sealed class AccountRepository(SqliteDatabase database) : IAccountRepository
{
    public async Task<IReadOnlyList<AccountInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, uuid, kind, xbox_user_id, skin_path, skin_model, created_at, last_used_at, is_verified
            FROM accounts ORDER BY kind DESC, last_used_at DESC, username;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<AccountInfo>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<AccountInfo?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, username, uuid, kind, xbox_user_id, skin_path, skin_model, created_at, last_used_at, is_verified
            FROM accounts WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task SaveAsync(AccountInfo account, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (id, username, uuid, kind, xbox_user_id, skin_path, skin_model, created_at, last_used_at, is_verified)
            VALUES ($id, $username, $uuid, $kind, $xbox, $skin, $skinModel, $created, $used, $verified)
            ON CONFLICT(id) DO UPDATE SET
                username = excluded.username,
                uuid = excluded.uuid,
                kind = excluded.kind,
                xbox_user_id = excluded.xbox_user_id,
                skin_path = excluded.skin_path,
                skin_model = excluded.skin_model,
                last_used_at = excluded.last_used_at,
                is_verified = excluded.is_verified;
            """;
        cmd.Parameters.AddWithValue("$id", account.Id);
        cmd.Parameters.AddWithValue("$username", account.Username);
        cmd.Parameters.AddWithValue("$uuid", account.Uuid.ToString("D"));
        cmd.Parameters.AddWithValue("$kind", (int)account.Kind);
        cmd.Parameters.AddWithValue("$xbox", (object?)account.XboxUserId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$skin", (object?)account.LocalSkinPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$skinModel", (int)account.SkinModel);
        cmd.Parameters.AddWithValue("$created", Db.Iso(account.CreatedAt));
        cmd.Parameters.AddWithValue("$used", (object?)Db.Iso(account.LastUsedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$verified", account.IsVerified ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task TouchAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET last_used_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Db.Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static AccountInfo Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Username = reader.GetString(1),
        Uuid = Guid.Parse(reader.GetString(2)),
        Kind = (AccountKind)reader.GetInt32(3),
        XboxUserId = reader.NullableString(4),
        LocalSkinPath = reader.NullableString(5),
        SkinModel = (SkinModel)reader.GetInt32(6),
        CreatedAt = reader.Date(7),
        LastUsedAt = reader.NullableDate(8),
        IsVerified = reader.GetInt32(9) == 1
    };
}
