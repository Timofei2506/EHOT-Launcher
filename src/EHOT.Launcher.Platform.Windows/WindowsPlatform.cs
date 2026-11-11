using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Platform.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Platform.Windows;

/// <summary>Пути Windows: %APPDATA%\EHOTLauncher.</summary>
public sealed class WindowsAppPaths : BaseAppPaths
{
    protected override string ResolveAppDataDirectory()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(roaming))
        {
            roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming");
        }

        return Path.Combine(roaming, AppFolderName);
    }
}

/// <summary>DPAPI (CurrentUser): секреты расшифровываются только тем же пользователем на той же машине.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EHOT.Launcher.Secrets.v1");

    public string Mechanism => "Windows DPAPI (CurrentUser)";

    public SecretProtectionStrength Strength => SecretProtectionStrength.OsUser;

    public byte[] Protect(byte[] plaintext, string context)
        => ProtectedData.Protect(plaintext, CombineEntropy(context), DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext, string context)
        => ProtectedData.Unprotect(ciphertext, CombineEntropy(context), DataProtectionScope.CurrentUser);

    private static byte[] CombineEntropy(string context)
    {
        var ctx = Encoding.UTF8.GetBytes(context);
        var result = new byte[Entropy.Length + ctx.Length];
        Entropy.CopyTo(result, 0);
        ctx.CopyTo(result, Entropy.Length);
        return result;
    }
}

/// <summary>
/// Уведомления Windows через toast (PowerShell + WinRT). Если PowerShell недоступен — IsSupported = false,
/// и лаунчер показывает уведомление внутри интерфейса.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNotificationService(IProcessRunner runner, ILogger<WindowsNotificationService> logger) : INotificationService
{
    public bool IsSupported { get; } = OperatingSystem.IsWindows() && File.Exists(PowerShellPath());

    public async Task NotifyAsync(string title, string message, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return;
        }

        var safeTitle = Escape(title);
        var safeMessage = Escape(message);
        var script = $"""
            [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null;
            $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);
            $texts = $template.GetElementsByTagName('text');
            $texts.Item(0).AppendChild($template.CreateTextNode('{safeTitle}')) > $null;
            $texts.Item(1).AppendChild($template.CreateTextNode('{safeMessage}')) > $null;
            $toast = [Windows.UI.Notifications.ToastNotification]::new($template);
            [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('EHOT Launcher').Show($toast);
            """;

        try
        {
            var result = await runner.RunAsync(new ProcessRequest(
                PowerShellPath(),
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script]), ct).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                logger.LogDebug("Toast не отправлен: {Error}", result.StandardError);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Toast уведомление не удалось: {Message}", ex.Message);
        }
    }

    private static string PowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Escape(string value) => value.Replace("'", "''");
}

/// <summary>
/// Права для TUN-адаптера: нужен elevated helper-сервис EHOT.Network.Service.
/// Лаунчер сам никогда не запускается от администратора.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkPrivileges : INetworkPrivileges
{
    private const string HelperServiceName = "EHOTNetworkHelper";

    public bool HasTunnelPrivileges => IsHelperRunning() || IsAdministrator();

    public bool CanRequestElevation => true;

    public string Describe(TunnelPrivilegeState state) => state switch
    {
        TunnelPrivilegeState.Granted => "Сетевой помощник запущен, туннель доступен.",
        TunnelPrivilegeState.ElevationRequired => "Требуется подтверждение UAC для запуска сетевого помощника.",
        TunnelPrivilegeState.HelperNotInstalled => "Сетевой помощник EHOTNetworkHelper не установлен. Установка требует прав администратора.",
        _ => "Туннель недоступен на этой системе."
    };

    public static TunnelPrivilegeState CurrentState()
    {
        if (IsHelperRunning())
        {
            return TunnelPrivilegeState.Granted;
        }

        return IsAdministrator() ? TunnelPrivilegeState.Granted : TunnelPrivilegeState.HelperNotInstalled;
    }

    internal static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsHelperRunning()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("EHOT.Network.Service"))
            {
                process.Dispose();
                return true;
            }
        }
        catch (Exception)
        {
            // ignore
        }

        return false;
    }
}

[SupportedOSPlatform("windows")]
public static class WindowsPlatformModule
{
    /// <summary>Регистрирует Windows-реализации платформенных контрактов.</summary>
    public static IServiceCollection AddWindowsPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, WindowsAppPaths>();
        services.AddSingleton<IPlatformInfo, BasePlatformInfo>();
        services.AddSingleton<ISecretProtector, WindowsDpapiSecretProtector>();
        // Токены Microsoft-аккаунта шифруются DPAPI и лежат только в профиле пользователя:
        // DPAPI привязан к учётной записи Windows, поэтому файл бесполезен на другой машине.
        services.AddSingleton<ISecretStore>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            return new FileSecretStore(
                Path.Combine(paths.AppDataDirectory, "secrets.json"),
                sp.GetRequiredService<ISecretProtector>());
        });
        services.AddSingleton<INotificationService, WindowsNotificationService>();
        services.AddSingleton<INetworkPrivileges, WindowsNetworkPrivileges>();
        services.AddSingleton<IJavaRuntimeService, JavaRuntimeService>();
        return services;
    }
}
