using System.Text.Json;
using System.Text.Json.Serialization;
using WortBruecke.Core.Models;

namespace WortBruecke.Infrastructure.Content;

public sealed class JsonContentLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ContentCatalog> LoadAsync(string contentRoot, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(contentRoot, "catalog.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Не найден каталог учебного контента.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ContentCatalog>(stream, SerializerOptions, cancellationToken)
            ?? throw new InvalidDataException("Каталог контента пуст или повреждён.");
    }
}
