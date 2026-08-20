using Microsoft.Data.Sqlite;

namespace WortBruecke.Tests.Persistence;

public sealed class SqliteRuntimeVersionTests
{
    [Fact]
    public async Task NativeRuntime_IsAtLeastVersionContainingCve20256965Fix()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var versionText = Assert.IsType<string>(await command.ExecuteScalarAsync());
        var version = Version.Parse(versionText);

        Assert.True(
            version >= new Version(3, 50, 2),
            $"SQLite {versionText} is affected by CVE-2025-6965; version 3.50.2 or newer is required.");
    }
}
