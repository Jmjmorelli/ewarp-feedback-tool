using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace FeedbackTool.Endpoints;

[ApiController]
[Route("api/v1/projects/{projectId}/vendors")]
public sealed class VendorApiController(IVendorReadRepository vendors, IVendorWriteRepository vendorWrites) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create(string projectId, CancellationToken ct)
    {
        using var body = await ReadBodyAsync(ct);
        if (body is null) return Bad("Enter a vendor name.");
        if (!TryString(body.RootElement, "name", out var rawName)) return Bad("Enter a vendor name.");
        var name = Normalize(rawName);
        if (name.Length is < 1 or > 80) return Bad("Enter a vendor name.");
        var form = TryString(body.RootElement, "form", out var rawForm) ? Normalize(rawForm) : name;
        if (form.Length > 200) return Bad("Form must be 200 characters or fewer.");
        return ToResult(await vendorWrites.CreateAsync(projectId, name, form, ct));
    }

    [HttpPatch("{vendorId}")]
    public async Task<IActionResult> Update(string projectId, string vendorId, CancellationToken ct)
    {
        using var body = await ReadBodyAsync(ct);
        if (body is null) return Bad("Send at least one of name, form or featureIds.");
        var root = body.RootElement;
        var hasName = root.TryGetProperty("name", out var nameElement);
        var hasForm = root.TryGetProperty("form", out var formElement);
        var hasFeatures = root.TryGetProperty("featureIds", out var featuresElement);
        if (!hasName && !hasForm && !hasFeatures) return Bad("Send at least one of name, form or featureIds.");
        string? name = null;
        string? form = null;
        if (hasName)
        {
            if (nameElement.ValueKind != JsonValueKind.String) return Bad("Enter a vendor name.");
            name = Normalize(nameElement.GetString()!);
            if (name.Length is < 1 or > 80) return Bad("Enter a vendor name.");
        }
        if (hasForm)
        {
            if (formElement.ValueKind != JsonValueKind.String) return Bad("Enter a form name.");
            form = Normalize(formElement.GetString()!);
            if (form.Length > 200) return Bad("Form must be 200 characters or fewer.");
        }
        List<string>? featureIds = null;
        if (hasFeatures)
        {
            if (featuresElement.ValueKind != JsonValueKind.Array) return Bad("featureIds must be an array.");
            featureIds = [];
            foreach (var feature in featuresElement.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.String) return Bad("featureIds must be an array of IDs.");
                featureIds.Add(feature.GetString()!);
            }
        }
        return ToResult(await vendorWrites.UpdateAsync(projectId, vendorId, name, form, featureIds, ct));
    }

    [HttpDelete("{vendorId}")]
    public async Task<IActionResult> Delete(string projectId, string vendorId, CancellationToken ct) => ToResult(await vendorWrites.DeleteAsync(projectId, vendorId, ct));

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
    private IActionResult? Validate(string? sort,int? limit,DateOnly? from,DateOnly? to,string? group,bool validateSort) { if(validateSort && sort is not null && sort is not ("fit" or "name"))return Bad("sort must be fit or name"); if(limit is <=0 or >500)return Bad("limit must be between 1 and 500"); if(!Data.RespondentGroups.IsValid(group))return Bad("group must be one of: "+string.Join(", ",Data.RespondentGroups.All)); if(from>to)return Bad("from must be on or before to"); return null; }
    private ObjectResult NotFoundProject(string id)=>NotFound(new {error=new {code="not_found",message=$"Project {id} not found"}});
    private ObjectResult NotFoundVendor(string id)=>NotFound(new {error=new {code="not_found",message=$"Vendor {id} not found"}});
    private ObjectResult Bad(string message)=>BadRequest(new {error=new {code="bad_request",message}});
    private IActionResult ToResult(VendorWriteResult result) => result.Code is null ? result.Status == 204 ? NoContent() : StatusCode(result.Status, result.Vendor) : StatusCode(result.Status, new { error = new { code = result.Code, message = result.Message } });
    private async Task<JsonDocument?> ReadBodyAsync(CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct); }
        catch (JsonException) { return null; }
    }
    private static bool TryString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String && (value = element.GetString() ?? string.Empty) is not null;
    }
    private static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
