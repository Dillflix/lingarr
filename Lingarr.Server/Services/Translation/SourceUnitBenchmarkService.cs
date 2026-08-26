using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Captures source-language boundary decisions and benchmarks dedicated boundary models against
/// Lingarr's heuristic baseline using blind A/B judges. Capture persistence is isolated in its own
/// DI scope so it cannot save or detach unrelated TranslationJob entities.
/// </summary>
public sealed class SourceUnitBenchmarkService : ISourceUnitBenchmarkService
{
    private const string DefaultJudgeSystemPrompt = """
        You are a subtitle source-unit segmentation judge. Compare Candidate A and Candidate B for the same consecutive source-language subtitle cues. Their origins are intentionally hidden and their A/B order is randomized. Judge only which boundary better identifies one complete linguistic unit beginning at cue 1. Prefer semantic and syntactic completeness while avoiding unrelated sentences or speaker turns. Do not infer or favor how either candidate was produced. Return JSON only.
        """;

    private const string DefaultJudgeUserPrompt = """
        Source language: {sourceLanguage}
        Candidate cue count: {candidateCount}

        Consecutive subtitle cues:
        {sourceCuesJson}

        Candidate A unitLength: {candidateAUnitLength}
        Candidate B unitLength: {candidateBUnitLength}

        Choose which candidate better identifies exactly one linguistic translation unit beginning at cue 1. The candidates' origins are deliberately undisclosed. Return JSON with winner ("A", "B", or "tie"), candidateAScore (0-100), candidateBScore (0-100), and reason.
        """;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingService _settings;
    private readonly ISourceUnitDetectionService _sourceUnitDetectionService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SourceUnitBenchmarkService> _logger;

