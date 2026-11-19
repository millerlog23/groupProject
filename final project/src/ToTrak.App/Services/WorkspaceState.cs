using ToTrak.App.Data;
using ToTrak.App.ViewModels;

namespace ToTrak.App.Services;

// Shell pages share one account, data set and synchronization gate.
public sealed class WorkspaceState
{
    public MainViewModel ViewModel { get; } = new();
    public TaskDatabase Database { get; } = new();
    public SemaphoreSlim SyncLock { get; } = new(1, 1);
    public AccountSession? Session { get; private set; }
    public HttpClient? HttpClient { get; private set; }
    public bool Restored { get; set; }
    public event EventHandler? Changed;

    private bool isBusy;
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            if (isBusy == value) return;
            isBusy = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetSession(AccountSession? session)
    {
        HttpClient?.Dispose();
        Session = session;
        HttpClient = session is null ? null : AccountService.CreateClient(session);
        ViewModel.Tasks.Clear();
        ViewModel.Assignments.Clear();
        ViewModel.Courses.Clear();
        ViewModel.Locations.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
