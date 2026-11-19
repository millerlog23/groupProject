using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToTrak.Api.Models;

namespace ToTrak.Api.Data;

public partial class ServerDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public long GetNextEntityId(int userId, string entity)
    {
        (string table, string id) = entity switch
        {
            "Task" => ("Tasks", "TaskId"),
            "Assignment" => ("Assignments", "AssignmentId"),
            "Course" => ("Courses", "CourseId"),
            "Location" => ("Locations", "LocationId"),
            _ => throw new ArgumentException("Unknown entity.")
        };
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(MAX({id}), 0) FROM {table} WHERE UserId = $user";
        command.Parameters.AddWithValue("$user", userId);
        return checked(Convert.ToInt64(command.ExecuteScalar()) + 1);
    }

    private static void InitializeDomainSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "Courses", "CourseDescription", "TEXT NULL");
        EnsureColumn(connection, "Assignments", "AssignmentDescription", "TEXT NULL");
        EnsureColumn(connection, "Assignments", "EstimatedTime", "INTEGER NULL");
        EnsureColumn(connection, "ReceivedChanges", "LocationId", "INTEGER NULL");
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Locations (
                UserId INTEGER NOT NULL, LocationId INTEGER NOT NULL,
                LocationName TEXT NOT NULL, LastUpdated TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0, LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, LocationId));
            CREATE TABLE IF NOT EXISTS Users (
                UserId INTEGER PRIMARY KEY, Username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                PasswordHash TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Sessions (
                TokenHash TEXT PRIMARY KEY, UserId INTEGER NOT NULL,
                ExpiresAt TEXT NOT NULL, FOREIGN KEY (UserId) REFERENCES Users(UserId));
            """;
        command.ExecuteNonQuery();
    }

    private static void ValidateTaskLocation(SqliteConnection connection, SqliteTransaction transaction, int userId, int locationId)
    {
        if (locationId == 0) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // A tombstone still resolves historical/offline task references as Unset in the UI.
        command.CommandText = "SELECT COUNT(*) FROM Locations WHERE UserId = $user AND LocationId = $id";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$id", locationId);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1)
            throw new ArgumentException("The task references an unknown location for this user.");
    }

    private sealed record NamedItem(int UserId, long Id, string Name, string? Description,
        DateTime Updated, bool Deleted, string Device, object Value);

    private static NamedItem ReadNamed(string entity, string json)
    {
        if (entity == "Course")
        {
            var item = JsonSerializer.Deserialize<CourseItem>(json, JsonOptions)
                ?? throw new ArgumentException("A course snapshot is required.");
            return new(item.UserId, item.CourseId, item.CourseName, item.CourseDescription,
                item.LastUpdated, item.IsDeleted, item.LastModifiedBy, item);
        }
        var location = JsonSerializer.Deserialize<LocationItem>(json, JsonOptions)
            ?? throw new ArgumentException("A location snapshot is required.");
        return new(location.UserId, location.LocationId, location.LocationName, null,
            location.LastUpdated, location.IsDeleted, location.LastModifiedBy, location);
    }

    private static SyncResult NamedResult(PendingAuditChange change, NamedItem winner,
        string outcome, string details, bool conflict, bool retry = false) => new()
        {
            ClientChangeId = change.ClientChangeId,
            Outcome = outcome,
            Details = details,
            WasConflict = conflict,
            AlreadyProcessed = retry,
            ServerCourse = winner.Value as CourseItem,
            ServerLocation = winner.Value as LocationItem
        };

    private SyncResult ApplyNamedChange(PendingAuditChange change)
    {
        bool course = change.EntityChanged == "Course";
        if (string.IsNullOrWhiteSpace(change.ClientChangeId) || change.UserId <= 0 || change.AffectedId <= 0 ||
            change.TaskId is not null || change.AssignmentId is not null ||
            (course ? change.CourseId != change.AffectedId || change.LocationId is not null
                : change.LocationId != change.AffectedId || change.CourseId is not null) ||
            change.ChangeType is not ("Create" or "Update" or "Delete") ||
            change.ChangedAtUtc == default || change.ChangedAtUtc.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(change.DeviceId) || string.IsNullOrWhiteSpace(change.NewValue))
            throw new ArgumentException("Invalid course/location change metadata.");

        var incoming = ReadNamed(change.EntityChanged, change.NewValue);
        if (incoming.UserId != change.UserId || incoming.Id != change.AffectedId ||
            string.IsNullOrWhiteSpace(incoming.Name) || incoming.Updated.Kind != DateTimeKind.Utc ||
            incoming.Updated != change.ChangedAtUtc || incoming.Device != change.DeviceId ||
            incoming.Deleted != (change.ChangeType == "Delete"))
            throw new ArgumentException("Invalid course/location snapshot.");

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var retry = connection.CreateCommand())
        {
            retry.Transaction = transaction;
            retry.CommandText = "SELECT UserId, EntityChanged, AffectedId, Outcome, Details, WasConflict, ServerValue FROM ReceivedChanges WHERE ClientChangeId = $id";
            retry.Parameters.AddWithValue("$id", change.ClientChangeId);
            using var reader = retry.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetInt32(0) != change.UserId || reader.GetString(1) != change.EntityChanged || reader.GetInt64(2) != change.AffectedId)
                    throw new ArgumentException("ClientChangeId belongs to another item.");
                return NamedResult(change, ReadNamed(change.EntityChanged, reader.GetString(6)),
                    reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), true);
            }
        }

        string table = course ? "Courses" : "Locations";
        string idColumn = course ? "CourseId" : "LocationId";
        string nameColumn = course ? "CourseName" : "LocationName";
        NamedItem? current = null;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = $"SELECT {nameColumn}, LastUpdated, IsDeleted, LastModifiedBy" +
                (course ? ", CourseDescription" : "") + $" FROM {table} WHERE UserId = $user AND {idColumn} = $id";
            lookup.Parameters.AddWithValue("$user", change.UserId);
            lookup.Parameters.AddWithValue("$id", change.AffectedId);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                DateTime updated = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
                object value = course
                    ? new CourseItem
                    {
                        UserId = change.UserId,
                        CourseId = change.AffectedId,
                        CourseName = reader.GetString(0),
                        LastUpdated = updated,
                        IsDeleted = reader.GetBoolean(2),
                        LastModifiedBy = reader.GetString(3),
                        CourseDescription = reader.IsDBNull(4) ? null : reader.GetString(4)
                    }
                    : new LocationItem
                    {
                        UserId = change.UserId,
                        LocationId = checked((int)change.AffectedId),
                        LocationName = reader.GetString(0),
                        LastUpdated = updated,
                        IsDeleted = reader.GetBoolean(2),
                        LastModifiedBy = reader.GetString(3)
                    };
                current = ReadNamed(change.EntityChanged, JsonSerializer.Serialize(value));
            }
        }
        if (current is not null && change.ChangeType == "Create")
            throw new InvalidOperationException($"{change.EntityChanged} ID collision: pull changes before creating another item.");
        bool conflict = current is null ? change.ChangeType != "Create"
            : change.BaseLastUpdatedUtc?.Ticks != current.Updated.Ticks;
        bool wins = current is null || incoming.Updated.Ticks > current.Updated.Ticks;
        bool deletionBlocked = false;
        if (wins && course && incoming.Deleted)
        {
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM Assignments WHERE UserId = $user AND CourseId = $id AND IsDeleted = 0";
            check.Parameters.AddWithValue("$user", change.UserId);
            check.Parameters.AddWithValue("$id", change.AffectedId);
            deletionBlocked = Convert.ToInt32(check.ExecuteScalar()) > 0;
            if (deletionBlocked)
            {
                if (current is null) throw new InvalidOperationException("The assignment's course is missing.");
                wins = false;
                conflict = true;
            }
        }
        var winner = wins ? incoming : current!;
        string outcome = wins ? "Applied" : "ServerKept";
        string details = deletionBlocked ? "Course deletion was not applied: move or delete its assignments first."
            : wins ? "Incoming version stored; newest UTC timestamp wins." : "Server version has an equal or newer UTC timestamp.";

        if (wins)
        {
            using var save = connection.CreateCommand();
            save.Transaction = transaction;
            save.CommandText = $"INSERT INTO {table} (UserId, {idColumn}, {nameColumn}, LastUpdated, IsDeleted, LastModifiedBy" +
                (course ? ", CourseDescription" : "") + ") VALUES ($user, $id, $name, $updated, $deleted, $device" +
                (course ? ", $description" : "") + $") ON CONFLICT(UserId, {idColumn}) DO UPDATE SET {nameColumn} = excluded.{nameColumn}, " +
                "LastUpdated = excluded.LastUpdated, IsDeleted = excluded.IsDeleted, LastModifiedBy = excluded.LastModifiedBy" +
                (course ? ", CourseDescription = excluded.CourseDescription" : "");
            save.Parameters.AddWithValue("$user", incoming.UserId);
            save.Parameters.AddWithValue("$id", incoming.Id);
            save.Parameters.AddWithValue("$name", incoming.Name.Trim());
            save.Parameters.AddWithValue("$updated", incoming.Updated.ToString("O"));
            save.Parameters.AddWithValue("$deleted", incoming.Deleted);
            save.Parameters.AddWithValue("$device", incoming.Device);
            if (course) save.Parameters.AddWithValue("$description", (object?)incoming.Description ?? DBNull.Value);
            save.ExecuteNonQuery();
        }
        WriteAudit(connection, transaction, change, current?.Value, winner.Value, outcome, details, conflict);
        transaction.Commit();
        return NamedResult(change, winner, outcome, details, conflict);
    }

    private static void WriteAudit(SqliteConnection connection, SqliteTransaction transaction,
        PendingAuditChange change, object? before, object winner, string outcome, string details, bool conflict)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ReceivedChanges (ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId,
                TaskId, AssignmentId, CourseId, LocationId, OldValue, NewValue, ReceivedAt,
                ChangedAtUtc, DeviceId, Outcome, Details, WasConflict, ServerValue)
            VALUES ($change, $user, $entity, $action, $id, NULL, NULL, $course, $location,
                $old, $new, $received, $changed, $device, $outcome, $details, $conflict, $winner)
            """;
        command.Parameters.AddWithValue("$change", change.ClientChangeId);
        command.Parameters.AddWithValue("$user", change.UserId);
        command.Parameters.AddWithValue("$entity", change.EntityChanged);
        command.Parameters.AddWithValue("$action", change.ChangeType);
        command.Parameters.AddWithValue("$id", change.AffectedId);
        command.Parameters.AddWithValue("$course", (object?)change.CourseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$location", (object?)change.LocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$old", before is null ? DBNull.Value : (object)JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("$new", change.NewValue!);
        command.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$changed", change.ChangedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$device", change.DeviceId);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$details", details);
        command.Parameters.AddWithValue("$conflict", conflict);
        command.Parameters.AddWithValue("$winner", JsonSerializer.Serialize(winner));
        command.ExecuteNonQuery();
    }
}
