using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FeedbackTool.Data;

// Signs the bearer tokens /auth/login hands out. Config: Auth:TokenHours (default 12).
public sealed class AuthTokens(IConfiguration configuration, SymmetricSecurityKey key)
{
    public const string Issuer = "ewarp-feedback-tool";

    // The signing key: Auth:JwtSecret if it's set, otherwise a random one the backend creates on its first start
    // and keeps in app_secrets, so tokens stay valid across restarts and deploys without any server setting.
    public static async Task<SymmetricSecurityKey> LoadKeyAsync(IConfiguration configuration, CancellationToken ct)
    {
        var secret = configuration["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            var cs = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured.");
            await using var c = new NpgsqlConnection(cs);
            await c.OpenAsync(ct);
            await using (var insert = new NpgsqlCommand("insert into app_secrets (key, value) values ('jwt_signing_key', @value) on conflict (key) do nothing", c))
            {
                insert.Parameters.AddWithValue("value", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using var select = new NpgsqlCommand("select value from app_secrets where key='jwt_signing_key'", c);
            secret = (string)(await select.ExecuteScalarAsync(ct))!;
        }
        if (secret.Length < 32) throw new InvalidOperationException("Auth:JwtSecret must be at least 32 characters.");
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
