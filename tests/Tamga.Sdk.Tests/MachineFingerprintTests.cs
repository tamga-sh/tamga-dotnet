using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Tamga.Sdk.Tests;

/// <summary>One positive vector from <c>Fixtures/Fingerprints/fingerprint.json</c>.</summary>
public sealed record FingerprintVector
{
    /// <summary>The vector's name, used as the test case label.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The <c>[label, value]</c> pairs, in the order a caller supplied them.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<IReadOnlyList<string>> Components { get; init; } = Array.Empty<IReadOnlyList<string>>();

    /// <summary>The canonical string, with <c>&lt;US&gt;</c> standing in for U+001F.</summary>
    [JsonPropertyName("canonical")]
    public string Canonical { get; init; } = "";

    /// <summary>The expected 64-character lowercase hex digest.</summary>
    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; init; } = "";

    /// <summary>Why this vector is in the set.</summary>
    [JsonPropertyName("note")]
    public string Note { get; init; } = "";
}

/// <summary>One rejected case: an input no port may accept.</summary>
public sealed record RejectedFingerprintVector
{
    /// <summary>The case's name, used as the test case label.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The <c>[label, value]</c> pairs that must be refused.</summary>
    [JsonPropertyName("components")]
    public IReadOnlyList<IReadOnlyList<string>> Components { get; init; } = Array.Empty<IReadOnlyList<string>>();

    /// <summary>Why the input is a caller bug rather than something to fix up.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}

/// <summary>The whole vector document.</summary>
public sealed record FingerprintVectorFile
{
    /// <summary>The positive vectors.</summary>
    [JsonPropertyName("vectors")]
    public IReadOnlyList<FingerprintVector> Vectors { get; init; } = Array.Empty<FingerprintVector>();

    /// <summary>The rejected cases.</summary>
    [JsonPropertyName("rejected")]
    public IReadOnlyList<RejectedFingerprintVector> Rejected { get; init; } = Array.Empty<RejectedFingerprintVector>();
}

/// <summary>
/// Pins <see cref="MachineFingerprint"/> against the cross-port vector file every Tamga SDK shares.
/// </summary>
/// <remarks>
/// <para>
/// WHY THESE VECTORS: the digests come from an independent SHA-256 implementation, not from any
/// SDK — see <c>Fixtures/Fingerprints/PROVENANCE.md</c>. A fixture this SDK produced could only
/// prove this SDK agrees with itself, which is exactly how a fleet-wide misreading of the
/// <c>.machine</c> wire format stayed green in CI for two years.
/// </para>
/// <para>
/// Every non-ASCII literal in this file is written as a <c>\uXXXX</c> escape rather than typed
/// directly. In a suite whose whole subject is byte-exactness, a literal would make the assertion
/// depend on whichever encoding the compiler happened to read this source file with; the escape
/// states the code point outright. tamga-python met the equivalent failure for real — its
/// <c>non_ascii_value</c> vector passed on Linux and macOS and failed on <c>windows-latest</c>
/// only, because <c>Path.read_text()</c> used the platform locale codec and the value decoded as
/// mojibake. That is one SDK disagreeing with itself across two operating systems, which is the
/// exact failure the shared specification exists to prevent, arriving by a route the specification
/// does not cover. The vector file is read as UTF-8 explicitly here for the same reason, and
/// <see cref="VectorFile_DecodedAsUtf8_NotThePlatformCodepage"/> fails loudly rather than as a
/// hash mismatch if it ever is not.
/// </para>
/// <para>
/// The other eight vectors are pure ASCII, so a suite without <c>non_ascii_value</c> would look
/// green against a mis-decoding reader and prove nothing.
/// </para>
/// </remarks>
public class MachineFingerprintTests
{
    private const string SeparatorPlaceholder = "<US>";

    /// <summary>U+00E9, LATIN SMALL LETTER E WITH ACUTE — the precomposed form.</summary>
    private const string EAcutePrecomposed = "\u00E9";

