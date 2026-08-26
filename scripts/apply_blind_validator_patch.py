from pathlib import Path
import re


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def regex_once(text: str, pattern: str, replacement: str, label: str) -> str:
    new_text, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one regex match, found {count}")
    return new_text


# --- Source-unit validator -------------------------------------------------
source_path = Path("Lingarr.Server/Services/Translation/SourceUnitDetectionService.cs")
source = source_path.read_text(encoding="utf-8")
source = replace_once(
    source,
    "using System.Net.Http.Headers;\n",
    "using System.Net.Http.Headers;\nusing System.Security.Cryptography;\n",
    "source security using",
)
source = regex_once(
    source,
    r'''    public const string DefaultValidatorSystemPrompt = """\n.*?    public const string DefaultValidatorUserPrompt = """\n.*?        """;\n''',
    '''    public const string DefaultValidatorSystemPrompt = """
        You are a subtitle source-unit segmentation judge. Compare Candidate A and Candidate B for the same consecutive source-language subtitle cues. Their origins are intentionally hidden and their A/B order is randomized. Judge only which boundary better identifies one complete linguistic unit beginning at cue 1. Prefer semantic/syntactic completeness while avoiding unrelated sentences or speaker turns. Do not infer or favor how either candidate was produced. Return JSON only.
        """;

    public const string DefaultValidatorUserPrompt = """
        Source language: {sourceLanguage}
        Candidate cue count: {candidateCount}

        Consecutive subtitle cues:
        {sourceCuesJson}

        Candidate A unitLength: {candidateAUnitLength}
        Candidate B unitLength: {candidateBUnitLength}

        Choose which candidate better identifies exactly one linguistic translation unit beginning at cue 1. The candidates' origins are deliberately undisclosed. Return JSON with winner ("A" or "B"), candidateAScore (0-100), candidateBScore (0-100), and reason.
        """;
''',
    "source validator prompts",
)
source = regex_once(
    source,
    r'''    private async Task<SourceUnitDetectionValidatorDecision\?> TryValidateCandidatesAsync\(.*?\n    private async Task<ModelConfiguration> GetModelConfigurationAsync\(''',
    '''    private async Task<SourceUnitDetectionValidatorDecision?> TryValidateCandidatesAsync(
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
            var modelIsCandidateA = IsModelCandidateA(request, modelUnitLength, heuristicUnitLength);
            var candidateAUnitLength = modelIsCandidateA ? modelUnitLength : heuristicUnitLength;
            var candidateBUnitLength = modelIsCandidateA ? heuristicUnitLength : modelUnitLength;
            var userPrompt = RenderValidatorPrompt(
                config.UserPrompt,
                request,
                candidateAUnitLength,
                candidateBUnitLength,
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

            string winner;
            double modelScore;
            double heuristicScore;
            if (parsed.Value.IsBlind)
            {
                var candidateAWon = string.Equals(parsed.Value.Winner, "A", StringComparison.OrdinalIgnoreCase);
                winner = candidateAWon == modelIsCandidateA
                    ? SourceUnitDetectionModes.Model
                    : SourceUnitDetectionModes.Heuristic;
                modelScore = modelIsCandidateA ? parsed.Value.FirstScore : parsed.Value.SecondScore;
                heuristicScore = modelIsCandidateA ? parsed.Value.SecondScore : parsed.Value.FirstScore;
            }
            else
            {
                // Backward compatibility for explicitly customized legacy validator prompts.
                winner = parsed.Value.Winner;
                modelScore = parsed.Value.FirstScore;
                heuristicScore = parsed.Value.SecondScore;
            }

            return new SourceUnitDetectionValidatorDecision
            {
                Winner = winner,
                ModelScore = modelScore,
                HeuristicScore = heuristicScore,
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

    private async Task<ModelConfiguration> GetModelConfigurationAsync(''',
    "source validator method",
)
source = regex_once(
    source,
    r'''    private static object CreateValidatorResponseFormat\(\) => new\n    \{.*?    \};\n\n    private static string RenderModelPrompt''',
    '''    private static object CreateValidatorResponseFormat() => new
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

    private static string RenderModelPrompt''',
    "source validator schema",
)
source = regex_once(
    source,
    r'''    private static string RenderValidatorPrompt\(.*?\n    private static int ParseUnitLength''',
    '''    private static string RenderValidatorPrompt(
        string template,
        SourceUnitDetectionRequest request,
        int candidateAUnitLength,
        int candidateBUnitLength,
        int modelUnitLength,
        int heuristicUnitLength) =>
        template
            .Replace("{sourceLanguage}", request.SourceLanguage, StringComparison.Ordinal)
            .Replace("{candidateCount}", request.Cues.Count.ToString(), StringComparison.Ordinal)
            .Replace("{sourceCuesJson}", JsonSerializer.Serialize(request.Cues), StringComparison.Ordinal)
            .Replace("{candidateAUnitLength}", candidateAUnitLength.ToString(), StringComparison.Ordinal)
            .Replace("{candidateBUnitLength}", candidateBUnitLength.ToString(), StringComparison.Ordinal)
            // Keep legacy placeholders usable only for intentionally customized old prompts.
            .Replace("{modelUnitLength}", modelUnitLength.ToString(), StringComparison.Ordinal)
            .Replace("{heuristicUnitLength}", heuristicUnitLength.ToString(), StringComparison.Ordinal);

    private static bool IsModelCandidateA(
        SourceUnitDetectionRequest request,
        int modelUnitLength,
        int heuristicUnitLength)
    {
        var low = Math.Min(modelUnitLength, heuristicUnitLength);
        var high = Math.Max(modelUnitLength, heuristicUnitLength);
        var material = string.Join("\\n",
            request.SourceLanguage,
            JsonSerializer.Serialize(request.Cues),
            $"{low}|{high}");
        return (SHA256.HashData(Encoding.UTF8.GetBytes(material))[0] & 1) == 0;
    }

    private static int ParseUnitLength''',
    "source validator render/order",
)
source = regex_once(
    source,
    r'''    private static \(string Winner, double ModelScore, double HeuristicScore, string Reason\)\? ParseValidatorDecision\(.*?\n    private static string ExtractAssistantContent''',
    '''    private static (string Winner, double FirstScore, double SecondScore, bool IsBlind, string Reason)? ParseValidatorDecision(
        string content)
    {
        using var document = JsonDocument.Parse(ExtractJsonObject(content));
        var root = document.RootElement;
        if (!root.TryGetProperty("winner", out var winnerElement))
        {
            return null;
        }

        var winner = winnerElement.GetString();
        var reason = root.TryGetProperty("reason", out var reasonElement)
            ? reasonElement.GetString() ?? string.Empty
            : string.Empty;

        if (winner is "A" or "B")
        {
            if (!root.TryGetProperty("candidateAScore", out var candidateAScoreElement) ||
                !root.TryGetProperty("candidateBScore", out var candidateBScoreElement))
            {
                return null;
            }
            return (winner, candidateAScoreElement.GetDouble(), candidateBScoreElement.GetDouble(), true, reason);
        }

        // Backward compatibility for user-authored prompts created before blind A/B validation.
        if (winner is "model" or "heuristic")
        {
            if (!root.TryGetProperty("modelScore", out var modelScoreElement) ||
                !root.TryGetProperty("heuristicScore", out var heuristicScoreElement))
            {
                return null;
            }
            return (winner, modelScoreElement.GetDouble(), heuristicScoreElement.GetDouble(), false, reason);
        }

        return null;
    }

    private static string ExtractAssistantContent''',
    "source validator parser",
)
source_path.write_text(source, encoding="utf-8")


