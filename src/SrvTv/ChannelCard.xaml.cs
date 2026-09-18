using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SrvTv.Models;

namespace SrvTv;

// 16:9 channel tile mirroring the Android card: hash gradient, centered
// logo (async), star + tiny language pill + category label.
public partial class ChannelCard : UserControl
{
    private static readonly (Color, Color)[] Palettes =
    {
        (C(0xFF1E88E5), C(0xFF8E24AA)), (C(0xFF00ACC1), C(0xFF43A047)),
        (C(0xFFF4511E), C(0xFFD81B60)), (C(0xFF5E35B1), C(0xFF00897B)),
        (C(0xFF7E57C2), C(0xFFEC407A)), (C(0xFF039BE5), C(0xFF66BB6A)),
        (C(0xFFE53935), C(0xFF6A1B9A)), (C(0xFF1A237E), C(0xFF26A69A)),
    };

    private string _boundId = "";

    public ChannelCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        Logo.ImageFailed += (_, _) => ShowFallback();
    }

    private void Bind()
    {
        if (DataContext is not Channel ch) return;
        _boundId = ch.Id;
        FallbackName.Visibility = Visibility.Collapsed;
        Star.Visibility = ch.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        Lang.Text = ShortLang(ch.Language);
        Category.Text = ShortCategory(ch.Category);
        FallbackName.Text = ch.Name;

        var (a, b) = Palettes[(int)((uint)Data.M3UParser.JavaHash(ch.Name) % (uint)Palettes.Length)];
        Card.Background = new LinearGradientBrush(
            Shade(a, 0.62f), Shade(b, 0.26f), new Point(0, 0), new Point(1, 1));

        if (ch.Logo.Length == 0) { ShowFallback(); return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(ch.Logo);
            bmp.DecodePixelWidth = 400;
            bmp.CacheOption = BitmapCacheOption.OnDemand;
            bmp.EndInit();
            Logo.Source = bmp;
        }
        catch { ShowFallback(); }
    }

    private void ShowFallback()
    {
        Logo.Source = null;
        FallbackName.Visibility = Visibility.Visible;
    }

    private static Color C(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static Color Shade(Color c, float f) => Color.FromRgb(
        (byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));

    private static string ShortLang(string language) => language.ToLowerInvariant().Trim() switch
    {
        "english" => "Eng",
        "hindi" => "हिं",
        "odia" or "oriya" => "Odia",
        _ => "Others",
    };

    private static string ShortCategory(string category)
    {
        var first = category.Split(';', ',').FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(first) ? "General" : first;
    }
}
