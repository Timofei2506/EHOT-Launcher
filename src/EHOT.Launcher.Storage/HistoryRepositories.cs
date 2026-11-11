using EHOT.Launcher.Core.Downloads;
using EHOT.Launcher.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace EHOT.Launcher.Storage;

public interface ILaunchHistoryRepository
{
    Task AddAsync(LaunchRecord record, CancellationToken ct = default);

    Task UpdateAsync(LaunchRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<LaunchRecord>> GetRecentAsync(int limit, CancellationToken ct = default);

    Task<LaunchRecord?> GetLastAsync(CancellationToken ct = default);
}

public sealed class LaunchHistoryRepository(SqliteDatabase database) : ILaunchHistoryRepository
{
    public async Task AddAsync(LaunchRecord record, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO launch_history (id, profile_id, profile_name, minecraft_version, account_name, status, started_at, ended_at, exit_code, error, log_path)
            VALUES ($id, $profile, $profileName, $mc, $account, $status, $started, $ended, $exit, $error, $log);
            """;
        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(LaunchRecord record, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE launch_history SET status = $status, ended_at = $ended, exit_code = $exit, error = $error, log_path = $log
            WHERE id = $id;
            """;
        AddParameters(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LaunchRecord>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, profile_id, profile_name, minecraft_version, account_name, status, started_at, ended_at, exit_code, error, log_path
            FROM launch_history ORDER BY started_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<LaunchRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new LaunchRecord
            {
                Id = reader.GetString(0),
                ProfileId = reader.GetString(1),
                ProfileName = reader.GetString(2),
                MinecraftVersion = reader.GetString(3),
                AccountName = reader.GetString(4),
                Status = (LaunchStatus)reader.GetInt32(5),
                StartedAt = reader.Date(6),
                EndedAt = reader.NullableDate(7),
                ExitCode = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                Error = reader.NullableString(9),
                LogPath = reader.NullableString(10)
            });
        }

        return result;
    }

    public async Task<LaunchRecord?> GetLastAsync(CancellationToken ct = default)
    {
        var recent = await GetRecentAsync(1, ct).ConfigureAwait(false);
        return recent.Count > 0 ? recent[0] : null;
    }

    private static void AddParameters(SqliteCommand cmd, LaunchRecord record)
    {
        cmd.Parameters.AddWithValue("$id", record.Id);
        cmd.Parameters.AddWithValue("$profile", record.ProfileId);
        cmd.Parameters.AddWithValue("$profileName", record.ProfileName);
        cmd.Parameters.AddWithValue("$mc", record.MinecraftVersion);
        cmd.Parameters.AddWithValue("$account", record.AccountName);
        cmd.Parameters.AddWithValue("$status", (int)record.Status);
        cmd.Parameters.AddWithValue("$started", Db.Iso(record.StartedAt));
        cmd.Parameters.AddWithValue("$ended", (object?)Db.Iso(record.EndedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exit", (object?)record.ExitCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)record.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$log", (object?)record.LogPath ?? DBNull.Value);
    }
}

public sealed class DownloadHistoryRepository(SqliteDatabase database) : IDownloadHistoryStore
{
    public async Task RecordCompletedAsync(DownloadRequest request, long bytes, TimeSpan duration, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (id, display_name, url, destination, bytes, duration_ms, completed_at, error)
            VALUES ($id, $name, $url, $dest, $bytes, $ms, $at, NULL);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$name", request.DisplayName ?? Path.GetFileName(request.DestinationPath));
        cmd.Parameters.AddWithValue("$url", request.Url.ToString());
        cmd.Parameters.AddWithValue("$dest", request.DestinationPath);
        cmd.Parameters.AddWithValue("$bytes", bytes);
        cmd.Parameters.AddWithValue("$ms", (long)duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$at", Db.Iso(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordFailedAsync(DownloadRequest request, string error, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO download_history (id, display_name, url, destination, bytes, duration_ms, completed_at, error)
            VALUES ($id, $name, $url, $dest, 0, 0, $at, $error);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$name", request.DisplayName ?? Path.GetFileName(request.DestinationPath));
        cmd.Parameters.AddWithValue("$url", request.Url.ToString());
        cmd.Parameters.AddWithValue("$dest", request.DestinationPath);
        cmd.Parameters.AddWithValue("$at", Db.Iso(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$error", error);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DownloadHistoryEntry>> GetRecentAsync(int limit, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, display_name, url, destination, bytes, completed_at, error, duration_ms
            FROM download_history ORDER BY completed_at DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<DownloadHistoryEntry>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new DownloadHistoryEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.Date(5),
                reader.NullableString(6),
                TimeSpan.FromMilliseconds(reader.GetInt64(7))));
        }

        return result;
    }
}

public interface IInstalledContentRepository
{
    Task<IReadOnlyList<InstalledContent>> GetForProfileAsync(string profileId, CancellationToken ct = default);

