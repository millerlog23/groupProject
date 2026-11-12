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
    }
    public bool ApplyChange(PendingAuditChange change)
{
    // Validate the incoming change.
    if (string.IsNullOrWhiteSpace(change.ClientChangeId))
        throw new ArgumentException("ClientChangeId is required.");

    if (change.UserId <= 0 || change.AffectedId <= 0)
        throw new ArgumentException("User ID and task ID must be positive.");

    if (change.EntityChanged != "Task")
        throw new ArgumentException("Only Task changes are supported.");

    if (change.ChangeType is not ("Create" or "Update" or "Delete"))
        throw new ArgumentException("ChangeType must be Create, Update, or Delete.");

    TaskItem? task = null;

    if (change.ChangeType != "Delete")
    {
        if (string.IsNullOrWhiteSpace(change.NewValue))
            throw new ArgumentException("NewValue is required.");

        task = JsonSerializer.Deserialize<TaskItem>(
            change.NewValue,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (task is null ||
            task.UserId != change.UserId ||
            task.TaskId != change.AffectedId)
        {
            throw new ArgumentException(
                "The task IDs must match the change IDs.");
        }

        if (string.IsNullOrWhiteSpace(task.TaskName))
            throw new ArgumentException("Task name is required.");

        if (task.LastUpdated == default)
            throw new ArgumentException("LastUpdated is required.");
    }

    using var connection = new SqliteConnection(connectionString);
    connection.Open();

    // Save the task and its change record together.
    using var transaction = connection.BeginTransaction();

    using var check = connection.CreateCommand();
    check.Transaction = transaction;
    check.CommandText = """
        SELECT COUNT(*)
        FROM ReceivedChanges
        WHERE ClientChangeId = $changeId;
        """;
    check.Parameters.AddWithValue("$changeId", change.ClientChangeId);

    if (Convert.ToInt64(check.ExecuteScalar()) > 0)
    {
        // A retry of an already accepted change needs no further work.
        transaction.Commit();
        return false;
    }

    using var command = connection.CreateCommand();
    command.Transaction = transaction;

    command.Parameters.AddWithValue("$userId", change.UserId);
    command.Parameters.AddWithValue("$taskId", change.AffectedId);

    if (change.ChangeType == "Delete")
    {
        command.CommandText = """
            DELETE FROM Tasks
            WHERE UserId = $userId AND TaskId = $taskId;
            """;
    }
    else
    {
        command.Parameters.AddWithValue("$name", task!.TaskName);
        command.Parameters.AddWithValue("$location", task.TaskLocation);
        command.Parameters.AddWithValue("$time", task.TaskTime.ToString("O"));
        command.Parameters.AddWithValue("$type", task.TaskType);
        command.Parameters.AddWithValue("$completed", task.Completed ? 1 : 0);
        command.Parameters.AddWithValue(
            "$updated", task.LastUpdated.ToString("O"));

        if (change.ChangeType == "Create")
        {
            command.CommandText = """
                INSERT INTO Tasks
                (
                    UserId, TaskId, TaskName, TaskLocation,
                    TaskTime, TaskType, Completed, LastUpdated
                )
                VALUES
                (
                    $userId, $taskId, $name, $location,
                    $time, $type, $completed, $updated
                );
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Tasks
                SET TaskName = $name,
                    TaskLocation = $location,
                    TaskTime = $time,
                    TaskType = $type,
                    Completed = $completed,
                    LastUpdated = $updated
                WHERE UserId = $userId AND TaskId = $taskId;
                """;
        }
    }

    int affectedRows = command.ExecuteNonQuery();

    if (change.ChangeType == "Update" && affectedRows == 0)
        throw new InvalidOperationException("The task does not exist on the server.");

    using var audit = connection.CreateCommand();
    audit.Transaction = transaction;
    audit.CommandText = """
        INSERT INTO ReceivedChanges
        (
            ClientChangeId, UserId, EntityChanged, ChangeType,
            AffectedId, OldValue, NewValue, ReceivedAt
        )
        VALUES
        (
            $changeId, $userId, $entity, $changeType,
            $taskId, $oldValue, $newValue, $receivedAt
        );
        """;

    audit.Parameters.AddWithValue("$changeId", change.ClientChangeId);
    audit.Parameters.AddWithValue("$userId", change.UserId);
    audit.Parameters.AddWithValue("$entity", change.EntityChanged);
    audit.Parameters.AddWithValue("$changeType", change.ChangeType);
    audit.Parameters.AddWithValue("$taskId", change.AffectedId);
    audit.Parameters.AddWithValue(
        "$oldValue", (object?)change.OldValue ?? DBNull.Value);
    audit.Parameters.AddWithValue(
        "$newValue", (object?)change.NewValue ?? DBNull.Value);
    audit.Parameters.AddWithValue("$receivedAt", DateTime.UtcNow.ToString("O"));

    audit.ExecuteNonQuery();
    transaction.Commit();

    return true;
}
}