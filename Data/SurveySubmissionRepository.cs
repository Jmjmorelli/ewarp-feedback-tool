using FeedbackTool.Models;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace FeedbackTool.Data;

public sealed class SurveySubmissionRepository(IConfiguration configuration, ILogger<SurveySubmissionRepository> logger) : ISurveySubmissionRepository
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
        // One transaction: if building the response fails, the raw submission is rolled back too, so the form's next
        // send is not swallowed by "on conflict do nothing" (fixed 2026-11-27).
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using var command = new NpgsqlCommand("""
            insert into survey_submissions (id, form, submitted_at, answers)
            values (@id, @form, @submittedAt, @answers)
            on conflict (id) do nothing;
            """, connection, tx);

        command.Parameters.AddWithValue("id", submission.Id);
        command.Parameters.AddWithValue("form", submission.Form);
        command.Parameters.AddWithValue("submittedAt", submission.SubmittedAt!.Value);
        command.Parameters.AddWithValue("answers", NpgsqlDbType.Jsonb, submission.Answers.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);

        // A submission saved earlier without a response (e.g. its form had no configuration yet) gets its response
        // built when it is sent again, instead of being skipped forever.
        await using (var existsCommand = new NpgsqlCommand(
            "select exists (select 1 from responses where raw_submission_id = @id);", connection, tx))
        {
            existsCommand.Parameters.AddWithValue("id", submission.Id);
            if ((bool)(await existsCommand.ExecuteScalarAsync(cancellationToken))!)
            {
                await tx.CommitAsync(cancellationToken);
                return;
            }
        }

        // Exact form name first. Otherwise a configured name inside the posted one: the Pryzm Google Form posts
        // "Belisar (Pryzm)" while the vendor was saved as "Pryzm", and the exact-only match dropped all 8 of its real
        // responses (fixed 2026-11-27, Refs DIU-64). Longest configured name wins so "Pryzm" never beats "Pryzm 2".
        await using var configCommand = new NpgsqlCommand("""
            select project_id, survey_id, vendor_id
            from form_configurations
            where lower(form) = lower(@form) or strpos(lower(@form), lower(form)) > 0
            order by (lower(form) = lower(@form)) desc, length(form) desc
            limit 1;
            """, connection, tx);
        configCommand.Parameters.AddWithValue("form", submission.Form);
        await using var configReader = await configCommand.ExecuteReaderAsync(cancellationToken);
        if (!await configReader.ReadAsync(cancellationToken))
        {
            await configReader.CloseAsync();
            await tx.CommitAsync(cancellationToken); // keep the raw submission; a resend after the vendor is set up builds the response
            logger.LogWarning("Survey submission {SubmissionId} from form {Form} matches no vendor form configuration; saved raw, no response built",
                submission.Id, submission.Form);
            return;
        }

        var projectId = configReader.GetString(0);
        var surveyId = configReader.GetString(1);
        var vendorId = configReader.GetString(2);
        await configReader.CloseAsync();

        var role = RespondentGroups.FromAnswers(submission.Answers);
        var group = RespondentGroups.Legacy(role);
        var likes = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.LikesPrefix);
        var dislikes = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.DislikesPrefix, SurveyAnswerExtractor.DislikesAltPrefix);
        var wants = SurveyAnswerExtractor.ValueStartingWith(submission.Answers, SurveyAnswerExtractor.WantsPrefix);
        if (string.IsNullOrEmpty(wants)) wants = SurveyAnswerExtractor.ValueContaining(submission.Answers, "would you like");
        var responseId = "rsp_" + submission.Id;

        await using var responseCommand = new NpgsqlCommand("""
            insert into responses (id, raw_submission_id, project_id, survey_id, vendor_id, respondent_group, respondent_role, likes, dislikes, wants, submitted_at)
            values (@id, @rawId, @projectId, @surveyId, @vendorId, @group, @role, @likes, @dislikes, @wants, @submittedAt);

            insert into activity_events (id, project_id, type, occurred_at, response_id)
            values (@eventId, @projectId, 'response.submitted', @submittedAt, @id);
            """, connection, tx);
        responseCommand.Parameters.AddWithValue("id", responseId);
        responseCommand.Parameters.AddWithValue("rawId", submission.Id);
        responseCommand.Parameters.AddWithValue("projectId", projectId);
        responseCommand.Parameters.AddWithValue("surveyId", surveyId);
        responseCommand.Parameters.AddWithValue("vendorId", vendorId);
        responseCommand.Parameters.AddWithValue("group", group);
        responseCommand.Parameters.AddWithValue("role", role);
        responseCommand.Parameters.AddWithValue("likes", likes);
        responseCommand.Parameters.AddWithValue("dislikes", dislikes);
        responseCommand.Parameters.AddWithValue("wants", wants);
        responseCommand.Parameters.AddWithValue("submittedAt", submission.SubmittedAt!.Value);
        responseCommand.Parameters.AddWithValue("eventId", "evt_submitted_" + submission.Id);
        await responseCommand.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
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
