using System.Net;
using System.Text.Json.Nodes;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class EntitlementTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    private static JsonObject EntitlementResource(Guid id, string name, string code) => new()
    {
        ["type"] = "entitlements",
        ["id"] = id.ToString(),
        ["attributes"] = new JsonObject { ["name"] = name, ["code"] = code },
    };

    // The real wire shape: `data` only. Every server serializer passes `links: None`, and the
    // field is skip_serializing_if none, so no response the API can produce has a `links` key.
    private static string ListBody(params JsonObject[] items) => new JsonObject
    {
        ["data"] = new JsonArray(items.Cast<JsonNode>().ToArray()),
    }.ToJsonString();

    [Fact]
    public async Task HasEntitlementAsync_MatchesByCode_NotByName()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        // name deliberately differs from code — regression fixture.
        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "Pretty Display Name", "stable-code-x")));

        var hasByCode = await client.HasEntitlementAsync(licenseId, "stable-code-x");
        Assert.True(hasByCode);

        var hasByDisplayName = await client.HasEntitlementAsync(licenseId, "Pretty Display Name");
        Assert.False(hasByDisplayName);
    }

    [Fact]
    public async Task ListEntitlementsAsync_NeverReportsANextCursor_BecauseTheRouteIgnoresPageAfter()
    {
        // The server unions direct and policy-inherited rows here, so it dropped its keyset
        // cursor: `page[after]` is accepted for wire compatibility and then ignored. Reporting a
        // NextCursor would invite a loop that re-fetches page one forever.
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();

        var full = Enumerable.Range(0, 100)
            .Select(i => EntitlementResource(Guid.NewGuid(), $"E{i}", $"code-{i}"))
            .ToArray();
        handler.Enqueue(HttpStatusCode.OK, ListBody(full));

        var page = await client.ListEntitlementsAsync(licenseId);

        Assert.Equal(100, page.Items.Count);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListEntitlementsAsync_SendsAnExplicitLimitOf100_RatherThanTakingTheServersSilentDefaultOf25()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "A", "code-a")));

        await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task GetCachedEntitlements_IssuesExactlyOneRequest_AtTheServerMaximum()
    {
        // The old cursor loop exited after one iteration with no explicit limit, silently capping
        // the cache at the server's default of 25 rows — and caching that truncation with no TTL,
        // so HasEntitlementAsync answered a permanent false for everything past row 25.
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();

        var full = Enumerable.Range(0, 100)
            .Select(i => EntitlementResource(Guid.NewGuid(), $"E{i}", $"code-{i}"))
            .ToArray();
        handler.Enqueue(HttpStatusCode.OK, ListBody(full));

        Assert.True(await client.HasEntitlementAsync(licenseId, "code-99"));
        Assert.Single(handler.Requests);
        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListEntitlementsAsync_SurfacesTheInheritedFlag()
    {
        var (client, handler) = MakeClient();
        var direct = EntitlementResource(Guid.NewGuid(), "Direct", "code-direct");
        var inherited = EntitlementResource(Guid.NewGuid(), "Inherited", "code-inherited");
        inherited["attributes"]!["inherited"] = true;
        direct["attributes"]!["inherited"] = false;
        handler.Enqueue(HttpStatusCode.OK, ListBody(direct, inherited));

        var page = await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.False(page.Items[0].Inherited);
        Assert.True(page.Items[1].Inherited);
    }

    [Fact]
    public async Task GetEntitlementAsync_FetchesSingleResourceById()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var body = new JsonObject { ["data"] = EntitlementResource(entitlementId, "Feature", "feature-code") }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var entitlement = await client.GetEntitlementAsync(licenseId, entitlementId);

        Assert.Equal(entitlementId, entitlement.Id);
        Assert.Equal("feature-code", entitlement.Code);
    }

    [Fact]
    public async Task InvalidateEntitlementsCache_ForcesReFetch()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "A", "code-a")));
        await client.HasEntitlementAsync(licenseId, "code-a");
        Assert.Single(handler.Requests);

        // Cached — no new request.
        await client.HasEntitlementAsync(licenseId, "code-a");
        Assert.Single(handler.Requests);

        client.InvalidateEntitlementsCache(licenseId);
        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "A", "code-a")));
        await client.HasEntitlementAsync(licenseId, "code-a");
        Assert.Equal(2, handler.Requests.Count);
    }
}
