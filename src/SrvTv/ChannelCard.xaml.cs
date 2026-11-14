using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SrvTv.Models;

namespace SrvTv;

// 16:9 channel tile mirroring the Android card: hash gradient, centered
// logo (async), star + tiny language pill + category label.
public partial class ChannelCard : UserControl
{
    public static readonly RoutedEvent PlayRequestedEvent = EventManager.RegisterRoutedEvent(
        "PlayRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ChannelCard));
    public static readonly RoutedEvent FavoriteToggledEvent = EventManager.RegisterRoutedEvent(
        "FavoriteToggled", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ChannelCard));

    public event RoutedEventHandler PlayRequested
    {
        add { AddHandler(PlayRequestedEvent, value); }
        remove { RemoveHandler(PlayRequestedEvent, value); }
    }
    public event RoutedEventHandler FavoriteToggled
    {
        add { AddHandler(FavoriteToggledEvent, value); }
        remove { RemoveHandler(FavoriteToggledEvent, value); }
    }

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
        MouseEnter += (_, _) => ShowHover(true);
        MouseLeave += (_, _) => ShowHover(false);
        Card.ToolTip = null;
    }

    private void Bind()
    {
        if (DataContext is not Channel ch) return;
        _boundId = ch.Id;
        FallbackName.Visibility = Visibility.Collapsed;
        RefreshFavVisual(ch.IsFavorite);
        Lang.Text = ShortLang(ch.Language);
        Category.Text = ShortCategory(ch.Category);
        FallbackName.Text = ch.Name;
        NowChip.Visibility = (AppState.Instance.LastOpenedChannelId ?? "") == ch.Id
            ? Visibility.Visible : Visibility.Collapsed;
        OffChip.Visibility = ch.IsWorking ? Visibility.Collapsed : Visibility.Visible;

        // Official-app shortcut: no star or language, blurb instead of category.
        var shortcut = Data.OfficialApps.IsShortcut(ch);
        Star.Visibility = shortcut ? Visibility.Collapsed : Visibility.Visible;
        LangPill.Visibility = shortcut ? Visibility.Collapsed : Visibility.Visible;
        if (CtxPlay != null) CtxPlay.Header = shortcut ? "Open" : "Watch";
        if (CtxFav != null) CtxFav.Visibility = shortcut ? Visibility.Collapsed : Visibility.Visible;
        if (shortcut) Category.Text = ch.Category;

        var name = ch.Name;
        var detail = ch.Category;
        if (ch.Language.Length > 0) detail += "  •  " + ch.Language;
        if (ch.IsHD) detail += "  •  HD";
        detail += ch.IsWorking ? "" : "  •  offline";
        if (shortcut) detail += "\nOpens inside Srv TV (sign in once)";
        Card.ToolTip = $"{name}\n{detail}";

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

    // Re-read the playing channel without touching the rest of the card
    // (used when focus returns from a PlayerWindow).
    internal void RefreshNowChip()
    {
        if (DataContext is not Channel ch) return;
        NowChip.Visibility = (AppState.Instance.LastOpenedChannelId ?? "") == ch.Id
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHover(bool on)
    {
        if (on)
        {
            CardScale.ScaleX = 1.04;
            CardScale.ScaleY = 1.04;
            Glow.Opacity = 1;
            if (FallbackName.Visibility == Visibility.Visible && Logo.Source == null)
                PlayChip.Visibility = Visibility.Visible;
        }
        else
        {
            CardScale.ScaleX = 1;
            CardScale.ScaleY = 1;
            Glow.Opacity = 0;
            PlayChip.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not Channel) return;
        if (e.ClickCount != 1) return;
        var hit = e.OriginalSource as DependencyObject;
        while (hit != null)
        {
            if (hit == Star) return; // star clicks toggle favorites, never play
            hit = hit is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(hit) : null;
        }
        RaiseEvent(new RoutedEventArgs(PlayRequestedEvent, this));
        e.Handled = true;
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Channel ch) return;
        var repo = AppState.Instance.Repo;
        repo.ToggleFavorite(ch.Id);
        repo.RefreshFavorites();
        RefreshFavVisual(repo.IsFavorite(ch.Id));
        RaiseEvent(new RoutedEventArgs(FavoriteToggledEvent, this));
        e.Handled = true;
    }

    // ----- card context menu -----

    private void OnCtxPlay(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(PlayRequestedEvent, this));
    }

    private void OnCtxFav(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Channel ch || Data.OfficialApps.IsShortcut(ch)) return;
        var repo = AppState.Instance.Repo;
        repo.ToggleFavorite(ch.Id);
        repo.RefreshFavorites();
        RefreshFavVisual(repo.IsFavorite(ch.Id));
        RaiseEvent(new RoutedEventArgs(FavoriteToggledEvent, this));
    }

    private void OnCtxCopy(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Channel ch) return;
        try { System.Windows.Clipboard.SetText(ch.Url); } catch { }
    }

    private void OnCtxSearch(object sender, RoutedEventArgs e)
    {
        if (DataContext is not Channel ch) return;
        var win = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        win?.OpenSearch(ch.Name);
    }

    private void RefreshFavVisual(bool isFav)
    {
        Star.Content = isFav ? "★" : "☆";
        Star.Foreground = new SolidColorBrush(isFav ? C(0xFFFFD600) : C(0x80FFFFFF));
        if (CtxFav != null) CtxFav.Header = isFav ? "Remove from Favorites" : "Add to Favorites";
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