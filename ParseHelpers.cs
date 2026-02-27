using System.Globalization;
using System.Net;
using System.Text;

namespace WebApplication1;

/// <summary>
/// Parsing and normalization helpers used by minimal API handlers.
/// </summary>
public static class ParseHelpers
{
    public static string H(string? s) => WebUtility.HtmlEncode(s) ?? "";

    public static int? IntOrNull(string? s) => int.TryParse(s, out var v) ? v : null;

    public static decimal? DecimalOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) return v;
        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.CurrentCulture, out v)) return v;
        var normalized = s.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out v) ? v : null;
    }

    public static bool? BoolOrNull(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s == "on" || s == "true" || s == "1" || s == "да" || s == "yes";
    }

    public static string NormalizeOpName(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        bool prevSpace = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            var c = ch;
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
        }
        return sb.ToString().Trim();
    }
}
