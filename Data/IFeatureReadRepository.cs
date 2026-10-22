namespace FeedbackTool.Data;

public interface IFeatureReadRepository
{
    Task<FeatureListResult?> ListAsync(string projectId, FeatureListQuery query, CancellationToken cancellationToken);
    Task<FeatureDto?> GetAsync(string projectId, string featureId, CancellationToken cancellationToken);
    Task<AiSummaryDto?> GetSummaryAsync(string projectId, string featureId, CancellationToken cancellationToken);
    Task<FeatureResponseListResult?> GetResponsesAsync(string projectId, string featureId, FeatureResponseQuery query, CancellationToken cancellationToken);
    Task<FeatureVendorListResult?> GetVendorsAsync(string projectId, string featureId, CancellationToken cancellationToken);
    Task<RelatedFeatureListResult?> GetRelatedAsync(string projectId, string featureId, int? limit, CancellationToken cancellationToken);
}

public sealed record ResponseFilters(DateOnly? From, DateOnly? To, string? Group);
public sealed record FeatureListQuery(string View, string? Search, string Sort, int? Limit, ResponseFilters Filters);
public sealed record FeatureResponseQuery(string? Kind, int? Limit, ResponseFilters Filters);
public sealed record SentimentDto(int Like, int Dislike, int Want);
public sealed record FeatureListItemDto(string Id, string Name, string Coverage, string[] VendorIds, int Mentions, SentimentDto Sentiment);
public sealed record FeatureDto(string Id, string Name, string Coverage, string[] VendorIds, int Mentions, SentimentDto Sentiment, DateTimeOffset CreatedAt);
public sealed record FeatureListResult(IReadOnlyList<FeatureListItemDto> Items, int Total, FeatureFacetsDto Facets);
public sealed record FeatureFacetsDto(int All, int Requested, int Gap, int Pain);
public sealed record AiSummaryDto(string? Text, int BasedOn, DateTimeOffset? GeneratedAt);
public sealed record NamedRefDto(string Id, string Name);
public sealed record FeatureResponseDto(string Id, string SurveyId, string Group, NamedRefDto Vendor, DateTimeOffset SubmittedAt, string Likes, string Dislikes, string Wants, bool Analyzed, string[] Kinds);
public sealed record FeatureResponseListResult(IReadOnlyList<FeatureResponseDto> Items, int Total);
public sealed record FeatureVendorDto(NamedRefDto Vendor, bool Offers, SentimentDto Sentiment);
public sealed record FeatureVendorListResult(IReadOnlyList<FeatureVendorDto> Items, int Total);
public sealed record RelatedFeatureDto(string Id, string Name, string Coverage, int SharedResponses);
public sealed record RelatedFeatureListResult(IReadOnlyList<RelatedFeatureDto> Items, int Total);
