using Npgsql;

namespace FeedbackTool.Data;

public sealed record FeedbackInput(string Kind, string Group, string? VendorId, string Note);
public sealed record FeedbackEntryDto(string Id, string Kind, string Group, NamedRef? Vendor, string Note, NamedRef? CreatedBy, DateTimeOffset CreatedAt);
public sealed record FeedbackEntryList(IReadOnlyList<FeedbackEntryDto> Items, int Total);

public sealed record FeatureWriteResult(int Status, string? Code, string? Message, object? Body)
{
    public static FeatureWriteResult Success(int status, object? body = null) => new(status, null, null, body);
    public static FeatureWriteResult Error(int status, string code, string message) => new(status, code, message, null);
}

public interface IFeatureWriteRepository
{
    Task<FeatureWriteResult> CreateAsync(string projectId, string name, IReadOnlyList<string> vendorIds, IReadOnlyList<FeedbackInput> feedback, string userId, CancellationToken cancellationToken);
    Task<FeatureWriteResult> UpdateAsync(string projectId, string featureId, string? name, IReadOnlyList<string>? vendorIds, CancellationToken cancellationToken);
    Task<FeatureWriteResult> DeleteAsync(string projectId, string featureId, CancellationToken cancellationToken);
    Task<FeedbackEntryList?> ListFeedbackAsync(string projectId, string featureId, CancellationToken cancellationToken);
    Task<FeatureWriteResult> AddFeedbackAsync(string projectId, string featureId, FeedbackInput feedback, string userId, CancellationToken cancellationToken);
    Task<FeatureWriteResult> DeleteFeedbackAsync(string projectId, string featureId, string entryId, CancellationToken cancellationToken);
}

// Features an admin adds by hand (source = 'manual'), which vendors offer a feature, and manual likes / dislikes / wants
// (feedback_entries). Same style as VendorWriteRepository: one transaction per call, an advisory lock on the name.
public sealed class FeatureWriteRepository(IConfiguration configuration, IFeatureReadRepository featureReads) : IFeatureWriteRepository
{
    public async Task<FeatureWriteResult> CreateAsync(string projectId, string name, IReadOnlyList<string> vendorIds, IReadOnlyList<FeedbackInput> feedback, string userId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (!await ExistsAsync(c, tx, "select exists(select 1 from projects where id=@projectId)", ct, ("projectId", projectId))) return MissingProject(projectId);
        await LockAsync(c, tx, $"feature-name:{projectId}:{name.ToLowerInvariant()}", ct);
        if (await NameTakenAsync(c, tx, projectId, name, null, ct)) return Taken(name);
        var unknown = await UnknownVendorAsync(c, tx, projectId, vendorIds.Concat(feedback.Select(f => f.VendorId).OfType<string>()), ct);
        if (unknown is not null) return UnknownVendor(unknown);

        var featureId = "ftr_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(c, tx, "insert into features (id, project_id, name, created_at, source) values (@id,@projectId,@name,now(),'manual')", ct, ("id", featureId), ("projectId", projectId), ("name", name));
        foreach (var vendorId in vendorIds.Distinct(StringComparer.Ordinal))
            await AddOfferingAsync(c, tx, vendorId, featureId, ct);
        foreach (var entry in feedback)
            await InsertFeedbackAsync(c, tx, projectId, featureId, entry, userId, ct);
        await tx.CommitAsync(ct);
        return FeatureWriteResult.Success(201, await featureReads.GetAsync(projectId, featureId, ct));
    }

