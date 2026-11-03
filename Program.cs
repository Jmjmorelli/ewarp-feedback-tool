using System.Security.Claims;
using System.Threading.RateLimiting;
using FeedbackTool.Data;
using FeedbackTool.Endpoints;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddScoped<ISurveySubmissionRepository, SurveySubmissionRepository>();
builder.Services.AddScoped<ProjectReadRepository>();
builder.Services.AddScoped<IFeatureReadRepository, FeatureReadRepository>();
builder.Services.AddScoped<IFeatureWriteRepository, FeatureWriteRepository>();
builder.Services.AddScoped<IVendorReadRepository, VendorReadRepository>();
builder.Services.AddScoped<IVendorWriteRepository, VendorWriteRepository>();
builder.Services.AddScoped<IResponseRepository, ResponseRepository>();
builder.Services.AddScoped<ScoringRepository>();
builder.Services.AddScoped<UserRepository>();
// Sign-in tokens are signed with JWT_SECRET from the environment (see AuthTokens.LoadKey).
var signingKey = AuthTokens.LoadKey(builder.Configuration);
builder.Services.AddSingleton(sp => new AuthTokens(sp.GetRequiredService<IConfiguration>(), signingKey));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService<ResponseAnalysisWorker>();

// Sign-in: POST /api/v1/auth/login returns a bearer token. Every other route needs one, except the Google Form webhook.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new()
    {
        ValidIssuer = AuthTokens.Issuer,
        ValidAudience = AuthTokens.Issuer,
        IssuerSigningKey = signingKey,
        NameClaimType = "sub",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromMinutes(1),
    };
    options.Events = new JwtBearerEvents
    {
        // Read the role from the database on every request, so a role change or a deleted user takes effect right away.
        OnTokenValidated = async context =>
        {
            var users = context.HttpContext.RequestServices.GetRequiredService<UserRepository>();
            var user = await users.GetAsync(context.Principal?.FindFirst("sub")?.Value ?? string.Empty, context.HttpContext.RequestAborted);
            if (user is null) { context.Fail("This account no longer exists."); return; }
            context.Principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.Id), new Claim("role", user.Role)], context.Scheme.Name, "sub", "role"));
        },
        OnChallenge = async context =>
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "unauthorized", message = "Sign in to continue." } });
        },
        OnForbidden = async context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = new { code = "forbidden", message = "Only admins can do that." } });
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Admin, policy => policy.RequireAuthenticatedUser().RequireRole(Roles.Admin))
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// Slows down password guessing: 30 login attempts per minute per IP.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:InitializeOnStartup"))
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

// Optional: create the first admin from Auth:BootstrapAdminEmail / Auth:BootstrapAdminPassword when there are no users yet.
// Without them, add the first admin with Data/Migrations/create-user.mjs.
using (var scope = app.Services.CreateScope())
{
    var email = app.Configuration["Auth:BootstrapAdminEmail"];
    var password = app.Configuration["Auth:BootstrapAdminPassword"];
    var users = scope.ServiceProvider.GetRequiredService<UserRepository>();
    if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password) && !await users.AnyAsync(CancellationToken.None))
        await users.CreateAsync(email, "Admin", password, Roles.Admin, CancellationToken.None);
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
