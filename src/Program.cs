using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MemReclaim
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            // ---- 启动即清除自身的网络标记 ----
            // 首次运行时用户仍需在安全警告上点一次「运行」；此后双击不再被拦。
            // 放在最前：命令行模式与托盘模式都要处理，且提权重启后还会再跑一次。
            // 任何异常都不影响主流程，所以整体包在 try 里。
            // 结果存入 MotwHelper.StartupOutcome，供 --selftest 与设置界面展示。
            try { MotwHelper.CleanSelf(); }
            catch { }

            // ---- 命令行模式：供计划任务或脚本调用 ----
            // 这些模式不弹 UAC（计划任务下弹窗会挂住任务，且会丢失输出），
            // 权限不足时由 --selftest / --once 自行报告，便于排查。
            if (args.Length > 0)
            {
                string a0 = args[0].ToLowerInvariant();

                if (a0 == "--once" || a0 == "-once")
                {
                    ElevationHelper.EnsureElevated(args, false);
                    return RunOnce();
                }
                if (a0 == "--selftest" || a0 == "-selftest")
                {
                    ElevationHelper.EnsureElevated(args, false);
                    return SelfTest.Run();
                }

                if (a0 == "--help" || a0 == "-h" || a0 == "/?") { PrintHelp(); return 0; }
            }

            // ---- 托盘常驻模式 ----
            // 需要管理员权限才能启用内存清理所需特权；
            // 非管理员时自动请求提权重启（用户拒绝则降权继续，并给出诊断）。
            if (!ElevationHelper.EnsureElevated(args, true))
            {
                return 0;   // 提权实例已启动，本实例退出
            }

            bool createdNew;
            using (Mutex single = new Mutex(true, "MemReclaim_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("内存回收程序已在运行。", "内存回收",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 1;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        /// <summary>单次清理后退出，便于任务计划程序调用。</summary>
        private static int RunOnce()
        {
            Config cfg = Config.Load();

            // 命令行模式下启用全部已勾选项，含 Combine
            StringBuilder log = new StringBuilder();
            log.AppendLine("内存回收 — 单次执行");
            log.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine();

            var priv1 = Privileges.Enable(Privileges.SeProfileSingleProcess);
            var priv2 = Privileges.Enable(Privileges.SeIncreaseQuota);
            log.AppendLine("特权：" + priv1.Name + " → " + priv1.Detail);
            log.AppendLine("特权：" + priv2.Name + " → " + priv2.Detail);
            log.AppendLine();

            MemoryState before = MemoryStateReader.Read();
            AppendState(log, "清理前", before);

            if (cfg.CleanStandbyList) log.AppendLine(MemoryCleaner.PurgeStandbyList().ToString());
            if (cfg.CleanLowPriorityStandbyList) log.AppendLine(MemoryCleaner.PurgeLowPriorityStandbyList().ToString());
            if (cfg.CleanSystemFileCache) log.AppendLine(MemoryCleaner.ClearSystemFileCache().ToString());
            if (cfg.CombineMemoryLists) log.AppendLine(MemoryCleaner.CombineMemoryLists().ToString());

            MemoryState after = MemoryStateReader.Read();
            AppendState(log, "清理后", after);

            string text = log.ToString();

            // 走 ConsoleOut：托盘版（winexe）没有控制台，直接写会抛 IOException。
            ConsoleOut.Write(text);

            // 同时写日志文件，便于事后核对
            Storage.AppendLog(text + new string('-', 60) + Environment.NewLine);

            return 0;
        }

        private static void AppendState(StringBuilder log, string tag, MemoryState s)
        {
            if (!s.Valid)
            {
                log.AppendLine("[" + tag + "] 无法读取内存列表状态");
                return;
            }
            log.AppendLine("[" + tag + "] 物理可用 " + MemoryState.FormatBytes((long)s.AvailableBytes) +
                           " / " + MemoryState.FormatBytes((long)s.TotalBytes) +
                           "（" + s.AvailablePercent.ToString("F1") + "%）");
            log.AppendLine("          待机列表 " + MemoryState.FormatBytes(s.StandbyBytesTotal) +
                           "（P0 " + MemoryState.FormatBytes(s.LowPriorityStandbyBytes) + "）" +
                           "，已修改 " + MemoryState.FormatBytes(s.ModifiedBytes));
            log.AppendLine();
        }

        private static void PrintHelp()
        {
            // 走 ConsoleOut：托盘版（winexe）没有控制台，直接写会抛 IOException 崩溃
            ConsoleOut.WriteLine("内存回收 (MemReclaim)");
            ConsoleOut.WriteLine("");
            ConsoleOut.WriteLine("用法：");
            ConsoleOut.WriteLine("  MemReclaim.exe               启动托盘常驻程序");
            ConsoleOut.WriteLine("  MemReclaim.exe --once        执行一次清理后退出（供计划任务调用）");
            ConsoleOut.WriteLine("  MemReclaim.exe --selftest    检测本机各项清理是否可用");
            ConsoleOut.WriteLine("  MemReclaim.exe --help        显示本帮助");
            ConsoleOut.WriteLine("");
            ConsoleOut.WriteLine("数据目录：" + Storage.DataDir);
        }
    }
}
