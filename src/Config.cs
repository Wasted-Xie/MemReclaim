using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml.Serialization;

namespace MemReclaim
{
    public enum TriggerMode
    {
        /// <summary>可用内存低于阈值时清理，且有冷却时间。</summary>
        Threshold = 0,
        /// <summary>固定周期清理，不看内存状况。</summary>
        Interval = 1
    }

    /// <summary>配置。以 XML 持久化到程序目录，便于用户直接查看和备份。</summary>
    public class Config
    {
        // ---- 清理项开关 ----
        public bool CleanStandbyList = true;
        public bool CleanLowPriorityStandbyList = true;
        public bool CleanSystemFileCache = true;
        public bool CombineMemoryLists = true;

        // ---- 触发模式 ----
        public TriggerMode Mode = TriggerMode.Threshold;

        // ---- 阈值模式参数 ----
        /// <summary>可用内存低于该百分比时触发清理。</summary>
        public int AvailableMemoryThresholdPercent = 15;
        /// <summary>待机列表超过该 MB 数时也触发（0 = 不启用该条件）。</summary>
        public int StandbyListThresholdMB = 0;

        // ---- 周期模式参数 ----
        /// <summary>固定周期秒数。</summary>
        public int IntervalSeconds = 60;

        // ---- 冷却 ----
        /// <summary>两次清理的最小间隔秒数，防止频繁清空磁盘缓存拖慢系统。</summary>
        public int CooldownSeconds = 300;

        // ---- Combine 触发方式（两种方式共存，各自可独立开关）----
        /// <summary>合并内存列表是否启用「定时」触发。</summary>
        public bool CombineUseInterval = true;
        /// <summary>定时触发的周期（分钟）。</summary>
        public int CombineIntervalMinutes = 30;

        /// <summary>合并内存列表是否启用「阈值」触发。</summary>
        public bool CombineUseThreshold = false;

        /// <summary>
        /// 内存占用超过该百分比时触发合并。
        /// 注意这里是「占用率」，与主清理的「可用率」互补：
        /// 占用 85% 等价于可用 15%。分开表述是为了贴合用户看任务管理器的习惯。
        /// </summary>
        public int CombineMemoryThresholdPercent = 85;

        /// <summary>合并的冷却时间（秒），防止阈值持续满足时连续执行。</summary>
        public int CombineCooldownSeconds = 300;

        // ---- 工作集修剪（Working Set Trim）----
        // 与上面四项清理同层级，但独立成一个列表：
        // 上四项作用于系统级内存列表，修剪作用于具体进程，两者的触发语义与
        // 风险面完全不同；混在同一个开关里会让用户无法只关其中一类。

        /// <summary>是否启用工作集修剪。</summary>
        public bool TrimWorkingSet = false;

        /// <summary>
        /// 修剪的并行线程数（1..32）。
        ///
        /// 默认 4。线程太少时进程多则线性耗时（200 个进程单线程约 4 秒）；
        /// 太多则收益递减——EmptyWorkingSet 会争用内存管理器的锁，
        /// 过度并发反而互相阻塞，还会和前台任务抢 CPU。
        /// </summary>
        public int TrimThreadCount = 4;

        /// <summary>一键纳入：自动修剪所有「后台应用」（无可见窗口的普通进程）。</summary>
        public bool TrimBackgroundApps = false;

        /// <summary>
        /// 一键纳入：自动修剪所有「Windows 进程」（svchost、conhost 等系统组件）。
        ///
        /// 注意：开启后会把系统组件纳入修剪范围，属于激进配置。
        /// 仍受「系统保护」与「当前焦点跳过」两道保护约束，但 conhost、svchost
        /// 这类进程被修剪后可能造成短暂卡顿，建议只在对内存极度敏感时开启。
        /// </summary>
        public bool TrimWindowsProcesses = false;

        /// <summary>
        /// 旧字段，仅用于从旧版配置迁移。
        ///
        /// 早期版本叫 TrimWindowsApps，表示「自动纳入 UWP 应用」；
        /// 分类改为三类后该开关的语义调整为「自动纳入 Windows 进程」。
        /// 保留此字段只为读出旧值，Load 之后会被清零，不再写回。
        /// </summary>
        [XmlElement("TrimWindowsApps")]
        public bool LegacyTrimWindowsApps = false;

