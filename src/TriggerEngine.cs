using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MemReclaim
{
    /// <summary>
    /// 触发引擎：按配置的两种模式决定何时清理，并独立调度 Combine。
    ///
    /// 设计要点：
    /// - 待机列表本质是系统的磁盘缓存，清空后首次读盘会变慢，
    ///   因此默认走阈值 + 冷却，而不是无条件高频清理；
    /// - Combine 开销高于前几项，独立定时，周期由用户配置。
    /// </summary>
    internal class TriggerEngine : IDisposable
    {
        private readonly Config _config;
        private Timer _timer;
        private readonly object _sync = new object();

        private DateTime _lastClean = DateTime.MinValue;
        private DateTime _lastCombine = DateTime.MinValue;
        private bool _busy;

        /// <summary>一次清理完成后回调（用于界面显示与通知）。</summary>
        public event Action<List<CleanResult>> Cleaned;

        /// <summary>清理前的内存状态快照（供界面刷新）。</summary>
        public event Action<MemoryState> StateUpdated;

        public TriggerEngine(Config config)
        {
            _config = config;
        }

        public DateTime LastCleanTime { get { lock (_sync) return _lastClean; } }
        public DateTime LastCombineTime { get { lock (_sync) return _lastCombine; } }

        public void Start()
        {
            // 每秒 tick 一次：阈值模式靠频繁采样才能及时响应，代价可忽略
            _timer = new Timer(OnTick, null, 1000, 1000);
        }

        public void Stop()
        {
            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
        }

        private void OnTick(object state)
        {
            lock (_sync)
            {
                if (_busy) return;
                _busy = true;
            }

            try
            {
                MemoryState st = MemoryStateReader.Read();
                Action<MemoryState> su = StateUpdated;
                if (su != null) su(st);

                if (!st.Valid) return;

                // ---- Combine 触发：定时与阈值两种方式共存，各自独立开关 ----
                if (_config.CombineMemoryLists)
                {
                    if (ShouldCombine(st)) RunCombine();
                }

                // ---- 主清理 ----
                bool shouldClean;
                if (_config.Mode == TriggerMode.Threshold) shouldClean = EvaluateThreshold(st);
                else shouldClean = EvaluateInterval();

                if (shouldClean) RunClean();
            }
            catch
            {
                // 单次 tick 失败不应终止常驻循环
            }
            finally
            {
                lock (_sync) _busy = false;
            }
        }

        /// <summary>
        /// 判断本轮是否应执行内存页面合并。
        ///
        /// 定时与阈值两种方式共存：任一满足即触发（各自带独立开关）。
        /// 两者都要先通过共用的冷却时间——阈值在内存持续高占用时会每秒成立，
        /// 没有冷却会导致合并被反复调用，而它恰恰是开销最大的操作。
        /// </summary>
        private bool ShouldCombine(MemoryState st)
        {
            // 两个开关都关：仅保留手动执行，不做任何自动触发
            if (!_config.CombineUseInterval && !_config.CombineUseThreshold) return false;

            double sinceLast;
            lock (_sync) sinceLast = (DateTime.Now - _lastCombine).TotalSeconds;

            // 共用冷却
            if (_config.CombineCooldownSeconds > 0 && sinceLast < _config.CombineCooldownSeconds)
                return false;

            // 方式一：定时
            if (_config.CombineUseInterval && _config.CombineIntervalMinutes > 0)
            {
                if (sinceLast >= _config.CombineIntervalMinutes * 60.0) return true;
            }

            // 方式二：阈值（内存占用超过设定百分比）
            if (_config.CombineUseThreshold)
            {
                double usedPercent = 100.0 - st.AvailablePercent;
                if (usedPercent >= _config.CombineMemoryThresholdPercent) return true;
            }

            return false;
        }

        private void RunCombine()
        {
            List<CleanResult> results = new List<CleanResult>();
            if (_config.CombineMemoryLists)
                results.Add(MemoryCleaner.CombineMemoryLists());
            lock (_sync) _lastCombine = DateTime.Now;
            Report(results);
        }

        /// <summary>阈值模式：可用内存过低，或待机列表过大时触发；冷却期内不重复清理。</summary>
        private bool EvaluateThreshold(MemoryState st)
        {
            if (_config.CooldownSeconds > 0)
            {
                lock (_sync)
                {
                    if ((DateTime.Now - _lastClean).TotalSeconds < _config.CooldownSeconds) return false;
                }
            }

            if (st.AvailablePercent < _config.AvailableMemoryThresholdPercent) return true;

            if (_config.StandbyListThresholdMB > 0)
            {
                long standbyMB = st.StandbyBytesTotal / 1048576;
                if (standbyMB > _config.StandbyListThresholdMB) return true;
            }

            return false;
        }

        /// <summary>周期模式：严格按间隔清理，冷却同样生效以免与间隔设置冲突。</summary>
        private bool EvaluateInterval()
        {
            lock (_sync)
            {
                double elapsed = (DateTime.Now - _lastClean).TotalSeconds;
                if (elapsed < _config.IntervalSeconds) return false;
                if (_config.CooldownSeconds > 0 && elapsed < _config.CooldownSeconds) return false;
                return true;
            }
        }

        /// <summary>
        /// 手动触发（托盘菜单 / 设置界面按钮）。忽略阈值与冷却。
        ///
        /// **本方法不阻塞调用线程**：清理动作在后台线程执行，完成后通过
        /// Cleaned 事件回报。UI 线程因此不会被 NtSetSystemInformation 冻住——
        /// 内存压力极大时单次清理可能耗时数秒，同步执行会让窗口呈现「无响应」。
        ///
        /// 完成后回调可能来自后台线程，订阅方需自行切回 UI 线程
        /// （见 TrayApp.OnCleaned / SettingsForm.OnCleaned）。
        ///
        /// 返回 false 表示已有清理在进行中，本次请求被忽略——
        /// 这样手动操作与自动触发共享同一把锁，不会并发调用内核接口。
        /// </summary>
        public bool CleanNowAsync(bool includeCombine)
        {
            // 与自动触发路径共用 _busy：两者都调用 NtSetSystemInformation，
            // 并发执行既无意义也可能相互干扰，必须互斥。
            lock (_sync)
            {
                if (_busy) return false;
                _busy = true;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<CleanResult> results = RunCleanCore(includeCombine);

                    lock (_sync)
                    {
                        _lastClean = DateTime.Now;
                        if (includeCombine) _lastCombine = DateTime.Now;
                    }

                    Report(results);
                }
                catch (Exception ex)
                {
                    // 后台线程里未捕获的异常会终止进程，必须兜住。
                    // 以一条失败结果回报，让界面能显示出来。
                    List<CleanResult> err = new List<CleanResult>();
                    CleanResult r = new CleanResult();
                    r.Item = "清理";
                    r.Executed = true;
                    r.Success = false;
                    r.Message = "后台执行异常：" + ex.Message;
                    err.Add(r);
                    Report(err);
                }
                finally
                {
                    lock (_sync) _busy = false;
                }
            });

            return true;
        }

        /// <summary>
        /// 仅合并内存列表（手动按钮调用）。与 CleanNowAsync 同样异步、同样互斥。
        /// </summary>
        public bool CombineNowAsync()
        {
            lock (_sync)
            {
                if (_busy) return false;
                _busy = true;
            }

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    List<CleanResult> results = new List<CleanResult>();
                    results.Add(MemoryCleaner.CombineMemoryLists());

                    lock (_sync) _lastCombine = DateTime.Now;

                    Report(results);
                }
                catch (Exception ex)
                {
                    List<CleanResult> err = new List<CleanResult>();
                    CleanResult r = new CleanResult();
                    r.Item = "合并内存列表";
                    r.Executed = true;
                    r.Success = false;
                    r.Message = "后台执行异常：" + ex.Message;
                    err.Add(r);
                    Report(err);
                }
                finally
                {
                    lock (_sync) _busy = false;
                }
            });

            return true;
        }

        private void RunClean()
        {
            // 注意：这里不含 Combine。
            // 合并由 ShouldCombine() 独立判断（定时 / 阈值，各有开关），
            // 若在此处再次执行，会在同一 tick 内重复合并，白白付出开销。
            List<CleanResult> results = RunCleanCore(false);
            lock (_sync) _lastClean = DateTime.Now;
            Report(results);
        }

        /// <summary>执行已启用的清理项。includeCombine 为 true 时一并合并内存列表。</summary>
        private List<CleanResult> RunCleanCore(bool includeCombine)
        {
            List<CleanResult> results = new List<CleanResult>();

            if (_config.CleanStandbyList)
                results.Add(MemoryCleaner.PurgeStandbyList());

            if (_config.CleanLowPriorityStandbyList)
                results.Add(MemoryCleaner.PurgeLowPriorityStandbyList());

            if (_config.CleanSystemFileCache)
                results.Add(MemoryCleaner.ClearSystemFileCache());

            if (includeCombine && _config.CombineMemoryLists)
                results.Add(MemoryCleaner.CombineMemoryLists());

            return results;
        }

        private void Report(List<CleanResult> results)
        {
            Action<List<CleanResult>> h = Cleaned;
            if (h != null) h(results);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
