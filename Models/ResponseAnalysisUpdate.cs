namespace FeedbackTool.Models;

public sealed class ResponseAnalysisUpdate
{
    public bool? IncludedInAnalysis { get; init; }
    public string? ExclusionReason { get; init; }
}
