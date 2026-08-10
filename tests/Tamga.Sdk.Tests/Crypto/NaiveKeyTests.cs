using System.Text;
using Tamga.Sdk.Crypto;
using Xunit;

namespace Tamga.Sdk.Tests.Crypto;

public class NaiveKeyTests
{
    [Fact]
    public void Derive_ZeroPads_ShortKeys()
    {
        var key = NaiveKey.Derive("short");
        Assert.Equal(32, key.Length);
        Assert.Equal(Encoding.UTF8.GetBytes("short"), key[..5]);
        Assert.All(key[5..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Derive_Truncates_LongKeys()
    {
        var longKey = new string('x', 40);
        var key = NaiveKey.Derive(longKey);
        Assert.Equal(32, key.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(longKey[..32]), key);
    }

    [Fact]
    public void Derive_ReturnsKeyUnchanged_WhenExactly32Bytes()
    {
        var exact = new string('k', 32);
        var key = NaiveKey.Derive(exact);
        Assert.Equal(32, key.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(exact), key);
    }

    [Fact]
    public void Derive_IsNotAHash_SameLengthDifferentKeysProduceDifferentBytesButPredictableStructure()
    {
        // Regression guard: if someone "fixes" this into a hash, the zero-padding structure
        // (trailing zero bytes for a short key) disappears — this test would then fail.
        var key = NaiveKey.Derive("ab");
        Assert.Equal((byte)'a', key[0]);
        Assert.Equal((byte)'b', key[1]);
        for (var i = 2; i < 32; i++)
        {
            Assert.Equal(0, key[i]);
        }
    }
}
