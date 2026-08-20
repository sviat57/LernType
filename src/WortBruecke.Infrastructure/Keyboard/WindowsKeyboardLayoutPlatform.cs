using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WortBruecke.Infrastructure.Keyboard;

public sealed class WindowsKeyboardLayoutPlatform : IKeyboardLayoutPlatform
{
    private const uint KlfSetForProcess = 0x00000100;
    private const uint WmInputLangChangeRequest = 0x0050;

    public IReadOnlyList<KeyboardLayoutDescriptor> GetInstalled() =>
        InputLanguage.InstalledInputLanguages
            .Cast<InputLanguage>()
            .Select(language => new KeyboardLayoutDescriptor(
                language.Culture.Name,
                language.LayoutName,
                language.Handle))
            .GroupBy(x => x.CultureCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    public bool TryActivateManaged(KeyboardLayoutDescriptor layout)
    {
        var target = InputLanguage.InstalledInputLanguages
            .Cast<InputLanguage>()
            .FirstOrDefault(language => language.Handle == layout.NativeHandle);
        if (target is null)
        {
            return false;
        }

        try
        {
            InputLanguage.CurrentInputLanguage = target;
            return InputLanguage.CurrentInputLanguage.Handle == target.Handle;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryActivateNative(KeyboardLayoutDescriptor layout)
    {
        var activated = ActivateKeyboardLayout(layout.NativeHandle, KlfSetForProcess) != nint.Zero;
        var foregroundWindow = GetForegroundWindow();
        var requested = foregroundWindow != nint.Zero &&
                        PostMessage(foregroundWindow, WmInputLangChangeRequest, nint.Zero, layout.NativeHandle);
        return activated || requested;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint ActivateKeyboardLayout(nint keyboardLayout, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint windowHandle, uint message, nint wParam, nint lParam);
}
