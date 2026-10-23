using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed record SuggestionCardViewModel(
    string Title,
    string Subtitle,
    string Prompt,
    Geometry Icon);

public sealed record QuickPromptViewModel(string Label, string Prompt);

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;

    public HomeViewModel(MainWindowViewModel host)
    {
        _host = host;
        Composer = new ComposerViewModel(host.Runtime, host.SettingsProvider, host.Picker, host.Voice, host.OpenSettingsAsync, host.Library, host.FileSearch);
        Composer.SendRequested += async (_, request) => await host.StartNewChatAsync(request);

        Cards =
        [
            new("查看这台电脑的配置", "系统、CPU、内存、磁盘一次看清",
                "帮我看下这台电脑的配置，整理成一份 Markdown 放到工作区。", ArcIcons.Monitor),
            new("整理下载目录", "找出大文件，按类型归类",
                "帮我看看下载目录里有哪些超过 100MB 的文件，列出来。", ArcIcons.FolderOpen),
            new("写一份项目周报", "把零散进展收成三段",
                "帮我写一份这周的项目周报，语气务实一点，不要空话。", ArcIcons.FileText),
            new("安排今天的优先级", "把待办排成能做的顺序",
                "帮我按重要和紧急排一下今天的待办，并说明先做哪件。", ArcIcons.Clock),
        ];

        QuickPrompts =
        [
            new("梳理这个文件夹", "帮我梳理一下工作区里的文件结构，告诉我哪些可以归档。"),
            new("查一下本机配置", "帮我看下这台电脑的配置。"),
            new("写一份周报", "帮我写一份这周的项目周报。"),
            new("总结一篇文档", "帮我总结工作区里最重要的一份文档。"),
            new("下载目录大文件", "列出下载目录里超过 100MB 的文件。"),
        ];

        Mode = host.Settings.Mode;
    }

    public ComposerViewModel Composer { get; }

    public ObservableCollection<SuggestionCardViewModel> Cards { get; }

    public ObservableCollection<QuickPromptViewModel> QuickPrompts { get; }

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            var part = hour switch
            {
                < 6 => "夜深了",
                < 11 => "早上好",
                < 14 => "中午好",
                < 18 => "下午好",
                _ => "晚上好",
            };

            return $"{part}，{_host.UserName}";
        }
    }

    public string Headline => "今天想做什么？";

    public string Subtitle => "说清目标就行，我来拆解、调用工具、把结果交给你。";

    public bool IsConfigured => _host.IsConfigured;

    public bool ShowSetupBanner => !IsConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffice))]
    [NotifyPropertyChangedFor(nameof(IsDevelopment))]
    [NotifyPropertyChangedFor(nameof(IsDesign))]
    private string mode;

    public bool IsOffice => Mode == "office";

    public bool IsDevelopment => Mode == "development";

    public bool IsDesign => Mode == "design";

    [RelayCommand]
    private async Task SetModeAsync(string mode)
    {
        Mode = mode;
        _host.Settings.Mode = mode;
        await _host.SettingsProvider.SaveAsync(_host.Settings);
    }

    [RelayCommand]
    private async Task UseCardAsync(SuggestionCardViewModel card)
        => await _host.StartNewChatAsync(card.Prompt);

    [RelayCommand]
    private void UseQuickPrompt(QuickPromptViewModel prompt)
    {
        Composer.Text = prompt.Prompt;
        Composer.RequestFocus();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync() => await _host.OpenSettingsAsync();

    [RelayCommand]
    private async Task AddWorkspaceAsync() => await _host.AddWorkspaceAsync();

    public void Refresh()
    {
        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ShowSetupBanner));
        Composer.Refresh();
    }
}
