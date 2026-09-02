using System.Text.Json;
using System.Text.Json.Serialization;
using WortBruecke.Core.Abstractions;
using WortBruecke.Core.Courses;

namespace WortBruecke.Infrastructure.Content;

/// <summary>Loads the bundled A0–C2 course path from strict, offline JSON.</summary>
public sealed class JsonCourseCatalogRepository : ICourseCatalogRepository
{
    private const string CatalogFileName = "courses-a0-a2.json";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly string _contentRoot;

    /// <summary>Creates a repository rooted at the application's immutable content directory.</summary>
    public JsonCourseCatalogRepository(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        _contentRoot = Path.GetFullPath(contentRoot);
    }

    /// <inheritdoc />
    public async Task<CourseCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_contentRoot, CatalogFileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Не найден автономный каталог учебных курсов.", path);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var catalog = await JsonSerializer.DeserializeAsync<CourseCatalog>(
                stream,
                SerializerOptions,
                cancellationToken);
            CourseCatalogValidator.Validate(catalog);
            return catalog!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Каталог курсов содержит недопустимый JSON (путь {exception.Path ?? "<root>"}, " +
                $"строка {exception.LineNumber?.ToString() ?? "?"}, позиция {exception.BytePositionInLine?.ToString() ?? "?"}).",
                exception);
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
}
