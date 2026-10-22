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

    public TaskDatabase()
    {
        string databasePath = Path.Combine(
            FileSystem.AppDataDirectory,
            "totrak.db3");

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
    }
    public async Task AddTaskAsync(TaskItem task)
{
    await InitializeAsync();

    await database.RunInTransactionAsync(connection =>
    {
        // Generate the next task ID for this user.
        task.TaskId = connection.ExecuteScalar<int>(
            """
            SELECT COALESCE(MAX(TaskId), 0) + 1
            FROM TaskItem
            WHERE UserId = ?
            """,
            task.UserId);

        task.LastUpdated = DateTime.UtcNow;

        connection.Execute(
            """
            INSERT INTO TaskItem
            (
                UserId, TaskId, TaskName, TaskLocation,
                TaskTime, TaskType, Completed, LastUpdated
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?)
            """,
            task.UserId,
            task.TaskId,
            task.TaskName,
            task.TaskLocation,
            task.TaskTime,
            task.TaskType,
            task.Completed,
            task.LastUpdated);

        connection.Execute(
            """
            INSERT INTO PendingAuditChange
                (ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId, OldValue, NewValue)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            Guid.NewGuid().ToString("N"),
            task.UserId,
            "Task",
            "Create",
            task.TaskId,
            null,
            JsonSerializer.Serialize(task));
    });
}

public async Task<List<TaskItem>> GetTasksAsync(int userId)
{
    await InitializeAsync();

    return await database.QueryAsync<TaskItem>(
        """
        SELECT * FROM TaskItem
        WHERE UserId = ?
        ORDER BY TaskTime
        """,
        userId);
}

public async Task<List<PendingAuditChange>> GetPendingChangesAsync(int userId)
{
    await InitializeAsync();

    return await database.QueryAsync<PendingAuditChange>(
        "SELECT * FROM PendingAuditChange WHERE UserId = ? ORDER BY rowid",
        userId);
}

public async Task RemovePendingChangeAsync(string clientChangeId)
{
    await InitializeAsync();

    await database.ExecuteAsync(
        "DELETE FROM PendingAuditChange WHERE ClientChangeId = ?",
        clientChangeId);
}

public async Task UpdateTaskAsync(TaskItem task, TaskItem originalTask)
{
    await InitializeAsync();

    task.LastUpdated = DateTime.UtcNow;

    await database.RunInTransactionAsync(connection =>
    {
        connection.Execute(
            """
            UPDATE TaskItem
            SET TaskName = ?, TaskLocation = ?, TaskTime = ?,
                TaskType = ?, Completed = ?, LastUpdated = ?
            WHERE UserId = ? AND TaskId = ?
            """,
            task.TaskName,
            task.TaskLocation,
            task.TaskTime,
            task.TaskType,
            task.Completed,
            task.LastUpdated,
            task.UserId,
            task.TaskId);

        connection.Execute(
            """
            INSERT INTO PendingAuditChange
                (ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId, OldValue, NewValue)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            Guid.NewGuid().ToString("N"),
            task.UserId,
            "Task",
            "Update",
            task.TaskId,
            JsonSerializer.Serialize(originalTask),
            JsonSerializer.Serialize(task));
    });
}

public async Task DeleteTaskAsync(int userId, int taskId)
{
    await InitializeAsync();

    var task = (await database.QueryAsync<TaskItem>(
        "SELECT * FROM TaskItem WHERE UserId = ? AND TaskId = ?",
        userId,
        taskId)).FirstOrDefault();
    if (task is null)
        return;

    await database.RunInTransactionAsync(connection =>
    {
        connection.Execute(
            """
            INSERT INTO PendingAuditChange
                (ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId, OldValue, NewValue)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            Guid.NewGuid().ToString("N"),
            task.UserId,
            "Task",
            "Delete",
            task.TaskId,
            JsonSerializer.Serialize(task),
            null);

        connection.Execute(
            "DELETE FROM TaskItem WHERE UserId = ? AND TaskId = ?",
            userId,
            taskId);
    });
}
}