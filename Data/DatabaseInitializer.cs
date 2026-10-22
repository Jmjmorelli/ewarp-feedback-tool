using Npgsql;

namespace FeedbackTool.Data;

public sealed class DatabaseInitializer(IConfiguration configuration, IWebHostEnvironment environment)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("Database connection is not configured.");

        var schemaPath = Path.Combine(environment.ContentRootPath, "Data", "Schema.sql");
        var sql = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
