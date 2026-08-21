using System.Reflection;
using System.Text.Json;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Models;

/// <summary>
/// <see cref="Artifact"/> / <see cref="ArtifactAttributes"/> wire-shape pinning, independent of
/// the transport.
/// </summary>
public class ArtifactTests
{
    /// <summary>
    /// The two timestamps are the exception to this resource's camelCase rule, and they are the
    /// half a port gets wrong. Asserted on the attribute metadata itself so the claim holds even
    /// if no test happened to deserialize a document.
    /// </summary>
    [Theory]
    [InlineData(nameof(ArtifactAttributes.Created), "created")]
    [InlineData(nameof(ArtifactAttributes.Updated), "updated")]
    [InlineData(nameof(ArtifactAttributes.RedirectUrl), "redirectUrl")]
    [InlineData(nameof(ArtifactAttributes.Filename), "filename")]
    [InlineData(nameof(ArtifactAttributes.Filetype), "filetype")]
    [InlineData(nameof(ArtifactAttributes.Filesize), "filesize")]
    [InlineData(nameof(ArtifactAttributes.Checksum), "checksum")]
    [InlineData(nameof(ArtifactAttributes.Platform), "platform")]
    [InlineData(nameof(ArtifactAttributes.Arch), "arch")]
    [InlineData(nameof(ArtifactAttributes.Signature), "signature")]
    [InlineData(nameof(ArtifactAttributes.Status), "status")]
    [InlineData(nameof(ArtifactAttributes.Metadata), "metadata")]
    public void ArtifactAttributes_PinsEveryWireName(string property, string expectedWireName)
    {
        var attribute = typeof(ArtifactAttributes)
            .GetProperty(property, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(expectedWireName, attribute.Name);
    }

    /// <summary>
    /// The serializer projects neither <c>release_id</c> nor <c>environment_id</c>, so a member for
    /// either could only ever answer <see cref="Guid.Empty"/> — the phantom-relationship trap that
    /// cost the licence and machine models five members in 2.1.0.
    /// </summary>
    [Theory]
    [InlineData("ReleaseId")]
    [InlineData("EnvironmentId")]
    public void Artifact_HasNoPhantomRelationshipId(string name)
    {
        Assert.Null(typeof(Artifact).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(typeof(ArtifactAttributes).GetProperty(name, BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// <c>data.id</c> is a sibling of <c>attributes</c>. A resource whose <c>attributes</c> is
    /// missing entirely still yields the right id — the id never comes from inside the bag.
    /// </summary>
    [Fact]
    public void FromResource_TakesTheIdFromTheSibling_EvenWithNoAttributes()
    {
        var id = Guid.NewGuid();
        var resource = new JsonApiResource<ArtifactAttributes> { Type = "artifacts", Id = id };

        var artifact = Artifact.FromResource(resource);

        Assert.Equal(id, artifact.Id);
        Assert.Equal("", artifact.Filename);
        Assert.Equal("", artifact.Status);
        Assert.Null(artifact.RedirectUrl);
        Assert.Null(artifact.Created);
    }

    /// <summary>Every attribute is carried across, so the flattening cannot silently drop a field.</summary>
    [Fact]
    public void FromResource_CarriesEveryAttributeAcross()
    {
        var id = Guid.NewGuid();
        var resource = new JsonApiResource<ArtifactAttributes>
        {
            Type = "artifacts",
            Id = id,
            Attributes = new ArtifactAttributes
            {
                Filename = "app.dmg",
                Filetype = "dmg",
                Filesize = 42,
                Checksum = "abc",
                Platform = "darwin",
                Arch = "arm64",
                Signature = "sig",
                Status = "UPLOADED",
                RedirectUrl = "https://storage.test/blob",
                Metadata = new Dictionary<string, JsonElement>(),
                Created = DateTimeOffset.UnixEpoch,
                Updated = DateTimeOffset.UnixEpoch.AddDays(1),
            },
        };

        var artifact = Artifact.FromResource(resource);

        Assert.Equal(id, artifact.Id);
        Assert.Equal("app.dmg", artifact.Filename);
        Assert.Equal("dmg", artifact.Filetype);
        Assert.Equal(42, artifact.Filesize);
        Assert.Equal("abc", artifact.Checksum);
        Assert.Equal("darwin", artifact.Platform);
        Assert.Equal("arm64", artifact.Arch);
        Assert.Equal("sig", artifact.Signature);
        Assert.Equal("UPLOADED", artifact.Status);
        Assert.Equal("https://storage.test/blob", artifact.RedirectUrl);
        Assert.NotNull(artifact.Metadata);
        Assert.Equal(DateTimeOffset.UnixEpoch, artifact.Created);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(1), artifact.Updated);
    }
}
