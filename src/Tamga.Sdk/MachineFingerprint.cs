using System.Security.Cryptography;
using System.Text;

namespace Tamga.Sdk;

/// <summary>
/// Turns caller-chosen, labelled machine characteristics into one canonical fingerprint string —
/// the value to pass as <c>Machine.Fingerprint</c> (or <c>Component.Fingerprint</c>) — so that the
/// same machine described in a different order, or with stray surrounding whitespace, does not
/// consume a second seat.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this exists to fix.</b> Every Tamga SDK sent the caller's fingerprint string
/// byte-for-byte. The server stores <c>fingerprint TEXT NOT NULL</c> — no length limit, no
/// <c>CHECK</c>, no normalisation — unique per <c>(license_id, fingerprint)</c>. So
/// <c>"ABC-123"</c>, <c>"abc-123"</c> and <c>" ABC-123 "</c> were three machines on three seats,
/// and a caller who reordered the pieces of a composite fingerprint between releases silently
/// re-activated every install in the field.
/// </para>
/// <para>
/// <b>What this deliberately does NOT do: read hardware identifiers.</b> There is no
/// <c>FromThisMachine()</c> here and there should not be. What identifies a machine is a product
/// decision, not a library's — a cloned VM template shares its identifiers, a container has none,
/// a replaced motherboard changes them — and no default is right for both a desktop app and a
/// Kubernetes sidecar. Choose the components yourself; this type only pins how they combine.
/// </para>
/// <para>
/// <b>The algorithm</b>, identical in all eight Tamga SDKs:
/// </para>
/// <code>
/// canonical   = "tamga-fingerprint-v1" &lt;US&gt; join(&lt;US&gt;, sort_bytewise(["label=" + trimmed_value]))
/// fingerprint = lowercase_hex( SHA-256( UTF-8( canonical ) ) )
/// </code>
/// <para>
/// <c>&lt;US&gt;</c> is <see cref="Separator"/>, U+001F, emitted as the single byte <c>0x1F</c>.
/// The literal prefix is a domain separator, so a future v2 rule cannot collide with v1.
/// </para>
/// <para>
/// <b>Case is preserved, deliberately.</b> Lowercasing a base64 or hex identifier corrupts it, so
/// there is no case folding: <c>"ABC123"</c> and <c>"abc123"</c> are different machines. Only
/// surrounding ASCII whitespace is absorbed.
/// </para>
/// <para>
/// <b>Values are NOT Unicode-normalised, and that is a constraint rather than an oversight.</b>
/// .NET has <see cref="string.Normalize()"/>, so adding NFC here would be one line — do not. NFC
/// needs a new dependency in Rust and Go, and in C11 it would mean ICU or hand-rolled Unicode
/// tables inside a library whose whole selling point is having none. A rule eight ports cannot
/// implement identically is worse than no rule: it would produce two fingerprints for one machine
/// depending on which SDK the application was written in, silently consuming two seats, and it
/// would make .NET the outlier that disagrees with the other seven. If your values can arrive in
/// more than one normal form, normalise them before calling — and do it the same way in every
/// application that activates the same machine.
/// </para>
/// <para>
/// <b>Invalid input throws; it is never quietly repaired.</b> Stripping a control character or
/// deduplicating a repeated label would map two different inputs onto one seat, which is the exact
/// failure mode this type exists to prevent.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var fingerprint = MachineFingerprint.Compute(
///     ("machine-id", machineId),
///     ("disk", diskSerial),
///     ("mac", primaryMac));
///
/// await client.ActivateMachineAsync(new ActivateMachineRequest
/// {
///     LicenseId = licenseId,
///     Fingerprint = fingerprint,
/// });
/// </code>
/// </example>
public static class MachineFingerprint
{
    /// <summary>The domain-separating prefix of every canonical string, <c>"tamga-fingerprint-v1"</c>.</summary>
    /// <remarks>
    /// Present so a future v2 rule cannot produce a canonical string that collides with a v1 one.
    /// Changing it changes every fingerprint this SDK produces and would re-activate every install
    /// in the field, so the rule is versioned rather than edited.
    /// </remarks>
    public const string DomainPrefix = "tamga-fingerprint-v1";

