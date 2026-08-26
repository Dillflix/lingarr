using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Translation;
using Lingarr.Server.Services.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class SourceUnitDetectionServiceTests
{
    [Fact]
    public async Task HeuristicMode_PreservesExistingSentenceAwareGrouping()
    {
        var service = CreateService(new QueueHttpMessageHandler([]), new Dictionary<string, string>
        {
            ["source_unit_detection_mode"] = "heuristic"
        });

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(1, 1000, 1800, "I was spurred on by my daughter,"),
                Cue(2, 1850, 2500, "who I was surprised to discover"),
                Cue(3, 2550, 3300, "had seen Bonnie everywhere."),
                Cue(4, 3400, 4100, "Next sentence.")
            ]
        }, CancellationToken.None);

        Assert.Equal("heuristic", result.SelectedMethod);
        Assert.Equal(3, result.UnitLength);
    }

    [Fact]
    public async Task ModelMode_CanChooseBoundaryDifferentFromHeuristic()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"unitLength\":3}")
        ]);
        var service = CreateService(handler);

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(1, 1000, 1800, "This looks complete."),
                Cue(2, 1850, 2500, "But semantically"),
                Cue(3, 2550, 3300, "it belongs together."),
                Cue(4, 3400, 4100, "Next sentence.")
            ],
            Mode = "model",
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal("model", result.SelectedMethod);
        Assert.Equal(3, result.UnitLength);
        Assert.Equal(1, result.Heuristic.UnitLength);
        Assert.True(result.Model!.IsValid);
    }

    [Fact]
    public async Task InvalidModelBoundary_FallsBackToHeuristic()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"unitLength\":99}")
        ]);
        var service = CreateService(handler);

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(1, 1000, 1800, "one,"),
                Cue(2, 1850, 2500, "two."),
                Cue(3, 2550, 3300, "three.")
            ],
            Mode = "model",
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal("heuristic", result.SelectedMethod);
        Assert.Equal(2, result.UnitLength);
        Assert.False(result.Model!.IsValid);
        Assert.NotNull(result.FallbackReason);
    }

    [Fact]
    public async Task ValidatedMode_UsesIndependentJudgeWhenBoundariesDisagree()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"unitLength\":3}"),
            JsonResponse("{\"winner\":\"heuristic\",\"modelScore\":60,\"heuristicScore\":90,\"reason\":\"The first sentence is already complete.\"}")
        ]);
        var service = CreateService(handler);

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues =
            [
                Cue(1, 1000, 1800, "Complete sentence."),
                Cue(2, 1850, 2500, "Another"),
                Cue(3, 2550, 3300, "sentence."),
                Cue(4, 3400, 4100, "Next.")
            ],
            Mode = "validated",
            ModelOverride = ModelOverride("boundary-model"),
            ValidatorOverride = ModelOverride("judge-model")
        }, CancellationToken.None);

        Assert.Equal("heuristic", result.SelectedMethod);
        Assert.Equal(1, result.UnitLength);
        Assert.Equal("heuristic", result.Validator!.Winner);
        Assert.Equal("judge-model", result.Validator.Model);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task UnsupportedJsonSchema_RetriesWithoutResponseFormat()
    {
        var handler = new QueueHttpMessageHandler([
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("response_format unsupported", Encoding.UTF8, "text/plain")
            },
            JsonResponse("{\"unitLength\":2}")
        ]);
        var service = CreateService(handler);

        var result = await service.DetectAsync(new SourceUnitDetectionRequest
        {
            SourceLanguage = "English",
            Cues = [Cue(1, 1000, 1800, "one,"), Cue(2, 1850, 2500, "two.")],
            Mode = "model",
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal(2, result.UnitLength);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("response_format", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("response_format", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    private static SourceUnitDetectionCue Cue(int position, int start, int end, string text) => new()
    {
        Position = position,
        StartTime = start,
        EndTime = end,
        Text = text
    };

    private static SourceUnitDetectionModelOverride ModelOverride(string model = "boundary-model") => new()
    {
        Endpoint = "http://localhost:9999/v1",
        Model = model,
        ApiKey = string.Empty,
        SystemPrompt = "Return JSON only.",
        UserPrompt = "{sourceCuesJson}\n{candidateCount}",
        TimeoutSeconds = 30
    };

    private static SourceUnitDetectionService CreateService(
        QueueHttpMessageHandler handler,
        Dictionary<string, string>? persisted = null)
    {
        persisted ??= new Dictionary<string, string>();
        var settings = new Mock<ISettingService>();
        settings
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string key) => persisted.GetValueOrDefault(key));
        settings
            .Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> keys) =>
                keys.ToDictionary(key => key, key => persisted.GetValueOrDefault(key) ?? string.Empty));
        settings
            .Setup(service => service.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler, disposeHandler: false));

        return new SourceUnitDetectionService(
            settings.Object,
            httpClientFactory.Object,
            NullLogger<SourceUnitDetectionService>.Instance);
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
}
