using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;
using Tamga.Sdk.Tests.Support;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>
/// The machine read/update surface: <c>GET</c>/<c>PATCH /machines/{id}</c>, the OFFSET-paginated
/// machine collection, exact-fingerprint lookup on top of a substring search, and the idempotent
/// activation path that exits a <c>409 FINGERPRINT_TAKEN</c>.
/// </summary>
public class MachineReadTests
{
    private static (TamgaClient Client, MockHttpMessageHandler Handler) MakeClient()
    {
        var handler = new MockHttpMessageHandler();
        var httpClient = new HttpClient(handler);
        var options = new TamgaClientOptions { AccountId = "acct-1", BaseUrl = "https://api.tamga.test" };
        return (new TamgaClient(options, httpClient), handler);
    }

    private static JsonObject MachineResource(Guid id, string fingerprint, string heartbeatStatus = "ALIVE") => new()
    {
        ["type"] = "machines",
        ["id"] = id.ToString(),
        ["attributes"] = new JsonObject
        {
            ["fingerprint"] = fingerprint,
            ["name"] = "workstation",
            ["hostname"] = "host-1",
            ["heartbeat_status"] = heartbeatStatus,
            ["created"] = "2026-01-02T03:04:05Z",
            ["updated"] = "2026-01-02T03:04:06Z",
        },
    };

    private static string SingleMachineBody(Guid id, string fingerprint, string heartbeatStatus = "ALIVE") =>
        new JsonObject { ["data"] = MachineResource(id, fingerprint, heartbeatStatus) }.ToJsonString();

    /// <summary>
    /// The real wire shape of the machine collection: JSON:API resources plus
    /// <c>meta.page{number,size,total,totalPages}</c>. Note <c>totalPages</c> is camelCase and the
    /// other three are not — an explicit serde rename server-side, easy to mistype as
    /// <c>total_pages</c>.
    /// </summary>
    private static string MachineListBody(int number, int size, long total, int totalPages, params (Guid Id, string Fingerprint)[] machines) => new JsonObject
    {
        ["data"] = new JsonArray(machines.Select(m => (JsonNode)MachineResource(m.Id, m.Fingerprint)).ToArray()),
        ["meta"] = new JsonObject
        {
            ["page"] = new JsonObject
            {
                ["number"] = number,
                ["size"] = size,
                ["total"] = total,
                ["totalPages"] = totalPages,
            },
        },
    }.ToJsonString();

