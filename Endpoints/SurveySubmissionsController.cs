using FeedbackTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[ApiController]
[Route("survey-submissions")]
public sealed class SurveySubmissionsController(ISurveySubmissionRepository submissions) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken) =>
        Ok(await submissions.GetAllAsync(cancellationToken));
}
