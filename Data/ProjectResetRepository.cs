using Npgsql;

namespace FeedbackTool.Data;

public sealed record ProjectResetResult(int Responses, int Features);

// Clears one project's survey data for a clean start (Settings → Reset). One transaction: all or nothing.
// Deletes: responses and their raw survey submissions, mentions, features, manual feedback, which vendors offer what,
// AI summaries and the activity feed. Keeps: the project, vendors, survey forms (so new submissions still arrive),
// users and group weights. Raw submissions for the project's forms go too, so editing a vendor doesn't re-import them.
public sealed class ProjectResetRepository(IConfiguration configuration)
{
    // null when the project does not exist.
    public async Task<ProjectResetResult?> ResetAsync(string projectId, CancellationToken ct)
    {
        var cs = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured.");
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (!(bool)(await ScalarAsync(c, tx, "select exists(select 1 from projects where id=@projectId)", projectId, ct) ?? false)) return null;

        const string submissions = """
            select coalesce(array_agg(distinct x.id), array[]::text[]) from (
              select r.raw_submission_id as id from responses r where r.project_id=@projectId and r.raw_submission_id is not null
              union
              select ss.id from survey_submissions ss join form_configurations fc
                on lower(regexp_replace(btrim(ss.form),'\s+',' ','g'))=lower(regexp_replace(btrim(fc.form),'\s+',' ','g'))
              where fc.project_id=@projectId) x;
            """;
        var submissionIds = (string[])(await ScalarAsync(c, tx, submissions, projectId, ct))!;

        const string projectFeatures = "select id from features where project_id=@projectId";
        await ExecuteAsync(c, tx, "delete from activity_events where project_id=@projectId", projectId, ct);
        await ExecuteAsync(c, tx, "delete from feedback_entries where project_id=@projectId", projectId, ct);
        await ExecuteAsync(c, tx, "delete from mentions m using responses r where r.id=m.response_id and r.project_id=@projectId", projectId, ct);
        await ExecuteAsync(c, tx, $"delete from mentions where feature_id in ({projectFeatures})", projectId, ct);
        await ExecuteAsync(c, tx, $"delete from vendor_offerings where feature_id in ({projectFeatures})", projectId, ct);
        await ExecuteAsync(c, tx, $"delete from vendor_offering_removals where feature_id in ({projectFeatures})", projectId, ct);
        await ExecuteAsync(c, tx, $"delete from feature_summaries where feature_id in ({projectFeatures})", projectId, ct);
        await ExecuteAsync(c, tx, "delete from vendor_summaries where vendor_id in (select id from vendors where project_id=@projectId)", projectId, ct);
        var responses = await ExecuteAsync(c, tx, "delete from responses where project_id=@projectId", projectId, ct);
        await using (var cmd = new NpgsqlCommand("delete from survey_submissions where id = any(@ids)", c, tx))
        {
            cmd.Parameters.AddWithValue("ids", submissionIds);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var features = await ExecuteAsync(c, tx, "delete from features where project_id=@projectId", projectId, ct);
        await tx.CommitAsync(ct);
        return new(responses, features);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string projectId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("projectId", projectId);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, string projectId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("projectId", projectId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
