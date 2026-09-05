using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tamga.Sdk.Checkout;
using Tamga.Sdk.Models;
using Xunit;

namespace Tamga.Sdk.Tests.Checkout;

/// <summary>
/// One manifest entry from <c>Fixtures/MachineFiles/manifest.json</c>.
/// </summary>
public sealed record MachineFileFixture
{
    /// <summary>The fixture's filename inside <c>Fixtures/MachineFiles/</c>.</summary>
    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    /// <summary>The <c>alg</c> string the server put inside this file's certificate.</summary>
    [JsonPropertyName("alg")]
    public string Alg { get; init; } = "";

    /// <summary>Whether <c>enc</c> is AES-256-GCM ciphertext rather than plain base64 JSON.</summary>
    [JsonPropertyName("encrypted")]
    public bool Encrypted { get; init; }

    /// <summary>Whether <c>enc</c> has the <c>&lt;nonce_b64&gt;.&lt;ciphertext_b64&gt;</c> shape.</summary>
    [JsonPropertyName("enc_is_dot_separated")]
    public bool EncIsDotSeparated { get; init; }

    /// <summary>The account public key, in whatever encoding the server actually hands out for this scheme.</summary>
    [JsonPropertyName("public_key_b64")]
    public string PublicKeyB64 { get; init; } = "";

    /// <summary>The <c>kid</c> claim expected inside the signed payload.</summary>
    [JsonPropertyName("kid")]
    public string Kid { get; init; } = "";

    /// <summary>HKDF input keying material for an encrypted file; <see langword="null"/> for a plain one.</summary>
    [JsonPropertyName("license_key")]
    public string? LicenseKey { get; init; }

    /// <summary>HKDF <c>info</c> — the fingerprint of the machine this file was issued for.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    /// <summary>Whether the signed <c>exp</c> claim had already passed when the file was issued.</summary>
    [JsonPropertyName("expired")]
    public bool Expired { get; init; }

    /// <summary>The license's signing scheme, as a <see cref="LicenseScheme"/> member name or its wire string.</summary>
    [JsonPropertyName("scheme")]
    public string Scheme { get; init; } = "";
}

/// <summary>
/// End-to-end machine-file verification against fixtures produced by the SERVER's own
/// <c>encode_machine_file</c> — see <c>Fixtures/MachineFiles/PROVENANCE.md</c>.
/// </summary>
/// <remarks>
/// Every case is driven off <c>manifest.json</c> rather than hardcoded filenames, so a later
/// fixture drop (a new scheme, a new variant) is picked up with no edit here.
///
/// WHY THIS FILE EXISTS: <see cref="MachineFileTests"/> builds its own <c>.machine</c> files and
/// then reads them back. That proves the SDK is self-consistent and nothing else — it is exactly
/// how a fleet-wide misreading of the wire format (<c>enc</c>'s layout and the <c>alg</c> grammar)
/// stayed green in CI for two years while no SDK could open a file the server actually emitted.
/// Do not "simplify" these tests by generating the fixtures locally.
/// </remarks>
public class MachineFileFixtureTests
{
    /// <summary>
    /// A clock far before any fixture's <c>exp</c>, so a case that is not about expiry never
    /// trips the expiry gate. Expiry gets its own cases below, driven off each file's own signed
    /// <c>exp</c> claim — never off <c>DateTimeOffset.UtcNow</c>, which would make the suite a
    /// time bomb that goes red the moment the fixtures age past their TTL.
    /// </summary>
    private const long ClockBeforeAnyExpiry = 0;

    /// <summary>The clock-skew tolerance the SDK allows on <c>exp</c>, mirrored here to pin the boundary.</summary>
    private const long SkewToleranceSeconds = 60;

    private static readonly IReadOnlyDictionary<string, MachineFileFixture> Manifest = LoadManifest();

    // ── Manifest-driven case sources ──────────────────────────────────────────

    /// <summary>Every fixture in the manifest.</summary>
    public static TheoryData<string> AllFixtures() => Names(_ => true);

    /// <summary>Only the AES-256-GCM fixtures.</summary>
    public static TheoryData<string> EncryptedFixtures() => Names(f => f.Encrypted);

