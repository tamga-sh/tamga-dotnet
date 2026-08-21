using System.Text.Json;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §M Health
    // ---------------------------------------------------------------

    /// <summary>The one route in this SDK that is not account-scoped. Registered at the server root, not under <c>/v1/accounts/{account_id}</c>.</summary>
    private const string HealthPath = "/v1/health";

    /// <summary>
    /// <c>GET /v1/health</c> — the server's liveness probe.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// <b>Why this is worth having, beyond "is it up".</b> This route is exempt from two gates that
    /// every other request passes through: it is on the server's public-route list, so it needs no
    /// credential, and it is skipped by the <c>Host</c>-header check, so it answers even when the
    /// request's host is not in the server's allow-list. That makes it a differential diagnostic.
    /// If every ordinary call is failing with <c>403</c> and "The Host header does not match any
    /// configured host" while this call succeeds, the problem is the deployment's allowed-hosts
    /// configuration — not the caller's token, not the account id, and not anything a caller can
    /// fix by re-issuing credentials.
    /// </para>
    /// <para>
    /// Two shape notes, both of which have bitten before. The response is <b>not</b> a JSON:API
    /// document — it is a plain <c>{ status, version, uptime_secs }</c> object with no <c>data</c>
    /// envelope — so it is decoded directly rather than through this SDK's envelope reader. And the
    /// URL skips the <c>/v1/accounts/{account_id}</c> prefix that every other call in this SDK
    /// builds unconditionally; that prefix, not the server, is why earlier versions could not reach
    /// this route at all.
    /// </para>
    /// <para>
    /// It is a liveness probe, not a readiness one: the handler never touches the database, so a
    /// healthy answer does not promise that licensing calls will succeed.
    /// </para>
    /// <para>
    /// The configured credential is still sent, as everywhere else in this SDK — the route ignores
    /// it, and sending it keeps callers forward-compatible if that ever changes.
    /// </para>
    /// </remarks>
    public async Task<TamgaHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var body = await _transport.SendUnscopedRawAsync(HttpMethod.Get, HealthPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TamgaHealth>(body, TamgaJsonOptions.Default)
            ?? throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "EMPTY_RESPONSE", Detail = "Health check returned an empty body." });
    }
}
