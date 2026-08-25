using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Core.Entities;
using Lingarr.Core.Enum;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class ContextualTranslationTests
{
    [Fact]
    public async Task TranslateSubtitles_PassesCompletedPairsInChronologicalOrder()
    {
        var provider = new ContextualProvider();
        var service = CreateService(provider);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "one"),
            Subtitle(2, "two"),
            Subtitle(3, "three")
        };

        await service.TranslateSubtitles(
            subtitles,
            Request(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 2,
            contextAfter: 0,
            CancellationToken.None);

        Assert.Equal(3, provider.SeenPairs.Count);
        Assert.Null(provider.SeenPairs[0]);

        Assert.Collection(provider.SeenPairs[1]!,
            pair => Assert.Equal(new TranslationContextPair("one", "tr:one"), pair));

        Assert.Collection(provider.SeenPairs[2]!,
            pair => Assert.Equal(new TranslationContextPair("one", "tr:one"), pair),
            pair => Assert.Equal(new TranslationContextPair("two", "tr:two"), pair));
    }

    [Fact]
    public async Task TranslateSubtitles_PairedContextSkipsDuplicateRenderingLayers()
    {
        var provider = new ContextualProvider();
        var service = CreateService(provider);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "hello"),
            Subtitle(2, "hello"),
            Subtitle(3, "next")
        };

        await service.TranslateSubtitles(
            subtitles,
            Request(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 2,
            contextAfter: 0,
            CancellationToken.None);

        // Identical stacked subtitle layers use the same default timestamps in this fixture,
        // matching the cache/deduplication identity used by the translation service.
        Assert.Equal(2, provider.SeenPairs.Count);
        var nextContext = provider.SeenPairs[1];
        Assert.NotNull(nextContext);
        Assert.Single(nextContext!);
        Assert.Equal(new TranslationContextPair("hello", "tr:hello"), nextContext![0]);
    }

    [Fact]
    public async Task TranslateSubtitles_NonContextualProviderKeepsLegacySourceContext()
    {
        var provider = new LegacyProvider();
        var service = CreateService(provider);
        var subtitles = new List<SubtitleItem>
        {
            Subtitle(1, "one"),
            Subtitle(2, "two")
        };

        await service.TranslateSubtitles(
            subtitles,
            Request(),
            stripSubtitleFormatting: false,
            preserveLineBreaks: false,
            contextBefore: 1,
            contextAfter: 0,
            CancellationToken.None);

        Assert.Equal(2, provider.SeenContext.Count);
        Assert.Null(provider.SeenContext[0]);
        Assert.Equal(["one"], provider.SeenContext[1]);
    }

    private static SubtitleTranslationService CreateService(ITranslationService provider)
    {
        var progress = new Mock<IProgressService>();
        progress.Setup(p => p.Emit(It.IsAny<TranslationRequest>(), It.IsAny<int>()))
            .Returns(Task.CompletedTask);
        progress.Setup(p => p.EmitLine(
                It.IsAny<TranslationRequest>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<LanguagePair?>()))
            .Returns(Task.CompletedTask);
        progress.Setup(p => p.EmitLines(It.IsAny<TranslationRequest>(), It.IsAny<List<TranslatedLineData>>()))
            .Returns(Task.CompletedTask);

        return new SubtitleTranslationService(
            [new TranslationServiceEntry("test", provider, null)],
            NullLogger.Instance,
            progress.Object);
    }

    private static TranslationRequest Request() => new()
    {
        Title = "context-test",
        SourceLanguage = "en",
        TargetLanguage = "da",
        MediaType = MediaType.Movie,
        Status = TranslationStatus.InProgress
    };

    private static SubtitleItem Subtitle(int position, string text) => new()
    {
        Position = position,
        Lines = [text],
        PlaintextLines = [text]
    };

    private abstract class ProviderBase : ITranslationService
    {
        public string? ModelName => "test";

        public abstract Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken);

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

    private sealed class ContextualProvider : ProviderBase, IContextualTranslationService
    {
        public List<IReadOnlyList<TranslationContextPair>?> SeenPairs { get; } = [];

        public override Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken) => Task.FromResult($"tr:{text}");

        public Task<string> TranslateWithContextAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            IReadOnlyList<TranslationContextPair>? contextPairsBefore,
            CancellationToken cancellationToken)
        {
            SeenPairs.Add(contextPairsBefore?.ToList());
            return Task.FromResult($"tr:{text}");
        }
    }

    private sealed class LegacyProvider : ProviderBase
    {
        public List<List<string>?> SeenContext { get; } = [];

        public override Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken)
        {
            SeenContext.Add(contextLinesBefore?.ToList());
            return Task.FromResult($"tr:{text}");
        }
    }
}
