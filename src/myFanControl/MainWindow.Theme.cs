using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace myFanControl;

public partial class MainWindow
{
    private const string DefaultThemeColor = "#0F766E";
    private string _themeColor = DefaultThemeColor;

    private static Color ParseThemeColor(string value)
    {
        if (value.Length != 7 || value[0] != '#' ||
            !int.TryParse(value.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
            throw new ArgumentException("主题色需为 #RRGGBB。");
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel) { double v = channel / 255d; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private static Color Blend(Color color, Color target, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R * (1 - amount) + target.R * amount),
        (byte)Math.Round(color.G * (1 - amount) + target.G * amount),
        (byte)Math.Round(color.B * (1 - amount) + target.B * amount));

    private void ApplyThemeColor()
    {
        Color accent = ParseThemeColor(_themeColor), ink = accent;
        while (1.05 / (Luminance(ink) + 0.05) < 7) ink = Blend(ink, Colors.Black, 0.12);
        Color foreground = Luminance(accent) > 0.179 ? Colors.Black : Colors.White;
        foreach (var (key, color) in new[] {
            ("Accent", accent), ("AccentInk", ink), ("AccentForeground", foreground),
            ("AccentHover", Blend(accent, foreground == Colors.Black ? Colors.White : Colors.Black, 0.12)),
            ("AccentSoft", Blend(accent, Colors.White, 0.92)), ("Canvas", Blend(accent, Colors.White, 0.97)),
            ("PlotSurface", Blend(accent, Colors.White, 0.985)), ("Line", Blend(accent, Colors.White, 0.88)) })
            Resources[key] = new SolidColorBrush(color);
        ThemeColorButton.ToolTip = _themeColor;
        CurveGraph.InvalidateVisual();
    }

    private bool SaveThemeColor(string value)
    {
        _ = ParseThemeColor(value);
        string previous = _themeColor;
        _themeColor = value.ToUpperInvariant();
        if (!SaveControlSettings()) { _themeColor = previous; return false; }
        ApplyThemeColor();
        return true;
    }

    private void OnThemeColorClick(object sender, RoutedEventArgs e)
    {
        Color current = ParseThemeColor(_themeColor);
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B) };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SaveThemeColor($"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}");
    }

    private void OnResetThemeColorClick(object sender, RoutedEventArgs e) => SaveThemeColor(DefaultThemeColor);
}
