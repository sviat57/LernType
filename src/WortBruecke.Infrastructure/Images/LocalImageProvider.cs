using WortBruecke.Core.Abstractions;

namespace WortBruecke.Infrastructure.Images;

public sealed class LocalImageProvider(string applicationRoot) : IImageProvider
{
    public string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(applicationRoot);
            var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var pathFromRoot = Path.GetRelativePath(root, fullPath);
            var escapesRoot = Path.IsPathRooted(pathFromRoot) ||
                              pathFromRoot.Equals("..", StringComparison.Ordinal) ||
                              pathFromRoot.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                              pathFromRoot.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
            return !escapesRoot && File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
