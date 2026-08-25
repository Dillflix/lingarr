using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Captures real multi-cue translation units and benchmarks resegmentation models without requiring
/// human target-language annotations. Candidate alignments are compared blindly against the
/// deterministic baseline by one or more multilingual judge models, judges are sanity-checked with
/// deliberately corrupted boundaries, and an optional backtranslation model supplies an independent
/// source-language-only alignment signal.
/// </summary>
public sealed class ResegmentationBenchmarkService : IResegmentationBenchmarkService
{
    private const string DefaultJudgeSystemPrompt = """
        You are a multilingual subtitle-boundary evaluator. Two candidates contain the same complete target-language translation with different subtitle boundaries. Compare how well each target segment maps semantically to the corresponding source subtitle timing slot. Consider clause/phrase alignment, punctuation, readability, and whether meaning leaks into an adjacent slot. Do not translate or rewrite either candidate. Candidate labels are randomized. Return JSON only.
        """;

    private const string DefaultJudgeUserPrompt = """
        Source language: {sourceLanguage}
        Target language: {targetLanguage}

        Source subtitle timing slots:
        {sourceSegmentsJson}

        Complete target translation:
        {translatedUnit}

        Candidate A:
        {candidateASegmentsJson}

        Candidate B:
        {candidateBSegmentsJson}

        Return JSON with winner ("A", "B", or "tie"), scoreA (0-100), scoreB (0-100), and a short reason in English.
        """;

    private const string DefaultBacktranslationSystemPrompt = """
        You are a precise subtitle backtranslation engine. Translate each target-language subtitle segment independently into the requested source language while preserving the number and order of segments. Do not merge or split segments. Return JSON only.
        """;

    private const string DefaultBacktranslationUserPrompt = """
        Translate these {targetLanguage} subtitle segments back into {sourceLanguage}.
        Preserve exactly {segmentCount} segments and their order.

        Target segments:
        {targetSegmentsJson}

        Return JSON as {"segments":["...", "..."]}.
        """;

    private static readonly Regex TokenRegex = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settings;
    private readonly ITranslationUnitResegmentationService _resegmentationService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ResegmentationBenchmarkService> _logger;

