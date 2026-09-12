using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamga.Sdk.Models;

/// <summary>
/// Whether an entitlement is a boolean grant or a named, per-license counter. Wire values
/// <c>flag</c>/<c>meter</c>. Always present on every entitlement response — not an
/// <c>Option</c>/nullable addition, unlike <see cref="EntitlementAttributes.MaxValue"/>/
/// <see cref="EntitlementAttributes.CurrentValue"/> below.
/// </summary>
public enum EntitlementKind
{
    /// <summary>Wire value <c>flag</c> — a boolean grant. The only kind that existed before the entitlement-metering migration.</summary>
    Flag,

    /// <summary>Wire value <c>meter</c> — a named, per-license counter with an independent cap (<see cref="EntitlementAttributes.MaxValue"/>/<see cref="EntitlementAttributes.CurrentValue"/>).</summary>
    Meter,

    /// <summary>Fallback for any wire value not modeled above (including a future server addition). Not a real server value itself.</summary>
    Unknown,
}

/// <summary>
/// Converts <see cref="EntitlementKind"/> to/from its lowercase wire string, falling back to
/// <see cref="EntitlementKind.Unknown"/> on read rather than throwing — the same forward-compat
/// posture as <see cref="ValidationCodeConverter"/>, since <c>kind</c> is a brand-new field this
/// SDK cannot assume every future server value for.
/// </summary>
public sealed class EntitlementKindConverter : JsonConverter<EntitlementKind>
{
    /// <summary>Deserializes the wire string into an <see cref="EntitlementKind"/>.</summary>
    public override EntitlementKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "flag" => EntitlementKind.Flag,
            "meter" => EntitlementKind.Meter,
            _ => EntitlementKind.Unknown,
        };
    }

    /// <summary>Serializes the <see cref="EntitlementKind"/> as its lowercase wire string.</summary>
    public override void Write(Utf8JsonWriter writer, EntitlementKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            EntitlementKind.Meter => "meter",
            _ => "flag",
        });
    }
}

/// <summary>
/// A full entitlement resource. Despite being nested under <c>/licenses/{id}/entitlements</c> in
/// the URL, these are complete <see cref="Entitlement"/> resources, not lightweight
/// junction/relationship records.
/// </summary>
/// <remarks>
/// One flat type covers all three scopes this SDK reads (plain/account, license-scoped, policy-
/// scoped), the same way <see cref="Inherited"/> already distinguishes license-scoped responses
/// from the rest: <see cref="MaxValue"/>/<see cref="CurrentValue"/> are nullable and simply absent
/// (<see langword="null"/>) on a scope that does not carry them, rather than three duplicated
/// attribute types.
/// </remarks>
public sealed record EntitlementAttributes
{
    /// <summary>The entitlement's display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The stable, developer-facing identifier — match on this, NOT <see cref="Name"/> (a display label that can change).</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>
    /// Whether this is a boolean grant (<see cref="EntitlementKind.Flag"/>) or a named counter
    /// (<see cref="EntitlementKind.Meter"/>). Always present — every entitlement response carries
    /// it, unlike <see cref="Inherited"/>/<see cref="MaxValue"/>/<see cref="CurrentValue"/>.
    /// </summary>
    [JsonPropertyName("kind")]
    [JsonConverter(typeof(EntitlementKindConverter))]
    public EntitlementKind Kind { get; init; } = EntitlementKind.Flag;

    /// <summary>
    /// <see langword="true"/> when the license holds this entitlement through its policy rather
    /// than by a direct attachment.
    /// </summary>
    /// <remarks>
    /// Only <c>GET /licenses/{id}/entitlements</c> emits this; it is absent (and so
    /// <see langword="null"/>) on account-, policy- and release-scoped entitlement responses.
    ///
    /// An inherited entitlement behaves differently on every write path: attaching it again
    /// answers <c>422 ENTITLEMENT_ALREADY_INHERITED</c> for a <see cref="EntitlementKind.Flag"/>
    /// (a <see cref="EntitlementKind.Meter"/> may be attached directly even while inherited — see
    /// <see cref="TamgaClient.IncrementEntitlementUsageAsync"/>), detaching it answers
    /// <c>403 POLICY_ENTITLEMENT</c>, and fetching it by id answers <c>404</c> (see
    /// <see cref="TamgaClient.GetEntitlementAsync"/>). It still counts for
    /// <see cref="Scope.Entitlements"/>.
    /// </remarks>
    [JsonPropertyName("inherited")]
    public bool? Inherited { get; init; }

    /// <summary>
    /// The effective cap: the license's own override if it has one, else the policy's default,
    /// else <see langword="null"/> = unlimited. The same "nullable = unlimited" convention every
    /// other <c>max_*</c> field on licenses/policies already uses.
    /// </summary>
    /// <remarks>
    /// Present on the license-scoped listing (<c>GET /licenses/{id}/entitlements</c>) and the
    /// policy-scoped listing (<c>GET /policies/{id}/entitlements</c>); <see langword="null"/> on
    /// the plain/account-scoped listing, which does not carry it at all. Meaningless — present but
    /// not enforced — for <see cref="EntitlementKind.Flag"/>.
    /// </remarks>
    [JsonPropertyName("max_value")]
    public int? MaxValue { get; init; }

