using System.Text.Json;
using System.Text.Json.Nodes;
using Tamga.Sdk.Models;

namespace Tamga.Sdk;

public sealed partial class TamgaClient
{
    // ---------------------------------------------------------------
    // §H Machine Offline Proof
    //
    // Lighter-weight alternative to full machine checkout (§F) for periodic "prove this machine is
    // still valid" pings in air-gapped environments.
    // ---------------------------------------------------------------

    /// <summary>
    /// <c>POST /machines/{id}/actions/generate-offline-proof</c> — generates an offline proof,
    /// parsing the returned <c>meta.proof</c> (<c>"v1x0.&lt;base64 signature&gt;"</c>) into a
    /// <see cref="MachineProof"/>. <paramref name="dataset"/> defaults to an empty object.
    /// </summary>
    /// <remarks>
    /// PERMISSIONS: this endpoint is role-gated, and the license-key role is not on the list —
    /// despite holding the <c>machine.proofs.generate</c> permission, which the role gate is
    /// checked independently of. A client configured with <see cref="AuthTransport.License"/> or
    /// <see cref="AuthTransport.BasicLicense"/> gets <c>403</c> here every time; generating a proof
    /// needs an admin, developer, product, environment, sales-agent or support-agent credential.
    /// Verification (<see cref="MachineProof.Verify"/>) is purely local and has no such
    /// restriction, so the usual split is: generate server-side with a privileged credential, ship
    /// the proof to the embedded client, verify there.
    /// </remarks>
    /// <exception cref="DatasetInvalidException"><c>422 DATASET_INVALID</c> — the server rejected <paramref name="dataset"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="dataset"/> is a non-null <see cref="JsonNode"/> that isn't a JSON object (e.g. an array or a scalar value) — the wire contract is <c>{ "meta": { "dataset": {...} } }</c>, an object.</exception>
    public async Task<MachineProof> GenerateOfflineProofAsync(
        Guid machineId,
        JsonNode? dataset = null,
        CancellationToken cancellationToken = default)
    {
        JsonObject datasetObject;
        switch (dataset)
        {
            case null:
                datasetObject = new JsonObject();
                break;
            case JsonObject obj:
                // Defensive copy: obj is caller-owned and this method serializes it later,
                // after an await (inside SendJsonApiAsync) -- without cloning, a caller that
                // mutates the same JsonObject instance from another thread between this call
                // and the point the request body is actually serialized would send bytes the
                // caller never intended, with no error. Found via audit.
                datasetObject = obj.DeepClone().AsObject();
                break;
            default:
                // CRITICAL (found in code review): silently substituting an empty object here
                // for a non-object dataset would send `{}` instead of what the caller asked for,
                // with no error — fail fast instead.
                throw new ArgumentException($"dataset must be a JSON object, got {dataset.GetType().Name}.", nameof(dataset));
        }

        var body = new GenerateOfflineProofRequest
        {
            Meta = new GenerateOfflineProofRequestMeta { Dataset = datasetObject },
        };

        var doc = await _transport.SendJsonApiAsync<MachineAttributes>(
            HttpMethod.Post,
            $"/machines/{machineId}/actions/generate-offline-proof",
            jsonBody: body,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (doc.Meta is not { ValueKind: JsonValueKind.Object } meta ||
            !meta.TryGetProperty("proof", out var proofElement) ||
            proofElement.GetString() is not { } proofString)
        {
            throw new TamgaApiException(new TamgaApiError { Status = 200, Code = "MISSING_PROOF", Detail = "Offline proof response had no meta.proof." });
        }

        return MachineProof.Parse(proofString);
    }
}
