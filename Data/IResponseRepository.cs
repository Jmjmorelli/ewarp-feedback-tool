using FeedbackTool.Models;

namespace FeedbackTool.Data;

public interface IResponseRepository
{
    Task<ResponseListDto?> ListAsync(string projectId, ResponseListQuery query, CancellationToken cancellationToken);
    Task<ResponseDetailDto?> GetAsync(string projectId, string responseId, bool includeExcluded, CancellationToken cancellationToken);
}
