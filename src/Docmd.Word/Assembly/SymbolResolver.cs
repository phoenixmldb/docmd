namespace Docmd.Word.Assembly;

using System.Globalization;
using System.Xml.Linq;
using Ooxml.Md.Core.Opc;

/// <summary>
/// Resolves <c>w:sym</c> to the character it stands for, and stamps the answer onto the
/// composite as <c>docmd:char</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>w:sym</c> carries <c>w:char</c>, a code point in the <em>font's own</em> encoding rather
/// than Unicode. Reading it as Unicode is not a small error: in these documents `0027` is a
/// section sign, and read literally it would be an apostrophe.
/// </para>
/// <para>
/// The resolution is stamped onto the composite rather than performed in the stylesheet so
/// there is exactly one table. The defect that prompted this existed because the stylesheet and
/// the coverage oracle each decided independently what counted as text, and quietly agreed about
/// an element neither of them read. A single annotation both then consume cannot drift apart.
/// </para>
/// <para>
/// A font or code point that is not in the table is left alone: no <c>docmd:char</c> is stamped,
/// the stylesheet emits nothing, and the coverage check reports the loss. Emitting a
/// plausible-looking wrong character would be worse, because nothing downstream could tell.
/// </para>
/// </remarks>
public static class SymbolResolver
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly XNamespace Word = W;

    /// <summary>
    /// Adobe's published Symbol encoding. Word writes these as F0xx — the Private Use Area
    /// offset — so both forms are accepted. This table is from the specification, not inferred.
    /// </summary>
    private static readonly Dictionary<int, string> SymbolFont = new()
    {
        [0x22] = "∀", [0x24] = "∃", [0x27] = "∋", [0x2D] = "−",
        [0x40] = "≅", [0x61] = "α", [0x62] = "β", [0x63] = "χ",
        [0x64] = "δ", [0x65] = "ε", [0x66] = "φ", [0x67] = "γ",
        [0x68] = "η", [0x69] = "ι", [0x6C] = "λ", [0x6D] = "μ",
        [0x6E] = "ν", [0x70] = "π", [0x71] = "θ", [0x72] = "ρ",
        [0x73] = "σ", [0x74] = "τ", [0x77] = "ω", [0x78] = "ξ",
        [0x79] = "ψ", [0x7A] = "ζ",
        [0xA3] = "≤", [0xA5] = "∞", [0xB0] = "°", [0xB1] = "±",
        [0xB3] = "≥", [0xB4] = "×", [0xB7] = "•", [0xB8] = "÷",
        [0xB9] = "≠", [0xBB] = "≈", [0xBC] = "…", [0xD7] = "⊗",
        [0xE5] = "∑", [0xF2] = "∫",
    };

    /// <summary>
    /// WordPerfect's "Typographic Symbols" set, as written by the WordPerfect-to-Word
    /// conversion that produced a large corpus of municipal codes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Determined empirically, not from a font specification</strong>, and only entries
    /// that several independent passages agree on are here. Each was read off text where the
    /// intended character is unambiguous from its surroundings:
    /// </para>
    /// <code>
    /// [0027][0027] 15-1[0043]15-25          sections 15-1 to 15-25   -> 0027 is a section sign
    /// Employees[003D] Pension Plan          Employees' Pension Plan  -> 003D is an apostrophe
    /// ([0041]DROP[0040])                    ("DROP")                 -> 0041/0040 are quotes
    /// ARTICLE X [0042] PENSION PLAN         ARTICLE X - PENSION PLAN -> 0042 is a dash
    /// three and one-half ([0032]) inches    three and one-half (3.5) -> 0032 is one half
    /// </code>
    /// <para>
    /// Entries stop where the evidence stops. Twenty-one further occurrences across
    /// "WP MathExtendedA", "WP Phonetic" and three more slots in this set are left unresolved,
    /// because their surroundings do not say what they were: "IPUD[symbol]" could be almost
    /// anything. Those are dropped and reported, which is the honest answer for a character
    /// nobody can identify.
    /// </para>
    /// <para>
    /// The two dash entries are the least certain, and the uncertainty is only about width: both
    /// are unmistakably dashes, and which of en or em was intended cannot be recovered from the
    /// file. An en dash for a range and an em dash for an aside is what the usage shows. Getting
    /// that choice wrong is cosmetic; dropping the character is not, because it turns
    /// "15-1 to 15-25" into "15-115-25".
    /// </para>
    /// </remarks>
    private static readonly Dictionary<int, string> WordPerfectTypographic = new()
    {
        [0x27] = "§",   // section sign
        [0x40] = "”",   // right double quotation mark
        [0x41] = "“",   // left double quotation mark
        [0x42] = "—",   // em dash, used as an aside separator
        [0x43] = "–",   // en dash, used between the ends of a range
        [0x3D] = "’",   // right single quotation mark, used as an apostrophe
        [0x32] = "½",   // one half
    };

    /// <summary>The character a <c>w:sym</c> stands for, or null when it cannot be resolved.</summary>
    public static string? Resolve(string? font, string? charCode)
    {
        if (string.IsNullOrEmpty(font) || string.IsNullOrEmpty(charCode))
        {
            return null;
        }

        if (!int.TryParse(charCode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return null;
        }

        // Word writes symbol-font code points offset into the Private Use Area. Both the
        // offset and bare forms appear in the wild.
        var bare = code is >= 0xF000 and <= 0xF0FF ? code - 0xF000 : code;

        var table = font switch
        {
            "Symbol" => SymbolFont,
            "WP TypographicSymbols" => WordPerfectTypographic,
            _ => null,
        };

        return table is not null && table.TryGetValue(bare, out var resolved) ? resolved : null;
    }

    /// <summary>Stamps <c>docmd:char</c> on every <c>w:sym</c> whose character is known.</summary>
    public static void Annotate(XDocument composite)
    {
        ArgumentNullException.ThrowIfNull(composite);

        var body = composite.Root?.Element(WordNames.Docmd + "body");
        if (body is null)
        {
            return;
        }

        foreach (var sym in body.Descendants(Word + "sym"))
        {
            var resolved = Resolve(
                (string?)sym.Attribute(Word + "font"),
                (string?)sym.Attribute(Word + "char"));

            if (resolved is not null)
            {
                sym.SetAttributeValue(WordNames.Docmd + "char", resolved);
            }
        }
    }
}