    /// <summary>U+0065 U+0301 — "e" plus COMBINING ACUTE ACCENT. NFC-equal to the above, byte-different.</summary>
    private const string EAcuteDecomposed = "e\u0301";

    /// <summary>U+00A0, NO-BREAK SPACE: whitespace to .NET, not ASCII whitespace.</summary>
    private const string NoBreakSpace = "\u00A0";

    private static readonly FingerprintVectorFile Vectors = LoadVectors();

    /// <summary>
    /// Reads the embedded vector file as UTF-8 <b>explicitly</b>.
    /// </summary>
    /// <remarks>
    /// <c>new StreamReader(stream)</c> already defaults to UTF-8 and .NET Core's
    /// <c>Encoding.Default</c> is UTF-8 on every platform (unlike .NET Framework, where it was the
    /// ANSI codepage), so this is belt-and-braces on this runtime rather than a fix. It is spelled
    /// out anyway because the equivalent default is NOT safe in every language, one sibling SDK
    /// shipped exactly that bug, and a reader three years from now should not have to know which
    /// .NET this was.
    /// </remarks>
    private static FingerprintVectorFile LoadVectors()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("FingerprintFixtures/fingerprint.json")
            ?? throw new InvalidOperationException("Embedded resource FingerprintFixtures/fingerprint.json was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonSerializer.Deserialize<FingerprintVectorFile>(reader.ReadToEnd())
            ?? throw new InvalidOperationException("fingerprint.json deserialized to null.");
    }

    private static (string Label, string Value)[] Components(FingerprintVector v) =>
        v.Components.Select(c => (c[0], c[1])).ToArray();

    private static (string Label, string Value)[] Components(RejectedFingerprintVector v) =>
        v.Components.Select(c => (c[0], c[1])).ToArray();

    private static string ExpandSeparator(string canonical) =>
        canonical.Replace(SeparatorPlaceholder, MachineFingerprint.Separator.ToString(), StringComparison.Ordinal);

    /// <summary>Every positive vector, by name.</summary>
    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Vectors.Vectors)
        {
            data.Add(vector.Name);
        }

