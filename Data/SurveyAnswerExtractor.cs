using System.Text.Json;
using FeedbackTool.Models;

namespace FeedbackTool.Data;

public static class SurveyAnswerExtractor
{
    public const string LikesPrefix = "What do you like about";
    public const string DislikesPrefix = "What don't you like about";
    public const string WantsPrefix = "What additions would";

    public static string ValueStartingWith(JsonElement answers, string prefix)
    {
        if (answers.ValueKind != JsonValueKind.Object)
            return string.Empty;

        foreach (var answer in answers.EnumerateObject())
            if (answer.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && answer.Value.ValueKind == JsonValueKind.String)
                return answer.Value.GetString() ?? string.Empty;

        return string.Empty;
    }

    // Who answered: never shown with the answers (the response already says which group).
    private static readonly string[] NameQuestions = ["full name", "name", "your name"];
    private static readonly string[] ContactWords = ["email", "phone"];

    // Every answer in the submission except likes / dislikes / wants (already on the response), names, contact details
    // and blanks, in the survey's order. Newer surveys (e.g. the role-based one) keep most feedback in these.
    public static IReadOnlyList<ResponseAnswerDto> OtherAnswers(string answersJson)
    {
        using var doc = JsonDocument.Parse(answersJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        var list = new List<ResponseAnswerDto>();
        foreach (var answer in doc.RootElement.EnumerateObject())
        {
            var question = answer.Name.Trim();
            var q = question.ToLowerInvariant();
            if (NameQuestions.Contains(q) || ContactWords.Any(q.Contains)) continue;
            if (new[] { LikesPrefix, DislikesPrefix, WantsPrefix }.Any(p => question.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            var text = answer.Value.ValueKind switch
            {
                JsonValueKind.String => answer.Value.GetString() ?? "",
                JsonValueKind.Number => answer.Value.GetRawText(),
                JsonValueKind.Array => string.Join(", ", answer.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
                _ => "",
            };
            if (!string.IsNullOrWhiteSpace(text)) list.Add(new(question, text.Trim()));
        }
        return list;
    }
}
