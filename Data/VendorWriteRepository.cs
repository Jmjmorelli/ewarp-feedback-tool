using Npgsql;

namespace FeedbackTool.Data;

public sealed class VendorWriteRepository(IConfiguration configuration, IVendorReadRepository vendorReads) : IVendorWriteRepository
{
    public async Task<VendorWriteResult> CreateAsync(string projectId, string name, string form, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (!await ProjectExistsAsync(connection, projectId, ct)) return VendorWriteResult.Error(404, "not_found", $"Project {projectId} not found");

        await LockAsync(connection, tx, $"vendor-name:{projectId}:{name.ToLowerInvariant()}", ct);
        await LockAsync(connection, tx, $"vendor-form:{form.ToLowerInvariant()}", ct);
        if (await VendorNameExistsAsync(connection, projectId, name, null, ct)) return VendorWriteResult.Error(409, "conflict", $"A vendor named \"{name}\" already exists in this project.");
        if (await FormExistsAsync(connection, form, null, ct)) return VendorWriteResult.Error(409, "conflict", $"The form \"{form}\" is already connected to another vendor.");

        var vendorId = "vnd_" + Guid.NewGuid().ToString("N");
        var surveyId = "srv_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, tx, "insert into vendors (id, project_id, name) values (@id,@projectId,@name)", ct, ("id", vendorId), ("projectId", projectId), ("name", name));
        await ExecuteAsync(connection, tx, "insert into surveys (id, project_id, name, source, status) values (@id,@projectId,@name,'google_forms','collecting')", ct, ("id", surveyId), ("projectId", projectId), ("name", name + " feedback"));
        await ExecuteAsync(connection, tx, "insert into form_configurations (form, project_id, survey_id, vendor_id) values (@form,@projectId,@surveyId,@vendorId)", ct, ("form", form), ("projectId", projectId), ("surveyId", surveyId), ("vendorId", vendorId));
        await ImportSubmissionsAsync(connection, tx, projectId, surveyId, vendorId, form, ct);
        await tx.CommitAsync(ct);
        return VendorWriteResult.Success(201, await vendorReads.GetAsync(projectId, vendorId, ct));
    }

    public async Task<VendorWriteResult> UpdateAsync(string projectId, string vendorId, string? name, string? form, IReadOnlyList<string>? featureIds, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (!await ProjectExistsAsync(connection, projectId, ct)) return VendorWriteResult.Error(404, "not_found", $"Project {projectId} not found");
        var current = await VendorAsync(connection, projectId, vendorId, ct);
        if (current is null) return MissingVendor(projectId, vendorId);
        var existing = current.Value;

        if (name is not null)
        {
            await LockAsync(connection, tx, $"vendor-name:{projectId}:{name.ToLowerInvariant()}", ct);
            if (await VendorNameExistsAsync(connection, projectId, name, vendorId, ct)) return VendorWriteResult.Error(409, "conflict", $"A vendor named \"{name}\" already exists in this project.");
            await ExecuteAsync(connection, tx, "update vendors set name=@name where id=@vendorId", ct, ("name", name), ("vendorId", vendorId));
        }

        if (form is not null)
        {
            await LockAsync(connection, tx, $"vendor-form:{form.ToLowerInvariant()}", ct);
            if (await FormExistsAsync(connection, form, vendorId, ct)) return VendorWriteResult.Error(409, "conflict", $"The form \"{form}\" is already connected to another vendor.");
            var surveyId = existing.SurveyId;
            if (surveyId is null)
            {
                surveyId = "srv_" + Guid.NewGuid().ToString("N");
                await ExecuteAsync(connection, tx, "insert into surveys (id, project_id, name, source, status) values (@id,@projectId,@name,'google_forms','collecting')", ct, ("id", surveyId), ("projectId", projectId), ("name", (name ?? existing.Name) + " feedback"));
                await ExecuteAsync(connection, tx, "insert into form_configurations (form, project_id, survey_id, vendor_id) values (@form,@projectId,@surveyId,@vendorId)", ct, ("form", form), ("projectId", projectId), ("surveyId", surveyId), ("vendorId", vendorId));
            }
            else
                await ExecuteAsync(connection, tx, "update form_configurations set form=@form where vendor_id=@vendorId", ct, ("form", form), ("vendorId", vendorId));
            await ImportSubmissionsAsync(connection, tx, projectId, surveyId, vendorId, form, ct);
        }

        if (featureIds is not null)
        {
            var ids = featureIds.Distinct(StringComparer.Ordinal).ToArray();
            foreach (var featureId in ids)
                if (!await FeatureExistsAsync(connection, projectId, featureId, ct)) return VendorWriteResult.Error(400, "bad_request", $"Unknown feature ID: {featureId}");
            await ExecuteAsync(connection, tx, "delete from vendor_offerings where vendor_id=@vendorId", ct, ("vendorId", vendorId));
            foreach (var featureId in ids)
                await ExecuteAsync(connection, tx, "insert into vendor_offerings (vendor_id, feature_id) values (@vendorId,@featureId)", ct, ("vendorId", vendorId), ("featureId", featureId));
        }
        await tx.CommitAsync(ct);
        return VendorWriteResult.Success(200, await vendorReads.GetAsync(projectId, vendorId, ct));
    }