    [Fact]
    public async Task GetMachineAsync_ReadsTheResourceAndItsTimestamps()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(id, "fp-1"));

        var machine = await client.GetMachineAsync(id);

        Assert.Equal(id, machine.Id);
        Assert.Equal("fp-1", machine.Fingerprint);
        Assert.Equal(HeartbeatStatus.Alive, machine.HeartbeatStatus);
        // `created`/`updated` are on every machine response and were previously dropped by the
        // model — the same defect the licence and policy models shipped with.
        Assert.NotNull(machine.Created);
        Assert.NotNull(machine.Updated);

        var request = handler.Requests[0].Request;
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"/v1/accounts/acct-1/machines/{id}", request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// The whole point of having a machine READ route: it is the only network call in this SDK
    /// whose response is built off a read rather than a write, so it is the only one that can
    /// report a genuine staleness verdict. A ping cannot — it writes the timestamp it then judges.
    /// </summary>
    [Fact]
    public async Task GetMachineAsync_CanReportDead_UnlikeEveryWriteRoute()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(id, "fp-1", heartbeatStatus: "DEAD"));

        var machine = await client.GetMachineAsync(id);

        Assert.Equal(HeartbeatStatus.Dead, machine.HeartbeatStatus);
    }

    [Fact]
    public async Task UpdateMachineAsync_SendsAnEnvelopedPatch_WithoutTheFingerprint()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(id, "fp-1"));

        await client.UpdateMachineAsync(id, new UpdateMachineRequest { Name = "renamed", Cores = 8 });

        var recorded = handler.Requests[0];
        Assert.Equal(HttpMethod.Patch, recorded.Request.Method);
        Assert.Equal($"/v1/accounts/acct-1/machines/{id}", recorded.Request.RequestUri!.AbsolutePath);

        var body = recorded.Body!;
        // Enveloped, like POST /machines and unlike the flat component/process creates.
        Assert.Contains("\"type\":\"machines\"", body);
        Assert.Contains("\"attributes\"", body);
        Assert.Contains("\"name\":\"renamed\"", body);
        Assert.Contains("\"cores\":8", body);
        // The update handler does not accept a fingerprint; sending one would be noise at best.
        Assert.DoesNotContain("fingerprint", body);
        // Omitted fields must stay off the wire: the server writes COALESCE($n, col), so a null
        // means "leave alone" and there is no spelling that clears a column.
        Assert.DoesNotContain("hostname", body);
    }

    /// <summary>
    /// The counterexample to "a write-backed response can never say DEAD". That rule holds because
    /// the write set <c>last_heartbeat_at</c> and the status is derived from it — and this update
    /// touches no heartbeat column, so the status is judged against a timestamp as old as it ever
    /// was. Anything that treated the HTTP verb as the discriminator would get this wrong.
    /// </summary>
    [Fact]
    public async Task UpdateMachineAsync_CanReportDead_DespiteBeingAWrite()
    {
        var (client, handler) = MakeClient();
        var id = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(id, "fp-1", heartbeatStatus: "DEAD"));

        var machine = await client.UpdateMachineAsync(id, new UpdateMachineRequest { Name = "x" });

        Assert.Equal(HeartbeatStatus.Dead, machine.HeartbeatStatus);
    }

    [Fact]
    public async Task ListMachinesAsync_ReadsOffsetMetadata_AndSendsNoCursor()
    {
        var (client, handler) = MakeClient();
        var ids = new[] { (Guid.NewGuid(), "fp-a"), (Guid.NewGuid(), "fp-b") };
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(2, 50, 142, 3, ids));

        var page = await client.ListMachinesAsync(pageNumber: 2, pageSize: 50);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(2, page.Number);
        Assert.Equal(50, page.Size);
        Assert.Equal(142, page.Total);
        Assert.Equal(3, page.TotalPages);
        Assert.True(page.HasMore);

        var query = handler.Requests[0].Request.RequestUri!.Query;
        Assert.Contains("page%5Bnumber%5D=2", query);
        Assert.Contains("page%5Bsize%5D=50", query);
        // Offset pagination, not keyset. `page[after]` belongs to the component listing and means
        // nothing here.
        Assert.DoesNotContain("after", query);
        // No license filter unless one was asked for — the unfiltered listing really is
        // account-wide, and pretending otherwise would hide that from a caller.
        Assert.DoesNotContain("filter%5Blicense%5D", query);
    }

    [Fact]
    public async Task ListMachinesAsync_SendsTheLicenseFilterWhenGiven()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (Guid.NewGuid(), "fp-a")));

        await client.ListMachinesAsync(licenseId: licenseId);

        Assert.Contains($"filter%5Blicense%5D={licenseId}", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListMachinesAsync_LastPage_HasNoMore()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(3, 50, 142, 3, (Guid.NewGuid(), "fp-c")));

        var page = await client.ListMachinesAsync(pageNumber: 3, pageSize: 50);

        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task ListMachinesAsync_DefaultsToTheServersMaximumPageSize()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (Guid.NewGuid(), "fp-a")));

        await client.ListMachinesAsync();

        // Explicit, so the server's silent default of 25 never applies.
        Assert.Contains("page%5Bsize%5D=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task ListMachinesAsync_ClampsPageSizeToTheServersCeiling()
    {
        var (client, handler) = MakeClient();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (Guid.NewGuid(), "fp-a")));

        await client.ListMachinesAsync(pageSize: 5000);

        Assert.Contains("page%5Bsize%5D=100", handler.Requests[0].Request.RequestUri!.Query);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    public async Task ListMachinesAsync_RejectsANonPositivePageNumber(int pageNumber, int? pageSize)
    {
        var (client, handler) = MakeClient();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ListMachinesAsync(pageNumber, pageSize));

        Assert.Equal("pageNumber", ex.ParamName);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ListMachinesAsync_RejectsANonPositivePageSize()
    {
        var (client, handler) = MakeClient();

        var ex = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.ListMachinesAsync(pageSize: 0));

        Assert.Equal("pageSize", ex.ParamName);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A missing <c>meta.page</c> must not read as "no further pages". Falling back to zero there
    /// would make <see cref="OffsetPage{T}.HasMore"/> false and silently truncate a walk at page
    /// one.
    /// </summary>
    [Fact]
    public async Task ListMachinesAsync_FallsBackToTheRequestedPage_WhenMetaIsAbsent()
    {
        var (client, handler) = MakeClient();
        var body = new JsonObject
        {
            ["data"] = new JsonArray(MachineResource(Guid.NewGuid(), "fp-a")),
        }.ToJsonString();
        handler.Enqueue(HttpStatusCode.OK, body);

        var page = await client.ListMachinesAsync(pageNumber: 1, pageSize: 10);

        Assert.Single(page.Items);
        Assert.Equal(1, page.Number);
        Assert.Equal(10, page.Size);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasMore);
    }

    /// <summary>
    /// There is no exact-match fingerprint filter on the collection — <c>filter[q]</c> is a
    /// substring search that also covers <c>name</c> and <c>hostname</c>. A lookup that trusted the
    /// server's result set would happily return a machine whose hostname merely contained the
    /// fingerprint.
    /// </summary>
    [Fact]
    public async Task FindMachineByFingerprintAsync_RejectsASubstringMatchOnAnotherColumn()
    {
        var (client, handler) = MakeClient();
        var decoy = Guid.NewGuid();
        // The server matched this row on `hostname`/`name`, not on an equal fingerprint.
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (decoy, "fp-1-extended")));

        var found = await client.FindMachineByFingerprintAsync(Guid.NewGuid(), "fp-1");

        Assert.Null(found);
        Assert.Contains("filter%5Bq%5D=fp-1", handler.Requests[0].Request.RequestUri!.Query);
    }

    /// <summary>
    /// The license scope is the safety property, not a convenience filter. An account-wide answer
    /// could hand back a machine belonging to another license — and since the resource carries no
    /// license id, the caller could never tell. It would then heartbeat and check out a machine its
    /// own license does not own while its own machines_count stayed at zero.
    /// </summary>
    [Fact]
    public async Task FindMachineByFingerprintAsync_ScopesTheSearchToTheLicense()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 0, 0));

        await client.FindMachineByFingerprintAsync(licenseId, "fp-1");

        var query = handler.Requests[0].Request.RequestUri!.Query;
        Assert.Contains($"filter%5Blicense%5D={licenseId}", query);
        Assert.Contains("filter%5Bq%5D=fp-1", query);
    }

    [Fact]
    public async Task FindMachineByFingerprintAsync_ReturnsTheExactMatch()
    {
        var (client, handler) = MakeClient();
        var wanted = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 2, 1, (Guid.NewGuid(), "fp-10"), (wanted, "fp-1")));

        var found = await client.FindMachineByFingerprintAsync(Guid.NewGuid(), "fp-1");

        Assert.NotNull(found);
        Assert.Equal(wanted, found!.Id);
    }

    [Fact]
    public async Task FindMachineByFingerprintAsync_WalksPagesUntilItFindsTheMatch()
    {
        var (client, handler) = MakeClient();
        var wanted = Guid.NewGuid();
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 2, 2, (Guid.NewGuid(), "fp-1x")));
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(2, 100, 2, 2, (wanted, "fp-1")));

        var found = await client.FindMachineByFingerprintAsync(Guid.NewGuid(), "fp-1");

        Assert.Equal(wanted, found!.Id);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("page%5Bnumber%5D=2", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task FindMachineByFingerprintAsync_MakesNoRequestForAnEmptyFingerprint()
    {
        var (client, handler) = MakeClient();

        Assert.Null(await client.FindMachineByFingerprintAsync(Guid.NewGuid(), ""));
        Assert.Empty(handler.Requests);
    }

    private const string FingerprintTakenBody = """
    {"errors":[{"id":"1","status":"409","code":"FINGERPRINT_TAKEN","title":"Conflict","detail":"This fingerprint is already activated"}]}
    """;

    private static string ValidationBody(Guid licenseId, string code) => new JsonObject
    {
        ["data"] = new JsonObject
        {
            ["type"] = "licenses",
            ["id"] = licenseId.ToString(),
            ["attributes"] = new JsonObject { ["status"] = "ACTIVE" },
        },
        ["meta"] = new JsonObject
        {
            ["ts"] = "2026-01-02T03:04:05Z",
            ["valid"] = code == "VALID",
            ["detail"] = "…",
            ["code"] = code,
        },
    }.ToJsonString();

    [Fact]
    public async Task ActivateMachineIdempotentAsync_AdoptsTheExistingMachine_OnFingerprintTaken()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var existing = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.Conflict, FingerprintTakenBody);                             // POST /machines
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (existing, "fp-1")));      // GET  /machines?filter[q]
        handler.Enqueue(HttpStatusCode.OK, ValidationBody(licenseId, "VALID"));                     // POST validate

        var result = await client.ActivateMachineIdempotentAsync(
            new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        Assert.True(result.AlreadyActivated);
        Assert.Equal(existing, result.Machine.Id);
        Assert.Equal(ValidationCode.Valid, result.Validation.Code);
        Assert.Equal(3, handler.Requests.Count);
        // The lookup is scoped to the license being activated, never account-wide.
        Assert.Contains($"filter%5Blicense%5D={licenseId}", handler.Requests[1].Request.RequestUri!.Query);
    }

    /// <summary>
    /// Under UNIQUE_PER_POLICY / UNIQUE_PER_ACCOUNT the conflict can come from a machine on a
    /// DIFFERENT license. Adopting it would share one fingerprint's seat across licenses — exactly
    /// what those wider scopes exist to prevent — and the client could never detect it, because the
    /// machine resource carries no license id. The scoped search finds nothing, so the server's own
    /// conflict surfaces instead.
    /// </summary>
    [Fact]
    public async Task ActivateMachineIdempotentAsync_RethrowsACrossLicenseConflict_RatherThanSharingASeat()
    {
        var (client, handler) = MakeClient();

        handler.Enqueue(HttpStatusCode.Conflict, FingerprintTakenBody);
        // The fingerprint IS taken account-wide, but not on the license being activated, so the
        // license-scoped listing comes back empty.
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 0, 0));

        var ex = await Assert.ThrowsAsync<FingerprintTakenException>(() =>
            client.ActivateMachineIdempotentAsync(
                new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() }));

        Assert.Equal("FINGERPRINT_TAKEN", ex.Error.Code);
        // No validate, no delete, and above all no machine from another license returned.
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ActivateMachineIdempotentAsync_ReportsAFreshActivationAsNotAlreadyActivated()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var created = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(created, "fp-1"));
        handler.Enqueue(HttpStatusCode.OK, ValidationBody(licenseId, "VALID"));

        var result = await client.ActivateMachineIdempotentAsync(
            new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        Assert.False(result.AlreadyActivated);
        Assert.Equal(created, result.Machine.Id);
        Assert.Equal(2, handler.Requests.Count);
    }

    /// <summary>
    /// The rollback belongs to the machine this call created. Deleting an adopted row on an
    /// over-limit verdict would destroy a seat this activation did not take — possibly another
    /// install's, and under a per-policy or per-account uniqueness strategy possibly another
    /// licence's.
    /// </summary>
    [Fact]
    public async Task ActivateMachineIdempotentAsync_NeverDeletesAnAdoptedMachine_EvenWhenOverLimit()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var existing = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.Conflict, FingerprintTakenBody);
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 1, 1, (existing, "fp-1")));
        handler.Enqueue(HttpStatusCode.OK, ValidationBody(licenseId, "TOO_MANY_MACHINES"));

        var result = await client.ActivateMachineIdempotentAsync(
            new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId },
            deleteOnOverLimit: true);

        Assert.True(result.AlreadyActivated);
        Assert.Equal(ValidationCode.TooManyMachines, result.Validation.Code);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ActivateMachineIdempotentAsync_StillRollsBackAMachineItCreated()
    {
        var (client, handler) = MakeClient();
        var licenseId = Guid.NewGuid();
        var created = Guid.NewGuid();

        handler.Enqueue(HttpStatusCode.OK, SingleMachineBody(created, "fp-1"));
        handler.Enqueue(HttpStatusCode.OK, ValidationBody(licenseId, "TOO_MANY_MACHINES"));
        handler.Enqueue(HttpStatusCode.NoContent, "");

        var result = await client.ActivateMachineIdempotentAsync(
            new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = licenseId });

        Assert.False(result.AlreadyActivated);
        var delete = Assert.Single(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
        Assert.Equal($"/v1/accounts/acct-1/machines/{created}", delete.Request.RequestUri!.AbsolutePath);
    }

    /// <summary>
    /// A conflict this method cannot resolve must surface as the server's own error, not as a
    /// second story invented client-side.
    /// </summary>
    [Fact]
    public async Task ActivateMachineIdempotentAsync_RethrowsWhenTheLicenseHoldsNoSuchFingerprint()
    {
        var (client, handler) = MakeClient();

        handler.Enqueue(HttpStatusCode.Conflict, FingerprintTakenBody);
        handler.Enqueue(HttpStatusCode.OK, MachineListBody(1, 100, 0, 0));

        var ex = await Assert.ThrowsAsync<FingerprintTakenException>(() =>
            client.ActivateMachineIdempotentAsync(
                new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() }));

        Assert.Equal("FINGERPRINT_TAKEN", ex.Error.Code);
        Assert.DoesNotContain(handler.Requests, r => r.Request.Method == HttpMethod.Delete);
    }

    /// <summary>
    /// A create-time <c>422</c> means no row was written, so there is nothing to adopt: the limit
    /// exception has to reach the caller rather than being turned into a fingerprint lookup.
    /// </summary>
    [Fact]
    public async Task ActivateMachineIdempotentAsync_PropagatesACreateTimeLimitRejection()
    {
        var (client, handler) = MakeClient();
        const string body = """
        {"errors":[{"id":"1","status":"422","code":"MACHINE_LIMIT_EXCEEDED","title":"Unprocessable Entity","detail":"limit"}]}
        """;
        handler.Enqueue(HttpStatusCode.UnprocessableEntity, body);

        await Assert.ThrowsAsync<MachineLimitExceededException>(() =>
            client.ActivateMachineIdempotentAsync(
                new CreateMachineRequest { Fingerprint = "fp-1", LicenseId = Guid.NewGuid() }));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public void OffsetPage_IsNotAKeysetPage()
    {
        // A guard against the two pagination shapes being conflated again: OffsetPage exposes a
        // total and no cursor; Page exposes a cursor and no total.
        var offset = new OffsetPage<int> { Items = [1], Number = 1, Size = 1, Total = 1, TotalPages = 1 };
        Assert.Equal(1, offset.Total);
        Assert.Null(typeof(OffsetPage<int>).GetProperty("NextCursor"));
        Assert.Null(typeof(Page<int>).GetProperty("Total"));
    }

    [Fact]
    public void MachineAttributes_BindTheServersCreatedAndUpdatedNames()
    {
        const string json = """{"created":"2026-01-02T03:04:05Z","updated":"2026-02-03T04:05:06Z"}""";
        var attrs = JsonSerializer.Deserialize<MachineAttributes>(json, TamgaJsonOptions.Default)!;

        // `created`/`updated`, not `created_at`/`updated_at` — the server renames them.
        Assert.Equal(2026, attrs.Created!.Value.Year);
        Assert.Equal(2, attrs.Updated!.Value.Month);
    }
}
