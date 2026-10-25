using System.Text.Json.Nodes;
using HentaiReader.App.Images;
using HentaiReader.Host.Sources;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HentaiReader.App.Controls;

/// <summary>
/// A page thumbnail. With `crop` (contract 1.1) the shared sprite is shown through a clipped
/// container offset to the cell, scaled by a Viewbox — no per-cell re-encode. The box size comes
/// from the page's size, so the grid never jumps while images load. Very tall pages (webtoons) get a
/// box at most <see cref="MaxAspect"/> times as high as wide showing the top of the page: a box of
/// the full height blew the small thumbnail up into a few blurry pixels.
/// </summary>
public sealed class SpriteThumbnail : Grid
{
    public const double ThumbWidth = 150;
    private const double MaxAspect = 2;
    private CancellationTokenSource? _loading;

    public SpriteThumbnail()
    {
        Width = ThumbWidth;
        Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"];
        CornerRadius = new CornerRadius(4);
    }

    public async void Show(SourceSession session, JsonObject page)
    {
        _loading?.Cancel();
        _loading = new CancellationTokenSource();
        var token = _loading.Token;
        var w = page["width"]?.GetValue<double>() ?? 2;
        var h = page["height"]?.GetValue<double>() ?? 3;
        Height = Math.Round(ThumbWidth * Math.Min(MaxAspect, h / Math.Max(1, w)));
        Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, ThumbWidth, Height) };
        Children.Clear();

        var request = page["thumbnail"];
        if (request is null) return;
        var crop = request["crop"] as JsonObject;
        var image = await ImageLoader.Shared.LoadAsync(session, request, crop is null ? (int)(ThumbWidth * (XamlRoot?.RasterizationScale ?? 1)) : 0, token);
        if (token.IsCancellationRequested || image is null) return;

        if (crop is null)
        {
            Children.Add(new Image { Source = image, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top });
            return;
        }
        var cw = crop["width"]!.GetValue<double>();
        var ch = crop["height"]!.GetValue<double>();
        var sprite = new Image { Source = image, Stretch = Stretch.None, Width = image.PixelWidth, Height = image.PixelHeight };
        Canvas.SetLeft(sprite, -crop["x"]!.GetValue<double>());
        Canvas.SetTop(sprite, -crop["y"]!.GetValue<double>());
        var cell = new Canvas
        {
            Width = cw,
            Height = ch,
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, cw, ch) },
        };
        cell.Children.Add(sprite);
        Children.Add(new Viewbox { Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Top, Child = cell });
    }
}
