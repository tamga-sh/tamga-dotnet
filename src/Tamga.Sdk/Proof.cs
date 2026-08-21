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
    /// <remarks>
    /// "Fails closed" is meant literally: this method does not throw for bad input. Two inputs can
    /// fail before the signature is even reached, and both return <see langword="false"/> rather
    /// than propagating —
    /// <list type="bullet">
    /// <item><description>a <see cref="RawSignatureBase64"/> that is not valid base64; and</description></item>
    /// <item><description>a <paramref name="dataset"/> that cannot be written back out as JSON.
    /// The realistic case is an unpaired UTF-16 surrogate: <c>{"a":"\ud800"}</c> is valid JSON
    /// grammar and <see cref="JsonNode.Parse(string,JsonNodeOptions?,JsonDocumentOptions)"/>
    /// accepts it, but writing it throws. That reaches a caller through the ordinary air-gapped
    /// flow — persist the dataset as JSON, reload it later, verify — and an unhandled exception
    /// there crashes an offline verification path that promised a boolean.</description></item>
    /// </list>
    /// <see cref="BuildSignedPayload"/> deliberately still throws for the second case: it is a
    /// builder, and a caller assembling a payload wants to hear about malformed input.
    /// </remarks>
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

        string payload;
        try
        {
            payload = BuildSignedPayload(accountId, machineId, fingerprint, dataset);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // A dataset that will not serialize cannot match what the server signed, so this is a
            // verification failure, not a caller error to propagate — same reasoning as the
            // base64 catch above. InvalidOperationException is what Utf8JsonWriter raises for an
            // unpaired surrogate, in a value or a key.
            return false;
        }

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
/// <b>Escaping.</b> <c>serde_json</c> escapes exactly three things: <c>"</c>, <c>\</c>, and
/// control characters below <c>U+0020</c> (short forms <c>\b</c>/<c>\t</c>/<c>\n</c>/<c>\f</c>/<c>\r</c>
/// where they exist, otherwise <c>\u00</c> plus LOWERCASE hex). Everything else — <c>+</c>,
/// <c>&lt;</c>, <c>&amp;</c>, <c>'</c>, <c>U+007F</c>, <c>U+2028</c>, emoji, unassigned code
/// points — goes out as raw UTF-8. Nothing in <c>System.Text.Json</c> reproduces that, which is
/// why <see cref="SerdeJsonEscaper"/> below exists.
///
/// The default encoder was the original bug: it escapes every non-ASCII character plus <c>+</c>,
/// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and <c>'</c> as XSS defence, and a base64-shaped hardware
/// fingerprint routinely contains <c>+</c>, so authentic proofs failed verification.
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> was the first fix and closed most of
/// it, but NOT all of it: measured across the entire scalar range on net8.0 it still diverges from
/// <c>serde_json</c> on <b>1,056,491 code points</b> —
/// <list type="bullet">
/// <item><description>every non-BMP scalar (all emoji, CJK ext-B and friends), emitted as an escaped UTF-16 surrogate pair instead of raw UTF-8;</description></item>
/// <item><description><c>U+007F</c>..<c>U+00A0</c> — DEL, the C1 controls, and NBSP;</description></item>
/// <item><description><c>U+2028</c>/<c>U+2029</c>, which .NET escapes deliberately because they are ECMAScript line terminators — so the relaxed encoder will never stop doing it;</description></item>
/// <item><description>every unassigned or private-use BMP scalar (roughly 340 further ranges); and</description></item>
/// <item><description>UPPERCASE hex — <c>\u001F</c> where <c>serde_json</c> writes <c>\u001f</c>.</description></item>
/// </list>
/// Every one of those fails CLOSED: an authentic proof simply fails to verify, and no different
/// literal input can forge an escape sequence, because a literal backslash is itself escaped. But
/// a proof that cannot be verified is still broken for any caller whose <c>dataset</c> or
/// fingerprint contains one of those characters — an emoji is enough.
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
        Encoder = SerdeJsonEscaper.Instance,
    };

    /// <summary>
    /// Escapes exactly what <c>serde_json</c> escapes and nothing else: <c>"</c>, <c>\</c>, and
    /// scalars below <c>U+0020</c>. Every other scalar — including <c>U+007F</c>, <c>U+2028</c>,
    /// <c>U+2029</c>, private-use and unassigned code points, and everything above the BMP — is
    /// passed through as raw UTF-8, because that is what the server signed.
    /// </summary>
    /// <remarks>
    /// Verified byte-for-byte against <c>serde_json</c> 1.0.150 (the server's pinned version) over
    /// all 1,112,064 Unicode scalars, as both string values and object keys.
    ///
    /// The two <see langword="unsafe"/> overrides are forced by the framework:
    /// <see cref="TextEncoder"/> declares them with <c>char*</c>, so there is no safe way to
    /// implement a custom encoder. Both bounds-check before writing, and neither retains the
    /// pointer. This is the only unsafe code in the assembly — see the note in
    /// <c>Tamga.Sdk.csproj</c>.
    /// </remarks>
    private sealed class SerdeJsonEscaper : JavaScriptEncoder
    {
        internal static readonly SerdeJsonEscaper Instance = new();

        /// <summary>Lowercase, matching <c>serde_json</c>'s <c>HEX_DIGITS</c>; .NET's own encoders emit uppercase.</summary>
        private const string HexDigits = "0123456789abcdef";

        /// <summary>Longest output for one input char: <c>\u00xx</c>.</summary>
        public override int MaxOutputCharactersPerInputCharacter => 6;

        public override bool WillEncode(int unicodeScalar) =>
            unicodeScalar < 0x20 || unicodeScalar == '"' || unicodeScalar == '\\';

        public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
        {
            for (var i = 0; i < textLength; i++)
            {
                if (WillEncode(text[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        public override unsafe bool TryEncodeUnicodeScalar(
            int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        {
            numberOfCharactersWritten = 0;

            var shortForm = unicodeScalar switch
            {
                0x08 => 'b',
                0x09 => 't',
                0x0A => 'n',
                0x0C => 'f',
                0x0D => 'r',
                '"' => '"',
                '\\' => '\\',
                _ => '\0',
            };

            if (shortForm != '\0')
            {
                if (bufferLength < 2)
                {
                    return false;
                }

                buffer[0] = '\\';
                buffer[1] = shortForm;
                numberOfCharactersWritten = 2;
                return true;
            }

            // Defensive: WillEncode returns false here, so the framework should never ask.
            if (unicodeScalar is < 0 or >= 0x20)
            {
                return false;
            }

            if (bufferLength < 6)
            {
                return false;
            }

            buffer[0] = '\\';
            buffer[1] = 'u';
            buffer[2] = '0';
            buffer[3] = '0';
            buffer[4] = HexDigits[(unicodeScalar >> 4) & 0xF];
            buffer[5] = HexDigits[unicodeScalar & 0xF];
            numberOfCharactersWritten = 6;
            return true;
        }
    }

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
