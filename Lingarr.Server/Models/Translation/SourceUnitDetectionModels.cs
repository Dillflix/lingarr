using System;
using System.Collections.Generic;

namespace Lingarr.Server.Models.Translation;

public static class SourceUnitDetectionModes
{
    public const string Heuristic = "heuristic";
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

        return Heuristic;
    }
}

public sealed class SourceUnitDetectionCue
{
    public required int Position { get; init; }
    public required int StartTime { get; init; }
    public required int EndTime { get; init; }
    public required string Text { get; init; }
}

public sealed class SourceUnitDetectionModelOverride
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class SourceUnitDetectionRequest
{
    public required string SourceLanguage { get; init; }
    public required IReadOnlyList<SourceUnitDetectionCue> Cues { get; init; }
    public string? Mode { get; init; }
    public SourceUnitDetectionModelOverride? ModelOverride { get; init; }
    public SourceUnitDetectionModelOverride? ValidatorOverride { get; init; }
}

public sealed class SourceUnitDetectionCandidate
{
    public required string Method { get; init; }
    public required int UnitLength { get; init; }
    public required bool IsValid { get; init; }
    public string? Error { get; init; }
    public long? LatencyMs { get; init; }
    public string? Model { get; init; }
}

public sealed class SourceUnitDetectionValidatorDecision
{
    public required string Winner { get; init; }
    public required double ModelScore { get; init; }
    public required double HeuristicScore { get; init; }
    public string? Reason { get; init; }
    public long? LatencyMs { get; init; }
    public string? Model { get; init; }
}

public sealed class SourceUnitDetectionResult
{
    public required string Mode { get; init; }
    public required string SelectedMethod { get; init; }
    public required int UnitLength { get; init; }
    public required SourceUnitDetectionCandidate Heuristic { get; init; }
    public SourceUnitDetectionCandidate? Model { get; init; }
    public SourceUnitDetectionValidatorDecision? Validator { get; init; }
    public string? FallbackReason { get; init; }
}
