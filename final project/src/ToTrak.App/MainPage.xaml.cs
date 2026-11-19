using System.Collections.ObjectModel;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Networking;
using ToTrak.App.Data;
using ToTrak.App.Models;
using ToTrak.App.Services;
using ToTrak.App.ViewModels;

namespace ToTrak.App;

public partial class MainPage : ContentPage
{
    private readonly WorkspaceState workspace;
    public MainViewModel ViewModel => workspace.ViewModel;
    private TaskDatabase database => workspace.Database;
    private SemaphoreSlim syncLock => workspace.SyncLock;
    private AccountSession? session => workspace.Session;
    private HttpClient? httpClient => workspace.HttpClient;
    private bool restored { get => workspace.Restored; set => workspace.Restored = value; }
    private bool busy { get => workspace.IsBusy; set => workspace.IsBusy = value; }
    private bool online;

    public View WorkspaceBody
    {
        get => WorkspaceBodyHost.Content;
        set => WorkspaceBodyHost.Content = value;
    }

    protected MainPage(WorkspaceState workspace)
    {
        this.workspace = workspace;
        InitializeComponent();
        BindingContext = ViewModel;
        workspace.Changed += OnWorkspaceChanged;
        UpdateWorkspaceChrome();
    }

    private void OnWorkspaceChanged(object? sender, EventArgs e) => UpdateWorkspaceChrome();

    private void UpdateWorkspaceChrome()
    {
        LoginPanel.IsVisible = session is null;
        WorkspacePanel.IsVisible = session is not null;
        WorkspacePanel.IsEnabled = !busy;
        SignInButton.IsEnabled = RegisterButton.IsEnabled = !busy;
        AccountLabel.Text = session is null ? "" : $"{session.Username} | User {session.UserId}";
        UpdateNavigationButton(TasksNavigationButton, this is TasksPage);
        UpdateNavigationButton(AssignmentsNavigationButton, this is AssignmentsPage);
        UpdateNavigationButton(CoursesNavigationButton, this is CoursesPage);
    }

    private static void UpdateNavigationButton(Button button, bool selected)
    {
        button.SetAppThemeColor(BackgroundColorProperty,
            selected ? Color.FromArgb("#EEE8FA") : Colors.Transparent,
            selected ? Color.FromArgb("#342A4A") : Colors.Transparent);
        button.SetAppThemeColor(Button.TextColorProperty,
            selected ? Color.FromArgb("#512BD4") : Color.FromArgb("#6E6E6E"),
            selected ? Color.FromArgb("#AC99EA") : Color.FromArgb("#ACACAC"));
        button.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
        SemanticProperties.SetDescription(button, selected ? $"{button.Text}, selected" : button.Text);
    }

    private async Task NavigateAsync(string route)
    {
        if (Shell.Current.CurrentState.Location.OriginalString.StartsWith(route, StringComparison.Ordinal)) return;
        try { await Shell.Current.GoToAsync(route); }
        catch (Exception ex) { await DisplayAlertAsync("Navigation", ex.Message, "OK"); }
    }