    public ResegmentationBenchmarkService(
        LingarrDbContext dbContext,
        ISettingService settings,
        ITranslationUnitResegmentationService resegmentationService,
        IHttpClientFactory httpClientFactory,
        ILogger<ResegmentationBenchmarkService> logger)
    {
        _dbContext = dbContext;
        _settings = settings;
        _resegmentationService = resegmentationService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task CaptureAsync(
        ResegmentationBenchmarkCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceSegments.Count <= 1 || string.IsNullOrWhiteSpace(request.TranslatedUnit))
        {
            return;
        }

        var captureEnabled = await _settings.GetSetting(
            SettingKeys.Translation.Resegmentation.BenchmarkCaptureEnabled);
        if (string.Equals(captureEnabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var sourceSegments = request.SourceSegments.Select(segment => segment.Trim()).ToArray();
        var translatedUnit = request.TranslatedUnit.Trim();
        var sourceJson = JsonSerializer.Serialize(sourceSegments);
        var fingerprint = Fingerprint(
            request.SourceLanguage,
            request.TargetLanguage,
            sourceJson,
            translatedUnit);

        if (await _dbContext.ResegmentationBenchmarkSamples
                .AnyAsync(sample => sample.Fingerprint == fingerprint, cancellationToken))
        {
            return;
        }

        _dbContext.ResegmentationBenchmarkSamples.Add(new ResegmentationBenchmarkSample
        {
            Fingerprint = fingerprint,
            SourceLanguage = request.SourceLanguage,
            TargetLanguage = request.TargetLanguage,
            SourceSegmentsJson = sourceJson,
            TranslatedUnit = translatedUnit,
            SegmentCount = sourceSegments.Length,
            TranslationRequestId = request.TranslationRequestId,
            StartPosition = request.StartPosition,
            EndPosition = request.EndPosition
        });

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogDebug(ex, "Benchmark sample was captured concurrently; ignoring duplicate.");
            _dbContext.ChangeTracker.Clear();
            return;
        }

        await TrimCorpusAsync(cancellationToken);
    }

    public Task<int> CountSamplesAsync(CancellationToken cancellationToken) =>
        _dbContext.ResegmentationBenchmarkSamples.CountAsync(cancellationToken);

    public async Task<IReadOnlyList<ResegmentationBenchmarkSampleView>> GetSamplesAsync(
        int limit,
        string? sourceLanguage,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var query = _dbContext.ResegmentationBenchmarkSamples.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            query = query.Where(sample => sample.SourceLanguage == sourceLanguage);
        }

        if (!string.IsNullOrWhiteSpace(targetLanguage))
        {
            query = query.Where(sample => sample.TargetLanguage == targetLanguage);
        }

        var samples = await query
            .OrderByDescending(sample => sample.CreatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return samples.Select(ToView).ToArray();
    }

    public async Task<int> ClearSamplesAsync(CancellationToken cancellationToken)
    {
        var samples = await _dbContext.ResegmentationBenchmarkSamples.ToListAsync(cancellationToken);
        _dbContext.ResegmentationBenchmarkSamples.RemoveRange(samples);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return samples.Count;
    }

    public async Task<ResegmentationBenchmarkRunResult> RunAsync(
        ResegmentationBenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var samples = await LoadRunSamplesAsync(request, cancellationToken);
        var candidateModels = await ResolveCandidatesAsync(request.CandidateModels, cancellationToken);
        var judgeModels = await ResolveJudgesAsync(request.JudgeModels, cancellationToken);

        if (samples.Count == 0)
        {
            warnings.Add("No captured multi-cue benchmark samples match the requested filters.");
        }
        if (candidateModels.Count == 0)
        {
            warnings.Add("No candidate alignment models were supplied and no configured alignment model is available.");
        }
        if (judgeModels.Count == 0)
        {
            warnings.Add("No judge models were supplied and no configured validator model is available; pairwise preference and adversarial calibration are omitted.");
        }
        if (request.BacktranslationModel is null)
        {
            warnings.Add("No backtranslation model was supplied; source-language backtranslation metrics are omitted.");
        }

        var candidateAggregates = candidateModels
            .Select((model, index) => new CandidateAggregate(UniqueName(model, index), model.Model))
            .ToArray();
        var judgeAggregates = judgeModels
            .Select((model, index) => new JudgeAggregate(UniqueName(model, index), model.Model))
            .ToArray();
        var baselineBacktranslations = new List<ResegmentationBacktranslationMetrics>();
        var sampleResults = new List<ResegmentationBenchmarkSampleResult>();

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var deterministicEvaluation = await _resegmentationService.EvaluateAsync(
                new ResegmentationEvaluationRequest
                {
                    SourceLanguage = sample.SourceLanguage,
                    TargetLanguage = sample.TargetLanguage,
                    SourceSegments = sample.SourceSegments,
                    TranslatedUnit = sample.TranslatedUnit,
                    Mode = ResegmentationModes.Deterministic
                },
                cancellationToken);
            var deterministicSegments = deterministicEvaluation.Deterministic.Segments ??
                                        deterministicEvaluation.SelectedSegments;

            ResegmentationBacktranslationMetrics? deterministicBacktranslation = null;
            if (request.BacktranslationModel is not null)
            {
                deterministicBacktranslation = await TryBacktranslateAndScoreAsync(
                    sample,
                    deterministicSegments,
                    request.BacktranslationModel,
                    cancellationToken);
                if (deterministicBacktranslation is not null)
                {
                    baselineBacktranslations.Add(deterministicBacktranslation);
                }
            }

            if (request.IncludeAdversarialCalibration && judgeModels.Count > 0)
            {
                var corrupted = CreateBoundaryCorruption(deterministicSegments);
                if (corrupted is not null)
                {
                    for (var judgeIndex = 0; judgeIndex < judgeModels.Count; judgeIndex++)
                    {
                        var decision = await TryBlindJudgeAsync(
                            sample,
                            deterministicSegments,
                            corrupted,
                            judgeModels[judgeIndex],
                            $"baseline:{sample.Id}:{judgeIndex}",
                            cancellationToken);
                        if (decision is null)
                        {
                            continue;
                        }

                        judgeAggregates[judgeIndex].RecordAdversarial(
                            decision.Winner == "first",
                            decision.LatencyMs);
                    }
                }
            }

            var perCandidate = new List<ResegmentationBenchmarkCandidateResult>();
            for (var candidateIndex = 0; candidateIndex < candidateModels.Count; candidateIndex++)
            {
                var candidate = candidateModels[candidateIndex];
                var aggregate = candidateAggregates[candidateIndex];
                aggregate.SamplesAttempted++;

                var evaluation = await _resegmentationService.EvaluateAsync(
                    new ResegmentationEvaluationRequest
                    {
                        SourceLanguage = sample.SourceLanguage,
                        TargetLanguage = sample.TargetLanguage,
                        SourceSegments = sample.SourceSegments,
                        TranslatedUnit = sample.TranslatedUnit,
                        Mode = ResegmentationModes.Model,
                        ModelOverride = ToResegmentationOverride(candidate)
                    },
                    cancellationToken);

                var modelCandidate = evaluation.Model;
                var valid = modelCandidate?.Validation.IsValid == true && modelCandidate.Segments is not null;
                var modelVotes = 0;
                var deterministicVotes = 0;
                var ties = 0;
                var adversarialTrials = 0;
                var adversarialPasses = 0;
                double? agreement = null;
                ResegmentationBacktranslationMetrics? backtranslation = null;

                if (modelCandidate?.LatencyMs is long latency)
                {
                    aggregate.AlignmentLatencies.Add(latency);
                }

                if (valid)
                {
                    aggregate.StructurallyValidSamples++;
                    var candidateSegments = modelCandidate!.Segments!;

                    if (judgeModels.Count > 0)
                    {
                        if (SegmentsEquivalent(candidateSegments, deterministicSegments))
                        {
                            ties = judgeModels.Count;
                            aggregate.JudgeTies += ties;
                            agreement = 100;
                            aggregate.JudgeAgreementPercents.Add(agreement.Value);
                        }
                        else
                        {
                            for (var judgeIndex = 0; judgeIndex < judgeModels.Count; judgeIndex++)
                            {
                                var decision = await TryBlindJudgeAsync(
                                    sample,
                                    candidateSegments,
                                    deterministicSegments,
                                    judgeModels[judgeIndex],
                                    $"pair:{sample.Id}:{candidateIndex}:{judgeIndex}",
                                    cancellationToken);
                                if (decision is null)
                                {
                                    continue;
                                }

                                judgeAggregates[judgeIndex].RecordPairwise(
                                    decision.Winner != "tie",
                                    decision.LatencyMs);

                                switch (decision.Winner)
                                {
                                    case "first":
                                        modelVotes++;
                                        aggregate.JudgeModelVotes++;
                                        break;
                                    case "second":
                                        deterministicVotes++;
                                        aggregate.JudgeDeterministicVotes++;
                                        break;
                                    default:
                                        ties++;
                                        aggregate.JudgeTies++;
                                        break;
                                }
                            }

                            var totalVotes = modelVotes + deterministicVotes + ties;
                            if (totalVotes > 0)
                            {
                                agreement = 100d * Math.Max(modelVotes, Math.Max(deterministicVotes, ties)) / totalVotes;
                                aggregate.JudgeAgreementPercents.Add(agreement.Value);
                            }
                        }
                    }

                    if (request.IncludeAdversarialCalibration && judgeModels.Count > 0)
                    {
                        var corrupted = CreateBoundaryCorruption(candidateSegments);
                        if (corrupted is not null)
                        {
                            for (var judgeIndex = 0; judgeIndex < judgeModels.Count; judgeIndex++)
                            {
                                var decision = await TryBlindJudgeAsync(
                                    sample,
                                    candidateSegments,
                                    corrupted,
                                    judgeModels[judgeIndex],
                                    $"adversarial:{sample.Id}:{candidateIndex}:{judgeIndex}",
                                    cancellationToken);
                                if (decision is null)
                                {
                                    continue;
                                }

                                adversarialTrials++;
                                aggregate.AdversarialTrials++;
                                var passed = decision.Winner == "first";
                                if (passed)
                                {
                                    adversarialPasses++;
                                    aggregate.AdversarialPasses++;
                                }
                                judgeAggregates[judgeIndex].RecordAdversarial(passed, decision.LatencyMs);
                            }
                        }
                    }

                    if (request.BacktranslationModel is not null)
                    {
                        backtranslation = await TryBacktranslateAndScoreAsync(
                            sample,
                            candidateSegments,
                            request.BacktranslationModel,
                            cancellationToken);
                        if (backtranslation is not null)
                        {
                            aggregate.Backtranslations.Add(backtranslation);
                        }
                    }
                }

                perCandidate.Add(new ResegmentationBenchmarkCandidateResult
                {
                    Name = aggregate.Name,
                    Model = candidate.Model,
                    StructurallyValid = valid,
                    Segments = modelCandidate?.Segments,
                    Error = modelCandidate?.Error ?? modelCandidate?.Validation.Error,
                    AlignmentLatencyMs = modelCandidate?.LatencyMs,
                    JudgeModelVotes = modelVotes,
                    JudgeDeterministicVotes = deterministicVotes,
                    JudgeTies = ties,
                    JudgeAgreementPercent = agreement,
                    AdversarialTrials = adversarialTrials,
                    AdversarialPasses = adversarialPasses,
                    Backtranslation = backtranslation
                });
            }

            sampleResults.Add(new ResegmentationBenchmarkSampleResult
            {
                SampleId = sample.Id,
                SourceLanguage = sample.SourceLanguage,
                TargetLanguage = sample.TargetLanguage,
                SourceSegments = sample.SourceSegments,
                TranslatedUnit = sample.TranslatedUnit,
                DeterministicSegments = deterministicSegments,
                DeterministicBacktranslation = deterministicBacktranslation,
                Candidates = perCandidate
            });
        }

        var candidateSummaries = candidateAggregates.Select(ToSummary).ToArray();
        var judgeSummaries = judgeAggregates.Select(ToSummary).ToArray();

        return new ResegmentationBenchmarkRunResult
        {
            SampleCount = samples.Count,
            DeterministicBaseline = SummariseBaseline(baselineBacktranslations),
            Candidates = candidateSummaries,
            Judges = judgeSummaries,
            Samples = sampleResults,
            Warnings = warnings
        };
    }

