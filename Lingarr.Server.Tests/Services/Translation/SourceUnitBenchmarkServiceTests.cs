using System.Net;
using System.Net.Http;
using System.Text;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
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

public class SourceUnitBenchmarkServiceTests
{
    [Fact]
    public async Task Capture_PersistsExactCandidateWindowAndProductionDecision()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var settings = SettingsMock();
        var handler = new QueueHttpMessageHandler([]);
        var httpFactory = HttpFactory(handler);
        var detector = new SourceUnitDetectionService(
            settings.Object,
            httpFactory.Object,
            NullLogger<SourceUnitDetectionService>.Instance);
        var benchmark = new SourceUnitBenchmarkService(
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            settings.Object,
            detector,
            httpFactory.Object,
            NullLogger<SourceUnitBenchmarkService>.Instance);

        var cues = new[]
        {
            Cue(10, 1000, 1700, "I was surprised,"),
            Cue(11, 1750, 2400, "when she told me.")
        };
        var detection = new SourceUnitDetectionResult
        {
            Mode = SourceUnitDetectionModes.Validated,
            SelectedMethod = SourceUnitDetectionModes.Model,
            UnitLength = 2,
            Heuristic = new SourceUnitDetectionCandidate
            {
                Method = SourceUnitDetectionModes.Heuristic,
                UnitLength = 2,
                IsValid = true,
                LatencyMs = 0
            },
            Model = new SourceUnitDetectionCandidate
            {
                Method = SourceUnitDetectionModes.Model,
                UnitLength = 2,
                IsValid = true,
                LatencyMs = 17,
                Model = "boundary-model"
            }
        };

        var added = await benchmark.CaptureAsync(new SourceUnitBenchmarkCaptureRequest
        {
            SourceLanguage = "English",
            Cues = cues,
            Detection = detection,
            TranslationRequestId = 42,
            StartPosition = 10,
            EndPosition = 11
        }, TestContext.Current.CancellationToken);

        Assert.True(added);
        using var scope = fixture.Provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
        var sample = await db.SourceUnitBenchmarkSamples.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, sample.CandidateCount);
        Assert.Equal(2, sample.HeuristicUnitLength);
        Assert.Equal(2, sample.ProductionModelUnitLength);
        Assert.Equal("model", sample.ProductionSelectedMethod);
        Assert.Equal(42, sample.TranslationRequestId);
        Assert.Contains("I was surprised", sample.CandidateCuesJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Benchmark_UsesBlindRandomizedJudgeWithoutProposalProvenance()
    {
        await using var fixture = await ServiceFixture.CreateAsync();
        var settings = SettingsMock();
        using (var scope = fixture.Provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();
            db.SourceUnitBenchmarkSamples.Add(new SourceUnitBenchmarkSample
            {
                Fingerprint = new string('a', 64),
                SourceLanguage = "English",
                CandidateCuesJson = "[{\"Position\":1,\"StartTime\":1000,\"EndTime\":1700,\"Text\":\"Complete sentence.\"},{\"Position\":2,\"StartTime\":1750,\"EndTime\":2400,\"Text\":\"Another sentence.\"}]",
                CandidateCount = 2,
                HeuristicUnitLength = 1
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"unitLength\":2}"),
            JsonResponse("{\"winner\":\"A\",\"candidateAScore\":90,\"candidateBScore\":55,\"reason\":\"better unit\"}")
        ]);
        var httpFactory = HttpFactory(handler);
        var detector = new SourceUnitDetectionService(
            settings.Object,
            httpFactory.Object,
            NullLogger<SourceUnitDetectionService>.Instance);
        var benchmark = new SourceUnitBenchmarkService(
            fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            settings.Object,
            detector,
            httpFactory.Object,
            NullLogger<SourceUnitBenchmarkService>.Instance);

        var result = await benchmark.RunAsync(new SourceUnitBenchmarkRunRequest
        {
            SampleLimit = 10,
            IncludeAdversarialCalibration = false,
            CandidateModels = [new SourceUnitBenchmarkModel
            {
                Name = "candidate",
                Endpoint = "http://localhost:9999/v1",
                Model = "boundary-model",
                SystemPrompt = "Return JSON only.",
                UserPrompt = "{sourceCuesJson}\n{candidateCount}",
                TimeoutSeconds = 30
            }],
            JudgeModels = [new SourceUnitBenchmarkModel
            {
                Name = "judge",
                Endpoint = "http://localhost:9999/v1",
                Model = "judge-model",
                TimeoutSeconds = 30
            }]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SampleCount);
        var summary = Assert.Single(result.Candidates);
        Assert.Equal(1, summary.StructurallyValidSamples);
        Assert.Equal(1, summary.DisagreementSamples);
        Assert.Equal(2, handler.RequestBodies.Count);

        var judgeRequest = handler.RequestBodies[1];
        Assert.Contains("Candidate A unitLength", judgeRequest, StringComparison.Ordinal);
        Assert.Contains("Candidate B unitLength", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Model proposal", judgeRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Heuristic proposal", judgeRequest, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceUnitDetectionCue Cue(int position, int start, int end, string text) => new()
    {
        Position = position,
        StartTime = start,
        EndTime = end,
        Text = text
    };

    private static Mock<ISettingService> SettingsMock()
    {
        var settings = new Mock<ISettingService>();
        settings.Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => key switch
            {
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

    private static Mock<IHttpClientFactory> HttpFactory(QueueHttpMessageHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static HttpResponseMessage JsonResponse(string assistantContent)
    {
        var escaped = assistantContent
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        var body = $"{{\"choices\":[{{\"message\":{{\"content\":\"{escaped}\"}}}}]}}";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class QueueHttpMessageHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response remains.");
            }
            return _responses.Dequeue();
        }
    }

    private sealed class ServiceFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public ServiceProvider Provider { get; }

        private ServiceFixture(SqliteConnection connection, ServiceProvider provider)
        {
            _connection = connection;
            Provider = provider;
        }

        public static async Task<ServiceFixture> CreateAsync()
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
            return new ServiceFixture(connection, provider);
        }

        public async ValueTask DisposeAsync()
        {
            await Provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
