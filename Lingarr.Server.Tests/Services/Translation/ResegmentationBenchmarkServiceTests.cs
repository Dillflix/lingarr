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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class ResegmentationBenchmarkServiceTests
{
    [Fact]
    public async Task HistoryHarvester_ReconstructsMultiCueUnitWithoutGoldDanish()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var settings = SettingsMock();

        var request = new TranslationRequest
        {
            Title = "Benchmark source",
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            MediaType = default,
            Status = default,
            CompletedAt = DateTimeOffset.UtcNow
        };
        fixture.Context.TranslationRequests.Add(request);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        fixture.Context.TranslationRequestLines.AddRange(
            Line(request.Id, 36, "I was spurred on by my 15-year-old daughter,", "Jeg blev opmuntret af min 15-årige datter,"),
            Line(request.Id, 37, "who I was surprised to discover", "som jeg med overraskelse opdagede"),
            Line(request.Id, 38, "had seen Bonnie all over her social media.", "havde set Bonnie overalt på hendes sociale medier."));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var harvester = new ResegmentationBenchmarkHistoryHarvester(
            fixture.Context,
            settings.Object,
            NullLogger<ResegmentationBenchmarkHistoryHarvester>.Instance);

        var result = await harvester.HarvestAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.RequestsScanned);
        Assert.Equal(1, result.MultiCueUnitsFound);
        Assert.Equal(1, result.NewSamplesCaptured);

        var sample = await fixture.Context.ResegmentationBenchmarkSamples.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, sample.SegmentCount);
        Assert.Contains("I was spurred on", sample.SourceSegmentsJson, StringComparison.Ordinal);
        Assert.Equal(
            "Jeg blev opmuntret af min 15-årige datter, som jeg med overraskelse opdagede havde set Bonnie overalt på hendes sociale medier.",
            sample.TranslatedUnit);
    }

    [Fact]
    public async Task BenchmarkRunner_UsesCapturedCorpusAndReportsValidCandidate()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var settings = SettingsMock();
        fixture.Context.ResegmentationBenchmarkSamples.Add(new ResegmentationBenchmarkSample
        {
            Fingerprint = new string('a', 64),
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegmentsJson = "[\"I love\",\"you.\"]",
            TranslatedUnit = "Jeg elsker dig.",
            SegmentCount = 2
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"Jeg elsker\",\"dig.\"]}")
        ]);
        var httpFactory = new Mock<IHttpClientFactory>();
        httpFactory.Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler, disposeHandler: false));

        var resegmentation = new TranslationUnitResegmentationService(
            settings.Object,
            httpFactory.Object,
            NullLogger<TranslationUnitResegmentationService>.Instance);
        var benchmark = new ResegmentationBenchmarkService(
            fixture.Context,
            settings.Object,
            resegmentation,
            httpFactory.Object,
            NullLogger<ResegmentationBenchmarkService>.Instance);

        var result = await benchmark.RunAsync(new ResegmentationBenchmarkRunRequest
        {
            SampleLimit = 10,
            AutoHarvest = false,
            IncludeAdversarialCalibration = false,
            CandidateModels = [new NamedBenchmarkModel
            {
                Name = "candidate",
                Endpoint = "http://localhost:9999/v1",
                Model = "alignment-model",
                SystemPrompt = "Return JSON only.",
                UserPrompt = "{sourceSegmentsJson}\n{translatedUnit}\n{segmentCount}",
                TimeoutSeconds = 30
            }]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.SampleCount);
        var summary = Assert.Single(result.Candidates);
        Assert.Equal(1, summary.SamplesAttempted);
        Assert.Equal(1, summary.StructurallyValidSamples);
        Assert.Equal(100, summary.StructuralValidityPercent);
        Assert.Single(handler.RequestBodies);
    }

    private static TranslationRequestLine Line(int requestId, int position, string source, string target) => new()
    {
        TranslationRequestId = requestId,
        Position = position,
        Source = source,
        Target = target
    };

    private static Mock<ISettingService> SettingsMock()
    {
        var settings = new Mock<ISettingService>();
        settings.Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => key.EndsWith("max_samples", StringComparison.Ordinal) ? "500" : null);
        settings.Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, string>());
        settings.Setup(service => service.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);
        return settings;
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

    private sealed class DatabaseFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public LingarrDbContext Context { get; }

        private DatabaseFixture(SqliteConnection connection, LingarrDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<LingarrDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            var context = new LingarrDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new DatabaseFixture(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
