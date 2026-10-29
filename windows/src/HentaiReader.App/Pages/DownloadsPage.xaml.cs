using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Data;
using HentaiReader.Host.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace HentaiReader.App.Pages;

/// <summary>
/// The save queue (US5, library-ui.md "Downloads"): state, reason and waiting time of a stop,
/// progress, Pause / Resume / Cancel / Open, Clear finished.
/// </summary>
public sealed partial class DownloadsPage : Page
{
    /// <summary>One row, created once per download and updated in place (no flicker, no re-animation).</summary>
    private sealed class RowView
    {
        public required Grid Root { get; init; }
        public required TextBlock Title { get; init; }
        public required TextBlock Status { get; init; }
        public required ProgressBar Progress { get; init; }
        public required StackPanel Buttons { get; init; }
        public DownloadState? ButtonsFor { get; set; }
        public int Done { get; set; } = -1;
        public DownloadState? State { get; set; }
    }

    private readonly Dictionary<long, RowView> _rows = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _loading;
    private bool _again;

    public DownloadsPage()
    {
        InitializeComponent();
        TitleText.Text = Strings.Get("Nav_Downloads");
        ClearDoneButton.Content = Strings.Get("Downloads_ClearDone");
        EmptyText.Text = Strings.Get("Downloads_Empty");
        // Downloads report every page (several per second): changes are gathered and shown at most 4 times a second.
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            _ = ReloadAsync();
        };
        Loaded += (_, _) => SaveTracker.Changed += OnChanged;
        Unloaded += (_, _) =>
        {
            SaveTracker.Changed -= OnChanged;
            _refresh.Stop();
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ReloadAsync();

    private void OnChanged(GalleryKey key) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_refresh.IsEnabled) _refresh.Start();
    });

    private async Task ReloadAsync()
    {
        // One reload at a time; a change arriving meanwhile is picked up by one more pass.
        if (_loading)
        {
            _again = true;
            return;
        }
        _loading = true;
        try
        {
            do
            {
                _again = false;
                var rows = await (await AppServices.Library).Downloads.ListAsync();
                Apply(rows);
            }
            while (_again);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(IReadOnlyList<DownloadRow> rows)
    {
        foreach (var gone in _rows.Keys.Where(id => rows.All(r => r.Id != id)).ToList())
        {
            DownloadList.Items.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (!_rows.TryGetValue(row.Id, out var view))
            {
                view = Create(row);
                _rows[row.Id] = view;
                DownloadList.Items.Insert(Math.Min(i, DownloadList.Items.Count), view.Root);
            }
            Update(view, row);
        }
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearDoneButton.IsEnabled = rows.Any(r => r.State == DownloadState.Done);
    }

    private static RowView Create(DownloadRow row)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 8, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        var title = new TextBlock { Text = row.Title, TextTrimming = TextTrimming.CharacterEllipsis };
        var status = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, row.PageCount), Margin = new Thickness(0, 4, 0, 0) };
        text.Children.Add(title);
        text.Children.Add(status);
        text.Children.Add(progress);
        grid.Children.Add(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        AutomationProperties.SetName(grid, row.Title);
        return new RowView { Root = grid, Title = title, Status = status, Progress = progress, Buttons = buttons };
    }

    private void Update(RowView view, DownloadRow row)
    {
        // Pages only ever add up; an older report arriving late must not move the bar back.
        var done = view.State == row.State ? Math.Max(view.Done, row.PagesDone) : row.PagesDone;
        view.Done = done;
        view.State = row.State;
        var source = MainWindow.Instance?.SourceNames.GetValueOrDefault(row.Key.SourceId) ?? row.Key.SourceId;
        view.Status.Text = $"{source} · {StateText(row with { PagesDone = done })}";
        view.Progress.Maximum = Math.Max(1, row.PageCount);
        view.Progress.Value = done;
        view.Progress.Visibility = row.State == DownloadState.Done ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetItemStatus(view.Root, $"{row.State} {done}/{row.PageCount}");
        if (view.ButtonsFor != row.State)
        {
            view.ButtonsFor = row.State;
            Buttons(view.Buttons, row);
        }
    }

    private void Buttons(StackPanel panel, DownloadRow row)
    {
        panel.Children.Clear();
        switch (row.State)
        {
            case DownloadState.Queued or DownloadState.Downloading or DownloadState.Assembling:
                panel.Children.Add(Action(Symbol.Pause, "Downloads_Pause", async () => await AppServices.Downloads!.PauseAsync(row.Id)));
                break;
            case DownloadState.Paused or DownloadState.Limited or DownloadState.Failed:
                panel.Children.Add(Action(Symbol.Play, "Downloads_Resume", async () => await AppServices.Downloads!.ResumeAsync(row.Id)));
                break;
            case DownloadState.Done:
                panel.Children.Add(Action(Symbol.OpenFile, "Downloads_Open", () => OpenAsync(row)));
                break;
        }
        if (row.State != DownloadState.Done)
            panel.Children.Add(Action(Symbol.Cancel, "Save_Cancel", async () => await AppServices.Downloads!.CancelAsync(row.Id)));
    }

    private static Button Action(Symbol symbol, string key, Func<Task> run)
    {
        var button = new Button { Content = new SymbolIcon(symbol) };
        var text = Strings.Get(key);
        ToolTipService.SetToolTip(button, text);
        AutomationProperties.SetName(button, text);
        button.Click += async (_, _) =>
        {
            if (AppServices.Downloads is null) return;
            button.IsEnabled = false;
            await run();
        };
        return button;
    }

    private static string StateText(DownloadRow row) => row.State switch
    {
        DownloadState.Queued => Strings.Get("Save_Queued"),
        DownloadState.Downloading or DownloadState.Assembling => Strings.Format("Save_Saving", row.PagesDone, row.PageCount),
        DownloadState.Paused => Strings.Format("Downloads_Paused", row.PagesDone, row.PageCount),
        DownloadState.Limited => (row.ErrorKind == SourceErrorKind.LoginRequired ? Strings.ErrorTitle("LoginRequired") : Strings.ErrorTitle("RateLimited"))
            + (row.RetryAfter is { } at && at > DateTimeOffset.UtcNow ? " · " + Strings.Format("Error_RateLimited_After", Math.Max(1, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalMinutes))) : ""),
        DownloadState.Failed => Strings.Format("Downloads_Failed", row.ErrorMessage is { Length: > 0 } m ? m : Strings.ErrorTitle("Network")),
        _ => Strings.Plural("Gallery_PageCount", row.PageCount),
    };

    private async Task OpenAsync(DownloadRow row)
    {
        var library = await AppServices.Library;
        if (await library.FindSavedAsync(row.Key) is not { } saved) return;
        var progress = await library.Progress.GetAsync(row.Key);
        Frame.Navigate(typeof(ReaderPage), ReaderPage.Args.Offline(saved, progress is { Finished: false } p ? p.Page : 0, progress is { Finished: false } q ? q.Offset : 0));
    }

    private async void ClearDoneButton_Click(object sender, RoutedEventArgs e)
    {
        await (await AppServices.Library).Downloads.DeleteDoneAsync();
        await ReloadAsync();
    }
}
