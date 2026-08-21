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
    // PREREQUISITE: license-key auth only works if the license's POLICY has
    // authentication_strategy set to LICENSE or MIXED. That column defaults to 'TOKEN', which
    // rejects license keys, so a freshly created policy answers 401 LICENSE_NOT_ALLOWED here — a
    // configuration problem, not a bad key. Fix the policy; retrying will not help.
    Auth = new AuthTransport.License(licenseKey),
});

try
{
    // This listing is NOT paginable: the server unions directly-attached and policy-inherited
    // rows, so it ignores page[after] and bounds the response with `limit` (max 100) alone. There
    // is deliberately no cursor loop here — Page.NextCursor is always null on this route, and
    // looping on it would re-fetch page one forever. A license with more than 100 effective
    // entitlements cannot be enumerated in full through this endpoint at all.
    Console.WriteLine("Entitlements for this license:");
    var page = await client.ListEntitlementsAsync(licenseId, limit: 100);
    foreach (var entitlement in page.Items)
    {
        // Always display/match by Code (the stable, developer-facing identifier) — Name is
        // just a display label and can change without notice.
        var source = entitlement.Inherited == true ? "inherited from policy" : "attached directly";
        Console.WriteLine($"  - {entitlement.Name} (code: {entitlement.Code}, {source})");
    }

    if (page.Items.Count == 100)
    {
        Console.WriteLine("  ...100 rows returned — this is the server's ceiling, the list may be truncated.");
    }

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
