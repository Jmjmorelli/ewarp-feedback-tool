using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId}/vendors")]
public sealed class VendorApiController(IVendorReadRepository vendors) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(string projectId, [FromQuery] string? sort, [FromQuery] int? limit, [FromQuery(Name="from")] DateOnly? from, [FromQuery(Name="to")] DateOnly? to, [FromQuery(Name="group")] string? group, CancellationToken ct)
    {
        var error=Validate(sort,limit,from,to,group, true); if(error is not null)return error;
        if(!await vendors.ProjectExistsAsync(projectId,ct))return NotFoundProject(projectId);
        return Ok(await vendors.ListAsync(projectId,new(from,to,group),sort ?? "fit",limit,ct));
    }
    [HttpGet("{vendorId}")]
    public async Task<IActionResult> Get(string projectId,string vendorId,CancellationToken ct) => await vendors.GetAsync(projectId,vendorId,ct) is { } item ? Ok(item) : NotFoundVendor(vendorId);
    [HttpGet("{vendorId}/summary")]
    public async Task<IActionResult> Summary(string projectId,string vendorId,CancellationToken ct) => await vendors.GetSummaryAsync(projectId,vendorId,ct) is { } item ? Ok(item) : NotFoundVendor(vendorId);
    [HttpGet("{vendorId}/requests")]
    public async Task<IActionResult> Requests(string projectId,string vendorId,[FromQuery(Name="from")] DateOnly? from,[FromQuery(Name="to")] DateOnly? to,[FromQuery(Name="group")] string? group,CancellationToken ct)
    { var error=Validate(null,null,from,to,group,false); if(error is not null)return error; return await vendors.GetRequestsAsync(projectId,vendorId,new(from,to,group),ct) is { } item ? Ok(item) : NotFoundVendor(vendorId); }
    [HttpGet("{vendorId}/features")]
    public async Task<IActionResult> Features(string projectId,string vendorId,[FromQuery(Name="from")] DateOnly? from,[FromQuery(Name="to")] DateOnly? to,[FromQuery(Name="group")] string? group,CancellationToken ct)
    { var error=Validate(null,null,from,to,group,false); if(error is not null)return error; return await vendors.GetFeaturesAsync(projectId,vendorId,new(from,to,group),ct) is { } item ? Ok(item) : NotFoundVendor(vendorId); }
    [HttpGet("{vendorId}/responses")]
    public async Task<IActionResult> Responses(string projectId,string vendorId,[FromQuery] int? limit,[FromQuery(Name="from")] DateOnly? from,[FromQuery(Name="to")] DateOnly? to,[FromQuery(Name="group")] string? group,CancellationToken ct)
    { var error=Validate(null,limit,from,to,group,false); if(error is not null)return error; return await vendors.GetResponsesAsync(projectId,vendorId,new(from,to,group),limit,ct) is { } item ? Ok(item) : NotFoundVendor(vendorId); }
    private IActionResult? Validate(string? sort,int? limit,DateOnly? from,DateOnly? to,string? group,bool validateSort) { if(validateSort && sort is not null && sort is not ("fit" or "name"))return Bad("sort must be fit or name"); if(limit is <=0 or >500)return Bad("limit must be between 1 and 500"); if(group is not null && group is not ("pm" or "sponsor" or "other"))return Bad("group must be pm, sponsor, or other"); if(from>to)return Bad("from must be on or before to"); return null; }
    private ObjectResult NotFoundProject(string id)=>NotFound(new {error=new {code="not_found",message=$"Project {id} not found"}});
    private ObjectResult NotFoundVendor(string id)=>NotFound(new {error=new {code="not_found",message=$"Vendor {id} not found"}});
    private ObjectResult Bad(string message)=>BadRequest(new {error=new {code="bad_request",message}});
}
