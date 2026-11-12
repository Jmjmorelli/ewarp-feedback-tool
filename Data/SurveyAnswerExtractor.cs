using System.Text.Json;
using FeedbackTool.Models;

namespace FeedbackTool.Data;

public sealed record SurveyAnswer(string Question, JsonElement Value);

public static class SurveyAnswerExtractor
{
    public const string LikesPrefix = "What do you like about";
    public const string DislikesPrefix = "What don't you like about";
    public const string WantsPrefix = "What additions would";

    public static string ValueStartingWith(JsonElement answers, string prefix)
    {
        foreach (var answer in EnumerateLeafAnswers(answers))
            if (answer.Question.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && answer.Value.ValueKind == JsonValueKind.String)
                return answer.Value.GetString() ?? string.Empty;

        return string.Empty;
    }

    public static string ValueContaining(JsonElement answers, string text)
    {
        foreach (var answer in EnumerateLeafAnswers(answers))
            if (answer.Question.Contains(text, StringComparison.OrdinalIgnoreCase) && answer.Value.ValueKind == JsonValueKind.String)
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

    // Every answer, grouped the way the form groups them: a nested object is a section (e.g. one per role the
    // respondent picked), and a section inside a section becomes "Outer › Inner". Questions outside any section go
    // in an untitled one. Leaves out names, contact details, the timestamp, blanks, and any answer that is exactly
    // one of `shown` (the likes / dislikes / wants the page already shows).
    public static IReadOnlyList<ResponseAnswerSectionDto> Sections(string answersJson, params string[] shown)
    {
        using var doc = JsonDocument.Parse(answersJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
        var skip = shown.Select(s => s.Trim()).Where(s => s.Length > 0).ToHashSet(StringComparer.Ordinal);
        var sections = new List<ResponseAnswerSectionDto>();
        AddSection(sections, null, doc.RootElement, skip);
        return sections;
    }

    private static void AddSection(List<ResponseAnswerSectionDto> sections, string? title, JsonElement section, HashSet<string> skip)
    {
        List<ResponseSurveyAnswerDto>? answers = null; // added on its first answer, so empty sections never show
        foreach (var item in section.EnumerateObject())
        {
            if (item.Value.ValueKind == JsonValueKind.Object)
            {
                var name = item.Name.Trim();
                AddSection(sections, title is null ? name : $"{title} › {name}", item.Value, skip);
                answers = null; // questions after a subsection start a new block, keeping the form's order
                continue;
            }
            if (Answer(item.Name, item.Value, skip) is not { } answer) continue;
            if (answers is null) { answers = []; sections.Add(new(title, answers)); }
            answers.Add(answer);
        }
    }

    private static ResponseSurveyAnswerDto? Answer(string name, JsonElement value, HashSet<string> skip)
    {
        var question = name.Trim();
        var q = question.ToLowerInvariant();
        if (q == "timestamp" || NameQuestions.Contains(q) || ContactWords.Any(q.Contains)) return null;
        var items = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToList() : new List<JsonElement> { value };
        var values = items.Select(Text).Where(t => t.Length > 0).ToList();
        if (values.Count == 0) return null;
        if (values.Count == 1 && skip.Contains(values[0])) return null;
        return new(question, values);
    }

    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()?.Trim() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "Yes",
        JsonValueKind.False => "No",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Text).Where(t => t.Length > 0)),
        _ => "",
    };

    // Google Forms can group questions by section. Sections are containers, while strings, numbers, and arrays are answers.
    public static IEnumerable<SurveyAnswer> EnumerateLeafAnswers(JsonElement answers)
    {
        if (answers.ValueKind != JsonValueKind.Object)
            yield break;

        foreach (var answer in answers.EnumerateObject())
            foreach (var leaf in Enumerate(answer.Name, answer.Value))
                yield return leaf;
    }

    private static IEnumerable<SurveyAnswer> Enumerate(string question, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            yield return new SurveyAnswer(question, value);
            yield break;
        }

        foreach (var child in value.EnumerateObject())
            foreach (var leaf in Enumerate(child.Name, child.Value))
                yield return leaf;
    }
}
