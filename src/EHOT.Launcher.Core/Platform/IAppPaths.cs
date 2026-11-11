namespace EHOT.Launcher.Core.Platform;

/// <summary>Абстракция путей приложения. Реализации — в Platform.* сборках.</summary>
public interface IAppPaths
{
    /// <summary>Каталог данных лаунчера (настройки, БД, кэш).</summary>
    string AppDataDirectory { get; }

    /// <summary>Каталог кэша (можно чистить без потери настроек).</summary>
    string CacheDirectory { get; }

    /// <summary>Каталог логов.</summary>
    string LogsDirectory { get; }

    /// <summary>Каталог по умолчанию для .minecraft-подобных инстансов.</summary>
    string DefaultMinecraftDirectory { get; }

    /// <summary>Каталог загрузок по умолчанию.</summary>
    string DefaultDownloadsDirectory { get; }

    /// <summary>Файл локальной БД SQLite.</summary>
    string DatabasePath { get; }

    void EnsureCreated();
}

/// <summary>Информация о платформе/устройстве.</summary>
public interface IPlatformInfo
{
    string OsName { get; }
    string OsVersion { get; }
    string Architecture { get; }
    bool IsWindows { get; }
    bool IsMacOS { get; }
    bool IsLinux { get; }
    string RuntimeVersion { get; }
    long TotalMemoryBytes { get; }
    int LogicalProcessorCount { get; }

    /// <summary>Имя дистрибутива/RID-подобный идентификатор: win-x64, osx-arm64, linux-x64.</summary>
    string RuntimeIdentifier { get; }
}
