using Npgsql;

namespace FeedbackTool.Data;

public sealed class VendorReadRepository(IConfiguration configuration) : IVendorReadRepository
{
    public async Task<bool> ProjectExistsAsync(string projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select exists(select 1 from projects where id = @projectId)", connection);
        command.Parameters.AddWithValue("projectId", projectId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<VendorListResult> ListAsync(string projectId, VendorFilter filter, string sort, int? limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = ResponseWhere(filter, "r", includeVendor: false);
        var sql = $$"""
            with filtered_responses as (select r.* from responses r where {{where}}),
            request_features as (select distinct m.feature_id from mentions m join filtered_responses r on r.id=m.response_id where m.kind='want'),
            vendor_metrics as (
              select v.id, v.name,
                (select count(*) from filtered_responses r where r.vendor_id=v.id)::int as response_count,
                (select count(*) from vendor_offerings vo join request_features rf on rf.feature_id=vo.feature_id where vo.vendor_id=v.id)::int as requests_met,
                (select count(*) from request_features)::int as requests_total,
                (select count(*) from vendor_offerings vo where vo.vendor_id=v.id)::int as feature_count,
                (select count(*) from vendor_offerings vo where vo.vendor_id=v.id and not exists (select 1 from vendor_offerings other where other.feature_id=vo.feature_id and other.vendor_id<>v.id))::int as exclusive_count,
                (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where r.vendor_id=v.id and m.kind='like')::int as likes,
                (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where r.vendor_id=v.id and m.kind='dislike')::int as dislikes,
                (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where r.vendor_id=v.id and m.kind='want')::int as wants
              from vendors v where v.project_id=@projectId)
            select id, name, response_count, requests_met, requests_total, feature_count, exclusive_count, likes, dislikes, wants,
                   count(*) over()::int as total
            from vendor_metrics
            order by {{(sort == "name" ? "name asc, id asc" : "requests_met desc, response_count desc, name asc, id asc")}}
            {{(limit.HasValue ? "limit @limit" : string.Empty)}};
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        AddFilterParameters(command, projectId, filter);
        if (limit.HasValue) command.Parameters.AddWithValue("limit", limit.Value);
        var items = new List<VendorListItem>(); var total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { total = reader.GetInt32(10); items.Add(ReadVendor(reader)); }
        return new VendorListResult(items, total);
    }

    public async Task<VendorDetail?> GetAsync(string projectId, string vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        const string sql = """
            with request_features as (select distinct m.feature_id from mentions m join responses r on r.id=m.response_id where r.project_id=@projectId and m.kind='want')
            select v.id,v.name,
              (select count(*) from responses r where r.project_id=@projectId and r.vendor_id=v.id)::int,
              (select count(*) from vendor_offerings vo join request_features rf on rf.feature_id=vo.feature_id where vo.vendor_id=v.id)::int,
              (select count(*) from request_features)::int,
              (select count(*) from vendor_offerings vo where vo.vendor_id=v.id)::int,
              (select count(*) from vendor_offerings vo where vo.vendor_id=v.id and not exists (select 1 from vendor_offerings o where o.feature_id=vo.feature_id and o.vendor_id<>v.id))::int,
              (select count(*) from mentions m join responses r on r.id=m.response_id where r.project_id=@projectId and r.vendor_id=v.id and m.kind='like')::int,
              (select count(*) from mentions m join responses r on r.id=m.response_id where r.project_id=@projectId and r.vendor_id=v.id and m.kind='dislike')::int,
              (select count(*) from mentions m join responses r on r.id=m.response_id where r.project_id=@projectId and r.vendor_id=v.id and m.kind='want')::int
            from vendors v where v.project_id=@projectId and v.id=@vendorId;
            """;
        await using var command = new NpgsqlCommand(sql, connection); command.Parameters.AddWithValue("projectId", projectId); command.Parameters.AddWithValue("vendorId", vendorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var item = ReadVendor(reader);
        var ids = new List<string>();
        await reader.CloseAsync();
        await using var idCommand = new NpgsqlCommand("select feature_id from vendor_offerings where vendor_id=@vendorId order by feature_id", connection);
        idCommand.Parameters.AddWithValue("vendorId", vendorId);
        await using var idReader = await idCommand.ExecuteReaderAsync(cancellationToken);
        while (await idReader.ReadAsync(cancellationToken)) ids.Add(idReader.GetString(0));
        return new VendorDetail(item.Id, item.Name, item.ResponseCount, item.RequestsMet, item.RequestsTotal, item.FeatureCount, item.ExclusiveCount, item.Sentiment, ids);
    }

    public async Task<AiSummary?> GetSummaryAsync(string projectId, string vendorId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("select s.text,s.based_on,s.generated_at from vendors v left join vendor_summaries s on s.vendor_id=v.id where v.project_id=@projectId and v.id=@vendorId", connection);
        command.Parameters.AddWithValue("projectId", projectId); command.Parameters.AddWithValue("vendorId", vendorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new AiSummary(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<VendorRequestList?> GetRequestsAsync(string projectId, string vendorId, VendorFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = ResponseWhere(filter, "r", false);
        var sql = $$"""
            with target as (select 1 from vendors where project_id=@projectId and id=@vendorId),
            filtered_responses as (select r.* from responses r where {{where}}),
            requests as (select f.id,f.name,count(m.id)::int as wants from features f join mentions m on m.feature_id=f.id join filtered_responses r on r.id=m.response_id where m.kind='want' and f.project_id=@projectId group by f.id,f.name)
            select r.id,r.name,exists(select 1 from vendor_offerings vo where vo.vendor_id=@vendorId and vo.feature_id=r.id),r.wants,count(*) over()::int
            from requests r where exists(select 1 from target) order by 3 desc,r.wants desc,r.name asc;
            """;
        await using var command = new NpgsqlCommand(sql, connection); AddFilterParameters(command, projectId, filter); command.Parameters.AddWithValue("vendorId", vendorId);
        var items = new List<VendorRequest>(); var total = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { total=reader.GetInt32(4); items.Add(new VendorRequest(new NamedRef(reader.GetString(0),reader.GetString(1)),reader.GetBoolean(2),reader.GetInt32(3))); }
        await reader.CloseAsync();
        if (items.Count == 0 && !await VendorExistsAsync(connection, projectId, vendorId, cancellationToken)) return null;
        return new VendorRequestList(items,total);
    }

    public async Task<VendorFeatureList?> GetFeaturesAsync(string projectId, string vendorId, VendorFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var where = ResponseWhere(filter, "r", false) + " and r.vendor_id=@vendorId";
        var sql = $$"""
            with target as (select 1 from vendors where project_id=@projectId and id=@vendorId), filtered_responses as (select r.* from responses r where {{where}})
            select f.id,f.name,not exists(select 1 from vendor_offerings o where o.feature_id=f.id and o.vendor_id<>@vendorId),
              (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where m.feature_id=f.id and m.kind='like')::int,
              (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where m.feature_id=f.id and m.kind='dislike')::int,
              (select count(*) from mentions m join filtered_responses r on r.id=m.response_id where m.feature_id=f.id and m.kind='want')::int,
              count(*) over()::int
            from features f join vendor_offerings vo on vo.feature_id=f.id and vo.vendor_id=@vendorId
            where f.project_id=@projectId and exists(select 1 from target)
            order by 4+5+6 desc,f.name asc;
            """;
        await using var command = new NpgsqlCommand(sql, connection); AddFilterParameters(command, projectId, filter); command.Parameters.AddWithValue("vendorId", vendorId);
        var items = new List<VendorFeature>(); var total=0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) { total=reader.GetInt32(6); items.Add(new VendorFeature(reader.GetString(0),reader.GetString(1),reader.GetBoolean(2),new SentimentCounts(reader.GetInt32(3),reader.GetInt32(4),reader.GetInt32(5)))); }
        await reader.CloseAsync();
        if (items.Count == 0 && !await VendorExistsAsync(connection, projectId, vendorId, cancellationToken)) return null;
        return new VendorFeatureList(items,total);
    }

    public async Task<ResponseWithMentionsList?> GetResponsesAsync(string projectId, string vendorId, VendorFilter filter, int? limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await VendorExistsAsync(connection, projectId, vendorId, cancellationToken)) return null;
        var where = ResponseWhere(filter, "r", true);
        var sql = $$"""
            select r.id,r.survey_id,r.respondent_group,v.id,v.name,r.submitted_at,r.likes,r.dislikes,r.wants,r.analyzed_at,count(*) over()::int
            from responses r join vendors v on v.id=r.vendor_id where {{where}}
            order by r.submitted_at desc,r.id desc {{(limit.HasValue ? "limit @limit" : string.Empty)}};
            """;
        await using var command = new NpgsqlCommand(sql, connection); AddFilterParameters(command, projectId, filter, vendorId); if(limit.HasValue)command.Parameters.AddWithValue("limit",limit.Value);
        var raw = new List<(string Id,string Survey,string Group,NamedRef Vendor,DateTimeOffset Submitted,string Likes,string Dislikes,string Wants,bool Analyzed)>(); var total=0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken)) { total=reader.GetInt32(10); raw.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2),new NamedRef(reader.GetString(3),reader.GetString(4)),reader.GetFieldValue<DateTimeOffset>(5),reader.GetString(6),reader.GetString(7),reader.GetString(8),!reader.IsDBNull(9))); }
        var mentionMap = await ReadMentionsAsync(connection, raw.Select(x=>x.Id).ToArray(), cancellationToken);
        return new ResponseWithMentionsList(raw.Select(x=>new ResponseWithMentions(x.Id,x.Survey,x.Group,x.Vendor,x.Submitted,x.Likes,x.Dislikes,x.Wants,x.Analyzed,mentionMap.GetValueOrDefault(x.Id,[]))).ToList(),total);
    }

    private async Task<Dictionary<string,IReadOnlyList<ResponseMention>>> ReadMentionsAsync(NpgsqlConnection connection, string[] ids, CancellationToken ct)
    {
        var result = new Dictionary<string,IReadOnlyList<ResponseMention>>(); if(ids.Length==0)return result;
        await using var command = new NpgsqlCommand("select m.response_id,f.id,f.name,m.kind from mentions m join features f on f.id=m.feature_id where m.response_id=any(@ids) order by m.response_id,f.name,m.kind",connection); command.Parameters.AddWithValue("ids",ids);
        await using var reader = await command.ExecuteReaderAsync(ct); var working=new Dictionary<string,List<ResponseMention>>();
        while(await reader.ReadAsync(ct)){ var id=reader.GetString(0); if(!working.TryGetValue(id,out var list))working[id]=list=[]; list.Add(new ResponseMention(new NamedRef(reader.GetString(1),reader.GetString(2)),reader.GetString(3))); }
        return working.ToDictionary(x=>x.Key,x=>(IReadOnlyList<ResponseMention>)x.Value);
    }

    private static VendorListItem ReadVendor(NpgsqlDataReader r) => new(r.GetString(0),r.GetString(1),r.GetInt32(2),r.GetInt32(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),new SentimentCounts(r.GetInt32(7),r.GetInt32(8),r.GetInt32(9)));
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var cs=configuration.GetConnectionString("DefaultConnection"); if(string.IsNullOrWhiteSpace(cs))throw new InvalidOperationException("Database connection is not configured."); var c=new NpgsqlConnection(cs); await c.OpenAsync(ct); return c; }
    private static string ResponseWhere(VendorFilter filter,string alias,bool includeVendor) { var clauses=new List<string>{$"{alias}.project_id=@projectId"}; if(includeVendor)clauses.Add($"{alias}.vendor_id=@vendorId"); if(filter.From.HasValue)clauses.Add($"{alias}.submitted_at>=@from"); if(filter.To.HasValue)clauses.Add($"{alias}.submitted_at<@toExclusive"); if(filter.Group is not null)clauses.Add($"{alias}.respondent_group=@group"); return string.Join(" and ",clauses); }
    private static void AddFilterParameters(NpgsqlCommand c,string projectId,VendorFilter f,string? vendorId=null) { c.Parameters.AddWithValue("projectId",projectId); if(vendorId is not null)c.Parameters.AddWithValue("vendorId",vendorId); if(f.From.HasValue)c.Parameters.AddWithValue("from",new DateTimeOffset(f.From.Value.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)); if(f.To.HasValue)c.Parameters.AddWithValue("toExclusive",new DateTimeOffset(f.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)); if(f.Group is not null)c.Parameters.AddWithValue("group",f.Group); }
    private static async Task<bool> VendorExistsAsync(NpgsqlConnection c,string projectId,string vendorId,CancellationToken ct) { await using var cmd=new NpgsqlCommand("select exists(select 1 from vendors where project_id=@projectId and id=@vendorId)",c);cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.AddWithValue("vendorId",vendorId);return (bool)(await cmd.ExecuteScalarAsync(ct)??false); }
}
