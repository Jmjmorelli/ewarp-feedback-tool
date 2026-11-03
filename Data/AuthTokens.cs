using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FeedbackTool.Data;

// Signs the bearer tokens /auth/login hands out. Config: Auth:TokenHours (default 12).
public sealed class AuthTokens(IConfiguration configuration, SymmetricSecurityKey key)
{
    public const string Issuer = "ewarp-feedback-tool";

    // The signing key, like JWT_SECRET in an Express app: env JWT_SECRET or Auth__JwtSecret, 32+ characters.
    // It only signs sign-in tokens; passwords are hashed with their own random salt (UserRepository).
    public static SymmetricSecurityKey LoadKey(IConfiguration configuration)
    {
        var secret = configuration["JWT_SECRET"] ?? configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("Set JWT_SECRET (32+ characters) so the backend can sign sign-in tokens.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }

    public (string Token, DateTimeOffset ExpiresAt) Create(UserDto user)
    {
        var expires = DateTimeOffset.UtcNow.AddHours(configuration.GetValue("Auth:TokenHours", 12));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Issuer,
            Subject = new ClaimsIdentity([new Claim("sub", user.Id), new Claim("role", user.Role)]),
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
