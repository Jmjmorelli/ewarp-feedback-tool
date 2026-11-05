namespace FeedbackTool.Models;

public sealed class SurveyAnalysisUpdate
{
    public bool? IncludedInAnalysis { get; init; }
    public string? ExclusionReason { get; init; }
}
