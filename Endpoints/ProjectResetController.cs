using FeedbackTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FeedbackTool.Endpoints;

[Route("api/v1/projects/{projectId}/reset")]
public sealed class ProjectResetController(ProjectResetRepository reset, ProjectReadRepository projects) : ApiControllerBase
{
    // Body: { confirm: "<the project's exact name>" }, so a stray call can't wipe a project.
    // 200 { responses, features }: how many were deleted. Can't be undone.
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Reset(string projectId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null || !AuthController.TryString(body.RootElement, "confirm", out var confirm))
            return BadRequestError("Send { confirm: \"<project name>\" }.");
        if (await projects.NameAsync(projectId, ct) is not { } name) return NotFoundError($"Project {projectId} not found");
        if (!string.Equals(confirm.Trim(), name.Trim(), StringComparison.Ordinal))
            return BadRequestError($"Type the project name exactly (\"{name}\") to confirm.");
        try
        {
            return await reset.ResetAsync(projectId, ct) is { } result ? Ok(result) : NotFoundError($"Project {projectId} not found");
        }
        catch (PostgresException e)
        {
            // Nothing was deleted: the transaction rolled back (e.g. a missing permission or a table we don't clear yet).
            return StatusCode(500, new { error = new { code = "internal", message = $"Nothing was deleted. The database said: {e.MessageText}" } });
        }
    }
}
