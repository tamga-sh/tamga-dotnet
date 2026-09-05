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
    // PREREQUISITE: license-key auth only works if the license's POLICY has
    // authentication_strategy set to LICENSE or MIXED. That column defaults to 'TOKEN', which
    // rejects license keys, so a freshly created policy answers 401 LICENSE_NOT_ALLOWED here — a
    // configuration problem, not a bad key. Fix the policy; retrying will not help.
    Auth = new AuthTransport.License(licenseKey),
});

try
{
    // An over-limit activation can fail in two different places, and both are live:
    //
    //  1. The CREATE is refused outright with a 422 (e.g. MACHINE_LIMIT_EXCEEDED). Nothing was
    //     created, so there is nothing to roll back.
    //  2. The create SUCCEEDS and validation reports the overage. The server's create-time check
    //     honours the policy's overage strategy, so under ALLOW_ACCESS / ALLOW_1_25X_OVERAGE the
    //     machine row is written and only validate objects. deleteOnOverLimit (true by default)
    //     then deletes the row and ActivateMachineAsync THROWS MachineOverLimitException — it no
    //     longer returns a machine that has just been deleted.
    //
    // Both are TamgaLimitExceededException, caught together below. A tuple that comes back means
    // the machine exists; a non-over-limit invalid code (EXPIRED, SUSPENDED, …) is returned, not thrown.
    //
    // Note Memory/Disk on CreateMachineRequest are MEGABYTES, not bytes — passing bytes inflates
    // the license's running total by 1,048,576x and locks out the next activation.
    var (machine, validation) = await client.ActivateMachineAsync(new CreateMachineRequest
    {
        Fingerprint = fingerprint,
        LicenseId = licenseId,
        Platform = Environment.OSVersion.Platform.ToString(),
        Hostname = Environment.MachineName,
    });

    if (!validation.Valid)
    {
        // Not an over-limit code (those throw above) — the machine is real, the license is not usable.
        Console.WriteLine($"Activation rejected: {validation.Code} — {validation.Detail}");
        return 1;
    }

    Console.WriteLine($"Machine {machine.Id} activated (fingerprint {machine.Fingerprint}).");

    // HeartbeatScheduler pings on ~1/3 of the server's DEFAULT 600s window. That 600s is only the
    // fallback: the real window is policy.heartbeat_duration whenever the policy sets it, and this
    // scheduler does not adapt — there is no policy getter, so on a policy with a shorter duration
    // the default is too slow and the machine lapses to DEAD between pings. Pass the interval
    // explicitly when you know the window:
    //     new HeartbeatScheduler(client, machine.Id, TimeSpan.FromSeconds(yourWindowSeconds / 3.0))
    //
    // And you can find that number rather than being told it: a checked-out .machine file carries
    // a read-backed NextHeartbeatAt, so on a machine that has pinged at least once,
    // NextHeartbeatAt - LastHeartbeatAt is the effective window as of the file's issue time.
    //
    // This sample runs it for a short window and then stops — a real long-running app would keep
    // it alive for the whole process lifetime.
    //
    // NOTE what this sample deliberately does NOT do: subscribe to scheduler.Dead. That event
    // cannot fire on the ping path. ping-heartbeat writes last_heartbeat_at = NOW() and the server
    // derives the status from that same timestamp, so a ping answers ALIVE or RESURRECTED, never
    // DEAD. A Dead handler here would be dead code, and putting re-activation in one would mean
    // the re-activation never runs. No heartbeat status stops this loop; the only terminal signal
    // is a 404, handled below.
    await using var scheduler = new HeartbeatScheduler(client, machine.Id);
    scheduler.Pinged += m => Console.WriteLine($"  heartbeat ok, status={m.HeartbeatStatus}");
    scheduler.Faulted += ex =>
    {
        // 404 is the only authoritative "the machine row is gone" signal. THIS is where a real app
        // re-activates.
        if (ex is TamgaNotFoundException)
        {
            Console.WriteLine("  machine no longer exists server-side — re-activate");
            return;
        }

        Console.WriteLine($"  heartbeat ping failed: {ex.Message}");
    };
    scheduler.Start();

    Console.WriteLine("Heartbeat scheduler running. Press any key to stop...");
    Console.ReadKey(intercept: true);

    return 0;
}
catch (FingerprintTakenException)
{
    // "Already activated, carry on" — NOT a reason to tell the user to buy more seats. The server
    // checks uniqueness before quota precisely so a re-activation is reported this way.
    Console.Error.WriteLine("This fingerprint is already registered within the policy's uniqueness scope.");
    return 1;
}
catch (MachineOverLimitException ex)
{
    // Path 2: created, validated over-limit, deleted again. The machine in ex.DeletedMachineId is
    // gone — nothing here may heartbeat it.
    Console.Error.WriteLine($"Activation rolled back ({ex.EquivalentValidationCode}): {ex.Validation.Detail} — machine {ex.DeletedMachineId} was deleted again.");
    return 1;
}
catch (TamgaLimitExceededException ex)
{
    // Path 1: the create itself was refused. EquivalentValidationCode normalizes it onto the same
    // ValidationCode a later validate would have reported, so both over-limit paths converge.
    Console.Error.WriteLine($"Activation refused at creation ({ex.Error.Code} / {ex.EquivalentValidationCode}): {ex.Error.Detail}");
    return 1;
}
catch (LicenseNotAllowedException)
{
    Console.Error.WriteLine("This license's policy does not permit license-key auth (authentication_strategy must be LICENSE or MIXED).");
    return 1;
}
catch (TamgaApiException ex)
{
    Console.Error.WriteLine($"API error ({ex.Error.Code}): {ex.Error.Detail}");
    return 1;
}
