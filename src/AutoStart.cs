using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
namespace MemReclaim
{
    /// <summary>
    /// 开机自启。在用户「启动」文件夹（shell:startup）创建指向本程序的快捷方式。
    ///
    /// 为什么从注册表 Run 项换成启动文件夹：
    ///   - 启动文件夹是用户在资源管理器里就能看见、能自行删除的位置，比注册表透明；
    ///   - 快捷方式本身可以被用户检查（右键属性看目标），不需要额外工具；
    ///   - 任务管理器「启动」页同样能管理它，可控性和注册表项一样。
    ///
    /// 一致性保证：IsEnabled() 以「启动文件夹里是否存在指向本程序的快捷方式」
    /// 为唯一判据。因此快捷方式被手动删除、或程序被移动到别处导致指向失效时，
    /// 界面会自动显示为未勾选——不会出现「界面说已启用、实际开机不启动」。
    /// </summary>
    internal static class AutoStart
    {
        /// <summary>本程序使用的快捷方式文件名。</summary>
        private const string ShortcutName = "MemReclaim.exe.lnk";

        /// <summary>旧版使用的注册表位置，仅用于升级时清理。</summary>
        private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string LegacyValueName = "MemReclaim";

        /// <summary>
        /// SLGP_RAWPATH：让 GetPath 返回快捷方式里存储的原始路径，不交给 Shell 解析。
        /// 目标文件已被删除或移动时，不带这个标志会解析失败并返回空串，
        /// 那样就无法区分「没有快捷方式」和「快捷方式存在但指向已失效」。
        /// </summary>
        private const uint SlgpRawPath = 0x00000004;

