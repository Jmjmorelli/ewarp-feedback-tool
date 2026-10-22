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
            on conflict (id) do nothing
            returning id;
            """, connection);

        command.Parameters.AddWithValue("id", submission.Id);
        command.Parameters.AddWithValue("form", submission.Form);
        command.Parameters.AddWithValue("submittedAt", submission.SubmittedAt!.Value);
        command.Parameters.AddWithValue("answers", NpgsqlDbType.Jsonb, submission.Answers.GetRawText());

        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is null)
            return;

        await using var configCommand = new NpgsqlCommand("""
            select project_id, survey_id, vendor_id
            from form_configurations where form = @form;
            """, connection);
        configCommand.Parameters.AddWithValue("form", submission.Form);
        await using var configReader = await configCommand.ExecuteReaderAsync(cancellationToken);
        if (!await configReader.ReadAsync(cancellationToken))
            return;

        var projectId = configReader.GetString(0);
        var surveyId = configReader.GetString(1);
        var vendorId = configReader.GetString(2);
        await configReader.CloseAsync();

        const string group = "other";
        var likes = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.LikesPrefix);
        var dislikes = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.DislikesPrefix);
        var wants = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.WantsPrefix);
        var responseId = "rsp_" + submission.Id;

        await using var responseCommand = new NpgsqlCommand("""
            insert into responses (id, raw_submission_id, project_id, survey_id, vendor_id, respondent_group, likes, dislikes, wants, submitted_at)
            values (@id, @rawId, @projectId, @surveyId, @vendorId, @group, @likes, @dislikes, @wants, @submittedAt);

            insert into activity_events (id, project_id, type, occurred_at, response_id)
            values (@eventId, @projectId, 'response.submitted', @submittedAt, @id);
            """, connection);
        responseCommand.Parameters.AddWithValue("id", responseId);
        responseCommand.Parameters.AddWithValue("rawId", submission.Id);
        responseCommand.Parameters.AddWithValue("projectId", projectId);
        responseCommand.Parameters.AddWithValue("surveyId", surveyId);
        responseCommand.Parameters.AddWithValue("vendorId", vendorId);
        responseCommand.Parameters.AddWithValue("group", group);
        responseCommand.Parameters.AddWithValue("likes", likes);
        responseCommand.Parameters.AddWithValue("dislikes", dislikes);
        responseCommand.Parameters.AddWithValue("wants", wants);
        responseCommand.Parameters.AddWithValue("submittedAt", submission.SubmittedAt!.Value);
        responseCommand.Parameters.AddWithValue("eventId", "evt_submitted_" + submission.Id);
        await responseCommand.ExecuteNonQueryAsync(cancellationToken);
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
