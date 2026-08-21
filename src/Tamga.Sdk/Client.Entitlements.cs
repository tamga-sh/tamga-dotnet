using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §J Entitlements
    //
    // GOTCHA: despite the URL nesting under /licenses, these are full Entitlement resources, not
    // lightweight junction/relationship records. No auth/permission check is applied beyond the
    // license existing.
    //
    // GOTCHA: this listing is NOT paginable. It is a union of directly-attached and
    // policy-inherited rows, so the server dropped its keyset cursor: `page[after]` is accepted
    // for wire compatibility and then ignored, and `limit` (max 100) is the only thing that bounds
    // the response. A license with more than 100 effective entitlements cannot be enumerated in
    // full through this endpoint at all.
    // ---------------------------------------------------------------

    /// <summary>The server's maximum (and this SDK's default) page size for this listing — <c>limit</c> is clamped to <c>1..100</c> server-side.</summary>
    private const int MaxEntitlementsPageSize = 100;

    private readonly Dictionary<Guid, List<Entitlement>> _entitlementsCache = new();
    private readonly object _entitlementsCacheLock = new();

    /// <summary><c>GET /licenses/{license_id}/entitlements</c>.</summary>
    /// <param name="licenseId">The license whose effective entitlements to list.</param>
    /// <param name="limit">Page size, clamped to <c>1..100</c> server-side. Defaults to 100 (the maximum) rather than letting the server apply its silent default of 25.</param>
    /// <param name="after">Accepted for source compatibility and sent as <c>page[after]</c>, but the server ignores it on this route — see the remarks.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// A page whose <see cref="Page{T}.NextCursor"/> is <b>always</b> <see langword="null"/>: there
    /// is no next page to ask for on this route. Treat <see cref="Page{T}.Items"/> as the complete
    /// set only while it is shorter than <paramref name="limit"/>; a full page means the listing
    /// was truncated and cannot be continued.
    /// </returns>
    /// <remarks>
    /// The <paramref name="after"/> parameter is retained deliberately rather than removed —
    /// dropping it would break callers threading a cursor — but it no longer does anything on the
    /// server, and this SDK no longer synthesizes a cursor to feed back into it. Looping on
    /// <see cref="Page{T}.NextCursor"/> here would re-fetch the same first page forever.
    /// </remarks>
    public async Task<Page<Entitlement>> ListEntitlementsAsync(
        Guid licenseId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        var query = BuildPaginationQuery(limit ?? MaxEntitlementsPageSize, after);
        var doc = await _transport.SendJsonApiListAsync<EntitlementAttributes>(
            HttpMethod.Get, $"/licenses/{licenseId}/entitlements", query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = doc.Data.Select(Entitlement.FromResource).ToList();

        // Unconditionally null: the server emits no `links` to read a cursor out of, and would
        // ignore one on this route even if we synthesized it from the last item's id.
        return new Page<Entitlement> { Items = items, NextCursor = null };
    }

    /// <summary><c>GET /licenses/{license_id}/entitlements/{entitlement_id}</c>.</summary>
    /// <remarks>
    /// Resolves DIRECT attachments only. The collection above is a union of direct and
    /// policy-inherited rows, but this route joins the direct table alone — so an entitlement the
    /// listing returned with <see cref="Entitlement.Inherited"/> <see langword="true"/> answers
    /// <c>404</c> here. List-then-get-each is not a valid pattern on this resource.
    /// </remarks>
    public async Task<Entitlement> GetEntitlementAsync(Guid licenseId, Guid entitlementId, CancellationToken cancellationToken = default)
    {
        var doc = await _transport.SendJsonApiAsync<EntitlementAttributes>(
            HttpMethod.Get, $"/licenses/{licenseId}/entitlements/{entitlementId}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return Entitlement.FromResource(doc.Data ?? throw MissingDataError());
    }

    /// <summary>
    /// Fetches (and caches, per-license, no baked-in TTL — see <see cref="InvalidateEntitlementsCache"/>)
    /// the license's entitlement list and matches on <see cref="Entitlement.Code"/> — the stable,
    /// developer-facing identifier — NEVER on <see cref="Entitlement.Name"/> (a display label that
    /// can change).
    /// </summary>
    /// <remarks>
    /// A <see langword="false"/> result is authoritative only below the server's hard ceiling of
    /// 100 effective entitlements per license: the underlying listing cannot be paginated past
    /// that (see <see cref="ListEntitlementsAsync"/>), so above it this method can report
    /// <see langword="false"/> for an entitlement the license genuinely holds. Do not use it as a
    /// negative authorization decision on a license that may exceed the ceiling.
    /// </remarks>
    public async Task<bool> HasEntitlementAsync(Guid licenseId, string code, CancellationToken cancellationToken = default)
    {
        var entitlements = await GetCachedEntitlementsAsync(licenseId, cancellationToken).ConfigureAwait(false);
        return entitlements.Any(e => e.Code == code);
    }

    /// <summary>Clears the cached entitlement list for a license, so the next <see cref="HasEntitlementAsync"/> call re-fetches. Caller controls freshness; there is no baked-in TTL.</summary>
    public void InvalidateEntitlementsCache(Guid licenseId)
    {
        lock (_entitlementsCacheLock)
        {
            _entitlementsCache.Remove(licenseId);
        }
    }

    private async Task<IReadOnlyList<Entitlement>> GetCachedEntitlementsAsync(Guid licenseId, CancellationToken cancellationToken)
    {
        lock (_entitlementsCacheLock)
        {
            if (_entitlementsCache.TryGetValue(licenseId, out var cached))
            {
                return cached;
            }
        }

        // Exactly one request, at the server's maximum page size. There is deliberately no loop:
        // `page[after]` is inert on this route, so a cursor loop would either exit after one
        // iteration (as this one used to, silently capping the cache at the server's default of
        // 25 rows and caching that truncation with no TTL) or spin on the same page forever.
        var page = await ListEntitlementsAsync(
            licenseId, limit: MaxEntitlementsPageSize, cancellationToken: cancellationToken).ConfigureAwait(false);
        var all = page.Items.ToList();

        lock (_entitlementsCacheLock)
        {
            _entitlementsCache[licenseId] = all;
        }

        return all;
    }
}
