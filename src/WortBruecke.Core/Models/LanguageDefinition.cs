namespace WortBruecke.Core.Models;

public sealed record LanguageDefinition(string CultureCode, string DisplayName, string ShortCode);

public sealed record LanguagePair(LanguageDefinition Source, LanguageDefinition Target)
{
    public static LanguagePair RussianToGerman { get; } = new(
        new LanguageDefinition("ru-RU", "Русский", "RU"),
        new LanguageDefinition("de-DE", "Deutsch", "DE"));
}
