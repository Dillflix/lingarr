using System;
using System.Collections.Generic;

namespace Lingarr.Server.Models.Translation;

public static class ResegmentationModes
{
    public const string Deterministic = "deterministic";
    public const string Model = "model";
    public const string Validated = "validated";

    public static string Normalise(string? value)
    {
        if (string.Equals(value, Model, StringComparison.OrdinalIgnoreCase))
        {
            return Model;
        }

        if (string.Equals(value, Validated, StringComparison.OrdinalIgnoreCase))
        {
            return Validated;
        }

        return Deterministic;
    }
}

public sealed class TranslationUnitResegmentationRequest
{
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required IReadOnlyList<string> SourceSegments { get; init; }
    public required string TranslatedUnit { get; init; }
}

public sealed class ResegmentationModelOverride
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class ResegmentationEvaluationRequest
{
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required IReadOnlyList<string> SourceSegments { get; init; }
    public required string TranslatedUnit { get; init; }
    public IReadOnlyList<string>? ReferenceSegments { get; init; }
    public string? Mode { get; init; }
    public ResegmentationModelOverride? ModelOverride { get; init; }
    public ResegmentationModelOverride? ValidatorOverride { get; init; }
}

public sealed class ResegmentationStructuralValidation
{
    public required bool IsValid { get; init; }
    public required bool CountMatches { get; init; }
    public required bool NonEmptySegments { get; init; }
    public required bool TextPreserved { get; init; }
    public string? Error { get; init; }
}

public sealed class ResegmentationCandidate
{
    public required string Method { get; init; }
    public IReadOnlyList<string>? Segments { get; init; }
    public required ResegmentationStructuralValidation Validation { get; init; }
    public string? Error { get; init; }
    public long? LatencyMs { get; init; }
    public string? Model { get; init; }
}

public sealed class ResegmentationValidatorDecision
{
    public required string Winner { get; init; }
    public required double ModelScore { get; init; }
    public required double DeterministicScore { get; init; }
    public string? Reason { get; init; }
    public long? LatencyMs { get; init; }
    public string? Model { get; init; }
}

public sealed class ResegmentationBoundaryMetrics
{
    public required int BoundaryCount { get; init; }
    public required double MeanAbsoluteErrorCharacters { get; init; }
    public required int MaxAbsoluteErrorCharacters { get; init; }
    public required double BoundariesWithinFiveCharactersPercent { get; init; }
    public required double ExactSegmentMatchPercent { get; init; }
}

public sealed class TranslationUnitResegmentationResult
{
    public required string Mode { get; init; }
    public required string SelectedMethod { get; init; }
    public required IReadOnlyList<string> Segments { get; init; }
    public required ResegmentationCandidate Deterministic { get; init; }
    public ResegmentationCandidate? Model { get; init; }
    public ResegmentationValidatorDecision? Validator { get; init; }
    public string? FallbackReason { get; init; }
}

public sealed class ResegmentationEvaluationResult
{
    public required string Mode { get; init; }
    public required string SelectedMethod { get; init; }
    public required IReadOnlyList<string> SelectedSegments { get; init; }
    public required ResegmentationCandidate Deterministic { get; init; }
    public ResegmentationCandidate? Model { get; init; }
    public ResegmentationValidatorDecision? Validator { get; init; }
    public ResegmentationStructuralValidation? ReferenceValidation { get; init; }
    public ResegmentationBoundaryMetrics? DeterministicReferenceMetrics { get; init; }
    public ResegmentationBoundaryMetrics? ModelReferenceMetrics { get; init; }
    public string? FallbackReason { get; init; }
}