    Task SaveAsync(InstalledContent content, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);

    Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default);
}

public sealed class InstalledContentRepository(SqliteDatabase database) : IInstalledContentRepository
{
    public async Task<IReadOnlyList<InstalledContent>> GetForProfileAsync(string profileId, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, profile_id, type, provider, project_id, version_id, project_title, file_name, relative_path,
                   sha512, size_bytes, installed_at, enabled, icon_url, dependencies
            FROM installed_content WHERE profile_id = $profile ORDER BY project_title;
            """;
        cmd.Parameters.AddWithValue("$profile", profileId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var result = new List<InstalledContent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new InstalledContent
            {
                Id = reader.GetString(0),
                ProfileId = reader.GetString(1),
                Type = (ContentType)reader.GetInt32(2),
                ProviderId = reader.GetString(3),
                ProjectId = reader.GetString(4),
                VersionId = reader.GetString(5),
                ProjectTitle = reader.GetString(6),
                FileName = reader.GetString(7),
                RelativePath = reader.GetString(8),
                Sha512 = reader.NullableString(9),
                SizeBytes = reader.GetInt64(10),
                InstalledAt = reader.Date(11),
                Enabled = reader.GetInt32(12) == 1,
                IconUrl = reader.NullableString(13),
                Dependencies = System.Text.Json.JsonSerializer.Deserialize<string[]>(reader.GetString(14)) ?? []
            });
        }

        return result;
    }

    public async Task SaveAsync(InstalledContent content, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO installed_content (id, profile_id, type, provider, project_id, version_id, project_title, file_name,
                relative_path, sha512, size_bytes, installed_at, enabled, icon_url, dependencies)
            VALUES ($id, $profile, $type, $provider, $project, $version, $title, $fileName, $path, $sha, $size, $at, $enabled, $icon, $deps)
            ON CONFLICT(profile_id, relative_path) DO UPDATE SET
                version_id = excluded.version_id,
                project_title = excluded.project_title,
                sha512 = excluded.sha512,
                size_bytes = excluded.size_bytes,
                installed_at = excluded.installed_at,
                enabled = excluded.enabled,
                icon_url = excluded.icon_url,
                dependencies = excluded.dependencies;
            """;
        cmd.Parameters.AddWithValue("$id", content.Id);
        cmd.Parameters.AddWithValue("$profile", content.ProfileId);
        cmd.Parameters.AddWithValue("$type", (int)content.Type);
        cmd.Parameters.AddWithValue("$provider", content.ProviderId);
        cmd.Parameters.AddWithValue("$project", content.ProjectId);
        cmd.Parameters.AddWithValue("$version", content.VersionId);
        cmd.Parameters.AddWithValue("$title", content.ProjectTitle);
        cmd.Parameters.AddWithValue("$fileName", content.FileName);
        cmd.Parameters.AddWithValue("$path", content.RelativePath);
        cmd.Parameters.AddWithValue("$sha", (object?)content.Sha512 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$size", content.SizeBytes);
        cmd.Parameters.AddWithValue("$at", Db.Iso(content.InstalledAt));
        cmd.Parameters.AddWithValue("$enabled", content.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$icon", (object?)content.IconUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$deps", System.Text.Json.JsonSerializer.Serialize(content.Dependencies));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM installed_content WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var connection = database.Open();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE installed_content SET enabled = $enabled WHERE id = $id;";
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}

public static class StorageModule
{
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddEhotStorage(
        this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ISettingsRepository, SettingsRepository>();
        services.AddSingleton<IProfileRepository, ProfileRepository>();
        services.AddSingleton<IAccountRepository, AccountRepository>();
        services.AddSingleton<ILaunchHistoryRepository, LaunchHistoryRepository>();
        services.AddSingleton<DownloadHistoryRepository>();
        services.AddSingleton<IDownloadHistoryStore>(sp => sp.GetRequiredService<DownloadHistoryRepository>());
        services.AddSingleton<IInstalledContentRepository, InstalledContentRepository>();
        return services;
    }
}
