using Lingarr.Core.Data;
using Lingarr.Server.Attributes;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Lingarr.Server.Services.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[LingarrAuthorize]
[Route("api/resegmentation")]
public sealed class ResegmentationController : ControllerBase
{
    private readonly ITranslationUnitResegmentationService _resegmentationService;
    private readonly IResegmentationBenchmarkService _benchmarkService;
    private readonly ResegmentationBenchmarkHistoryHarvester _historyHarvester;

    public ResegmentationController(
        ITranslationUnitResegmentationService resegmentationService,
        LingarrDbContext dbContext,
        ISettingService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<ResegmentationBenchmarkService> benchmarkLogger,
        ILogger<ResegmentationBenchmarkHistoryHarvester> harvesterLogger)
    {
        _resegmentationService = resegmentationService;
        _benchmarkService = new ResegmentationBenchmarkService(
            dbContext,
            settings,
            resegmentationService,
            httpClientFactory,
            benchmarkLogger);
        _historyHarvester = new ResegmentationBenchmarkHistoryHarvester(
            dbContext,
            settings,
            harvesterLogger);
    }

    /// <summary>
    /// Evaluates deterministic and dedicated-model subtitle resegmentation for one translated unit.
    /// Optional model/validator overrides make it possible to compare hosted models and prompts
    /// without changing persistent settings. If reference segments are supplied, objective boundary
    /// and exact-segment metrics are returned for both candidates.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<ActionResult<ResegmentationEvaluationResult>> Evaluate(
        [FromBody] ResegmentationEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceSegments.Count == 0)
        {
            return BadRequest("At least one source segment is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TranslatedUnit))
        {
            return BadRequest("A translated unit is required.");
        }

        if (request.ReferenceSegments is not null &&
            request.ReferenceSegments.Count != request.SourceSegments.Count)
        {
            return BadRequest("Reference segment count must match source segment count.");
        }

        var result = await _resegmentationService.EvaluateAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>
    /// Returns the number of locally captured/harvested multi-cue translation units available for
    /// reference-free benchmarking.
    /// </summary>
    [HttpGet("benchmark/count")]
    public async Task<ActionResult<int>> BenchmarkCount(CancellationToken cancellationToken) =>
        Ok(await _benchmarkService.CountSamplesAsync(cancellationToken));

    /// <summary>
    /// Reconstructs multi-cue translation units from recently completed Lingarr translations.
    /// No target-language reference annotation is needed: final per-cue targets are concatenated
    /// back into the complete translation unit and deduplicated into the local benchmark corpus.
    /// </summary>
    [HttpPost("benchmark/harvest")]
    public async Task<ActionResult<ResegmentationBenchmarkHarvestResult>> HarvestBenchmarkSamples(
        [FromQuery] int maxRequests = 100,
        CancellationToken cancellationToken = default)
    {
        if (maxRequests <= 0)
        {
            return BadRequest("maxRequests must be greater than zero.");
        }

        return Ok(await _historyHarvester.HarvestAsync(maxRequests, cancellationToken));
    }

    /// <summary>
    /// Returns recently captured multi-cue translation units. These samples contain source timing
    /// slots and the complete translated unit only; no target-language gold annotation is required.
    /// </summary>
    [HttpGet("benchmark/samples")]
    public async Task<ActionResult<IReadOnlyList<ResegmentationBenchmarkSampleView>>> BenchmarkSamples(
        [FromQuery] int limit = 100,
        [FromQuery] string? sourceLanguage = null,
        [FromQuery] string? targetLanguage = null,
        CancellationToken cancellationToken = default)
    {
        var samples = await _benchmarkService.GetSamplesAsync(
            limit,
            sourceLanguage,
            targetLanguage,
            cancellationToken);
        return Ok(samples);
    }

    /// <summary>
    /// Clears the locally captured reference-free benchmark corpus.
    /// </summary>
    [HttpDelete("benchmark/samples")]
    public async Task<ActionResult<int>> ClearBenchmarkSamples(CancellationToken cancellationToken) =>
        Ok(await _benchmarkService.ClearSamplesAsync(cancellationToken));

    /// <summary>
    /// Runs a reference-free benchmark across captured translation units. Candidate alignment
    /// models are compared blindly against the deterministic baseline by multiple judges. Optional
    /// backtranslation produces source-language same-slot/cross-slot metrics, and adversarially
    /// shifted boundaries measure whether each judge can detect obviously degraded segmentation.
    /// By default the runner first harvests recent completed translations, so no manually prepared
    /// corpus or Danish-language annotation is required.
    /// </summary>
    [HttpPost("benchmark/run")]
    public async Task<ActionResult<ResegmentationBenchmarkRunResult>> RunBenchmark(
        [FromBody] ResegmentationBenchmarkRunRequest request,
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

        if (request.BacktranslationModel is not null &&
            (string.IsNullOrWhiteSpace(request.BacktranslationModel.Endpoint) ||
             string.IsNullOrWhiteSpace(request.BacktranslationModel.Model)))
        {
            return BadRequest("The backtranslation model requires an endpoint and model name.");
        }

        ResegmentationBenchmarkHarvestResult? harvest = null;
        if (request.AutoHarvest)
        {
            harvest = await _historyHarvester.HarvestAsync(
                Math.Clamp(request.HarvestRequestLimit, 1, 1000),
                cancellationToken);
        }

        var result = await _benchmarkService.RunAsync(request, cancellationToken);
        return Ok(new ResegmentationBenchmarkRunResult
        {
            SampleCount = result.SampleCount,
            Harvest = harvest,
            DeterministicBaseline = result.DeterministicBaseline,
            Candidates = result.Candidates,
            Judges = result.Judges,
            Samples = result.Samples,
            Warnings = result.Warnings
        });
    }
}
