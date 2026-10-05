using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MemReclaim
{
    /// <summary>
    /// 处理「网络标记」（Mark-of-the-Web, MOTW）。
    ///
    /// 背景：从网络下载的 exe 会被 Windows 附加一个 Zone.Identifier 备用数据流
    /// （内容形如 ZoneId=3）。附件管理器（Attachment Manager）据此在双击时弹出
    /// 「打开文件 - 安全警告」对话框。该弹框由 MOTW 驱动，与代码签名无关
    /// —— 实测同一份 exe，带 MOTW 的弹框、去掉 MOTW 的不弹。
    ///
    /// 本模块在程序启动时检查自身的 MOTW，若存在则清除，使后续双击不再被拦。
    ///
    /// 实测要点（均为本机验证）：
    ///   1. 运行中的 exe 可以删除自己的 ADS —— 映像节并不独占文件，
    ///      DeleteFileW 对 &lt;自身路径&gt;:Zone.Identifier 返回成功。
    ///   2. 必须用 DeleteFileW 真删。用 CREATE_ALWAYS 截断只会留下 0 字节的空流，
    ///      空流仍被附件管理器视为存在标记，无效。
    ///   3. 不支持 ADS 的文件系统（FAT32/exFAT、部分网络盘）会直接失败，
    ///      按「无 MOTW」处理，不报错。
    /// </summary>
    internal static class MotwHelper
    {
        private const string ZoneStreamName = ":Zone.Identifier";

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share,
            IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool DeleteFileW(string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, byte[] buf, int toRead, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        /// <summary>处理结果。</summary>
        public enum MotwResult
        {
            /// <summary>本来就没有 MOTW，无需处理（本地编译/复制产生的文件属此类）。</summary>
            NotPresent,
            /// <summary>存在 MOTW，已成功清除。</summary>
            Removed,
            /// <summary>存在 MOTW，但清除失败。</summary>
            Failed,
            /// <summary>无法判定（路径获取失败或文件系统不支持 ADS）。</summary>
            Unknown
        }

        /// <summary>
        /// 本次启动时的处理结果。
        ///
        /// 必须保存下来：清除发生在 Main 开头，而 --selftest 在那之后才运行，
        /// 届时再查已经查不到 MOTW 了。自检要如实报告「启动时曾清除」，而不是
        /// 报「无网络标记」 —— 后者会让人误以为功能没生效。
        /// </summary>
        public static Outcome StartupOutcome;

        public class Outcome
        {
            public MotwResult Result;
            public string Path;
            public int ZoneId = -1;
            public string Detail;

            public override string ToString()
            {
                switch (Result)
                {
                    case MotwResult.NotPresent:
                        return "无网络标记（无需处理）";
                    case MotwResult.Removed:
                        return "已清除网络标记" +
                               (ZoneId >= 0 ? "（ZoneId=" + ZoneId + "）" : "") +
                               "，此后双击不再弹出安全警告";
                    case MotwResult.Failed:
                        return "清除网络标记失败：" + Detail;
                    default:
                        return "网络标记状态未知：" + Detail;
                }
            }
        }

        /// <summary>取自身 exe 的路径；失败返回 null。</summary>
        public static string GetSelfPath()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetEntryAssembly();
                if (asm != null)
                {
                    string p = asm.Location;
                    if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) return p;
                }
            }
            catch { }

            try
            {
                string p = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(p) && System.IO.File.Exists(p)) return p;
            }
            catch { }

            return null;
        }

        /// <summary>判断 Zone.Identifier 流是否存在（不解析内容）。</summary>
        public static bool ZoneStreamExists(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;

            IntPtr h = CreateFileW(exePath + ZoneStreamName, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (h == new IntPtr(-1)) return false;   // 无流，或文件系统不支持 ADS

            CloseHandle(h);
            return true;
        }

        /// <summary>
        /// 读取 ZoneId；流不存在或无法解析时返回 -1。
        ///
        /// 注意：返回 -1 不等于「没有 MOTW」—— 流可能存在但内容不可解析
        /// （空流、被其他程序改写过、格式异常）。是否要清理请用 ZoneStreamExists，
        /// 不要用本方法的返回值判断。
        /// </summary>
        public static int ReadZoneId(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return -1;

            IntPtr h = CreateFileW(exePath + ZoneStreamName, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);

            if (h == new IntPtr(-1)) return -1;   // 无流（或不可访问）

            try
            {
                byte[] buf = new byte[1024];
                int read;
                if (!ReadFile(h, buf, buf.Length, out read, IntPtr.Zero) || read <= 0) return -1;

                string text = Encoding.ASCII.GetString(buf, 0, read);

                // 内容形如 "[ZoneTransfer]\r\nZoneId=3\r\n"，逐行找 ZoneId
                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("ZoneId", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = t.IndexOf('=');
                        if (eq >= 0)
                        {
                            int v;
                            if (int.TryParse(t.Substring(eq + 1).Trim(), out v)) return v;
                        }
                    }
                }
                return -1;   // 有流但没有可解析的 ZoneId
            }
            catch
            {
                return -1;
            }
            finally
            {
                CloseHandle(h);
            }
        }

        /// <summary>
        /// 检查并清除指定 exe 的 MOTW。
        ///
        /// 判据是「Zone.Identifier 流是否存在」，而不是「能否解析出 ZoneId」：
        /// 附件管理器只要看到这个流就可能拦截，空流或格式异常的流同样要清掉。
        ///
        /// 用 DeleteFileW 直接删除整个备用数据流。这是实测唯一有效的方式：
        /// 把流截断为 0 字节仍会留下空流，附件管理器照样拦截。
        /// </summary>
        public static Outcome Clean(string exePath)
        {
            Outcome o = new Outcome();
            o.Path = exePath;

            if (string.IsNullOrEmpty(exePath))
            {
                o.Result = MotwResult.Unknown;
                o.Detail = "无法确定可执行文件路径";
                return o;
            }

            if (!ZoneStreamExists(exePath))
            {
                // 确实没有 MOTW，或文件系统不支持 ADS（FAT32 等），两者都无需处理
                o.Result = MotwResult.NotPresent;
                return o;
            }

            // 流存在，尝试解析 ZoneId 仅用于展示（可能解析不出来）
            o.ZoneId = ReadZoneId(exePath);

            if (DeleteFileW(exePath + ZoneStreamName))
            {
                o.Result = MotwResult.Removed;
                return o;
            }

            int err = Marshal.GetLastWin32Error();

            if (err == ERROR_FILE_NOT_FOUND || err == ERROR_PATH_NOT_FOUND)
            {
                // 竞态：检测与删除之间流消失了，视为已清除
                o.Result = MotwResult.Removed;
                return o;
            }

            o.Result = MotwResult.Failed;
            o.Detail = new Win32Exception(err).Message + "（错误 " + err + "）";
            return o;
        }

        /// <summary>检查并清除自身 exe 的 MOTW，同时记录结果供自检使用。</summary>
        public static Outcome CleanSelf()
        {
            Outcome o = Clean(GetSelfPath());

            // 保留最有信息量的结果。
            // 提权重启会让 Main 再跑一次，那时 MOTW 已经没了，若直接覆盖，
            // 用户点「检查权限」只会看到「无网络标记」，反而看不出功能是否生效。
            if (StartupOutcome == null || StartupOutcome.Result != MotwResult.Removed)
                StartupOutcome = o;

            return o;
        }
    }
}
