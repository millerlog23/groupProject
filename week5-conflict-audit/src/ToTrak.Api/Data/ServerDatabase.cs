using System.Text.Json;
using ToTrak.Api.Models;
using Microsoft.Data.Sqlite;

namespace ToTrak.Api.Data;

public class ServerDatabase
{
    private readonly string connectionString;

    public ServerDatabase(string databasePath)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Tasks
            (
                UserId INTEGER NOT NULL,
                TaskId INTEGER NOT NULL,
                TaskName TEXT NOT NULL,
                TaskLocation INTEGER NOT NULL,
                TaskTime TEXT NOT NULL,
                TaskType TEXT NOT NULL,
                Completed INTEGER NOT NULL,
                LastUpdated TEXT NOT NULL,
                PRIMARY KEY (UserId, TaskId)
            );

            CREATE TABLE IF NOT EXISTS ReceivedChanges
            (
                ServerChangeId INTEGER PRIMARY KEY AUTOINCREMENT,
                ClientChangeId TEXT NOT NULL UNIQUE,
                UserId INTEGER NOT NULL,
                EntityChanged TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                AffectedId INTEGER NOT NULL,
                OldValue TEXT NULL,
                NewValue TEXT NULL,
                ReceivedAt TEXT NOT NULL
            );
            """;

        command.ExecuteNonQuery();
        // Preserve deleted tasks so older changes cannot restore them.
        EnsureColumn(
            connection, "Tasks", "IsDeleted",
            "INTEGER NOT NULL DEFAULT 0");

        EnsureColumn(
            connection, "Tasks", "LastModifiedBy",
            "TEXT NOT NULL DEFAULT ''");

        // Store the source and outcome of each uploaded change.
        EnsureColumn(
            connection, "ReceivedChanges", "ChangedAtUtc",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection, "ReceivedChanges", "DeviceId",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection, "ReceivedChanges", "Outcome",
            "TEXT NOT NULL DEFAULT 'Applied'");

        EnsureColumn(
            connection, "ReceivedChanges", "Details",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection, "ReceivedChanges", "WasConflict",
            "INTEGER NOT NULL DEFAULT 0");

        // Save the winning task snapshot for responses and retry handling.
        EnsureColumn(
            connection, "ReceivedChanges", "ServerValue",
            "TEXT NULL");
    }

    public Dictionary<string, List<Dictionary<string, object?>>> GetDebugSnapshot()
    {
        var snapshot = new Dictionary<string, List<Dictionary<string, object?>>>();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var tableNames = new List<string>();
        using (var tableCommand = connection.CreateCommand())
        {
            tableCommand.CommandText = """
                SELECT name
                FROM sqlite_master
                WHERE type = 'table'
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY name;
                """;

            using var tableReader = tableCommand.ExecuteReader();
            while (tableReader.Read())
            {
                tableNames.Add(tableReader.GetString(0));
            }
        }

        foreach (string tableName in tableNames)
        {
            var rows = new List<Dictionary<string, object?>>();
            using var rowCommand = connection.CreateCommand();
            rowCommand.CommandText = $"SELECT * FROM \"{tableName.Replace("\"", "\"\"")}\";";

            using var rowReader = rowCommand.ExecuteReader();
            while (rowReader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < rowReader.FieldCount; i++)
                {
                    row[rowReader.GetName(i)] = rowReader.IsDBNull(i)
                        ? null
                        : rowReader.GetValue(i);
                }

                rows.Add(row);
            }

            snapshot[tableName] = rows;
        }

        return snapshot;
    }

    private static void EnsureColumn(
    SqliteConnection connection,
    string tableName,
    string columnName,
    string definition)
    {
        // Arguments come only from our fixed schema definitions.
        using var check = connection.CreateCommand();

        check.CommandText =
            $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') " +
            "WHERE name = $columnName;";

        check.Parameters.AddWithValue("$columnName", columnName);

        bool exists = Convert.ToInt64(check.ExecuteScalar()) > 0;

        if (!exists)
        {
            using var alter = connection.CreateCommand();

            alter.CommandText =
                $"ALTER TABLE {tableName} " +
                $"ADD COLUMN {columnName} {definition};";

            alter.ExecuteNonQuery();
        }
    }
    public List<object> GetAuditHistory(int userId)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = """
        SELECT ServerChangeId, ClientChangeId, UserId,
               AffectedId, ChangeType, OldValue, NewValue,
               ChangedAtUtc, DeviceId, Outcome, Details,
               WasConflict, ReceivedAt, ServerValue
        FROM ReceivedChanges
        WHERE UserId = $userId
        ORDER BY ServerChangeId DESC
        LIMIT 100;
        """;

        command.Parameters.AddWithValue("$userId", userId);

        using var reader = command.ExecuteReader();
        var entries = new List<object>();

        while (reader.Read())
        {
            entries.Add(new
            {
                ServerChangeId = reader.GetInt64(0),
                AuditId = reader.GetString(1),
                UserId = reader.GetInt32(2),
                TaskId = reader.GetInt64(3),
                Action = reader.GetString(4),
                OldValue = reader.IsDBNull(5) ? null : reader.GetString(5),
                NewValue = reader.IsDBNull(6) ? null : reader.GetString(6),
                ChangedAtUtc = reader.GetString(7),
                DeviceId = reader.GetString(8),
                Outcome = reader.GetString(9),
                Details = reader.GetString(10),
                WasConflict = reader.GetInt32(11) != 0,
                ReceivedAtUtc = reader.GetString(12),
                ServerValue = reader.IsDBNull(13) ? null : reader.GetString(13)
            });
        }

        return entries;
    }
    public List<ServerChange> GetChanges(
    int userId,
    long after,
    DateTimeOffset? since = null)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();

        command.CommandText = """
        SELECT ServerChangeId, ClientChangeId, UserId,
           EntityChanged, ChangeType, AffectedId,
           COALESCE(ServerValue, NewValue), ReceivedAt
         FROM ReceivedChanges
         WHERE UserId = $userId
         AND ServerChangeId > $after
          AND Outcome = 'Applied'
         AND ($since IS NULL OR ReceivedAt > $since)
         ORDER BY ServerChangeId;
    """;

        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue(
            "$since",
            (object?)since?.UtcDateTime.ToString("O") ?? DBNull.Value);

        using var reader = command.ExecuteReader();

        var changes = new List<ServerChange>();

        while (reader.Read())
        {
            changes.Add(new ServerChange
            {
                ServerChangeId = reader.GetInt64(0),
                ClientChangeId = reader.GetString(1),
                UserId = reader.GetInt32(2),
                EntityChanged = reader.GetString(3),
                ChangeType = reader.GetString(4),
                AffectedId = reader.GetInt64(5),
                NewValue = reader.IsDBNull(6)
                    ? null
                    : reader.GetString(6),
                ReceivedAt = reader.GetString(7)
            });
        }

        return changes;
    }
    public SyncResult ApplyChange(PendingAuditChange change)
    {
        if (string.IsNullOrWhiteSpace(change.ClientChangeId))
            throw new ArgumentException("ClientChangeId is required.");

        if (change.UserId <= 0 || change.AffectedId <= 0)
            throw new ArgumentException("User ID and task ID must be positive.");

        if (change.EntityChanged != "Task")
            throw new ArgumentException("Only Task changes are supported.");

        if (change.ChangeType is not ("Create" or "Update" or "Delete"))
            throw new ArgumentException("Unsupported change type.");

        if (change.ChangedAtUtc == default ||
            change.ChangedAtUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("ChangedAtUtc must be a UTC timestamp.");
        }

        if (string.IsNullOrWhiteSpace(change.DeviceId))
            throw new ArgumentException("DeviceId is required.");

        if (string.IsNullOrWhiteSpace(change.NewValue))
            throw new ArgumentException("A task snapshot is required.");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var incoming = JsonSerializer.Deserialize<TaskItem>(
            change.NewValue, jsonOptions);

        if (incoming is null ||
            incoming.UserId != change.UserId ||
            incoming.TaskId != change.AffectedId ||
            string.IsNullOrWhiteSpace(incoming.TaskName))
        {
            throw new ArgumentException("Invalid task snapshot.");
        }

        if (incoming.LastUpdated.Kind != DateTimeKind.Utc ||
            incoming.LastUpdated != change.ChangedAtUtc ||
            incoming.LastModifiedBy != change.DeviceId ||
            incoming.IsDeleted != (change.ChangeType == "Delete"))
        {
            throw new ArgumentException(
                "Task metadata must match the uploaded change.");
        }

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        // Return the original decision when a request is retried.
        using (var retry = connection.CreateCommand())
        {
            retry.Transaction = transaction;
            retry.CommandText = """
            SELECT UserId, Outcome, Details, WasConflict, ServerValue
            FROM ReceivedChanges
            WHERE ClientChangeId = $id;
            """;
            retry.Parameters.AddWithValue("$id", change.ClientChangeId);

            SyncResult? previous = null;

            using (var reader = retry.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetInt32(0) != change.UserId)
                    {
                        throw new ArgumentException(
                            "ClientChangeId belongs to another user.");
                    }

                    previous = new SyncResult
                    {
                        ClientChangeId = change.ClientChangeId,
                        AlreadyProcessed = true,
                        Outcome = reader.GetString(1),
                        Details = reader.GetString(2),
                        WasConflict = reader.GetInt32(3) != 0,
                        ServerTask = reader.IsDBNull(4)
                            ? null
                            : JsonSerializer.Deserialize<TaskItem>(
                                reader.GetString(4), jsonOptions)
                    };
                }
            }

            if (previous is not null)
            {
                transaction.Commit();
                return previous;
            }
        }

        TaskItem? current = null;

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
            SELECT UserId, TaskId, TaskName, TaskLocation,
                   TaskTime, TaskType, Completed, LastUpdated,
                   IsDeleted, LastModifiedBy
            FROM Tasks
            WHERE UserId = $userId AND TaskId = $taskId;
            """;

