using System.Diagnostics;

namespace EHOT.Launcher.UI.Services;

/// <summary>Единые честные действия с файловой системой из UI: открыть папку / файл / показать файл.</summary>
public static class FileSystemActions
{
    /// <summary>Открыть папку в файловом менеджере ОС. false — если папки нет или ОС не дала открыть.</summary>
    public static bool OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Start("explorer.exe", $"\"{path}\"", useShellExecute: true);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Start("open", $"\"{path}\"", useShellExecute: false);
            }

            return Start("xdg-open", $"\"{path}\"", useShellExecute: true);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Показать файл в проводнике (на Linux открывается содержащая папка).</summary>
    public static bool RevealFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                return Start("explorer.exe", $"/select,\"{filePath}\"", useShellExecute: true);
            }

            if (OperatingSystem.IsMacOS())
            {
                return Start("open", $"-R \"{filePath}\"", useShellExecute: false);
            }

            return OpenFolder(Path.GetDirectoryName(filePath) ?? filePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Открыть файл приложением по умолчанию.</summary>
    public static bool OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return Start(filePath, null, useShellExecute: true);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool Start(string fileName, string? arguments, bool useShellExecute)
    {
        var startInfo = arguments is null
            ? new ProcessStartInfo(fileName) { UseShellExecute = useShellExecute }
            : new ProcessStartInfo(fileName, arguments) { UseShellExecute = useShellExecute };

        Process.Start(startInfo)?.Dispose();
        return true;
    }
}
