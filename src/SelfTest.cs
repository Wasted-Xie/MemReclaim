using System;
using System.Text;
using System.Windows.Forms;

namespace MemReclaim
{
    /// <summary>
    /// 自检：逐项调用清理接口并报告本机可用性。
    /// 这是本机排查得出的必要功能——不同机器上安全软件/策略会影响这些接口，
    /// 用户需要能自己看出到底哪一项不可用、原因是什么。
    /// </summary>
    internal static class SelfTest
    {
        /// <summary>是否以管理员身份运行。统一走 ElevationHelper，避免多处判断不一致。</summary>
        public static bool IsElevated()
        {
            return ElevationHelper.IsElevated();
        }

        public static int Run()
        {
            StringBuilder log = new StringBuilder();

            IntegrityLevel level = IntegrityCheck.GetCurrentLevel();
            string exePath = System.Windows.Forms.Application.ExecutablePath;

            log.AppendLine("内存回收 — 环境自检");
            log.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine("系统：" + Environment.OSVersion.Version + "，" +
                           (Environment.Is64BitProcess ? "64" : "32") + " 位进程");
            log.AppendLine("管理员权限：" + (IsElevated() ? "是" : "否"));
            log.AppendLine("完整性级别：" + IntegrityCheck.LevelName(level));
            log.AppendLine("程序位置：" + exePath);
            log.AppendLine("数据目录：" + Storage.DataDir);
            if (Storage.IsFallback)
            {
                log.AppendLine("  ⚠ 数据目录已回退到程序所在目录（未能使用 %APPDATA%）");
                log.AppendLine("    原因：" + Storage.FallbackReason);
                log.AppendLine("    影响：程序装在只读目录时配置与日志将无法保存。");
            }
            if (!IntegrityCheck.IsSufficient(level))
                log.AppendLine("  ⚠ 完整性级别不足，特权会被系统剥离（详见文末说明）");
            log.AppendLine();

            log.AppendLine("—— 网络标记（MOTW）——");
            MotwHelper.Outcome mo = MotwHelper.StartupOutcome;
            if (mo == null)
            {
                log.AppendLine("  启动时未执行检查");
            }
            else
            {
                log.AppendLine("  " + mo.ToString());
                if (mo.Result == MotwHelper.MotwResult.NotPresent)
                    log.AppendLine("     （本地编译或从本地目录复制的文件不带此标记，属正常）");
                else if (mo.Result == MotwHelper.MotwResult.Removed)
                    log.AppendLine("     首次运行时仍需在安全警告上点一次「运行」，之后不再弹框");
            }
            // 自检运行时再查一次现状，便于确认清除是否真的生效
            bool zoneNow = MotwHelper.ZoneStreamExists(exePath);
            int zid = zoneNow ? MotwHelper.ReadZoneId(exePath) : -1;
            log.AppendLine("  当前 Zone.Identifier：" + (zoneNow
                ? "存在" + (zid >= 0 ? "（ZoneId=" + zid + "）" : "（内容无法解析）")
                : "不存在"));
            log.AppendLine();

            log.AppendLine("—— 结构体布局 ——");
            log.AppendLine("  SYSTEM_MEMORY_LIST_INFORMATION      " + Native.SizeOfMemoryListInformation + " 字节");
            log.AppendLine("  SYSTEM_FILECACHE_INFORMATION        " + Native.SizeOfFileCacheInformation + " 字节");
            log.AppendLine("  MEMORY_COMBINE_INFORMATION_EX       " + Native.SizeOfCombineInformationEx + " 字节");
            log.AppendLine();

            log.AppendLine("—— 特权 ——");
            var p1 = Privileges.Enable(Privileges.SeProfileSingleProcess);
            log.AppendLine("  " + p1.Name.PadRight(32) + (p1.Enabled ? "可用" : "不可用") + "：" + p1.Detail);
            var p2 = Privileges.Enable(Privileges.SeIncreaseQuota);
            log.AppendLine("  " + p2.Name.PadRight(32) + (p2.Enabled ? "可用" : "不可用") + "：" + p2.Detail);
            log.AppendLine();

            log.AppendLine("—— 清理项实测 ——");
            CleanResult r1 = MemoryCleaner.PurgeStandbyList();
            log.AppendLine("  待机列表                " + Mark(r1.Success) + " " + r1.Message);
            CleanResult r2 = MemoryCleaner.PurgeLowPriorityStandbyList();
            log.AppendLine("  待机列表（无优先级）    " + Mark(r2.Success) + " " + r2.Message);
            CleanResult r3 = MemoryCleaner.ClearSystemFileCache();
            log.AppendLine("  系统文件缓存            " + Mark(r3.Success) + " " + r3.Message);
            CleanResult r4 = MemoryCleaner.CombineMemoryLists();
            log.AppendLine("  合并内存列表            " + Mark(r4.Success) + " " + r4.Message);
            log.AppendLine();

            MemoryState st = MemoryStateReader.Read();
            if (st.Valid)
            {
                log.AppendLine("—— 当前内存状态 ——");
                log.AppendLine("  物理可用 " + MemoryState.FormatBytes((long)st.AvailableBytes) +
                               " / " + MemoryState.FormatBytes((long)st.TotalBytes) +
                               "（" + st.AvailablePercent.ToString("F1") + "%）");
                log.AppendLine("  待机列表合计 " + MemoryState.FormatBytes(st.StandbyBytesTotal));
                log.AppendLine("  已修改页     " + MemoryState.FormatBytes(st.ModifiedBytes));
                log.AppendLine();
            }

            int failed = (r1.Success ? 0 : 1) + (r2.Success ? 0 : 1) +
                         (r3.Success ? 0 : 1) + (r4.Success ? 0 : 1);
            if (failed > 0)
            {
                log.AppendLine("—— 失败项说明 ——");
                if (!p1.Enabled)
                    log.AppendLine("  待机列表/合并内存列表需要 " + Privileges.SeProfileSingleProcess + "。");
                if (!p2.Enabled)
                    log.AppendLine("  系统文件缓存需要 " + Privileges.SeIncreaseQuota + "。");
                log.AppendLine();

                // 优先给出完整性级别这个根因；它比“缺特权”精确得多
                string advice = IntegrityCheck.BuildAdvice(level, IsElevated(), exePath);
                if (advice != null) log.AppendLine(advice);
            }

            string text = log.ToString();
            // 走 ConsoleOut 而不是直接 Console.Write：
            // 托盘版（winexe）没有控制台，直接写会抛 IOException 让进程崩溃。
            ConsoleOut.Write(text);

            try
            {
                System.IO.File.WriteAllText(Storage.SelfTestPath, text, new UTF8Encoding(false));
            }
            catch { }

            return failed == 0 ? 0 : 2;
        }

        private static string Mark(bool ok) { return ok ? "[可用]" : "[失败]"; }
    }
}
