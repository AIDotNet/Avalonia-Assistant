using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 产物采集：面板里那一行「改了哪个文件、加减多少行」全是它算的。
/// 算错了比不显示更糟——用户会照着假 diff 做判断。
/// </summary>
public sealed class ArtifactTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-artifacts-" + Guid.NewGuid().ToString("n"));

    public ArtifactTrackerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言
        }
    }

    private string PathOf(string name) => Path.Combine(_root, name);

    private static string Args(string path) => "{\"path\":\"" + path.Replace("\\", "\\\\") + "\"}";

    [Fact]
    public void RecordsCreatedFileWithAddedLines()
    {
        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c1", "write_file", Args("清单.md"));

        File.WriteAllText(PathOf("清单.md"), "第一行\n第二行\n第三行");

        var change = tracker.ObserveFinished("c1", "write_file", false);

        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Created, change!.Kind);
        Assert.Equal(3, change.AddedLines);
        Assert.Equal(0, change.RemovedLines);
        Assert.Equal("清单.md", change.Path);
        Assert.Contains("+第一行", change.Diff);
    }

    [Fact]
    public void RecordsModifiedFileWithBothDirections()
    {
        File.WriteAllText(PathOf("a.txt"), "a\nb\nc");

        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c2", "edit", Args("a.txt"));

        File.WriteAllText(PathOf("a.txt"), "a\nB\nc");

        var change = tracker.ObserveFinished("c2", "edit", false);

        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Modified, change!.Kind);
        Assert.Equal(1, change.AddedLines);
        Assert.Equal(1, change.RemovedLines);
        Assert.Contains("-b", change.Diff);
        Assert.Contains("+B", change.Diff);
    }

    [Fact]
    public void IgnoresRewriteWithSameContent()
    {
        File.WriteAllText(PathOf("same.txt"), "内容没变");

        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c3", "write_file", Args("same.txt"));

        // 模型又「写」了一次，内容一模一样
        File.WriteAllText(PathOf("same.txt"), "内容没变");

        Assert.Null(tracker.ObserveFinished("c3", "write_file", false));
    }

    [Fact]
    public void RecordsDeletion()
    {
        File.WriteAllText(PathOf("gone.txt"), "1\n2");

        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c4", "write_file", Args("gone.txt"));
        File.Delete(PathOf("gone.txt"));

        var change = tracker.ObserveFinished("c4", "write_file", false);

        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Deleted, change!.Kind);
    }

    [Fact]
    public void SkipsToolThatFailed()
    {
        File.WriteAllText(PathOf("half.txt"), "写了一半");

        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c5", "write_file", Args("half.txt"));

        // 工具报错时文件可能只写了一半，这时候给 diff 是误导
        Assert.Null(tracker.ObserveFinished("c5", "write_file", true));
    }

    [Fact]
    public void IgnoresReadOnlyTools()
    {
        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c6", "read", Args("a.txt"));

        Assert.Null(tracker.ObserveFinished("c6", "read", false));
    }

    [Fact]
    public void IgnoresUnknownCallId()
    {
        var tracker = new ArtifactTracker(_root);

        Assert.Null(tracker.ObserveFinished("never-started", "write_file", false));
    }

    [Fact]
    public void KeepsAbsolutePathOutsideWorkspace()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ava-outside-" + Guid.NewGuid().ToString("n") + ".txt");
        File.WriteAllText(outside, "外部文件");

        try
        {
            var tracker = new ArtifactTracker(_root);
            tracker.ObserveStarted("c7", "write_file", Args(outside));
            File.WriteAllText(outside, "外部文件改了");

            var change = tracker.ObserveFinished("c7", "write_file", false);

            Assert.NotNull(change);

            // 工作区外的文件显示完整路径：相对路径在这里没有意义
            Assert.Equal(outside, change!.Path);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void ResetDropsPendingSnapshots()
    {
        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c8", "write_file", Args("x.txt"));
        tracker.Reset();

        File.WriteAllText(PathOf("x.txt"), "内容");

        Assert.Null(tracker.ObserveFinished("c8", "write_file", false));
    }

    [Fact]
    public void FollowsWorkspaceRebase()
    {
        var sub = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(sub);

        // 新任务开口前换了工作区：解析相对路径的根必须跟着换
        var tracker = new ArtifactTracker(null);
        tracker.Rebase(sub);

        tracker.ObserveStarted("c10", "write_file", Args("a.md"));
        File.WriteAllText(Path.Combine(sub, "a.md"), "内容");

        var change = tracker.ObserveFinished("c10", "write_file", false);

        Assert.NotNull(change);
        Assert.Equal("a.md", change!.Path);
        Assert.Equal(Path.Combine(sub, "a.md"), change.FullPath);
    }

    [Fact]
    public void RecordsDocumentToolOutput()
    {
        // write_document 也要进产物面板，否则生成 docx 这件事在界面上看不见
        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c11", "write_document", Args("周报.docx"));

        File.WriteAllBytes(PathOf("周报.docx"), [0x50, 0x4B, 0x03, 0x04]);

        var change = tracker.ObserveFinished("c11", "write_document", false);

        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Created, change!.Kind);
        Assert.Equal("write_document", change.Tool);
    }

    [Fact]
    public void SkipsBinaryExtension()
    {
        var tracker = new ArtifactTracker(_root);
        tracker.ObserveStarted("c9", "write_file", Args("img.png"));
        File.WriteAllBytes(PathOf("img.png"), [0x89, 0x50, 0x4E, 0x47]);

        var change = tracker.ObserveFinished("c9", "write_file", false);

        // 记一条「改过」，但不给 diff：二进制读出来全是乱码
        Assert.NotNull(change);
        Assert.Equal(FileChangeKind.Created, change!.Kind);
        Assert.Null(change.Diff);
        Assert.Equal(0, change.AddedLines);
    }
}
