using System.Text.Json;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Crypto;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Checkout;

/// <summary>
/// <see cref="SigningKeySet"/> construction, lookup, and the strict/lenient split between pinned
/// keys and a fetched key history.
/// </summary>
public class SigningKeySetTests
{
    private const string ZeroKeyB64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
    private const string ZeroKeyKid = "51643eac9777b63a";
    private const string SequentialKeyB64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private const string SequentialKeyKid = "905f28def18eaac0";

    private static SigningKeyResource Resource(string id, string algorithm, string publicKey, string status = "retired") =>
        JsonSerializer.Deserialize<SigningKeyResource>(
            $$"""
              {
                "type": "signing-keys",
                "id": "{{id}}",
                "attributes": {
                  "algorithm": "{{algorithm}}",
                  "publicKey": "{{publicKey}}",
                  "status": "{{status}}",
                  "created": "2026-01-01T00:00:00Z"
                }
              }
              """,
            TamgaJsonOptions.Default)!;

    [Fact]
    public void FromPublicKeys_IndexesAPinnedKeyByItsComputedId()
    {
        var set = SigningKeySet.FromPublicKeys(ZeroKeyB64);

        Assert.Equal(1, set.Count);
        Assert.False(set.IsEmpty);
        Assert.NotNull(set.Find(ZeroKeyKid));
        Assert.Equal(new[] { ZeroKeyKid }, set.KeyIds);
    }

    /// <summary>
    /// A mistyped pinned key must fail at startup. Skipping it produces a set that reports every
    /// genuine file as signed by an unknown key — at runtime, in the field.
    /// </summary>
    [Fact]
    public void FromPublicKeys_FailsLoudly_OnAMistypedPinnedKey()
    {
        Assert.Throws<ArgumentException>(() => SigningKeySet.FromPublicKeys("not base64 at all"));
        Assert.Throws<ArgumentException>(() => SigningKeySet.FromPublicKeys("QUJD"));
        Assert.Throws<ArgumentException>(() => SigningKeySet.FromPublicKeys(new string[] { null! }));
    }

    /// <summary>The server's <c>id</c> IS the kid; nothing is hashed on the fetched path.</summary>
    [Fact]
    public void FromResources_TakesTheKidFromTheResourceId_NotFromALocalHash()
    {
        var set = SigningKeySet.FromResources(new[] { Resource("deadbeefdeadbeef", "ed25519", ZeroKeyB64) });

        Assert.NotNull(set.Find("deadbeefdeadbeef"));
        Assert.Null(set.Find(ZeroKeyKid));
    }

    /// <summary>
    /// The local computation is a cross-check, not the index. A disagreement is surfaced without
    /// failing the fetch or stranding the key.
    /// </summary>
    [Fact]
    public void FromResources_ReportsAKeyWhoseServedIdDisagreesWithTheComputedOne()
    {
        var set = SigningKeySet.FromResources(new[]
        {
            Resource("deadbeefdeadbeef", "ed25519", ZeroKeyB64),
            Resource(SequentialKeyKid, "ed25519", SequentialKeyB64),
        });

        var inconsistent = Assert.Single(set.InconsistentKeys);
        Assert.Equal("deadbeefdeadbeef", inconsistent.KeyId);
        Assert.Equal(ZeroKeyKid, inconsistent.ComputedKeyId);

        // Surfaced, not fatal: it is still indexed under the served id.
        Assert.NotNull(set.Find("deadbeefdeadbeef"));
    }

    [Fact]
    public void FromResources_ReportsNoInconsistency_WhenTheServerIsSelfConsistent()
    {
        var set = SigningKeySet.FromResources(new[]
        {
            Resource(ZeroKeyKid, "ed25519", ZeroKeyB64),
            Resource(SequentialKeyKid, "ed25519", SequentialKeyB64),
        });

        Assert.Empty(set.InconsistentKeys);
        Assert.Equal(2, set.Count);
    }

    /// <summary>
    /// Lenient on the fetched path, deliberately: this is the account's whole key history, and one
    /// unusable row must not strand every file the account ever signed.
    /// </summary>
    [Fact]
    public void FromResources_OneUnusableKeyDoesNotStrandTheOthers()
    {
        var set = SigningKeySet.FromResources(new[]
        {
            Resource("0000000000000000", "ml-dsa-44", ZeroKeyB64),
            Resource("1111111111111111", "ed25519", "!!!not base64!!!"),
            Resource("2222222222222222", "ed25519", ZeroKeyB64),
        });

        Assert.Equal(1, set.Count);
        Assert.NotNull(set.Find("2222222222222222"));
        Assert.Null(set.Find("0000000000000000"));
        Assert.Null(set.Find("1111111111111111"));

        // Kept for diagnostics — a set that fetched three rows and can use one is a very
        // different situation from an empty account, and both are silent otherwise.
        Assert.Equal(3, set.Keys.Count);
    }

