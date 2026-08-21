using System.Text.Json;
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
    /// <remarks>
    /// Revives a machine that has gone <c>DEAD</c> server-side: this is a bare
    /// <c>UPDATE … SET last_heartbeat_at = NOW()</c> with no resurrection gate in front of it, so
    /// it succeeds however long the machine has been silent.
    ///
    /// The returned resource will not tell you that is what happened. Status is derived from the
    /// timestamp this call just wrote, so its age is ~0 and the answer is always
    /// <see cref="HeartbeatStatus.Alive"/>, or <see cref="HeartbeatStatus.Resurrected"/> when a
    /// death event had already been recorded — never <see cref="HeartbeatStatus.Dead"/>. Branching
    /// on <c>Dead</c> here is unreachable code. <c>NextHeartbeatAt</c> on this response is also
    /// fallback-derived rather than policy-derived; see <see cref="Machine.NextHeartbeatAt"/>.
    ///
    /// A <see cref="TamgaNotFoundException"/> (<c>404</c>) is the one response that means the row
    /// is gone and re-activation is required.
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — the machine row no longer exists; re-activate.</exception>
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

    // ---------------------------------------------------------------
    // §G.2 Machine reads
    //
    // GetMachineAsync and ListMachinesAsync are the SDK's only network routes whose query LEFT
    // JOINs `policies`, so they are the only ones whose `HeartbeatStatus` and `NextHeartbeatAt`
    // are computed against the policy's real window rather than the 600s fallback.
    //
    // Note that this is NOT the same axis as write-vs-read, and the two must not be collapsed.
    // Write-vs-read answers "can this response say DEAD?" — a response the server builds off a
    // write that touched `last_heartbeat_at` cannot, because the status is derived from the
    // timestamp that write just set. Joined-vs-unjoined answers "is next_heartbeat_at policy-
    // accurate?". UpdateMachineAsync is the case that separates them: it is a write, but it never
    // touches `last_heartbeat_at`, so its response CAN report DEAD — and its UPDATE … RETURNING
    // does not join `policies`, so its next_heartbeat_at is on the fallback side anyway.
    // ---------------------------------------------------------------

    /// <summary><c>GET /machines/{id}</c> — reads a machine resource.</summary>
    /// <param name="machineId">The machine to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="PingHeartbeatAsync"/>, this is a genuine staleness verdict: nothing here
    /// wrote <c>last_heartbeat_at</c> first, so <see cref="Machine.HeartbeatStatus"/> can come back
    /// <see cref="HeartbeatStatus.Dead"/> and <see cref="Machine.NextHeartbeatAt"/> is derived from
    /// the policy's window. <c>NextHeartbeatAt - LastHeartbeatAt</c> on this result therefore
    /// recovers the effective window, the same way a checked-out <c>.machine</c> file does — null
    /// until the machine has pinged at least once.
    /// </para>
    /// <para>
    /// <see cref="HeartbeatStatus.Dead"/> here still does not mean the row was culled or its seat
    /// released: culling is gated on <c>policy.require_heartbeat</c>, which defaults to
    /// <see langword="false"/>. A <c>404</c> is the only proof the row is gone.
    /// </para>
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such machine in this account.</exception>
    public async Task<Machine> GetMachineAsync(Guid machineId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Get, $"/machines/{machineId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Machine.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary><c>PATCH /machines/{id}</c> — updates a machine's mutable attributes.</summary>
    /// <param name="machineId">The machine to update.</param>
    /// <param name="request">The attributes to change; omitted ones are left untouched.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// Enveloped like <see cref="CreateMachineAsync"/> (<c>{"data":{"type":"machines","attributes":{…}}}</c>),
    /// unlike the flat component/process creates. A field cannot be set back to null through this
    /// route — see <see cref="UpdateMachineRequest"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>This is a write whose response can still report <see cref="HeartbeatStatus.Dead"/>.</b>
    /// The rule that a write-backed response never says <c>DEAD</c> holds because the write set
    /// <c>last_heartbeat_at</c> and the status is then derived from it — and this update touches no
    /// heartbeat column at all, so the status is judged against a timestamp that is as old as it
    /// ever was. Do not treat "it was a <c>PATCH</c>" as a reason the status must be live.
    /// </para>
    /// <para>
    /// <see cref="Machine.NextHeartbeatAt"/> on this response is nevertheless the UNTRUSTWORTHY
    /// kind: the <c>UPDATE … RETURNING</c> does not join <c>policies</c>, so it is computed against
    /// the 600s fallback. Do not size a heartbeat interval from it — use
    /// <see cref="GetLicensePolicyAsync"/> or a read-backed machine.
    /// </para>
    /// <para>
    /// ⚠ This route is permission-gated (<c>machine.update</c>) and <b>not</b> scoped to the
    /// caller's own license — a license key holds <c>machine.update</c> and <c>machine.delete</c>,
    /// and no machine route applies the per-license scope check. So a credential can modify or
    /// delete any machine in the account, not only its own. Do not build a UI on the assumption
    /// that it can only reach its own row.
    /// </para>
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such machine in this account.</exception>
    public async Task<Machine> UpdateMachineAsync(Guid machineId, UpdateMachineRequest request, CancellationToken cancellationToken = default)
    {
        var body = new JsonApiCreateRequest<MachineAttributes>
        {
            Data = new JsonApiCreateRequestData<MachineAttributes>
            {
                Type = "machines",
                Attributes = new MachineAttributes
                {
                    Name = request.Name,
                    Ip = request.Ip,
                    Hostname = request.Hostname,
                    Platform = request.Platform,
                    Cores = request.Cores,
                    Memory = request.Memory,
                    Disk = request.Disk,
                    Metadata = request.Metadata,
                },
            },
        };

        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Patch, $"/machines/{machineId}", jsonBody: body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Machine.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>The server's maximum (and this SDK's default) page size for the machine collection — <c>page[size]</c> is clamped to <c>1..100</c> server-side.</summary>
    private const int MaxMachinesPageSize = 100;

    /// <summary>
    /// <c>GET /machines</c> — lists the account's machines, OFFSET-paginated.
    /// </summary>
    /// <param name="pageNumber">1-based page number. Defaults to <c>1</c>.</param>
    /// <param name="pageSize">Rows per page, <c>1..100</c>. Defaults to 100 (the maximum) rather than letting the server apply its silent default of 25. Values above 100 are clamped to match the server's own clamp.</param>
    /// <param name="search">
    /// Free-text search, sent as <c>filter[q]</c>. A case-insensitive SUBSTRING match run across
    /// <c>name</c>, <c>hostname</c> AND <c>fingerprint</c> — not an exact-match filter on any one
    /// of them. See <see cref="FindMachineByFingerprintAsync"/>.
    /// </param>
    /// <param name="licenseId">
    /// Restrict the listing to machines on one license, sent as <c>filter[license]</c>. Omitting it
    /// lists every machine in the account, which a license credential is permitted to do — see the
    /// remarks.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// ⚠ <b>This listing is OFFSET-paginated, not keyset.</b> It returns
    /// <c>meta.page{number,size,total,totalPages}</c> — an exact count from the same filter that
    /// selected the rows — so it yields an <see cref="OffsetPage{T}"/> and there is no cursor to
    /// thread. Do not reach for <see cref="Page{T}"/>'s <c>NextCursor</c> pattern here, and do not
    /// synthesize one: the entitlement and component listings work the other way round and
    /// confusing the two drops rows in whichever direction the mistake was made. Walk pages while
    /// <see cref="OffsetPage{T}.HasMore"/>.
    /// </para>
    /// <para>
    /// The server refuses an offset past 100 000 rows with <c>400 PAGE_OUT_OF_RANGE</c>, which at
    /// the maximum page size is page 1001.
    /// </para>
    /// <para>
    /// ⚠ <b>The unfiltered listing is account-wide, not license-scoped.</b> No machine route
    /// applies the per-license scope check that validate and check-out apply, and the
    /// <c>LicenseToken</c> role holds <c>machine.read</c>, so a client authenticated with one
    /// license key sees every machine in the account. Pass <paramref name="licenseId"/> whenever
    /// the answer is supposed to be about one license — the server will not narrow it for you, and
    /// the machine resource carries no <c>license_id</c> to narrow it afterwards.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageNumber"/> or <paramref name="pageSize"/> is less than 1.</exception>
    public async Task<OffsetPage<Machine>> ListMachinesAsync(
        int? pageNumber = null,
        int? pageSize = null,
        string? search = null,
        Guid? licenseId = null,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber), pageNumber, "Page numbers are 1-based; pass null for the first page.");
        }

        if (pageSize is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize), pageSize, "Page size must be at least 1; pass null for the default of 100.");
        }

        var effectiveNumber = pageNumber ?? 1;
        var effectiveSize = Math.Min(pageSize ?? MaxMachinesPageSize, MaxMachinesPageSize);

        var parts = new List<string>
        {
            $"page%5Bnumber%5D={effectiveNumber}",
            $"page%5Bsize%5D={effectiveSize}",
        };
        if (!string.IsNullOrEmpty(search))
        {
            parts.Add($"filter%5Bq%5D={Uri.EscapeDataString(search)}");
        }

        if (licenseId is { } license)
        {
            parts.Add($"filter%5Blicense%5D={license}");
        }

        var doc = await _transport.SendJsonApiListAsync<MachineAttributes>(
            HttpMethod.Get, "/machines", query: string.Join('&', parts), cancellationToken: cancellationToken).ConfigureAwait(false);

        var items = doc.Data.Select(Machine.FromResource).ToList();
        var page = doc.Meta?.Deserialize<JsonApiListMeta>(TamgaJsonOptions.Default)?.Page;

        return new OffsetPage<Machine>
        {
            Items = items,
            // Fall back to what was asked for rather than to zero: a missing `meta.page` would
            // otherwise report TotalPages = 0, which makes HasMore false and silently ends a walk
            // on its first page.
            Number = page?.Number ?? effectiveNumber,
            Size = page?.Size ?? effectiveSize,
            Total = page?.Total ?? items.Count,
            TotalPages = page?.TotalPages ?? (items.Count == 0 ? 0 : effectiveNumber),
        };
    }

    /// <summary>
    /// Finds the machine on <paramref name="licenseId"/> holding an exact fingerprint, or
    /// <see langword="null"/> if that license has none.
    /// </summary>
    /// <param name="licenseId">The license to search within. Required — see the remarks.</param>
    /// <param name="fingerprint">The exact fingerprint to look for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The license scope is not a convenience filter; it is the safety property.</b> The
    /// account-wide answer to "who holds this fingerprint" is a different and more dangerous
    /// question, because the machine resource carries no <c>license_id</c> — so a caller handed a
    /// machine from another license has no way to notice. It would then heartbeat and check out a
    /// machine its own license does not own, while its own <c>machines_count</c> stayed at zero.
    /// Seat-sharing across licenses is precisely what the server's wider uniqueness scopes exist to
    /// prevent (<c>machines/service.rs:47-50</c>), so an SDK must not quietly reconstruct it.
    /// </para>
    /// <para>
    /// Scoping loses nothing, because all three uniqueness strategies' duplicate checks include the
    /// caller's own license rows: <c>UNIQUE_PER_LICENSE</c> matches on
    /// <c>license_id = the caller's</c>, <c>UNIQUE_PER_POLICY</c> joins licenses on the policy the
    /// caller's license shares, and <c>UNIQUE_PER_ACCOUNT</c> covers everything. A genuine
    /// re-activation of a machine this license already owns therefore produces
    /// <c>FINGERPRINT_TAKEN</c> under all three, and a license-scoped search finds it every time.
    /// The only thing an account-wide search would add is the cross-license case — the one the
    /// server is deliberately refusing.
    /// </para>
    /// <para>
    /// ⚠ <b>There is no exact-match fingerprint filter on the machine collection.</b> The only
    /// fingerprint-aware query parameter is <c>filter[q]</c>, a case-insensitive substring search
    /// that also matches <c>name</c> and <c>hostname</c>. So this narrows with
    /// <c>filter[license]</c> plus the fingerprint as a search term, then re-checks
    /// <see cref="Machine.Fingerprint"/> for exact equality client-side, walking pages until it
    /// finds a match or runs out. Anything that trusted the search result directly could return a
    /// machine whose <em>hostname</em> merely contained the fingerprint. The narrowing and the
    /// scoping are independent: both err toward a superset, and the client-side equality check is
    /// what makes the result exact.
    /// </para>
    /// <para>
    /// The comparison is ordinal and case-sensitive, matching how the server stores and uniqueness-
    /// checks the column.
    /// </para>
    /// </remarks>
    public async Task<Machine?> FindMachineByFingerprintAsync(Guid licenseId, string fingerprint, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return null;
        }

        var pageNumber = 1;
        while (true)
        {
            var page = await ListMachinesAsync(pageNumber, MaxMachinesPageSize, fingerprint, licenseId, cancellationToken).ConfigureAwait(false);
            var match = page.Items.FirstOrDefault(m => string.Equals(m.Fingerprint, fingerprint, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }

            if (!page.HasMore)
            {
                return null;
            }

            pageNumber++;
        }
    }

    /// <summary>
    /// Activation that treats <c>409 FINGERPRINT_TAKEN</c> as "already activated" instead of a
    /// dead end: creates the machine, or adopts the existing one holding that fingerprint, then
    /// validates the license either way.
    /// </summary>
    /// <param name="request">The machine to activate.</param>
    /// <param name="deleteOnOverLimit">Whether to roll back an over-limit activation. Applies ONLY to a machine this call created — see the remarks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// The server reports a re-activation of the same fingerprint as a conflict on purpose, and
    /// <see cref="CreateMachineAsync"/> and <see cref="ActivateMachineAsync"/> both surface that
    /// raw. For an application whose activation step can run twice — a reinstall, a crash before
    /// the machine id was persisted, a user clicking "activate" again — that leaves no way forward
    /// except catching the exception and guessing. This method is the way forward: it looks the
    /// fingerprint up (see <see cref="FindMachineByFingerprintAsync"/>) and carries on with the row
    /// that already exists, reporting it as <see cref="MachineActivation.AlreadyActivated"/>.
    /// </para>
    /// <para>
    /// ⚠ <b>An adopted machine is never rolled back.</b> When the fingerprint was already taken,
    /// <paramref name="deleteOnOverLimit"/> is ignored even on an over-limit verdict: deleting a row
    /// this call did not create would destroy a seat — possibly another install's, possibly another
    /// license's — on the strength of a limit that machine did not cause. The over-limit code still
    /// reaches you in <see cref="MachineActivation.Validation"/>; what to do about a seat you did
    /// not take is the caller's decision, not this method's.
    /// </para>
    /// <para>
    /// ⚠ <b>A cross-license conflict is re-thrown, not adopted.</b> The lookup is scoped to
    /// <see cref="CreateMachineRequest.LicenseId"/>, so under a policy whose
    /// <see cref="Policy.MachineUniquenessStrategy"/> is <c>UNIQUE_PER_POLICY</c> or
    /// <c>UNIQUE_PER_ACCOUNT</c> — where the conflict can come from a machine on a
    /// <em>different</em> license — no match is found and the original
    /// <see cref="FingerprintTakenException"/> surfaces. That is the correct outcome, not a gap:
    /// returning another license's machine would have this client heartbeat and check out a machine
    /// its own license does not own while its own <c>machines_count</c> stayed at zero, and since
    /// the resource carries no license id it could never detect that. Sharing one fingerprint's
    /// seat across licenses is exactly what the wider uniqueness scopes exist to prevent.
    /// </para>
    /// <para>
    /// Scoping costs nothing for the case this method is for. All three uniqueness strategies'
    /// duplicate checks include the caller's own license rows, so a genuine re-activation of a
    /// machine this license already owns conflicts under every strategy and is found by the scoped
    /// search every time. <see cref="MachineActivation.AlreadyActivated"/> therefore means "this
    /// license already has this machine" — the strong reading, not a hedge.
    /// </para>
    /// <para>
    /// A create-time limit rejection (<c>422</c>) still propagates as a
    /// <see cref="TamgaLimitExceededException"/>: no row was written, so there is nothing to adopt
    /// and nothing to roll back.
    /// </para>
    /// </remarks>
    /// <exception cref="FingerprintTakenException">The create conflicted and no machine on <see cref="CreateMachineRequest.LicenseId"/> holds that exact fingerprint — so the conflict came from another license, and adopting its machine would be a seat-sharing bug rather than a re-activation. The server's original error is preserved.</exception>
    /// <exception cref="TamgaLimitExceededException">The server refused the create itself.</exception>
    public async Task<MachineActivation> ActivateMachineIdempotentAsync(
        CreateMachineRequest request,
        bool deleteOnOverLimit = true,
        CancellationToken cancellationToken = default)
    {
        Machine machine;
        var alreadyActivated = false;

        try
        {
            machine = await CreateMachineAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (FingerprintTakenException)
        {
            var existing = await FindMachineByFingerprintAsync(request.LicenseId, request.Fingerprint, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                // The conflict is real, but this license does not hold that fingerprint — so it
                // came from another license under a wider uniqueness scope. Adopting that machine
                // would share a seat across licenses, which is the thing those scopes exist to
                // stop. Rethrowing keeps the server's own code and detail rather than inventing a
                // second story about it.
                throw;
            }

            machine = existing;
            alreadyActivated = true;
        }

        var validation = await ValidateByIdAsync(request.LicenseId, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (deleteOnOverLimit && !alreadyActivated && IsOverLimitCode(validation.Code))
        {
            await DeleteMachineAsync(machine.Id, cancellationToken).ConfigureAwait(false);
        }

        return new MachineActivation
        {
            Machine = machine,
            Validation = validation,
            AlreadyActivated = alreadyActivated,
        };
    }

    private static TamgaApiException MissingDataError() =>
        new(new TamgaApiError { Status = 200, Code = "MISSING_DATA", Detail = "Response had no resource." });
}

/// <summary>
/// Periodic heartbeat pinger for a single machine, built on <see cref="PeriodicTimer"/>. Pings on
/// an interval set to ~1/3 of the server's <em>default</em> 600s heartbeat window
/// (<see cref="DefaultInterval"/>) — see <see cref="ServerHeartbeatWindowSeconds"/> for why that
/// is a default and not the window in force. The loop runs until cancelled or disposed; no
/// heartbeat status it reads can stop it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>On a policy that sets <c>heartbeat_duration</c> below 600s, pass your own
/// <c>interval</c>.</b> The window is policy-driven —
/// <c>Policy::effective_heartbeat_duration_secs</c> returns <c>policy.heartbeat_duration</c> when
/// set and falls back to 600 only when it is null, and the culling job measures against
/// <c>COALESCE(p.heartbeat_duration, 600)</c>. This scheduler does not adapt — there is no policy
/// getter, so <see cref="DefaultInterval"/> is always computed from the 600s fallback. Against a
/// policy with, say, a 120s duration, that default pings roughly every 200s — well outside the
/// window — and the machine lapses to <c>DEAD</c> server-side between pings. You will not see that
/// happen: the ping responses keep saying <c>ALIVE</c> (see below), so the failure is silent.
/// Nothing in this type can detect it for you.
/// </para>
/// <para>
/// You are not left without a source for the right number, though. A checked-out <c>.machine</c>
/// file carries a read-backed <see cref="Machine.NextHeartbeatAt"/>, so
/// <c>NextHeartbeatAt - LastHeartbeatAt</c> recovers the effective window — see
/// <see cref="Machine.NextHeartbeatAt"/> for the recipe and its two caveats (it needs a machine
/// that has pinged at least once, and it is a snapshot from issue time). Learning the duration out
/// of band is the fallback for when no machine file is available, not the only option.
/// </para>
/// <para>
/// ⚠ <b>THE RULE: no <see cref="HeartbeatStatus"/> read off a response ends this loop.</b> Not
/// <c>DEAD</c>, not anything else. The loop ends for exactly three reasons — the
/// <see cref="CancellationToken"/> fires, <see cref="DisposeAsync"/> runs, or you stop it
/// yourself. Everything else is a tick that pings again.
/// </para>
/// <para>
/// The reason to state that as a rule about <em>all</em> statuses rather than a warning about
/// <c>DEAD</c> specifically: a response from <see cref="TamgaClient.PingHeartbeatAsync"/> can
/// never say <c>DEAD</c> in the first place. The ping writes
/// <c>last_heartbeat_at = NOW()</c> and the server then derives <c>heartbeat_status</c> from that
/// same freshly-written timestamp, so its age is ~0 and always inside the window — the response is
/// <c>ALIVE</c>, or <c>RESURRECTED</c> when a death event had already been recorded. A
/// <c>if (status == Dead)</c> branch written against this loop is unreachable code, and writing
/// re-activation into one means the re-activation never happens.
/// </para>
/// <para>
/// The durable form of that, which survives new endpoints in a way a route list does not: <b>a
/// response the server builds off a WRITE it just performed can never report <c>DEAD</c></b>,
/// because the status is derived from the very timestamp that write set. All three heartbeat-
/// bearing writes obey it — <see cref="TamgaClient.PingHeartbeatAsync"/> sets the timestamp to
/// <c>NOW()</c> (<c>ALIVE</c>, or <c>RESURRECTED</c> when a death event predates it), while
/// <see cref="TamgaClient.CreateMachineAsync"/> leaves it unset and
/// <see cref="TamgaClient.ResetHeartbeatAsync"/> nulls it (both <c>NOT_STARTED</c>). License
/// validation likewise never emits <c>HEARTBEAT_DEAD</c>.
/// </para>
/// <para>
/// <b>A response built off a READ can report <c>DEAD</c>, and one such route already reaches
/// you:</b> the machine embedded in a checked-out <c>.machine</c> file.
/// <see cref="Checkout.MachineFile.VerifyAndDecrypt(Tamga.Sdk.Models.LicenseScheme, System.ReadOnlySpan{byte}, string, string)"/> returns a <see cref="Machine"/> whose
/// <see cref="Machine.HeartbeatStatus"/> is bound straight from the file's payload, and that file
/// is resolved server-side through a read query. So <c>DEAD</c> is a real state a caller of this
/// SDK can genuinely receive — just never from this loop. A machine-read method added later would
/// fall in the same category, which is why <see cref="Dead"/> stays on this type.
/// </para>
/// <para>
/// Worth knowing for when you do see it there: <c>DEAD</c> does not mean the row was culled. The
/// cull job early-returns unless <c>policy.require_heartbeat</c> is set, and that column defaults
/// to <c>FALSE</c>, so under a default policy a machine reports <c>DEAD</c> indefinitely with its
/// row and its seat both still present, and a later ping simply revives it.
/// </para>
/// <para>
/// The only terminal signal from a ping is a <c>404 NOT_FOUND</c>, which means the row is gone.
/// It arrives on <see cref="Faulted"/> as a <see cref="TamgaNotFoundException"/>. Hang
/// re-activation off that, and off nothing else.
/// </para>
/// </remarks>
public sealed class HeartbeatScheduler : IAsyncDisposable
{
    /// <summary>
    /// The server's DEFAULT heartbeat window, in seconds — the value
    /// <c>Policy::effective_heartbeat_duration_secs</c> falls back to when
    /// <c>policy.heartbeat_duration</c> is null. It is NOT necessarily the window in force.
    /// </summary>
    /// <remarks>
    /// Kept at its original name for source compatibility, but read it as "default", not
    /// "hardcoded". A policy that sets <c>heartbeat_duration</c> overrides it everywhere it
    /// matters: <c>heartbeat_status</c>, <c>next_heartbeat_at</c>, and the culling job's
    /// <c>COALESCE(p.heartbeat_duration, 600)</c>.
    /// </remarks>
    public const int ServerHeartbeatWindowSeconds = 600;

    /// <summary>Default ping interval: ~1/3 of <see cref="ServerHeartbeatWindowSeconds"/>.</summary>
    /// <remarks>
    /// Safe only where the effective window really is the 600s fallback — i.e. a policy that
    /// leaves <c>heartbeat_duration</c> null, or sets it to 600 or more. Under a shorter duration
    /// this interval is too slow and the machine will lapse to <c>DEAD</c> between pings; pass an
    /// explicit <c>interval</c> to the constructor instead. Nothing picks that value for you — the
    /// scheduler does not adapt — but you can obtain it: a checked-out <c>.machine</c> file gives
    /// the real window as <c>NextHeartbeatAt - LastHeartbeatAt</c> (see
    /// <see cref="Machine.NextHeartbeatAt"/>). Fall back to learning the duration out of band only
    /// when no machine file is available.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(ServerHeartbeatWindowSeconds / 3.0);

    /// <summary>
    /// The ping interval for a given heartbeat window — the window divided by three, the same ratio
    /// <see cref="DefaultInterval"/> uses against the 600s fallback.
    /// </summary>
    /// <param name="window">The effective heartbeat window, e.g. <see cref="Policy.EffectiveHeartbeatWindow"/>.</param>
    /// <returns>An interval comfortably inside <paramref name="window"/>.</returns>
    /// <remarks>
    /// <para>
    /// Three ticks per window means two pings can be lost — to a 429 that outlived its retry
    /// budget, a network blip, a suspended process — before the machine falls outside the window
    /// server-side. One tick per window would leave no margin at all; the machine would lapse to
    /// <c>DEAD</c> on the first missed ping, and (on a policy that sets <c>require_heartbeat</c>)
    /// eventually be culled, without the client being able to see it happen.
    /// </para>
    /// <para>
    /// A non-positive <paramref name="window"/> yields <see cref="DefaultInterval"/> rather than a
    /// zero or negative interval, which <see cref="PeriodicTimer"/> would reject.
    /// </para>
    /// <para>
    /// Pair with <see cref="TamgaClient.GetHeartbeatIntervalAsync(Guid, CancellationToken)"/>,
    /// which fetches the governing policy and applies this in one call.
    /// </para>
    /// </remarks>
    public static TimeSpan IntervalForWindow(TimeSpan window) =>
        window > TimeSpan.Zero ? TimeSpan.FromSeconds(window.TotalSeconds / 3.0) : DefaultInterval;

    private readonly TamgaClient _client;
    private readonly Guid _machineId;
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _disposed;

    /// <summary>Raised after each successful ping with the updated machine resource. A throwing handler is caught and rerouted to <see cref="Faulted"/> rather than killing the ping loop.</summary>
    public event Action<Machine>? Pinged;

    /// <summary>Raised when a ping throws, or when a <see cref="Pinged"/>/<see cref="Dead"/> handler itself throws — the loop continues on the next tick rather than terminating.</summary>
    /// <remarks>
    /// This — not <see cref="Dead"/> — is where the row-is-gone signal shows up: a
    /// <see cref="TamgaNotFoundException"/> here means <c>404 NOT_FOUND</c> from the ping, i.e. the
    /// machine really was deleted. Re-activate on that.
    /// </remarks>
    public event Action<Exception>? Faulted;

    /// <summary>
    /// ⚠ <b>No call this SDK currently makes can raise this.</b> Do not wire re-activation — or any
    /// other recovery — to it. Raised when a ping observes <see cref="HeartbeatStatus.Dead"/>,
    /// which a ping response cannot report: the ping writes <c>last_heartbeat_at = NOW()</c> and
    /// the status is derived from that same timestamp, so the answer is always <c>ALIVE</c> or
    /// <c>RESURRECTED</c>. See the type-level remarks.
    /// </summary>
    /// <remarks>
    /// Deliberately kept, and deliberately NOT <c>[Obsolete]</c>. The event is not deprecated and
    /// nothing supersedes it — it is correct API that goes live the moment a machine-read method
    /// lands, since a read <em>can</em> return <c>DEAD</c>. Marking it obsolete would file it
    /// alongside this SDK's genuinely dead members (the ones annotated "always null" and
    /// "scheduled for removal"), inviting a future maintainer to delete a member we want to keep,
    /// and — because this repo builds with <c>TreatWarningsAsErrors</c> — would break the build of
    /// any consumer who subscribes defensively. A doc warning costs nobody a build and says the
    /// same thing.
    ///
    /// If it does fire, treat it as information only: the loop continues, and that is correct
    /// behaviour, not a gap. A throwing handler is caught and rerouted to <see cref="Faulted"/>
    /// rather than killing the ping loop.
    /// </remarks>
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
                // Defensive only: a ping response cannot currently be DEAD. The ping writes
                // last_heartbeat_at = NOW() and the server derives the status from that same
                // timestamp, so it answers ALIVE or RESURRECTED. This branch exists because DEAD
                // is a real wire value that a machine-read route can return, and because the loop
                // must survive ANY status it is handed. Note what it does not do: it does not
                // break, return, or skip a tick. No status ends this loop — only cancellation,
                // disposal, or the caller. A genuinely deleted machine surfaces as a 404 on the
                // ping, via Faulted.
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
