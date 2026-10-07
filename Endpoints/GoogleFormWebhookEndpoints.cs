namespace FeedbackTool.Endpoints;

public static class GoogleFormWebhookEndpoints
{
    public static IEndpointRouteBuilder MapGoogleFormWebhookEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapControllerRoute(
            name: "google-form-webhook",
            pattern: "webhook/google-form",
            defaults: new { controller = "GoogleFormWebhook", action = "Receive" });

        endpoints.MapControllerRoute(
            name: "survey-submissions",
            pattern: "survey-submissions",
            defaults: new { controller = "SurveySubmissions", action = "GetAll" });

        return endpoints;
    }
}
