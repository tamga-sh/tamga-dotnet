// MachineActivation — create a machine, validate the license, interpret TooManyMachines-style
// over-limit codes, and run a heartbeat scheduler loop.
//
// Run: dotnet run --project samples/MachineActivation -- <account-id> <base-url> <license-key> <license-id> <fingerprint>
using Tamga.Sdk;
using Tamga.Sdk.Models;

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: MachineActivation <account-id> <base-url> <license-key> <license-id> <fingerprint>");
    return 1;
}

var (accountId, baseUrl, licenseKey, licenseIdArg, fingerprint) = (args[0], args[1], args[2], args[3], args[4]);

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
    // ActivateMachineAsync handles the "no server-side limit check at creation time" gotcha:
    // it creates the machine, validates the license, and deletes the machine again if validation
    // comes back over-limit (TooManyMachines/TooManyCores/TooMuchMemory/TooMuchDisk/
    // TooManyProcesses) — deleteOnOverLimit defaults to true.
    var (machine, validation) = await client.ActivateMachineAsync(new CreateMachineRequest
    {
        Fingerprint = fingerprint,
        LicenseId = licenseId,
        Platform = Environment.OSVersion.Platform.ToString(),
        Hostname = Environment.MachineName,
    });

    if (!validation.Valid)
    {
        Console.WriteLine($"Activation rejected: {validation.Code} — {validation.Detail}");
        return 1;
    }

    Console.WriteLine($"Machine {machine.Id} activated (fingerprint {machine.Fingerprint}).");

    // HeartbeatScheduler pings on ~1/3 of the server's hardcoded 600s window by default. This
    // sample runs it for a short window and then stops — a real long-running app would keep it
    // alive for the process lifetime and react to the Dead event by re-activating.
    await using var scheduler = new HeartbeatScheduler(client, machine.Id);
    scheduler.Pinged += m => Console.WriteLine($"  heartbeat ok, status={m.HeartbeatStatus}");
    scheduler.Dead += _ => Console.WriteLine("  machine observed DEAD — re-activate rather than keep pinging");
    scheduler.Faulted += ex => Console.WriteLine($"  heartbeat ping failed: {ex.Message}");
    scheduler.Start();

    Console.WriteLine("Heartbeat scheduler running. Press any key to stop...");
    Console.ReadKey(intercept: true);

    return 0;
}
catch (FingerprintTakenException)
{
    Console.Error.WriteLine("This fingerprint is already registered for this license.");
    return 1;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
