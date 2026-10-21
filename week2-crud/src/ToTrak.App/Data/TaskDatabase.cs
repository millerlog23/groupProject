using ToTrak.App.Models;
using Microsoft.Maui.Storage;
using SQLite;

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
public async Task UpdateTaskAsync(TaskItem task)
{
    await InitializeAsync();

    task.LastUpdated = DateTime.UtcNow;

    await database.ExecuteAsync(
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
}

public async Task DeleteTaskAsync(int userId, int taskId)
{
    await InitializeAsync();

    await database.ExecuteAsync(
        """
        DELETE FROM TaskItem
        WHERE UserId = ? AND TaskId = ?
        """,
        userId,
        taskId);
}
}