using System.Net;
using System.Text.Json;
using Encore.Orchestration;
using Encore.Retention;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Encore.Tests.EndToEnd;

/// <summary>
/// Boots the real Program with retention on or off (v04 section 14 T132), so a wiring mistake (a missing registration, a
/// dependency the container cannot build) fails here and not on the server. Every hosted service is resolved, /health answers and the
/// LAN-only report endpoint answers. The container validates every registration when it is built.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RetentionHostTests : IDisposable
{
    private static readonly string[] Names =
    [
        "ENCORE_DATA_DIR", "ENCORE_FETCH_MODE", "ENCORE_SLSKD_URL", "ENCORE_SLSKD_API_KEY", "ENCORE_SLSKD_DOWNLOADS_DIR", "ENCORE_RADIO_DIR",
        "ENCORE_LIBRARY_DIR", "ENCORE_RETENTION_MODE",
    ];

    private readonly Dictionary<string, string?> _saved = Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "encore-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the host may still hold the database for a moment.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SetEnvironment(string retentionMode)
    {
        foreach (var dir in new[] { "data", "downloads", "radio", "library" })
        {
            Directory.CreateDirectory(Path.Combine(_root, dir));
        }

        Environment.SetEnvironmentVariable("ENCORE_DATA_DIR", Path.Combine(_root, "data"));
        Environment.SetEnvironmentVariable("ENCORE_FETCH_MODE", "live");
        Environment.SetEnvironmentVariable("ENCORE_SLSKD_URL", "http://slskd.test:5030");
        Environment.SetEnvironmentVariable("ENCORE_SLSKD_API_KEY", "test-slskd-key-not-real-0123456789");
        Environment.SetEnvironmentVariable("ENCORE_SLSKD_DOWNLOADS_DIR", Path.Combine(_root, "downloads"));
        Environment.SetEnvironmentVariable("ENCORE_RADIO_DIR", Path.Combine(_root, "radio"));
        Environment.SetEnvironmentVariable("ENCORE_LIBRARY_DIR", Path.Combine(_root, "library"));
        Environment.SetEnvironmentVariable("ENCORE_RETENTION_MODE", retentionMode);
    }

    private WebApplicationFactory<Program> Boot(string mode)
    {
        SetEnvironment(mode);
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseDefaultServiceProvider(o =>
            {
                o.ValidateOnBuild = true;
                o.ValidateScopes = true;
            });
            // The test server has no network peer; the report answers only a private-network one.
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter, LanPeerFilter>());
        });
    }

    private sealed class LanPeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, run) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
                return run(context);
            });
            next(app);
        };
    }

    private static readonly Type[] RetentionHosted = [typeof(RetentionWorker), typeof(SharesKeeper), typeof(SlskdEvents), typeof(PlayTally)];

    [Theory]
    [InlineData("dry_run")]
    [InlineData("live")]
    public async Task H01_the_host_boots_with_retention_on_resolves_every_service_and_answers(string mode)
    {
        await using var factory = Boot(mode);
        var client = factory.CreateClient();

        // Every hosted service resolves, and the retention ones are among them, each the same instance as its singleton.
        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        foreach (var type in RetentionHosted)
        {
            Assert.Contains(hosted, h => h.GetType() == type);
            Assert.Same(factory.Services.GetRequiredService(type), hosted.Single(h => h.GetType() == type));
        }

        Assert.Contains(hosted, h => h is Orchestrator);
        foreach (var type in new[] { typeof(RetentionPass), typeof(RetentionSignals), typeof(Deleter), typeof(Promoter), typeof(LidarrGuard), typeof(RetentionSteps) })
        {
            Assert.NotNull(factory.Services.GetRequiredService(type));
        }

        Assert.NotNull(factory.Services.GetRequiredService<IUploadCheck>());
        Assert.NotNull(factory.Services.GetRequiredService<ISharesDirty>());

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using (var doc = JsonDocument.Parse(await health.Content.ReadAsStringAsync()))
        {
            Assert.Equal(mode, doc.RootElement.GetProperty("retention").GetProperty("mode").GetString());
        }

        var report = await client.GetAsync("/retention/report");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using (var doc = JsonDocument.Parse(await report.Content.ReadAsStringAsync()))
        {
            Assert.Equal(mode, doc.RootElement.GetProperty("mode").GetString());
        }

        var text = await client.GetAsync("/retention/report?format=text");
        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        Assert.Contains("No retention pass has run yet", await text.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task H02_a_report_request_with_a_forwarding_header_gets_nothing()
    {
        await using var factory = Boot("dry_run");
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/retention/report");
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task H03_mode_off_registers_nothing_of_retention_and_has_no_report()
    {
        await using var factory = Boot("off");
        var client = factory.CreateClient();

        var hosted = factory.Services.GetServices<IHostedService>().ToList();

        Assert.Contains(hosted, h => h is Orchestrator);
        Assert.DoesNotContain(hosted, h => h.GetType().Namespace == typeof(RetentionWorker).Namespace);
        foreach (var type in RetentionHosted.Concat([typeof(RetentionPass), typeof(Deleter), typeof(Promoter), typeof(RetentionSignals)]))
        {
            Assert.Null(factory.Services.GetService(type));
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/health"));
        Assert.Equal("off", doc.RootElement.GetProperty("retention").GetProperty("mode").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/retention/report")).StatusCode);
    }
}
