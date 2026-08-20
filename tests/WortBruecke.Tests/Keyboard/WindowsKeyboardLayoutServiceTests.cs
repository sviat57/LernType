using WortBruecke.Infrastructure.Keyboard;

namespace WortBruecke.Tests.Keyboard;

public sealed class WindowsKeyboardLayoutServiceTests
{
    [Fact]
    public void CheckInstalled_ReportsEachRequestedCultureWithoutChangingLayout()
    {
        var platform = new FakePlatform(
            new KeyboardLayoutDescriptor("ru-RU", "Russian", 1),
            new KeyboardLayoutDescriptor("de-DE", "German", 2));
        var service = new WindowsKeyboardLayoutService(platform);

        var availability = service.CheckInstalled("RU-ru", "de-DE", "en-US");

        Assert.Collection(
            availability,
            item => Assert.True(item.IsInstalled),
            item => Assert.True(item.IsInstalled),
            item => Assert.False(item.IsInstalled));
        Assert.Empty(platform.Attempts);
    }

    [Fact]
    public void SwitchTo_ReturnsFalseWhenCultureIsNotInstalled()
    {
        var platform = new FakePlatform(new KeyboardLayoutDescriptor("ru-RU", "Russian", 1));

        var switched = new WindowsKeyboardLayoutService(platform).SwitchTo("de-DE");

        Assert.False(switched);
        Assert.Empty(platform.Attempts);
    }

    [Fact]
    public void SwitchTo_UsesManagedPathFirst()
    {
        var platform = new FakePlatform(new KeyboardLayoutDescriptor("de-DE", "German", 2))
        {
            ManagedResult = true,
            NativeResult = true
        };

        var switched = new WindowsKeyboardLayoutService(platform).SwitchTo("de-DE");

        Assert.True(switched);
        Assert.Equal(["managed:de-DE"], platform.Attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SwitchTo_UsesNativeFallbackWhenManagedActivationDoesNotStick(bool nativeResult)
    {
        var platform = new FakePlatform(new KeyboardLayoutDescriptor("de-DE", "German", 2))
        {
            ManagedResult = false,
            NativeResult = nativeResult
        };

        var switched = new WindowsKeyboardLayoutService(platform).SwitchTo("DE-de");

        Assert.Equal(nativeResult, switched);
        Assert.Equal(["managed:de-DE", "native:de-DE"], platform.Attempts);
    }

    private sealed class FakePlatform(params KeyboardLayoutDescriptor[] layouts) : IKeyboardLayoutPlatform
    {
        public bool ManagedResult { get; init; }
        public bool NativeResult { get; init; }
        public List<string> Attempts { get; } = [];

        public IReadOnlyList<KeyboardLayoutDescriptor> GetInstalled() => layouts;

        public bool TryActivateManaged(KeyboardLayoutDescriptor layout)
        {
            Attempts.Add($"managed:{layout.CultureCode}");
            return ManagedResult;
        }

        public bool TryActivateNative(KeyboardLayoutDescriptor layout)
        {
            Attempts.Add($"native:{layout.CultureCode}");
            return NativeResult;
        }
    }
}
