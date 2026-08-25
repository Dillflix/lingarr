using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(21)]
public class M0021_SeedResegmentationSettings : Migration
{
    public override void Up()
    {
        Insert.IntoTable("settings").Row(new { key = "resegmentation_mode", value = "deterministic" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_endpoint", value = "" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_api_key", value = "" });
        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_system_prompt",
            value = "You are a subtitle alignment engine. Split an existing target-language translation across the exact number of source subtitle timing slots. Do not translate, paraphrase, omit, duplicate, or reorder text. Preserve the translated wording exactly except for boundary whitespace. Return JSON only."
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_user_prompt",
            value = "Source language: {sourceLanguage}\nTarget language: {targetLanguage}\nSegment count: {segmentCount}\n\nSource subtitle segments:\n{sourceSegmentsJson}\n\nTarget translation to align:\n{translatedUnit}\n\nReturn exactly {segmentCount} target segments in JSON as {\"segments\":[\"...\", \"...\"]}."
        });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_timeout_seconds", value = "120" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_validator_endpoint", value = "" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_validator_model", value = "" });
        Insert.IntoTable("settings").Row(new { key = "resegmentation_validator_api_key", value = "" });
        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_validator_system_prompt",
            value = "You are a subtitle segmentation judge. Compare two segmentations of exactly the same target translation against the source timing segments. Judge semantic alignment of each target segment to its source slot, readability, punctuation, and balance. Do not reward changes to the translation wording. Return JSON only."
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_validator_user_prompt",
            value = "Source language: {sourceLanguage}\nTarget language: {targetLanguage}\n\nSource subtitle segments:\n{sourceSegmentsJson}\n\nComplete target translation:\n{translatedUnit}\n\nModel-assisted segmentation:\n{modelSegmentsJson}\n\nDeterministic segmentation:\n{deterministicSegmentsJson}\n\nChoose which segmentation better aligns the unchanged target translation to the source timing slots. Return JSON with winner (\"model\" or \"deterministic\"), modelScore (0-100), deterministicScore (0-100), and reason."
        });
    }

    public override void Down()
    {
        foreach (var key in new[]
                 {
                     "resegmentation_mode",
                     "resegmentation_endpoint",
                     "resegmentation_model",
                     "resegmentation_api_key",
                     "resegmentation_system_prompt",
                     "resegmentation_user_prompt",
                     "resegmentation_timeout_seconds",
                     "resegmentation_validator_endpoint",
                     "resegmentation_validator_model",
                     "resegmentation_validator_api_key",
                     "resegmentation_validator_system_prompt",
                     "resegmentation_validator_user_prompt"
                 })
        {
            Delete.FromTable("settings").Row(new { key });
        }
    }
}
