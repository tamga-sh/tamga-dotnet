using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §G Machine Management
    //
    // Machine/core/memory/disk limits are checked BOTH at creation time and by license validation
    // (§C), and the two are not redundant. The create-time check runs through the policy's overage
    // strategy, so under ALLOW_ACCESS / ALLOW_1_25X_OVERAGE the create still succeeds and the
    // limit only surfaces at validate. ActivateMachineAsync below therefore keeps the full
    // "create → validate → interpret over-limit codes → optionally delete" dance AND surfaces the
    // create-time 422 path, normalized onto the same ValidationCode values.
    // ---------------------------------------------------------------

    /// <summary><c>POST /machines</c> — creates a machine. Required: <see cref="CreateMachineRequest.Fingerprint"/>, <see cref="CreateMachineRequest.LicenseId"/>.</summary>
    /// <remarks>
    /// Quota is enforced here, not only at validation time. Uniqueness is checked first, so
    /// re-sending an already-activated fingerprint is reported as
    /// <see cref="FingerprintTakenException"/> ("already activated, carry on") rather than as a
    /// quota failure — do not tell the user to buy more seats on a <c>409</c>.
    /// </remarks>
    /// <exception cref="FingerprintTakenException"><c>409 FINGERPRINT_TAKEN</c> — the fingerprint is already activated within the policy's uniqueness scope (which may be a <em>different</em> license under <c>UNIQUE_PER_POLICY</c>/<c>UNIQUE_PER_ACCOUNT</c>).</exception>
    /// <exception cref="MachineLimitExceededException"><c>422 MACHINE_LIMIT_EXCEEDED</c> — the license is at its machine limit under the policy's overage strategy.</exception>
    /// <exception cref="CoreLimitExceededException"><c>422 CORE_LIMIT_EXCEEDED</c>.</exception>
    /// <exception cref="MemoryLimitExceededException"><c>422 MEMORY_LIMIT_EXCEEDED</c> — remember <see cref="CreateMachineRequest.Memory"/> is in megabytes.</exception>
    /// <exception cref="DiskLimitExceededException"><c>422 DISK_LIMIT_EXCEEDED</c> — remember <see cref="CreateMachineRequest.Disk"/> is in megabytes.</exception>
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
    /// <see langword="true"/> (the default), because the machine row exists and is counted against
    /// the license unless this SDK removes it.
    /// </summary>
    /// <remarks>
    /// There are TWO ways an activation can be over-limit, and a caller has to handle both:
    ///
    /// <list type="number">
    /// <item>
    /// <description>
    /// <b>The create is refused outright</b> (<c>422</c>, e.g. <c>MACHINE_LIMIT_EXCEEDED</c>).
    /// This method then throws a <see cref="TamgaLimitExceededException"/> and never reaches the
    /// validate or delete steps — there is nothing to roll back, because nothing was created.
    /// Read <see cref="TamgaLimitExceededException.EquivalentValidationCode"/> to get the same
    /// <see cref="ValidationCode"/> path 2 would have reported.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>The create succeeds and validation reports the overage.</b> The server's create-time
    /// check runs through the policy's overage strategy, so under <c>ALLOW_ACCESS</c> or
    /// <c>ALLOW_1_25X_OVERAGE</c> the row is created and only <c>validate</c> objects. This is the
    /// path <paramref name="deleteOnOverLimit"/> exists for, and it is still live — do not assume
    /// path 1 replaced it.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    /// <exception cref="TamgaLimitExceededException">The server refused the create itself — see path 1 above.</exception>
    /// <exception cref="FingerprintTakenException">The fingerprint is already activated in the policy's uniqueness scope; the existing machine is untouched.</exception>
    public async Task<(Machine Machine, ValidationResult Validation)> ActivateMachineAsync(
        CreateMachineRequest request,
        bool deleteOnOverLimit = true,
        CancellationToken cancellationToken = default)
    {
        // A create-time limit rejection propagates as-is: no machine row was written, so running
        // the validate/delete rollback below would delete nothing and only obscure the cause.
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
    /// <remarks>
    /// PERMISSIONS: this endpoint is role-gated, not permission-gated, and the license-key role is
    /// not on the list (admin / developer / product token / environment token only). A client
    /// configured with <see cref="AuthTransport.License"/> or
    /// <see cref="AuthTransport.BasicLicense"/> therefore gets <c>403</c> from this call every
    /// time. That matters because resetting is the only server-side way to unstick a machine whose
    /// heartbeat job is wedged — so an embedded client cannot self-recover here, and should not
    /// present this as a recovery action. Contrast <see cref="PingHeartbeatAsync"/>, which is
    /// permission-gated and works fine on a license key.
    /// </remarks>
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
/// <c>policy.heartbeat_duration</c>, which the server ignores for this purpose (Tamga API
/// protocol specification gap #8). Raises <see cref="Dead"/> when a ping observes
/// <see cref="HeartbeatStatus.Dead"/> — per the protocol reference, this means the machine was
/// likely deleted/culled server-side; callers should re-activate rather than keep retrying the
/// ping.
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
    private bool _disposed;

    /// <summary>Raised after each successful ping with the updated machine resource. A throwing handler is caught and rerouted to <see cref="Faulted"/> rather than killing the ping loop.</summary>
    public event Action<Machine>? Pinged;

    /// <summary>Raised when a ping throws, or when a <see cref="Pinged"/>/<see cref="Dead"/> handler itself throws — the loop continues on the next tick rather than terminating.</summary>
    public event Action<Exception>? Faulted;

    /// <summary>Raised when a ping observes <see cref="HeartbeatStatus.Dead"/> — see type-level remarks. A throwing handler is caught and rerouted to <see cref="Faulted"/> rather than killing the ping loop.</summary>
    public event Action<Machine>? Dead;

    /// <summary>Creates a scheduler for a single machine. Call <see cref="Start"/> to begin pinging.</summary>
    /// <param name="client">The client used to send each heartbeat ping.</param>
    /// <param name="machineId">The ID of the machine to ping.</param>
    /// <param name="interval">The ping interval; defaults to <see cref="DefaultInterval"/> when omitted.</param>
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
                Machine machine;
                try
                {
                    machine = await _client.PingHeartbeatAsync(_machineId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    InvokeFaulted(ex);
                    continue;
                }

                // A throwing Pinged/Dead handler must not kill the loop — reroute to Faulted
                // instead, same as a failed ping itself.
                InvokeSafely(Pinged, machine);
                if (machine.HeartbeatStatus == HeartbeatStatus.Dead)
                {
                    InvokeSafely(Dead, machine);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on Stop/DisposeAsync.
        }
    }

    private void InvokeSafely(Action<Machine>? handler, Machine machine)
    {
        try
        {
            handler?.Invoke(machine);
        }
        catch (Exception ex)
        {
            InvokeFaulted(ex);
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

    /// <summary>Stops the ping loop and releases the underlying timer. Safe to call more than once.</summary>
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
