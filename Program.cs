var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapPost("/webhook/google-form", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    Console.WriteLine("===== GOOGLE FORM SUBMISSION =====");
    Console.WriteLine(body);
    Console.WriteLine("==================================");

    return Results.Ok(new
    {
        success = true,
        message = "Webhook received"
    });
});

app.Run();