using System.Text.Json.Nodes;
using HentaiReader.Host.Html;

namespace HentaiReader.Host.Tests.Unit;

/// <summary>Port of tools/runner/test/html.test.ts for the AngleSharp-based HtmlHost.</summary>
public class HtmlHostTests
{
    private const string Doc = """
        <!doctype html><html><body>
          <table id="t"><tr class="row"><td>a</td><td data-x="1">  b
             c </td></tr><tr class="row"><td>d</td></tr></table>
          <div class="g"><a href="/1">One</a><span>own <b>bold</b> tail</span></div>
          <div class="g empty"></div>
        </body></html>
        """;

    private static JsonArray Select(HtmlHost h, string selector, int element = -1) =>
        JsonNode.Parse(h.Select(Doc, element, selector))!["value"]!.AsArray();

    private static string? Call(HtmlHost h, string op, int id, string arg = "") =>
        JsonNode.Parse(h.Call(op, id, arg))!["value"]?.GetValue<string>();

    [Fact]
    public void Selects_within_elements_descendants_only()
    {
        var h = new HtmlHost();
        var rows = Select(h, "tr.row");
        Assert.Equal([2, 1], rows.Select(r => Select(h, "td", r!["id"]!.GetValue<int>()).Count));
        var table = Select(h, "table")[0]!["id"]!.GetValue<int>();
        Assert.Empty(Select(h, "table", table));
    }

    [Fact]
    public void Text_normalisation_and_attributes()
    {
        var h = new HtmlHost();
        var td = Select(h, "td[data-x]")[0]!["id"]!.GetValue<int>();
        var span = Select(h, "div.g span")[0]!["id"]!.GetValue<int>();
        Assert.Equal("b c", Call(h, "text", td));
        Assert.Equal("1", Call(h, "attr", td, "data-x"));
        Assert.Null(Call(h, "attr", td, "missing"));
        Assert.Equal("own tail", Call(h, "ownText", span));
        Assert.Equal("own bold tail", Call(h, "text", span));
    }

    [Theory]
    [InlineData("div.g:has(a)", 1)]
    [InlineData("a[href^='/']", 1)]
    [InlineData("tr:first-child td:last-child", 1)]
    [InlineData("div.g:not(.empty)", 1)]
    public void Portable_selectors(string selector, int count) => Assert.Equal(count, Select(new HtmlHost(), selector).Count);

    [Theory]
    [InlineData(":contains(One)")]
    [InlineData("td:eq(0)")]
    [InlineData("tr:first")]
    [InlineData("td:last")]
    [InlineData("tr:odd")]
    [InlineData(":header")]
    [InlineData("div:parent")]
    [InlineData("div[")]
    public void Non_standard_or_invalid_selectors_are_rejected(string selector)
    {
        var env = JsonNode.Parse(new HtmlHost().Select(Doc, -1, selector))!;
        Assert.Equal("InvalidInput", env["error"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Released_handles_are_invalid()
    {
        var h = new HtmlHost();
        var id = Select(h, "td")[0]!["id"]!.GetValue<int>();
        h.ReleaseHandles();
        Assert.Equal("InvalidInput", JsonNode.Parse(h.Call("text", id, ""))!["error"]!["kind"]!.GetValue<string>());
    }
}
