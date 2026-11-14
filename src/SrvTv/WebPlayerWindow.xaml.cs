using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SrvTv.Models;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SrvTv;

public partial class WebPlayerWindow : Window
{
    private readonly Channel? _channel;
    private bool _closing;

    public WebPlayerWindow(Channel channel)
    {
        _channel = channel;
        InitializeComponent();
        TitleText.Text = channel.Name;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SrvTv", "WebView2");
            Directory.CreateDirectory(userData);

            var options = new CoreWebView2EnvironmentOptions(
                "--autoplay-policy=no-user-gesture-required");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
            await WebView.EnsureCoreWebView2Async(environment);

            WebView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(e.Uri))
                    WebView.CoreWebView2.Navigate(e.Uri);
            };
            WebView.CoreWebView2.NavigationStarting += (_, e) =>
            {
                TitleText.Text = _channel?.Name ?? "Srv TV Web Player";
            };

            if (_channel != null && !string.IsNullOrWhiteSpace(_channel.Url))
                WebView.CoreWebView2.Navigate(_channel.Url);
        }
        catch (Exception ex)
        {
            Log.Write("web player init failed: " + ex);
            StatusPanel.Visibility = Visibility.Visible;
            StatusText.Text = "Embedded browser unavailable. Opening the official site externally instead.";
            try
            {
                if (_channel != null)
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _channel.Url,
                        UseShellExecute = true,
                    });
            }
            catch { }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Cleanup()
    {
        if (_closing) return;
        _closing = true;
        try { WebView.Dispose(); } catch { }
    }
}
