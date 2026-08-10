using System.Net;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

public class ComponentProcessTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    [Fact]
    public async Task CreateComponentAsync_MapsFingerprintTaken()
    {
        var (client, handler) = MakeClient();
        const string errorBody = """{"errors":[{"id":"1","status":409,"code":"FINGERPRINT_TAKEN","title":"t","detail":"taken"}]}""";
        handler.Enqueue(HttpStatusCode.Conflict, errorBody);

        var request = new CreateComponentRequest { MachineId = Guid.NewGuid(), Fingerprint = "fp-c", Name = "cpu" };
        await Assert.ThrowsAsync<FingerprintTakenException>(() => client.CreateComponentAsync(request));
    }

    [Fact]
    public async Task CreateComponentAsync_SendsFlatNonEnvelopedBody()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, $$"""{"id":"{{componentId}}","machine_id":"{{machineId}}","fingerprint":"fp-c","name":"cpu"}""", contentType: "application/json");

        var component = await client.CreateComponentAsync(new CreateComponentRequest { MachineId = machineId, Fingerprint = "fp-c", Name = "cpu" });

        Assert.Equal(componentId, component.Id);
        var body = handler.Requests[0].Body!;
        Assert.DoesNotContain("\"data\"", body);
        Assert.DoesNotContain("\"attributes\"", body);
    }

    [Fact]
    public async Task CreateProcessAsync_MapsPidTaken()
    {
        var (client, handler) = MakeClient();
        const string errorBody = """{"errors":[{"id":"1","status":409,"code":"PID_TAKEN","title":"t","detail":"taken"}]}""";
        handler.Enqueue(HttpStatusCode.Conflict, errorBody);

        await Assert.ThrowsAsync<PidTakenException>(() => client.CreateProcessAsync(Guid.NewGuid(), "1234"));
    }

    [Fact]
    public async Task CreateProcessAsync_SendsPidAsJsonString()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, $$"""{"id":"{{processId}}","machine_id":"{{machineId}}","pid":"1234"}""", contentType: "application/json");

        var process = await client.CreateProcessAsync(machineId, "1234");

        Assert.Equal("1234", process.Pid);
        Assert.Contains("\"pid\":\"1234\"", handler.Requests[0].Body!);
    }

    [Fact]
    public async Task PingProcessAsync_ReturnsUpdatedProcess()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, $$"""{"id":"{{processId}}","machine_id":"{{Guid.NewGuid()}}","pid":"1"}""", contentType: "application/json");

        var process = await client.PingProcessAsync(processId);
        Assert.Equal(processId, process.Id);
    }

    [Fact]
    public async Task ListComponentsAsync_ThreadsKeysetPaginationCursor_AcrossMultiplePages()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var componentId1 = Guid.NewGuid();
        var componentId2 = Guid.NewGuid();

        var page1Body = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject { ["id"] = componentId1.ToString(), ["machine_id"] = machineId.ToString(), ["fingerprint"] = "fp-1", ["name"] = "cpu" },
            },
            ["links"] = new JsonObject { ["next"] = "/machines/x/components?page%5Bafter%5D=cursor-1" },
        }.ToJsonString();
        var page2Body = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject { ["id"] = componentId2.ToString(), ["machine_id"] = machineId.ToString(), ["fingerprint"] = "fp-2", ["name"] = "ram" },
            },
            ["links"] = new JsonObject(),
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, page1Body, contentType: "application/json");
        handler.Enqueue(HttpStatusCode.OK, page2Body, contentType: "application/json");

        var page1 = await client.ListComponentsAsync(machineId);
        Assert.Single(page1.Items);
        Assert.Equal("cursor-1", page1.NextCursor);

        var page2 = await client.ListComponentsAsync(machineId, after: page1.NextCursor);
        Assert.Single(page2.Items);
        Assert.Null(page2.NextCursor);
        Assert.Contains("page%5Bafter%5D=cursor-1", handler.Requests[1].Request.RequestUri!.Query);
    }
}
