namespace WortBruecke.Core.Models;

public sealed class LocalizedText : Dictionary<string, string>
{
    public LocalizedText() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public string For(string cultureCode, string fallback = "") =>
        TryGetValue(cultureCode, out var text) ? text : Values.FirstOrDefault() ?? fallback;
}
