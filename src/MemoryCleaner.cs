using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MemReclaim
{
    /// <summary>单个清理项的执行结果。</summary>
    internal class CleanResult
    {
        public string Item;
        public bool Executed;     // 是否真的发起了调用（被配置禁用时为 false）
        public bool Success;
        public string Message;
        public long BytesFreed;   // 估算释放量（仅待机列表可估算）

        public override string ToString()
        {
            if (!Executed) return Item + "：已跳过（" + Message + "）";
            return Item + "：" + (Success ? "成功" : "失败") + " — " + Message +
                   (BytesFreed > 0 ? "，释放约 " + MemoryState.FormatBytes(BytesFreed) : "");
        }
    }

    /// <summary>
    /// 清理动作实现。
    ///
    /// 与 Mem Reduct 的对应关系（已核对源码 main.c）：
    ///   Standby list                  → NtSetSystemInformation(0x50, MemoryPurgeStandbyList=4)
    ///   Standby list (without priority)→ NtSetSystemInformation(0x50, MemoryPurgeLowPriorityStandbyList=5)
    ///   System file cache             → NtSetSystemInformation(0x51, SYSTEM_FILECACHE_INFORMATION{Min=Max=-1})
    ///   Combine memory lists          → NtSetSystemInformation(0x82, MEMORY_COMBINE_INFORMATION_EX 全零)
    ///
    /// 前两者需要 SeProfileSingleProcessPrivilege；
    /// 文件缓存需要 SeIncreaseQuotaPrivilege（Mem Reduct 的日志字符串写的是
    /// SystemFileCacheInformation，但真实信息类是 SystemFileCacheInformationEx）。
    /// </summary>
    internal static class MemoryCleaner
    {
        /// <summary>清空待机列表全部优先级（含被缓存的文件数据，清理后首次读盘会变慢）。</summary>
        public static CleanResult PurgeStandbyList()
        {
            return SetMemoryListCommand("待机列表", Native.MemoryPurgeStandbyList);
        }

        /// <summary>仅清 priority 0 的待机列表，代价比全清低。</summary>
        public static CleanResult PurgeLowPriorityStandbyList()
        {
            return SetMemoryListCommand("待机列表（无优先级）", Native.MemoryPurgeLowPriorityStandbyList);
        }

        private static CleanResult SetMemoryListCommand(string label, int command)
        {
            CleanResult r = new CleanResult();
            r.Item = label;

            MemoryState before = MemoryStateReader.Read();

            IntPtr buf = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(buf, command);
                int status = Native.NtSetSystemInformation(
                    Native.SystemMemoryListInformation, buf, sizeof(int));

                r.Success = status == 0;
                r.Executed = true;
                r.Message = Native.StatusToText(status);

                if (r.Success && before.Valid)
                {
                    MemoryState after = MemoryStateReader.Read();
                    if (after.Valid)
                    {
                        long freed = before.StandbyBytesTotal - after.StandbyBytesTotal;
                        if (freed > 0) r.BytesFreed = freed;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            return r;
        }

        /// <summary>
        /// 清空系统文件缓存。Mem Reduct 的做法：结构体清零后
        /// 仅把 MinimumWorkingSet / MaximumWorkingSet 置为 MAXSIZE_T。
        /// </summary>
        public static CleanResult ClearSystemFileCache()
        {
            CleanResult r = new CleanResult();
            r.Item = "系统文件缓存";
            r.Executed = true;

            int size = Native.SizeOfFileCacheInformation;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int i;
                for (i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);

                // -1 即 MAXSIZE_T，表示不设上限，促使系统回收文件缓存
                Marshal.WriteInt64(buf, Native.OffsetFileCacheMinWorkingSet, -1);
                Marshal.WriteInt64(buf, Native.OffsetFileCacheMinWorkingSet + IntPtr.Size, -1);

                int status = Native.NtSetSystemInformation(
                    Native.SystemFileCacheInformationEx, buf, size);

                r.Success = status == 0;
                r.Message = Native.StatusToText(status);
            }
            finally { Marshal.FreeHGlobal(buf); }

            return r;
        }

        /// <summary>
        /// 合并内存列表（Win10+）。传全零的 MEMORY_COMBINE_INFORMATION_EX，
        /// 内核回填 PagesCombined。需要 SeProfileSingleProcessPrivilege。
        /// 返回 STATUS_SUCCESS 且页数为 0 表示当时没有可合并的页面，不算失败。
        /// </summary>
        public static CleanResult CombineMemoryLists()
        {
            CleanResult r = new CleanResult();
            r.Item = "合并内存列表";
            r.Executed = true;

            int size = Native.SizeOfCombineInformationEx;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int i;
                for (i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);

                int status = Native.NtSetSystemInformation(
                    Native.SystemCombinePhysicalMemoryInformation, buf, size);

                r.Success = status == 0;

                if (r.Success)
                {
                    long pages = Marshal.ReadIntPtr(buf, IntPtr.Size).ToInt64();
                    r.BytesFreed = pages * Native.PageSize;
                    r.Message = pages > 0
                        ? "已合并 " + pages + " 页"
                        : "无重复页可合并（正常）";
                }
                else
                {
                    r.Message = Native.StatusToText(status);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }

            return r;
        }
    }
}
