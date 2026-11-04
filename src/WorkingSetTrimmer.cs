using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MemReclaim
{
    /// <summary>一次工作集修剪的汇总结果。</summary>
    internal class TrimSummary
    {
        public int Attempted;      // 实际尝试的进程数
        public int Succeeded;
        public int Failed;
        public long BytesFreed;    // 估算释放量（修剪前后工作集差）
        public long ElapsedMs;

        /// <summary>异常说明；正常为空。</summary>
        public string Description;

        /// <summary>逐进程明细，供界面列表回填。</summary>
        public List<ProcessEntry> Entries = new List<ProcessEntry>();

        public override string ToString()
        {
            if (!string.IsNullOrEmpty(Description)) return Description;
            if (Attempted == 0) return "工作集修剪：没有匹配的进程";
            return "工作集修剪：" + Succeeded + "/" + Attempted + " 个进程成功" +
                   "，释放约 " + MemoryState.FormatBytes(BytesFreed) +
                   "，耗时 " + ElapsedMs + " ms";
        }
    }

    /// <summary>
    /// 工作集修剪执行器。
    ///
    /// 【为什么选 EmptyWorkingSet】
    /// 用户提供的单进程压缩报告（C:\Projects\内存压缩\docs\单进程压缩完整报告.md）
    /// 结论明确：驱动层的压缩控制手段全部无效（per-process store 门禁关闭、
    /// 全局压缩是被动机制），唯一真正触发压缩的是制造真实的「页面需要换出」需求，
    /// 而 EmptyWorkingSet 正是用户态唯一能做到这件事的接口。
    /// 实测 38 个后台进程回收 1,694.8 MB，压缩进程工作集 590 MB → 839 MB。
    ///
    /// 【并发模型】
    /// 修剪是逐进程的独立操作，进程之间没有共享状态，因此可以安全并行。
    /// 这里用固定数量的工作线程从队列取任务（而不是对每个进程开一个线程），
    /// 线程数由配置决定，默认 4：
    ///   - 太少：进程多时线性耗时，例如 200 个进程 × 20 ms ≈ 4 s；
    ///   - 太多：EmptyWorkingSet 会持有内存管理器锁，过度并发反而互相阻塞，
    ///     且在低配机器上会与前台任务争抢 CPU。
    /// 线程数上限在 Config.Normalize 中夹到 1..32。
    ///
    /// 【严格异步】
    /// 本类的 Run 方法本身是同步的（阻塞调用它的线程），
    /// 由调用方放到后台线程执行。TriggerEngine 与界面都不会在 UI 线程调用它。
    /// </summary>
    internal static class WorkingSetTrimmer
    {
        /// <summary>
        /// 异步执行一次修剪：枚举进程 → 选目标 → 并行修剪。
        ///
        /// 【严格异步】本方法立即返回，全部工作在后台线程完成，UI 与
        /// 触发引擎的定时器都不会被阻塞。传入的 callback 来自后台线程，
        /// 订阅方必须自行切回 UI 线程。
        /// </summary>
        public static void RunAsync(Config config, Action<TrimSummary> callback,
            Action<int, int> progress)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                TrimSummary sum;
                try
                {
                    List<ProcessEntry> all = ProcessEnumerator.Enumerate();
                    List<ProcessEntry> targets = SelectTargets(all, config);

                    Action<int> onOne = null;
                    if (progress != null)
                    {
                        int total = targets.Count;
                        onOne = delegate (int done)
                        {
                            try { progress(done, total); } catch { }
                        };
                    }

                    sum = Run(targets, config.TrimThreadCount, onOne);
                }
                catch (Exception ex)
                {
                    // 后台线程未捕获的异常会终止进程，必须兜住
                    sum = new TrimSummary();
                    sum.Attempted = 0;
                    sum.Failed = 1;
                    sum.Description = "工作集修剪异常：" + ex.Message;
                }

                if (callback != null)
                {
                    try { callback(sum); } catch { }
                }
            });
        }

        /// <summary>
        /// 对给定进程列表执行并行修剪（同步，阻塞调用线程）。
        /// </summary>
        /// <param name="targets">目标进程（会被就地回填 TrimOk / TrimMs / WorkingSetAfterTrim）。</param>
        /// <param name="threadCount">并行线程数，1..32。</param>
        /// <param name="progress">每完成一个进程回调一次（可为 null）。回调来自工作线程。</param>
        public static TrimSummary Run(List<ProcessEntry> targets, int threadCount,
            Action<int> progress)
        {
            TrimSummary sum = new TrimSummary();
            if (targets == null || targets.Count == 0) return sum;

            if (threadCount < 1) threadCount = 1;
            if (threadCount > 32) threadCount = 32;
            if (threadCount > targets.Count) threadCount = targets.Count;

            Stopwatch sw = Stopwatch.StartNew();

            int next = -1;              // 共享游标，用 Interlocked 递增领取
            int done = 0;
            int succeeded = 0;
            int failed = 0;
            long freed = 0;

            // 进度回调节流：进程多时逐条上报会让 UI 每秒重绘上百次，表现为持续闪烁。
            // 这里最多上报约 20 次，且最后一次必报，保证界面能收到完成信号。
            int progressStep = targets.Count / 20;
            if (progressStep < 1) progressStep = 1;

            Thread[] workers = new Thread[threadCount];
            for (int i = 0; i < threadCount; i++)
            {
                workers[i] = new Thread(delegate ()
                {
                    while (true)
                    {
                        int idx = Interlocked.Increment(ref next);
                        if (idx >= targets.Count) break;

                        ProcessEntry e = targets[idx];

                        bool ok;
                        int ms;
                        long after;
                        TrimOne(e, out ok, out ms, out after);

                        // 每个进程只被一个线程处理，直接写字段即可
                        e.TrimOk = ok;
                        e.TrimMs = ms;
                        e.WorkingSetAfterTrim = after;

                        if (ok)
                        {
                            Interlocked.Increment(ref succeeded);
                            long delta = e.WorkingSet - after;
                            if (delta > 0) Interlocked.Add(ref freed, delta);
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                        }

                        int n = Interlocked.Increment(ref done);
                        if (progress != null &&
                            (n % progressStep == 0 || n == targets.Count))
                        {
                            try { progress(n); } catch { }
                        }
                    }
                });
                workers[i].IsBackground = true;   // 主程序退出时不阻止进程结束
                workers[i].Name = "MemReclaim-Trim-" + i;
                workers[i].Start();
            }

            for (int i = 0; i < workers.Length; i++)
            {
                try { workers[i].Join(); } catch { }
            }

            sw.Stop();

            sum.Attempted = targets.Count;
            sum.Succeeded = succeeded;
            sum.Failed = failed;
            sum.BytesFreed = freed;
            sum.ElapsedMs = sw.ElapsedMilliseconds;
            sum.Entries = targets;

            return sum;
        }

        /// <summary>
        /// 修剪单个进程的工作集。
        ///
        /// 测量方式：记下调用前后的工作集差值。EmptyWorkingSet 是同步的，
        /// 返回时目标进程的工作集已被压缩完毕，因此紧随其后的读取能反映效果。
        /// 注意这个差值只是「本次推出物理内存的量」，进程随后仍会按需重新调入，
        /// 所以它是回收量而不是永久节省量。
        /// </summary>
        private static void TrimOne(ProcessEntry e, out bool ok, out int ms, out long afterWs)
        {
            ok = false;
            ms = 0;
            afterWs = e.WorkingSet;

            Stopwatch sw = Stopwatch.StartNew();
            IntPtr h = IntPtr.Zero;

            try
            {
                // PROCESS_QUERY_INFORMATION | PROCESS_SET_QUOTA 是 EmptyWorkingSet
                // 文档要求的组合。部分受保护进程会拒绝，此时如实记失败。
                h = Native.OpenProcess(
                    Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA,
                    false, e.Pid);

                if (h == IntPtr.Zero)
                {
                    ms = (int)sw.ElapsedMilliseconds;
                    return;
                }

                ok = Native.EmptyWorkingSet(h);

                if (ok)
                {
                    long now = ProcessEnumerator.GetWorkingSet(e.Pid);
                    afterWs = now >= 0 ? now : e.WorkingSet;
                }
            }
            catch
            {
                ok = false;
            }
            finally
            {
                if (h != IntPtr.Zero) Native.CloseHandle(h);
                ms = (int)sw.ElapsedMilliseconds;
            }
        }

        // ---------------- 目标选择 ----------------

        /// <summary>
        /// 根据配置决定要修剪哪些进程。
        ///
        /// 优先级规则（越靠前越强）：
        ///   1. 系统关键进程（IsProtected）永远排除；
        ///   2. 白名单永远排除（用户显式豁免，即使被一键纳入也无效）；
        ///   3. 显式勾选的进程参与（可包含 Windows 进程，由用户自己决定）；
        ///   4. 「自动纳入后台应用」只作用于后台应用（不含前台应用与 Windows 进程）；
        ///   5. 「自动纳入 Windows 进程」作用于系统组件（svchost、conhost 等）。
        ///
        /// 为什么自动纳入不碰前台应用：那是用户正在使用的程序，
        /// 修剪其工作集会让它立刻缺页，表现为卡顿。
        /// 为什么不碰 Windows 进程：svchost 等是服务宿主，修剪会造成连锁缺页。
        /// 两者都只能由用户显式勾选后才参与。
        /// </summary>
        public static List<ProcessEntry> SelectTargets(List<ProcessEntry> all, Config config)
        {
            List<ProcessEntry> targets = new List<ProcessEntry>();
            if (all == null) return targets;

            HashSet<string> whitelist = config.GetWhitelistSet();
            HashSet<string> selected = config.GetSelectedSet();

            foreach (ProcessEntry e in all)
            {
                if (e.IsProtected) continue;

                string name = ProcessEnumerator.StripExe(e.Name);
                if (whitelist.Contains(name)) continue;

                // 跳过用户此刻正在交互的应用（同映像路径的进程一并跳过）。
                // 这是硬保护：即使该进程被显式勾选、或落在「自动纳入」范围内，
                // 只要它正持有输入焦点就不修剪——修剪正在使用的程序会立刻
                // 让它缺页，表现为输入卡顿、界面掉帧。
                if (config.TrimSkipForeground && e.IsForegroundActive) continue;

                bool hit = selected.Contains(name) ||
                    (config.TrimBackgroundApps && e.Kind == ProcessKind.Background) ||
                    (config.TrimWindowsProcesses && e.Kind == ProcessKind.WindowsProcess);

                if (hit) targets.Add(e);
            }

            return targets;
        }
    }
}
