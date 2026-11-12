using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using EHOT.Launcher.Core.Platform;

namespace EHOT.Launcher.UI.ViewModels;

/// <summary>Конвертеры для сетевых статусов и возможностей (единый визуальный язык).</summary>
public static class NetConverters
{
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34D399"));
    private static readonly IBrush Yellow = new SolidColorBrush(Color.Parse("#FBBF24"));
    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#64748B"));

    public static IValueConverter StatusBrush { get; } = new FuncValueConverter<bool, IBrush>(
        connected => connected ? Green : Gray);

    public static IValueConverter CapabilityBrush { get; } = new FuncValueConverter<CapabilityState, IBrush>(
        state => state switch
        {
            CapabilityState.Available => Green,
            CapabilityState.Degraded => Yellow,
            _ => Red
        });

    public static IValueConverter CapabilityStateLabel { get; } = new FuncValueConverter<CapabilityState, string>(
        state => state switch
        {
            CapabilityState.Available => "доступно",
            CapabilityState.Degraded => "с ограничениями",
            _ => "недоступно"
        });

    public static IValueConverter UuidSourceLabel { get; } = new FuncValueConverter<bool, string>(
        official => official ? "UUID официальный" : "UUID оффлайн");

    public static IValueConverter OfficialNickBrush { get; } = new FuncValueConverter<bool, IBrush>(
        official => official ? new SolidColorBrush(Color.Parse("#34D399")) : new SolidColorBrush(Color.Parse("#FBBF24")));

    public static IValueConverter TunnelLabel { get; } = new FuncValueConverter<bool, string>(
        available => available ? "Туннель доступен" : "Туннель недоступен — используется relay/прямое соединение");

    public static IValueConverter VerifiedLabel { get; } = new FuncValueConverter<bool, string>(
        verified => verified ? "подтверждён" : "не подтверждён");

    public static IValueConverter Speed { get; } = new FuncValueConverter<double, string>(
        bytesPerSecond => bytesPerSecond <= 0
            ? "—"
            : Converters.BytesToHumanConverter.Format((long)bytesPerSecond) + "/с");

    public static IValueConverter Percent { get; } = new FuncValueConverter<double, string>(
        fraction => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
}
