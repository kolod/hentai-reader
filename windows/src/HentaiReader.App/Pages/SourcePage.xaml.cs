using System.ComponentModel;
using System.Text.Json.Nodes;
using HentaiReader.App.Controls;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace HentaiReader.App.Pages;

/// <summary>Source front feed and search with declared filters and endless results (US2).</summary>
public sealed partial class SourcePage : Page
{
    /// <summary>Navigation parameter; Query starts a search for it (tag clicks, US3-3).</summary>
    public sealed record Args(SourceEntry Source, string? Query);

    private SearchViewModel? _vm;
    private SourceEntry? _source;

    public SourcePage()
    {
        InitializeComponent();
        QueryBox.PlaceholderText = Strings.Get("Search_Placeholder");
        SearchButton.Content = Strings.Get("Search_Button");
        FiltersButton.Content = Strings.Get("Search_Filters");
        ResetButton.Content = Strings.Get("Search_ResetFilters");
        RetryButton.Content = Strings.Get("Search_Retry");
        CopyDetailsButton.Content = Strings.Get("Search_CopyDetails");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is not Args args) return;
        var newSource = _source?.Id != args.Source.Id;
        if (newSource)
        {
            if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged;
            _source = args.Source;
            _vm = new SearchViewModel(args.Source);
            _vm.PropertyChanged += Vm_PropertyChanged;
            FilterHost.Content = new FilterPanel(_vm.Filters);
            ResultsGrid.ItemsSource = _vm.Results;
        }
        if (args.Query is not null)
        {
            QueryBox.Text = args.Query;
            Run();
        }
        else if (newSource)
        {
            // The source's default search, if one is saved (FR-023); else its front page.
            if (AppServices.Preferences.DefaultQueries.GetValueOrDefault(args.Source.Id) is { } saved)
            {
                QueryBox.Text = saved.Text;
                _vm!.ApplyFilters(saved.Filters);
                FilterHost.Content = new FilterPanel(_vm.Filters);
            }
            else
            {
                QueryBox.Text = "";
            }
            Run();
        }
        Render();
        UpdateStar();
    }

    // --- default query star (FR-022) ---------------------------------------------------------------

    private bool IsDefault() =>
        _vm is not null && _source is not null &&
        AppServices.Preferences.DefaultQueries.GetValueOrDefault(_source.Id) is { } saved &&
        saved.Text == QueryBox.Text.Trim() && JsonNode.DeepEquals(saved.Filters, _vm.CurrentFilters());

    private void UpdateStar()
    {
        var on = IsDefault();
        DefaultQueryIcon.Glyph = on ? "" : "";   // filled / outline star
        var text = Strings.Get(on ? "Search_DefaultSaved" : "Search_SaveDefault");
        ToolTipService.SetToolTip(DefaultQueryButton, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DefaultQueryButton, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(DefaultQueryButton, on ? "Default" : "NotDefault");
    }

    private void DefaultQueryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null || _source is null) return;
        var preferences = AppServices.Preferences;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrl) preferences.DefaultQueries.Remove(_source.Id);
        else preferences.DefaultQueries[_source.Id] = new DefaultQuery(QueryBox.Text.Trim(), _vm.CurrentFilters());
        preferences.Save();
        UpdateStar();
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateStar();

    private void Run()
    {
        _vm!.Query = QueryBox.Text;
        _vm.Search();
        UpdateStar();
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) Run();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => Run();
    // Filters as they were when the flyout opened: closing it without a change keeps the results.
    private JsonNode? _filtersOnOpen;

    private void FiltersFlyout_Opened(object? sender, object e) => _filtersOnOpen = _vm?.CurrentFilters();

    private void FiltersFlyout_Closed(object? sender, object e)
    {
        if (_vm is null || JsonNode.DeepEquals(_filtersOnOpen, _vm.CurrentFilters())) return;
        Run();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _vm!.ResetFilters();
        FilterHost.Content = new FilterPanel(_vm.Filters);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e) => _vm?.Retry();

    private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_vm?.ErrorDetails ?? "");
        Clipboard.SetContent(package);
    }

    private void ResultsGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not JsonObject item) return;
        if (args.ItemContainer.ContentTemplateRoot is not ResultCard card) return;
        card.Show(_vm!.Session, item);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, item["title"]?.GetValue<string>() ?? "");
        args.Handled = true;
    }

    private void ResultsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is JsonObject item && _source is not null)
        {
            Frame.Navigate(typeof(GalleryPage), new GalleryPage.Args(_source, item));
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        if (_vm is null) return;
        Progress.IsActive = _vm.Status == SearchStatus.Loading;
        StatusText.Text = _vm.Status switch
        {
            SearchStatus.Loading => Strings.Get("Search_Loading"),
            SearchStatus.End when _vm.Results.Count == 0 => Strings.Get("Search_Empty"),
            SearchStatus.End => Strings.Get("Search_End"),
            _ => _vm.Total ?? "",
        };
        ErrorBar.IsOpen = _vm.Status == SearchStatus.Error;
        ErrorBar.Title = _vm.ErrorTitle;
        ErrorBar.Message = _vm.ErrorMessage;
    }
}
