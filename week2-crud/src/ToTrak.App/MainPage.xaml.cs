using ToTrak.App.Data;
using ToTrak.App.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using System.Net.Http.Json;

namespace ToTrak.App;

public partial class MainPage : ContentPage
{
    private readonly TaskDatabase database = new();
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim syncLock = new(1, 1);
    private const int CurrentUserId = 1;

    public MainPage()
    {
        InitializeComponent();
        var defaultApiBaseUrl = DeviceInfo.Platform == DevicePlatform.Android
            ? "http://10.0.2.2:5125/"
            : "http://localhost:5125/";
        var apiBaseUrl = Preferences.Get("ApiBaseUrl", defaultApiBaseUrl);
        httpClient = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };
        TaskDatePicker.Date = DateTime.Today;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;

        try
        {
            await RefreshTasksAndSyncAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Database error", ex.Message, "OK");
        }
    }

    protected override void OnDisappearing()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        base.OnDisappearing();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await RefreshTasksAndSyncAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Sync paused: {ex.Message}";
            }
        });
    }

    private async Task RefreshTasksAndSyncAsync()
    {
        var pendingChanges = await database.GetPendingChangesAsync(CurrentUserId);
        bool online = Connectivity.Current.NetworkAccess != NetworkAccess.None;
        bool syncFailed = false;

        if (pendingChanges.Count > 0 && online)
        {
            StatusLabel.Text = $"Sending {pendingChanges.Count} audit change(s)...";
            var syncResult = await SendPendingChangesAsync(CurrentUserId);
            syncFailed = syncResult.Failed;
        }

        int taskCount = await LoadTasksAsync();
        int pendingCount = (await database.GetPendingChangesAsync(CurrentUserId)).Count;
        online = Connectivity.Current.NetworkAccess != NetworkAccess.None;

        StatusLabel.Text = pendingCount == 0
            ? $"{taskCount} task(s) • Audit changes sent"
            : syncFailed
                ? $"{pendingCount} audit change(s) saved locally; sending will retry"
                : online
                    ? $"{taskCount} task(s) • {pendingCount} audit change(s) waiting to send"
                    : $"{taskCount} task(s) • {pendingCount} audit change(s) saved offline";
    }

    private async Task<(int SentCount, bool Failed)> SendPendingChangesAsync(int userId)
    {
        if (Connectivity.Current.NetworkAccess == NetworkAccess.None)
            return (0, false);

        await syncLock.WaitAsync();
        try
        {
            var pendingChanges = await database.GetPendingChangesAsync(userId);
            int sentCount = 0;

            foreach (var change in pendingChanges)
            {
                try
                {
                    using var response = await httpClient.PostAsJsonAsync("api/audit", change);
                    response.EnsureSuccessStatusCode();
                    await database.RemovePendingChangeAsync(change.ClientChangeId);
                    sentCount++;
                }
                catch (Exception exception) when (
                    exception is HttpRequestException or TaskCanceledException)
                {
                    return (sentCount, true);
                }
            }

            return (sentCount, false);
        }
        finally
        {
            syncLock.Release();
        }
    }

    private async Task<int> LoadTasksAsync()
    {
        var tasks = await database.GetTasksAsync(CurrentUserId);

        TasksCollection.ItemsSource = tasks;
        return tasks.Count;
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
            await RefreshTasksAndSyncAsync();
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

        var originalTask = task.Copy();
        task.TaskName = newName.Trim();

        await database.UpdateTaskAsync(task, originalTask);
        await RefreshTasksAndSyncAsync();
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
        var originalTask = task.Copy();
        task.Completed = !task.Completed;

        await database.UpdateTaskAsync(task, originalTask);
        await RefreshTasksAndSyncAsync();
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
        await RefreshTasksAndSyncAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync("Delete error", ex.Message, "OK");
    }
}
}