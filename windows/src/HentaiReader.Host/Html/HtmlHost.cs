using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using HentaiReader.Host.Engine;

namespace HentaiReader.Host.Html;

/// <summary>
/// html.select for the Windows host, backed by AngleSharp. Restricts selectors to the contract's
/// portable subset (contracts/host-bridge.md) — AngleSharp itself also accepts `:contains`.
/// </summary>
public sealed partial class HtmlHost
{
    private const int DocCacheSize = 8;
    private readonly HtmlParser _parser = new();
    private readonly LinkedList<(string Markup, IDocument Doc)> _docs = new();
    private readonly Dictionary<int, IElement> _handles = new();
    private int _nextId = 1;

    [GeneratedRegex(@":(contains|icontains|eq|gt|lt|first|last|even|odd|header|parent|selected|checkbox|radio|password|file|submit|reset|button|input|text|image|animated|hidden|visible)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenPseudo();

    [GeneratedRegex("[ \t\n\r\f]+")]
    private static partial Regex AsciiWhitespace();

    public static string CollapseWhitespace(string text) => AsciiWhitespace().Replace(text, " ").Trim(' ');

    public string Select(string markup, int elementId, string selector)
    {
        var forbidden = ForbiddenPseudo().Match(selector);
        if (forbidden.Success) return Envelope.Error(SourceErrorKind.InvalidInput, $"html.select: non-standard selector {forbidden.Value} is not supported");
        try
        {
            IEnumerable<IElement> nodes;
            if (elementId < 0)
            {
                nodes = Document(markup).QuerySelectorAll(selector);
            }
            else
            {
                if (!_handles.TryGetValue(elementId, out var element)) return Envelope.Error(SourceErrorKind.InvalidInput, "html.select: element is no longer valid");
                nodes = element.QuerySelectorAll(selector);
            }
            var result = new List<object>();
            foreach (var node in nodes)
            {
                var id = _nextId++;
                _handles[id] = node;
                result.Add(new Dictionary<string, object> { ["id"] = id, ["tag"] = node.LocalName.ToLowerInvariant() });
            }
            return Envelope.Value(result);
        }
        catch (Exception e) when (e is DomException or ArgumentException)
        {
            return Envelope.Error(SourceErrorKind.InvalidInput, $"html.select: invalid selector '{selector}': {e.Message}");
        }
    }

    public string Call(string op, int elementId, string arg)
    {
        if (!_handles.TryGetValue(elementId, out var node)) return Envelope.Error(SourceErrorKind.InvalidInput, "html: element is no longer valid");
        return op switch
        {
            "text" => Envelope.Value(CollapseWhitespace(node.TextContent)),
            "ownText" => Envelope.Value(CollapseWhitespace(OwnText(node))),
            "html" => Envelope.Value(node.InnerHtml),
            "outerHtml" => Envelope.Value(node.OuterHtml),
            "attr" => Envelope.Value(node.GetAttribute(arg)),
            _ => Envelope.Error(SourceErrorKind.InvalidInput, $"html: unknown operation {op}"),
        };
    }

    private static string OwnText(IElement node)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text) sb.Append(child.TextContent);
        }
        return sb.ToString();
    }

    /// <summary>Drops element handles; called when no source call is in flight.</summary>
    public void ReleaseHandles() => _handles.Clear();

    private IDocument Document(string markup)
    {
        for (var n = _docs.First; n is not null; n = n.Next)
        {
            if (!ReferenceEquals(n.Value.Markup, markup) && n.Value.Markup != markup) continue;
            _docs.Remove(n);
            _docs.AddFirst(n);
            return n.Value.Doc;
        }
        var doc = _parser.ParseDocument(markup);
        _docs.AddFirst((markup, doc));
        if (_docs.Count > DocCacheSize) _docs.RemoveLast();
        return doc;
    }
}
