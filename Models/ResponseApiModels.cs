namespace FeedbackTool.Models;

public sealed record NamedReference(string Id, string Name);

public sealed record ResponseMentionDto(NamedReference Feature, string Kind);

public sealed record ResponseMentionDetailDto(NamedReference Feature, string Kind, string Coverage);

// One written answer from the survey that isn't in likes / dislikes / wants (e.g. "If you rated X low, why?").
public sealed record ResponseAnswerDto(string Question, string Answer);

public sealed record ResponseItemDto(
    string Id,
    string SurveyId,
    string Group,
    NamedReference Vendor,
    DateTimeOffset SubmittedAt,
    string Likes,
    string Dislikes,
    string Wants,
    bool Analyzed,
    IReadOnlyList<ResponseMentionDto> Mentions,
    bool IncludedInAnalysis = true,
    string? ExclusionReason = null,
    DateTimeOffset? ExcludedAt = null,
    NamedReference? ExcludedBy = null);

public sealed record GroupCountsDto(int Pm, int Sponsor, int Other);

public sealed record StatusCountsDto(int Analyzed, int Pending);

public sealed record ResponseFacetsDto(GroupCountsDto Group, StatusCountsDto Status);

public sealed record ResponseListDto(IReadOnlyList<ResponseItemDto> Items, int Total, ResponseFacetsDto Facets);

public sealed record ResponseDetailDto(
    string Id,
    string SurveyId,
    string Group,
    NamedReference Vendor,
    DateTimeOffset SubmittedAt,
    string Likes,
    string Dislikes,
    string Wants,
    bool Analyzed,
    NamedReference Survey,
    IReadOnlyList<ResponseMentionDetailDto> Mentions,
    bool RereadQueued = false, // an admin asked the AI to read it again and it hasn't yet
    IReadOnlyList<ResponseAnswerDto>? Answers = null, // the survey's other answers; the classifier reads these too
    bool IncludedInAnalysis = true,
    string? ExclusionReason = null,
    DateTimeOffset? ExcludedAt = null,
    NamedReference? ExcludedBy = null);

public sealed class ResponseListQuery
{
    public string? VendorId { get; init; }
    public string? SurveyId { get; init; }
    public string? Status { get; init; }
    public string? Q { get; init; }
    public string Sort { get; init; } = "newest";
    public int? Limit { get; init; }
    public int Offset { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public string? Group { get; init; }
    public bool IncludeExcluded { get; init; }
}
