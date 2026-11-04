using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MemReclaim
{
    /// <summary>
    /// 进程分类。对齐任务管理器的三分法。
    ///
    /// 注意「系统关键」与「UWP」不在分类维度里：
    ///   - 系统关键是一个**保护标志**（ProcessEntry.IsProtected），跨分类都可能出现，
    ///     用它来决定「能不能修剪」，而不是用来分类；
    ///   - UWP 是一个**属性**（IsUwp），用于「自动纳入 Windows 应用」开关，
    ///     界面上仍按有无窗口归入前台/后台。
    /// 分类维度只保留任务管理器展示的那三种。
    /// </summary>
    internal enum ProcessKind
    {
        /// <summary>前台应用：自己持有可见窗口，或与某个有窗口的进程同映像路径。</summary>
        Foreground = 0,
        /// <summary>后台应用：第三方无窗口进程。</summary>
        Background = 1,
        /// <summary>Windows 进程：Windows 自身组件（svchost、conhost、驱动服务等）。</summary>
        WindowsProcess = 2
    }

    /// <summary>进程列表中的一行。</summary>
    internal class ProcessEntry
    {
        public int Pid;
        public string Name = "";
        /// <summary>映像完整路径；取不到时为空。</summary>
        public string Path = "";
        /// <summary>运行该进程的账户名（DOMAIN\User 或 SYSTEM 等）。</summary>
        public string User = "";

        /// <summary>工作集字节数（物理内存中已映射部分）。</summary>
        public long WorkingSet;
        /// <summary>私有提交字节数（私有虚拟内存）。</summary>
        public long PrivateBytes;
        /// <summary>句柄数，仅用于状态展示。</summary>
        public int HandleCount;
        public int ThreadCount;

        public ProcessKind Kind = ProcessKind.Background;
        /// <summary>UWP 应用的包全名；非 UWP 为 null。</summary>
        public string PackageFullName;

        /// <summary>是否 UWP/Store 应用。仅用于「自动纳入 Windows 应用」开关。</summary>
        public bool IsUwp;

        /// <summary>
        /// 是否系统关键进程（csrss、services、Memory Compression 等）。
        /// 与分类无关：它是一个保护标志，命中后禁止修剪、禁止勾选。
        /// </summary>
        public bool IsProtected;

        /// <summary>
        /// 是否属于「当前拥有输入焦点」的应用（同映像路径的进程一并标记）。
        ///
        /// 与「前台应用」分类不同：分类看的是「有没有可见窗口」，
        /// 这里看的是「用户此刻正在操作谁」。有窗口但被遮挡/最小化的不算。
        /// 修剪时用这个标志跳过正在交互的程序。
        /// </summary>
        public bool IsForegroundActive;

        /// <summary>是否有可见的顶层窗口（用于区分前台/后台）。</summary>
        public bool HasVisibleWindow;

        /// <summary>修剪是否成功；null 表示尚未尝试。</summary>
        public bool? TrimOk;
        /// <summary>修剪耗时（毫秒）。</summary>
        public int TrimMs;
        /// <summary>修剪后工作集，用于估算释放量。</summary>
        public long WorkingSetAfterTrim;

        public bool IsForeground { get { return Kind == ProcessKind.Foreground; } }
        public bool IsBackground { get { return Kind == ProcessKind.Background; } }
        public bool IsWindowsProcess { get { return Kind == ProcessKind.WindowsProcess; } }

        public override string ToString()
        {
            return Name + " (" + Pid + ")";
        }
    }

    /// <summary>
    /// 进程枚举与分类。
    ///
    /// 分类口径（尽量贴合任务管理器的直觉，但以可程序化判定为准）：
    ///   - Windows 应用：有包标识（GetPackageFullName 返回有效包名），即 UWP/Store 应用；
    ///   - 前台/后台：以「是否存在可见且非工具窗口的顶层窗口」区分；
    ///     没有可见窗口的普通进程归为后台应用；
    ///   - 系统关键进程：按名称硬编码屏蔽，避免修剪内核/会话管理相关进程
    ///     造成系统卡顿或蓝屏（如 csrss、wininit、services、Memory Compression）。
    ///
    /// 为什么按名称判定系统关键进程而不是按签名/权限：
    /// 这些进程的关键性来自内核约定，而非可查询的属性；名称判定简单、可预判，
    /// 且误伤代价远高于多修剪几个无害进程。
    /// </summary>
    internal static class ProcessEnumerator
    {
        /// <summary>系统关键进程（小写比较，不带 .exe）。默认不参与修剪。</summary>
        private static readonly string[] CriticalNames =
        {
            "system", "idle", "registry", "memory compression", "memcompr",
            "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
            "fontdrvhost", "dwm", "sihost",
            "shellexperiencehost", "startmenuexperiencehost", "textinputhost",
            "securityhealthservice", "msmpeng", "nissrv",
            "mpssvc", "windefend", "trustedinstaller", "tiworker"
        };

        private static readonly HashSet<string> CriticalSet = BuildCriticalSet();

        private static HashSet<string> BuildCriticalSet()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in CriticalNames) set.Add(n);
            return set;
        }

        public static bool IsCriticalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return CriticalSet.Contains(StripExe(name));
        }

        /// <summary>去掉末尾的 .exe，便于名称比较。</summary>
        public static string StripExe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return name.Substring(0, name.Length - 4);
            return name;
        }

        /// <summary>
        /// 枚举当前所有进程并完成分类。
        ///
        /// 说明：逐进程打开句柄查询会失败一部分（受保护进程），失败时该进程
        /// 仍会出现在列表里，只是拿不到包标识/账户名，分类退化为「后台」或
        /// 「系统关键」——不能因为查询失败就把进程从列表里丢掉，否则用户会
        /// 看到与实际不符的进程数。
        /// </summary>
        public static List<ProcessEntry> Enumerate()
        {
            List<ProcessEntry> list = new List<ProcessEntry>();

            // 先一次性收集「有可见窗口」的 PID 集合，避免对每个进程都枚举窗口
            HashSet<int> windowPids = CollectWindowPids();

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { return list; }

            int selfPid = Process.GetCurrentProcess().Id;

            foreach (Process p in procs)
            {
                ProcessEntry e = new ProcessEntry();
                try
                {
                    e.Pid = p.Id;
                    e.Name = SafeName(p);

                    if (e.Pid == selfPid)
                    {
                        // 本程序自身不参与修剪：EmptyWorkingSet 会把自己刚用到的
                        // 页面全部推出物理内存，随后立刻又要读回，净效果为负。
                        continue;
                    }

                    e.HasVisibleWindow = windowPids.Contains(e.Pid);

                    ReadMemoryInfo(p, e);
                    e.ThreadCount = SafeThreadCount(p);
                    e.HandleCount = SafeHandleCount(p);

                    FillIdentity(e);

                    // 分类放到循环之后统一做：需要先知道「哪些映像路径有窗口」，
                    // 才能把同一应用的无窗口进程并入「应用」。
                    list.Add(e);
                }
                catch
                {
                    // 单个进程查询失败不影响整体列表
                }
                finally
                {
                    try { p.Dispose(); } catch { }
                }
            }

            // 收集「有可见窗口的进程」的映像路径。
            // 同一路径的所有进程视为同一个应用（对应任务管理器的「QQ (8)」式归组）。
            HashSet<string> appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessEntry e in list)
            {
                if (e.HasVisibleWindow && !string.IsNullOrEmpty(e.Path))
                    appPaths.Add(e.Path);
            }

            foreach (ProcessEntry e in list) Classify(e, appPaths);

            // 标记「当前焦点应用」：用户此刻正在操作的那个程序。
            // 同映像路径的进程一并标记——QQ 有 8 个进程，
            // 只跳过持有焦点窗口的那一个，另外 7 个照样会被修剪，起不到保护作用。
            MarkForeground(list);

            list.Sort(delegate (ProcessEntry a, ProcessEntry b)
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return a.Pid.CompareTo(b.Pid);
            });

            return list;
        }

        private static void Classify(ProcessEntry e, HashSet<string> appPaths)
        {
            // 保护标志与 UWP 属性先行赋值，它们不参与分类
            e.IsProtected = IsCriticalName(e.Name);
            e.IsUwp = !string.IsNullOrEmpty(e.PackageFullName);

            // 前台应用：自己有可见窗口，或与某个有窗口的进程共用同一映像路径。
            //
            // 后半句对应任务管理器的「应用」归组：QQ 有 8 个进程，只有 1 个
            // 持有窗口，任务管理器把它们合并显示为「QQ (8)」，整组算作应用。
            if (e.HasVisibleWindow)
            {
                e.Kind = ProcessKind.Foreground;
                return;
            }

            if (!string.IsNullOrEmpty(e.Path) && appPaths.Contains(e.Path))
            {
                e.Kind = ProcessKind.Foreground;
                return;
            }

            // Windows 进程：路径在 %SystemRoot% 下（排除第三方驱动包 DriverStore）。
            if (IsWindowsComponent(e.Path))
            {
                e.Kind = ProcessKind.WindowsProcess;
                return;
            }

            e.Kind = ProcessKind.Background;
        }

        private static string _windowsDir;

        private static string WindowsDir
        {
            get
            {
                if (_windowsDir == null)
                {
                    try
                    {
                        _windowsDir = Environment.GetFolderPath(
                            Environment.SpecialFolder.Windows).TrimEnd('\\');
                    }
                    catch { _windowsDir = ""; }
                }
                return _windowsDir;
            }
        }

        /// <summary>
        /// 进程是否属于 Windows 自身组件。
        ///
        /// 判据是路径落在 %SystemRoot% 下，但排除 DriverStore——
        /// 那里是第三方驱动包（Intel/NVIDIA/Realtek 等），任务管理器同样把它们
        /// 算作「后台进程」而不是「Windows 进程」，这里保持一致。
        ///
        /// 取不到路径时按「是 Windows 组件」处理：拿不到路径的多为受保护的系统
        /// 进程，宁可让它落在保守的一侧（不被「自动纳入后台应用」扫进修剪目标），
        /// 也不要因为查询失败就把它当第三方程序去修剪。
        /// </summary>
        private static bool IsWindowsComponent(string path)
        {
            string dir = WindowsDir;
            if (string.IsNullOrEmpty(dir)) return false;

            if (string.IsNullOrEmpty(path)) return true;

            if (!path.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)) return false;

            if (path.IndexOf("\\DriverStore\\", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return true;
        }

        private static string SafeName(Process p)
        {
            try { return p.ProcessName; }
            catch { return ""; }
        }

        private static int SafeThreadCount(Process p)
        {
            try { return p.Threads.Count; } catch { return 0; }
        }

        private static int SafeHandleCount(Process p)
        {
            try { return p.HandleCount; } catch { return 0; }
        }

        private static void ReadMemoryInfo(Process p, ProcessEntry e)
        {
            try
            {
                e.WorkingSet = p.WorkingSet64;
                e.PrivateBytes = p.PrivateMemorySize64;
            }
            catch
            {
                // 受保护进程读不到内存信息，保持 0
            }

            try { e.Path = p.MainModule.FileName; }
            catch { e.Path = ""; }
        }

        /// <summary>读取账户名与包标识。</summary>
        private static void FillIdentity(ProcessEntry e)
        {
            IntPtr h = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION, false, e.Pid);
            if (h == IntPtr.Zero) return;

            try
            {
                e.User = QueryUser(h);
                e.PackageFullName = QueryPackage(h);
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        private static string QueryUser(IntPtr hProcess)
        {
            IntPtr token = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                if (!Native.OpenProcessToken(hProcess, Native.TOKEN_QUERY, out token))
                    return "";

                int len;
                Native.GetTokenInformation(token, Native.TokenUserClass, IntPtr.Zero, 0, out len);
                if (len <= 0) return "";

                buf = Marshal.AllocHGlobal(len);
                int got;
                if (!Native.GetTokenInformation(token, Native.TokenUserClass, buf, len, out got))
                    return "";

                // TOKEN_USER 的第一个字段是 SID_AND_ATTRIBUTES.Sid
                IntPtr sid = Marshal.ReadIntPtr(buf, 0);
                if (sid == IntPtr.Zero) return "";

                StringBuilder name = new StringBuilder(256);
                StringBuilder domain = new StringBuilder(256);
                int nameLen = name.Capacity;
                int domLen = domain.Capacity;
                int use;

                if (!Native.LookupAccountSid(null, sid, name, ref nameLen, domain, ref domLen, out use))
                    return "";

                string n = name.ToString();
                string d = domain.ToString();
                if (string.IsNullOrEmpty(d)) return n;
                return d + "\\" + n;
            }
            catch
            {
                return "";
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (token != IntPtr.Zero) Native.CloseHandle(token);
            }
        }

        private static string QueryPackage(IntPtr hProcess)
        {
            try
            {
                uint len = 0;
                int rc = Native.GetPackageFullName(hProcess, ref len, null);

                if (rc == Native.APPMODEL_ERROR_NO_PACKAGE) return null;
                if (rc != Native.ERROR_INSUFFICIENT_BUFFER_CODE && rc != 0) return null;
                if (len == 0) return null;

                StringBuilder sb = new StringBuilder((int)len);
                rc = Native.GetPackageFullName(hProcess, ref len, sb);
                if (rc != 0) return null;

                string s = sb.ToString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>收集拥有可见顶层窗口的进程 ID 集合。</summary>
        private static HashSet<int> CollectWindowPids()
        {
            HashSet<int> pids = new HashSet<int>();

            try
            {
                Native.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
                {
                    try
                    {
                        if (!Native.IsWindowVisible(hWnd)) return true;

                        // 工具窗口、无主窗口的浮层不算「前台应用」
                        int exStyle = Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE);
                        if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return true;

                        // 有 owner 的多为对话框/弹出层，不算独立应用窗口
                        if (Native.GetWindow(hWnd, Native.GW_OWNER) != IntPtr.Zero) return true;

                        int pid;
                        Native.GetWindowThreadProcessId(hWnd, out pid);
                        if (pid > 0) pids.Add(pid);
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return pids;
        }

        /// <summary>供界面做增量同步：暴露窗口 PID 集合。</summary>
        public static HashSet<int> SnapshotWindowPids()
        {
            return CollectWindowPids();
        }

        /// <summary>
        /// 按当前窗口状态重算分类与焦点标记，复用已有的 PID/路径/内存数据。
        ///
        /// 用于界面实时同步：进程列表打开后用户可能打开或关闭别的程序窗口，
        /// 若只在初次枚举时算一次分类，界面就会一直显示旧分类。
        /// 这里只重新枚举窗口（EnumWindows，约 1–3 ms），远轻于完整 Enumerate。
        /// </summary>
        public static void Reclassify(List<ProcessEntry> list)
        {
            if (list == null || list.Count == 0) return;

            HashSet<int> windowPids = CollectWindowPids();
            foreach (ProcessEntry e in list)
                e.HasVisibleWindow = windowPids.Contains(e.Pid);

            HashSet<string> appPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ProcessEntry e in list)
                if (e.HasVisibleWindow && !string.IsNullOrEmpty(e.Path))
                    appPaths.Add(e.Path);

            foreach (ProcessEntry e in list) Classify(e, appPaths);

            MarkForeground(list);
        }

        /// <summary>标记「当前拥有输入焦点」的应用（同映像路径一并标记）。</summary>
        public static void MarkForeground(List<ProcessEntry> list)
        {
            if (list == null) return;

            int fgPid = GetForegroundPid();
            string fgPath = null;
            if (fgPid > 0)
            {
                foreach (ProcessEntry e in list)
                    if (e.Pid == fgPid) { fgPath = e.Path; break; }
            }

            foreach (ProcessEntry e in list)
            {
                if (fgPid == 0)
                    e.IsForegroundActive = false;
                else if (e.Pid == fgPid)
                    e.IsForegroundActive = true;
                else
                    e.IsForegroundActive = !string.IsNullOrEmpty(fgPath) &&
                        string.Equals(e.Path, fgPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>按 PID 取单个进程的最新工作集；查不到返回 -1。</summary>
        public static long GetWorkingSet(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.WorkingSet64;
                }
            }
            catch { return -1; }
        }

        /// <summary>
        /// 取当前拥有焦点窗口的进程 PID；无焦点时返回 0。
        ///
        /// 用途：修剪时要跳过用户正在交互的程序。注意它和「前台应用」分类不是一回事——
        /// 分类里的「前台应用」指有可见窗口（任务管理器口径，包含被遮挡/最小化的窗口），
        /// 而这里是真正的输入焦点，只有用户此刻正在操作的那一个。
        /// </summary>
        public static int GetForegroundPid()
        {
            try
            {
                IntPtr hWnd = Native.GetForegroundWindow();
                if (hWnd == IntPtr.Zero) return 0;

                int pid;
                Native.GetWindowThreadProcessId(hWnd, out pid);
                return pid > 0 ? pid : 0;
            }
            catch { return 0; }
        }
    }
}
