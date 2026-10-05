using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MemReclaim
{
    /// <summary>
    /// 安全的控制台输出。
    ///
    /// 为什么需要它：本项目同时产出两种子系统的 exe——
    /// 控制台版（/target:exe）和托盘版（/target:winexe）。
    /// winexe 进程默认**不附加控制台**，此时：
    ///
    ///   Console.OutputEncoding = Encoding.UTF8;
    ///
    /// 会抛 System.IO.IOException（内部走 __Error.WinIOError，
    /// 因为拿不到有效的控制台句柄），导致进程以未处理异常终止。
    ///
    /// 实测后果：MemReclaim.exe 与 MemReclaimNoUAC.exe 执行 --selftest
    /// 都会崩溃（退出码 0xE0434352），而 MemReclaimConsole.exe 正常。
    /// 事件日志中的调用栈明确指向 Console.set_OutputEncoding：
    ///     System.IO.IOException
    ///       在 System.Console.set_OutputEncoding(...)
    ///       在 MemReclaim.SelfTest.Run()
    ///
    /// 因此所有控制台输出统一走这里：先判断是否真的挂着控制台，
    /// 再整体包在 try 里——计划任务、服务等无控制台场景下静默跳过即可，
    /// 输出丢失不影响功能（日志文件另有记录）。
    /// </summary>
    internal static class ConsoleOut
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        /// <summary>当前进程是否附加了控制台。</summary>
        public static bool HasConsole
        {
            get
            {
                try { return GetConsoleWindow() != IntPtr.Zero; }
                catch { return false; }
            }
        }

        /// <summary>是否应当向控制台输出。可用环境变量强制关闭，便于排错。</summary>
        private static bool ShouldWrite
        {
            get
            {
                if (!HasConsole) return false;
                return Environment.GetEnvironmentVariable("MEMRECLAIM_NO_CONSOLE") != "1";
            }
        }

        /// <summary>输出文本。无控制台或写入失败时静默跳过。</summary>
        public static void Write(string text)
        {
            if (!ShouldWrite) return;
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.Write(text);
            }
            catch
            {
                // 无控制台、句柄失效、管道已关闭等：忽略。
                // 这些都不是功能性问题，不该让进程崩溃。
            }
        }

        /// <summary>输出一行。</summary>
        public static void WriteLine(string text)
        {
            Write(text + Environment.NewLine);
        }
    }
}
