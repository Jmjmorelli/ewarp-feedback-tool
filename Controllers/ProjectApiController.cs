using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Controllers;

[Route("api/v1")]
public sealed class ProjectApiController(ProjectReadRepository projects) : ApiControllerBase
{
    [HttpGet("me")]
    public object Me() => new { id = "usr_system", name = "Program office", role = "Admin" };

    [HttpGet("projects")]
    public async Task<object> List(CancellationToken ct) { var items = await projects.ListProjectsAsync(ct); return new { items, total = items.Count }; }

    [HttpGet("projects/{projectId}")]
    public async Task<IActionResult> Get(string projectId, CancellationToken ct) => (await projects.GetProjectAsync(projectId, ct)) is { } project ? Ok(project) : NotFoundError($"Project {projectId} not found");

    [HttpGet("projects/{projectId}/summary")]
    public async Task<IActionResult> Summary(string projectId, DateOnly? from, DateOnly? to, string? group, CancellationToken ct)
    {
        if (!Valid(group, from, to)) return BadRequestError("Invalid filters.");
        return (await projects.SummaryAsync(projectId, from, to, group, ct)) is { } summary ? Ok(summary) : NotFoundError($"Project {projectId} not found");
    }

    [HttpGet("projects/{projectId}/analytics/coverage")]
    public async Task<IActionResult> Coverage(string projectId, CancellationToken ct) => (await projects.CoverageAsync(projectId, ct)) is { } coverage ? Ok(coverage) : NotFoundError($"Project {projectId} not found");

    private static bool Valid(string? group, DateOnly? from, DateOnly? to) => (group is null or "pm" or "sponsor" or "other") && (!from.HasValue || !to.HasValue || from <= to);
}
