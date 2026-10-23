using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task SettingsRoundTripPreservesProviderAndTools()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");

        var provider = new JsonSettingsProvider(file);
        await provider.LoadAsync();
        var settings = provider.Current;
        settings.Provider = AgentProviderKind.OpenAI;
        settings.ApiKey = "sk-test";
        settings.Model = "gpt-4.1";
        settings.Permission = PermissionMode.FullAccess;
        settings.DisabledTools.Add("run_command");
        await provider.SaveAsync(settings);

        var reloaded = new JsonSettingsProvider(file);
        await reloaded.LoadAsync();

        Assert.Equal(AgentProviderKind.OpenAI, reloaded.Current.Provider);
        Assert.Equal("sk-test", reloaded.Current.ApiKey);
        Assert.Equal("gpt-4.1", reloaded.Current.Model);
        Assert.Equal(PermissionMode.FullAccess, reloaded.Current.Permission);
        Assert.Contains("run_command", reloaded.Current.DisabledTools);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task EmptyWorkspaceStaysEmptyAfterReload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");

        var provider = new JsonSettingsProvider(file);
        await provider.LoadAsync();

        // 用户把「空间」里的记录删干净：工作区留空，历史也清掉
        var settings = provider.Current;
        settings.WorkspacePath = string.Empty;
        settings.RecentWorkspaces.Clear();
        await provider.SaveAsync(settings);

        var reloaded = new JsonSettingsProvider(file);
        await reloaded.LoadAsync();

        // 空工作区不能被回落成默认工作区，否则删掉的记录下次启动又冒出来
        Assert.Equal(string.Empty, reloaded.Current.WorkspacePath);
        Assert.Empty(reloaded.Current.RecentWorkspaces);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task SessionStoreRoundTripAndDelete()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        var store = new JsonSessionStore(directory);

        var session = new ChatSession
        {
            Id = "session-1",
            Title = "测试会话",
            Entries =
            [
                new ChatEntry { Kind = ChatEntryKind.User, Text = "你好" },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    Text = "你好，有什么可以帮你的？",
                    ToolCalls =
                    [
                        new ToolCallRecord
                        {
                            Id = "call-1",
                            Name = "current_time",
                            Label = "查看当前时间",
                            Arguments = "{}",
                            Result = "2026-10-23",
                            DurationMs = 12,
                        },
                    ],
                },
            ],
        };

        await store.SaveAsync(session);
        var loaded = await store.LoadAllAsync();

        var item = Assert.Single(loaded);
        Assert.Equal("测试会话", item.Title);
        Assert.Equal(2, item.Entries.Count);
        Assert.Equal("current_time", item.Entries[1].ToolCalls[0].Name);

        await store.DeleteAsync("session-1");
        Assert.Empty(await store.LoadAllAsync());

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
