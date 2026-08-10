using System.Text.Json;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Models;

public class PolicyTests
{
    [Theory]
    [InlineData("NO_OVERAGE", OverageStrategy.NoOverage)]
    [InlineData("ALLOW_1_25X_OVERAGE", OverageStrategy.Allow125xOverage)]
    [InlineData("ALLOW_1_5X_OVERAGE", OverageStrategy.Allow15xOverage)]
    [InlineData("ALLOW_2X_OVERAGE", OverageStrategy.Allow2xOverage)]
    [InlineData("ALWAYS_ALLOW_OVERAGE", OverageStrategy.AlwaysAllowOverage)]
    public void OverageStrategy_RoundTrips_AllFiveVariants(string wire, OverageStrategy expected)
    {
        var deserialized = JsonSerializer.Deserialize<OverageStrategy>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Fact]
    public void OverageStrategy_Deserializes_DenyAccess_ToNoOverage_WithoutThrowing()
    {
        var result = JsonSerializer.Deserialize<OverageStrategy>("\"DENY_ACCESS\"");
        Assert.Equal(OverageStrategy.NoOverage, result);
    }

    [Fact]
    public void OverageStrategy_Deserializes_UnrecognizedValue_ToNoOverage()
    {
        var result = JsonSerializer.Deserialize<OverageStrategy>("\"SOMETHING_ELSE\"");
        Assert.Equal(OverageStrategy.NoOverage, result);
    }

    [Theory]
    [InlineData("NO_REVIVE", HeartbeatResurrectionStrategy.NoRevive)]
    [InlineData("1_MINUTE_REVIVE", HeartbeatResurrectionStrategy.OneMinuteRevive)]
    [InlineData("2_MINUTE_REVIVE", HeartbeatResurrectionStrategy.TwoMinuteRevive)]
    [InlineData("5_MINUTE_REVIVE", HeartbeatResurrectionStrategy.FiveMinuteRevive)]
    [InlineData("10_MINUTE_REVIVE", HeartbeatResurrectionStrategy.TenMinuteRevive)]
    [InlineData("15_MINUTE_REVIVE", HeartbeatResurrectionStrategy.FifteenMinuteRevive)]
    [InlineData("ALWAYS_REVIVE", HeartbeatResurrectionStrategy.AlwaysRevive)]
    public void HeartbeatResurrectionStrategy_RoundTrips_AllSevenVariants(string wire, HeartbeatResurrectionStrategy expected)
    {
        var deserialized = JsonSerializer.Deserialize<HeartbeatResurrectionStrategy>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Fact]
    public void HeartbeatResurrectionStrategy_Deserializes_NoResurrection_ToNoRevive_WithoutThrowing()
    {
        var result = JsonSerializer.Deserialize<HeartbeatResurrectionStrategy>("\"NO_RESURRECTION\"");
        Assert.Equal(HeartbeatResurrectionStrategy.NoRevive, result);
    }

    [Theory]
    [InlineData("DEACTIVATE_DEAD", HeartbeatCullStrategy.DeactivateDead)]
    [InlineData("KEEP_DEAD", HeartbeatCullStrategy.KeepDead)]
    public void HeartbeatCullStrategy_RoundTrips(string wire, HeartbeatCullStrategy expected)
    {
        var deserialized = JsonSerializer.Deserialize<HeartbeatCullStrategy>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Theory]
    [InlineData("day", CheckInInterval.Day)]
    [InlineData("week", CheckInInterval.Week)]
    [InlineData("month", CheckInInterval.Month)]
    [InlineData("year", CheckInInterval.Year)]
    public void CheckInInterval_RoundTrips_AllFourLowercaseValues(string wire, CheckInInterval expected)
    {
        var deserialized = JsonSerializer.Deserialize<CheckInInterval>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        // Regression: must stay lowercase, not accidentally uppercase like every other enum here.
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Theory]
    [InlineData("ED25519_SIGN", LicenseScheme.Ed25519Sign)]
    [InlineData("RSA_2048_PKCS1_SIGN", LicenseScheme.Rsa2048Pkcs1Sign)]
    [InlineData("RSA_2048_PKCS1_PSS_SIGN", LicenseScheme.Rsa2048Pkcs1PssSign)]
    [InlineData("ECDSA_P256_SIGN", LicenseScheme.EcdsaP256Sign)]
    [InlineData("RSA_2048_JWT_RS256", LicenseScheme.Rsa2048JwtRs256)]
    public void LicenseScheme_RoundTrips_AllFiveVariants(string wire, LicenseScheme expected)
    {
        var deserialized = JsonSerializer.Deserialize<LicenseScheme>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Fact]
    public void LicenseScheme_Deserializes_EmptyString_ToNone()
    {
        Assert.Equal(LicenseScheme.None, JsonSerializer.Deserialize<LicenseScheme>("\"\""));
    }

    [Fact]
    public void Policy_Tolerates_MissingMaxMemoryAndMaxDisk()
    {
        const string json = """
        {
            "id": "11111111-1111-1111-1111-111111111111",
            "max_machines": 5,
            "require_check_in": false,
            "overage_strategy": "NO_OVERAGE",
            "heartbeat_cull_strategy": "DEACTIVATE_DEAD",
            "heartbeat_resurrection_strategy": "NO_REVIVE",
            "scheme": "ED25519_SIGN"
        }
        """;

        var policy = JsonSerializer.Deserialize<Policy>(json);
        Assert.NotNull(policy);
        Assert.Equal(5, policy!.MaxMachines);
        Assert.Null(policy.MaxMemory);
        Assert.Null(policy.MaxDisk);
    }
}
