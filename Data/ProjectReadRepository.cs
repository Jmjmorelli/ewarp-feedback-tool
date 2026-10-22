using Npgsql;

namespace FeedbackTool.Data;

public sealed class ProjectReadRepository(IConfiguration configuration)
{
    private string ConnectionString => configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("Database connection is not configured.");

    public async Task<IReadOnlyList<object>> ListProjectsAsync(CancellationToken ct)
    {
        const string sql = """
            select p.id, p.name, count(r.id)::int, max(r.submitted_at)
            from projects p left join responses r on r.project_id = p.id
            group by p.id, p.name order by p.name;
            """;
        var results = new List<object>();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new { id = reader.GetString(0), name = reader.GetString(1), responseCount = reader.GetInt32(2), lastResponseAt = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3) });
        return results;
    }

    public async Task<object?> GetProjectAsync(string projectId, CancellationToken ct)
    {
        const string sql = """
            select p.id, p.name, p.description, p.created_at, max(r.submitted_at),
              (select count(*)::int from features where project_id = p.id),
              (select count(*)::int from vendors where project_id = p.id),
              count(r.id)::int,
              (select count(*)::int from surveys where project_id = p.id)
            from projects p left join responses r on r.project_id = p.id
            where p.id = @id group by p.id, p.name, p.description, p.created_at;
            """;
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", projectId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new { id = reader.GetString(0), name = reader.GetString(1), description = reader.GetString(2), createdAt = reader.GetFieldValue<DateTimeOffset>(3), lastResponseAt = reader.IsDBNull(4) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(4), counts = new { features = reader.GetInt32(5), vendors = reader.GetInt32(6), responses = reader.GetInt32(7), surveys = reader.GetInt32(8) } };
    }

    public async Task<object?> SummaryAsync(string projectId, DateOnly? from, DateOnly? to, string? group, CancellationToken ct)
    {
        if (!await ExistsAsync(projectId, ct)) return null;
        const string sql = """
            with filtered as (select * from responses r where r.project_id=@id and (@from is null or r.submitted_at >= @from) and (@to is null or r.submitted_at < @to + interval '1 day') and (@group is null or r.respondent_group=@group)),
            fm as (select m.* from mentions m join filtered r on r.id=m.response_id)
            select (select count(*)::int from filtered),
              (select count(*)::int from filtered where submitted_at >= now() - interval '7 days'),
              (select count(*)::int from features where project_id=@id),
              (select count(*)::int from fm),
              (select count(distinct feature_id)::int from fm where kind='want'),
              (select count(distinct f.id)::int from features f where f.project_id=@id and exists(select 1 from fm where fm.feature_id=f.id and fm.kind='want') and not exists(select 1 from vendor_offerings o join vendors v on v.id=o.vendor_id where o.feature_id=f.id and v.project_id=@id)),
              (select count(*)::int from fm where kind='like'), (select count(*)::int from fm where kind='dislike'), (select count(*)::int from fm where kind='want'),
              (select count(*)::int from filtered where analyzed_at is not null);
            """;
        await using var c = new NpgsqlConnection(ConnectionString); await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c); AddFilters(cmd, projectId, from, to, group);
        await using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return new { responses = new { total = r.GetInt32(0), thisWeek = r.GetInt32(1) }, features = new { total = r.GetInt32(2), mentions = r.GetInt32(3) }, requests = new { total = r.GetInt32(4), unmet = r.GetInt32(5) }, sentiment = new { like = r.GetInt32(6), dislike = r.GetInt32(7), want = r.GetInt32(8) }, analysis = new { processed = r.GetInt32(9), total = r.GetInt32(0) } };
    }

    public async Task<object?> CoverageAsync(string projectId, CancellationToken ct)
    {
        if (!await ExistsAsync(projectId, ct)) return null;
        const string sql = """
            with c as (select f.id, count(o.vendor_id)::int offered, (select count(*)::int from vendors v where v.project_id=@id) vendors from features f left join vendor_offerings o on o.feature_id=f.id where f.project_id=@id group by f.id)
            select count(*) filter(where offered=vendors)::int, count(*) filter(where offered>0 and offered<vendors)::int, count(*) filter(where offered=0)::int, count(*)::int from c;
            """;
        await using var c = new NpgsqlConnection(ConnectionString); await c.OpenAsync(ct); await using var cmd = new NpgsqlCommand(sql, c); cmd.Parameters.AddWithValue("id", projectId);
        await using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return new { all = r.GetInt32(0), some = r.GetInt32(1), none = r.GetInt32(2), total = r.GetInt32(3) };
    }

    public async Task<bool> ExistsAsync(string projectId, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(ConnectionString); await c.OpenAsync(ct); await using var cmd = new NpgsqlCommand("select exists(select 1 from projects where id=@id)", c); cmd.Parameters.AddWithValue("id", projectId);
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    private static void AddFilters(NpgsqlCommand command, string projectId, DateOnly? from, DateOnly? to, string? group)
    {
        command.Parameters.AddWithValue("id", projectId);
        command.Parameters.AddWithValue("from", (object?)from?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value);
        command.Parameters.AddWithValue("to", (object?)to?.ToDateTime(TimeOnly.MinValue) ?? DBNull.Value);
        command.Parameters.AddWithValue("group", (object?)group ?? DBNull.Value);
    }
}
