using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class HkdfTests
{
    [Fact]
    public void DeriveMachineFileKey_Is32Bytes()
    {
        var key = Hkdf.DeriveMachineFileKey("license-key-123", "fingerprint-abc");
        Assert.Equal(32, key.Length);
    }

    [Fact]
    public void DeriveMachineFileKey_IsDeterministic()
    {
        var key1 = Hkdf.DeriveMachineFileKey("license-key-123", "fingerprint-abc");
        var key2 = Hkdf.DeriveMachineFileKey("license-key-123", "fingerprint-abc");
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DeriveMachineFileKey_DiffersByFingerprint()
    {
        // CRITICAL: decrypting with the wrong fingerprint must derive a different key (and thus
        // fail AES-GCM authentication downstream) — see MachineFileTests for the end-to-end check.
        var key1 = Hkdf.DeriveMachineFileKey("license-key-123", "fingerprint-a");
        var key2 = Hkdf.DeriveMachineFileKey("license-key-123", "fingerprint-b");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void DeriveMachineFileKey_DiffersByLicenseKey()
    {
        var key1 = Hkdf.DeriveMachineFileKey("license-key-A", "fingerprint-abc");
        var key2 = Hkdf.DeriveMachineFileKey("license-key-B", "fingerprint-abc");
        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Salt_MatchesServerFixedString()
    {
        Assert.Equal("tamga:machine-file-key-v1", System.Text.Encoding.UTF8.GetString(Hkdf.Salt));
    }

    [Fact]
    public void DeriveMachineFileKey_IsNotTheNaiveKeyDerivation()
    {
        // Regression guard against "unifying" the two key-derivation paths (see CLAUDE.md gotcha):
        // HKDF output for a short license key must NOT equal NaiveKey's zero-padded bytes.
        var naive = NaiveKey.Derive("short");
        var hkdf = Hkdf.DeriveMachineFileKey("short", "some-fingerprint");
        Assert.NotEqual(naive, hkdf);
    }
}
