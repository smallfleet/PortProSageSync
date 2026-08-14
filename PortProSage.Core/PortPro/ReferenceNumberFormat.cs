using System.Globalization;
using System.Text.RegularExpressions;

namespace PortProSage.Core.PortPro;

/// <summary>
/// Splits a PortPro invoice reference number into a leading text prefix, a
/// zero-padded numeric sequence, and a trailing text suffix - e.g.
/// "RSRE_000284" -> prefix="RSRE_", number=284, width=6, suffix="", and
/// "RSRE_000198-1" -> prefix="RSRE_", number=198, width=6, suffix="-1" (the
/// "-1" sub-bill/revision marker seen on real RS Rush invoices). Built
/// deliberately generic - not hardcoded to "RSRE_" - since a future client's
/// reference numbers will use a different prefix, and possibly no prefix at
/// all (e.g. a bare padded number).
///
/// Matches the FIRST digit run in the string, not the last - confirmed
/// against real data this is the correct choice for the "-1" suffix case
/// above (the last digit run there is the "1" in "-1", which is NOT the
/// invoice's actual sequence number). This assumes a single embedded numeric
/// run (prefix, ONE number, optional suffix) - a client whose reference
/// numbers embed more than one number (e.g. a year AND a sequence) would need
/// a smarter parser than this one.
/// </summary>
public static class ReferenceNumberFormat
{
    private static readonly Regex Pattern = new(@"^(?<prefix>\D*)(?<digits>\d+)(?<suffix>\D.*)?$", RegexOptions.Compiled);

    public static bool TryParse(string? referenceNumber, out string prefix, out int number, out int width, out string suffix)
    {
        prefix = string.Empty;
        suffix = string.Empty;
        number = 0;
        width = 0;

        if (string.IsNullOrWhiteSpace(referenceNumber)) return false;

        var match = Pattern.Match(referenceNumber.Trim());
        if (!match.Success) return false;

        prefix = match.Groups["prefix"].Value;
        var digits = match.Groups["digits"].Value;
        suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value : string.Empty;
        width = digits.Length;

        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Rebuilds a reference number from its parts, zero-padding the number
    /// to the given width - the inverse of TryParse.</summary>
    public static string Format(string prefix, int number, int width, string suffix)
        => prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') + suffix;
}
