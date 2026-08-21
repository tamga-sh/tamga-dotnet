using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>The JSON:API <c>attributes</c> bag for a release-artifact resource.</summary>
/// <remarks>
/// <para>
/// ⚠ <b>The casing is mixed and a uniform camelCase mapping gets two of the fields wrong.</b>
/// <c>ArtifactAttributes</c> is declared <c>#[serde(rename_all = "camelCase")]</c> server-side
/// (<c>artifacts/serializer.rs:20</c>) — so <c>redirect_url</c> really does go out as
/// <c>redirectUrl</c> — but <c>created_at</c> and <c>updated_at</c> carry their own explicit
/// <c>#[serde(rename = "created")]</c> / <c>#[serde(rename = "updated")]</c>
/// (<c>serializer.rs:34-37</c>) that fire first. The wire names are therefore <c>created</c> and
/// <c>updated</c>, <b>not</b> <c>createdAt</c>/<c>updatedAt</c>. A port that applies camelCase
/// uniformly binds neither and reports two null timestamps on every artifact, silently — the
/// same exception-to-the-exception <see cref="ReleaseAttributes"/> carries.
/// </para>
/// <para>
/// Every <c>[JsonPropertyName]</c> below is therefore spelled out rather than left to a naming
/// policy.
/// </para>
/// <para>
/// Note what the serializer does <em>not</em> project: the <c>artifacts</c> row has
/// <c>release_id</c> and <c>environment_id</c> columns, but <c>ArtifactAttributes</c> lists
/// neither, so there is no way to get from an artifact back to its release through this
/// resource. Do not add a <c>ReleaseId</c> member that could only ever be
/// <see cref="Guid.Empty"/> — the same phantom-relationship trap that cost the licence and
/// machine models five members in 2.1.0.
/// </para>
/// </remarks>
public sealed record ArtifactAttributes
{
    /// <summary>The artifact's filename, e.g. <c>MyApp-1.2.3-win-x64.exe</c>.</summary>
    [JsonPropertyName("filename")]
    public string Filename { get; init; } = "";

    /// <summary>The file's type/extension, e.g. <c>exe</c>, <c>dmg</c>, <c>tar.gz</c>. Null when unset.</summary>
    [JsonPropertyName("filetype")]
    public string? Filetype { get; init; }

    /// <summary>The file's size in BYTES (unlike <c>machine.memory</c>/<c>machine.disk</c>, which are megabytes). Null until an upload completes.</summary>
    [JsonPropertyName("filesize")]
    public long? Filesize { get; init; }

    /// <summary>
    /// The publisher-supplied checksum of the file's bytes. Verify the downloaded bytes against
    /// this before executing them — see <see cref="TamgaClient.DownloadArtifactAsync"/>.
    /// </summary>
    /// <remarks>
    /// The encoding and algorithm are not stated in a separate field: the server infers both from
    /// the string's length and alphabet (hex or base64; MD5/SHA-1/SHA-224/SHA-256/SHA-384/SHA-512).
    /// It is whatever the publisher uploaded, so it is <see langword="null"/> on an artifact whose
    /// publisher supplied none — absent is not "verified".
    /// </remarks>
    [JsonPropertyName("checksum")]
    public string? Checksum { get; init; }

    /// <summary>The target platform, e.g. <c>windows</c>, <c>darwin</c>, <c>linux</c>. Null when unset.</summary>
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    /// <summary>The target architecture, e.g. <c>x64</c>, <c>arm64</c>. Null when unset.</summary>
    [JsonPropertyName("arch")]
    public string? Arch { get; init; }

    /// <summary>A detached code signature over the artifact, when the publisher attached one. Null otherwise.</summary>
    [JsonPropertyName("signature")]
    public string? Signature { get; init; }

    /// <summary>The artifact's upload/publication status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>
    /// The short-lived presigned storage URL. Populated <b>only</b> on the download action, and
    /// only when it was asked for with <c>?redirect=false</c>; absent on list and show.
    /// </summary>
    /// <remarks>
    /// ⚠ This URL points at the storage host, not at the Tamga API, and it carries its own
    /// signature in its query string. <b>Never send a Tamga credential to it.</b> See
    /// <see cref="TamgaClient.GetArtifactDownloadUrlAsync"/> for the whole hazard.
    /// </remarks>
    [JsonPropertyName("redirectUrl")]
    public string? RedirectUrl { get; init; }

