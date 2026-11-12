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
    private const int CurrentUserId = 4005;
    private bool hasInternetAccess;

    public MainPage()
    {
        InitializeComponent();

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

        TaskDatePicker.Date = DateTime.Today;
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
                StatusLabel.Text = $"Sync paused: {ex.Message}";
            }
        });
    }

    private async Task RefreshTasksAndSyncAsync()
    {
        await syncLock.WaitAsync();

        try
        {
            int taskCount = await LoadTasksAsync();

            int pendingCount =
                (await database.GetPendingChangesAsync(CurrentUserId)).Count;

            if (!HasInternetAccess(Connectivity.Current.NetworkAccess))
            {
                StatusLabel.Text =
                    $"{taskCount} task(s) • {pendingCount} change(s) saved offline";
                return;
            }

            StatusLabel.Text = "Sending local changes...";

            var pushResult = await SendPendingChangesAsync(CurrentUserId);

            if (pushResult.Failed)
            {
                StatusLabel.Text =
                    "Changes saved locally. Push failed; pull will wait.";
                return;
            }

            StatusLabel.Text = "Downloading server changes...";

            try
            {
                var pullResult = await PullChangesAsync(CurrentUserId);

                taskCount = await LoadTasksAsync();

                StatusLabel.Text = pullResult.PauseReason is null
                    ? $"{taskCount} task(s) • Sent {pushResult.SentCount}, "
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
                StatusLabel.Text =
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

    private async Task<(int SentCount, bool Failed)>
        SendPendingChangesAsync(int userId)
    {
        var pendingChanges = await database.GetPendingChangesAsync(userId);
        int sentCount = 0;

        foreach (var change in pendingChanges)
        {
            try
            {
                using var response =
                    await httpClient.PostAsJsonAsync("api/audit", change);

                if (!response.IsSuccessStatusCode)
                {
                    string details =
                        await response.Content.ReadAsStringAsync();

                    await DisplayAlertAsync(
                        "Push error",
                        $"HTTP {(int)response.StatusCode}\n{details}",
                        "OK");

                    return (sentCount, true);
                }

                var result =
                    await response.Content.ReadFromJsonAsync<SyncResult>();

                if (result is null)
                {
                    throw new InvalidOperationException(
                        "The server returned an empty sync result.");
                }

                await database.AcknowledgeChangeAsync(change, result);
                sentCount++;
            }
            catch (Exception ex) when (
                ex is HttpRequestException
                    or TaskCanceledException
                    or System.Text.Json.JsonException
                    or InvalidOperationException
                    or SQLite.SQLiteException)
            {
                await DisplayAlertAsync(
                    "Sync paused",
                    ex.Message,
                    "OK");

                return (sentCount, true);
            }
        }

        return (sentCount, false);
    }
    private async Task<(int Applied, string? PauseReason)>
    PullChangesAsync(int userId)
    {
        string scope =
            $"{httpClient.BaseAddress!.AbsoluteUri}|user:{userId}";

        long lastChangeId =
            await database.GetPullCursorAsync(scope);

        var changes =
            await httpClient.GetFromJsonAsync<List<ServerChange>>(
                $"changes?userId={userId}&after={lastChangeId}");

        if (changes is null)
        {
            throw new InvalidOperationException(
                "The server returned an empty response.");
        }

        return await database.ApplyIncomingChangesAsync(
            scope,
            userId,
            changes);
    }

    private async Task<int> LoadTasksAsync()
    {
        var tasks = await database.GetTasksAsync(CurrentUserId);

        TasksCollection.ItemsSource = tasks;
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
            StatusLabel.Text = $"Sync failed: {ex.Message}";
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
                    Text = "No audit records yet. Create or edit a task."
                });
            }

            foreach (var entry in entries)
            {
                string syncStatus = entry.IsSynced
                    ? "Synced to server"
                    : "Pending upload";

                history.Children.Add(new Border
                {
                    Padding = new Thickness(12),
                    Content = new Label
                    {
                        Text =
                            $"Task {entry.TaskId} • {entry.Action}\n" +
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