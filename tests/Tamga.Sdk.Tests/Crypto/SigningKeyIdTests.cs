using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

/// <summary>One entry from <c>Fixtures/SigningKeys/signing-key-ids.json</c>.</summary>
public sealed record SigningKeyIdVector
{
    /// <summary>The vector's name, used as the test case label.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The public key string, exactly as the server stores and publishes it.</summary>
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; init; } = "";

    /// <summary>The expected 16-hex-character id.</summary>
    [JsonPropertyName("kid")]
    public string Kid { get; init; } = "";

    /// <summary>Why this particular key is in the set.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";
}

/// <summary>The negative vector: the same key under the correct rule and under the wrong one.</summary>
public sealed record SigningKeyIdNegativeVector
{
    /// <summary>The public key string.</summary>
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; init; } = "";

    /// <summary>What a correct port produces.</summary>
    [JsonPropertyName("correctKid")]
    public string CorrectKid { get; init; } = "";

    /// <summary>What a port that base64-decodes before hashing produces.</summary>
    [JsonPropertyName("wrongKidIfDecodedFirst")]
    public string WrongKidIfDecodedFirst { get; init; } = "";
}

/// <summary>The whole vector document.</summary>
public sealed record SigningKeyIdVectorFile
{
    /// <summary>The positive vectors.</summary>
    [JsonPropertyName("vectors")]
    public IReadOnlyList<SigningKeyIdVector> Vectors { get; init; } = Array.Empty<SigningKeyIdVector>();

    /// <summary>The negative vector.</summary>
    [JsonPropertyName("negative")]
    public SigningKeyIdNegativeVector Negative { get; init; } = new();
}

/// <summary>
/// Pins <see cref="Ed25519.KeyId(string)"/> against vectors this SDK did not generate.
/// </summary>
/// <remarks>
/// WHY THESE VECTORS: they come from an independent SHA-256 implementation, confirmed against
/// <c>tamga-rust</c>'s committed vector — see <c>Fixtures/SigningKeys/PROVENANCE.md</c>. A fixture
/// this SDK produced could only prove this SDK agrees with itself, which is exactly how a
/// fleet-wide misreading of the <c>.machine</c> wire format stayed green in CI for two years.
///
/// The algorithm is additionally cross-checked against the twelve server-issued machine-file
/// fixtures already in this repo, which carry real server-generated <c>kid</c> values.
/// </remarks>
public class SigningKeyIdTests
{
    private static readonly SigningKeyIdVectorFile Vectors = LoadVectors();

