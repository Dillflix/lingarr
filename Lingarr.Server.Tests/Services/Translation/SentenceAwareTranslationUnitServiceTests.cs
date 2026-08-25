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
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class SentenceAwareTranslationUnitServiceTests
{
    [Fact]
    public async Task CompleteCues_AreOneCueTranslationUnits()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1800, "Hello."),
            Subtitle(2, 1900, 2700, "Next sentence.")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Equal(["Hello.", "Next sentence."], harness.Provider.Sources);
        Assert.Equal("tr:Hello.", string.Join(" ", subtitles[0].TranslatedLines));
        Assert.Equal("tr:Next sentence.", string.Join(" ", subtitles[1].TranslatedLines));
    }

    [Fact]
    public async Task SplitSentence_TranslatesAllCuesAsOneUnit()
    {
        const string target = "Jeg blev opmuntret af min 15-årige datter, som jeg med overraskelse opdagede havde set Bonnie overalt på hendes sociale medier.";
        var harness = CreateHarness(_ => target);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(36, 1000, 1800, "I was spurred on by my 15-year-old daughter,"),
            Subtitle(37, 1850, 2500, "who I was surprised to discover"),
            Subtitle(38, 2550, 3400, "had seen Bonnie all over her social media.")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Single(harness.Provider.Sources);
        Assert.Equal(
            "I was spurred on by my 15-year-old daughter, who I was surprised to discover had seen Bonnie all over her social media.",
            harness.Provider.Sources[0]);
        Assert.All(subtitles, subtitle => Assert.NotEmpty(subtitle.TranslatedLines));
        Assert.Equal(
            Normalize(target),
            Normalize(string.Join(" ", subtitles.SelectMany(subtitle => subtitle.TranslatedLines))));
    }

    [Fact]
    public async Task TerminalPunctuation_EndsTranslationUnit()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1800, "This is complete."),
            Subtitle(2, 1850, 2500, "and this begins with a continuation word.")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Equal(2, harness.Provider.Sources.Count);
    }

    [Fact]
    public async Task LowercaseContinuation_JoinsAcrossCueBoundary()
    {
        var harness = CreateHarness(_ => "Hvis jeg var atlet og løb mange maratoner, ville ingen være ligeglade.");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(24, 1000, 1800, "If I was an athlete and did loads of marathons,"),
            Subtitle(25, 1850, 2500, "no-one would care because they're like,")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Single(harness.Provider.Sources);
        Assert.Contains("no-one would care", harness.Provider.Sources[0]);
    }

    [Fact]
    public async Task LargeTimingGap_IsHardBoundary()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1500, "I was thinking,"),
            Subtitle(2, 4000, 4800, "and then I left.")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Equal(2, harness.Provider.Sources.Count);
    }

    [Fact]
    public async Task CompletedCue_IsHardBoundaryAndIsNotRetranslated()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1800, "one,"),
            Subtitle(2, 1850, 2500, "two"),
            Subtitle(3, 2550, 3300, "and three.")
        };
        subtitles[1].TranslatedLines = ["allerede oversat"];

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Equal(["one,", "and three."], harness.Provider.Sources);
        Assert.Equal(["allerede oversat"], subtitles[1].TranslatedLines);
    }

    [Fact]
    public async Task DuplicateRenderingLayers_AreTranslatedOnceAndShareTarget()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1800, "Hello."),
            Subtitle(2, 1000, 1800, "Hello."),
            Subtitle(3, 1900, 2600, "Next.")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.Equal(2, harness.Provider.Sources.Count);
        Assert.Equal(subtitles[0].TranslatedLines, subtitles[1].TranslatedLines);
    }

    [Fact]
    public async Task TranslationUnit_DoesNotPassLegacyOrPairedContextToProvider()
    {
        var harness = CreateHarness(source => $"tr:{source}");
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, 1000, 1800, "Where'd you get this?"),
            Subtitle(2, 1850, 2500, "English!")
        };

        await harness.Service.TranslateSubtitles(
            subtitles, Request(), false, false, CancellationToken.None);

        Assert.All(harness.Provider.ContextCalls, call =>
        {
            Assert.Null(call.Before);
            Assert.Null(call.After);
            Assert.Null(call.Pairs);
        });
    }

    [Fact]
    public async Task PreserveLineBreaks_DoesNotCauseAdditionalModelCalls()
    {
        var harness = CreateHarness(_ => "Hej der ven");
        var subtitle = Subtitle(1, 1000, 1800, "hello there", "friend");

        await harness.Service.TranslateSubtitles(
            [subtitle], Request(), false, true, CancellationToken.None);

        Assert.Single(harness.Provider.Sources);
        Assert.Equal(2, subtitle.TranslatedLines.Count);
        Assert.Equal("Hej der ven", string.Join(" ", subtitle.TranslatedLines));
    }

    [Fact]
    public void ResegmentTranslation_ReturnsExactSegmentCountWithoutLosingText()
    {
        const string target = "For seks måneder siden begyndte jeg at lave en film om Bonnie Blue, som allerede var kendt på sociale medier.";
        var source = new[]
        {
            "Six months ago, I began to make a film about Bonnie Blue,",
            "who was already well known",
            "across social media."
        };

        var result = SentenceAwareTranslationUnitService.ResegmentTranslation(target, source);

        Assert.Equal(3, result.Count);
        Assert.All(result, segment => Assert.False(string.IsNullOrWhiteSpace(segment)));
        Assert.Equal(Normalize(target), Normalize(string.Join(" ", result)));
    }

    private static Harness CreateHarness(Func<string, string> translate)
    {
        var provider = new RecordingProvider(translate);
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

        var translator = new SubtitleTranslationService(
            [new TranslationServiceEntry("test", provider, null)],
            NullLogger.Instance,
            progress.Object);

        return new Harness
        {
            Provider = provider,
            Service = new SentenceAwareTranslationUnitService(
                translator,
                progress.Object,
                NullLogger.Instance)
        };
    }

    private static TranslationRequest Request() => new()
    {
        Title = "unit-test",
        SourceLanguage = "en",
        TargetLanguage = "da",
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

    private static string Normalize(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class Harness
    {
        public required RecordingProvider Provider { get; init; }
        public required SentenceAwareTranslationUnitService Service { get; init; }
    }

    private sealed class RecordingProvider : ITranslationService, IContextualTranslationService
    {
        private readonly Func<string, string> _translate;

        public RecordingProvider(Func<string, string> translate)
        {
            _translate = translate;
        }

        public string? ModelName => "test";
        public List<string> Sources { get; } = [];
        public List<ContextCall> ContextCalls { get; } = [];

        public Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken)
        {
            Sources.Add(text);
            return Task.FromResult(_translate(text));
        }

        public Task<string> TranslateWithContextAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            IReadOnlyList<TranslationContextPair>? contextPairsBefore,
            CancellationToken cancellationToken)
        {
            Sources.Add(text);
            ContextCalls.Add(new ContextCall(
                contextLinesBefore?.ToList(),
                contextLinesAfter?.ToList(),
                contextPairsBefore?.ToList()));
            return Task.FromResult(_translate(text));
        }

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

    private sealed record ContextCall(
        List<string>? Before,
        List<string>? After,
        List<TranslationContextPair>? Pairs);
}
