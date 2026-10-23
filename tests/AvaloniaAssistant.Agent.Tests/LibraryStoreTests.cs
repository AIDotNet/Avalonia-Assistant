using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 资料库的落盘行为：复制进来、改名去重、抽正文、删得干净。
/// 这些都在 Storage 层，跑测试不需要开窗口。
/// </summary>
public sealed class LibraryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aa-library-" + Guid.NewGuid().ToString("n")[..8]);

    private readonly LibraryStore _store = new();

    public LibraryStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不算测试失败
        }
    }

    [Fact]
    public async Task ImportCopiesFileAndIndexesItsText()
    {
        var source = WriteSource("notes.md", "# 周报要点\n第一行正文");

        var result = await _store.ImportAsync(_root, [source]);

        Assert.Single(result.Imported);
        Assert.Empty(result.Errors);
        Assert.True(File.Exists(LibraryStore.IndexFileFor(_root)));
        Assert.True(File.Exists(Path.Combine(LibraryStore.FilesFor(_root), result.Imported[0].FileName)));

        var loaded = await _store.LoadAsync(_root);
        var document = Assert.Single(loaded);
        Assert.Equal("notes.md", document.Name);
        Assert.Equal("MD", document.Kind);
        Assert.Contains("周报要点", document.Text);
        Assert.Equal("周报要点", document.Summary);
    }

    [Fact]
    public async Task SameNameAndSizeIsSkippedInsteadOfDuplicated()
    {
        var source = WriteSource("notes.md", "同样内容");
        await _store.ImportAsync(_root, [source]);

        var second = await _store.ImportAsync(_root, [source]);

        Assert.Empty(second.Imported);
        Assert.Single(second.Skipped);
        Assert.Single(await _store.LoadAsync(_root));
    }

    [Fact]
    public async Task SameNameWithDifferentSizeGetsASuffix()
    {
        var first = WriteSource("report.md", "短的");
        await _store.ImportAsync(_root, [first]);

        var second = WriteSource("report.md", "长一点点的一份报告");
        var result = await _store.ImportAsync(_root, [second]);

        var document = Assert.Single(result.Imported);
        Assert.Equal("report (2).md", document.Name);
        Assert.Equal(2, (await _store.LoadAsync(_root)).Count);
    }

    [Fact]
    public async Task RemoveDropsTheIndexEntryAndTheStoredCopy()
    {
        var source = WriteSource("draft.txt", "草稿");
        var imported = (await _store.ImportAsync(_root, [source])).Imported[0];
        var stored = Path.Combine(LibraryStore.FilesFor(_root), imported.FileName);

        Assert.True(await _store.RemoveAsync(_root, imported.Id));

        Assert.Empty(await _store.LoadAsync(_root));
        Assert.False(File.Exists(stored));

        // 原文件不能跟着没，资料库只是复制了一份
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task SearchHitsBodyNotOnlyName()
    {
        await _store.ImportAsync(_root, [WriteSource("notes.md", "开头\n这里提到 NebulaGraph 的图查询")]);
        await _store.ImportAsync(_root, [WriteSource("other.md", "别的东西")]);

        var documents = await _store.LoadAsync(_root);
        var hits = documents.Where(document => LibraryStore.Matches(document, "nebulagraph")).ToList();

        var hit = Assert.Single(hits);
        Assert.Equal("notes.md", hit.Name);
    }

    [Fact]
    public async Task BinaryFileIsListedWithoutText()
    {
        var source = Path.Combine(_root, "picture.png");
        await File.WriteAllBytesAsync(source, [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF]);

        var result = await _store.ImportAsync(_root, [source]);

        var document = Assert.Single(result.Imported);
        Assert.Equal("PNG", document.Kind);
        Assert.False(document.HasText);
        Assert.NotNull(document.Summary);

        // 名字还能搜到，正文没有也不该报错
        Assert.True(LibraryStore.Matches(document, "picture"));
        Assert.False(LibraryStore.Matches(document, "nebula"));
    }

    [Fact]
    public async Task MissingFileIsReportedInsteadOfThrown()
    {
        var result = await _store.ImportAsync(_root, [Path.Combine(_root, "nope.md")]);

        Assert.Empty(result.Imported);
        Assert.Single(result.Errors);

        var empty = await _store.ImportAsync(_root, []);
        Assert.Empty(empty.Imported);
        Assert.Empty(empty.Errors);
    }

    private string WriteSource(string name, string content)
    {
        // 源文件放在工作区的子目录里，别和 .library 混在一起
        var directory = Path.Combine(_root, "sources");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
