using System.Text.Json;

namespace FeedbackTool.Data;

// The respondent's DIU role, read from the survey's role question, so scores can be split per role
// (sponsor 2026-10-22: "count once per role"). Older values pm / sponsor / other stay valid.
public static class RespondentGroups
{
    public const string Other = "other";

    static readonly (string Match, string Group)[] Roles =
    [
        ("project manager", "pm"), ("portfolio director", "pd"), ("portfolio analyst", "pa"),
        ("congress", "congo"), ("congo", "congo"), ("acquisition", "acquisition"), ("data cell", "data_cell"),
        ("communication", "comops"), ("comops", "comops"), ("commercial", "comops"), ("warfighter", "wit"), ("wit", "wit"),
    ];

    public static readonly string[] All = ["pm", "pd", "pa", "congo", "acquisition", "data_cell", "comops", "wit", "sponsor", Other];

    public static bool IsValid(string? group) => group is null || All.Contains(group);

    public static string FromAnswers(JsonElement answers)
    {
        if (answers.ValueKind != JsonValueKind.Object)
            return Other;
        foreach (var answer in answers.EnumerateObject())
        {
            if (!answer.Name.Contains("your role", StringComparison.OrdinalIgnoreCase))
                continue;
            // a multi-select role comes in as a list: the first role picked is the group
            var value = answer.Value.ValueKind switch
            {
                JsonValueKind.String => answer.Value.GetString(),
                JsonValueKind.Array when answer.Value.GetArrayLength() > 0 => answer.Value[0].ToString(),
                _ => null,
            };
            return FromRole(value);
        }
        return Other;
    }

    public static string FromRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return Other;
        var words = role.ToLowerInvariant();
        if (words.StartsWith("pm") || words.Contains("pm/"))
            return "pm";
        foreach (var (match, group) in Roles)
            if (words.Contains(match))
                return group;
        return Other;
    }
}
