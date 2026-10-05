using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;

namespace MemReclaim
{
    /// <summary>
    /// 提权处理。
    ///
    /// 本程序需要 SeProfileSingleProcessPrivilege 与 SeIncreaseQuotaPrivilege，
    /// 二者默认仅分配给 Administrators，因此必须以管理员身份运行。
    ///
    /// 采用两道保障：
    ///   1. 可执行文件嵌入 requireAdministrator 清单 —— 由 Windows 在启动时提权
    ///   2. 运行时检测 + 自我重启提权 —— 清单未生效时的兜底
    ///
    /// 第 2 道并非多余：当系统关闭 UAC（EnableLUA=0）时，requireAdministrator
    /// 清单会被忽略，进程直接以调用者令牌运行；若调用者非管理员，就只能靠这里兜底。
    /// </summary>
    internal static class ElevationHelper
    {
        /// <summary>重启标记。用于识别“已经尝试过提权”，避免无限重启。</summary>
        public const string RestartMarker = "--elevation-retry";

        private const int ERROR_CANCELLED = 1223;   // 用户在 UAC 对话框点了“否”

        public static bool IsElevated()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal p = new WindowsPrincipal(id);
                return p.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>判断本次启动是否已经是“提权重启后的第二次尝试”。</summary>
        public static bool IsRestartAttempt(string[] args)
        {
            if (args == null) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, RestartMarker, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把参数拼成命令行，含空格的加引号。</summary>
        private static string BuildArgumentLine(string[] args, bool appendMarker)
        {
            StringBuilder sb = new StringBuilder();
            if (args != null)
            {
                foreach (string a in args)
                {
                    // 标记参数不重复传递，避免重启后参数里堆叠多个
                    if (string.Equals(a, RestartMarker, StringComparison.OrdinalIgnoreCase)) continue;

                    if (sb.Length > 0) sb.Append(' ');
                    if (a.IndexOf(' ') >= 0) sb.Append('"').Append(a).Append('"');
                    else sb.Append(a);
                }
            }
            if (appendMarker)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(RestartMarker);
            }
            return sb.ToString();
        }

        private static string GetExecutablePath()
        {
            try
            {
                // 优先用程序集位置，它比 MainModule 更可靠（不受工作目录影响）
                string path = Assembly.GetEntryAssembly() != null
                    ? Assembly.GetEntryAssembly().Location
                    : null;
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
            }
            catch { }

            try { return Process.GetCurrentProcess().MainModule.FileName; }
            catch { }

            return null;
        }

        /// <summary>提权重启的结果。</summary>
        public enum RestartResult
        {
            /// <summary>已成功启动提权后的新实例，当前实例应退出。</summary>
            Launched,
            /// <summary>用户在 UAC 对话框取消。</summary>
            Declined,
            /// <summary>启动失败（其他错误）。</summary>
            Failed
        }

        /// <summary>
        /// 以管理员身份重新启动自身。
        /// 调用方在返回 Launched 时应立即退出，避免两个实例并存。
        /// </summary>
        public static RestartResult RestartElevated(string[] args, out string error)
        {
            error = null;

            string exe = GetExecutablePath();
            if (string.IsNullOrEmpty(exe))
            {
                error = "无法确定自身的可执行文件路径。";
                return RestartResult.Failed;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = BuildArgumentLine(args, true);
                psi.UseShellExecute = true;      // Verb 只在 ShellExecute 模式下有效
                psi.Verb = "runas";              // 请求提权，触发 UAC
                psi.WorkingDirectory = Path.GetDirectoryName(exe);

                Process.Start(psi);
                return RestartResult.Launched;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == ERROR_CANCELLED)
                {
                    error = "用户取消了提权请求。";
                    return RestartResult.Declined;
                }
                error = "启动提权实例失败：" + ex.Message + "（Win32 错误 " + ex.NativeErrorCode + "）";
                return RestartResult.Failed;
            }
            catch (Exception ex)
            {
                error = "启动提权实例失败：" + ex.Message;
                return RestartResult.Failed;
            }
        }

        /// <summary>
        /// 确保以管理员身份运行。
        /// 返回 true 表示当前实例可以继续；返回 false 表示调用方应退出
        /// （已启动提权实例，或用户拒绝且选择了退出）。
        /// </summary>
        public static bool EnsureElevated(string[] args, bool interactive)
        {
            if (IsElevated()) return true;

            // 已经尝试过一次仍非管理员，说明提权这条路走不通。
            // 此时不再重启，交由调用方以降权状态继续，并展示诊断信息。
            if (IsRestartAttempt(args)) return true;

            if (!interactive)
            {
                // 非交互模式（命令行/计划任务）不能弹 UAC：
                // 计划任务下弹窗会挂住任务，管道输出也会丢失。
                return true;
            }

            string error;
            RestartResult result = RestartElevated(args, out error);

            if (result == RestartResult.Launched) return false;   // 新实例已起，本实例退出

            // 用户拒绝或失败：给出说明，并让程序继续运行以展示诊断信息。
            // 直接退出会让用户看不到任何原因。
            string msg = result == RestartResult.Declined
                ? "未获得管理员权限，程序无法执行内存清理。\r\n\r\n" +
                  "你可以继续打开界面查看诊断信息，但清理操作会失败。\r\n" +
                  "如需正常使用，请右键程序 →「以管理员身份运行」。"
                : "提权失败：" + error + "\r\n\r\n" +
                  "程序将以当前权限继续运行，清理操作可能失败。";

            System.Windows.Forms.MessageBox.Show(msg, "内存回收 — 提权未完成",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);

            return true;
        }
    }
}
