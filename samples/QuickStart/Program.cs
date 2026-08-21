// QuickStart — validate a license by key and interpret the result.
//
// Run: dotnet run --project samples/QuickStart -- <account-id> <base-url> <license-key>
using Tamga.Sdk;
using Tamga.Sdk.Models;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: QuickStart <account-id> <base-url> <license-key>");
    return 1;
}

var (accountId, baseUrl, licenseKey) = (args[0], args[1], args[2]);

using var client = new TamgaClient(new TamgaClientOptions
{
    AccountId = accountId,
    BaseUrl = baseUrl,
    // License auth is the primary transport for embedded/client SDKs like this one — see the
    // README's auth-transport matrix for the other 7 options.
    //
    // PREREQUISITE: this only works if the license's POLICY has authentication_strategy set to
    // LICENSE or MIXED. That column defaults to 'TOKEN', which rejects license keys, so a
    // freshly created policy answers 401 LICENSE_NOT_ALLOWED here — a configuration problem, not
    // a bad key. Fix the policy; retrying will not help.
    Auth = new AuthTransport.License(licenseKey),
});

try
{
    var result = await client.ValidateByKeyAsync(licenseKey);

    // 16 of ValidationCode's 24 values are reachable today — see the README's "Known gaps"
    // section before building UX around the rest.
    switch (result.Code)
    {
        case ValidationCode.Valid:
            Console.WriteLine($"License is valid. Uses: {result.License.Uses}, expiry: {result.License.Expiry?.ToString() ?? "never"}.");
            break;
        case ValidationCode.Suspended:
            Console.WriteLine("License is suspended.");
            break;
        case ValidationCode.Expired:
            Console.WriteLine($"License expired at {result.License.Expiry}.");
            break;
        default:
            Console.WriteLine($"License is not valid: {result.Code} — {result.Detail}");
            break;
    }

    return result.Valid ? 0 : 1;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