        /// <summary>
        /// 让旧字段只读不写：升级后保存配置时不应再写出 TrimWindowsApps，
        /// 否则配置里会同时存在新旧两个开关，用户手动编辑时容易困惑。
        /// </summary>
        public bool ShouldSerializeLegacyTrimWindowsApps() { return false; }

        /// <summary>
        /// 自动修剪的触发门槛：内存占用超过该百分比才修剪。
        ///
        /// 默认 85（与合并的默认值一致）。内存充裕时修剪只是把页面推出去、
        /// 进程马上又读回来，纯属制造缺页。手动修剪不受此限制。
        /// </summary>
        public int TrimMemoryThresholdPercent = 85;

        /// <summary>两次自动修剪的最小间隔（秒）。默认 600，理由同主清理的冷却。</summary>
        public int TrimCooldownSeconds = 600;

        /// <summary>
        /// 修剪时跳过「当前拥有输入焦点」的应用（默认开启）。
        ///
        /// 与「前台应用」分类是两个概念：
        ///   - 分类看有没有可见窗口（任务管理器口径，包含被遮挡、最小化的窗口）；
        ///   - 本开关看的是真正的输入焦点，只保护用户此刻正在操作的那一个。
        /// 关掉它意味着正在打字的程序也可能被修剪，会明显卡顿，一般不要关。
        /// </summary>
        public bool TrimSkipForeground = true;

        /// <summary>
        /// 白名单（进程名，可带或不带 .exe）。白名单中的进程即使被一键纳入也永不修剪。
        ///
        /// 用 string[] 而不是 List&lt;string&gt;：XmlSerializer 对数组的行为更稳定
        /// （空数组落成 &lt;TrimWhitelist /&gt;，List 可能落成 null），
        /// 而这份配置是要给人看、也可能被手改的。
        /// </summary>
        public string[] TrimWhitelist = new string[0];

        /// <summary>
        /// 显式勾选的进程（进程名，不带 .exe，大小写不敏感）。
        ///
        /// 按名称而不是 PID 保存：PID 每次开机都变，存 PID 等于勾选只对本次会话
        /// 有效，用户会以为设置丢了。按名称保存的副作用是同名的多个实例
        /// （如多个 chrome.exe）会一起被选中——对内存修剪来说这正是期望行为。
        /// </summary>
        public string[] TrimSelectedNames = new string[0];

        /// <summary>白名单的快速查找集合（大小写不敏感，已去 .exe）。</summary>
        public HashSet<string> GetWhitelistSet()
        {
            return ToNameSet(TrimWhitelist);
        }

        /// <summary>显式勾选的快速查找集合。</summary>
        public HashSet<string> GetSelectedSet()
        {
            return ToNameSet(TrimSelectedNames);
        }

