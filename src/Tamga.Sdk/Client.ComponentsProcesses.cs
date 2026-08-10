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

    /// <summary><c>GET /machines/{id}/components</c> — keyset-paginated (<c>limit</c>/<c>page[after]</c>).</summary>
    public async Task<Page<Component>> ListComponentsAsync(Guid machineId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        var query = BuildPaginationQuery(limit, after);
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Get, $"/machines/{machineId}/components", query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();

        var doc = JsonSerializer.Deserialize<FlatListDocument<Component>>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "List components returned an empty body." });
        return new Page<Component> { Items = doc.Data, NextCursor = ExtractAfterCursor(doc.Links?.Next) };
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

    private static string? ExtractAfterCursor(string? nextLink)
    {
        if (string.IsNullOrEmpty(nextLink))
        {
            return null;
        }

        // `links.next` is a full URL/query fragment; pull the bare `page[after]` cursor value out
        // of it rather than assuming it's already a bare cursor.
        var queryStart = nextLink.IndexOf('?');
        var query = queryStart >= 0 ? nextLink[(queryStart + 1)..] : nextLink;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && (kv[0] == "page[after]" || kv[0] == "page%5Bafter%5D"))
            {
                return Uri.UnescapeDataString(kv[1]);
            }
        }

        return null;
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

    /// <summary>Raised after each successful ping with the updated process resource.</summary>
    public event Action<Process>? Pinged;

    /// <summary>Raised when a ping throws — the loop continues on the next tick rather than terminating.</summary>
    public event Action<Exception>? Faulted;

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
                try
                {
                    var process = await _client.PingProcessAsync(_processId, cancellationToken).ConfigureAwait(false);
                    Pinged?.Invoke(process);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Faulted?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on DisposeAsync.
        }
    }

    public async ValueTask DisposeAsync()
    {
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
