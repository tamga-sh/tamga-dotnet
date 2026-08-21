// OfflineProof — generate an air-gapped offline proof with a custom dataset, then verify it
// entirely client-side (no network round-trip needed for the verify step).
//
// Run: dotnet run --project samples/OfflineProof -- <account-id> <base-url> <license-key> <machine-id> <fingerprint> <account-rsa-public-key-der-base64>
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Tamga.Sdk;

if (args.Length < 6)
{
    Console.Error.WriteLine("Usage: OfflineProof <account-id> <base-url> <license-key> <machine-id> <fingerprint> <rsa-public-key-spki-der-base64>");
    return 1;
}

var (accountId, baseUrl, licenseKey, machineIdArg, fingerprint, publicKeyBase64) = (args[0], args[1], args[2], args[3], args[4], args[5]);

if (!Guid.TryParse(machineIdArg, out var machineId))
{
    Console.Error.WriteLine($"Invalid machine id: {machineIdArg}");
    return 1;
}

if (!Guid.TryParse(accountId, out var accountGuid))
{
    Console.Error.WriteLine("account-id must be a UUID for offline proof verification (the account's own GUID, not a code).");
    return 1;
}

using var client = new TamgaClient(new TamgaClientOptions
{
    AccountId = accountId,
    BaseUrl = baseUrl,
    // PREREQUISITE: license-key auth only works if the license's POLICY has
    // authentication_strategy set to LICENSE or MIXED. That column defaults to 'TOKEN', which
    // rejects license keys, so a freshly created policy answers 401 LICENSE_NOT_ALLOWED here — a
    // configuration problem, not a bad key. Fix the policy; retrying will not help.
    Auth = new AuthTransport.License(licenseKey),
});

// Any JSON object — the server signs it verbatim as part of the canonical payload. Defaults to
// {} if omitted entirely.
var dataset = new JsonObject { ["build"] = "1.4.2", ["seat_count"] = 3 };

try
{
    var proof = await client.GenerateOfflineProofAsync(machineId, dataset);

    using var rsa = RSA.Create();
    rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

    // Proof verification is always RSA-2048 PKCS#1 v1.5/SHA-256, regardless of the license's own
    // signing scheme — it reconstructs the exact same (account, machine, dataset) tuple and
    // re-derives the canonical (alphabetically key-sorted) JSON payload the server signed.
    var verified = proof.Verify(rsa, accountGuid, machineId, fingerprint, dataset);

    Console.WriteLine(verified
        ? $"Offline proof for machine {machineId} verified successfully."
        : "Offline proof FAILED verification — do not trust this machine's liveness claim.");

    return verified ? 0 : 1;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
