using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §I Components & Processes
    //
    // GOTCHA: unlike /machines, POST /components and POST /processes take flat, non-JSON:API-
    // enveloped REQUEST bodies — {machine_id, fingerprint, name, metadata} straight at the root,
    // no {"data": {"attributes": …}} wrapping. That asymmetry is real server behaviour and a port
    // that "normalizes" it will fail against the live API.
    //
    // It is REQUEST-ONLY. Every response on these routes — create, ping and both listings — comes
    // back through components::serializer / processes::serializer as an ordinary JSON:API document
    // with {type, id, attributes}, exactly like machines and licences. This SDK read the asymmetry
    // as symmetric and decoded the responses flat, which cost it every attribute on every one of
    // them; see the commit that introduced these FromResource calls.
    // ---------------------------------------------------------------

    /// <summary>
    /// Parses a JSON:API single-resource document from a body fetched with
    /// <see cref="TamgaTransport.SendRawAsync"/>.
    /// </summary>
    /// <remarks>
    /// These four routes go through <c>SendRawAsync</c> rather than
    /// <see cref="TamgaTransport.SendJsonApiAsync{T}"/> for one reason: the two creates must send
    /// their flat body as <c>application/json</c>, and <c>SendJsonApiAsync</c> hardcodes
    /// <c>application/vnd.api+json</c>. The response parsing is identical either way.
    /// </remarks>
    private static JsonApiDocument<T> ParseResourceDocument<T>(string body, string what) =>
        JsonSerializer.Deserialize<JsonApiDocument<T>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = what });

    /// <summary>Parses a JSON:API list document from a body fetched with <see cref="TamgaTransport.SendRawAsync"/>.</summary>
    private static JsonApiListDocument<T> ParseListDocument<T>(string body, string what) =>
        JsonSerializer.Deserialize<JsonApiListDocument<T>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = what });

    /// <summary><c>POST /components</c> — creates a machine component.</summary>
    /// <exception cref="FingerprintTakenException"><c>409 FINGERPRINT_TAKEN</c> — duplicate fingerprint on the same machine.</exception>
    public async Task<Component> CreateComponentAsync(CreateComponentRequest request, CancellationToken cancellationToken = default)
    {
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Post, "/components", jsonBody: request, jsonApiContentType: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();
        var doc = ParseResourceDocument<Component>(body, "Create component returned an empty body.");
        return Component.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>The server's maximum (and this SDK's default) page size for component listings — <c>limit</c> is clamped to <c>1..100</c> server-side.</summary>
    private const int MaxComponentsPageSize = 100;

    /// <summary><c>GET /machines/{id}/components</c> — keyset-paginated (<c>limit</c>/<c>page[after]</c>). Unlike entitlements, the cursor genuinely works here.</summary>
    /// <param name="machineId">The machine whose components to list.</param>
    /// <param name="limit">Page size, <c>1..100</c>. Defaults to 100 (the maximum) rather than letting the server apply its silent default of 25. Values above 100 are clamped to match the server's own clamp; values below 1 are rejected.</param>
    /// <param name="after">The <c>page[after]</c> cursor from a previous page's <see cref="Page{T}.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The cursor is synthesized from the last item's id when the page came back full, because the
    /// server emits no <c>links</c> object to read one out of (see <see cref="JsonApiLinks"/>) —
    /// page fullness measured against a known <c>limit</c> is the only available end-of-list
    /// signal. That is also why an explicit <c>limit</c> is always sent: with the limit left
    /// implicit there is no number to compare the row count against, and a listing would stop at
    /// the server's default of 25 rows with no indication it had been cut short.
    ///
    /// Because the end-of-list signal is a row-count comparison, <paramref name="limit"/> has to
    /// agree with the limit the server will actually apply, and it must be positive:
    /// <list type="bullet">
    /// <item><description>
    /// A <paramref name="limit"/> above 100 is clamped here to the server's own ceiling. Left
    /// unclamped, a full 100-row page would never equal the requested count, the cursor would come
    /// back <see langword="null"/>, and the listing would silently truncate at 100 rows.
    /// </description></item>
    /// <item><description>
    /// A <paramref name="limit"/> of <c>0</c> or less is rejected rather than passed on. Zero in
    /// particular used to satisfy the fullness test against an empty page (<c>0 == 0</c>) and then
    /// index <c>[^1]</c> into an empty list, throwing
    /// <see cref="ArgumentOutOfRangeException"/> from deep inside the mapper.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than 1.</exception>
    public async Task<Page<Component>> ListComponentsAsync(Guid machineId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "Page size must be at least 1; pass null for the default of 100.");
        }

        // Clamped to the server's ceiling so the fullness comparison below is measured against the
        // limit the server will actually honour, not the one that was asked for.
        var effectiveLimit = Math.Min(limit ?? MaxComponentsPageSize, MaxComponentsPageSize);
        var query = BuildPaginationQuery(effectiveLimit, after);
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Get, $"/machines/{machineId}/components", query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();

        var doc = ParseListDocument<Component>(body, "List components returned an empty body.");
        var items = doc.Data.Select(Component.FromResource).ToList();
        return new Page<Component>
        {
            Items = items,
            NextCursor = SynthesizeCursor(items.Count, effectiveLimit, items.Count > 0 ? items[^1].Id : Guid.Empty),
        };
    }

    /// <summary>
    /// <c>POST /processes</c> — creates a machine process. <paramref name="pid"/> is deliberately
    /// <see cref="string"/>, not <see cref="int"/> — see <see cref="Process.Pid"/>'s remarks;
    /// forcing callers to pass a string here prevents an accidental silent numeric-to-string
    /// coercion bug at the call site.
    /// </summary>
    /// <exception cref="PidTakenException"><c>409 PID_TAKEN</c> — duplicate PID on the same machine.</exception>
    public async Task<Process> CreateProcessAsync(Guid machineId, string pid, Dictionary<string, JsonElement>? metadata = null, CancellationToken cancellationToken = default)
    {
        var request = new CreateProcessRequest { MachineId = machineId, Pid = pid, Metadata = metadata };
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Post, "/processes", jsonBody: request, jsonApiContentType: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();
        var doc = ParseResourceDocument<Process>(body, "Create process returned an empty body.");
        return Process.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>
    /// <c>POST /processes/{id}/actions/ping</c> — heartbeat ping, no body. GOTCHA: the process
    /// heartbeat window is a hardcoded 30 seconds with no resurrection grace period — a dead
    /// process row is deleted immediately, unlike machines.
    /// </summary>
    public async Task<Process> PingProcessAsync(Guid processId, CancellationToken cancellationToken = default)
    {
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Post, $"/processes/{processId}/actions/ping", jsonApiContentType: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();
        var doc = ParseResourceDocument<Process>(body, "Ping process returned an empty body.");
        return Process.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>The server's maximum (and this SDK's default) page size for process listings — <c>limit</c> is clamped to <c>1..100</c> server-side.</summary>
    private const int MaxProcessesPageSize = 100;

    /// <summary>
    /// <c>GET /machines/{id}/processes</c> — keyset-paginated (<c>limit</c>/<c>page[after]</c>)
    /// listing of the processes registered against one machine.
    /// </summary>
    /// <param name="machineId">The machine whose processes to list.</param>
    /// <param name="limit">Page size, <c>1..100</c>. Defaults to 100 (the maximum) rather than letting the server apply its silent default of 25. Values above 100 are clamped to match the server's own clamp; values below 1 are rejected.</param>
    /// <param name="after">The <c>page[after]</c> cursor from a previous page's <see cref="Page{T}.NextCursor"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// KEYSET, like the component listing and unlike <see cref="ListMachinesAsync"/> — the server
    /// emits no <c>meta.page</c> here, so the cursor is synthesized from the last item of a full
    /// page and there is no total to read. See <see cref="OffsetPage{T}"/> for why the two shapes
    /// are kept apart.
    /// </para>
    /// <para>
    /// This is the listing that makes leaked process rows visible. Nothing on the server removes
    /// them (see <see cref="DeleteProcessAsync"/>), so a machine that has been running a
    /// process-registering worker for months accumulates one row per run — and they count against
    /// <c>policy.max_processes</c>. Enumerate here to find them; <see cref="DeleteProcessAsync"/>
    /// to remove them.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is less than 1.</exception>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such machine in this account.</exception>
    public async Task<Page<Process>> ListMachineProcessesAsync(Guid machineId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit, "Page size must be at least 1; pass null for the default of 100.");
        }

        var effectiveLimit = Math.Min(limit ?? MaxProcessesPageSize, MaxProcessesPageSize);
        var query = BuildPaginationQuery(effectiveLimit, after);
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Get, $"/machines/{machineId}/processes", query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();

        var doc = ParseListDocument<Process>(body, "List machine processes returned an empty body.");
        var items = doc.Data.Select(Process.FromResource).ToList();
        return new Page<Process>
        {
            Items = items,
            NextCursor = SynthesizeCursor(items.Count, effectiveLimit, items.Count > 0 ? items[^1].Id : Guid.Empty),
        };
    }

    /// <summary>
    /// The <c>page[after]</c> cursor for a keyset listing: the last row's id when the page came
    /// back full, otherwise <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server emits no <c>links</c> object on any route, so page fullness measured against a
    /// known limit is the only end-of-list signal available. <c>&gt;=</c> rather than <c>==</c>,
    /// and the emptiness guard, keep <c>[^1]</c> off an empty list whatever row count comes back.
    /// </para>
    /// <para>
    /// The all-zero guard is the belt to that braces. A cursor of
    /// <c>00000000-0000-0000-0000-000000000000</c> sorts before every UUIDv7 row, so feeding one
    /// back as <c>page[after]</c> returns the same first page again — and a caller looping until
    /// <see cref="Page{T}.NextCursor"/> goes null would never terminate. No correct decode can
    /// produce an empty id, but a decode bug can, and silently: this SDK spent its whole life
    /// decoding these listings flat. A truncated listing is a bad outcome; an infinite loop is a
    /// worse one, so an empty id ends the walk rather than restarting it.
    /// </para>
    /// </remarks>
    private static string? SynthesizeCursor(int count, int limit, Guid lastId) =>
        count > 0 && count >= limit && lastId != Guid.Empty ? lastId.ToString() : null;

    /// <summary><c>DELETE /processes/{id}</c> — removes a process row. <c>204</c>, no body.</summary>
    /// <param name="processId">The process to delete.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Nothing deletes these rows for you.</b> The server has a process reaper, but it is not
    /// wired to run — so unlike the machine cull (which at least runs when
    /// <c>policy.require_heartbeat</c> is set), a process row outlives the process it represents
    /// forever unless a client removes it. Every run of a short-lived worker that registers a
    /// process therefore leaks one row, and those rows count against the policy's
    /// <c>max_processes</c> limit, so a long-running install eventually starts failing activation
    /// with <see cref="TooManyProcessesException"/> for processes that exited months ago.
    /// </para>
    /// <para>
    /// Call this when the process ends. <see cref="ProcessHeartbeatScheduler.DeleteOnDispose"/>
    /// wires it to the scheduler's own lifetime for the common case.
    /// </para>
    /// <para>
    /// A <c>404</c> means the row is already gone — for a cleanup path that is usually success, not
    /// failure. Treat <see cref="TamgaNotFoundException"/> accordingly.
    /// </para>
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such process; it was already deleted.</exception>
    public Task DeleteProcessAsync(Guid processId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync($"/processes/{processId}", cancellationToken);

    private static string? BuildPaginationQuery(int? limit, string? after)
    {
        var parts = new List<string>();
        if (limit is int l)
        {
            parts.Add($"limit={l}");
        }

        if (!string.IsNullOrEmpty(after))
        {
            parts.Add($"page%5Bafter%5D={Uri.EscapeDataString(after)}");
        }

        return parts.Count == 0 ? null : string.Join('&', parts);
    }
}

