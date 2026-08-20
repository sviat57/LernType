namespace WortBruecke.Core.Models;

/// <summary>
/// Describes how much language material a practice step contains.
/// Difficulty (for example, CEFR) and translation direction are separate axes.
/// </summary>
public enum PracticeUnit
{
    Word,
    Sentence,
    Text
}

/// <summary>
/// Describes a direction relative to a configured <see cref="LanguagePair"/>.
/// </summary>
public enum TranslationDirection
{
    SourceToTarget,
    TargetToSource
}
