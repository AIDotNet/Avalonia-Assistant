using System.IO.Compression;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>技能导入：文件夹和 zip 两条路都要落到技能目录，并且只在合规时落。</summary>
public sealed class CapabilityInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-installer-" + Guid.NewGuid().ToString("n"));

    public CapabilityInstallerTests() => Directory.CreateDirectory(_root);

    private string SkillsRoot => Path.Combine(_root, "skills");

    [Fact]
    public void InstallsFolderWithNestedFiles()
    {
        var source = CreateSkill(Path.Combine(_root, "from-folder", "daily-report"), "# 日报\n按模板写。");
        File.WriteAllText(Path.Combine(source, "template.md"), "模板正文");

        var target = CapabilityInstaller.InstallSkill(source, SkillsRoot);

        Assert.Equal(Path.Combine(SkillsRoot, "daily-report"), target);
        Assert.True(File.Exists(Path.Combine(target, "SKILL.md")));
        Assert.Equal("模板正文", File.ReadAllText(Path.Combine(target, "template.md")));
    }

    [Fact]
    public void InstallsZipWithWrapperFolder()
    {
        CreateSkill(Path.Combine(_root, "pack", "code-review"), "# 代码审查\n先看 diff。", string.Empty);
        var zip = ZipDirectory(Path.Combine(_root, "pack"), Path.Combine(_root, "code-review.zip"));

        var target = CapabilityInstaller.InstallSkillZip(zip, SkillsRoot);

        Assert.Equal(Path.Combine(SkillsRoot, "code-review"), target);
        Assert.Contains("代码审查", File.ReadAllText(Path.Combine(target, "SKILL.md")));
    }

    [Fact]
    public void InstallsZipWithManifestAtRootUsingZipName()
    {
        var source = CreateSkill(Path.Combine(_root, "flat"), "# 会议纪要\n按三段写。");
        var zip = ZipDirectory(source, Path.Combine(_root, "会议纪要助手.zip"));

        var target = CapabilityInstaller.InstallSkillZip(zip, SkillsRoot);

        Assert.Equal(Path.Combine(SkillsRoot, "会议纪要助手"), target);
        Assert.True(File.Exists(Path.Combine(target, "SKILL.md")));
    }

    [Fact]
    public void RejectsFolderWithoutManifest()
    {
        var source = Path.Combine(_root, "not-a-skill");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "readme.md"), "没有 SKILL.md");

        var error = Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.InstallSkill(source, SkillsRoot));

        Assert.Contains("SKILL.md", error.Message);
        Assert.False(Directory.Exists(SkillsRoot));
    }

    [Fact]
    public void RejectsZipWithoutManifest()
    {
        var source = Path.Combine(_root, "empty-pack");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "notes.txt"), "nothing");
        var zip = ZipDirectory(source, Path.Combine(_root, "empty.zip"));

        var error = Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.InstallSkillZip(zip, SkillsRoot));

        Assert.Contains("SKILL.md", error.Message);
    }

    [Fact]
    public void RejectsZipPackingTwoSkills()
    {
        var bundle = Path.Combine(_root, "bundle");
        CreateSkill(Path.Combine(bundle, "one"), "# 一", "正文");
        CreateSkill(Path.Combine(bundle, "two"), "# 二", "正文");
        var zip = ZipDirectory(bundle, Path.Combine(_root, "bundle.zip"));

        var error = Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.InstallSkillZip(zip, SkillsRoot));

        Assert.Contains("一个技能包", error.Message);
    }

    [Fact]
    public void RejectsInstallingSameNameTwice()
    {
        var source = CreateSkill(Path.Combine(_root, "dup", "writer"), "# 写作", "正文");
        CapabilityInstaller.InstallSkill(source, SkillsRoot);

        var error = Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.InstallSkill(source, SkillsRoot));

        Assert.Contains("已经装过", error.Message);
    }

    [Fact]
    public void RejectsNonZipFile()
    {
        var file = Path.Combine(_root, "skill.rar");
        File.WriteAllText(file, "not a zip");

        var error = Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.InstallSkillZip(file, SkillsRoot));

        Assert.Contains(".zip", error.Message);
    }

    private static string CreateSkill(string directory, string title, string body = "")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {new DirectoryInfo(directory).Name}\ndescription: 测试技能\n---\n\n{title}\n\n{body}\n");
        return directory;
    }

    private static string ZipDirectory(string source, string zipPath)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        ZipFile.CreateFromDirectory(source, zipPath);
        return zipPath;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }
}
