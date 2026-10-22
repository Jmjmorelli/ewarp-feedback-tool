using FeedbackTool.Models;
using Npgsql;
using NpgsqlTypes;

namespace FeedbackTool.Data;

public sealed class ResponseRepository(IConfiguration configuration) : IResponseRepository
{
    public async Task<ResponseListDto?> ListAsync(string projectId, ResponseListQuery query, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await ProjectExistsAsync(connection, projectId, cancellationToken)) return null;

        var where = BuildWhere(query, includeGroup: true, includeStatus: true);
        var total = await ScalarAsync(connection, $"select count(*) from responses r {where.Sql}", projectId, query, cancellationToken);
        var order = query.Sort == "oldest" ? "r.submitted_at asc, r.id asc" : "r.submitted_at desc, r.id desc";
        var page = query.Limit is not null ? " limit @limit offset @offset" : query.Offset > 0 ? " offset @offset" : string.Empty;
        var sql = $"""
            select r.id, r.survey_id, r.respondent_group, v.id, v.name, r.submitted_at,
                   r.likes, r.dislikes, r.wants, r.analyzed_at is not null
            from responses r join vendors v on v.id = r.vendor_id
            {where.Sql}
            order by {order}{page};
            """;
        var items = new List<ResponseItemDto>();
        var ids = new List<string>();
        await using (var command = CreateCommand(connection, sql, projectId, query))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0); ids.Add(id);
                items.Add(new ResponseItemDto(id, reader.GetString(1), reader.GetString(2),
                    new NamedReference(reader.GetString(3), reader.GetString(4)), reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetBoolean(9), Array.Empty<ResponseMentionDto>()));
            }
        }
        var mentions = await GetMentionsAsync(connection, ids, cancellationToken);
        items = items.Select(x => x with { Mentions = mentions.TryGetValue(x.Id, out var value) ? value : Array.Empty<ResponseMentionDto>() }).ToList();

        var groupWhere = BuildWhere(query, includeGroup: false, includeStatus: true);
        var statusWhere = BuildWhere(query, includeGroup: true, includeStatus: false);
        var groups = await CountsAsync(connection, $"select r.respondent_group, count(*) from responses r {groupWhere.Sql} group by r.respondent_group", projectId, query, cancellationToken);
        var statuses = await CountsAsync(connection, $"select case when r.analyzed_at is null then 'pending' else 'analyzed' end, count(*) from responses r {statusWhere.Sql} group by 1", projectId, query, cancellationToken);
        return new ResponseListDto(items, total, new ResponseFacetsDto(
            new GroupCountsDto(Get(groups, "pm"), Get(groups, "sponsor"), Get(groups, "other")),
            new StatusCountsDto(Get(statuses, "analyzed"), Get(statuses, "pending"))));
    }

    public async Task<ResponseDetailDto?> GetAsync(string projectId, string responseId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await ProjectExistsAsync(connection, projectId, cancellationToken)) return null;
        const string sql = """
            select r.id, r.survey_id, r.respondent_group, v.id, v.name, r.submitted_at, r.likes, r.dislikes, r.wants,
                   r.analyzed_at is not null, s.id, s.name
            from responses r join vendors v on v.id = r.vendor_id join surveys s on s.id = r.survey_id
            where r.project_id = @projectId and r.id = @responseId;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("projectId", projectId); command.Parameters.AddWithValue("responseId", responseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var id = reader.GetString(0);
        var result = new ResponseDetailDto(id, reader.GetString(1), reader.GetString(2), new NamedReference(reader.GetString(3), reader.GetString(4)),
            reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetBoolean(9),
            new NamedReference(reader.GetString(10), reader.GetString(11)), Array.Empty<ResponseMentionDetailDto>());
        await reader.CloseAsync();
        const string mentionsSql = """
            select f.id, f.name, m.kind,
                   case when count(offer_vendor.id) = 0 then 'none'
                        when count(offer_vendor.id) = (select count(*) from vendors where project_id = @projectId) then 'all'
                        else 'some' end
            from mentions m join features f on f.id = m.feature_id
            left join vendor_offerings vo on vo.feature_id = f.id
            left join vendors offer_vendor on offer_vendor.id = vo.vendor_id and offer_vendor.project_id = @projectId
            where m.response_id = @responseId and f.project_id = @projectId
            group by f.id, f.name, m.kind order by f.name, m.kind;
            """;
        await using var mentionsCommand = new NpgsqlCommand(mentionsSql, connection);
        mentionsCommand.Parameters.AddWithValue("projectId", projectId); mentionsCommand.Parameters.AddWithValue("responseId", id);
        var mentions = new List<ResponseMentionDetailDto>();
        await using var mentionsReader = await mentionsCommand.ExecuteReaderAsync(cancellationToken);
        while (await mentionsReader.ReadAsync(cancellationToken))
            mentions.Add(new ResponseMentionDetailDto(new NamedReference(mentionsReader.GetString(0), mentionsReader.GetString(1)), mentionsReader.GetString(2), mentionsReader.GetString(3)));
        return result with { Mentions = mentions };
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var value = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Database connection is not configured.");
        var connection = new NpgsqlConnection(value); await connection.OpenAsync(ct); return connection;
    }
    private static async Task<bool> ProjectExistsAsync(NpgsqlConnection c, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select exists(select 1 from projects where id = @id)", c); cmd.Parameters.AddWithValue("id", id);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
    private sealed record WhereClause(string Sql);

    private static WhereClause BuildWhere(ResponseListQuery q, bool includeGroup, bool includeStatus)
    {
        var clauses = new List<string> { "where r.project_id = @projectId" };
        if (!string.IsNullOrWhiteSpace(q.VendorId)) clauses.Add("r.vendor_id = @vendorId");
        if (!string.IsNullOrWhiteSpace(q.SurveyId)) clauses.Add("r.survey_id = @surveyId");
        if (includeStatus && !string.IsNullOrWhiteSpace(q.Status)) clauses.Add(q.Status == "analyzed" ? "r.analyzed_at is not null" : "r.analyzed_at is null");
        if (!string.IsNullOrWhiteSpace(q.Q)) clauses.Add("(r.likes ilike @search or r.dislikes ilike @search or r.wants ilike @search)");
        if (q.From is not null) clauses.Add("r.submitted_at >= @from");
        if (q.To is not null) clauses.Add("r.submitted_at < @toExclusive");
        if (includeGroup && !string.IsNullOrWhiteSpace(q.Group)) clauses.Add("r.respondent_group = @group");
        return new WhereClause(string.Join(" and ", clauses));
    }
    private static NpgsqlCommand CreateCommand(NpgsqlConnection c, string sql, string projectId, ResponseListQuery q)
    {
        var cmd = new NpgsqlCommand(sql, c); cmd.Parameters.AddWithValue("projectId", projectId);
        if (!string.IsNullOrWhiteSpace(q.VendorId)) cmd.Parameters.AddWithValue("vendorId", q.VendorId);
        if (!string.IsNullOrWhiteSpace(q.SurveyId)) cmd.Parameters.AddWithValue("surveyId", q.SurveyId);
        if (!string.IsNullOrWhiteSpace(q.Q)) cmd.Parameters.AddWithValue("search", $"%{q.Q}%");
        if (q.From is not null) cmd.Parameters.AddWithValue("from", q.From.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (q.To is not null) cmd.Parameters.AddWithValue("toExclusive", q.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        if (!string.IsNullOrWhiteSpace(q.Group)) cmd.Parameters.AddWithValue("group", q.Group);
        if (q.Limit is not null) cmd.Parameters.AddWithValue("limit", q.Limit.Value);
        if (q.Limit is not null || q.Offset > 0) cmd.Parameters.AddWithValue("offset", q.Offset);
        return cmd;
    }
    private static async Task<int> ScalarAsync(NpgsqlConnection c, string sql, string projectId, ResponseListQuery q, CancellationToken ct)
    { await using var cmd = CreateCommand(c, sql, projectId, q); return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)); }
    private static async Task<Dictionary<string, int>> CountsAsync(NpgsqlConnection c, string sql, string projectId, ResponseListQuery q, CancellationToken ct)
    {
        await using var cmd = CreateCommand(c, sql, projectId, q); var values = new Dictionary<string, int>();
        await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) values[r.GetString(0)] = Convert.ToInt32(r.GetValue(1)); return values;
    }
    private static int Get(Dictionary<string, int> values, string key) => values.TryGetValue(key, out var value) ? value : 0;
    private static async Task<Dictionary<string, IReadOnlyList<ResponseMentionDto>>> GetMentionsAsync(NpgsqlConnection c, List<string> ids, CancellationToken ct)
    {
        var values = new Dictionary<string, List<ResponseMentionDto>>(); if (ids.Count == 0) return new();
        await using var cmd = new NpgsqlCommand("select m.response_id, f.id, f.name, m.kind from mentions m join features f on f.id = m.feature_id where m.response_id = any(@ids) order by f.name, m.kind", c);
        cmd.Parameters.AddWithValue("ids", NpgsqlDbType.Array | NpgsqlDbType.Text, ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct); while (await r.ReadAsync(ct)) { var id = r.GetString(0); if (!values.TryGetValue(id, out var list)) values[id] = list = []; list.Add(new ResponseMentionDto(new NamedReference(r.GetString(1), r.GetString(2)), r.GetString(3))); }
        return values.ToDictionary(x => x.Key, x => (IReadOnlyList<ResponseMentionDto>)x.Value);
    }
}
