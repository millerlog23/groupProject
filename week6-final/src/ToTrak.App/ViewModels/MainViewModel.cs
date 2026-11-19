using System.Collections.ObjectModel;
using System.ComponentModel;
using ToTrak.App.Models;

namespace ToTrak.App.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<TaskDisplayItem> Tasks { get; } = new();
    public ObservableCollection<AssignmentDisplayItem> Assignments { get; } = new();
    public ObservableCollection<CourseItem> Courses { get; } = new();
    public ObservableCollection<LocationItem> Locations { get; } = new();
    public int TaskSortIndex { get; set; }
    private string statusText = "Sign in to load your data.";
    public string StatusText
    {
        get => statusText;
        set { statusText = value; PropertyChanged?.Invoke(this, new(nameof(StatusText))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
