using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MemReclaim
{
    /// <summary>
    /// 设置窗口。
    ///
    /// 【布局方式说明】
    /// 全部控件用代码创建，不依赖设计器资源。坐标按 96 DPI（100% 缩放）设计，
    /// 运行时统一乘以实际缩放系数 _scale。
    ///
    /// 为什么不用 AutoScaleMode.Font 或 .Dpi：
    /// 本程序清单声明了 dpiAware，Windows 不做位图拉伸，字体按系统 DPI 渲染。
    /// 而 WinForms 的自动缩放与手动设置 Font 叠加时行为难以预测，实测在
    /// 150% 缩放下会出现文字相互覆盖（控件坐标未同步放大）。
    /// 因此改用显式缩放：AutoScaleMode 设为 None，所有尺寸经 S() 换算，
    /// 逻辑清晰且可逐项验证。
    /// </summary>
    internal class SettingsForm : Form
    {
        private readonly Config _config;
        private readonly TriggerEngine _engine;
        private readonly TrayApp _app;

        /// <summary>DPI 缩放系数（96 DPI 时为 1.0，150% 缩放时为 1.5）。</summary>
        private readonly float _scale = 1f;

        private RadioButton _modeThreshold;
        private RadioButton _modeInterval;
        private NumericUpDown _thresholdPercent;
        private NumericUpDown _standbyThresholdMB;
        private NumericUpDown _intervalSeconds;
        private NumericUpDown _cooldownSeconds;
        private NumericUpDown _combineMinutes;

        private CheckBox _chkStandby;
        private CheckBox _chkLowPriority;
        private CheckBox _chkFileCache;
        private CheckBox _chkCombine;
        private CheckBox _chkCombineInterval;
        private CheckBox _chkCombineThreshold;
        private NumericUpDown _combineThresholdPercent;

        private CheckBox _chkAutoStart;
        private CheckBox _chkNotify;

        private Label _statusLabel;
        private TextBox _logBox;

        // ---- 96 DPI 下的设计尺寸 ----
        // 高度须容纳所有内容。逐项累加的实际结果：
        //   模式组 12..170 → 清理组 178..352 → 其它组 360..440
        //   → 状态 448..514 → 日志 520..600 → 按钮 608..636
        // 故设计高度不得低于 636。取 648 留出 12px 底部留白。
        // 窗口总高 = 648*1.5 + 标题栏 23 + 边框 8 ≈ 1003 < 工作区 1027，可完整显示。
        //
        // 注意：曾把此值压到 620，导致按钮行被窗口底边裁掉（只露出上半截）。
        // 压缩高度前务必按上面的累加方式复核。
        private const int FormW = 560;
        private const int FormH = 648;
        private const int Margin_ = 12;
        private const int GroupW = FormW - Margin_ * 2;   // 536

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

        private const int LOGPIXELSX = 88;
        private const int DESKTOPHORZRES = 118;   // 物理桌面宽度
        private const int HORZRES = 8;            // 逻辑桌面宽度

        /// <summary>
        /// 取系统 DPI 缩放系数（96 DPI 为 1.0，150% 缩放为 1.5）。
        ///
        /// 【实测数据】本机 AppliedDPI=144（150%），同一段代码在不同 DPI 感知状态下结果相反：
        ///
        ///   获取方式                    非感知进程    感知进程（本程序）
        ///   GetDeviceCaps(LOGPIXELSX)      96 ✗        144 ✓
        ///   DESKTOPHORZRES / HORZRES      1.500 ✓      1.000 ✗
        ///   CreateGraphics().DpiX          96 ✗        144 ✓
        ///
        /// 原因：非感知进程会被 DPI 虚拟化，LOGPIXELSX 恒报 96，而物理/逻辑宽度
        /// 反映了虚拟化比例；感知进程则相反——LOGPIXELSX 为真实值，但 HORZRES
        /// 也变成物理宽度，两者比值恒为 1。
        ///
        /// 本程序在清单中声明了 dpiAware，属感知进程，因此以 LOGPIXELSX 为准，
        /// 并保留比值法作为非感知情形（如清单未生效）的回退。
        /// </summary>
        private static float GetSystemScale()
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return 1f;

                // 首选：真实 DPI（DPI 感知进程下有效）
                int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                if (dpi > 0 && dpi != 96)
                {
                    float s = dpi / 96f;
                    if (s >= 0.5f && s <= 4f) return s;
                }

                // 次选：物理/逻辑宽度比（非感知进程下有效）
                int physical = GetDeviceCaps(hdc, DESKTOPHORZRES);
                int logical = GetDeviceCaps(hdc, HORZRES);
                if (physical > 0 && logical > 0 && physical != logical)
                {
                    float ratio = (float)physical / logical;
                    if (ratio >= 0.5f && ratio <= 4f) return ratio;
                }

                // 两者一致时按 100% 处理
                return 1f;
            }
            catch
            {
                return 1f;
            }
            finally
            {
                if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
            }
        }

        public SettingsForm(Config config, TriggerEngine engine, TrayApp app)
        {
            _config = config;
            _engine = engine;
            _app = app;

            _scale = GetSystemScale();

            // 关闭 WinForms 自动缩放，改由本类显式换算，避免与手动 Font 叠加导致错位
            AutoScaleMode = AutoScaleMode.None;

            Text = "内存回收 — 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(S(FormW), S(FormH));

            BuildUi();
            LoadFromConfig();
            RefreshStatus();

            _engine.StateUpdated += OnState;
        }

        /// <summary>把 96 DPI 设计值换算为当前 DPI 下的像素值。</summary>
        private int S(int value)
        {
            return (int)Math.Round(value * _scale);
        }

        private Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        private Size Sz(int w, int h) { return new Size(S(w), S(h)); }

        private void BuildUi()
        {
            int y = Margin_;

            // ================= 自动清理模式 =================
            // 每一行独立成行，不再把多个控件塞进同一行，避免文字变宽后互相覆盖
            GroupBox grpMode = new GroupBox();
            grpMode.Text = "自动清理模式";
            grpMode.Location = Pt(Margin_, y);
            grpMode.Size = Sz(GroupW, 158);
            Controls.Add(grpMode);

            _modeThreshold = new RadioButton();
            _modeThreshold.Text = "阈值触发：内存紧张时才清理（推荐）";
            _modeThreshold.Location = Pt(14, 24);
            _modeThreshold.AutoSize = true;
            _modeThreshold.CheckedChanged += delegate { SyncEnabled(); };
            grpMode.Controls.Add(_modeThreshold);

            AddLabeledRow(grpMode, 30, 50, "可用内存低于", out _thresholdPercent, 140, 60, "% 时清理");
            _thresholdPercent.Minimum = 1;
            _thresholdPercent.Maximum = 90;

            AddLabeledRow(grpMode, 30, 76, "待机列表超过", out _standbyThresholdMB, 140, 70, "MB 时清理（0=不启用该条件）");
            _standbyThresholdMB.Minimum = 0;
            _standbyThresholdMB.Maximum = 1000000;
            _standbyThresholdMB.Increment = 128;

            _modeInterval = new RadioButton();
            _modeInterval.Text = "固定周期：每隔";
            _modeInterval.Location = Pt(14, 104);
            _modeInterval.AutoSize = true;
            _modeInterval.CheckedChanged += delegate { SyncEnabled(); };
            grpMode.Controls.Add(_modeInterval);

            _intervalSeconds = new NumericUpDown();
            _intervalSeconds.Location = Pt(140, 102);
            _intervalSeconds.Size = Sz(70, 23);
            _intervalSeconds.Minimum = 0;
            _intervalSeconds.Maximum = 86400;
            grpMode.Controls.Add(_intervalSeconds);

            Label lblI2 = new Label();
            lblI2.Text = "秒清理一次（0=尽可能快，受冷却限制）";
            lblI2.Location = Pt(218, 105);
            lblI2.AutoSize = true;
            grpMode.Controls.Add(lblI2);

            Label lblCd = new Label();
            lblCd.Text = "两次清理最小间隔（冷却）：";
            lblCd.Location = Pt(34, 131);
            lblCd.AutoSize = true;
            grpMode.Controls.Add(lblCd);

            // 标签实际宽度约 165px，数值框须从 206 起，否则会重叠
            _cooldownSeconds = new NumericUpDown();
            _cooldownSeconds.Location = Pt(206, 128);
            _cooldownSeconds.Size = Sz(70, 23);
            _cooldownSeconds.Minimum = 0;
            _cooldownSeconds.Maximum = 86400;
            grpMode.Controls.Add(_cooldownSeconds);

            Label lblCd2 = new Label();
            lblCd2.Text = "秒（防止频繁清空缓存拖慢系统）";
            lblCd2.Location = Pt(284, 131);
            lblCd2.AutoSize = true;
            grpMode.Controls.Add(lblCd2);

            y += 166;

            // ================= 清理项目 =================
            GroupBox grpItems = new GroupBox();
            grpItems.Text = "清理项目";
            grpItems.Location = Pt(Margin_, y);
            grpItems.Size = Sz(GroupW, 174);
            Controls.Add(grpItems);

            _chkStandby = new CheckBox();
            _chkStandby.Text = "待机列表 (Standby list)";
            _chkStandby.Location = Pt(14, 24);
            _chkStandby.AutoSize = true;
            grpItems.Controls.Add(_chkStandby);

            _chkLowPriority = new CheckBox();
            _chkLowPriority.Text = "待机列表（无优先级）(Standby list without priority)";
            _chkLowPriority.Location = Pt(14, 50);
            _chkLowPriority.AutoSize = true;
            grpItems.Controls.Add(_chkLowPriority);

            _chkFileCache = new CheckBox();
            _chkFileCache.Text = "系统文件缓存 (System file cache)";
            _chkFileCache.Location = Pt(14, 76);
            _chkFileCache.AutoSize = true;
            grpItems.Controls.Add(_chkFileCache);

            // ---- 合并内存列表：主开关 + 两种独立触发方式 ----
            _chkCombine = new CheckBox();
            _chkCombine.Text = "合并内存列表 (Combine memory lists)";
            _chkCombine.Location = Pt(14, 98);
            _chkCombine.AutoSize = true;
            _chkCombine.CheckedChanged += delegate { SyncEnabled(); };
            grpItems.Controls.Add(_chkCombine);

            // 定时方式
            _chkCombineInterval = new CheckBox();
            _chkCombineInterval.Text = "定时：每";
            _chkCombineInterval.Location = Pt(34, 122);
            _chkCombineInterval.AutoSize = true;
            _chkCombineInterval.CheckedChanged += delegate { SyncEnabled(); };
            grpItems.Controls.Add(_chkCombineInterval);

            _combineMinutes = new NumericUpDown();
            _combineMinutes.Location = Pt(148, 119);
            _combineMinutes.Size = Sz(66, 23);
            _combineMinutes.Minimum = 0;
            _combineMinutes.Maximum = 1440;
            grpItems.Controls.Add(_combineMinutes);

            Label lblMin = new Label();
            lblMin.Text = "分钟（0=不定时）";
            lblMin.Location = Pt(222, 122);
            lblMin.AutoSize = true;
            grpItems.Controls.Add(lblMin);

            // 阈值方式
            _chkCombineThreshold = new CheckBox();
            _chkCombineThreshold.Text = "阈值：内存占用超过";
            _chkCombineThreshold.Location = Pt(34, 146);
            _chkCombineThreshold.AutoSize = true;
            _chkCombineThreshold.CheckedChanged += delegate { SyncEnabled(); };
            grpItems.Controls.Add(_chkCombineThreshold);

            _combineThresholdPercent = new NumericUpDown();
            _combineThresholdPercent.Location = Pt(184, 143);
            _combineThresholdPercent.Size = Sz(66, 23);
            _combineThresholdPercent.Minimum = 1;
            _combineThresholdPercent.Maximum = 99;
            grpItems.Controls.Add(_combineThresholdPercent);

            Label lblPct = new Label();
            lblPct.Text = "% 时合并";
            lblPct.Location = Pt(258, 146);
            lblPct.AutoSize = true;
            grpItems.Controls.Add(lblPct);

            y += 182;

            // ================= 其它 =================
            GroupBox grpOther = new GroupBox();
            grpOther.Text = "其它";
            grpOther.Location = Pt(Margin_, y);
            grpOther.Size = Sz(GroupW, 88);
            Controls.Add(grpOther);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "开机自动启动";
            _chkAutoStart.Location = Pt(14, 22);
            _chkAutoStart.AutoSize = true;
            grpOther.Controls.Add(_chkAutoStart);

            _chkNotify = new CheckBox();
            _chkNotify.Text = "清理成功后显示气泡通知（默认关闭；失败时始终提示）";
            _chkNotify.Location = Pt(14, 46);
            _chkNotify.AutoSize = true;
            grpOther.Controls.Add(_chkNotify);

            y += 96;

            // ================= 状态 =================
            // 固定 3 行高度：内容行 + 上次清理行 + 警告行，避免被下方文本框盖住
            _statusLabel = new Label();
            _statusLabel.Location = Pt(Margin_, y);
            _statusLabel.Size = Sz(GroupW, 66);
            _statusLabel.AutoSize = false;
            Controls.Add(_statusLabel);

            y += 72;

            // ================= 日志 =================
            _logBox = new TextBox();
            _logBox.Location = Pt(Margin_, y);
            _logBox.Size = Sz(GroupW, 80);
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.BackColor = SystemColors.Window;
            _logBox.WordWrap = true;
            Controls.Add(_logBox);

            y += 88;

            // ================= 按钮 =================
            Button btnTest = new Button();
            btnTest.Text = "检查权限";
            btnTest.Location = Pt(Margin_, y);
            btnTest.Size = Sz(88, 28);
            btnTest.Click += delegate { RunSelfTest(); };
            Controls.Add(btnTest);

            Button btnClean = new Button();
            btnClean.Text = "立即清理";
            btnClean.Location = Pt(Margin_ + 96, y);
            btnClean.Size = Sz(88, 28);
            btnClean.Click += delegate { ManualClean(); };
            Controls.Add(btnClean);

            // 单独的手动合并按钮：合并开销高于其他清理项，
            // 且与「立即清理」用途不同（后者做全套），故独立成键。
            Button btnCombine = new Button();
            btnCombine.Text = "合并内存页";
            btnCombine.Location = Pt(Margin_ + 192, y);
            btnCombine.Size = Sz(96, 28);
            btnCombine.Click += delegate { ManualCombine(); };
            Controls.Add(btnCombine);

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = Pt(FormW - Margin_ - 188, y);
            btnSave.Size = Sz(88, 28);
            btnSave.Click += delegate { SaveAndClose(); };
            Controls.Add(btnSave);

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = Pt(FormW - Margin_ - 92, y);
            btnCancel.Size = Sz(88, 28);
            btnCancel.Click += delegate { Close(); };
            Controls.Add(btnCancel);
        }

        /// <summary>
        /// 生成「标签 + 数值框 + 标签」一行。
        /// 抽出来是为了三处保持一致间距，也便于按 DPI 统一换算。
        /// </summary>
        private void AddLabeledRow(Control parent, int x, int y, string label1,
            out NumericUpDown spinner, int spinnerX, int spinnerW, string label2)
        {
            Label l1 = new Label();
            l1.Text = label1;
            l1.Location = Pt(x, y + 3);
            l1.AutoSize = true;
            parent.Controls.Add(l1);

            spinner = new NumericUpDown();
            spinner.Location = Pt(spinnerX, y);
            spinner.Size = Sz(spinnerW, 23);
            parent.Controls.Add(spinner);

            Label l2 = new Label();
            l2.Text = label2;
            l2.Location = Pt(spinnerX + spinnerW + 8, y + 3);
            l2.AutoSize = true;
            parent.Controls.Add(l2);
        }

        private void LoadFromConfig()
        {
            _modeThreshold.Checked = _config.Mode == TriggerMode.Threshold;
            _modeInterval.Checked = _config.Mode == TriggerMode.Interval;
            _thresholdPercent.Value = Clamp(_config.AvailableMemoryThresholdPercent, _thresholdPercent);
            _standbyThresholdMB.Value = Clamp(_config.StandbyListThresholdMB, _standbyThresholdMB);
            _intervalSeconds.Value = Clamp(_config.IntervalSeconds, _intervalSeconds);
            _cooldownSeconds.Value = Clamp(_config.CooldownSeconds, _cooldownSeconds);
            _combineMinutes.Value = Clamp(_config.CombineIntervalMinutes, _combineMinutes);

            _chkStandby.Checked = _config.CleanStandbyList;
            _chkLowPriority.Checked = _config.CleanLowPriorityStandbyList;
            _chkFileCache.Checked = _config.CleanSystemFileCache;
            _chkCombine.Checked = _config.CombineMemoryLists;

            _chkCombineInterval.Checked = _config.CombineUseInterval;
            _chkCombineThreshold.Checked = _config.CombineUseThreshold;
            _combineThresholdPercent.Value = Clamp(
                _config.CombineMemoryThresholdPercent, _combineThresholdPercent);

            _chkAutoStart.Checked = _config.AutoStart;
            _chkNotify.Checked = _config.ShowNotifications;

            SyncEnabled();
        }

        private static decimal Clamp(int value, NumericUpDown ctl)
        {
            if (value < ctl.Minimum) return ctl.Minimum;
            if (value > ctl.Maximum) return ctl.Maximum;
            return value;
        }

        /// <summary>
        /// 按当前勾选状态启用/禁用相关输入框，避免用户误以为改了就生效。
        /// </summary>
        private void SyncEnabled()
        {
            bool threshold = _modeThreshold.Checked;
            _thresholdPercent.Enabled = threshold;
            _standbyThresholdMB.Enabled = threshold;
            _intervalSeconds.Enabled = !threshold;
            _cooldownSeconds.Enabled = true;

            // 合并的两级联动：主开关关 → 触发方式全部不可用
            _chkCombineInterval.Enabled = _chkCombine.Checked;
            _chkCombineThreshold.Enabled = _chkCombine.Checked;
            _combineMinutes.Enabled = _chkCombine.Checked && _chkCombineInterval.Checked;
            _combineThresholdPercent.Enabled = _chkCombine.Checked && _chkCombineThreshold.Checked;
        }

        private void OnState(MemoryState st)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) { BeginInvoke(new Action<MemoryState>(OnState), st); return; }
                RefreshStatus(st);
            }
            catch { }
        }

        private void RefreshStatus() { RefreshStatus(MemoryStateReader.Read()); }

        /// <summary>
        /// 状态显示。控制在 3 行以内——标签高度按 3 行分配，
        /// 多出的内容会被下方控件遮住，因此这里主动精简而不是任其增长。
        /// </summary>
        private void RefreshStatus(MemoryState st)
        {
            StringBuilder sb = new StringBuilder();

            if (st.Valid)
            {
                sb.AppendLine("可用 " + MemoryState.FormatBytes((long)st.AvailableBytes) +
                              " / " + MemoryState.FormatBytes((long)st.TotalBytes) +
                              "（" + st.AvailablePercent.ToString("F1") + "%）" +
                              "    待机 " + MemoryState.FormatBytes(st.StandbyBytesTotal) +
                              "    已修改 " + MemoryState.FormatBytes(st.ModifiedBytes));
            }
            else
            {
                sb.AppendLine("无法读取内存列表状态。");
            }

            // 第二行：上次清理时间，以及周期模式下的实际生效间隔
            string line2 = "上次清理：" + (_engine.LastCleanTime == DateTime.MinValue
                ? "尚未执行"
                : _engine.LastCleanTime.ToString("HH:mm:ss"));

            if (_modeInterval.Checked)
            {
                int effective = _config.IntervalSeconds;
                if (_config.CooldownSeconds > effective) effective = _config.CooldownSeconds;
                line2 += "    实际间隔：" + effective + " 秒";
                if (effective != _config.IntervalSeconds)
                    line2 += "（受冷却 " + _config.CooldownSeconds + " 秒限制）";
            }

            // 只要合并开启了任一自动触发方式，就显示上次合并时间
            if (_config.CombineMemoryLists &&
                (_config.CombineUseInterval || _config.CombineUseThreshold))
            {
                line2 += "    上次合并：" + (_engine.LastCombineTime == DateTime.MinValue
                    ? "尚未执行"
                    : _engine.LastCombineTime.ToString("HH:mm:ss"));
            }

            sb.Append(line2);

            if (!_app.PrivilegeOk)
            {
                sb.AppendLine();
                sb.Append("⚠ 特权不足，清理会失败（点「检查权限」查看原因）");
                _statusLabel.ForeColor = Color.Firebrick;
            }
            else
            {
                _statusLabel.ForeColor = SystemColors.ControlText;
            }

            _statusLabel.Text = sb.ToString();
        }

        /// <summary>
        /// 只检查特权可用性，不执行任何清理动作——
        /// 该按钮若顺带清理，用户点「检查权限」就会意外清空磁盘缓存。
        /// 需要实际清理请用「立即清理」。
        /// </summary>
        private void RunSelfTest()
        {
            var p1 = Privileges.Enable(Privileges.SeProfileSingleProcess);
            var p2 = Privileges.Enable(Privileges.SeIncreaseQuota);
            IntegrityLevel level = IntegrityCheck.GetCurrentLevel();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("完整性级别：" + IntegrityCheck.LevelName(level));
            sb.AppendLine("管理员权限：" + (SelfTest.IsElevated() ? "是" : "否"));
            sb.AppendLine();

            sb.AppendLine("特权 " + p1.Name + "：" + (p1.Enabled ? "可用" : "不可用"));
            if (!p1.Enabled) sb.AppendLine("    " + p1.Detail);
            sb.AppendLine("特权 " + p2.Name + "：" + (p2.Enabled ? "可用" : "不可用"));
            if (!p2.Enabled) sb.AppendLine("    " + p2.Detail);

            if (!p1.Enabled || !p2.Enabled)
            {
                sb.AppendLine();
                string advice = IntegrityCheck.BuildAdvice(
                    level, SelfTest.IsElevated(), Application.ExecutablePath);
                sb.AppendLine(advice ?? "完整性级别与管理员权限均正常，但特权仍无法启用，原因未确定。");
            }

            // 网络标记：与权限无关，但同样影响「双击能不能直接打开」
            sb.AppendLine();
            MotwHelper.Outcome mo = MotwHelper.StartupOutcome;
            if (mo != null)
            {
                sb.AppendLine("网络标记：" + mo.ToString());
                if (mo.Result == MotwHelper.MotwResult.Removed)
                    sb.AppendLine("    首次运行需点一次「运行」，此后双击不再弹安全警告");
            }
            else
            {
                sb.AppendLine("网络标记：启动时未检查");
            }
            int zoneNow2;
            bool hasZoneNow = MotwHelper.ZoneStreamExists(Application.ExecutablePath);
            zoneNow2 = hasZoneNow ? MotwHelper.ReadZoneId(Application.ExecutablePath) : -1;
            sb.AppendLine("    当前 Zone.Identifier：" + (hasZoneNow
                ? "存在" + (zoneNow2 >= 0 ? "（ZoneId=" + zoneNow2 + "）" : "（内容无法解析）")
                : "不存在"));

            _logBox.Text = sb.ToString();
            RefreshStatus();
        }

        private void ManualClean()
        {
            Cursor = Cursors.WaitCursor;
            List<CleanResult> results;
            try
            {
                results = _engine.CleanNow(true);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("手动清理");
            sb.AppendLine("时间：" + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine();

            long freed = 0;
            foreach (CleanResult r in results)
            {
                sb.AppendLine(r.ToString());
                freed += r.BytesFreed;
            }
            sb.AppendLine("合计释放（估算）：" + MemoryState.FormatBytes(freed));
            _logBox.Text = sb.ToString();
            RefreshStatus();
        }

        /// <summary>
        /// 手动执行内存页面合并。
        ///
        /// 与「立即清理」的区别：这里只做合并这一项，不触发其他清理。
        /// 也不受「清理项目」中合并复选框的约束——用户既然点了这个按钮，
        /// 意图就是执行合并，与是否纳入自动策略无关。
        /// </summary>
        private void ManualCombine()
        {
            // 合并需要 SeProfileSingleProcessPrivilege，先确保已启用
            var priv = Privileges.Enable(Privileges.SeProfileSingleProcess);
            if (!priv.Enabled)
            {
                IntegrityLevel lvl = IntegrityCheck.GetCurrentLevel();
                StringBuilder warn = new StringBuilder();
                warn.AppendLine("无法执行合并：缺少所需特权。");
                warn.AppendLine();
                warn.AppendLine("特权：" + priv.Name);
                warn.AppendLine("详情：" + priv.Detail);
                warn.AppendLine();
                warn.AppendLine(IntegrityCheck.BuildAdvice(
                    lvl, SelfTest.IsElevated(), Application.ExecutablePath)
                    ?? "完整性级别与管理员权限均正常，但特权仍无法启用，原因未确定。");
                _logBox.Text = warn.ToString();
                RefreshStatus();
                return;
            }

            Cursor = Cursors.WaitCursor;
            CleanResult r;
            try
            {
                List<CleanResult> rs = _engine.CombineNow();
                r = (rs != null && rs.Count > 0) ? rs[0] : new CleanResult();
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("手动合并内存页");
            sb.AppendLine("时间：" + DateTime.Now.ToString("HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine(r.Success ? "结果：成功" : "结果：失败");
            sb.AppendLine("说明：" + r.Message);

            if (r.Success)
            {
                if (r.BytesFreed > 0)
                    sb.AppendLine("合并页数折算：" + MemoryState.FormatBytes(r.BytesFreed));
                else
                    sb.AppendLine("本次没有找到可合并的重复页面——这属正常情况，不是故障。");
            }

            _logBox.Text = sb.ToString();
            RefreshStatus();
        }

        private void SaveAndClose()
        {
            _config.Mode = _modeThreshold.Checked ? TriggerMode.Threshold : TriggerMode.Interval;
            _config.AvailableMemoryThresholdPercent = (int)_thresholdPercent.Value;
            _config.StandbyListThresholdMB = (int)_standbyThresholdMB.Value;
            _config.IntervalSeconds = (int)_intervalSeconds.Value;
            _config.CooldownSeconds = (int)_cooldownSeconds.Value;
            _config.CombineIntervalMinutes = (int)_combineMinutes.Value;

            _config.CleanStandbyList = _chkStandby.Checked;
            _config.CleanLowPriorityStandbyList = _chkLowPriority.Checked;
            _config.CleanSystemFileCache = _chkFileCache.Checked;
            _config.CombineMemoryLists = _chkCombine.Checked;

            _config.CombineUseInterval = _chkCombineInterval.Checked;
            _config.CombineUseThreshold = _chkCombineThreshold.Checked;
            _config.CombineMemoryThresholdPercent = (int)_combineThresholdPercent.Value;

            _config.ShowNotifications = _chkNotify.Checked;

            // 开机自启：写注册表 Run 项，不需要管理员权限，
            // 因此可以和其它设置一起在「保存」时统一生效。
            if (_chkAutoStart.Checked != _config.AutoStart)
            {
                _config.AutoStart = _chkAutoStart.Checked;
                AutoStart.Apply(_config.AutoStart);
            }

            _app.ApplyConfig();
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.StateUpdated -= OnState;
            base.OnFormClosed(e);
        }
    }
}