    public async Task<VendorWriteResult> DeleteAsync(string projectId, string vendorId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        if (!await ProjectExistsAsync(connection, projectId, ct)) return VendorWriteResult.Error(404, "not_found", $"Project {projectId} not found");
        var current = await VendorAsync(connection, projectId, vendorId, ct);
        if (current is null) return MissingVendor(projectId, vendorId);
        var existing = current.Value;
        var count = await ScalarIntAsync(connection, "select count(*)::int from responses where vendor_id=@vendorId", ct, ("vendorId", vendorId));
        if (count > 0) return VendorWriteResult.Error(409, "conflict", $"{existing.Name} has {count} responses, so it can't be deleted. Move or remove them first.");
        await ExecuteAsync(connection, tx, "delete from vendor_offerings where vendor_id=@vendorId", ct, ("vendorId", vendorId));
        await ExecuteAsync(connection, tx, "delete from form_configurations where vendor_id=@vendorId", ct, ("vendorId", vendorId));
        if (existing.SurveyId is not null)
            await ExecuteAsync(connection, tx, "delete from surveys s where s.id=@surveyId and not exists(select 1 from form_configurations fc where fc.survey_id=s.id) and not exists(select 1 from responses r where r.survey_id=s.id)", ct, ("surveyId", existing.SurveyId));
        await ExecuteAsync(connection, tx, "delete from vendors where id=@vendorId", ct, ("vendorId", vendorId));
        await tx.CommitAsync(ct);
        return VendorWriteResult.Success(204);
    }

    private static VendorWriteResult MissingVendor(string projectId, string vendorId) => VendorWriteResult.Error(404, "not_found", $"Vendor {vendorId} not found in project {projectId}");
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var cs = configuration.GetConnectionString("DefaultConnection"); if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured."); var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return c; }
    private static async Task<bool> ProjectExistsAsync(NpgsqlConnection c, string projectId, CancellationToken ct) => (bool)(await ScalarAsync(c, "select exists(select 1 from projects where id=@projectId)", ct, ("projectId", projectId)) ?? false);
    private static async Task<bool> VendorNameExistsAsync(NpgsqlConnection c, string projectId, string name, string? excluding, CancellationToken ct) => (bool)(await ScalarAsync(c, "select exists(select 1 from vendors where project_id=@projectId and lower(regexp_replace(btrim(name),'\\s+',' ','g'))=lower(@name) and (@excluding='' or id<>@excluding))", ct, ("projectId", projectId), ("name", name), ("excluding", excluding ?? string.Empty)) ?? false);
    private static async Task<bool> FormExistsAsync(NpgsqlConnection c, string form, string? excluding, CancellationToken ct) => (bool)(await ScalarAsync(c, "select exists(select 1 from form_configurations where lower(regexp_replace(btrim(form),'\\s+',' ','g'))=lower(@form) and (@excluding='' or vendor_id<>@excluding))", ct, ("form", form), ("excluding", excluding ?? string.Empty)) ?? false);
    private static async Task<bool> FeatureExistsAsync(NpgsqlConnection c, string projectId, string featureId, CancellationToken ct) => (bool)(await ScalarAsync(c, "select exists(select 1 from features where project_id=@projectId and id=@featureId)", ct, ("projectId", projectId), ("featureId", featureId)) ?? false);
    private static async Task<(string Name, string? SurveyId)?> VendorAsync(NpgsqlConnection c, string projectId, string vendorId, CancellationToken ct) { await using var cmd = Command(c, "select v.name,(select fc.survey_id from form_configurations fc where fc.vendor_id=v.id limit 1) from vendors v where v.project_id=@projectId and v.id=@vendorId", ("projectId", projectId), ("vendorId", vendorId)); await using var r = await cmd.ExecuteReaderAsync(ct); return await r.ReadAsync(ct) ? (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)) : null; }
    private static async Task ImportSubmissionsAsync(NpgsqlConnection c, NpgsqlTransaction tx, string projectId, string surveyId, string vendorId, string form, CancellationToken ct) => await ExecuteAsync(c, tx, """with imported as (insert into responses (id,raw_submission_id,project_id,survey_id,vendor_id,respondent_group,likes,dislikes,wants,submitted_at) select 'rsp_' || ss.id,ss.id,@projectId,@surveyId,@vendorId,case lower(btrim(coalesce(ss.answers->>'What is your role at DIU?',''))) when 'pm/project manager' then 'pm' when 'program manager' then 'pm' when 'project manager' then 'pm' when 'sponsor' then 'sponsor' when 'sponsor''s team' then 'sponsor' else 'other' end,coalesce(ss.answers->>'What do you like about GovSignals?',''),coalesce(ss.answers->>'What don''t you like about GovSignals?',''),coalesce(ss.answers->>'What additions would you suggest for GovSignals?',''),ss.submitted_at from survey_submissions ss where lower(regexp_replace(btrim(ss.form),'\s+',' ','g'))=lower(@form) and not exists(select 1 from responses r where r.raw_submission_id=ss.id) returning id,submitted_at) insert into activity_events (id,project_id,type,occurred_at,response_id) select 'evt_submitted_' || substring(id from 5),@projectId,'response.submitted',submitted_at,id from imported""", ct, ("projectId", projectId), ("surveyId", surveyId), ("vendorId", vendorId), ("form", form));
    private static async Task LockAsync(NpgsqlConnection c, NpgsqlTransaction tx, string key, CancellationToken ct) => await ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext(@key))", ct, ("key", key));
    private static async Task<int> ScalarIntAsync(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object?)[] values) => Convert.ToInt32(await ScalarAsync(c, sql, ct, values));
    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); return await cmd.ExecuteScalarAsync(ct); }
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(ct); }
    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string, object?)[] values) { var cmd = new NpgsqlCommand(sql, c); foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd; }
}
