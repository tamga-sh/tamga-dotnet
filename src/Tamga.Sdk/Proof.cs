using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
    /// <summary>The caller-supplied dataset to embed in the offline proof's signed payload.</summary>
    [JsonPropertyName("dataset")]
    public JsonObject Dataset { get; init; } = new();
}

/// <summary>Request body for <c>POST /machines/{id}/actions/generate-offline-proof</c>.</summary>
public sealed record GenerateOfflineProofRequest
{
    /// <summary>The request's <c>meta</c> object, containing the dataset to sign.</summary>
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
/// CRITICAL — canonical payload field order: it is tempting to assume the signed payload is
/// <c>{"account":{"id":...},"machine":{"id":...,"fingerprint":...},"dataset":...}</c>
/// in that literal source-code order. **That assumption is WRONG**: the server builds this
/// payload with <c>serde_json::json!(...)</c>, which constructs a <c>serde_json::Value</c>. Its
/// backing <c>serde_json::Map</c> is <c>BTreeMap</c>-backed (the <c>preserve_order</c>/
/// <c>indexmap</c> Cargo feature is enabled on neither the server nor the Rust SDK —
/// confirmed via <c>cargo tree</c>), so the actual wire bytes are recursively
/// **alphabetically key-sorted at every nesting level**, not literal source order:
/// <c>{"account":{"id":...},"dataset":{...sorted...},"machine":{"fingerprint":...,"id":...}}</c>
/// — note <c>dataset</c> sorts before <c>machine</c>, and inside <c>machine</c>,
/// <c>fingerprint</c> sorts before <c>id</c>. This applies recursively to whatever keys the
/// caller's own <c>dataset</c> object contains too. <see cref="BuildSignedPayload"/> implements
/// this via a canonical (alphabetical, recursive) JSON writer rather than a fixed-property-order
/// DTO.
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
/// <remarks>
/// Two details here are signature-critical, and both were wrong before:
///
/// <b>Escaping.</b> <c>System.Text.Json</c>'s default encoder escapes far more than JSON requires
/// — every non-ASCII character plus <c>+</c>, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>'</c>
/// — as XSS defence for HTML embedding. <c>serde_json</c> escapes only <c>"</c>, <c>\</c> and
/// control characters. Any of those characters in the payload therefore produced different bytes
/// from the ones the server signed, and verification failed on an authentic proof. A base64-shaped
/// hardware fingerprint routinely contains <c>+</c>, so this was not an edge case.
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> matches <c>serde_json</c>. "Unsafe"
/// here means "do not paste this into HTML without escaping" — this output is signature input, it
/// is never rendered.
///
/// <b>Key order.</b> <see cref="StringComparer.Ordinal"/> compares UTF-16 code units;
/// <c>BTreeMap&lt;String, _&gt;</c> compares UTF-8 bytes. The two disagree for any key containing
/// a character above U+FFFF (surrogate pairs sort before U+E000..U+FFFF in UTF-16, after them in
/// UTF-8), so key sets mixing emoji/CJK-extension keys with high-BMP ones would be ordered
/// differently from the signed bytes. Comparing UTF-8 bytes directly removes the whole class.
/// </remarks>
internal static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(JsonNode? node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteNode(writer, node);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Orders keys by their UTF-8 bytes, matching <c>BTreeMap&lt;String, _&gt;</c> — not by UTF-16 code unit.</summary>
    private sealed class Utf8OrdinalComparer : IComparer<string>
    {
        public static readonly Utf8OrdinalComparer Instance = new();

        public int Compare(string? x, string? y) =>
            Encoding.UTF8.GetBytes(x ?? string.Empty).AsSpan()
                .SequenceCompareTo(Encoding.UTF8.GetBytes(y ?? string.Empty));
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
                foreach (var key in obj.Select(kv => kv.Key).OrderBy(k => k, Utf8OrdinalComparer.Instance))
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
