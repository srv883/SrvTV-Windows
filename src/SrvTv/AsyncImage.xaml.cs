using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SrvTv;

// Tiny async logo loader for list rows: hides itself on failure so rows
// never show broken-image boxes.
public partial class AsyncImage : UserControl
{
    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(nameof(Url), typeof(string), typeof(AsyncImage),
            new PropertyMetadata("", (d, _) => ((AsyncImage)d).Reload()));

    public string Url
    {
        get => (string)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public AsyncImage()
    {
        InitializeComponent();
        Img.ImageFailed += (_, _) => Fallback.Visibility = Visibility.Visible;
    }

    private void Reload()
    {
        Fallback.Visibility = Visibility.Collapsed;
        Img.Source = null;
        if (string.IsNullOrWhiteSpace(Url)) { Fallback.Visibility = Visibility.Visible; return; }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Url);
            bmp.DecodePixelWidth = 120;
            bmp.CacheOption = BitmapCacheOption.OnDemand;
            bmp.EndInit();
            Img.Source = bmp;
        }
        catch { Fallback.Visibility = Visibility.Visible; }
    }
}
