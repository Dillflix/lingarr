using Lingarr.Server.Attributes;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[LingarrAuthorize]
[Route("api/source-unit")]
public sealed class SourceUnitController : ControllerBase
{
    private readonly ISourceUnitDetectionService _sourceUnitDetectionService;
    private readonly ISourceUnitBenchmarkService _benchmarkService;

    public SourceUnitController(
        ISourceUnitDetectionService sourceUnitDetectionService,
        ISourceUnitBenchmarkService benchmarkService)
    {
        _sourceUnitDetectionService = sourceUnitDetectionService;
        _benchmarkService = benchmarkService;
    }

    /// <summary>
    /// Evaluates source-unit detection for one safe candidate cue window using persisted settings
    /// or request-scoped model/validator overrides.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<ActionResult<SourceUnitDetectionResult>> Evaluate(
        [FromBody] SourceUnitDetectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Cues.Count == 0)
        {
            return BadRequest("At least one source cue is required.");
        }
        if (request.Cues.Count > 4)
        {
            return BadRequest("Source-unit evaluation accepts at most four candidate cues.");
        }

        return Ok(await _sourceUnitDetectionService.DetectAsync(request, cancellationToken));
    }

    [HttpGet("benchmark/count")]
    public async Task<ActionResult<int>> BenchmarkCount(CancellationToken cancellationToken) =>
        Ok(await _benchmarkService.CountSamplesAsync(cancellationToken));

    [HttpGet("benchmark/samples")]
    public async Task<ActionResult<IReadOnlyList<SourceUnitBenchmarkSampleView>>> BenchmarkSamples(
        [FromQuery] int limit = 100,
        [FromQuery] string? sourceLanguage = null,
        CancellationToken cancellationToken = default) =>
        Ok(await _benchmarkService.GetSamplesAsync(limit, sourceLanguage, cancellationToken));

    [HttpDelete("benchmark/samples")]
    public async Task<ActionResult<int>> ClearBenchmarkSamples(CancellationToken cancellationToken) =>
        Ok(await _benchmarkService.ClearSamplesAsync(cancellationToken));

    /// <summary>
    /// Benchmarks one or more source-boundary models against the heuristic baseline on exact live
    /// source cue windows. Blind judges receive randomized Candidate A/B boundaries with no proposal
    /// provenance. High-confidence synthetic boundary corruptions provide judge calibration.
    /// </summary>
    [HttpPost("benchmark/run")]
    public async Task<ActionResult<SourceUnitBenchmarkRunResult>> RunBenchmark(
        [FromBody] SourceUnitBenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SampleLimit <= 0)
        {
            return BadRequest("SampleLimit must be greater than zero.");
        }
        if (request.CandidateModels.Any(model =>
                string.IsNullOrWhiteSpace(model.Endpoint) || string.IsNullOrWhiteSpace(model.Model)))
        {
            return BadRequest("Every supplied candidate model requires an endpoint and model name.");
        }
        if (request.JudgeModels.Any(model =>
                string.IsNullOrWhiteSpace(model.Endpoint) || string.IsNullOrWhiteSpace(model.Model)))
        {
            return BadRequest("Every supplied judge model requires an endpoint and model name.");
        }

        return Ok(await _benchmarkService.RunAsync(request, cancellationToken));
    }
}
