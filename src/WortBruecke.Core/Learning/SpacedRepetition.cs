namespace WortBruecke.Core.Learning;

public enum ReviewRating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4
}

public sealed record ReviewState(
    string ContentKey,
    double StabilityDays,
    double Difficulty,
    DateTimeOffset DueAtUtc,
    DateTimeOffset LastReviewedAtUtc,
    int Repetitions,
    int Lapses,
    string SchedulerVersion);

public interface ISpacedRepetitionScheduler
{
    string Version { get; }

    ReviewState Schedule(ReviewState? previous, AttemptEvent attempt, ReviewRating rating);
}

/// <summary>
/// Small deterministic FSRS-like scheduler. Constants and version are explicit so existing
/// schedules never silently change when a future algorithm is introduced.
/// </summary>
public sealed class DeterministicSpacedRepetitionScheduler : ISpacedRepetitionScheduler
{
    public const string CurrentVersion = "fsrs-like-v1";
    public string Version => CurrentVersion;

    public ReviewState Schedule(ReviewState? previous, AttemptEvent attempt, ReviewRating rating)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (!Enum.IsDefined(rating))
        {
            throw new ArgumentOutOfRangeException(nameof(rating), rating, "Unknown review rating.");
        }
        if (previous is not null && !string.Equals(previous.ContentKey, attempt.ContentKey, StringComparison.Ordinal))
        {
            throw new ArgumentException("The previous state belongs to another content item.", nameof(previous));
        }

        var oldStability = previous?.StabilityDays ?? 0.6;
        var oldDifficulty = previous?.Difficulty ?? 5.0;
        var difficultyDelta = rating switch
        {
            ReviewRating.Again => 1.2,
            ReviewRating.Hard => 0.35,
            ReviewRating.Good => -0.15,
            ReviewRating.Easy => -0.65,
            _ => 0
        };
        var difficulty = Math.Clamp(oldDifficulty + difficultyDelta, 1, 10);
        var stability = rating switch
        {
            ReviewRating.Again => Math.Max(0.15, oldStability * 0.28),
            ReviewRating.Hard => Math.Max(0.5, oldStability * (1.18 + (10 - difficulty) * 0.015)),
            ReviewRating.Good => Math.Max(1, oldStability * (1.9 + (10 - difficulty) * 0.04)),
            ReviewRating.Easy => Math.Max(3, oldStability * (3.0 + (10 - difficulty) * 0.06)),
            _ => oldStability
        };
        stability = Math.Round(Math.Min(stability, 3650), 4, MidpointRounding.AwayFromZero);
        var interval = TimeSpan.FromDays(stability);

        return new ReviewState(
            attempt.ContentKey,
            stability,
            Math.Round(difficulty, 4, MidpointRounding.AwayFromZero),
            attempt.CompletedAtUtc + interval,
            attempt.CompletedAtUtc,
            (previous?.Repetitions ?? 0) + 1,
            (previous?.Lapses ?? 0) + (rating == ReviewRating.Again ? 1 : 0),
            Version);
    }
}
