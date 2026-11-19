using ToTrak.App.Services;

namespace ToTrak.App;

public partial class TasksPage : MainPage
{
    public TasksPage(WorkspaceState workspace) : base(workspace)
    {
        InitializeComponent();
    }
}