    /// <summary>Every positive vector, by name.</summary>
    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Vectors.Vectors)
        {
            data.Add(vector.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void KeyId_ReproducesEveryIndependentlyGeneratedVector(string name)
    {
        var vector = Vectors.Vectors.Single(v => v.Name == name);

        Assert.Equal(vector.Kid, Ed25519.KeyId(vector.PublicKey));
    }

    /// <summary>
    /// THE TRAP. The server hands <c>key_id</c> the stored base64 <c>&amp;str</c>, never the 32
    /// decoded bytes (<c>license_file.rs:70-77</c>) — and getting it wrong fails silently, because
    /// decoding first yields a different but perfectly self-consistent id.
    /// </summary>
    /// <remarks>
    /// Asserting only the positive does NOT catch this: it would still pass against an
    /// implementation that hashed the right thing for the wrong reason. This pins the specific
    /// wrong answer too, so an implementation that decodes first fails here by name.
    /// </remarks>
    [Fact]
    public void KeyId_HashesTheBase64String_NotTheDecodedBytes()
    {
        var negative = Vectors.Negative;

        Assert.Equal(negative.CorrectKid, Ed25519.KeyId(negative.PublicKey));

        // What a port that base64-decoded first would produce. Recomputed here rather than merely
        // quoted, so this stays honest if the vector file is ever swapped.
        var decodedFirst = Convert.ToHexString(
                SHA256.HashData(Convert.FromBase64String(negative.PublicKey)), 0, Ed25519.KeyIdByteLength)
            .ToLowerInvariant();

        Assert.Equal(negative.WrongKidIfDecodedFirst, decodedFirst);
        Assert.NotEqual(decodedFirst, Ed25519.KeyId(negative.PublicKey));
    }

    /// <summary>
    /// Second, independent cross-check: every server-issued machine-file fixture's <c>kid</c>
    /// reproduces from its own published public key under this rule, and none reproduces from the
    /// decoded bytes — across all four signing schemes.
    /// </summary>
    [Fact]
    public void KeyId_ReproducesEveryServerIssuedMachineFileFixtureKid()
    {
        var manifest = LoadMachineFileManifest();
        Assert.NotEmpty(manifest);

        foreach (var (name, fixture) in manifest)
        {
            var publicKey = fixture["public_key_b64"]!.GetValue<string>();
            var kid = fixture["kid"]!.GetValue<string>();

            Assert.Equal(kid, Ed25519.KeyId(publicKey));

            var decodedFirst = Convert.ToHexString(
                    SHA256.HashData(Convert.FromBase64String(publicKey)), 0, Ed25519.KeyIdByteLength)
                .ToLowerInvariant();
            Assert.False(
                string.Equals(kid, decodedFirst, StringComparison.Ordinal),
                $"{name}: the decode-first rule must NOT reproduce a server-issued kid.");
        }
    }

    [Fact]
    public void KeyId_IsSixteenLowercaseHexCharacters()
    {
        var kid = Ed25519.KeyId("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

        // Eight BYTES, sixteen characters — not eight characters.
        Assert.Equal(16, kid.Length);
        Assert.Equal(8, Ed25519.KeyIdByteLength);
        Assert.All(kid, c => Assert.True(char.IsAsciiDigit(c) || (c is >= 'a' and <= 'f'), $"'{c}' is not lowercase hex."));
    }

    /// <summary>
    /// An account whose <c>ed25519_public_key</c> column was never populated signs every file with
    /// <c>key_id("")</c>, because both checkout handlers pass <c>unwrap_or_default()</c>.
    /// </summary>
    [Fact]
    public void KeyId_OfTheEmptyString_IsTheUnpublishedAccountConstant()
    {
        Assert.Equal("e3b0c44298fc1c14", Ed25519.KeyId(""));
        Assert.Equal(Ed25519.UnpublishedAccountKeyId, Ed25519.KeyId(""));
    }

    [Fact]
    public void KeyId_Throws_OnNull()
    {
        Assert.Throws<ArgumentNullException>(() => Ed25519.KeyId(null!));
    }

    // ── SigningKey model-level id behaviour ───────────────────────────────────

    [Fact]
    public void SigningKey_ComputedKeyId_AgreesWithTheServedIdOnAWellFormedKey()
    {
        const string publicKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
        var key = SigningKey.FromEd25519PublicKey(publicKey);

        Assert.Equal("905f28def18eaac0", key.KeyId);
        Assert.Equal(key.KeyId, key.ComputedKeyId);
        Assert.True(key.KeyIdIsSelfConsistent);
    }

    [Fact]
    public void SigningKey_KeyIdIsSelfConsistent_IsFalse_WhenTheServerLabelledTheKeyInconsistently()
    {
        var key = new SigningKey { KeyId = "0000000000000000", PublicKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=" };

        Assert.False(key.KeyIdIsSelfConsistent);
        Assert.Equal("905f28def18eaac0", key.ComputedKeyId);
    }

    [Fact]
    public void SigningKey_TryGetPublicKeyBytes_FailsClosed_OnAnythingThatIsNotThirtyTwoBytes()
    {
        Assert.False(new SigningKey { KeyId = "x", PublicKey = "!!!not base64!!!" }.TryGetPublicKeyBytes(out _));
        Assert.False(new SigningKey { KeyId = "x", PublicKey = "QUJD" }.TryGetPublicKeyBytes(out _), "3 bytes is not a key");
        Assert.False(new SigningKey { KeyId = "x", PublicKey = "" }.TryGetPublicKeyBytes(out _));

        Assert.True(new SigningKey { KeyId = "x", PublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=" }
            .TryGetPublicKeyBytes(out var bytes));
        Assert.Equal(32, bytes.Length);
    }

    // ── Fixture loading ───────────────────────────────────────────────────────

    private static SigningKeyIdVectorFile LoadVectors()
    {
        using var stream = typeof(SigningKeyIdTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("SigningKeyFixtures/signing-key-ids.json")
            ?? throw new InvalidOperationException("Embedded signing-key vectors are missing.");

        return JsonSerializer.Deserialize<SigningKeyIdVectorFile>(stream)
            ?? throw new InvalidOperationException("Signing-key vectors failed to deserialize.");
    }

    private static IReadOnlyDictionary<string, System.Text.Json.Nodes.JsonObject> LoadMachineFileManifest()
    {
        using var stream = typeof(SigningKeyIdTests).GetTypeInfo().Assembly
            .GetManifestResourceStream("MachineFileFixtures/manifest.json")
            ?? throw new InvalidOperationException("Embedded machine-file manifest is missing.");

        var node = System.Text.Json.Nodes.JsonNode.Parse(stream)!.AsObject();
        return node.ToDictionary(kv => kv.Key, kv => kv.Value!.AsObject(), StringComparer.Ordinal);
    }
}
