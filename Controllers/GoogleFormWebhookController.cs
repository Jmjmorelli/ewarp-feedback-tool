using FeedbackTool.Data;
using FeedbackTool.Models;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Controllers;

[ApiController]
[Route("webhook/google-form")]
public sealed class GoogleFormWebhookController(ISurveySubmissionRepository submissions) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(
        [FromBody] GoogleFormSubmission submission,
        CancellationToken cancellationToken)
    {
        if (submission.Answers.ValueKind == System.Text.Json.JsonValueKind.Undefined)
        {
            ModelState.AddModelError(nameof(submission.Answers), "The answers field is required.");
            return ValidationProblem(ModelState);
        }

        await submissions.SaveAsync(submission, cancellationToken);

        return Ok(new
        {
            success = true,
            message = "Webhook received and saved"
        });
    }
}
