using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;

namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 本地定时任务调度器：每 30 秒检查一次，到点就以当前登录身份发起一条新任务，
/// 并把每次触发写进运行记录。只在应用开着的时候跑（对应 WorkBuddy 的「本地」来源），
/// 不做开机自启、不做云端沙箱——那需要系统级注册，不该偷偷替用户装上。
/// </summary>
public sealed class ScheduledTaskRunner
{
    private readonly JsonWorkbenchStore _store;
    private readonly Func<ScheduledTask, Task<string?>> _runTask;
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastCheck;
    private bool _checking;

    public ScheduledTaskRunner(JsonWorkbenchStore store, Func<ScheduledTask, Task<string?>> runTask)
    {
        _store = store;
        _runTask = runTask;
        _lastCheck = DateTimeOffset.Now;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _timer.Tick += (_, _) => _ = CheckAsync();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>开发用：立刻跑一次检查，不用等 30 秒。</summary>
    public Task CheckNowAsync() => CheckAsync();

    /// <summary>开发用：把检查窗口的起点往前拨，让「刚刚到点」的任务能被这次检查命中。</summary>
    public void PrimeWindow(DateTimeOffset from) => _lastCheck = from;

    private async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }

        _checking = true;
        try
        {
            var now = DateTimeOffset.Now;
            var from = _lastCheck;

            var due = _store.Data.ScheduledTasks
                .Where(task => task.Enabled
                               && ScheduleMath.IsDueInWindow(task, from, now)
                               && (task.LastRunAt is null || (now - task.LastRunAt.Value).TotalMinutes >= 1))
                .ToList();

            foreach (var task in due)
            {
                await FireAsync(task, now, autoDisableOnce: true);
            }

            _lastCheck = now;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>开发 / 手动用：立刻试跑一次（「先试跑一次，再放开频率」）。</summary>
    public async Task<ScheduledRunRecord> TriggerAsync(ScheduledTask task)
        => await FireAsync(task, DateTimeOffset.Now, autoDisableOnce: false);

    private async Task<ScheduledRunRecord> FireAsync(ScheduledTask task, DateTimeOffset now, bool autoDisableOnce)
    {
        task.LastRunAt = now;

        var record = new ScheduledRunRecord
        {
            TaskId = task.Id,
            TaskName = task.Name,
            StartedAt = now,
        };

        try
        {
            var sessionId = await _runTask(task);
            record.SessionId = sessionId;
            record.Status = "已触发";
            record.Detail = string.IsNullOrWhiteSpace(sessionId)
                ? "已发起任务"
                : "已新建任务并开始执行";
            task.LastResult = "已触发";
            task.LastSessionId = sessionId;
        }
        catch (Exception ex)
        {
            record.Status = "失败";
            record.Detail = ex.Message;
            task.LastResult = "失败：" + ex.Message;
        }

        _store.Data.RunRecords.Insert(0, record);
        if (_store.Data.RunRecords.Count > 200)
        {
            _store.Data.RunRecords.RemoveRange(200, _store.Data.RunRecords.Count - 200);
        }

        // 「写入结果文件」是本地助理的推送渠道：和企业微信无关，但结果确实落到了工作区里。
        if (task.NotifyChannel == TaskNotificationText.File)
        {
            record.Detail = WriteResultFile(task, record);
        }

        // 单次任务跑完就停用，别第二天又来一遍；手动试跑不算「执行过」
        if (autoDisableOnce && task.Frequency == ScheduleFrequency.Once)
        {
            task.Enabled = false;
        }

        await _store.SaveAsync();
        RunsChanged?.Invoke(this, EventArgs.Empty);
        return record;
    }

    private string WriteResultFile(ScheduledTask task, ScheduledRunRecord record)
    {
        try
        {
            var config = TaskRunConfig.From(task, _store.Data);
            var root = string.IsNullOrWhiteSpace(config.WorkspacePath)
                ? PlainWorkspace.Root
                : config.WorkspacePath!;
            Directory.CreateDirectory(root);

            var file = Path.Combine(root, "定时任务结果.md");
            var header = File.Exists(file)
                ? string.Empty
                : "# 定时任务结果\n\n每次触发追加一行，任务本身可以在侧栏 / 运行记录里打开。\n\n";
            var line = $"- {record.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {task.Name}"
                       + $" · {record.Status} · 会话 {record.SessionId ?? "—"}\n";
            File.AppendAllText(file, header + line);
            return $"已触发，结果已写入 {file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"已触发，但结果文件没写成：{ex.Message}";
        }
    }

    /// <summary>运行记录变了：工作台页据此刷新列表（此时用户可能正开着这一页）。</summary>
    public event EventHandler? RunsChanged;

}
