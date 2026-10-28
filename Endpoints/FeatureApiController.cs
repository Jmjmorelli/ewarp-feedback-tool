using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[ApiController]
[Route("api/v1/projects/{projectId}/features")]
public sealed class FeatureApiController(IFeatureReadRepository features) : ControllerBase
{
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
    private static bool ValidLimit(int? x)=>x is null or >=1 and <=500;
    private static bool OneOf(string? value,params string[] allowed)=>value is null||allowed.Contains(value,StringComparer.Ordinal);
    private static bool ValidFilters(DateOnly? from,DateOnly? to,string? group)=> (from is null||to is null||from<=to)&&RespondentGroups.IsValid(group);
}
