using System.Text.Json.Nodes;
using HentaiReader.Host.Http;
using HentaiReader.Host.Sources;

namespace HentaiReader.Host.Tests.Unit;

public class CallArgsTests
{
    private static readonly JsonObject V10 = JsonNode.Parse("""{"contract":"1.0","capabilities":{"suggestTags":false}}""")!.AsObject();

    private static readonly JsonObject V11 = JsonNode.Parse("""
        {"contract":"1.1","capabilities":{"suggestTags":false,"accountStatus":true},
         "settings":[{"id":"image_quality","values":["resampled","original"],"default":"resampled"},
                     {"id":"site","values":["e-hentai","exhentai"],"default":"e-hentai"}]}
        """)!.AsObject();

    [Fact]
    public void Contract_10_keeps_the_10_arity()
    {
        var s = CallArgs.ResolveSettings(V10);
        Assert.Equal(2, CallArgs.Build(V10, "getPageUrl", [JsonNode.Parse("{}"), 0], s).Count);
        Assert.Equal(2, CallArgs.Build(V10, "getPageUrl", [JsonNode.Parse("{}"), 0, null], s).Count);
        Assert.Equal(3, CallArgs.Build(V10, "getPageUrl", [JsonNode.Parse("{}"), 0, JsonNode.Parse("{\"attempt\":1}")], s).Count);
    }

    [Fact]
    public void Contract_11_gets_null_retry_and_settings_last()
    {
        var s = CallArgs.ResolveSettings(V11, new Dictionary<string, string> { ["site"] = "exhentai" });
        var args = CallArgs.Build(V11, "getPageUrl", [JsonNode.Parse("{}"), 3], s);
        Assert.Equal("[{},3,null,{\"image_quality\":\"resampled\",\"site\":\"exhentai\"}]", args.ToJsonString());
        Assert.Equal("[{\"image_quality\":\"resampled\",\"site\":\"exhentai\"}]", CallArgs.Build(V11, "getAccountStatus", [], s).ToJsonString());
    }

    [Fact]
    public void Settings_validation_and_gating()
    {
        Assert.Throws<SettingsException>(() => CallArgs.ResolveSettings(V11, new Dictionary<string, string> { ["theme"] = "dark" }));
        Assert.Throws<SettingsException>(() => CallArgs.ResolveSettings(V11, new Dictionary<string, string> { ["site"] = "nhentai" }));
        Assert.Throws<SettingsException>(() => CallArgs.Build(V10, "getAccountStatus", [], new JsonObject()));
        Assert.True(CallArgs.TakesSettings(V11));
        Assert.False(CallArgs.TakesSettings(V10));
    }

    [Fact]
    public void Scheduler_without_limits_does_not_wait()
    {
        var s = new Scheduler(() => null);
        Assert.True(s.AcquireAsync(TestContext.Current.CancellationToken).IsCompleted);
        s.Release();
    }

    [Fact]
    public void Cookie_jar_round_trips_and_imports_netscape()
    {
        var jar = new CookieJar();
        jar.SeedNetscape(".e-hentai.org\tTRUE\t/\tFALSE\t0\tipb_member_id\t1\n# comment\n");
        Assert.Equal("1", jar.For("https://api.e-hentai.org/x")["ipb_member_id"]);
        var copy = new CookieJar();
        copy.Load(jar.Serialize());
        Assert.Equal("1", copy.For("https://e-hentai.org/")["ipb_member_id"]);
        Assert.Empty(copy.For("https://exhentai.org/"));
    }
}
