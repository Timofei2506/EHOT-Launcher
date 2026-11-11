using System.Text.Json;
using EHOT.Launcher.Core.Models;
using Microsoft.Data.Sqlite;

namespace EHOT.Launcher.Storage;

public interface IProfileRepository
{
    Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken ct = default);

    Task<GameProfile?> GetAsync(string id, CancellationToken ct = default);

    Task SaveAsync(GameProfile profile, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task MarkLaunchedAsync(string id, CancellationToken ct = default);
}

public sealed class ProfileRepository(SqliteDatabase database) : IProfileRepository
{
    private const string SelectColumns = """
        id, name, minecraft_version, loader, loader_version, instance_dir, memory_mb, min_memory_mb,
        java_path, extra_jvm_args, extra_game_args, icon_path, account_id, created_at, last_launched_at,
        modpack_id, is_favorite
        """;

    public async Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM profiles ORDER BY is_favorite DESC, last_launched_at DESC, name;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<GameProfile>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<GameProfile?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM profiles WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task SaveAsync(GameProfile profile, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO profiles (
                id, name, minecraft_version, loader, loader_version, instance_dir, memory_mb, min_memory_mb,
                java_path, extra_jvm_args, extra_game_args, icon_path, account_id, created_at, last_launched_at,
                modpack_id, is_favorite)
            VALUES (
                $id, $name, $mc, $loader, $loaderVersion, $dir, $mem, $minMem,
                $java, $jvmArgs, $gameArgs, $icon, $account, $created, $launched, $modpack, $favorite)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                minecraft_version = excluded.minecraft_version,
                loader = excluded.loader,
                loader_version = excluded.loader_version,
                instance_dir = excluded.instance_dir,
                memory_mb = excluded.memory_mb,
                min_memory_mb = excluded.min_memory_mb,
                java_path = excluded.java_path,
                extra_jvm_args = excluded.extra_jvm_args,
                extra_game_args = excluded.extra_game_args,
                icon_path = excluded.icon_path,
                account_id = excluded.account_id,
                last_launched_at = excluded.last_launched_at,
                modpack_id = excluded.modpack_id,
                is_favorite = excluded.is_favorite;
            """;

        cmd.Parameters.AddWithValue("$id", profile.Id);
        cmd.Parameters.AddWithValue("$name", profile.Name);
        cmd.Parameters.AddWithValue("$mc", profile.MinecraftVersion);
        cmd.Parameters.AddWithValue("$loader", (int)profile.Loader);
        cmd.Parameters.AddWithValue("$loaderVersion", (object?)profile.LoaderVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$dir", profile.InstanceDirectory);
        cmd.Parameters.AddWithValue("$mem", profile.AllocatedMemoryMb);
        cmd.Parameters.AddWithValue("$minMem", (object?)profile.MinMemoryMb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$java", (object?)profile.JavaPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$jvmArgs", JsonSerializer.Serialize(profile.ExtraJvmArgs));
        cmd.Parameters.AddWithValue("$gameArgs", JsonSerializer.Serialize(profile.ExtraGameArgs));
        cmd.Parameters.AddWithValue("$icon", (object?)profile.IconPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$account", (object?)profile.AccountId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", Db.Iso(profile.CreatedAt));
        cmd.Parameters.AddWithValue("$launched", (object?)Db.Iso(profile.LastLaunchedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modpack", (object?)profile.ModpackId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$favorite", profile.IsFavorite ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM profiles WHERE id = $id; DELETE FROM installed_content WHERE profile_id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkLaunchedAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE profiles SET last_launched_at = $now WHERE id = $id;";
        cmd.Parameters.AddWithValue("$now", Db.Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static GameProfile Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Name = reader.GetString(1),
        MinecraftVersion = reader.GetString(2),
        Loader = (LoaderKind)reader.GetInt32(3),
        LoaderVersion = reader.NullableString(4),
        InstanceDirectory = reader.GetString(5),
        AllocatedMemoryMb = reader.GetInt32(6),
        MinMemoryMb = reader.IsDBNull(7) ? null : reader.GetInt32(7),
        JavaPath = reader.NullableString(8),
        ExtraJvmArgs = JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [],
        ExtraGameArgs = JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? [],
        IconPath = reader.NullableString(11),
        AccountId = reader.NullableString(12),
        CreatedAt = reader.Date(13),
        LastLaunchedAt = reader.NullableDate(14),
        ModpackId = reader.NullableString(15),
        IsFavorite = reader.GetInt32(16) == 1
    };
}
