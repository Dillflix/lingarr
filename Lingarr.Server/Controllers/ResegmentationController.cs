using System.Threading;
using System.Threading.Tasks;
using Lingarr.Server.Attributes;
using Lingarr.Server.Interfaces.Services.Translation;
using Lingarr.Server.Models.Translation;
using Microsoft.AspNetCore.Mvc;

namespace Lingarr.Server.Controllers;

[ApiController]
[LingarrAuthorize]
[Route("api/resegmentation")]
public sealed class ResegmentationController : ControllerBase
{
    private readonly ITranslationUnitResegmentationService _resegmentationService;

    public ResegmentationController(ITranslationUnitResegmentationService resegmentationService)
    {
        _resegmentationService = resegmentationService;
    }

    /// <summary>
    /// Evaluates deterministic and dedicated-model subtitle resegmentation for one translated unit.
    /// Optional model/validator overrides make it possible to compare hosted models and prompts
    /// without changing persistent settings. If reference segments are supplied, objective boundary
    /// and exact-segment metrics are returned for both candidates.
    /// </summary>
    [HttpPost("evaluate")]
    public async Task<ActionResult<ResegmentationEvaluationResult>> Evaluate(
        [FromBody] ResegmentationEvaluationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SourceSegments.Count == 0)
        {
            return BadRequest("At least one source segment is required.");
        }

        if (string.IsNullOrWhiteSpace(request.TranslatedUnit))
        {
            return BadRequest("A translated unit is required.");
        }

        if (request.ReferenceSegments is not null &&
            request.ReferenceSegments.Count != request.SourceSegments.Count)
        {
            return BadRequest("Reference segment count must match source segment count.");
        }

        var result = await _resegmentationService.EvaluateAsync(request, cancellationToken);
        return Ok(result);
    }
}
