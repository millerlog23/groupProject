using System.Text.Json;
using ToTrak.Api.Models;
using Microsoft.Data.Sqlite;

namespace ToTrak.Api.Data;

public partial class ServerDatabase
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

            CREATE TABLE IF NOT EXISTS Assignments
            (
                UserId INTEGER NOT NULL,
                AssignmentId INTEGER NOT NULL,
                AssignmentName TEXT NOT NULL,
                CourseId INTEGER NOT NULL DEFAULT 1,
                AssignmentLocation INTEGER NOT NULL,
                AssignmentTime TEXT NOT NULL,
                AssignmentType TEXT NOT NULL,
                Completed INTEGER NOT NULL,
                LastUpdated TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, AssignmentId)
            );

            CREATE TABLE IF NOT EXISTS Courses
            (
                UserId INTEGER NOT NULL,
                CourseId INTEGER NOT NULL,
                CourseName TEXT NOT NULL,
                LastUpdated TEXT NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, CourseId)
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
        InitializeDomainSchema(connection);
        // Preserve deleted tasks so older changes cannot restore them.
        EnsureColumn(
            connection, "Tasks", "IsDeleted",
            "INTEGER NOT NULL DEFAULT 0");

        EnsureColumn(
            connection, "Tasks", "LastModifiedBy",
            "TEXT NOT NULL DEFAULT ''");

        EnsureColumn(
            connection, "Assignments", "CourseId", "INTEGER NOT NULL DEFAULT 1");

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

        EnsureColumn(
            connection, "ReceivedChanges", "TaskId", "INTEGER NULL");

        EnsureColumn(
            connection, "ReceivedChanges", "AssignmentId", "INTEGER NULL");

        EnsureColumn(
            connection, "ReceivedChanges", "CourseId", "INTEGER NULL");

        using (var backfill = connection.CreateCommand())
        {
            backfill.CommandText = """
                UPDATE ReceivedChanges
                SET TaskId = AffectedId
                WHERE EntityChanged = 'Task' AND TaskId IS NULL;
                UPDATE ReceivedChanges
                SET AssignmentId = AffectedId
                WHERE EntityChanged = 'Assignment' AND AssignmentId IS NULL;
                UPDATE ReceivedChanges
                SET CourseId = AffectedId
                WHERE EntityChanged = 'Course' AND CourseId IS NULL;
                """;
            backfill.ExecuteNonQuery();
        }

        using (var seedCourses = connection.CreateCommand())
        {
            seedCourses.CommandText = """
                INSERT OR IGNORE INTO Courses
                    (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                SELECT DISTINCT UserId, 1, 'Unassigned', $now, 0, ''
                FROM Assignments WHERE CourseId = 1;
                """;
            seedCourses.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            seedCourses.ExecuteNonQuery();
        }
    }

    public Dictionary<string, List<Dictionary<string, object?>>> GetDebugSnapshot(int userId)
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
            if (tableName is "Users" or "Sessions") continue;
            var rows = new List<Dictionary<string, object?>>();
            using var rowCommand = connection.CreateCommand();
            rowCommand.CommandText = $"SELECT * FROM \"{tableName.Replace("\"", "\"\"")}\";";

            rowCommand.CommandText = rowCommand.CommandText.TrimEnd(';') + " WHERE UserId = $userId;";
            rowCommand.Parameters.AddWithValue("$userId", userId);

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
                TaskId, AssignmentId, CourseId, ChangeType, OldValue, NewValue,
               ChangedAtUtc, DeviceId, Outcome, Details,
               WasConflict, ReceivedAt, ServerValue, EntityChanged, AffectedId, LocationId
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
                ChangeId = reader.GetInt64(0),
                ChangeTime = reader.GetString(14),
                EntityChanged = reader.GetString(16),
                AffectedId = reader.GetInt64(17),
                LocationId = reader.IsDBNull(18) ? (int?)null : reader.GetInt32(18),
                ChangeType = reader.GetString(6),
                ServerChangeId = reader.GetInt64(0),
                AuditId = reader.GetString(1),
                UserId = reader.GetInt32(2),
                TaskId = reader.IsDBNull(3) ? (long?)null : reader.GetInt64(3),
                AssignmentId = reader.IsDBNull(4) ? (long?)null : reader.GetInt64(4),
                CourseId = reader.IsDBNull(5) ? (long?)null : reader.GetInt64(5),
                Action = reader.GetString(6),
                OldValue = reader.IsDBNull(7) ? null : reader.GetString(7),
                NewValue = reader.IsDBNull(8) ? null : reader.GetString(8),
                ChangedAtUtc = reader.GetString(9),
                DeviceId = reader.GetString(10),
                Outcome = reader.GetString(11),
                Details = reader.GetString(12),
                WasConflict = reader.GetInt32(13) != 0,
                ReceivedAtUtc = reader.GetString(14),
                ServerValue = reader.IsDBNull(15) ? null : reader.GetString(15)
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
           EntityChanged, ChangeType, AffectedId, TaskId, AssignmentId, CourseId,
           COALESCE(ServerValue, NewValue), ReceivedAt, LocationId
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
                TaskId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                AssignmentId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                CourseId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                NewValue = reader.IsDBNull(9)
                    ? null
                    : reader.GetString(9),
                ReceivedAt = reader.GetString(10),
                LocationId = reader.IsDBNull(11) ? null : reader.GetInt32(11)
            });
        }

        return changes;
    }
    public SyncResult ApplyChange(PendingAuditChange change)
    {
        if (change.EntityChanged == "Assignment")
            return ApplyAssignmentChange(change);
        if (change.EntityChanged is "Course" or "Location")
            return ApplyNamedChange(change);

        if (change.EntityChanged != "Task")
            throw new ArgumentException("Only Task, Assignment, Course, and Location changes are supported.");

        if (string.IsNullOrWhiteSpace(change.ClientChangeId))
            throw new ArgumentException("ClientChangeId is required.");

        if (change.UserId <= 0 || change.AffectedId <= 0 ||
            change.TaskId != change.AffectedId || change.AssignmentId is not null ||
            change.CourseId is not null || change.LocationId is not null)
            throw new ArgumentException("User ID and task ID must be positive.");

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
            string.IsNullOrWhiteSpace(incoming.TaskName) ||
            string.IsNullOrWhiteSpace(incoming.TaskType) || incoming.TaskTime == default || incoming.TaskLocation < 0)
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

        if (incomingWins && !incoming.IsDeleted)
            ValidateTaskLocation(connection, transaction, incoming.UserId, incoming.TaskLocation);

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
            AffectedId, TaskId, AssignmentId, CourseId, OldValue, NewValue, ReceivedAt,
            ChangedAtUtc, DeviceId, Outcome, Details,
            WasConflict, ServerValue
        )
        VALUES
        (
            $id, $userId, $entity, $action,
            $taskId, $taskId, NULL, NULL, $old, $new, $received,
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
            "$old", current is null ? DBNull.Value : (object)JsonSerializer.Serialize(current));
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

    private SyncResult ApplyAssignmentChange(PendingAuditChange change)
    {
        if (string.IsNullOrWhiteSpace(change.ClientChangeId))
            throw new ArgumentException("ClientChangeId is required.");

        if (change.UserId <= 0 || change.AffectedId <= 0 ||
            change.AssignmentId != change.AffectedId || change.TaskId is not null ||
            change.CourseId is not null || change.LocationId is not null)
            throw new ArgumentException("User ID and assignment ID must be positive.");

        if (change.ChangeType is not ("Create" or "Update" or "Delete"))
            throw new ArgumentException("Unsupported change type.");

        if (change.ChangedAtUtc == default ||
            change.ChangedAtUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("ChangedAtUtc must be a UTC timestamp.");

        if (string.IsNullOrWhiteSpace(change.DeviceId) ||
            string.IsNullOrWhiteSpace(change.NewValue))
            throw new ArgumentException("Device ID and assignment snapshot are required.");

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        var incoming = JsonSerializer.Deserialize<AssignmentItem>(change.NewValue, jsonOptions);

        if (incoming is null || incoming.UserId != change.UserId ||
            incoming.AssignmentId != change.AffectedId ||
            incoming.CourseId <= 0 ||
            string.IsNullOrWhiteSpace(incoming.AssignmentName) ||
            string.IsNullOrWhiteSpace(incoming.AssignmentType) || incoming.AssignmentTime == default ||
            incoming.EstimatedTime < 0 ||
            incoming.LastUpdated.Kind != DateTimeKind.Utc ||
            incoming.LastUpdated != change.ChangedAtUtc ||
            incoming.LastModifiedBy != change.DeviceId ||
            incoming.IsDeleted != (change.ChangeType == "Delete"))
            throw new ArgumentException("Invalid assignment snapshot.");

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var retry = connection.CreateCommand())
        {
            retry.Transaction = transaction;
            retry.CommandText = """
                SELECT UserId, Outcome, Details, WasConflict, ServerValue
                FROM ReceivedChanges WHERE ClientChangeId = $id;
                """;
            retry.Parameters.AddWithValue("$id", change.ClientChangeId);

            SyncResult? previous = null;
            using (var reader = retry.ExecuteReader())
            {
                if (reader.Read())
                {
                    if (reader.GetInt32(0) != change.UserId)
                        throw new ArgumentException("ClientChangeId belongs to another user.");

                    previous = new SyncResult
                    {
                        ClientChangeId = change.ClientChangeId,
                        AlreadyProcessed = true,
                        Outcome = reader.GetString(1),
                        Details = reader.GetString(2),
                        WasConflict = reader.GetInt32(3) != 0,
                        ServerAssignment = reader.IsDBNull(4)
                            ? null
                            : JsonSerializer.Deserialize<AssignmentItem>(reader.GetString(4), jsonOptions)
                    };
                }
            }

            if (previous is not null)
            {
                transaction.Commit();
                return previous;
            }
        }

        AssignmentItem? current = null;

        using (var courseCheck = connection.CreateCommand())
        {
            courseCheck.Transaction = transaction;
            courseCheck.CommandText = """
                SELECT COUNT(*) FROM Courses
                WHERE UserId = $userId AND CourseId = $courseId AND IsDeleted = 0;
                """;
            courseCheck.Parameters.AddWithValue("$userId", change.UserId);
            courseCheck.Parameters.AddWithValue("$courseId", incoming.CourseId);
            if (!incoming.IsDeleted && Convert.ToInt32(courseCheck.ExecuteScalar()) != 1)
                throw new ArgumentException("The assignment references an unknown course.");
        }

        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = """
                SELECT UserId, AssignmentId, AssignmentName, CourseId, AssignmentLocation,
                       AssignmentTime, AssignmentType, Completed, LastUpdated,
                       IsDeleted, LastModifiedBy, AssignmentDescription, EstimatedTime
                FROM Assignments WHERE UserId = $userId AND AssignmentId = $id;
                """;
            lookup.Parameters.AddWithValue("$userId", change.UserId);
            lookup.Parameters.AddWithValue("$id", change.AffectedId);
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                current = new AssignmentItem
                {
                    UserId = reader.GetInt32(0),
                    AssignmentId = reader.GetInt64(1),
                    AssignmentName = reader.GetString(2),
                    CourseId = reader.GetInt64(3),
                    AssignmentLocation = reader.GetInt32(4),
                    AssignmentTime = DateTime.Parse(reader.GetString(5),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    AssignmentType = reader.GetString(6),
                    Completed = reader.GetInt32(7) != 0,
                    LastUpdated = DateTime.Parse(reader.GetString(8),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind),
                    IsDeleted = reader.GetInt32(9) != 0,
                    LastModifiedBy = reader.GetString(10),
                    AssignmentDescription = reader.IsDBNull(11) ? null : reader.GetString(11),
                    EstimatedTime = reader.IsDBNull(12) ? null : reader.GetInt32(12)
                };
            }
        }

        if (current is not null && change.ChangeType == "Create")
            throw new InvalidOperationException("Assignment ID collision: this assignment ID already exists.");

        bool conflict = current is null
            ? change.ChangeType != "Create"
            : change.BaseLastUpdatedUtc is null ||
              change.BaseLastUpdatedUtc.Value.Ticks != current.LastUpdated.Ticks;
        bool incomingWins = current is null || incoming.LastUpdated.Ticks > current.LastUpdated.Ticks;
        AssignmentItem winner = incomingWins ? incoming : current!;
        string outcome = incomingWins ? "Applied" : "ServerKept";
        string details = current is null
            ? "No server version existed; incoming version stored."
            : incomingWins
                ? "Incoming version has the newer UTC timestamp."
                : "Server version has the newer UTC timestamp.";

        if (incomingWins)
        {
            using var save = connection.CreateCommand();
            save.Transaction = transaction;
            save.CommandText = """
                INSERT INTO Assignments
                (
                    UserId, AssignmentId, AssignmentName, CourseId, AssignmentLocation,
                    AssignmentTime, AssignmentType, Completed, LastUpdated,
                    IsDeleted, LastModifiedBy, AssignmentDescription, EstimatedTime
                )
                VALUES ($userId, $id, $name, $courseId, $location, $time, $type,
                        $completed, $updated, $deleted, $device, $description, $estimated)
                ON CONFLICT(UserId, AssignmentId) DO UPDATE SET
                    AssignmentName = excluded.AssignmentName,
                    CourseId = excluded.CourseId,
                    AssignmentDescription = excluded.AssignmentDescription,
                    EstimatedTime = excluded.EstimatedTime,
                    AssignmentLocation = excluded.AssignmentLocation,
                    AssignmentTime = excluded.AssignmentTime,
                    AssignmentType = excluded.AssignmentType,
                    Completed = excluded.Completed,
                    LastUpdated = excluded.LastUpdated,
                    IsDeleted = excluded.IsDeleted,
                    LastModifiedBy = excluded.LastModifiedBy;
                """;
            save.Parameters.AddWithValue("$userId", winner.UserId);
            save.Parameters.AddWithValue("$id", winner.AssignmentId);
            save.Parameters.AddWithValue("$name", winner.AssignmentName);
            save.Parameters.AddWithValue("$courseId", winner.CourseId);
            save.Parameters.AddWithValue("$location", winner.AssignmentLocation);
            save.Parameters.AddWithValue("$description", (object?)winner.AssignmentDescription ?? DBNull.Value);
            save.Parameters.AddWithValue("$estimated", (object?)winner.EstimatedTime ?? DBNull.Value);
            save.Parameters.AddWithValue("$time", winner.AssignmentTime.ToString("O"));
            save.Parameters.AddWithValue("$type", winner.AssignmentType);
            save.Parameters.AddWithValue("$completed", winner.Completed ? 1 : 0);
            save.Parameters.AddWithValue("$updated", winner.LastUpdated.ToString("O"));
            save.Parameters.AddWithValue("$deleted", winner.IsDeleted ? 1 : 0);
            save.Parameters.AddWithValue("$device", winner.LastModifiedBy);
            save.ExecuteNonQuery();
        }

        using (var audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = """
                INSERT INTO ReceivedChanges
                (
                    ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId,
                    TaskId, AssignmentId, CourseId, OldValue, NewValue, ReceivedAt,
                    ChangedAtUtc, DeviceId, Outcome, Details, WasConflict, ServerValue
                )
                VALUES
                (
                    $id, $userId, 'Assignment', $action, $affectedId,
                    NULL, $assignmentId, NULL, $old, $new, $received,
                    $changed, $device, $outcome, $details, $conflict, $winner
                );
                """;
            audit.Parameters.AddWithValue("$id", change.ClientChangeId);
            audit.Parameters.AddWithValue("$userId", change.UserId);
            audit.Parameters.AddWithValue("$action", change.ChangeType);
            audit.Parameters.AddWithValue("$affectedId", change.AffectedId);
            audit.Parameters.AddWithValue("$assignmentId", change.AffectedId);
            audit.Parameters.AddWithValue("$old", current is null ? DBNull.Value : (object)JsonSerializer.Serialize(current));
            audit.Parameters.AddWithValue("$new", change.NewValue);
            audit.Parameters.AddWithValue("$received", DateTime.UtcNow.ToString("O"));
            audit.Parameters.AddWithValue("$changed", change.ChangedAtUtc.ToString("O"));
            audit.Parameters.AddWithValue("$device", change.DeviceId);
            audit.Parameters.AddWithValue("$outcome", outcome);
            audit.Parameters.AddWithValue("$details", details);
            audit.Parameters.AddWithValue("$conflict", conflict ? 1 : 0);
            audit.Parameters.AddWithValue("$winner", JsonSerializer.Serialize(winner));
            audit.ExecuteNonQuery();
        }

        transaction.Commit();
        return new SyncResult
        {
            ClientChangeId = change.ClientChangeId,
            WasConflict = conflict,
            Outcome = outcome,
            Details = details,
            ServerAssignment = winner
        };
    }

}
