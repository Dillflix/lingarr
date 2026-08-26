using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Decides how many consecutive source subtitle cues should be translated as one linguistic unit.
/// The current punctuation/timing heuristic is retained as the deterministic baseline and fallback;
/// model and validated modes may use independently hosted OpenAI-compatible models.
/// </summary>
public sealed class SourceUnitDetectionService : ISourceUnitDetectionService
{
    private const int MaxGapMs = 2000;

    public const string DefaultSystemPrompt = """
        You are a subtitle source-unit boundary detector. Given consecutive source-language subtitle timing cues, decide how many leading cues, starting with cue 1, belong to the same complete linguistic utterance that should be translated together. Do not translate, rewrite, summarize, or add context. Separate distinct sentences, speakers, or dialogue turns. Return JSON only.
        """;

    public const string DefaultUserPrompt = """
        Source language: {sourceLanguage}
        Candidate cue count: {candidateCount}

        Consecutive subtitle cues (position, timing in milliseconds, text):
        {sourceCuesJson}

        Choose a contiguous prefix beginning with cue 1. Return unitLength from 1 through {candidateCount}. The selected cues should form one linguistic translation unit; later cues are only candidates and must not be included merely because they provide useful context.

        Return JSON as {"unitLength":2}.
        """;

    public const string DefaultValidatorSystemPrompt = """
        You are a subtitle source-unit segmentation judge. Compare two proposed boundaries for the same consecutive source-language subtitle cues. Decide which grouping better identifies one complete linguistic unit beginning at cue 1. Prefer semantic/syntactic completeness while avoiding unrelated sentences or speaker turns. Return JSON only.
        """;

    public const string DefaultValidatorUserPrompt = """
        Source language: {sourceLanguage}
        Candidate cue count: {candidateCount}

        Consecutive subtitle cues:
        {sourceCuesJson}

        Model proposal unitLength: {modelUnitLength}
        Heuristic proposal unitLength: {heuristicUnitLength}

        Choose which proposal better identifies exactly one linguistic translation unit beginning at cue 1. Return JSON with winner ("model" or "heuristic"), modelScore (0-100), heuristicScore (0-100), and reason.
        """;

    private readonly ISettingService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SourceUnitDetectionService> _logger;

