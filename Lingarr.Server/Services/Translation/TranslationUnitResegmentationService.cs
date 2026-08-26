using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Resegments translated linguistic units back onto subtitle timing slots. The deterministic
/// algorithm is retained as a baseline and fail-safe, while model and validated modes can use
/// independently hosted OpenAI-compatible models for semantic alignment and judging.
/// </summary>
public sealed class TranslationUnitResegmentationService : ITranslationUnitResegmentationService
{
    public const string DefaultSystemPrompt = """
        You are a subtitle alignment engine. Split an existing target-language translation across the exact number of source subtitle timing slots. Do not translate, paraphrase, omit, duplicate, or reorder text. Preserve the translated wording exactly except for boundary whitespace. Return JSON only.
        """;

    public const string DefaultUserPrompt = """
        Source language: {sourceLanguage}
        Target language: {targetLanguage}
        Segment count: {segmentCount}

        Source subtitle segments:
        {sourceSegmentsJson}

        Target translation to align:
        {translatedUnit}

        Return exactly {segmentCount} target segments in JSON as {"segments":["...", "..."]}.
        """;

    public const string DefaultValidatorSystemPrompt = """
        You are a subtitle segmentation judge. Compare Candidate A and Candidate B, which are two segmentations of exactly the same target translation against the same source timing segments. Their origins are intentionally hidden and their A/B order is randomized. Judge semantic alignment of each target segment to its source slot, readability, punctuation, and balance. Do not infer or favor how either candidate was produced. Do not reward changes to the translation wording. Return JSON only.
        """;

    public const string DefaultValidatorUserPrompt = """
        Source language: {sourceLanguage}
        Target language: {targetLanguage}

        Source subtitle segments:
        {sourceSegmentsJson}

        Complete target translation:
        {translatedUnit}

        Candidate A segmentation:
        {candidateASegmentsJson}

        Candidate B segmentation:
        {candidateBSegmentsJson}

        Choose which candidate better aligns the unchanged target translation to the source timing slots. The candidates' origins are deliberately undisclosed. Return JSON with winner ("A" or "B"), candidateAScore (0-100), candidateBScore (0-100), and reason.
        """;

    private readonly ISettingService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TranslationUnitResegmentationService> _logger;

    public TranslationUnitResegmentationService(
        ISettingService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<TranslationUnitResegmentationService> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<TranslationUnitResegmentationResult> ResegmentAsync(
        TranslationUnitResegmentationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceSegments.Count <= 1)
        {
            var deterministic = BuildDeterministicCandidate(request.TranslatedUnit, request.SourceSegments);
            return new TranslationUnitResegmentationResult
            {
                Mode = ResegmentationModes.Deterministic,
                SelectedMethod = "deterministic",
                Segments = deterministic.Segments ?? [request.TranslatedUnit.Trim()],
                Deterministic = deterministic
            };
        }

        var evaluation = await EvaluateInternalAsync(
            new ResegmentationEvaluationRequest
            {
                SourceLanguage = request.SourceLanguage,
                TargetLanguage = request.TargetLanguage,
                SourceSegments = request.SourceSegments,
                TranslatedUnit = request.TranslatedUnit
            },
            cancellationToken);

        return new TranslationUnitResegmentationResult
        {
            Mode = evaluation.Mode,
            SelectedMethod = evaluation.SelectedMethod,
            Segments = evaluation.SelectedSegments,
            Deterministic = evaluation.Deterministic,
            Model = evaluation.Model,
            Validator = evaluation.Validator,
            FallbackReason = evaluation.FallbackReason
        };
    }

    public Task<ResegmentationEvaluationResult> EvaluateAsync(
        ResegmentationEvaluationRequest request,
        CancellationToken cancellationToken) =>
        EvaluateInternalAsync(request, cancellationToken);

    private async Task<ResegmentationEvaluationResult> EvaluateInternalAsync(
        ResegmentationEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceSegments.Count == 0)
        {
            throw new ArgumentException("At least one source segment is required.", nameof(request));
        }

        var mode = ResegmentationModes.Normalise(
            request.Mode ?? await _settings.GetSetting(SettingKeys.Translation.Resegmentation.Mode));
        var deterministic = BuildDeterministicCandidate(request.TranslatedUnit, request.SourceSegments);

