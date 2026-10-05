using System;
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

        // ---- 其它 ----
        public bool AutoStart = false;

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

            // AutoStart 以注册表实际状态为准：用户可能在任务管理器里手动禁用过启动项，
            // 若只信配置文件，界面会显示错误的状态。
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
            return sb.ToString();
        }
    }
}
