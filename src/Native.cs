using System;
using System.Runtime.InteropServices;

namespace MemReclaim
{
    /// <summary>
    /// Windows 原生内存管理接口封装。
    ///
    /// 信息类数值来源（已交叉核实）：
    ///   - Geoff Chappell, SYSTEM_INFORMATION_CLASS / ZwSetSystemInformation
    ///   - phnt (System Informer) ntexapi.h
    ///   - Mem Reduct src/main.c 实际调用
    ///
    /// 结构体布局与 Mem Reduct / phnt 一致，并按进程位宽动态计算，避免 x86/x64 混用出错。
    /// </summary>
    internal static class Native
    {
        // ---------- SYSTEM_INFORMATION_CLASS ----------
        public const int SystemFileCacheInformation = 0x15;              // 21
        public const int SystemFileCacheInformationEx = 0x51;            // 81  Mem Reduct 实际使用
        public const int SystemMemoryListInformation = 0x50;             // 80
        public const int SystemCombinePhysicalMemoryInformation = 0x82;  // 130

        // ---------- SYSTEM_MEMORY_LIST_COMMAND ----------
        public const int MemoryPurgeStandbyList = 4;                     // 清空整个待机列表
        public const int MemoryPurgeLowPriorityStandbyList = 5;           // 仅清 priority 0

        public const int PageSize = 4096;

        // ---------- ntdll ----------
        [DllImport("ntdll.dll", ExactSpelling = true)]
        public static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

        [DllImport("ntdll.dll", ExactSpelling = true)]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int retLength);

        /// <summary>启用调用进程令牌中的特权。BOOLEAN 必须按 1 字节编组，否则参数错位。</summary>
        [DllImport("ntdll.dll", ExactSpelling = true)]
        public static extern int RtlAdjustPrivilege(uint privilege,
            [MarshalAs(UnmanagedType.U1)] bool enable,
            [MarshalAs(UnmanagedType.U1)] bool currentThread,
            out byte wasEnabled);

        // ---------- advapi32 ----------
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;
        public const uint SE_PRIVILEGE_ENABLED = 0x0002;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool LookupPrivilegeValue(string system, string name, out long luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr token,
            [MarshalAs(UnmanagedType.Bool)] bool disableAll, IntPtr newState,
            int bufferLength, IntPtr previousState, IntPtr returnLength);

        // ---------- kernel32 ----------
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetLastError(uint code);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        // ---------- 结构体尺寸/偏移（按位宽动态计算）----------
        private static int Align(int value, int alignment)
        {
            return (value + alignment - 1) / alignment * alignment;
        }

        /// <summary>
        /// SYSTEM_MEMORY_LIST_INFORMATION：
        /// 5 个 SIZE_T + SIZE_T[8] + SIZE_T[8] + 1 个 SIZE_T = 22 * sizeof(SIZE_T)。
        /// x64 = 176 字节（已实测 NtQuerySystemInformation 返回长度正是 176）。
        /// </summary>
        public static int SizeOfMemoryListInformation
        {
            get { return 22 * IntPtr.Size; }
        }

        /// <summary>
        /// SYSTEM_FILECACHE_INFORMATION 中 MinimumWorkingSet 的偏移。
        /// 布局：CurrentSize(p) PeakSize(p) PageFaultCount(4) → 对齐 → MinimumWorkingSet(p)
        /// MaximumWorkingSet(p) CurrentSizeIncluding(p) PeakSizeIncluding(p)
        /// TransitionRePurposeCount(4) Flags(4)。x64 共 64 字节。
        /// </summary>
        public static int OffsetFileCacheMinWorkingSet
        {
            get { int p = IntPtr.Size; return Align(2 * p + 4, p); }
        }

        public static int SizeOfFileCacheInformation
        {
            get { int p = IntPtr.Size; return OffsetFileCacheMinWorkingSet + 4 * p + 8; }
        }

        /// <summary>
        /// MEMORY_COMBINE_INFORMATION_EX：HANDLE + SIZE_T + ULONG（尾部对齐）。
        /// x64 = 24 字节（Mem Reduct 传的正是 sizeof(MEMORY_COMBINE_INFORMATION_EX)）。
        /// </summary>
        public static int SizeOfCombineInformationEx
        {
            get { int p = IntPtr.Size; return Align(2 * p + 4, p); }
        }

