using WortBruecke.Core.Models;

namespace WortBruecke.Core.Abstractions;

public interface IExamBlueprintRepository
{
    Task<ExamBlueprintCatalog> LoadAsync(CancellationToken cancellationToken = default);
}