        ResegmentationCandidate? modelCandidate = null;
        ResegmentationValidatorDecision? validator = null;
        string? fallbackReason = null;
        var selectedMethod = "deterministic";
        var selectedSegments = deterministic.Segments!;

        if (mode is ResegmentationModes.Model or ResegmentationModes.Validated)
        {
            var modelConfig = await GetModelConfigurationAsync(request.ModelOverride, validator: false);
            modelCandidate = await BuildModelCandidateAsync(request, modelConfig, cancellationToken);

            if (modelCandidate.Validation.IsValid && modelCandidate.Segments is not null)
            {
                selectedMethod = "model";
                selectedSegments = modelCandidate.Segments;

                if (mode == ResegmentationModes.Validated)
                {
                    var validatorConfig = await GetModelConfigurationAsync(request.ValidatorOverride, validator: true);
                    validator = await TryValidateCandidatesAsync(
                        request,
                        modelCandidate.Segments,
                        deterministic.Segments!,
                        validatorConfig,
                        cancellationToken);

                    if (validator is not null)
                    {
                        if (string.Equals(validator.Winner, "deterministic", StringComparison.OrdinalIgnoreCase))
                        {
                            selectedMethod = "deterministic";
                            selectedSegments = deterministic.Segments!;
                        }
                    }
                    else
                    {
                        fallbackReason = "Validator was unavailable or returned an invalid decision; using the structurally valid model segmentation.";
                    }
                }
            }
            else
            {
                fallbackReason = modelCandidate.Error ?? modelCandidate.Validation.Error ??
                                 "Model segmentation failed structural validation; deterministic segmentation used.";
                _logger.LogWarning("Model-assisted subtitle resegmentation rejected: {Reason}", fallbackReason);
            }
        }

        ResegmentationStructuralValidation? referenceValidation = null;
        ResegmentationBoundaryMetrics? deterministicMetrics = null;
        ResegmentationBoundaryMetrics? modelMetrics = null;

        if (request.ReferenceSegments is not null)
        {
            referenceValidation = ValidateSegments(
                request.ReferenceSegments,
                request.SourceSegments,
                request.TranslatedUnit);

            if (referenceValidation.IsValid)
            {
                deterministicMetrics = CalculateBoundaryMetrics(
                    deterministic.Segments!,
                    request.ReferenceSegments);

                if (modelCandidate?.Validation.IsValid == true && modelCandidate.Segments is not null)
                {
                    modelMetrics = CalculateBoundaryMetrics(modelCandidate.Segments, request.ReferenceSegments);
                }
            }
        }