    /// <summary>Retired keys are the entire point — they must be usable for verification.</summary>
    [Fact]
    public void FromResources_KeepsRetiredKeys()
    {
        var set = SigningKeySet.FromResources(new[]
        {
            Resource(ZeroKeyKid, "ed25519", ZeroKeyB64, status: "retired"),
            Resource(SequentialKeyKid, "ed25519", SequentialKeyB64, status: "active"),
        });

        Assert.Equal(2, set.Count);
        var retired = set.Find(ZeroKeyKid);
        Assert.NotNull(retired);
        Assert.True(retired!.IsRetired);
        Assert.False(set.Find(SequentialKeyKid)!.IsRetired);
    }

    [Fact]
    public void Find_MatchesExactly_AndIsCaseSensitive()
    {
        var set = SigningKeySet.FromPublicKeys(ZeroKeyB64);

        Assert.Null(set.Find(ZeroKeyKid.ToUpperInvariant()));
        Assert.Null(set.Find(ZeroKeyKid[..^1]));
        Assert.Null(set.Find(""));
        Assert.Null(set.Find(null!));
    }

    [Fact]
    public void AnEmptySet_IsBuildable_AndFindsNothing()
    {
        Assert.True(SigningKeySet.Empty.IsEmpty);
        Assert.Equal(0, SigningKeySet.Empty.Count);
        Assert.Null(SigningKeySet.Empty.Find(ZeroKeyKid));
        Assert.Empty(SigningKeySet.FromResources(Array.Empty<SigningKeyResource>()).Keys);
        Assert.Equal(0, SigningKeySet.FromPublicKeys(Array.Empty<string>()).Count);
    }

    [Fact]
    public void Constructors_RejectNull()
    {
        Assert.Throws<ArgumentNullException>(() => new SigningKeySet(null!));
        Assert.Throws<ArgumentNullException>(() => SigningKeySet.FromResources(null!));
        Assert.Throws<ArgumentNullException>(() => SigningKeySet.FromPublicKeys((IEnumerable<string>)null!));
    }

    // The distinguishable conditions (unknown kid vs. unpublished account vs. forged file vs.
    // wrong license key) are exercised end-to-end through the public verification entry points in
    // SigningKeyRotationTests — that is the real call path. The two internal helpers behind them
    // are pinned here only for the parts a public test cannot reach.

    [Fact]
    public void FindVerifyingKey_TriesOnlyUsableKeys_InOrder_AndStopsAtTheFirstMatch()
    {
        var set = SigningKeySet.FromResources(new[]
        {
            Resource("0000000000000000", "ml-dsa-44", ZeroKeyB64),      // unusable: not Ed25519
            Resource("1111111111111111", "ed25519", "!!!not base64!!!"), // unusable: undecodable
            Resource(ZeroKeyKid, "ed25519", ZeroKeyB64),
            Resource(SequentialKeyKid, "ed25519", SequentialKeyB64),
        });
        var tried = new List<byte[]>();

        var found = set.FindVerifyingKey(publicKey => { tried.Add(publicKey); return publicKey[0] == 0x00 && publicKey[1] == 0x01; });

        Assert.Equal(SequentialKeyKid, found!.KeyId);
        Assert.Equal(2, tried.Count);
        Assert.Null(set.FindVerifyingKey(_ => false));
        Assert.Null(SigningKeySet.Empty.FindVerifyingKey(_ => true));
    }

    [Fact]
    public void UnverifiableFileFailure_LabelsByTheClaimedKid()
    {
        var set = SigningKeySet.FromPublicKeys(ZeroKeyB64);

        Assert.IsType<SignatureVerificationException>(set.UnverifiableFileFailure(null, "License file"));
        Assert.IsType<SignatureVerificationException>(set.UnverifiableFileFailure("", "License file"));
        Assert.IsType<SignatureVerificationException>(set.UnverifiableFileFailure(ZeroKeyKid, "License file"));       // held → forged
        Assert.IsType<UnknownSigningKeyException>(set.UnverifiableFileFailure(SequentialKeyKid, "License file"));     // not held
        Assert.IsType<UnpublishedSigningKeyException>(set.UnverifiableFileFailure(Ed25519.UnpublishedAccountKeyId, "License file"));
        Assert.Contains("Machine file", set.UnverifiableFileFailure(null, "Machine file").Message, StringComparison.Ordinal);
    }
}