    /// <summary>The component separator: U+001F, the ASCII unit separator — one byte (<c>0x1F</c>) in UTF-8.</summary>
    public const char Separator = '\u001F';

    /// <summary>The character separating a label from its value inside a component.</summary>
    public const char LabelValueSeparator = '=';

    /// <summary>Lowest ASCII code point allowed in a label (<c>'!'</c>, 0x21).</summary>
    private const char MinLabelChar = '!';

    /// <summary>Highest ASCII code point allowed in a label (<c>'~'</c>, 0x7E).</summary>
    private const char MaxLabelChar = '~';

    /// <summary>Highest ASCII C0 control code point; anything at or below it is refused in a value.</summary>
    private const char MaxControlChar = '\u001F';

    /// <summary>DEL — the one control character above the C0 block.</summary>
    private const char DeleteChar = '\u007F';

    /// <summary>The parameter name reported by every validation failure, whichever overload the components arrived through.</summary>
    private const string ComponentsParamName = "components";

    /// <summary>
    /// ASCII whitespace trimmed from both ends of a value BEFORE it is validated: space, tab, CR,
    /// LF, vertical tab and form feed.
    /// </summary>
    /// <remarks>
    /// Deliberately this exact set rather than <see cref="string.Trim()"/>, which trims every
    /// Unicode whitespace scalar — U+00A0, U+2028 and two dozen more that the Rust, Go and C11
    /// ports do not touch. Trimming more than the other seven ports do would produce a different
    /// fingerprint for the same machine.
    /// </remarks>
    private static readonly char[] AsciiWhitespace = [' ', '\t', '\r', '\n', '\v', '\f'];

    /// <summary>
    /// Builds the canonical string that <see cref="Compute(IEnumerable{KeyValuePair{string, string}})"/>
    /// hashes, without hashing it.
    /// </summary>
    /// <param name="components">
    /// The labelled components. Order is irrelevant — they are sorted internally — but every label
    /// must be distinct.
    /// </param>
    /// <returns>
    /// <see cref="DomainPrefix"/> followed by each <c>label=trimmedValue</c> component, joined with
    /// <see cref="Separator"/> and ordered by their UTF-8 bytes.
    /// </returns>
    /// <remarks>
    /// Public because it is what makes a fingerprint mismatch diagnosable: comparing two canonical
    /// strings shows which component differs, where comparing two digests shows only that they do.
    /// It is also the value another Tamga SDK can be asked to produce for the same input when
    /// cross-checking a port.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// No components were supplied; a label is null, empty, repeated, or contains a character
    /// outside ASCII <c>0x21</c>-<c>0x7E</c> or a <c>'='</c>; or a value is null or still contains
    /// an ASCII control character after trimming.
    /// </exception>
    public static string Canonicalize(IEnumerable<KeyValuePair<string, string>> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return Encoding.UTF8.GetString(BuildCanonicalBytes(components));
    }

