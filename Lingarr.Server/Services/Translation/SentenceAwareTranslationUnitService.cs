using Lingarr.Contracts.Models;
using Lingarr.Core.Entities;
using Lingarr.Server.Extensions;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.FileSystem;
using Lingarr.Server.Services.Subtitle;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Groups subtitle cues into complete linguistic translation units, translates each unit once,
/// and deterministically resegments the translated text back onto the original cue timings.
/// A one-cue unit is the normal trivial case; multi-cue units are formed only when adjacent
/// subtitle text strongly indicates that a sentence or utterance continues across cue boundaries.
/// </summary>
public sealed class SentenceAwareTranslationUnitService
{
    private const int MaxLineLength = 42;
    private const int MaxTranslationUnitCues = 4;
    private const int MaxTranslationUnitChars = 500;
    private const int MaxTranslationUnitGapMs = 2000;

    private static readonly HashSet<string> ContinuationWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "but", "or", "so", "because", "who", "whom", "whose", "which", "that",
        "when", "where", "while", "after", "before", "if", "as", "than", "then", "to",
        "of", "for", "with", "from", "in", "on", "at", "by", "into", "about", "over", "under"
    };

    private static readonly HashSet<string> DanglingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "but", "or", "because", "that", "who", "whom", "whose",
        "which", "if", "when", "while", "as", "than", "to", "of", "for", "with", "from",
        "in", "on", "at", "by", "into", "about", "over", "under"
    };

    private readonly SubtitleTranslationService _translator;
    private readonly IProgressService _progressService;
    private readonly ILogger _logger;
    private int _lastProgression = -1;

    public SentenceAwareTranslationUnitService(
        SubtitleTranslationService translator,
        IProgressService progressService,
        ILogger logger)
    {
        _translator = translator;
        _progressService = progressService;
        _logger = logger;
    }

    /// <summary>
    /// Translates all not-yet-completed subtitles as sentence-aware translation units.
    /// Surrounding-context fields are intentionally not used: every source token sent to the
    /// translation model belongs to the unit that should actually be translated.
    /// </summary>
    public async Task<List<SubtitleItem>> TranslateSubtitles(
        List<SubtitleItem> subtitles,
        TranslationRequest translationRequest,
        bool stripSubtitleFormatting,
        bool preserveLineBreaks,
        CancellationToken cancellationToken)
    {
        var logicalCues = BuildLogicalCues(subtitles, stripSubtitleFormatting);
        var totalSubtitles = subtitles.Count;
        var iteration = 0;

        for (var cueIndex = 0; cueIndex < logicalCues.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cue = logicalCues[cueIndex];

            if (TryGetExistingTranslation(cue, out var existingTranslation))
            {
                PropagateExistingTranslation(cue, existingTranslation!);
                iteration += cue.Items.Count;
                await EmitProgress(translationRequest, iteration, totalSubtitles);
                cueIndex++;
                continue;
            }

            var unit = BuildTranslationUnit(logicalCues, cueIndex);
            var sourceSegments = unit.Select(item => item.SourceText).ToList();
            var unitSource = string.Join(" ", sourceSegments.Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

            if (string.IsNullOrWhiteSpace(unitSource))
            {
                foreach (var emptyCue in unit)
                {
                    foreach (var item in emptyCue.Items)
                    {
                        item.TranslatedLines = GetContentLines(item, stripSubtitleFormatting).ToList();
                        iteration++;
                        await EmitProgress(translationRequest, iteration, totalSubtitles);
                    }
                }

                cueIndex += unit.Count;
                continue;
            }

            _logger.LogDebug(
                "Translating sentence-aware unit {StartPosition}-{EndPosition} ({CueCount} cue(s)): {Source}",
                unit[0].Representative.Position,
                unit[^1].Representative.Position,
                unit.Count,
                unitSource);

            var result = await _translator.TranslateSubtitleLine(new TranslateAbleSubtitleLine
            {
                SubtitleLine = unitSource,
                SourceLanguage = translationRequest.SourceLanguage,
                TargetLanguage = translationRequest.TargetLanguage,
                ContextLinesBefore = null,
                ContextLinesAfter = null,
                ContextPairsBefore = null
            }, cancellationToken);

            var translatedUnit = stripSubtitleFormatting
                ? SubtitleFormatterService.RemoveMarkup(result.Translation)
                : result.Translation;
            var targetSegments = ResegmentTranslation(translatedUnit, sourceSegments);

            for (var unitIndex = 0; unitIndex < unit.Count; unitIndex++)
            {
                var logicalCue = unit[unitIndex];
                var targetSegment = targetSegments[unitIndex];

                foreach (var item in logicalCue.Items)
                {
                    item.TranslatedLines = FormatCueTarget(
                        targetSegment,
                        item,
                        stripSubtitleFormatting,
                        preserveLineBreaks);

                    await _progressService.EmitLine(
                        translationRequest,
                        item.Position,
                        string.Join(" ", GetContentLines(item, stripSubtitleFormatting)),
                        string.Join(" ", item.TranslatedLines),
                        result.Service,
                        result.Pair);

                    iteration++;
                    await EmitProgress(translationRequest, iteration, totalSubtitles);
                }
            }

            cueIndex += unit.Count;
        }

        _lastProgression = -1;
        return subtitles;
    }

    /// <summary>
    /// Splits a translated unit into exactly the same number of subtitle segments as the source
    /// unit. Boundaries are selected near source-length-proportional positions, preferring target
    /// punctuation compatible with the corresponding source boundary and otherwise whitespace.
    /// </summary>
    public static IReadOnlyList<string> ResegmentTranslation(
        string translatedUnit,
        IReadOnlyList<string> sourceSegments)
    {
        if (sourceSegments.Count == 0)
        {
            return [];
        }

        var text = translatedUnit.Trim();
        if (sourceSegments.Count == 1)
        {
            return [text];
        }

        if (text.Length == 0)
        {
            return Enumerable.Repeat(string.Empty, sourceSegments.Count).ToList();
        }

        var weights = sourceSegments.Select(SegmentWeight).ToArray();
        var totalWeight = weights.Sum();
        var boundaries = new List<int>(sourceSegments.Count - 1);
        var previousBoundary = 0;
        var cumulativeWeight = 0;

        for (var segmentIndex = 0; segmentIndex < sourceSegments.Count - 1; segmentIndex++)
        {
            cumulativeWeight += weights[segmentIndex];
            var desired = (int)Math.Round((double)text.Length * cumulativeWeight / totalWeight);
            var remainingSegments = sourceSegments.Count - segmentIndex - 1;
            var minBoundary = Math.Min(text.Length - remainingSegments, previousBoundary + 1);
            var maxBoundary = Math.Max(minBoundary, text.Length - remainingSegments);
            var preferredPunctuation = GetBoundaryPunctuation(sourceSegments[segmentIndex]);
            var boundary = FindBestBoundary(
                text,
                desired,
                minBoundary,
                maxBoundary,
                preferredPunctuation);

            boundaries.Add(boundary);
            previousBoundary = boundary;
        }

        var result = new List<string>(sourceSegments.Count);
        var start = 0;
        foreach (var boundary in boundaries)
        {
            result.Add(text[start..boundary].Trim());
            start = boundary;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
        }
        result.Add(text[start..].Trim());

        return result;
    }

    private static List<LogicalSubtitleCue> BuildLogicalCues(
        IReadOnlyList<SubtitleItem> subtitles,
        bool stripSubtitleFormatting)
    {
        var cues = new List<LogicalSubtitleCue>();
        var byIdentity = new Dictionary<string, LogicalSubtitleCue>();

        foreach (var subtitle in subtitles)
        {
            var sourceText = string.Join(" ", GetContentLines(subtitle, stripSubtitleFormatting)).Trim();
            var analysisText = string.Join(" ", subtitle.PlaintextLines).Trim();
            if (string.IsNullOrWhiteSpace(analysisText))
            {
                analysisText = sourceText;
            }

            // Plain text is deliberately used for duplicate-layer identity. ASS/SSA shadow/glow/main
            // rendering layers can differ in markup while representing the same timed spoken text.
            var identity = $"{subtitle.StartTime}|{subtitle.EndTime}|{analysisText}";
            if (byIdentity.TryGetValue(identity, out var existing))
            {
                existing.Items.Add(subtitle);
                continue;
            }

            var cue = new LogicalSubtitleCue(sourceText, analysisText, [subtitle]);
            cues.Add(cue);
            byIdentity[identity] = cue;
        }

        return cues;
    }

    private static List<LogicalSubtitleCue> BuildTranslationUnit(
        IReadOnlyList<LogicalSubtitleCue> cues,
        int startIndex)
    {
        var unit = new List<LogicalSubtitleCue> { cues[startIndex] };
        var sourceChars = cues[startIndex].SourceText.Length;

        while (unit.Count < MaxTranslationUnitCues && startIndex + unit.Count < cues.Count)
        {
            var current = unit[^1];
            var next = cues[startIndex + unit.Count];

            if (TryGetExistingTranslation(next, out _))
            {
                break;
            }

            if (sourceChars + 1 + next.SourceText.Length > MaxTranslationUnitChars)
            {
                break;
            }

            if (!ShouldJoin(current, next))
            {
                break;
            }

            unit.Add(next);
            sourceChars += 1 + next.SourceText.Length;
        }

        return unit;
    }

    private static bool ShouldJoin(LogicalSubtitleCue current, LogicalSubtitleCue next)
    {
        var gap = next.Representative.StartTime - current.Representative.EndTime;
        if (gap > MaxTranslationUnitGapMs)
        {
            return false;
        }

        var currentText = current.AnalysisText.Trim();
        var nextText = next.AnalysisText.Trim();
        if (currentText.Length == 0 || nextText.Length == 0)
        {
            return false;
        }

        if (StartsDialogueTurn(nextText) && !EndsWithContinuationPunctuation(currentText))
        {
            return false;
        }

        if (HasHardTerminalPunctuation(currentText))
        {
            return false;
        }

        if (EndsWithContinuationPunctuation(currentText))
        {
            return true;
        }

        if (EndsWithEllipsis(currentText))
        {
            return StartsWithLowercase(nextText) || StartsWithContinuationWord(nextText);
        }

        if (StartsWithLowercase(nextText) || StartsWithContinuationWord(nextText))
        {
            return true;
        }

        return EndsWithDanglingWord(currentText);
    }

    private static bool TryGetExistingTranslation(LogicalSubtitleCue cue, out string? translation)
    {
        var existing = cue.Items.FirstOrDefault(item => item.TranslatedLines.Count > 0);
        translation = existing is null ? null : string.Join(" ", existing.TranslatedLines);
        return existing is not null;
    }

    private static void PropagateExistingTranslation(LogicalSubtitleCue cue, string translation)
    {
        foreach (var item in cue.Items.Where(item => item.TranslatedLines.Count == 0))
        {
            item.TranslatedLines = [translation];
        }
    }

    private static IReadOnlyList<string> GetContentLines(SubtitleItem subtitle, bool stripSubtitleFormatting) =>
        stripSubtitleFormatting ? subtitle.PlaintextLines : subtitle.Lines;

    private static List<string> FormatCueTarget(
        string target,
        SubtitleItem subtitle,
        bool stripSubtitleFormatting,
        bool preserveLineBreaks)
    {
        var contentLines = GetContentLines(subtitle, stripSubtitleFormatting);

        if (preserveLineBreaks && contentLines.Count > 1)
        {
            return ResegmentTranslation(target, subtitle.PlaintextLines).ToList();
        }

        return contentLines.Count > 1 && stripSubtitleFormatting
            ? target.SplitIntoLines(MaxLineLength)
            : [target];
    }

    private static int SegmentWeight(string value)
    {
        var weight = value.Count(character => !char.IsWhiteSpace(character));
        return Math.Max(1, weight);
    }

    private static int FindBestBoundary(
        string text,
        int desired,
        int minBoundary,
        int maxBoundary,
        char? preferredPunctuation)
    {
        desired = Math.Clamp(desired, minBoundary, maxBoundary);
        var radius = Math.Max(24, Math.Min(80, text.Length / 5));
        var searchStart = Math.Max(minBoundary, desired - radius);
        var searchEnd = Math.Min(maxBoundary, desired + radius);
        var bestBoundary = -1;
        var bestScore = int.MaxValue;

        for (var index = searchStart; index <= searchEnd; index++)
        {
            if (index <= 0 || index >= text.Length || !char.IsWhiteSpace(text[index]))
            {
                continue;
            }

            var punctuation = PreviousNonWhitespaceCharacter(text, index);
            var score = Math.Abs(index - desired) * 2;

            if (punctuation is not null && IsBoundaryPunctuation(punctuation.Value))
            {
                score -= 14;
            }

            if (preferredPunctuation is not null && punctuation is not null)
            {
                score -= PunctuationCompatibilityBonus(preferredPunctuation.Value, punctuation.Value);
            }

            if (score < bestScore)
            {
                bestScore = score;
                bestBoundary = index;
            }
        }

        if (bestBoundary >= 0)
        {
            return bestBoundary;
        }

        for (var offset = 0; offset <= Math.Max(desired - minBoundary, maxBoundary - desired); offset++)
        {
            var left = desired - offset;
            if (left >= minBoundary && left < text.Length && char.IsWhiteSpace(text[left]))
            {
                return left;
            }

            var right = desired + offset;
            if (right <= maxBoundary && right < text.Length && char.IsWhiteSpace(text[right]))
            {
                return right;
            }
        }

        return desired;
    }

    private static char? PreviousNonWhitespaceCharacter(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }
        return null;
    }

    private static int PunctuationCompatibilityBonus(char source, char target)
    {
        if (source == target)
        {
            return 40;
        }

        if (source is ',' or ';' or ':' && target is ',' or ';' or ':')
        {
            return 28;
        }

        if (source is '.' or '?' or '!' && target is '.' or '?' or '!')
        {
            return 32;
        }

        if (source is '-' or '—' && target is '-' or '—')
        {
            return 28;
        }

        return 0;
    }

    private static bool IsBoundaryPunctuation(char value) => value is ',' or ';' or ':' or '.' or '?' or '!' or '-' or '—';

    private static char? GetBoundaryPunctuation(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        if (trimmed.Length == 0)
        {
            return null;
        }

        var last = trimmed[^1];
        return IsBoundaryPunctuation(last) ? last : null;
    }

    private static bool HasHardTerminalPunctuation(string text)
    {
        if (EndsWithEllipsis(text))
        {
            return false;
        }

        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith('.') || trimmed.EndsWith('?') || trimmed.EndsWith('!');
    }

    private static bool EndsWithEllipsis(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith("...", StringComparison.Ordinal) || trimmed.EndsWith('…');
    }

    private static bool EndsWithContinuationPunctuation(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        return trimmed.EndsWith(',') || trimmed.EndsWith(';') || trimmed.EndsWith(':') ||
               trimmed.EndsWith('—') || (trimmed.EndsWith('-') && !trimmed.EndsWith("--", StringComparison.Ordinal));
    }

    private static bool StartsWithLowercase(string text)
    {
        var firstLetter = text.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsLower(firstLetter);
    }

    private static bool StartsWithContinuationWord(string text)
    {
        var word = FirstWord(text);
        return word is not null && ContinuationWords.Contains(word);
    }

    private static bool EndsWithDanglingWord(string text)
    {
        var word = LastWord(text);
        return word is not null && DanglingWords.Contains(word);
    }

    private static bool StartsDialogueTurn(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("– ", StringComparison.Ordinal) ||
               trimmed.StartsWith("— ", StringComparison.Ordinal);
    }

    private static string? FirstWord(string text)
    {
        var letters = text.SkipWhile(character => !char.IsLetter(character)).TakeWhile(char.IsLetter).ToArray();
        return letters.Length == 0 ? null : new string(letters);
    }

    private static string? LastWord(string text)
    {
        var trimmed = TrimTrailingClosers(text.TrimEnd());
        var end = trimmed.Length - 1;
        while (end >= 0 && !char.IsLetter(trimmed[end]))
        {
            end--;
        }
        if (end < 0)
        {
            return null;
        }

        var start = end;
        while (start >= 0 && char.IsLetter(trimmed[start]))
        {
            start--;
        }

        return trimmed[(start + 1)..(end + 1)];
    }

    private static string TrimTrailingClosers(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] is '"' or '\'' or '”' or '’' or ')' or ']' or '}')
        {
            end--;
        }
        return text[..end];
    }

    private async Task EmitProgress(TranslationRequest request, int iteration, int total)
    {
        if (total <= 0)
        {
            return;
        }

        var progress = (int)Math.Round((double)iteration * 100 / total);
        if (progress == _lastProgression)
        {
            return;
        }

        _logger.LogInformation("Progress: {Progress}% (Subtitle {Iteration} of {Total})", progress, iteration, total);
        await _progressService.Emit(request, progress);
        _lastProgression = progress;
    }

    private sealed class LogicalSubtitleCue(
        string sourceText,
        string analysisText,
        List<SubtitleItem> items)
    {
        public string SourceText { get; } = sourceText;
        public string AnalysisText { get; } = analysisText;
        public List<SubtitleItem> Items { get; } = items;
        public SubtitleItem Representative => Items[0];
    }
}
