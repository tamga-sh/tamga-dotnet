using System.Net;
using System.Text.Json;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class LicenseValidationTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions
        {
            AccountId = "acct-1",
            BaseUrl = "https://api.tamga.test",
            Auth = new AuthTransport.License("LIC-1"),
        };
        return (new TamgaClient(options, httpClient), handler);
    }

    private static string LicenseResourceJson(Guid id, string code = "VALID", bool valid = true) => $$"""
    {
        "data": {
            "type": "licenses",
            "id": "{{id}}",
            "attributes": { "key": "LIC-ABC", "suspended": false, "uses": 1 }
        },
        "meta": { "ts": "2024-01-01T00:00:00Z", "valid": {{valid.ToString().ToLowerInvariant()}}, "detail": "ok", "code": "{{code}}" }
    }
    """;

    [Fact]
    public async Task ValidateByKeyAsync_SendsExactKeyBody_NoScopeField()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, LicenseResourceJson(Guid.NewGuid()));

        await client.ValidateByKeyAsync("LIC-ABC-123");

        var body = handler.Requests[0].Body!;
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("LIC-ABC-123", doc.RootElement.GetProperty("key").GetString());
        Assert.False(doc.RootElement.TryGetProperty("scope", out _));
        Assert.Single(doc.RootElement.EnumerateObject());
        Assert.Equal("/v1/accounts/acct-1/licenses/actions/validate-key", handler.Requests[0].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ValidateByKeyAsync_AlwaysSendsConfiguredAuth_EvenThoughServerDoesNotEnforceIt()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, LicenseResourceJson(Guid.NewGuid()));

        await client.ValidateByKeyAsync("LIC-ABC-123");

        Assert.Equal("License", handler.Requests[0].Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task ValidateByIdAsync_SerializesAllEightScopeFields_AndSkipTouch()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, LicenseResourceJson(licenseId));

        var scope = new Scope
        {
            Product = Guid.NewGuid(),
            Policy = Guid.NewGuid(),
            User = Guid.NewGuid(),
            Environment = Guid.NewGuid(),
            Entitlements = new[] { "feature-x" },
            Fingerprint = "fp-1",
            Version = "2.0.0",
            Checksum = "deadbeef",
        };

        await client.ValidateByIdAsync(licenseId, scope, skipTouch: true);

        var body = handler.Requests[0].Body!;
        using var doc = JsonDocument.Parse(body);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.True(meta.GetProperty("skip_touch").GetBoolean());
        var scopeJson = meta.GetProperty("scope");
        foreach (var field in new[] { "product", "policy", "user", "environment", "entitlements", "fingerprint", "version", "checksum" })
        {
            Assert.True(scopeJson.TryGetProperty(field, out _), $"missing {field}");
        }
    }

    [Fact]
    public async Task ValidateByIdAsync_SkipTouch_DefaultsToFalse()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, LicenseResourceJson(licenseId));

        await client.ValidateByIdAsync(licenseId, scope: null, skipTouch: false);

        // No scope, no skip_touch => body omitted entirely per Client.cs's null-body optimization.
        Assert.Null(handler.Requests[0].Body);
    }

    [Fact]
    public async Task QuickValidateAsync_MapsFlatBody_WithoutExpectingDataKey()
    {
        var (client, handler) = MakeClient();
        const string body = """{"ts":"2024-01-01T00:00:00Z","valid":true,"detail":"ok","code":"VALID"}""";
        handler.Enqueue(HttpStatusCode.OK, body, contentType: "application/json");

        var result = await client.QuickValidateAsync(Guid.NewGuid());

        Assert.True(result.Valid);
        Assert.Equal(ValidationCode.Valid, result.Code);
        Assert.Equal("ok", result.Detail);
    }

    [Fact]
    public async Task ValidateByIdAsync_MapsValidationCode_AndLicenseFields()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, LicenseResourceJson(licenseId, code: "TOO_MANY_MACHINES", valid: false));

        var result = await client.ValidateByIdAsync(licenseId);

        Assert.False(result.Valid);
        Assert.Equal(ValidationCode.TooManyMachines, result.Code);
        Assert.Equal(licenseId, result.License.Id);
    }
}
