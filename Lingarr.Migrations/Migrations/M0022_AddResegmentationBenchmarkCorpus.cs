using FluentMigrator;

namespace Lingarr.Migrations.Migrations;

[Migration(22)]
public class M0022_AddResegmentationBenchmarkCorpus : Migration
{
    public override void Up()
    {
        Create.Table("resegmentation_benchmark_samples")
            .WithColumn("id").AsInt32().PrimaryKey().Identity()
            .WithColumn("fingerprint").AsString(64).NotNullable()
            .WithColumn("source_language").AsString(100).NotNullable()
            .WithColumn("target_language").AsString(100).NotNullable()
            .WithColumn("source_segments_json").AsCustom("TEXT").NotNullable()
            .WithColumn("translated_unit").AsCustom("TEXT").NotNullable()
            .WithColumn("segment_count").AsInt32().NotNullable()
            .WithColumn("translation_request_id").AsInt32().Nullable()
            .WithColumn("start_position").AsInt32().Nullable()
            .WithColumn("end_position").AsInt32().Nullable()
            .WithColumn("created_at").AsDateTime().NotNullable()
            .WithColumn("updated_at").AsDateTime().NotNullable();

        Create.Index("ux_resegmentation_benchmark_samples_fingerprint")
            .OnTable("resegmentation_benchmark_samples")
            .OnColumn("fingerprint").Unique();

        Create.Index("ix_resegmentation_benchmark_samples_language_pair")
            .OnTable("resegmentation_benchmark_samples")
            .OnColumn("source_language").Ascending()
            .OnColumn("target_language").Ascending();

        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_benchmark_capture_enabled",
            value = "true"
        });
        Insert.IntoTable("settings").Row(new
        {
            key = "resegmentation_benchmark_max_samples",
            value = "500"
        });
    }

    public override void Down()
    {
        Delete.FromTable("settings").Row(new { key = "resegmentation_benchmark_capture_enabled" });
        Delete.FromTable("settings").Row(new { key = "resegmentation_benchmark_max_samples" });
        Delete.Table("resegmentation_benchmark_samples");
    }
}
