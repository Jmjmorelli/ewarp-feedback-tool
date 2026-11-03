using Npgsql;

namespace FeedbackTool.Data;

// Asks the classifier to read responses again, with no schema change: it stamps responses.analysis_error with
// "reread:<time>". That column is otherwise only cleared by ResponseAnalysisWorker, for responses not stamped yet
// (which the classifier reads anyway). The classifier (classifier/backend_sync.py) rereads any response whose stamp
// differs from the one it last handled, then clears the stamp, so a stamp still there means "in the queue"
// (ResponseDetailDto.RereadQueued). It only adds features and mentions; existing ones stay.
public sealed class RereadRepository(IConfiguration configuration)
{
    public const string Marker = "reread:";

    // responseIds = null queues every response in the project that has no linked features yet.
    // Responses already in the queue are skipped, so clicking twice doesn't make the AI read them twice.
    // Returns null when the project does not exist, else (queued count, first ID not in the project).
    public async Task<(int Queued, string? UnknownResponse)?> QueueAsync(string projectId, IReadOnlyList<string>? responseIds, CancellationToken ct)
    {
        var cs = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured.");
        await using var c = new NpgsqlConnection(cs);
        await c.OpenAsync(ct);
        await using (var exists = new NpgsqlCommand("select exists(select 1 from projects where id=@projectId)", c))
        {
            exists.Parameters.AddWithValue("projectId", projectId);
            if (!(bool)(await exists.ExecuteScalarAsync(ct) ?? false)) return null;
        }

        if (responseIds is not null)
        {
            await using var check = new NpgsqlCommand("select x from unnest(@ids) x where not exists(select 1 from responses r where r.id=x and r.project_id=@projectId) limit 1", c);
            check.Parameters.AddWithValue("ids", responseIds.ToArray());
            check.Parameters.AddWithValue("projectId", projectId);
            if (await check.ExecuteScalarAsync(ct) is string unknown) return (0, unknown);
        }

        const string sql = """
            update responses r set analysis_error = @marker || to_char(clock_timestamp() at time zone 'utc', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')
            where r.project_id=@projectId
              and coalesce(r.analysis_error, '') not like 'reread:%'
              and (case when @all then not exists(select 1 from mentions m where m.response_id=r.id) else r.id = any(@ids) end);
            """;
        await using var update = new NpgsqlCommand(sql, c);
        update.Parameters.AddWithValue("marker", Marker);
        update.Parameters.AddWithValue("projectId", projectId);
        update.Parameters.AddWithValue("all", responseIds is null);
        update.Parameters.AddWithValue("ids", (responseIds ?? []).ToArray());
        return (await update.ExecuteNonQueryAsync(ct), null);
    }
}
