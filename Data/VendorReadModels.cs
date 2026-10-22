namespace FeedbackTool.Data;

public sealed record VendorFilter(DateOnly? From, DateOnly? To, string? Group);
public sealed record SentimentCounts(int Like, int Dislike, int Want);
public sealed record NamedRef(string Id, string Name);
public sealed record VendorListItem(string Id, string Name, int ResponseCount, int RequestsMet, int RequestsTotal, int FeatureCount, int ExclusiveCount, SentimentCounts Sentiment);
public sealed record VendorListResult(IReadOnlyList<VendorListItem> Items, int Total);
public sealed record VendorDetail(string Id, string Name, int ResponseCount, int RequestsMet, int RequestsTotal, int FeatureCount, int ExclusiveCount, SentimentCounts Sentiment, IReadOnlyList<string> FeatureIds);
public sealed record AiSummary(string? Text, int BasedOn, DateTimeOffset? GeneratedAt);
public sealed record VendorRequest(NamedRef Feature, bool Offers, int Wants);
public sealed record VendorRequestList(IReadOnlyList<VendorRequest> Items, int Total);
public sealed record VendorFeature(string Id, string Name, bool Exclusive, SentimentCounts Sentiment);
public sealed record VendorFeatureList(IReadOnlyList<VendorFeature> Items, int Total);
public sealed record ResponseMention(NamedRef Feature, string Kind);
public sealed record ResponseWithMentions(string Id, string SurveyId, string Group, NamedRef Vendor, DateTimeOffset SubmittedAt, string Likes, string Dislikes, string Wants, bool Analyzed, IReadOnlyList<ResponseMention> Mentions);
public sealed record ResponseWithMentionsList(IReadOnlyList<ResponseWithMentions> Items, int Total);
