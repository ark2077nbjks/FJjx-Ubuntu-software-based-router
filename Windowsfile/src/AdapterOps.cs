namespace NetPanel.Services;

/// <summary>启用 / 禁用本机物理网卡（需要管理员权限）</summary>
public static class AdapterOps
{
    public static async Task<(bool Ok, string Message)> SetEnabledAsync(string name, bool enable)
    {
        var verb = enable ? "Enable-NetAdapter" : "Disable-NetAdapter";
        var cmd =
            $"powershell -NoProfile -NonInteractive -Command \"{verb} -Name '{name}' -Confirm:$false -ErrorAction Stop; (Get-NetAdapter -Name '{name}').Status\"";

        var (rc, output) = await Proc.CmdAsync(cmd, 40);
        var text = output.Trim();

        if (rc != 0 || text.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                     || text.Contains("错误", StringComparison.OrdinalIgnoreCase))
        {
            AppPaths.Log($"{(enable ? "启用" : "禁用")}网卡 {name} 失败 rc={rc}: {text}");
            return (false, text);
        }

        AppPaths.Log($"{(enable ? "启用" : "禁用")}网卡 {name} 成功，当前状态 {text}");
        return (true, text);
    }
}
