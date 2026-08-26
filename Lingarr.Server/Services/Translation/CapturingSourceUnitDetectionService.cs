using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lingarr.Core.Configuration;
using Lingarr.Core.Data;
using Lingarr.Core.Entities;
using Lingarr.Server.Interfaces.Services;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lingarr.Server.Services.Translation;

/// <summary>
/// Production-facing source detector decorator. It delegates the actual boundary decision to the
/// normal detector, then records the exact candidate cue window and decision for later benchmark
/// replay. Capture uses its own DI scope/DbContext so it cannot persist or detach unrelated
/// TranslationJob state. Benchmark calls set an explicit Mode and therefore bypass capture.
/// </summary>
public sealed class CapturingSourceUnitDetectionService : ISourceUnitDetectionService
{
    private readonly SourceUnitDetectionService _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISettingService _settings;
    private readonly ILogger<CapturingSourceUnitDetectionService> _logger;

    public CapturingSourceUnitDetectionService(
        SourceUnitDetectionService inner,
        IServiceScopeFactory scopeFactory,
        ISettingService settings,
        ILogger<CapturingSourceUnitDetectionService> logger)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<SourceUnitDetectionResult> DetectAsync(
        SourceUnitDetectionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _inner.DetectAsync(request, cancellationToken);

        // SentenceAwareTranslationUnitService deliberately leaves Mode null so persisted production
        // settings are used. Benchmark/evaluation calls supply an explicit mode and must not pollute
        // the live corpus with replayed or synthetic cases.
        if (request.Mode is null && request.Cues.Count > 1)
        {
            try
            {
                await CaptureAsync(request, result, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to capture source-unit benchmark candidate window beginning at cue {Position}; detection will continue.",
                    request.Cues[0].Position);
            }
        }

        return result;
    }

    private async Task CaptureAsync(
        SourceUnitDetectionRequest request,
        SourceUnitDetectionResult result,
        CancellationToken cancellationToken)
    {
        var enabled = await _settings.GetSetting(SettingKeys.Translation.SourceUnitDetection.BenchmarkCaptureEnabled);
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cuesJson = JsonSerializer.Serialize(request.Cues);
        var fingerprint = Fingerprint(request.SourceLanguage, cuesJson);
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<LingarrDbContext>();

        var existing = await dbContext.SourceUnitBenchmarkSamples
            .FirstOrDefaultAsync(sample => sample.Fingerprint == fingerprint, cancellationToken);
        if (existing is not null)
        {
            ApplyDecision(existing, request, result);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var maxSamples = ParsePositiveInt(
            await _settings.GetSetting(SettingKeys.Translation.SourceUnitDetection.BenchmarkMaxSamples),
            5000);
        if (await dbContext.SourceUnitBenchmarkSamples.CountAsync(cancellationToken) >= maxSamples)
        {
            return;
        }

        var entity = new SourceUnitBenchmarkSample
        {
            Fingerprint = fingerprint,
            SourceLanguage = request.SourceLanguage,
            CandidateCuesJson = cuesJson,
            CandidateCount = request.Cues.Count,
            HeuristicUnitLength = result.Heuristic.UnitLength,
            StartPosition = request.Cues[0].Position,
            EndPosition = request.Cues[^1].Position
        };
        ApplyDecision(entity, request, result);
        dbContext.SourceUnitBenchmarkSamples.Add(entity);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // Duplicate races are harmless. This is a dedicated capture DbContext, so abandoning it
            // cannot alter the translation job's state even if another worker inserted first.
            _logger.LogDebug(ex, "Source-unit benchmark capture insert raced with another worker; sample ignored.");
        }
    }

    private static void ApplyDecision(
        SourceUnitBenchmarkSample entity,
        SourceUnitDetectionRequest request,
        SourceUnitDetectionResult result)
    {
        entity.HeuristicUnitLength = result.Heuristic.UnitLength;
        entity.ProductionMode = result.Mode;
        entity.ProductionModelUnitLength = result.Model?.UnitLength;
        entity.ProductionModelIsValid = result.Model?.IsValid;
        entity.ProductionModelError = result.Model?.Error;
        entity.ProductionModelLatencyMs = result.Model?.LatencyMs;
        entity.ProductionValidatorWinner = result.Validator?.Winner;
        entity.ProductionValidatorModel = result.Validator?.Model;
        entity.ProductionValidatorModelScore = result.Validator?.ModelScore;
        entity.ProductionValidatorHeuristicScore = result.Validator?.HeuristicScore;
        entity.ProductionValidatorLatencyMs = result.Validator?.LatencyMs;
        entity.ProductionSelectedUnitLength = result.UnitLength;
        entity.ProductionSelectedMethod = result.SelectedMethod;
        entity.StartPosition ??= request.Cues[0].Position;
        entity.EndPosition ??= request.Cues[^1].Position;
    }

    private static string Fingerprint(string sourceLanguage, string cuesJson)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceLanguage + "\n" + cuesJson));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
}
