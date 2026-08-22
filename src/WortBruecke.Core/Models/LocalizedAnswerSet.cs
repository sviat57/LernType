namespace WortBruecke.Core.Models;

public sealed class LocalizedAnswerSet : Dictionary<string, List<string>>
{
    public LocalizedAnswerSet() : base(StringComparer.OrdinalIgnoreCase)
    {
    }

    public IReadOnlyList<string> For(string cultureCode) =>
        TryGetValue(cultureCode, out var answers)
            ? answers
                .Where(answer => !string.IsNullOrWhiteSpace(answer))
                .Select(answer => answer.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
}
