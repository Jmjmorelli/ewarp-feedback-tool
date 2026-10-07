using FeedbackTool.Data;
using FeedbackTool.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<ISurveySubmissionRepository, SurveySubmissionRepository>();

var app = builder.Build();

app.MapGoogleFormWebhookEndpoints();

app.Run();
