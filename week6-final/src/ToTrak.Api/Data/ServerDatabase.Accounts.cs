using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToTrak.Api.Models;

namespace ToTrak.Api.Data;

public sealed record Credentials(string Username, string Password);
public sealed record AccountSession(int UserId, string Username, string Token, DateTime ExpiresAtUtc);

public partial class ServerDatabase
{
    private const int PasswordIterations = 210_000;

    private static string HashPassword(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
        return $"{PasswordIterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        string[] parts = stored.Split('.');
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(parts[1]),
            int.Parse(parts[0]), HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(parts[2]));
    }

    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public AccountSession Register(Credentials credentials)
    {
        string username = credentials.Username?.Trim() ?? "";
        if (username.Length is < 3 or > 64 || username.Any(char.IsControl) ||
            credentials.Password is null || credentials.Password.Length is < 8 or > 256)
            throw new ArgumentException("Use a username of 3–64 characters and a password of 8–256 characters.");

        string hash = HashPassword(credentials.Password);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        // Legacy demonstration users cannot be claimed by registering a new account.
        insert.CommandText = """
            SELECT COALESCE(MAX(UserId), 0) + 1 FROM (
                SELECT UserId FROM Users UNION ALL SELECT UserId FROM Tasks
                UNION ALL SELECT UserId FROM Assignments UNION ALL SELECT UserId FROM Courses
                UNION ALL SELECT UserId FROM Locations UNION ALL SELECT UserId FROM ReceivedChanges);
            """;
        int userId = checked(Convert.ToInt32(insert.ExecuteScalar()));
        insert.CommandText = "INSERT INTO Users(UserId, Username, PasswordHash) VALUES ($id, $name, $hash)";
        insert.Parameters.AddWithValue("$id", userId);
        insert.Parameters.AddWithValue("$name", username);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.ExecuteNonQuery();

        string[] names = ["Home", "School", "Work"];
        for (int index = 0; index < names.Length; index++)
        {
            var location = new LocationItem
            {
                UserId = userId,
                LocationId = index + 1,
                LocationName = names[index],
                LastUpdated = DateTime.UtcNow,
                LastModifiedBy = "server"
            };
            using var seed = connection.CreateCommand();
            seed.Transaction = transaction;
            seed.CommandText = "INSERT INTO Locations(UserId, LocationId, LocationName, LastUpdated, IsDeleted, LastModifiedBy) VALUES ($user, $id, $name, $time, 0, 'server')";
            seed.Parameters.AddWithValue("$user", userId);
            seed.Parameters.AddWithValue("$id", location.LocationId);
            seed.Parameters.AddWithValue("$name", location.LocationName);
            seed.Parameters.AddWithValue("$time", location.LastUpdated.ToString("O"));
            seed.ExecuteNonQuery();
            WriteAudit(connection, transaction, new PendingAuditChange
            {
                ClientChangeId = Guid.NewGuid().ToString("N"),
                UserId = userId,
                EntityChanged = "Location",
                ChangeType = "Create",
                AffectedId = location.LocationId,
                LocationId = location.LocationId,
                NewValue = JsonSerializer.Serialize(location),
                ChangedAtUtc = location.LastUpdated,
                DeviceId = "server"
            }, null, location, "Applied", "Default location created with account.", false);
        }
        var session = CreateSession(connection, transaction, userId, username);
        transaction.Commit();
        return session;
    }

    public AccountSession? Login(Credentials credentials)
    {
        if (credentials.Username is null || credentials.Password is null || credentials.Password.Length > 256)
            return null;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        int userId;
        string username;
        using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT UserId, Username, PasswordHash FROM Users WHERE Username = $name";
            query.Parameters.AddWithValue("$name", credentials.Username.Trim());
            using var reader = query.ExecuteReader();
            if (!reader.Read() || !VerifyPassword(credentials.Password, reader.GetString(2))) return null;
            userId = reader.GetInt32(0);
            username = reader.GetString(1);
        }
        using var transaction = connection.BeginTransaction();
        var session = CreateSession(connection, transaction, userId, username);
        transaction.Commit();
        return session;
    }

    private static AccountSession CreateSession(SqliteConnection connection, SqliteTransaction transaction, int userId, string username)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        DateTime expires = DateTime.UtcNow.AddDays(30);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Sessions WHERE ExpiresAt <= $now; INSERT INTO Sessions(TokenHash, UserId, ExpiresAt) VALUES ($token, $user, $expires)";
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$token", TokenHash(token));
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.ExecuteNonQuery();
        return new(userId, username, token, expires);
    }

    public int? Authenticate(string token)
    {
        if (token.Length != 64) return null;
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UserId FROM Sessions WHERE TokenHash = $token AND ExpiresAt > $now";
        command.Parameters.AddWithValue("$token", TokenHash(token));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        object? value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    public void Logout(string token)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Sessions WHERE TokenHash = $token";
        command.Parameters.AddWithValue("$token", TokenHash(token));
        command.ExecuteNonQuery();
    }
}
