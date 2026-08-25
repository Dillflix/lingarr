using Lingarr.Contracts.Models;

namespace Lingarr.Contracts.Translation;

/// <summary>
/// Optional capability for translation providers that can consume previously completed
/// source/target subtitle pairs as context for the current translation.
/// </summary>
public interface IContextualTranslationService
{
    Task<string> TranslateWithContextAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        List<string>? contextLinesBefore,
        List<string>? contextLinesAfter,
        IReadOnlyList<TranslationContextPair>? contextPairsBefore,
        CancellationToken cancellationToken);
}
