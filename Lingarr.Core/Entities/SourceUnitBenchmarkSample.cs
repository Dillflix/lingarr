namespace Lingarr.Core.Entities;

/// <summary>
/// A captured source-language boundary-decision opportunity. The complete safe candidate cue
/// window is stored before translation so source-unit models can be benchmarked later without
/// target-language labels.
/// </summary>
public class SourceUnitBenchmarkSample : BaseEntity
{
    public required string Fingerprint { get; set; }
    public required string SourceLanguage { get; set; }
    public required string CandidateCuesJson { get; set; }
    public int CandidateCount { get; set; }
    public int HeuristicUnitLength { get; set; }

    public string? ProductionMode { get; set; }
    public int? ProductionModelUnitLength { get; set; }
    public bool? ProductionModelIsValid { get; set; }
    public string? ProductionModelError { get; set; }
    public long? ProductionModelLatencyMs { get; set; }
    public string? ProductionValidatorWinner { get; set; }
    public string? ProductionValidatorModel { get; set; }
    public double? ProductionValidatorModelScore { get; set; }
    public double? ProductionValidatorHeuristicScore { get; set; }
    public long? ProductionValidatorLatencyMs { get; set; }
    public int? ProductionSelectedUnitLength { get; set; }
    public string? ProductionSelectedMethod { get; set; }

    public int? TranslationRequestId { get; set; }
    public int? StartPosition { get; set; }
    public int? EndPosition { get; set; }
}
