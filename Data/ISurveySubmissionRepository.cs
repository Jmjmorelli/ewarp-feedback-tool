using FeedbackTool.Models;

namespace FeedbackTool.Data;

public interface ISurveySubmissionRepository
{
    Task SaveAsync(GoogleFormSubmission submission, CancellationToken cancellationToken);

    Task<IReadOnlyList<SurveySubmission>> GetAllAsync(CancellationToken cancellationToken);
}
