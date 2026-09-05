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
        // Real wire shape: `status` is a string.
        const string errorBody = """{"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"This fingerprint is already activated within the policy's uniqueness scope"}]}""";
        handler.Enqueue(HttpStatusCode.Conflict, errorBody);

        var request = new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() };
        await Assert.ThrowsAsync<FingerprintTakenException>(() => client.CreateMachineAsync(request));
    }

    [Fact]
    public async Task CreateMachineAsync_ExposesTheConflictingMachineId_WhenTheServerNamesIt()
    {
        var (client, handler) = MakeClient();
        var existing = Guid.NewGuid();
        // Exact wire shape: string `status`, `meta` = {"machineId": "<uuid>"} — sent only when the
        // machine holding the fingerprint is on the license named in the request.
        var errorBody = "{\"errors\":[{\"id\":\"1\",\"status\":\"409\",\"code\":\"FINGERPRINT_TAKEN\",\"title\":\"Conflict\",\"detail\":\"This fingerprint is already activated\",\"meta\":{\"machineId\":\"" + existing + "\"}}]}";

        handler.Enqueue(HttpStatusCode.Conflict, errorBody);

        var ex = await Assert.ThrowsAsync<FingerprintTakenException>(() =>
            client.CreateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() }));

        Assert.Equal(existing, ex.ExistingMachineId);
        Assert.Null(ex.ErrorBodyParseFailure);
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

    /// <summary>
    /// The create-time quota path. The server now checks machine/core/memory/disk limits inside
    /// the create transaction, so an over-limit activation can be refused before a row exists —
    /// and when that happens there is nothing to roll back. Issuing a DELETE here would target a
    /// machine that was never created.
    /// </summary>
    [Fact]
    public async Task ActivateMachineAsync_PropagatesACreateTime422_WithoutValidatingOrDeleting()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();

        const string errorBody = """
        {"errors":[{"id":"01926b3e-0000-7000-8000-000000000000","status":"422","code":"MACHINE_LIMIT_EXCEEDED","title":"Unprocessable Entity","detail":"This license has reached its machine limit"}]}
        """;
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, errorBody);

        var ex = await Assert.ThrowsAsync<MachineLimitExceededException>(() =>
            client.ActivateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId }));

        // Exactly one request: the create. No validate, and crucially no DELETE.
        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Request.Method);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Delete);

        // The server's own code survives, and normalizes onto the validate-time equivalent so a
        // caller can handle both over-limit paths with one value.
        Assert.Equal("MACHINE_LIMIT_EXCEEDED", ex.Error.Code);
        Assert.Equal(ValidationCode.TooManyMachines, ex.EquivalentValidationCode);
    }

    /// <summary>
    /// The overage path, which the create-time check did NOT replace: under ALLOW_ACCESS or
    /// ALLOW_1_25X_OVERAGE the row IS written and only validate objects. After the rollback the
    /// machine no longer exists, so returning it as a success value was a trap (audit D15): a
    /// caller that carried on heartbeated a row that answers 404. The rollback is now a failure
    /// that carries everything the tuple used to.
    /// </summary>
    [Fact]
    public async Task ActivateMachineAsync_ThrowsMachineOverLimit_AfterRollingBack_InsteadOfReturningTheDeletedMachine()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();

        // 1) create succeeds — the policy's overage strategy allowed the extra seat.
        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId));
        // 2) validate reports the overage anyway.
        handler.Enqueue(HttpStatusCode.OK, $$"""
        {
            "data": {
                "type": "licenses",
                "id": "{{licenseId}}",
                "attributes": { "key": "L", "status": "ACTIVE", "suspended": false, "uses": 0, "machines_count": 6, "max_machines": 5 }
            },
            "meta": { "ts": "2024-01-01T00:00:00Z", "valid": false, "detail": "over limit", "code": "TOO_MANY_MACHINES" }
        }
        """);
        // 3) rollback.
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var ex = await Assert.ThrowsAsync<MachineOverLimitException>(() =>
            client.ActivateMachineAsync(new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId }));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(HttpMethod.Delete, handler.Requests[2].Request.Method);
        Assert.Contains(machineId.ToString(), handler.Requests[2].Request.RequestUri!.AbsolutePath);

        // Everything the old tuple carried is on the exception, plus the fact that the row is gone.
        Assert.Equal(machineId, ex.DeletedMachineId);
        Assert.Equal(ValidationCode.TooManyMachines, ex.Validation.Code);
        Assert.Equal(6, ex.Validation.License.MachinesCount);
        Assert.Equal(5, ex.Validation.License.MaxMachines);

        // It is a limit-exceeded exception like the create-time 422s, so one catch clause covers
        // both over-limit paths, and Error.Code is what the server actually said.
        Assert.IsAssignableFrom<TamgaLimitExceededException>(ex);
        Assert.Equal(ValidationCode.TooManyMachines, ex.EquivalentValidationCode);
        Assert.Equal("TOO_MANY_MACHINES", ex.Error.Code);
        Assert.Equal((ushort)422, ex.Error.Status);
        Assert.Contains(machineId.ToString(), ex.Error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>deleteOnOverLimit: false</c> keeps the old contract exactly: no DELETE, the tuple comes
    /// back, and the caller decides what to do with a machine that still exists.
    /// </summary>
    [Fact]
    public async Task ActivateMachineAsync_ReturnsTheTuple_AndDeletesNothing_WhenDeleteOnOverLimitIsFalse()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var licenseId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, MachineResourceJson(machineId));
        handler.Enqueue(HttpStatusCode.OK, $$"""
        {
            "data": { "type": "licenses", "id": "{{licenseId}}", "attributes": { "key": "L", "suspended": false, "uses": 0 } },
            "meta": { "ts": "2024-01-01T00:00:00Z", "valid": false, "detail": "over limit", "code": "TOO_MANY_MACHINES" }
        }
        """);

        var (machine, validation) = await client.ActivateMachineAsync(
            new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId },
            deleteOnOverLimit: false);

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
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
