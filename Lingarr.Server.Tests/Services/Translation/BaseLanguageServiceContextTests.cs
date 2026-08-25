using Lingarr.Contracts.Models;
using Lingarr.Contracts.Translation;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Services;
using Lingarr.Server.Services.Translation.Base;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Lingarr.Server.Tests.Services.Translation;

public class BaseLanguageServiceContextTests
{
    [Fact]
    public async Task ContextPairsBefore_RendersNativeSourceTargetPairsWithLanguageNames()
    {
        var service = new PromptHarness();
        var contextual = (IContextualTranslationService)service;

        var rendered = await contextual.TranslateWithContextAsync(
            "I couldn't.",
            "en",
            "da",
            contextLinesBefore: null,
            contextLinesAfter: null,
            contextPairsBefore:
            [
                new TranslationContextPair("Where have you been?", "Hvor har du været?"),
                new TranslationContextPair("I was looking everywhere for you.", "Jeg ledte efter dig overalt.")
            ],
            CancellationToken.None);

        Assert.Equal(
            "Translate this from English to Danish:\n" +
            "English: Where have you been?\n" +
            "Danish: Hvor har du været?\n" +
            "English: I was looking everywhere for you.\n" +
            "Danish: Jeg ledte efter dig overalt.\n" +
            "English: I couldn't.\n" +
            "Danish:",
            rendered);
    }

    [Fact]
    public async Task ContextPairsBefore_IsEmptyWhenNoCompletedHistoryExists()
    {
        var service = new PromptHarness();
        var contextual = (IContextualTranslationService)service;

        var rendered = await contextual.TranslateWithContextAsync(
            "Hello.",
            "en",
            "da",
            contextLinesBefore: null,
            contextLinesAfter: null,
            contextPairsBefore: null,
            CancellationToken.None);

        Assert.Equal(
            "Translate this from English to Danish:\nEnglish: Hello.\nDanish:",
            rendered);
    }

    private sealed class PromptHarness : BaseLanguageService
    {
        public PromptHarness()
            : base(
                new Mock<ISettingService>().Object,
                NullLogger.Instance,
                new LanguageCodeService())
        {
        }

        public override string? ModelName => "test";

        public override Task<string> TranslateAsync(
            string text,
            string sourceLanguage,
            string targetLanguage,
            List<string>? contextLinesBefore,
            List<string>? contextLinesAfter,
            CancellationToken cancellationToken)
        {
            SetLanguageReplacements(sourceLanguage, targetLanguage, "false");
            _prompt = string.Empty;
            _userPrompt =
                "Translate this from {sourceLanguage} to {targetLanguage}:\n" +
                "{contextPairsBefore}{sourceLanguage}: {lineToTranslate}\n" +
                "{targetLanguage}:";

            var replacements = GetReplacements(
                ModelName!, text, contextLinesBefore, contextLinesAfter);
            return Task.FromResult(replacements["userMessage"]);
        }
    }
}
