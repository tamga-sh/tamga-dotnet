using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §K Policy reads
    //
    // ⚠ SECURITY NOTE, and it is not one this SDK can fix. Neither GET /policies/{id} nor
    // GET /licenses/{id}/policy applies the per-license scope check that the validate and
    // checkout routes apply. Both are gated only on a `license.read` permission plus the
    // account id carried on the verified credential — so a caller authenticated with ONE
    // license key can read any policy in the same account, and (via GetLicenseAsync) any
    // license in it, including that license's plaintext `key`.
    //
    // Do not present this surface as safe to expose to end users. An application that hands
    // a license key to an untrusted client and relies on these routes being self-scoped is
    // relying on something the server does not do. Reported upstream; the fix belongs there.
    // ---------------------------------------------------------------

    /// <summary><c>GET /policies/{policy_id}</c> — reads a policy by ID.</summary>
    /// <param name="policyId">The policy to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Use <see cref="GetLicensePolicyAsync"/> instead when you hold a license id rather than a
    /// policy id: the license resource carries no <c>policy_id</c> attribute (and no
    /// <c>relationships</c> object), so there is no way to get from a <see cref="License"/> to its
    /// policy id client-side.
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such policy in this account.</exception>
    public async Task<Policy> GetPolicyAsync(Guid policyId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<Policy>(
            HttpMethod.Get, $"/policies/{policyId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Policy.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>
    /// <c>GET /licenses/{license_id}/policy</c> — reads the policy governing a license, without
    /// needing to know its policy id.
    /// </summary>
    /// <param name="licenseId">The license whose governing policy to read.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// Returns the same <c>policies</c> resource as <see cref="GetPolicyAsync"/>. This is the route
    /// that closes the gap the heartbeat scheduler documented for several releases: with the policy
    /// in hand, <see cref="Policy.EffectiveHeartbeatDurationSeconds"/> gives the real window and
    /// <see cref="GetHeartbeatIntervalAsync"/> turns it into a ping interval.
    /// </remarks>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such license, or the license's policy row is missing.</exception>
    public async Task<Policy> GetLicensePolicyAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<Policy>(
            HttpMethod.Get, $"/licenses/{licenseId}/policy", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Policy.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>
    /// Reads the license's governing policy and returns the heartbeat ping interval that matches
    /// the window it actually sets — the value to hand <see cref="HeartbeatScheduler"/>'s
    /// constructor.
    /// </summary>
    /// <param name="licenseId">The license whose policy sets the window.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns><see cref="Policy.EffectiveHeartbeatWindow"/> divided by three.</returns>
    /// <remarks>
    /// <para>
    /// One round trip, at activation time. The scheduler itself still does not adapt: it takes a
    /// fixed interval and keeps it, so a policy whose <c>heartbeat_duration</c> changes later needs
    /// this called again and a new scheduler started.
    /// </para>
    /// <para>
    /// ⚠ Do NOT try to derive the window from a ping response's <c>next_heartbeat_at</c> instead.
    /// That field is computed against whichever window the query that loaded the machine happened
    /// to have joined, and the write routes a scheduler calls — create, ping-heartbeat,
    /// reset-heartbeat — do not join the policy, so they report the 600s fallback even on a policy
    /// that sets 120. Two responses for the same machine, seconds apart, can disagree, and the one
    /// a scheduler naturally has is the wrong one. This route and
    /// <see cref="Machine.NextHeartbeatAt"/> on a read-backed machine are the two trustworthy
    /// sources.
    /// </para>
    /// </remarks>
    public async Task<TimeSpan> GetHeartbeatIntervalAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        var policy = await GetLicensePolicyAsync(licenseId, cancellationToken).ConfigureAwait(false);
        return HeartbeatScheduler.IntervalForWindow(policy.EffectiveHeartbeatWindow);
    }
}
