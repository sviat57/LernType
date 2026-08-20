using WortBruecke.Infrastructure.Images;

namespace WortBruecke.Tests.Images;

public sealed class LocalImageProviderTests : IDisposable
{
    private readonly string _parent = Path.Combine(
        Path.GetTempPath(),
        "WortBrueckeImageProviderTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_AcceptsExistingFileInsideApplicationRoot()
    {
        var root = Path.Combine(_parent, "app");
        var imagePath = Path.Combine(root, "Content", "inside.png");
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
        File.WriteAllBytes(imagePath, [0x89, 0x50, 0x4E, 0x47]);

        var resolved = new LocalImageProvider(root).Resolve("Content/inside.png");

        Assert.Equal(Path.GetFullPath(imagePath), resolved);
    }

    [Fact]
    public void Resolve_RejectsSiblingDirectoryThatSharesRootPrefix()
    {
        var root = Path.Combine(_parent, "app");
        var siblingPath = Path.Combine(_parent, "app-escape", "outside.png");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.GetDirectoryName(siblingPath)!);
        File.WriteAllBytes(siblingPath, [0x89, 0x50, 0x4E, 0x47]);

        var resolved = new LocalImageProvider(root).Resolve("../app-escape/outside.png");

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_ReturnsNullForMalformedPath()
    {
        var root = Path.Combine(_parent, "app");
        Directory.CreateDirectory(root);

        var resolved = new LocalImageProvider(root).Resolve("bad\0path.png");

        Assert.Null(resolved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent))
        {
            Directory.Delete(_parent, true);
        }
    }
}
