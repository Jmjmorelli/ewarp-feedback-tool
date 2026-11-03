using System.Text.Json;
using FeedbackTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[Route("api/v1/projects/{projectId}")]
public sealed class ScoringController(ScoringRepository scoring) : ApiControllerBase
{
    public const double MaxWeight = 10;

    [HttpGet("weights")]
    public async Task<IActionResult> Weights(string projectId, CancellationToken ct) =>
        await scoring.GetWeightsAsync(projectId, ct) is { } weights ? Ok(weights) : NotFoundError($"Project {projectId} not found");

    // Body: { groups?: [{ key, weight }], features?: [{ id, weight }] }. Only the rows sent change. Returns the full weights.
    [Authorize(Policy = Policies.Admin)]
    [HttpPut("weights")]
    public async Task<IActionResult> UpdateWeights(string projectId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null || body.RootElement.ValueKind != JsonValueKind.Object) return BadRequestError("Send groups and/or features.");
        var root = body.RootElement;
        List<(string, double)>? groups = null, features = null;
        if (root.TryGetProperty("groups", out var groupsElement))
        {
            if (ReadRows(groupsElement, "key") is not { } rows) return BadRequestError($"groups must be a list of {{ key, weight }} with weights from 0 to {MaxWeight}.");
            if (rows.FirstOrDefault(x => !RespondentGroups.All.Contains(x.Item1)) is { Item1: { } unknown }) return BadRequestError($"Unknown group: {unknown}");
            groups = rows;
        }
        if (root.TryGetProperty("features", out var featuresElement))
        {
            if (ReadRows(featuresElement, "id") is not { } rows) return BadRequestError($"features must be a list of {{ id, weight }} with weights from 0 to {MaxWeight}.");
            features = rows;
        }
        if (groups is null && features is null) return BadRequestError("Send groups and/or features.");
        var (found, unknownFeature) = await scoring.UpdateWeightsAsync(projectId, new(groups, features), ct);
        if (!found) return NotFoundError($"Project {projectId} not found");
        if (unknownFeature is not null) return BadRequestError($"Unknown feature ID: {unknownFeature}");
        return Ok(await scoring.GetWeightsAsync(projectId, ct));
    }

    [HttpGet("scores")]
    public async Task<IActionResult> Scores(string projectId, CancellationToken ct) =>
        await scoring.GetScoresAsync(projectId, ct) is { } scores ? Ok(scores) : NotFoundError($"Project {projectId} not found");

    private static List<(string, double)>? ReadRows(JsonElement list, string idProperty)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var rows = new List<(string, double)>();
        foreach (var row in list.EnumerateArray())
        {
            if (!AuthController.TryString(row, idProperty, out var id) || !row.TryGetProperty("weight", out var w) || w.ValueKind != JsonValueKind.Number) return null;
            var weight = w.GetDouble();
            if (weight is < 0 or > MaxWeight || double.IsNaN(weight)) return null;
            rows.Add((id, weight));
        }
        return rows;
    }
}
