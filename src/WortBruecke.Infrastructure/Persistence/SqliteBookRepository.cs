using System.Text.Json;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Learning;
using WortBruecke.Core.Models;
using WortBruecke.Core.Training;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteBookRepository(
    SqliteDatabase database,
    IManagedBackupService? managedBackupService = null) : IBookRepository
{
    public async Task<UserBook> SaveAsync(
        string title,
        string sourceCulture,
        string rawText,
        IReadOnlyList<ExtractedVocabularyItem> vocabulary,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCulture);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawText);
        ArgumentNullException.ThrowIfNull(vocabulary);
        var normalizedTitle = title.Trim();
        var normalizedSourceCulture = sourceCulture.Trim();
        if (normalizedTitle.Length > 200)
        {
            throw new ArgumentException("Название книги не должно превышать 200 символов.", nameof(title));
        }
        if (rawText.Length > BookVocabularyExtractor.MaximumTextLength)
        {
            throw new ArgumentException($"Текст длиннее допустимых {BookVocabularyExtractor.MaximumTextLength:N0} символов.", nameof(rawText));
        }
        var createdUtc = DateTimeOffset.UtcNow;
        var savedVocabulary = new List<ExtractedVocabularyItem>(vocabulary.Count);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bookId = await FindExistingBookAsync(
            connection,
            (SqliteTransaction)transaction,
            normalizedTitle,
            normalizedSourceCulture,
            rawText,
            cancellationToken);
        var existingWordIds = bookId is null
            ? new Dictionary<string, Queue<long>>(StringComparer.Ordinal)
            : await LoadExistingWordIdsAsync(connection, (SqliteTransaction)transaction, bookId.Value, cancellationToken);
        if (bookId is null)
        {
            await using var insertBook = connection.CreateCommand();
            insertBook.Transaction = (SqliteTransaction)transaction;
            insertBook.CommandText = """
                INSERT INTO user_books(title, source_culture, raw_text, created_utc)
                VALUES($title, $culture, $text, $created);
                SELECT last_insert_rowid();
                """;
            insertBook.Parameters.AddWithValue("$title", normalizedTitle);
            insertBook.Parameters.AddWithValue("$culture", normalizedSourceCulture);
            insertBook.Parameters.AddWithValue("$text", rawText);
            insertBook.Parameters.AddWithValue("$created", createdUtc.ToString("O"));
            bookId = (long)(await insertBook.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        else
        {
            await using var refreshBook = connection.CreateCommand();
            refreshBook.Transaction = (SqliteTransaction)transaction;
            refreshBook.CommandText = "UPDATE user_books SET created_utc = $created WHERE id = $book;";
            refreshBook.Parameters.AddWithValue("$created", createdUtc.ToString("O"));
            refreshBook.Parameters.AddWithValue("$book", bookId.Value);
            await refreshBook.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in vocabulary)
        {
            var wordId = TryTakeExistingWordId(existingWordIds, item.Source, out var existingId)
                ? existingId
                : 0L;
            await using var wordCommand = connection.CreateCommand();
            wordCommand.Transaction = (SqliteTransaction)transaction;
            if (wordId > 0)
            {
                wordCommand.CommandText = """
                    UPDATE user_book_words SET
                        translations_json = $translations,
                        frequency = $frequency,
                        context_text = $context,
                        part_of_speech = $pos
                    WHERE id = $word;
                    """;
                wordCommand.Parameters.AddWithValue("$word", wordId);
            }
            else
            {
                wordCommand.CommandText = """
                    INSERT INTO user_book_words(book_id, source_text, translations_json, frequency, context_text, part_of_speech)
                    VALUES($book, $source, $translations, $frequency, $context, $pos);
                    SELECT last_insert_rowid();
                    """;
                wordCommand.Parameters.AddWithValue("$book", bookId.Value);
                wordCommand.Parameters.AddWithValue("$source", item.Source);
            }
            wordCommand.Parameters.AddWithValue("$translations", JsonSerializer.Serialize(item.Translations));
            wordCommand.Parameters.AddWithValue("$frequency", item.Frequency);
            wordCommand.Parameters.AddWithValue("$context", item.Context);
            wordCommand.Parameters.AddWithValue("$pos", item.PartOfSpeech);
            var result = await wordCommand.ExecuteScalarAsync(cancellationToken);
            if (wordId == 0)
            {
                wordId = (long)(result ?? 0L);
            }
            savedVocabulary.Add(item with { Id = wordId });
        }
        await DeleteUnusedWordsAsync(
            connection,
            (SqliteTransaction)transaction,
            bookId.Value,
            existingWordIds,
            vocabulary
                .Select(item => LearningContentKey.ForBookWord(bookId.Value, item.Source))
                .ToHashSet(StringComparer.Ordinal),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserBook(bookId.Value, normalizedTitle, normalizedSourceCulture, rawText, createdUtc, savedVocabulary);
    }

    public async Task<UserBook?> GetAsync(long bookId, CancellationToken cancellationToken = default)
    {
        if (bookId <= 0)
        {
            return null;
        }

        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        UserBook? book;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, title, source_culture, raw_text, created_utc FROM user_books WHERE id = $book;";
            command.Parameters.AddWithValue("$book", bookId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            book = new UserBook(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                []);
        }

        return book with { Vocabulary = await LoadVocabularyAsync(connection, bookId, cancellationToken) };
    }

    public async Task<IReadOnlyList<UserBookSummary>> GetRecentSummariesAsync(
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var books = new List<UserBookSummary>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.id, b.title, b.source_culture, b.created_utc, length(b.raw_text),
                   COALESCE(w.word_count, 0)
            FROM user_books b
            LEFT JOIN (
                SELECT book_id, count(*) AS word_count
                FROM user_book_words
                GROUP BY book_id
            ) w ON w.book_id = b.id
            ORDER BY b.created_utc DESC, b.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            books.Add(new UserBookSummary(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                reader.GetInt32(4),
                reader.GetInt32(5)));
        }
        return books;
    }

    public async Task<IReadOnlyList<UserBook>> GetRecentAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        var books = new List<UserBook>();
        var byId = new Dictionary<long, int>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH recent AS (
                SELECT id, title, source_culture, raw_text, created_utc
                FROM user_books ORDER BY created_utc DESC, id DESC LIMIT $limit
            )
            SELECT b.id, b.title, b.source_culture, b.raw_text, b.created_utc,
                   w.id, w.source_text, w.translations_json, w.frequency, w.context_text, w.part_of_speech
            FROM recent b
            LEFT JOIN user_book_words w ON w.book_id = b.id
            ORDER BY b.created_utc DESC, b.id DESC, w.frequency DESC, w.source_text;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bookId = reader.GetInt64(0);
            if (!byId.TryGetValue(bookId, out var index))
            {
                index = books.Count;
                byId.Add(bookId, index);
                books.Add(new UserBook(bookId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    DateTimeOffset.Parse(reader.GetString(4)), new List<ExtractedVocabularyItem>()));
            }
            if (!reader.IsDBNull(5))
            {
                ((List<ExtractedVocabularyItem>)books[index].Vocabulary).Add(ReadVocabulary(reader, 5));
            }
        }
        return books;
    }

    public async Task<bool> DeleteAsync(long bookId, CancellationToken cancellationToken = default)
    {
        if (bookId <= 0)
        {
            return false;
        }
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnableSecureDeletionAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bookWordIds = await LoadBookWordIdsAsync(connection, (SqliteTransaction)transaction, bookId, cancellationToken);
        await DeleteProgressAsync(connection, (SqliteTransaction)transaction, "WHERE book_id = $book", bookId, cancellationToken);
        await DeleteBookEvidenceAsync(
            connection,
            (SqliteTransaction)transaction,
            bookId,
            bookWordIds,
            cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM user_books WHERE id = $book;";
        command.Parameters.AddWithValue("$book", bookId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        try
        {
            await CheckpointWalAsync(connection, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            throw new BookPrivacyCleanupException("The book was deleted, but the WAL cleanup is pending.", exception);
        }
        if (managedBackupService is not null)
        {
            await managedBackupService.PurgeBookFromManagedBackupsAsync(bookId, bookWordIds, CancellationToken.None);
        }
        return affected > 0;
    }

    public async Task<int> DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await EnableSecureDeletionAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DeleteProgressAsync(connection, (SqliteTransaction)transaction, string.Empty, null, cancellationToken);
        await DeleteAllBookEvidenceAsync(connection, (SqliteTransaction)transaction, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM user_books;";
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        try
        {
            await using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            await vacuum.ExecuteNonQueryAsync(CancellationToken.None);
            await CheckpointWalAsync(connection, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or SqliteException)
        {
            throw new BookPrivacyCleanupException("The books were deleted, but secure storage cleanup is pending.", exception);
        }
        if (managedBackupService is not null)
        {
            await managedBackupService.PurgeBookDataFromManagedBackupsAsync(CancellationToken.None);
        }
        return affected;
    }

    public async Task ExportAsync(long bookId, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Поток экспорта недоступен для записи.", nameof(destination));
        }
        var book = await GetAsync(bookId, cancellationToken)
            ?? throw new KeyNotFoundException("Сохранённая книга не найдена.");
        var envelope = new
        {
            format = "lerntype-book",
            schemaVersion = 1,
            exportedUtc = DateTimeOffset.UtcNow,
            book = new
            {
                title = book.Title,
                sourceCulture = book.SourceCulture,
                targetCulture = book.SourceCulture.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? "ru-RU" : "de-DE",
                text = book.RawText,
                vocabulary = book.Vocabulary.Select(item => new
                {
                    source = item.Source,
                    translations = item.Translations,
                    frequency = item.Frequency,
                    context = item.Context,
                    partOfSpeech = item.PartOfSpeech
                })
            }
        };
        await JsonSerializer.SerializeAsync(destination, envelope, cancellationToken: cancellationToken);
        await destination.FlushAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ExtractedVocabularyItem>> LoadVocabularyAsync(
        SqliteConnection connection,
        long bookId,
        CancellationToken cancellationToken)
    {
        var vocabulary = new List<ExtractedVocabularyItem>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source_text, translations_json, frequency, context_text, part_of_speech
            FROM user_book_words WHERE book_id = $book ORDER BY frequency DESC, source_text;
            """;
        command.Parameters.AddWithValue("$book", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            vocabulary.Add(ReadVocabulary(reader, 0));
        }
        return vocabulary;
    }

    private static async Task<IReadOnlyList<long>> LoadBookWordIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookId,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM user_book_words WHERE book_id = $book ORDER BY id;";
        command.Parameters.AddWithValue("$book", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static ExtractedVocabularyItem ReadVocabulary(SqliteDataReader reader, int offset) => new(
        reader.GetString(offset + 1),
        JsonSerializer.Deserialize<List<string>>(reader.GetString(offset + 2)) ?? [],
        reader.GetInt32(offset + 3),
        reader.GetString(offset + 4),
        reader.GetString(offset + 5),
        reader.GetInt64(offset));

    private static async Task DeleteProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string wordFilter,
        long? bookId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            DELETE FROM user_progress
            WHERE content_type = 'BookWord'
              AND content_id IN (SELECT id FROM user_book_words {wordFilter});
            """;
        if (bookId is not null)
        {
            command.Parameters.AddWithValue("$book", bookId.Value);
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteBookEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookId,
        IReadOnlyCollection<long> bookWordIds,
        CancellationToken cancellationToken)
    {
        await PopulatePurgeWordIdsAsync(connection, transaction, bookWordIds, cancellationToken);
        await using (var quarantine = connection.CreateCommand())
        {
            quarantine.Transaction = transaction;
            quarantine.CommandText = """
                DELETE FROM legacy_progress_quarantine
                WHERE content_type = 'BookWord'
                  AND legacy_numeric_id IN (SELECT id FROM purge_book_word_ids);
                """;
            await quarantine.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in new[] { "attempt_events", "review_state" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {table}
                WHERE content_key LIKE $bookPrefix
                   OR content_key IN (
                       SELECT 'user.book-word.' || id FROM purge_book_word_ids);
                """;
            command.Parameters.AddWithValue("$bookPrefix", $"user.book.{bookId}.%");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task DeleteAllBookEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var quarantine = connection.CreateCommand())
        {
            quarantine.Transaction = transaction;
            quarantine.CommandText = "DELETE FROM legacy_progress_quarantine WHERE content_type = 'BookWord';";
            await quarantine.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var table in new[] { "attempt_events", "review_state" })
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                DELETE FROM {table}
                WHERE content_key LIKE 'user.book.%'
                   OR content_key LIKE 'user.book-word.%';
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task PopulatePurgeWordIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyCollection<long> bookWordIds,
        CancellationToken cancellationToken)
    {
        await using (var initialize = connection.CreateCommand())
        {
            initialize.Transaction = transaction;
            initialize.CommandText = """
                CREATE TEMP TABLE IF NOT EXISTS purge_book_word_ids(id INTEGER PRIMARY KEY);
                DELETE FROM purge_book_word_ids;
                """;
            await initialize.ExecuteNonQueryAsync(cancellationToken);
        }
        foreach (var wordId in bookWordIds.Distinct())
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR IGNORE INTO purge_book_word_ids(id) VALUES($id);";
            insert.Parameters.AddWithValue("$id", wordId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EnableSecureDeletionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA secure_delete=ON;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (Convert.ToInt32(result) != 1)
        {
            throw new IOException("SQLite secure deletion could not be enabled.");
        }
    }

    private static async Task CheckpointWalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0)
        {
            throw new IOException("SQLite WAL cleanup is temporarily busy.");
        }
    }

    private static async Task<long?> FindExistingBookAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string title,
        string sourceCulture,
        string rawText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id FROM user_books
            WHERE title = $title AND source_culture = $culture AND raw_text = $text
            ORDER BY id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$culture", sourceCulture);
        command.Parameters.AddWithValue("$text", rawText);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long id ? id : null;
    }

    private static async Task<Dictionary<string, Queue<long>>> LoadExistingWordIdsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Queue<long>>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, source_text FROM user_book_words WHERE book_id = $book ORDER BY id;";
        command.Parameters.AddWithValue("$book", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var source = reader.GetString(1);
            if (!result.TryGetValue(source, out var ids))
            {
                ids = new Queue<long>();
                result.Add(source, ids);
            }
            ids.Enqueue(reader.GetInt64(0));
        }
        return result;
    }

    private static bool TryTakeExistingWordId(
        Dictionary<string, Queue<long>> existingWordIds,
        string source,
        out long wordId)
    {
        if (existingWordIds.TryGetValue(source, out var ids) && ids.Count > 0)
        {
            wordId = ids.Dequeue();
            return true;
        }
        wordId = 0;
        return false;
    }

    private static async Task DeleteUnusedWordsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long bookId,
        Dictionary<string, Queue<long>> existingWordIds,
        IReadOnlySet<string> retainedContentKeys,
        CancellationToken cancellationToken)
    {
        foreach (var (source, ids) in existingWordIds)
        {
            var canonicalKey = LearningContentKey.ForBookWord(bookId, source);
            foreach (var wordId in ids)
            {
                await using (var progressCommand = connection.CreateCommand())
                {
                    progressCommand.Transaction = transaction;
                    progressCommand.CommandText = """
                        DELETE FROM user_progress WHERE content_type = 'BookWord' AND content_id = $word;
                        DELETE FROM legacy_progress_quarantine WHERE content_type = 'BookWord' AND legacy_numeric_id = $word;
                        """;
                    progressCommand.Parameters.AddWithValue("$word", wordId);
                    await progressCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                foreach (var table in new[] { "attempt_events", "review_state" })
                {
                    await using var evidenceCommand = connection.CreateCommand();
                    evidenceCommand.Transaction = transaction;
                    evidenceCommand.CommandText = retainedContentKeys.Contains(canonicalKey)
                        ? $"DELETE FROM {table} WHERE content_key = $legacyKey;"
                        : $"DELETE FROM {table} WHERE content_key IN ($legacyKey, $canonicalKey);";
                    evidenceCommand.Parameters.AddWithValue("$legacyKey", $"user.book-word.{wordId}");
                    if (!retainedContentKeys.Contains(canonicalKey))
                    {
                        evidenceCommand.Parameters.AddWithValue("$canonicalKey", canonicalKey);
                    }
                    await evidenceCommand.ExecuteNonQueryAsync(cancellationToken);
                }
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM user_book_words WHERE id = $word;";
                command.Parameters.AddWithValue("$word", wordId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }
}
