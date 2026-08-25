using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Interfaces.Services.Translation;

public interface IResegmentationBenchmarkService
{
    Task CaptureAsync(
        ResegmentationBenchmarkCaptureRequest request,
        CancellationToken cancellationToken);

    Task<int> CountSamplesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ResegmentationBenchmarkSampleView>> GetSamplesAsync(
        int limit,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken);

    Task<int> ClearSamplesAsync(CancellationToken cancellationToken);

    Task<ResegmentationBenchmarkRunResult> RunAsync(
        ResegmentationBenchmarkRunRequest request,
        CancellationToken cancellationToken);
}
