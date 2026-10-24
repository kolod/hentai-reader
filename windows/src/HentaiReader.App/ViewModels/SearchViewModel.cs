using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HentaiReader.App.Localization;
using HentaiReader.App.Services;
using HentaiReader.Host.Engine;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace HentaiReader.App.ViewModels;

public enum SearchStatus { Idle, Loading, End, Error }

/// <summary>Current value of one declared filter (manifest FilterDefinition).</summary>
public sealed partial class FilterValue : ObservableObject
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public required JsonObject Definition { get; init; }
    [ObservableProperty] public partial string? Single { get; set; }
    [ObservableProperty] public partial double Number { get; set; }
    [ObservableProperty] public partial double Min { get; set; }
    [ObservableProperty] public partial double Max { get; set; }
    public HashSet<string> Multi { get; } = new();

    /// <summary>Contract SearchQuery.filters entry, or null when unset.</summary>
    public JsonNode? ToJson() => Kind switch
    {
        "single" => Single is null ? null : JsonValue.Create(Single),
        "multi" => Multi.Count == 0 ? null : new JsonArray(Multi.Order().Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
        "number" => double.IsNaN(Number) ? null : JsonValue.Create(Number),
        "range" when double.IsNaN(Min) && double.IsNaN(Max) => null,
        "range" => RangeJson(),
        _ => null,
    };

    private JsonObject RangeJson()
    {
        var o = new JsonObject();
        if (!double.IsNaN(Min)) o["min"] = Min;
        if (!double.IsNaN(Max)) o["max"] = Max;
        return o;
    }

    public void Reset()
    {
        Single = Kind == "single" ? Definition["default"]?.GetValue<string>() : null;
        Number = Min = Max = double.NaN;
        Multi.Clear();
    }
}

/// <summary>Result list that asks the view model for the next page when the grid nears its end.</summary>
public sealed class ResultCollection(SearchViewModel owner) : ObservableCollection<JsonObject>, ISupportIncrementalLoading
{
    public bool HasMoreItems => owner.HasMore;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => Load().AsAsyncOperation();

    private async Task<LoadMoreItemsResult> Load() => new() { Count = (uint)await owner.LoadMoreAsync() };
}

/// <summary>
/// Search state of one source (data-model.md SearchState): query, declared filters, endless
/// results via the source cursor, cancellation of superseded searches, error kinds, and the
/// Limited state after RateLimited that only an explicit retry clears (FR-013).
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly SourceEntry _source;
    private CancellationTokenSource? _cts;
    private string? _cursor;
    private bool _started;
    private int _generation;
    private Task<int>? _inflight;

    public SearchViewModel(SourceEntry source)
    {
        _source = source;
        Query = "";
        Results = new ResultCollection(this);
        foreach (var f in source.Session!.Manifest["filters"]!.AsArray())
        {
            var def = f!.AsObject();
            var value = new FilterValue { Id = def["id"]!.GetValue<string>(), Kind = def["kind"]!.GetValue<string>(), Definition = def };
            value.Reset();
            Filters.Add(value);
        }
    }

    public SourceSession Session => _source.Session!;
    public string SourceName => _source.Name;
    public ResultCollection Results { get; }
    public List<FilterValue> Filters { get; } = new();

    [ObservableProperty] public partial string Query { get; set; }
    [ObservableProperty] public partial SearchStatus Status { get; set; }
    [ObservableProperty] public partial string? ErrorKind { get; set; }
    [ObservableProperty] public partial string? ErrorTitle { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string? ErrorDetails { get; set; }
    [ObservableProperty] public partial string? Total { get; set; }

    public bool HasMore => Status is not (SearchStatus.End or SearchStatus.Error) && (!_started || _cursor is not null);

    [RelayCommand]
    public void Search()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _generation++;
        _cursor = null;
        _started = false;
        _inflight = null;
        Results.Clear();
        Total = null;
        ClearError();
        Status = SearchStatus.Idle;
        OnPropertyChanged(nameof(HasMore));
        // The grid asks for items through ISupportIncrementalLoading; trigger the first page now.
        _ = LoadMoreAsync();
    }

    [RelayCommand]
    public void Retry()
    {
        Session.ClearLimit();
        if (_started && Results.Count > 0)
        {
            ClearError();
            Status = SearchStatus.Idle;
            OnPropertyChanged(nameof(HasMore));
            _ = LoadMoreAsync();
        }
        else
        {
            Search();
        }
    }

    public void ResetFilters()
    {
        foreach (var f in Filters) f.Reset();
    }

    /// <summary>
    /// Loads the next page. A call made while a page is loading (the grid asks while Search already
    /// started one) shares that load; answering 0 would make the grid stop asking for more.
    /// </summary>
    public Task<int> LoadMoreAsync()
    {
        if (_inflight is { IsCompleted: false } running) return running;
        return _inflight = LoadNextAsync();
    }

    private async Task<int> LoadNextAsync()
    {
        if (!HasMore) return 0;
        var generation = _generation;
        var token = _cts?.Token ?? CancellationToken.None;
        Status = SearchStatus.Loading;
        var filters = new JsonObject();
        foreach (var f in Filters)
        {
            if (f.ToJson() is { } v) filters[f.Id] = v;
        }
        var query = new JsonObject { ["text"] = Query.Trim(), ["filters"] = filters };
        CallResult result;
        try
        {
            result = await _source.CallAsync("search", [query, _cursor is null ? null : JsonValue.Create(_cursor)], token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        if (generation != _generation) return 0;
        _started = true;
        if (!result.Ok)
        {
            ShowError(result.Error!);
            OnPropertyChanged(nameof(HasMore));
            return 0;
        }
        var page = result.Value!.AsObject();
        var items = page["items"]!.AsArray();
        foreach (var item in items) Results.Add(item!.AsObject());
        _cursor = page["nextCursor"]?.GetValue<string>();
        if (page["total"] is JsonValue t) Total = Strings.Plural("Search_ResultCount", (long)t.GetValue<double>());
        Status = _cursor is null ? SearchStatus.End : SearchStatus.Idle;
        OnPropertyChanged(nameof(HasMore));
        return items.Count;
    }

    private void ShowError(CallError e)
    {
        var kind = e.Type == "SourceError" ? e.Kind! : "ScriptError";
        if (e.Details?["limit"] is not null) kind = "ScriptError";
        ErrorKind = kind;
        ErrorTitle = Strings.ErrorTitle(kind);
        var message = Strings.ErrorBody(kind);
        if (kind == SourceErrorKind.RateLimited && e.RetryAfterSeconds is { } seconds)
        {
            message += " " + Strings.Format("Error_RateLimited_After", Math.Max(1, (int)Math.Ceiling(seconds / 60)));
        }
        ErrorMessage = message;
        ErrorDetails = $"{SourceName}: {e.Type}({e.Kind ?? e.Name}): {e.Message}";
        Status = SearchStatus.Error;
    }

    private void ClearError()
    {
        ErrorKind = ErrorTitle = ErrorMessage = ErrorDetails = null;
    }
}