        /// <summary>用户启动文件夹，即资源管理器地址栏输入 shell:startup 打开的位置。</summary>
        public static string StartupFolder
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.Startup); }
        }

        /// <summary>按开关状态创建或删除快捷方式。返回是否操作成功。</summary>
        public static bool Apply(bool enable)
        {
            return ApplyTo(StartupFolder, enable);
        }

        /// <summary>当前是否已设置为开机自启。</summary>
        public static bool IsEnabled()
        {
            return IsEnabledIn(StartupFolder);
        }

        /// <summary>
        /// 在指定文件夹里执行启用/停用。独立出来是为了能在临时目录中验证逻辑，
        /// 避免测试时碰到真实的启动文件夹。
        /// </summary>
        internal static bool ApplyTo(string folder, bool enable)
        {
            if (string.IsNullOrEmpty(folder)) return false;

            try
            {
                // 旧版写的是 HKCU\...\Run。若不清掉，升级后会与快捷方式同时存在，
                // 开机启动两次——第二个实例会被单实例互斥锁拦下并弹「已在运行」。
                RemoveLegacyRunEntry();

                if (enable) return EnsureShortcut(folder);
                RemoveShortcuts(folder);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 清理旧版写在 HKCU\...\Run 里的自启项。
        ///
        /// 旧版用注册表实现自启，新版改用启动文件夹。若升级后不清掉旧项，
        /// 两处会同时生效、开机启动两个实例——第二个会被单实例互斥锁拦下并
        /// 弹出「已在运行」，看起来像故障。程序启动时调用一次即可。
        ///
        /// 判定始终只看快捷方式，因此清理旧项不会改变界面显示的状态。
        /// </summary>
        public static void CleanupLegacy()
        {
            RemoveLegacyRunEntry();
        }

        /// <summary>判断指定文件夹里是否存在指向本程序的快捷方式。</summary>
        internal static bool IsEnabledIn(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return false;

            try
            {
                foreach (string lnk in Directory.GetFiles(folder, "*.lnk"))
                {
                    if (IsTargetForApp(GetShortcutTarget(lnk))) return true;
                }
            }
            catch
            {
                // 目录不可读时按「未启用」处理：宁可让用户重新勾选，
                // 也不要显示一个实际不生效的勾选状态。
            }
            return false;
        }

        /// <summary>确保快捷方式存在且指向当前 exe。</summary>
        private static bool EnsureShortcut(string folder)
        {
            string exe = Application.ExecutablePath;
            if (string.IsNullOrEmpty(exe)) return false;

            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string lnk = Path.Combine(folder, ShortcutName);

            // 已存在且指向正确时不再重写：用户可能手动改过图标或备注，
            // 无谓覆盖会把这些改动抹掉。
            if (File.Exists(lnk) && IsTargetForApp(GetShortcutTarget(lnk))) return true;

            return CreateShortcut(lnk, exe);
        }

        /// <summary>
        /// 删除文件夹里所有指向本程序的快捷方式。
        ///
        /// 不局限于 ShortcutName：用户可能自己建过别的名字的快捷方式，
        /// 只删固定名字的话，取消勾选后开机仍会启动，与界面显示矛盾。
        /// </summary>
        private static void RemoveShortcuts(string folder)
        {
            if (!Directory.Exists(folder)) return;

            foreach (string lnk in Directory.GetFiles(folder, "*.lnk"))
            {
                if (!IsTargetForApp(GetShortcutTarget(lnk))) continue;
                try { File.Delete(lnk); } catch { }
            }
        }

        /// <summary>清掉旧版留在注册表 Run 项里的自启条目。</summary>
        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true))
                {
                    if (key == null) return;
                    if (key.GetValue(LegacyValueName) != null)
                        key.DeleteValue(LegacyValueName, false);
                }
            }
            catch { }
        }

        /// <summary>快捷方式的目标是否就是当前运行的这一个 exe。</summary>
        private static bool IsTargetForApp(string target)
        {
            if (string.IsNullOrEmpty(target)) return false;
            return string.Equals(
                NormalizePath(Application.ExecutablePath),
                NormalizePath(target),
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 把路径规范成可比较的形式：去引号、去 \\?\ 前缀、转绝对路径、展开 8.3 短名、去尾部分隔符。
        ///
        /// 展开短名是必需的：Application.ExecutablePath 在某些启动方式下返回
        /// C:\Users\ADMINI~1\... 这种短路径，而用户在资源管理器里建的快捷方式存的是
        /// 长路径。两者指向同一文件，直接字符串比较却判不相等，会导致
        /// 「明明有快捷方式却显示未勾选」。
        /// </summary>
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;

            try
            {
                string p = path.Trim();
                if (p.Length >= 2 && p[0] == '"' && p[p.Length - 1] == '"')
                    p = p.Substring(1, p.Length - 2);
                if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                    p = p.Substring(4);

                p = Path.GetFullPath(p);

                // Path.GetFullPath 通常已展开短名，但并非所有路径形态都能保证，
                // 这里显式再展开一次；失败则保留原值继续比较。
                StringBuilder sb = new StringBuilder(1024);
                if (Native.GetLongPathName(p, sb, (uint)sb.Capacity) > 0)
                    p = sb.ToString();

                return p.TrimEnd('\\');
            }
            catch
            {
                return path;
            }
        }

        /// <summary>读取快捷方式里存储的目标路径；读取失败返回 null。</summary>
        private static string GetShortcutTarget(string lnkPath)
        {
            object link = null;
            try
            {
                link = new ShellLinkCoClass();

                // STGM_READ = 0
                ((IPersistFile)link).Load(lnkPath, 0);

                StringBuilder sb = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, SlgpRawPath);

                string result = sb.ToString();
                return string.IsNullOrEmpty(result) ? null : result;
            }
            catch
            {
                return null;
            }
            finally
            {
                ReleaseCom(link);
            }
        }

        /// <summary>创建（或覆盖）指向 targetPath 的快捷方式。</summary>
        private static bool CreateShortcut(string lnkPath, string targetPath)
        {
            object link = null;
            try
            {
                link = new ShellLinkCoClass();

                IShellLinkW sl = (IShellLinkW)link;
                sl.SetPath(targetPath);
                sl.SetWorkingDirectory(Path.GetDirectoryName(targetPath));
                sl.SetDescription("内存回收 — 开机自动启动");
                sl.SetIconLocation(targetPath, 0);

                ((IPersistFile)link).Save(lnkPath, true);
                return File.Exists(lnkPath);
            }
            catch
            {
                return false;
            }
            finally
            {
                ReleaseCom(link);
            }
        }

        private static void ReleaseCom(object o)
        {
            try
            {
                if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
            }
            catch { }
        }

        // ---------------- Shell 链接 COM 互操作 ----------------
        // 用 IShellLinkW 而非 WScript.Shell：后者要靠 Microsoft.CSharp 的 dynamic 绑定，
        // 而本项目的编译脚本刻意只引用 System / System.Drawing / System.Windows.Forms / System.Xml。

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass
        {
        }

        /// <summary>
        /// IShellLinkW。方法顺序必须与 shobjidl_core.h 中的 vtable 完全一致，
        /// 少一个或多一个都会导致调用错位（表现为访问冲突或莫名的 E_FAIL）。
        /// </summary>
        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
                int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
                int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }
    }
}