    public SourceUnitBenchmarkService(
        IServiceScopeFactory scopeFactory,
        ISettingService settings,
        ISourceUnitDetectionService sourceUnitDetectionService,
        IHttpClientFactory httpClientFactory,
        ILogger<SourceUnitBenchmarkService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _sourceUnitDetectionService = sourceUnitDetectionService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<bool> CaptureAsync(
        SourceUnitBenchmarkCaptureRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Cues.Count <= 1)
        {
            return false;
        }

        var enabled = await _settings.GetSetting(SettingKeys.Translation.SourceUnitDetection.BenchmarkCaptureEnabled);
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cuesJson = JsonSerializer.Serialize(request.Cues);
        var fingerprint = Fingerprint(request.SourceLanguage, cuesJson);

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var existing = await dbContext.SourceUnitBenchmarkSamples
            .FirstOrDefaultAsync(sample => sample.Fingerprint == fingerprint, cancellationToken);

        if (existing is not null)
        {
            ApplyProductionDecision(existing, request);
            await dbContext.SaveChangesAsync(cancellationToken);
            return false;
        }

        var maxSamples = ParsePositiveInt(
            await _settings.GetSetting(SettingKeys.Translation.SourceUnitDetection.BenchmarkMaxSamples),
            5000);
        var count = await dbContext.SourceUnitBenchmarkSamples.CountAsync(cancellationToken);
        if (count >= maxSamples)
        {
            return false;
        }

        var entity = new SourceUnitBenchmarkSample
        {
            Fingerprint = fingerprint,
            SourceLanguage = request.SourceLanguage,
            CandidateCuesJson = cuesJson,
            CandidateCount = request.Cues.Count,
            HeuristicUnitLength = request.Detection.Heuristic.UnitLength,
            TranslationRequestId = request.TranslationRequestId,
            StartPosition = request.StartPosition,
            EndPosition = request.EndPosition
        };
        ApplyProductionDecision(entity, request);
        dbContext.SourceUnitBenchmarkSamples.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // The context belongs only to this capture scope. A duplicate race is harmless and
            // clearing/detaching it cannot affect the translation job's tracked entities.
            var duplicateExists = await dbContext.SourceUnitBenchmarkSamples
                .AsNoTracking()
                .AnyAsync(sample => sample.Fingerprint == fingerprint, cancellationToken);
            if (duplicateExists)
            {
                _logger.LogDebug(ex, "Duplicate source-unit benchmark sample ignored.");
                return false;
            }

            throw;
        }
    }

    public async Task<int> CountSamplesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        return await dbContext.SourceUnitBenchmarkSamples.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SourceUnitBenchmarkSampleView>> GetSamplesAsync(
        int limit,
        string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var query = dbContext.SourceUnitBenchmarkSamples.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(sourceLanguage))
        {
            query = query.Where(sample => sample.SourceLanguage == sourceLanguage);
        }

        var samples = await query
            .OrderByDescending(sample => sample.Id)
            .Take(Math.Clamp(limit, 1, 5000))
            .ToListAsync(cancellationToken);
        return samples.Select(ToView).ToList();
    }

    public async Task<int> ClearSamplesAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var samples = await dbContext.SourceUnitBenchmarkSamples.ToListAsync(cancellationToken);
        dbContext.SourceUnitBenchmarkSamples.RemoveRange(samples);
        await dbContext.SaveChangesAsync(cancellationToken);
        return samples.Count;
    }

    public async Task<SourceUnitBenchmarkRunResult> RunAsync(
        SourceUnitBenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var samples = await LoadSamplesAsync(request, cancellationToken);
        var candidates = await ResolveCandidatesAsync(request.CandidateModels, cancellationToken);
        var judges = await ResolveJudgesAsync(request.JudgeModels, cancellationToken);

        if (samples.Count == 0)
        {
            warnings.Add("No source-unit benchmark samples matched the requested filters. Translate fresh non-batch subtitles to populate the corpus automatically.");
        }
        if (candidates.Count == 0)
        {
            warnings.Add("No candidate source-boundary model is configured. Supply CandidateModels or configure the dedicated source-boundary model.");
        }
        if (judges.Count == 0)
        {
            warnings.Add("No blind judge model is configured. Candidate validity and disagreement metrics will still be reported, but there will be no preference signal.");
        }

        var judgeAccumulators = judges.ToDictionary(
            JudgeName,
            judge => new JudgeAccumulator(JudgeName(judge), judge.Model),
            StringComparer.Ordinal);
        var sampleResults = new List<SourceUnitBenchmarkSampleResult>();

        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cues = DeserializeCues(sample.CandidateCuesJson);
            if (cues.Count <= 1)
            {
                continue;
            }

            var heuristicUnitLength = SourceUnitDetectionService.DetermineHeuristicUnitLength(cues);
            var calibration = new Dictionary<string, bool?>(StringComparer.Ordinal);
            if (request.IncludeAdversarialCalibration && judges.Count > 0)
            {
                var pair = BuildAdversarialCalibrationPair(cues, heuristicUnitLength);
                if (pair is not null)
                {
                    foreach (var judge in judges)
                    {
                        var judgeName = JudgeName(judge);
                        var decision = await RunBlindJudgeAsync(
                            sample.Id,
                            sample.SourceLanguage,
                            cues,
                            pair.Value.Good,
                            pair.Value.Bad,
                            judge,
                            "good",
                            "bad",
                            "adversarial",
                            cancellationToken);
                        var accumulator = judgeAccumulators[judgeName];
                        accumulator.RecordAdversarial(decision);
                        calibration[judgeName] = decision is null
                            ? null
                            : string.Equals(decision.Winner, "good", StringComparison.Ordinal);
                    }
                }
            }

            var candidateResults = new List<SourceUnitBenchmarkCandidateResult>();
            foreach (var candidate in candidates)
            {
                var name = CandidateName(candidate);
                var detection = await _sourceUnitDetectionService.DetectAsync(
                    new SourceUnitDetectionRequest
                    {
                        SourceLanguage = sample.SourceLanguage,
                        Cues = cues,
                        Mode = SourceUnitDetectionModes.Model,
                        ModelOverride = new SourceUnitDetectionModelOverride
                        {
                            Endpoint = candidate.Endpoint,
                            Model = candidate.Model,
                            ApiKey = candidate.ApiKey,
                            SystemPrompt = candidate.SystemPrompt ?? SourceUnitDetectionService.DefaultSystemPrompt,
                            UserPrompt = candidate.UserPrompt ?? SourceUnitDetectionService.DefaultUserPrompt,
                            TimeoutSeconds = candidate.TimeoutSeconds
                        }
                    },
                    cancellationToken);

                var modelCandidate = detection.Model;
                var valid = modelCandidate?.IsValid == true;
                var unitLength = valid ? modelCandidate!.UnitLength : (int?)null;
                var disagrees = valid && unitLength != heuristicUnitLength;
                var modelVotes = 0;
                var heuristicVotes = 0;
                var ties = 0;
                var decisiveVotes = 0;
                var majorityVotes = 0;

                if (disagrees && judges.Count > 0)
                {
                    var votes = new List<string>();
                    foreach (var judge in judges)
                    {
                        var judgeName = JudgeName(judge);
                        var decision = await RunBlindJudgeAsync(
                            sample.Id,
                            sample.SourceLanguage,
                            cues,
                            unitLength!.Value,
                            heuristicUnitLength,
                            judge,
                            "model",
                            "heuristic",
                            $"candidate:{name}",
                            cancellationToken);
                        judgeAccumulators[judgeName].RecordPairwise(decision);
                        if (decision is null)
                        {
                            continue;
                        }

                        votes.Add(decision.Winner);
                        if (decision.Winner == "model") modelVotes++;
                        else if (decision.Winner == "heuristic") heuristicVotes++;
                        else ties++;
                    }

                    decisiveVotes = modelVotes + heuristicVotes;
                    majorityVotes = Math.Max(modelVotes, heuristicVotes);
                }

                var adversarialTrials = calibration.Values.Count(value => value.HasValue);
                var adversarialPasses = calibration.Values.Count(value => value == true);
                candidateResults.Add(new SourceUnitBenchmarkCandidateResult
                {
                    Name = name,
                    Model = candidate.Model,
                    StructurallyValid = valid,
                    UnitLength = unitLength,
                    Error = valid ? null : modelCandidate?.Error ?? detection.FallbackReason,
                    BoundaryLatencyMs = modelCandidate?.LatencyMs,
                    DisagreesWithHeuristic = disagrees,
                    JudgeModelVotes = modelVotes,
                    JudgeHeuristicVotes = heuristicVotes,
                    JudgeTies = ties,
                    JudgeAgreementPercent = decisiveVotes == 0 ? null : majorityVotes * 100.0 / decisiveVotes,
                    AdversarialTrials = adversarialTrials,
                    AdversarialPasses = adversarialPasses
                });
            }

            sampleResults.Add(new SourceUnitBenchmarkSampleResult
            {
                SampleId = sample.Id,
                SourceLanguage = sample.SourceLanguage,
                Cues = cues,
                HeuristicUnitLength = heuristicUnitLength,
                CapturedProductionModelUnitLength = sample.ProductionModelUnitLength,
                CapturedProductionSelectedUnitLength = sample.ProductionSelectedUnitLength,
                CapturedProductionSelectedMethod = sample.ProductionSelectedMethod,
                Candidates = candidateResults
            });
        }

        var candidateSummaries = candidates.Select(candidate =>
        {
            var name = CandidateName(candidate);
            var results = sampleResults.SelectMany(sample => sample.Candidates).Where(result => result.Name == name).ToList();
            var valid = results.Count(result => result.StructurallyValid);
            var disagreements = results.Count(result => result.StructurallyValid && result.DisagreesWithHeuristic);
            var modelVotes = results.Sum(result => result.JudgeModelVotes);
            var heuristicVotes = results.Sum(result => result.JudgeHeuristicVotes);
            var ties = results.Sum(result => result.JudgeTies);
            var decisive = modelVotes + heuristicVotes;
            var agreements = results.Where(result => result.JudgeAgreementPercent.HasValue)
                .Select(result => result.JudgeAgreementPercent!.Value)
                .ToList();
            var latencies = results.Where(result => result.BoundaryLatencyMs.HasValue)
                .Select(result => (double)result.BoundaryLatencyMs!.Value)
                .ToList();
            var adversarialTrials = results.Sum(result => result.AdversarialTrials);
            var adversarialPasses = results.Sum(result => result.AdversarialPasses);

            return new SourceUnitBenchmarkCandidateSummary
            {
                Name = name,
                Model = candidate.Model,
                SamplesAttempted = results.Count,
                StructurallyValidSamples = valid,
                StructuralValidityPercent = Percent(valid, results.Count),
                DisagreementSamples = disagreements,
                DisagreementPercent = Percent(disagreements, valid),
                MeanBoundaryLatencyMs = Mean(latencies),
                JudgeModelVotes = modelVotes,
                JudgeHeuristicVotes = heuristicVotes,
                JudgeTies = ties,
                JudgePreferencePercent = Percent(modelVotes, decisive),
                MeanJudgeAgreementPercent = Mean(agreements),
                AdversarialTrials = adversarialTrials,
                AdversarialPassPercent = Percent(adversarialPasses, adversarialTrials)
            };
        }).ToList();

        var judgeSummaries = judgeAccumulators.Values.Select(accumulator => accumulator.ToSummary()).ToList();
        return new SourceUnitBenchmarkRunResult
        {
            SampleCount = sampleResults.Count,
            Candidates = candidateSummaries,
            Judges = judgeSummaries,
            Samples = sampleResults,
            Warnings = warnings
        };
    }

    private async Task<List<SourceUnitBenchmarkSample>> LoadSamplesAsync(
        SourceUnitBenchmarkRunRequest request,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var query = dbContext.SourceUnitBenchmarkSamples.AsNoTracking().AsQueryable();
        if (request.SampleIds is { Count: > 0 })
        {
            query = query.Where(sample => request.SampleIds.Contains(sample.Id));
        }
        if (!string.IsNullOrWhiteSpace(request.SourceLanguage))
        {
            query = query.Where(sample => sample.SourceLanguage == request.SourceLanguage);
        }

        return await query
            .OrderByDescending(sample => sample.Id)
            .Take(Math.Clamp(request.SampleLimit, 1, 5000))
            .ToListAsync(cancellationToken);
    }

    private async Task<List<SourceUnitBenchmarkModel>> ResolveCandidatesAsync(
        IReadOnlyList<SourceUnitBenchmarkModel> supplied,
        CancellationToken cancellationToken)
    {
        if (supplied.Count > 0)
        {
            return supplied.ToList();
        }

        var values = await _settings.GetSettings([
            SettingKeys.Translation.SourceUnitDetection.Endpoint,
            SettingKeys.Translation.SourceUnitDetection.Model,
            SettingKeys.Translation.SourceUnitDetection.SystemPrompt,
            SettingKeys.Translation.SourceUnitDetection.UserPrompt,
            SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds
        ]);
        var endpoint = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.Endpoint);
        var model = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.Model);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        return [new SourceUnitBenchmarkModel
        {
            Name = "configured source-boundary model",
            Endpoint = endpoint,
            Model = model,
            ApiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.SourceUnitDetection.ApiKey),
            SystemPrompt = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.SystemPrompt),
            UserPrompt = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.UserPrompt),
            TimeoutSeconds = ParsePositiveInt(values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds), 120)
        }];
    }

    private async Task<List<SourceUnitBenchmarkModel>> ResolveJudgesAsync(
        IReadOnlyList<SourceUnitBenchmarkModel> supplied,
        CancellationToken cancellationToken)
    {
        if (supplied.Count > 0)
        {
            return supplied.ToList();
        }

        var values = await _settings.GetSettings([
            SettingKeys.Translation.SourceUnitDetection.ValidatorEndpoint,
            SettingKeys.Translation.SourceUnitDetection.ValidatorModel,
            SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds
        ]);
        var endpoint = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.ValidatorEndpoint);
        var model = values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.ValidatorModel);
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        return [new SourceUnitBenchmarkModel
        {
            Name = "configured source-boundary validator",
            Endpoint = endpoint,
            Model = model,
            ApiKey = await _settings.GetEncryptedSetting(SettingKeys.Translation.SourceUnitDetection.ValidatorApiKey),
            SystemPrompt = DefaultJudgeSystemPrompt,
            UserPrompt = DefaultJudgeUserPrompt,
            TimeoutSeconds = ParsePositiveInt(values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds), 120)
        }];
    }

    private async Task<BlindJudgeDecision?> RunBlindJudgeAsync(
        int sampleId,
        string sourceLanguage,
        IReadOnlyList<SourceUnitDetectionCue> cues,
        int leftUnitLength,
        int rightUnitLength,
        SourceUnitBenchmarkModel judge,
        string leftLabel,
        string rightLabel,
        string salt,
        CancellationToken cancellationToken)
    {
        if (leftUnitLength == rightUnitLength)
        {
            return new BlindJudgeDecision("tie", 100, 100, 0);
        }

        try
        {
            var leftIsA = IsLeftCandidateA(sampleId, sourceLanguage, cues, leftUnitLength, rightUnitLength, JudgeName(judge), salt);
            var candidateA = leftIsA ? leftUnitLength : rightUnitLength;
            var candidateB = leftIsA ? rightUnitLength : leftUnitLength;
            var template = HasBlindPlaceholders(judge.UserPrompt) ? judge.UserPrompt! : DefaultJudgeUserPrompt;
            var userPrompt = template
                .Replace("{sourceLanguage}", sourceLanguage, StringComparison.Ordinal)
                .Replace("{candidateCount}", cues.Count.ToString(), StringComparison.Ordinal)
                .Replace("{sourceCuesJson}", JsonSerializer.Serialize(cues), StringComparison.Ordinal)
                .Replace("{candidateAUnitLength}", candidateA.ToString(), StringComparison.Ordinal)
                .Replace("{candidateBUnitLength}", candidateB.ToString(), StringComparison.Ordinal);

            var (content, latencyMs) = await RunModelAsync(
                judge,
                judge.SystemPrompt ?? DefaultJudgeSystemPrompt,
                userPrompt,
                CreateJudgeResponseFormat(),
                cancellationToken);
            var parsed = ParseJudgeDecision(content);
            if (parsed is null)
            {
                return null;
            }

            var winner = parsed.Value.Winner switch
            {
                "A" => leftIsA ? leftLabel : rightLabel,
                "B" => leftIsA ? rightLabel : leftLabel,
                _ => "tie"
            };
            var leftScore = leftIsA ? parsed.Value.CandidateAScore : parsed.Value.CandidateBScore;
            var rightScore = leftIsA ? parsed.Value.CandidateBScore : parsed.Value.CandidateAScore;
            return new BlindJudgeDecision(winner, leftScore, rightScore, latencyMs);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Source-unit benchmark judge {Judge} timed out.", JudgeName(judge));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source-unit benchmark judge {Judge} failed.", JudgeName(judge));
            return null;
        }
    }

    private async Task<(string Content, long LatencyMs)> RunModelAsync(
        SourceUnitBenchmarkModel model,
        string systemPrompt,
        string userPrompt,
        object responseFormat,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(model.Endpoint);
        var client = _httpClientFactory.CreateClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(model.TimeoutSeconds ?? 120, 5, 3600)));
        var stopwatch = Stopwatch.StartNew();

        var first = await SendOnceAsync(client, endpoint, model, systemPrompt, userPrompt, responseFormat, timeoutCts.Token);
        if (!first.Response.IsSuccessStatusCode && SupportsSchemaFallback(first.Response.StatusCode))
        {
            first.Response.Dispose();
            var second = await SendOnceAsync(client, endpoint, model, systemPrompt, userPrompt, null, timeoutCts.Token);
            using (second.Response)
            {
                if (!second.Response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"Source-unit benchmark request failed ({(int)second.Response.StatusCode}): {second.Body}");
                }
                stopwatch.Stop();
                return (ExtractAssistantContent(second.Body), stopwatch.ElapsedMilliseconds);
            }
        }

        using (first.Response)
        {
            if (!first.Response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Source-unit benchmark request failed ({(int)first.Response.StatusCode}): {first.Body}");
            }
            stopwatch.Stop();
            return (ExtractAssistantContent(first.Body), stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<HttpResult> SendOnceAsync(
        HttpClient client,
        Uri endpoint,
        SourceUnitBenchmarkModel model,
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
            name = "source_unit_benchmark_judgment",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    winner = new { type = "string", @enum = new[] { "A", "B", "tie" } },
                    candidateAScore = new { type = "number", minimum = 0, maximum = 100 },
                    candidateBScore = new { type = "number", minimum = 0, maximum = 100 },
                    reason = new { type = "string" }
                },
                required = new[] { "winner", "candidateAScore", "candidateBScore", "reason" },
                additionalProperties = false
            }
        }
    };

    private static (string Winner, double CandidateAScore, double CandidateBScore)? ParseJudgeDecision(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJsonObject(content));
            var root = document.RootElement;
            var winner = root.GetProperty("winner").GetString();
            if (winner is not ("A" or "B" or "tie")) return null;
            return (winner, root.GetProperty("candidateAScore").GetDouble(), root.GetProperty("candidateBScore").GetDouble());
        }
        catch
        {
            return null;
        }
    }

    private static (int Good, int Bad)? BuildAdversarialCalibrationPair(
        IReadOnlyList<SourceUnitDetectionCue> cues,
        int heuristicUnitLength)
    {
        if (cues.Count <= 1) return null;

        // Only create calibration trials around high-confidence surface cues. This avoids pretending
        // that every extreme boundary is objectively wrong when the source itself is ambiguous.
        if (heuristicUnitLength == 1 && EndsWithStrongTerminal(cues[0].Text))
        {
            return (1, cues.Count);
        }

        if (heuristicUnitLength > 1 &&
            (EndsWithContinuationPunctuation(cues[0].Text) || StartsWithLowercase(cues[1].Text)))
        {
            return (heuristicUnitLength, 1);
        }

        if (heuristicUnitLength < cues.Count && EndsWithStrongTerminal(cues[heuristicUnitLength - 1].Text))
        {
            return (heuristicUnitLength, cues.Count);
        }

        return null;
    }

    private static bool EndsWithStrongTerminal(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd('"', '\'', '”', '’', ')', ']', '}');
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!');
    }

    private static bool EndsWithContinuationPunctuation(string text)
    {
        var trimmed = text.TrimEnd().TrimEnd('"', '\'', '”', '’', ')', ']', '}');
        return trimmed.EndsWith(',') || trimmed.EndsWith(';') || trimmed.EndsWith(':') ||
               trimmed.EndsWith('—') || trimmed.EndsWith('-');
    }

    private static bool StartsWithLowercase(string text)
    {
        var firstLetter = text.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsLower(firstLetter);
    }

    private static bool IsLeftCandidateA(
        int sampleId,
        string sourceLanguage,
        IReadOnlyList<SourceUnitDetectionCue> cues,
        int left,
        int right,
        string judge,
        string salt)
    {
        var low = Math.Min(left, right);
        var high = Math.Max(left, right);
        var material = string.Join("\n", sampleId, sourceLanguage, JsonSerializer.Serialize(cues), $"{low}|{high}", judge, salt);
        return (SHA256.HashData(Encoding.UTF8.GetBytes(material))[0] & 1) == 0;
    }

    private static string ExtractAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var choice = document.RootElement.GetProperty("choices")[0];
        if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? string.Empty;
            if (content.ValueKind == JsonValueKind.Array)
            {
                return string.Concat(content.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString() ?? string.Empty));
            }
            return content.GetRawText();
        }
        if (choice.TryGetProperty("text", out var text)) return text.GetString() ?? string.Empty;
        throw new JsonException("OpenAI-compatible response did not contain assistant content.");
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
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end >= start ? trimmed[start..(end + 1)] : trimmed;
    }

    private static Uri BuildChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid source-unit benchmark endpoint: '{endpoint}'.");
        }
        var path = baseUri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return baseUri;
        return new Uri(trimmed + (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/chat/completions" : "/v1/chat/completions"));
    }

    private static bool SupportsSchemaFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static bool HasBlindPlaceholders(string? prompt) =>
        !string.IsNullOrWhiteSpace(prompt) &&
        prompt.Contains("{candidateAUnitLength}", StringComparison.Ordinal) &&
        prompt.Contains("{candidateBUnitLength}", StringComparison.Ordinal);

    private static string CandidateName(SourceUnitBenchmarkModel model) =>
        string.IsNullOrWhiteSpace(model.Name) ? model.Model : model.Name;

    private static string JudgeName(SourceUnitBenchmarkModel model) =>
        string.IsNullOrWhiteSpace(model.Name) ? model.Model : model.Name;

    private static double Percent(int numerator, int denominator) =>
        denominator == 0 ? 0 : numerator * 100.0 / denominator;

    private static double Mean(IReadOnlyList<double> values) => values.Count == 0 ? 0 : values.Average();

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    private static string Fingerprint(string sourceLanguage, string cuesJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceLanguage + "\n" + cuesJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IReadOnlyList<SourceUnitDetectionCue> DeserializeCues(string json) =>
        JsonSerializer.Deserialize<List<SourceUnitDetectionCue>>(json) ?? [];

    private static SourceUnitBenchmarkSampleView ToView(SourceUnitBenchmarkSample sample) => new()
    {
        Id = sample.Id,
        CreatedAt = sample.CreatedAt,
        SourceLanguage = sample.SourceLanguage,
        Cues = DeserializeCues(sample.CandidateCuesJson),
        CandidateCount = sample.CandidateCount,
        HeuristicUnitLength = sample.HeuristicUnitLength,
        ProductionMode = sample.ProductionMode,
        ProductionModelUnitLength = sample.ProductionModelUnitLength,
        ProductionModelIsValid = sample.ProductionModelIsValid,
        ProductionModelError = sample.ProductionModelError,
        ProductionModelLatencyMs = sample.ProductionModelLatencyMs,
        ProductionValidatorWinner = sample.ProductionValidatorWinner,
        ProductionValidatorModel = sample.ProductionValidatorModel,
        ProductionValidatorModelScore = sample.ProductionValidatorModelScore,
        ProductionValidatorHeuristicScore = sample.ProductionValidatorHeuristicScore,
        ProductionValidatorLatencyMs = sample.ProductionValidatorLatencyMs,
        ProductionSelectedUnitLength = sample.ProductionSelectedUnitLength,
        ProductionSelectedMethod = sample.ProductionSelectedMethod,
        TranslationRequestId = sample.TranslationRequestId,
        StartPosition = sample.StartPosition,
        EndPosition = sample.EndPosition
    };

    private static void ApplyProductionDecision(
        SourceUnitBenchmarkSample entity,
        SourceUnitBenchmarkCaptureRequest request)
    {
        entity.HeuristicUnitLength = request.Detection.Heuristic.UnitLength;
        entity.ProductionMode = request.Detection.Mode;
        entity.ProductionModelUnitLength = request.Detection.Model?.UnitLength;
        entity.ProductionModelIsValid = request.Detection.Model?.IsValid;
        entity.ProductionModelError = request.Detection.Model?.Error;
        entity.ProductionModelLatencyMs = request.Detection.Model?.LatencyMs;
        entity.ProductionValidatorWinner = request.Detection.Validator?.Winner;
        entity.ProductionValidatorModel = request.Detection.Validator?.Model;
        entity.ProductionValidatorModelScore = request.Detection.Validator?.ModelScore;
        entity.ProductionValidatorHeuristicScore = request.Detection.Validator?.HeuristicScore;
        entity.ProductionValidatorLatencyMs = request.Detection.Validator?.LatencyMs;
        entity.ProductionSelectedUnitLength = request.Detection.UnitLength;
        entity.ProductionSelectedMethod = request.Detection.SelectedMethod;
        entity.TranslationRequestId ??= request.TranslationRequestId;
        entity.StartPosition ??= request.StartPosition;
        entity.EndPosition ??= request.EndPosition;
    }

    private sealed record HttpResult(HttpResponseMessage Response, string Body);
    private sealed record BlindJudgeDecision(string Winner, double LeftScore, double RightScore, long LatencyMs);

    private sealed class JudgeAccumulator(string name, string model)
    {
        private long _latencyMs;
        private int _calls;
        public int PairwiseComparisons { get; private set; }
        public int DecisiveComparisons { get; private set; }
        public int AdversarialTrials { get; private set; }
        public int AdversarialPasses { get; private set; }

        public void RecordPairwise(BlindJudgeDecision? decision)
        {
            if (decision is null) return;
            PairwiseComparisons++;
            if (decision.Winner != "tie") DecisiveComparisons++;
            _latencyMs += decision.LatencyMs;
            _calls++;
        }

        public void RecordAdversarial(BlindJudgeDecision? decision)
        {
            if (decision is null) return;
            AdversarialTrials++;
            if (decision.Winner == "good") AdversarialPasses++;
            _latencyMs += decision.LatencyMs;
            _calls++;
        }

        public SourceUnitBenchmarkJudgeSummary ToSummary() => new()
        {
            Name = name,
            Model = model,
            PairwiseComparisons = PairwiseComparisons,
            DecisiveComparisons = DecisiveComparisons,
            AdversarialTrials = AdversarialTrials,
            AdversarialPassPercent = Percent(AdversarialPasses, AdversarialTrials),
            MeanLatencyMs = _calls == 0 ? 0 : _latencyMs / (double)_calls
        };
    }
}
