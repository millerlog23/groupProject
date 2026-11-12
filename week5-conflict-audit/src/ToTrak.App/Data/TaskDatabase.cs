using ToTrak.App.Models;
using Microsoft.Maui.Storage;
using SQLite;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Maui.Networking;

namespace ToTrak.App.Data;

public class TaskDatabase
{
    private readonly SQLiteAsyncConnection database;

    private readonly string deviceId = GetOrCreateDeviceId();

    public TaskDatabase()
    {
        string databasePath = Path.Combine(
            FileSystem.AppDataDirectory,
            "totrak-week5.db3");

        database = new SQLiteAsyncConnection(databasePath);
    }

    public async Task InitializeAsync()
    {
        await database.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS TaskItem
            (
                UserId INTEGER NOT NULL,
                TaskId INTEGER NOT NULL,
                TaskName TEXT NOT NULL,
                TaskLocation INTEGER NOT NULL,
                TaskTime INTEGER NOT NULL,
                TaskType TEXT NOT NULL,
                Completed INTEGER NOT NULL,
                LastUpdated INTEGER NOT NULL,
                PRIMARY KEY (UserId, TaskId)
            );
            """);

        await database.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS PendingAuditChange
            (
                ClientChangeId TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                EntityChanged TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                AffectedId INTEGER NOT NULL,
                OldValue TEXT NULL,
                NewValue TEXT NULL
            );
            """);
        // Upgrade existing Week 4 databases without deleting their data.
        await EnsureColumnAsync(
            "TaskItem", "IsDeleted",
            "INTEGER NOT NULL DEFAULT 0");

        await EnsureColumnAsync(
            "TaskItem", "LastModifiedBy",
            "TEXT NOT NULL DEFAULT ''");

        await EnsureColumnAsync(
            "PendingAuditChange", "ChangedAtUtc",
            "INTEGER NOT NULL DEFAULT 0");

        await EnsureColumnAsync(
            "PendingAuditChange", "BaseLastUpdatedUtc",
            "INTEGER NULL");

        await EnsureColumnAsync(
            "PendingAuditChange", "DeviceId",
            "TEXT NOT NULL DEFAULT ''");

