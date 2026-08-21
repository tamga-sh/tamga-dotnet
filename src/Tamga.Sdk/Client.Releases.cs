using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §L Auto-update
    //
    // The route is `OptionalAuth`: on a product whose distribution strategy is Open it answers an
    // unauthenticated caller, because otherwise every auto-updater in the field would break the
    // moment its credential lapsed. This SDK still sends whatever transport is configured — a
    // Licensed or Closed product needs it, and sending it costs an Open product nothing.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>GET /releases/actions/upgrade</c> — asks whether a newer release is available to this
    /// caller.
    /// </summary>
    /// <param name="request">The product, platform, filetype and current version to check, plus the optional channel and constraint.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>
    /// An <see cref="UpgradeCheck"/> whose <see cref="UpgradeCheck.Release"/> is the offered
    /// release, or <see langword="null"/> when none is being offered.
    /// </returns>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Do not render a <see langword="null"/> result as "you are up to date".</b> The server
    /// answers <c>204 No Content</c> both when nothing newer exists and when something newer exists
    /// that this license may not have, and it does so deliberately — see
    /// <see cref="UpgradeCheck"/> for the reasoning and the exact two cases. The only claim the
    /// response supports is <em>"no update is available to you"</em>.
    /// </para>
    /// <para>
    /// Access follows the product's distribution strategy, not this SDK's credential: <c>Open</c>
    /// products answer anonymously, <c>Licensed</c> and <c>Closed</c> ones answer <c>401</c>
    /// without a bearer and <c>403</c> without the right permission or role. A <b>suspended</b>
    /// license gets <c>403</c> here rather than the silent <c>204</c> an expired one gets.
    /// </para>
    /// <para>
    /// Note what this endpoint does not give you: the release, not its bytes. The artifacts that
    /// carry the bytes are a separate resource, reachable since <c>tamga-api</c> <c>e6d317b</c>
    /// granted <c>artifact.read</c> and <c>artifact.download</c> to <c>Role::LicenseToken</c> —
    /// see <see cref="ListReleaseArtifactsAsync"/> and <see cref="DownloadArtifactAsync"/>. Until
    /// that commit the download route was gated behind a permission no role held and this SDK
    /// documented it as unreachable; that is no longer true.
    /// </para>
    /// </remarks>
    /// <exception cref="TamgaApiException">
    /// <c>422 INVALID_VERSION</c> / <c>INVALID_CONSTRAINT</c> when <see cref="UpgradeCheckRequest.Version"/>
    /// is not valid semver or <see cref="UpgradeCheckRequest.Constraint"/> is not a valid semver
    /// requirement. Both arrive as the base type; match on <see cref="TamgaApiError.Code"/> via
    /// <see cref="TamgaApiException.Error"/>.
    /// </exception>
    /// <exception cref="TamgaNotFoundException"><c>404 NOT_FOUND</c> — no such product in this account.</exception>
    /// <exception cref="TamgaForbiddenException"><c>403 FORBIDDEN</c> — the credential may not read this product's releases, or its license is suspended.</exception>
    public async Task<UpgradeCheck> CheckForUpgradeAsync(UpgradeCheckRequest request, CancellationToken cancellationToken = default)
    {
        var parts = new List<string>
        {
            $"product={Uri.EscapeDataString(request.ProductId.ToString())}",
            $"platform={Uri.EscapeDataString(request.Platform)}",
            $"filetype={Uri.EscapeDataString(request.Filetype)}",
            $"version={Uri.EscapeDataString(request.Version)}",
        };
        if (!string.IsNullOrEmpty(request.Channel))
        {
            parts.Add($"channel={Uri.EscapeDataString(request.Channel)}");
        }

        if (!string.IsNullOrEmpty(request.Constraint))
        {
            parts.Add($"constraint={Uri.EscapeDataString(request.Constraint)}");
        }

        var doc = await _transport.SendJsonApiAllowNoContentAsync<ReleaseAttributes>(
            HttpMethod.Get,
            "/releases/actions/upgrade",
            query: string.Join('&', parts),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new UpgradeCheck
        {
            Release = doc?.Data is { } data ? Release.FromResource(data) : null,
        };
    }
}
