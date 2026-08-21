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
}
