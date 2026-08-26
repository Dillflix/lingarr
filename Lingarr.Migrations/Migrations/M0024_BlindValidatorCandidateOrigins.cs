using FluentMigrator;

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
