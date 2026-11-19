using ToTrak.App.Services;

namespace ToTrak.App;

public partial class AppShell : Shell
{
    public const string TasksRoute = "//workspace/tasks";
    public const string AssignmentsRoute = "//workspace/assignments";
    public const string CoursesRoute = "//workspace/courses";

    public AppShell()
    {
        InitializeComponent();
        var workspace = new WorkspaceState();
        TasksContent.ContentTemplate = new DataTemplate(() => new TasksPage(workspace));
        AssignmentsContent.ContentTemplate = new DataTemplate(() => new AssignmentsPage(workspace));
        CoursesContent.ContentTemplate = new DataTemplate(() => new CoursesPage(workspace));
        CurrentItem = WorkspaceTabs;
        WorkspaceTabs.CurrentItem = TasksTab;
    }
}
