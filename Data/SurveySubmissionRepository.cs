using FeedbackTool.Models;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace FeedbackTool.Data;

public sealed class SurveySubmissionRepository(IConfiguration configuration) : ISurveySubmissionRepository
{
    public async Task SaveAsync(GoogleFormSubmission submission, CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Database connection is not configured.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            insert into survey_submissions (id, form, submitted_at, answers)
            values (@id, @form, @submittedAt, @answers)
            on conflict (id) do nothing;
            """, connection);

        command.Parameters.AddWithValue("id", submission.Id);
        command.Parameters.AddWithValue("form", submission.Form);
        command.Parameters.AddWithValue("submittedAt", submission.SubmittedAt!.Value);
        command.Parameters.AddWithValue("answers", NpgsqlDbType.Jsonb, submission.Answers.GetRawText());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SurveySubmission>> GetAllAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Database connection is not configured.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            select id, form, submitted_at, answers, received_at
            from survey_submissions
            order by submitted_at desc;
            """, connection);

        var submissions = new List<SurveySubmission>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            using var answers = JsonDocument.Parse(reader.GetString(3));
            submissions.Add(new SurveySubmission(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                answers.RootElement.Clone(),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return submissions;
    }
}
