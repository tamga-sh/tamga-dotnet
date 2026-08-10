using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

/// <summary>
/// <c>POST /machines/{id}/actions/generate-offline-proof</c> request body: <c>{ "meta": { "dataset": {...} } }</c>.
/// </summary>
public sealed record GenerateOfflineProofRequestMeta
{
    [JsonPropertyName("dataset")]
    public JsonObject Dataset { get; init; } = new();
}

/// <summary>Request body for <c>POST /machines/{id}/actions/generate-offline-proof</c>.</summary>
public sealed record GenerateOfflineProofRequest
{
    [JsonPropertyName("meta")]
    public required GenerateOfflineProofRequestMeta Meta { get; init; }
}

/// <summary>
/// Builds the byte-exact canonical JSON payload an offline proof's RSA signature covers, and
/// parses/verifies a <c>meta.proof</c> string returned by
/// <c>POST /machines/{id}/actions/generate-offline-proof</c>.
/// </summary>
/// <remarks>
/// Proof signing is ALWAYS RSA-2048 PKCS#1 v1.5 / SHA-256, regardless of the license's
/// <see cref="LicenseScheme"/> — this type never dispatches by scheme, unlike
/// <see cref="Checkout.MachineFile"/> (§F).
///
/// <c>meta.proof</c> has the shape <c>"v1x0.&lt;base64 signature&gt;"</c> — <see cref="Parse"/>
/// splits the version prefix from the signature and rejects malformed/missing-prefix strings.
///
/// CRITICAL — canonical payload field order: the plan's literal wording describes the signed
/// payload as <c>{"account":{"id":...},"machine":{"id":...,"fingerprint":...},"dataset":...}</c>
/// in that literal source-code order. **That wording is WRONG and has been corrected here** after
/// reading the actual server source
/// (<c>tamga-api/src/features/machines/generate_offline_proof.rs</c>): the server builds this
/// payload with <c>serde_json::json!(...)</c>, which constructs a <c>serde_json::Value</c>. Its
/// backing <c>serde_json::Map</c> is <c>BTreeMap</c>-backed (the <c>preserve_order</c>/
/// <c>indexmap</c> Cargo feature is enabled on neither <c>tamga-api</c> nor <c>tamga-rust</c> —
/// confirmed via <c>cargo tree</c>), so the actual wire bytes are recursively
/// **alphabetically key-sorted at every nesting level**, not literal source order:
/// <c>{"account":{"id":...},"dataset":{...sorted...},"machine":{"fingerprint":...,"id":...}}</c>
/// — note <c>dataset</c> sorts before <c>machine</c>, and inside <c>machine</c>,
/// <c>fingerprint</c> sorts before <c>id</c>. This applies recursively to whatever keys the
/// caller's own <c>dataset</c> object contains too. <see cref="BuildSignedPayload"/> implements
/// this via a canonical (alphabetical, recursive) JSON writer rather than a fixed-property-order
/// DTO — see <c>docs/plans/tamga-dotnet.plan.md</c> §H's checkbox note for this deviation.
/// </remarks>
public sealed class MachineProof
{
    /// <summary>The only version prefix this SDK recognizes.</summary>
    public const string VersionPrefix = "v1x0.";

    /// <summary>The base64-encoded RSA signature, with the version prefix already stripped.</summary>
    public string RawSignatureBase64 { get; }

    private MachineProof(string rawSignatureBase64)
    {
        RawSignatureBase64 = rawSignatureBase64;
    }

    /// <summary>Parses a <c>meta.proof</c> string, splitting the <c>"v1x0."</c> version prefix from the base64 signature.</summary>
    /// <exception cref="UnsupportedAlgorithmException">The string is missing the expected version prefix, or an unrecognized one is present.</exception>
    /// <exception cref="OfflineFileFormatException">The prefix is present but the remaining signature is empty.</exception>
    public static MachineProof Parse(string proof)
    {
        if (!proof.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            throw new UnsupportedAlgorithmException($"Unrecognized offline proof format: expected the '{VersionPrefix}' prefix.");
        }

        var signature = proof[VersionPrefix.Length..];
        if (signature.Length == 0)
        {
            throw new OfflineFileFormatException("Offline proof signature was empty after the version prefix.");
        }

        return new MachineProof(signature);
    }

    /// <summary>
    /// Builds the exact canonical JSON byte string the server signs — recursively alphabetically
    /// key-sorted, matching <c>serde_json::json!()</c>'s <c>BTreeMap</c>-backed output. See
    /// type-level remarks for why this is NOT literal source order.
    /// </summary>
    public static string BuildSignedPayload(Guid accountId, Guid machineId, string fingerprint, JsonNode? dataset)
    {
        var payload = new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = accountId.ToString() },
            ["machine"] = new JsonObject
            {
                ["id"] = machineId.ToString(),
                ["fingerprint"] = fingerprint,
            },
            ["dataset"] = dataset?.DeepClone() ?? new JsonObject(),
        };

        return CanonicalJson.Serialize(payload);
    }

    /// <summary>
    /// Verifies this proof's RSA-2048 PKCS#1 v1.5/SHA-256 signature against the reconstructed
    /// canonical payload. Fails closed (returns <see langword="false"/>) on any mismatch, including
    /// a <paramref name="dataset"/> that was altered post-signing.
    /// </summary>
    public bool Verify(RSA publicKey, Guid accountId, Guid machineId, string fingerprint, JsonNode? dataset)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(RawSignatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var payload = BuildSignedPayload(accountId, machineId, fingerprint, dataset);
        var message = Encoding.UTF8.GetBytes(payload);
        return Rsa.VerifyPkcs1(publicKey, message, signature);
    }
}

/// <summary>
/// Recursively alphabetically-key-sorted, whitespace-free JSON serialization of a
/// <see cref="JsonNode"/> tree — reproduces <c>serde_json::Value</c>'s <c>BTreeMap</c>-backed
/// serialization order (see <see cref="MachineProof"/>'s remarks). Arrays keep their original
/// element order (JSON arrays are ordered by spec; only object keys get sorted).
/// </summary>
internal static class CanonicalJson
{
    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, obj[key]);
                }

                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                // Leaf JsonValue (string/number/bool) — no ordering concern, write as-is.
                node.WriteTo(writer);
                break;
        }
    }
}
