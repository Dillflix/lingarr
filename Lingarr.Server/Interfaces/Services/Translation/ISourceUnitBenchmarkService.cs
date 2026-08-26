using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Interfaces.Services.Translation;

public interface ISourceUnitBenchmarkService
{
    Task<bool> CaptureAsync(SourceUnitBenchmarkCaptureRequest request, CancellationToken cancellationToken);
    Task<int> CountSamplesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceUnitBenchmarkSampleView>> GetSamplesAsync(
        int limit,
        string? sourceLanguage,
        CancellationToken cancellationToken);
    Task<int> ClearSamplesAsync(CancellationToken cancellationToken);
    Task<SourceUnitBenchmarkRunResult> RunAsync(
        SourceUnitBenchmarkRunRequest request,
        CancellationToken cancellationToken);
}
