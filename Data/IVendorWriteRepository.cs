namespace FeedbackTool.Data;

public interface IVendorWriteRepository
{
    Task<VendorWriteResult> CreateAsync(string projectId, string name, string form, CancellationToken cancellationToken);
    Task<VendorWriteResult> UpdateAsync(string projectId, string vendorId, string? name, string? form, IReadOnlyList<string>? featureIds, CancellationToken cancellationToken);
    Task<VendorWriteResult> DeleteAsync(string projectId, string vendorId, CancellationToken cancellationToken);
}

public sealed record VendorWriteResult(int Status, string? Code, string? Message, VendorDetail? Vendor)
{
    public static VendorWriteResult Success(int status, VendorDetail? vendor = null) => new(status, null, null, vendor);
    public static VendorWriteResult Error(int status, string code, string message) => new(status, code, message, null);
}
