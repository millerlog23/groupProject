using ToTrak.App.Services;
using ToTrak.App.ViewModels;
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
    public MainViewModel ViewModel { get; }
    private readonly TaskDatabase database = new();
    private readonly HttpClient httpClient;
private readonly SyncService syncService;
private readonly SemaphoreSlim syncLock = new(1, 1);
    private const int CurrentUserId = 4005;
    private bool hasInternetAccess;

    public MainPage()
{
    InitializeComponent();

    ViewModel = new MainViewModel(database, CurrentUserId);

ViewModel.RefreshRequestedAsync = RefreshTasksAndSyncAsync;
ViewModel.RequestEditNameAsync = task =>
    DisplayPromptAsync(
        "Edit task",
        "Enter the task name:",
        initialValue: task.TaskName);

ViewModel.RequestDeleteConfirmationAsync = task =>
    DisplayAlertAsync(
        "Delete task",
        $"Delete \"{task.TaskName}\"?",
        "Delete",
        "Cancel");
ViewModel.RequestAssignmentEditNameAsync = assignment =>
    DisplayPromptAsync(
        "Edit assignment",
        "Enter the assignment name:",
        initialValue: assignment.AssignmentName);
ViewModel.RequestAssignmentDeleteConfirmationAsync = assignment =>
    DisplayAlertAsync(
        "Delete assignment",
        $"Delete \"{assignment.AssignmentName}\"?",
        "Delete",
        "Cancel");
BindingContext = ViewModel;

        var defaultApiBaseUrl =
            DeviceInfo.Platform == DevicePlatform.Android &&
            DeviceInfo.DeviceType == DeviceType.Virtual
                ? "http://10.0.2.2:5125/"
                : "http://127.0.0.1:5125/";

        var apiBaseUrl = Preferences.Get(
            "ApiBaseUrl", defaultApiBaseUrl);

        httpClient = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromSeconds(15)
        };
        syncService = new SyncService(database, httpClient);

    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        hasInternetAccess = HasInternetAccess(Connectivity.Current.NetworkAccess);
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
        bool internetAvailable = HasInternetAccess(e.NetworkAccess);
        bool reconnected = internetAvailable && !hasInternetAccess;
        hasInternetAccess = internetAvailable;

        if (!reconnected)
            return;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                await RefreshTasksAndSyncAsync();
            }
            catch (Exception ex)
            {
                ViewModel.StatusText = $"Sync paused: {ex.Message}";
            }
        });
    }

    private async Task RefreshTasksAndSyncAsync()
    {
        await syncLock.WaitAsync();

        try
        {
            int taskCount = await LoadTasksAsync();
            int assignmentCount = ViewModel.Assignments.Count;

            int pendingCount =
                (await database.GetPendingChangesAsync(CurrentUserId)).Count;

            if (!HasInternetAccess(Connectivity.Current.NetworkAccess))
            {
                ViewModel.StatusText =
                    $"{taskCount} task(s), {assignmentCount} assignment(s) • " +
                    $"{pendingCount} change(s) saved offline";
                return;
            }

            ViewModel.StatusText = "Sending local changes...";

            var pushResult = await syncService.SendPendingChangesAsync(CurrentUserId);

            if (pushResult.Failed)
            {
                ViewModel.StatusText =
                    "Changes saved locally. Push failed; pull will wait.";
                return;
            }

            ViewModel.StatusText = "Downloading server changes...";

            try
            {
                var pullResult = await syncService.PullChangesAsync(CurrentUserId);

                taskCount = await LoadTasksAsync();
                assignmentCount = ViewModel.Assignments.Count;

                ViewModel.StatusText = pullResult.PauseReason is null
                    ? $"{taskCount} task(s), {assignmentCount} assignment(s) • " +
                        $"Sent {pushResult.SentCount}, "
                        + $"applied {pullResult.Applied} change(s)"
                    : $"Applied {pullResult.Applied} change(s). "
                        + $"Pull paused: {pullResult.PauseReason}.";
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                    or TaskCanceledException
                    or System.Text.Json.JsonException
                    or InvalidOperationException
                    or SQLite.SQLiteException)
            {
                ViewModel.StatusText =
                    $"Pull failed; local tasks are still available: {ex.Message}";
            }
        }
        finally
        {
            syncLock.Release();
        }
    }

    private static bool HasInternetAccess(NetworkAccess access) =>
        access is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;


    private async Task<int> LoadTasksAsync()
{
    var tasks = await database.GetTasksAsync(CurrentUserId);
    var assignments = await database.GetAssignmentsAsync(CurrentUserId);
    var courses = await database.GetCoursesAsync(CurrentUserId);

    ViewModel.Tasks.Clear();

    foreach (var task in tasks)
    {
        ViewModel.Tasks.Add(task);
    }

    ViewModel.Assignments.Clear();
    ViewModel.Courses.Clear();
    foreach (var course in courses)
        ViewModel.Courses.Add(course);

    var courseNames = courses.ToDictionary(course => course.CourseId, course => course.CourseName);
    foreach (var assignment in assignments)
    {
        string courseName = courseNames.TryGetValue(assignment.CourseId, out var name)
            ? name
            : "Unassigned";
        ViewModel.Assignments.Add(new AssignmentDisplayItem(assignment, courseName));
    }

    return tasks.Count;
}

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        SyncNowButton.IsEnabled = false;

        try
        {
            await RefreshTasksAndSyncAsync();
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Sync failed: {ex.Message}";
        }
        finally
        {
            SyncNowButton.IsEnabled = true;
        }
    }

    private async void OnViewAuditClicked(object? sender, EventArgs e)
    {
        try
        {
            var entries = await database.GetAuditEntriesAsync(CurrentUserId);

            var history = new VerticalStackLayout
            {
                Padding = new Thickness(16),
                Spacing = 12
            };

            history.Children.Add(new Label
            {
                Text = $"Audit History — User {CurrentUserId}",
                FontSize = 24,
                FontAttributes = FontAttributes.Bold
            });

            history.Children.Add(new Label
            {
                Text = "Latest 50 records. Times shown in UTC."
            });

            if (entries.Count == 0)
            {
                history.Children.Add(new Label
                {
                    Text = "No audit records yet. Create a task, assignment, or course."
                });
            }

            foreach (var entry in entries)
            {
                string syncStatus = entry.IsSynced
                    ? "Synced to server"
                    : "Pending upload";
                string itemId = entry.CourseId.HasValue
                    ? $"Course {entry.CourseId}"
                    : entry.AssignmentId.HasValue
                        ? $"Assignment {entry.AssignmentId}"
                        : $"Task {entry.TaskId}";

                history.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    Content = new Label
                    {
                        Text =
                            $"{itemId} • {entry.Action}\n" +
                            $"{entry.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC\n" +
                            $"Outcome: {entry.Outcome}\n" +
                            $"Sync: {syncStatus}\n" +
                            $"Device: {entry.DeviceId}\n" +
                            $"Audit ID: {entry.AuditId}\n" +
                            $"{entry.Details}\n\n" +
                            $"Before: {entry.OldValue ?? "(none)"}\n" +
                            $"After: {entry.NewValue ?? "(none)"}",
                        FontSize = 14
                    }
                });
            }

            var closeButton = new Button
            {
                Text = "Close"
            };

            closeButton.Clicked += async (_, _) =>
                await Navigation.PopModalAsync();

            var layout = new Grid
            {
                Padding = new Thickness(12),
                RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Star }
            }
            };

            layout.Children.Add(closeButton);

            var scroll = new ScrollView { Content = history };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            await Navigation.PushModalAsync(new ContentPage
            {
                Title = "Audit History",
                Content = layout
            });
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Audit history error", ex.Message, "OK");
        }
    }

    private async void OnAddTaskClicked(object? sender, EventArgs e)
{
    if (string.IsNullOrWhiteSpace(ViewModel.TaskName))
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
        await ViewModel.AddTaskAsync(CurrentUserId);

        await RefreshTasksAndSyncAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync(
            "Save error",
            ex.Message,
            "OK");
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

    button.IsEnabled = false;

    try
    {
        string? newName = await DisplayPromptAsync(
            "Edit task",
            "Enter the task name:",
            initialValue: task.TaskName);

        if (newName is null)
            return;

        await ViewModel.EditTaskAsync(task, newName);

        await RefreshTasksAndSyncAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync(
            "Update error",
            ex.Message,
            "OK");
    }
    finally
    {
        button.IsEnabled = true;
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
        await ViewModel.ToggleTaskAsync(task);

        await RefreshTasksAndSyncAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync(
            "Update error",
            ex.Message,
            "OK");
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

    button.IsEnabled = false;

    try
    {
        bool confirmed = await DisplayAlertAsync(
            "Delete task",
            $"Delete \"{task.TaskName}\"?",
            "Delete",
            "Cancel");

        if (!confirmed)
            return;

        await ViewModel.DeleteTaskAsync(task);

        await RefreshTasksAndSyncAsync();
    }
    catch (Exception ex)
    {
        await DisplayAlertAsync(
            "Delete error",
            ex.Message,
            "OK");
    }
    finally
    {
        button.IsEnabled = true;
    }
}
}