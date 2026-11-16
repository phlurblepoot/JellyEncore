using System.Net;
using Xunit;

namespace Encore.Tests.Requests;

/// <summary>v03 section 15, T91: the request page is served from embedded resources with the security headers.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class PageTests
{
    [Fact]
    public async Task PG01_The_page_is_served_with_html_type_csp_and_nosniff()
    {
        using var rig = new ApiRig();
        using var response = await rig.GetAsync("/request");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/html", response.Content.Headers.ContentType!.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("request/app.js", html);
        Assert.Contains("request/app.css", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("style=", html);
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-referrer", response.Headers.GetValues("Referrer-Policy"));
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("default-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("base-uri 'none'", csp);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
    }

    [Theory]
    [InlineData("/request/app.js", "text/javascript")]
    [InlineData("/request/app.css", "text/css")]
    public async Task PG02_The_assets_are_served_with_their_type_csp_and_nosniff(string path, string type)
    {
        using var rig = new ApiRig();
        using var response = await rig.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(type, response.Content.Headers.ContentType!.ToString());
        Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("public, max-age=300", response.Headers.CacheControl!.ToString());
        Assert.True((await response.Content.ReadAsStringAsync()).Length > 500);
    }

    [Fact]
    public async Task PG03_The_script_never_writes_markup_from_data()
    {
        using var rig = new ApiRig();
        var js = await (await rig.GetAsync("/request/app.js")).Content.ReadAsStringAsync();
        foreach (var banned in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
        {
            Assert.DoesNotContain(banned, js);
        }
    }

    [Fact]
    public async Task PG04_A_trailing_slash_goes_to_the_page_and_keeps_the_query()
    {
        using var rig = new ApiRig();
        using var response = await rig.GetAsync("/request/?q=radiohead");
        // The test client follows the redirect: it must end on the page, with the query kept.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/request", response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Equal("?q=radiohead", response.RequestMessage.RequestUri.Query);
    }

    [Fact]
    public async Task PG05_With_a_path_base_the_page_needs_the_prefix_and_a_proxy_may_keep_or_strip_it()
    {
        using var rig = new ApiRig(new Dictionary<string, string?>
        {
            ["ENCORE_PATH_BASE"] = "/encore",
            ["ENCORE_TRUSTED_PROXIES"] = "172.21.0.0/16",
        });
        using var direct = await rig.GetAsync("/encore/request");
        Assert.Equal(HttpStatusCode.OK, direct.StatusCode);
        using var bare = await rig.GetAsync("/request");
        Assert.Equal(HttpStatusCode.NotFound, bare.StatusCode);
        using var stripped = await rig.GetAsync("/request/app.js", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.OK, stripped.StatusCode);
        using var kept = await rig.GetAsync("/encore/request/app.js", peer: "172.21.0.5");
        Assert.Equal(HttpStatusCode.OK, kept.StatusCode);
    }

    [Fact]
    public async Task PG06_The_page_is_not_there_when_requests_are_off()
    {
        using var rig = new ApiRig(on: false);
        using var response = await rig.GetAsync("/request");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PG07_Hsts_is_added_on_forwarded_https()
    {
        using var rig = new ApiRig(new Dictionary<string, string?> { ["ENCORE_TRUSTED_PROXIES"] = "172.21.0.0/16" });
        using var response = await rig.GetAsync("/request", peer: "172.21.0.5", xff: "203.0.113.9", proto: "https");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
    }
}
