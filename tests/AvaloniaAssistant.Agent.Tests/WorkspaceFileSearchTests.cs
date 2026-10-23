using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// @ 背后的文件搜索：能翻到子目录、跳过依赖目录、按"像不像"排序。
/// 这几条决定了用户打两个字能不能一眼看到想要的文件。
/// </summary>
public sealed class WorkspaceFileSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aa-filesearch-" + Guid.NewGuid().ToString("n")[..8]);

    public WorkspaceFileSearchTests()
    {
        Write("src/AvaloniaAssistant.UI/Views/LibraryView.axaml", "<UserControl />");
        Write("src/AvaloniaAssistant.UI/ViewModels/LibraryItems.cs", "class LibraryItems {}");
        Write("src/AvaloniaAssistant.Storage/LibraryStore.cs", "class LibraryStore {}");
        Write("docs/images/library.png", "png");
        Write("README.md", "# readme");
        Write("node_modules/pkg/LibraryHelper.js", "module.exports = {}");
        Write(".git/config", "[core]");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不算测试失败
        }
    }

    [Fact]
    public void SearchFindsFilesInNestedDirectories()
    {
        var results = new WorkspaceFileSearch().Search(_root, "library", limit: 5);

        Assert.Equal("docs/images/library.png", results[0].RelativePath);
        Assert.Contains(results, entry => entry.Name == "LibraryView.axaml");
        Assert.Contains(results, entry => entry.Name == "LibraryStore.cs");
    }

    [Fact]
    public void DependencyAndDotDirectoriesAreSkipped()
    {
        var files = new WorkspaceFileSearch().List(_root);

        Assert.DoesNotContain(files, entry => entry.RelativePath.Contains("node_modules"));
        Assert.DoesNotContain(files, entry => entry.RelativePath.StartsWith(".git"));
        Assert.Contains(files, entry => entry.RelativePath == "README.md");
    }

    [Fact]
    public void EmptyQueryPrefersTheMostRecentlyTouchedFile()
    {
        var newest = Path.Combine(_root, "src", "AvaloniaAssistant.Storage", "LibraryStore.cs");
        File.SetLastWriteTime(newest, DateTime.Now.AddMinutes(5));

        var results = new WorkspaceFileSearch().Search(_root, string.Empty, limit: 3);

        Assert.Equal("src/AvaloniaAssistant.Storage/LibraryStore.cs", results[0].RelativePath);
    }

    [Fact]
    public void NamePrefixBeatsPathSubstring()
    {
        var search = new WorkspaceFileSearch();
        var results = search.Search(_root, "readme");

        Assert.Equal("README.md", results[0].RelativePath);
    }

    [Fact]
    public void ScoreRanksExactThenPrefixThenContains()
    {
        Assert.Equal(0, WorkspaceFileSearch.Score("src/app.ts", "app.ts"));
        Assert.Equal(1, WorkspaceFileSearch.Score("src/apple.ts", "app"));

        // 名字里出现得越靠后越不像，得分跟着涨
        Assert.Equal(12, WorkspaceFileSearch.Score("src/myapples.ts", "app"));

        // 只在目录名里出现时排在名字命中之后
        Assert.True(WorkspaceFileSearch.Score("src/deep/folder/x.txt", "deep") > 30);
        Assert.Equal(int.MaxValue, WorkspaceFileSearch.Score("src/app.ts", "zzz"));
    }

    [Fact]
    public void SubsequenceMatchStillFindsTheFile()
    {
        // lvw → LibraryView.axaml，字符按顺序出现就算命中
        var results = new WorkspaceFileSearch().Search(_root, "lvw", limit: 3);

        Assert.Contains(results, entry => entry.Name == "LibraryView.axaml");
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
