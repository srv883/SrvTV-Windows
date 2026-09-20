using System.Windows;
using System.Windows.Input;
using SrvTv.Models;

namespace SrvTv;

public partial class SearchWindow : Window
{
    public SearchWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Query.Focus();
        Refresh("");
    }

    private void Refresh(string q)
    {
        var list = AppState.Instance.Repo.SearchChannels(q);
        Results.ItemsSource = list.Count > 400 ? list.GetRange(0, 400) : list;
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Refresh(Query.Text);

    private void OnPick(object sender, RoutedEventArgs e) => PlaySelected();

    private void OnResultsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { PlaySelected(); e.Handled = true; }
    }

    private void PlaySelected()
    {
        if (Results.SelectedItem is not Channel ch) return;
        AppState.Instance.LastOpenedChannelId = ch.Id;
        var cat = AppState.Instance.Repo.Categories
            .FirstOrDefault(c => c.Id == AppState.Instance.SelectedCategoryId);
        new PlayerWindow(ch, cat?.Id, cat?.Name).Show();
        Close();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
