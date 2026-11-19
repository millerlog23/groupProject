using Microsoft.Maui.Controls;
using ToTrak.App.Data;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ToTrak.App.Models;

namespace ToTrak.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public async Task EditTaskAsync(TaskItem task, string newName)
{
    if (string.IsNullOrWhiteSpace(newName))
        throw new ArgumentException("The task name cannot be empty.");

    var originalTask = task.Copy();

    task.TaskName = newName.Trim();

    await database.UpdateTaskAsync(task, originalTask);
}

public async Task EditAssignmentAsync(AssignmentItem assignment, string newName)
{
    if (string.IsNullOrWhiteSpace(newName))
        throw new ArgumentException("The assignment name cannot be empty.");
    var original = assignment.Copy();
    assignment.AssignmentName = newName.Trim();
    await database.UpdateAssignmentAsync(assignment, original);
}

public async Task DeleteAssignmentAsync(AssignmentItem assignment) =>
    await database.DeleteAssignmentAsync(assignment.UserId, assignment.AssignmentId);

public async Task ToggleAssignmentAsync(AssignmentItem assignment)
{
    var original = assignment.Copy();
    assignment.Completed = !assignment.Completed;
    await database.UpdateAssignmentAsync(assignment, original);
}

public async Task DeleteTaskAsync(TaskItem task)
{
    await database.DeleteTaskAsync(task.UserId, task.TaskId);
}
    public async Task ToggleTaskAsync(TaskItem task)
{
    var originalTask = task.Copy();

    task.Completed = !task.Completed;

    await database.UpdateTaskAsync(task, originalTask);
}
   private readonly TaskDatabase database;
private readonly int userId;

// MVVM command for adding tasks.
public Command AddTaskCommand { get; }
public Command AddAssignmentCommand { get; }

public Command<TaskItem> ToggleTaskCommand { get; }
public Command<TaskItem> EditTaskCommand { get; }
public Command<TaskItem> DeleteTaskCommand { get; }
public Command<AssignmentItem> ToggleAssignmentCommand { get; }
public Command<AssignmentItem> EditAssignmentCommand { get; }
public Command<AssignmentItem> DeleteAssignmentCommand { get; }
public Command SyncNowCommand { get; }

public Func<TaskItem, Task<bool>>? RequestDeleteConfirmationAsync { get; set; }
public Func<TaskItem, Task<string?>>? RequestEditNameAsync { get; set; }
public Func<AssignmentItem, Task<bool>>? RequestAssignmentDeleteConfirmationAsync { get; set; }
public Func<AssignmentItem, Task<string?>>? RequestAssignmentEditNameAsync { get; set; }

// Allows MainPage to request synchronization after a task changes.
public Func<Task>? RefreshRequestedAsync { get; set; }

