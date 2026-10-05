using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MemReclaim
{
    /// <summary>
    /// 托盘常驻程序。
    /// 托盘图标用代码绘制，避免依赖外部资源文件，保证单 exe 可直接分发。
    /// </summary>
    internal class TrayApp : ApplicationContext
    {
        private readonly Config _config;
        private readonly NotifyIcon _icon;
        private readonly TriggerEngine _engine;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _autoItem;
        private SettingsForm _form;
        private bool _lastPrivilegeOk;
        private IntegrityLevel _integrityLevel = IntegrityLevel.Unknown;

        public TrayApp()
        {
            _config = Config.Load();
            _config.Elevated = SelfTest.IsElevated();

            // ---- 托盘菜单 ----
            ContextMenuStrip menu = new ContextMenuStrip();

            _statusItem = new ToolStripMenuItem("正在读取内存状态…");
            _statusItem.Enabled = false;
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem cleanItem = new ToolStripMenuItem("立即清理");
            cleanItem.Click += delegate { ManualClean(); };
            menu.Items.Add(cleanItem);

            _autoItem = new ToolStripMenuItem("启用自动清理");
            _autoItem.CheckOnClick = true;
            _autoItem.Checked = true;
            _autoItem.Click += delegate { ToggleAuto(); };
            menu.Items.Add(_autoItem);

            ToolStripMenuItem settingsItem = new ToolStripMenuItem("设置…");
            settingsItem.Click += delegate { ShowSettings(); };
            menu.Items.Add(settingsItem);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };
            menu.Items.Add(exitItem);

            _icon = new NotifyIcon();
            _icon.Icon = CreateIcon();
            _icon.Text = "内存回收";
            _icon.ContextMenuStrip = menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate { ShowSettings(); };

            // ---- 引擎 ----
            _engine = new TriggerEngine(_config);
            _engine.StateUpdated += OnStateUpdated;
            _engine.Cleaned += OnCleaned;

            // 启动时先启用特权并报告结果，缺权限要立刻让用户知道
            CheckPrivileges(true);

            _engine.Start();

            if (_config.ShowNotifications)
            {
                _icon.BalloonTipTitle = "内存回收已启动";
                _icon.BalloonTipText = _lastPrivilegeOk
                    ? "正在按设定策略自动回收内存。双击图标可修改设置。"
                    : "缺少必要特权，清理可能失败。请以管理员身份运行，详见设置窗口。";
                _icon.ShowBalloonTip(3000);
            }
        }

        /// <summary>
        /// 本机权限检查。启动时若权限不足，直接弹出可操作的原因说明，
        /// 而不是只报“缺特权”——后者会让人查错方向。
        /// </summary>
        private void CheckPrivileges(bool announce)
        {
            var p1 = Privileges.Enable(Privileges.SeProfileSingleProcess);
            var p2 = Privileges.Enable(Privileges.SeIncreaseQuota);
            _lastPrivilegeOk = p1.Enabled && p2.Enabled;
            _integrityLevel = IntegrityCheck.GetCurrentLevel();

            if (!_lastPrivilegeOk && announce)
            {
                StringBuilder sb = new StringBuilder();

                string advice = IntegrityCheck.BuildAdvice(
                    _integrityLevel, SelfTest.IsElevated(),
                    System.Windows.Forms.Application.ExecutablePath);

                if (advice != null)
                {
                    sb.AppendLine("程序缺少执行内存回收所需的特权，全部清理项都会失败。");
                    sb.AppendLine();
                    sb.AppendLine(advice);
                }
                else
                {
                    // 完整性级别正常却仍失败，如实说明未能确定原因，不硬编一个解释
                    sb.AppendLine("程序缺少执行内存回收所需的特权。");
                    sb.AppendLine();
                    sb.AppendLine("完整性级别：" + IntegrityCheck.LevelName(_integrityLevel) + "（正常）");
                    sb.AppendLine("管理员权限：" + (SelfTest.IsElevated() ? "是" : "否"));
                    sb.AppendLine();
                    sb.AppendLine("特权启用结果：");
                    sb.AppendLine("  " + p1.Name + "：" + p1.Detail);
                    sb.AppendLine("  " + p2.Name + "：" + p2.Detail);
                    sb.AppendLine();
                    sb.AppendLine("完整性级别与管理员权限均正常，但特权仍无法启用。");
                    sb.AppendLine("请运行 --selftest 查看完整诊断信息。");
                }

                MessageBox.Show(sb.ToString(), "内存回收 — 权限不足",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnStateUpdated(MemoryState st)
        {
            // 该回调来自定时器线程，而 NotifyIcon/菜单项属于 UI 线程，
            // 必须 marshal 回 UI 线程，否则可能抛跨线程异常或导致托盘图标异常。
            if (_icon == null) return;
            try
            {
                if (_icon.ContextMenuStrip != null && _icon.ContextMenuStrip.InvokeRequired)
                {
                    _icon.ContextMenuStrip.BeginInvoke(new Action<MemoryState>(OnStateUpdated), st);
                    return;
                }

                string text = st.Valid
                    ? "可用 " + MemoryState.FormatBytes((long)st.AvailableBytes) +
                      " (" + st.AvailablePercent.ToString("F1") + "%)\n" +
                      "待机 " + MemoryState.FormatBytes(st.StandbyBytesTotal)
                    : "状态读取失败";

                _statusItem.Text = text.Replace("\n", "   ");
                // NotifyIcon.Text 上限 63 字符，超长会抛异常
                string tip = "内存回收 — 可用 " + st.AvailablePercent.ToString("F1") + "%";
                if (tip.Length > 62) tip = tip.Substring(0, 62);
                _icon.Text = tip;
            }
            catch
            {
                // 界面更新失败不影响清理逻辑
            }
        }

        private void OnCleaned(List<CleanResult> results)
        {
            int ok = 0, fail = 0;
            long freed = 0;
            string firstFailure = null;
            StringBuilder sb = new StringBuilder();
            foreach (CleanResult r in results)
            {
                if (!r.Executed) continue;
                if (r.Success) ok++;
                else
                {
                    fail++;
                    if (firstFailure == null) firstFailure = r.Item + "：" + r.Message;
                }
                freed += r.BytesFreed;
                sb.AppendLine(r.ToString());
            }

            // 通知关闭时成功保持静默；但失败必须告知，
            // 否则清理一直失败而用户以为程序在正常工作。
            bool shouldNotify = _config.ShowNotifications || fail > 0;
            if (!shouldNotify) return;

            string title;
            string msg;
            if (fail == 0)
            {
                title = "清理完成";
                msg = (freed > 0 ? "释放约 " + MemoryState.FormatBytes(freed) + "\n" : "") + sb.ToString();
            }
            else
            {
                title = "清理有 " + fail + " 项失败";
                msg = firstFailure + "\n（双击图标 →「检查权限」查看原因）";
            }

            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = msg.Length > 250 ? msg.Substring(0, 250) : msg;
                _icon.ShowBalloonTip(fail == 0 ? 4000 : 8000);
            }
            catch { }
        }

        private void ManualClean()
        {
            CheckPrivileges(false);
            // Combine 手动执行时一并做，用户点了就期望全清一遍
            List<CleanResult> r = _engine.CleanNow(true);
            ShowResultDialog(r);
        }

        private void ShowResultDialog(List<CleanResult> results)
        {
            StringBuilder sb = new StringBuilder();
            long freed = 0;
            foreach (CleanResult r in results)
            {
                sb.AppendLine(r.ToString());
                freed += r.BytesFreed;
            }
            sb.AppendLine();
            sb.AppendLine("合计释放（估算）：" + MemoryState.FormatBytes(freed));

            MessageBox.Show(sb.ToString(), "内存回收 — 执行结果",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ToggleAuto()
        {
            if (_autoItem.Checked) _engine.Start();
            else _engine.Stop();
        }

        public void ShowSettings()
        {
            if (_form != null && !_form.IsDisposed)
            {
                _form.Activate();
                return;
            }
            _form = new SettingsForm(_config, _engine, this);
            _form.Show();
        }

        public void ApplyConfig()
        {
            _config.Normalize();
            _config.Save();
            CheckPrivileges(false);
        }

        /// <summary>
        /// 当前是否具备执行清理所需的特权。
        ///
        /// 这里实时查询而非返回缓存的 _lastPrivilegeOk：
        /// 该字段只在启动与保存设置时更新，用户在其它时机（或外部环境变化后）
        /// 权限状态可能与缓存不一致，导致界面显示与实际能力矛盾
        /// ——例如刚成功完成一次合并，状态栏却仍提示「特权不足」。
        ///
        /// 代价是每次读属性都会调用两次特权查询，但它们只是
        /// LookupPrivilegeValue + RtlAdjustPrivilege，开销可忽略。
        /// </summary>
        public bool PrivilegeOk
        {
            get
            {
                _lastPrivilegeOk = Privileges.Enable(Privileges.SeProfileSingleProcess).Enabled
                                && Privileges.Enable(Privileges.SeIncreaseQuota).Enabled;
                return _lastPrivilegeOk;
            }
        }

        private void ExitApp()
        {
            _engine.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            ExitThread();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// 用代码画一个简洁的内存图标，避免额外资源文件。
        /// 注意：Icon.FromHandle 不接管句柄所有权，必须自行 DestroyIcon，
        /// 因此这里克隆一份再释放原句柄，避免 GDI 句柄泄漏。
        /// </summary>
        private static Icon CreateIcon()
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        DrawMemoryIcon(g);
                    }
                    hIcon = bmp.GetHicon();
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();   // 克隆后即可安全销毁原句柄
                    }
                }
            }
            finally
            {
                if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            }
        }

        private static void DrawMemoryIcon(Graphics g)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 内存条外形
            using (SolidBrush body = new SolidBrush(Color.FromArgb(48, 120, 200)))
            using (Pen border = new Pen(Color.FromArgb(24, 70, 130), 2f))
            {
                g.FillRectangle(body, 3, 9, 26, 14);
                g.DrawRectangle(border, 3, 9, 26, 14);
            }

            // 金手指
            using (SolidBrush pins = new SolidBrush(Color.FromArgb(220, 180, 60)))
            {
                for (int i = 0; i < 5; i++) g.FillRectangle(pins, 6 + i * 5, 23, 3, 4);
            }

            // 芯片
            using (SolidBrush chip = new SolidBrush(Color.FromArgb(235, 240, 245)))
            {
                for (int i = 0; i < 3; i++) g.FillRectangle(chip, 6 + i * 8, 13, 5, 6);
            }
        }
    }
}
