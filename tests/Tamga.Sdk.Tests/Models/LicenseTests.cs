using System.Text.Json;
using Tamga.Sdk;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Models;

public class LicenseTests
{
    [Fact]
    public void Scope_SerializesOnlySetFields_NullsOmitted()
    {
        var scope = new Scope { Product = Guid.Parse("11111111-1111-1111-1111-111111111111") };

        var json = JsonSerializer.Serialize(scope, TamgaJsonOptions.Default);

        Assert.Contains("\"product\"", json);
        Assert.DoesNotContain("\"policy\"", json);
        Assert.DoesNotContain("\"user\"", json);
        Assert.DoesNotContain("\"environment\"", json);
        Assert.DoesNotContain("\"entitlements\"", json);
        Assert.DoesNotContain("\"fingerprint\"", json);
        Assert.DoesNotContain("\"version\"", json);
        Assert.DoesNotContain("\"checksum\"", json);
    }

    [Fact]
    public void Scope_SerializesTheSixEnforcedFields_AndNeverVersionOrChecksum()
    {
        // version/checksum are not "ignored" server-side any more: sending either one makes the
        // server answer 422 SCOPE_NOT_SUPPORTED and fail the WHOLE validate call, so a caller that
        // set them would stop getting a meta.valid at all. Dropping them client-side degrades such
        // a caller to a working validate instead of a hard error.
        var scope = new Scope
        {
            Product = Guid.NewGuid(),
            Policy = Guid.NewGuid(),
            User = Guid.NewGuid(),
            Environment = Guid.NewGuid(),
            Entitlements = new[] { "feature-a" },
            Fingerprint = "fp-1",
#pragma warning disable CS0618 // deliberately setting the obsolete members: the point is that they still do not reach the wire
            Version = "1.2.3",
            Checksum = "abc123",
#pragma warning restore CS0618
        };

        var json = JsonSerializer.Serialize(scope, TamgaJsonOptions.Default);

        foreach (var field in new[] { "product", "policy", "user", "environment", "entitlements", "fingerprint" })
        {
            Assert.Contains($"\"{field}\"", json);
        }

        Assert.DoesNotContain("\"version\"", json);
        Assert.DoesNotContain("\"checksum\"", json);
    }

    /// <summary>
    /// The server's license serializer emits 21 attributes; this SDK used to bind 7 of them, so
    /// `status`, `machines_count` and `max_machines` — the three a licensing client most needs —
    /// were silently dropped on the floor.
    /// </summary>
    [Fact]
    public void License_BindsEveryAttributeTheServerEmits()
    {
        const string json = """
        {
            "type": "licenses",
            "id": "11111111-1111-1111-1111-111111111111",
            "attributes": {
                "name": "Acme Pro",
                "key": "KEY-123",
                "status": "EXPIRING",
                "expiry": "2030-01-01T00:00:00Z",
                "suspended": false,
                "protected": true,
                "uses": 7,
                "scheme": "ED25519_SIGN",
                "encrypted": true,
                "strict": true,
                "floating": false,
                "max_machines": 5,
                "max_uses": 100,
                "max_users": 3,
                "last_validated_at": "2026-08-01T00:00:00Z",
                "last_check_in_at": "2026-08-02T00:00:00Z",
                "last_check_out_at": "2026-08-03T00:00:00Z",
                "machines_count": 4,
                "metadata": { "tier": "gold" },
                "created": "2024-01-01T00:00:00Z",
                "updated": "2026-08-04T00:00:00Z"
            }
        }
        """;

        var resource = JsonSerializer.Deserialize<JsonApiResource<LicenseAttributes>>(json, TamgaJsonOptions.Default);
        var license = License.FromResource(resource!);

        Assert.Equal("Acme Pro", license.Name);
        Assert.Equal("KEY-123", license.Key);
        Assert.Equal("EXPIRING", license.Status);
        Assert.False(license.Suspended);
        Assert.True(license.Protected);
        Assert.Equal(7, license.Uses);
        Assert.Equal("ED25519_SIGN", license.Scheme);
        Assert.True(license.Encrypted);
        Assert.True(license.Strict);
        Assert.False(license.Floating);
        Assert.Equal(5, license.MaxMachines);
        Assert.Equal(100, license.MaxUses);
        Assert.Equal(3, license.MaxUsers);
        Assert.Equal(4, license.MachinesCount);
        Assert.NotNull(license.LastCheckOutAt);
        Assert.NotNull(license.Created);
        Assert.NotNull(license.Updated);
        Assert.Equal("gold", license.Metadata!["tier"].GetString());
    }

    [Theory]
    [InlineData("ACTIVE")]
    [InlineData("INACTIVE")]
    [InlineData("EXPIRING")]
    [InlineData("EXPIRED")]
    [InlineData("SUSPENDED")]
    [InlineData("SOME_FUTURE_STATUS")]
    public void License_Status_IsAPlainString_SoUnknownValuesRoundTripInsteadOfThrowing(string status)
    {
        var json =
            "{\"type\":\"licenses\",\"id\":\"11111111-1111-1111-1111-111111111111\","
            + "\"attributes\":{\"status\":\"" + status + "\"}}";

        var resource = JsonSerializer.Deserialize<JsonApiResource<LicenseAttributes>>(json, TamgaJsonOptions.Default);

        Assert.Equal(status, License.FromResource(resource!).Status);
    }

    [Fact]
    public void SkipTouch_DefaultsToFalse_AndSerializesAsMetaSkipTouch()
    {
        var meta = new ValidateByIdRequestMeta();
        Assert.False(meta.SkipTouch);

        var request = new ValidateByIdRequest { Meta = meta };
        var json = JsonSerializer.Serialize(request, TamgaJsonOptions.Default);

        Assert.Contains("\"skip_touch\":false", json);
    }

    /// <summary>
    /// The four relationship ids are dead. The server's license serializer emits
    /// <c>{ type, id, attributes }</c> and nothing else — no <c>relationships</c> object exists on
    /// a <c>licenses</c> resource, so those properties could never have been populated from a real
    /// response. They are kept, marked <c>[Obsolete]</c>, only because deleting a public member is
    /// source-breaking; the mapper no longer pretends to read them.
    /// </summary>
    [Fact]
    public void License_FromResource_IgnoresRelationships_BecauseTheServerNeverSendsAny()
    {
        var licenseId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var resource = new JsonApiResource<LicenseAttributes>
        {
            Type = "licenses",
            Id = licenseId,
            Attributes = new LicenseAttributes { Key = "LIC-1", Suspended = true, Uses = 5 },
            // Hand-built, since no server response can contain this.
            Relationships = new Dictionary<string, JsonApiRelationship>
            {
                ["policy"] = new JsonApiRelationship { Data = new JsonApiResourceIdentifier { Type = "policies", Id = policyId } },
            },
        };

        var license = License.FromResource(resource);

        Assert.Equal(licenseId, license.Id);
        Assert.Equal("LIC-1", license.Key);
        Assert.True(license.Suspended);
        Assert.Equal(5, license.Uses);

#pragma warning disable CS0618 // the point of the test is that these obsolete members stay null
        Assert.Null(license.PolicyId);
        Assert.Null(license.ProductId);
        Assert.Null(license.UserId);
        Assert.Null(license.EnvironmentId);
#pragma warning restore CS0618
    }
}
