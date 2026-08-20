using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WortBruecke.App.Infrastructure;

/// <summary>
/// Applies the documented Windows 11 system backdrop to the top-level HWND.
/// Interior glass surfaces remain regular WPF brushes, so Windows 10 keeps a
/// deterministic, hardware-accelerated fallback without per-element blur.
/// </summary>
public static class WindowBackdropService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmSystemBackdropNone = 1;
    private const int DwmSystemBackdropMainWindow = 2;

    public static void Apply(Window window, bool useDarkTheme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (SystemParameters.HighContrast)
        {
            RestoreFallback(window, handle);
            return;
        }

        TrySetAttribute(handle, DwmwaUseImmersiveDarkMode, useDarkTheme ? 1 : 0);

        // DWMWA_SYSTEMBACKDROP_TYPE is supported from Windows 11, version 22H2.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            window.ClearValue(Window.BackgroundProperty);
            return;
        }

        TrySetAttribute(handle, DwmwaWindowCornerPreference, DwmWindowCornerPreferenceRound);
        if (!TrySetAttribute(handle, DwmwaSystemBackdropType, DwmSystemBackdropMainWindow))
        {
            RestoreFallback(window, handle);
            return;
        }

        var fullClientFrame = new Margins(-1);
        if (DwmExtendFrameIntoClientArea(handle, ref fullClientFrame) != 0)
        {
            RestoreFallback(window, handle);
            return;
        }

        // AllowsTransparency intentionally remains false. A transparent WPF
        // background only reveals the compositor-owned system backdrop.
        window.Background = Brushes.Transparent;
    }

    private static void RestoreFallback(Window window, IntPtr handle)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            TrySetAttribute(handle, DwmwaSystemBackdropType, DwmSystemBackdropNone);
            var clientFrame = new Margins(0);
            _ = DwmExtendFrameIntoClientArea(handle, ref clientFrame);
        }

        window.ClearValue(Window.BackgroundProperty);
    }

    private static bool TrySetAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr windowHandle, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Margins
    {
        private readonly int leftWidth;
        private readonly int rightWidth;
        private readonly int topHeight;
        private readonly int bottomHeight;

        public Margins(int value)
        {
            leftWidth = value;
            rightWidth = value;
            topHeight = value;
            bottomHeight = value;
        }
    }
}