    private async Task TrimCorpusAsync(CancellationToken cancellationToken)
    {
        var configured = await _settings.GetSetting(
            SettingKeys.Translation.Resegmentation.BenchmarkMaxSamples);
        var maxSamples = int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 10, 10000)
            : 500;

        var count = await _dbContext.ResegmentationBenchmarkSamples.CountAsync(cancellationToken);
        var excess = count - maxSamples;
        if (excess <= 0)
        {
            return;
        }

        var oldest = await _dbContext.ResegmentationBenchmarkSamples
            .OrderBy(sample => sample.CreatedAt)
            .ThenBy(sample => sample.Id)
            .Take(excess)
            .ToListAsync(cancellationToken);
        _dbContext.ResegmentationBenchmarkSamples.RemoveRange(oldest);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ResegmentationBenchmarkSampleView>> LoadRunSamplesAsync(
        ResegmentationBenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.SampleLimit, 1, 1000);
        var query = _dbContext.ResegmentationBenchmarkSamples.AsNoTracking().AsQueryable();
        var ids = request.SampleIds?.Distinct().ToArray();

        if (ids is { Length: > 0 })
        {
            query = query.Where(sample => ids.Contains(sample.Id));
        }
        if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
        {
            query = query.Where(sample => sample.SourceLanguage == request.SourceLanguage);
        }
        if (!string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            query = query.Where(sample => sample.TargetLanguage == request.TargetLanguage);
        }

