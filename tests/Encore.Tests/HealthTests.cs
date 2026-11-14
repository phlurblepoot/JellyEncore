using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Encore.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200()
    {
        var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_in_fetch_mode_off_has_the_slskd_and_fetch_sections_and_no_warnings()
    {
        var client = factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/health"));
        var root = doc.RootElement;

        Assert.False(root.GetProperty("slskd").GetProperty("configured").GetBoolean());
        Assert.Equal("off", root.GetProperty("fetch").GetProperty("mode").GetString());
        Assert.Equal(0, root.GetProperty("warnings").GetArrayLength());
    }
}
