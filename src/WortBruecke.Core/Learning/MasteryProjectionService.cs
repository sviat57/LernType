namespace WortBruecke.Core.Learning;

public interface IMasteryProjectionService
{
    LearningPathProgress Rebuild(
        LearningPathDefinition definition,
        IEnumerable<AttemptEvent> canonicalEvents,
        GermanLevel? placementLevel = null);
}

/// <summary>
/// Rebuildable projection over the immutable event journal. No second mutable mastery store is
/// required; a future cache can always be discarded and recreated from canonical evidence.
/// </summary>
public sealed class MasteryProjectionService : IMasteryProjectionService
{
    private readonly LearningProgressService _progress = new();

    public LearningPathProgress Rebuild(
        LearningPathDefinition definition,
        IEnumerable<AttemptEvent> canonicalEvents,
        GermanLevel? placementLevel = null) =>
        _progress.EvaluatePathFromEvents(definition, canonicalEvents, placementLevel);
}
