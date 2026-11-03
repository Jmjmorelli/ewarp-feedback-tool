namespace FeedbackTool.Endpoints;

// Authorization policy names. Every route needs a signed-in user unless it says [AllowAnonymous];
// routes that change data also need [Authorize(Policy = Policies.Admin)].
public static class Policies
{
    public const string Admin = "admin";
}
