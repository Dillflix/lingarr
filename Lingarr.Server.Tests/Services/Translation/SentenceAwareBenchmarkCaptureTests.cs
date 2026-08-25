using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Models.Translation;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class SentenceAwareBenchmarkCaptureTests
{
    [Fact]
    public async Task MultiCueUnit_CapturesExactCompleteTranslationBeforeResegmentation()
    {
        const string translatedUnit = "Jeg blev opmuntret af min 15-årige datter, som jeg med overraskelse opdagede havde set Bonnie overalt på hendes sociale medier.";
        var provider = new RecordingProvider(_ => translatedUnit);
        var progress = ProgressMock();
        var benchmark = new Mock<IResegmentationBenchmarkService>();
        ResegmentationBenchmarkCaptureRequest? captured = null;
        benchmark.Setup(service => service.CaptureAsync(
                It.IsAny<ResegmentationBenchmarkCaptureRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback<ResegmentationBenchmarkCaptureRequest, CancellationToken>((request, _) => captured = request)
            .Returns(Task.CompletedTask);

        var translator = new SubtitleTranslationService(
            [new TranslationServiceEntry("test", provider, null)],
            NullLogger.Instance,
            progress.Object);
        var service = new SentenceAwareTranslationUnitService(
            translator,
            progress.Object,
            NullLogger.Instance,
            resegmentationService: null,
            benchmarkService: benchmark.Object);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(36, 1000, 1800, "I was spurred on by my 15-year-old daughter,"),
            Subtitle(37, 1850, 2500, "who I was surprised to discover"),
            Subtitle(38, 2550, 3400, "had seen Bonnie all over her social media.")
        };
        var translationRequest = Request();
        translationRequest.Id = 123;

        await service.TranslateSubtitles(
            subtitles,
            translationRequest,
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(123, captured.TranslationRequestId);
        Assert.Equal(36, captured.StartPosition);
        Assert.Equal(38, captured.EndPosition);
        Assert.Equal(["I was spurred on by my 15-year-old daughter,", "who I was surprised to discover", "had seen Bonnie all over her social media."], captured.SourceSegments);
        Assert.Equal(translatedUnit, captured.TranslatedUnit);
        benchmark.Verify(service => service.CaptureAsync(
            It.IsAny<ResegmentationBenchmarkCaptureRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OneCueUnit_IsNotAddedToResegmentationBenchmarkCorpus()
    {
        var provider = new RecordingProvider(source => $"tr:{source}");
        var progress = ProgressMock();
        var benchmark = new Mock<IResegmentationBenchmarkService>();
        var translator = new SubtitleTranslationService(
            [new TranslationServiceEntry("test", provider, null)],
            NullLogger.Instance,
            progress.Object);
        var service = new SentenceAwareTranslationUnitService(
            translator,
            progress.Object,
            NullLogger.Instance,
            resegmentationService: null,
            benchmarkService: benchmark.Object);

        await service.TranslateSubtitles(
            [Subtitle(1, 1000, 1800, "This is complete.")],
            Request(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            TestContext.Current.CancellationToken);

        benchmark.Verify(service => service.CaptureAsync(
            It.IsAny<ResegmentationBenchmarkCaptureRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IProgressService> ProgressMock()
    {
        var progress = new Mock<IProgressService>();
        progress.Setup(service => service.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        progress.Setup(service => service.EmitLine(
                It.IsAny<TranslationRequest>(),
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<LanguagePair?>()))
            .Returns(Task.CompletedTask);
        progress.Setup(service => service.EmitLines(
                It.IsAny<TranslationRequest>(),
                It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);
        return progress;
    }

    private static TranslationRequest Request() => new()
    {
        Title = "benchmark-capture-test",
        SourceLanguage = "English",
        TargetLanguage = "Danish",
        MediaType = MediaType.Movie,
        Status = TranslationStatus.InProgress
    };

    private static SubtitleItem Subtitle(int position, int start, int end, params string[] lines) => new()
    {
        Position = position,
        StartTime = start,
        EndTime = end,
        Lines = lines.ToList(),
        PlaintextLines = lines.ToList()
    };

    private sealed class RecordingProvider : ITranslationService, IContextualTranslationService
    {
        private readonly Func<string, string> _translate;

        public RecordingProvider(Func<string, string> translate)
        {
            _translate = translate;
        }

        public string? ModelName => "test";

        public Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken) =>
            Task.FromResult(_translate(text));

        public Task<string> TranslateWithContextAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            IReadOnlyList<TranslationContextPair>? contextPairsBefore,
            CancellationToken cancellationToken) =>
            Task.FromResult(_translate(text));

        public Task<List<SourceLanguage>> GetLanguages() => Task.FromResult(new List<SourceLanguage>());
        public Task<ModelsResponse> GetModels() => Task.FromResult(new ModelsResponse());

        public Task<LanguagePair?> GetLanguagePair(
            string requestedSource,
            string requestedTarget,
            CancellationToken cancellationToken) =>
            Task.FromResult<LanguagePair?>(new LanguagePair
            {
                Source = requestedSource,
                Target = requestedTarget,
                Tier = MatchTier.Exact
            });
    }
}
