using System.Text.Json;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Models;

public class ValidationCodeTests
{
    public static IEnumerable<object[]> AllKnownWireValues()
    {
        yield return new object[] { "VALID", ValidationCode.Valid };
        yield return new object[] { "SUSPENDED", ValidationCode.Suspended };
        yield return new object[] { "EXPIRED", ValidationCode.Expired };
        yield return new object[] { "OVERDUE", ValidationCode.Overdue };
        yield return new object[] { "PRODUCT_SCOPE_MISMATCH", ValidationCode.ProductScopeMismatch };
        yield return new object[] { "POLICY_SCOPE_MISMATCH", ValidationCode.PolicyScopeMismatch };
        yield return new object[] { "USER_SCOPE_MISMATCH", ValidationCode.UserScopeMismatch };
        yield return new object[] { "ENVIRONMENT_SCOPE_MISMATCH", ValidationCode.EnvironmentScopeMismatch };
        yield return new object[] { "TOO_MANY_MACHINES", ValidationCode.TooManyMachines };
        yield return new object[] { "TOO_MANY_CORES", ValidationCode.TooManyCores };
        yield return new object[] { "TOO_MUCH_MEMORY", ValidationCode.TooMuchMemory };
        yield return new object[] { "TOO_MUCH_DISK", ValidationCode.TooMuchDisk };
        yield return new object[] { "TOO_MANY_PROCESSES", ValidationCode.TooManyProcesses };
        yield return new object[] { "TOO_MANY_USES", ValidationCode.TooManyUses };
        yield return new object[] { "NOT_FOUND", ValidationCode.NotFound };
        yield return new object[] { "BANNED", ValidationCode.Banned };
        yield return new object[] { "ENTITLEMENTS_MISSING", ValidationCode.EntitlementsMissing };
        yield return new object[] { "TOO_MANY_USERS", ValidationCode.TooManyUsers };
        yield return new object[] { "HEARTBEAT_DEAD", ValidationCode.HeartbeatDead };
        yield return new object[] { "HEARTBEAT_NOT_STARTED", ValidationCode.HeartbeatNotStarted };
        yield return new object[] { "FINGERPRINT_SCOPE_MISMATCH", ValidationCode.FingerprintScopeMismatch };
        yield return new object[] { "COMPONENTS_SCOPE_MISMATCH", ValidationCode.ComponentsScopeMismatch };
        yield return new object[] { "CHECKSUM_SCOPE_MISMATCH", ValidationCode.ChecksumScopeMismatch };
        yield return new object[] { "VERSION_SCOPE_MISMATCH", ValidationCode.VersionScopeMismatch };
    }

    [Theory]
    [MemberData(nameof(AllKnownWireValues))]
    public void Deserializes_AllKnownWireValues(string wireValue, ValidationCode expected)
    {
        var json = $"\"{wireValue}\"";
        var result = JsonSerializer.Deserialize<ValidationCode>(json);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Deserializes_UnrecognizedValue_ToUnknown_WithoutThrowing()
    {
        var result = JsonSerializer.Deserialize<ValidationCode>("\"SOME_FUTURE_CODE_NOT_YET_MODELED\"");
        Assert.Equal(ValidationCode.Unknown, result);
    }

    [Theory]
    [MemberData(nameof(AllKnownWireValues))]
    public void RoundTrips_ThroughSerializeThenDeserialize(string wireValue, ValidationCode code)
    {
        var serialized = JsonSerializer.Serialize(code);
        Assert.Equal($"\"{wireValue}\"", serialized);
        var roundTripped = JsonSerializer.Deserialize<ValidationCode>(serialized);
        Assert.Equal(code, roundTripped);
    }

    [Fact]
    public void AllTwentyFourKnownValuesAreModeled()
    {
        Assert.Equal(24, AllKnownWireValues().Count());
    }
}
