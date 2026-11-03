using System.Net.Mail;
using System.Text.Json;
using FeedbackTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FeedbackTool.Endpoints;

[Authorize(Policy = Policies.Admin)]
[Route("api/v1/users")]
public sealed class UsersController(UserRepository users) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await users.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null) return BadRequestError("Send email, name, password and role.");
        var root = body.RootElement;
        if (!AuthController.TryString(root, "email", out var email) || !ValidEmail(email)) return BadRequestError("Enter a valid email.");
        if (!AuthController.TryString(root, "name", out var name) || Normalize(name) is not { Length: >= 1 and <= 80 }) return BadRequestError("Enter a name (up to 80 characters).");
        if (!AuthController.TryString(root, "password", out var password) || !ValidPassword(password)) return BadRequestError(PasswordRule);
        if (!AuthController.TryString(root, "role", out var role) || !Roles.IsValid(role)) return BadRequestError("role must be admin or viewer.");
        return ToResult(await users.CreateAsync(email, Normalize(name), password, role, ct));
    }

    [HttpPatch("{userId}")]
    public async Task<IActionResult> Update(string userId, CancellationToken ct)
    {
        using var body = await AuthController.ReadBodyAsync(Request, ct);
        if (body is null || body.RootElement.ValueKind != JsonValueKind.Object) return BadRequestError("Send at least one of name, role or password.");
        var root = body.RootElement;
        string? name = null, role = null, password = null;
        if (root.TryGetProperty("name", out _))
        {
            if (!AuthController.TryString(root, "name", out var value) || Normalize(value) is not { Length: >= 1 and <= 80 }) return BadRequestError("Enter a name (up to 80 characters).");
            name = Normalize(value);
        }
        if (root.TryGetProperty("role", out _))
        {
            if (!AuthController.TryString(root, "role", out var value) || !Roles.IsValid(value)) return BadRequestError("role must be admin or viewer.");
            role = value;
        }
        if (root.TryGetProperty("password", out _))
        {
            if (!AuthController.TryString(root, "password", out var value) || !ValidPassword(value)) return BadRequestError(PasswordRule);
            password = value;
        }
        if (name is null && role is null && password is null) return BadRequestError("Send at least one of name, role or password.");
        return ToResult(await users.UpdateAsync(userId, name, role, password, ct));
    }

    [HttpDelete("{userId}")]
    public async Task<IActionResult> Delete(string userId, CancellationToken ct) => ToResult(await users.DeleteAsync(userId, User.UserId(), ct));

    private const string PasswordRule = "Password must be 8 to 128 characters.";
    private static bool ValidPassword(string password) => password.Length is >= 8 and <= 128;
    private static bool ValidEmail(string email) => email.Trim().Length <= 200 && MailAddress.TryCreate(email.Trim(), out var parsed) && parsed.Address == email.Trim();
    private static string Normalize(string value) => string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private IActionResult ToResult(UserWriteResult result) => result.Code is null ? result.Status == 204 ? NoContent() : StatusCode(result.Status, result.User) : StatusCode(result.Status, new { error = new { code = result.Code, message = result.Message } });
}
