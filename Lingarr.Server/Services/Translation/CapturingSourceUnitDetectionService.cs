using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Production-facing source detector decorator. It delegates the actual boundary decision to the
/// normal detector, then records the exact candidate cue window and decision for later benchmark
/// replay. Benchmark calls set an explicit Mode and therefore bypass capture.
/// </summary>
public sealed class CapturingSourceUnitDetectionService : ISourceUnitDetectionService
{
    private readonly SourceUnitDetectionService _inner;
    private readonly ISourceUnitBenchmarkService _benchmarkService;
    private readonly ILogger<CapturingSourceUnitDetectionService> _logger;

    public CapturingSourceUnitDetectionService(
        SourceUnitDetectionService inner,
        ISourceUnitBenchmarkService benchmarkService,
        ILogger<CapturingSourceUnitDetectionService> logger)
    {
        _inner = inner;
        _benchmarkService = benchmarkService;
        _logger = logger;
    }

    public async Task<SourceUnitDetectionResult> DetectAsync(
        SourceUnitDetectionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _inner.DetectAsync(request, cancellationToken);

        // SentenceAwareTranslationUnitService deliberately leaves Mode null so persisted production
        // settings are used. Benchmark/evaluation calls supply an explicit mode and must not pollute
        // the live corpus with replayed or synthetic cases.
        if (request.Mode is null && request.Cues.Count > 1)
        {
            try
            {
                await _benchmarkService.CaptureAsync(
                    new SourceUnitBenchmarkCaptureRequest
                    {
                        SourceLanguage = request.SourceLanguage,
                        Cues = request.Cues,
                        Detection = result,
                        StartPosition = request.Cues[0].Position,
                        EndPosition = request.Cues[^1].Position
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to capture source-unit benchmark candidate window beginning at cue {Position}; detection will continue.",
                    request.Cues[0].Position);
            }
        }

        return result;
    }
}
