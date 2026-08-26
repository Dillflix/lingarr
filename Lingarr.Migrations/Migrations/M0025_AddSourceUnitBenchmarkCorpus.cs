using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(25)]
public class M0025_AddSourceUnitBenchmarkCorpus : Migration
{
    public override void Up()
    {
        Create.Table("source_unit_benchmark_samples")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("fingerprint").AsString(64).NotNullable()
            .WithColumn("source_language").AsString(100).NotNullable()
            .WithColumn("candidate_cues_json").AsCustom("TEXT").NotNullable()
            .WithColumn("candidate_count").AsInt32().NotNullable()
            .WithColumn("heuristic_unit_length").AsInt32().NotNullable()
            .WithColumn("production_mode").AsString(32).Nullable()
            .WithColumn("production_model_unit_length").AsInt32().Nullable()
            .WithColumn("production_model_is_valid").AsBoolean().Nullable()
            .WithColumn("production_model_error").AsCustom("TEXT").Nullable()
            .WithColumn("production_model_latency_ms").AsInt64().Nullable()
            .WithColumn("production_validator_winner").AsString(32).Nullable()
            .WithColumn("production_validator_model").AsString(255).Nullable()
            .WithColumn("production_validator_model_score").AsDouble().Nullable()
            .WithColumn("production_validator_heuristic_score").AsDouble().Nullable()
            .WithColumn("production_validator_latency_ms").AsInt64().Nullable()
            .WithColumn("production_selected_unit_length").AsInt32().Nullable()
            .WithColumn("production_selected_method").AsString(32).Nullable()
            .WithColumn("translation_request_id").AsInt32().Nullable()
            .WithColumn("start_position").AsInt32().Nullable()
            .WithColumn("end_position").AsInt32().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        Create.Index("ux_source_unit_benchmark_samples_fingerprint")
            .OnTable("source_unit_benchmark_samples")
            .OnColumn("fingerprint").Unique();

        Create.Index("ix_source_unit_benchmark_samples_source_language")
            .OnTable("source_unit_benchmark_samples")
            .OnColumn("source_language").Ascending();

        Create.Index("ix_source_unit_benchmark_samples_translation_request_id")
            .OnTable("source_unit_benchmark_samples")
            .OnColumn("translation_request_id").Ascending();

        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_benchmark_capture_enabled",
            value = "true"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "source_unit_benchmark_max_samples",
            value = "5000"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "source_unit_benchmark_capture_enabled" });
        Delete.FromTable("settings").Row(new { key = "source_unit_benchmark_max_samples" });
        Delete.Table("source_unit_benchmark_samples");
    }
}