        return new ResegmentationEvaluationResult
        {
            Mode = mode,
            SelectedMethod = selectedMethod,
            SelectedSegments = selectedSegments,
            Deterministic = deterministic,
            Model = modelCandidate,
            Validator = validator,
            ReferenceValidation = referenceValidation,
            DeterministicReferenceMetrics = deterministicMetrics,
            ModelReferenceMetrics = modelMetrics,
            FallbackReason = fallbackReason
        };
    }

    private static ResegmentationCandidate BuildDeterministicCandidate(
        string translatedUnit,
        IReadOnlyList<string> sourceSegments)
    {
        var segments = SentenceAwareTranslationUnitService.ResegmentTranslation(translatedUnit, sourceSegments);
        return new ResegmentationCandidate
        {
            Method = "deterministic",
            Segments = segments,
            Validation = ValidateSegments(segments, sourceSegments, translatedUnit),
            LatencyMs = 0
        };
    }

    private async Task<ResegmentationCandidate> BuildModelCandidateAsync(
        ResegmentationEvaluationRequest request,
        ModelConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model))
        {
            return InvalidCandidate(
                "model",
                "Dedicated resegmentation endpoint and model must both be configured.",
                config.Model);
        }

        try
        {
            var userPrompt = RenderAlignmentPrompt(config.UserPrompt, request);
            var responseFormat = CreateSegmentationResponseFormat(request.SourceSegments.Count);
            var (content, latencyMs) = await SendChatCompletionAsync(
                config,
                config.SystemPrompt,
                userPrompt,
                responseFormat,
                cancellationToken);
            var segments = ParseSegments(content);
            var validation = ValidateSegments(segments, request.SourceSegments, request.TranslatedUnit);

            return new ResegmentationCandidate
            {
                Method = "model",
                Segments = segments,
                Validation = validation,
                Error = validation.IsValid ? null : validation.Error,
                LatencyMs = latencyMs,
                Model = config.Model
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InvalidCandidate(
                "model",
                $"Dedicated resegmentation model timed out after {config.TimeoutSeconds} seconds.",
                config.Model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dedicated resegmentation model call failed.");
            return InvalidCandidate("model", ex.Message, config.Model);
        }
    }

    private async Task<ResegmentationValidatorDecision?> TryValidateCandidatesAsync(
        ResegmentationEvaluationRequest request,
        IReadOnlyList<string> modelSegments,
        IReadOnlyList<string> deterministicSegments,
        ModelConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model))
        {
            _logger.LogWarning("Validated resegmentation requested but validator endpoint/model is not configured.");
            return null;
        }

        try
        {
            var modelIsCandidateA = IsModelCandidateA(request, modelSegments, deterministicSegments);
            var candidateASegments = modelIsCandidateA ? modelSegments : deterministicSegments;
            var candidateBSegments = modelIsCandidateA ? deterministicSegments : modelSegments;
            var userPrompt = RenderValidatorPrompt(
                config.UserPrompt,
                request,
                candidateASegments,
                candidateBSegments,
                modelSegments,
                deterministicSegments);
            var (content, latencyMs) = await SendChatCompletionAsync(
                config,
                config.SystemPrompt,
                userPrompt,
                CreateValidatorResponseFormat(),
                cancellationToken);
            var decision = ParseValidatorDecision(content);

            if (decision is null)
            {
                _logger.LogWarning("Validator returned an invalid JSON decision.");
                return null;
            }

            string winner;
            double modelScore;
            double deterministicScore;
            if (decision.IsBlind)
            {
                var candidateAWon = string.Equals(decision.Winner, "A", StringComparison.OrdinalIgnoreCase);
                winner = candidateAWon == modelIsCandidateA ? "model" : "deterministic";
                modelScore = modelIsCandidateA ? decision.FirstScore : decision.SecondScore;
                deterministicScore = modelIsCandidateA ? decision.SecondScore : decision.FirstScore;
            }
            else
            {
                // Backward compatibility for explicitly customized legacy validator prompts.
                winner = decision.Winner;
                modelScore = decision.FirstScore;
                deterministicScore = decision.SecondScore;
            }

            return new ResegmentationValidatorDecision
            {
                Winner = winner,
                ModelScore = modelScore,
                DeterministicScore = deterministicScore,
                Reason = decision.Reason,
                LatencyMs = latencyMs,
                Model = config.Model
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Resegmentation validator timed out after {TimeoutSeconds} seconds.", config.TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resegmentation validator call failed.");
            return null;
        }
    }

    private async Task<ModelConfiguration> GetModelConfigurationAsync(
        ResegmentationModelOverride? overrides,
        bool validator)
    {
        var keys = validator
            ? new[]
            {
                SettingKeys.Translation.Resegmentation.ValidatorEndpoint,
                SettingKeys.Translation.Resegmentation.ValidatorModel,
                SettingKeys.Translation.Resegmentation.ValidatorSystemPrompt,
                SettingKeys.Translation.Resegmentation.ValidatorUserPrompt,
                SettingKeys.Translation.Resegmentation.TimeoutSeconds
            }
            : new[]
            {
                SettingKeys.Translation.Resegmentation.Endpoint,
                SettingKeys.Translation.Resegmentation.Model,
                SettingKeys.Translation.Resegmentation.SystemPrompt,
                SettingKeys.Translation.Resegmentation.UserPrompt,
                SettingKeys.Translation.Resegmentation.TimeoutSeconds
            };

        var values = await _settings.GetSettings(keys);
        var apiKey = overrides?.ApiKey;
        if (apiKey is null)
        {
            apiKey = await _settings.GetEncryptedSetting(
                validator
                    ? SettingKeys.Translation.Resegmentation.ValidatorApiKey
                    : SettingKeys.Translation.Resegmentation.ApiKey);
        }

        var endpointKey = validator
            ? SettingKeys.Translation.Resegmentation.ValidatorEndpoint
            : SettingKeys.Translation.Resegmentation.Endpoint;
        var modelKey = validator
            ? SettingKeys.Translation.Resegmentation.ValidatorModel
            : SettingKeys.Translation.Resegmentation.Model;
        var systemPromptKey = validator
            ? SettingKeys.Translation.Resegmentation.ValidatorSystemPrompt
            : SettingKeys.Translation.Resegmentation.SystemPrompt;
        var userPromptKey = validator
            ? SettingKeys.Translation.Resegmentation.ValidatorUserPrompt
            : SettingKeys.Translation.Resegmentation.UserPrompt;

        var defaultSystemPrompt = validator ? DefaultValidatorSystemPrompt : DefaultSystemPrompt;
        var defaultUserPrompt = validator ? DefaultValidatorUserPrompt : DefaultUserPrompt;
        var timeout = overrides?.TimeoutSeconds ?? ParseTimeout(values.GetValueOrDefault(
            SettingKeys.Translation.Resegmentation.TimeoutSeconds));

        return new ModelConfiguration(
            overrides?.Endpoint ?? values.GetValueOrDefault(endpointKey) ?? string.Empty,
            overrides?.Model ?? values.GetValueOrDefault(modelKey) ?? string.Empty,
            apiKey ?? string.Empty,
            overrides?.SystemPrompt ?? values.GetValueOrDefault(systemPromptKey) ?? defaultSystemPrompt,
            overrides?.UserPrompt ?? values.GetValueOrDefault(userPromptKey) ?? defaultUserPrompt,
            Math.Clamp(timeout, 5, 3600));
    }

    private async Task<(string Content, long LatencyMs)> SendChatCompletionAsync(
        ModelConfiguration config,
        string systemPrompt,
        string userPrompt,
        object responseFormat,
        CancellationToken cancellationToken)
    {
        var endpoint = BuildChatCompletionsEndpoint(config.Endpoint);
        var client = _httpClientFactory.CreateClient();
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));

        var first = await SendOnceAsync(
            client,
            endpoint,
            config,
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
                config,
                systemPrompt,
                userPrompt,
                null,
                timeoutCts.Token);
            using (second.Response)
            {
                var body = second.Body;
                if (!second.Response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"OpenAI-compatible resegmentation request failed ({(int)second.Response.StatusCode}): {body}");
                }

                stopwatch.Stop();
                return (ExtractAssistantContent(body), stopwatch.ElapsedMilliseconds);
            }
        }

        using (first.Response)
        {
            if (!first.Response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"OpenAI-compatible resegmentation request failed ({(int)first.Response.StatusCode}): {first.Body}");
            }

            stopwatch.Stop();
            return (ExtractAssistantContent(first.Body), stopwatch.ElapsedMilliseconds);
        }
    }

    private static async Task<HttpResult> SendOnceAsync(
        HttpClient client,
        Uri endpoint,
        ModelConfiguration config,
        string systemPrompt,
        string userPrompt,
        object? responseFormat,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = config.Model,
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
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResult(response, body);
    }

    private static bool SupportsSchemaFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static Uri BuildChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid resegmentation endpoint: '{endpoint}'.");
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

    private static object CreateSegmentationResponseFormat(int segmentCount) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "subtitle_resegmentation",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    segments = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        minItems = segmentCount,
                        maxItems = segmentCount
                    }
                },
                required = new[] { "segments" },
                additionalProperties = false
            }
        }
    };

    private static object CreateValidatorResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "subtitle_resegmentation_validation",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    winner = new { type = "string", @enum = new[] { "A", "B" } },
                    candidateAScore = new { type = "number", minimum = 0, maximum = 100 },
                    candidateBScore = new { type = "number", minimum = 0, maximum = 100 },
                    reason = new { type = "string" }
                },
                required = new[] { "winner", "candidateAScore", "candidateBScore", "reason" },
                additionalProperties = false
            }
        }
    };

    private static string RenderAlignmentPrompt(
        string template,
        ResegmentationEvaluationRequest request)
    {
        return template
            .Replace("{sourceLanguage}", request.SourceLanguage, StringComparison.Ordinal)
            .Replace("{targetLanguage}", request.TargetLanguage, StringComparison.Ordinal)
            .Replace("{segmentCount}", request.SourceSegments.Count.ToString(), StringComparison.Ordinal)
            .Replace("{sourceSegmentsJson}", JsonSerializer.Serialize(request.SourceSegments), StringComparison.Ordinal)
            .Replace("{translatedUnit}", request.TranslatedUnit, StringComparison.Ordinal);
    }

    private static string RenderValidatorPrompt(
        string template,
        ResegmentationEvaluationRequest request,
        IReadOnlyList<string> candidateASegments,
        IReadOnlyList<string> candidateBSegments,
        IReadOnlyList<string> modelSegments,
        IReadOnlyList<string> deterministicSegments)
    {
        return template
            .Replace("{sourceLanguage}", request.SourceLanguage, StringComparison.Ordinal)
            .Replace("{targetLanguage}", request.TargetLanguage, StringComparison.Ordinal)
            .Replace("{segmentCount}", request.SourceSegments.Count.ToString(), StringComparison.Ordinal)
            .Replace("{sourceSegmentsJson}", JsonSerializer.Serialize(request.SourceSegments), StringComparison.Ordinal)
            .Replace("{translatedUnit}", request.TranslatedUnit, StringComparison.Ordinal)
            .Replace("{candidateASegmentsJson}", JsonSerializer.Serialize(candidateASegments), StringComparison.Ordinal)
            .Replace("{candidateBSegmentsJson}", JsonSerializer.Serialize(candidateBSegments), StringComparison.Ordinal)
            // Keep legacy placeholders usable only for intentionally customized old prompts.
            .Replace("{modelSegmentsJson}", JsonSerializer.Serialize(modelSegments), StringComparison.Ordinal)
            .Replace("{deterministicSegmentsJson}", JsonSerializer.Serialize(deterministicSegments), StringComparison.Ordinal);
    }

    private static bool IsModelCandidateA(
        ResegmentationEvaluationRequest request,
        IReadOnlyList<string> modelSegments,
        IReadOnlyList<string> deterministicSegments)
    {
        var left = JsonSerializer.Serialize(modelSegments);
        var right = JsonSerializer.Serialize(deterministicSegments);
        var ordered = new[] { left, right };
        Array.Sort(ordered, StringComparer.Ordinal);
        var material = string.Join("\n",
            request.SourceLanguage,
            request.TargetLanguage,
            JsonSerializer.Serialize(request.SourceSegments),
            request.TranslatedUnit,
            ordered[0],
            ordered[1]);
        return (SHA256.HashData(Encoding.UTF8.GetBytes(material))[0] & 1) == 0;
    }

    private static string ExtractAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new JsonException("OpenAI-compatible response did not contain choices.");
        }

        var choice = choices[0];
        if (choice.TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (content.ValueKind == JsonValueKind.Array)
            {
                var pieces = new List<string>();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        pieces.Add(text.GetString() ?? string.Empty);
                    }
                }
                return string.Concat(pieces);
            }
        }

        if (choice.TryGetProperty("text", out var legacyText) && legacyText.ValueKind == JsonValueKind.String)
        {
            return legacyText.GetString() ?? string.Empty;
        }

        throw new JsonException("OpenAI-compatible response did not contain assistant content.");
    }

    private static IReadOnlyList<string> ParseSegments(string content)
    {
        var json = ExtractJson(content);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("segments", out var segments)
                ? segments
                : throw new JsonException("Resegmentation response did not contain a segments array.");

        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Resegmentation segments value was not an array.");
        }

        return array.EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToList();
    }

    private static ValidatorPayload? ParseValidatorDecision(string content)
    {
        try
        {
            var json = ExtractJson(content);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var winner = root.GetProperty("winner").GetString();
            var reason = root.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() : null;

            if (winner is "A" or "B")
            {
                return new ValidatorPayload(
                    winner,
                    root.GetProperty("candidateAScore").GetDouble(),
                    root.GetProperty("candidateBScore").GetDouble(),
                    true,
                    reason);
            }

            // Backward compatibility for user-authored prompts created before blind A/B validation.
            if (winner is "model" or "deterministic")
            {
                return new ValidatorPayload(
                    winner,
                    root.GetProperty("modelScore").GetDouble(),
                    root.GetProperty("deterministicScore").GetDouble(),
                    false,
                    reason);
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ExtractJson(string content)
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

        var objectStart = trimmed.IndexOf('{');
        var arrayStart = trimmed.IndexOf('[');
        var start = objectStart < 0
            ? arrayStart
            : arrayStart < 0
                ? objectStart
                : Math.Min(objectStart, arrayStart);
        var objectEnd = trimmed.LastIndexOf('}');
        var arrayEnd = trimmed.LastIndexOf(']');
        var end = Math.Max(objectEnd, arrayEnd);

        if (start >= 0 && end >= start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    public static ResegmentationStructuralValidation ValidateSegments(
        IReadOnlyList<string> segments,
        IReadOnlyList<string> sourceSegments,
        string translatedUnit)
    {
        var countMatches = segments.Count == sourceSegments.Count;
        var nonEmpty = countMatches && segments
            .Select((segment, index) => (segment, index))
            .All(entry => string.IsNullOrWhiteSpace(sourceSegments[entry.index]) ||
                          !string.IsNullOrWhiteSpace(entry.segment));
        var textPreserved = string.Equals(
            NormaliseWhitespace(string.Join(" ", segments)),
            NormaliseWhitespace(translatedUnit),
            StringComparison.Ordinal);
        var valid = countMatches && nonEmpty && textPreserved;

        string? error = null;
        if (!countMatches)
        {
            error = $"Expected {sourceSegments.Count} segments but received {segments.Count}.";
        }
        else if (!nonEmpty)
        {
            error = "At least one non-empty source timing slot received an empty target segment.";
        }
        else if (!textPreserved)
        {
            error = "Model segmentation changed, omitted, duplicated, or reordered target text.";
        }

        return new ResegmentationStructuralValidation
        {
            IsValid = valid,
            CountMatches = countMatches,
            NonEmptySegments = nonEmpty,
            TextPreserved = textPreserved,
            Error = error
        };
    }

    public static ResegmentationBoundaryMetrics CalculateBoundaryMetrics(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> reference)
    {
        if (candidate.Count != reference.Count)
        {
            throw new ArgumentException("Candidate and reference segment counts must match.");
        }

        var candidateBoundaries = GetBoundaries(candidate);
        var referenceBoundaries = GetBoundaries(reference);
        var errors = candidateBoundaries
            .Zip(referenceBoundaries, (candidateBoundary, referenceBoundary) =>
                Math.Abs(candidateBoundary - referenceBoundary))
            .ToArray();
        var exactMatches = candidate.Zip(reference, (left, right) =>
                string.Equals(NormaliseWhitespace(left), NormaliseWhitespace(right), StringComparison.Ordinal))
            .Count(match => match);

        return new ResegmentationBoundaryMetrics
        {
            BoundaryCount = errors.Length,
            MeanAbsoluteErrorCharacters = errors.Length == 0 ? 0 : errors.Average(),
            MaxAbsoluteErrorCharacters = errors.Length == 0 ? 0 : errors.Max(),
            BoundariesWithinFiveCharactersPercent = errors.Length == 0
                ? 100
                : errors.Count(error => error <= 5) * 100.0 / errors.Length,
            ExactSegmentMatchPercent = candidate.Count == 0
                ? 100
                : exactMatches * 100.0 / candidate.Count
        };
    }

    private static int[] GetBoundaries(IReadOnlyList<string> segments)
    {
        if (segments.Count <= 1)
        {
            return [];
        }

        var boundaries = new int[segments.Count - 1];
        var position = 0;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            position += NormaliseWhitespace(segments[index]).Length;
            boundaries[index] = position;
            position += 1;
        }
        return boundaries;
    }

    private static string NormaliseWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static ResegmentationCandidate InvalidCandidate(string method, string error, string? model) => new()
    {
        Method = method,
        Segments = null,
        Model = model,
        Error = error,
        Validation = new ResegmentationStructuralValidation
        {
            IsValid = false,
            CountMatches = false,
            NonEmptySegments = false,
            TextPreserved = false,
            Error = error
        }
    };

    private static int ParseTimeout(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : 120;

    private sealed record ModelConfiguration(
        string Endpoint,
        string Model,
        string ApiKey,
        string SystemPrompt,
        string UserPrompt,
        int TimeoutSeconds);

    private sealed record HttpResult(HttpResponseMessage Response, string Body);

    private sealed record ValidatorPayload(
        string Winner,
        double FirstScore,
        double SecondScore,
        bool IsBlind,
        string? Reason);
}
