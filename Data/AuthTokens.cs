using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace FeedbackTool.Data;

// Signs the bearer tokens /auth/login hands out. Config: Auth:JwtSecret (32+ characters), Auth:TokenHours (default 12).
public sealed class AuthTokens(IConfiguration configuration)
{
    public const string Issuer = "ewarp-feedback-tool";

    public static SymmetricSecurityKey Key(IConfiguration configuration)
    {
        var secret = configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new InvalidOperationException("Auth:JwtSecret must be set to at least 32 characters.");
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
            SigningCredentials = new SigningCredentials(Key(configuration), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }
}
