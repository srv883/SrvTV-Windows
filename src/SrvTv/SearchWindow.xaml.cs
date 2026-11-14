using System.Windows;
using System.Windows.Input;
using SrvTv.Models;

namespace SrvTv;

public partial class SearchWindow : Window
{
    private const int MaxResults = 400;

    public SearchWindow()
    {
        InitializeComponent();
        if (Application.Current.MainWindow is { IsLoaded: true } main && !ReferenceEquals(main, this))
            Owner = main;
        UiTest.Place(this, 860, 720);
        UiTest.Attach(this, "search", (verb, arg) => { if (verb == "type") Query.Text = arg; });
        Loaded += (_, _) => Query.Focus();
        Refresh("");
    }

    public void Prefill(string query)
    {
        Query.Text = query;
        Query.SelectAll();
        Query.Focus();
        Refresh(query);
    }

    private void Refresh(string q)
    {
        var list = AppState.Instance.Repo.SearchChannels(q);
        Results.ItemsSource = list.Count > MaxResults ? list.GetRange(0, MaxResults) : list;
        if (Results.Items.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.ScrollIntoView(Results.SelectedItem);
        }
        Placeholder.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoResults.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultCount.Text = list.Count switch
        {
            0 => "",
            > MaxResults => $"Showing first {MaxResults} of {list.Count} channels",
            1 => "1 channel",
            _ => $"{list.Count} channels",
        };
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
        AppState.Instance.ReleaseParked();
        var cat = AppState.Instance.Repo.Categories
            .FirstOrDefault(c => c.Id == AppState.Instance.SelectedCategoryId);
        new PlayerWindow(ch, cat?.Id, cat?.Name).Show();
        Close();
    }

    // Typing stays in the box: arrows and paging move the highlighted
    // result and Enter plays it, so the mouse is never needed.
    private void StepResult(int delta)
    {
        var count = Results.Items.Count;
        if (count == 0) return;
        Results.SelectedIndex = Math.Clamp(Results.SelectedIndex + delta, 0, count - 1);
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var inBox = Query.IsKeyboardFocused || UiTest.On;
        if (e.Key == Key.Escape)
        {
            if (inBox && Query.Text.Length > 0) Query.Clear();
            else Close();
            e.Handled = true;
            return;
        }
        if (!inBox) return;
        switch (e.Key)
        {
            case Key.Down: StepResult(1); e.Handled = true; break;
            case Key.Up: StepResult(-1); e.Handled = true; break;
            case Key.PageDown: StepResult(8); e.Handled = true; break;
            case Key.PageUp: StepResult(-8); e.Handled = true; break;
            case Key.Enter: PlaySelected(); e.Handled = true; break;
        }
    }
}