# --- Target resegmentation validator --------------------------------------
target_path = Path("Lingarr.Server/Services/Translation/TranslationUnitResegmentationService.cs")
target = target_path.read_text(encoding="utf-8")
target = replace_once(
    target,
    "using System.Net.Http.Headers;\n",
    "using System.Net.Http.Headers;\nusing System.Security.Cryptography;\n",
    "target security using",
)
target = regex_once(
    target,
    r'''    public const string DefaultValidatorSystemPrompt = """\n.*?    public const string DefaultValidatorUserPrompt = """\n.*?        """;\n''',
    '''    public const string DefaultValidatorSystemPrompt = """
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
''',
    "target validator prompts",
)
target = regex_once(
    target,
    r'''    private async Task<ResegmentationValidatorDecision\?> TryValidateCandidatesAsync\(.*?\n    private async Task<ModelConfiguration> GetModelConfigurationAsync\(''',
    '''    private async Task<ResegmentationValidatorDecision?> TryValidateCandidatesAsync(
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

    private async Task<ModelConfiguration> GetModelConfigurationAsync(''',
    "target validator method",
)
target = regex_once(
    target,
    r'''    private static object CreateValidatorResponseFormat\(\) => new\n    \{.*?    \};\n\n    private static string RenderAlignmentPrompt''',
    '''    private static object CreateValidatorResponseFormat() => new
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

    private static string RenderAlignmentPrompt''',
    "target validator schema",
)
target = regex_once(
    target,
    r'''    private static string RenderValidatorPrompt\(.*?\n    private static string ExtractAssistantContent''',
    '''    private static string RenderValidatorPrompt(
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
        var material = string.Join("\\n",
            request.SourceLanguage,
            request.TargetLanguage,
            JsonSerializer.Serialize(request.SourceSegments),
            request.TranslatedUnit,
            ordered[0],
            ordered[1]);
        return (SHA256.HashData(Encoding.UTF8.GetBytes(material))[0] & 1) == 0;
    }

    private static string ExtractAssistantContent''',
    "target validator render/order",
)
target = regex_once(
    target,
    r'''    private static ValidatorPayload\? ParseValidatorDecision\(string content\)\n    \{.*?\n    private static string ExtractJson''',
    '''    private static ValidatorPayload? ParseValidatorDecision(string content)
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

    private static string ExtractJson''',
    "target validator parser",
)
target = replace_once(
    target,
    '''    private sealed record ValidatorPayload(
        string Winner,
        double ModelScore,
        double DeterministicScore,
        string? Reason);''',
    '''    private sealed record ValidatorPayload(
        string Winner,
        double FirstScore,
        double SecondScore,
        bool IsBlind,
        string? Reason);''',
    "target validator record",
)
target_path.write_text(target, encoding="utf-8")


