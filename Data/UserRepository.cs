using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace FeedbackTool.Data;

public static class Roles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
    public static bool IsValid(string? role) => role is Admin or Viewer;
}

public sealed record UserDto(string Id, string Email, string Name, string Role, DateTimeOffset CreatedAt);
public sealed record UserListResult(IReadOnlyList<UserDto> Items, int Total);

public sealed record UserWriteResult(int Status, string? Code, string? Message, UserDto? User)
{
    public static UserWriteResult Success(int status, UserDto? user = null) => new(status, null, null, user);
    public static UserWriteResult Error(int status, string code, string message) => new(status, code, message, null);
}

// Users, roles and password checks. Passwords are hashed with ASP.NET Core's PasswordHasher (PBKDF2, salted).
public sealed class UserRepository(IConfiguration configuration)
{
    private static readonly PasswordHasher<string> Hasher = new();
    private const string Columns = "id, email, name, role, created_at";

    public async Task<UserDto?> GetAsync(string userId, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, $"select {Columns} from users where id=@id", ("id", userId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<UserListResult> ListAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, $"select {Columns} from users order by lower(name), id");
        var items = new List<UserDto>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(Read(r));
        return new(items, items.Count);
    }

    // The user, if the email and password match.
    public async Task<UserDto?> CheckPasswordAsync(string email, string password, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, $"select {Columns}, password_hash from users where email=@email", ("email", email.Trim().ToLowerInvariant()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var user = Read(r);
        return Hasher.VerifyHashedPassword(user.Id, r.GetString(5), password) == PasswordVerificationResult.Failed ? null : user;
    }

    public async Task<bool> AnyAsync(CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var cmd = Command(c, "select exists(select 1 from users)");
        return (bool)(await cmd.ExecuteScalarAsync(ct) ?? false);
    }

    public async Task<UserWriteResult> CreateAsync(string email, string name, string password, string role, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext(@key))", ct, ("key", "user-email:" + email));
        if ((bool)(await ScalarAsync(c, tx, "select exists(select 1 from users where email=@email)", ct, ("email", email)) ?? false))
            return UserWriteResult.Error(409, "conflict", $"A user with the email {email} already exists.");
        var id = "usr_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(c, tx, "insert into users (id, email, name, password_hash, role) values (@id,@email,@name,@hash,@role)", ct,
            ("id", id), ("email", email), ("name", name), ("hash", Hasher.HashPassword(id, password)), ("role", role));
        await tx.CommitAsync(ct);
        return UserWriteResult.Success(201, await GetAsync(id, ct));
    }

    public async Task<UserWriteResult> UpdateAsync(string userId, string? name, string? role, string? password, CancellationToken ct)
    {
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('users-admins'))", ct);
        var current = await ScalarAsync(c, tx, "select role from users where id=@id", ct, ("id", userId)) as string;
        if (current is null) return Missing(userId);
        if (current == Roles.Admin && role == Roles.Viewer && await AdminCountAsync(c, tx, ct) <= 1)
            return UserWriteResult.Error(409, "conflict", "This is the only admin. Make someone else an admin first.");
        if (name is not null) await ExecuteAsync(c, tx, "update users set name=@name where id=@id", ct, ("name", name), ("id", userId));
        if (role is not null) await ExecuteAsync(c, tx, "update users set role=@role where id=@id", ct, ("role", role), ("id", userId));
        if (password is not null) await ExecuteAsync(c, tx, "update users set password_hash=@hash where id=@id", ct, ("hash", Hasher.HashPassword(userId, password)), ("id", userId));
        await tx.CommitAsync(ct);
        return UserWriteResult.Success(200, await GetAsync(userId, ct));
    }

    public async Task<UserWriteResult> DeleteAsync(string userId, string currentUserId, CancellationToken ct)
    {
        if (userId == currentUserId) return UserWriteResult.Error(409, "conflict", "You can't delete your own account.");
        await using var c = await OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await ExecuteAsync(c, tx, "select pg_advisory_xact_lock(hashtext('users-admins'))", ct);
        var current = await ScalarAsync(c, tx, "select role from users where id=@id", ct, ("id", userId)) as string;
        if (current is null) return Missing(userId);
        if (current == Roles.Admin && await AdminCountAsync(c, tx, ct) <= 1)
            return UserWriteResult.Error(409, "conflict", "This is the only admin, so it can't be deleted.");
        await ExecuteAsync(c, tx, "delete from users where id=@id", ct, ("id", userId));
        await tx.CommitAsync(ct);
        return UserWriteResult.Success(204);
    }

    private static UserWriteResult Missing(string userId) => UserWriteResult.Error(404, "not_found", $"User {userId} not found");
    private static async Task<int> AdminCountAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct) => Convert.ToInt32(await ScalarAsync(c, tx, "select count(*)::int from users where role='admin'", ct));
    private static UserDto Read(NpgsqlDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));
    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct) { var cs = configuration.GetConnectionString("DefaultConnection"); if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("Database connection is not configured."); var c = new NpgsqlConnection(cs); await c.OpenAsync(ct); return c; }
    private static async Task<object?> ScalarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; return await cmd.ExecuteScalarAsync(ct); }
    private static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct, params (string, object?)[] values) { await using var cmd = Command(c, sql, values); cmd.Transaction = tx; await cmd.ExecuteNonQueryAsync(ct); }
    private static NpgsqlCommand Command(NpgsqlConnection c, string sql, params (string, object?)[] values) { var cmd = new NpgsqlCommand(sql, c); foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value); return cmd; }
}
