using WortBruecke.Infrastructure.Content;
using WortBruecke.Infrastructure.Paths;
using WortBruecke.Infrastructure.Persistence;
using WortBruecke.Core.Models;

namespace WortBruecke.Tests.Persistence;

public sealed class ContentImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WortBrueckeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Initialize_ImportsGenericTranslationsIntoSqlite()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await File.WriteAllTextAsync(Path.Combine(contentRoot, "catalog.json"), """
            {
              "revision": 1,
              "themes": [{ "id": 1, "key": "food", "iconKey": "food", "names": { "ru-RU": "Еда", "de-DE": "Essen" } }],
              "words": [{ "id": 1, "themeId": 1, "imagePath": "Assets/Images/food/1.png", "level": "A1", "partOfSpeech": "noun", "translations": { "ru-RU": "яблоко", "de-DE": "der Apfel" }, "examples": {} }],
              "passages": [],
              "grammarTasks": []
            }
            """);

        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        var repository = new SqliteContentRepository(database);

        var words = await repository.GetWordsAsync();

        var word = Assert.Single(words);
        Assert.Equal("яблоко", word.Translations.For("ru-RU"));
        Assert.Equal("der Apfel", word.Translations.For("de-DE"));
    }

    [Fact]
    public async Task Initialize_ImportsSentencesAndPreservesUserBooksAcrossCatalogRevision()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        var catalogPath = Path.Combine(contentRoot, "catalog.json");
        await WriteCatalogAsync(catalogPath, 1, "Я вижу дом.", "Ich sehe das Haus.");
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();

        var content = new SqliteContentRepository(database);
        var sentence = Assert.Single(await content.GetSentencesAsync());
        Assert.Equal("Ich sehe das Haus.", sentence.Translations.For("de-DE"));

        var books = new SqliteBookRepository(database);
        var createdBook = await books.SaveAsync("Одиссея", "ru-RU", "Море было тёмным.",
        [
            new ExtractedVocabularyItem("море", ["das Meer"], 1, "Море было тёмным.", "noun")
        ]);
        var createdWordId = Assert.Single(createdBook.Vocabulary).Id;
        Assert.True(createdWordId > 0);

        await WriteCatalogAsync(catalogPath, 2, "Это новый дом.", "Das ist ein neues Haus.");
        await database.InitializeAsync();

        var importedSentence = Assert.Single(await content.GetSentencesAsync());
        Assert.Equal("Das ist ein neues Haus.", importedSentence.Translations.For("de-DE"));
        var savedBook = Assert.Single(await books.GetRecentAsync());
        Assert.Equal("Одиссея", savedBook.Title);
        var savedWord = Assert.Single(savedBook.Vocabulary);
        Assert.Equal(createdWordId, savedWord.Id);
        Assert.Equal("das Meer", savedWord.Translations[0]);

        var refreshedBook = await books.SaveAsync("Одиссея", "ru-RU", "Море было тёмным.",
        [
            new ExtractedVocabularyItem("море", ["das Meer", "die See"], 2, "Море было тёмным.", "noun")
        ]);
        Assert.Equal(savedBook.Id, refreshedBook.Id);
        Assert.Equal(createdWordId, Assert.Single(refreshedBook.Vocabulary).Id);
        var onlyBook = Assert.Single(await books.GetRecentAsync());
        var refreshedWord = Assert.Single(onlyBook.Vocabulary);
        Assert.Equal(createdWordId, refreshedWord.Id);
        Assert.Equal(2, refreshedWord.Translations.Count);
        Assert.Equal(2, refreshedWord.Frequency);
    }

    [Fact]
    public async Task ProgressRepository_Preserves64BitBookWordIdentifiers()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await WriteCatalogAsync(Path.Combine(contentRoot, "catalog.json"), 1, "Я вижу дом.", "Ich sehe das Haus.");
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        var progress = new SqliteProgressRepository(database);
        var contentId = (long)int.MaxValue + 42;

        await progress.RecordAttemptAsync(ContentType.BookWord, contentId, true);
        var saved = await progress.GetAsync(ContentType.BookWord, contentId);

        Assert.NotNull(saved);
        Assert.Equal(contentId, saved.ContentId);
        Assert.Equal(1, saved.AttemptCount);
        Assert.Equal(1, saved.CorrectCount);
    }

    [Fact]
    public async Task ProgressRepository_GetAllReturnsEveryKnownRecordAndSkipsUnknownTypes()
    {
        var contentRoot = Path.Combine(_root, "Content");
        Directory.CreateDirectory(contentRoot);
        await WriteCatalogAsync(Path.Combine(contentRoot, "catalog.json"), 1, "Я вижу дом.", "Ich sehe das Haus.");
        var paths = new AppPaths(contentRoot, Path.Combine(_root, "Data"));
        var database = new SqliteDatabase(paths, new JsonContentLoader());
        await database.InitializeAsync();
        var progress = new SqliteProgressRepository(database);

        await progress.RecordAttemptAsync(ContentType.Word, 1, true);
        await progress.RecordAttemptAsync(ContentType.Sentence, 7, false);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO user_progress(content_type, content_id, attempt_count, correct_count, last_attempt_utc)
                VALUES('FutureContentType', 99, 1, 1, '2026-08-20T00:00:00.0000000+00:00');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var records = await progress.GetAllAsync();

        Assert.Equal(2, records.Count);
        Assert.Contains(records, item => item.ContentType == ContentType.Word && item.ContentId == 1 && item.Accuracy == 1);
        Assert.Contains(records, item => item.ContentType == ContentType.Sentence && item.ContentId == 7 && item.Accuracy == 0);
    }

    private static Task WriteCatalogAsync(string path, int revision, string russianSentence, string germanSentence) =>
        File.WriteAllTextAsync(path, $$"""
            {
              "revision": {{revision}},
              "themes": [{ "id": 1, "key": "home", "iconKey": "home", "names": { "ru-RU": "Дом", "de-DE": "Haus" } }],
              "words": [{ "id": 1, "themeId": 1, "imagePath": "Assets/Images/home/1.png", "level": "A1", "partOfSpeech": "noun", "translations": { "ru-RU": "дом", "de-DE": "das Haus" }, "examples": {} }],
              "sentences": [{ "id": 1, "themeId": 1, "level": "A1", "translations": { "ru-RU": "{{russianSentence}}", "de-DE": "{{germanSentence}}" } }],
              "passages": [],
              "grammarTasks": []
            }
            """);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }
}
