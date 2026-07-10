using System.Globalization;
using System.Windows.Data;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;

namespace CrabRuntimeProbe.Dashboard;

public sealed class StatusBrushConverter : IValueConverter
{
    public MediaBrush Green { get; set; } = MediaBrushes.LimeGreen;
    public MediaBrush Yellow { get; set; } = MediaBrushes.Goldenrod;
    public MediaBrush Red { get; set; } = MediaBrushes.IndianRed;
    public MediaBrush Blue { get; set; } = MediaBrushes.CornflowerBlue;
    public MediaBrush Gray { get; set; } = MediaBrushes.Gray;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolean)
        {
            if (string.Equals(parameter?.ToString(), "alert", StringComparison.OrdinalIgnoreCase))
                return boolean ? Red : Green;
            return string.Equals(parameter?.ToString(), "stale", StringComparison.OrdinalIgnoreCase)
                ? boolean ? Red : Green
                : boolean ? Green : Red;
        }
        var text = value?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
        if (Contains(text, "crash", "dirty", "error", "failed", "unsafe")) return Red;
        if (Contains(text, "partial", "stale", "warning", "needs", "blocked")) return Yellow;
        if (Contains(text, "inprogress", "in-progress", "observing", "monitoring", "loaded", "running")) return Blue;
        if (Contains(text, "confirmed", "complete", "healthy", "clean", "stable", "safe")) return Green;
        return Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static bool Contains(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}
