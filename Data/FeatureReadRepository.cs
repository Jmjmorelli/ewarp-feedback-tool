using Npgsql;
using NpgsqlTypes;

namespace FeedbackTool.Data;

public sealed class FeatureReadRepository(IConfiguration configuration) : IFeatureReadRepository
{
    public async Task<FeatureListResult?> ListAsync(string projectId, FeatureListQuery query, CancellationToken ct)
    {
        if (!await ProjectExistsAsync(projectId, ct)) return null;
        const string sql = """
            with filtered_mentions as (
              select m.feature_id, m.kind from mentions m join responses r on r.id=m.response_id
              where r.project_id=@projectId and (@from is null or r.submitted_at >= @from)
                and (@toExclusive is null or r.submitted_at < @toExclusive)
                and (@group is null or r.respondent_group=@group)
            ), offerings as (
              select vo.feature_id, array_agg(vo.vendor_id order by vo.vendor_id) vendor_ids, count(*) offering_count
              from vendor_offerings vo join vendors v on v.id=vo.vendor_id where v.project_id=@projectId group by vo.feature_id
            ), stats as (
              select f.id, f.name, f.created_at, coalesce(o.vendor_ids, array[]::text[]) vendor_ids,
                coalesce(o.offering_count,0)::int offering_count, count(fm.feature_id)::int mentions,
                count(*) filter (where fm.kind='like')::int likes, count(*) filter (where fm.kind='dislike')::int dislikes,
                count(*) filter (where fm.kind='want')::int wants,
                (select count(*) from vendors where project_id=@projectId)::int vendor_count
              from features f left join filtered_mentions fm on fm.feature_id=f.id left join offerings o on o.feature_id=f.id
              where f.project_id=@projectId and (@search is null or f.name ilike '%' || @search || '%')
              group by f.id, f.name, f.created_at, o.vendor_ids, o.offering_count
            ) select id,name,created_at,vendor_ids,offering_count,mentions,likes,dislikes,wants,vendor_count from stats;
            """;
        await using var connection = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, connection); AddFilters(cmd, projectId, query.Filters); cmd.Parameters.Add(new NpgsqlParameter("search", NpgsqlDbType.Text) { Value = (object?)query.Search ?? DBNull.Value });
        var rows = new List<FeatureRow>(); await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadFeatureRow(reader));
        var facets = new FeatureFacetsDto(rows.Count, rows.Count(IsRequested), rows.Count(IsGap), rows.Count(IsPain));
        var selected = query.View switch { "requested" => rows.Where(IsRequested), "gap" => rows.Where(IsGap), "pain" => rows.Where(IsPain), _ => rows };
        selected = query.Sort switch { "newest" => selected.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id), "name" => selected.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id), _ => selected.OrderByDescending(x => x.Mentions).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id) };
        var all = selected.ToList(); if (query.Limit is int limit) all = all.Take(limit).ToList();
        return new FeatureListResult(all.Select(ToListItem).ToList(), selected.Count(), facets);
    }

    public async Task<FeatureDto?> GetAsync(string projectId, string featureId, CancellationToken ct)
    {
        var row = await GetRowAsync(projectId, featureId, null, ct);
        return row is null ? null : new FeatureDto(row.Id, row.Name, Coverage(row), row.VendorIds, row.Mentions, new(row.Likes,row.Dislikes,row.Wants), row.CreatedAt);
    }

    public async Task<AiSummaryDto?> GetSummaryAsync(string projectId, string featureId, CancellationToken ct)
    {
        const string sql = """select fs.text, coalesce(fs.based_on,0), fs.generated_at from features f left join feature_summaries fs on fs.feature_id=f.id where f.project_id=@projectId and f.id=@featureId""";
        await using var c=await OpenAsync(ct); await using var cmd=new NpgsqlCommand(sql,c); cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.AddWithValue("featureId",featureId);
        await using var r=await cmd.ExecuteReaderAsync(ct); if(!await r.ReadAsync(ct)) return null;
        return new AiSummaryDto(r.IsDBNull(0)?null:r.GetString(0),r.GetInt32(1),r.IsDBNull(2)?null:r.GetFieldValue<DateTimeOffset>(2));
    }

    public async Task<FeatureResponseListResult?> GetResponsesAsync(string projectId, string featureId, FeatureResponseQuery query, CancellationToken ct)
    {
        if (!await ExistsAsync(projectId, featureId, ct)) return null;
        const string sql = """
          with matches as (select distinct r.id from responses r join mentions m on m.response_id=r.id where r.project_id=@projectId and m.feature_id=@featureId
            and (@kind is null or m.kind=@kind) and (@from is null or r.submitted_at>=@from) and (@toExclusive is null or r.submitted_at<@toExclusive) and (@group is null or r.respondent_group=@group))
          select r.id,r.survey_id,r.respondent_group,v.id,v.name,r.submitted_at,r.likes,r.dislikes,r.wants,r.analyzed_at,
            array_agg(m.kind order by m.kind) filter(where m.id is not null) kinds, count(*) over()::int total
          from matches x join responses r on r.id=x.id join vendors v on v.id=r.vendor_id join mentions m on m.response_id=r.id and m.feature_id=@featureId
          group by r.id,r.survey_id,r.respondent_group,v.id,v.name,r.submitted_at,r.likes,r.dislikes,r.wants,r.analyzed_at order by r.submitted_at desc,r.id desc limit @limit
          """;
        await using var c=await OpenAsync(ct); await using var cmd=new NpgsqlCommand(sql,c); AddFilters(cmd,projectId,query.Filters);cmd.Parameters.AddWithValue("featureId",featureId);cmd.Parameters.AddWithValue("kind",(object?)query.Kind??DBNull.Value);cmd.Parameters.AddWithValue("limit",query.Limit??500);
        var items=new List<FeatureResponseDto>(); var total=0; await using var r=await cmd.ExecuteReaderAsync(ct); while(await r.ReadAsync(ct)){ total=r.GetInt32(11);items.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),new(r.GetString(3),r.GetString(4)),r.GetFieldValue<DateTimeOffset>(5),r.GetString(6),r.GetString(7),r.GetString(8),!r.IsDBNull(9),r.GetFieldValue<string[]>(10))); } return new(items,total);
    }

    public async Task<FeatureVendorListResult?> GetVendorsAsync(string projectId, string featureId, CancellationToken ct)
    {
        if (!await ExistsAsync(projectId, featureId, ct)) return null;
        const string sql="""select v.id,v.name,exists(select 1 from vendor_offerings vo where vo.vendor_id=v.id and vo.feature_id=@featureId), count(m.id) filter(where m.kind='like')::int,count(m.id) filter(where m.kind='dislike')::int,count(m.id) filter(where m.kind='want')::int from vendors v left join responses r on r.vendor_id=v.id left join mentions m on m.response_id=r.id and m.feature_id=@featureId where v.project_id=@projectId group by v.id,v.name order by exists(select 1 from vendor_offerings vo where vo.vendor_id=v.id and vo.feature_id=@featureId) desc,v.name""";
        await using var c=await OpenAsync(ct);await using var cmd=new NpgsqlCommand(sql,c);cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.AddWithValue("featureId",featureId);var a=new List<FeatureVendorDto>();await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))a.Add(new(new(r.GetString(0),r.GetString(1)),r.GetBoolean(2),new(r.GetInt32(3),r.GetInt32(4),r.GetInt32(5))));return new(a,a.Count);
    }

    public async Task<RelatedFeatureListResult?> GetRelatedAsync(string projectId, string featureId, int? limit, CancellationToken ct)
    {
        if (!await ExistsAsync(projectId, featureId, ct)) return null;
        const string sql="""with related as (select m2.feature_id,count(distinct m2.response_id)::int shared from mentions m1 join mentions m2 on m2.response_id=m1.response_id and m2.feature_id<>m1.feature_id join responses r on r.id=m1.response_id where r.project_id=@projectId and m1.feature_id=@featureId group by m2.feature_id), coverage as (select f.id,count(vo.vendor_id)::int offers,(select count(*) from vendors where project_id=@projectId)::int vendors from features f left join vendor_offerings vo on vo.feature_id=f.id group by f.id) select f.id,f.name,case when c.offers=0 then 'none' when c.offers=c.vendors then 'all' else 'some' end,r.shared,count(*) over()::int total from related r join features f on f.id=r.feature_id join coverage c on c.id=f.id order by r.shared desc,f.name limit @limit""";
        await using var c=await OpenAsync(ct);await using var cmd=new NpgsqlCommand(sql,c);cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.AddWithValue("featureId",featureId);cmd.Parameters.AddWithValue("limit",limit??500);var a=new List<RelatedFeatureDto>();var total=0;await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct)){total=r.GetInt32(4);a.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt32(3)));}return new(a,total);
    }

    private async Task<FeatureRow?> GetRowAsync(string projectId,string featureId,ResponseFilters? filters,CancellationToken ct)
    {
        const string sql="""with fm as (select m.feature_id,m.kind from mentions m join responses r on r.id=m.response_id where r.project_id=@projectId and (@from is null or r.submitted_at>=@from) and (@toExclusive is null or r.submitted_at<@toExclusive) and (@group is null or r.respondent_group=@group)),o as(select vo.feature_id,array_agg(vo.vendor_id order by vo.vendor_id) ids,count(*) offers from vendor_offerings vo join vendors v on v.id=vo.vendor_id where v.project_id=@projectId group by vo.feature_id) select f.id,f.name,f.created_at,coalesce(o.ids,array[]::text[]),coalesce(o.offers,0)::int,count(fm.feature_id)::int,count(*) filter(where fm.kind='like')::int,count(*) filter(where fm.kind='dislike')::int,count(*) filter(where fm.kind='want')::int,(select count(*) from vendors where project_id=@projectId)::int from features f left join fm on fm.feature_id=f.id left join o on o.feature_id=f.id where f.project_id=@projectId and f.id=@featureId group by f.id,f.name,f.created_at,o.ids,o.offers""";
        await using var c=await OpenAsync(ct);await using var cmd=new NpgsqlCommand(sql,c);AddFilters(cmd,projectId,filters??new(null,null,null));cmd.Parameters.AddWithValue("featureId",featureId);await using var r=await cmd.ExecuteReaderAsync(ct);return await r.ReadAsync(ct)?ReadFeatureRow(r):null;
    }
    private async Task<bool> ExistsAsync(string projectId,string featureId,CancellationToken ct){await using var c=await OpenAsync(ct);await using var cmd=new NpgsqlCommand("select exists(select 1 from features where project_id=@projectId and id=@featureId)",c);cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.AddWithValue("featureId",featureId);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private async Task<bool> ProjectExistsAsync(string projectId,CancellationToken ct){await using var c=await OpenAsync(ct);await using var cmd=new NpgsqlCommand("select exists(select 1 from projects where id=@projectId)",c);cmd.Parameters.AddWithValue("projectId",projectId);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct){var cs=configuration.GetConnectionString("DefaultConnection");if(string.IsNullOrWhiteSpace(cs))throw new InvalidOperationException("Database connection is not configured.");var c=new NpgsqlConnection(cs);await c.OpenAsync(ct);return c;}
    private static void AddFilters(NpgsqlCommand cmd,string projectId,ResponseFilters f){cmd.Parameters.AddWithValue("projectId",projectId);cmd.Parameters.Add(new("from",NpgsqlDbType.TimestampTz){Value=f.From is null?DBNull.Value:new DateTimeOffset(f.From.Value.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)});cmd.Parameters.Add(new("toExclusive",NpgsqlDbType.TimestampTz){Value=f.To is null?DBNull.Value:new DateTimeOffset(f.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)});cmd.Parameters.Add(new("group",NpgsqlDbType.Text){Value=(object?)f.Group??DBNull.Value});}
    private static FeatureRow ReadFeatureRow(NpgsqlDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetFieldValue<DateTimeOffset>(2),r.GetFieldValue<string[]>(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetInt32(7),r.GetInt32(8),r.GetInt32(9));
    private static bool IsRequested(FeatureRow r)=>r.Wants>0; private static bool IsGap(FeatureRow r)=>r.OfferingCount==0; private static bool IsPain(FeatureRow r)=>r.Dislikes>0;
    private static string Coverage(FeatureRow r)=>r.OfferingCount==0?"none":r.OfferingCount==r.VendorCount?"all":"some";
    private static FeatureListItemDto ToListItem(FeatureRow r)=>new(r.Id,r.Name,Coverage(r),r.VendorIds,r.Mentions,new(r.Likes,r.Dislikes,r.Wants));
    private sealed record FeatureRow(string Id,string Name,DateTimeOffset CreatedAt,string[] VendorIds,int OfferingCount,int Mentions,int Likes,int Dislikes,int Wants,int VendorCount);
}
