namespace FeedbackTool.Data;

public interface IVendorReadRepository
{
    Task<bool> ProjectExistsAsync(string projectId, CancellationToken cancellationToken);
    Task<VendorListResult> ListAsync(string projectId, VendorFilter filter, string sort, int? limit, CancellationToken cancellationToken);
    Task<VendorDetail?> GetAsync(string projectId, string vendorId, CancellationToken cancellationToken);
    Task<AiSummary?> GetSummaryAsync(string projectId, string vendorId, CancellationToken cancellationToken);
    Task<VendorRequestList?> GetRequestsAsync(string projectId, string vendorId, VendorFilter filter, CancellationToken cancellationToken);
    Task<VendorFeatureList?> GetFeaturesAsync(string projectId, string vendorId, VendorFilter filter, CancellationToken cancellationToken);
    Task<ResponseWithMentionsList?> GetResponsesAsync(string projectId, string vendorId, VendorFilter filter, int? limit, CancellationToken cancellationToken);
}