    /// <summary>Builds the canonical string from a parameter list of <c>(label, value)</c> tuples.</summary>
    /// <param name="components">The labelled components.</param>
    /// <returns>The canonical string — see the other overload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">See the other overload.</exception>
    public static string Canonicalize(params (string Label, string Value)[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return Canonicalize(ToPairs(components));
    }

    /// <summary>
    /// Computes the fingerprint: the lowercase hex SHA-256 of the canonical string's UTF-8 bytes,
    /// 64 characters.
    /// </summary>
    /// <param name="components">
    /// The labelled components. Order is irrelevant; every label must be distinct; values are
    /// trimmed of ASCII whitespace and otherwise passed through byte-for-byte, case included.
    /// </param>
    /// <returns>A 64-character lowercase hex string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// See <see cref="Canonicalize(IEnumerable{KeyValuePair{string, string}})"/>. Invalid input is
    /// refused rather than repaired — see the type-level remarks for why.
    /// </exception>
    public static string Compute(IEnumerable<KeyValuePair<string, string>> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return Convert.ToHexString(SHA256.HashData(BuildCanonicalBytes(components))).ToLowerInvariant();
    }

    /// <summary>Computes the fingerprint from a parameter list of <c>(label, value)</c> tuples.</summary>
    /// <param name="components">The labelled components.</param>
    /// <returns>A 64-character lowercase hex string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">See the other overload.</exception>
    public static string Compute(params (string Label, string Value)[] components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return Compute(ToPairs(components));
    }

    private static IEnumerable<KeyValuePair<string, string>> ToPairs((string Label, string Value)[] components)
    {
        foreach (var (label, value) in components)
        {
            yield return new KeyValuePair<string, string>(label, value);
        }
    }

    /// <summary>
    /// The single place the canonical form is assembled. <see cref="Canonicalize(IEnumerable{KeyValuePair{string, string}})"/>
    /// decodes these bytes and <see cref="Compute(IEnumerable{KeyValuePair{string, string}})"/>
    /// hashes them, so the two can never disagree about what was canonicalised — and the separator
    /// is unambiguously the single byte <c>0x1F</c> rather than something that round-tripped
    /// through UTF-16.
    /// </summary>
    private static byte[] BuildCanonicalBytes(IEnumerable<KeyValuePair<string, string>> components)
    {
        var encoded = EncodeComponents(components);
        var prefix = Encoding.UTF8.GetBytes(DomainPrefix);

        var total = prefix.Length;
        foreach (var component in encoded)
        {
            total += 1 + component.Length;
        }

        var buffer = new byte[total];
        prefix.CopyTo(buffer, 0);

        var offset = prefix.Length;
        foreach (var component in encoded)
        {
            buffer[offset++] = (byte)Separator;
            component.CopyTo(buffer, offset);
            offset += component.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Validates every component, encodes each as the UTF-8 bytes of <c>label=trimmedValue</c>, and
    /// sorts them bytewise ascending.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The sort is on UTF-8 BYTES, and .NET's convenient sorts are not.</b>
    /// <c>Array.Sort(string[])</c>, <c>List&lt;string&gt;.Sort()</c> and
    /// <see cref="string.CompareTo(string)"/> all route through <see cref="Comparer{T}.Default"/>
    /// and are <b>culture-sensitive</b>. Measured on net8.0: <c>"Disk=x"</c> vs <c>"arch=y"</c>
    /// compares <c>+1</c> under the current culture and <c>-1</c> bytewise, so the default sort
    /// yields <c>arch=y, Disk=x</c> where this rule requires <c>Disk=x, arch=y</c>. Any mixed-case
    /// label set diverges — and it would diverge from the other seven SDKs, not merely from a
    /// specification document.
    /// </para>
    /// <para>
    /// <see cref="StringComparer.Ordinal"/>, by contrast, <b>cannot</b> diverge from a UTF-8 byte
    /// sort for any input this type accepts, and it is worth knowing why rather than guessing.
    /// Labels are unique and ASCII-printable and <c>'='</c> is ASCII, so when two components are
    /// compared the deciding byte is always below <c>0x80</c> — either inside the labels, or at
    /// the <c>'='</c> that terminates the shorter one. A value's bytes never reach the comparison,
    /// so the place the two orderings genuinely differ (UTF-16 code units vs UTF-8 bytes above the
    /// BMP) is unreachable. tamga-js measured this over 8 732 016 valid pairs and found zero
    /// divergence. <b>Do not add a test asserting the difference</b>: it would be green for the
    /// wrong reason, and the corresponding mutation survives. The bytes are compared directly
    /// because it is the rule as written and costs nothing, not because <c>Ordinal</c> would be
    /// wrong today.
    /// </para>
    /// <para>
    /// The two mutations that DO bite, here and in every sibling port, are sorting on the label
    /// alone instead of the whole component, and sorting case-insensitively. Both are covered.
    /// This repo already carries the same class of fix one layer down —
    /// <c>CanonicalJson.Utf8OrdinalComparer</c> in <c>Proof.cs</c> exists because an offline-proof
    /// field ordering diverged from the server's <c>BTreeMap</c>.
    /// </para>
    /// <para>
    /// Sorting the encoded bytes also means each component is encoded exactly once, and the sort
    /// key is literally the thing that gets hashed.
    /// </para>
    /// </remarks>
    private static List<byte[]> EncodeComponents(IEnumerable<KeyValuePair<string, string>> components)
    {
        var seenLabels = new HashSet<string>(StringComparer.Ordinal);
        var encoded = new List<byte[]>();

        foreach (var (label, value) in components)
        {
            ValidateLabel(label, seenLabels);
            var trimmed = TrimAsciiWhitespace(label, value);
            ValidateValue(label, trimmed);
            encoded.Add(Encoding.UTF8.GetBytes(label + LabelValueSeparator + trimmed));
        }

        if (encoded.Count == 0)
        {
            throw new ArgumentException(
                "At least one component is required to compute a fingerprint.", ComponentsParamName);
        }

        // Bytewise ascending. Span<byte>.SequenceCompareTo compares unsigned bytes, which is the
        // ordering the specification names. Stability is irrelevant: duplicate labels are already
        // refused, so no two components can compare equal.
        encoded.Sort(static (x, y) => x.AsSpan().SequenceCompareTo(y.AsSpan()));
        return encoded;
    }

    private static void ValidateLabel(string label, HashSet<string> seenLabels)
    {
        if (label is null)
        {
            throw new ArgumentException("A component label must not be null.", ComponentsParamName);
        }

        if (label.Length == 0)
        {
            throw new ArgumentException("A component label must not be empty.", ComponentsParamName);
        }

        foreach (var c in label)
        {
            // ASCII printable only. Space (0x20) is excluded too, so a label can never itself need
            // trimming, and a non-ASCII label can never itself need normalising — which is what
            // keeps the deliberate absence of Unicode normalisation off the labels entirely.
            if (c < MinLabelChar || c > MaxLabelChar)
            {
                throw new ArgumentException(
                    $"Component label '{label}' contains a character outside the allowed range: labels are ASCII printable (0x21-0x7E) only.",
                    ComponentsParamName);
            }

            if (c == LabelValueSeparator)
            {
                throw new ArgumentException(
                    $"Component label '{label}' contains '{LabelValueSeparator}', which would make the label/value split ambiguous.",
                    ComponentsParamName);
            }
        }

        // Refused, never deduplicated: two values for one label is a caller bug, and silently
        // picking one of them maps two different machines onto one seat.
        if (!seenLabels.Add(label))
        {
            throw new ArgumentException(
                $"Component label '{label}' appears more than once. Duplicate labels are refused rather than deduplicated.",
                ComponentsParamName);
        }
    }

    private static void ValidateValue(string label, string trimmedValue)
    {
        foreach (var c in trimmedValue)
        {
            // Checked AFTER trimming, so ordinary trailing CR/LF from a command's output is
            // absorbed, while an interior control character is refused. Stripping one would map two
            // different inputs onto one seat. The separator itself is caught here: U+001F is a
            // control character.
            if (c <= MaxControlChar || c == DeleteChar)
            {
                throw new ArgumentException(
                    $"Value for component label '{label}' contains an ASCII control character (U+{(int)c:X4}). Control characters are refused, never stripped.",
                    ComponentsParamName);
            }
        }
    }

    private static string TrimAsciiWhitespace(string label, string value)
    {
        if (value is null)
        {
            throw new ArgumentException(
                $"Value for component label '{label}' must not be null. An empty value is legal; a null one is a caller bug.",
                ComponentsParamName);
        }

        return value.Trim(AsciiWhitespace);
    }
}
