using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(23)]
public class M0023_SeedSourceUnitDetectionSettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_mode", value = "heuristic" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_endpoint", value = "" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_api_key", value = "" });
        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_detection_system_prompt",
            value = "You are a subtitle source-unit boundary detector. Given consecutive source-language subtitle timing cues, decide how many leading cues, starting with cue 1, belong to the same complete linguistic utterance that should be translated together. Do not translate, rewrite, summarize, or add context. Separate distinct sentences, speakers, or dialogue turns. Return JSON only."
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_detection_user_prompt",
            value = "Source language: {sourceLanguage}\nCandidate cue count: {candidateCount}\n\nConsecutive subtitle cues (position, timing in milliseconds, text):\n{sourceCuesJson}\n\nChoose a contiguous prefix beginning with cue 1. Return unitLength from 1 through {candidateCount}. The selected cues should form one linguistic translation unit; later cues are only candidates and must not be included merely because they provide useful context.\n\nReturn JSON as {\"unitLength\":2}."
        });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_timeout_seconds", value = "120" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_validator_endpoint", value = "" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_validator_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "source_unit_detection_validator_api_key", value = "" });
        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_detection_validator_system_prompt",
            value = "You are a subtitle source-unit segmentation judge. Compare two proposed boundaries for the same consecutive source-language subtitle cues. Decide which grouping better identifies one complete linguistic unit beginning at cue 1. Prefer semantic/syntactic completeness while avoiding unrelated sentences or speaker turns. Return JSON only."
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_detection_validator_user_prompt",
            value = "Source language: {sourceLanguage}\nCandidate cue count: {candidateCount}\n\nConsecutive subtitle cues:\n{sourceCuesJson}\n\nModel proposal unitLength: {modelUnitLength}\nHeuristic proposal unitLength: {heuristicUnitLength}\n\nChoose which proposal better identifies exactly one linguistic translation unit beginning at cue 1. Return JSON with winner (\"model\" or \"heuristic\"), modelScore (0-100), heuristicScore (0-100), and reason."
        });
    }

    public override void Down()
    {
        foreach (var key in new[]
                 {
                     "source_unit_detection_mode",
                     "source_unit_detection_endpoint",
                     "source_unit_detection_model",
                     "source_unit_detection_api_key",
                     "source_unit_detection_system_prompt",
                     "source_unit_detection_user_prompt",
                     "source_unit_detection_timeout_seconds",
                     "source_unit_detection_validator_endpoint",
                     "source_unit_detection_validator_model",
                     "source_unit_detection_validator_api_key",
                     "source_unit_detection_validator_system_prompt",
                     "source_unit_detection_validator_user_prompt"
                 })
        {
            Delete.FromTable("settings").Row(new { key });
        }
    }
}
