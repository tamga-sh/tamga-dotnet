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
    /// <exception cref="DatasetInvalidException"><c>422 DATASET_INVALID</c> — the server rejected <paramref name="dataset"/>.</exception>
    public async Task<MachineProof> GenerateOfflineProofAsync(
        Guid machineId,
        JsonNode? dataset = null,
        CancellationToken cancellationToken = default)
    {
        var body = new GenerateOfflineProofRequest
        {
            Meta = new GenerateOfflineProofRequestMeta { Dataset = dataset as JsonObject ?? new JsonObject() },
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
