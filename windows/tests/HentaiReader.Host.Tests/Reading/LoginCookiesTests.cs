using System.Text.Json.Nodes;
using HentaiReader.Host.Http;
using HentaiReader.Host.Reading;

namespace HentaiReader.Host.Tests.Reading;

public class LoginCookiesTests
{
    private static readonly JsonObject Manifest = JsonNode.Parse("""
        {
          "domains": ["e-hentai.org", "*.e-hentai.org", "exhentai.org", "*.ehgt.org"],
          "loginPage": { "url": "https://e-hentai.org/bounce_login.php", "cookies": ["ipb_member_id", "ipb_pass_hash"], "shareWith": ["exhentai.org"] }
        }
        """)!.AsObject();

    private static CookieJar.Cookie C(string name, string domain, string value = "v") => new(name, value, domain, false, "/", null);

    [Fact]
    public void Only_cookies_of_the_source_domains_are_kept()
    {
        var kept = LoginCookies.Select(Manifest, [C("ipb_member_id", ".e-hentai.org"), C("tracker", ".ads.example"), C("cf", "forums.e-hentai.org")]);
        Assert.DoesNotContain(kept, c => c.Domain == "ads.example");
        Assert.Contains(kept, c => c.Name == "cf" && c.Domain == "forums.e-hentai.org");
        Assert.Contains(kept, c => c.Name == "ipb_member_id" && c.Domain == "e-hentai.org");
    }

    [Fact]
    public void Sign_in_cookies_are_shared_with_the_mirror_and_nothing_else_is()
    {
        var kept = LoginCookies.Select(Manifest, [C("ipb_member_id", ".e-hentai.org", "1"), C("ipb_pass_hash", ".e-hentai.org", "2"), C("other", ".e-hentai.org")]);
        var mirror = kept.Where(c => c.Domain == "exhentai.org").Select(c => (c.Name, c.Value)).OrderBy(c => c.Name).ToList();
        Assert.Equal([("ipb_member_id", "1"), ("ipb_pass_hash", "2")], mirror);
    }

    [Fact]
    public void Cookies_pasted_from_the_mirror_also_sign_in_the_main_site()
    {
        var kept = LoginCookies.Select(Manifest, [C("ipb_member_id", ".exhentai.org", "1"), C("ipb_pass_hash", ".exhentai.org", "2"), C("igneous", ".exhentai.org")]);
        Assert.Equal(["ipb_member_id", "ipb_pass_hash"], kept.Where(c => c.Domain == "e-hentai.org").Select(c => c.Name).Order());
        Assert.DoesNotContain(kept, c => c.Name == "igneous" && c.Domain == "e-hentai.org");
        Assert.Equal(1, kept.Count(c => c.Name == "ipb_member_id" && c.Domain == "exhentai.org"));
    }

    [Fact]
    public void Missing_reports_absent_or_empty_sign_in_cookies()
    {
        var page = Manifest["loginPage"]!.AsObject();
        Assert.Equal(["ipb_pass_hash"], LoginCookies.Missing(page, [C("ipb_member_id", "e-hentai.org"), C("ipb_pass_hash", "e-hentai.org", "")]));
        Assert.Empty(LoginCookies.Missing(page, [C("ipb_member_id", "e-hentai.org"), C("ipb_pass_hash", "e-hentai.org")]));
    }

    [Fact]
    public void Pasted_header_style_cookies_belong_to_the_default_domain()
    {
        var parsed = LoginCookies.Parse(" ipb_member_id=123 ; ipb_pass_hash=abc;broken; =x ", ".e-hentai.org");
        Assert.Equal([("ipb_member_id", "123", "e-hentai.org"), ("ipb_pass_hash", "abc", "e-hentai.org")], parsed.Select(c => (c.Name, c.Value, c.Domain)));
    }

    [Fact]
    public void Pasted_netscape_file_keeps_its_domains()
    {
        var text = "# Netscape HTTP Cookie File\n.exhentai.org\tTRUE\t/\tTRUE\t1893456000\tigneous\tx1\n#HttpOnly_.e-hentai.org\tTRUE\t/\tTRUE\t0\tipb_pass_hash\tx2\n";
        var parsed = LoginCookies.Parse(text, "e-hentai.org");
        Assert.Equal([("igneous", "exhentai.org"), ("ipb_pass_hash", "e-hentai.org")], parsed.Select(c => (c.Name, c.Domain)));
    }

    [Fact]
    public void Netscape_text_from_a_text_box_with_bare_cr_line_breaks_is_parsed()
    {
        var text = "# Netscape HTTP Cookie File\r.exhentai.org\tTRUE\t/\tTRUE\t0\tipb_member_id\t1\r.exhentai.org\tTRUE\t/\tTRUE\t0\tipb_pass_hash\t2\r";
        Assert.Equal(["ipb_member_id", "ipb_pass_hash"], LoginCookies.Parse(text, "e-hentai.org").Select(c => c.Name));
    }

    [Fact]
    public void Jar_add_clear_and_names()
    {
        var jar = new CookieJar();
        var changes = 0;
        jar.Changed += () => changes++;
        jar.Add(LoginCookies.Select(Manifest, [C("ipb_member_id", ".e-hentai.org"), C("ipb_pass_hash", ".e-hentai.org")]));
        Assert.Equal(["ipb_member_id", "ipb_pass_hash"], jar.NamesFor("exhentai.org").Order());
        Assert.Contains("ipb_member_id", jar.For("https://forums.e-hentai.org/x").Keys);
        jar.Clear();
        Assert.Empty(jar.NamesFor("e-hentai.org"));
        Assert.Equal(2, changes);
    }
}
