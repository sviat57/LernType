using WortBruecke.Core.Learning;

namespace WortBruecke.Core.Models;

/// <summary>Requests the dedicated study centre for one exact learning level.</summary>
public sealed record LevelStudyRequest(GermanLevel Level);

/// <summary>
/// Independent practice routes exposed by a level centre. Translation routes remain separate so
/// choosing word practice can never silently switch to sentences or passages.
/// </summary>
public enum LevelModuleKind
{
    WordGermanToRussian,
    WordRussianToGerman,
    SentenceGermanToRussian,
    SentenceRussianToGerman,
    Text,
    Grammar,
    Audio
}

/// <summary>Requests one module without losing the level selected by the learner.</summary>
public sealed record LevelModuleLaunch(GermanLevel Level, LevelModuleKind Module);

/// <summary>Typed trainer request used for word and sentence modules.</summary>
public sealed record PracticeLaunchRequest(
    GermanLevel Level,
    PracticeUnit Unit,
    TranslationDirection Direction);

public static class LevelModuleLaunchExtensions
{
    /// <summary>Maps word and sentence modules to the trainer's language-pair-relative request.</summary>
    public static bool TryGetPracticeRequest(
        this LevelModuleLaunch launch,
        out PracticeLaunchRequest? request)
    {
        request = launch.Module switch
        {
            LevelModuleKind.WordGermanToRussian => new(
                launch.Level,
                PracticeUnit.Word,
                TranslationDirection.TargetToSource),
            LevelModuleKind.WordRussianToGerman => new(
                launch.Level,
                PracticeUnit.Word,
                TranslationDirection.SourceToTarget),
            LevelModuleKind.SentenceGermanToRussian => new(
                launch.Level,
                PracticeUnit.Sentence,
                TranslationDirection.TargetToSource),
            LevelModuleKind.SentenceRussianToGerman => new(
                launch.Level,
                PracticeUnit.Sentence,
                TranslationDirection.SourceToTarget),
            _ => null
        };
        return request is not null;
    }
}
