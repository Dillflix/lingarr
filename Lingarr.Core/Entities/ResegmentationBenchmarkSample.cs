namespace Lingarr.Core.Entities;

/// <summary>
/// A locally captured multi-cue translation unit used to benchmark subtitle resegmentation.
/// The source segments and complete target translation are stored exactly as they were presented
/// to the resegmentation stage; no human/reference target segmentation is required.
/// </summary>
public class ResegmentationBenchmarkSample : BaseEntity
{
    public required string Fingerprint { get; set; }
    public required string SourceLanguage { get; set; }
    public required string TargetLanguage { get; set; }
    public required string SourceSegmentsJson { get; set; }
    public required string TranslatedUnit { get; set; }
    public int SegmentCount { get; set; }
    public int? TranslationRequestId { get; set; }
    public int? StartPosition { get; set; }
    public int? EndPosition { get; set; }
}
