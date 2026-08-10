using System.Net;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class MachineTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    private static string MachineResourceJson(Guid id, string heartbeatStatus = "NOT_STARTED") => $$"""
    {
        "data": {
            "type": "machines",
            "id": "{{id}}",
            "attributes": { "fingerprint": "fp-1", "heartbeat_status": "{{heartbeatStatus}}" }
        }
    }
    """;

    [Fact]
    public async Task CreateMachineAsync_MapsFingerprintTaken_ToTypedException()
    {
        var (client, handler) = MakeClient();
        const string errorBody = """{"errors":[{"id":"1","status":409,"code":"FINGERPRINT_TAKEN","title":"t","detail":"taken"}]}""";
        handler.Enqueue(HttpStatusCode.Conflict, errorBody);

        var request = new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() };
        await Assert.ThrowsAsync<FingerprintTakenException>(() => client.CreateMachineAsync(request));
    }

    [Fact]
    public async Task CreateMachineAsync_SendsJsonApiRelationshipToLicense()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(Guid.NewGuid()));

        await client.CreateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        var body = handler.Requests[0].Body!;
        Assert.Contains(licenseId.ToString(), body);
        Assert.Contains("\"type\":\"licenses\"", body);
    }

    [Fact]
    public async Task ActivateMachineAsync_DeletesMachine_OnOverLimitValidationCode()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();

        // 1) create machine
        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId));
        // 2) validate -> over limit
        handler.Enqueue(HttpStatusCode.OK, $$"""
        {
            "data": { "type": "licenses", "id": "{{licenseId}}", "attributes": { "key": "L", "suspended": false, "uses": 0 } },
            "meta": { "ts": "2024-01-01T00:00:00Z", "valid": false, "detail": "over limit", "code": "TOO_MANY_MACHINES" }
        }
        """);
        // 3) delete machine
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var (machine, validation) = await client.ActivateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Request.Method);
        Assert.Equal(machineId, machine.Id);
        Assert.Equal(ValidationCode.TooManyMachines, validation.Code);
    }

    [Fact]
    public async Task ActivateMachineAsync_DoesNotDelete_WhenValidationSucceeds()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId));
        handler.Enqueue(HttpStatusCode.OK, $$"""
        {
            "data": { "type": "licenses", "id": "{{licenseId}}", "attributes": { "key": "L", "suspended": false, "uses": 0 } },
            "meta": { "ts": "2024-01-01T00:00:00Z", "valid": true, "detail": "ok", "code": "VALID" }
        }
        """);

        await client.ActivateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PingHeartbeatAsync_ReturnsUpdatedMachine()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId, heartbeatStatus: "ALIVE"));

        var machine = await client.PingHeartbeatAsync(machineId);

        Assert.Equal(HeartbeatStatus.Alive, machine.HeartbeatStatus);
    }

    [Fact]
    public async Task ResetHeartbeatAsync_ReturnsMachine_RewoundToNotStarted()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId, heartbeatStatus: "NOT_STARTED"));

        var machine = await client.ResetHeartbeatAsync(machineId);

        Assert.Equal(HeartbeatStatus.NotStarted, machine.HeartbeatStatus);
    }
}
