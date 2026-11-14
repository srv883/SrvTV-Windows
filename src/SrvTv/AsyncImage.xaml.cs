using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SrvTv;

// Tiny async logo loader for list rows: downloads and decodes off the UI
// thread (a slow logo host can't freeze the window), caches per URL, and
// hides itself on failure so rows never show broken-image boxes.
public partial class AsyncImage : UserControl
{
    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(nameof(Url), typeof(string), typeof(AsyncImage),
            new PropertyMetadata("", (d, _) => ((AsyncImage)d).Reload()));

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> Cache = new();
    private static readonly SemaphoreSlim Gate = new(6);

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

    private async void Reload()
    {
        var url = Url;
        Fallback.Visibility = Visibility.Collapsed;
        Img.Source = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            Fallback.Visibility = Visibility.Visible;
            return;
        }
        if (Cache.Count > 2000) Cache.Clear();
        BitmapSource? img;
        try { img = await Cache.GetOrAdd(url, Load); }
        catch { img = null; }
        if (url != Url) return; // row was recycled for another channel meanwhile
        if (img != null) Img.Source = img;
        else Fallback.Visibility = Visibility.Visible;
    }

    private static Task<BitmapSource?> Load(string url) => Task.Run<BitmapSource?>(async () =>
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var uri = new Uri(url);
            byte[] bytes = uri.IsFile
                ? await File.ReadAllBytesAsync(uri.LocalPath).ConfigureAwait(false)
                : await Http.GetByteArrayAsync(uri).ConfigureAwait(false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.DecodePixelWidth = 120;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
        finally { Gate.Release(); }
    });
}
