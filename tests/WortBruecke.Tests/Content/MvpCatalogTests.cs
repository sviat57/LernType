using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WortBruecke.Core.Models;
using WortBruecke.Infrastructure.Content;

namespace WortBruecke.Tests.Content;

public sealed class MvpCatalogTests
{
    [Fact]
    public async Task Catalog_ContainsRunnableContentForEveryA0ToC2StageAndResolvableImages()
    {
        var solutionRoot = FindSolutionRoot();
        var appRoot = Path.Combine(solutionRoot, "src", "WortBruecke.App");
        var catalog = await new JsonContentLoader().LoadAsync(Path.Combine(appRoot, "Content"));

        Assert.Equal(5, catalog.Revision);
        Assert.Equal(10, catalog.Themes.Count);
        Assert.InRange(catalog.Words.Count, 150, 250);
        Assert.InRange(catalog.Sentences.Count, 70, 140);
        Assert.Equal(35, catalog.Passages.Count);
        Assert.InRange(catalog.GrammarTasks.Count, 7, 24);

        foreach (var theme in catalog.Themes)
        {
            var themeWords = catalog.Words.Count(word => word.ThemeId == theme.Id);
            Assert.InRange(themeWords, 15, 25);
        }

        foreach (var word in catalog.Words)
        {
            Assert.False(string.IsNullOrWhiteSpace(word.Translations.For("ru-RU")));
            Assert.False(string.IsNullOrWhiteSpace(word.Translations.For("de-DE")));
            Assert.True(File.Exists(Path.Combine(appRoot, word.ImagePath)), $"Missing image: {word.ImagePath}");
        }

        foreach (var sentence in catalog.Sentences)
        {
            Assert.Contains(sentence.Level, new[] { "A0", "A1", "A2", "B1", "B2", "C1", "C2" });
            Assert.Contains(catalog.Themes, theme => theme.Id == sentence.ThemeId);
            Assert.False(string.IsNullOrWhiteSpace(sentence.Translations.For("ru-RU")));
            Assert.False(string.IsNullOrWhiteSpace(sentence.Translations.For("de-DE")));
        }

        var completePath = new[] { "A0", "A1", "A2", "B1", "B2", "C1", "C2" };
        Assert.Equal(completePath, catalog.Sentences.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(completePath, catalog.Passages.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(completePath, catalog.GrammarTasks.Select(item => item.Level).Distinct().Order().ToArray());
        Assert.Equal(20, catalog.Words.Count(item => item.Level == "A0"));
        Assert.Equal(["речка"], Assert.Single(catalog.Words, item => item.Id == 604).AcceptedAnswers.For("ru-RU"));

        foreach (var level in completePath)
        {
            Assert.Equal(5, catalog.Passages.Count(passage => passage.Level == level));
        }
    }

    [Fact]
    public async Task PassagePack_HasStableLegacyContentAndValidatedBilingualStructure()
    {
        var appRoot = Path.Combine(FindSolutionRoot(), "src", "WortBruecke.App");
        var catalog = await new JsonContentLoader().LoadAsync(Path.Combine(appRoot, "Content"));
        var legacyKeys = new[]
        {
            "first_morning", "market_visit", "star_taler", "berlin_morning",
            "sea_journey", "odyssey_echo", "language_and_memory"
        };

        Assert.Equal(
            "73c00f78ddba4aaa5d1ceab456b203f49e9083bf9d7008758ffed4a4e86c6b3b",
            LegacyPassageFingerprint(catalog.Passages, legacyKeys));

        Assert.Equal(catalog.Passages.Count, catalog.Passages.Select(passage => passage.Id).Distinct().Count());
        Assert.Equal(catalog.Passages.Count, catalog.Passages.Select(passage => passage.Key).Distinct(StringComparer.Ordinal).Count());
        var segments = catalog.Passages.SelectMany(passage => passage.Segments).ToArray();
        Assert.Equal(segments.Length, segments.Select(segment => segment.Id).Distinct().Count());

        foreach (var passage in catalog.Passages)
        {
            Assert.Equal(Enumerable.Range(1, passage.Segments.Count), passage.Segments.Select(segment => segment.Order));
            AssertLocalizedText(passage.Titles, passage.Key);
            foreach (var segment in passage.Segments)
            {
                AssertLocalizedText(segment.Translations, $"{passage.Key}/{segment.Order}");
            }
        }

        var fullGermanTexts = catalog.Passages
            .Select(passage => string.Join(' ', passage.Segments.Select(segment => segment.Translations.For("de-DE"))))
            .ToArray();
        var fullRussianTexts = catalog.Passages
            .Select(passage => string.Join(' ', passage.Segments.Select(segment => segment.Translations.For("ru-RU"))))
            .ToArray();
        Assert.Equal(fullGermanTexts.Length, fullGermanTexts.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(fullRussianTexts.Length, fullRussianTexts.Distinct(StringComparer.Ordinal).Count());

        var lengthBounds = new Dictionary<string, (int Minimum, int Maximum)>
        {
            ["A0"] = (20, 35),
            ["A1"] = (40, 60),
            ["A2"] = (60, 90),
            ["B1"] = (90, 120),
            ["B2"] = (120, 160),
            ["C1"] = (150, 200),
            ["C2"] = (180, 240)
        };
        foreach (var passage in catalog.Passages.Where(passage => passage.Id >= 101))
        {
            var germanText = string.Join(' ', passage.Segments.Select(segment => segment.Translations.For("de-DE")));
            var wordCount = Regex.Matches(germanText, @"[\p{L}]+(?:[-’'][\p{L}]+)*").Count;
            var bounds = lengthBounds[passage.Level];
            Assert.InRange(wordCount, bounds.Minimum, bounds.Maximum);
        }
    }

    [Fact]
    public async Task PassagePack_UsesPlannedTitlesAndCompleteProvenance()
    {
        var appRoot = Path.Combine(FindSolutionRoot(), "src", "WortBruecke.App");
        var contentRoot = Path.Combine(appRoot, "Content");
        var catalog = await new JsonContentLoader().LoadAsync(contentRoot);
        var expectedTitles = new Dictionary<string, string[]>
        {
            ["A0"] = ["Первое утро", "Семья", "Комната", "Завтрак", "Дорога в школу"],
            ["A1"] = ["На рынке", "Семейный визит", "Городской автобус", "Рабочий день", "Выходные"],
            ["A2"] = ["Звёздные талеры", "Квартира", "У врача", "Поезд", "Языковой курс"],
            ["B1"] = ["Утро в Берлине", "Новая работа", "Помощь соседям", "Неделя без соцсетей", "Трудное решение"],
            ["B2"] = ["Путь через море", "Город без машин", "Удалённая работа", "Культурное недоразумение", "Городские деревья"],
            ["C1"] = ["Эхо возвращения", "Язык и принадлежность", "Владение данными", "Память", "Литература"],
            ["C2"] = ["Язык и память", "Невысказанное", "Историография", "Теория перевода", "Прогресс и ответственность"]
        };
        foreach (var (level, titles) in expectedTitles)
        {
            Assert.Equal(titles, catalog.Passages.Where(passage => passage.Level == level).Select(passage => passage.Titles.For("ru-RU")));
        }

        await using var stream = File.OpenRead(Path.Combine(contentRoot, "passage-provenance.json"));
        using var provenance = await JsonDocument.ParseAsync(stream);
        var root = provenance.RootElement;
        Assert.Equal(5, root.GetProperty("catalogRevision").GetInt32());
        Assert.Contains("not an official CEFR certification", root.GetProperty("cefrStatement").GetString(), StringComparison.Ordinal);
        var records = root.GetProperty("passages").EnumerateArray().ToArray();
        Assert.Equal(35, records.Length);
        Assert.Equal(
            catalog.Passages.Select(passage => passage.Key).Order(StringComparer.Ordinal),
            records.Select(record => record.GetProperty("key").GetString()).Order(StringComparer.Ordinal));
        foreach (var record in records)
        {
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("origin").GetString()));
            Assert.Equal("MIT", record.GetProperty("license").GetString());
            Assert.Equal("pending-independent-review", record.GetProperty("reviewStatus").GetString());
        }
    }

    private static void AssertLocalizedText(LocalizedText text, string identity)
    {
        foreach (var culture in new[] { "ru-RU", "de-DE" })
        {
            var value = text.For(culture);
            Assert.False(string.IsNullOrWhiteSpace(value), $"Missing {culture} text for {identity}.");
            Assert.True(value.IsNormalized(NormalizationForm.FormC), $"Non-NFC {culture} text for {identity}.");
        }
    }

    private static string LegacyPassageFingerprint(IEnumerable<PassageSeed> passages, IEnumerable<string> orderedKeys)
    {
        var byKey = passages.ToDictionary(passage => passage.Key, StringComparer.Ordinal);
        var fields = new List<string>();
        foreach (var key in orderedKeys)
        {
            var passage = byKey[key];
            fields.AddRange([
                passage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                passage.Key,
                passage.Titles.For("ru-RU"),
                passage.Titles.For("de-DE"),
                passage.Kind.ToString(),
                passage.Level,
                passage.Topic
            ]);
            foreach (var segment in passage.Segments)
            {
                fields.AddRange([
                    segment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    segment.Order.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    segment.Translations.For("ru-RU"),
                    segment.Translations.For("de-DE")
                ]);
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', fields)))).ToLowerInvariant();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LernType.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not find the LernType solution root.");
    }
}
