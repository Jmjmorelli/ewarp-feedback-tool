using FeedbackTool.Data;
using FeedbackTool.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId}/responses")]
public sealed class ResponsesController(IResponseRepository responses) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string projectId, [FromQuery] ResponseListQuery query, CancellationToken cancellationToken)
    {
        var error = Validate(query); if (error is not null) return BadRequest(Error(error));
        var result = await responses.ListAsync(projectId, query, cancellationToken);
        return result is null ? NotFound(NotFoundError("Project was not found.")) : Ok(result);
    }

    [HttpGet("{responseId}")]
    public async Task<IActionResult> Get(string projectId, string responseId, CancellationToken cancellationToken)
    {
        var result = await responses.GetAsync(projectId, responseId, cancellationToken);
        return result is null ? NotFound(NotFoundError("Project or response was not found.")) : Ok(result);
    }

    private static string? Validate(ResponseListQuery q)
    {
        if (q.Limit is < 1 or > 500) return "limit must be between 1 and 500.";
        if (q.Offset < 0) return "offset must be zero or greater.";
        if (q.Sort is not ("newest" or "oldest")) return "sort must be newest or oldest.";
        if (q.Status is not null && q.Status is not ("analyzed" or "pending")) return "status must be analyzed or pending.";
        if (q.Group is not null && q.Group is not ("pm" or "sponsor" or "other")) return "group must be pm, sponsor, or other.";
        if (q.Q?.Length > 100) return "q must be at most 100 characters.";
        if (q.From is not null && q.To is not null && q.From > q.To) return "from must not be after to.";
        return null;
    }
    private static object Error(string message) => new { error = new { code = "bad_request", message } };
    private static object NotFoundError(string message) => new { error = new { code = "not_found", message } };
}
