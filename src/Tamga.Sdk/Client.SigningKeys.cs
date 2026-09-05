using System.Text.Json;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §N Signing keys (key rotation)
    //
    // An account keeps every Ed25519 key it has ever signed with, and an offline file names the
    // one that signed it in its `kid` claim. Without that set, a file signed before a rotation is
    // indistinguishable from a forgery — see SigningKeySet.
    //
    // ⚠ PERMISSION: this route requires `account.read`, which Role::LicenseToken does NOT hold.
    // An embedded client authenticating with a license key gets 403 here, unconditionally. That is
    // not fatal to rotation support — see the remarks on ListSigningKeysAsync for the pinned-key
    // path, which needs no network at all.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>GET /accounts/{account_id}/signing-keys</c> — every public signing key the account has
    /// held, current and retired, newest first.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// <para>
    /// <b>Retired keys are included by design.</b> A client holding a file signed before the last
    /// rotation needs the key its <c>kid</c> names, and its only other options are to fail
    /// verification or to accept any key — the second of which defeats signing entirely. Ordering
    /// is <c>created_at DESC, kid ASC</c> (<c>signing_keys.rs:57-63</c>).
    /// </para>
    /// <para>
    /// Only public halves come back: <c>PublishedSigningKey</c> has no field for a private key, so
    /// one cannot leak through this route even if the query were changed to select it.
    /// </para>
    /// <para>
    /// ⚠ <b>Not reachable with a license-key credential.</b> The route requires
    /// <c>account.read</c> (<c>accounts/policy.rs:16-18</c>), and <c>Role::LicenseToken</c> — what
    /// a <see cref="AuthTransport.License"/> or <see cref="AuthTransport.BasicLicense"/> credential
    /// resolves to — does not hold it (<c>shared/authz/mod.rs:241-268</c>). An embedded client
    /// authenticating with a license key gets <c>403</c>. Unlike
    /// <see cref="GetPolicyAsync"/>/<see cref="GetLicensePolicyAsync"/>, there is <b>no</b> second
    /// route returning the same resource under a permission it does hold.
    /// </para>
    /// <para>
    /// That is not fatal to key rotation, because <b>a key set does not have to arrive over the
    /// wire</b>: build one with <see cref="SigningKeySet.FromPublicKeys(string[])"/> from keys
    /// pinned into the application, or fetched by a build step or a server of your own using an
    /// admin token. An offline verifier that only works while it has a network is not offline.
    /// </para>
    /// <para>
    /// <b>An empty result is possible but no longer the norm.</b> Every account created after the
    /// server's key-set backfill publishes its active key from creation, and a startup sweep
    /// backfills the accounts that predate it. An account on a server that has not run that sweep
    /// still answers <c>{"data": []}</c>. Treat an empty list as "not yet backfilled", pin the
    /// account's published key with <see cref="SigningKeySet.FromPublicKeys(string[])"/>, and
    /// verification works either way.
    /// </para>
    /// <para>
    /// <c>algorithm</c> is <c>ed25519</c> on every row today: the table's <c>CHECK</c> also admits
    /// <c>rsa2048</c> and <c>ecdsa_p256</c>, but nothing writes them.
    /// </para>
    /// </remarks>
    /// <exception cref="TamgaForbiddenException"><c>403 FORBIDDEN</c> — the credential lacks <c>account.read</c>; a license key always does. Pin keys instead.</exception>
    /// <exception cref="OfflineFileFormatException">The response body was not a decodable JSON:API document.</exception>
    public async Task<IReadOnlyList<SigningKey>> ListSigningKeysAsync(CancellationToken cancellationToken = default)
    {
        var (body, response) = await _transport.SendRawAsync(
            HttpMethod.Get, "/signing-keys", jsonApiContentType: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        response.Dispose();

        // Decoded through the real JSON:API envelope — `id` is a SIBLING of `attributes`, and it
        // carries the kid. Two things make this its own decode rather than the shared one:
        //
        //   1. JsonApiListDocument<T> types `id` as a Guid, and a kid is 16 hex characters, which
        //      Guid cannot parse. Every response the server can produce would throw.
        //   2. The JsonException is surfaced, never swallowed. A silent catch here is exactly the
        //      defect that cost this repo its entire typed error surface for two years.
        SigningKeyListDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SigningKeyListDocument>(body, TamgaJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new OfflineFileFormatException($"Signing-key response JSON is malformed: {ex.Message}");
        }

        if (document is null)
        {
            throw new OfflineFileFormatException("Signing-key response body was empty.");
        }

        return document.Data.Select(SigningKey.FromResource).ToList();
    }

    /// <summary>
    /// <c>GET /accounts/{account_id}/signing-keys</c>, returned as a ready-to-use
    /// <see cref="SigningKeySet"/> for the rotation-aware verification entry points.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// One call, and the result is cacheable for the life of the process — a key set only changes
    /// when the account rotates. Carries the same <c>account.read</c> permission requirement as
    /// <see cref="ListSigningKeysAsync"/>; see its remarks, and prefer
    /// <see cref="SigningKeySet.FromPublicKeys(string[])"/> in an embedded client.
    /// </remarks>
    public async Task<SigningKeySet> GetSigningKeySetAsync(CancellationToken cancellationToken = default)
        => new(await ListSigningKeysAsync(cancellationToken).ConfigureAwait(false));
}
