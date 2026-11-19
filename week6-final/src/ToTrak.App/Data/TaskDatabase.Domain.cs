using System.Text.Json;
using System.Text.Json.Nodes;
using SQLite;
using ToTrak.App.Models;

namespace ToTrak.App.Data;

public partial class TaskDatabase
{
    private async Task InitializeSchemaAsync()
    {
        // sqlite-net executes one prepared statement per call.
        string[] tables = [
            """
            CREATE TABLE IF NOT EXISTS TaskItem (
                UserId INTEGER NOT NULL, TaskId INTEGER NOT NULL, TaskName TEXT NOT NULL,
                TaskLocation INTEGER NOT NULL, TaskTime INTEGER NOT NULL, TaskType TEXT NOT NULL,
                Completed INTEGER NOT NULL, LastUpdated INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0, LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, TaskId))
            """,
            """
            CREATE TABLE IF NOT EXISTS CourseItem (
                UserId INTEGER NOT NULL, CourseId INTEGER NOT NULL, CourseName TEXT NOT NULL,
                CourseDescription TEXT NULL, LastUpdated INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0, LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, CourseId))
            """,
            """
            CREATE TABLE IF NOT EXISTS AssignmentItem (
                UserId INTEGER NOT NULL, AssignmentId INTEGER NOT NULL, AssignmentName TEXT NOT NULL,
                CourseId INTEGER NOT NULL, AssignmentLocation INTEGER NOT NULL, AssignmentTime INTEGER NOT NULL,
                AssignmentType TEXT NOT NULL, AssignmentDescription TEXT NULL, EstimatedTime INTEGER NULL,
                Completed INTEGER NOT NULL, LastUpdated INTEGER NOT NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0, LastModifiedBy TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (UserId, AssignmentId))
            """,
            """
            CREATE TABLE IF NOT EXISTS LocationItem (
                UserId INTEGER NOT NULL, LocationId INTEGER NOT NULL, LocationName TEXT NOT NULL,
                LastUpdated INTEGER NOT NULL, IsDeleted INTEGER NOT NULL DEFAULT 0,
                LastModifiedBy TEXT NOT NULL DEFAULT '', PRIMARY KEY (UserId, LocationId))
            """,
            """
            CREATE TABLE IF NOT EXISTS PendingAuditChange (
                ClientChangeId TEXT PRIMARY KEY, UserId INTEGER NOT NULL, EntityChanged TEXT NOT NULL,
                ChangeType TEXT NOT NULL, AffectedId INTEGER NOT NULL, TaskId INTEGER NULL,
                AssignmentId INTEGER NULL, CourseId INTEGER NULL, LocationId INTEGER NULL,
                OldValue TEXT NULL, NewValue TEXT NULL, ChangedAtUtc INTEGER NOT NULL DEFAULT 0,
                BaseLastUpdatedUtc INTEGER NULL, DeviceId TEXT NOT NULL DEFAULT '')
            """,
            """
            CREATE TABLE IF NOT EXISTS AuditLog (
                AuditId TEXT PRIMARY KEY, UserId INTEGER NOT NULL, TaskId INTEGER NULL,
                AssignmentId INTEGER NULL, CourseId INTEGER NULL, LocationId INTEGER NULL,
                Action TEXT NOT NULL, OldValue TEXT NULL, NewValue TEXT NULL, OccurredAtUtc INTEGER NOT NULL,
                DeviceId TEXT NOT NULL, Outcome TEXT NOT NULL, Details TEXT NOT NULL, IsSynced INTEGER NOT NULL DEFAULT 0)
            """,
            """
            CREATE TABLE IF NOT EXISTS AuditSyncReceipt (
                AuditId TEXT PRIMARY KEY, UserId INTEGER NOT NULL, Outcome TEXT NOT NULL,
                Details TEXT NOT NULL, SyncedAtUtc INTEGER NOT NULL)
            """
        ];
        foreach (string sql in tables) await database.ExecuteAsync(sql);
        foreach (string table in new[] { "TaskItem", "AssignmentItem", "CourseItem" })
        {
            await EnsureColumnAsync(table, "IsDeleted", "INTEGER NOT NULL DEFAULT 0");
            await EnsureColumnAsync(table, "LastModifiedBy", "TEXT NOT NULL DEFAULT ''");
        }
        await EnsureColumnAsync("CourseItem", "CourseDescription", "TEXT NULL");
        await EnsureColumnAsync("AssignmentItem", "CourseId", "INTEGER NOT NULL DEFAULT 1");
        await EnsureColumnAsync("AssignmentItem", "AssignmentDescription", "TEXT NULL");
        await EnsureColumnAsync("AssignmentItem", "EstimatedTime", "INTEGER NULL");
        foreach (string table in new[] { "PendingAuditChange", "AuditLog" })
            foreach (string column in new[] { "TaskId", "AssignmentId", "CourseId", "LocationId" })
                await EnsureColumnAsync(table, column, "INTEGER NULL");
        await EnsureColumnAsync("PendingAuditChange", "ChangedAtUtc", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync("PendingAuditChange", "BaseLastUpdatedUtc", "INTEGER NULL");
        await EnsureColumnAsync("PendingAuditChange", "DeviceId", "TEXT NOT NULL DEFAULT ''");
        await EnsureColumnAsync("PendingAuditChange", "OriginalAffectedId", "INTEGER NULL");

        await database.RunInTransactionAsync(connection =>
        {
            string schema = connection.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE name = 'AuditLog'");
            if (schema.Contains("CHECK", StringComparison.OrdinalIgnoreCase) ||
                connection.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('AuditLog') WHERE name = 'TaskId' AND [notnull] = 1") > 0)
            {
                connection.Execute("ALTER TABLE AuditLog RENAME TO AuditLog_DomainUpgrade");
                connection.Execute(tables[5]);
                connection.Execute("""
                    INSERT INTO AuditLog (AuditId, UserId, TaskId, AssignmentId, CourseId, LocationId, Action,
                        OldValue, NewValue, OccurredAtUtc, DeviceId, Outcome, Details, IsSynced)
                    SELECT AuditId, UserId, TaskId, AssignmentId, CourseId, LocationId, Action,
                        OldValue, NewValue, OccurredAtUtc, DeviceId, Outcome, Details, IsSynced FROM AuditLog_DomainUpgrade
                    """);
                connection.Execute("DROP TABLE AuditLog_DomainUpgrade");
            }
            foreach (string entity in new[] { "Task", "Assignment", "Course" })
                connection.Execute($"UPDATE PendingAuditChange SET {entity}Id = AffectedId WHERE EntityChanged = ? AND {entity}Id IS NULL", entity);
            connection.Execute("""
                INSERT OR IGNORE INTO CourseItem (UserId, CourseId, CourseName, LastUpdated, IsDeleted, LastModifiedBy)
                SELECT DISTINCT UserId, 1, 'Unassigned', ?, 0, '' FROM AssignmentItem WHERE CourseId = 1
                """, DateTime.UtcNow.Ticks);
        });
        await MigratePendingAssignmentSnapshotsAsync();
    }

    private static long NextId(SQLiteConnection connection, string table, string column, int userId) =>
        checked((table is "TaskItem" or "AssignmentItem"
            ? connection.ExecuteScalar<long>($"SELECT COALESCE(MAX({column}), 0) FROM {table}")
            : connection.ExecuteScalar<long>($"SELECT COALESCE(MAX({column}), 0) FROM {table} WHERE UserId = ?", userId)) + 1);

    public async Task ReassignCreatedItemAsync(PendingAuditChange change, long suggestedId)
    {
        await InitializeAsync();
        if (change.ChangeType != "Create" || suggestedId <= change.AffectedId)
            throw new InvalidOperationException("Invalid ID collision response.");
        (string table, string id) = change.EntityChanged switch
        {
            "Task" => ("TaskItem", "TaskId"), "Assignment" => ("AssignmentItem", "AssignmentId"),
            "Course" => ("CourseItem", "CourseId"), "Location" => ("LocationItem", "LocationId"),
            _ => throw new InvalidOperationException("Unknown item type.")
        };
        await database.RunInTransactionAsync(connection =>
        {
            long replacement = Math.Max(suggestedId, NextId(connection, table, id, change.UserId));
            if (change.EntityChanged == "Location") _ = checked((int)replacement);
            if (connection.Execute($"UPDATE {table} SET {id} = ? WHERE UserId = ? AND {id} = ?",
                replacement, change.UserId, change.AffectedId) != 1)
                throw new InvalidOperationException("The colliding local item is missing.");
            if (change.EntityChanged == "Course")
                connection.Execute("UPDATE AssignmentItem SET CourseId = ? WHERE UserId = ? AND CourseId = ?", replacement, change.UserId, change.AffectedId);
            if (change.EntityChanged == "Location")
                connection.Execute("UPDATE TaskItem SET TaskLocation = ? WHERE UserId = ? AND TaskLocation = ?", replacement, change.UserId, change.AffectedId);
            var pending = connection.Query<PendingAuditChange>("SELECT * FROM PendingAuditChange WHERE UserId = ?", change.UserId);
            foreach (var item in pending)
            {
                bool ownItem = item.EntityChanged == change.EntityChanged && item.AffectedId == change.AffectedId;
                string? field = ownItem ? id : change.EntityChanged == "Course" && item.EntityChanged == "Assignment" ? "CourseId"
                    : change.EntityChanged == "Location" && item.EntityChanged == "Task" ? "TaskLocation" : null;
                if (field is null) continue;
                string? oldValue = RewriteId(item.OldValue, field, change.AffectedId, replacement);
                string? newValue = RewriteId(item.NewValue, field, change.AffectedId, replacement);
                if (ownItem)
                    connection.Execute($"UPDATE PendingAuditChange SET OriginalAffectedId = COALESCE(OriginalAffectedId, AffectedId), AffectedId = ?, {id} = ?, OldValue = ?, NewValue = ? WHERE ClientChangeId = ?",
                        replacement, replacement, oldValue, newValue, item.ClientChangeId);
                else
                    connection.Execute("UPDATE PendingAuditChange SET OldValue = ?, NewValue = ? WHERE ClientChangeId = ?", oldValue, newValue, item.ClientChangeId);
            }
            // The original audit records retain the IDs visible when the edits were made.
            // Acknowledgment receipts explain the final ID used by the server.
        });
    }

    private static string? RewriteId(string? json, string field, long original, long replacement)
    {
        if (json is null) return null;
        var value = JsonNode.Parse(json)!.AsObject();
        string? key = value.Select(x => x.Key).FirstOrDefault(x => string.Equals(x, field, StringComparison.OrdinalIgnoreCase));
        if (key is not null && value[key]?.GetValue<long>() == original) value[key] = replacement;
        return value.ToJsonString();
    }

    private static void ValidateTask(SQLiteConnection connection, TaskItem task)
    {
        if (task.UserId <= 0 || string.IsNullOrWhiteSpace(task.TaskName) || string.IsNullOrWhiteSpace(task.TaskType) ||
            task.TaskTime == default || task.TaskLocation < 0)
            throw new ArgumentException("A name, type, time and valid location are required.");
        if (task.TaskLocation != 0 && connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM LocationItem WHERE UserId = ? AND LocationId = ?", task.UserId, task.TaskLocation) != 1)
            throw new ArgumentException("Select a location belonging to this account.");
    }

    private static void ValidateAssignment(SQLiteConnection connection, AssignmentItem assignment)
    {
        if (assignment.UserId <= 0 || string.IsNullOrWhiteSpace(assignment.AssignmentName) ||
            string.IsNullOrWhiteSpace(assignment.AssignmentType) || assignment.AssignmentTime == default || assignment.EstimatedTime < 0)
            throw new ArgumentException("A title, type and due date are required; estimated minutes cannot be negative.");
        if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM CourseItem WHERE UserId = ? AND CourseId = ? AND IsDeleted = 0",
            assignment.UserId, assignment.CourseId) != 1)
            throw new ArgumentException("Select an available course belonging to this account.");
    }

    public async Task<List<LocationItem>> GetLocationsAsync(int userId)
    {
        await InitializeAsync();
        return await database.QueryAsync<LocationItem>("SELECT * FROM LocationItem WHERE UserId = ? AND IsDeleted = 0 ORDER BY LocationName COLLATE NOCASE", userId);
    }

    public async Task SaveCourseAsync(CourseItem course, CourseItem? original = null)
    {
        await InitializeAsync();
        if (course.UserId <= 0 || string.IsNullOrWhiteSpace(course.CourseName)) throw new ArgumentException("A course name is required.");
        await database.RunInTransactionAsync(connection =>
        {
            if (original is null) course.CourseId = NextId(connection, "CourseItem", "CourseId", course.UserId);
            else if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM CourseItem WHERE UserId = ? AND CourseId = ? AND IsDeleted = 0", course.UserId, course.CourseId) != 1)
                throw new InvalidOperationException("This course is no longer available.");
            if (course.IsDeleted && connection.ExecuteScalar<int>("SELECT COUNT(*) FROM AssignmentItem WHERE UserId = ? AND CourseId = ? AND IsDeleted = 0", course.UserId, course.CourseId) > 0)
                throw new InvalidOperationException("Move or delete the course's assignments first.");
            course.CourseName = course.CourseName.Trim();
            course.LastUpdated = NewTimestamp(original?.LastUpdated);
            course.LastModifiedBy = deviceId;
            UpsertCourse(connection, course);
            RecordLocalChange(connection, original is null ? "Create" : course.IsDeleted ? "Delete" : "Update", course, original);
        });
    }

    private static DateTime NewTimestamp(DateTime? previous) => new(Math.Max(DateTime.UtcNow.Ticks, (previous?.Ticks ?? 0) + 1), DateTimeKind.Utc);

    private static void UpsertCourse(SQLiteConnection connection, CourseItem course) => connection.Execute("""
        INSERT INTO CourseItem (UserId, CourseId, CourseName, CourseDescription, LastUpdated, IsDeleted, LastModifiedBy)
        VALUES (?, ?, ?, ?, ?, ?, ?) ON CONFLICT(UserId, CourseId) DO UPDATE SET
            CourseName = excluded.CourseName, CourseDescription = excluded.CourseDescription,
            LastUpdated = excluded.LastUpdated, IsDeleted = excluded.IsDeleted, LastModifiedBy = excluded.LastModifiedBy
        """, course.UserId, course.CourseId, course.CourseName, course.CourseDescription, course.LastUpdated, course.IsDeleted, course.LastModifiedBy);

    public async Task SaveLocationAsync(LocationItem location, LocationItem? original = null)
    {
        await InitializeAsync();
        if (location.UserId <= 0 || string.IsNullOrWhiteSpace(location.LocationName)) throw new ArgumentException("A location name is required.");
        await database.RunInTransactionAsync(connection =>
        {
            if (original is null) location.LocationId = checked((int)NextId(connection, "LocationItem", "LocationId", location.UserId));
            else if (connection.ExecuteScalar<int>("SELECT COUNT(*) FROM LocationItem WHERE UserId = ? AND LocationId = ? AND IsDeleted = 0", location.UserId, location.LocationId) != 1)
                throw new InvalidOperationException("This location is no longer available.");
            location.LocationName = location.LocationName.Trim();
            location.LastUpdated = NewTimestamp(original?.LastUpdated);
            location.LastModifiedBy = deviceId;
            UpsertLocation(connection, location);
            string action = original is null ? "Create" : location.IsDeleted ? "Delete" : "Update";
            string changeId = Guid.NewGuid().ToString("N");
            string? oldValue = original is null ? null : JsonSerializer.Serialize(original);
            string newValue = JsonSerializer.Serialize(location);
            connection.Execute("""
                INSERT INTO PendingAuditChange (ClientChangeId, UserId, EntityChanged, ChangeType, AffectedId,
                    LocationId, OldValue, NewValue, ChangedAtUtc, BaseLastUpdatedUtc, DeviceId)
                VALUES (?, ?, 'Location', ?, ?, ?, ?, ?, ?, ?, ?)
                """, changeId, location.UserId, action, location.LocationId, location.LocationId, oldValue, newValue,
                location.LastUpdated, original?.LastUpdated, deviceId);
            connection.Execute("""
                INSERT INTO AuditLog (AuditId, UserId, LocationId, Action, OldValue, NewValue,
                    OccurredAtUtc, DeviceId, Outcome, Details, IsSynced)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'Pending', 'Saved locally; waiting for server confirmation.', 0)
                """, changeId, location.UserId, location.LocationId, action, oldValue, newValue, location.LastUpdated, deviceId);
        });
    }

    private static void UpsertLocation(SQLiteConnection connection, LocationItem location) => connection.Execute("""
        INSERT INTO LocationItem (UserId, LocationId, LocationName, LastUpdated, IsDeleted, LastModifiedBy)
        VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(UserId, LocationId) DO UPDATE SET
            LocationName = excluded.LocationName, LastUpdated = excluded.LastUpdated,
            IsDeleted = excluded.IsDeleted, LastModifiedBy = excluded.LastModifiedBy
        """, location.UserId, location.LocationId, location.LocationName, location.LastUpdated, location.IsDeleted, location.LastModifiedBy);

    private static void ApplyLocationSnapshot(SQLiteConnection connection, int userId, long affectedId, string? json, string action)
    {
        var location = JsonSerializer.Deserialize<LocationItem>(json ?? throw new InvalidOperationException("Missing location snapshot."),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (location is null || location.UserId != userId || location.LocationId != affectedId ||
            string.IsNullOrWhiteSpace(location.LocationName) || location.LastUpdated == default || location.IsDeleted != (action == "Delete"))
            throw new InvalidOperationException("Invalid location snapshot.");
        // Keep the tombstone to resolve offline references. The UI maps removed locations to Unset.
        UpsertLocation(connection, location);
    }
}