    public SourceUnitDetectionService(
        ISettingService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<SourceUnitDetectionService> logger)
    {
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<SourceUnitDetectionResult> DetectAsync(
        SourceUnitDetectionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Cues.Count == 0)
        {
            throw new ArgumentException("At least one source cue is required.", nameof(request));
        }

        if (request.Cues.Count == 1)
        {
            var single = new SourceUnitDetectionCandidate
            {
                Method = SourceUnitDetectionModes.Heuristic,
                UnitLength = 1,
                IsValid = true,
                LatencyMs = 0
            };
            return new SourceUnitDetectionResult
            {
                Mode = SourceUnitDetectionModes.Heuristic,
                SelectedMethod = SourceUnitDetectionModes.Heuristic,
                UnitLength = 1,
                Heuristic = single
            };
        }

        var mode = SourceUnitDetectionModes.Normalise(
            request.Mode ?? await _settings.GetSetting(SettingKeys.Translation.SourceUnitDetection.Mode));
        var heuristic = new SourceUnitDetectionCandidate
        {
            Method = SourceUnitDetectionModes.Heuristic,
            UnitLength = DetermineHeuristicUnitLength(request.Cues),
            IsValid = true,
            LatencyMs = 0
        };

        if (mode == SourceUnitDetectionModes.Heuristic)
        {
            return new SourceUnitDetectionResult
            {
                Mode = mode,
                SelectedMethod = SourceUnitDetectionModes.Heuristic,
                UnitLength = heuristic.UnitLength,
                Heuristic = heuristic
            };
        }

        var modelConfig = await GetModelConfigurationAsync(request.ModelOverride, validator: false);
        var model = await BuildModelCandidateAsync(request, modelConfig, cancellationToken);
        if (!model.IsValid)
        {
            var reason = model.Error ?? "Source-unit model returned an invalid boundary; heuristic grouping used.";
            _logger.LogWarning("Model-assisted source-unit detection rejected: {Reason}", reason);
            return new SourceUnitDetectionResult
            {
                Mode = mode,
                SelectedMethod = SourceUnitDetectionModes.Heuristic,
                UnitLength = heuristic.UnitLength,
                Heuristic = heuristic,
                Model = model,
                FallbackReason = reason
            };
        }

        var selectedMethod = SourceUnitDetectionModes.Model;
        var selectedLength = model.UnitLength;
        SourceUnitDetectionValidatorDecision? validator = null;
        string? fallbackReason = null;

        if (mode == SourceUnitDetectionModes.Validated && model.UnitLength != heuristic.UnitLength)
        {
            var validatorConfig = await GetModelConfigurationAsync(request.ValidatorOverride, validator: true);
            validator = await TryValidateCandidatesAsync(
                request,
                model.UnitLength,
                heuristic.UnitLength,
                validatorConfig,
                cancellationToken);

            if (validator is null)
            {
                fallbackReason = "Source-unit validator was unavailable or returned an invalid decision; using the structurally valid model boundary.";
            }
            else if (string.Equals(validator.Winner, SourceUnitDetectionModes.Heuristic, StringComparison.OrdinalIgnoreCase))
            {
                selectedMethod = SourceUnitDetectionModes.Heuristic;
                selectedLength = heuristic.UnitLength;
            }
        }

        return new SourceUnitDetectionResult
        {
            Mode = mode,
            SelectedMethod = selectedMethod,
            UnitLength = selectedLength,
            Heuristic = heuristic,
            Model = model,
            Validator = validator,
            FallbackReason = fallbackReason
        };
    }

    /// <summary>
    /// Current deterministic source grouping baseline. This intentionally mirrors the original
    /// sentence-aware Lingarr rules so switching the new setting to heuristic preserves behavior.
    /// </summary>
    public static int DetermineHeuristicUnitLength(IReadOnlyList<SourceUnitDetectionCue> cues)
    {
        if (cues.Count == 0)
        {
            return 0;
        }

        var length = 1;
        while (length < cues.Count)
        {
            if (!ShouldJoin(cues[length - 1], cues[length]))
            {
                break;
            }
            length++;
        }
        return length;
    }

    private static bool ShouldJoin(SourceUnitDetectionCue current, SourceUnitDetectionCue next)
    {
        if (next.StartTime - current.EndTime > MaxGapMs)
        {
            return false;
        }

        var currentText = current.Text.Trim();
        var nextText = next.Text.Trim();
        if (currentText.Length == 0 || nextText.Length == 0)
        {
            return false;
        }

        if (StartsDialogueTurn(nextText) && !EndsWithContinuationPunctuation(currentText))
        {
            return false;
        }

        if (HasHardTerminalPunctuation(currentText))
        {
            return false;
        }

        if (EndsWithContinuationPunctuation(currentText))
        {
            return true;
        }

        if (EndsWithEllipsis(currentText))
        {
            return StartsWithLowercase(nextText) || StartsWithContinuationWord(nextText);
        }

        if (StartsWithLowercase(nextText) || StartsWithContinuationWord(nextText))
        {
            return true;
        }

        return EndsWithDanglingWord(currentText);
    }

