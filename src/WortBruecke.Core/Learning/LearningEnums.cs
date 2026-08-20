namespace WortBruecke.Core.Learning;

/// <summary>
/// The complete application learning path. A0 is the pre-CEFR entry stage;
/// A1-C2 are CEFR-aligned stages.
/// </summary>
public enum GermanLevel
{
    A0 = 0,
    A1 = 1,
    A2 = 2,
    B1 = 3,
    B2 = 4,
    C1 = 5,
    C2 = 6
}

public static class GermanLevelExtensions
{
    public static bool IsCefrLevel(this GermanLevel level) => level is >= GermanLevel.A1 and <= GermanLevel.C2;

    public static GermanLevel? Previous(this GermanLevel level)
    {
        EnsureDefined(level);
        return level == GermanLevel.A0 ? null : level - 1;
    }

    public static GermanLevel? Next(this GermanLevel level)
    {
        EnsureDefined(level);
        return level == GermanLevel.C2 ? null : level + 1;
    }

    public static bool TryParse(string? value, out GermanLevel level) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level);

    private static void EnsureDefined(GermanLevel level)
    {
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown German learning level.");
        }
    }
}

/// <summary>
/// Communicative skills and the language systems that support them.
/// </summary>
public enum LanguageSkill
{
    Vocabulary,
    Grammar,
    Reading,
    Listening,
    Writing,
    Speaking,
    Mediation
}

/// <summary>
/// Exercise families used to keep curriculum objectives independent from a concrete UI.
/// </summary>
public enum ExerciseType
{
    VocabularyRecognition,
    VocabularyRecall,
    BidirectionalTranslation,
    ImageAssociation,
    SentenceAssembly,
    GapFill,
    MultipleChoice,
    ErrorCorrection,
    GrammarTransformation,
    ReadingComprehension,
    InformationMatching,
    ListeningComprehension,
    Dictation,
    NoteTaking,
    GuidedWriting,
    FunctionalWriting,
    EssayWriting,
    SpokenResponse,
    Pronunciation,
    Dialogue,
    OralPresentation,
    MediationSummary,
    IntegratedSkills,
    ExamModuleSimulation
}

public enum AssessmentMode
{
    Practice,
    Diagnostic,
    Checkpoint,
    MockExam
}