        private static HashSet<string> ToNameSet(string[] source)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (string s in source)
                {
                    if (string.IsNullOrEmpty(s)) continue;

                    // 注意要先 Trim 再判空：配置里可能残留 "  " 这种纯空白项，
                    // 若直接 StripExe 会得到空串并混进集合，最终落盘成一个
                    // 看不见的空条目。
                    string t = ProcessEnumerator.StripExe(s.Trim());
                    if (t.Length == 0) continue;

                    set.Add(t);
                }
            }
            return set;
        }

        /// <summary>把集合写回数组（排序后保存，便于人工核对配置）。</summary>
        public static string[] FromNameSet(HashSet<string> set)
        {
            if (set == null || set.Count == 0) return new string[0];
            List<string> list = new List<string>(set);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        // ---- 其它 ----
        public bool AutoStart = false;

        /// <summary>
        /// 配置文件里原本记录的自启状态（加载时的原始值）。
        ///
        /// AutoStart 在 Load() 中会被启动文件夹的实际状态覆盖，覆盖之后就看不出
        /// 「配置文件说已启用、实际却没有快捷方式」这种偏差了。保留原值是为了让
        /// 程序启动时能判断是否需要回写修正——只改内存不落盘的话，XML 里会一直
        /// 残留 true，日后排查时会误导。
        /// </summary>
        [XmlIgnore]
        public bool AutoStartConfigured = false;

        /// <summary>
        /// 是否在清理后弹出托盘气泡通知。默认关闭——
        /// 自动清理是后台行为，频繁弹窗会打扰使用；需要时可在设置里开启。
        /// 注意：这不影响「权限不足」等异常提示，那些仍会显示，
        /// 否则权限问题会被静默吞掉，用户无从察觉。
        /// </summary>
        public bool ShowNotifications = false;
        /// <summary>界面语言：空=自动（跟随系统），zh-CN 或 en-US。</summary>
        public string Language = "";

        /// <summary>是否需要管理员权限才能工作（缺特权时界面会提示）。</summary>
        [XmlIgnore]
        public bool Elevated = false;

        /// <summary>
        /// 配置文件路径。
        /// 放在 %APPDATA%\MemReclaim\ 而非 exe 目录：程序可能装在
        /// Program Files 等只读位置，写入 exe 目录会失败。
        /// </summary>
        private static string ConfigPath
        {
            get { return Storage.ConfigPath; }
        }

        public static Config Load()
        {
            // 旧版把配置放在 exe 目录，升级后自动迁移一次
            Storage.MigrateLegacyConfig();

            Config c = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    XmlSerializer ser = new XmlSerializer(typeof(Config));
                    using (FileStream fs = File.OpenRead(ConfigPath))
                    {
                        c = (Config)ser.Deserialize(fs);
                    }
                }
            }
            catch
            {
                // 配置损坏时回退到默认值，不影响程序启动
            }

            if (c == null) c = new Config();
            c.Normalize();

            // 先记下配置文件里的原始值，再以实际状态覆盖。
            c.AutoStartConfigured = c.AutoStart;

            // AutoStart 以启动文件夹的实际状态为准：判据是「shell:startup 里是否存在
            // 指向本 exe 的快捷方式」。用户手动删掉快捷方式、或程序被移动到别处导致
            // 快捷方式指向失效时，这里会得到 false，界面随即显示为未勾选。
            // 若只信配置文件，界面会显示一个开机实际不会生效的勾选状态。
            // 注意用完全限定名：本类的 AutoStart 字段会遮蔽同名的静态类。
            c.AutoStart = MemReclaim.AutoStart.IsEnabled();

            return c;
        }

        public void Save()
        {
            try
            {
                XmlSerializer ser = new XmlSerializer(typeof(Config));
                using (FileStream fs = File.Create(ConfigPath))
                {
                    ser.Serialize(fs, this);
                }
            }
            catch
            {
                // 保存失败不致命，下次仍可用默认值
            }
        }

        /// <summary>把越界参数夹回合理范围，避免用户输入导致异常行为。</summary>
        public Config Normalize()
        {
            if (AvailableMemoryThresholdPercent < 1) AvailableMemoryThresholdPercent = 1;
            if (AvailableMemoryThresholdPercent > 90) AvailableMemoryThresholdPercent = 90;

            if (StandbyListThresholdMB < 0) StandbyListThresholdMB = 0;
            if (StandbyListThresholdMB > 1000000) StandbyListThresholdMB = 1000000;

            if (IntervalSeconds < 0) IntervalSeconds = 0;
            if (IntervalSeconds > 86400) IntervalSeconds = 86400;

            if (CooldownSeconds < 0) CooldownSeconds = 0;
            if (CooldownSeconds > 86400) CooldownSeconds = 86400;

            if (CombineIntervalMinutes < 0) CombineIntervalMinutes = 0;
            if (CombineIntervalMinutes > 1440) CombineIntervalMinutes = 1440;

            if (CombineMemoryThresholdPercent < 1) CombineMemoryThresholdPercent = 1;
            if (CombineMemoryThresholdPercent > 99) CombineMemoryThresholdPercent = 99;

            if (CombineCooldownSeconds < 0) CombineCooldownSeconds = 0;
            if (CombineCooldownSeconds > 86400) CombineCooldownSeconds = 86400;

            // 工作集修剪：线程数必须落在可用区间。上限 32 不是随意取的——
            // 再高就会淹没在内存管理器的锁竞争里，实测无收益。
            if (TrimThreadCount < 1) TrimThreadCount = 1;
            if (TrimThreadCount > 32) TrimThreadCount = 32;

            if (TrimMemoryThresholdPercent < 1) TrimMemoryThresholdPercent = 1;
            if (TrimMemoryThresholdPercent > 99) TrimMemoryThresholdPercent = 99;

            if (TrimCooldownSeconds < 0) TrimCooldownSeconds = 0;
            if (TrimCooldownSeconds > 86400) TrimCooldownSeconds = 86400;

            // 白名单/勾选列表清洗：去掉空白项与末尾 .exe，避免存进配置后比对不上。
            TrimWhitelist = FromNameSet(ToNameSet(TrimWhitelist));
            TrimSelectedNames = FromNameSet(ToNameSet(TrimSelectedNames));

            // 兼容旧配置：旧版 TrimWindowsApps 表示「自动纳入 UWP」，
            // 现已改为「自动纳入 Windows 进程」。旧值 true 时延续开启意图，
            // 然后清零，避免下次保存又写回旧字段。
            if (LegacyTrimWindowsApps && !TrimWindowsProcesses)
                TrimWindowsProcesses = true;
            LegacyTrimWindowsApps = false;

            // 兼容旧配置：早先版本用 CombineIntervalMinutes = 0 表示「随主清理一起执行」，
            // 且没有独立开关。此时若两个开关都关着，合并将永不被自动触发，
            // 与用户原本的预期不符，故按阈值方式启用一次，交由冷却控制频率。
            if (!CombineUseInterval && !CombineUseThreshold && CombineMemoryLists)
                CombineUseThreshold = true;

            return this;
        }

        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("模式：" + (Mode == TriggerMode.Threshold
                ? "阈值触发（可用内存低于 " + AvailableMemoryThresholdPercent + "%" +
                  (StandbyListThresholdMB > 0 ? "，或待机列表超过 " + StandbyListThresholdMB + " MB" : "") + "）"
                : "固定周期（每 " + IntervalSeconds + " 秒" +
                  (CooldownSeconds > IntervalSeconds ? "，实际受冷却限制为 " + CooldownSeconds + " 秒" : "") + "）"));
            sb.AppendLine("冷却时间：" + CooldownSeconds + " 秒" + (CooldownSeconds == 0 ? "（不限制）" : ""));
            sb.AppendLine("清理项：");
            sb.AppendLine("  待机列表                " + (CleanStandbyList ? "开" : "关"));
            sb.AppendLine("  待机列表（无优先级）    " + (CleanLowPriorityStandbyList ? "开" : "关"));
            sb.AppendLine("  系统文件缓存            " + (CleanSystemFileCache ? "开" : "关"));

            string combineMode;
            if (!CombineMemoryLists) combineMode = "关";
            else if (!CombineUseInterval && !CombineUseThreshold) combineMode = "开（无自动触发，仅手动）";
            else
            {
                StringBuilder cm = new StringBuilder("开（");
                if (CombineUseInterval) cm.Append("每 " + CombineIntervalMinutes + " 分钟");
                if (CombineUseInterval && CombineUseThreshold) cm.Append("，或");
                if (CombineUseThreshold) cm.Append("内存占用超过 " + CombineMemoryThresholdPercent + "%");
                cm.Append("）");
                combineMode = cm.ToString();
            }
            sb.AppendLine("  合并内存列表            " + combineMode);
            sb.AppendLine("工作集修剪：");
            sb.AppendLine("  总开关                  " + (TrimWorkingSet ? "开（线程 " + TrimThreadCount + "）" : "关"));
            sb.AppendLine("  自动纳入后台应用        " + (TrimBackgroundApps ? "开" : "关"));
            sb.AppendLine("  自动纳入 Windows 进程   " + (TrimWindowsProcesses ? "开" : "关"));
            sb.AppendLine("  白名单                  " +
                (TrimWhitelist != null && TrimWhitelist.Length > 0
                    ? TrimWhitelist.Length + " 项：" + string.Join(", ", TrimWhitelist)
                    : "无"));
            return sb.ToString();
        }
    }
}