    /// <summary>The running per-license count for a meter.</summary>
    /// <remarks>
    /// Present ONLY on the license-scoped listing (<c>GET /licenses/{id}/entitlements</c>) —
    /// <see langword="null"/> everywhere else, including the policy-scoped listing, because usage
    /// is never pooled at the policy level. On the license-scoped listing it is always an integer,
    /// <c>0</c> if never incremented. <c>0</c> does not necessarily mean "never used": it also
    /// means "this entitlement is only inherited from the license's policy and has never been
    /// directly attached to this license", because only a direct <c>license_entitlements</c> row
    /// carries a counter at all — check <see cref="Inherited"/> to tell the two apart.
    /// </remarks>
    [JsonPropertyName("current_value")]
    public int? CurrentValue { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the entitlement.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the entitlement was created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the entitlement was last updated.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }
}

/// <summary>An entitlement resource, flattened from the JSON:API shape like <see cref="License"/>/<see cref="Machine"/>.</summary>
public sealed record Entitlement
{
    /// <summary>The entitlement's unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>The entitlement's display name.</summary>
    public string Name { get; init; } = "";

    /// <summary>The stable, developer-facing identifier — match on this, NOT <see cref="Name"/> (a display label that can change).</summary>
    public string Code { get; init; } = "";

    /// <summary>Whether this is a boolean grant or a named counter. See <see cref="EntitlementAttributes.Kind"/>.</summary>
    public EntitlementKind Kind { get; init; } = EntitlementKind.Flag;

    /// <summary>
    /// <see langword="true"/> when the license holds this through its policy rather than directly;
    /// <see langword="null"/> on responses that do not carry the flag. See
    /// <see cref="EntitlementAttributes.Inherited"/> for what it changes.
    /// </summary>
    public bool? Inherited { get; init; }

    /// <summary>The effective cap, or <see langword="null"/> for unlimited/not-carried-by-this-scope. See <see cref="EntitlementAttributes.MaxValue"/>.</summary>
    public int? MaxValue { get; init; }

    /// <summary>The running per-license meter count, or <see langword="null"/> on a scope that does not carry it. See <see cref="EntitlementAttributes.CurrentValue"/>.</summary>
    public int? CurrentValue { get; init; }

    /// <summary>Arbitrary key/value metadata attached to the entitlement.</summary>
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>When the entitlement was created.</summary>
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the entitlement was last updated.</summary>
    public DateTimeOffset? Updated { get; init; }

    /// <summary>Flattens a raw JSON:API entitlement resource into an <see cref="Entitlement"/>.</summary>
    /// <param name="resource">The JSON:API resource, with <c>data.id</c> and <c>data.attributes</c>.</param>
    /// <returns>The flattened <see cref="Entitlement"/>.</returns>
    public static Entitlement FromResource(JsonApiResource<EntitlementAttributes> resource)
    {
        var attrs = resource.Attributes ?? new EntitlementAttributes();
        return new Entitlement
        {
            Id = resource.Id,
            Name = attrs.Name,
            Code = attrs.Code,
            Kind = attrs.Kind,
            Inherited = attrs.Inherited,
            MaxValue = attrs.MaxValue,
            CurrentValue = attrs.CurrentValue,
            Metadata = attrs.Metadata,
            Created = attrs.Created,
            Updated = attrs.Updated,
        };
    }
}

/// <summary>
/// Request body for <c>POST /licenses/{license_id}/entitlements/{entitlement_id}/actions/increment</c>:
/// <c>{ "increment": 3 }</c>, body entirely optional.
/// </summary>
/// <remarks>
/// When <see cref="Increment"/> is <see langword="null"/>, no body is sent and the server defaults
/// to <c>1</c>. A <c>0</c> or negative value is not rejected — the server clamps it up to <c>1</c>
/// rather than erroring, the same rule the retired global counter's <c>increment-usage</c> used.
/// </remarks>
public sealed record IncrementEntitlementUsageRequest
{
    /// <summary>The amount to increment by, or <see langword="null"/> to let the server default to <c>1</c>.</summary>
    [JsonPropertyName("increment")]
    public int? Increment { get; init; }
}

/// <summary>
/// Request body for <c>POST /licenses/{license_id}/entitlements/{entitlement_id}/actions/decrement</c>:
/// <c>{ "decrement": 3 }</c>, body entirely optional.
/// </summary>
/// <remarks>
/// When <see cref="Decrement"/> is <see langword="null"/>, no body is sent and the server defaults
/// to <c>1</c>. A <c>0</c> or negative value is clamped up to <c>1</c>, same as
/// <see cref="IncrementEntitlementUsageRequest.Increment"/>. <c>current_value</c> floors at
/// <c>0</c> server-side; it never goes negative.
/// </remarks>
public sealed record DecrementEntitlementUsageRequest
{
    /// <summary>The amount to decrement by, or <see langword="null"/> to let the server default to <c>1</c>.</summary>
    [JsonPropertyName("decrement")]
    public int? Decrement { get; init; }
}