        // Permanent history: these records remain after successful sync.
        await database.ExecuteAsync("""
    CREATE TABLE IF NOT EXISTS AuditLog
    (
        AuditId TEXT PRIMARY KEY,
        UserId INTEGER NOT NULL,
        TaskId INTEGER NOT NULL,
        Action TEXT NOT NULL,
        OldValue TEXT NULL,
        NewValue TEXT NULL,
        OccurredAtUtc INTEGER NOT NULL,
        DeviceId TEXT NOT NULL,
        Outcome TEXT NOT NULL,
        Details TEXT NOT NULL,
        IsSynced INTEGER NOT NULL DEFAULT 0
    );
    """);
    }
    private static string GetOrCreateDeviceId()
    {
        string id = Preferences.Get("ToTrak.DeviceId", "");

        if (string.IsNullOrWhiteSpace(id))
        {
            id = Guid.NewGuid().ToString("N");
            Preferences.Set("ToTrak.DeviceId", id);
        }

        return id;
    }

    private void RecordLocalChange(
        SQLiteConnection connection,
        string action,
        TaskItem task,
        TaskItem? originalTask)
    {
        string changeId = Guid.NewGuid().ToString("N");

        string? oldValue = originalTask is null
            ? null
            : JsonSerializer.Serialize(originalTask);

        // Keep the deleted task's snapshot too, including its timestamp.
        string newValue = JsonSerializer.Serialize(task);

        DateTime? baseLastUpdatedUtc = originalTask?.LastUpdated;

        connection.Execute(
            """
        INSERT INTO PendingAuditChange
        (
            ClientChangeId, UserId, EntityChanged,
            ChangeType, AffectedId, OldValue, NewValue,
            ChangedAtUtc, BaseLastUpdatedUtc, DeviceId
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
            changeId,
            task.UserId,
            "Task",
            action,
            task.TaskId,
            oldValue,
            newValue,
            task.LastUpdated,
            baseLastUpdatedUtc,
            deviceId);

        connection.Execute(
            """
        INSERT INTO AuditLog
        (
            AuditId, UserId, TaskId, Action,
            OldValue, NewValue, OccurredAtUtc,
            DeviceId, Outcome, Details, IsSynced
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
            changeId,
            task.UserId,
            task.TaskId,
            action,
            oldValue,
            newValue,
            task.LastUpdated,
            deviceId,
            "Pending",
            "Saved locally; waiting for server confirmation.",
            false);
    }
    private async Task EnsureColumnAsync(
    string tableName,
    string columnName,
    string definition)
    {
        // Arguments come only from our fixed schema definitions.
        int exists = await database.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') " +
            "WHERE name = ?",
            columnName);

        if (exists == 0)
        {
            await database.ExecuteAsync(
                $"ALTER TABLE {tableName} " +
                $"ADD COLUMN {columnName} {definition}");
        }
    }
    private async Task InitializePullStateAsync()
    {
        await InitializeAsync();

        await database.ExecuteAsync("""
        CREATE TABLE IF NOT EXISTS PullState
        (
            Scope TEXT PRIMARY KEY,
            LastChangeId INTEGER NOT NULL
        );
        """);
    }

    public async Task<long> GetPullCursorAsync(string scope)
    {
        await InitializePullStateAsync();

        return await database.ExecuteScalarAsync<long>(
            """
        SELECT COALESCE(
            (SELECT LastChangeId FROM PullState WHERE Scope = ?),
            0
        )
        """,
            scope);
    }
    public async Task<(int Applied, string? PauseReason)>
    ApplyIncomingChangesAsync(
        string scope,
        int userId,
        List<ServerChange> changes)
    {
        await InitializePullStateAsync();

        int applied = 0;
        string? pauseReason = null;

        await database.RunInTransactionAsync(connection =>
        {
            long cursor = connection.ExecuteScalar<long>(
                """
            SELECT COALESCE(
                (SELECT LastChangeId FROM PullState WHERE Scope = ?),
                0
            )
            """,
                scope);

            foreach (var change in changes.OrderBy(c => c.ServerChangeId))
            {
                if (change.ServerChangeId <= cursor)
                    continue;

                if (change.UserId != userId ||
                    change.EntityChanged != "Task" ||
                    change.AffectedId <= 0 ||
                    change.ChangeType is not ("Create" or "Update" or "Delete"))
                {
                    throw new InvalidOperationException(
                        "The server returned an invalid task change.");
                }

                int pending = connection.ExecuteScalar<int>(
                    """
                SELECT COUNT(*) FROM PendingAuditChange
                WHERE UserId = ? AND AffectedId = ?
                """,
                    userId,
                    change.AffectedId);

                if (pending > 0)
                {
                    pauseReason = "local changes must be pushed first";
                    break;
                }

                // Old Week 4 deletion records did not contain a snapshot.
                // Replay their deletion without removing the local row.
                if (change.ChangeType == "Delete" &&
                    string.IsNullOrWhiteSpace(change.NewValue))
                {
                    connection.Execute(
                        """
                    UPDATE TaskItem
                    SET IsDeleted = 1
                    WHERE UserId = ? AND TaskId = ?
                    """,
                        userId,
                        change.AffectedId);

                    cursor = change.ServerChangeId;
                    applied++;
                    continue;
                }

                var task = JsonSerializer.Deserialize<TaskItem>(
                    change.NewValue
                        ?? throw new InvalidOperationException(
                            "Missing downloaded task data."),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                if (task is null ||
                    task.UserId != userId ||
                    task.TaskId != change.AffectedId ||
                    string.IsNullOrWhiteSpace(task.TaskName) ||
                    task.LastUpdated == default ||
                    task.IsDeleted != (change.ChangeType == "Delete"))
                {
                    throw new InvalidOperationException(
                        "Invalid downloaded task data.");
                }

                // Replay the accepted server history in server-change order.
                // No outgoing change is generated for downloaded data.
                connection.Execute(
                    """
                INSERT INTO TaskItem
                (
                    UserId, TaskId, TaskName, TaskLocation,
                    TaskTime, TaskType, Completed, LastUpdated,
                    IsDeleted, LastModifiedBy
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                ON CONFLICT(UserId, TaskId) DO UPDATE SET
                    TaskName = excluded.TaskName,
                    TaskLocation = excluded.TaskLocation,
                    TaskTime = excluded.TaskTime,
                    TaskType = excluded.TaskType,
                    Completed = excluded.Completed,
                    LastUpdated = excluded.LastUpdated,
                    IsDeleted = excluded.IsDeleted,
                    LastModifiedBy = excluded.LastModifiedBy;
                """,
                    task.UserId,
                    task.TaskId,
                    task.TaskName,
                    task.TaskLocation,
                    task.TaskTime,
                    task.TaskType,
                    task.Completed,
                    task.LastUpdated,
                    task.IsDeleted,
                    task.LastModifiedBy);

                cursor = change.ServerChangeId;
                applied++;
            }

            connection.Execute(
                """
            INSERT OR REPLACE INTO PullState (Scope, LastChangeId)
            VALUES (?, ?)
            """,
                scope,
                cursor);
        });

        return (applied, pauseReason);
    }
    public async Task AddTaskAsync(TaskItem task)
    {
        await InitializeAsync();

        await database.RunInTransactionAsync(connection =>
        {
            // Generate a positive 63-bit ID and check this device's database.
            do
            {
                byte[] bytes =
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);

                task.TaskId = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            }
            while (
                task.TaskId == 0 ||
                connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM TaskItem WHERE UserId = ? AND TaskId = ?",
                    task.UserId,
                    task.TaskId) > 0);

            task.LastUpdated = DateTime.UtcNow;
            task.LastModifiedBy = deviceId;
            task.IsDeleted = false;

            connection.Execute(
                """
            INSERT INTO TaskItem
            (
                UserId, TaskId, TaskName, TaskLocation,
                TaskTime, TaskType, Completed, LastUpdated,
                IsDeleted, LastModifiedBy
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
                task.UserId,
                task.TaskId,
                task.TaskName,
                task.TaskLocation,
                task.TaskTime,
                task.TaskType,
                task.Completed,
                task.LastUpdated,
                task.IsDeleted,
                task.LastModifiedBy);

            RecordLocalChange(connection, "Create", task, null);
        });
    }
    public async Task<List<AuditEntry>> GetAuditEntriesAsync(int userId)
    {
        await InitializeAsync();

        return await database.QueryAsync<AuditEntry>(
            """
        SELECT * FROM AuditLog
        WHERE UserId = ?
        ORDER BY OccurredAtUtc DESC, rowid DESC
        LIMIT 50
        """,
            userId);
    }
    public async Task<List<TaskItem>> GetTasksAsync(int userId)
    {
        await InitializeAsync();

        return await database.QueryAsync<TaskItem>(
            """
        SELECT * FROM TaskItem
        WHERE UserId = ? AND IsDeleted = 0
        ORDER BY TaskTime
        """,
            userId);
    }

    public async Task<List<PendingAuditChange>> GetPendingChangesAsync(int userId)
    {
        await InitializeAsync();

        var changes = await database.QueryAsync<PendingAuditChange>(
            """
        SELECT * FROM PendingAuditChange
        WHERE UserId = ?
        ORDER BY rowid
        """,
            userId);

        foreach (var change in changes)
        {
            // These values were saved as UTC ticks.
            // Restore the UTC designation before JSON serialization.
            change.ChangedAtUtc = DateTime.SpecifyKind(
                change.ChangedAtUtc,
                DateTimeKind.Utc);

            if (change.BaseLastUpdatedUtc.HasValue)
            {
                change.BaseLastUpdatedUtc = DateTime.SpecifyKind(
                    change.BaseLastUpdatedUtc.Value,
                    DateTimeKind.Utc);
            }
        }

        return changes;
    }
    public async Task AcknowledgeChangeAsync(
    PendingAuditChange change,
    SyncResult result)
    {
        await InitializeAsync();

        if (!result.Success ||
            result.ClientChangeId != change.ClientChangeId ||
            result.Outcome is not ("Applied" or "ServerKept"))
        {
            throw new InvalidOperationException(
                "The server returned an invalid sync result.");
        }

        var winner = result.ServerTask;

        if (winner is null ||
            winner.UserId != change.UserId ||
            winner.TaskId != change.AffectedId ||
            winner.LastUpdated == default ||
            string.IsNullOrWhiteSpace(winner.TaskName))
        {
            throw new InvalidOperationException(
                "The server did not return a valid winning task.");
        }

        await database.RunInTransactionAsync(connection =>
        {
            int updated = connection.Execute(
                """
            UPDATE AuditLog
            SET Outcome = ?, Details = ?, IsSynced = 1
            WHERE AuditId = ? AND UserId = ?
            """,
                result.Outcome,
                (result.WasConflict ? "Conflict resolved. " : "") +
                    result.Details,
                change.ClientChangeId,
                change.UserId);

            if (updated != 1)
            {
                throw new InvalidOperationException(
                    "The local audit entry is missing.");
            }

            connection.Execute(
                """
            DELETE FROM PendingAuditChange
            WHERE ClientChangeId = ? AND UserId = ?
            """,
                change.ClientChangeId,
                change.UserId);

            int remaining = connection.ExecuteScalar<int>(
                """
            SELECT COUNT(*) FROM PendingAuditChange
            WHERE UserId = ? AND AffectedId = ?
            """,
                change.UserId,
                change.AffectedId);

            // Preserve local edits that are still waiting to upload.
            if (remaining > 0)
                return;

            connection.Execute(
                """
            INSERT INTO TaskItem
            (
                UserId, TaskId, TaskName, TaskLocation,
                TaskTime, TaskType, Completed, LastUpdated,
                IsDeleted, LastModifiedBy
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(UserId, TaskId) DO UPDATE SET
                TaskName = excluded.TaskName,
                TaskLocation = excluded.TaskLocation,
                TaskTime = excluded.TaskTime,
                TaskType = excluded.TaskType,
                Completed = excluded.Completed,
                LastUpdated = excluded.LastUpdated,
                IsDeleted = excluded.IsDeleted,
                LastModifiedBy = excluded.LastModifiedBy
            WHERE excluded.LastUpdated >= TaskItem.LastUpdated;
            """,
                winner.UserId,
                winner.TaskId,
                winner.TaskName,
                winner.TaskLocation,
                winner.TaskTime,
                winner.TaskType,
                winner.Completed,
                winner.LastUpdated,
                winner.IsDeleted,
                winner.LastModifiedBy);
        });
    }

    public async Task UpdateTaskAsync(TaskItem task, TaskItem originalTask)
    {
        await InitializeAsync();

        // Ensure this edit is newer than the version being edited.
        task.LastUpdated = new DateTime(
            Math.Max(
                DateTime.UtcNow.Ticks,
                originalTask.LastUpdated.Ticks + 1),
            DateTimeKind.Utc);

        task.LastModifiedBy = deviceId;
        task.IsDeleted = false;

        await database.RunInTransactionAsync(connection =>
        {
            int updated = connection.Execute(
                """
            UPDATE TaskItem
            SET TaskName = ?, TaskLocation = ?, TaskTime = ?,
                TaskType = ?, Completed = ?, LastUpdated = ?,
                IsDeleted = ?, LastModifiedBy = ?
            WHERE UserId = ? AND TaskId = ? AND IsDeleted = 0
            """,
                task.TaskName,
                task.TaskLocation,
                task.TaskTime,
                task.TaskType,
                task.Completed,
                task.LastUpdated,
                task.IsDeleted,
                task.LastModifiedBy,
                task.UserId,
                task.TaskId);

            if (updated != 1)
            {
                throw new InvalidOperationException(
                    "This task is no longer available. Refresh the task list.");
            }

            RecordLocalChange(connection, "Update", task, originalTask);
        });
    }

    public async Task DeleteTaskAsync(int userId, long taskId)
    {
        await InitializeAsync();

        await database.RunInTransactionAsync(connection =>
        {
            var task = connection.Query<TaskItem>(
                """
            SELECT * FROM TaskItem
            WHERE UserId = ? AND TaskId = ? AND IsDeleted = 0
            """,
                userId,
                taskId).FirstOrDefault();

            if (task is null)
                return;

            var originalTask = task.Copy();

            task.LastUpdated = new DateTime(
                Math.Max(
                    DateTime.UtcNow.Ticks,
                    originalTask.LastUpdated.Ticks + 1),
                DateTimeKind.Utc);

            task.LastModifiedBy = deviceId;
            task.IsDeleted = true;

            // Preserve a deletion record for conflict handling.
            connection.Execute(
                """
            UPDATE TaskItem
            SET IsDeleted = 1,
                LastUpdated = ?,
                LastModifiedBy = ?
            WHERE UserId = ? AND TaskId = ?
            """,
                task.LastUpdated,
                task.LastModifiedBy,
                userId,
                taskId);

            RecordLocalChange(connection, "Delete", task, originalTask);
        });
    }
}
