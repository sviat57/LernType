using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WortBruecke.Infrastructure.Persistence;

internal sealed record MigrationJournalEntry(
    string OperationId,
    DataMigrationPhase Phase,
    string SourcePath,
    string DestinationPath,
    string? SourceFingerprint,
    string? BackupPath,
    string? RollbackPath,
    DateTimeOffset RecordedAtUtc,
    string? Detail = null);

internal sealed class MigrationJournal(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public IReadOnlyList<MigrationJournalEntry> ReadAll()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<MigrationJournalEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<MigrationJournalEntry>(line, JsonOptions);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A process can stop between writing a JSON record and its trailing newline.
                // Earlier flushed records remain authoritative and the incomplete tail is ignored.
            }
        }

        return entries;
    }

    public void Append(MigrationJournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(entry, JsonOptions);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        writer.WriteLine(json);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}
