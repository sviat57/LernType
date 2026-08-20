using System.Text.Json;
using Microsoft.Data.Sqlite;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Persistence;

public sealed class SqliteBookRepository(SqliteDatabase database) : IBookRepository
{
    public async Task<UserBook> SaveAsync(
        string title,
        string sourceCulture,
        string rawText,
        IReadOnlyList<ExtractedVocabularyItem> vocabulary,
        CancellationToken cancellationToken = default)
    {
        var normalizedTitle = title.Trim();
        var createdUtc = DateTimeOffset.UtcNow;
        var savedVocabulary = new List<ExtractedVocabularyItem>(vocabulary.Count);
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var bookId = await FindExistingBookAsync(
            connection,
            (SqliteTransaction)transaction,
            normalizedTitle,
            sourceCulture,
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
            insertBook.Parameters.AddWithValue("$culture", sourceCulture);
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
        await DeleteUnusedWordsAsync(connection, (SqliteTransaction)transaction, existingWordIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UserBook(bookId.Value, normalizedTitle, sourceCulture, rawText, createdUtc, savedVocabulary);
    }

    public async Task<IReadOnlyList<UserBook>> GetRecentAsync(int limit = 10, CancellationToken cancellationToken = default)
    {
        var books = new List<UserBook>();
        await using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, source_culture, raw_text, created_utc
            FROM user_books ORDER BY created_utc DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            books.Add(new UserBook(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)), []));
        }
        await reader.DisposeAsync();

        for (var index = 0; index < books.Count; index++)
        {
            var vocabulary = new List<ExtractedVocabularyItem>();
            await using var wordCommand = connection.CreateCommand();
            wordCommand.CommandText = """
                SELECT id, source_text, translations_json, frequency, context_text, part_of_speech
                FROM user_book_words WHERE book_id = $book ORDER BY frequency DESC, source_text;
                """;
            wordCommand.Parameters.AddWithValue("$book", books[index].Id);
            await using var wordReader = await wordCommand.ExecuteReaderAsync(cancellationToken);
            while (await wordReader.ReadAsync(cancellationToken))
            {
                vocabulary.Add(new ExtractedVocabularyItem(
                    wordReader.GetString(1),
                    JsonSerializer.Deserialize<List<string>>(wordReader.GetString(2)) ?? [],
                    wordReader.GetInt32(3),
                    wordReader.GetString(4),
                    wordReader.GetString(5),
                    wordReader.GetInt64(0)));
            }
            books[index] = books[index] with { Vocabulary = vocabulary };
        }
        return books;
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
        Dictionary<string, Queue<long>> existingWordIds,
        CancellationToken cancellationToken)
    {
        foreach (var wordId in existingWordIds.Values.SelectMany(ids => ids))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM user_book_words WHERE id = $word;";
            command.Parameters.AddWithValue("$word", wordId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
