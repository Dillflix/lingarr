using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Core.Data;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Translation;
using Lingarr.Server.Services.Translation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class CapturingSourceUnitDetectionServiceTests
{
    [Fact]
    public async Task ProductionDetection_AutomaticallyCapturesExactCandidateWindow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = Settings();
        var inner = new SourceUnitDetectionService(
            settings.Object,
            EmptyHttpFactory().Object,
            NullLogger<SourceUnitDetectionService>.Instance);
        var decorator = new CapturingSourceUnitDetectionService(
            inner,
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            settings.Object,
            NullLogger<CapturingSourceUnitDetectionService>.Instance);

        var result = await decorator.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(25, 1000, 1700, "This sentence continues,"),
                Cue(26, 1750, 2400, "onto the next subtitle."),
                Cue(27, 2450, 3100, "Another sentence.")
            ]
            // Mode intentionally null: this is the production persisted-settings path.
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.UnitLength);
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var sample = await db.SourceUnitBenchmarkSamples.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, sample.CandidateCount);
        Assert.Equal(25, sample.StartPosition);
        Assert.Equal(27, sample.EndPosition);
        Assert.Equal(2, sample.HeuristicUnitLength);
        Assert.Equal(2, sample.ProductionSelectedUnitLength);
        Assert.Equal("heuristic", sample.ProductionSelectedMethod);
        Assert.Contains("onto the next subtitle", sample.CandidateCuesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplicitBenchmarkMode_DoesNotPolluteLiveCorpus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var settings = Settings();
        var inner = new SourceUnitDetectionService(
            settings.Object,
            EmptyHttpFactory().Object,
            NullLogger<SourceUnitDetectionService>.Instance);
        var decorator = new CapturingSourceUnitDetectionService(
            inner,
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            settings.Object,
            NullLogger<CapturingSourceUnitDetectionService>.Instance);

        await decorator.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues = [Cue(1, 1000, 1700, "one,"), Cue(2, 1750, 2400, "two.")],
            Mode = SourceUnitDetectionModes.Heuristic
        }, TestContext.Current.CancellationToken);

        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        Assert.Equal(0, await db.SourceUnitBenchmarkSamples.CountAsync(TestContext.Current.CancellationToken));
    }

    private static SourceUnitDetectionCue Cue(int position, int start, int end, string text) => new()
    {
        Position = position,
        StartTime = start,
        EndTime = end,
        Text = text
    };

    private static Mock<ISettingService> Settings()
    {
        var settings = new Mock<ISettingService>();
        settings.Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => key switch
            {
                "source_unit_detection_mode" => "heuristic",
                "source_unit_benchmark_capture_enabled" => "true",
                "source_unit_benchmark_max_samples" => "5000",
                _ => null
            });
        settings.Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, string>());
        settings.Setup(service => service.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);
        return settings;
    }

    private static Mock<IHttpClientFactory> EmptyHttpFactory()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient());
        return factory;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Provider { get; }

        private Fixture(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            Provider = provider;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var services = new ServiceCollection();
            services.AddDbContext<LingarrDbContext>(options =>
                options.UseSqlite(connection).UseSnakeCaseNamingConvention());
            var provider = services.BuildServiceProvider();
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            }
            return new Fixture(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
