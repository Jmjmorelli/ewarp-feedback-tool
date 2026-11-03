using Npgsql;

namespace FeedbackTool.Data;

public sealed record GroupWeightDto(string Key, string Label, double Weight);
public sealed record FeatureWeightDto(string Id, string Name, string Source, double Weight);
public sealed record WeightsDto(IReadOnlyList<GroupWeightDto> Groups, IReadOnlyList<FeatureWeightDto> Features);
public sealed record WeightUpdate(IReadOnlyList<(string Key, double Weight)>? Groups, IReadOnlyList<(string Id, double Weight)>? Features);

// One feature's part in a vendor's score. Demand, likes and dislikes are group-weighted counts.
public sealed record ScoreLineDto(NamedRef Feature, double Weight, double Demand, bool Offers, double Likes, double Dislikes, double Contribution);
public sealed record VendorScoreDto(NamedRef Vendor, int Score, double Coverage, double Satisfaction, IReadOnlyList<ScoreLineDto> Breakdown);
public sealed record ScoringMixDto(double Coverage, double Satisfaction);
public sealed record VendorScoreList(IReadOnlyList<VendorScoreDto> Items, int Total, ScoringMixDto Mix);

// Vendor scores (0-100) from what each vendor offers, what people want, and what its own users like and dislike.
// Every like / dislike / want (survey or manual) counts its group's weight; every feature counts its own weight.
//   demand(f)       = sum of group weights over f's wants, project-wide
//   coverage(v)     = sum(fw * demand * offers) / sum(fw * demand)              0..1, the share of weighted demand v meets
//   satisfaction(v) = sum(fw * gw * (like - dislike)) / (sum(fw * gw * (like + dislike)) + NeutralVotes), over the features v offers, -1..1
//   score(v)        = 100 * (CoverageShare * coverage + SatisfactionShare * (satisfaction + 1) / 2)
// No demand at all counts as full coverage. Every vendor starts with NeutralVotes neutral votes, so a little feedback
// moves satisfaction a little, more feedback moves it further, and feature and group weights matter even for one feature.
public sealed class ScoringRepository(IConfiguration configuration)
{
    public const double CoverageShare = 0.7;
    public const double SatisfactionShare = 0.3;
    public const double NeutralVotes = 3;

    public async Task<WeightsDto?> GetWeightsAsync(string projectId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        if (!await ProjectExistsAsync(c, projectId, ct)) return null;
        var groupWeights = await GroupWeightsAsync(c, projectId, ct);
        var groups = RespondentGroups.All.Select(key => new GroupWeightDto(key, RespondentGroups.Labels[key], groupWeights.GetValueOrDefault(key, 1))).ToList();
        var features = (await FeaturesAsync(c, projectId, ct)).Select(f => new FeatureWeightDto(f.Id, f.Name, f.Source, f.Weight)).ToList();
        return new(groups, features);
    }

    // Found = false when the project does not exist; UnknownFeature = the first feature ID not in the project (nothing is saved then).
    public async Task<(bool Found, string? UnknownFeature)> UpdateWeightsAsync(string projectId, WeightUpdate update, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        if (!await ProjectExistsAsync(c, projectId, ct)) return (false, null);
        foreach (var (key, weight) in update.Groups ?? [])
            await ExecuteAsync(c, tx, "insert into group_weights (project_id, role_key, weight) values (@projectId,@key,@weight) on conflict (project_id, role_key) do update set weight=excluded.weight", ct, ("projectId", projectId), ("key", key), ("weight", (decimal)weight));
        foreach (var (id, weight) in update.Features ?? [])
            if (await ExecuteAsync(c, tx, "update features set weight=@weight where project_id=@projectId and id=@id", ct, ("weight", (decimal)weight), ("projectId", projectId), ("id", id)) == 0)
                return (true, id);
        await tx.CommitAsync(ct);
        return (true, null);
    }

