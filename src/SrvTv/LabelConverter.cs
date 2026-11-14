using System.Globalization;
using System.Windows.Data;

namespace SrvTv;

// Display-only tidy-up of playlist labels: "Movies;Series" -> "Movies · Series",
// "english" -> "English". The underlying channel data is untouched.
public sealed class LabelConverter : IValueConverter
{
    public static string Tidy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Length > 0 && char.IsLower(p[0])
                ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(p) : p);
        return string.Join(" · ", parts);
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Tidy(value as string);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
