using System.Net;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// <c>GET /accounts/{account_id}/signing-keys</c> — request shape and envelope decoding.
/// </summary>
public class SigningKeysClientTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) NewClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    /// <summary>
    /// The response the server actually sends: <c>id</c> is a SIBLING of <c>attributes</c> and
    /// carries the kid, and <c>publicKey</c> is the one camelCase field in the bag.
    /// </summary>
    private const string TwoKeyResponse = """
        {
          "data": [
            {
              "type": "signing-keys",
              "id": "905f28def18eaac0",
              "attributes": {
                "algorithm": "ed25519",
                "publicKey": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                "status": "active",
                "created": "2026-02-01T00:00:00Z"
              }
            },
            {
              "type": "signing-keys",
              "id": "51643eac9777b63a",
              "attributes": {
                "algorithm": "ed25519",
                "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                "status": "retired",
                "created": "2026-01-01T00:00:00Z",
                "retired": "2026-02-01T00:00:00Z"
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task ListSigningKeysAsync_CallsTheAccountScopedRoute()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, TwoKeyResponse);

        await client.ListSigningKeysAsync();

        var request = Assert.Single(handler.Requests).Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.tamga.test/v1/accounts/acct-1/signing-keys", request.RequestUri!.ToString());
    }

    /// <summary>
    /// Decoding must go through the JSON:API envelope. Reading <c>id</c> from inside
    /// <c>attributes</c> — or decoding straight into a flat model — is a defect this repo has
    /// actually shipped, and it is silent: every key comes back with an empty kid.
    /// </summary>
    [Fact]
    public async Task ListSigningKeysAsync_TakesTheKidFromTheResourceId_NotFromAttributes()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, TwoKeyResponse);

        var keys = await client.ListSigningKeysAsync();

        Assert.Equal(2, keys.Count);
        Assert.Equal("905f28def18eaac0", keys[0].KeyId);
        Assert.Equal("51643eac9777b63a", keys[1].KeyId);
        Assert.All(keys, k => Assert.NotEqual("", k.KeyId));

        // Every served id agrees with the id computed locally from its own public key.
        Assert.All(keys, k => Assert.True(k.KeyIdIsSelfConsistent));
    }

    [Fact]
    public async Task ListSigningKeysAsync_ReadsTheCamelCasePublicKeyAndEveryOtherAttribute()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, TwoKeyResponse);

        var keys = await client.ListSigningKeysAsync();

        Assert.Equal("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=", keys[0].PublicKey);
        Assert.Equal("ed25519", keys[0].Algorithm);
        Assert.Equal("active", keys[0].Status);
        Assert.Equal(DateTimeOffset.Parse("2026-02-01T00:00:00Z"), keys[0].Created);
        Assert.Null(keys[0].Retired);

        // `retired` is absent rather than null while a key is active, and present once it is not.
        Assert.Equal(DateTimeOffset.Parse("2026-02-01T00:00:00Z"), keys[1].Retired);
        Assert.True(keys[1].IsRetired);
    }

    /// <summary>Retired keys must come back — a client holding an older file needs exactly those.</summary>
    [Fact]
    public async Task ListSigningKeysAsync_ReturnsRetiredKeys()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, TwoKeyResponse);

        var keys = await client.ListSigningKeysAsync();

        Assert.Contains(keys, k => k.IsRetired);
    }

    [Fact]
    public async Task GetSigningKeySetAsync_ReturnsAUsableSetIndexedByServedId()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, TwoKeyResponse);

        var set = await client.GetSigningKeySetAsync();

        Assert.Equal(2, set.Count);
        Assert.NotNull(set.Find("51643eac9777b63a"));
        Assert.NotNull(set.Find("905f28def18eaac0"));
        Assert.Empty(set.InconsistentKeys);
    }

    /// <summary>
    /// An account that has never rotated has no rows at all. That is normal, not a failure — the
    /// table is written only by <c>rotate_ed25519</c>.
    /// </summary>
    [Fact]
    public async Task ListSigningKeysAsync_TreatsAnEmptySetAsNormal()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, """{"data": []}""");

        var keys = await client.ListSigningKeysAsync();

        Assert.Empty(keys);
    }

    /// <summary>
    /// A malformed body must surface as a typed error, never be swallowed. A silent catch here is
    /// exactly the defect that cost this repo its entire typed error surface for two years.
    /// </summary>
    [Fact]
    public async Task ListSigningKeysAsync_SurfacesMalformedJson_RatherThanSwallowingIt()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, "{ this is not json");

        var ex = await Assert.ThrowsAsync<OfflineFileFormatException>(() => client.ListSigningKeysAsync());
        Assert.Contains("malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ The route requires <c>account.read</c>, which <c>Role::LicenseToken</c> does not hold, so
    /// an embedded client authenticating with a license key gets 403 here every time. It must map
    /// like every other API error, so a caller can tell this apart and fall back to pinned keys.
    /// </summary>
    [Fact]
    public async Task ListSigningKeysAsync_MapsThe403ALicenseKeyAlwaysGets()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.Forbidden, """
            {"errors":[{"status":"403","code":"FORBIDDEN","detail":"insufficient permissions"}]}
            """);

        await Assert.ThrowsAsync<TamgaForbiddenException>(() => client.ListSigningKeysAsync());
    }

    /// <summary>An unknown future algorithm must not fail the decode of the whole key history.</summary>
    [Fact]
    public async Task ListSigningKeysAsync_DoesNotFailTheWholeDecodeOnAnUnknownAlgorithm()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, """
            {
              "data": [
                { "type": "signing-keys", "id": "aaaaaaaaaaaaaaaa",
                  "attributes": { "algorithm": "ml-dsa-44", "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "status": "active", "created": "2026-01-01T00:00:00Z" } },
                { "type": "signing-keys", "id": "51643eac9777b63a",
                  "attributes": { "algorithm": "ed25519", "publicKey": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "status": "active", "created": "2026-01-01T00:00:00Z" } }
              ]
            }
            """);

        var set = await client.GetSigningKeySetAsync();

        Assert.Equal(2, set.Keys.Count);
        Assert.Equal(1, set.Count);
        Assert.NotNull(set.Find("51643eac9777b63a"));
        Assert.Null(set.Find("aaaaaaaaaaaaaaaa"));
    }

    /// <summary>A literal <c>null</c> body deserializes to null and must be reported, not returned as an empty set.</summary>
    [Fact]
    public async Task ListSigningKeysAsync_ReportsANullBody()
    {
        var (client, handler) = NewClient();
        handler.Enqueue(HttpStatusCode.OK, "null");

        var ex = await Assert.ThrowsAsync<OfflineFileFormatException>(() => client.ListSigningKeysAsync());
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The envelope models <c>errors</c> for wire-shape completeness, the same way every other
    /// document type in this SDK does.
    /// </summary>
    [Fact]
    public void SigningKeyListDocument_ModelsTheErrorsMember()
    {
        var document = System.Text.Json.JsonSerializer.Deserialize<SigningKeyListDocument>(
            """{"errors":[{"status":"403","code":"FORBIDDEN","detail":"nope"}]}""",
            TamgaJsonOptions.Default)!;

        var error = Assert.Single(document.Errors!);
        Assert.Equal(403, error.Status);
        Assert.Equal("FORBIDDEN", error.Code);
        Assert.Empty(document.Data);
    }
}
