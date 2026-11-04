using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MemReclaim
{
    /// <summary>
    /// 进程列表窗口：勾选要参与工作集修剪的进程，管理白名单。
    ///
    /// 【为什么单开一个窗口而不是塞进设置窗口】
    /// 进程列表是动态数据（数量随运行情况变化），需要刷新、排序、多选，
    /// 与设置项那种「一改就存」的静态表单性质不同。混在一起会让两者都难用，
    /// 也会把设置窗口撑到屏幕放不下。
    ///
    /// 【勾选语义】
    /// 复选框表示「显式勾选」——即使用户没开「自动纳入后台应用」，
    /// 被勾选的进程也会参与修剪。它按进程名保存（见 Config.TrimSelectedNames），
    /// 因此同名进程重启后仍保持勾选。
    ///
    /// 白名单与勾选是互斥的：把进程加入白名单会同时取消它的勾选。
    /// 否则会出现「勾了但又不修剪」的矛盾状态，用户无法从界面判断实际行为。
    /// </summary>
    internal class ProcessListForm : Form
    {
        private readonly Config _config;
        private readonly TriggerEngine _engine;

        private readonly float _scale = 1f;

        private ListView _list;
        private Button _btnRefresh;
        private Button _btnIncludeBackground;
        private Button _btnIncludeWindowsApps;
        private Button _btnWhitelist;
        private Button _btnUnwhitelist;
        private Button _btnTrimSelected;
        private Button _btnClose;
        private Button _btnClearSelection;
        private NumericUpDown _threadCount;
        private Label _statusLabel;
        private CheckBox _chkBackground;
        private CheckBox _chkWindowsProc;
        private CheckBox _chkSkipForeground;

        private List<ProcessEntry> _entries = new List<ProcessEntry>();

        /// <summary>按列排序状态：-1 表示未排序。</summary>
        private int _sortColumn = -1;
        private bool _sortAscending = true;

        /// <summary>程序化修改复选框时置位，阻止 ItemCheck 再回头写配置。</summary>
        private bool _updatingChecks;

        /// <summary>初始化期间置位，避免 CheckedChanged 触发不必要的列表操作。</summary>
        private bool _suppressAutoRefresh;

        /// <summary>状态行更新是否已排队，用于合并高频请求。</summary>
        private bool _statusPending;

        /// <summary>后台枚举是否进行中，避免重复刷新。</summary>
        private bool _refreshing;

        /// <summary>焦点监听定时器（兜底）：钩子失效或漏事件时仍能收敛。</summary>
        private System.Windows.Forms.Timer _fgTimer;

        /// <summary>周期同步定时器：重算分类与内存数值。</summary>
        private System.Windows.Forms.Timer _liveTimer;

        /// <summary>焦点变化事件的钩子句柄；IntPtr.Zero 表示安装失败。</summary>
        private IntPtr _winEventHook = IntPtr.Zero;

        /// <summary>
        /// 钩子回调的托管委托。必须用字段持有引用——
        /// 只传局部变量的话委托可能被 GC 回收，之后系统回调进来就是野指针。
        /// </summary>
        private Native.WinEventDelegate _winEventProc;

        /// <summary>上一次观察到的焦点 PID，用于判断是否发生切换。</summary>
        private int _lastFgPid = -1;

        private const int FormW = 860;
        private const int FormH = 560;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
        private const int LOGPIXELSX = 88;

        private static float GetSystemScale()
        {
            IntPtr hdc = IntPtr.Zero;
            try
            {
                hdc = GetDC(IntPtr.Zero);
                if (hdc == IntPtr.Zero) return 1f;
                int dpi = GetDeviceCaps(hdc, LOGPIXELSX);
                if (dpi > 0 && dpi != 96)
                {
                    float s = dpi / 96f;
                    if (s >= 0.5f && s <= 4f) return s;
                }
                return 1f;
            }
            catch { return 1f; }
            finally { if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc); }
        }

        private int S(int v) { return (int)Math.Round(v * _scale); }
        private Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        private Size Sz(int w, int h) { return new Size(S(w), S(h)); }

        public ProcessListForm(Config config, TriggerEngine engine)
        {
            _config = config;
            _engine = engine;
            _scale = GetSystemScale();

            AutoScaleMode = AutoScaleMode.None;
            Text = "内存回收 — 进程与工作集修剪";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9f);
            ClientSize = new Size(S(FormW), S(FormH));
            MinimumSize = new Size(S(640), S(420));

            BuildUi();
            LoadSettings();
            RefreshList();

            _engine.Trimmed += OnTrimmed;
            _engine.TrimProgress += OnTrimProgress;

            // 焦点监听：用户切换窗口后立即更新前台保护状态。
            //
            // 主通道是 SetWinEventHook(EVENT_SYSTEM_FOREGROUND)：事件驱动，
            // 切换瞬间就回调，没有轮询延迟。定时器只作兜底（钩子安装失败时
            // 才回到高频轮询），避免个别场景漏事件后状态永久错位。
            _lastFgPid = ProcessEnumerator.GetForegroundPid();
            InstallForegroundHook();

            _fgTimer = new System.Windows.Forms.Timer();
            _fgTimer.Interval = _winEventHook != IntPtr.Zero ? 5000 : 300;
            _fgTimer.Tick += delegate { OnForegroundTick(); };
            _fgTimer.Start();

            // 周期同步：钩子只解决「焦点变了」，但窗口的出现/消失会改变分类，
            // 工作集也在持续变化。这两样都需要定时重算，否则列表看起来是静态的。
            _liveTimer = new System.Windows.Forms.Timer();
            _liveTimer.Interval = 1500;
            _liveTimer.Tick += delegate { OnLiveTick(); };
            _liveTimer.Start();
        }

        private void OnLiveTick()
        {
            if (IsDisposed) return;
            if (_refreshing) return;        // 正在全量刷新，跳过一次
            try { SyncLiveState(); } catch { }
        }

        /// <summary>
        /// 安装前台窗口变化钩子。成功返回后，用户每次切换窗口都会触发回调，
        /// 响应时间取决于事件派发而非定时器间隔。
        /// </summary>
        private void InstallForegroundHook()
        {
            try
            {
                _winEventProc = delegate (IntPtr hHook, uint evt, IntPtr hwnd,
                    int idObject, int idChild, uint thread, uint time)
                {
                    if (IsDisposed) return;
                    try
                    {
                        // 回调在 UI 线程的消息循环里执行，但仍投递一次，
                        // 避免在系统事件分发过程中做较重的界面更新
                        BeginInvoke(new Action(OnForegroundEvent));
                    }
                    catch { }
                };

                _winEventHook = Native.SetWinEventHook(
                    Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero, _winEventProc, 0, 0,
                    Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
            }
            catch
            {
                _winEventHook = IntPtr.Zero;
            }
        }

        /// <summary>钩子回调：焦点已切换，立即重算。</summary>
        private void OnForegroundEvent()
        {
            if (IsDisposed) return;
            try
            {
                int pid = ProcessEnumerator.GetForegroundPid();
                if (pid == _lastFgPid) return;
                _lastFgPid = pid;
                RefreshForegroundState();
            }
            catch { }
        }

        /// <summary>兜底轮询：只做一次 PID 比较，没有变化就直接返回。</summary>
        private void OnForegroundTick()
        {
            if (IsDisposed) return;
            try
            {
                int pid = ProcessEnumerator.GetForegroundPid();
                if (pid == _lastFgPid) return;
                _lastFgPid = pid;
                RefreshForegroundState();
            }
            catch { }
        }

        /// <summary>
        /// 按当前焦点重算前台保护状态，并取消已切到前台的进程的勾选。
        ///
        /// 只操作已加载的 _entries（不重新枚举），因此开销很小，可以每秒跑。
        /// 若焦点进程不在列表里（刚启动的新程序），才触发一次全量刷新。
        /// </summary>
        private void RefreshForegroundState()
        {
            int fgPid = _lastFgPid;
            if (fgPid <= 0) return;

            // 焦点落在本程序自己的窗口上：说明用户正在看这个列表，
            // 此时没有外部程序处于交互状态，清空标记即可。
            // 不特殊处理的话会被当成「未知进程」而触发一次全量刷新。
            if (fgPid == System.Diagnostics.Process.GetCurrentProcess().Id)
            {
                bool anyMark = false;
                foreach (ProcessEntry e in _entries) if (e.IsForegroundActive) { anyMark = true; break; }
                if (!anyMark) return;

                foreach (ProcessEntry e in _entries) e.IsForegroundActive = false;
                ApplyAllStates();
                return;
            }

            string fgPath = null;
            bool found = false;
            foreach (ProcessEntry e in _entries)
            {
                if (e.Pid == fgPid) { fgPath = e.Path; found = true; break; }
            }

            if (!found)
            {
                // 焦点进程不在当前列表（新启动的），需要重新枚举
                if (!_refreshing) RefreshListAsync();
                return;
            }

            bool changed = false;
            foreach (ProcessEntry e in _entries)
            {
                bool want = (e.Pid == fgPid) ||
                    (!string.IsNullOrEmpty(fgPath) &&
                     string.Equals(e.Path, fgPath, StringComparison.OrdinalIgnoreCase));

                if (e.IsForegroundActive != want)
                {
                    e.IsForegroundActive = want;
                    changed = true;
                }
            }

            if (!changed) return;

            int dropped = DropForegroundSelections();
            ApplyAllStates();

            if (dropped > 0)
            {
                _statusLabel.Text = "已自动取消 " + dropped +
                    " 个切到前台的进程的勾选（正在使用的程序不修剪）。";
            }
        }

        /// <summary>
        /// 实时同步：重算窗口状态与分类、刷新内存数值，并把变化的行更新到界面。
        ///
        /// 【为什么要单独做这个】
        /// 钩子只负责「焦点变了」，但界面还依赖两样会变的东西：
        ///   - 程序窗口的出现/消失会改变「前台应用 / 后台应用」分类；
        ///   - 工作集/私有内存在持续变化。
        /// 之前这两项只在点「刷新」时重算，所以列表看起来是静态的。
        ///
        /// 【为什么不直接 RefreshList】
        /// 那会重新枚举全部进程（每个都要开句柄查令牌与包标识），耗时上百毫秒，
        /// 且 Clear + 重建会丢选中状态、肉眼可见地闪。这里复用已有的
        /// PID/路径数据，只重算窗口相关状态并改变化的单元格。
        /// </summary>
        private void SyncLiveState()
        {
            if (_entries.Count == 0) return;

            // 窗口状态 + 分类（EnumWindows 约 1–3 ms）
            ProcessEnumerator.Reclassify(_entries);

            // 内存数值：只更新仍在运行的进程
            HashSet<int> alivePids = new HashSet<int>();
            foreach (ProcessEntry e in _entries)
            {
                long ws = ProcessEnumerator.GetWorkingSet(e.Pid);
                if (ws >= 0)
                {
                    e.WorkingSet = ws;
                    alivePids.Add(e.Pid);
                }
            }

            UpdateVisibleRows();
        }

        /// <summary>
        /// 把 _entries 的当前状态写回 ListView，只改真正变化的单元格。
        ///
        /// 逐个判断再赋值是必要的：直接全量赋值每秒会让两百行重绘，
        /// 即使有双缓冲也会看到明显的滚动条跳动和文字闪烁。
        /// </summary>
        private void UpdateVisibleRows()
        {
            HashSet<string> whitelist = _config.GetWhitelistSet();
            HashSet<string> selected = _config.GetSelectedSet();

            _updatingChecks = true;
            _list.BeginUpdate();
            try
            {
                foreach (ListViewItem item in _list.Items)
                {
                    ProcessEntry e = item.Tag as ProcessEntry;
                    if (e == null) continue;

                    string name = ProcessEnumerator.StripExe(e.Name);
                    bool whitelisted = whitelist.Contains(name);
                    bool critical = e.IsProtected;

                    // 分类列
                    string kind = KindText(e.Kind);
                    if (item.SubItems[1].Text != kind) item.SubItems[1].Text = kind;

                    // 工作集 / 私有内存
                    string ws = MemoryState.FormatBytes(e.WorkingSet);
                    if (item.SubItems[4].Text != ws) item.SubItems[4].Text = ws;
                    string pv = MemoryState.FormatBytes(e.PrivateBytes);
                    if (item.SubItems[5].Text != pv) item.SubItems[5].Text = pv;

                    // 状态列
                    string status = StatusText(e, whitelisted, selected.Contains(name));
                    if (item.SubItems[7].Text != status) item.SubItems[7].Text = status;

                    // 复选框
                    bool canCheck = !whitelisted && !critical;
                    bool want = canCheck && (selected.Contains(name) ||
                        (e.Kind == ProcessKind.Background && _config.TrimBackgroundApps) ||
                        (e.Kind == ProcessKind.WindowsProcess && _config.TrimWindowsProcesses));
                    if (item.Checked != want) item.Checked = want;

                    // 颜色
                    Color wantColor = whitelisted ? Color.Gray
                                    : critical ? Color.Firebrick
                                    : e.Kind == ProcessKind.WindowsProcess ? Color.DimGray
                                    : SystemColors.ControlText;
                    if (item.ForeColor != wantColor) item.ForeColor = wantColor;
                }
            }
            finally
            {
                _list.EndUpdate();
                _updatingChecks = false;
            }

            UpdateStatusLine();
        }

        private void BuildUi()
        {
            int margin = 10;
            int y = margin;

            // ---- 顶部：一键纳入 + 线程数 ----
            // 位置一律用前一个控件的 Right 递推，而不是写死 x 坐标。
            // 写死坐标在中文/不同 DPI 下会算错文本宽度——之前「并行线程数：」
            // 的标签宽 84、数字框却放在 82 处，数字框左缘被标签压住，
            // 表现为数字只剩半个。
            _chkBackground = new CheckBox();
            _chkBackground.Text = "自动纳入后台应用";
            _chkBackground.AutoSize = true;
            _chkBackground.CheckedChanged += delegate
            {
                if (_suppressAutoRefresh) return;
                _config.TrimBackgroundApps = _chkBackground.Checked;
                ApplyAllStates();
            };
            Controls.Add(_chkBackground);
            _chkBackground.Location = new Point(S(margin), S(y + 2));

            _chkWindowsProc = new CheckBox();
            _chkWindowsProc.Text = "自动纳入 Windows 进程";
            _chkWindowsProc.AutoSize = true;
            _chkWindowsProc.CheckedChanged += delegate
            {
                if (_suppressAutoRefresh) return;
                _config.TrimWindowsProcesses = _chkWindowsProc.Checked;
                ApplyAllStates();
            };
            Controls.Add(_chkWindowsProc);
            _chkWindowsProc.Location = new Point(_chkBackground.Right + S(16), S(y + 2));

            Label lblThreads = new Label();
            lblThreads.Text = "并行线程数：";
            lblThreads.AutoSize = true;
            Controls.Add(lblThreads);
            lblThreads.Location = new Point(_chkWindowsProc.Right + S(16), S(y + 4));

            _threadCount = new NumericUpDown();
            _threadCount.Minimum = 1;
            _threadCount.Maximum = 32;
            _threadCount.Size = Sz(56, 23);
            _threadCount.ValueChanged += delegate
            {
                _config.TrimThreadCount = (int)_threadCount.Value;
            };
            Controls.Add(_threadCount);
            // 与标签留 4px 间隙，避免数字框边框压在「：」上
            _threadCount.Location = new Point(lblThreads.Right + S(4), S(y));

            // 前台保护开关。放在这里而不是设置窗口：设置窗口该组已排满，
            // 而这里是用户勾选进程的地方，开关与勾选行为直接相关。
            _chkSkipForeground = new CheckBox();
            _chkSkipForeground.Text = "跳过当前前台程序";
            _chkSkipForeground.AutoSize = true;
            _chkSkipForeground.CheckedChanged += delegate
            {
                if (_suppressAutoRefresh) return;
                _config.TrimSkipForeground = _chkSkipForeground.Checked;
                ApplyAllStates();
            };
            Controls.Add(_chkSkipForeground);
            _chkSkipForeground.Location = new Point(_threadCount.Right + S(16), S(y + 2));

            y += 32;

            // ---- 进程列表 ----
            _list = new ListView();
            _list.Location = Pt(margin, y);
            _list.Size = Sz(FormW - margin * 2, FormH - y - 96);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _list.View = View.Details;
            _list.CheckBoxes = true;
            _list.FullRowSelect = true;
            _list.GridLines = false;
            _list.HideSelection = false;
            _list.MultiSelect = true;

            // 开启双缓冲。ListView 默认不双缓冲，重建/排序时会整块重绘，
            // 表现为白底一闪。这是「刷新闪烁」的直接原因之一。
            try
            {
                System.Reflection.PropertyInfo dbl = typeof(Control).GetProperty(
                    "DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (dbl != null) dbl.SetValue(_list, true, null);
            }
            catch { }

            // 列宽之和须小于「ListView 宽度 − 垂直滚动条」。
            // 150% 缩放下 ListView 宽 1260px、滚动条 17px，可用 1243px = 828 设计px；
            // 之前合计 833 超了 5px，于是底部多出一条横向滚动条。
            // 当前合计 800，留出余量。
            // 「状态」列需要容纳「自动纳入（后台）」「自动纳入（UWP）」等最长的文案，
            // 给 120 才不会被省略号截断。
            _list.Columns.Add("进程名称", S(144));
            _list.Columns.Add("分类", S(106));
            _list.Columns.Add("用户", S(178));
            _list.Columns.Add("PID", S(50), HorizontalAlignment.Right);
            _list.Columns.Add("工作集", S(80), HorizontalAlignment.Right);
            _list.Columns.Add("私有内存", S(80), HorizontalAlignment.Right);
            _list.Columns.Add("线程", S(42), HorizontalAlignment.Right);
            _list.Columns.Add("状态", S(120));
            _list.ItemCheck += OnItemCheck;
            _list.ColumnClick += OnColumnClick;
            Controls.Add(_list);

            y += FormH - y - 96;

            // ---- 状态行 ----
            _statusLabel = new Label();
            _statusLabel.Location = Pt(margin, y);
            _statusLabel.Size = Sz(FormW - margin * 2, 18);
            _statusLabel.AutoSize = false;
            _statusLabel.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_statusLabel);

            y += 24;

            // ---- 按钮行 ----
            // 按钮行总宽 836（8 个按钮 + 7 个 8px 间隙），可用宽度 840。
            // 必须逐个累加排布：之前给「清空勾选」写死 x=margin+540，
            // 而「修剪选中」是从右边反推的，两者在中间撞上了。
            int bx = margin;
            _btnRefresh = MakeButton(bx, y, 76, "刷新", delegate { RefreshListAsync(); });
            bx += 76 + 8;
            _btnIncludeBackground = MakeButton(bx, y, 116, "勾选全部后台", delegate { IncludeAll(ProcessKind.Background); });
            bx += 116 + 8;
            _btnIncludeWindowsApps = MakeButton(bx, y, 116, "勾选全部 UWP", delegate { IncludeAllUwp(); });
            bx += 116 + 8;
            _btnWhitelist = MakeButton(bx, y, 96, "加入白名单", delegate { ModifyWhitelist(true); });
            bx += 96 + 8;
            _btnUnwhitelist = MakeButton(bx, y, 96, "移出白名单", delegate { ModifyWhitelist(false); });
            bx += 96 + 8;
            // 清空显式勾选：分类口径调整后（Windows 组件不再算后台应用），
            // 旧配置里按旧口径点「勾选全部后台」加进来的名字会残留在
            // TrimSelectedNames 中，继续把 conhost/svchost 之类纳入修剪。
            // 没有这个按钮，用户只能逐个取消勾选。
            _btnClearSelection = MakeButton(bx, y, 80, "清空勾选", delegate { ClearSelection(); });
            bx += 80 + 8;

            _btnTrimSelected = MakeButton(bx, y, 100, "修剪选中", delegate { TrimSelected(); });
            bx += 100 + 8;

            _btnClose = MakeButton(bx, y, 80, "关闭", delegate { Close(); });
        }

        private Button MakeButton(int x, int y, int w, string text, EventHandler handler)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = Pt(x, y);
            b.Size = Sz(w, 28);
            b.Click += handler;
            Controls.Add(b);
            return b;
        }

        private void LoadSettings()
        {
            // 这里只是把配置回填到控件，不应触发任何列表操作
            _suppressAutoRefresh = true;
            try
            {
                _chkBackground.Checked = _config.TrimBackgroundApps;
                _chkWindowsProc.Checked = _config.TrimWindowsProcesses;
                _chkSkipForeground.Checked = _config.TrimSkipForeground;
                _threadCount.Value = _config.TrimThreadCount;
            }
            finally
            {
                _suppressAutoRefresh = false;
            }
        }

        // ---------------- 列表刷新 ----------------

        /// <summary>
        /// 就地重算所有行的复选框、状态列与前景色，**不重建列表**。
        ///
        /// 勾选「自动纳入」、增删白名单、批量勾选都只是改变了「谁应该被选中」，
        /// 行本身（进程名/PID/内存）没有变化，因此不需要重新枚举进程。
        /// 走 RefreshList 会重新 Enumerate（每个进程都要开句柄查令牌与包标识），
        /// 在 UI 线程上阻塞上百毫秒并 Clear 重建两百多项——那正是闪烁的来源。
        /// </summary>
        private void ApplyAllStates()
        {
            HashSet<string> whitelist = _config.GetWhitelistSet();
            HashSet<string> selected = _config.GetSelectedSet();

            _updatingChecks = true;
            _list.BeginUpdate();
            try
            {
                foreach (ListViewItem item in _list.Items)
                {
                    ProcessEntry e = item.Tag as ProcessEntry;
                    if (e == null) continue;

                    string name = ProcessEnumerator.StripExe(e.Name);
                    bool whitelisted = whitelist.Contains(name);
                    bool critical = e.IsProtected;
                    bool canCheck = !whitelisted && !critical;

                    bool want = canCheck && (selected.Contains(name) ||
                        (e.Kind == ProcessKind.Background && _config.TrimBackgroundApps) ||
                        (e.Kind == ProcessKind.WindowsProcess && _config.TrimWindowsProcesses));

                    if (item.Checked != want) item.Checked = want;

                    item.SubItems[7].Text = StatusText(e, whitelisted, selected.Contains(name));

                    Color wantColor = whitelisted ? Color.Gray
                                    : critical ? Color.Firebrick
                                    : e.Kind == ProcessKind.WindowsProcess ? Color.DimGray
                                    : SystemColors.ControlText;
                    if (item.ForeColor != wantColor) item.ForeColor = wantColor;
                }
            }
            finally
            {
                _list.EndUpdate();
                _updatingChecks = false;
            }

            UpdateStatusLine();
        }

        /// <summary>
        /// 异步刷新：进程枚举放到后台线程。
        ///
        /// Enumerate() 要遍历全部进程并为每个开句柄查询令牌与包标识，
        /// 本机 220 个进程约需 50–200 ms。放在 UI 线程上点击「刷新」会明显卡顿，
        /// 期间窗口不重绘，看起来就是一闪。这里改为后台枚举、回 UI 一次性填充。
        /// </summary>
        private void RefreshListAsync()
        {
            if (_refreshing) return;
            _refreshing = true;
            _btnRefresh.Enabled = false;
            _statusLabel.Text = "正在枚举进程…";

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                List<ProcessEntry> list = null;
                string error = null;
                try { list = ProcessEnumerator.Enumerate(); }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    if (IsDisposed) return;
                    BeginInvoke(new Action(delegate
                    {
                        _refreshing = false;
                        if (IsDisposed) return;
                        _btnRefresh.Enabled = true;

                        if (list == null)
                        {
                            _statusLabel.Text = "进程枚举失败：" + error;
                            return;
                        }

                        _entries = list;
                        DropForegroundSelections();
                        PopulateList();
                    }));
                }
                catch { }
            });
        }

        /// <summary>用 _entries 重建列表项。调用方负责提供数据。</summary>
        private void PopulateList()
        {
            _updatingChecks = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();

                HashSet<string> whitelist = _config.GetWhitelistSet();
                HashSet<string> selected = _config.GetSelectedSet();

                foreach (ProcessEntry e in _entries)
                {
                    string name = ProcessEnumerator.StripExe(e.Name);
                    bool whitelisted = whitelist.Contains(name);

                    ListViewItem item = new ListViewItem(e.Name);
                    item.SubItems.Add(KindText(e.Kind));
                    item.SubItems.Add(string.IsNullOrEmpty(e.User) ? "—" : e.User);
                    item.SubItems.Add(e.Pid.ToString());
                    item.SubItems.Add(MemoryState.FormatBytes(e.WorkingSet));
                    item.SubItems.Add(MemoryState.FormatBytes(e.PrivateBytes));
                    item.SubItems.Add(e.ThreadCount.ToString());
                    item.SubItems.Add(StatusText(e, whitelisted, selected.Contains(name)));

                    item.Tag = e;

                    bool canCheck = !whitelisted && !e.IsProtected;
                    item.Checked = canCheck && (selected.Contains(name) ||
                        (e.Kind == ProcessKind.Background && _config.TrimBackgroundApps) ||
                        (e.Kind == ProcessKind.WindowsProcess && _config.TrimWindowsProcesses));

                    if (whitelisted) item.ForeColor = Color.Gray;
                    else if (e.IsProtected) item.ForeColor = Color.Firebrick;
                    else if (e.Kind == ProcessKind.WindowsProcess) item.ForeColor = Color.DimGray;

                    _list.Items.Add(item);
                }

                ApplySort();
            }
            finally
            {
                _list.EndUpdate();
                _updatingChecks = false;
            }

            UpdateStatusLine();
        }

        /// <summary>重建列表（同步）。仅在窗口构造时使用——此时窗口尚未显示，不会闪。</summary>
        private void RefreshList()
        {
            _entries = ProcessEnumerator.Enumerate();
            DropForegroundSelections();
            PopulateList();
        }

        /// <summary>
        /// 把「已切到前台的、原先被勾选的后台应用」自动取消勾选。
        ///
        /// 用户要的语义：后台应用一旦被切到前台使用，就不该再被修剪，
        /// 勾选状态也应随之取消，否则界面显示「已勾选」而实际已被跳过，
        /// 两者不一致会让人以为设置没生效。
        ///
        /// 只在进程「由后台变为前台」时移除，不会误删一直处于前台的勾选
        /// （那种勾选本来就选不中——前台应用无法通过「勾选全部后台」加入）。
        ///
        /// 返回被取消的数量。
        /// </summary>
        private int DropForegroundSelections()
        {
            HashSet<string> selected = _config.GetSelectedSet();
            if (selected.Count == 0) return 0;

            HashSet<string> whitelist = _config.GetWhitelistSet();
            int dropped = 0;

            foreach (ProcessEntry e in _entries)
            {
                if (!e.IsForegroundActive) continue;
                if (e.IsProtected) continue;

                string name = ProcessEnumerator.StripExe(e.Name);
                if (whitelist.Contains(name)) continue;

                if (selected.Remove(name)) dropped++;
            }

            if (dropped > 0)
            {
                _config.TrimSelectedNames = Config.FromNameSet(selected);
                _statusLabel.Text = "已自动取消 " + dropped +
                    " 个切到前台的进程的勾选（正在使用的程序不修剪）。";
            }

            return dropped;
        }

        /// <summary>分类文案。只保留任务管理器那三类。</summary>
        private static string KindText(ProcessKind kind)
        {
            switch (kind)
            {
                case ProcessKind.Foreground: return "前台应用";
                case ProcessKind.WindowsProcess: return "Windows 进程";
                default: return "后台应用";
            }
        }

        private string StatusText(ProcessEntry e, bool whitelisted, bool selected)
        {
            if (whitelisted) return "白名单（豁免）";
            if (e.IsProtected) return "系统保护，不修剪";
            // 正在被操作的程序：即便命中规则也会被跳过，必须显式告知，
            // 否则用户看到「自动纳入」却什么都没发生，会以为功能失效
            if (e.IsForegroundActive && _config.TrimSkipForeground) return "当前焦点，已跳过";

            if (e.TrimOk.HasValue)
                return e.TrimOk.Value
                    ? "已修剪 " + e.TrimMs + " ms"
                    : "修剪失败";

            if (selected) return "已勾选";
            if (e.IsBackground && _config.TrimBackgroundApps) return "自动纳入（后台）";
            if (e.Kind == ProcessKind.WindowsProcess && _config.TrimWindowsProcesses) return "自动纳入（Win 进程）";
            if (e.Kind == ProcessKind.WindowsProcess) return "Windows 组件";
            if (e.Kind == ProcessKind.Foreground) return "前台运行中";
            return "";
        }

        private void UpdateStatusLine()
        {
            int total = _list.Items.Count;
            int checkedCount = 0;
            foreach (ListViewItem it in _list.Items) if (it.Checked) checkedCount++;

            int whitelist = _config.GetWhitelistSet().Count;

            _statusLabel.Text = "共 " + total + " 个进程，参与修剪 " + checkedCount +
                                " 个，白名单 " + whitelist + " 项" +
                                (_engine.TrimRunning ? "    ⏳ 修剪进行中…" : "");
        }

        // ---------------- 交互 ----------------

        /// <summary>
        /// 复选框变更处理。
        ///
        /// 白名单与系统关键进程不允许勾选：这里把状态改回并提示原因，
        /// 而不是静默忽略——用户点了没反应会以为是程序卡了。
        /// </summary>
        private void OnItemCheck(object sender, ItemCheckEventArgs e)
        {
            // 程序化更新（PopulateList / ApplyAllStates）不应改写配置
            if (_updatingChecks) return;

            ListViewItem item = _list.Items[e.Index];
            ProcessEntry entry = item.Tag as ProcessEntry;
            if (entry == null) return;

            HashSet<string> whitelist = _config.GetWhitelistSet();
            string name = ProcessEnumerator.StripExe(entry.Name);

            if (e.NewValue == CheckState.Checked)
            {
                if (whitelist.Contains(name))
                {
                    e.NewValue = CheckState.Unchecked;
                    _statusLabel.Text = entry.Name + " 在白名单中，请先点「移出白名单」。";
                    return;
                }
                if (entry.IsProtected)
                {
                    e.NewValue = CheckState.Unchecked;
                    _statusLabel.Text = entry.Name + " 是系统关键进程，不参与修剪（避免系统卡顿）。";
                    return;
                }
            }

            // 复选框代表「显式勾选」，按名称写入配置。
            // 但若该进程是被「一键纳入」自动选中的，取消勾选会立即失效——
            // 因为自动纳入规则仍在。这种情况提示用户去关总开关。
            HashSet<string> selected = _config.GetSelectedSet();
            if (e.NewValue == CheckState.Checked) selected.Add(name);
            else selected.Remove(name);
            _config.TrimSelectedNames = Config.FromNameSet(selected);

            // 延迟到事件结束后更新（此时 item.Checked 仍是旧值），
            // 且合并连续请求，避免快速勾选时排队上百次重绘
            ScheduleStatusUpdate();
        }

        /// <summary>合并高频的状态行刷新请求。</summary>
        private void ScheduleStatusUpdate()
        {
            if (_statusPending) return;
            _statusPending = true;
            BeginInvoke(new Action(delegate
            {
                _statusPending = false;
                if (!IsDisposed) UpdateStatusLine();
            }));
        }

        private void IncludeAll(ProcessKind kind)
        {
            HashSet<string> selected = _config.GetSelectedSet();
            int n = 0;

            foreach (ProcessEntry e in _entries)
            {
                if (e.Kind != kind) continue;
                if (e.IsProtected) continue;          // 系统关键进程不纳入
                selected.Add(ProcessEnumerator.StripExe(e.Name));
                n++;
            }

            _config.TrimSelectedNames = Config.FromNameSet(selected);
            // 只改了勾选状态，行数据没变 → 就地更新即可，重建列表会闪
            ApplyAllStates();
            _statusLabel.Text = "已勾选 " + n + " 个" + KindText(kind) + "。";
        }

        /// <summary>
        /// 勾选全部 UWP 应用。
        ///
        /// UWP 不再是分类维度（界面按有无窗口归入前台/后台），
        /// 因此这里按 IsUwp 属性筛选，而不是按 Kind。
        /// </summary>
        private void IncludeAllUwp()
        {
            HashSet<string> selected = _config.GetSelectedSet();
            int n = 0;

            foreach (ProcessEntry e in _entries)
            {
                if (!e.IsUwp || e.IsProtected) continue;
                selected.Add(ProcessEnumerator.StripExe(e.Name));
                n++;
            }

            _config.TrimSelectedNames = Config.FromNameSet(selected);
            ApplyAllStates();
            _statusLabel.Text = "已勾选 " + n + " 个 UWP 应用。";
        }

        /// <summary>
        /// 清空显式勾选（白名单保留）。
        ///
        /// 分类口径调整后，旧配置里按旧口径批量勾选进来的名字会残留，
        /// 继续把 conhost / svchost 之类算进修剪目标。这个入口让用户能一键重置，
        /// 之后按新的分类重新选择（或直接依赖「自动纳入」开关）。
        /// </summary>
        private void ClearSelection()
        {
            _config.TrimSelectedNames = new string[0];
            ApplyAllStates();
            _statusLabel.Text = "已清空显式勾选（白名单保留）。";
        }

        private void ModifyWhitelist(bool add)        {
            if (_list.SelectedItems.Count == 0)
            {
                _statusLabel.Text = "请先在列表中选中进程。";
                return;
            }

            HashSet<string> whitelist = _config.GetWhitelistSet();
            HashSet<string> selected = _config.GetSelectedSet();
            int n = 0;

            foreach (ListViewItem item in _list.SelectedItems)
            {
                ProcessEntry e = item.Tag as ProcessEntry;
                if (e == null) continue;

                string name = ProcessEnumerator.StripExe(e.Name);

                if (add)
                {
                    if (e.IsProtected) continue;   // 本来就是保护状态
                    whitelist.Add(name);
                    selected.Remove(name);   // 互斥：进了白名单就不再显式勾选
                    n++;
                }
                else
                {
                    if (whitelist.Remove(name)) n++;
                }
            }

            _config.TrimWhitelist = Config.FromNameSet(whitelist);
            _config.TrimSelectedNames = Config.FromNameSet(selected);
            // 白名单只影响复选框可用性与颜色，进程数据本身没变 → 就地更新
            ApplyAllStates();

            _statusLabel.Text = add
                ? "已把 " + n + " 个进程加入白名单（豁免修剪）。"
                : "已把 " + n + " 个进程移出白名单。";
        }

        private void TrimSelected()
        {
            List<ProcessEntry> targets = new List<ProcessEntry>();
            foreach (ListViewItem item in _list.SelectedItems)
            {
                ProcessEntry e = item.Tag as ProcessEntry;
                if (e == null) continue;
                if (e.IsProtected) continue;
                if (_config.GetWhitelistSet().Contains(ProcessEnumerator.StripExe(e.Name))) continue;
                targets.Add(e);
            }

            if (targets.Count == 0)
            {
                _statusLabel.Text = "请选中要修剪的进程（系统关键进程与白名单进程会被跳过）。";
                return;
            }

            SetBusy(true);

            if (!_engine.TrimProcessesAsync(targets))
            {
                SetBusy(false);
                _statusLabel.Text = "已有一次修剪正在进行，请稍候。";
            }
        }

        private void SetBusy(bool busy)
        {
            _btnTrimSelected.Enabled = !busy;
            _btnRefresh.Enabled = !busy;
            _btnWhitelist.Enabled = !busy;
            _btnUnwhitelist.Enabled = !busy;
            _btnIncludeBackground.Enabled = !busy;
            _btnIncludeWindowsApps.Enabled = !busy;
            if (_btnClearSelection != null) _btnClearSelection.Enabled = !busy;
            UseWaitCursor = busy;
        }

        // ---------------- 引擎回调 ----------------

        private void OnTrimProgress(int done, int total)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<int, int>(OnTrimProgress), done, total);
                    return;
                }
                // 回调源头已节流（约 20 次），这里再做一次值比较，
                // 避免相同文本重复触发重绘
                string text = "修剪中… " + done + " / " + total;
                if (_statusLabel.Text != text) _statusLabel.Text = text;
            }
            catch { }
        }

        private void OnTrimmed(TrimSummary sum)
        {
            try
            {
                if (IsDisposed) return;
                if (InvokeRequired)
                {
                    BeginInvoke(new Action<TrimSummary>(OnTrimmed), sum);
                    return;
                }

                SetBusy(false);

                // 把逐进程结果回填到当前列表
                Dictionary<int, ProcessEntry> byPid = new Dictionary<int, ProcessEntry>();
                foreach (ProcessEntry e in sum.Entries) {
                    if (!byPid.ContainsKey(e.Pid)) byPid.Add(e.Pid, e);
                }

                _updatingChecks = true;
                _list.BeginUpdate();
                try
                {
                    foreach (ListViewItem item in _list.Items)
                    {
                        ProcessEntry e = item.Tag as ProcessEntry;
                        if (e == null) continue;

                        ProcessEntry updated;
                        if (!byPid.TryGetValue(e.Pid, out updated)) continue;

                        e.TrimOk = updated.TrimOk;
                        e.TrimMs = updated.TrimMs;
                        e.WorkingSet = updated.WorkingSetAfterTrim > 0
                            ? updated.WorkingSetAfterTrim : e.WorkingSet;

                        item.SubItems[4].Text = MemoryState.FormatBytes(e.WorkingSet);
                        item.SubItems[7].Text = updated.TrimOk.HasValue
                            ? (updated.TrimOk.Value ? "已修剪 " + updated.TrimMs + " ms" : "修剪失败")
                            : "";
                    }
                }
                finally
                {
                    _list.EndUpdate();
                    _updatingChecks = false;
                }

                _statusLabel.Text = sum.ToString();
                UpdateStatusLine();
            }
            catch { }
        }

        // ---------------- 排序 ----------------

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (_sortColumn == e.Column) _sortAscending = !_sortAscending;
            else { _sortColumn = e.Column; _sortAscending = true; }
            ApplySort();
        }

        /// <summary>
        /// 按当前排序列重排。
        ///
        /// 用 ListViewItemSorter 而不是重建列表：重建会丢失选中状态，
        /// 用户在等排序时选中的一批进程就白选了。
        /// </summary>
        private void ApplySort()
        {
            if (_sortColumn < 0) return;

            _list.ListViewItemSorter = new ProcComparer(_sortColumn, _sortAscending);
            _list.Sort();
        }

        private class ProcComparer : System.Collections.IComparer
        {
            private readonly int _col;
            private readonly bool _asc;

            public ProcComparer(int col, bool asc) { _col = col; _asc = asc; }

            public int Compare(object x, object y)
            {
                ListViewItem a = (ListViewItem)x;
                ListViewItem b = (ListViewItem)y;

                int result;
                if (_col == 3 || _col == 6)   // PID / 线程：按数值比
                {
                    int ai = ParseInt(a.SubItems[_col].Text);
                    int bi = ParseInt(b.SubItems[_col].Text);
                    result = ai.CompareTo(bi);
                }
                else if (_col == 4 || _col == 5)   // 工作集 / 私有内存：按 Tag 里的字节数比
                {
                    ProcessEntry ea = a.Tag as ProcessEntry;
                    ProcessEntry eb = b.Tag as ProcessEntry;
                    long av = ea == null ? 0 : (_col == 4 ? ea.WorkingSet : ea.PrivateBytes);
                    long bv = eb == null ? 0 : (_col == 4 ? eb.WorkingSet : eb.PrivateBytes);
                    result = av.CompareTo(bv);
                }
                else
                {
                    result = string.Compare(a.SubItems[_col].Text, b.SubItems[_col].Text,
                        StringComparison.OrdinalIgnoreCase);
                }

                return _asc ? result : -result;
            }

            private static int ParseInt(string s)
            {
                int v;
                return int.TryParse(s, out v) ? v : 0;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _engine.Trimmed -= OnTrimmed;
            _engine.TrimProgress -= OnTrimProgress;

            if (_fgTimer != null)
            {
                _fgTimer.Stop();
                _fgTimer.Dispose();
                _fgTimer = null;
            }

            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer.Dispose();
                _liveTimer = null;
            }

            if (_winEventHook != IntPtr.Zero)
            {
                try { Native.UnhookWinEvent(_winEventHook); } catch { }
                _winEventHook = IntPtr.Zero;
                _winEventProc = null;
            }

            // 本窗口的所有勾选/白名单改动都是即时写入 _config 的，
            // 这里补一次落盘：用户直接点窗口右上角关闭时，改动也不会丢。
            // 注意设置窗口的「保存」仍会再存一次，重复写入无副作用。
            try { _config.Save(); } catch { }

            base.OnFormClosed(e);
        }
    }
}
