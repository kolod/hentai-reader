using HentaiReader.App.Localization;
using HentaiReader.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HentaiReader.App.Controls;

/// <summary>
/// Controls for exactly the filters a source declares (US2-2): single → combo box, multi → check
/// boxes, number → number box, range → two number boxes. Labels come from the contract vocabulary.
/// </summary>
public sealed class FilterPanel : StackPanel
{
    public FilterPanel(IEnumerable<FilterValue> filters)
    {
        Spacing = 12;
        // A fixed width inside the flyout's default 456 px maximum, so check boxes wrap instead of
        // running off the edge and long German or Japanese labels keep their full text (SC-005).
        Width = 400;
        foreach (var f in filters) Children.Add(Build(f));
    }

    private static FrameworkElement Build(FilterValue f)
    {
        var label = Strings.FilterLabel(f.Id);
        switch (f.Kind)
        {
            case "single":
            {
                var values = f.Definition["values"]!.AsArray().Select(v => v!.GetValue<string>()).ToList();
                var hasDefault = f.Definition["default"] is not null;
                var combo = new ComboBox { Header = label, HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Text" };
                var options = new List<Option>();
                if (!hasDefault) options.Add(new Option(null, Strings.Get("Search_Any")));
                options.AddRange(values.Select(v => new Option(v, ValueLabel(f.Id, v))));
                if (f.Id == "language") options = options.Take(hasDefault ? 0 : 1).Concat(options.Skip(hasDefault ? 0 : 1).OrderBy(o => o.Text, StringComparer.CurrentCulture)).ToList();
                combo.ItemsSource = options;
                combo.SelectedItem = options.FirstOrDefault(o => o.Value == f.Single) ?? options[0];
                combo.SelectionChanged += (_, _) => f.Single = (combo.SelectedItem as Option)?.Value;
                return combo;
            }
            case "multi":
            {
                var panel = new StackPanel { Spacing = 2 };
                panel.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
                var grid = new WrapPanel { Spacing = 8 };
                var values = f.Definition["values"]!.AsArray().Select(v => v!.GetValue<string>());
                // Languages (contract 1.2 multi filter) are many: alphabetical by their localized name.
                if (f.Id == "language") values = values.OrderBy(v => ValueLabel(f.Id, v), StringComparer.CurrentCulture);
                foreach (var v in values)
                {
                    var box = new CheckBox { Content = ValueLabel(f.Id, v), IsChecked = f.Multi.Contains(v), MinWidth = 120 };
                    box.Checked += (_, _) => f.Multi.Add(v);
                    box.Unchecked += (_, _) => f.Multi.Remove(v);
                    grid.Children.Add(box);
                }
                panel.Children.Add(grid);
                return panel;
            }
            case "number":
            {
                var box = new NumberBox
                {
                    Header = label,
                    Minimum = f.Definition["min"]!.GetValue<double>(),
                    Maximum = f.Definition["max"]!.GetValue<double>(),
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Value = f.Number,
                };
                box.ValueChanged += (_, e) => f.Number = e.NewValue;
                return box;
            }
            case "range":
            {
                var min = f.Definition["min"]!.GetValue<double>();
                var max = f.Definition["max"]!.GetValue<double>();
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition());
                var from = new NumberBox { Header = $"{label}: {Strings.Get("Search_From")}", Minimum = min, Maximum = max, Value = f.Min };
                var to = new NumberBox { Header = Strings.Get("Search_To"), Minimum = min, Maximum = max, Value = f.Max };
                from.ValueChanged += (_, e) => f.Min = e.NewValue;
                to.ValueChanged += (_, e) => f.Max = e.NewValue;
                Grid.SetColumn(to, 1);
                row.Children.Add(from);
                row.Children.Add(to);
                return row;
            }
            default:
                return new TextBlock { Text = label };
        }
    }

    private static string ValueLabel(string filterId, string value) => filterId switch
    {
        "sort" => Strings.SortLabel(value),
        "type" => Strings.TypeLabel(value),
        "language" => Strings.LanguageName(value),
        _ => value,
    };

    private sealed record Option(string? Value, string Text);
}