    public async Task<FeatureWriteResult> UpdateAsync(string projectId, string featureId, string? name, IReadOnlyList<string>? vendorIds, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (!await FeatureExistsAsync(c, tx, projectId, featureId, ct)) return MissingFeature(featureId);
        if (name is not null)
        {
            await LockAsync(c, tx, $"feature-name:{projectId}:{name.ToLowerInvariant()}", ct);
            if (await NameTakenAsync(c, tx, projectId, name, featureId, ct)) return Taken(name);
            await ExecuteAsync(c, tx, "update features set name=@name where id=@featureId", ct, ("name", name), ("featureId", featureId));
        }
        if (vendorIds is not null)
        {
            var ids = vendorIds.Distinct(StringComparer.Ordinal).ToArray();
            var unknown = await UnknownVendorAsync(c, tx, projectId, ids, ct);
            if (unknown is not null) return UnknownVendor(unknown);
            // Same bookkeeping as VendorWriteRepository: pairs taken off are remembered so the classifier leaves them off.
            await ExecuteAsync(c, tx, "insert into vendor_offering_removals (vendor_id, feature_id) select vendor_id, feature_id from vendor_offerings where feature_id=@featureId and not (vendor_id=any(@ids)) on conflict do nothing", ct, ("featureId", featureId), ("ids", ids));
            await ExecuteAsync(c, tx, "delete from vendor_offerings where feature_id=@featureId", ct, ("featureId", featureId));
            foreach (var vendorId in ids)
                await AddOfferingAsync(c, tx, vendorId, featureId, ct);
        }
        await tx.CommitAsync(ct);
        return FeatureWriteResult.Success(200, await featureReads.GetAsync(projectId, featureId, ct));
    }

    public async Task<FeatureWriteResult> DeleteAsync(string projectId, string featureId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var source = await ScalarAsync(c, tx, "select source from features where project_id=@projectId and id=@featureId", ct, ("projectId", projectId), ("featureId", featureId)) as string;
        if (source is null) return MissingFeature(featureId);
        if (source != "manual") return FeatureWriteResult.Error(409, "conflict", "This feature came from survey answers, so it can't be deleted.");
        var mentions = Convert.ToInt32(await ScalarAsync(c, tx, "select count(*)::int from mentions where feature_id=@featureId", ct, ("featureId", featureId)));
        if (mentions > 0) return FeatureWriteResult.Error(409, "conflict", $"Survey answers mention this feature {mentions} times, so it can't be deleted.");
        foreach (var table in new[] { "feedback_entries", "vendor_offerings", "vendor_offering_removals", "feature_summaries", "activity_events" })
            await ExecuteAsync(c, tx, $"delete from {table} where feature_id=@featureId", ct, ("featureId", featureId));
        await ExecuteAsync(c, tx, "delete from features where id=@featureId", ct, ("featureId", featureId));
        await tx.CommitAsync(ct);
        return FeatureWriteResult.Success(204);
    }

