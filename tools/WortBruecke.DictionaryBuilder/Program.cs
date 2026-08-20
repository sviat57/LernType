using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Training;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: DictionaryBuilder <output.sqlite> <rus-deu.tei> <deu-rus.tei>");
    return 2;
}

var outputPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
if (File.Exists(outputPath))
{
    File.Delete(outputPath);
}

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = outputPath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Pooling = false
}.ToString();

await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync();
await using (var schema = connection.CreateCommand())
{
    schema.CommandText = """
        PRAGMA journal_mode=OFF;
        PRAGMA synchronous=OFF;
        CREATE TABLE dictionary_entries (
            id INTEGER PRIMARY KEY,
            source_culture TEXT NOT NULL,
            target_culture TEXT NOT NULL,
            headword TEXT NOT NULL,
            lookup_key TEXT NOT NULL,
            translation TEXT NOT NULL,
            part_of_speech TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ux_dictionary_entry
            ON dictionary_entries(source_culture, target_culture, lookup_key, headword, translation, part_of_speech);
        CREATE INDEX ix_dictionary_lookup
            ON dictionary_entries(source_culture, target_culture, lookup_key);
        CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
        INSERT INTO metadata(key, value) VALUES
            ('source', 'FreeDict+WikDict'),
            ('edition', '2025.11.23'),
            ('license', 'CC BY-SA 3.0 Unported');
        """;
    await schema.ExecuteNonQueryAsync();
}

var ruCount = await ImportAsync(connection, args[1], "ru-RU", "de-DE");
var deCount = await ImportAsync(connection, args[2], "de-DE", "ru-RU");
await using (var optimize = connection.CreateCommand())
{
    optimize.CommandText = "ANALYZE; VACUUM;";
    await optimize.ExecuteNonQueryAsync();
}

Console.WriteLine($"Created {outputPath}");
Console.WriteLine($"RU→DE rows: {ruCount:N0}; DE→RU rows: {deCount:N0}; size: {new FileInfo(outputPath).Length:N0} bytes");
return 0;

static async Task<int> ImportAsync(SqliteConnection connection, string sourcePath, string sourceCulture, string targetCulture)
{
    var settings = new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreWhitespace = true
    };
    var xmlNamespace = (XNamespace)"http://www.tei-c.org/ns/1.0";
    await using var transaction = await connection.BeginTransactionAsync();
    await using var insert = connection.CreateCommand();
    insert.Transaction = (SqliteTransaction)transaction;
    insert.CommandText = """
        INSERT OR IGNORE INTO dictionary_entries(
            source_culture, target_culture, headword, lookup_key, translation, part_of_speech)
        VALUES($source, $target, $headword, $key, $translation, $pos);
        """;
    insert.Parameters.AddWithValue("$source", sourceCulture);
    insert.Parameters.AddWithValue("$target", targetCulture);
    var headwordParameter = insert.Parameters.Add("$headword", SqliteType.Text);
    var keyParameter = insert.Parameters.Add("$key", SqliteType.Text);
    var translationParameter = insert.Parameters.Add("$translation", SqliteType.Text);
    var partOfSpeechParameter = insert.Parameters.Add("$pos", SqliteType.Text);

    var inserted = 0;
    using var reader = XmlReader.Create(Path.GetFullPath(sourcePath), settings);
    while (reader.Read())
    {
        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "entry")
        {
            continue;
        }

        using var subtree = reader.ReadSubtree();
        var entry = XElement.Load(subtree);
        var headword = Clean(entry.Descendants(xmlNamespace + "orth").FirstOrDefault()?.Value);
        if (!IsUseful(headword, 80))
        {
            continue;
        }
        var lookupKey = DictionaryKeyNormalizer.Normalize(headword, sourceCulture);
        var partOfSpeech = Clean(entry.Descendants(xmlNamespace + "pos").FirstOrDefault()?.Value);
        foreach (var translation in entry.Descendants(xmlNamespace + "cit")
                     .Where(element => string.Equals((string?)element.Attribute("type"), "trans", StringComparison.Ordinal))
                     .SelectMany(element => element.Descendants(xmlNamespace + "quote"))
                     .Select(element => Clean(element.Value))
                     .Where(value => IsUseful(value, 160))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            headwordParameter.Value = headword;
            keyParameter.Value = lookupKey;
            translationParameter.Value = translation;
            partOfSpeechParameter.Value = partOfSpeech;
            inserted += await insert.ExecuteNonQueryAsync();
        }
    }
    await transaction.CommitAsync();
    return inserted;
}

static string Clean(string? value) => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();

static bool IsUseful(string value, int maximumLength) =>
    value.Length is >= 1 && value.Length <= maximumLength && value.Any(char.IsLetter);
