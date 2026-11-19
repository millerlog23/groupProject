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
            CREATE TABLE IF NOT EXISTS CourseItem
            (
                UserId INTEGER NOT NULL,
                CourseId INTEGER NOT NULL,
                CourseName TEXT NOT NULL,
                LastUpdated INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, CourseId)
            );

            CREATE TABLE IF NOT EXISTS AssignmentItem
            (
                UserId INTEGER NOT NULL,
                AssignmentId INTEGER NOT NULL,
                AssignmentName TEXT NOT NULL,
                CourseId INTEGER NOT NULL,
                AssignmentLocation INTEGER NOT NULL,
                AssignmentTime INTEGER NOT NULL,
                AssignmentType TEXT NOT NULL,
                Completed INTEGER NOT NULL,
                LastUpdated INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, AssignmentId)
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
            "AssignmentItem", "CourseId", "INTEGER NOT NULL DEFAULT 1");

        await database.ExecuteAsync(
            """
            INSERT OR IGNORE INTO CourseItem
                (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
            SELECT DISTINCT UserId, 1, 'Unassigned', ?, 0, ?
            FROM AssignmentItem WHERE CourseId = 1;
            """,
            DateTime.UtcNow.Ticks,
            deviceId);

        await EnsureColumnAsync(
            "PendingAuditChange", "ChangedAtUtc",
            "INTEGER NOT NULL DEFAULT 0");

        await EnsureColumnAsync(
            "PendingAuditChange", "BaseLastUpdatedUtc",
            "INTEGER NULL");

        await EnsureColumnAsync(
            "PendingAuditChange", "DeviceId",
            "TEXT NOT NULL DEFAULT ''");

        await EnsureColumnAsync(
            "PendingAuditChange", "TaskId", "INTEGER NULL");

        await EnsureColumnAsync(
            "PendingAuditChange", "AssignmentId", "INTEGER NULL");

        await EnsureColumnAsync(
            "PendingAuditChange", "CourseId", "INTEGER NULL");

        await database.ExecuteAsync("""
            UPDATE PendingAuditChange
            SET TaskId = AffectedId
            WHERE EntityChanged = 'Task' AND TaskId IS NULL;
            UPDATE PendingAuditChange
            SET AssignmentId = AffectedId
            WHERE EntityChanged = 'Assignment' AND AssignmentId IS NULL;
            UPDATE PendingAuditChange
            SET CourseId = AffectedId
            WHERE EntityChanged = 'Course' AND CourseId IS NULL;
            """);
        await MigratePendingAssignmentSnapshotsAsync();

        // Permanent history: these records remain after successful sync.
        await database.ExecuteAsync("""
    CREATE TABLE IF NOT EXISTS AuditLog
    (
        AuditId TEXT PRIMARY KEY,
        UserId INTEGER NOT NULL,
        TaskId INTEGER NULL,
        AssignmentId INTEGER NULL,
        CourseId INTEGER NULL,
        Action TEXT NOT NULL,
        OldValue TEXT NULL,
        NewValue TEXT NULL,
        OccurredAtUtc INTEGER NOT NULL,
        DeviceId TEXT NOT NULL,
        Outcome TEXT NOT NULL,
        Details TEXT NOT NULL,
        IsSynced INTEGER NOT NULL DEFAULT 0,
        CHECK (
            (TaskId IS NOT NULL AND AssignmentId IS NULL AND CourseId IS NULL) OR
            (TaskId IS NULL AND AssignmentId IS NOT NULL AND CourseId IS NULL) OR
            (TaskId IS NULL AND AssignmentId IS NULL AND CourseId IS NOT NULL)
        )
    );
    
    """);
        await EnsureColumnAsync("AuditLog", "AssignmentId", "INTEGER NULL");
        await EnsureColumnAsync("AuditLog", "CourseId", "INTEGER NULL");
        await MigrateAuditLogForAssignmentsAsync();
        // Store synchronization results separately.
        // Original audit records must remain unchanged.
        await database.ExecuteAsync("""
    CREATE TABLE IF NOT EXISTS AuditSyncReceipt
    (
        AuditId TEXT PRIMARY KEY,
        UserId INTEGER NOT NULL,
        Outcome TEXT NOT NULL,
        Details TEXT NOT NULL,
        SyncedAtUtc INTEGER NOT NULL
    );
    """);
    }

    private async Task MigratePendingAssignmentSnapshotsAsync()
    {
        var changes = await database.QueryAsync<PendingAuditChange>(
            "SELECT * FROM PendingAuditChange WHERE EntityChanged = 'Assignment'");

        foreach (var change in changes)
        {
            bool updated = false;
            if (!string.IsNullOrWhiteSpace(change.NewValue))
            {
                var assignment = JsonSerializer.Deserialize<AssignmentItem>(change.NewValue);
                if (assignment is not null && assignment.CourseId <= 0)
                {
                    assignment.CourseId = 1;
                    change.NewValue = JsonSerializer.Serialize(assignment);
                    updated = true;
                }
            }

            if (!string.IsNullOrWhiteSpace(change.OldValue))
            {
                var assignment = JsonSerializer.Deserialize<AssignmentItem>(change.OldValue);
                if (assignment is not null && assignment.CourseId <= 0)
                {
                    assignment.CourseId = 1;
                    change.OldValue = JsonSerializer.Serialize(assignment);
                    updated = true;
                }
            }

            if (updated)
            {
                await database.ExecuteAsync(
                    "UPDATE PendingAuditChange SET OldValue = ?, NewValue = ? WHERE ClientChangeId = ?",
                    change.OldValue,
                    change.NewValue,
                    change.ClientChangeId);
            }
        }
    }

    private async Task MigrateAuditLogForAssignmentsAsync()
    {
        int taskIdIsRequired = await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM pragma_table_info('AuditLog') " +
            "WHERE name = 'TaskId' AND notnull = 1");
        string? schema = await database.ExecuteScalarAsync<string?>(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'AuditLog'");
        bool supportsCourseId = schema?.Contains(
            "CourseId IS NOT NULL", StringComparison.OrdinalIgnoreCase) == true;

        if (taskIdIsRequired == 0 && supportsCourseId)
            return;

        await database.ExecuteAsync("""
            DROP TABLE IF EXISTS AuditLog_CourseUpgrade;
            CREATE TABLE AuditLog_CourseUpgrade
            (
                AuditId TEXT PRIMARY KEY,
                UserId INTEGER NOT NULL,
                TaskId INTEGER NULL,
                AssignmentId INTEGER NULL,
                CourseId INTEGER NULL,
                Action TEXT NOT NULL,
                OldValue TEXT NULL,
                NewValue TEXT NULL,
                OccurredAtUtc INTEGER NOT NULL,
                DeviceId TEXT NOT NULL,
                Outcome TEXT NOT NULL,
                Details TEXT NOT NULL,
                IsSynced INTEGER NOT NULL DEFAULT 0,
                CHECK (
                    (TaskId IS NOT NULL AND AssignmentId IS NULL AND CourseId IS NULL) OR
                    (TaskId IS NULL AND AssignmentId IS NOT NULL AND CourseId IS NULL) OR
                    (TaskId IS NULL AND AssignmentId IS NULL AND CourseId IS NOT NULL)
                )
            );

            INSERT INTO AuditLog_CourseUpgrade
            (
                AuditId, UserId, TaskId, AssignmentId, CourseId, Action,
                OldValue, NewValue, OccurredAtUtc, DeviceId,
                Outcome, Details, IsSynced
            )
            SELECT
                AuditId, UserId, TaskId, AssignmentId, CourseId, Action,
                OldValue, NewValue, OccurredAtUtc, DeviceId,
                Outcome, Details, IsSynced
            FROM AuditLog;

            DROP TABLE AuditLog;
            ALTER TABLE AuditLog_CourseUpgrade RENAME TO AuditLog;
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
            ChangeType, AffectedId, TaskId, AssignmentId, CourseId, OldValue, NewValue,
            ChangedAtUtc, BaseLastUpdatedUtc, DeviceId
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
            changeId,
            task.UserId,
            "Task",
            action,
            task.TaskId,
            task.TaskId,
            null,
            null,
            oldValue,
            newValue,
            task.LastUpdated,
            baseLastUpdatedUtc,
            deviceId);

        connection.Execute(
            """
        INSERT INTO AuditLog
        (
            AuditId, UserId, TaskId, AssignmentId, CourseId, Action,
            OldValue, NewValue, OccurredAtUtc,
            DeviceId, Outcome, Details, IsSynced
        )
        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
            changeId,
            task.UserId,
            task.TaskId,
            null,
            null,
            action,
            oldValue,
            newValue,
            task.LastUpdated,
            deviceId,
            "Pending",
            "Saved locally; waiting for server confirmation.",
            false);
    }

    private void RecordLocalChange(
        SQLiteConnection connection,
        string action,
        AssignmentItem assignment,
        AssignmentItem? originalAssignment)
    {
        string changeId = Guid.NewGuid().ToString("N");
        string? oldValue = originalAssignment is null
            ? null
            : JsonSerializer.Serialize(originalAssignment);
        string newValue = JsonSerializer.Serialize(assignment);

        connection.Execute(
            """
            INSERT INTO PendingAuditChange
            (
                ClientChangeId, UserId, EntityChanged, ChangeType,
                AffectedId, TaskId, AssignmentId, CourseId, OldValue, NewValue,
                ChangedAtUtc, BaseLastUpdatedUtc, DeviceId
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            changeId,
            assignment.UserId,
            "Assignment",
            action,
            assignment.AssignmentId,
            null,
            assignment.AssignmentId,
            null,
            oldValue,
            newValue,
            assignment.LastUpdated,
            originalAssignment?.LastUpdated,
            deviceId);

        connection.Execute(
            """
            INSERT INTO AuditLog
            (
                AuditId, UserId, TaskId, AssignmentId, CourseId, Action,
                OldValue, NewValue, OccurredAtUtc, DeviceId,
                Outcome, Details, IsSynced
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            changeId,
            assignment.UserId,
            null,
            assignment.AssignmentId,
            null,
            action,
            oldValue,
            newValue,
            assignment.LastUpdated,
            deviceId,
            "Pending",
            "Saved locally; waiting for server confirmation.",
            false);
    }

    private void RecordLocalChange(
        SQLiteConnection connection,
        string action,
        CourseItem course,
        CourseItem? originalCourse)
    {
        string changeId = Guid.NewGuid().ToString("N");
        string? oldValue = originalCourse is null
            ? null
            : JsonSerializer.Serialize(originalCourse);
        string newValue = JsonSerializer.Serialize(course);

        connection.Execute(
            """
            INSERT INTO PendingAuditChange
            (
                ClientChangeId, UserId, EntityChanged, ChangeType,
                AffectedId, TaskId, AssignmentId, CourseId, OldValue, NewValue,
                ChangedAtUtc, BaseLastUpdatedUtc, DeviceId
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            changeId,
            course.UserId,
            "Course",
            action,
            course.CourseId,
            null,
            null,
            course.CourseId,
            oldValue,
            newValue,
            course.LastUpdated,
            originalCourse?.LastUpdated,
            deviceId);

        connection.Execute(
            """
            INSERT INTO AuditLog
            (
                AuditId, UserId, TaskId, AssignmentId, CourseId, Action,
                OldValue, NewValue, OccurredAtUtc, DeviceId,
                Outcome, Details, IsSynced
            )
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            changeId,
            course.UserId,
            null,
            null,
            course.CourseId,
            action,
            oldValue,
            newValue,
            course.LastUpdated,
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
                    change.EntityChanged is not ("Task" or "Assignment" or "Course") ||
                    change.AffectedId <= 0 ||
                    (change.EntityChanged == "Task" &&
                        (change.TaskId != change.AffectedId || change.AssignmentId is not null || change.CourseId is not null)) ||
                    (change.EntityChanged == "Assignment" &&
                        (change.AssignmentId != change.AffectedId || change.TaskId is not null || change.CourseId is not null)) ||
                    (change.EntityChanged == "Course" &&
                        (change.CourseId != change.AffectedId || change.TaskId is not null || change.AssignmentId is not null)) ||
                    change.ChangeType is not ("Create" or "Update" or "Delete"))
                {
                    throw new InvalidOperationException(
                        "The server returned an invalid item change.");
                }

                int pending = connection.ExecuteScalar<int>(
                    """
                SELECT COUNT(*) FROM PendingAuditChange
                WHERE UserId = ? AND EntityChanged = ? AND AffectedId = ?
                """,
                    userId,
                    change.EntityChanged,
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
                    string tableName = change.EntityChanged == "Task"
                        ? "TaskItem"
                        : "AssignmentItem";
                    string idColumn = change.EntityChanged == "Task"
                        ? "TaskId"
                        : change.EntityChanged == "Assignment"
                            ? "AssignmentId"
                            : "CourseId";
                    connection.Execute(
                        $"UPDATE {tableName} SET IsDeleted = 1 " +
                        $"WHERE UserId = ? AND {idColumn} = ?",
                        userId,
                        change.AffectedId);

                    cursor = change.ServerChangeId;
                    applied++;
                    continue;
                }

                if (change.EntityChanged == "Assignment")
                {
                    var assignment = JsonSerializer.Deserialize<AssignmentItem>(
                        change.NewValue
                            ?? throw new InvalidOperationException(
                                "Missing downloaded assignment data."),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (assignment is not null && assignment.CourseId <= 0)
                        assignment.CourseId = 1;

                    if (assignment is null ||
                        assignment.UserId != userId ||
                        assignment.AssignmentId != change.AffectedId ||
                        string.IsNullOrWhiteSpace(assignment.AssignmentName) ||
                        assignment.LastUpdated == default ||
                        assignment.IsDeleted != (change.ChangeType == "Delete"))
                    {
                        throw new InvalidOperationException(
                            "Invalid downloaded assignment data.");
                    }

                    connection.Execute(
                        """
                        INSERT OR IGNORE INTO CourseItem
                        (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                        VALUES (?, 1, 'Unassigned', ?, 0, '')
                        """,
                        userId,
                        DateTime.UtcNow.Ticks);

                    int courseExists = connection.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM CourseItem WHERE UserId = ? AND CourseId = ? AND IsDeleted = 0",
                        userId,
                        assignment.CourseId);
                    if (courseExists != 1)
                        throw new InvalidOperationException(
                            "The assignment references a course that is not available locally.");

                    connection.Execute(
                        """
                        INSERT INTO AssignmentItem
                        (
                            UserId, AssignmentId, AssignmentName, CourseId,
                            AssignmentLocation, AssignmentTime,
                            AssignmentType, Completed, LastUpdated,
                            IsDeleted, LastModifiedBy
                        )
                        VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                        ON CONFLICT(UserId, AssignmentId) DO UPDATE SET
                            AssignmentName = excluded.AssignmentName,
                            CourseId = excluded.CourseId,
                            AssignmentLocation = excluded.AssignmentLocation,
                            AssignmentTime = excluded.AssignmentTime,
                            AssignmentType = excluded.AssignmentType,
                            Completed = excluded.Completed,
                            LastUpdated = excluded.LastUpdated,
                            IsDeleted = excluded.IsDeleted,
                            LastModifiedBy = excluded.LastModifiedBy;
                        """,
                        assignment.UserId,
                        assignment.AssignmentId,
                        assignment.AssignmentName,
                        assignment.CourseId,
                        assignment.AssignmentLocation,
                        assignment.AssignmentTime,
                        assignment.AssignmentType,
                        assignment.Completed,
                        assignment.LastUpdated,
                        assignment.IsDeleted,
                        assignment.LastModifiedBy);

                    cursor = change.ServerChangeId;
                    applied++;
                    continue;
                }

                if (change.EntityChanged == "Course")
                {
                    var course = JsonSerializer.Deserialize<CourseItem>(
                        change.NewValue
                            ?? throw new InvalidOperationException(
                                "Missing downloaded course data."),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                    if (course is null || course.UserId != userId ||
                        course.CourseId != change.AffectedId ||
                        string.IsNullOrWhiteSpace(course.CourseName) ||
                        course.LastUpdated == default || course.IsDeleted)
                        throw new InvalidOperationException("Invalid downloaded course data.");

                    connection.Execute(
                        """
                        INSERT INTO CourseItem
                        (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                        VALUES (?, ?, ?, ?, ?, ?)
                        ON CONFLICT(UserId, CourseId) DO UPDATE SET
                            CourseName = excluded.CourseName,
                            LastUpdated = excluded.LastUpdated,
                            IsDeleted = excluded.IsDeleted,
                            LastModifiedBy = excluded.LastModifiedBy;
                        """,
                        course.UserId,
                        course.CourseId,
                        course.CourseName,
                        course.LastUpdated,
                        course.IsDeleted,
                        course.LastModifiedBy);

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
                    change.TaskId != task.TaskId ||
                    change.AssignmentId is not null || change.CourseId is not null ||
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
    public async Task AddAssignmentAsync(
        AssignmentItem assignment,
        string courseName,
        long? selectedCourseId = null)
    {
        await InitializeAsync();

        await database.RunInTransactionAsync(connection =>
        {
            long courseId;
            if (selectedCourseId.HasValue)
            {
                int exists = connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM CourseItem WHERE UserId = ? AND CourseId = ? AND IsDeleted = 0",
                    assignment.UserId,
                    selectedCourseId.Value);
                if (exists != 1)
                    throw new InvalidOperationException("The selected course is no longer available.");
                courseId = selectedCourseId.Value;
            }
            else
            {
                courseName = courseName.Trim();
                if (string.IsNullOrWhiteSpace(courseName))
                    throw new ArgumentException("Select a course or enter a new course name.");

                var existingCourse = connection.Query<CourseItem>(
                    "SELECT * FROM CourseItem WHERE UserId = ? AND CourseName = ? COLLATE NOCASE AND IsDeleted = 0",
                    assignment.UserId,
                    courseName).FirstOrDefault();

                if (existingCourse is not null)
                {
                    courseId = existingCourse.CourseId;
                }
                else
                {
                    var course = new CourseItem
                    {
                        UserId = assignment.UserId,
                        CourseName = courseName,
                        LastUpdated = DateTime.UtcNow,
                        LastModifiedBy = deviceId
                    };

                    do
                    {
                        byte[] bytes =
                            System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
                        course.CourseId = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
                    }
                    while (course.CourseId == 0 || connection.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM CourseItem WHERE UserId = ? AND CourseId = ?",
                        course.UserId,
                        course.CourseId) > 0);

                    connection.Execute(
                        """
                        INSERT INTO CourseItem
                        (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                        VALUES (?, ?, ?, ?, ?, ?)
                        """,
                        course.UserId,
                        course.CourseId,
                        course.CourseName,
                        course.LastUpdated,
                        course.IsDeleted,
                        course.LastModifiedBy);
                    RecordLocalChange(connection, "Create", course, null);
                    courseId = course.CourseId;
                }
            }

            assignment.CourseId = courseId;
            do
            {
                byte[] bytes =
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
                assignment.AssignmentId = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            }
            while (
                assignment.AssignmentId == 0 ||
                connection.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM AssignmentItem WHERE UserId = ? AND AssignmentId = ?",
                    assignment.UserId,
                    assignment.AssignmentId) > 0);

            assignment.LastUpdated = DateTime.UtcNow;
            assignment.LastModifiedBy = deviceId;
            assignment.IsDeleted = false;

            connection.Execute(
                """
                INSERT INTO AssignmentItem
                (
                    UserId, AssignmentId, AssignmentName, CourseId,
                    AssignmentLocation, AssignmentTime, AssignmentType,
                    Completed, LastUpdated, IsDeleted, LastModifiedBy
                )
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                assignment.UserId,
                assignment.AssignmentId,
                assignment.AssignmentName,
                assignment.CourseId,
                assignment.AssignmentLocation,
                assignment.AssignmentTime,
                assignment.AssignmentType,
                assignment.Completed,
                assignment.LastUpdated,
                assignment.IsDeleted,
                assignment.LastModifiedBy);

            RecordLocalChange(connection, "Create", assignment, null);
        });
    }

    public async Task<List<CourseItem>> GetCoursesAsync(int userId)
    {
        await InitializeAsync();
        return await database.QueryAsync<CourseItem>(
            "SELECT * FROM CourseItem WHERE UserId = ? AND IsDeleted = 0 ORDER BY CourseName COLLATE NOCASE",
            userId);
    }

    public async Task<List<AuditEntry>> GetAuditEntriesAsync(int userId)
{
    await InitializeAsync();

    return await database.QueryAsync<AuditEntry>(
        """
        SELECT
            a.AuditId,
            a.UserId,
            a.TaskId,
            a.AssignmentId,
            a.CourseId,
            a.Action,
            a.OldValue,
            a.NewValue,
            a.OccurredAtUtc,
            a.DeviceId,

            COALESCE(r.Outcome, a.Outcome) AS Outcome,

            COALESCE(r.Details, a.Details) AS Details,

            CASE
                WHEN r.AuditId IS NOT NULL THEN 1
                ELSE a.IsSynced
            END AS IsSynced

        FROM AuditLog a

        LEFT JOIN AuditSyncReceipt r
            ON r.AuditId = a.AuditId
            AND r.UserId = a.UserId

        WHERE a.UserId = ?

        ORDER BY a.OccurredAtUtc DESC, a.rowid DESC

        LIMIT 50
        """,
        userId);
}

    public async Task<List<AssignmentItem>> GetAssignmentsAsync(int userId)
    {
        await InitializeAsync();

        return await database.QueryAsync<AssignmentItem>(
            """
            SELECT * FROM AssignmentItem
            WHERE UserId = ? AND IsDeleted = 0
            ORDER BY AssignmentTime
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
        var assignmentWinner = result.ServerAssignment;
        var courseWinner = result.ServerCourse;

        bool validWinner = change.EntityChanged switch
        {
            "Assignment" => assignmentWinner is not null &&
              assignmentWinner.UserId == change.UserId &&
              assignmentWinner.AssignmentId == change.AffectedId &&
              assignmentWinner.CourseId > 0 &&
              assignmentWinner.LastUpdated != default &&
              !string.IsNullOrWhiteSpace(assignmentWinner.AssignmentName),
            "Course" => courseWinner is not null &&
              courseWinner.UserId == change.UserId &&
              courseWinner.CourseId == change.AffectedId &&
              courseWinner.LastUpdated != default &&
              !string.IsNullOrWhiteSpace(courseWinner.CourseName),
            _ => winner is not null &&
              winner.UserId == change.UserId &&
              winner.TaskId == change.AffectedId &&
              winner.LastUpdated != default &&
              !string.IsNullOrWhiteSpace(winner.TaskName)
        };

        if (!validWinner)
        {
            throw new InvalidOperationException(
                "The server did not return a valid winning item.");
        }

        await database.RunInTransactionAsync(connection =>
        {
            // Verify that the original audit event exists.
            int exists = connection.ExecuteScalar<int>(
                """
    SELECT COUNT(*)
    FROM AuditLog
    WHERE AuditId = ? AND UserId = ?
    """,
                change.ClientChangeId,
                change.UserId);

            if (exists != 1)
            {
                throw new InvalidOperationException(
                    "The local audit entry is missing.");
            }

            // Store synchronization confirmation separately.
            // Never modify the original AuditLog record.
            connection.Execute(
                """
    INSERT INTO AuditSyncReceipt
    (
        AuditId,
        UserId,
        Outcome,
        Details,
        SyncedAtUtc
    )
    VALUES (?, ?, ?, ?, ?)
    ON CONFLICT(AuditId) DO UPDATE SET
        Outcome = excluded.Outcome,
        Details = excluded.Details,
        SyncedAtUtc = excluded.SyncedAtUtc
    """,
                change.ClientChangeId,
                change.UserId,
                result.Outcome,
                (result.WasConflict ? "Conflict resolved. " : "") +
                    result.Details,
                DateTime.UtcNow.Ticks);

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
            WHERE UserId = ? AND EntityChanged = ? AND AffectedId = ?
            """,
                change.UserId,
                change.EntityChanged,
                change.AffectedId);

            // Preserve local edits that are still waiting to upload.
            if (remaining > 0)
                return;

            if (change.EntityChanged == "Course")
            {
                connection.Execute(
                    """
                    INSERT INTO CourseItem
                    (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                    VALUES (?, ?, ?, ?, ?, ?)
                    ON CONFLICT(UserId, CourseId) DO UPDATE SET
                        CourseName = excluded.CourseName,
                        LastUpdated = excluded.LastUpdated,
                        IsDeleted = excluded.IsDeleted,
                        LastModifiedBy = excluded.LastModifiedBy
                    WHERE excluded.LastUpdated >= CourseItem.LastUpdated;
                    """,
                    courseWinner!.UserId,
                    courseWinner.CourseId,
                    courseWinner.CourseName,
                    courseWinner.LastUpdated,
                    courseWinner.IsDeleted,
                    courseWinner.LastModifiedBy);
                return;
            }

            if (change.EntityChanged == "Assignment")
            {
                connection.Execute(
                    """
                    INSERT INTO AssignmentItem
                    (
                        UserId, AssignmentId, AssignmentName,
                        AssignmentLocation, AssignmentTime,
                        AssignmentType, Completed, LastUpdated,
                        IsDeleted, LastModifiedBy
                    )
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                    ON CONFLICT(UserId, AssignmentId) DO UPDATE SET
                        AssignmentName = excluded.AssignmentName,
                        AssignmentLocation = excluded.AssignmentLocation,
                        AssignmentTime = excluded.AssignmentTime,
                        AssignmentType = excluded.AssignmentType,
                        Completed = excluded.Completed,
                        LastUpdated = excluded.LastUpdated,
                        IsDeleted = excluded.IsDeleted,
                        LastModifiedBy = excluded.LastModifiedBy
                    WHERE excluded.LastUpdated >= AssignmentItem.LastUpdated;
                    """,
                    assignmentWinner!.UserId,
                    assignmentWinner.AssignmentId,
                    assignmentWinner.AssignmentName,
                    assignmentWinner.AssignmentLocation,
                    assignmentWinner.AssignmentTime,
                    assignmentWinner.AssignmentType,
                    assignmentWinner.Completed,
                    assignmentWinner.LastUpdated,
                    assignmentWinner.IsDeleted,
                    assignmentWinner.LastModifiedBy);
                return;
            }

            var taskWinner = winner!;
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
                taskWinner.UserId,
                taskWinner.TaskId,
                taskWinner.TaskName,
                taskWinner.TaskLocation,
                taskWinner.TaskTime,
                taskWinner.TaskType,
                taskWinner.Completed,
                taskWinner.LastUpdated,
                taskWinner.IsDeleted,
                taskWinner.LastModifiedBy);
        });
    }

    public async Task UpdateAssignmentAsync(
        AssignmentItem assignment,
        AssignmentItem originalAssignment)
    {
        await InitializeAsync();
        assignment.LastUpdated = new DateTime(
            Math.Max(DateTime.UtcNow.Ticks, originalAssignment.LastUpdated.Ticks + 1),
            DateTimeKind.Utc);
        assignment.LastModifiedBy = deviceId;
        assignment.IsDeleted = false;

        await database.RunInTransactionAsync(connection =>
        {
            int updated = connection.Execute(
                """
                UPDATE AssignmentItem
                SET AssignmentName = ?, AssignmentLocation = ?,
                    AssignmentTime = ?, AssignmentType = ?, Completed = ?,
                    LastUpdated = ?, IsDeleted = ?, LastModifiedBy = ?
                WHERE UserId = ? AND AssignmentId = ? AND IsDeleted = 0
                """,
                assignment.AssignmentName,
                assignment.AssignmentLocation,
                assignment.AssignmentTime,
                assignment.AssignmentType,
                assignment.Completed,
                assignment.LastUpdated,
                assignment.IsDeleted,
                assignment.LastModifiedBy,
                assignment.UserId,
                assignment.AssignmentId);

            if (updated != 1)
                throw new InvalidOperationException(
                    "This assignment is no longer available. Refresh the assignment list.");

            RecordLocalChange(connection, "Update", assignment, originalAssignment);
        });
    }

    public async Task DeleteAssignmentAsync(int userId, long assignmentId)
    {
        await InitializeAsync();

        await database.RunInTransactionAsync(connection =>
        {
            var assignment = connection.Query<AssignmentItem>(
                """
                SELECT * FROM AssignmentItem
                WHERE UserId = ? AND AssignmentId = ? AND IsDeleted = 0
                """,
                userId,
                assignmentId).FirstOrDefault();
            if (assignment is null)
                return;

            var original = assignment.Copy();
            assignment.IsDeleted = true;
            assignment.LastUpdated = new DateTime(
                Math.Max(DateTime.UtcNow.Ticks, original.LastUpdated.Ticks + 1),
                DateTimeKind.Utc);
            assignment.LastModifiedBy = deviceId;

            connection.Execute(
                """
                UPDATE AssignmentItem
                SET IsDeleted = 1, LastUpdated = ?, LastModifiedBy = ?
                WHERE UserId = ? AND AssignmentId = ? AND IsDeleted = 0
                """,
                assignment.LastUpdated,
                assignment.LastModifiedBy,
                userId,
                assignmentId);
            RecordLocalChange(connection, "Delete", assignment, original);
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
