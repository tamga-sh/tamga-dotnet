// OfflineLicense — check out a .lic file, verify + decrypt it entirely client-side, and print
// the embedded license.
//
// Run: dotnet run --project samples/OfflineLicense -- <account-id> <base-url> <license-key> <license-id> <account-ed25519-public-key-base64> [--encrypt]
using Tamga.Sdk;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: OfflineLicense <account-id> <base-url> <license-key> <license-id> <public-key-base64> [--encrypt]");
    return 1;
}

var (accountId, baseUrl, licenseKey, licenseIdArg, publicKeyBase64) = (args[0], args[1], args[2], args[3], args.Length > 4 ? args[4] : "");
var encrypt = args.Contains("--encrypt");

if (!Guid.TryParse(licenseIdArg, out var licenseId))
{
    Console.Error.WriteLine($"Invalid license id: {licenseIdArg}");
    return 1;
}

using var client = new TamgaClient(new TamgaClientOptions
{
    AccountId = accountId,
    BaseUrl = baseUrl,
    Auth = new AuthTransport.License(licenseKey),
});

try
{
    // ttl/expiry returned alongside the certificate are metadata only — NOT embedded in the
    // signed payload and NOT re-checked server-side later. This sample doesn't persist the file,
    // but a real offline-capable client should record `ttl`/`expiry` itself and enforce it.
    var licenseFile = await client.CheckOutLicenseAsync(licenseId, encrypt: encrypt, ttl: 3600);

    var publicKey = Convert.FromBase64String(publicKeyBase64);

    // VerifyAndDecrypt fails closed (throws SignatureVerificationException) on any tampering or
    // a wrong key — see Checkout/LicenseFile.cs's remarks for the base64-string-vs-decoded-bytes
    // detail this verification depends on getting right.
    var license = licenseFile.VerifyAndDecrypt(publicKey, licenseKey);

    Console.WriteLine($"Verified offline license file for license {license.Id}.");
    Console.WriteLine($"  Key: {license.Key}");
    Console.WriteLine($"  Suspended: {license.Suspended}");
    Console.WriteLine($"  Uses: {license.Uses}");
    return 0;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
catch (SignatureVerificationException ex)
{
    Console.Error.WriteLine($"License file failed verification — treat as untrusted: {ex.Message}");
    return 1;
}
