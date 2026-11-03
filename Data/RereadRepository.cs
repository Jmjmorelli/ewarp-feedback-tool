using Npgsql;

namespace FeedbackTool.Data;

// Queues responses for the classifier to read again (table response_rereads, Migrations/002).
// The classifier (classifier/backend_sync.py) reads every queued response on its next pass, adds any new
// features and mentions, and removes the row. Existing mentions stay; it only adds what it finds.
public sealed class RereadRepository(IConfiguration configuration)
{
    // responseIds = null queues every response in the project that has no linked features yet.
    // Returns null when the project does not exist, else (queued count, first ID not in the project).
    public async Task<(int Queued, string? UnknownResponse)?> QueueAsync(string projectId, IReadOnlyList<string>? responseIds, string userId, CancellationToken ct)
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
            insert into response_rereads (response_id, requested_at, requested_by)
            select r.id, now(), @userId from responses r
            where r.project_id=@projectId
              and (case when @all then not exists(select 1 from mentions m where m.response_id=r.id) else r.id = any(@ids) end)
            on conflict (response_id) do update set requested_at=excluded.requested_at, requested_by=excluded.requested_by;
            """;
        await using var insert = new NpgsqlCommand(sql, c);
        insert.Parameters.AddWithValue("projectId", projectId);
        insert.Parameters.AddWithValue("userId", userId);
        insert.Parameters.AddWithValue("all", responseIds is null);
        insert.Parameters.AddWithValue("ids", (responseIds ?? []).ToArray());
        return (await insert.ExecuteNonQueryAsync(ct), null);
    }
}
