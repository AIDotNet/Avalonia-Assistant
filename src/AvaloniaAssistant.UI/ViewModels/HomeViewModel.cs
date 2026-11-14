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

/// <summary>首页「类别胶囊」。选中项决定下面四张模板卡，照 WorkBuddy 的「选类别 → 换模板」。</summary>
public sealed record SuggestionCategoryViewModel(string Key, string Label, bool IsSelected);

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;

    public HomeViewModel(MainWindowViewModel host)
    {
        _host = host;
        Composer = new ComposerViewModel(host);
        Composer.SendRequested += async (_, request) => await host.StartNewChatAsync(request);

        Mode = NormalizeMode(host.Settings.Mode);
        ApplyMode(Mode);

        // 输入框已经就地提示「还没配模型服务」时，顶部那条橙色横幅就别再叠一遍。
        Composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposerViewModel.HasSetupHint))
            {
                OnPropertyChanged(nameof(ShowSetupBanner));
            }
        };
    }

    public ComposerViewModel Composer { get; }

    /// <summary>首页右上角的「工作区」和输入框下面那条挂的是同一个浮层。</summary>
    public MainWindowViewModel Host => _host;

    public ObservableCollection<SuggestionCardViewModel> Cards { get; } = [];

    public ObservableCollection<QuickPromptViewModel> QuickPrompts { get; } = [];

    public ObservableCollection<SuggestionCategoryViewModel> Categories { get; } = [];

    /// <summary>模式（场景）下面那句话：让「日常办公 / 代码开发 / 设计创意」点完有可见的解释。</summary>
    public string ModeHint => _modeHint;

    private string _modeHint = string.Empty;

    private const string CategoryAll = "all";

    /// <summary>
    /// 类别胶囊。顺序照 WorkBuddy 首页：先「全部」回到当前场景那一套，再按办公场景分。
    /// 每个类别都是真实能用现有工具跑的模板，不做点进去没有下文的假入口。
    /// </summary>
    private static readonly (string Key, string Label)[] CategoryDefs =
    [
        (CategoryAll, "全部"),
        ("documents", "文档处理"),
        ("data", "数据分析"),
        ("slides", "幻灯片"),
        ("research", "深度研究"),
        ("files", "文件整理"),
    ];

    [ObservableProperty]
    private string selectedCategory = CategoryAll;

    /// <summary>个别类别有边界要说清（比如不生成 pptx），选到它才显示，不占常驻版面。</summary>
    public string CategoryHint => SelectedCategory switch
    {
        "slides" => "生成汇报大纲与逐页讲稿（docx），不直接生成 pptx",
        "research" => "联网抓取走 fetch_url；端点不可达时会给出提示",
        _ => string.Empty,
    };

    public bool HasCategoryHint => !string.IsNullOrEmpty(CategoryHint);

    private void ApplyMode(string mode)
    {
        QuickPrompts.Clear();
        foreach (var prompt in PromptsFor(mode))
        {
            QuickPrompts.Add(prompt);
        }

        _modeHint = HintFor(mode);
        OnPropertyChanged(nameof(ModeHint));

        // 换场景回到「全部」，让它和这一场景的默认模板对齐。
        SelectedCategory = CategoryAll;
        RebuildCategories();
        RebuildCards();
        Composer.SetPreview(null);
    }

    private void RebuildCategories()
    {
        Categories.Clear();
        foreach (var (key, label) in CategoryDefs)
        {
            Categories.Add(new SuggestionCategoryViewModel(key, label, key == SelectedCategory));
        }

        OnPropertyChanged(nameof(CategoryHint));
        OnPropertyChanged(nameof(HasCategoryHint));
    }

    private void RebuildCards()
    {
        Cards.Clear();
        foreach (var card in CardsFor(Mode, SelectedCategory))
        {
            Cards.Add(card);
        }
    }

    [RelayCommand]
    private void SelectCategory(SuggestionCategoryViewModel category)
    {
        if (string.Equals(SelectedCategory, category.Key, StringComparison.Ordinal))
        {
            return;
        }

        SelectedCategory = category.Key;
        RebuildCategories();
        RebuildCards();
        Composer.SetPreview(null);
    }

    /// <summary>「全部」= 当前场景那一套；其余六个类别各自带四张模板。</summary>
    private static SuggestionCardViewModel[] CardsFor(string mode, string category) => category switch
    {
        "documents" => DocumentCards(),
        "data" => DataCards(),
        "slides" => SlideCards(),
        "research" => ResearchCards(),
        "files" => FileCards(),
        _ => SceneCards(mode),
    };

    private static SuggestionCardViewModel[] SceneCards(string mode) => mode switch
    {
        WorkModeText.Development =>
        [
            new("读一遍项目结构", "先讲清模块划分和入口",
                "帮我读一遍当前项目，讲清模块划分、入口在哪、一次请求怎么流转。", ArcIcons.Command),
            new("定位一个报错", "从堆栈找到真正出错的那一行",
                "我贴一段报错，帮我从堆栈里找到真正出错的位置，并说明为什么会这样。", ArcIcons.Search),
            new("补一组单元测试", "先覆盖边界，再谈覆盖率",
                "帮我看下这个项目里最该补测试的地方，先补一组边界用例。", ArcIcons.Check),
            new("评估一次改动的影响", "哪些文件会被牵连",
                "我打算改一处逻辑，帮我评估会影响哪些文件、哪些行为需要回归验证。", ArcIcons.Pencil),
        ],
        WorkModeText.Design =>
        [
            new("拆一个页面的信息层级", "先定主次，再谈样式",
                "帮我拆解当前页面的信息层级：主次是什么、哪些元素在抢注意力。", ArcIcons.Sparkles),
            new("给一套配色方案", "主色、辅助色和禁用态",
                "基于现在的界面给一套配色：主色、辅助色、悬停和禁用态，说明各自用在哪。", ArcIcons.Eye),
            new("改一版文案", "同一句话的三种语气",
                "把这段文案改三版：克制、口语、专业，分别说明适合什么场景。", ArcIcons.FileText),
            new("对齐一次视觉规范", "间距、字号、圆角",
                "帮我整理一套视觉规范：间距梯度、字号阶梯、圆角和阴影，给具体数值。", ArcIcons.Monitor),
        ],
        _ =>
        [
            new("整理这个文件夹", "先看清结构，再决定归档",
                "帮我梳理一下工作区里的文件结构，告诉我哪些可以归档、哪些该删。", ArcIcons.FolderOpen),
            new("写一份项目周报", "把零散进展收成三段",
                "帮我写一份这周的项目周报，语气务实一点，不要空话。", ArcIcons.FileText),
            new("总结一篇文档", "长文压成要点和待办",
                "帮我总结工作区里最重要的一份文档，给出要点和接下来要做的事。", ArcIcons.Sparkles),
            new("看看本机配置", "系统、CPU、内存、磁盘一次看清",
                "帮我看下这台电脑的配置，整理成一份 Markdown 放到工作区。", ArcIcons.Monitor),
        ],
    };

    private static SuggestionCardViewModel[] DocumentCards() =>
    [
        new("整理一份会议纪要", "把零散记录收成要点和待办",
            "帮我把手上的会议记录整理成一份纪要：分议题、结论、待办（标负责人），写成 docx 放到工作区。", ArcIcons.FileText),
        new("写一份项目周报", "把进展收成三段，不写空话",
            "帮我写一份这周的项目周报：进展、风险、下周计划各一段，写成 docx。", ArcIcons.Pencil),
        new("总结一篇文档", "长文压成要点和待办",
            "帮我总结工作区里最重要的一份文档，给出要点和接下来要做的事。", ArcIcons.Sparkles),
        new("把 Markdown 转成 Word", "标题、列表、表格落成样式",
            "把工作区里这份 Markdown 转成 docx，标题、项目符号和表格要落成真正的样式。", ArcIcons.FileText),
    ];

    private static SuggestionCardViewModel[] DataCards() =>
    [
        new("汇总一份表格", "按维度合计，给一张新表",
            "帮我把工作区里这份表格按指定维度汇总，生成一张带合计行的 xlsx。", ArcIcons.Monitor),
        new("核对两版数据", "找出对不上的行",
            "帮我对比工作区里两份表格，列出对不上的行和具体差异，写成 Markdown。", ArcIcons.Search),
        new("挑出异常值", "标出明显偏离的记录",
            "帮我扫一遍这份数据，标出明显偏离的记录，说明可能的原因。", ArcIcons.Sparkles),
        new("做一份对比表", "把几份材料摆成一张表",
            "把工作区里这几份材料的关键信息汇总成一张对比表，写成 xlsx。", ArcIcons.Check),
    ];

    private static SuggestionCardViewModel[] SlideCards() =>
    [
        new("列一份汇报大纲", "先定结构，再谈内容",
            "帮我为这个主题列一份 10 页左右的汇报大纲，每页给标题和三个要点，写成 docx。", ArcIcons.Pencil),
        new("写逐页讲稿", "照大纲补每页要说的话",
            "照这份大纲给每一页写一段讲稿，口语化、每页控制在 150 字内，写成 docx。", ArcIcons.FileText),
        new("把长文压成要点", "一页只留一句话",
            "把这份长文压缩成汇报要点，一页只留一个结论加两条依据，写成 Markdown。", ArcIcons.Sparkles),
        new("整理成汇报材料", "结论先行，数据跟上",
            "把工作区里的材料整理成一份汇报稿：结论先行、数据跟上、最后给建议，写成 docx。", ArcIcons.Check),
    ];

    private static SuggestionCardViewModel[] ResearchCards() =>
    [
        new("查一份资料并汇总", "抓正文，去重整理",
            "帮我查这个主题的资料，抓回正文后去重整理成一份要点清单，写成 Markdown。", ArcIcons.Search),
        new("对比几个方案", "列差异、优劣和适用",
            "帮我对比这几个方案，列清差异、各自优劣和适用场景，写成一张表。", ArcIcons.Check),
        new("写一份调研报告", "背景、现状、结论三段",
            "帮我写一份这个主题的调研报告：背景、现状、结论和建议，写成 docx。", ArcIcons.FileText),
        new("核对一个说法", "找依据，标不确定处",
            "帮我核对这个说法，给出支持与反对的依据，并标出哪些地方还不确定。", ArcIcons.Sparkles),
    ];

    private static SuggestionCardViewModel[] FileCards() =>
    [
        new("整理这个文件夹", "先看清结构，再决定归档",
            "帮我梳理一下工作区里的文件结构，告诉我哪些可以归档、哪些该删。", ArcIcons.FolderOpen),
        new("找大文件", "按大小列出来",
            "列一下工作区里超过 100MB 的文件，按大小排，标出修改时间。", ArcIcons.Search),
        new("按类型归类", "先给方案，不要直接搬",
            "帮我按类型给工作区里的文件设计一个归档方案，先给目录结构和规则，等我确认再动手。", ArcIcons.Pencil),
        new("看看本机配置", "系统、CPU、内存、磁盘一次看清",
            "帮我看下这台电脑的配置，整理成一份 Markdown 放到工作区。", ArcIcons.Monitor),
    ];

    /// <summary>快捷词只负责填进输入框，所以内容和上面的卡片不重复：一个是直接开跑，一个是先改再发。</summary>
    private static QuickPromptViewModel[] PromptsFor(string mode) => mode switch
    {
        WorkModeText.Development =>
        [
            new("这个项目怎么跑起来", "这个项目怎么跑起来？先看构建配置和入口，再给我一条最短的启动路径。"),
            new("帮我审一下这段代码", "帮我审一下这段代码，重点看边界情况和容易出错的地方。"),
            new("重构成更简单的写法", "把这段逻辑重构成更简单的写法，保持行为不变。"),
        ],
        WorkModeText.Design =>
        [
            new("这个界面哪里显得乱", "这个界面哪里显得乱？从信息层级和间距两个角度说。"),
            new("给我三个标题方向", "给这段内容起三个不同方向的标题，说明各自适合谁看。"),
            new("把这段说明写短一点", "把这段说明写短一点，保留关键信息，去掉套话。"),
        ],
        _ =>
        [
            new("会议要点整理成待办", "把这周的会议要点整理成一份待办清单，标出负责人和时间。"),
            new("把这段话改简洁", "把这段话改得更简洁，意思不变，去掉重复和空话。"),
            new("列一下大文件", "列一下工作区里超过 10MB 的文件，按大小排。"),
        ],
    };

    /// <summary>
    /// 模式写的是全局偏好：点一下影响的是之后所有新任务，不是这一条。
    /// 以前只在下面换一句说明，看不出作用域，用户会以为它只对这次提问生效。
    /// </summary>
    private static string HintFor(string mode) => mode switch
    {
        WorkModeText.Development => "代码开发：先读代码再动手，改完自己跑一遍构建再回话。之后新建的任务默认用它。",
        WorkModeText.Design => "设计创意：先对齐信息层级和规范，给到具体间距、字号和颜色。之后新建的任务默认用它。",
        _ => "日常办公：先给结论再给细节，少用命令行，改文件前说清会改什么。之后新建的任务默认用它。",
    };

    /// <summary>
    /// WorkBuddy 首页的标志性交互：悬停模板，输入框里用灰字预览整段提示词。
    /// 只是预览，不会进草稿、更不会被发出去；点一下才填入。
    /// </summary>
    public void PreviewCard(SuggestionCardViewModel card) => Composer.SetPreview(card.Prompt);

    public void ClearPreview() => Composer.SetPreview(null);

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

    public bool ShowSetupBanner => !IsConfigured && !Composer.HasSetupHint;

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
        if (string.Equals(Mode, mode, StringComparison.Ordinal))
        {
            return;
        }

        Mode = mode;
        ApplyMode(mode);
        _host.Settings.Mode = mode;
        await _host.SettingsProvider.SaveAsync(_host.Settings);
    }

    /// <summary>
    /// 卡片正文：只把提示词填进输入框，和人自己打的一样。
    /// 以前整张卡是「点了就跑」，想先把「下载目录」改成自己的路径都做不到，
    /// 而且右上角画的还是 ↗（通用语义是打开），与实际行为正好相反。
    /// </summary>
    [RelayCommand]
    private void FillCard(SuggestionCardViewModel card)
    {
        Composer.SetPreview(null);
        Composer.Text = card.Prompt;
        Composer.RequestFocus();
    }

    /// <summary>卡片右上角那颗箭头：明确地「直接开始」，会新建任务并消耗一次请求。</summary>
    [RelayCommand]
    private async Task RunCardAsync(SuggestionCardViewModel card)
        => await _host.StartNewChatAsync(card.Prompt);

    [RelayCommand]
    private void UseQuickPrompt(QuickPromptViewModel prompt)
    {
        Composer.SetPreview(null);
        Composer.Text = prompt.Prompt;
        Composer.RequestFocus();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync() => await _host.OpenSettingsAsync();

    public void Refresh()
    {
        // 首页 VM 在设置加载前就构造好了，那时读到的是默认值 office。
        // 设置读盘 / 改动之后必须把模式同步回来，否则重启后永远停在办公。
        var saved = NormalizeMode(_host.Settings.Mode);
        if (!string.Equals(Mode, saved, StringComparison.Ordinal))
        {
            Mode = saved;
            ApplyMode(saved);
        }

        OnPropertyChanged(nameof(Greeting));
        OnPropertyChanged(nameof(IsConfigured));
        OnPropertyChanged(nameof(ShowSetupBanner));
        Composer.Refresh();
    }

    private static string NormalizeMode(string? mode) => mode switch
    {
        WorkModeText.Development => WorkModeText.Development,
        WorkModeText.Design => WorkModeText.Design,
        _ => WorkModeText.Office,
    };
}
