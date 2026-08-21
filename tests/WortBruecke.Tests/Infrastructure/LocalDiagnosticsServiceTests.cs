using WortBruecke.App.Infrastructure;
using WortBruecke.Infrastructure.Paths;

namespace WortBruecke.Tests.Infrastructure;

public sealed class LocalDiagnosticsServiceTests
{
    [Fact]
    public void Write_RecordsOnlyTechnicalShapeWithoutExceptionMessageOrUserContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "LernType.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var diagnostics = new LocalDiagnosticsService(new AppPaths(dataRoot: root));
            diagnostics.Write("book.export.failed", new IOException("SECRET BOOK TEXT at C:\\Users\\person\\private.txt"));

            var record = File.ReadAllText(diagnostics.LogPath);
            Assert.Contains("book.export.failed", record, StringComparison.Ordinal);
            Assert.Contains("System.IO.IOException", record, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET BOOK TEXT", record, StringComparison.Ordinal);
            Assert.DoesNotContain("private.txt", record, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
