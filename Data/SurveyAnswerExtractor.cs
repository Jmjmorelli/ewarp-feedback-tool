using System.Text.Json;

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
}
