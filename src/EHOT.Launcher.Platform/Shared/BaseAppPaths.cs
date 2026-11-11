using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Platform.Shared;

/// <summary>Общая логика путей: конкретные корни задаёт платформа.</summary>
public abstract class BaseAppPaths : IAppPaths
{
    public const string AppFolderName = "EHOTLauncher";

    protected BaseAppPaths()
    {
        AppDataDirectory = ResolveAppDataDirectory();
        CacheDirectory = Path.Combine(AppDataDirectory, "cache");
        LogsDirectory = Path.Combine(AppDataDirectory, "logs");
        DefaultMinecraftDirectory = Path.Combine(AppDataDirectory, "instances");
        DefaultDownloadsDirectory = Path.Combine(AppDataDirectory, "downloads");
        DatabasePath = Path.Combine(AppDataDirectory, "ehot.db");
    }

    public string AppDataDirectory { get; }

    public string CacheDirectory { get; }

    public string LogsDirectory { get; }

    public string DefaultMinecraftDirectory { get; }

    public string DefaultDownloadsDirectory { get; }

    public string DatabasePath { get; }

    protected abstract string ResolveAppDataDirectory();

    public void EnsureCreated()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(DefaultMinecraftDirectory);
        Directory.CreateDirectory(DefaultDownloadsDirectory);

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(
                    AppDataDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception)
            {
                // не критично
            }
        }
    }
}

/// <summary>Базовая реализация IPlatformInfo.</summary>
public class BasePlatformInfo : IPlatformInfo
{
    public BasePlatformInfo()
    {
        RuntimeIdentifier = BuildRid();
    }

    public virtual string OsName => OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : "Unknown";

    public virtual string OsVersion => Environment.OSVersion.VersionString;

    public string Architecture => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public bool IsLinux => OperatingSystem.IsLinux();

    public string RuntimeVersion => Environment.Version.ToString();

    public long TotalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public int LogicalProcessorCount => Environment.ProcessorCount;

    public string RuntimeIdentifier { get; }

    private static string BuildRid()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";

        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => "unknown"
        };

        return $"{os}-{arch}";
    }
}
