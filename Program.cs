using FeedbackTool.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<ISurveySubmissionRepository, SurveySubmissionRepository>();
builder.Services.AddScoped<ProjectReadRepository>();
builder.Services.AddScoped<IFeatureReadRepository, FeatureReadRepository>();
builder.Services.AddScoped<IVendorReadRepository, VendorReadRepository>();
builder.Services.AddScoped<IResponseRepository, ResponseRepository>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService<ResponseAnalysisWorker>();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:InitializeOnStartup"))
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.MapControllers();

app.Run();
