using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Models.Translation;
using Microsoft.EntityFrameworkCore;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Reconstructs sentence-aware multi-cue units from completed Lingarr translation history.
/// Concatenating each unit's final target segments recovers the complete translated text while
/// requiring no Danish reference annotation. This lets a benchmark corpus be bootstrapped from
/// ordinary translation jobs with no special capture step.
/// </summary>
public sealed class ResegmentationBenchmarkHistoryHarvester
{
    private const int MaxUnitCues = 4;
    private const int MaxUnitChars = 500;

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

    private readonly LingarrDbContext _dbContext;
    private readonly ISettingService _settings;
    private readonly ILogger<ResegmentationBenchmarkHistoryHarvester> _logger;

    public ResegmentationBenchmarkHistoryHarvester(
        LingarrDbContext dbContext,
        ISettingService settings,
        ILogger<ResegmentationBenchmarkHistoryHarvester> logger)
    {
        _dbContext = dbContext;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ResegmentationBenchmarkHarvestResult> HarvestAsync(
        int maxRequests,
        CancellationToken cancellationToken)
    {
        maxRequests = Math.Clamp(maxRequests, 1, 1000);
        var requests = await _dbContext.TranslationRequests
            .AsNoTracking()
            .Where(request => request.CompletedAt != null)
            .OrderByDescending(request => request.CompletedAt)
            .ThenByDescending(request => request.Id)
            .Take(maxRequests)
            .Select(request => new
            {
                request.Id,
                request.SourceLanguage,
                request.TargetLanguage
            })
            .ToListAsync(cancellationToken);

        var existingFingerprints = (await _dbContext.ResegmentationBenchmarkSamples
                .AsNoTracking()
                .Select(sample => sample.Fingerprint)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        var found = 0;
        var captured = 0;

        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var rawLines = await _dbContext.TranslationRequestLines
                .AsNoTracking()
                .Where(line => line.TranslationRequestId == request.Id)
                .OrderBy(line => line.Position)
                .ThenBy(line => line.Id)
                .Select(line => new HistoryLine(line.Id, line.Position, line.Source, line.Target))
                .ToListAsync(cancellationToken);

            var lines = rawLines
                .GroupBy(line => line.Position)
                .Select(group => group.OrderByDescending(line => line.Id).First())
                .Where(line => !string.IsNullOrWhiteSpace(line.Source) && !string.IsNullOrWhiteSpace(line.Target))
                .OrderBy(line => line.Position)
                .ToArray();

            for (var index = 0; index < lines.Length;)
            {
                var unit = BuildUnit(lines, index);
                if (unit.Count <= 1)
                {
                    index++;
                    continue;
                }

                found++;
                var sourceSegments = unit.Select(line => line.Source.Trim()).ToArray();
                var translatedUnit = string.Join(" ", unit.Select(line => line.Target.Trim())).Trim();
                if (string.IsNullOrWhiteSpace(translatedUnit))
                {
                    index += unit.Count;
                    continue;
                }

                var sourceJson = JsonSerializer.Serialize(sourceSegments);
                var fingerprint = Fingerprint(
                    request.SourceLanguage,
                    request.TargetLanguage,
                    sourceJson,
                    translatedUnit);

                if (existingFingerprints.Add(fingerprint))
                {
                    _dbContext.ResegmentationBenchmarkSamples.Add(new ResegmentationBenchmarkSample
                    {
                        Fingerprint = fingerprint,
                        SourceLanguage = request.SourceLanguage,
                        TargetLanguage = request.TargetLanguage,
                        SourceSegmentsJson = sourceJson,
                        TranslatedUnit = translatedUnit,
                        SegmentCount = sourceSegments.Length,
                        TranslationRequestId = request.Id,
                        StartPosition = unit[0].Position,
                        EndPosition = unit[^1].Position
                    });
                    captured++;
                }

                index += unit.Count;
            }
        }

        if (captured > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await TrimCorpusAsync(cancellationToken);
        }

        var total = await _dbContext.ResegmentationBenchmarkSamples.CountAsync(cancellationToken);
        _logger.LogInformation(
            "Reference-free benchmark harvest scanned {Requests} request(s), found {Units} multi-cue unit(s), and captured {Captured} new sample(s). Corpus now contains {Total} sample(s).",
            requests.Count,
            found,
            captured,
            total);

        return new ResegmentationBenchmarkHarvestResult
        {
            RequestsScanned = requests.Count,
            MultiCueUnitsFound = found,
            NewSamplesCaptured = captured,
            TotalCorpusSamples = total
        };
    }

    private async Task TrimCorpusAsync(CancellationToken cancellationToken)
    {
        var configured = await _settings.GetSetting(
            SettingKeys.Translation.Resegmentation.BenchmarkMaxSamples);
        var maxSamples = int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, 10, 10000)
            : 500;

        var count = await _dbContext.ResegmentationBenchmarkSamples.CountAsync(cancellationToken);
        var excess = count - maxSamples;
        if (excess <= 0)
        {
            return;
        }

        var oldest = await _dbContext.ResegmentationBenchmarkSamples
            .OrderBy(sample => sample.CreatedAt)
            .ThenBy(sample => sample.Id)
            .Take(excess)
            .ToListAsync(cancellationToken);
        _dbContext.ResegmentationBenchmarkSamples.RemoveRange(oldest);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static List<HistoryLine> BuildUnit(IReadOnlyList<HistoryLine> lines, int startIndex)
    {
        var unit = new List<HistoryLine> { lines[startIndex] };
        var sourceChars = lines[startIndex].Source.Length;

        while (unit.Count < MaxUnitCues && startIndex + unit.Count < lines.Count)
        {
            var current = unit[^1];
            var next = lines[startIndex + unit.Count];
            if (next.Position != current.Position + 1)
            {
                break;
            }
            if (sourceChars + 1 + next.Source.Length > MaxUnitChars)
            {
                break;
            }
            if (!ShouldJoin(current.Source, next.Source))
            {
                break;
            }

            unit.Add(next);
            sourceChars += 1 + next.Source.Length;
        }

        return unit;
    }

    private static bool ShouldJoin(string currentValue, string nextValue)
    {
        var current = currentValue.Trim();
        var next = nextValue.Trim();
        if (current.Length == 0 || next.Length == 0)
        {
            return false;
        }
        if (StartsDialogueTurn(next) && !EndsWithContinuationPunctuation(current))
        {
            return false;
        }
        if (HasHardTerminalPunctuation(current))
        {
            return false;
        }
        if (EndsWithContinuationPunctuation(current))
        {
            return true;
        }
        if (EndsWithEllipsis(current))
        {
            return StartsWithLowercase(next) || StartsWithContinuationWord(next);
        }
        if (StartsWithLowercase(next) || StartsWithContinuationWord(next))
        {
            return true;
        }
        return EndsWithDanglingWord(current);
    }

    private static bool HasHardTerminalPunctuation(string text)
    {
        if (EndsWithEllipsis(text)) return false;
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
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ||
               trimmed.StartsWith("– ", StringComparison.Ordinal) ||
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
        while (end >= 0 && !char.IsLetter(trimmed[end])) end--;
        if (end < 0) return null;

        var start = end;
        while (start >= 0 && char.IsLetter(trimmed[start])) start--;
        return trimmed[(start + 1)..(end + 1)];
    }

    private static string TrimTrailingClosers(string text)
    {
        var end = text.Length;
        while (end > 0 && text[end - 1] is '"' or '\'' or '”' or '’' or ')' or ']' or '}') end--;
        return text[..end];
    }

    private static string Fingerprint(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed record HistoryLine(int Id, int Position, string Source, string Target);
}
