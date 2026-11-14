namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 定时任务（或任何一次单独运行）带「本轮专用模型 / 权限」时，用它从全局设置派生一份副本。
/// 纯函数、可单测；绝不改动传入的全局对象——否则一轮定时任务跑完，用户当前选中的模型和
/// 权限就被悄悄换掉了。
/// </summary>
public static class RunSettingsOverride
{
    public static AssistantSettings Apply(
        AssistantSettings settings,
        string? modelProfileId,
        PermissionMode permission)
    {
        var needsModel = !string.IsNullOrWhiteSpace(modelProfileId)
                         && !string.Equals(modelProfileId, settings.ActiveModelId, StringComparison.Ordinal);
        if (!needsModel && permission == settings.Permission)
        {
            return settings;
        }

        var clone = settings.Clone();
        if (needsModel && !clone.ActivateModel(modelProfileId))
        {
            // 配置被删了就退回原来的模型，别让这一轮直接报「模型不存在」
            clone.ActivateModel(settings.ActiveModelId);
        }

        clone.Permission = permission;
        return clone;
    }
}