# --- UI prompt validation/help --------------------------------------------
source_ui_path = Path("Lingarr.Client/src/components/features/settings/SourceUnitDetectionSettings.vue")
source_ui = source_ui_path.read_text(encoding="utf-8")
source_ui = replace_once(
    source_ui,
    '''                        <p class="text-secondary-content/60 text-xs">
                            Compares the model boundary with the heuristic boundary only when they disagree.
                        </p>''',
    '''                        <p class="text-secondary-content/60 text-xs">
                            Compares the two boundaries only when they disagree. Candidate origins are hidden and
                            A/B order is randomized before the judge sees them to reduce evaluator bias.
                        </p>''',
    "source UI judge description",
)
source_ui = replace_once(
    source_ui,
    '''                            :required-placeholders="[
                                '{sourceCuesJson}',
                                '{modelUnitLength}',
                                '{heuristicUnitLength}'
                            ]"''',
    '''                            :required-placeholders="[
                                '{sourceCuesJson}',
                                '{candidateAUnitLength}',
                                '{candidateBUnitLength}'
                            ]"''',
    "source UI validator placeholders",
)
source_ui = replace_once(
    source_ui,
    '''                            Validator placeholders additionally include <code>{modelUnitLength}</code> and
                            <code>{heuristicUnitLength}</code>.''',
    '''                            Validator placeholders additionally include <code>{candidateAUnitLength}</code> and
                            <code>{candidateBUnitLength}</code>. Candidate identity is mapped back only after judging.''',
    "source UI placeholder help",
)
source_ui_path.write_text(source_ui, encoding="utf-8")

reseg_ui_path = Path("Lingarr.Client/src/components/features/settings/ResegmentationSettings.vue")
reseg_ui = reseg_ui_path.read_text(encoding="utf-8")
reseg_ui = replace_once(
    reseg_ui,
    '''                        <p class="text-secondary-content/60 text-xs">
                            Configure a separate model to compare the semantic alignment from the dedicated
                            model against the deterministic baseline. It can be a different endpoint and model.
                        </p>''',
    '''                        <p class="text-secondary-content/60 text-xs">
                            Configure a separate model to compare the two segmentations. Candidate origins are
                            hidden and A/B order is randomized before the judge sees them to reduce evaluator bias.
                        </p>''',
    "target UI judge description",
)
reseg_ui = replace_once(
    reseg_ui,
    '''                            :required-placeholders="[
                                '{sourceSegmentsJson}',
                                '{translatedUnit}',
                                '{modelSegmentsJson}',
                                '{deterministicSegmentsJson}'
                            ]"''',
    '''                            :required-placeholders="[
                                '{sourceSegmentsJson}',
                                '{translatedUnit}',
                                '{candidateASegmentsJson}',
                                '{candidateBSegmentsJson}'
                            ]"''',
    "target UI validator placeholders",
)
reseg_ui = replace_once(
    reseg_ui,
    '''                            Validator placeholders additionally include <code>{modelSegmentsJson}</code> and
                            <code>{deterministicSegmentsJson}</code>.''',
    '''                            Validator placeholders additionally include <code>{candidateASegmentsJson}</code> and
                            <code>{candidateBSegmentsJson}</code>. Candidate identity is mapped back only after judging.''',
    "target UI placeholder help",
)
reseg_ui_path.write_text(reseg_ui, encoding="utf-8")


