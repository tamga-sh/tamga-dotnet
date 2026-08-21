using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tamga.Sdk;
using Xunit;

namespace Tamga.Sdk.Tests;

public class ProofTests
{
    // Ported from tamga-rust/src/proof.rs's
    // `payload_json_matches_a_known_good_server_produced_fixture` test — same account/machine
    // IDs, fingerprint, and dataset, asserting byte-identical canonical output.
    private static readonly Guid FixtureAccountId = Guid.Parse("01926b3e-0000-7000-8000-000000000000");
    private static readonly Guid FixtureMachineId = Guid.Parse("01926b3e-1111-7000-8000-000000000000");
    private const string FixtureFingerprint = "fp-abc";

    [Fact]
    public void BuildSignedPayload_MatchesKnownGoodServerFixture_ByteForByte()
    {
        var dataset = JsonNode.Parse("""{"cores":4}""");

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);

        var expected = new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = FixtureAccountId.ToString() },
            ["dataset"] = new JsonObject { ["cores"] = 4 },
            ["machine"] = new JsonObject { ["fingerprint"] = FixtureFingerprint, ["id"] = FixtureMachineId.ToString() },
        }.ToJsonString();
        Assert.Equal(expected, json);
    }

    [Fact]
    public void BuildSignedPayload_TopLevelOrder_IsAlphabetical_NotLiteralSourceOrder()
    {
        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, JsonNode.Parse("""{"cores":4}"""));

        var accountPos = json.IndexOf("\"account\"", StringComparison.Ordinal);
        var datasetPos = json.IndexOf("\"dataset\"", StringComparison.Ordinal);
        var machinePos = json.IndexOf("\"machine\"", StringComparison.Ordinal);

        // account, dataset, machine (alphabetical) — NOT account, machine, dataset, which is the
        // literal source order and is wrong on the wire; see Proof.cs remarks.
        Assert.True(accountPos < datasetPos, json);
        Assert.True(datasetPos < machinePos, json);
    }

    [Fact]
    public void BuildSignedPayload_NestedMachineObject_SortsFingerprintBeforeId()
    {
        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, JsonNode.Parse("""{"cores":4}"""));

        var fingerprintPos = json.IndexOf("\"fingerprint\"", StringComparison.Ordinal);
        var idPosInMachine = json.LastIndexOf("\"id\"", StringComparison.Ordinal);

        Assert.True(fingerprintPos < idPosInMachine, json);
    }

    [Fact]
    public void BuildSignedPayload_NullDataset_DefaultsToEmptyObject()
    {
        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset: null);
        var expected = new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = FixtureAccountId.ToString() },
            ["dataset"] = new JsonObject(),
            ["machine"] = new JsonObject { ["fingerprint"] = FixtureFingerprint, ["id"] = FixtureMachineId.ToString() },
        }.ToJsonString();
        Assert.Equal(expected, json);
    }

    [Fact]
    public void Verify_RoundTrips_WithCorrectPayload()
    {
        using var rsa = RSA.Create(2048);
        var dataset = JsonNode.Parse("""{"cores":4}""");
        var payload = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(signature));

        Assert.True(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset));
    }

    [Fact]
    public void Verify_FailsClosed_WhenDatasetAlteredPostSigning()
    {
        using var rsa = RSA.Create(2048);
        var signedDataset = JsonNode.Parse("""{"cores":4}""");
        var payload = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, signedDataset);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(signature));

        var tamperedDataset = JsonNode.Parse("""{"cores":999}""");
        Assert.False(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, tamperedDataset));
    }

    [Fact]
    public void Verify_FailsClosed_WhenPayloadFieldOrderDiffers()
    {
        using var rsa = RSA.Create(2048);
        var dataset = new JsonObject { ["cores"] = 4 };

        var correctPayload = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);
        // Same field set, literal source order (account, machine, dataset; id before fingerprint)
        // instead of the canonical alphabetical order this SDK actually produces.
        var wrongOrderPayload = new JsonObject
        {
            ["account"] = new JsonObject { ["id"] = FixtureAccountId.ToString() },
            ["machine"] = new JsonObject { ["id"] = FixtureMachineId.ToString(), ["fingerprint"] = FixtureFingerprint },
            ["dataset"] = new JsonObject { ["cores"] = 4 },
        }.ToJsonString();
        Assert.NotEqual(correctPayload, wrongOrderPayload);

        var wrongSignature = rsa.SignData(Encoding.UTF8.GetBytes(wrongOrderPayload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(wrongSignature));

        Assert.False(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset));
    }

    [Fact]
    public void Verify_FailsClosed_WithWrongPublicKey()
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var dataset = JsonNode.Parse("""{"cores":4}""");
        var payload = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(signature));

        Assert.False(proof.Verify(otherRsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset));
    }

    [Theory]
    [InlineData("no-prefix-at-all")]
    [InlineData("v2x0.someSignatureBytes")]
    [InlineData("")]
    public void Parse_Throws_OnMalformedOrMissingPrefix(string input)
    {
        Assert.Throws<UnsupportedAlgorithmException>(() => MachineProof.Parse(input));
    }

    [Fact]
    public void Parse_Throws_WhenPrefixPresentButSignatureEmpty()
    {
        Assert.Throws<OfflineFileFormatException>(() => MachineProof.Parse("v1x0."));
    }

    [Fact]
    public void Parse_Succeeds_OnWellFormedProof()
    {
        var proof = MachineProof.Parse("v1x0.QUJD");
        Assert.Equal("QUJD", proof.RawSignatureBase64);
    }

    /// <summary>
    /// Regression test for the canonical-JSON escaping bug. System.Text.Json's default encoder is
    /// an HTML-safety encoder: it escapes every non-ASCII character plus <c>+</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>&amp;</c> and <c>'</c>. serde_json — which is what actually produced the
    /// bytes the server signed — escapes only <c>"</c>, <c>\</c> and control characters. Any of
    /// those characters anywhere in the payload therefore made this SDK reconstruct different
    /// bytes and reject an authentic proof.
    ///
    /// The two values here are the realistic triggers: base64-shaped hardware fingerprints
    /// routinely contain <c>+</c>, and dataset values carry user- or machine-supplied text.
    /// </summary>
    [Fact]
    public void BuildSignedPayload_DoesNotHtmlEscape_PlusSignsOrNonAscii()
    {
        const string fingerprintWithPlus = "aB+cD/eF+gh=";
        var dataset = new JsonObject { ["owner"] = "Necip Sünmaz", ["note"] = "a<b>c&d'e" };

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, fingerprintWithPlus, dataset);

        // Present literally, exactly as serde_json would have written them.
        Assert.Contains(fingerprintWithPlus, json);
        Assert.Contains("Necip Sünmaz", json);
        Assert.Contains("a<b>c&d'e", json);

        // And none of the \uXXXX escapes the default encoder would have emitted.
        Assert.DoesNotContain("\\u002B", json);
        Assert.DoesNotContain("\\u00FC", json);
        Assert.DoesNotContain("\\u003C", json);
        Assert.DoesNotContain("\\u0026", json);
        Assert.DoesNotContain("\\u0027", json);
    }

    [Fact]
    public void Verify_RoundTrips_ForAFingerprintWithAPlusAndANonAsciiDatasetValue()
    {
        // End-to-end form of the above: the server signs its own bytes, and this SDK has to
        // reconstruct them exactly or every genuine proof fails to verify.
        using var rsa = RSA.Create(2048);
        const string fingerprintWithPlus = "aB+cD/eF+gh=";
        var dataset = new JsonObject { ["owner"] = "Necip Sünmaz", ["tier"] = "prö" };

        // Byte-for-byte what serde_json writes: keys sorted, no whitespace, and only ", \\ and
        // control characters escaped.
        var serverJson =
            "{\"account\":{\"id\":\"" + FixtureAccountId + "\"},"
            + "\"dataset\":{\"owner\":\"Necip Sünmaz\",\"tier\":\"prö\"},"
            + "\"machine\":{\"fingerprint\":\"" + fingerprintWithPlus + "\",\"id\":\"" + FixtureMachineId + "\"}}";
        var serverBytes = Encoding.UTF8.GetBytes(serverJson);
        var signature = rsa.SignData(serverBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(signature));

        Assert.True(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, fingerprintWithPlus, dataset));
    }

    [Fact]
    public void BuildSignedPayload_OrdersDatasetKeysByUtf8Bytes_NotUtf16CodeUnits()
    {
        // StringComparer.Ordinal compares UTF-16 code units; BTreeMap<String, _> compares UTF-8
        // bytes. These two keys are exactly where they disagree: U+FF21 is the single UTF-16 code
        // unit 0xFF21, while U+1F600 is the surrogate pair 0xD83D 0xDE00 — so UTF-16 sorts the
        // emoji FIRST (0xD83D < 0xFF21). UTF-8 is the other way round: U+FF21 encodes to EF BC A1
        // and U+1F600 to F0 9F 98 80. serde_json writes the UTF-8 order, so that is the order the
        // signature covers.
        var dataset = new JsonObject
        {
            ["\U0001F600-emoji"] = 1,
            ["Ａ-fullwidth"] = 2,
        };

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);

        var fullwidthPos = json.IndexOf("-fullwidth", StringComparison.Ordinal);
        var emojiPos = json.IndexOf("-emoji", StringComparison.Ordinal);
        Assert.True(fullwidthPos < emojiPos, json);
    }

    /// <summary>
    /// The escaping divergences that survived the switch to
    /// <c>UnsafeRelaxedJsonEscaping</c>. That encoder closed <c>+</c>/<c>&lt;</c>/<c>&gt;</c>/
    /// <c>&amp;</c>/<c>'</c> and ordinary non-ASCII, but still escaped 1,056,491 scalars that
    /// <c>serde_json</c> writes raw — every one of which made an authentic proof fail to verify.
    /// Each case here is one class of that set, and each is a literal round-trip against the exact
    /// bytes serde_json produces (confirmed against serde_json 1.0.150, the server's pinned
    /// version).
    /// </summary>
    [Theory]
    [InlineData(0x7F, "U+007F DEL")]
    [InlineData(0xA0, "U+00A0 NBSP")]
    [InlineData(0x2028, "U+2028 line separator")]
    [InlineData(0x2029, "U+2029 paragraph separator")]
    [InlineData(0x1F600, "U+1F600 emoji (non-BMP)")]
    [InlineData(0x24B62, "U+24B62 CJK ext-B (non-BMP)")]
    [InlineData(0xE000, "U+E000 private use")]
    [InlineData(0xFFFE, "U+FFFE noncharacter")]
    public void BuildSignedPayload_EmitsRawUtf8_ForScalarsSerdeJsonDoesNotEscape(int scalar, string why)
    {
        var raw = char.ConvertFromUtf32(scalar);
        var dataset = new JsonObject { ["v"] = raw };

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);

        // Present as the literal character, not as any \uXXXX escape.
        Assert.True(json.Contains(raw, StringComparison.Ordinal), $"{why}: expected raw, got {json}");
        Assert.DoesNotContain("\\u", json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Control characters below <c>U+0020</c> ARE escaped by both — but <c>serde_json</c> writes
    /// lowercase hex and every built-in .NET encoder writes uppercase, so <c>\u001f</c> vs
    /// <c>\u001F</c> was itself a one-byte divergence on any dataset carrying a control character.
    /// </summary>
    [Fact]
    public void BuildSignedPayload_EscapesControlCharacters_WithSerdeJsonsLowercaseHex()
    {
        var dataset = new JsonObject { ["v"] = char.ConvertFromUtf32(0x1F) + char.ConvertFromUtf32(0x0B) };

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);

        Assert.Contains("\\u001f", json, StringComparison.Ordinal);
        Assert.Contains("\\u000b", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u001F", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000B", json, StringComparison.Ordinal);
    }

    /// <summary>Short escape forms still match serde_json, and <c>"</c>/<c>\</c> stay escaped — the property that stops a literal input forging an escape sequence.</summary>
    [Fact]
    public void BuildSignedPayload_KeepsSerdeJsonShortEscapes_AndAlwaysEscapesQuoteAndBackslash()
    {
        var dataset = new JsonObject { ["v"] = "a\tb\nc\rd\"e\\f" };

        var json = MachineProof.BuildSignedPayload(FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset);

        Assert.Contains("a\\tb\\nc\\rd\\\"e\\\\f", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// End-to-end: an emoji in the dataset is the realistic trigger. The server signs raw UTF-8;
    /// before the custom encoder this SDK rebuilt the same payload with an escaped surrogate pair
    /// and rejected a genuine proof.
    /// </summary>
    [Fact]
    public void Verify_RoundTrips_ForADatasetContainingAnEmojiAndU2028()
    {
        using var rsa = RSA.Create(2048);
        var emoji = char.ConvertFromUtf32(0x1F600);
        var lineSep = char.ConvertFromUtf32(0x2028);
        var dataset = new JsonObject { ["mood"] = emoji, ["sep"] = lineSep };

        var serverJson =
            "{\"account\":{\"id\":\"" + FixtureAccountId + "\"},"
            + "\"dataset\":{\"mood\":\"" + emoji + "\",\"sep\":\"" + lineSep + "\"},"
            + "\"machine\":{\"fingerprint\":\"" + FixtureFingerprint + "\",\"id\":\"" + FixtureMachineId + "\"}}";
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(serverJson), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(signature));

        Assert.True(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset));
    }

    /// <summary>
    /// Regression: <see cref="MachineProof.Verify"/> documents that it fails closed, and it already
    /// caught the base64 <see cref="FormatException"/> for that reason — but the JSON-writing step
    /// was unguarded. An unpaired UTF-16 surrogate is valid JSON grammar and
    /// <c>JsonNode.Parse</c> accepts it, so the realistic air-gapped flow (persist the dataset,
    /// reload it, verify) handed the caller an <see cref="InvalidOperationException"/> out of the
    /// writer instead of the promised <see langword="false"/>. Keys throw as well as values.
    /// </summary>
    [Theory]
    [InlineData("{\"a\":\"\\ud800\"}", "lone high surrogate in a value")]
    [InlineData("{\"a\":\"\\udc00\"}", "lone low surrogate in a value")]
    [InlineData("{\"a\":{\"b\":[\"\\ud800\"]}}", "lone high surrogate nested in an array")]
    [InlineData("{\"\\ud800\":1}", "lone high surrogate in a key")]
    public void Verify_ReturnsFalse_RatherThanThrowing_ForAnUnserializableDataset(string datasetJson, string why)
    {
        using var rsa = RSA.Create(2048);
        var dataset = JsonNode.Parse(datasetJson);
        Assert.NotNull(dataset);

        var proof = MachineProof.Parse("v1x0." + Convert.ToBase64String(new byte[256]));

        var ex = Record.Exception(() =>
            Assert.False(proof.Verify(rsa, FixtureAccountId, FixtureMachineId, FixtureFingerprint, dataset)));

        Assert.True(ex is null, $"{why}: Verify threw {ex?.GetType().Name} instead of returning false");
    }
}