    public async Task<VendorScoreList?> GetScoresAsync(string projectId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        if (!await ProjectExistsAsync(c, projectId, ct)) return null;
        var groupWeights = await GroupWeightsAsync(c, projectId, ct);
        var features = await FeaturesAsync(c, projectId, ct);

        var vendors = new List<NamedRef>();
        await using (var cmd = Command(c, "select id, name from vendors where project_id=@projectId order by name, id", ("projectId", projectId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) vendors.Add(new(r.GetString(0), r.GetString(1)));

        var offers = new HashSet<(string Vendor, string Feature)>();
        await using (var cmd = Command(c, "select vo.vendor_id, vo.feature_id from vendor_offerings vo join vendors v on v.id=vo.vendor_id where v.project_id=@projectId", ("projectId", projectId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) offers.Add((r.GetString(0), r.GetString(1)));

        // Group-weighted counts: wants per feature, and likes / dislikes per (vendor, feature).
        var demand = new Dictionary<string, double>();
        var likes = new Dictionary<(string, string), double>();
        var dislikes = new Dictionary<(string, string), double>();
        await using (var cmd = Command(c, "select feature_id, kind, vendor_id, respondent_role, count(*)::int from feedback_mentions where project_id=@projectId group by 1, 2, 3, 4", ("projectId", projectId)))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var (featureId, kind, vendorId) = (r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
                var weighted = r.GetInt32(4) * groupWeights.GetValueOrDefault(r.IsDBNull(3) ? RespondentGroups.Other : r.GetString(3), 1);
                if (kind == "want") demand[featureId] = demand.GetValueOrDefault(featureId) + weighted;
                else if (vendorId is not null) Add(kind == "like" ? likes : dislikes, (vendorId, featureId), weighted);
            }

        var items = vendors.Select(vendor => Score(vendor, features, offers, demand, likes, dislikes)).OrderByDescending(x => x.Score).ThenBy(x => x.Vendor.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new(items, items.Count, new(CoverageShare, SatisfactionShare));
    }

    private static VendorScoreDto Score(NamedRef vendor, IReadOnlyList<FeatureRow> features, HashSet<(string, string)> offers, Dictionary<string, double> demand,
        Dictionary<(string, string), double> likes, Dictionary<(string, string), double> dislikes)
    {
        double totalDemand = 0, metDemand = 0, net = 0, volume = 0;
        foreach (var f in features)
        {
            var offered = offers.Contains((vendor.Id, f.Id));
            totalDemand += f.Weight * demand.GetValueOrDefault(f.Id);
            if (!offered) continue;
            metDemand += f.Weight * demand.GetValueOrDefault(f.Id);
            net += f.Weight * (likes.GetValueOrDefault((vendor.Id, f.Id)) - dislikes.GetValueOrDefault((vendor.Id, f.Id)));
            volume += f.Weight * (likes.GetValueOrDefault((vendor.Id, f.Id)) + dislikes.GetValueOrDefault((vendor.Id, f.Id)));
        }
        var coverage = totalDemand > 0 ? metDemand / totalDemand : 1;
        var satisfaction = net / (volume + NeutralVotes);
        var score = (int)Math.Round(100 * (CoverageShare * coverage + SatisfactionShare * (satisfaction + 1) / 2));

        // Points each feature adds on top of the neutral baseline (a met want adds, a missed want adds nothing, a dislike takes away).
        var lines = new List<ScoreLineDto>();
        foreach (var f in features)
        {
            var offered = offers.Contains((vendor.Id, f.Id));
            var want = demand.GetValueOrDefault(f.Id);
            var like = offered ? likes.GetValueOrDefault((vendor.Id, f.Id)) : 0;
            var dislike = offered ? dislikes.GetValueOrDefault((vendor.Id, f.Id)) : 0;
            if (want == 0 && like == 0 && dislike == 0) continue;
            var points = (totalDemand > 0 && offered ? 100 * CoverageShare * f.Weight * want / totalDemand : 0)
                         + 100 * SatisfactionShare / 2 * f.Weight * (like - dislike) / (volume + NeutralVotes);
            lines.Add(new(new(f.Id, f.Name), f.Weight, want, offered, like, dislike, Math.Round(points, 1)));
        }
        lines.Sort((a, b) => Math.Abs(b.Contribution).CompareTo(Math.Abs(a.Contribution)));
        return new(vendor, score, Math.Round(coverage, 3), Math.Round(satisfaction, 3), lines);
    }

    private static void Add(Dictionary<(string, string), double> map, (string, string) key, double value) => map[key] = map.GetValueOrDefault(key) + value;

    private sealed record FeatureRow(string Id, string Name, string Source, double Weight);

    private static async Task<List<FeatureRow>> FeaturesAsync(NpgsqlConnection c, string projectId, CancellationToken ct)
    {
        var features = new List<FeatureRow>();
        await using var cmd = Command(c, "select id, name, source, weight::float8 from features where project_id=@projectId order by lower(name), id", ("projectId", projectId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) features.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        return features;
    }

    private static async Task<Dictionary<string, double>> GroupWeightsAsync(NpgsqlConnection c, string projectId, CancellationToken ct)
    {
        var weights = new Dictionary<string, double>();
        await using var cmd = Command(c, "select role_key, weight::float8 from group_weights where project_id=@projectId", ("projectId", projectId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) weights[r.GetString(0)] = r.GetDouble(1);
        return weights;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var cs = configuration.GetConnectionString("DefaultConnection"); if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured."); var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return c; }
    private static async Task<bool> ProjectExistsAsync(NpgsqlConnection c, string projectId, CancellationToken ct) { await using var cmd = Command(c, "select exists(select 1 from projects where id=@projectId)", ("projectId", projectId)); return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false); }
    private static async Task<int> ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; return await cmd.ExecuteNonQueryAsync(ct); }
    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string, object?)[] values) { var cmd = new NpgsqlCommand(sql, c); foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd; }
}
