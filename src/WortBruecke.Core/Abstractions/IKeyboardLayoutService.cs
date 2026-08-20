namespace WortBruecke.Core.Abstractions;

public sealed record LayoutAvailability(string CultureCode, bool IsInstalled, string DisplayName);

public interface IKeyboardLayoutService
{
    IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes);
    bool SwitchTo(string cultureCode);
}
