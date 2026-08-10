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
    // ---------------------------------------------------------------

    private readonly Dictionary<Guid, List<Entitlement>> _entitlementsCache = new();
    private readonly object _entitlementsCacheLock = new();

    /// <summary><c>GET /licenses/{license_id}/entitlements</c> — keyset-paginated.</summary>
    public async Task<Page<Entitlement>> ListEntitlementsAsync(
        Guid licenseId, int? limit = null, string? after = null, CancellationToken cancellationToken = default)
    {
        var query = BuildPaginationQuery(limit, after);
        var doc = await _transport.SendJsonApiListAsync<EntitlementAttributes>(
            HttpMethod.Get, $"/licenses/{licenseId}/entitlements", query: query, cancellationToken: cancellationToken).ConfigureAwait(false);
        var items = doc.Data.Select(Entitlement.FromResource).ToList();
        return new Page<Entitlement> { Items = items, NextCursor = ExtractAfterCursor(doc.Links?.Next) };
    }

    /// <summary><c>GET /licenses/{license_id}/entitlements/{entitlement_id}</c>.</summary>
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

        var all = new List<Entitlement>();
        string? cursor = null;
        do
        {
            var page = await ListEntitlementsAsync(licenseId, after: cursor, cancellationToken: cancellationToken).ConfigureAwait(false);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        lock (_entitlementsCacheLock)
        {
            _entitlementsCache[licenseId] = all;
        }

        return all;
    }
}
