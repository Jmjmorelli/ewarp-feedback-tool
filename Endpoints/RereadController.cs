using System.Text.Json;
using FeedbackTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[Route("api/v1/projects/{projectId}/responses/reread")]
public sealed class RereadController(RereadRepository rereads) : ApiControllerBase
{
    public const int MaxIds = 500;

    // Body: { responseIds?: [...] }. Omit responseIds to queue every response with no linked features.
    // 202 { queued }: the classifier reads them again on its next pass (about every 30 s).
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Queue(string projectId, CancellationToken ct)
    {
        List<string>? ids = null;
        if (Request.ContentLength is > 0 || Request.Headers.ContentType.Count > 0)
        {
            using var body = await AuthController.ReadBodyAsync(Request, ct);
            if (body is null || body.RootElement.ValueKind != JsonValueKind.Object) return BadRequestError("Send { responseIds?: [...] }.");
            if (body.RootElement.TryGetProperty("responseIds", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
                    return BadRequestError("responseIds must be a list of response IDs.");
                ids = list.EnumerateArray().Select(x => x.GetString()!).Distinct().ToList();
                if (ids.Count is 0 or > MaxIds) return BadRequestError($"responseIds must have 1 to {MaxIds} IDs.");
            }
        }
        var result = await rereads.QueueAsync(projectId, ids, ct);
        if (result is null) return NotFoundError($"Project {projectId} not found");
        if (result.Value.UnknownResponse is { } unknown) return NotFoundError($"Response {unknown} not found in project {projectId}");
        return Accepted(new { queued = result.Value.Queued });
    }
}
