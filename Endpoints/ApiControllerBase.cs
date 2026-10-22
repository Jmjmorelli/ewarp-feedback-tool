using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    protected NotFoundObjectResult NotFoundError(string message) => NotFound(new { error = new { code = "not_found", message } });
    protected BadRequestObjectResult BadRequestError(string message) => BadRequest(new { error = new { code = "bad_request", message } });
}
