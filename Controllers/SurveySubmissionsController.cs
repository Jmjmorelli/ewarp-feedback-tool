using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Controllers;

[ApiController]
public sealed class SurveySubmissionsController(ISurveySubmissionRepository submissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await submissions.GetAllAsync(cancellationToken));
}
