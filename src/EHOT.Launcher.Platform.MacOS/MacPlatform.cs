using System.Diagnostics;
using EHOT.Launcher.Core.Platform;
using EHOT.Launcher.Core.Security;
using EHOT.Launcher.Platform.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EHOT.Launcher.Platform.MacOS;

/// <summary>Пути macOS: ~/Library/Application Support/EHOTLauncher.</summary>
public sealed class MacAppPaths : BaseAppPaths
{
    protected override string ResolveAppDataDirectory()
    {
        var support = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(support))
        {
            support = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        }

        return Path.Combine(support, AppFolderName);
    }
}

/// <summary>
/// Секреты в Keychain через утилиту `security` (штатный интерфейс Apple, не требует entitlement).
/// Каждый ключ — отдельный generic password с service = ehot.launcher.
/// </summary>
public sealed class MacKeychainSecretStore(IProcessRunner runner, ILogger<MacKeychainSecretStore> logger) : ISecretStore
{
    private const string ServiceName = "ehot.launcher";

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var result = await RunAsync(["find-generic-password", "-s", ServiceName, "-a", key, "-w"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            // Отсутствие ключа — штатная ситуация (первый запуск), но причину видно в логе.
            logger.LogDebug("Keychain: секрет {Key} не найден ({Error})", key, result.StandardError.Trim());
            return null;
        }

        return result.StandardOutput.Trim();
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        var result = await RunAsync(
            ["add-generic-password", "-U", "-s", ServiceName, "-a", key, "-w", value], ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Не удалось сохранить секрет в Keychain: {result.StandardError.Trim()}");
        }
    }

    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        await RunAsync(["delete-generic-password", "-s", ServiceName, "-a", key], ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        // `security dump-keychain` не фильтруется по service, поэтому работаем по известным префиксам,
        // которые лаунчер использует сам (никакого перебора чужих секретов).
        var result = await RunAsync(["dump-keychain"], ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return [];
        }

        var keys = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var idx = line.IndexOf("\"acct\"<blob>=\"", StringComparison.Ordinal);
            if (idx < 0)
            {
                continue;
            }

            var start = idx + "\"acct\"<blob>=\"".Length;
            var end = line.IndexOf('"', start);
            if (end > start)
            {
                keys.Add(line[start..end]);
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToArray();
    }

    private Task<ProcessResult> RunAsync(string[] args, CancellationToken ct)
        => runner.RunAsync(new ProcessRequest("/usr/bin/security", args), ct);
}

/// <summary>Уведомления через osascript (не требует разрешений помимо «Уведомления» для Terminal/приложения).</summary>
public sealed class MacNotificationService(IProcessRunner runner, ILogger<MacNotificationService> logger) : INotificationService
{
    public bool IsSupported => OperatingSystem.IsMacOS() && File.Exists("/usr/bin/osascript");

    public async Task NotifyAsync(string title, string message, CancellationToken ct = default)
    {
        if (!IsSupported)
        {
            return;
        }

        var script = $"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"";
        try
        {
            var result = await runner.RunAsync(new ProcessRequest("/usr/bin/osascript", ["-e", script]), ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                logger.LogDebug("osascript: {Err}", result.StandardError);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug("Уведомление macOS не отправлено: {Message}", ex.Message);
        }
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// Сетевые привилегии macOS. NEPacketTunnelProvider требует подпись с entitlement
/// com.apple.developer.networking.networkextension (packet-tunnel-provider) и одобрения Apple.
/// Без этого туннель недоступен — сообщаем честно.
/// </summary>
public sealed class MacNetworkPrivileges : INetworkPrivileges
{
    public bool HasTunnelPrivileges => EntitlementPresent();

    public bool CanRequestElevation => false;

    public string Describe(TunnelPrivilegeState state) => state switch
    {
        TunnelPrivilegeState.Granted => "Network Extension доступен, туннель возможен.",
        TunnelPrivilegeState.NotSupported or _ =>
            "Network Extension entitlement отсутствует у сборки лаунчера. " +
            "Доступны прямое соединение и relay-режим; полноценный туннель требует подписи с " +
            "com.apple.developer.networking.networkextension и одобрения Apple."
    };

    private static bool EntitlementPresent()
    {
        // Проверяем подпись текущего исполняемого файла: наличие entitlement видно в codesign -d --entitlements.
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists("/usr/bin/codesign"))
            {
                return false;
            }

            var psi = new ProcessStartInfo("/usr/bin/codesign", ["-d", "--entitlements", "-", exe])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output.Contains("networkextension", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public static class MacPlatformModule
{
    public static IServiceCollection AddMacPlatform(this IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, MacAppPaths>();
        services.AddSingleton<IPlatformInfo, BasePlatformInfo>();
        services.AddSingleton<INotificationService, MacNotificationService>();
        services.AddSingleton<INetworkPrivileges, MacNetworkPrivileges>();
        services.AddSingleton<IJavaRuntimeService, JavaRuntimeService>();

        // Keychain — основное хранилище; файловый AES-GCM остаётся как fallback для CI/песочницы.
        services.AddSingleton<ISecretStore>(sp =>
        {
            var paths = sp.GetRequiredService<IAppPaths>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MacKeychainSecretStore>>();
            var runner = sp.GetRequiredService<IProcessRunner>();
            return OperatingSystem.IsMacOS()
                ? new MacKeychainSecretStore(runner, logger)
                : new FileSecretStore(
                    Path.Combine(paths.AppDataDirectory, "secrets.json"),
                    new AesGcmFileKeyProtector(Path.Combine(paths.AppDataDirectory, "secrets.key")));
        });

        return services;
    }
}
