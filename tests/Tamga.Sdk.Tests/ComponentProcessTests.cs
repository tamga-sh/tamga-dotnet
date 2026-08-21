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

    /// <summary>
    /// The request body really is flat — that half of the asymmetry is right and must stay. The
    /// RESPONSE is not: <c>component_created_response</c> answers <c>201</c> with a full JSON:API
    /// document. Decoding the response as if it mirrored the request is what produced components
    /// with <c>Guid.Empty</c> ids and empty strings, silently, because
    /// <c>TamgaJsonOptions.Default</c> sets no <c>UnmappedMemberHandling</c> and so the unknown
    /// <c>data</c> key was simply ignored.
    /// </summary>
    [Fact]
    public async Task CreateComponentAsync_SendsAFlatBody_AndDecodesAnEnvelopedResponse()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.Created, ComponentBody(componentId, machineId));

        var component = await client.CreateComponentAsync(new CreateComponentRequest { MachineId = machineId, Fingerprint = "fp-c", Name = "cpu" });

        Assert.Equal(componentId, component.Id);
        Assert.Equal(machineId, component.MachineId);
        Assert.Equal("fp-c", component.Fingerprint);
        Assert.Equal("cpu", component.Name);
        Assert.NotNull(component.Metadata);
        Assert.NotNull(component.Created);
        Assert.NotNull(component.Updated);

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
    public async Task CreateProcessAsync_SendsAFlatBody_AndDecodesAnEnvelopedResponse()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.Created, ProcessBody(processId, machineId, "1234"));

        var process = await client.CreateProcessAsync(machineId, "1234");

        Assert.Equal(processId, process.Id);
        Assert.Equal(machineId, process.MachineId);
        Assert.Equal("1234", process.Pid);
        Assert.NotNull(process.Metadata);
        Assert.NotNull(process.Created);
        Assert.NotNull(process.Updated);

        var body = handler.Requests[0].Body!;
        Assert.Contains("\"pid\":\"1234\"", body);
        Assert.DoesNotContain("\"data\"", body);
    }

    [Fact]
    public async Task PingProcessAsync_ReturnsUpdatedProcess()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ProcessBody(processId, Guid.NewGuid(), "1"));

        var process = await client.PingProcessAsync(processId);
        Assert.Equal(processId, process.Id);
    }

    /// <summary>
    /// The ping writes <c>last_heartbeat_at = NOW()</c> and the serializer emits it as a non-null
    /// attribute. Without a property to bind it to, the one fact the call establishes — that the
    /// server recorded this ping, and when — could not reach the caller at all.
    /// </summary>
    [Fact]
    public async Task PingProcessAsync_SurfacesTheHeartbeatTimestampTheServerJustWrote()
    {
        var (client, handler) = MakeClient();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ProcessBody(processId, Guid.NewGuid(), "1"));

        var process = await client.PingProcessAsync(processId);

        Assert.NotNull(process.LastHeartbeatAt);
        Assert.Equal(2026, process.LastHeartbeatAt!.Value.Year);
    }

    // The real wire shape, per components/serializer.rs: a JSON:API resource object per row —
    // { type, id, attributes } — with machine_id/fingerprint/name/metadata/created/updated INSIDE
    // `attributes`, not at the element root. These fixtures previously put those fields at the
    // root, which is why four decode paths could be wrong and CI stayed green for the entire life
    // of the SDK.
    //
    // `data` only: every serializer passes `links: None` and the field is skip_serializing_if
    // none, so no response ever carries a `links` key — which is why the cursor has to be
    // synthesized from the last item of a full page instead.
    private static JsonNode ComponentResource(Guid id, Guid machineId) => new JsonObject
    {
        ["type"] = "components",
        ["id"] = id.ToString(),
        ["attributes"] = new JsonObject
        {
            ["fingerprint"] = "fp-c",
            ["name"] = "cpu",
            ["machine_id"] = machineId.ToString(),
            ["metadata"] = new JsonObject { ["slot"] = "0" },
            ["created"] = "2026-01-02T03:04:05Z",
            ["updated"] = "2026-01-02T03:04:06Z",
        },
    };

    private static string ComponentBody(Guid id, Guid machineId) =>
        new JsonObject { ["data"] = ComponentResource(id, machineId) }.ToJsonString();

    private static string ComponentListBody(Guid machineId, params Guid[] ids) => new JsonObject
    {
        ["data"] = new JsonArray(ids.Select(id => ComponentResource(id, machineId)).ToArray()),
    }.ToJsonString();

    private static JsonNode ProcessResource(Guid id, Guid machineId, string pid) => new JsonObject
    {
        ["type"] = "processes",
        ["id"] = id.ToString(),
        ["attributes"] = new JsonObject
        {
            ["pid"] = pid,
            ["machine_id"] = machineId.ToString(),
            ["last_heartbeat_at"] = "2026-01-02T03:04:05Z",
            ["metadata"] = new JsonObject { ["role"] = "worker" },
            ["created"] = "2026-01-02T03:04:05Z",
            ["updated"] = "2026-01-02T03:04:06Z",
        },
    };

    private static string ProcessBody(Guid id, Guid machineId, string pid) =>
        new JsonObject { ["data"] = ProcessResource(id, machineId, pid) }.ToJsonString();

    private static string ProcessListBody(Guid machineId, params Guid[] ids) => new JsonObject
    {
        ["data"] = new JsonArray(ids.Select(id => ProcessResource(id, machineId, "1")).ToArray()),
    }.ToJsonString();

    [Fact]
    public async Task ListComponentsAsync_SynthesizesTheCursorFromTheLastItemOfAFullPage()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, ids));

        var page = await client.ListComponentsAsync(machineId, limit: 3);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(ids[^1].ToString(), page.NextCursor);
        Assert.Contains("limit=3", handler.Requests[0].Request.RequestUri!.Query);
    }

    /// <summary>
    /// The list path loses attributes but NOT the id, and the distinction matters for how bad the
    /// bug is. JSON:API puts <c>id</c> at the resource root, a sibling of <c>attributes</c> — so a
    /// flat decode still binds <see cref="Component.Id"/> correctly and the synthesized cursor
    /// stays a real UUID. What it loses is everything under <c>attributes</c>:
    /// <c>machine_id</c>, <c>fingerprint</c>, <c>name</c>, <c>metadata</c>, <c>created</c>,
    /// <c>updated</c> — so a caller got the right number of components, with the right ids, and
    /// nothing else.
    /// </summary>
    [Fact]
    public async Task ListComponentsAsync_BindsEveryAttribute_NotJustTheId()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var componentId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, componentId));

        var page = await client.ListComponentsAsync(machineId, limit: 1);

        var component = Assert.Single(page.Items);
        Assert.Equal(componentId, component.Id);
        Assert.Equal(machineId, component.MachineId);
        Assert.Equal("fp-c", component.Fingerprint);
        Assert.Equal("cpu", component.Name);
        Assert.NotNull(component.Metadata);
        Assert.NotNull(component.Created);
        Assert.NotNull(component.Updated);
    }

    [Fact]
    public async Task ListComponentsAsync_ReportsNoCursor_OnAPartialPage()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()));

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
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()));

        await client.ListComponentsAsync(machineId);

        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListComponentsAsync_ThreadsTheSynthesizedCursorBackAsPageAfter()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var first = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, first));
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, Guid.NewGuid()));

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
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId));

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
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId));

        var page = await client.ListComponentsAsync(machineId, limit: 1);

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    // ── GET /machines/{id}/processes ─────────────────────────────────────────

    [Fact]
    public async Task ListMachineProcessesAsync_BindsEveryAttribute()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var processId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ProcessListBody(machineId, processId));

        var page = await client.ListMachineProcessesAsync(machineId, limit: 1);

        var process = Assert.Single(page.Items);
        Assert.Equal(processId, process.Id);
        Assert.Equal(machineId, process.MachineId);
        Assert.Equal("1", process.Pid);
        Assert.NotNull(process.LastHeartbeatAt);
        Assert.NotNull(process.Metadata);
        Assert.NotNull(process.Created);
        Assert.NotNull(process.Updated);

        var request = handler.Requests[0].Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/accounts/acct-1/machines/{machineId}/processes", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// Keyset, like components — the server emits no <c>meta.page</c> on this route, so the cursor
    /// is synthesized from a full page rather than read.
    /// </summary>
    [Fact]
    public async Task ListMachineProcessesAsync_SynthesizesAKeysetCursor_AndThreadsItBack()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        var first = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ProcessListBody(machineId, first));
        handler.Enqueue(HttpStatusCode.OK, ProcessListBody(machineId));

        var page1 = await client.ListMachineProcessesAsync(machineId, limit: 1);
        Assert.Equal(first.ToString(), page1.NextCursor);

        var page2 = await client.ListMachineProcessesAsync(machineId, limit: 1, after: page1.NextCursor);
        Assert.Null(page2.NextCursor);
        Assert.Contains($"page%5Bafter%5D={first}", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListMachineProcessesAsync_SendsAnExplicitLimit_AndClampsAnOversizedOne()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, ProcessListBody(machineId));
        handler.Enqueue(HttpStatusCode.OK, ProcessListBody(machineId));

        await client.ListMachineProcessesAsync(machineId);
        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);

        await client.ListMachineProcessesAsync(machineId, limit: 500);
        Assert.Contains("limit=100", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ListMachineProcessesAsync_RejectsANonPositiveLimit_BeforeSendingAnything(int limit)
    {
        var (client, handler) = MakeClient();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ListMachineProcessesAsync(Guid.NewGuid(), limit: limit));

        Assert.Equal("limit", ex.ParamName);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// The infinite-loop guard. An all-zero id sorts before every UUIDv7 row, so feeding one back
    /// as <c>page[after]</c> returns the same first page again and a caller looping until
    /// <c>NextCursor</c> is null never terminates. No correct decode can produce an empty id — but
    /// a decode bug can, and did, silently. A truncated listing is a bad outcome; a non-terminating
    /// loop is a worse one, so an empty id ends the walk instead of restarting it.
    /// </summary>
    [Fact]
    public async Task ListComponentsAsync_RefusesToSynthesizeAnAllZeroCursor()
    {
        var (client, handler) = MakeClient();
        var machineId = Guid.NewGuid();
        // A full page whose single row carries no id at all.
        const string body = """{"data":[{"type":"components","attributes":{"fingerprint":"fp-c","name":"cpu"}}]}""";
        handler.Enqueue(HttpStatusCode.OK, body);

        var page = await client.ListComponentsAsync(machineId, limit: 1);

        Assert.Single(page.Items);
        Assert.Equal(Guid.Empty, page.Items[0].Id);
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
        handler.Enqueue(HttpStatusCode.OK, ComponentListBody(machineId, ids));

        var page = await client.ListComponentsAsync(machineId, limit: 500);

        Assert.Contains("limit=100", handler.Requests[0].Request.RequestUri!.Query);
        Assert.Equal(ids[^1].ToString(), page.NextCursor);
    }
}
