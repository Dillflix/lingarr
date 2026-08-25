using System;
using System.Collections.Generic;

namespace Lingarr.Server.Models.Translation;

public sealed class ResegmentationBenchmarkCaptureRequest
{
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required IReadOnlyList<string> SourceSegments { get; init; }
    public required string TranslatedUnit { get; init; }
    public int? TranslationRequestId { get; init; }
    public int? StartPosition { get; init; }
    public int? EndPosition { get; init; }
}

public sealed class ResegmentationBenchmarkSampleView
{
    public required int Id { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required IReadOnlyList<string> SourceSegments { get; init; }
    public required string TranslatedUnit { get; init; }
    public required int SegmentCount { get; init; }
    public int? TranslationRequestId { get; init; }
    public int? StartPosition { get; init; }
    public int? EndPosition { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed class NamedBenchmarkModel
{
    public string? Name { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class ResegmentationBenchmarkRunRequest
{
    public int SampleLimit { get; init; } = 100;
    public IReadOnlyList<int>? SampleIds { get; init; }
    public string? SourceLanguage { get; init; }
    public string? TargetLanguage { get; init; }
    public IReadOnlyList<NamedBenchmarkModel> CandidateModels { get; init; } = [];
    public IReadOnlyList<NamedBenchmarkModel> JudgeModels { get; init; } = [];
    public NamedBenchmarkModel? BacktranslationModel { get; init; }
    public bool IncludeAdversarialCalibration { get; init; } = true;
}

public sealed class ResegmentationBenchmarkRunResult
{
    public required int SampleCount { get; init; }
    public required IReadOnlyList<ResegmentationBenchmarkCandidateSummary> Candidates { get; init; }
    public required IReadOnlyList<ResegmentationBenchmarkJudgeSummary> Judges { get; init; }
    public required IReadOnlyList<ResegmentationBenchmarkSampleResult> Samples { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ResegmentationBenchmarkCandidateSummary
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required int SamplesAttempted { get; init; }
    public required int StructurallyValidSamples { get; init; }
    public required double StructuralValidityPercent { get; init; }
    public required long MeanAlignmentLatencyMs { get; init; }
    public required int JudgeModelVotes { get; init; }
    public required int JudgeDeterministicVotes { get; init; }
    public required int JudgeTies { get; init; }
    public required double JudgePreferencePercent { get; init; }
    public required double MeanJudgeAgreementPercent { get; init; }
    public required int AdversarialTrials { get; init; }
    public required double AdversarialPassPercent { get; init; }
    public int BacktranslationSamples { get; init; }
    public double? MeanSameSlotTokenF1Percent { get; init; }
    public double? MeanCrossSlotMarginPercentagePoints { get; init; }
    public double? CrossSlotLeakagePercent { get; init; }
}

public sealed class ResegmentationBenchmarkJudgeSummary
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required int PairwiseComparisons { get; init; }
    public required int DecisiveComparisons { get; init; }
    public required int AdversarialTrials { get; init; }
    public required double AdversarialPassPercent { get; init; }
    public required long MeanLatencyMs { get; init; }
}

public sealed class ResegmentationBenchmarkSampleResult
{
    public required int SampleId { get; init; }
    public required string SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public required IReadOnlyList<string> SourceSegments { get; init; }
    public required string TranslatedUnit { get; init; }
    public required IReadOnlyList<string> DeterministicSegments { get; init; }
    public required IReadOnlyList<ResegmentationBenchmarkCandidateResult> Candidates { get; init; }
}

public sealed class ResegmentationBenchmarkCandidateResult
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required bool StructurallyValid { get; init; }
    public IReadOnlyList<string>? Segments { get; init; }
    public string? Error { get; init; }
    public long? AlignmentLatencyMs { get; init; }
    public required int JudgeModelVotes { get; init; }
    public required int JudgeDeterministicVotes { get; init; }
    public required int JudgeTies { get; init; }
    public double? JudgeAgreementPercent { get; init; }
    public required int AdversarialTrials { get; init; }
    public required int AdversarialPasses { get; init; }
    public ResegmentationBacktranslationMetrics? Backtranslation { get; init; }
}

public sealed class ResegmentationBacktranslationMetrics
{
    public required IReadOnlyList<string> BacktranslatedSegments { get; init; }
    public required double MeanSameSlotTokenF1Percent { get; init; }
    public required double MeanCrossSlotMarginPercentagePoints { get; init; }
    public required double CrossSlotLeakagePercent { get; init; }
    public long? LatencyMs { get; init; }
}

public sealed class BlindSegmentationJudgeDecision
{
    public required string Winner { get; init; }
    public required double ScoreA { get; init; }
    public required double ScoreB { get; init; }
    public string? Reason { get; init; }
    public long? LatencyMs { get; init; }
}
