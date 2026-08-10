using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §G Machine Management
    //
    // GOTCHA: no machine/core/etc. limit is checked at machine creation time — those limits only
    // surface later via license validation (§C). ActivateMachineAsync below implements the
    // "create → validate → interpret over-limit codes → optionally delete" dance this requires.
    // ---------------------------------------------------------------

    /// <summary><c>POST /machines</c> — creates a machine. Required: <see cref="CreateMachineRequest.Fingerprint"/>, <see cref="CreateMachineRequest.LicenseId"/>.</summary>
    /// <exception cref="FingerprintTakenException"><c>409 FINGERPRINT_TAKEN</c> — duplicate fingerprint on the same license.</exception>
    public async Task<Machine> CreateMachineAsync(CreateMachineRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonApiCreateRequest<MachineAttributes>
        {
            Data = new JsonApiCreateRequestData<MachineAttributes>
            {
                Type = "machines",
                Attributes = new MachineAttributes
                {
                    Fingerprint = request.Fingerprint,
                    Name = request.Name,
                    Ip = request.Ip,
                    Hostname = request.Hostname,
                    Platform = request.Platform,
                    Cores = request.Cores,
                    Memory = request.Memory,
                    Disk = request.Disk,
                    Metadata = request.Metadata,
                },
                Relationships = new Dictionary<string, JsonApiRelationship>
                {
                    ["license"] = new JsonApiRelationship { Data = new JsonApiResourceIdentifier { Type = "licenses", Id = request.LicenseId } },
                },
            },
        };

        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Post, "/machines", jsonBody: body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Machine.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary><c>DELETE /machines/{id}</c>.</summary>
    public Task DeleteMachineAsync(Guid machineId, CancellationToken cancellationToken = default) =>
        _transport.DeleteAsync($"/machines/{machineId}", cancellationToken);

    /// <summary>
    /// Convenience helper implementing "reject over-limit activation" UX: creates the machine,
    /// then validates the license and interprets <see cref="ValidationCode.TooManyMachines"/>/
    /// <see cref="ValidationCode.TooManyCores"/>/<see cref="ValidationCode.TooMuchMemory"/>/
    /// <see cref="ValidationCode.TooMuchDisk"/>/<see cref="ValidationCode.TooManyProcesses"/>. On
    /// any of these, deletes the just-created machine when <paramref name="deleteOnOverLimit"/> is
    /// <see langword="true"/> (the default) — there is no server-side limit check at creation time,
    /// so the machine row exists even though the license is over its limit unless this SDK removes
    /// it.
    /// </summary>
    public async Task<(Machine Machine, ValidationResult Validation)> ActivateMachineAsync(
        CreateMachineRequest request,
        bool deleteOnOverLimit = true,
        CancellationToken cancellationToken = default)
    {
        var machine = await CreateMachineAsync(request, cancellationToken).ConfigureAwait(false);
        var validation = await ValidateByIdAsync(request.LicenseId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (deleteOnOverLimit && IsOverLimitCode(validation.Code))
        {
            await DeleteMachineAsync(machine.Id, cancellationToken).ConfigureAwait(false);
        }

        return (machine, validation);
    }

    private static bool IsOverLimitCode(ValidationCode code) => code is
        ValidationCode.TooManyMachines or
        ValidationCode.TooManyCores or
        ValidationCode.TooMuchMemory or
        ValidationCode.TooMuchDisk or
        ValidationCode.TooManyProcesses;

    /// <summary><c>POST /machines/{id}/actions/ping-heartbeat</c> — no body, sets <c>last_heartbeat_at = now</c>. Returns the updated machine resource.</summary>
    public async Task<Machine> PingHeartbeatAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Post, $"/machines/{machineId}/actions/ping-heartbeat", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Machine.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary><c>POST /machines/{id}/actions/reset-heartbeat</c> — no body, fully rewinds heartbeat state to <see cref="HeartbeatStatus.NotStarted"/>. Returns the updated machine resource.</summary>
    public async Task<Machine> ResetHeartbeatAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Post, $"/machines/{machineId}/actions/reset-heartbeat", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Machine.FromResource(doc.Data ?? throw MissingDataError());
    }

    private static TamgaApiException MissingDataError() =>
        new(new TamgaApiError { Status = 200, Code = "MISSING_DATA", Detail = "Response had no resource." });
}

/// <summary>
/// Periodic heartbeat pinger for a single machine, built on <see cref="PeriodicTimer"/>. Pings on
/// an interval set to ~1/3 of the server's hardcoded 600s heartbeat window
/// (<see cref="DefaultInterval"/>) — GOTCHA: this is deliberately NOT derived from
/// <c>policy.heartbeat_duration</c>, which the server ignores for this purpose (docs/sdk.md gap
/// #8). Raises <see cref="Dead"/> when a ping observes <see cref="HeartbeatStatus.Dead"/> — per
/// the protocol reference, this means the machine was likely deleted/culled server-side; callers
/// should re-activate rather than keep retrying the ping.
/// </summary>
public sealed class HeartbeatScheduler : IAsyncDisposable
{
    /// <summary>The server's hardcoded heartbeat window, in seconds — NOT policy-driven (gap #8).</summary>
    public const int ServerHeartbeatWindowSeconds = 600;

    /// <summary>Default ping interval: ~1/3 of <see cref="ServerHeartbeatWindowSeconds"/>.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(ServerHeartbeatWindowSeconds / 3.0);

    private readonly TamgaClient _client;
    private readonly Guid _machineId;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised after each successful ping with the updated machine resource.</summary>
    public event Action<Machine>? Pinged;

    /// <summary>Raised when a ping throws — the loop continues on the next tick rather than terminating.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Raised when a ping observes <see cref="HeartbeatStatus.Dead"/> — see type-level remarks.</summary>
    public event Action<Machine>? Dead;

    public HeartbeatScheduler(TamgaClient client, Guid machineId, TimeSpan? interval = null)
    {
        _client = client;
        _machineId = machineId;
        _timer = new PeriodicTimer(interval ?? DefaultInterval);
    }

    /// <summary>Starts the ping loop on a background task. Idempotent-unsafe: call once per instance.</summary>
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
                    var machine = await _client.PingHeartbeatAsync(_machineId, cancellationToken).ConfigureAwait(false);
                    Pinged?.Invoke(machine);
                    if (machine.HeartbeatStatus == HeartbeatStatus.Dead)
                    {
                        Dead?.Invoke(machine);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Faulted?.Invoke(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop/DisposeAsync.
        }
    }

    /// <summary>Stops the ping loop and releases the underlying timer.</summary>
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
