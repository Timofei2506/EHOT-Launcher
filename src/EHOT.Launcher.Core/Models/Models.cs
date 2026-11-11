namespace EHOT.Launcher.Core.Models;

public enum AccountKind
{
    /// <summary>Локальный (оффлайн) профиль. Не является лицензионным аккаунтом.</summary>
    Local = 0,

    /// <summary>Microsoft/Minecraft аккаунт, авторизованный через официальный OAuth2.</summary>
    Microsoft = 1
}

/// <summary>Аккаунт в лаунчере. Токены НЕ хранятся здесь — только ссылка на секрет.</summary>
public sealed record AccountInfo
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public required Guid Uuid { get; init; }
    public required AccountKind Kind { get; init; }

    /// <summary>Для локального профиля — выбранный скин (путь к PNG) и его модель.</summary>
    public string? LocalSkinPath { get; init; }
    public SkinModel SkinModel { get; init; } = SkinModel.Classic;

    /// <summary>Xbox User Id (только для MS-аккаунтов).</summary>
    public string? XboxUserId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Честная пометка: аккаунт подтверждён официальным сервисом.</summary>
    public bool IsVerified { get; init; }

    public string DisplayKind => Kind == AccountKind.Microsoft ? "Microsoft" : "Локальный";
}

public enum SkinModel
{
    Classic = 0,
    Slim = 1
}

public enum LoaderKind
{
    Vanilla = 0,
    Fabric = 1,
    Forge = 2,
    NeoForge = 3,
    Quilt = 4
}

/// <summary>Игровой профиль (инстанс): независимая папка, версия, лоадер, настройки Java.</summary>
public sealed record GameProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string MinecraftVersion { get; init; }
    public LoaderKind Loader { get; init; }
    public string? LoaderVersion { get; init; }

    /// <summary>Папка инстанса (внутри общего каталога игры).</summary>
    public required string InstanceDirectory { get; init; }

    public int AllocatedMemoryMb { get; init; } = 4096;
    public int? MinMemoryMb { get; init; }

    /// <summary>Путь к выбранной Java (null → автовыбор).</summary>
    public string? JavaPath { get; init; }

    public IReadOnlyList<string> ExtraJvmArgs { get; init; } = [];
    public IReadOnlyList<string> ExtraGameArgs { get; init; } = [];

    public string? IconPath { get; init; }
    public string? AccountId { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLaunchedAt { get; init; }

    /// <summary>Профиль создан из сборки (mrpack) — тогда хранится идентификатор сборки.</summary>
    public string? ModpackId { get; init; }

    public bool IsFavorite { get; init; }

    public string DisplayVersion => Loader == LoaderKind.Vanilla
        ? MinecraftVersion
        : $"{Loader} {LoaderVersion} · {MinecraftVersion}";
}

public enum LaunchStatus
{
    Started = 0,
    Exited = 1,
    Failed = 2,
    Aborted = 3
}

public sealed record LaunchRecord
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required string ProfileName { get; init; }
    public required string MinecraftVersion { get; init; }
    public required string AccountName { get; init; }
    public LaunchStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? Error { get; init; }
    public string? LogPath { get; init; }
}

public enum ContentType
{
    Mod = 0,
    Modpack = 1,
    ResourcePack = 2,
    Shader = 3,
    World = 4,
    Plugin = 5
}

/// <summary>Установленный контент (локальный учёт, чтобы определять обновления/конфликты).</summary>
public sealed record InstalledContent
{
    public required string Id { get; init; }
    public required string ProfileId { get; init; }
    public required ContentType Type { get; init; }
    public required string ProviderId { get; init; }   // modrinth | curseforge | manual
    public required string ProjectId { get; init; }
    public required string VersionId { get; init; }
    public required string ProjectTitle { get; init; }
    public required string FileName { get; init; }
    public required string RelativePath { get; init; }
    public string? Sha512 { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Enabled { get; init; } = true;
    public string? IconUrl { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
}

/// <summary>Настройки лаунчера (сохраняются в SQLite + экспорт в JSON).</summary>
public sealed record LauncherSettings
{
    public string Theme { get; init; } = "dark";
    public string Language { get; init; } = "ru";
    public string? MinecraftDirectory { get; init; }
    public string? DownloadsDirectory { get; init; }
    public int MaxMemoryMb { get; init; } = 4096;
    public int MaxParallelDownloads { get; init; } = 4;
    public bool AutoUpdateContent { get; init; } = true;
    public bool CheckLauncherUpdates { get; init; } = true;
    public bool NotificationsEnabled { get; init; } = true;
    public bool ReduceAnimations { get; init; } = false;

    // Game Network
    public string ControlPlaneUrl { get; init; } = "https://api.ehot.example";
    public bool PreferDirectConnection { get; init; } = true;
    public bool AllowRelay { get; init; } = true;
    public bool AllowLanDiscovery { get; init; } = true;

    // Каталог
    public string? CurseForgeApiKey { get; init; }
    public bool ContentSafeMode { get; init; } = true;

    public static LauncherSettings Default { get; } = new();
}

/// <summary>Новость из официального источника.</summary>
public sealed record NewsItem(string Title, string Version, string Type, string ImageUrl, DateTimeOffset Date, string ContentPath, string Id);

/// <summary>Точка входа в быстрый запуск на главном экране.</summary>
public sealed record QuickAction(string Id, string Title, string Subtitle, string AccentColor);
