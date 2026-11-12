using System.Globalization;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Models;
using EHOT.Launcher.Core.Platform;
using Microsoft.Data.Sqlite;

namespace EHOT.Launcher.Storage;

/// <summary>
/// Локальная база: SQLite в WAL-режиме, схема версионируется через user_version.
/// Все идентификаторы — строки (Guid), время — ISO-8601 UTC.
/// </summary>
public sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 2;

    private readonly string _connectionString;

    public SqliteDatabase(IAppPaths paths, ILogService log)
    {
        paths.EnsureCreated();
        DatabasePath = paths.DatabasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();

        Initialize(log);
    }

    public string DatabasePath { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize(ILogService log)
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            wal.ExecuteNonQuery();
        }

        var version = GetUserVersion(connection);
        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        log.Info("Storage", $"Инициализация схемы SQLite: {version} → {CurrentSchemaVersion}");
        using var transaction = connection.BeginTransaction();

        if (version < 1)
        {
            Execute(connection, transaction, SchemaV1);
        }

        if (version >= 1 && version < 2)
        {
            // v2: откуда взят скин и официальный ли UUID — часть сценария «ввёл ник → его скин и UUID».
            // Только для существующих баз: свежая база создаётся по SchemaV1 сразу с этими колонками.
            AddColumnIfMissing(connection, transaction, "accounts", "skin_source", "TEXT NULL");
            AddColumnIfMissing(connection, transaction, "accounts", "uuid_official", "INTEGER NOT NULL DEFAULT 0");
        }

        SetUserVersion(connection, transaction, CurrentSchemaVersion);
        transaction.Commit();
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS accounts (
            id TEXT PRIMARY KEY,
            username TEXT NOT NULL,
            uuid TEXT NOT NULL,
            kind INTEGER NOT NULL,
            xbox_user_id TEXT NULL,
            skin_path TEXT NULL,
            skin_model INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            last_used_at TEXT NULL,
            is_verified INTEGER NOT NULL DEFAULT 0,
            skin_source TEXT NULL,
            uuid_official INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS profiles (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            minecraft_version TEXT NOT NULL,
            loader INTEGER NOT NULL,
            loader_version TEXT NULL,
            instance_dir TEXT NOT NULL,
            memory_mb INTEGER NOT NULL,
            min_memory_mb INTEGER NULL,
            java_path TEXT NULL,
            extra_jvm_args TEXT NOT NULL DEFAULT '[]',
            extra_game_args TEXT NOT NULL DEFAULT '[]',
            icon_path TEXT NULL,
            account_id TEXT NULL,
            created_at TEXT NOT NULL,
            last_launched_at TEXT NULL,
            modpack_id TEXT NULL,
            is_favorite INTEGER NOT NULL DEFAULT 0,
            FOREIGN KEY (account_id) REFERENCES accounts(id) ON DELETE SET NULL
        );

        CREATE TABLE IF NOT EXISTS launch_history (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            profile_name TEXT NOT NULL,
            minecraft_version TEXT NOT NULL,
            account_name TEXT NOT NULL,
            status INTEGER NOT NULL,
            started_at TEXT NOT NULL,
            ended_at TEXT NULL,
            exit_code INTEGER NULL,
            error TEXT NULL,
            log_path TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_launch_history_started ON launch_history(started_at DESC);

        CREATE TABLE IF NOT EXISTS download_history (
            id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            url TEXT NOT NULL,
            destination TEXT NOT NULL,
            bytes INTEGER NOT NULL,
            duration_ms INTEGER NOT NULL,
            completed_at TEXT NOT NULL,
            error TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_download_history_completed ON download_history(completed_at DESC);

        CREATE TABLE IF NOT EXISTS installed_content (
            id TEXT PRIMARY KEY,
            profile_id TEXT NOT NULL,
            type INTEGER NOT NULL,
            provider TEXT NOT NULL,
            project_id TEXT NOT NULL,
            version_id TEXT NOT NULL,
            project_title TEXT NOT NULL,
            file_name TEXT NOT NULL,
            relative_path TEXT NOT NULL,
            sha512 TEXT NULL,
            size_bytes INTEGER NOT NULL,
            installed_at TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1,
            icon_url TEXT NULL,
            dependencies TEXT NOT NULL DEFAULT '[]',
            UNIQUE(profile_id, relative_path)
        );
        CREATE INDEX IF NOT EXISTS ix_installed_profile ON installed_content(profile_id);

        CREATE TABLE IF NOT EXISTS settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS modpacks (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            version TEXT NOT NULL,
            minecraft_version TEXT NOT NULL,
            loader INTEGER NOT NULL,
            loader_version TEXT NULL,
            source TEXT NOT NULL,
            project_id TEXT NULL,
            installed_profile_id TEXT NULL,
            imported_at TEXT NOT NULL
        );
        """;

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void AddColumnIfMissing(SqliteConnection connection, SqliteTransaction transaction, string table,
        string column, string definition)
    {
        using (var probe = connection.CreateCommand())
        {
            probe.Transaction = transaction;
            probe.CommandText = $"PRAGMA table_info({table});";
            using var reader = probe.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        Execute(connection, transaction, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
    }

    private static void SetUserVersion(SqliteConnection connection, SqliteTransaction tx, int version)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA user_version = {version};";
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction tx, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

internal static class Db
{
    public static string? NullableString(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTimeOffset Date(this SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static DateTimeOffset? NullableDate(this SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.Date(ordinal);

    public static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? Iso(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