    /// <summary>Only fixtures the server issued already expired.</summary>
    public static TheoryData<string> ExpiredFixtures() => Names(f => f.Expired);

    /// <summary>Only fixtures that were live when issued.</summary>
    public static TheoryData<string> LiveFixtures() => Names(f => !f.Expired);

    // ── The happy path, per fixture ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_Verifies_AndYieldsMachineAndSignedClaims(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));

        // The manifest and the file must describe the same thing — catches a half-updated copy
        // of the fixture directory, which otherwise surfaces as a baffling signature failure.
        Assert.Equal(fixture.Alg, file.Certificate.Alg);
        Assert.Equal(fixture.EncIsDotSeparated, file.Certificate.Enc.Contains('.', StringComparison.Ordinal));

        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        Assert.True(file.Verify(scheme, publicKey), $"{name}: signature did not verify against the server-issued public key.");

        var (machine, claims) = file.VerifyWithClaims(
            scheme,
            publicKey,
            fixture.LicenseKey ?? string.Empty,
            fixture.Fingerprint,
            ClockBeforeAnyExpiry);

        Assert.Equal(fixture.Fingerprint, machine.Fingerprint);
        Assert.NotEqual(Guid.Empty, machine.Id);

        // M3: the signed meta claims exist and are surfaced. `kid` is the cross-check against the
        // manifest — a wrong one means the fixture was signed by a different key than advertised.
        Assert.Equal(fixture.Kid, claims.KeyId);
        Assert.True(claims.IssuedAt > 0, $"{name}: 'iat' claim missing or zero.");
        Assert.NotEmpty(claims.Id);
    }

    // ── M3: exp is enforced, and it is enforced off the SIGNED claim ──────────

    [Theory]
    [MemberData(nameof(ExpiredFixtures))]
    public void ExpiredFixture_IsRejectedAsExpired_NotAsForged(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        // The signature is perfectly good. Expiry must be a DIFFERENT outcome from a forgery, or
        // a caller cannot tell "fetch a fresh file" from "someone tampered with this one".
        Assert.True(file.Verify(scheme, publicKey));

        var claims = ClaimsOf(file, fixture);
        Assert.NotNull(claims.ExpiresAt);

        var expired = Assert.Throws<LicenseFileExpiredException>(() => file.VerifyWithClaims(
            scheme,
            publicKey,
            fixture.LicenseKey ?? string.Empty,
            fixture.Fingerprint,
            claims.IssuedAt));

        Assert.Equal(claims.ExpiresAt, expired.ExpiresAt);
    }

    [Theory]
    [MemberData(nameof(LiveFixtures))]
    public void LiveFixture_IsAccepted_AtItsOwnIssueTime(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        var claims = ClaimsOf(file, fixture);

        var machine = file.VerifyAndDecrypt(
            scheme,
            publicKey,
            fixture.LicenseKey ?? string.Empty,
            fixture.Fingerprint,
            claims.IssuedAt);

        Assert.Equal(fixture.Fingerprint, machine.Fingerprint);
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_ExpiryBoundary_AllowsExactlyTheSkewTolerance(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        if (ClaimsOf(file, fixture).ExpiresAt is not { } exp)
        {
            // A checkout made with no TTL legitimately produces a file with no `exp` that never
            // expires. Absence is not an error — there is simply no boundary to probe.
            return;
        }

        Machine Verify(long now) => file.VerifyAndDecrypt(
            scheme, publicKey, fixture.LicenseKey ?? string.Empty, fixture.Fingerprint, now);

        Verify(exp);
        Verify(exp + SkewToleranceSeconds);
        Assert.Throws<LicenseFileExpiredException>(() => Verify(exp + SkewToleranceSeconds + 1));
    }

    // ── M1: the `alg` grammar is parsed, not substring-sniffed ────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_WithoutV2Marker_IsRejected_AtParse(string name)
    {
        var fixture = Manifest[name];
        var original = MachineFile.Parse(ReadFixture(fixture.File));

        // `alg` sits OUTSIDE the signature (the server signs `enc`'s string bytes only), so
        // rewriting it leaves bytes whose signature would still verify — which is exactly why the
        // v2 gate cannot wait for a verifier. A v1 alg carried no `exp` inside the signature and
        // derived its AES key by zero-padding the license key instead of HKDF. The gate now fires
        // at Parse, before any key or signature work, on every entry point alike (D17).
        var downgraded = WithAlg(original, fixture.Alg.Replace("+v2", "", StringComparison.Ordinal));

        Assert.Throws<UnsupportedAlgorithmException>(() => MachineFile.Parse(downgraded));
    }

    /// <summary>
    /// The exact strings a substring test lets through. <c>Contains("base64")</c> plus
    /// <c>Contains("aes-256-gcm")</c> accepts every one of these, which is why the old check
    /// "passed" against the real server format entirely by accident.
    /// </summary>
    public static TheoryData<string, string> AlgMutations()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, fixture) in Manifest)
        {
            var alg = fixture.Alg;
            data.Add(name, alg.Replace("+v2", "+v3", StringComparison.Ordinal));
            data.Add(name, alg + "junk");
            data.Add(name, "x" + alg);
            data.Add(name, alg.Replace("+v2", "+V2", StringComparison.Ordinal));
            data.Add(name, alg.Replace("+v2", "", StringComparison.Ordinal) + "+ed25519+v2");
            data.Add(name, alg.Replace("+", "-", StringComparison.Ordinal));
            data.Add(name, "base64");
            data.Add(name, "");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AlgMutations))]
    public void Fixture_WithMalformedAlg_IsRejected(string name, string mutatedAlg)
    {
        var fixture = Manifest[name];
        if (string.Equals(mutatedAlg, fixture.Alg, StringComparison.Ordinal))
        {
            return; // A mutation that happens to be a no-op for this fixture's alg.
        }

        var mutated = WithAlg(MachineFile.Parse(ReadFixture(fixture.File)), mutatedAlg);

        // Grammar, version and encoding-prefix mutations are refused by Parse; a mutation that
        // survives Parse (it only corrupts the signing suffix) is refused by the scheme cross-check
        // in Open. Either way it is the one exception type, and never a signature verdict.
        Assert.Throws<UnsupportedAlgorithmException>(() => Open(MachineFile.Parse(mutated), fixture));
    }

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_WhoseAlgSuffixContradictsTheCallerScheme_IsRejected(string name)
    {
        var fixture = Manifest[name];
        var scheme = ParseScheme(fixture.Scheme);

        // Swap the middle segment for a different, individually-valid signing suffix. The suffix
        // is a cross-check only — the scheme is the caller's — but a file that disagrees with the
        // caller about how it was signed is not a file to open.
        var foreign = scheme == LicenseScheme.EcdsaP256Sign ? "ed25519" : "ecdsa-p256";
        var parts = fixture.Alg.Split('+');
        var contradicting = $"{parts[0]}+{foreign}+v2";

        var mutated = MachineFile.Parse(WithAlg(MachineFile.Parse(ReadFixture(fixture.File)), contradicting));

        Assert.Throws<UnsupportedAlgorithmException>(() => Open(mutated, fixture));
    }

    // ── M1: `alg` cannot identify the scheme; the caller's scheme is authoritative ──

    [Fact]
    public void RsaPkcs1Fixture_VerifiesUnderPkcs1_ButIsRefusedUnderJwtRs256()
    {
        // The server emits the SAME `rsa-sha256` suffix for RSA_2048_PKCS1_SIGN and
        // RSA_2048_JWT_RS256 (machine_file.rs:119-126). Identical bytes, identical `alg`, two
        // different caller-supplied schemes, two different outcomes — that is the proof the
        // scheme parameter is authoritative and `alg` is only ever a cross-check.
        var fixture = Manifest.Values.Single(f =>
            f.Alg.Contains("+rsa-sha256+", StringComparison.Ordinal) && !f.Encrypted && !f.Expired);
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        Assert.True(file.Verify(LicenseScheme.Rsa2048Pkcs1Sign, publicKey));
        Assert.Throws<SchemeNotSupportedException>(() => file.Verify(LicenseScheme.Rsa2048JwtRs256, publicKey));
        Assert.Throws<SchemeNotSupportedException>(() => file.VerifyAndDecrypt(
            LicenseScheme.Rsa2048JwtRs256, publicKey, string.Empty, fixture.Fingerprint));
    }

    // ── M2: `enc` is verified before anything is decoded ──────────────────────

    [Theory]
    [MemberData(nameof(AllFixtures))]
    public void Fixture_WithTamperedEnc_FailsSignature_BeforeAnythingIsDecoded(string name)
    {
        var fixture = Manifest[name];
        var original = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        // (a) still-well-formed base64, one character different.
        var flipped = MachineFile.Parse(WithEnc(original, FlipOneChar(original.Certificate.Enc)));
        Assert.False(flipped.Verify(scheme, publicKey));
        Assert.Throws<SignatureVerificationException>(() => Open(flipped, fixture));

        // (b) not base64 at all. If the pipeline decoded before verifying, this would surface as a
        // format error — proving attacker-controlled bytes had already been parsed. It must be a
        // signature failure instead.
        var garbage = MachineFile.Parse(WithEnc(original, "!!! not base64 !!!"));
        Assert.Throws<SignatureVerificationException>(() => Open(garbage, fixture));
    }

    // ── M2: decryption is bound to BOTH the license key and the fingerprint ───

    [Theory]
    [MemberData(nameof(EncryptedFixtures))]
    public void EncryptedFixture_WithWrongFingerprint_FailsAuthentication(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        Assert.Throws<SignatureVerificationException>(() => file.VerifyAndDecrypt(
            scheme, publicKey, fixture.LicenseKey ?? string.Empty, fixture.Fingerprint + "-wrong", ClockBeforeAnyExpiry));
    }

    [Theory]
    [MemberData(nameof(EncryptedFixtures))]
    public void EncryptedFixture_WithWrongLicenseKey_FailsAuthentication(string name)
    {
        var fixture = Manifest[name];
        var file = MachineFile.Parse(ReadFixture(fixture.File));
        var scheme = ParseScheme(fixture.Scheme);
        var publicKey = Convert.FromBase64String(fixture.PublicKeyB64);

        Assert.Throws<SignatureVerificationException>(() => file.VerifyAndDecrypt(
            scheme, publicKey, (fixture.LicenseKey ?? string.Empty) + "-wrong", fixture.Fingerprint, ClockBeforeAnyExpiry));
    }

    [Theory]
    [MemberData(nameof(EncryptedFixtures))]
    public void EncryptedFixture_HasTwoSeparatelyBase64dHalves(string name)
    {
        // Pinning the shape the SDK must read, independently of whether it can decrypt: a single
        // base64 blob with a nonce sliced off the first 12 bytes cannot open any of these.
        var fixture = Manifest[name];
        var enc = MachineFile.Parse(ReadFixture(fixture.File)).Certificate.Enc;

        var halves = enc.Split('.');
        Assert.Equal(2, halves.Length);
        Assert.Equal(12, Convert.FromBase64String(halves[0]).Length);
        Assert.True(Convert.FromBase64String(halves[1]).Length > 16, "ciphertext half must carry the 16-byte GCM tag on top of the ciphertext");

        // Why the old single-blob read was an ACTIVE failure here and merely a latent one in some
        // sibling SDKs: both halves are 4-character aligned, so a LENIENT base64 decoder (CPython,
        // Node) silently drops the '.', decodes the concatenation as one stream, and reconstructs
        // `nonce || ciphertext` byte for byte — the wrong code lands on the right bytes by luck.
        // `Convert.FromBase64String` is strict and rejects the '.', so on .NET no encrypted
        // machine file could be opened at all. Pinning that here keeps the distinction honest if
        // the decode path is ever swapped for something more permissive.
        Assert.Throws<FormatException>(() => Convert.FromBase64String(enc));
    }

    // ── The manifest is the contract; keep it honest ──────────────────────────

    [Fact]
    public void EveryEmbeddedFixtureFile_HasAManifestEntry_AndViceVersa()
    {
        var embedded = typeof(MachineFileFixtureTests).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith("MachineFileFixtures/", StringComparison.Ordinal) && n.EndsWith(".machine", StringComparison.Ordinal))
            .Select(n => n["MachineFileFixtures/".Length..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var declared = Manifest.Values.Select(f => f.File).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(declared, embedded);
        Assert.NotEmpty(declared);
    }

    [Fact]
    public void ManifestCoversEverySchemeTheSdkDispatchesOn()
    {
        // Not a hardcoded list of fixture names — a coverage floor. If a scheme branch of the
        // verifier has no server-issued file behind it, it is being tested against nothing.
        var covered = Manifest.Values.Select(f => ParseScheme(f.Scheme)).ToHashSet();

        Assert.Contains(LicenseScheme.Ed25519Sign, covered);
        Assert.Contains(LicenseScheme.EcdsaP256Sign, covered);
        Assert.Contains(LicenseScheme.Rsa2048Pkcs1Sign, covered);
        Assert.Contains(LicenseScheme.Rsa2048Pkcs1PssSign, covered);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Machine Open(MachineFile file, MachineFileFixture fixture) => file.VerifyAndDecrypt(
        ParseScheme(fixture.Scheme),
        Convert.FromBase64String(fixture.PublicKeyB64),
        fixture.LicenseKey ?? string.Empty,
        fixture.Fingerprint,
        ClockBeforeAnyExpiry);

    private static LicenseFileClaims ClaimsOf(MachineFile file, MachineFileFixture fixture) => file.VerifyWithClaims(
        ParseScheme(fixture.Scheme),
        Convert.FromBase64String(fixture.PublicKeyB64),
        fixture.LicenseKey ?? string.Empty,
        fixture.Fingerprint,
        ClockBeforeAnyExpiry).Claims;

    private static TheoryData<string> Names(Func<MachineFileFixture, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (var name in Manifest.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>
    /// Accepts both the <see cref="LicenseScheme"/> member name and the server's wire string, so a
    /// later fixture drop that switches convention fails loudly here rather than silently
    /// defaulting to Ed25519.
    /// </summary>
    private static LicenseScheme ParseScheme(string scheme) => scheme switch
    {
        "ED25519_SIGN" => LicenseScheme.Ed25519Sign,
        "RSA_2048_PKCS1_SIGN" => LicenseScheme.Rsa2048Pkcs1Sign,
        "RSA_2048_PKCS1_PSS_SIGN" => LicenseScheme.Rsa2048Pkcs1PssSign,
        "ECDSA_P256_SIGN" => LicenseScheme.EcdsaP256Sign,
        "RSA_2048_JWT_RS256" => LicenseScheme.Rsa2048JwtRs256,
        _ => Enum.Parse<LicenseScheme>(scheme),
    };

    private static string WithAlg(MachineFile file, string alg) => Wrap(file.Certificate.Enc, file.Certificate.Sig, alg);

    private static string WithEnc(MachineFile file, string enc) => Wrap(enc, file.Certificate.Sig, file.Certificate.Alg);

    private static string Wrap(string enc, string sig, string alg)
    {
        var certJson = JsonSerializer.Serialize(new { enc, sig, alg });
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(certJson));
        return $"-----BEGIN MACHINE FILE-----\n{body}\n-----END MACHINE FILE-----";
    }

    private static string FlipOneChar(string base64)
    {
        var chars = base64.ToCharArray();
        var i = chars.Length / 2;
        while (chars[i] is '.' or '=')
        {
            i++;
        }

        chars[i] = chars[i] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private static string ReadFixture(string file)
    {
        using var stream = FixtureStream(file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Stream FixtureStream(string file) =>
        typeof(MachineFileFixtureTests).Assembly.GetManifestResourceStream($"MachineFileFixtures/{file}")
        ?? throw new InvalidOperationException(
            $"Embedded fixture 'MachineFileFixtures/{file}' is missing. Fixtures live in " +
            "tests/Tamga.Sdk.Tests/Fixtures/MachineFiles and are wired up in the test csproj.");

    private static IReadOnlyDictionary<string, MachineFileFixture> LoadManifest()
    {
        using var stream = FixtureStream("manifest.json");
        var parsed = JsonSerializer.Deserialize<Dictionary<string, MachineFileFixture>>(stream)
            ?? throw new InvalidOperationException("Machine-file fixture manifest deserialized to null.");
        return parsed;
    }
}
