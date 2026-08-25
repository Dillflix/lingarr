using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Interfaces.Services.Translation;

public interface ITranslationUnitResegmentationService
{
    Task<TranslationUnitResegmentationResult> ResegmentAsync(
        TranslationUnitResegmentationRequest request,
        CancellationToken cancellationToken);

    Task<ResegmentationEvaluationResult> EvaluateAsync(
        ResegmentationEvaluationRequest request,
        CancellationToken cancellationToken);
}
