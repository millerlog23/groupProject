using ToTrak.App.Data;
using ToTrak.App.Models;

namespace ToTrak.App;

public partial class MainPage : ContentPage
{
    private readonly TaskDatabase database = new();
    private const int CurrentUserId = 1;

    public MainPage()
    {
        InitializeComponent();
        TaskDatePicker.Date = DateTime.Today;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await LoadTasksAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Database error", ex.Message, "OK");
        }
    }

    private async Task LoadTasksAsync()
    {
        var tasks = await database.GetTasksAsync(CurrentUserId);

        TasksCollection.ItemsSource = tasks;
        StatusLabel.Text = $"{tasks.Count} task(s)";
    }

    private async void OnAddTaskClicked(object? sender, EventArgs e)
    {
        string taskName = TaskNameEntry.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(taskName))
        {
            await DisplayAlertAsync(
                "Missing name",
                "Please enter a task name.",
                "OK");

            return;
        }

        AddTaskButton.IsEnabled = false;

        try
        {
            var task = new TaskItem
            {
                UserId = CurrentUserId,
                TaskName = taskName,
                TaskLocation = 0,
                TaskTime = TaskDatePicker.Date ?? DateTime.Today,
                TaskType = "General",
                Completed = false
            };

            await database.AddTaskAsync(task);

            TaskNameEntry.Text = "";
            await LoadTasksAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Save error", ex.Message, "OK");
        }
        finally
        {
            AddTaskButton.IsEnabled = true;
        }
    }
	private async void OnEditTaskClicked(object? sender, EventArgs e)
{
    if (sender is not Button button ||
        button.CommandParameter is not TaskItem task)
        return;

    try
    {
        string? newName = await DisplayPromptAsync(
            "Edit task",
            "Enter the task name:",
            initialValue: task.TaskName);

        if (newName is null)
            return;

        if (string.IsNullOrWhiteSpace(newName))
        {
            await DisplayAlertAsync(
                "Missing name",
                "The task name cannot be empty.",
                "OK");

            return;
        }

        task.TaskName = newName.Trim();

        await database.UpdateTaskAsync(task);
        await LoadTasksAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("Update error", ex.Message, "OK");
    }
}

private async void OnToggleTaskClicked(object? sender, EventArgs e)
{
    if (sender is not Button button ||
        button.CommandParameter is not TaskItem task)
        return;

    button.IsEnabled = false;

    try
    {
        task.Completed = !task.Completed;

        await database.UpdateTaskAsync(task);
        await LoadTasksAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("Update error", ex.Message, "OK");
    }
    finally
    {
        button.IsEnabled = true;
    }
}

private async void OnDeleteTaskClicked(object? sender, EventArgs e)
{
    if (sender is not Button button ||
        button.CommandParameter is not TaskItem task)
        return;

    try
    {
        bool confirmed = await DisplayAlertAsync(
            "Delete task",
            $"Delete \"{task.TaskName}\"?",
            "Delete",
            "Cancel");

        if (!confirmed)
            return;

        await database.DeleteTaskAsync(task.UserId, task.TaskId);
        await LoadTasksAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("Delete error", ex.Message, "OK");
    }
}
}