/// <summary>
/// Periodic heartbeat pinger for a single process, built on <see cref="PeriodicTimer"/>. Defaults
/// to a ~10s interval — much tighter than <see cref="HeartbeatScheduler"/>'s ~200s default,
/// because the server's process heartbeat window is a hardcoded 30 seconds with no resurrection
/// grace period (a dead process row is deleted immediately). Do not reuse
/// <see cref="HeartbeatScheduler.DefaultInterval"/> for processes.
/// </summary>
public sealed class ProcessHeartbeatScheduler : IAsyncDisposable
{
    /// <summary>The server's hardcoded process heartbeat window, in seconds.</summary>
    public const int ServerProcessHeartbeatWindowSeconds = 30;

    /// <summary>Default ping interval — well inside the 30s window.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(10);

    private readonly TamgaClient _client;
    private readonly Guid _processId;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Raised after each successful ping with the updated process resource. A throwing handler is caught and rerouted to <see cref="Faulted"/> rather than killing the ping loop.</summary>
    public event Action<Process>? Pinged;

    /// <summary>Raised when a ping throws, or when a <see cref="Pinged"/> handler itself throws — the loop continues on the next tick rather than terminating.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// Whether <see cref="DisposeAsync"/> should also <c>DELETE</c> the process row. Defaults to
    /// <see langword="false"/>, which preserves the previous behaviour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it when the scheduler's lifetime matches the process's — the usual case, since a
    /// scheduler exists to say "this process is still running". Nothing on the server removes the
    /// row otherwise (see <see cref="TamgaClient.DeleteProcessAsync"/>), so without this every run
    /// of the host application leaves one behind, and they count against
    /// <c>policy.max_processes</c>.
    /// </para>
    /// <para>
    /// The delete runs on its own <see cref="CancellationToken"/>, not the one that just stopped
    /// the loop — cancelling the pings is the signal to clean up, so reusing that token would
    /// cancel the cleanup along with them. A failed delete is reported on <see cref="Faulted"/> and
    /// never propagates out of <see cref="DisposeAsync"/>: throwing from disposal would mask
    /// whatever exception was already unwinding the caller's <c>await using</c> block.
    /// </para>
    /// <para>
    /// Set it before <see cref="Start"/>; it is read once, at disposal.
    /// </para>
    /// </remarks>
    public bool DeleteOnDispose { get; init; }