public MainViewModel(TaskDatabase database, int userId)
{
    this.database = database;
    this.userId = userId;

    AddTaskCommand = new Command(
        async () =>
        {
            if (IsBusy)
                return;

            IsBusy = true;

            try
            {
                await AddTaskAsync(this.userId);

                if (RefreshRequestedAsync is not null)
                {
                    await RefreshRequestedAsync();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Save error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        },
        () => !IsBusy
    );
    AddAssignmentCommand = new Command(async () =>
    {
        if (IsBusy)
            return;
        IsBusy = true;
        try
        {
            await AddAssignmentAsync(this.userId);
            if (RefreshRequestedAsync is not null)
                await RefreshRequestedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Save error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    });
    ToggleTaskCommand = new Command<TaskItem>(
    async task =>
    {
        if (task is null || IsBusy)
            return;

        IsBusy = true;

        try
        {
            await ToggleTaskAsync(task);

            if (RefreshRequestedAsync is not null)
            {
                await RefreshRequestedAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Update error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
    
);
    ToggleAssignmentCommand = new Command<AssignmentItem>(async assignment =>
    {
        if (assignment is null || IsBusy)
            return;
        IsBusy = true;
        try
        {
            await ToggleAssignmentAsync(assignment);
            if (RefreshRequestedAsync is not null)
                await RefreshRequestedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Update error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    });
    EditAssignmentCommand = new Command<AssignmentItem>(async assignment =>
    {
        if (assignment is null || IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (RequestAssignmentEditNameAsync is null)
                throw new InvalidOperationException("Edit dialog is not available.");
            string? name = await RequestAssignmentEditNameAsync(assignment);
            if (name is null)
                return;
            await EditAssignmentAsync(assignment, name);
            if (RefreshRequestedAsync is not null)
                await RefreshRequestedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Edit error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    });
    DeleteAssignmentCommand = new Command<AssignmentItem>(async assignment =>
    {
        if (assignment is null || IsBusy)
            return;
        IsBusy = true;
        try
        {
            if (RequestAssignmentDeleteConfirmationAsync is null)
                throw new InvalidOperationException("Delete confirmation is not available.");
            if (!await RequestAssignmentDeleteConfirmationAsync(assignment))
                return;
            await DeleteAssignmentAsync(assignment);
            if (RefreshRequestedAsync is not null)
                await RefreshRequestedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Delete error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    });
EditTaskCommand = new Command<TaskItem>(
    async task =>
    {
        if (task is null || IsBusy)
            return;

        IsBusy = true;

        try
        {
            if (RequestEditNameAsync is null)
                throw new InvalidOperationException(
                    "Edit dialog is not available.");

            string? newName = await RequestEditNameAsync(task);

            if (newName is null)
                return;

            await EditTaskAsync(task, newName);

            if (RefreshRequestedAsync is not null)
            {
                await RefreshRequestedAsync();
            }

        }
        catch (Exception ex)
        {
            StatusText = $"Edit error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
);
DeleteTaskCommand = new Command<TaskItem>(
    async task =>
    {
        if (task is null || IsBusy)
            return;

        IsBusy = true;

        try
        {
            if (RequestDeleteConfirmationAsync is null)
                throw new InvalidOperationException(
                    "Delete confirmation is not available.");

            bool confirmed =
                await RequestDeleteConfirmationAsync(task);

            if (!confirmed)
                return;

            await DeleteTaskAsync(task);

            if (RefreshRequestedAsync is not null)
            {
                await RefreshRequestedAsync();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Delete error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
);
SyncNowCommand = new Command(
    async () =>
    {
        if (IsBusy)
            return;

        IsBusy = true;

        try
        {
            StatusText = "Synchronizing...";

            if (RefreshRequestedAsync is null)
                throw new InvalidOperationException(
                    "Synchronization is not configured.");

            await RefreshRequestedAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Sync error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    },
    () => !IsBusy
);
}
public async Task AddTaskAsync(int userId)
{
    string name = TaskName.Trim();

    if (string.IsNullOrWhiteSpace(name))
        throw new ArgumentException("Please enter a task name.");

    var task = new TaskItem
    {
        UserId = userId,
        TaskName = name,
        TaskLocation = 0,
        TaskTime = TaskDate,
        TaskType = "General",
        Completed = false
    };

    await database.AddTaskAsync(task);

    TaskName = string.Empty;
}

public async Task AddAssignmentAsync(int userId)
{
    string name = AssignmentName.Trim();
    if (string.IsNullOrWhiteSpace(name))
        throw new ArgumentException("Please enter an assignment name.");

    await database.AddAssignmentAsync(new AssignmentItem
    {
        UserId = userId,
        AssignmentName = name,
        AssignmentLocation = 0,
        AssignmentTime = AssignmentDate,
        AssignmentType = "General",
        Completed = false
    }, CourseName);
    AssignmentName = string.Empty;
    CourseName = string.Empty;
    SelectedCourse = null;
}
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TaskItem> Tasks { get; } = new();
    public ObservableCollection<AssignmentDisplayItem> Assignments { get; } = new();
    public ObservableCollection<CourseItem> Courses { get; } = new();

    private string taskName = string.Empty;

    public string TaskName
    {
        get => taskName;
        set => SetProperty(ref taskName, value);
    }

    private string courseName = string.Empty;
    public string CourseName
    {
        get => courseName;
        set => SetProperty(ref courseName, value);
    }

    private CourseItem? selectedCourse;
    public CourseItem? SelectedCourse
    {
        get => selectedCourse;
        set
        {
            if (SetProperty(ref selectedCourse, value) && value is not null)
                CourseName = value.CourseName;
        }
    }

    private string assignmentName = string.Empty;
    public string AssignmentName
    {
        get => assignmentName;
        set => SetProperty(ref assignmentName, value);
    }

    private DateTime assignmentDate = DateTime.Today;
    public DateTime AssignmentDate
    {
        get => assignmentDate;
        set => SetProperty(ref assignmentDate, value);
    }

    private DateTime taskDate = DateTime.Today;

    public DateTime TaskDate
    {
        get => taskDate;
        set => SetProperty(ref taskDate, value);
    }

    private string statusText = "Loading tasks...";

    public string StatusText
    {
        get => statusText;
        set => SetProperty(ref statusText, value);
    }

    private bool isBusy;

   public bool IsBusy
{
    get => isBusy;

    set
    {
        if (SetProperty(ref isBusy, value))
        {
            AddTaskCommand?.ChangeCanExecute();
            SyncNowCommand?.ChangeCanExecute();
        }
    }
}

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;

        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));

        return true;
    }
}