    public async Task<FeedbackEntryList?> ListFeedbackAsync(string projectId, string featureId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (!await FeatureExistsAsync(c, tx, projectId, featureId, ct)) return null;
        await using var cmd = Command(c, """
            select e.id, e.kind, e.respondent_role, v.id, v.name, e.note, u.id, u.name, e.created_at
            from feedback_entries e left join vendors v on v.id=e.vendor_id left join users u on u.id=e.created_by
            where e.project_id=@projectId and e.feature_id=@featureId order by e.created_at desc, e.id desc
            """, ("projectId", projectId), ("featureId", featureId));
        cmd.Transaction = tx;
        var items = new List<FeedbackEntryDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : new NamedRef(r.GetString(3), r.GetString(4)), r.GetString(5),
                r.IsDBNull(6) ? null : new NamedRef(r.GetString(6), r.GetString(7)), r.GetFieldValue<DateTimeOffset>(8)));
        return new(items, items.Count);
    }

    public async Task<FeatureWriteResult> AddFeedbackAsync(string projectId, string featureId, FeedbackInput feedback, string userId, CancellationToken ct)
    {
        string entryId;
        await using (var c = await OpenAsync(ct))
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            if (!await FeatureExistsAsync(c, tx, projectId, featureId, ct)) return MissingFeature(featureId);
            if (feedback.VendorId is not null && await UnknownVendorAsync(c, tx, projectId, [feedback.VendorId], ct) is { } unknown) return UnknownVendor(unknown);
            entryId = await InsertFeedbackAsync(c, tx, projectId, featureId, feedback, userId, ct);
            await tx.CommitAsync(ct);
        }
        var list = await ListFeedbackAsync(projectId, featureId, ct);
        return FeatureWriteResult.Success(201, list?.Items.FirstOrDefault(x => x.Id == entryId));
    }

    public async Task<FeatureWriteResult> DeleteFeedbackAsync(string projectId, string featureId, string entryId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await using var cmd = Command(c, "delete from feedback_entries where id=@id and feature_id=@featureId and project_id=@projectId", ("id", entryId), ("featureId", featureId), ("projectId", projectId));
        cmd.Transaction = tx;
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) return FeatureWriteResult.Error(404, "not_found", $"Feedback {entryId} not found");
        await tx.CommitAsync(ct);
        return FeatureWriteResult.Success(204);
    }

    // A manual like or dislike about a vendor means the vendor has the feature, the same rule the classifier uses for survey answers.
    private static async Task<string> InsertFeedbackAsync(NpgsqlConnection c, NpgsqlTransaction tx, string projectId, string featureId, FeedbackInput f, string userId, CancellationToken ct)
    {
        var id = "fbk_" + Guid.NewGuid().ToString("N")[..12];
        await ExecuteAsync(c, tx, "insert into feedback_entries (id, project_id, feature_id, vendor_id, kind, respondent_role, respondent_group, note, created_by) values (@id,@projectId,@featureId,@vendorId,@kind,@role,@group,@note,@userId)", ct,
            ("id", id), ("projectId", projectId), ("featureId", featureId), ("vendorId", f.VendorId), ("kind", f.Kind), ("role", f.Group), ("group", RespondentGroups.Legacy(f.Group)), ("note", f.Note), ("userId", userId));
        if (f.VendorId is not null && f.Kind is "like" or "dislike")
            await AddOfferingAsync(c, tx, f.VendorId, featureId, ct);
        return id;
    }

    private static async Task AddOfferingAsync(NpgsqlConnection c, NpgsqlTransaction tx, string vendorId, string featureId, CancellationToken ct)
    {
        await ExecuteAsync(c, tx, "delete from vendor_offering_removals where vendor_id=@vendorId and feature_id=@featureId", ct, ("vendorId", vendorId), ("featureId", featureId));
        await ExecuteAsync(c, tx, "insert into vendor_offerings (vendor_id, feature_id) select @vendorId, @featureId where not exists (select 1 from vendor_offerings where vendor_id=@vendorId and feature_id=@featureId)", ct, ("vendorId", vendorId), ("featureId", featureId));
    }

    private static async Task<string?> UnknownVendorAsync(NpgsqlConnection c, NpgsqlTransaction tx, string projectId, IEnumerable<string> vendorIds, CancellationToken ct)
    {
        foreach (var vendorId in vendorIds.Distinct(StringComparer.Ordinal))
            if (!await ExistsAsync(c, tx, "select exists(select 1 from vendors where project_id=@projectId and id=@vendorId)", ct, ("projectId", projectId), ("vendorId", vendorId)))
                return vendorId;
        return null;
    }

    private static Task<bool> NameTakenAsync(NpgsqlConnection c, NpgsqlTransaction tx, string projectId, string name, string? excluding, CancellationToken ct) =>
        ExistsAsync(c, tx, "select exists(select 1 from features where project_id=@projectId and lower(regexp_replace(btrim(name),'\\s+',' ','g'))=lower(@name) and (@excluding='' or id<>@excluding))", ct, ("projectId", projectId), ("name", name), ("excluding", excluding ?? string.Empty));

    private static Task<bool> FeatureExistsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string projectId, string featureId, CancellationToken ct) =>
        ExistsAsync(c, tx, "select exists(select 1 from features where project_id=@projectId and id=@featureId)", ct, ("projectId", projectId), ("featureId", featureId));

    private static FeatureWriteResult MissingProject(string projectId) => FeatureWriteResult.Error(404, "not_found", $"Project {projectId} not found");
    private static FeatureWriteResult MissingFeature(string featureId) => FeatureWriteResult.Error(404, "not_found", $"Feature {featureId} not found");
    private static FeatureWriteResult Taken(string name) => FeatureWriteResult.Error(409, "conflict", $"A feature named \"{name}\" already exists in this project.");
    private static FeatureWriteResult UnknownVendor(string vendorId) => FeatureWriteResult.Error(400, "bad_request", $"Unknown vendor ID: {vendorId}");
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var cs = configuration.GetConnectionString("DefaultConnection"); if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured."); var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return c; }
    private static async Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, CancellationToken ct) => await ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext(@key))", ct, ("key", key));
    private static async Task<bool> ExistsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) => (bool)(await ScalarAsync(c, tx, sql, ct, values) ?? false);
    private static async Task<object?> ScalarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; return await cmd.ExecuteScalarAsync(ct); }
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(ct); }
    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string, object?)[] values) { var cmd = new NpgsqlCommand(sql, c); foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd; }
}
