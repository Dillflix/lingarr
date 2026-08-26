namespace Lingarr.Server.Models.Translation;

public sealed class SourceUnitBenchmarkCaptureRequest
{
    public required string SourceLanguage { get; init; }
    public required IReadOnlyList<SourceUnitDetectionCue> Cues { get; init; }
    public required SourceUnitDetectionResult Detection { get; init; }
    public int? TranslationRequestId { get; init; }
    public int? StartPosition { get; init; }
    public int? EndPosition { get; init; }
}

public sealed class SourceUnitBenchmarkSampleView
{
    public required int Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string SourceLanguage { get; init; }
    public required IReadOnlyList<SourceUnitDetectionCue> Cues { get; init; }
    public required int CandidateCount { get; init; }
    public required int HeuristicUnitLength { get; init; }
    public string? ProductionMode { get; init; }
    public int? ProductionModelUnitLength { get; init; }
    public bool? ProductionModelIsValid { get; init; }
    public string? ProductionModelError { get; init; }
    public long? ProductionModelLatencyMs { get; init; }
    public string? ProductionValidatorWinner { get; init; }
    public string? ProductionValidatorModel { get; init; }
    public double? ProductionValidatorModelScore { get; init; }
    public double? ProductionValidatorHeuristicScore { get; init; }
    public long? ProductionValidatorLatencyMs { get; init; }
    public int? ProductionSelectedUnitLength { get; init; }
    public string? ProductionSelectedMethod { get; init; }
    public int? TranslationRequestId { get; init; }
    public int? StartPosition { get; init; }
    public int? EndPosition { get; init; }
}

public sealed class SourceUnitBenchmarkModel
{
    public string? Name { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string? ApiKey { get; init; }
    public string? SystemPrompt { get; init; }
    public string? UserPrompt { get; init; }
    public int? TimeoutSeconds { get; init; }
}

public sealed class SourceUnitBenchmarkRunRequest
{
    public int SampleLimit { get; init; } = 100;
    public IReadOnlyList<int>? SampleIds { get; init; }
    public string? SourceLanguage { get; init; }
    public IReadOnlyList<SourceUnitBenchmarkModel> CandidateModels { get; init; } = [];
    public IReadOnlyList<SourceUnitBenchmarkModel> JudgeModels { get; init; } = [];
    public bool IncludeAdversarialCalibration { get; init; } = true;
}

public sealed class SourceUnitBenchmarkCandidateSummary
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required int SamplesAttempted { get; init; }
    public required int StructurallyValidSamples { get; init; }
    public required double StructuralValidityPercent { get; init; }
    public required int DisagreementSamples { get; init; }
    public required double DisagreementPercent { get; init; }
    public required double MeanBoundaryLatencyMs { get; init; }
    public required int JudgeModelVotes { get; init; }
    public required int JudgeHeuristicVotes { get; init; }
    public required int JudgeTies { get; init; }
    public required double JudgePreferencePercent { get; init; }
    public required double MeanJudgeAgreementPercent { get; init; }
    public required int AdversarialTrials { get; init; }
    public required double AdversarialPassPercent { get; init; }
}

public sealed class SourceUnitBenchmarkJudgeSummary
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required int PairwiseComparisons { get; init; }
    public required int DecisiveComparisons { get; init; }
    public required int AdversarialTrials { get; init; }
    public required double AdversarialPassPercent { get; init; }
    public required double MeanLatencyMs { get; init; }
}

public sealed class SourceUnitBenchmarkCandidateResult
{
    public required string Name { get; init; }
    public required string Model { get; init; }
    public required bool StructurallyValid { get; init; }
    public int? UnitLength { get; init; }
    public string? Error { get; init; }
    public long? BoundaryLatencyMs { get; init; }
    public required bool DisagreesWithHeuristic { get; init; }
    public required int JudgeModelVotes { get; init; }
    public required int JudgeHeuristicVotes { get; init; }
    public required int JudgeTies { get; init; }
    public double? JudgeAgreementPercent { get; init; }
    public required int AdversarialTrials { get; init; }
    public required int AdversarialPasses { get; init; }
}

public sealed class SourceUnitBenchmarkSampleResult
{
    public required int SampleId { get; init; }
    public required string SourceLanguage { get; init; }
    public required IReadOnlyList<SourceUnitDetectionCue> Cues { get; init; }
    public required int HeuristicUnitLength { get; init; }
    public int? CapturedProductionModelUnitLength { get; init; }
    public int? CapturedProductionSelectedUnitLength { get; init; }
    public string? CapturedProductionSelectedMethod { get; init; }
    public required IReadOnlyList<SourceUnitBenchmarkCandidateResult> Candidates { get; init; }
}

public sealed class SourceUnitBenchmarkRunResult
{
    public required int SampleCount { get; init; }
    public required IReadOnlyList<SourceUnitBenchmarkCandidateSummary> Candidates { get; init; }
    public required IReadOnlyList<SourceUnitBenchmarkJudgeSummary> Judges { get; init; }
    public required IReadOnlyList<SourceUnitBenchmarkSampleResult> Samples { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}
