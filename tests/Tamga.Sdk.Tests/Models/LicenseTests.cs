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
    public void Scope_SerializesAllEightFields_WhenAllSet()
    {
        var scope = new Scope
        {
            Product = Guid.NewGuid(),
            Policy = Guid.NewGuid(),
            User = Guid.NewGuid(),
            Environment = Guid.NewGuid(),
            Entitlements = new[] { "feature-a" },
            Fingerprint = "fp-1",
            Version = "1.2.3",
            Checksum = "abc123",
        };

        var json = JsonSerializer.Serialize(scope, TamgaJsonOptions.Default);

        foreach (var field in new[] { "product", "policy", "user", "environment", "entitlements", "fingerprint", "version", "checksum" })
        {
            Assert.Contains($"\"{field}\"", json);
        }
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

    [Fact]
    public void License_FromResource_MapsAttributesAndRelationships()
    {
        var licenseId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var resource = new JsonApiResource<LicenseAttributes>
        {
            Type = "licenses",
            Id = licenseId,
            Attributes = new LicenseAttributes { Key = "LIC-1", Suspended = true, Uses = 5 },
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
        Assert.Equal(policyId, license.PolicyId);
        Assert.Null(license.ProductId);
    }
}
