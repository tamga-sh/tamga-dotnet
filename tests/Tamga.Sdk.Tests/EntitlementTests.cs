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

    private static string ListBody(JsonObject item, string? nextLink = null) => new JsonObject
    {
        ["data"] = new JsonArray { item },
        ["links"] = nextLink is null ? new JsonObject() : new JsonObject { ["next"] = nextLink },
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
    public async Task ListEntitlementsAsync_ThreadsKeysetPaginationCursor_AcrossMultiplePages()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "A", "code-a"), "/licenses/x/entitlements?page%5Bafter%5D=cursor-1"));
        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "B", "code-b")));

        var page1 = await client.ListEntitlementsAsync(licenseId);
        Assert.Single(page1.Items);
        Assert.Equal("cursor-1", page1.NextCursor);

        var page2 = await client.ListEntitlementsAsync(licenseId, after: page1.NextCursor);
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);
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
