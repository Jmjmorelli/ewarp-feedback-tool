using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace FeedbackTool.Data;

/// <summary>Deterministic first-pass analysis. It keeps responses queryable while an AI provider is added later.</summary>
public sealed class ResponseAnalysisWorker(IServiceScopeFactory scopes, ILogger<ResponseAnalysisWorker> logger) : BackgroundService
{
    private static readonly (string Question, string Feature)[] RatedQuestions =
    [
        ("How would you rate GovSignals' data sync with DIU information?", "DIU data sync"),
        ("How would you rate GovSignals' ability to perform market research?", "Market research"),
        ("How would you rate GovSignals' CSO workflow and pipeline visualization?", "CSO workflow and pipeline visualization"),
        ("How would you rate GovSignals' Salesforce and Google Drive integration?", "Salesforce and Google Drive integration"),
        ("How would you rate GovSignals' ability to automate processes and tasks?", "Process automation"),
        ("How would you rate GovSignals' tracking of a project's lifetime and history?", "Project history tracking"),
        ("How would you rate GovSignals' ability for collaboration with project vendors?", "Vendor collaboration"),
        ("How would you rate GovSignals' ability to generate solicitation and proposal forms?", "Solicitation and proposal forms"),
        ("How would you rate GovSignals' role-based permissions and controls on the platform?", "Role-based permissions"),
        ("How would you rate GovSignals' ability to load finances from ledgers and record them in projects?", "Financial ledger imports"),
        ("How would you rate GovSignals' ability to load data from documents, and import them into the system?", "Document data imports"),
        ("How would you rate GovSignals' ability to generate documents and excel sheets for congressional reporting?", "Congressional reporting exports")
    ];

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
        const string select = """select r.id,r.project_id,s.answers from responses r join survey_submissions s on s.id=r.raw_submission_id where r.analyzed_at is null order by r.submitted_at limit 1 for update skip locked;""";
        await using var tx = await c.BeginTransactionAsync(ct);
        string? responseId = null; string? projectId = null; JsonDocument? answers = null;
        await using (var cmd = new NpgsqlCommand(select,c,tx))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                responseId = r.GetString(0);
                projectId = r.GetString(1);
                answers = JsonDocument.Parse(r.GetString(2));
            }
        }

        if (responseId is null || projectId is null || answers is null)
        {
            await tx.RollbackAsync(ct);
            return;
        }
        var mentions = new List<(string Feature, string Kind)>();
        AddText(answers.RootElement,"What do you like about GovSignals?","General positive feedback","like",mentions);
        AddText(answers.RootElement,"What don't you like about GovSignals?","General negative feedback","dislike",mentions);
        AddText(answers.RootElement,"What additions would you suggest for GovSignals?","Requested additions","want",mentions);
        foreach(var (question,feature) in RatedQuestions) if(answers.RootElement.TryGetProperty(question,out var v) && v.ValueKind==JsonValueKind.String && int.TryParse(v.GetString(),out var score)) { if(score<=2) mentions.Add((feature,"dislike")); else if(score>=4) mentions.Add((feature,"like")); }
        foreach(var (feature,kind) in mentions.Distinct()) { var featureId="ftr_"+Hash(projectId+feature); await using var f=new NpgsqlCommand("insert into features(id,project_id,name) values(@id,@project,@name) on conflict(project_id,name) do nothing; insert into mentions(id,response_id,feature_id,kind) values(@mention,@response,@id,@kind) on conflict(response_id,feature_id,kind) do nothing;",c,tx); f.Parameters.AddWithValue("id",featureId);f.Parameters.AddWithValue("project",projectId!);f.Parameters.AddWithValue("name",feature);f.Parameters.AddWithValue("mention","mtn_"+Hash(responseId+feature+kind));f.Parameters.AddWithValue("response",responseId!);f.Parameters.AddWithValue("kind",kind);await f.ExecuteNonQueryAsync(ct); }
        await using(var update=new NpgsqlCommand("update responses set analyzed_at=now(),analysis_error=null where id=@id;",c,tx)){update.Parameters.AddWithValue("id",responseId!);await update.ExecuteNonQueryAsync(ct);}
        if (mentions.Count > 0) await using(var activity=new NpgsqlCommand("insert into activity_events(id,project_id,type,occurred_at,response_id,feature_ids) values(@event,@project,'features.linked',now(),@id,@features) on conflict(id) do nothing;",c,tx)){activity.Parameters.AddWithValue("id",responseId!);activity.Parameters.AddWithValue("event","evt_linked_"+Hash(responseId!));activity.Parameters.AddWithValue("project",projectId!);activity.Parameters.AddWithValue("features",JsonSerializer.Serialize(mentions.Select(x=>"ftr_"+Hash(projectId+x.Feature)).Distinct()));await activity.ExecuteNonQueryAsync(ct);}
        answers.Dispose(); await tx.CommitAsync(ct);
    }
    private static void AddText(JsonElement answers,string question,string feature,string kind,List<(string Feature,string Kind)> target) { if(answers.TryGetProperty(question,out var v)&&v.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(v.GetString())) target.Add((feature,kind)); }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..20];
}
