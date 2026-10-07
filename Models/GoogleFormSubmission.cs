using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace FeedbackTool.Models;

public sealed class GoogleFormSubmission
{
    [Required]
    public string Id { get; init; } = string.Empty;

    [Required]
    public string Form { get; init; } = string.Empty;

    [Required]
    public DateTimeOffset? SubmittedAt { get; init; }

    public JsonElement Answers { get; init; }
}
