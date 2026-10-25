using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace NetPanel.Services;

/// <summary>
/// 内存优化：与 PCL2 百宝箱的实现一致 —— 提权 SeProfileSingleProcessPrivilege，
/// 然后依次执行 NtSetSystemInformation(SystemMemoryListInformation, 2/3/4)：
///   2 = MemoryEmptyWorkingSets   清空所有进程工作集
///   3 = MemoryFlushModifiedList  把已修改页刷回磁盘
///   4 = MemoryPurgeStandbyList   清空待机列表
/// 不结束任何进程，只请求系统把闲置物理页腾出来。
/// </summary>
public static class MemOptimize
{
    private const int SystemMemoryListInformation = 80;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PrivilegeToken
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int NtSetSystemInformation(int SystemInformationClass, IntPtr SystemInformation, int SystemInformationLength);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref PrivilegeToken NewState, int BufferLength, ref IntPtr PreviousState, ref int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys;
        public ulong ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    public static ulong AvailPhys()
    {
        var m = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(m) ? m.ullAvailPhys : 0;
    }

    public static ulong TotalPhys()
    {
        var m = new MEMORYSTATUSEX();
        return GlobalMemoryStatusEx(m) ? m.ullTotalPhys : 0;
    }

    public sealed class Result
    {
        public ulong AvailBefore, AvailAfter;
        public bool PrivilegeOk;
        public int Steps;
        public string Error = "";
        public double FreedMB => AvailAfter > AvailBefore ? (AvailAfter - AvailBefore) / 1048576.0 : 0;
        public double BeforeGB => AvailBefore / 1073741824.0;
        public double AfterGB => AvailAfter / 1073741824.0;
    }

    private static bool EnablePrivilege(string name, out int lastError)
    {
        lastError = 0;
        using var id = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
        var st = new PrivilegeToken { PrivilegeCount = 1, Luid = 0, Attributes = 2 /* SE_PRIVILEGE_ENABLED */ };
        if (!LookupPrivilegeValue(null, name, out long luid)) { lastError = Marshal.GetLastWin32Error(); return false; }
        st.Luid = luid;
        IntPtr prev = IntPtr.Zero; int ret = 0;
        if (!AdjustTokenPrivileges(id.Token, false, ref st, Marshal.SizeOf(st), ref prev, ref ret)) { lastError = Marshal.GetLastWin32Error(); return false; }
        lastError = Marshal.GetLastWin32Error();
        return lastError == 0;   // ERROR_NOT_ALL_ASSIGNED(1300) 表示没拿到权限
    }

    /// <summary>同步执行，调用方放到后台线程。</summary>
    public static Result Run(Action<string> log = null)
    {
        var r = new Result();
        try
        {
            r.AvailBefore = AvailPhys();

            if (!EnablePrivilege("SeProfileSingleProcessPrivilege", out int err))
            {
                r.Error = err == 0 ? "提权失败" : $"获取内存优化权限失败（错误码 {err}）";
                r.AvailAfter = r.AvailBefore;
                return r;
            }
            r.PrivilegeOk = true;

            // 与 PCL2 完全相同的三条命令，顺序也一样
            for (int cmd = 2; cmd <= 4; cmd++)
            {
                IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(buf, cmd);
                    int rc = NtSetSystemInformation(SystemMemoryListInformation, buf, sizeof(int));
                    if (rc != 0)
                    {
                        log?.Invoke($"内存优化命令 {cmd} 失败，NTSTATUS=0x{rc:X8}");
                        continue;
                    }
                    r.Steps++;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }

            System.Threading.Thread.Sleep(300);
            r.AvailAfter = AvailPhys();
            if (r.Steps == 0 && r.Error.Length == 0) r.Error = "三条命令都被系统拒绝（可能不是管理员）";
        }
        catch (Exception ex) { r.Error = ex.Message; r.AvailAfter = AvailPhys(); }
        return r;
    }
}