    /// <summary>Creates a scheduler for a single process. Call <see cref="Start"/> to begin pinging.</summary>
    /// <param name="client">The client used to send each heartbeat ping.</param>
    /// <param name="processId">The ID of the process to ping.</param>
    /// <param name="interval">
    /// The ping interval; defaults to <see cref="DefaultInterval"/> when omitted, falls back to it
    /// when non-positive, and is raised to one second when positive but shorter.
    /// </param>
    /// <remarks>
    /// Same clamp, and for the same reason, as <see cref="HeartbeatScheduler(TamgaClient, Guid, TimeSpan?)"/>
    /// — see its remarks. A zero or negative <paramref name="interval"/> falls back to
    /// <see cref="DefaultInterval"/> instead of reaching <see cref="PeriodicTimer"/> and throwing
    /// <see cref="ArgumentOutOfRangeException"/>; a positive one shorter than one second is raised
    /// to one second, because <see cref="PeriodicTimer"/> honours a 1ms period exactly (~765 ticks
    /// per second on net8.0) and only a floor bounds the request rate; and
    /// <see cref="Timeout.InfiniteTimeSpan"/>, which the timer accepts as "never tick", is passed
    /// through unchanged. The process window is not policy-driven (it is a hardcoded 30s
    /// server-side), so a bad value cannot arrive here from a policy — but a caller computing an
    /// interval arithmetically can still produce one, and the two schedulers should not answer the
    /// same mistake differently.
    /// </remarks>
    public ProcessHeartbeatScheduler(TamgaClient client, Guid processId, TimeSpan? interval = null)
    {
        _client = client;
        _processId = processId;
        _timer = new PeriodicTimer(TickPeriod(interval));
    }

