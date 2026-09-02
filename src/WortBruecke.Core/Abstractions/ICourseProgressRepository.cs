using WortBruecke.Core.Courses;

namespace WortBruecke.Core.Abstractions;

/// <summary>Persists course-node progress and the exact resume location independently of catalog JSON.</summary>
public interface ICourseProgressRepository
{
    /// <summary>Returns all durable node aggregates for one course.</summary>
    Task<IReadOnlyList<CourseNodeProgress>> GetCourseAsync(
        string courseId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the durable aggregate for one course node.</summary>
    Task UpsertAsync(
        CourseNodeProgress progress,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the last exact location visited in one course.</summary>
    Task<CourseResumeState?> GetResumeAsync(
        string courseId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the last exact location visited in one course.</summary>
    Task SaveResumeAsync(
        CourseResumeState state,
        CancellationToken cancellationToken = default);
}
