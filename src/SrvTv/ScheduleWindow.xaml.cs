using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SrvTv;

public sealed class ScheduleRow
{
    public string Time { get; set; } = "";
    public string Title { get; set; } = "";
    public Brush TitleColor { get; set; } = Brushes.White;
    public string Desc { get; set; } = "";
    public bool HasDesc => Desc.Length > 0;
}

// Full day schedule for one channel (arrows scroll, Esc closes).
public partial class ScheduleWindow : Window
{
    public ScheduleWindow(string channelId, string channelName)
    {
        InitializeComponent();
        ChanTitle.Text = channelName;
        DateLine.Text = DateTime.Now.ToString("dddd, d MMMM");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = AppState.Instance.Epg.Schedule(channelId)
            .Select(p => new ScheduleRow
            {
                Time = p.RangeLabel(),
                Title = (p.StartMs <= now && now < p.StopMs ? "▶ " : "") + p.Title,
                TitleColor = (p.StartMs <= now && now < p.StopMs)
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x00)) : Brushes.White,
                Desc = p.Desc,
            }).ToList();
        if (rows.Count == 0)
        {
            Empty.Visibility = Visibility.Visible;
            Rows.Visibility = Visibility.Collapsed;
        }
        else
        {
            Rows.ItemsSource = rows;
            var pos = rows.FindIndex(r => r.Title.StartsWith("▶ "));
            Rows.SelectedIndex = Math.Max(0, pos);
            Rows.ScrollIntoView(Rows.SelectedItem);
            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (Rows.ItemContainerGenerator.ContainerFromIndex(Rows.SelectedIndex)
                    is System.Windows.Controls.ListBoxItem it) it.Focus();
            });
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
