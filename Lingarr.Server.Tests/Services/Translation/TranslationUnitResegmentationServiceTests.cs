using System;
using System.Collections.Generic;
using System.Linq;
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

public class TranslationUnitResegmentationServiceTests
{
    [Fact]
    public void ValidateSegments_RejectsRewordedTargetText()
    {
        var validation = TranslationUnitResegmentationService.ValidateSegments(
            ["Jeg elsker", "dig meget."],
            ["I love", "you."],
            "Jeg elsker dig.");

        Assert.False(validation.IsValid);
        Assert.False(validation.TextPreserved);
        Assert.Contains("changed", validation.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelMode_AcceptsStructurallyExactAlignment()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"Jeg elsker\",\"dig.\"]}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Model,
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal("model", result.SelectedMethod);
        Assert.Equal(["Jeg elsker", "dig."], result.SelectedSegments);
        Assert.NotNull(result.Model);
        Assert.True(result.Model!.Validation.IsValid);
    }

    [Fact]
    public async Task ModelMode_InvalidAlignmentFallsBackToDeterministic()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"Jeg kan\",\"lide dig.\"]}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Model,
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal("deterministic", result.SelectedMethod);
        Assert.NotNull(result.Model);
        Assert.False(result.Model!.Validation.IsValid);
        Assert.NotNull(result.FallbackReason);
    }

    [Fact]
    public async Task ValidatedMode_UsesIndependentJudgeWinner()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"Jeg elsker\",\"dig.\"]}"),
            JsonResponse("{\"winner\":\"deterministic\",\"modelScore\":62,\"deterministicScore\":91,\"reason\":\"Better timing-slot alignment\"}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Validated,
            ModelOverride = ModelOverride("alignment-model"),
            ValidatorOverride = ModelOverride("judge-model")
        }, CancellationToken.None);

        Assert.Equal("deterministic", result.SelectedMethod);
        Assert.NotNull(result.Validator);
        Assert.Equal("deterministic", result.Validator!.Winner);
        Assert.Equal("judge-model", result.Validator.Model);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task DefaultValidatorPrompt_BlindsCandidateOrigins()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"Jeg elsker\",\"dig.\"]}"),
            JsonResponse("{\"winner\":\"A\",\"candidateAScore\":90,\"candidateBScore\":70,\"reason\":\"Better alignment\"}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Validated,
            ModelOverride = ModelOverride("alignment-model"),
            ValidatorOverride = new ResegmentationModelOverride
            {
                Endpoint = "http://localhost:9999/v1",
                Model = "judge-model",
                ApiKey = string.Empty,
                SystemPrompt = TranslationUnitResegmentationService.DefaultValidatorSystemPrompt,
                UserPrompt = TranslationUnitResegmentationService.DefaultValidatorUserPrompt,
                TimeoutSeconds = 30
            }
        }, CancellationToken.None);

        Assert.NotNull(result.Validator);
        var judgeRequest = handler.RequestBodies[1];
        Assert.Contains("Candidate A segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.Contains("Candidate B segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Model-assisted segmentation", judgeRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("Deterministic segmentation", judgeRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonSchemaUnsupported_RetriesWithoutResponseFormat()
    {
        var handler = new QueueHttpMessageHandler([
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("response_format unsupported", Encoding.UTF8, "text/plain")
            },
            JsonResponse("{\"segments\":[\"Jeg elsker\",\"dig.\"]}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = ["I love", "you."],
            TranslatedUnit = "Jeg elsker dig.",
            Mode = ResegmentationModes.Model,
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.Equal("model", result.SelectedMethod);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Contains("response_format", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("response_format", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferenceSegments_ReturnObjectiveBoundaryMetrics()
    {
        var handler = new QueueHttpMessageHandler([
            JsonResponse("{\"segments\":[\"For seks måneder siden begyndte jeg\",\"at lave en film om Bonnie Blue.\"]}")
        ]);
        var service = CreateService(handler);

        var result = await service.EvaluateAsync(new ResegmentationEvaluationRequest
        {
            SourceLanguage = "English",
            TargetLanguage = "Danish",
            SourceSegments = [
                "Six months ago, I began",
                "to make a film about Bonnie Blue."
            ],
            TranslatedUnit = "For seks måneder siden begyndte jeg at lave en film om Bonnie Blue.",
            ReferenceSegments = [
                "For seks måneder siden begyndte jeg",
                "at lave en film om Bonnie Blue."
            ],
            Mode = ResegmentationModes.Model,
            ModelOverride = ModelOverride()
        }, CancellationToken.None);

        Assert.NotNull(result.ReferenceValidation);
        Assert.True(result.ReferenceValidation!.IsValid);
        Assert.NotNull(result.ModelReferenceMetrics);
        Assert.Equal(0, result.ModelReferenceMetrics!.MeanAbsoluteErrorCharacters);
        Assert.Equal(100, result.ModelReferenceMetrics.ExactSegmentMatchPercent);
        Assert.NotNull(result.DeterministicReferenceMetrics);
    }

    [Fact]
    public void CalculateBoundaryMetrics_ReportsCharacterBoundaryError()
    {
        var metrics = TranslationUnitResegmentationService.CalculateBoundaryMetrics(
            ["abc", "def"],
            ["ab", "cdef"]);

        Assert.Equal(1, metrics.BoundaryCount);
        Assert.Equal(1, metrics.MeanAbsoluteErrorCharacters);
        Assert.Equal(1, metrics.MaxAbsoluteErrorCharacters);
        Assert.Equal(100, metrics.BoundariesWithinFiveCharactersPercent);
        Assert.Equal(0, metrics.ExactSegmentMatchPercent);
    }

    private static TranslationUnitResegmentationService CreateService(QueueHttpMessageHandler handler)
    {
        var settings = new Mock<ISettingService>();
        settings
            .Setup(service => service.GetSettings(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync(new Dictionary<string, string>());
        settings
            .Setup(service => service.GetSetting(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        settings
            .Setup(service => service.GetEncryptedSetting(It.IsAny<string>()))
            .ReturnsAsync(string.Empty);

        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory
            .Setup(factory => factory.CreateClient(It.IsAny<string>()))
            .Returns(new HttpClient(handler, disposeHandler: false));

        return new TranslationUnitResegmentationService(
            settings.Object,
            httpClientFactory.Object,
            NullLogger<TranslationUnitResegmentationService>.Instance);
    }

    private static ResegmentationModelOverride ModelOverride(string model = "alignment-model") => new()
    {
        Endpoint = "http://localhost:9999/v1",
        Model = model,
        ApiKey = string.Empty,
        SystemPrompt = "Return JSON only.",
        UserPrompt = "{sourceSegmentsJson}\n{translatedUnit}\n{segmentCount}",
        TimeoutSeconds = 30
    };

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

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

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
