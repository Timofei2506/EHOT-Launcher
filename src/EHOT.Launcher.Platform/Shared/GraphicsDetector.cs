using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using EHOT.Launcher.Core.Diagnostics;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.Platform.Shared;

/// <summary>Класс видеокарты — от него зависит, какие моды оптимизации имеют смысл.</summary>
public enum GraphicsTier
{
    /// <summary>Определить не удалось: рекомендации не выдаём, чтобы не врать.</summary>
    Unknown = 0,

    /// <summary>Слабая/встроенная графика, мало видеопамяти.</summary>
    Low = 1,

    /// <summary>Средняя дискретная или современная встроенная.</summary>
    Medium = 2,

    /// <summary>Производительная дискретная.</summary>
    High = 3
}

/// <summary>Сведения о видеокарте, которые реально удалось прочитать из системы.</summary>
public sealed record GraphicsInfo(
    string Name,
    long VramBytes,
    GraphicsTier Tier,
    string Source,
    bool IsIntegrated)
{
    public static GraphicsInfo Unknown { get; } = new("Не определена", 0, GraphicsTier.Unknown, "нет данных", false);

    public string VramLabel => VramBytes <= 0
        ? "объём неизвестен"
        : (VramBytes / (1024d * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " ГБ";
}

public interface IGraphicsDetector
{
    Task<GraphicsInfo> DetectAsync(CancellationToken ct = default);
}

/// <summary>
/// Определение видеокарты без внешних утилит:
/// Windows — реестр (Win32_VideoController через wmic недоступен на новых сборках) и DirectX-ключи,
/// Linux — /sys/class/drm и lspci, macOS — system_profiler.
/// Если данных нет, честно возвращаем <see cref="GraphicsInfo.Unknown"/>.
/// </summary>
public sealed partial class GraphicsDetector(IPlatformInfo platform, IProcessRunner runner, ILogService log)
    : IGraphicsDetector
{
    private GraphicsInfo? _cached;

    public async Task<GraphicsInfo> DetectAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        try
        {
            _cached = platform.IsWindows
                ? await DetectWindowsAsync(ct).ConfigureAwait(false)
                : platform.IsMacOS
                    ? await DetectMacAsync(ct).ConfigureAwait(false)
                    : await DetectLinuxAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            log.Warn("GPU", "Не удалось определить видеокарту: " + ex.Message);
            _cached = GraphicsInfo.Unknown;
        }

        return _cached;
    }

    // --- Windows ---------------------------------------------------------

    private async Task<GraphicsInfo> DetectWindowsAsync(CancellationToken ct)
    {
        // Реестр: сведения о видеоадаптере лежат в HKLM\HARDWARE\DEVICEMAP\VIDEO и
        // в ключе DirectX; объём памяти точнее всего в HARDWARE\...\Video\*.
        var name = ReadRegistryString(@"HARDWARE\DEVICEMAP\VIDEO", "Device Description")
                   ?? await QueryWmiAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
        {
            return GraphicsInfo.Unknown;
        }

        var vram = ReadRegistryQword(@"SYSTEM\CurrentControlSet\Control\Video", "HardwareInformation.qwMemorySize")
                   ?? ReadRegistryQword(@"SYSTEM\CurrentControlSet\Control\Video", "HardwareInformation.MemorySize")
                   ?? 0;

        return Classify(name, vram, "реестр Windows");
    }

    private static string? ReadRegistryString(string path, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadRegistryQword(string path, string valueName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
            if (root is null)
            {
                return null;
            }

            // В ключе Video по подключениям: {GUID}\0000 — перебираем и берём максимум.
            long best = 0;
            foreach (var child in root.GetSubKeyNames())
            {
                using var connection = root.OpenSubKey(child);
                if (connection is null)
                {
                    continue;
                }

                foreach (var instance in connection.GetSubKeyNames())
                {
                    using var key = connection.OpenSubKey(instance);
                    var raw = key?.GetValue(valueName);
                    var bytes = raw switch
                    {
                        long l => l,
                        int i => i,
                        byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                        _ => 0
                    };
                    best = Math.Max(best, bytes);
                }
            }

            return best > 0 ? best : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<string?> QueryWmiAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(new ProcessRequest(
            "powershell",
            [
                "-NoProfile", "-NonInteractive", "-Command",
                "(Get-CimInstance Win32_VideoController | Select-Object -First 1 -ExpandProperty Name)"
            ]), ct).ConfigureAwait(false);

        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }

    // --- macOS -----------------------------------------------------------

    private async Task<GraphicsInfo> DetectMacAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(new ProcessRequest(
            "system_profiler",
            ["SPDisplaysDataType"]), ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return GraphicsInfo.Unknown;
        }

        var text = result.StandardOutput;
        var chip = Regex.Match(text, @"Chipset Model:\s*(.+)");
        var vram = Regex.Match(text, @"VRAM \(Total\):\s*(\d+)\s*(MB|GB)", RegexOptions.IgnoreCase);
        var name = chip.Success ? chip.Groups[1].Value.Trim() : "Apple GPU";

        long bytes = 0;
        if (vram.Success && long.TryParse(vram.Groups[1].Value, out var amount))
        {
            bytes = vram.Groups[2].Value.Equals("GB", StringComparison.OrdinalIgnoreCase)
                ? amount * 1024L * 1024 * 1024
                : amount * 1024L * 1024;
        }

        // У Apple Silicon память общая: VRAM не указан, но производительность высокая.
        var apple = name.Contains("Apple", StringComparison.OrdinalIgnoreCase);
        return Classify(name, bytes, "system_profiler", appleIntegrated: apple);
    }

    // --- Linux -----------------------------------------------------------

    private async Task<GraphicsInfo> DetectLinuxAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(new ProcessRequest("lspci", []), ct).ConfigureAwait(false);
        string? name = null;
        if (result.Succeeded)
        {
            var line = result.StandardOutput
                .Split('\n')
                .FirstOrDefault(l => l.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase)
                                     || l.Contains("3D controller", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                var colon = line.IndexOf(':');
                name = colon >= 0 ? line[(line.IndexOf(':', colon + 1) + 1)..].Trim() : line.Trim();
                name = Regex.Replace(name, @"\s*\(rev [0-9a-f]+\)\s*$", string.Empty, RegexOptions.IgnoreCase);
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // Фолбэк: /sys/class/drm/*/device — там есть идентификаторы, но не имя модели.
            name = TryReadSysDrm();
        }

        return string.IsNullOrWhiteSpace(name)
            ? GraphicsInfo.Unknown
            : Classify(name, 0, "lspci");
    }

    private static string? TryReadSysDrm()
    {
        try
        {
            if (!Directory.Exists("/sys/class/drm"))
            {
                return null;
            }

            foreach (var card in Directory.GetDirectories("/sys/class/drm", "card[0-9]*"))
            {
                var vendor = File.Exists(Path.Combine(card, "device", "vendor"))
                    ? File.ReadAllText(Path.Combine(card, "device", "vendor")).Trim()
                    : null;
                var device = File.Exists(Path.Combine(card, "device", "device"))
                    ? File.ReadAllText(Path.Combine(card, "device", "device")).Trim()
                    : null;
                if (!string.IsNullOrEmpty(vendor) && !string.IsNullOrEmpty(device))
                {
                    return $"GPU {vendor}:{device}";
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    // --- Классификация ---------------------------------------------------

    /// <summary>
    /// Класс видеокарты по модели и объёму памяти. Правила намеренно простые и объяснимые:
    /// встроенная графика Intel/AMD без своей памяти — низкий класс, дискретные карты
    /// с 4+ ГБ — средний, 8+ ГБ или свежие модели — высокий.
    /// </summary>
    public static GraphicsInfo Classify(string name, long vramBytes, string source, bool appleIntegrated = false)
    {
        var lower = name.ToLowerInvariant();

        var integrated = appleIntegrated
                         || lower.Contains("intel", StringComparison.Ordinal)
                         || lower.Contains("uhd graphics", StringComparison.Ordinal)
                         || lower.Contains("hd graphics", StringComparison.Ordinal)
                         || lower.Contains("iris", StringComparison.Ordinal)
                         || lower.Contains("vega", StringComparison.Ordinal)
                         || lower.Contains("radeon graphics", StringComparison.Ordinal)
                         || lower.Contains("integrated", StringComparison.Ordinal);

        // Номера моделей, по которым сразу видно уровень.
        var highEnd = Regex.IsMatch(lower,
            @"(rtx\s*(30|40|50)\d0|rx\s*(6|7|9)\d00|arc\s*a7|apple\s*m\d\s*(max|ultra)|radeon\s*pro\s*w\d)");
        var lowEnd = Regex.IsMatch(lower, @"(gt\s*7\d0|gt\s*10\d0|hd\s*graphics|uhd\s*graphics\s*(6|7)\d\d|microsoft basic)");

        // Apple Silicon (M1/M2/M3…) — это мощная графика с общей памятью, не «встроенная»
        // в смысле слабых Intel-адаптеров, поэтому сразу высокий класс.
        var tier = GraphicsTier.Medium;
        if (appleIntegrated && Regex.IsMatch(lower, @"apple\s*m\d"))
        {
            tier = GraphicsTier.High;
        }
        else if (highEnd || vramBytes >= 8L * 1024 * 1024 * 1024)
        {
            tier = GraphicsTier.High;
        }
        else if (lowEnd || (integrated && vramBytes < 2L * 1024 * 1024 * 1024) || vramBytes is > 0 and < 2L * 1024 * 1024 * 1024)
        {
            tier = GraphicsTier.Low;
        }
        else if (integrated && vramBytes == 0)
        {
            tier = GraphicsTier.Low;
        }

        return new GraphicsInfo(name.Trim(), vramBytes, tier, source, integrated);
    }
}
