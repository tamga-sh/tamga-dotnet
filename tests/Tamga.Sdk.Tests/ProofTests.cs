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

        // account, dataset, machine (alphabetical) — NOT account, machine, dataset (the plan's
        // original literal-source-order wording, which is wrong; see Proof.cs remarks).
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
}
