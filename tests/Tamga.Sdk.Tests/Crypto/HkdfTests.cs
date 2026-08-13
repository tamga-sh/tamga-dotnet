using System.Text;
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
    public void TheTwoDerivationsNeverCollide()
    {
        // Regression guard against "unifying" the two paths (see CLAUDE.md gotcha). Both are
        // HKDF-SHA256 now, so the only thing keeping them apart is the salt and info — a change
        // that accidentally aligned those would silently let one file type decrypt as the other.
        var license = Hkdf.DeriveLicenseFileKey("short");
        var machine = Hkdf.DeriveMachineFileKey("short", "license-file");
        Assert.NotEqual(license, machine);
    }

    [Fact]
    public void DeriveLicenseFileKey_DoesNotLeakTheLicenseKey()
    {
        // v1 zero-padded the license key, so the derived key literally contained it in cleartext
        // and everything past its length was zero — a stolen .lic was a dictionary attack against
        // the key string, not a 256-bit one.
        var derived = Hkdf.DeriveLicenseFileKey("SHORT-KEY");
        Assert.Equal(32, derived.Length);

        var naive = new byte[32];
        Encoding.UTF8.GetBytes("SHORT-KEY").CopyTo(naive, 0);
        Assert.NotEqual(naive, derived);
        Assert.Contains(derived[9..], b => b != 0);
    }

    [Fact]
    public void DeriveLicenseFileKey_IsDeterministic()
    {
        Assert.Equal(Hkdf.DeriveLicenseFileKey("LK-1"), Hkdf.DeriveLicenseFileKey("LK-1"));
        Assert.NotEqual(Hkdf.DeriveLicenseFileKey("LK-1"), Hkdf.DeriveLicenseFileKey("LK-2"));
    }
}
