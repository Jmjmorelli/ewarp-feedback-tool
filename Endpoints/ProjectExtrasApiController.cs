using Npgsql;
using NpgsqlTypes;
using System.Text.Json;
using FeedbackTool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[Route("api/v1/projects/{projectId}")]
public sealed class ProjectExtrasApiController(IConfiguration configuration) : ApiControllerBase
{
    private string Cs => configuration.GetConnectionString("DefaultConnection") ??
                         throw new InvalidOperationException("Database connection is not configured.");

    [HttpGet("analytics/responses-by-week")]
    public async Task<IActionResult> Weeks(string projectId, int? weeks, DateOnly? from, DateOnly? to, string? group,
        CancellationToken ct)
    {
        if (weeks is < 1 or > 52 || !FiltersValid(from, to, group)) return BadRequestError("Invalid query parameters.");
        if (!await Exists(projectId, ct)) return NotFoundError($"Project {projectId} not found");
        var n = weeks ?? 6;
        const string sql = """
                           with bucket as (select generate_series(date_trunc('week', current_date) - ((@weeks-1) * interval '1 week'), date_trunc('week', current_date), interval '1 week')::date week_start),
                           filtered as (select r.* from responses r join surveys s on s.id=r.survey_id where r.project_id=@projectId and s.included_in_analysis and (@from is null or r.submitted_at>=@from) and (@to is null or r.submitted_at<@to + interval '1 day') and (@group is null or (r.respondent_group=@group or r.respondent_role=@group)))
                           select b.week_start, count(f.id) filter(where f.respondent_group='pm')::int, count(f.id) filter(where f.respondent_group='sponsor')::int, count(f.id) filter(where f.respondent_group not in ('pm','sponsor'))::int from bucket b left join filtered f on f.submitted_at>=b.week_start and f.submitted_at<b.week_start+interval '1 week' group by b.week_start order by b.week_start;
                           """;
        var items = new List<object>();
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        AddFilters(cmd, projectId, from, to, group);
        cmd.Parameters.AddWithValue("weeks", n);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                weekStart = DateOnly.FromDateTime(r.GetDateTime(0)).ToString("yyyy-MM-dd"),
                counts = new { pm = r.GetInt32(1), sponsor = r.GetInt32(2), other = r.GetInt32(3) }
            });
        return Ok(new { weeks = items });
    }

    [HttpGet("surveys")]
    public async Task<IActionResult> Surveys(string projectId, CancellationToken ct)
    {
        if (!await Exists(projectId, ct)) return NotFoundError($"Project {projectId} not found");
        var items = await ReadSurveys(projectId, null, ct);
        return Ok(new { items, total = items.Count });
    }

    // Every survey in the project, or just surveyId, in the Survey shape (counts include excluded surveys' responses).
    private async Task<List<object>> ReadSurveys(string projectId, string? surveyId, CancellationToken ct)
    {
        const string sql =
            """select s.id,s.name,s.source,s.status,s.created_at,s.included_in_analysis,s.exclusion_reason,s.excluded_at,u.id,u.name,count(r.id)::int,count(r.id) filter(where r.analyzed_at is not null)::int,count(distinct r.vendor_id)::int,max(r.submitted_at),count(r.id) filter(where r.respondent_group='pm')::int,count(r.id) filter(where r.respondent_group='sponsor')::int,count(r.id) filter(where r.respondent_group not in ('pm','sponsor'))::int from surveys s left join users u on u.id=s.excluded_by_user_id left join responses r on r.survey_id=s.id where s.project_id=@projectId and (@surveyId::text is null or s.id=@surveyId) group by s.id,s.name,s.source,s.status,s.created_at,s.included_in_analysis,s.exclusion_reason,s.excluded_at,u.id,u.name order by s.created_at desc;""";
        var items = new List<object>();
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("projectId", projectId);
        cmd.Parameters.Add(new NpgsqlParameter("surveyId", NpgsqlDbType.Text) { Value = (object?)surveyId ?? DBNull.Value });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new
            {
                id = r.GetString(0), name = r.GetString(1), source = r.GetString(2), status = r.GetString(3),
                createdAt = r.GetFieldValue<DateTimeOffset>(4), includedInAnalysis = r.GetBoolean(5),
                exclusionReason = r.IsDBNull(6) ? null : r.GetString(6),
                excludedAt = r.IsDBNull(7) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(7),
                excludedBy = r.IsDBNull(8) ? null : new { id = r.GetString(8), name = r.GetString(9) },
                responseCount = r.GetInt32(10), analyzedCount = r.GetInt32(11), vendorCount = r.GetInt32(12),
                lastResponseAt = r.IsDBNull(13) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(13),
                groups = new { pm = r.GetInt32(14), sponsor = r.GetInt32(15), other = r.GetInt32(16) }
            });
        return items;
    }

    [HttpPatch("surveys/{surveyId}")]
    [Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> UpdateSurvey(string projectId, string surveyId, [FromBody] SurveyAnalysisUpdate update, CancellationToken ct)
    {
        if (update.IncludedInAnalysis is null) return BadRequestError("includedInAnalysis is required.");
        var reason = update.ExclusionReason?.Trim();
        if (!update.IncludedInAnalysis.Value && string.IsNullOrWhiteSpace(reason)) return BadRequestError("exclusionReason is required when excluding a survey.");
        if (reason?.Length > 2_000) return BadRequestError("exclusionReason must be at most 2000 characters.");
        var userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId)) return Forbid();

        const string sql = """
            with updated as (
              update surveys s set included_in_analysis=@included,
                exclusion_reason=case when @included then null else @reason end,
                excluded_at=case when @included then null else now() end,
                excluded_by_user_id=case when @included then null else @userId end
              where s.project_id=@projectId and s.id=@surveyId
              returning s.id,s.included_in_analysis,s.exclusion_reason,s.excluded_at,s.excluded_by_user_id
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
        await using var c = new NpgsqlConnection(Cs); await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("included", update.IncludedInAnalysis.Value);
        cmd.Parameters.AddWithValue("reason", (object?)reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("userId", userId);
        cmd.Parameters.AddWithValue("projectId", projectId);
        cmd.Parameters.AddWithValue("surveyId", surveyId);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (!await r.ReadAsync(ct)) return NotFoundError($"Survey {surveyId} was not found in project {projectId}");
        return Ok((await ReadSurveys(projectId, surveyId, ct)).Single());
    }

    [HttpGet("graph")]
    public async Task<IActionResult> Graph(string projectId, DateOnly? from, DateOnly? to, string? group,
        CancellationToken ct)
    {
        if (!FiltersValid(from, to, group)) return BadRequestError("Invalid query parameters.");
        if (!await Exists(projectId, ct)) return NotFoundError($"Project {projectId} not found");
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(ct);
        var responses = await ReadResponses(c, projectId, from, to, group, ct);
        const string fsql =
            """select f.id,f.name,count(o.vendor_id)::int,(select count(*)::int from vendors where project_id=@projectId),coalesce(array_agg(o.vendor_id) filter(where o.vendor_id is not null),array[]::text[]),f.source from features f left join vendor_offerings o on o.feature_id=f.id where f.project_id=@projectId group by f.id,f.name,f.source having count(o.vendor_id)>0 or f.source='manual' or exists(select 1 from mentions m join responses r on r.id=m.response_id join surveys s on s.id=r.survey_id where m.feature_id=f.id and r.project_id=@projectId and s.included_in_analysis and (@from is null or r.submitted_at>=@from) and (@to is null or r.submitted_at<@to+interval '1 day') and (@group is null or (r.respondent_group=@group or r.respondent_role=@group))) or exists(select 1 from feedback_entries e where e.feature_id=f.id and e.project_id=@projectId and (@from is null or e.created_at>=@from) and (@to is null or e.created_at<@to+interval '1 day') and (@group is null or (e.respondent_group=@group or e.respondent_role=@group)));""";
        var features = new List<object>();
        await using (var cmd = new NpgsqlCommand(fsql, c))
        {
            AddFilters(cmd, projectId, from, to, group);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var offered = r.GetInt32(2);
                var total = r.GetInt32(3);
                features.Add(new
                {
                    id = r.GetString(0), name = r.GetString(1),
                    coverage = offered == 0 ? "none" : offered == total ? "all" : "some",
                    vendorIds = r.GetFieldValue<string[]>(4),
                    source = r.GetString(5)
                });
            }
        }

        var vendors = new List<object>();
        await using (var cmd = new NpgsqlCommand(
                         "select id,name from vendors where project_id=@projectId order by name", c))
        {
            cmd.Parameters.AddWithValue("projectId", projectId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) vendors.Add(new { id = r.GetString(0), name = r.GetString(1) });
        }

        const string lsql =
            """select m.response_id,m.feature_id,m.kind from mentions m join responses r on r.id=m.response_id join surveys s on s.id=r.survey_id where r.project_id=@projectId and s.included_in_analysis and (@from is null or r.submitted_at>=@from) and (@to is null or r.submitted_at<@to+interval '1 day') and (@group is null or (r.respondent_group=@group or r.respondent_role=@group));""";
        var links = new List<object>();
        await using (var cmd = new NpgsqlCommand(lsql, c))
        {
            AddFilters(cmd, projectId, from, to, group);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                links.Add(new { responseId = r.GetString(0), featureId = r.GetString(1), kind = r.GetString(2) });
        }

        // Likes / dislikes / wants admins entered by hand (feature page "Added by admins"); the map draws them from one "Admins" node.
        const string msql =
            """select e.id,e.feature_id,e.kind,e.respondent_role,v.id,v.name,e.note,u.id,u.name,e.created_at from feedback_entries e left join vendors v on v.id=e.vendor_id left join users u on u.id=e.created_by where e.project_id=@projectId and (@from is null or e.created_at>=@from) and (@to is null or e.created_at<@to+interval '1 day') and (@group is null or (e.respondent_group=@group or e.respondent_role=@group)) order by e.created_at desc,e.id desc;""";
        var manual = new List<object>();
        await using (var cmd = new NpgsqlCommand(msql, c))
        {
            AddFilters(cmd, projectId, from, to, group);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                manual.Add(new
                {
                    id = r.GetString(0), featureId = r.GetString(1), kind = r.GetString(2), group = r.GetString(3),
                    vendor = r.IsDBNull(4) ? null : new { id = r.GetString(4), name = r.GetString(5) },
                    note = r.GetString(6),
                    createdBy = r.IsDBNull(7) ? null : new { id = r.GetString(7), name = r.GetString(8) },
                    createdAt = r.GetFieldValue<DateTimeOffset>(9)
                });
        }

        return Ok(new { responses, features, vendors, links, manual });
    }

    [HttpGet("activity")]
    public async Task<IActionResult> Activity(string projectId, int? limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) return BadRequestError("limit must be between 1 and 100.");
        if (!await Exists(projectId, ct)) return NotFoundError($"Project {projectId} not found");
        var n = limit ?? 10;
        var items = new List<object>();
        const string sql =
            """select a.id,a.type,a.occurred_at,a.response_id,a.feature_id,a.feature_ids,r.respondent_group,v.id,v.name,f.id,f.name from activity_events a left join responses r on r.id=a.response_id left join surveys s on s.id=r.survey_id left join vendors v on v.id=r.vendor_id left join features f on f.id=a.feature_id where a.project_id=@projectId and (a.response_id is null or s.included_in_analysis) order by a.occurred_at desc limit @limit;""";
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("projectId", projectId);
        cmd.Parameters.AddWithValue("limit", n);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var id = r.GetString(0);
                var type = r.GetString(1);
                var at = r.GetFieldValue<DateTimeOffset>(2);
                if (type == "response.submitted")
                    items.Add(new
                    {
                        id, type, occurredAt = at,
                        response = new
                        {
                            id = r.GetString(3), group = r.GetString(6),
                            vendor = new { id = r.GetString(7), name = r.GetString(8) }
                        }
                    });
                else if (type == "gap.detected")
                    items.Add(new
                        { id, type, occurredAt = at, feature = new { id = r.GetString(9), name = r.GetString(10) } });
                else
                {
                    var ids = r.IsDBNull(5)
                        ? Array.Empty<string>()
                        : JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [];
                    items.Add(new
                    {
                        id, type, occurredAt = at, responseId = r.IsDBNull(3) ? null : r.GetString(3),
                        features = ids.Select(x => new { id = x, name = x })
                    });
                }
            }
        }

        var total = await CountActivity(c, projectId, ct);
        return Ok(new { items, total });
    }

    private async Task<List<object>> ReadResponses(NpgsqlConnection c, string projectId, DateOnly? from, DateOnly? to,
        string? group, CancellationToken ct)
    {
        const string sql =
            """select r.id,r.survey_id,r.respondent_group,v.id,v.name,r.submitted_at,r.likes,r.dislikes,r.wants,r.analyzed_at is not null from responses r join vendors v on v.id=r.vendor_id join surveys s on s.id=r.survey_id where r.project_id=@projectId and s.included_in_analysis and (@from is null or r.submitted_at>=@from) and (@to is null or r.submitted_at<@to+interval '1 day') and (@group is null or (r.respondent_group=@group or r.respondent_role=@group)) order by r.submitted_at desc;""";
        var list = new List<object>();
        await using var cmd = new NpgsqlCommand(sql, c);
        AddFilters(cmd, projectId, from, to, group);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new
            {
                id = r.GetString(0), surveyId = r.GetString(1), group = r.GetString(2),
                vendor = new { id = r.GetString(3), name = r.GetString(4) },
                submittedAt = r.GetFieldValue<DateTimeOffset>(5), likes = r.GetString(6), dislikes = r.GetString(7),
                wants = r.GetString(8), analyzed = r.GetBoolean(9)
            });
        return list;
    }

    private async Task<int> CountActivity(NpgsqlConnection c, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select count(*) from activity_events a left join responses r on r.id=a.response_id left join surveys s on s.id=r.survey_id where a.project_id=@id and (a.response_id is null or s.included_in_analysis)", c);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private async Task<bool> Exists(string id, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(Cs);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select exists(select 1 from projects where id=@id)", c);
        cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static bool FiltersValid(DateOnly? from, DateOnly? to, string? group) =>
        (from is null || to is null || from <= to) && Data.RespondentGroups.IsValid(group);

    private static void AddFilters(NpgsqlCommand cmd, string id, DateOnly? from, DateOnly? to, string? group)
    {
        cmd.Parameters.AddWithValue("projectId", id);
        cmd.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = from is null ? DBNull.Value : new DateTimeOffset(from.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) });
        cmd.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.TimestampTz) { Value = to is null ? DBNull.Value : new DateTimeOffset(to.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) });
        cmd.Parameters.Add(new NpgsqlParameter("group", NpgsqlDbType.Text) { Value = (object?)group ?? DBNull.Value });
    }
}