# --- Migration: upgrade only untouched default prompts --------------------
migration = Path("Lingarr.Migrations/Migrations/M0024_BlindValidatorCandidateOrigins.cs")
migration.write_text(r'''using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(24)]
public class M0024_BlindValidatorCandidateOrigins : Migration
{
    private const string OldSourceSystem = "You are a subtitle source-unit segmentation judge. Compare two proposed boundaries for the same consecutive source-language subtitle cues. Decide which grouping better identifies one complete linguistic unit beginning at cue 1. Prefer semantic/syntactic completeness while avoiding unrelated sentences or speaker turns. Return JSON only.";
    private const string NewSourceSystem = "You are a subtitle source-unit segmentation judge. Compare Candidate A and Candidate B for the same consecutive source-language subtitle cues. Their origins are intentionally hidden and their A/B order is randomized. Judge only which boundary better identifies one complete linguistic unit beginning at cue 1. Prefer semantic/syntactic completeness while avoiding unrelated sentences or speaker turns. Do not infer or favor how either candidate was produced. Return JSON only.";
    private const string OldSourceUser = "Source language: {sourceLanguage}\nCandidate cue count: {candidateCount}\n\nConsecutive subtitle cues:\n{sourceCuesJson}\n\nModel proposal unitLength: {modelUnitLength}\nHeuristic proposal unitLength: {heuristicUnitLength}\n\nChoose which proposal better identifies exactly one linguistic translation unit beginning at cue 1. Return JSON with winner (\"model\" or \"heuristic\"), modelScore (0-100), heuristicScore (0-100), and reason.";
    private const string NewSourceUser = "Source language: {sourceLanguage}\nCandidate cue count: {candidateCount}\n\nConsecutive subtitle cues:\n{sourceCuesJson}\n\nCandidate A unitLength: {candidateAUnitLength}\nCandidate B unitLength: {candidateBUnitLength}\n\nChoose which candidate better identifies exactly one linguistic translation unit beginning at cue 1. The candidates' origins are deliberately undisclosed. Return JSON with winner (\"A\" or \"B\"), candidateAScore (0-100), candidateBScore (0-100), and reason.";

    private const string OldTargetSystem = "You are a subtitle segmentation judge. Compare two segmentations of exactly the same target translation against the source timing segments. Judge semantic alignment of each target segment to its source slot, readability, punctuation, and balance. Do not reward changes to the translation wording. Return JSON only.";
    private const string NewTargetSystem = "You are a subtitle segmentation judge. Compare Candidate A and Candidate B, which are two segmentations of exactly the same target translation against the same source timing segments. Their origins are intentionally hidden and their A/B order is randomized. Judge semantic alignment of each target segment to its source slot, readability, punctuation, and balance. Do not infer or favor how either candidate was produced. Do not reward changes to the translation wording. Return JSON only.";
    private const string OldTargetUser = "Source language: {sourceLanguage}\nTarget language: {targetLanguage}\n\nSource subtitle segments:\n{sourceSegmentsJson}\n\nComplete target translation:\n{translatedUnit}\n\nModel-assisted segmentation:\n{modelSegmentsJson}\n\nDeterministic segmentation:\n{deterministicSegmentsJson}\n\nChoose which segmentation better aligns the unchanged target translation to the source timing slots. Return JSON with winner (\"model\" or \"deterministic\"), modelScore (0-100), deterministicScore (0-100), and reason.";
    private const string NewTargetUser = "Source language: {sourceLanguage}\nTarget language: {targetLanguage}\n\nSource subtitle segments:\n{sourceSegmentsJson}\n\nComplete target translation:\n{translatedUnit}\n\nCandidate A segmentation:\n{candidateASegmentsJson}\n\nCandidate B segmentation:\n{candidateBSegmentsJson}\n\nChoose which candidate better aligns the unchanged target translation to the source timing slots. The candidates' origins are deliberately undisclosed. Return JSON with winner (\"A\" or \"B\"), candidateAScore (0-100), candidateBScore (0-100), and reason.";

    public override void Up()
    {
        UpdateIfUnmodified("source_unit_detection_validator_system_prompt", OldSourceSystem, NewSourceSystem);
        UpdateIfUnmodified("source_unit_detection_validator_user_prompt", OldSourceUser, NewSourceUser);
        UpdateIfUnmodified("resegmentation_validator_system_prompt", OldTargetSystem, NewTargetSystem);
        UpdateIfUnmodified("resegmentation_validator_user_prompt", OldTargetUser, NewTargetUser);
    }

    public override void Down()
    {
        UpdateIfUnmodified("source_unit_detection_validator_system_prompt", NewSourceSystem, OldSourceSystem);
        UpdateIfUnmodified("source_unit_detection_validator_user_prompt", NewSourceUser, OldSourceUser);
        UpdateIfUnmodified("resegmentation_validator_system_prompt", NewTargetSystem, OldTargetSystem);
        UpdateIfUnmodified("resegmentation_validator_user_prompt", NewTargetUser, OldTargetUser);
    }

    private void UpdateIfUnmodified(string key, string expectedValue, string replacementValue)
    {
        Update.Table("settings")
            .Set(new { value = replacementValue })
            .Where(new { key, value = expectedValue });
    }
}
''', encoding="utf-8")


