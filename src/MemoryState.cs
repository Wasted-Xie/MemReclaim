using System;
using System.Runtime.InteropServices;

namespace MemReclaim
{
    /// <summary>一次内存状态快照。</summary>
    internal class MemoryState
    {
        public bool Valid;

        public ulong TotalBytes;      // 物理内存总量
        public ulong AvailableBytes;  // 可用物理内存
        public uint LoadPercent;

        public long ZeroPages;
        public long FreePages;
        public long ModifiedPages;
        public long ModifiedNoWritePages;
        public long BadPages;
        public long ModifiedPageFilePages;

        /// <summary>8 个优先级的待机列表页数。索引即优先级，[0] 为「无优先级」。</summary>
        public long[] StandbyPagesByPriority = new long[8];
        public long[] RepurposedPagesByPriority = new long[8];

        public long StandbyPagesTotal
        {
            get
            {
                long t = 0;
                for (int i = 0; i < 8; i++) t += StandbyPagesByPriority[i];
                return t;
            }
        }

        public long StandbyBytesTotal { get { return StandbyPagesTotal * Native.PageSize; } }

        /// <summary>priority 0（界面上的 “Standby list (without priority)”）。</summary>
        public long LowPriorityStandbyBytes { get { return StandbyPagesByPriority[0] * Native.PageSize; } }

        public long ModifiedBytes { get { return ModifiedPages * Native.PageSize; } }

        public double AvailablePercent
        {
            get { return TotalBytes == 0 ? 0 : AvailableBytes * 100.0 / TotalBytes; }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) bytes = 0;
            double mb = bytes / 1048576.0;
            if (mb >= 1024) return (mb / 1024.0).ToString("F2") + " GB";
            return mb.ToString("F1") + " MB";
        }
    }

    /// <summary>
    /// 读取内存状态。
    ///
    /// 关键实现说明（实测踩坑）：
    /// SYSTEM_MEMORY_LIST_INFORMATION 含两个 SIZE_T[8] 内联数组，
    /// 用 Marshal.PtrToStructure 解析不可靠（数组字段会被错位），
    /// 因此这里用固定偏移逐个读取 QWORD，并已用性能计数器交叉验证过正确性。
    /// </summary>
    internal static class MemoryStateReader
    {
        public static MemoryState Read()
        {
            MemoryState s = new MemoryState();

            Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                s.TotalBytes = ms.ullTotalPhys;
                s.AvailableBytes = ms.ullAvailPhys;
                s.LoadPercent = ms.dwMemoryLoad;
            }

            int size = Native.SizeOfMemoryListInformation;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int i;
                for (i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);

                int retLength;
                int status = Native.NtQuerySystemInformation(
                    Native.SystemMemoryListInformation, buf, size, out retLength);
                if (status != 0)
                {
                    s.Valid = false;
                    return s;
                }

                s.ZeroPages = Marshal.ReadInt64(buf, 0 * 8);
                s.FreePages = Marshal.ReadInt64(buf, 1 * 8);
                s.ModifiedPages = Marshal.ReadInt64(buf, 2 * 8);
                s.ModifiedNoWritePages = Marshal.ReadInt64(buf, 3 * 8);
                s.BadPages = Marshal.ReadInt64(buf, 4 * 8);

                int k;
                for (k = 0; k < 8; k++) s.StandbyPagesByPriority[k] = Marshal.ReadInt64(buf, (5 + k) * 8);
                for (k = 0; k < 8; k++) s.RepurposedPagesByPriority[k] = Marshal.ReadInt64(buf, (13 + k) * 8);

                s.ModifiedPageFilePages = Marshal.ReadInt64(buf, 21 * 8);
                s.Valid = true;
            }
            catch
            {
                s.Valid = false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }

            return s;
        }
    }
}