            lookup.Parameters.AddWithValue("$userId", change.UserId);
            lookup.Parameters.AddWithValue("$taskId", change.AffectedId);

            using var reader = lookup.ExecuteReader();

            if (reader.Read())
            {
                current = new TaskItem
                {
                    UserId = reader.GetInt32(0),
                    TaskId = reader.GetInt64(1),
                    TaskName = reader.GetString(2),
                    TaskLocation = reader.GetInt32(3),
                    TaskTime = DateTime.Parse(
                        reader.GetString(4),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    TaskType = reader.GetString(5),
                    Completed = reader.GetInt32(6) != 0,
                    LastUpdated = DateTime.Parse(
                        reader.GetString(7),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    IsDeleted = reader.GetInt32(8) != 0,
                    LastModifiedBy = reader.GetString(9)
                };
            }
        }

        // A duplicate creation could be a different task with the same ID.
        // Do not overwrite it as though it were an edit.
        if (current is not null && change.ChangeType == "Create")
        {
            throw new InvalidOperationException(
                "Task ID collision: this task ID already exists.");
        }

        bool wasConflict = current is null
            ? change.ChangeType != "Create"
            : change.BaseLastUpdatedUtc is null ||
              change.BaseLastUpdatedUtc.Value.Ticks != current.LastUpdated.Ticks;

        // Equal timestamps keep the existing server version.
        bool incomingWins = current is null ||
            incoming.LastUpdated.Ticks > current.LastUpdated.Ticks;

        TaskItem winner = incomingWins ? incoming : current!;

        string outcome = incomingWins ? "Applied" : "ServerKept";

        string details = current is null
            ? "No server version existed; incoming version stored."
            : incoming.LastUpdated.Ticks == current.LastUpdated.Ticks
                ? "Equal timestamps; existing server version kept."
                : incomingWins
                    ? "Incoming version has the newer UTC timestamp."
                    : "Server version has the newer UTC timestamp.";

        if (incomingWins)
        {
            using var save = connection.CreateCommand();
            save.Transaction = transaction;

            save.CommandText = """
            INSERT INTO Tasks
            (
                UserId, TaskId, TaskName, TaskLocation,
                TaskTime, TaskType, Completed, LastUpdated,
                IsDeleted, LastModifiedBy
            )
            VALUES
            (
                $userId, $taskId, $name, $location,
                $time, $type, $completed, $updated,
                $deleted, $device
            )
            ON CONFLICT(UserId, TaskId) DO UPDATE SET
                TaskName = excluded.TaskName,
                TaskLocation = excluded.TaskLocation,
                TaskTime = excluded.TaskTime,
                TaskType = excluded.TaskType,
                Completed = excluded.Completed,
                LastUpdated = excluded.LastUpdated,
                IsDeleted = excluded.IsDeleted,
                LastModifiedBy = excluded.LastModifiedBy;
            """;

            save.Parameters.AddWithValue("$userId", winner.UserId);
            save.Parameters.AddWithValue("$taskId", winner.TaskId);
            save.Parameters.AddWithValue("$name", winner.TaskName);
            save.Parameters.AddWithValue("$location", winner.TaskLocation);
            save.Parameters.AddWithValue("$time", winner.TaskTime.ToString("O"));
            save.Parameters.AddWithValue("$type", winner.TaskType);
            save.Parameters.AddWithValue("$completed", winner.Completed ? 1 : 0);
            save.Parameters.AddWithValue("$updated", winner.LastUpdated.ToString("O"));
            save.Parameters.AddWithValue("$deleted", winner.IsDeleted ? 1 : 0);
            save.Parameters.AddWithValue("$device", winner.LastModifiedBy);

            save.ExecuteNonQuery();
        }

        // Keep an audit record even when the incoming change loses.
        using var audit = connection.CreateCommand();
        audit.Transaction = transaction;

        audit.CommandText = """
        INSERT INTO ReceivedChanges
        (
            ClientChangeId, UserId, EntityChanged, ChangeType,
            AffectedId, OldValue, NewValue, ReceivedAt,
            ChangedAtUtc, DeviceId, Outcome, Details,
            WasConflict, ServerValue
        )
        VALUES
        (
            $id, $userId, $entity, $action,
            $taskId, $old, $new, $received,
            $changed, $device, $outcome, $details,
            $conflict, $winner
        );
        """;

        audit.Parameters.AddWithValue("$id", change.ClientChangeId);
        audit.Parameters.AddWithValue("$userId", change.UserId);
        audit.Parameters.AddWithValue("$entity", change.EntityChanged);
        audit.Parameters.AddWithValue("$action", change.ChangeType);
        audit.Parameters.AddWithValue("$taskId", change.AffectedId);
        audit.Parameters.AddWithValue(
            "$old", (object?)change.OldValue ?? DBNull.Value);
        audit.Parameters.AddWithValue("$new", change.NewValue);
        audit.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O"));
        audit.Parameters.AddWithValue("$changed", change.ChangedAtUtc.ToString("O"));
        audit.Parameters.AddWithValue("$device", change.DeviceId);
        audit.Parameters.AddWithValue("$outcome", outcome);
        audit.Parameters.AddWithValue("$details", details);
        audit.Parameters.AddWithValue("$conflict", wasConflict ? 1 : 0);
        audit.Parameters.AddWithValue("$winner", JsonSerializer.Serialize(winner));

        audit.ExecuteNonQuery();
        transaction.Commit();

        return new SyncResult
        {
            ClientChangeId = change.ClientChangeId,
            WasConflict = wasConflict,
            Outcome = outcome,
            Details = details,
            ServerTask = winner
        };
    }
}