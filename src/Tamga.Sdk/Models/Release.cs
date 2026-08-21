using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>The JSON:API <c>attributes</c> bag for a release resource.</summary>
/// <remarks>
/// ⚠ This is the one resource in the API whose attributes are <b>camelCase</b>
/// (<c>productId</c>), not snake_case. <c>created</c>/<c>updated</c> are the exceptions to the
/// exception — they carry explicit renames server-side and stay unsuffixed.
/// </remarks>
public sealed record ReleaseAttributes
{
    /// <summary>The product this release belongs to. A plain attribute — no <c>relationships</c> object exists on any resource.</summary>
    [JsonPropertyName("productId")]
    public Guid ProductId { get; init; }

    /// <summary>The release's display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The semver version string.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    /// <summary>The distribution channel this release was published to (e.g. <c>stable</c>, <c>beta</c>, <c>alpha</c>, <c>dev</c>).</summary>
    [JsonPropertyName("channel")]
    public string Channel { get; init; } = "";

    /// <summary>The release's publication status.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>An optional VCS tag. Omitted from the response entirely when unset, rather than sent as null.</summary>
    [JsonPropertyName("tag")]
    public string? Tag { get; init; }

    /// <summary>Arbitrary metadata attached to the release.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the release was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the release was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>A release resource, flattened from the JSON:API <c>data.attributes</c> + <c>data.id</c> shape.</summary>
public sealed record Release
{
    /// <summary>The release's unique ID.</summary>
    public Guid Id { get; init; }

    /// <summary>The product this release belongs to.</summary>
    public Guid ProductId { get; init; }

    /// <summary>The release's display name.</summary>
    public string? Name { get; init; }

    /// <summary>The semver version string.</summary>
    public string Version { get; init; } = "";

    /// <summary>The distribution channel this release was published to.</summary>
    public string Channel { get; init; } = "";

    /// <summary>The release's publication status.</summary>
    public string Status { get; init; } = "";

    /// <summary>An optional VCS tag.</summary>
    public string? Tag { get; init; }

    /// <summary>Arbitrary metadata attached to the release.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the release was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the release was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Flattens a raw JSON:API release resource into a <see cref="Release"/>.</summary>
    /// <param name="resource">The JSON:API resource object to flatten.</param>
    public static Release FromResource(JsonApiResource<ReleaseAttributes> resource)
    {
        var attrs = resource.Attributes ?? new ReleaseAttributes();
        return new Release
        {
            Id = resource.Id,
            ProductId = attrs.ProductId,
            Name = attrs.Name,
            Version = attrs.Version,
            Channel = attrs.Channel,
            Status = attrs.Status,
            Tag = attrs.Tag,
            Metadata = attrs.Metadata,
            Created = attrs.Created,
            Updated = attrs.Updated,
        };
    }
}

/// <summary>
/// The query for <c>GET /releases/actions/upgrade</c>. Four of the six parameters are required by
/// the server, not optional.
/// </summary>
/// <remarks>
/// ⚠ A missing required parameter is rejected by the framework's own query extractor, before any
/// handler code runs, so the answer is a <b>plain-text 400</b> rather than the JSON:API error
/// envelope every other failure on this API produces. That is why all four are
/// <see langword="required"/> here: the SDK cannot give a caller a typed error for a mistake it
/// let through.
/// </remarks>
public sealed record UpgradeCheckRequest
{
    /// <summary>The product whose releases to search. Required.</summary>
    public required Guid ProductId { get; init; }

    /// <summary>The platform to match, e.g. <c>windows</c>. Required.</summary>
    public required string Platform { get; init; }

    /// <summary>The artifact filetype to match, e.g. <c>exe</c>. Required.</summary>
    public required string Filetype { get; init; }

    /// <summary>The caller's current semver version. Required.</summary>
    /// <remarks>Not valid semver answers <c>422 INVALID_VERSION</c>.</remarks>
    public required string Version { get; init; }

    /// <summary>
    /// Restrict the search to one channel. Omitting it matches <b>every</b> channel — including
    /// <c>alpha</c> and <c>dev</c>, which is rarely what a shipped auto-updater wants.
    /// </summary>
    public string? Channel { get; init; }

    /// <summary>
    /// A semver requirement bounding how far the upgrade may go. Omitting it defaults to
    /// <c>~major.minor.patch</c> of <see cref="Version"/>, i.e. patch-level upgrades only.
    /// </summary>
    /// <remarks>Not a valid semver requirement answers <c>422 INVALID_CONSTRAINT</c>.</remarks>
    public string? Constraint { get; init; }
}

/// <summary>
/// The result of an upgrade check: the release the caller may move to, or nothing.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b><see cref="Release"/> being <see langword="null"/> does NOT mean "you are up to date."</b>
/// The server answers <c>204 No Content</c> in two different situations and deliberately makes them
/// indistinguishable:
/// </para>
/// <list type="number">
/// <item><description>There is no newer release matching the product, platform, filetype, channel and constraint.</description></item>
/// <item><description>There <b>is</b> a newer release, but this caller's license is expired under a policy that
/// stops delivering builds published after expiry — so it may not have it.</description></item>
/// </list>
/// <para>
/// The server's own comment gives the reason: answering <c>403</c> in the second case would leak
/// "a newer version exists but you cannot have it", and <c>204</c> is the truthful answer for a
/// license that is not entitled to move further. There is no client-side way to tell the two apart
/// and there is not meant to be one. The honest phrasing for a UI is
/// <em>"no update is available to you"</em>, never <em>"you are on the latest version"</em>.
/// </para>
/// <para>
/// A <b>suspended</b> license is the one nearby case that is not silent: it answers
/// <c>403 FORBIDDEN</c> (<see cref="TamgaForbiddenException"/>) rather than <c>204</c>.
/// </para>
/// </remarks>
public sealed record UpgradeCheck
{
    /// <summary>The release offered, or <see langword="null"/> when none is — see the type-level remarks before interpreting <see langword="null"/>.</summary>
    public Release? Release { get; init; }

    /// <summary>
    /// Whether the server offered a release. Named for what it actually means — an upgrade was
    /// offered — rather than "out of date", which <see langword="false"/> does not establish.
    /// </summary>
    public bool UpgradeOffered => Release is not null;
}
