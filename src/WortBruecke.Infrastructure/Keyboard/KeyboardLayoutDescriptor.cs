namespace WortBruecke.Infrastructure.Keyboard;

public sealed record KeyboardLayoutDescriptor(
    string CultureCode,
    string DisplayName,
    nint NativeHandle);

public interface IKeyboardLayoutPlatform
{
    IReadOnlyList<KeyboardLayoutDescriptor> GetInstalled();
    bool TryActivateManaged(KeyboardLayoutDescriptor layout);
    bool TryActivateNative(KeyboardLayoutDescriptor layout);
}
