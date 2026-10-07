using System.Text.Json;

namespace FeedbackTool.Models;

public sealed record SurveySubmission(
    string Id,
    string Form,
    DateTimeOffset SubmittedAt,
    JsonElement Answers,
    DateTimeOffset ReceivedAt);
