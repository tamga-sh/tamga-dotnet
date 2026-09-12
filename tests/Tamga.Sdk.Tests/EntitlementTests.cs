using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
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

    // -----------------------------------------------------------------------------------------
    // Entitlement metering migration: `kind`, `max_value`, `current_value`, and the three new
    // increment/decrement/reset actions.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("flag", EntitlementKind.Flag)]
    [InlineData("meter", EntitlementKind.Meter)]
    public async Task ListEntitlementsAsync_DecodesKind(string wireValue, EntitlementKind expected)
    {
        var (client, handler) = MakeClient();
        var resource = EntitlementResource(Guid.NewGuid(), "Requests", "requests");
        resource["attributes"]!["kind"] = wireValue;
        handler.Enqueue(HttpStatusCode.OK, ListBody(resource));

        var page = await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.Equal(expected, page.Items[0].Kind);
    }

    [Fact]
    public async Task ListEntitlementsAsync_MissingKind_DefaultsToFlag()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, ListBody(EntitlementResource(Guid.NewGuid(), "Legacy", "legacy")));

        var page = await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.Equal(EntitlementKind.Flag, page.Items[0].Kind);
    }

    [Fact]
    public async Task ListEntitlementsAsync_LicenseScoped_SurfacesMaxValueAndCurrentValue()
    {
        // The license-scoped listing shape (§2.2 of the entitlement-metering migration): kind,
        // inherited, max_value AND current_value all present.
        var (client, handler) = MakeClient();
        var resource = EntitlementResource(Guid.NewGuid(), "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["inherited"] = false;
        resource["attributes"]!["max_value"] = 1000;
        resource["attributes"]!["current_value"] = 650;
        handler.Enqueue(HttpStatusCode.OK, ListBody(resource));

        var page = await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.Equal(EntitlementKind.Meter, page.Items[0].Kind);
        Assert.False(page.Items[0].Inherited);
        Assert.Equal(1000, page.Items[0].MaxValue);
        Assert.Equal(650, page.Items[0].CurrentValue);
    }

    [Fact]
    public async Task ListEntitlementsAsync_CurrentValueZero_DoesNotMeanNeverIncremented_WhenOnlyInherited()
    {
        // §2.2: 0 is a real value (never incremented) on a directly-attached row, but it also
        // shows up for an entitlement that is only inherited and has no counter row at all —
        // Inherited is what tells the two apart, not CurrentValue itself.
        var (client, handler) = MakeClient();
        var resource = EntitlementResource(Guid.NewGuid(), "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["inherited"] = true;
        resource["attributes"]!["max_value"] = 1000;
        resource["attributes"]!["current_value"] = 0;
        handler.Enqueue(HttpStatusCode.OK, ListBody(resource));

        var page = await client.ListEntitlementsAsync(Guid.NewGuid());

        Assert.True(page.Items[0].Inherited);
        Assert.Equal(0, page.Items[0].CurrentValue);
    }

    [Fact]
    public void PolicyScopedListing_CarriesMaxValueOnly_NeverCurrentValueOrInherited()
    {
        // §2.3: no client method lists policy-scoped entitlements today, but the wire shape is the
        // same EntitlementAttributes type this SDK already models — pin that it decodes correctly:
        // max_value present, current_value and inherited both absent (null).
        const string json = """
        {
            "type": "entitlements",
            "id": "11111111-1111-1111-1111-111111111111",
            "attributes": {
                "name": "API Requests",
                "code": "requests",
                "kind": "meter",
                "max_value": 500
            }
        }
        """;

        var resource = JsonSerializer.Deserialize<JsonApiResource<EntitlementAttributes>>(json, TamgaJsonOptions.Default);
        var entitlement = Entitlement.FromResource(resource!);

        Assert.Equal(EntitlementKind.Meter, entitlement.Kind);
        Assert.Equal(500, entitlement.MaxValue);
        Assert.Null(entitlement.CurrentValue);
        Assert.Null(entitlement.Inherited);
    }

    [Fact]
    public async Task IncrementEntitlementUsageAsync_NoAmountGiven_SendsNoBody_AndReturnsFreshResource()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var resource = EntitlementResource(entitlementId, "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["max_value"] = 1000;
        resource["attributes"]!["current_value"] = 1;
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = resource }.ToJsonString());

        var entitlement = await client.IncrementEntitlementUsageAsync(licenseId, entitlementId);

        Assert.Equal(1, entitlement.CurrentValue);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Request.Method);
        Assert.Contains($"/licenses/{licenseId}/entitlements/{entitlementId}/actions/increment", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task IncrementEntitlementUsageAsync_WithAmount_SendsFlatIncrementBody()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var resource = EntitlementResource(entitlementId, "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["current_value"] = 5;
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = resource }.ToJsonString());

        var entitlement = await client.IncrementEntitlementUsageAsync(licenseId, entitlementId, increment: 5);

        Assert.Equal(5, entitlement.CurrentValue);
        Assert.Equal("{\"increment\":5}", handler.Requests[0].Body);
    }

    [Fact]
    public async Task DecrementEntitlementUsageAsync_WithAmount_SendsFlatDecrementBody_AndReturnsFreshResource()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var resource = EntitlementResource(entitlementId, "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["current_value"] = 0;
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = resource }.ToJsonString());

        var entitlement = await client.DecrementEntitlementUsageAsync(licenseId, entitlementId, decrement: 3);

        Assert.Equal(0, entitlement.CurrentValue);
        Assert.Equal("{\"decrement\":3}", handler.Requests[0].Body);
    }

    [Fact]
    public async Task ResetEntitlementUsageAsync_SendsNoBody_AndReturnsFreshResource()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        var resource = EntitlementResource(entitlementId, "API Requests", "requests");
        resource["attributes"]!["kind"] = "meter";
        resource["attributes"]!["current_value"] = 0;
        handler.Enqueue(HttpStatusCode.OK, new JsonObject { ["data"] = resource }.ToJsonString());

        var entitlement = await client.ResetEntitlementUsageAsync(licenseId, entitlementId);

        Assert.Equal(0, entitlement.CurrentValue);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Request.Method);
        Assert.Contains($"/licenses/{licenseId}/entitlements/{entitlementId}/actions/reset", handler.Requests[0].Request.RequestUri!.AbsolutePath);
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task IncrementEntitlementUsageAsync_MeterLimitExceeded_ThrowsTypedExceptionWithEntitlementId()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var entitlementId = Guid.NewGuid();
        // Exact wire shape per the migration spec: 422 METER_LIMIT_EXCEEDED, meta.entitlement_id.
        var errorBody = "{\"errors\":[{\"id\":\"1\",\"status\":\"422\",\"code\":\"METER_LIMIT_EXCEEDED\",\"title\":\"Unprocessable Entity\",\"detail\":\"This meter has reached its limit\",\"meta\":{\"entitlement_id\":\"" + entitlementId + "\"}}]}";
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, errorBody);

        var ex = await Assert.ThrowsAsync<MeterLimitExceededException>(() =>
            client.IncrementEntitlementUsageAsync(licenseId, entitlementId, increment: 100));

        Assert.Equal("METER_LIMIT_EXCEEDED", ex.Error.Code);
        Assert.Equal(entitlementId, ex.EntitlementId);
    }
}
