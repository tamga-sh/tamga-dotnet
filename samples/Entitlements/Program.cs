// Entitlements — list a license's entitlements and check for a specific one by code.
//
// Run: dotnet run --project samples/Entitlements -- <account-id> <base-url> <license-key> <license-id> [feature-code]
using Tamga.Sdk;

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: Entitlements <account-id> <base-url> <license-key> <license-id> [feature-code]");
    return 1;
}

var (accountId, baseUrl, licenseKey, licenseIdArg) = (args[0], args[1], args[2], args[3]);
var featureCode = args.Length > 4 ? args[4] : null;

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
    Console.WriteLine("Entitlements for this license:");
    string? cursor = null;
    do
    {
        var page = await client.ListEntitlementsAsync(licenseId, after: cursor);
        foreach (var entitlement in page.Items)
        {
            // Always display/match by Code (the stable, developer-facing identifier) — Name is
            // just a display label and can change without notice.
            Console.WriteLine($"  - {entitlement.Name} (code: {entitlement.Code})");
        }

        cursor = page.NextCursor;
    }
    while (cursor is not null);

    if (featureCode is not null)
    {
        var has = await client.HasEntitlementAsync(licenseId, featureCode);
        Console.WriteLine(has
            ? $"License HAS entitlement '{featureCode}'."
            : $"License does NOT have entitlement '{featureCode}'.");
        return has ? 0 : 1;
    }

    return 0;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
