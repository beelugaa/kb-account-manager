using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BBManager.Core;

[ValueConversion(typeof(string), typeof(Brush))]
public class ColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#6B7080")); }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Version translucide d'une couleur (pour les fonds de pastilles).</summary>
public class ColorSoftBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(value as string ?? "#6B7080");
            return new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B));
        }
        catch { return Brushes.Transparent; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 hors ligne · 1 en jeu · 2 invalide · 3 occupé.</summary>
public class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is int i ? i switch { 1 => "GoodBrush", 2 => "BadBrush", 3 => "WarnBrush", _ => "FaintBrush" } : "FaintBrush";
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>Visible si la chaîne n'est pas vide.</summary>
public class StringVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 -> « Illimité » ; sinon « N Mo ».</summary>
public class RamLimitTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i > 0 ? $"{i} Mo" : "Auto";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Nom de groupe -> pinceau de la couleur du groupe.</summary>
public class GroupNameColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var g = BBManager.Services.AppState.Current?.Groups.FirstOrDefault(x => x.Name == value as string);
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(g?.Color ?? "#6B7080")); }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
