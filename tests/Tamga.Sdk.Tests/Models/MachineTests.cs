using System.Text.Json;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Models;

public class MachineTests
{
    [Theory]
    [InlineData("NOT_STARTED", HeartbeatStatus.NotStarted)]
    [InlineData("ALIVE", HeartbeatStatus.Alive)]
    [InlineData("DEAD", HeartbeatStatus.Dead)]
    [InlineData("RESURRECTED", HeartbeatStatus.Resurrected)]
    public void HeartbeatStatus_RoundTrips_AllFourValues(string wire, HeartbeatStatus expected)
    {
        var deserialized = JsonSerializer.Deserialize<HeartbeatStatus>($"\"{wire}\"");
        Assert.Equal(expected, deserialized);
        Assert.Equal($"\"{wire}\"", JsonSerializer.Serialize(expected));
    }

    [Fact]
    public void Machine_FromResource_MapsLicenseRelationship()
    {
        var machineId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var resource = new JsonApiResource<MachineAttributes>
        {
            Type = "machines",
            Id = machineId,
            Attributes = new MachineAttributes { Fingerprint = "fp-1", HeartbeatStatus = HeartbeatStatus.Alive },
            Relationships = new Dictionary<string, JsonApiRelationship>
            {
                ["license"] = new JsonApiRelationship { Data = new JsonApiResourceIdentifier { Type = "licenses", Id = licenseId } },
            },
        };

        var machine = Machine.FromResource(resource);

        Assert.Equal(machineId, machine.Id);
        Assert.Equal("fp-1", machine.Fingerprint);
        Assert.Equal(HeartbeatStatus.Alive, machine.HeartbeatStatus);
        Assert.Equal(licenseId, machine.LicenseId);
    }

    [Theory]
    [InlineData("1234")]
    [InlineData("abc-def")]
    [InlineData("0")]
    public void Process_Pid_RoundTrips_AsJsonString_EvenForNumericLookingValues(string pid)
    {
        var process = new Process { Pid = pid };
        var json = JsonSerializer.Serialize(process, TamgaJsonOptions.Default);

        // Must be a quoted JSON string, never a bare number.
        Assert.Contains($"\"pid\":\"{pid}\"", json);

        var deserialized = JsonSerializer.Deserialize<Process>(json, TamgaJsonOptions.Default);
        Assert.Equal(pid, deserialized!.Pid);
    }

    [Fact]
    public void CreateProcessRequest_Pid_IsStringTyped_ConstructedFromIntCaller()
    {
        var request = new CreateProcessRequest { MachineId = Guid.NewGuid(), Pid = 1234.ToString() };
        var json = JsonSerializer.Serialize(request, TamgaJsonOptions.Default);
        Assert.Contains("\"pid\":\"1234\"", json);
    }
}