# --- Regression tests for prompt blindness --------------------------------
source_test_path = Path("Lingarr.Server.Tests/Services/Translation/SourceUnitDetectionServiceTests.cs")
source_test = source_test_path.read_text(encoding="utf-8")
insert_point = '''    [Fact]
    public async Task UnsupportedJsonSchema_RetriesWithoutResponseFormat()
'''
blind_test = '''    [Fact]
    public async Task DefaultValidatorPrompt_BlindsCandidateOrigins()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\\\"unitLength\\\":3}"),
            JsonResponse("{\\\"winner\\\":\\\"A\\\",\\\"candidateAScore\\\":90,\\\"candidateBScore\\\":70,\\\"reason\\\":\\\"Better boundary\\\"}")
        ]);
        var service = CreateService(handler);

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(1, 1000, 1800, "Complete sentence."),
                Cue(2, 1850, 2500, "Another"),
                Cue(3, 2550, 3300, "sentence."),
                Cue(4, 3400, 4100, "Next.")
            ],
            Mode = "validated",
            ModelOverride = ModelOverride("boundary-model"),
            ValidatorOverride = new SourceUnitDetectionModelOverride
            {
                Endpoint = "http://localhost:9999/v1",
                Model = "judge-model",
                ApiKey = string.Empty,
                SystemPrompt = SourceUnitDetectionService.DefaultValidatorSystemPrompt,
                UserPrompt = SourceUnitDetectionService.DefaultValidatorUserPrompt,
                TimeoutSeconds = 30
            }
        }, CancellationToken.None);

        Assert.NotNull(result.Validator);
        var judgeRequest = handler.RequestBodies[1];
        Assert.Contains("Candidate A unitLength", judgeRequest, StringComparison.Ordinal);
        Assert.Contains("Candidate B unitLength", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Model proposal", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Heuristic proposal", judgeRequest, StringComparison.Ordinal);
    }

'''
source_test = replace_once(source_test, insert_point, blind_test + insert_point, "source blind test insertion")
source_test_path.write_text(source_test, encoding="utf-8")

target_test_path = Path("Lingarr.Server.Tests/Services/Translation/TranslationUnitResegmentationServiceTests.cs")
target_test = target_test_path.read_text(encoding="utf-8")
insert_point = '''    [Fact]
    public async Task JsonSchemaUnsupported_RetriesWithoutResponseFormat()
'''
blind_test = '''    [Fact]
    public async Task DefaultValidatorPrompt_BlindsCandidateOrigins()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\\\"segments\\\":[\\\"Jeg elsker\\\",\\\"dig.\\\"]}"),
            JsonResponse("{\\\"winner\\\":\\\"A\\\",\\\"candidateAScore\\\":90,\\\"candidateBScore\\\":70,\\\"reason\\\":\\\"Better alignment\\\"}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Validated,
            ModelOverride = ModelOverride("alignment-model"),
            ValidatorOverride = new ResegmentationModelOverride
            {
                Endpoint = "http://localhost:9999/v1",
                Model = "judge-model",
                ApiKey = string.Empty,
                SystemPrompt = TranslationUnitResegmentationService.DefaultValidatorSystemPrompt,
                UserPrompt = TranslationUnitResegmentationService.DefaultValidatorUserPrompt,
                TimeoutSeconds = 30
            }
        }, CancellationToken.None);

        Assert.NotNull(result.Validator);
        var judgeRequest = handler.RequestBodies[1];
        Assert.Contains("Candidate A segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.Contains("Candidate B segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Model-assisted segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Deterministic segmentation", judgeRequest, StringComparison.Ordinal);
    }

'''
target_test = replace_once(target_test, insert_point, blind_test + insert_point, "target blind test insertion")
target_test_path.write_text(target_test, encoding="utf-8")

print("Blind validator patch applied successfully.")