        var samples = await query
            .OrderByDescending(sample => sample.CreatedAt)
            .ThenByDescending(sample => sample.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return samples.Select(ToView).ToArray();
    }

    private async Task<IReadOnlyList<NamedBenchmarkModel>> ResolveCandidatesAsync(
        IReadOnlyList<NamedBenchmarkModel> supplied,
        CancellationToken cancellationToken)
    {
        if (supplied.Count > 0)
        {
            return supplied;
        }

        var values = await _settings.GetSettings(new[]
        {
            SettingKeys.Translation.Resegmentation.Endpoint,
            SettingKeys.Translation.Resegmentation.Model,
            SettingKeys.Translation.Resegmentation.TimeoutSeconds
        });
        var endpoint = values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.Endpoint) ?? string.Empty;
        var model = values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.Model) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.Resegmentation.ApiKey);
        var timeout = ParseTimeout(values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.TimeoutSeconds));
        return [new NamedBenchmarkModel
        {
            Name = "configured",
            Endpoint = endpoint,
            Model = model,
            ApiKey = apiKey,
            TimeoutSeconds = timeout
        }];
    }

    private async Task<IReadOnlyList<NamedBenchmarkModel>> ResolveJudgesAsync(
        IReadOnlyList<NamedBenchmarkModel> supplied,
        CancellationToken cancellationToken)
    {
        if (supplied.Count > 0)
        {
            return supplied;
        }

        var values = await _settings.GetSettings(new[]
        {
            SettingKeys.Translation.Resegmentation.ValidatorEndpoint,
            SettingKeys.Translation.Resegmentation.ValidatorModel,
            SettingKeys.Translation.Resegmentation.TimeoutSeconds
        });
        var endpoint = values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.ValidatorEndpoint) ?? string.Empty;
        var model = values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.ValidatorModel) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        var apiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.Resegmentation.ValidatorApiKey);
        var timeout = ParseTimeout(values.GetValueOrDefault(SettingKeys.Translation.Resegmentation.TimeoutSeconds));
        return [new NamedBenchmarkModel
        {
            Name = "configured-validator",
            Endpoint = endpoint,
            Model = model,
            ApiKey = apiKey,
            TimeoutSeconds = timeout
        }];
    }

    private async Task<BlindJudgeMappedDecision?> TryBlindJudgeAsync(
        ResegmentationBenchmarkSampleView sample,
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        NamedBenchmarkModel judge,
        string salt,
        CancellationToken cancellationToken)
    {
        try
        {
            var swap = StableSwap(salt);
            var candidateA = swap ? second : first;
            var candidateB = swap ? first : second;
            var systemPrompt = string.IsNullOrWhiteSpace(judge.SystemPrompt)
                ? DefaultJudgeSystemPrompt
                : judge.SystemPrompt!;
            var userPromptTemplate = string.IsNullOrWhiteSpace(judge.UserPrompt)
                ? DefaultJudgeUserPrompt
                : judge.UserPrompt!;
            var userPrompt = userPromptTemplate
                .Replace("{sourceLanguage}", sample.SourceLanguage, StringComparison.Ordinal)
                .Replace("{targetLanguage}", sample.TargetLanguage, StringComparison.Ordinal)
                .Replace("{sourceSegmentsJson}", JsonSerializer.Serialize(sample.SourceSegments), StringComparison.Ordinal)
                .Replace("{translatedUnit}", sample.TranslatedUnit, StringComparison.Ordinal)
                .Replace("{candidateASegmentsJson}", JsonSerializer.Serialize(candidateA), StringComparison.Ordinal)
                .Replace("{candidateBSegmentsJson}", JsonSerializer.Serialize(candidateB), StringComparison.Ordinal);

            var (content, latencyMs) = await SendJsonChatAsync(
                judge,
                systemPrompt,
                userPrompt,
                CreateJudgeResponseFormat(),
                cancellationToken);
            var parsed = ParseJudgeDecision(content);
            if (parsed is null)
            {
                return null;
            }

            var mapped = parsed.Winner switch
            {
                "A" => swap ? "second" : "first",
                "B" => swap ? "first" : "second",
                _ => "tie"
            };

            return new BlindJudgeMappedDecision(mapped, latencyMs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Reference-free resegmentation judge call failed for model {Model}.", judge.Model);
            return null;
        }
    }

    private async Task<ResegmentationBacktranslationMetrics?> TryBacktranslateAndScoreAsync(
        ResegmentationBenchmarkSampleView sample,
        IReadOnlyList<string> targetSegments,
        NamedBenchmarkModel model,
        CancellationToken cancellationToken)
    {
        try
        {
            var systemPrompt = string.IsNullOrWhiteSpace(model.SystemPrompt)
                ? DefaultBacktranslationSystemPrompt
                : model.SystemPrompt!;
            var template = string.IsNullOrWhiteSpace(model.UserPrompt)
                ? DefaultBacktranslationUserPrompt
                : model.UserPrompt!;
            var userPrompt = template
                .Replace("{sourceLanguage}", sample.SourceLanguage, StringComparison.Ordinal)
                .Replace("{targetLanguage}", sample.TargetLanguage, StringComparison.Ordinal)
                .Replace("{segmentCount}", targetSegments.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("{targetSegmentsJson}", JsonSerializer.Serialize(targetSegments), StringComparison.Ordinal);

            var (content, latencyMs) = await SendJsonChatAsync(
                model,
                systemPrompt,
                userPrompt,
                CreateSegmentsResponseFormat(targetSegments.Count, "backtranslated_segments"),
                cancellationToken);
            var backtranslated = ParseSegments(content);
            if (backtranslated is null || backtranslated.Count != sample.SourceSegments.Count)
            {
                return null;
            }

            var sameScores = new List<double>(backtranslated.Count);
            var margins = new List<double>(backtranslated.Count);
            var leakage = 0;

            for (var index = 0; index < backtranslated.Count; index++)
            {
                var same = TokenF1(sample.SourceSegments[index], backtranslated[index]);
                var bestOther = 0d;
                for (var sourceIndex = 0; sourceIndex < sample.SourceSegments.Count; sourceIndex++)
                {
                    if (sourceIndex == index)
                    {
                        continue;
                    }
                    bestOther = Math.Max(bestOther, TokenF1(sample.SourceSegments[sourceIndex], backtranslated[index]));
                }

                sameScores.Add(same);
                margins.Add(same - bestOther);
                if (bestOther > same)
                {
                    leakage++;
                }
            }

            return new ResegmentationBacktranslationMetrics
            {
                BacktranslatedSegments = backtranslated,
                MeanSameSlotTokenF1Percent = sameScores.Count == 0 ? 0 : 100 * sameScores.Average(),
                MeanCrossSlotMarginPercentagePoints = margins.Count == 0 ? 0 : 100 * margins.Average(),
                CrossSlotLeakagePercent = backtranslated.Count == 0 ? 0 : 100d * leakage / backtranslated.Count,
                LatencyMs = latencyMs
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Benchmark backtranslation call failed for model {Model}.", model.Model);
            return null;
        }
    }

    private async Task<(string Content, long LatencyMs)> SendJsonChatAsync(
        NamedBenchmarkModel model,
        string systemPrompt,
        string userPrompt,
        object responseFormat,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(model.Endpoint);
        var timeoutSeconds = Math.Clamp(model.TimeoutSeconds ?? 120, 5, 3600);
        var client = _httpClientFactory.CreateClient();
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var first = await SendOnceAsync(
            client,
            endpoint,
            model,
            systemPrompt,
            userPrompt,
            responseFormat,
            timeoutCts.Token);

        if (!first.Response.IsSuccessStatusCode && SupportsSchemaFallback(first.Response.StatusCode))
        {
            first.Response.Dispose();
            var second = await SendOnceAsync(
                client,
                endpoint,
                model,
                systemPrompt,
                userPrompt,
                null,
                timeoutCts.Token);
            using (second.Response)
            {
                if (!second.Response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"Benchmark model request failed ({(int)second.Response.StatusCode}): {second.Body}");
                }
                stopwatch.Stop();
                return (ExtractAssistantContent(second.Body), stopwatch.ElapsedMilliseconds);
            }
        }

        using (first.Response)
        {
            if (!first.Response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Benchmark model request failed ({(int)first.Response.StatusCode}): {first.Body}");
            }
            stopwatch.Stop();
            return (ExtractAssistantContent(first.Body), stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<HttpResult> SendOnceAsync(
        HttpClient client,
        Uri endpoint,
        NamedBenchmarkModel model,
        string systemPrompt,
        string userPrompt,
        object? responseFormat,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            ["temperature"] = 0
        };
        if (responseFormat is not null)
        {
            payload["response_format"] = responseFormat;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(model.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);
        }

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResult(response, body);
    }

    private static object CreateJudgeResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "blind_resegmentation_judgement",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    winner = new { type = "string", @enum = new[] { "A", "B", "tie" } },
                    scoreA = new { type = "number", minimum = 0, maximum = 100 },
                    scoreB = new { type = "number", minimum = 0, maximum = 100 },
                    reason = new { type = "string" }
                },
                required = new[] { "winner", "scoreA", "scoreB", "reason" },
                additionalProperties = false
            }
        }
    };

    private static object CreateSegmentsResponseFormat(int count, string name) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name,
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    segments = new
                    {
                        type = "array",
                        minItems = count,
                        maxItems = count,
                        items = new { type = "string" }
                    }
                },
                required = new[] { "segments" },
                additionalProperties = false
            }
        }
    };

    private static ParsedJudgeDecision? ParseJudgeDecision(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(content));
            var root = document.RootElement;
            var winner = root.GetProperty("winner").GetString();
            if (winner is not ("A" or "B" or "tie"))
            {
                return null;
            }
            var scoreA = root.TryGetProperty("scoreA", out var a) ? a.GetDouble() : 0;
            var scoreB = root.TryGetProperty("scoreB", out var b) ? b.GetDouble() : 0;
            return new ParsedJudgeDecision(winner, scoreA, scoreB);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? ParseSegments(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(content));
            if (!document.RootElement.TryGetProperty("segments", out var segments) ||
                segments.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            return segments.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Concat(content.EnumerateArray().Select(part =>
                part.TryGetProperty("text", out var text) ? text.GetString() : string.Empty));
        }
        return content.ToString();
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
            {
                trimmed = trimmed[(firstNewline + 1)..lastFence].Trim();
            }
        }

        var firstBrace = trimmed.IndexOf('{');
        var lastBrace = trimmed.LastIndexOf('}');
        return firstBrace >= 0 && lastBrace >= firstBrace
            ? trimmed[firstBrace..(lastBrace + 1)]
            : trimmed;
    }

    private static Uri BuildChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid benchmark endpoint: '{endpoint}'.");
        }
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return baseUri;
        }
        var suffix = path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? "/chat/completions"
            : "/v1/chat/completions";
        return new Uri(trimmed + suffix, UriKind.Absolute);
    }

    private static bool SupportsSchemaFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static IReadOnlyList<string>? CreateBoundaryCorruption(IReadOnlyList<string> segments)
    {
        if (segments.Count < 2)
        {
            return null;
        }

        var result = segments.Select(segment => segment.Trim()).ToArray();
        for (var index = 0; index < result.Length - 1; index++)
        {
            var left = SplitWords(result[index]);
            var right = SplitWords(result[index + 1]);

            if (left.Length >= 4)
            {
                var move = Math.Min(2, left.Length - 1);
                var moved = left[^move..];
                result[index] = string.Join(' ', left[..^move]);
                result[index + 1] = string.Join(' ', moved.Concat(right));
                return result;
            }

            if (right.Length >= 4)
            {
                var move = Math.Min(2, right.Length - 1);
                var moved = right[..move];
                result[index] = string.Join(' ', left.Concat(moved));
                result[index + 1] = string.Join(' ', right[move..]);
                return result;
            }
        }

        return null;
    }

    private static string[] SplitWords(string value) =>
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static double TokenF1(string expected, string actual)
    {
        var expectedTokens = Tokenize(expected);
        var actualTokens = Tokenize(actual);
        if (expectedTokens.Count == 0 || actualTokens.Count == 0)
        {
            return expectedTokens.Count == actualTokens.Count ? 1 : 0;
        }

        var expectedCounts = expectedTokens.GroupBy(token => token).ToDictionary(group => group.Key, group => group.Count());
        var actualCounts = actualTokens.GroupBy(token => token).ToDictionary(group => group.Key, group => group.Count());
        var overlap = expectedCounts.Sum(item => Math.Min(item.Value, actualCounts.GetValueOrDefault(item.Key)));
        var precision = (double)overlap / actualTokens.Count;
        var recall = (double)overlap / expectedTokens.Count;
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static IReadOnlyList<string> Tokenize(string value) =>
        TokenRegex.Matches(value.ToLowerInvariant()).Select(match => match.Value).ToArray();

    private static bool SegmentsEquivalent(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(NormalizeWhitespace(pair.First), NormalizeWhitespace(pair.Second), StringComparison.Ordinal));

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool StableSwap(string salt)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(salt));
        return (hash[0] & 1) == 1;
    }

    private static string Fingerprint(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int ParseTimeout(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 5, 3600)
            : 120;

    private static string UniqueName(NamedBenchmarkModel model, int index) =>
        string.IsNullOrWhiteSpace(model.Name) ? $"{model.Model} #{index + 1}" : model.Name.Trim();

    private static ResegmentationModelOverride ToResegmentationOverride(NamedBenchmarkModel model) => new()
    {
        Endpoint = model.Endpoint,
        Model = model.Model,
        ApiKey = model.ApiKey,
        SystemPrompt = model.SystemPrompt,
        UserPrompt = model.UserPrompt,
        TimeoutSeconds = model.TimeoutSeconds
    };

    private static ResegmentationBenchmarkSampleView ToView(ResegmentationBenchmarkSample sample)
    {
        var sourceSegments = JsonSerializer.Deserialize<string[]>(sample.SourceSegmentsJson) ?? [];
        return new ResegmentationBenchmarkSampleView
        {
            Id = sample.Id,
            SourceLanguage = sample.SourceLanguage,
            TargetLanguage = sample.TargetLanguage,
            SourceSegments = sourceSegments,
            TranslatedUnit = sample.TranslatedUnit,
            SegmentCount = sample.SegmentCount,
            TranslationRequestId = sample.TranslationRequestId,
            StartPosition = sample.StartPosition,
            EndPosition = sample.EndPosition,
            CreatedAt = sample.CreatedAt
        };
    }

    private static ResegmentationBenchmarkCandidateSummary ToSummary(CandidateAggregate aggregate)
    {
        var totalVotes = aggregate.JudgeModelVotes + aggregate.JudgeDeterministicVotes + aggregate.JudgeTies;
        return new ResegmentationBenchmarkCandidateSummary
        {
            Name = aggregate.Name,
            Model = aggregate.Model,
            SamplesAttempted = aggregate.SamplesAttempted,
            StructurallyValidSamples = aggregate.StructurallyValidSamples,
            StructuralValidityPercent = Percent(aggregate.StructurallyValidSamples, aggregate.SamplesAttempted),
            MeanAlignmentLatencyMs = MeanLong(aggregate.AlignmentLatencies),
            JudgeModelVotes = aggregate.JudgeModelVotes,
            JudgeDeterministicVotes = aggregate.JudgeDeterministicVotes,
            JudgeTies = aggregate.JudgeTies,
            JudgePreferencePercent = totalVotes == 0
                ? 0
                : 100d * (aggregate.JudgeModelVotes + 0.5 * aggregate.JudgeTies) / totalVotes,
            MeanJudgeAgreementPercent = aggregate.JudgeAgreementPercents.Count == 0
                ? 0
                : aggregate.JudgeAgreementPercents.Average(),
            AdversarialTrials = aggregate.AdversarialTrials,
            AdversarialPassPercent = Percent(aggregate.AdversarialPasses, aggregate.AdversarialTrials),
            BacktranslationSamples = aggregate.Backtranslations.Count,
            MeanSameSlotTokenF1Percent = MeanNullable(aggregate.Backtranslations.Select(item => item.MeanSameSlotTokenF1Percent)),
            MeanCrossSlotMarginPercentagePoints = MeanNullable(aggregate.Backtranslations.Select(item => item.MeanCrossSlotMarginPercentagePoints)),
            CrossSlotLeakagePercent = MeanNullable(aggregate.Backtranslations.Select(item => item.CrossSlotLeakagePercent))
        };
    }

    private static ResegmentationBenchmarkJudgeSummary ToSummary(JudgeAggregate aggregate) => new()
    {
        Name = aggregate.Name,
        Model = aggregate.Model,
        PairwiseComparisons = aggregate.PairwiseComparisons,
        DecisiveComparisons = aggregate.DecisiveComparisons,
        AdversarialTrials = aggregate.AdversarialTrials,
        AdversarialPassPercent = Percent(aggregate.AdversarialPasses, aggregate.AdversarialTrials),
        MeanLatencyMs = MeanLong(aggregate.Latencies)
    };

    private static ResegmentationBenchmarkBaselineSummary SummariseBaseline(
        IReadOnlyList<ResegmentationBacktranslationMetrics> metrics) => new()
    {
        BacktranslationSamples = metrics.Count,
        MeanSameSlotTokenF1Percent = MeanNullable(metrics.Select(item => item.MeanSameSlotTokenF1Percent)),
        MeanCrossSlotMarginPercentagePoints = MeanNullable(metrics.Select(item => item.MeanCrossSlotMarginPercentagePoints)),
        CrossSlotLeakagePercent = MeanNullable(metrics.Select(item => item.CrossSlotLeakagePercent))
    };

    private static double Percent(int numerator, int denominator) =>
        denominator == 0 ? 0 : 100d * numerator / denominator;

    private static long MeanLong(IReadOnlyCollection<long> values) =>
        values.Count == 0 ? 0 : (long)Math.Round(values.Average());

    private static double? MeanNullable(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? null : array.Average();
    }

    private sealed class CandidateAggregate(string name, string model)
    {
        public string Name { get; } = name;
        public string Model { get; } = model;
        public int SamplesAttempted { get; set; }
        public int StructurallyValidSamples { get; set; }
        public List<long> AlignmentLatencies { get; } = [];
        public int JudgeModelVotes { get; set; }
        public int JudgeDeterministicVotes { get; set; }
        public int JudgeTies { get; set; }
        public List<double> JudgeAgreementPercents { get; } = [];
        public int AdversarialTrials { get; set; }
        public int AdversarialPasses { get; set; }
        public List<ResegmentationBacktranslationMetrics> Backtranslations { get; } = [];
    }

    private sealed class JudgeAggregate(string name, string model)
    {
        public string Name { get; } = name;
        public string Model { get; } = model;
        public int PairwiseComparisons { get; private set; }
        public int DecisiveComparisons { get; private set; }
        public int AdversarialTrials { get; private set; }
        public int AdversarialPasses { get; private set; }
        public List<long> Latencies { get; } = [];

        public void RecordPairwise(bool decisive, long? latency)
        {
            PairwiseComparisons++;
            if (decisive) DecisiveComparisons++;
            if (latency.HasValue) Latencies.Add(latency.Value);
        }

        public void RecordAdversarial(bool passed, long? latency)
        {
            AdversarialTrials++;
            if (passed) AdversarialPasses++;
            if (latency.HasValue) Latencies.Add(latency.Value);
        }
    }

    private sealed record BlindJudgeMappedDecision(string Winner, long? LatencyMs);
    private sealed record ParsedJudgeDecision(string Winner, double ScoreA, double ScoreB);
    private sealed record HttpResult(HttpResponseMessage Response, string Body);
}