    /// <summary>
    /// The period actually handed to <see cref="PeriodicTimer"/>: <see cref="Timeout.InfiniteTimeSpan"/>
    /// untouched, <see cref="DefaultInterval"/> for a non-positive <paramref name="interval"/>, and
    /// otherwise <paramref name="interval"/> raised to
    /// <see cref="HeartbeatScheduler.MinimumInterval"/>. Every value this returns is either the
    /// sentinel or at least one second. See the constructor's remarks.
    /// </summary>
    private static TimeSpan TickPeriod(TimeSpan? interval)
    {
        var period = interval ?? DefaultInterval;
        if (period == Timeout.InfiniteTimeSpan)
        {
            return period;
        }

        return period > TimeSpan.Zero ? HeartbeatScheduler.AtLeastMinimum(period) : DefaultInterval;
    }

    /// <summary>Starts the ping loop on a background task. Call once per instance.</summary>
    public void Start()
    {
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Process process;
                try
                {
                    process = await _client.PingProcessAsync(_processId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    InvokeFaulted(ex);
                    continue;
                }

                // A throwing Pinged handler must not kill the loop — reroute to Faulted instead,
                // same as a failed ping itself.
                try
                {
                    Pinged?.Invoke(process);
                }
                catch (Exception ex)
                {
                    InvokeFaulted(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync.
        }
    }

    private void InvokeFaulted(Exception ex)
    {
        try
        {
            Faulted?.Invoke(ex);
        }
        catch
        {
            // A throwing Faulted handler has nowhere left to report to — swallow rather than
            // crash the loop that exists specifically to keep reporting failures.
        }
    }

    /// <summary>
    /// Stops the background heartbeat loop, optionally deletes the process row
    /// (<see cref="DeleteOnDispose"/>), and releases resources. Safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (DeleteOnDispose)
        {
            try
            {
                // CancellationToken.None on purpose: _cts was just cancelled to stop the pings, and
                // that is precisely the moment the row should be removed. Threading the same token
                // through would cancel the cleanup the cancellation was asking for.
                await _client.DeleteProcessAsync(_processId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Disposal must not throw. A cleanup failure is worth reporting, but not at the
                // cost of replacing whatever exception was already unwinding the caller's scope.
                InvokeFaulted(ex);
            }
        }

        _timer.Dispose();
        _cts.Dispose();
    }
}
