using HentaiReader.App.Images;
using HentaiReader.App.Localization;
using HentaiReader.Host.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HentaiReader.App.Controls;

/// <summary>
/// One saved gallery (library-ui.md): cover from the archive, title, source · pages, reading progress;
/// unreadable or unavailable archives greyed with the reason. Same size as a search result card.
/// </summary>
public sealed class LibraryCard : Grid
{
    private readonly Image _cover = new() { Stretch = Stretch.UniformToFill, Height = ResultCard.CoverHeight, Width = ResultCard.CardWidth };
    private readonly TextBlock _title = new() { TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _meta = new() { Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new() { Opacity = 0.6, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 2, 0, 0) };
    private CancellationTokenSource? _loading;

    public LibraryCard()
    {
        Width = ResultCard.CardWidth;
        RowSpacing = 4;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ResultCard.CoverHeight) });
        for (var i = 0; i < 4; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = _cover,
        });
        SetRow(_progress, 1);
        SetRow(_title, 2);
        SetRow(_meta, 3);
        SetRow(_state, 4);
        Children.Add(_progress);
        Children.Add(_title);
        Children.Add(_meta);
        Children.Add(_state);
    }

    public async void Show(LibraryItem item, string sourceName, bool showFolder)
    {
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var a = item.Archive;
        _title.Text = a.Title;
        ToolTipService.SetToolTip(_title, a.Title);
        var parts = new List<string> { sourceName, Strings.Plural("Gallery_PageCount", a.Pages.Count) };
        if (a.Language is { } lang) parts.Add(Strings.LanguageName(lang));
        _meta.Text = string.Join(" · ", parts);
        _state.Text = a.State switch
        {
            ArchiveState.Unavailable => Strings.Get("Library_Unavailable"),
            ArchiveState.Unreadable => Strings.Get("Library_Unreadable"),
            _ => item.Progress switch
            {
                { Finished: true } => Strings.Get("Gallery_Finished"),
                { } p => Strings.Format("Gallery_Progress", p.Page + 1, Math.Max(p.PageCount, a.Pages.Count)),
                null => Strings.Get("Library_Unread"),
            } + (showFolder ? " · " + Path.GetFileName(item.FolderPath) : ""),
        };
        _progress.Value = item.Progress is { } pr ? (pr.Finished ? 1 : (pr.Page + pr.Offset) / Math.Max(1, a.Pages.Count)) : 0;
        _progress.Visibility = item.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        Opacity = a.State == ArchiveState.Ok ? 1 : 0.5;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(this, a.State.ToString());
        _cover.Source = null;
        if (a.State != ArchiveState.Ok) return;
        var image = await ArchiveCovers.Shared.LoadAsync(item.FullPath, a.Size, (int)(ResultCard.CardWidth * (XamlRoot?.RasterizationScale ?? 1)), token);
        if (!token.IsCancellationRequested) _cover.Source = image;
    }
}
