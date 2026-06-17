namespace SnapStakMobile.Converters;

/// <summary>
/// Returns true when the bound string is NOT null or empty.
/// Used to show/hide the URL label in the app list card.
/// </summary>
public class StringNotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => !string.IsNullOrEmpty(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// Converts a "#RRGGBB" hex string to a MAUI Color.
/// When ConverterParameter is "20" it returns the colour at 20% opacity (for badge backgrounds).
/// </summary>
public class HexToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string hex) return Colors.Transparent;

        try
        {
            var color = Color.FromArgb(hex);

            if (parameter is string opacityStr && float.TryParse(opacityStr, out float opacity))
                return color.WithAlpha(opacity / 100f);

            return color;
        }
        catch
        {
            return Colors.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}
