using WortBruecke.Core.Abstractions;

namespace WortBruecke.Infrastructure.Keyboard;

public sealed class WindowsKeyboardLayoutService(IKeyboardLayoutPlatform platform) : IKeyboardLayoutService
{
    public WindowsKeyboardLayoutService() : this(new WindowsKeyboardLayoutPlatform())
    {
    }

    public IReadOnlyList<LayoutAvailability> CheckInstalled(params string[] cultureCodes)
    {
        var installed = platform.GetInstalled();
        return cultureCodes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(code =>
            {
                var layout = Find(installed, code);
                return new LayoutAvailability(code, layout is not null, layout?.DisplayName ?? code);
            })
            .ToList();
    }

    public bool SwitchTo(string cultureCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cultureCode);
        var layout = Find(platform.GetInstalled(), cultureCode);
        if (layout is null)
        {
            return false;
        }

        return platform.TryActivateManaged(layout) || platform.TryActivateNative(layout);
    }

    private static KeyboardLayoutDescriptor? Find(IEnumerable<KeyboardLayoutDescriptor> installed, string cultureCode) =>
        installed.FirstOrDefault(layout =>
            string.Equals(layout.CultureCode, cultureCode, StringComparison.OrdinalIgnoreCase));
}