        /// <summary>把 NTSTATUS 转成可读文本，便于界面直接展示失败原因。</summary>
        public static string StatusToText(int status)
        {
            switch (status)
            {
                case 0x00000000: return "成功";
                case unchecked((int)0xC0000003): return "STATUS_INVALID_INFO_CLASS 信息类无效";
                case unchecked((int)0xC0000004): return "STATUS_INFO_LENGTH_MISMATCH 结构体长度不符";
                case unchecked((int)0xC000000D): return "STATUS_INVALID_PARAMETER 参数无效";
                case unchecked((int)0xC00000BB): return "STATUS_NOT_SUPPORTED 系统不支持该操作";
                case unchecked((int)0xC0000022): return "STATUS_ACCESS_DENIED 访问被拒绝";
                case unchecked((int)0xC0000061): return "STATUS_PRIVILEGE_NOT_HELD 缺少所需特权";
                case unchecked((int)0xC0000002): return "STATUS_NOT_IMPLEMENTED 未实现";
                default: return "NTSTATUS 0x" + status.ToString("X8");
            }
        }

        public static string Win32ToText(int error)
        {
            switch (error)
            {
                case 0: return "成功";
                case 5: return "ERROR_ACCESS_DENIED 访问被拒绝";
                case 87: return "ERROR_INVALID_PARAMETER 参数无效";
                case 1300: return "ERROR_NOT_ALL_ASSIGNED 令牌中未持有该特权";
                case 1314: return "ERROR_PRIVILEGE_NOT_HELD 缺少所需特权";
                default: return "Win32 错误 " + error;
            }
        }
    }

    /// <summary>
    /// 特权启用。先试 RtlAdjustPrivilege，失败再退回 AdjustTokenPrivileges，
    /// 两条路径都会给出可直接展示的失败原因（这是本机排查的关键，勿省略诊断信息）。
    /// </summary>
    internal static class Privileges
    {
        public const string SeProfileSingleProcess = "SeProfileSingleProcessPrivilege"; // 内存列表操作
        public const string SeIncreaseQuota = "SeIncreaseQuotaPrivilege";               // 文件缓存操作

        public class Result
        {
            public string Name;
            public bool Enabled;
            public string Detail;
        }

        public static Result Enable(string name)
        {
            Result r = new Result();
            r.Name = name;
            r.Enabled = false;

            long luid;
            if (!Native.LookupPrivilegeValue(null, name, out luid))
            {
                r.Detail = "LookupPrivilegeValue 失败：" + Native.Win32ToText(Marshal.GetLastWin32Error());
                return r;
            }

            // 路径 1：RtlAdjustPrivilege（ntdll，最直接）
            uint low = (uint)(luid & 0xFFFFFFFF);
            byte wasEnabled;
            int status = Native.RtlAdjustPrivilege(low, true, false, out wasEnabled);
            if (status == 0)
            {
                r.Enabled = true;
                r.Detail = wasEnabled != 0 ? "原本已启用" : "已启用 (RtlAdjustPrivilege)";
                return r;
            }

            // 路径 2：AdjustTokenPrivileges（回退）
            IntPtr token;
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(),
                Native.TOKEN_ADJUST_PRIVILEGES | Native.TOKEN_QUERY, out token))
            {
                r.Detail = "RtlAdjustPrivilege 失败(" + Native.StatusToText(status) +
                           ")；OpenProcessToken 亦失败：" + Native.Win32ToText(Marshal.GetLastWin32Error());
                return r;
            }

            try
            {
                // 手工构造 TOKEN_PRIVILEGES，避免托管结构体布局差异
                IntPtr buf = Marshal.AllocHGlobal(16);
                try
                {
                    int i;
                    for (i = 0; i < 16; i++) Marshal.WriteByte(buf, i, 0);
                    Marshal.WriteInt32(buf, 0, 1);                                  // PrivilegeCount
                    Marshal.WriteInt32(buf, 4, (int)(luid & 0xFFFFFFFF));            // Luid.LowPart
                    Marshal.WriteInt32(buf, 8, (int)(luid >> 32));                   // Luid.HighPart
                    Marshal.WriteInt32(buf, 12, (int)Native.SE_PRIVILEGE_ENABLED);   // Attributes

                    Native.SetLastError(0);   // 必须清零，否则误读残留错误码
                    bool ok = Native.AdjustTokenPrivileges(token, false, buf, 0, IntPtr.Zero, IntPtr.Zero);
                    int err = Marshal.GetLastWin32Error();

                    if (ok && err == 0)
                    {
                        r.Enabled = true;
                        r.Detail = "已启用 (AdjustTokenPrivileges)";
                    }
                    else
                    {
                        r.Detail = "RtlAdjustPrivilege 失败(" + Native.StatusToText(status) +
                                   ")；AdjustTokenPrivileges 也失败(" + Native.Win32ToText(err) + ")";
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Native.CloseHandle(token); }

            return r;
        }
    }
}
