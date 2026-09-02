using WortBruecke.Core.Courses;

namespace WortBruecke.Core.Abstractions;

/// <summary>Loads the validated offline curriculum catalog.</summary>
public interface ICourseCatalogRepository
{
    /// <summary>Loads and validates the complete course catalog.</summary>
    Task<CourseCatalog> LoadAsync(CancellationToken cancellationToken = default);
}
