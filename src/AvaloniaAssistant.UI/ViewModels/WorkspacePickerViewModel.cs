using System.Collections.ObjectModel;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// 工作区浮层里的一行。<see cref="IsSelected"/> 是键盘上下键停在哪，
/// 和「当前工作区」（打勾那一行）不是一回事，所以两个状态分开存。
/// </summary>
public sealed partial class WorkspaceChoiceViewModel : ObservableObject
{
    public WorkspaceChoiceViewModel(string path, bool isCurrent)
    {
        Path = path;
        Name = WorkspaceNaming.ShortName(path);
        ShortPath = Ui.ShortenPath(path);
        IsCurrent = isCurrent;

        // 记录还在、目录被移走的很常见：这一行照样能点，但要让人看出它已经不在原处
        IsMissing = !Directory.Exists(path);
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>完整路径（用户目录缩成 ~），挂在悬停提示上，不占行的宽度。</summary>
    public string ShortPath { get; }

    public bool IsCurrent { get; }

    public bool IsMissing { get; }

    [ObservableProperty]
    private bool isSelected;
}

/// <summary>
/// 输入框下面那条工作区选择：搜索 + 列表 + 新建 / 打开 / 不使用。
/// 数据来自 <see cref="MainWindowViewModel"/>，和侧栏「空间」是同一份记录，
/// 不在这里另存一套，免得两边各改各的。
/// </summary>
public sealed partial class WorkspacePickerViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;
    private readonly List<WorkspaceChoiceViewModel> _all = [];

    public WorkspacePickerViewModel(MainWindowViewModel host)
    {
        _host = host;
        Reload();
    }

    /// <summary>浮层该收起来了：选中、新建成功、去选目录、不使用工作区，四种都从这里走。</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<WorkspaceChoiceViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public string EmptyText => string.IsNullOrWhiteSpace(SearchText)
        ? "还没有工作空间。选一个文件夹，任务就在里面读写文件。"
        : $"没有找到和「{SearchText.Trim()}」相关的工作空间。";

    /// <summary>新建模式下那行小字：说清目录会落在哪，别让人建完不知道去哪找。</summary>
    public string CreatePreview
    {
        get
        {
            var name = (NewName ?? string.Empty).Trim();
            var root = MainWindowViewModel.WorkspaceRoot;
            return "建在 " + Ui.ShortenPath(name.Length == 0 ? root : Path.Combine(root, name));
        }
    }

    [ObservableProperty]
    private string? searchText = string.Empty;

    [ObservableProperty]
    private int selectedIndex = -1;

    [ObservableProperty]
    private bool isCreating;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CreatePreview))]
    private string? newName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreateError))]
    private string? createError;

    public bool HasCreateError => !string.IsNullOrWhiteSpace(CreateError);

    /// <summary>重新读一遍工作区列表：每次打开浮层都调，刚在侧栏加过的能立刻看到。</summary>
    public void Reload()
    {
        _all.Clear();
        var settings = _host.Settings;

        foreach (var path in MainWindowViewModel.WorkspacePaths(settings))
        {
            _all.Add(new WorkspaceChoiceViewModel(path, MainWindowViewModel.SamePath(path, settings.WorkspacePath)));
        }

        ApplyFilter(preferCurrent: true);
    }

    partial void OnSearchTextChanged(string? value) => ApplyFilter(preferCurrent: false);

    partial void OnSelectedIndexChanged(int value) => SyncSelection();

    /// <param name="preferCurrent">
    /// 刚打开时把光标停在当前工作区上，这样「灰底 + 勾」落在同一行，一眼看出自己在哪；
    /// 一打字就按搜索结果从头排，否则搜出来的第一行可能被跳到中间去。
    /// </param>
    private void ApplyFilter(bool preferCurrent)
    {
        var query = (SearchText ?? string.Empty).Trim();

        Items.Clear();
        foreach (var item in _all)
        {
            if (query.Length == 0
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                Items.Add(item);
            }
        }

        if (Items.Count == 0)
        {
            SelectedIndex = -1;
        }
        else
        {
            var current = preferCurrent ? Items.ToList().FindIndex(item => item.IsCurrent) : -1;
            SelectedIndex = current >= 0 ? current : 0;
        }

        // 选中态不能只靠 SelectedIndex 的变化事件：重开浮层时初值可能就是 0，
        // 不会再触发一次，结果第一行不亮。
        SyncSelection();

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(EmptyText));
    }

    private void SyncSelection()
    {
        for (var index = 0; index < Items.Count; index++)
        {
            Items[index].IsSelected = index == SelectedIndex;
        }
    }

    /// <summary>键盘上下移动，到边界绕回去。</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var next = (SelectedIndex + delta) % Items.Count;
        if (next < 0)
        {
            next += Items.Count;
        }

        SelectedIndex = next;
    }

    public void ActivateSelection()
    {
        if (SelectedIndex >= 0 && SelectedIndex < Items.Count)
        {
            _ = ChooseAsync(Items[SelectedIndex]);
        }
    }

    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task ChooseAsync(WorkspaceChoiceViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // 先收浮层再切：切工作区会刷设置、重建侧栏，浮层挂在旧状态上会闪一下
        Close();
        await _host.SelectWorkspaceAsync(item.Path);
    }

    [RelayCommand]
    private void StartCreate()
    {
        CreateError = null;
        NewName = string.Empty;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        CreateError = null;
    }

    [RelayCommand]
    private async Task ConfirmCreateAsync()
    {
        var message = await _host.CreateWorkspaceAsync(NewName ?? string.Empty);
        if (message is not null)
        {
            CreateError = message;
            return;
        }

        Close();
    }

    /// <summary>打开一个已有的目录。目录对话框是模态窗口，先把浮层收掉再弹。</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        Close();
        await _host.AddWorkspaceAsync();
    }

    /// <summary>不带工作区：只清当前选择，列表里的记录一条都不删。</summary>
    [RelayCommand]
    private async Task ClearWorkspaceAsync()
    {
        Close();
        await _host.ClearWorkspaceAsync();
    }
}
