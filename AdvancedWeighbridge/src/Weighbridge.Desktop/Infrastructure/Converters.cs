using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Weighbridge.Core.Units;
using Weighbridge.Core.Weighing;

namespace Weighbridge.Desktop.Infrastructure;

/// <summary>Formats integer grams as a weight in the configured display unit.</summary>
public sealed class GramsConverter : IValueConverter
{
    public static WeightDisplayOptions Display { get; set; } = WeightDisplayOptions.DefaultKg;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long g => Display.Format(Weight.FromGrams(g)),
        int g => Display.Format(Weight.FromGrams(g)),
        Weight w => Display.Format(w),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>UTC timestamps shown in local time.</summary>
public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "yyyy-MM-dd HH:mm";
        return value switch
        {
            DateTimeOffset d => d.ToLocalTime().ToString(format, CultureInfo.InvariantCulture),
            DateTime d => d.ToLocalTime().ToString(format, CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch { bool b => b, null => false, string s => s.Length > 0, _ => true };
        return on ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Status names (Pending/Completed/Cancelled, modes) translated through the string table.</summary>
public sealed class LocalizedEnumConverter : IValueConverter
{
    public string Prefix { get; set; } = "tx.";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? string.Empty : Loc.T(Prefix + value, value.ToString());

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class StateBrushes
{
    public static Brush For(DisplayColor color) => (Brush)System.Windows.Application.Current.FindResource(color switch
    {
        DisplayColor.Green => "Brush.StateGreen",
        DisplayColor.Yellow => "Brush.StateYellow",
        DisplayColor.Red => "Brush.StateRed",
        DisplayColor.Blue => "Brush.StateBlue",
        _ => "Brush.StateGrey",
    });

    /// <summary>A symbol for each state so that the state is never shown by colour alone.</summary>
    public static string Symbol(DisplayColor color) => color switch
    {
        DisplayColor.Green => "✔",
        DisplayColor.Yellow => "⏳",
        DisplayColor.Red => "✖",
        DisplayColor.Blue => "🔒",
        _ => "⏻",
    };
}
