using System.Net;
using Encore.Jellyfin;

namespace Encore.Tests.Jellyfin;

public sealed class AllowListTests
{
    private static readonly UserDto Nathan = new() { Id = Guid.Parse("4f1c2d3e-5a6b-4c7d-8e9f-0a1b2c3d4e5f"), Name = "Nathan" };
    private static readonly UserDto Kid = new() { Id = Guid.Parse("0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d"), Name = "Kid" };
    private static readonly UserDto DupA = new() { Id = Guid.Parse("11111111-2222-4333-8444-555555555555"), Name = "Dup" };
    private static readonly UserDto DupB = new() { Id = Guid.Parse("66666666-7777-4888-8999-000000000000"), Name = "DUP" };
    private static readonly IReadOnlyList<UserDto> Users = [Nathan, Kid, DupA, DupB];

    [Fact]
    public void Names_match_case_insensitively()
    {
        var result = AllowList.Resolve(["nathan", "KID"], Users);

        Assert.Equal(new HashSet<Guid> { Nathan.Id, Kid.Id }, result.UserIds);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Guids_match_by_id_in_dashed_or_plain_form()
    {
        var result = AllowList.Resolve(["4f1c2d3e5a6b4c7d8e9f0a1b2c3d4e5f", "0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D"], Users);

        Assert.Equal(new HashSet<Guid> { Nathan.Id, Kid.Id }, result.UserIds);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Unknown_names_and_ids_are_reported_not_dropped_silently()
    {
        var result = AllowList.Resolve(["Ghost", "99999999-9999-4999-8999-999999999999"], Users);

        Assert.Empty(result.UserIds);
        Assert.Equal(2, result.Problems.Count);
        Assert.Equal("Ghost", result.Problems[0].Entry);
        Assert.Contains("no Jellyfin user has this name", result.Problems[0].Reason);
        Assert.Contains("no Jellyfin user has this id", result.Problems[1].Reason);
    }

    [Fact]
    public void Ambiguous_names_are_reported_so_the_wrong_user_is_never_picked()
    {
        var result = AllowList.Resolve(["dup"], Users);

        Assert.Empty(result.UserIds);
        Assert.Single(result.Problems);
        Assert.Contains("more than one", result.Problems[0].Reason);
    }

    [Fact]
    public void Blank_entries_are_ignored_and_duplicates_collapse()
    {
        var result = AllowList.Resolve(["  ", "nathan", "Nathan", ""], Users);

        Assert.Single(result.UserIds);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public async Task ResolveAsync_reads_users_once_and_resolves_them()
    {
        var script = new ScriptedHandler().Reply(HttpStatusCode.OK, JellyfinFixtures.Users);
        var http = new HttpClient(new JellyfinAuthHandler(JellyfinAuth.BuildParameter("k-test-value", "encore-0123456789ab", "0.1.0")) { InnerHandler = script })
        {
            BaseAddress = new Uri("http://jellyfin.test:8096/"),
        };
        var client = new JellyfinClient(http, new JellyfinClientOptions());

        var result = await AllowList.ResolveAsync(client, ["nathan", "Kid"]);

        Assert.Equal(2, result.UserIds.Count);
        Assert.Empty(result.Problems);
        var sent = Assert.Single(script.Sent);
        Assert.Equal("/Users", sent.PathAndQuery);
    }
}
