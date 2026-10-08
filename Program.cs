using FeedbackTool.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<ISurveySubmissionRepository, SurveySubmissionRepository>();

var app = builder.Build();

app.MapControllers();

app.Run();
