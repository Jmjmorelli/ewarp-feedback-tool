using FeedbackTool.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[ApiController]
[Route("api/v1/projects/{projectId}/features")]
public sealed class FeatureApiController(IFeatureReadRepository features, IFeatureWriteRepository writes) : ControllerBase
{
    // Body: { name, vendorIds?: [vendorId], feedback?: [{ kind, group, vendorId?, note? }] }. Adds a manual feature.
    [Authorize(Policy = Policies.Admin)]
    [HttpPost]
    public async Task<IActionResult> Create(string projectId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null || body.RootElement.ValueKind != JsonValueKind.Object) return Bad("Enter a feature name.");
        var root = body.RootElement;
        if (!AuthController.TryString(root, "name", out var rawName) || Normalize(rawName) is not { Length: >= 1 and <= 80 } name) return Bad("Enter a feature name (up to 80 characters).");
        List<string>? vendorIds = [];
        if (root.TryGetProperty("vendorIds", out var vendorsElement) && (vendorIds = ReadIds(vendorsElement)) is null) return Bad("vendorIds must be an array of IDs.");
        List<FeedbackInput> feedback = [];
        if (root.TryGetProperty("feedback", out var feedbackElement))
        {
            if (feedbackElement.ValueKind != JsonValueKind.Array || feedbackElement.GetArrayLength() > 50) return Bad("feedback must be an array of up to 50 entries.");
            foreach (var entry in feedbackElement.EnumerateArray())
            {
                if (ReadFeedback(entry, out var error) is not { } input) return Bad(error);
                feedback.Add(input);
            }
        }
        return ToResult(await writes.CreateAsync(projectId, name, vendorIds ?? [], feedback, User.UserId(), ct));
    }

    // Body: { name?, vendorIds? }. vendorIds replaces the full list of vendors that offer the feature.
    [Authorize(Policy = Policies.Admin)]
    [HttpPatch("{featureId}")]
    public async Task<IActionResult> Update(string projectId, string featureId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null || body.RootElement.ValueKind != JsonValueKind.Object) return Bad("Send at least one of name or vendorIds.");
        var root = body.RootElement;
        string? name = null;
        List<string>? vendorIds = null;
        if (root.TryGetProperty("name", out _))
        {
            if (!AuthController.TryString(root, "name", out var rawName) || Normalize(rawName) is not { Length: >= 1 and <= 80 } normalized) return Bad("Enter a feature name (up to 80 characters).");
            name = normalized;
        }
        if (root.TryGetProperty("vendorIds", out var vendorsElement) && (vendorIds = ReadIds(vendorsElement)) is null) return Bad("vendorIds must be an array of IDs.");
        if (name is null && vendorIds is null) return Bad("Send at least one of name or vendorIds.");
        return ToResult(await writes.UpdateAsync(projectId, featureId, name, vendorIds, ct));
    }

    // Only manual features nobody mentioned in a survey can be deleted.
    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{featureId}")]
    public async Task<IActionResult> Delete(string projectId, string featureId, CancellationToken ct) => ToResult(await writes.DeleteAsync(projectId, featureId, ct));

    [HttpGet("{featureId}/feedback")]
    public async Task<IActionResult> Feedback(string projectId, string featureId, CancellationToken ct) => await writes.ListFeedbackAsync(projectId, featureId, ct) is { } x ? Ok(x) : Missing(featureId);

    // Body: { kind, group, vendorId?, note? }
    [Authorize(Policy = Policies.Admin)]
    [HttpPost("{featureId}/feedback")]
    public async Task<IActionResult> AddFeedback(string projectId, string featureId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null) return Bad("Send kind and group.");
        if (ReadFeedback(body.RootElement, out var error) is not { } input) return Bad(error);
        return ToResult(await writes.AddFeedbackAsync(projectId, featureId, input, User.UserId(), ct));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpDelete("{featureId}/feedback/{entryId}")]
    public async Task<IActionResult> DeleteFeedback(string projectId, string featureId, string entryId, CancellationToken ct) => ToResult(await writes.DeleteFeedbackAsync(projectId, featureId, entryId, ct));

    [HttpGet]
    public async Task<IActionResult> List(string projectId, [FromQuery] string? view, [FromQuery(Name="q")] string? search, [FromQuery] string? sort, [FromQuery] int? limit, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery(Name="group")] string? group, CancellationToken ct)
    {
        if (!ValidFilters(from,to,group) || !OneOf(view,"all","requested","gap","pain") || !OneOf(sort,"mentions","newest","name") || !ValidLimit(limit)) return Bad();
        return await features.ListAsync(projectId,new(view??"all",search,sort??"mentions",limit,new(from,to,group)),ct) is { } result ? Ok(result) : MissingProject(projectId);
    }
    [HttpGet("{featureId}")] public async Task<IActionResult> Get(string projectId,string featureId,CancellationToken ct)=> await features.GetAsync(projectId,featureId,ct) is { } x?Ok(x):Missing(featureId);
    [HttpGet("{featureId}/summary")] public async Task<IActionResult> Summary(string projectId,string featureId,CancellationToken ct)=>await features.GetSummaryAsync(projectId,featureId,ct) is { } x?Ok(x):Missing(featureId);
    [HttpGet("{featureId}/responses")]
    public async Task<IActionResult> Responses(string projectId,string featureId,[FromQuery] string? kind,[FromQuery]int? limit,[FromQuery]DateOnly? from,[FromQuery]DateOnly? to,[FromQuery(Name="group")]string? group,CancellationToken ct)
    {if(!OneOf(kind,"like","dislike","want")||!ValidLimit(limit)||!ValidFilters(from,to,group))return Bad();return await features.GetResponsesAsync(projectId,featureId,new(kind,limit,new(from,to,group)),ct) is {} x?Ok(x):Missing(featureId);}
    [HttpGet("{featureId}/vendors")] public async Task<IActionResult> Vendors(string projectId,string featureId,CancellationToken ct)=>await features.GetVendorsAsync(projectId,featureId,ct) is {} x?Ok(x):Missing(featureId);
    [HttpGet("{featureId}/related")] public async Task<IActionResult> Related(string projectId,string featureId,[FromQuery]int? limit,CancellationToken ct){if(!ValidLimit(limit))return Bad();return await features.GetRelatedAsync(projectId,featureId,limit,ct) is {} x?Ok(x):Missing(featureId);}
    private IActionResult Missing(string id)=>NotFound(new { error=new { code="not_found",message=$"Feature {id} not found" }});
    private IActionResult MissingProject(string id)=>NotFound(new { error=new { code="not_found",message=$"Project {id} not found" }});
    private IActionResult Bad()=>BadRequest(new { error=new { code="bad_request",message="One or more query parameters are invalid" }});
    private IActionResult Bad(string message)=>BadRequest(new { error=new { code="bad_request",message }});
    private IActionResult ToResult(FeatureWriteResult result) => result.Code is null ? result.Status == 204 ? NoContent() : StatusCode(result.Status, result.Body) : StatusCode(result.Status, new { error = new { code = result.Code, message = result.Message } });
    private static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static List<string>? ReadIds(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array) return null;
        var ids = new List<string>();
        foreach (var id in list.EnumerateArray()) { if (id.ValueKind != JsonValueKind.String) return null; ids.Add(id.GetString()!); }
        return ids;
    }
    private static FeedbackInput? ReadFeedback(JsonElement entry, out string error)
    {
        error = "Each feedback entry needs a kind (like, dislike or want) and a group.";
        if (entry.ValueKind != JsonValueKind.Object) return null;
        if (!AuthController.TryString(entry, "kind", out var kind) || kind is not ("like" or "dislike" or "want")) return null;
        if (!AuthController.TryString(entry, "group", out var group) || !RespondentGroups.All.Contains(group)) { error = "group must be one of: " + string.Join(", ", RespondentGroups.All); return null; }
        string? vendorId = null;
        if (entry.TryGetProperty("vendorId", out var v) && v.ValueKind != JsonValueKind.Null)
        {
            if (v.ValueKind != JsonValueKind.String) { error = "vendorId must be an ID or null."; return null; }
            vendorId = v.GetString();
        }
        var note = AuthController.TryString(entry, "note", out var rawNote) ? rawNote.Trim() : string.Empty;
        if (note.Length > 1000) { error = "note must be 1000 characters or fewer."; return null; }
        return new(kind, group, vendorId, note);
    }
    private static bool ValidLimit(int? x)=>x is null or >=1 and <=500;
    private static bool OneOf(string? value,params string[] allowed)=>value is null||allowed.Contains(value,StringComparer.Ordinal);
    private static bool ValidFilters(DateOnly? from,DateOnly? to,string? group)=> (from is null||to is null||from<=to)&&RespondentGroups.IsValid(group);
}