        return data;
    }

    /// <summary>Every rejected case, by name.</summary>
    public static TheoryData<string> RejectedNames()
    {
        var data = new TheoryData<string>();
        foreach (var rejected in Vectors.Rejected)
        {
            data.Add(rejected.Name);
        }

        return data;
    }

    /// <summary>The fixture loaded and carries exactly what the specification says it carries.</summary>
    [Fact]
    public void VectorFile_LoadsWithEveryCaseItIsSupposedToCarry()
    {
        Assert.Equal(9, Vectors.Vectors.Count);
        Assert.Equal(8, Vectors.Rejected.Count);
    }

    /// <summary>
    /// THE CROSS-PLATFORM GUARD. The one non-ASCII vector must arrive as the code point the file
    /// actually contains, not as whatever the platform's default codec made of its UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// Fails with "the fixture was mis-decoded" rather than with an unexplained hash mismatch,
    /// which is the difference between a five-minute diagnosis and an afternoon. A Latin-1 reading
    /// of the same bytes yields two characters (U+00C3 U+00A9) where UTF-8 yields one (U+00E9), so
    /// the length assertion alone catches it.
    /// </remarks>
    [Fact]
    public void VectorFile_DecodedAsUtf8_NotThePlatformCodepage()
    {
        var vector = Vectors.Vectors.Single(v => v.Name == "non_ascii_value");
        var value = vector.Components[0][1];

        Assert.Equal("caf" + EAcutePrecomposed, value);
        Assert.Equal(4, value.Length);
        Assert.Equal(0x00E9, value[3]);

        // The exact mojibake a locale-codec read would have produced.
        Assert.NotEqual("caf" + (char)0x00C3 + (char)0x00A9, value);
    }

    // ── The vectors themselves ───────────────────────────────────────────────

    /// <summary>Each vector's canonical string reproduces exactly, separator byte included.</summary>
    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Canonicalize_ReproducesEveryVector(string name)
    {
        var vector = Vectors.Vectors.Single(v => v.Name == name);

        Assert.Equal(ExpandSeparator(vector.Canonical), MachineFingerprint.Canonicalize(Components(vector)));
    }

    /// <summary>Each vector's digest reproduces exactly.</summary>
    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Compute_ReproducesEveryVector(string name)
    {
        var vector = Vectors.Vectors.Single(v => v.Name == name);

        var fingerprint = MachineFingerprint.Compute(Components(vector));

        Assert.Equal(vector.Fingerprint, fingerprint);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint.ToLowerInvariant(), fingerprint);
    }

    /// <summary>
    /// <c>Compute</c> really is the SHA-256 of <c>Canonicalize</c>'s UTF-8 bytes, recomputed here
    /// rather than merely quoted from the file.
    /// </summary>
    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Compute_IsExactlyTheSha256OfTheCanonicalStringsUtf8Bytes(string name)
    {
        var vector = Vectors.Vectors.Single(v => v.Name == name);

        var canonical = MachineFingerprint.Canonicalize(Components(vector));
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        Assert.Equal(expected, MachineFingerprint.Compute(Components(vector)));
    }

    // ── The three invariants, each with its vector pair ──────────────────────

    /// <summary>
    /// ORDER-INDEPENDENCE. Ordering is the caller's convenience, not part of the identity — a
    /// caller who reorders the pieces of a composite fingerprint between releases must not
    /// re-activate every install in the field.
    /// </summary>
    [Fact]
    public void OrderIndependence_TwoSortedEqualsTwoUnsorted()
    {
        var sorted = Vectors.Vectors.Single(v => v.Name == "two_sorted");
        var unsorted = Vectors.Vectors.Single(v => v.Name == "two_unsorted");

        Assert.Equal(
            MachineFingerprint.Compute(Components(sorted)),
            MachineFingerprint.Compute(Components(unsorted)));

        // And not merely equal to each other — equal to the answer the file states.
        Assert.Equal(sorted.Fingerprint, MachineFingerprint.Compute(Components(unsorted)));
    }

    /// <summary>
    /// WHITESPACE EQUIVALENCE. Leading and trailing ASCII whitespace is the footgun this helper
    /// exists to absorb: a serial number read from a command's stdout arrives with a trailing
    /// newline.
    /// </summary>
    [Fact]
    public void WhitespaceEquivalence_TrimmedEqualsSingle()
    {
        var single = Vectors.Vectors.Single(v => v.Name == "single");
        var trimmed = Vectors.Vectors.Single(v => v.Name == "whitespace_trimmed");

        Assert.Equal(
            MachineFingerprint.Compute(Components(single)),
            MachineFingerprint.Compute(Components(trimmed)));
        Assert.Equal(single.Fingerprint, MachineFingerprint.Compute(Components(trimmed)));
    }

    /// <summary>
    /// CASE PRESERVATION. The inequality is the assertion. Lowercasing a base64 or hex identifier
    /// corrupts it, so folding is deliberately absent — and a port that "helpfully" folded would
    /// pass both tests above while failing this one.
    /// </summary>
    [Fact]
    public void CasePreservation_CasePreservedDiffersFromSingle()
    {
        var single = Vectors.Vectors.Single(v => v.Name == "single");
        var cased = Vectors.Vectors.Single(v => v.Name == "case_preserved");

        Assert.NotEqual(
            MachineFingerprint.Compute(Components(single)),
            MachineFingerprint.Compute(Components(cased)));
        Assert.Equal(cased.Fingerprint, MachineFingerprint.Compute(Components(cased)));
    }

    // ── Rejections ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every rejected case throws. Silently repairing any of them — stripping a control character,
    /// deduplicating a repeated label — would map two different inputs onto one seat, which is the
    /// bug this helper exists to prevent.
    /// </summary>
    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void Compute_RejectsEveryInvalidCase(string name)
    {
        var rejected = Vectors.Rejected.Single(v => v.Name == name);

        var ex = Assert.Throws<ArgumentException>(() => MachineFingerprint.Compute(Components(rejected)));
        Assert.Equal("components", ex.ParamName);
    }

    /// <summary>Canonicalize refuses exactly what Compute refuses — validation is not on the hashing side.</summary>
    [Theory]
    [MemberData(nameof(RejectedNames))]
    public void Canonicalize_RejectsEveryInvalidCase(string name)
    {
        var rejected = Vectors.Rejected.Single(v => v.Name == name);

        Assert.Throws<ArgumentException>(() => MachineFingerprint.Canonicalize(Components(rejected)));
    }

    /// <summary>
    /// A duplicate label must not be silently collapsed onto one component — the specific repair
    /// that would put two machines on one seat.
    /// </summary>
    [Fact]
    public void Compute_DoesNotDeduplicateARepeatedLabel()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => MachineFingerprint.Compute(("id", "a"), ("id", "b")));

        Assert.Contains("more than once", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A control character inside a value must be refused, not stripped. The stripped input is a
    /// perfectly valid fingerprint, so an implementation that strips would return that instead of
    /// throwing — which is the failure this pins.
    /// </summary>
    [Fact]
    public void Compute_RefusesAnInteriorControlCharacterRatherThanStrippingIt()
    {
        const string withBell = "a\u0007b";

        var whatStrippingWouldProduce = MachineFingerprint.Compute(("id", "ab"));
        Assert.Equal(64, whatStrippingWouldProduce.Length);

        var ex = Assert.Throws<ArgumentException>(() => MachineFingerprint.Compute(("id", withBell)));
        Assert.Contains("control character", ex.Message, StringComparison.Ordinal);
    }

    // ── Sorting: the two cases the shared vectors do not discriminate ────────

    /// <summary>
    /// THE .NET TRAP. The sort is on UTF-8 bytes; <c>Array.Sort(string[])</c>,
    /// <c>List&lt;string&gt;.Sort()</c> and <see cref="string.CompareTo(string)"/> are
    /// culture-sensitive.
    /// </summary>
    /// <remarks>
    /// Measured on net8.0/ICU: <c>string.Compare("Disk=x", "arch=y", StringComparison.CurrentCulture)</c>
    /// is <c>+1</c> while the byte comparison is <c>-1</c>, so the default sort produces
    /// <c>arch=y</c> then <c>Disk=x</c> where this rule requires the opposite. None of the nine
    /// shared vectors discriminates it — <c>many</c>'s four labels sort identically under every
    /// comparer — so it is asserted here against <c>Canonicalize</c> output derived from the stated
    /// rule, inventing no digest.
    /// </remarks>
    [Fact]
    public void Canonicalize_OrdersMixedCaseLabelsByBytes_NotByCulture()
    {
        var canonical = MachineFingerprint.Canonicalize(("arch", "y"), ("Disk", "x"));

        // 'D' is 0x44 and 'a' is 0x61, so "Disk=x" sorts first.
        Assert.Equal(
            $"{MachineFingerprint.DomainPrefix}{MachineFingerprint.Separator}Disk=x{MachineFingerprint.Separator}arch=y",
            canonical);

        // Pin the divergence itself. Without this the test could quietly stop discriminating if the
        // culture-aware answer ever agreed, and would then prove nothing while staying green.
        Assert.Equal(1, Math.Sign(string.Compare("Disk=x", "arch=y", StringComparison.CurrentCulture)));
    }

    /// <summary>
    /// The sort key is the WHOLE <c>label=value</c> component, not the label alone. The two differ
    /// whenever one label is a prefix of another and the next byte sorts below <c>'='</c>.
    /// </summary>
    /// <remarks>
    /// <c>"mac-1"</c> vs <c>"mac"</c>: sorting on labels alone puts <c>mac</c> first (it is a
    /// prefix), but on whole components the comparison reaches <c>'-'</c> (0x2D) against
    /// <c>'='</c> (0x3D) and <c>mac-1=x</c> sorts first. No shared vector covers this either.
    /// </remarks>
    [Fact]
    public void Canonicalize_SortsOnTheWholeComponent_NotOnTheLabelAlone()
    {
        var canonical = MachineFingerprint.Canonicalize(("mac", "y"), ("mac-1", "x"));

        Assert.Equal(
            $"{MachineFingerprint.DomainPrefix}{MachineFingerprint.Separator}mac-1=x{MachineFingerprint.Separator}mac=y",
            canonical);
    }

    // ── Shape of the output ──────────────────────────────────────────────────

    /// <summary>
    /// The separator really is the single byte 0x1F, never the four-character placeholder the
    /// vector file displays it as. A port that hashed the placeholder would reproduce nothing.
    /// </summary>
    [Fact]
    public void Canonicalize_UsesTheRealUnitSeparatorByte_NotTheDisplayPlaceholder()
    {
        var canonical = MachineFingerprint.Canonicalize(("a", "1"), ("b", "2"));

        Assert.DoesNotContain(SeparatorPlaceholder, canonical, StringComparison.Ordinal);
        Assert.Equal(2, canonical.Count(c => c == MachineFingerprint.Separator));
        Assert.Equal(0x1F, MachineFingerprint.Separator);

        var bytes = Encoding.UTF8.GetBytes(canonical);
        Assert.Equal(2, bytes.Count(b => b == 0x1F));
    }

    /// <summary>The domain prefix leads every canonical string, so a future v2 rule cannot collide with v1.</summary>
    [Fact]
    public void Canonicalize_AlwaysStartsWithTheDomainPrefix()
    {
        Assert.StartsWith(
            MachineFingerprint.DomainPrefix + MachineFingerprint.Separator,
            MachineFingerprint.Canonicalize(("a", "1")),
            StringComparison.Ordinal);
    }

    /// <summary>The two overloads are the same function.</summary>
    [Fact]
    public void TheTupleAndPairOverloadsAgree()
    {
        var pairs = new[]
        {
            new KeyValuePair<string, string>("machine-id", "abc123"),
            new KeyValuePair<string, string>("disk", "SN-9"),
        };

        Assert.Equal(MachineFingerprint.Compute(pairs), MachineFingerprint.Compute(("machine-id", "abc123"), ("disk", "SN-9")));
        Assert.Equal(MachineFingerprint.Canonicalize(pairs), MachineFingerprint.Canonicalize(("machine-id", "abc123"), ("disk", "SN-9")));
    }

    // ── Deliberate non-behaviours ────────────────────────────────────────────

    /// <summary>
    /// NFC normalisation is deliberately absent. .NET could do it in one line and must not: NFC
    /// needs a new dependency in Rust and Go and ICU or hand-rolled tables in C11, so adding it
    /// here would make .NET the one SDK that disagrees with the other seven about which machine a
    /// given input describes.
    /// </summary>
    /// <remarks>
    /// U+00E9 and U+0065 U+0301 are the same string after NFC and different byte sequences before
    /// it. This asserts they stay different, which is the surprising direction and therefore the
    /// one worth pinning.
    /// </remarks>
    [Fact]
    public void Compute_DoesNotUnicodeNormalise()
    {
        var precomposed = "caf" + EAcutePrecomposed;
        var decomposed = "caf" + EAcuteDecomposed;

        // The premise: NFC-equal, not byte-equal. If this ever fails the test below is vacuous.
        Assert.Equal(precomposed, decomposed.Normalize(NormalizationForm.FormC));
        Assert.NotEqual(precomposed, decomposed);

        Assert.NotEqual(
            MachineFingerprint.Compute(("owner", precomposed)),
            MachineFingerprint.Compute(("owner", decomposed)));

        // The precomposed form is the one the shared vector file pins.
        var vector = Vectors.Vectors.Single(v => v.Name == "non_ascii_value");
        Assert.Equal(vector.Fingerprint, MachineFingerprint.Compute(("owner", precomposed)));
    }

    /// <summary>
    /// Trimming is the six ASCII whitespace characters, not <see cref="string.Trim()"/>'s full
    /// Unicode set. U+00A0 is whitespace to .NET and is NOT trimmed by the Rust, Go and C11 ports,
    /// so it must survive here too.
    /// </summary>
    [Fact]
    public void Compute_TrimsOnlyAsciiWhitespace_NotEveryUnicodeSpace()
    {
        // The premise: .NET really does consider this whitespace, so Trim() really would eat it.
        Assert.True(char.IsWhiteSpace(NoBreakSpace[0]));
        Assert.Equal("x", (NoBreakSpace + "x" + NoBreakSpace).Trim());

        // It survives here, so it is part of the identity like any other character.
        Assert.NotEqual(
            MachineFingerprint.Compute(("id", "x")),
            MachineFingerprint.Compute(("id", NoBreakSpace + "x" + NoBreakSpace)));
    }

    /// <summary>Every ASCII whitespace character in the set really is trimmed, from both ends.</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\v")]
    [InlineData("\f")]
    public void Compute_TrimsEachAsciiWhitespaceCharacterFromBothEnds(string ws)
    {
        Assert.Equal(
            MachineFingerprint.Compute(("id", "x")),
            MachineFingerprint.Compute(("id", ws + "x" + ws)));
    }

    /// <summary>An empty value is legal and the label still contributes; a null one is a caller bug.</summary>
    [Fact]
    public void EmptyValueIsLegal_ButNullIsNot()
    {
        var empty = Vectors.Vectors.Single(v => v.Name == "empty_value");
        Assert.Equal(empty.Fingerprint, MachineFingerprint.Compute(("machine-id", "")));

        // A value that is only whitespace trims to empty, and is therefore also legal.
        Assert.Equal(empty.Fingerprint, MachineFingerprint.Compute(("machine-id", "   ")));

        Assert.Throws<ArgumentException>(() => MachineFingerprint.Compute(("machine-id", null!)));
        Assert.Throws<ArgumentException>(() => MachineFingerprint.Compute((null!, "x")));
    }

    /// <summary>A null component list is a null argument, not an empty one.</summary>
    [Fact]
    public void NullComponentsThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => MachineFingerprint.Compute((IEnumerable<KeyValuePair<string, string>>)null!));
        Assert.Throws<ArgumentNullException>(
            () => MachineFingerprint.Compute(((string, string)[])null!));
        Assert.Throws<ArgumentNullException>(
            () => MachineFingerprint.Canonicalize((IEnumerable<KeyValuePair<string, string>>)null!));
        Assert.Throws<ArgumentNullException>(
            () => MachineFingerprint.Canonicalize(((string, string)[])null!));
    }

    /// <summary>A label may not carry a space, so it can never itself need trimming.</summary>
    [Fact]
    public void SpaceIsNotAllowedInALabel()
    {
        Assert.Throws<ArgumentException>(() => MachineFingerprint.Compute(("machine id", "x")));
    }

    /// <summary>Many components still round-trip, and the result stays a 64-character digest.</summary>
    [Fact]
    public void ManyComponentsStillProduceOneDigest()
    {
        var components = Enumerable.Range(0, 200)
            .Select(i => new KeyValuePair<string, string>($"label-{i:D3}", $"value-{i}"))
            .ToArray();

        var shuffled = Enumerable.Reverse(components).ToArray();

        Assert.Equal(64, MachineFingerprint.Compute(components).Length);
        Assert.Equal(MachineFingerprint.Compute(components), MachineFingerprint.Compute(shuffled));
    }
}
