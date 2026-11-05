using FeedbackTool.Data;
using FeedbackTool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace FeedbackTool.Endpoints;

[ApiController]
[Route("api/v1/projects/{projectId}/responses")]
public sealed class ResponsesController(IResponseRepository responses, IConfiguration configuration) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string projectId, [FromQuery] ResponseListQuery query, CancellationToken cancellationToken)
    {
        var error = Validate(query); if (error is not null) return BadRequest(Error(error));
        if (query.IncludeExcluded && !User.IsInRole(Roles.Admin)) return Forbid();
        var result = await responses.ListAsync(projectId, query, cancellationToken);
        return result is null ? NotFound(NotFoundError("Project was not found.")) : Ok(result);
    }

    [HttpGet("{responseId}")]
    public async Task<IActionResult> Get(string projectId, string responseId, bool includeExcluded, CancellationToken cancellationToken)
    {
        if (includeExcluded && !User.IsInRole(Roles.Admin)) return Forbid();
        var result = await responses.GetAsync(projectId, responseId, includeExcluded, cancellationToken);
        return result is null ? NotFound(NotFoundError("Project or response was not found.")) : Ok(result);
    }

    [HttpPatch("{responseId}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> UpdateAnalysisInclusion(string projectId, string responseId, [FromBody] ResponseAnalysisUpdate update, CancellationToken cancellationToken)
    {
        if (update.IncludedInAnalysis is null) return BadRequest(Error("includedInAnalysis is required."));
        var reason = update.ExclusionReason?.Trim();
        if (!update.IncludedInAnalysis.Value && string.IsNullOrWhiteSpace(reason)) return BadRequest(Error("exclusionReason is required when excluding a response."));
        if (reason?.Length > 2_000) return BadRequest(Error("exclusionReason must be at most 2000 characters."));
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("Database connection is not configured.");

        const string sql = """
            with updated as (
              update responses r set included_in_analysis=@included,
                exclusion_reason=case when @included then null else @reason end,
                excluded_at=case when @included then null else now() end,
                excluded_by_user_id=case when @included then null else @userId end
              where r.project_id=@projectId and r.id=@responseId
              returning r.id,r.included_in_analysis,r.exclusion_reason,r.excluded_at,r.excluded_by_user_id
            ), cleared_feature_summaries as (
              delete from feature_summaries fs using features f
              where fs.feature_id=f.id and f.project_id=@projectId and exists(select 1 from updated)
              returning fs.feature_id
            ), cleared_vendor_summaries as (
              delete from vendor_summaries vs using vendors v
              where vs.vendor_id=v.id and v.project_id=@projectId and exists(select 1 from updated)
              returning vs.vendor_id
            )
            select x.id,x.included_in_analysis,x.exclusion_reason,x.excluded_at,u.id,u.name
            from updated x left join users u on u.id=x.excluded_by_user_id;
            """;
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("included", update.IncludedInAnalysis.Value);
        command.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("projectId", projectId);
        command.Parameters.AddWithValue("responseId", responseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return NotFound(NotFoundError("Project or response was not found."));
        return Ok(new
        {
            id = reader.GetString(0), includedInAnalysis = reader.GetBoolean(1),
            exclusionReason = reader.IsDBNull(2) ? null : reader.GetString(2),
            excludedAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3),
            excludedBy = reader.IsDBNull(4) ? null : new { id = reader.GetString(4), name = reader.GetString(5) }
        });
    }

    private static string? Validate(ResponseListQuery q)
    {
        if (q.Limit is < 1 or > 500) return "limit must be between 1 and 500.";
        if (q.Offset < 0) return "offset must be zero or greater.";
        if (q.Sort is not ("newest" or "oldest")) return "sort must be newest or oldest.";
        if (q.Status is not null && q.Status is not ("analyzed" or "pending")) return "status must be analyzed or pending.";
        if (!Data.RespondentGroups.IsValid(q.Group)) return "group must be one of: " + string.Join(", ", Data.RespondentGroups.All) + ".";
        if (q.Q?.Length > 100) return "q must be at most 100 characters.";
        if (q.From is not null && q.To is not null && q.From > q.To) return "from must not be after to.";
        return null;
    }
    private static object Error(string message) => new { error = new { code = "bad_request", message } };
    private static object NotFoundError(string message) => new { error = new { code = "not_found", message } };
}
