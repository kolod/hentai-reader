using System.Text.Json.Nodes;
using HentaiReader.Host.Sources;

namespace HentaiReader.Host.Tests.Sources;

/// <summary>Same cases as tools/runner/test/login-page.test.ts (one rule set, two hosts).</summary>
public class ManifestRulesTests
{
    private static JsonObject Manifest(string? loginPage, string contract = "1.2", string login = "optional") => new()
    {
        ["contract"] = contract,
        ["login"] = login,
        ["domains"] = new JsonArray("e-hentai.org", "*.e-hentai.org", "exhentai.org"),
        ["loginPage"] = loginPage is null ? null : JsonNode.Parse(loginPage),
    };

    [Fact]
    public void Valid_or_absent_loginPage_has_no_violations()
    {
        Assert.Empty(ManifestRules.LoginPageViolations(Manifest("""{"url":"https://e-hentai.org/bounce_login.php","cookies":["a","b"],"shareWith":["exhentai.org"]}""")));
        Assert.Empty(ManifestRules.LoginPageViolations(Manifest(null)));
    }

    [Theory]
    [InlineData("""{"url":"http://e-hentai.org/x","cookies":["a"]}""", "absolute https URL")]
    [InlineData("""{"url":"not a url","cookies":["a"]}""", "absolute https URL")]
    [InlineData("""{"url":"https://evil.test/x","cookies":["a"]}""", "host evil.test is outside manifest.domains")]
    [InlineData("""{"url":"https://e-hentai.org/x","cookies":[]}""", "at least one cookie name")]
    [InlineData("""{"url":"https://e-hentai.org/x","cookies":["a","a"]}""", "each once")]
    [InlineData("""{"url":"https://e-hentai.org/x","cookies":["a"],"shareWith":["other.org"]}""", "other.org is outside")]
    public void Each_rule_is_reported(string loginPage, string expected)
    {
        Assert.Contains(ManifestRules.LoginPageViolations(Manifest(loginPage)), v => v.Contains(expected));
    }

    [Fact]
    public void Login_none_and_old_contracts_cannot_declare_a_login_page()
    {
        const string page = """{"url":"https://e-hentai.org/x","cookies":["a"]}""";
        Assert.Contains(ManifestRules.LoginPageViolations(Manifest(page, login: "none")), v => v.Contains("not allowed when login is \"none\""));
        Assert.Contains(ManifestRules.LoginPageViolations(Manifest(page, contract: "1.1")), v => v.Contains("requires contract 1.2"));
    }

    [Theory]
    [InlineData("forums.e-hentai.org", true)]
    [InlineData("e-hentai.org", true)]
    [InlineData("E-Hentai.org", true)]
    [InlineData("xe-hentai.org", false)]
    public void Domain_patterns_follow_the_http_allow_list(string host, bool allowed)
    {
        Assert.Equal(allowed, ManifestRules.HostInDomains(host, ["e-hentai.org", "*.e-hentai.org"]));
    }
}
