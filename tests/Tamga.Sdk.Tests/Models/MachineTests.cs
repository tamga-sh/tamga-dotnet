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

    /// <summary>
    /// The mapper must ignore <c>relationships</c> outright rather than bind anything from it. The
    /// server's machine serializer emits <c>{ type, id, attributes }</c> and nothing else;
    /// <c>relationships</c> exists on the machine CREATE <em>request</em> body only. A
    /// <c>Machine.LicenseId</c> property used to be populated from it — always
    /// <see langword="null"/> in practice, <c>[Obsolete]</c> from 2.0.0, removed in 2.1.0. This
    /// test feeds the mapper a hand-built relationships object no server could send and pins that
    /// the surviving fields still bind and nothing throws.
    /// </summary>
    [Fact]
    public void Machine_FromResource_IgnoresRelationships_BecauseTheServerNeverSendsAny()
    {
        var machineId = Guid.NewGuid();
        var resource = new JsonApiResource<MachineAttributes>
        {
            Type = "machines",
            Id = machineId,
            Attributes = new MachineAttributes { Fingerprint = "fp-1", HeartbeatStatus = HeartbeatStatus.Alive },
            // Hand-built, since no server response can contain this.
            Relationships = new Dictionary<string, JsonApiRelationship>
            {
                ["license"] = new JsonApiRelationship { Data = new JsonApiResourceIdentifier { Type = "licenses", Id = Guid.NewGuid() } },
            },
        };

        var machine = Machine.FromResource(resource);

        Assert.Equal(machineId, machine.Id);
        Assert.Equal("fp-1", machine.Fingerprint);
        Assert.Equal(HeartbeatStatus.Alive, machine.HeartbeatStatus);
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