    private static readonly HashSet<string> ContinuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "who", "whom", "whose", "which", "that",
        "when", "where", "while", "after", "before", "if", "as", "than", "then", "to",
        "of", "for", "with", "from", "in", "on", "at", "by", "into", "about", "over", "under"
    };

    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "but", "or", "because", "that", "who", "whom", "whose",
        "which", "if", "when", "while", "as", "than", "to", "of", "for", "with", "from",
        "in", "on", "at", "by", "into", "about", "over", "under"
    };

    private async Task<SourceUnitDetectionCandidate> BuildModelCandidateAsync(
        SourceUnitDetectionRequest request,
        ModelConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model))
        {
            return InvalidCandidate("Source-unit endpoint and model must both be configured.", config.Model);
        }

        try
        {
            var userPrompt = RenderModelPrompt(config.UserPrompt, request);
            var (content, latencyMs) = await SendChatCompletionAsync(
                config,
                config.SystemPrompt,
                userPrompt,
                CreateModelResponseFormat(request.Cues.Count),
                cancellationToken);
            var unitLength = ParseUnitLength(content);
            var valid = unitLength >= 1 && unitLength <= request.Cues.Count;
            return new SourceUnitDetectionCandidate
            {
                Method = SourceUnitDetectionModes.Model,
                UnitLength = valid ? unitLength : 1,
                IsValid = valid,
                Error = valid ? null : $"Model returned unitLength {unitLength}; expected 1 through {request.Cues.Count}.",
                LatencyMs = latencyMs,
                Model = config.Model
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return InvalidCandidate($"Source-unit model timed out after {config.TimeoutSeconds} seconds.", config.Model);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source-unit model call failed.");
            return InvalidCandidate(ex.Message, config.Model);
        }
    }

    private async Task<SourceUnitDetectionValidatorDecision?> TryValidateCandidatesAsync(
        SourceUnitDetectionRequest request,
        int modelUnitLength,
        int heuristicUnitLength,
        ModelConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Endpoint) || string.IsNullOrWhiteSpace(config.Model))
        {
            _logger.LogWarning("Validated source-unit detection requested but validator endpoint/model is not configured.");
            return null;
        }

        try
        {
            var userPrompt = RenderValidatorPrompt(
                config.UserPrompt,
                request,
                modelUnitLength,
                heuristicUnitLength);
            var (content, latencyMs) = await SendChatCompletionAsync(
                config,
                config.SystemPrompt,
                userPrompt,
                CreateValidatorResponseFormat(),
                cancellationToken);
            var parsed = ParseValidatorDecision(content);
            if (parsed is null)
            {
                return null;
            }

            return new SourceUnitDetectionValidatorDecision
            {
                Winner = parsed.Value.Winner,
                ModelScore = parsed.Value.ModelScore,
                HeuristicScore = parsed.Value.HeuristicScore,
                Reason = parsed.Value.Reason,
                LatencyMs = latencyMs,
                Model = config.Model
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Source-unit validator timed out after {TimeoutSeconds} seconds.", config.TimeoutSeconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source-unit validator call failed.");
            return null;
        }
    }

    private async Task<ModelConfiguration> GetModelConfigurationAsync(
        SourceUnitDetectionModelOverride? overrides,
        bool validator)
    {
        var keys = validator
            ? new[]
            {
                SettingKeys.Translation.SourceUnitDetection.ValidatorEndpoint,
                SettingKeys.Translation.SourceUnitDetection.ValidatorModel,
                SettingKeys.Translation.SourceUnitDetection.ValidatorSystemPrompt,
                SettingKeys.Translation.SourceUnitDetection.ValidatorUserPrompt,
                SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds
            }
            : new[]
            {
                SettingKeys.Translation.SourceUnitDetection.Endpoint,
                SettingKeys.Translation.SourceUnitDetection.Model,
                SettingKeys.Translation.SourceUnitDetection.SystemPrompt,
                SettingKeys.Translation.SourceUnitDetection.UserPrompt,
                SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds
            };

        var values = await _settings.GetSettings(keys);
        var apiKey = overrides?.ApiKey;
        if (apiKey is null)
        {
            apiKey = await _settings.GetEncryptedSetting(
                validator
                    ? SettingKeys.Translation.SourceUnitDetection.ValidatorApiKey
                    : SettingKeys.Translation.SourceUnitDetection.ApiKey);
        }

        var endpointKey = validator
            ? SettingKeys.Translation.SourceUnitDetection.ValidatorEndpoint
            : SettingKeys.Translation.SourceUnitDetection.Endpoint;
        var modelKey = validator
            ? SettingKeys.Translation.SourceUnitDetection.ValidatorModel
            : SettingKeys.Translation.SourceUnitDetection.Model;
        var systemPromptKey = validator
            ? SettingKeys.Translation.SourceUnitDetection.ValidatorSystemPrompt
            : SettingKeys.Translation.SourceUnitDetection.SystemPrompt;
        var userPromptKey = validator
            ? SettingKeys.Translation.SourceUnitDetection.ValidatorUserPrompt
            : SettingKeys.Translation.SourceUnitDetection.UserPrompt;

        var timeout = overrides?.TimeoutSeconds ?? ParseTimeout(
            values.GetValueOrDefault(SettingKeys.Translation.SourceUnitDetection.TimeoutSeconds));
        return new ModelConfiguration(
            overrides?.Endpoint ?? values.GetValueOrDefault(endpointKey) ?? string.Empty,
            overrides?.Model ?? values.GetValueOrDefault(modelKey) ?? string.Empty,
            apiKey ?? string.Empty,
            overrides?.SystemPrompt ?? values.GetValueOrDefault(systemPromptKey) ??
                (validator ? DefaultValidatorSystemPrompt : DefaultSystemPrompt),
            overrides?.UserPrompt ?? values.GetValueOrDefault(userPromptKey) ??
                (validator ? DefaultValidatorUserPrompt : DefaultUserPrompt),
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
            client, endpoint, config, systemPrompt, userPrompt, responseFormat, timeoutCts.Token);
        if (!first.Response.IsSuccessStatusCode && SupportsSchemaFallback(first.Response.StatusCode))
        {
            first.Response.Dispose();
            var second = await SendOnceAsync(
                client, endpoint, config, systemPrompt, userPrompt, null, timeoutCts.Token);
            using (second.Response)
            {
                if (!second.Response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"OpenAI-compatible source-unit request failed ({(int)second.Response.StatusCode}): {second.Body}");
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
                    $"OpenAI-compatible source-unit request failed ({(int)first.Response.StatusCode}): {first.Body}");
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
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);
        }

        var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResult(response, body);
    }

    private static object CreateModelResponseFormat(int cueCount) => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "source_unit_detection",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    unitLength = new { type = "integer", minimum = 1, maximum = cueCount }
                },
                required = new[] { "unitLength" },
                additionalProperties = false
            }
        }
    };

    private static object CreateValidatorResponseFormat() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "source_unit_detection_validation",
            strict = true,
            schema = new
            {
                type = "object",
                properties = new
                {
                    winner = new { type = "string", @enum = new[] { "model", "heuristic" } },
                    modelScore = new { type = "number", minimum = 0, maximum = 100 },
                    heuristicScore = new { type = "number", minimum = 0, maximum = 100 },
                    reason = new { type = "string" }
                },
                required = new[] { "winner", "modelScore", "heuristicScore", "reason" },
                additionalProperties = false
            }
        }
    };

    private static string RenderModelPrompt(string template, SourceUnitDetectionRequest request) =>
        template
            .Replace("{sourceLanguage}", request.SourceLanguage, StringComparison.Ordinal)
            .Replace("{candidateCount}", request.Cues.Count.ToString(), StringComparison.Ordinal)
            .Replace("{sourceCuesJson}", JsonSerializer.Serialize(request.Cues), StringComparison.Ordinal);

    private static string RenderValidatorPrompt(
        string template,
        SourceUnitDetectionRequest request,
        int modelUnitLength,
        int heuristicUnitLength) =>
        template
            .Replace("{sourceLanguage}", request.SourceLanguage, StringComparison.Ordinal)
            .Replace("{candidateCount}", request.Cues.Count.ToString(), StringComparison.Ordinal)
            .Replace("{sourceCuesJson}", JsonSerializer.Serialize(request.Cues), StringComparison.Ordinal)
            .Replace("{modelUnitLength}", modelUnitLength.ToString(), StringComparison.Ordinal)
            .Replace("{heuristicUnitLength}", heuristicUnitLength.ToString(), StringComparison.Ordinal);

    private static int ParseUnitLength(string content)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(content));
        if (!document.RootElement.TryGetProperty("unitLength", out var value) || !value.TryGetInt32(out var unitLength))
        {
            throw new JsonException("Source-unit model did not return integer unitLength.");
        }
        return unitLength;
    }

    private static (string Winner, double ModelScore, double HeuristicScore, string Reason)? ParseValidatorDecision(
        string content)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(content));
        var root = document.RootElement;
        if (!root.TryGetProperty("winner", out var winnerElement) ||
            !root.TryGetProperty("modelScore", out var modelScoreElement) ||
            !root.TryGetProperty("heuristicScore", out var heuristicScoreElement))
        {
            return null;
        }

        var winner = winnerElement.GetString();
        if (winner is not ("model" or "heuristic"))
        {
            return null;
        }

        var reason = root.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString() ?? string.Empty
            : string.Empty;
        return (winner, modelScoreElement.GetDouble(), heuristicScoreElement.GetDouble(), reason);
    }

    private static string ExtractAssistantContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            throw new JsonException("OpenAI-compatible response did not contain choices.");
        }

        var message = choices[0].GetProperty("message");
        var content = message.GetProperty("content");
        return content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : content.GetRawText();
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
        return trimmed;
    }

    private static bool SupportsSchemaFallback(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity;

    private static Uri BuildChatCompletionsEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid source-unit endpoint: '{endpoint}'.");
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

    private static SourceUnitDetectionCandidate InvalidCandidate(string error, string? model) => new()
    {
        Method = SourceUnitDetectionModes.Model,
        UnitLength = 1,
        IsValid = false,
        Error = error,
        Model = model
    };

    private static int ParseTimeout(string? value) =>
        int.TryParse(value, out var timeout) ? timeout : 120;

    private static bool HasHardTerminalPunctuation(string text)
    {
        if (EndsWithEllipsis(text))
        {
            return false;
        }
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!');
    }

    private static bool EndsWithEllipsis(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith('…');
    }

    private static bool EndsWithContinuationPunctuation(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith(',') || trimmed.EndsWith(';') || trimmed.EndsWith(':') ||
               trimmed.EndsWith('—') || (trimmed.EndsWith('-') && !trimmed.EndsWith("--", StringComparison.Ordinal));
    }

    private static bool StartsWithLowercase(string text)
    {
        var firstLetter = text.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsLower(firstLetter);
    }

    private static bool StartsWithContinuationWord(string text)
    {
        var word = FirstWord(text);
        return word is not null && ContinuationWords.Contains(word);
    }

    private static bool EndsWithDanglingWord(string text)
    {
        var word = LastWord(text);
        return word is not null && DanglingWords.Contains(word);
    }

    private static bool StartsDialogueTurn(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("– ", StringComparison.Ordinal) ||
               trimmed.StartsWith("— ", StringComparison.Ordinal);
    }

    private static string? FirstWord(string text)
    {
        var letters = text.SkipWhile(character => !char.IsLetter(character)).TakeWhile(char.IsLetter).ToArray();
        return letters.Length == 0 ? null : new string(letters);
    }

    private static string? LastWord(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        var end = trimmed.Length - 1;
        while (end >= 0 && !char.IsLetter(trimmed[end]))
        {
            end--;
        }
        if (end < 0)
        {
            return null;
        }

        var start = end;
        while (start >= 0 && char.IsLetter(trimmed[start]))
        {
            start--;
        }
        return trimmed[(start + 1)..(end + 1)];
    }

    private static string TrimTrailingClosers(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] is '"' or '\'' or '”' or '’' or ')' or ']' or '}')
        {
            end--;
        }
        return text[..end];
    }

    private sealed record ModelConfiguration(
        string Endpoint,
        string Model,
        string ApiKey,
        string SystemPrompt,
        string UserPrompt,
        int TimeoutSeconds);

    private sealed record HttpResult(HttpResponseMessage Response, string Body);
}
