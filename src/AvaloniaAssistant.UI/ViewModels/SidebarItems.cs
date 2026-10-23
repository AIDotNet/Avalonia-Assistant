using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed class SessionGroupViewModel
{
    public SessionGroupViewModel(string header, IEnumerable<SessionItemViewModel> items)
    {
        Header = header;
        Items = new ObservableCollection<SessionItemViewModel>(items);
    }

    public string Header { get; }

    public ObservableCollection<SessionItemViewModel> Items { get; }
}

public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Action<string> _open;
    private readonly Action<string> _delete;

    public SessionItemViewModel(ChatSession session, Action<string> open, Action<string> delete)
    {
        Id = session.Id;
        Title = session.Title;
        TimeText = SessionText.RelativeTime(session.UpdatedAt);
        _open = open;
        _delete = delete;
    }

    public string Id { get; }

    public string Title { get; }

    public string TimeText { get; }

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    private bool confirmDelete;

    public string DeleteText => ConfirmDelete ? "确认" : "删除";

    [RelayCommand]
    private void Open() => _open(Id);

    [RelayCommand]
    private void Delete()
    {
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        _delete(Id);
    }
}

public sealed partial class WorkspaceItemViewModel : ObservableObject
{
    private readonly Action<string> _select;

    public WorkspaceItemViewModel(string path, Action<string> select)
    {
        Path = path;
        Name = string.IsNullOrWhiteSpace(path)
            ? "未设置"
            : System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : path;
        _select = select;
    }

    public string Path { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool isActive;

    [RelayCommand]
    private void Select() => _select(Path);
}
