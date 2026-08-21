using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

/// <summary>Flat (non-JSON:API-enveloped) keyset-paginated list response shape used by <c>GET /machines/{id}/components</c>.</summary>
internal sealed record FlatListDocument<T>
{
    [JsonPropertyName("data")]
    public IReadOnlyList<T> Data { get; init; } = Array.Empty<T>();

    [JsonPropertyName("links")]
    public JsonApiLinks? Links { get; init; }
}

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §I Components & Processes
    //
    // GOTCHA: unlike /machines, POST /components and POST /processes use flat, non-JSON:API-
    // enveloped request AND response bodies — no {"data": {"attributes": ...}} wrapping.
    // ---------------------------------------------------------------

    /// <summary><c>POST /components</c> — creates a machine component.</summary>
    /// <exception cref="FingerprintTakenException"><c>409 FINGERPRINT_TAKEN</c> — duplicate fingerprint on the same machine.</exception>
    public async Task<Component> CreateComponentAsync(CreateComponentRequest request, CancellationToken cancellationToken = default)
    {
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Post, "/components", jsonBody: request, jsonApiContentType: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();
        return JsonSerializer.Deserialize<Component>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "Create component returned an empty body." });
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

        var doc = JsonSerializer.Deserialize<FlatListDocument<Component>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "List components returned an empty body." });
        return new Page<Component>
        {
            Items = doc.Data,
            // `>= ` not `==`, and the emptiness guard is load-bearing: it keeps `[^1]` off an empty
            // list no matter what row count the server returns.
            NextCursor = doc.Data.Count > 0 && doc.Data.Count >= effectiveLimit ? doc.Data[^1].Id.ToString() : null,
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
        return JsonSerializer.Deserialize<Process>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "Create process returned an empty body." });
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
        return JsonSerializer.Deserialize<Process>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "Ping process returned an empty body." });
    }

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

    /// <summary>Creates a scheduler for a single process. Call <see cref="Start"/> to begin pinging.</summary>
    /// <param name="client">The client used to send each heartbeat ping.</param>
    /// <param name="processId">The ID of the process to ping.</param>
    /// <param name="interval">The ping interval; defaults to <see cref="DefaultInterval"/> when omitted.</param>
    public ProcessHeartbeatScheduler(TamgaClient client, Guid processId, TimeSpan? interval = null)
    {
        _client = client;
        _processId = processId;
        _timer = new PeriodicTimer(interval ?? DefaultInterval);
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

    /// <summary>Stops the background heartbeat loop and releases resources. Safe to call more than once.</summary>
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

        _timer.Dispose();
        _cts.Dispose();
    }
}
