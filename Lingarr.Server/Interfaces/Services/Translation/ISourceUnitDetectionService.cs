using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Interfaces.Services.Translation;

public interface ISourceUnitDetectionService
{
    Task<SourceUnitDetectionResult> DetectAsync(
        SourceUnitDetectionRequest request,
        CancellationToken cancellationToken);
}
