using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WortBruecke.Core.Training;

public static partial class DictionaryKeyNormalizer
{
    public static string Normalize(string value, string cultureCode)
    {
        var normalized = CollapseWhitespace().Replace(value.Trim(), " ")
            .Trim('.', ',', ';', ':', '!', '?', '"', '„', '“', '«', '»', '(', ')', '[', ']')
            .ToLower(CultureInfo.GetCultureInfo(cultureCode))
            .Normalize(NormalizationForm.FormC);
        if (cultureCode.StartsWith("de", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
        }
        else if (cultureCode.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Replace('ё', 'е');
        }
        return normalized;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();
}
