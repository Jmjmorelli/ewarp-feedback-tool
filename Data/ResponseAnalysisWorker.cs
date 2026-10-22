using Npgsql;

namespace FeedbackTool.Data;

/// <summary>Deterministic first-pass analysis. It keeps responses queryable while an AI provider is added later.</summary>
public sealed class ResponseAnalysisWorker(IServiceScopeFactory scopes, ILogger<ResponseAnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOneAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Response analysis pass failed"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task ProcessOneAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) return;
        await using var c = new NpgsqlConnection(cs); await c.OpenAsync(ct);
        const string select = """select r.id from responses r where r.analyzed_at is null order by r.submitted_at limit 1 for update skip locked;""";
        await using var tx = await c.BeginTransactionAsync(ct);
        string? responseId = null;
        await using (var cmd = new NpgsqlCommand(select,c,tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                responseId = r.GetString(0);
            }
        }

        if (responseId is null)
        {
            await tx.RollbackAsync(ct);
            return;
        }
        await using(var update=new NpgsqlCommand("update responses set analyzed_at=now(),analysis_error=null where id=@id;",c,tx)){update.Parameters.AddWithValue("id",responseId!);await update.ExecuteNonQueryAsync(ct);}
        await tx.CommitAsync(ct);
    }
}