    protected async void OnTasksNavigationClicked(object? sender, EventArgs e) => await NavigateAsync(AppShell.TasksRoute);
    protected async void OnAssignmentsNavigationClicked(object? sender, EventArgs e) => await NavigateAsync(AppShell.AssignmentsRoute);
    protected async void OnCoursesNavigationClicked(object? sender, EventArgs e) => await NavigateAsync(AppShell.CoursesRoute);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        online = HasNetwork;
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        if (busy) return;
        await ExecuteAsync(async () =>
        {
            if (!restored)
            {
                restored = true;
                SetSession(await AccountService.RestoreAsync());
            }
            if (session is not null) await RefreshTasksAndSyncAsync();
        });
    }

    protected override void OnDisappearing()
    {
        Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;
        base.OnDisappearing();
    }

    private static bool HasNetwork => Connectivity.Current.NetworkAccess is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        bool reconnect = HasNetwork && !online;
        online = HasNetwork;
        if (reconnect) MainThread.BeginInvokeOnMainThread(async () => await ExecuteAsync(RefreshTasksAndSyncAsync));
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        try { await action(); }
        catch (Exception ex)
        {
            if (session is null) LoginStatus.Text = ex.Message;
            else await DisplayAlertAsync("ToTrak", ex.Message, "OK");
        }
        finally
        {
            busy = false;
        }
    }

    private void SetSession(AccountSession? value) => workspace.SetSession(value);

    // Available before sign-in so another device can configure its private tunnel.
    protected async void OnConnectionSettingsClicked(object? sender, EventArgs e)
        => await Navigation.PushModalAsync(new ConnectionSettingsPage());

    protected async void OnSignInClicked(object? sender, EventArgs e) => await SignInAsync(false);
    protected async void OnRegisterClicked(object? sender, EventArgs e) => await SignInAsync(true);

    private async Task SignInAsync(bool register) => await ExecuteAsync(async () =>
    {
        LoginStatus.Text = register ? "Creating account…" : "Signing in…";
        try
        {
            SetSession(await AccountService.SignInAsync(UsernameEntry.Text ?? "", PasswordEntry.Text ?? "", register));
            LoginStatus.Text = "";
            await RefreshTasksAndSyncAsync();
            await Shell.Current.GoToAsync(AppShell.TasksRoute);
        }
        finally { PasswordEntry.Text = ""; }
    });

    protected async void OnSignOutClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (session is null) return;
        await syncLock.WaitAsync();
        try
        {
            await AccountService.SignOutAsync(session);
            SetSession(null);
            LoginStatus.Text = "Signed out. Pending changes are kept for your next sign in.";
        }
        finally { syncLock.Release(); }
        await Shell.Current.GoToAsync(AppShell.TasksRoute);
    });

    private async Task LoadAsync(int userId)
    {
        var courses = await database.GetCoursesAsync(userId);
        var locations = await database.GetLocationsAsync(userId);
        Replace(ViewModel.Courses, courses);
        Replace(ViewModel.Locations, locations);
        var names = courses.ToDictionary(x => x.CourseId, x => x.CourseName);
        Replace(ViewModel.Assignments, (await database.GetAssignmentsAsync(userId)).Select(x =>
            new AssignmentDisplayItem(x, names.GetValueOrDefault(x.CourseId, "Unassigned"))));
        var locationNames = locations.ToDictionary(x => x.LocationId, x => x.LocationName);
        IEnumerable<TaskDisplayItem> tasks = (await database.GetTasksAsync(userId)).Select(x =>
            new TaskDisplayItem(x, locationNames.GetValueOrDefault(x.TaskLocation, "Unset")));
        tasks = ViewModel.TaskSortIndex switch
        {
            1 => tasks.OrderBy(x => x.LocationName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.TaskTime),
            2 => tasks.OrderBy(x => x.TaskType, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.TaskTime),
            _ => tasks.OrderBy(x => x.TaskTime)
        };
        Replace(ViewModel.Tasks, tasks);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private async Task RefreshTasksAndSyncAsync()
    {
        await syncLock.WaitAsync();
        try
        {
            if (session is null || httpClient is null) return;
            int userId = session.UserId;
            await LoadAsync(userId);
            int pending = (await database.GetPendingChangesAsync(userId)).Count;
            if (!HasNetwork)
            {
                ViewModel.StatusText = $"Offline • {pending} changes waiting to sync.";
                return;
            }
            ViewModel.StatusText = "Synchronizing…";
            var sync = new SyncService(database, httpClient);
            var push = await sync.SendPendingChangesAsync(userId);
            await LoadAsync(userId);
            if (push.Failed)
            {
                ViewModel.StatusText = $"Saved locally. Sync paused: {push.Error}";
                return;
            }
            try
            {
                var pull = await sync.PullChangesAsync(userId);
                await LoadAsync(userId);
                ViewModel.StatusText = pull.PauseReason is null
                    ? $"Synced • {push.SentCount} uploaded, {pull.Applied} downloaded."
                    : $"Sync paused: {pull.PauseReason}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or SQLite.SQLiteException)
            { ViewModel.StatusText = $"Local data is available. Pull paused: {ex.Message}"; }
        }
        finally { syncLock.Release(); }
    }

    protected async void OnSyncNowClicked(object? sender, EventArgs e) => await ExecuteAsync(RefreshTasksAndSyncAsync);
    protected async void OnTaskSortChanged(object? sender, EventArgs e)
    {
        if (sender is Picker picker) ViewModel.TaskSortIndex = picker.SelectedIndex;
        if (session is not null) await ExecuteAsync(() => LoadAsync(session.UserId));
    }

    private static Entry TextInput(string? text, string id) => new() { Text = text, AutomationId = id };
    private static View Field(string title, View input) => new VerticalStackLayout { Spacing = 4, Children = { new Label { Text = title }, input } };
    private static T? Item<T>(object? sender) where T : class => (sender as Button)?.CommandParameter as T;

    private async Task<bool> ShowEditorAsync(string title, IEnumerable<View> fields, Func<Task> save)
    {
        var result = new TaskCompletionSource<bool>();
        bool saved = false;
        var error = new Label { TextColor = Colors.Red };
        var saveButton = new Button { Text = "Save", AutomationId = "SaveEditorButton" };
        var cancel = new Button { Text = "Cancel" };
        var content = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        content.Children.Add(new Label { Text = title, FontSize = 24, FontAttributes = FontAttributes.Bold });
        foreach (var field in fields) content.Children.Add(field);
        content.Children.Add(error);
        content.Children.Add(saveButton);
        content.Children.Add(cancel);
        var page = new ContentPage { Title = title, Content = new ScrollView { Content = content } };
        page.Disappearing += (_, _) => result.TrySetResult(saved);
        cancel.Clicked += async (_, _) => await Navigation.PopModalAsync();
        saveButton.Clicked += async (_, _) =>
        {
            saveButton.IsEnabled = cancel.IsEnabled = false;
            try { await save(); saved = true; await Navigation.PopModalAsync(); }
            catch (Exception ex) { error.Text = ex.Message; }
            finally { saveButton.IsEnabled = cancel.IsEnabled = true; }
        };
        await Navigation.PushModalAsync(page);
        return await result.Task;
    }

    private async Task EditTaskAsync(TaskItem? original)
    {
        if (session is null) return;
        var item = original?.Copy() ?? new TaskItem { UserId = session.UserId, TaskTime = DateTime.Now, TaskType = "General" };
        var name = TextInput(item.TaskName, "TaskNameEntry");
        var type = TextInput(item.TaskType, "TaskTypeEntry");
        var date = new DatePicker { Date = item.TaskTime.Date, AutomationId = "TaskDatePicker" };
        var time = new TimePicker { Time = item.TaskTime.TimeOfDay, AutomationId = "TaskTimePicker" };
        var done = new Switch { IsToggled = item.Completed };
        var choices = new List<LocationItem> { new() { LocationId = 0, LocationName = "Unset" } };
        choices.AddRange(await database.GetLocationsAsync(session.UserId));
        var location = new Picker { ItemsSource = choices, ItemDisplayBinding = new Binding(nameof(LocationItem.LocationName)), AutomationId = "TaskLocationPicker" };
        location.SelectedItem = choices.FirstOrDefault(x => x.LocationId == item.TaskLocation) ?? choices[0];
        bool saved = await ShowEditorAsync(original is null ? "Add task" : "Edit task", [
            Field("Task name", name), Field("Type", type), Field("Date", date), Field("Time", time),
            Field("Location", location), Field("Completed", done)], async () =>
        {
            item.TaskName = name.Text?.Trim() ?? "";
            item.TaskType = type.Text?.Trim() ?? "";
            item.TaskTime = (date.Date ?? DateTime.Today).Date + (time.Time ?? TimeSpan.Zero);
            item.TaskLocation = ((LocationItem)location.SelectedItem).LocationId;
            item.Completed = done.IsToggled;
            if (original is null) await database.AddTaskAsync(item);
            else await database.UpdateTaskAsync(item, original);
        });
        if (saved) await RefreshTasksAndSyncAsync();
    }

    protected async void OnAddTaskClicked(object? sender, EventArgs e) => await ExecuteAsync(() => EditTaskAsync(null));
    protected async void OnEditTaskClicked(object? sender, EventArgs e) { if (Item<TaskItem>(sender) is { } item) await ExecuteAsync(() => EditTaskAsync(item)); }
    protected async void OnToggleTaskClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<TaskItem>(sender) is not { } original) return;
        var item = original.Copy(); item.Completed = !item.Completed;
        await database.UpdateTaskAsync(item, original);
        await RefreshTasksAndSyncAsync();
    });
    protected async void OnDeleteTaskClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<TaskItem>(sender) is not { } item || !await DisplayAlertAsync("Delete task", $"Delete {item.TaskName}?", "Delete", "Cancel")) return;
        await database.DeleteTaskAsync(item.UserId, item.TaskId);
        await RefreshTasksAndSyncAsync();
    });

    private async Task EditAssignmentAsync(AssignmentItem? original)
    {
        if (session is null) return;
        var courses = await database.GetCoursesAsync(session.UserId);
        if (courses.Count == 0) { await DisplayAlertAsync("Course required", "Add a course before adding an assignment.", "OK"); return; }
        var item = original?.Copy() ?? new AssignmentItem { UserId = session.UserId, AssignmentTime = DateTime.Now, AssignmentType = "General" };
        var name = TextInput(item.AssignmentName, "AssignmentNameEntry");
        var type = TextInput(item.AssignmentType, "AssignmentTypeEntry");
        var description = new Editor { Text = item.AssignmentDescription, AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 80 };
        var minutes = TextInput(item.EstimatedTime?.ToString(), "EstimatedTimeEntry"); minutes.Keyboard = Keyboard.Numeric;
        var date = new DatePicker { Date = item.AssignmentTime.Date };
        var time = new TimePicker { Time = item.AssignmentTime.TimeOfDay };
        var done = new Switch { IsToggled = item.Completed };
        var course = new Picker { ItemsSource = courses, ItemDisplayBinding = new Binding(nameof(CourseItem.CourseName)), AutomationId = "AssignmentCoursePicker" };
        course.SelectedItem = courses.FirstOrDefault(x => x.CourseId == item.CourseId) ?? courses[0];
        bool saved = await ShowEditorAsync(original is null ? "Add assignment" : "Edit assignment", [
            Field("Title", name), Field("Course", course), Field("Type", type), Field("Description (optional)", description),
            Field("Due date", date), Field("Due time", time), Field("Estimated minutes (optional)", minutes), Field("Completed", done)], async () =>
        {
            int? estimate = null;
            if (!string.IsNullOrWhiteSpace(minutes.Text))
            {
                if (!int.TryParse(minutes.Text, out int value) || value < 0) throw new ArgumentException("Estimated minutes must be a nonnegative whole number.");
                estimate = value;
            }
            item.AssignmentName = name.Text?.Trim() ?? "";
            item.AssignmentType = type.Text?.Trim() ?? "";
            item.AssignmentDescription = string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim();
            item.EstimatedTime = estimate;
            item.AssignmentTime = (date.Date ?? DateTime.Today).Date + (time.Time ?? TimeSpan.Zero);
            item.CourseId = ((CourseItem)course.SelectedItem).CourseId;
            item.Completed = done.IsToggled;
            if (original is null) await database.AddAssignmentAsync(item, "", item.CourseId);
            else await database.UpdateAssignmentAsync(item, original);
        });
        if (saved) await RefreshTasksAndSyncAsync();
    }

    protected async void OnAddAssignmentClicked(object? sender, EventArgs e) => await ExecuteAsync(() => EditAssignmentAsync(null));
    protected async void OnEditAssignmentClicked(object? sender, EventArgs e) { if (Item<AssignmentItem>(sender) is { } item) await ExecuteAsync(() => EditAssignmentAsync(item)); }
    protected async void OnToggleAssignmentClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<AssignmentItem>(sender) is not { } original) return;
        var item = original.Copy(); item.Completed = !item.Completed;
        await database.UpdateAssignmentAsync(item, original);
        await RefreshTasksAndSyncAsync();
    });
    protected async void OnDeleteAssignmentClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<AssignmentItem>(sender) is not { } item || !await DisplayAlertAsync("Delete assignment", $"Delete {item.AssignmentName}?", "Delete", "Cancel")) return;
        await database.DeleteAssignmentAsync(item.UserId, item.AssignmentId);
        await RefreshTasksAndSyncAsync();
    });

    private async Task EditCourseAsync(CourseItem? original)
    {
        if (session is null) return;
        var item = original?.Copy() ?? new CourseItem { UserId = session.UserId };
        var name = TextInput(item.CourseName, "CourseNameEntry");
        var description = new Editor { Text = item.CourseDescription, MinimumHeightRequest = 80, AutoSize = EditorAutoSizeOption.TextChanges };
        if (await ShowEditorAsync(original is null ? "Add course" : "Edit course", [Field("Course name", name), Field("Description (optional)", description)], async () =>
        {
            item.CourseName = name.Text?.Trim() ?? "";
            item.CourseDescription = string.IsNullOrWhiteSpace(description.Text) ? null : description.Text.Trim();
            await database.SaveCourseAsync(item, original);
        })) await RefreshTasksAndSyncAsync();
    }

    protected async void OnAddCourseClicked(object? sender, EventArgs e) => await ExecuteAsync(() => EditCourseAsync(null));
    protected async void OnEditCourseClicked(object? sender, EventArgs e) { if (Item<CourseItem>(sender) is { } item) await ExecuteAsync(() => EditCourseAsync(item)); }
    protected async void OnDeleteCourseClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<CourseItem>(sender) is not { } original || !await DisplayAlertAsync("Delete course", $"Delete {original.CourseName}? Move or delete its assignments first.", "Delete", "Cancel")) return;
        var item = original.Copy(); item.IsDeleted = true;
        await database.SaveCourseAsync(item, original);
        await RefreshTasksAndSyncAsync();
    });

    private async Task EditLocationAsync(LocationItem? original)
    {
        if (session is null) return;
        var item = original?.Copy() ?? new LocationItem { UserId = session.UserId };
        var name = TextInput(item.LocationName, "LocationNameEntry");
        if (await ShowEditorAsync(original is null ? "Add location" : "Rename location", [Field("Location name", name)], async () =>
        {
            item.LocationName = name.Text?.Trim() ?? "";
            await database.SaveLocationAsync(item, original);
        })) await RefreshTasksAndSyncAsync();
    }

    protected async void OnAddLocationClicked(object? sender, EventArgs e) => await ExecuteAsync(() => EditLocationAsync(null));
    protected async void OnEditLocationClicked(object? sender, EventArgs e) { if (Item<LocationItem>(sender) is { } item) await ExecuteAsync(() => EditLocationAsync(item)); }
    protected async void OnDeleteLocationClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (Item<LocationItem>(sender) is not { } original || !await DisplayAlertAsync("Delete location", $"Delete {original.LocationName}? Its tasks will show Unset.", "Delete", "Cancel")) return;
        var item = original.Copy(); item.IsDeleted = true;
        await database.SaveLocationAsync(item, original);
        await RefreshTasksAndSyncAsync();
    });

    protected async void OnViewAuditClicked(object? sender, EventArgs e) => await ExecuteAsync(async () =>
    {
        if (session is null) return;
        var entries = await database.GetAuditEntriesAsync(session.UserId);
        var content = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        var close = new Button { Text = "Close" };
        close.Clicked += async (_, _) => await Navigation.PopModalAsync();
        content.Children.Add(close);
        content.Children.Add(new Label { Text = "Latest 50 local changes • UTC", FontSize = 22 });
        foreach (var entry in entries)
        {
            string entity = entry.LocationId.HasValue ? $"Location {entry.LocationId}" : entry.CourseId.HasValue
                ? $"Course {entry.CourseId}" : entry.AssignmentId.HasValue ? $"Assignment {entry.AssignmentId}" : $"Task {entry.TaskId}";
            content.Children.Add(new Border { Padding = 12, Content = new Label { Text =
                $"{entity} • {entry.Action}\n{entry.OccurredAtUtc:yyyy-MM-dd HH:mm:ss} UTC\n" +
                $"{entry.Outcome} • {(entry.IsSynced ? "Synced" : "Pending upload")}\n{entry.Details}\n" +
                $"Before: {entry.OldValue ?? "(none)"}\nAfter: {entry.NewValue ?? "(none)"}" } });
        }
        if (entries.Count == 0) content.Children.Add(new Label { Text = "No local changes yet." });
        var page = new ContentPage { Title = "Audit History", Content = new ScrollView { Content = content } };
        var closed = new TaskCompletionSource();
        page.Disappearing += (_, _) => closed.TrySetResult();
        await Navigation.PushModalAsync(page);
        await closed.Task;
    });
}
