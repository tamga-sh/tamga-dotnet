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

    // The real wire shape: `data` only. The server passes `links: None` on every serializer and
    // the field is skip_serializing_if none, so no response ever carries a `links` key — which is
    // why the cursor has to be synthesized from the last item of a full page instead.
    private static string ComponentListBody(Guid machineId, params Guid[] ids) => new JsonObject
    {
        ["data"] = new JsonArray(ids
            .Select(id => (JsonNode)new JsonObject
            {
                ["id"] = id.ToString(),
                ["machine_id"] = machineId.ToString(),
                ["fingerprint"] = $"fp-{id}",
                ["name"] = "cpu",
            })
            .ToArray()),
    }.ToJsonString();

    [Fact]
    public async Task ListComponentsAsync_SynthesizesTheCursorFromTheLastItemOfAFullPage()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, ids), contentType: "application/json");

        var page = await client.ListComponentsAsync(machineId, limit: 3);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(ids[^1].ToString(), page.NextCursor);
        Assert.Contains("limit=3", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListComponentsAsync_ReportsNoCursor_OnAPartialPage()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()), contentType: "application/json");

        var page = await client.ListComponentsAsync(machineId, limit: 3);

        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ListComponentsAsync_SendsAnExplicitLimit_SoAFullPageIsDetectable()
    {
        // With the limit left implicit the server applies its own default of 25 and there is no
        // number to compare the row count against, so a truncated listing looks complete.
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()), contentType: "application/json");

        await client.ListComponentsAsync(machineId);

        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListComponentsAsync_ThreadsTheSynthesizedCursorBackAsPageAfter()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var first = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, first), contentType: "application/json");
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()), contentType: "application/json");

        var page1 = await client.ListComponentsAsync(machineId, limit: 1);
        Assert.Equal(first.ToString(), page1.NextCursor);

        await client.ListComponentsAsync(machineId, limit: 1, after: page1.NextCursor);
        Assert.Contains($"page%5Bafter%5D={first}", handler.Requests[1].Request.RequestUri!.Query);
    }

    /// <summary>
    /// Regression: <c>limit: 0</c> used to satisfy the page-fullness test against an empty page
    /// (<c>Count 0 == effectiveLimit 0</c>), take the cursor-synthesis branch, and index
    /// <c>[^1]</c> into an empty list — surfacing as an <see cref="ArgumentOutOfRangeException"/>
    /// with <c>ParamName "index"</c>, thrown from inside the response mapper long after the real
    /// mistake. An empty page IS queued here on purpose: it is the exact response that used to
    /// crash, so this test would still fail if the guard were removed. The limit is now rejected
    /// at the door instead — <c>ParamName "limit"</c>, and no request sent at all.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListComponentsAsync_RejectsANonPositiveLimit_BeforeSendingAnything(int limit)
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId), contentType: "application/json");

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ListComponentsAsync(machineId, limit: limit));

        // "limit", not "index" — the caller's mistake, named at the boundary it was made at.
        Assert.Equal("limit", ex.ParamName);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListComponentsAsync_ReportsNoCursor_OnAnEmptyPage()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId), contentType: "application/json");

        var page = await client.ListComponentsAsync(machineId, limit: 1);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    /// <summary>
    /// The server clamps <c>limit</c> to 100. Without the SDK applying the same clamp, a caller
    /// asking for 500 got a full 100-row page whose count never equalled the requested limit, so
    /// the cursor came back <see langword="null"/> and the listing silently truncated at 100 rows
    /// with no way to tell it had been cut short.
    /// </summary>
    [Fact]
    public async Task ListComponentsAsync_ClampsAnOversizedLimit_SoAFullPageStillYieldsACursor()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()).ToArray();
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, ids), contentType: "application/json");

        var page = await client.ListComponentsAsync(machineId, limit: 500);

        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal(ids[^1].ToString(), page.NextCursor);
    }
}
