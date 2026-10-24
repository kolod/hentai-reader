using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace HentaiReader.App.Reader;

public enum PageLoad { Placeholder, Resolving, Downloading, Decoding, Loaded, Failed }

/// <summary>One page of the reading session (data-model.md "Reading session").</summary>
public sealed partial class PageState(int index, int total) : ObservableObject
{
    public int Index { get; } = index;
    public int Total { get; } = total;

    [ObservableProperty] public partial PageLoad Load { get; set; }
    [ObservableProperty] public partial BitmapImage? Image { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }

    /// <summary>Last PageImage from the source; sent back as RetryHint.previous.</summary>
    public System.Text.Json.Nodes.JsonNode? PageImage { get; set; }

    /// <summary>Failed attempts so far (RetryHint.attempt of the next try).</summary>
    public int Attempts { get; set; }

    /// <summary>HTTP status of the last failed image fetch, if any.</summary>
    public int? FailedStatus { get; set; }

    /// <summary>Decoded pixel width of <see cref="Image"/>; 0 when none.</summary>
    public int DecodedWidth { get; set; }
}
