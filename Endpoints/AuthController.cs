using System.Text.Json;
using FeedbackTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FeedbackTool.Endpoints;

[Route("api/v1")]
public sealed class AuthController(UserRepository users, AuthTokens tokens) : ApiControllerBase
{
    [AllowAnonymous]
    [EnableRateLimiting("login")]
    [HttpPost("auth/login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        using var body = await ReadBodyAsync(Request, ct);
        if (body is null || !TryString(body.RootElement, "email", out var email) || !TryString(body.RootElement, "password", out var password))
            return BadRequestError("Enter your email and password.");
        // TEMPORARY, remove once the first admin exists: with no users yet, the first sign-in creates an admin
        // with that email and password. Until then, whoever signs in first becomes the admin.
        if (!await users.AnyAsync(ct) && email.Contains('@') && password.Length is >= 8 and <= 128)
            await users.CreateAsync(email, "Admin", password, Roles.Admin, ct);

        var user = await users.CheckPasswordAsync(email, password, ct);
        if (user is null) return Unauthorized(new { error = new { code = "invalid_credentials", message = "That email and password don't match." } });
        var (token, expiresAt) = tokens.Create(user);
        return Ok(new { token, expiresAt, user = Me(user) });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMe(CancellationToken ct) =>
        await users.GetAsync(User.UserId(), ct) is { } user ? Ok(Me(user)) : Unauthorized(new { error = new { code = "unauthorized", message = "Sign in again." } });

    private static object Me(UserDto user) => new { id = user.Id, name = user.Name, email = user.Email, role = user.Role };

    internal static async Task<JsonDocument?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        try { return await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
        catch (JsonException) { return null; }
    }

    internal static bool TryString(JsonElement root, string property, out string value)
    {
        value = string.Empty;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString() ?? string.Empty;
        return true;
    }
}

public static class ClaimsExtensions
{
    // The signed-in user's ID (the token's "sub").
    public static string UserId(this System.Security.Claims.ClaimsPrincipal user) => user.FindFirst("sub")?.Value ?? string.Empty;
}