    /// <summary>Arbitrary metadata attached to the artifact.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the artifact was created. Wire name is <c>created</c>, NOT <c>createdAt</c> — see the type-level remarks.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the artifact was last updated. Wire name is <c>updated</c>, NOT <c>updatedAt</c> — see the type-level remarks.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>
/// A release artifact — the binary a release actually distributes — flattened from the JSON:API
/// <c>data.attributes</c> + <c>data.id</c> shape.
/// </summary>
/// <remarks>
/// <see cref="Id"/> comes from <c>data.id</c>, a SIBLING of <c>attributes</c>, never from inside
/// it. Decoding a resource straight into a model instead of through the envelope is a defect this
/// repo has actually shipped: the component and process listings did it for the SDK's whole life
/// and returned correctly-counted rows of <see cref="Guid.Empty"/> ids and empty strings, because
/// <c>TamgaJsonOptions.Default</c> sets no <c>UnmappedMemberHandling</c> and the unknown
/// <c>data</c> key was ignored in silence.
/// </remarks>
public sealed record Artifact
{
    /// <summary>The artifact's unique ID, taken from <c>data.id</c>.</summary>
    public Guid Id { get; init; }

    /// <summary>The artifact's filename.</summary>
    public string Filename { get; init; } = "";

    /// <summary>The file's type/extension.</summary>
    public string? Filetype { get; init; }

    /// <summary>The file's size in bytes.</summary>
    public long? Filesize { get; init; }

    /// <summary>The publisher-supplied checksum — see <see cref="ArtifactAttributes.Checksum"/>.</summary>
    public string? Checksum { get; init; }

    /// <summary>The target platform.</summary>
    public string? Platform { get; init; }

    /// <summary>The target architecture.</summary>
    public string? Arch { get; init; }

    /// <summary>A detached code signature, when the publisher attached one.</summary>
    public string? Signature { get; init; }

    /// <summary>The artifact's upload/publication status.</summary>
    public string Status { get; init; } = "";

    /// <summary>The presigned storage URL — populated only by the download action, and null everywhere else.</summary>
    public string? RedirectUrl { get; init; }

    /// <summary>Arbitrary metadata attached to the artifact.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the artifact was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the artifact was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Flattens a raw JSON:API artifact resource into an <see cref="Artifact"/>.</summary>
    /// <param name="resource">The JSON:API resource object to flatten.</param>
    public static Artifact FromResource(JsonApiResource<ArtifactAttributes> resource)
    {
        var attrs = resource.Attributes ?? new ArtifactAttributes();
        return new Artifact
        {
            Id = resource.Id,
            Filename = attrs.Filename,
            Filetype = attrs.Filetype,
            Filesize = attrs.Filesize,
            Checksum = attrs.Checksum,
            Platform = attrs.Platform,
            Arch = attrs.Arch,
            Signature = attrs.Signature,
            Status = attrs.Status,
            RedirectUrl = attrs.RedirectUrl,
            Metadata = attrs.Metadata,
            Created = attrs.Created,
            Updated = attrs.Updated,
        };
    }
}

/// <summary>
/// A resolved artifact download: the artifact's metadata plus the short-lived presigned storage
/// URL its bytes can be fetched from.
/// </summary>
/// <remarks>
/// <para>
/// A separate type from <see cref="Artifact"/> so that <see cref="Url"/> can be non-nullable.
/// <see cref="Artifact.RedirectUrl"/> has to stay nullable because it is absent on every other
/// route, and a caller who has just asked for a download URL should not have to null-check the
/// one thing they asked for.
/// </para>
/// <para>
/// ⚠ <see cref="Url"/> is a bearer credential in itself — anyone holding it can fetch the bytes
/// until it expires, with no Tamga credential at all. Do not log it, and do not hand it to a
/// component that would attach an <c>Authorization</c> header or a session cookie to it.
/// </para>
/// </remarks>
public sealed record ArtifactDownload
{
    /// <summary>The artifact's metadata, as the download action returned it.</summary>
    public required Artifact Artifact { get; init; }

    /// <summary>
    /// The presigned storage URL. Valid for the TTL that was requested (default and bounds are
    /// documented on <see cref="TamgaClient.GetArtifactDownloadUrlAsync"/>).
    /// </summary>
    public required Uri Url { get; init; }
}
